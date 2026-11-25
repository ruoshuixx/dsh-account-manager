using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;

namespace AccountManager.Core.Sharing;

/// <summary>One API provider as dsh has it: what OpenCode and Codex get.</summary>
public sealed record SharedApi(string Id, string Name, string BaseUrl, bool UsesResponses, IReadOnlyList<SharedModel> Models, string Key);

/// <summary>
/// <paramref name="Variants"/>: the view's reasoning tiers (<c>{id, body.reasoning.effort}</c>), null when none.
/// <paramref name="Fast"/>: the model has a Fast twin in dsh (ticked "Fast" for its API); OpenCode gets <c>*-fast</c> variants for it.
/// </summary>
public sealed record SharedModel(string Id, string Name, JsonArray? Variants, bool Fast = false);

/// <summary>
/// Managed dsh API ids available to each program. Previous Codex ids also identify legacy per-API tables to remove.
/// </summary>
public sealed record SharedState(IReadOnlyCollection<string> OpenCode, IReadOnlyCollection<string> Codex)
{
    public static readonly SharedState Empty = new(Array.Empty<string>(), Array.Empty<string>());
}

public sealed record ShareResult(SharedState State, IReadOnlyList<string> Notes, bool Changed);

/// <summary>
/// dsh's API providers are the one list: every one is also written into OpenCode (provider in <c>opencode.json</c>, key as
/// its OpenCode credential) and every Responses one into Codex (<c>config.toml</c>). Removed from dsh = removed there.
/// A missing OpenCode or Codex is skipped quietly; a failure on one side never stops the other.
/// </summary>
public sealed class ApiShareService
{
    public const string OpenCodeChatPackage = "@opencode/ai/providers/openai-compatible";
    public const string OpenCodeResponsesPackage = "@opencode/ai/providers/openai/responses";

    private readonly IAccountBackend _dsh;
    private readonly CodexApiConfig _codex;
    private readonly IOpenCodeCredentialApi _openCodeApi;
    private readonly IOpenCodeConfigFile _openCodeConfig;
    private readonly SecretRegistry _secrets;
    private readonly AppLog? _log;
    private readonly BackupService? _backups;
    private readonly Func<CancellationToken, Task<Version?>>? _openCodeVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The first OpenCode that has the <c>experimental.model_speeds</c> switch (speed tiers such as Fast for ChatGPT models).</summary>
    public static readonly Version ModelSpeedsMinimumVersion = new(2, 4, 1);

    /// <param name="openCodeVersion">Tells the installed OpenCode's version; without it the speed switch is never written (an older OpenCode may reject an unknown key).</param>
    public ApiShareService(IAccountBackend dsh, CodexApiConfig codex, IOpenCodeCredentialApi openCodeApi,
        IOpenCodeConfigFile openCodeConfig, SecretRegistry secrets, AppLog? log = null, BackupService? backups = null,
        Func<CancellationToken, Task<Version?>>? openCodeVersion = null)
    {
        _openCodeVersion = openCodeVersion;
        _backups = backups;
        _dsh = dsh ?? throw new ArgumentNullException(nameof(dsh));
        _codex = codex ?? throw new ArgumentNullException(nameof(codex));
        _openCodeApi = openCodeApi ?? throw new ArgumentNullException(nameof(openCodeApi));
        _openCodeConfig = openCodeConfig ?? throw new ArgumentNullException(nameof(openCodeConfig));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _log = log;
    }

    /// <summary>dsh's managed API providers that have a key (Fast twins left out: only dsh's plugin understands them).</summary>
    public async Task<IReadOnlyList<SharedApi>> ReadDshAsync(CancellationToken ct = default)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var credential in (await _dsh.GetCredentialsAsync(ct).ConfigureAwait(false)).OfType<JsonObject>())
        {
            var value = JsonMerge.Get(credential, "value");
            if (JsonMerge.GetString(value, "type") != "key") { continue; }
            var id = JsonMerge.GetString(credential, "integrationID");
            var key = JsonMerge.GetString(value, "key");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(key)) { keys[id] = key; }
        }

        var result = new List<SharedApi>();
        foreach (var entry in (await _dsh.GetConfigEntriesAsync(ct).ConfigureAwait(false)).OfType<JsonObject>())
        {
            if (JsonMerge.Get(JsonMerge.Get(entry, "info"), "providers") is not JsonObject providers) { continue; }
            foreach (var (id, node) in providers)
            {
                if (node is not JsonObject view || !keys.TryGetValue(id, out var key)) { continue; }
                var baseUrl = ProviderViewMapper.ReadString(view["settings"], "baseURL") ?? string.Empty;
                var responses = OpenAiApiProtocol.UsesResponses(ProviderViewMapper.ReadString(view, "package"));
                var twins = ApiFastTwins.FindTwins(view["models"], baseUrl, responses).ToHashSet(StringComparer.Ordinal);
                var models = new List<SharedModel>();
                if (view["models"] is JsonObject list)
                {
                    foreach (var (modelId, model) in list)
                    {
                        if (twins.Contains(modelId)) { continue; }
                        var variants = (model?["variants"] as JsonArray)?.OfType<JsonObject>()
                            .Where(v => ProviderViewMapper.ReadString(v["body"]?["reasoning"], "effort") is not null)
                            .Select(v => (JsonNode)v.DeepClone()).ToArray();
                        models.Add(new SharedModel(modelId, ProviderViewMapper.ReadString(model, "name") ?? modelId,
                            variants is { Length: > 0 } ? new JsonArray(variants) : null, twins.Contains(ApiFastTwins.TwinId(modelId))));
                    }
                }

                result.Add(new SharedApi(id, ProviderViewMapper.ReadString(view, "name") ?? id, baseUrl, responses, models, key));
            }
        }

        return result;
    }

    /// <summary>Makes OpenCode and Codex match dsh's API list. <paramref name="previous"/> is what earlier runs wrote.</summary>
    public async Task<ShareResult> SyncAsync(SharedState previous, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var apis = await ReadDshAsync(ct).ConfigureAwait(false);
            foreach (var api in apis) { _secrets.RegisterValue(api.Key); }
            var notes = new List<string>();
            var changed = false;

            IReadOnlyCollection<string> codex;
            try
            {
                (codex, var codexChanged) = SyncCodex(apis, previous.Codex);
                changed |= codexChanged;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                codex = previous.Codex;
                notes.Add("API 未同步到 Codex：" + _secrets.Redact(ex.Message));
            }

            IReadOnlyCollection<string> openCode;
            try
            {
                (openCode, var openCodeChanged) = await SyncOpenCodeAsync(apis, previous.OpenCode, ct).ConfigureAwait(false);
                changed |= openCodeChanged;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                openCode = previous.OpenCode;
                // Not installed / not found: nothing to share with, not worth a warning.
                if (ex is OpenCodeException && ex.Message.StartsWith("找不到 OpenCode", StringComparison.Ordinal)) { _log?.Info("未找到 OpenCode，API 未同步到 OpenCode"); }
                else { notes.Add("API 未同步到 OpenCode：" + _secrets.Redact(ex.Message)); }
            }

            foreach (var note in notes) { _log?.Warn(note); }
            return new ShareResult(new SharedState(openCode, codex), notes, changed);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Codex ---------------------------------------------------------------

    private (IReadOnlyCollection<string> Shared, bool Changed) SyncCodex(IReadOnlyList<SharedApi> apis, IReadOnlyCollection<string> previous)
    {
        if (!_codex.Available) { return (previous, false); }
        var wanted = apis.Where(a => a.UsesResponses && a.Models.Count > 0 && CodexApiConfig.CanUse(a.Id)).ToList();
        var wantedIds = wanted.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var ours = previous.Concat(wantedIds).ToHashSet(StringComparer.Ordinal);
        var state = _codex.LoadState();
        var changed = _codex.Update(toml => CodexApiConfig.Migrate(toml, ours, wanted, state));
        _codex.SaveState(state);
        if (changed) { _log?.Info("已把 API 同步到 Codex 的 config.toml（" + wanted.Count + " 个）"); }
        return (wantedIds.ToList(), changed);
    }

    // ---- OpenCode ------------------------------------------------------------

    private async Task<(IReadOnlyCollection<string> Shared, bool Changed)> SyncOpenCodeAsync(IReadOnlyList<SharedApi> apis,
        IReadOnlyCollection<string> previous, CancellationToken ct)
    {
        var wantedIds = apis.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var gone = previous.Where(id => !wantedIds.Contains(id)).ToList();
        var document = await _openCodeConfig.ReadAsync(ct).ConfigureAwait(false);
        var config = (JsonObject)document.Config.DeepClone();
        if (config["providers"] is null) { config["providers"] = new JsonObject(); }
        if (config["providers"] is not JsonObject providers) { throw new OpenCodeException("opencode.json 的 providers 不是对象，未同步。"); }

        var configChanged = false;
        foreach (var api in apis)
        {
            var desired = OpenCodeProvider(api);
            if (providers[api.Id] is JsonNode existing && JsonNode.DeepEquals(existing, desired)) { continue; }
            providers[api.Id] = desired;
            configChanged = true;
        }

        foreach (var id in gone)
        {
            configChanged |= providers.Remove(id);
        }

        var openCodeVersion = await TryGetOpenCodeVersionAsync(ct).ConfigureAwait(false);
        configChanged |= EnsureModelSpeeds(config, openCodeVersion);
        configChanged |= EnsureFastVariants(config, openCodeVersion);

        // Keys first: a provider without its credential would fail in OpenCode, a credential without provider is inert.
        var credentials = (await _openCodeApi.GetCredentialsAsync(ct).ConfigureAwait(false)).OfType<JsonObject>().ToList();
        var credentialsChanged = false;
        var needsWrite = configChanged || gone.Any(id => credentials.Any(c => JsonMerge.GetString(c, "integrationID") == id))
            || apis.Any(api => !HasKey(credentials, api));
        if (needsWrite)
        {
            var backup = _backups?.Save(credentials, document.Path, document.Raw);
            if (backup is not null) { _log?.Info("已生成加密备份（同步 API 到 OpenCode）：" + backup); }
        }

        foreach (var api in apis)
        {
            if (HasKey(credentials, api)) { continue; }
            var mine = credentials.Where(c => JsonMerge.GetString(c, "integrationID") == api.Id).ToList();
            var value = new JsonObject
            {
                ["type"] = "key",
                ["key"] = api.Key,
                ["metadata"] = new JsonObject { ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["last_used"] = 0L },
            };
            await _openCodeApi.CreateCredentialAsync(api.Id, api.Name, value, activate: true, ct).ConfigureAwait(false);
            foreach (var old in mine)
            {
                if (JsonMerge.GetString(old, "id") is { Length: > 0 } oldId) { await _openCodeApi.DeleteCredentialAsync(oldId, ct).ConfigureAwait(false); }
            }

            credentialsChanged = true;
        }

        if (configChanged)
        {
            await _openCodeConfig.WriteAsync(new OpenCodeConfigDocument { Path = document.Path, Raw = document.Raw, Config = config }, ct).ConfigureAwait(false);
        }

        foreach (var id in gone)
        {
            foreach (var old in credentials.Where(c => JsonMerge.GetString(c, "integrationID") == id))
            {
                if (JsonMerge.GetString(old, "id") is { Length: > 0 } oldId)
                {
                    await _openCodeApi.DeleteCredentialAsync(oldId, ct).ConfigureAwait(false);
                    credentialsChanged = true;
                }
            }
        }

        if (configChanged || credentialsChanged) { _log?.Info("已把 API 同步到 OpenCode（" + apis.Count + " 个）"); }
        return (wantedIds.ToList(), configChanged || credentialsChanged);
    }

    /// <summary>
    /// Turns on OpenCode's speed tiers (<c>experimental.model_speeds</c>: Fast for ChatGPT-plan GPT models, <c>/fast</c>, the speed
    /// dropdown) when the installed OpenCode has the switch. A value the user already set (either way) is never changed, and an
    /// unknown or older OpenCode is left alone.
    /// </summary>
    private bool EnsureModelSpeeds(JsonObject config, Version? version)
    {
        if (config["experimental"] is JsonObject existing)
        {
            if (existing.ContainsKey("model_speeds")) { return false; }
        }
        else if (config["experimental"] is not null)
        {
            return false;
        }

        if (version is null || version < ModelSpeedsMinimumVersion) { return false; }
        var experimental = config["experimental"] as JsonObject ?? new JsonObject();
        experimental["model_speeds"] = true;
        config["experimental"] = experimental;
        _log?.Info("已在 OpenCode 配置里打开速度选项（experimental.model_speeds）");
        return true;
    }

    /// <summary>Fast variants for the ChatGPT-plan GPT models (<see cref="OpenCodeFastVariants"/>), for an OpenCode that reads the V2 config.</summary>
    private bool EnsureFastVariants(JsonObject config, Version? version)
    {
        if (version is null || version < OpenCodeFastVariants.MinimumOpenCodeVersion) { return false; }
        if (!OpenCodeFastVariants.Apply(config)) { return false; }
        _log?.Info("已在 OpenCode 配置里给 ChatGPT 订阅的 GPT 模型加上 Fast 档位（*-fast）");
        return true;
    }

    private async Task<Version?> TryGetOpenCodeVersionAsync(CancellationToken ct)
    {
        if (_openCodeVersion is null) { return null; }
        try { return await _openCodeVersion(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Info("未能读取 OpenCode 版本，未设置速度选项：" + _secrets.Redact(ex.Message));
            return null;
        }
    }

    /// <summary>Exactly one credential for the provider, holding this key.</summary>
    private static bool HasKey(IReadOnlyList<JsonObject> credentials, SharedApi api)
    {
        var mine = credentials.Where(c => JsonMerge.GetString(c, "integrationID") == api.Id).ToList();
        return mine.Count == 1 && JsonMerge.GetString(JsonMerge.Get(mine[0], "value"), "type") == "key"
            && JsonMerge.GetString(JsonMerge.Get(mine[0], "value"), "key") == api.Key;
    }

    /// <summary>
    /// The model's reasoning variants, and for a model with a Fast twin in dsh one <c>&lt;tier&gt;-fast</c> variant per tier: the same body plus
    /// <c>service_tier: "priority"</c> (a lone <c>fast</c> variant when the model has no tiers). OpenCode has no plugin that understands the
    /// twin model dsh uses, so Fast is a variant of the base model there. Null when the model has neither.
    /// </summary>
    private static JsonArray? WithFastVariants(SharedModel model)
    {
        var result = new JsonArray();
        if (model.Variants is not null) { foreach (var variant in model.Variants) { result.Add(variant?.DeepClone()); } }
        if (model.Fast)
        {
            var tiers = model.Variants?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            if (tiers.Count == 0)
            {
                result.Add(new JsonObject { ["id"] = "fast", ["body"] = new JsonObject { ["service_tier"] = "priority" } });
            }

            foreach (var tier in tiers)
            {
                var id = JsonMerge.GetString(tier, "id");
                if (string.IsNullOrEmpty(id)) { continue; }
                var body = tier["body"] is JsonObject b ? (JsonObject)b.DeepClone() : new JsonObject();
                body["service_tier"] = "priority";
                result.Add(new JsonObject { ["id"] = id + CodexFastService.FastSuffix, ["body"] = body });
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>An OpenCode V2 provider entry. Reasoning tiers only for Responses: that is where OpenCode sends <c>reasoning.effort</c>.</summary>
    internal static JsonObject OpenCodeProvider(SharedApi api)
    {
        var settings = new JsonObject { ["baseURL"] = api.BaseUrl };
        // A third-party endpoint need not support OpenAI WebSockets.
        if (api.UsesResponses) { settings["transport"] = "http"; }
        var models = new JsonObject();
        foreach (var model in api.Models)
        {
            var entry = new JsonObject { ["name"] = model.Name };
            var variants = api.UsesResponses ? WithFastVariants(model) : null;
            if (variants is not null) { entry["variants"] = variants; }
            models[model.Id] = entry;
        }

        return new JsonObject
        {
            ["name"] = api.Name,
            ["package"] = api.UsesResponses ? OpenCodeResponsesPackage : OpenCodeChatPackage,
            ["settings"] = settings,
            ["models"] = models,
        };
    }
}
