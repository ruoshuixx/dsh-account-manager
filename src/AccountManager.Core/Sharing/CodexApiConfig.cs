using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using AccountManager.Core.Security;

namespace AccountManager.Core.Sharing;

/// <summary>
/// The API side of Codex's <c>config.toml</c>: all shared APIs use one provider table. The active dsh API id,
/// available APIs and models, and the user's original model/provider live in an owner-only state file.
/// Switching APIs keeps Codex's provider identity stable. <c>auth.json</c> is never touched here.
/// </summary>
public sealed class CodexApiConfig
{
    public const string ProviderId = "ai-account-manager";

    /// <summary>Provider ids Codex reserves for its built-in providers.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "openai", "ollama", "lmstudio", ProviderId };

    private const string LegacyProfilesTable = "profiles";

    private readonly CodexCliAuthStore _codex;
    private readonly BackupService? _backups;
    private readonly string? _statePath;

    /// <param name="statePath">Where the remembered models live (the app's data folder); null keeps them next to config.toml.</param>
    public CodexApiConfig(CodexCliAuthStore codex, BackupService? backups = null, string? statePath = null)
    {
        _codex = codex ?? throw new ArgumentNullException(nameof(codex));
        _backups = backups;
        _statePath = statePath;
    }

    public string ConfigPath => _codex.ConfigPath;

    private string StatePath => _statePath ?? Path.Combine(_codex.Home, "ai-account-manager.json");

    /// <summary>Whether Codex is installed here at all (its home directory exists; it is never created).</summary>
    public bool Available => Directory.Exists(_codex.Home);

    public static bool CanUse(string providerId) => !Reserved.Contains(providerId);

    /// <summary>The raw file text, or null when there is no file.</summary>
    public string? ReadText()
    {
        try { return File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath, Encoding.UTF8) : null; }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public CodexConfigToml Read() => new(ReadText());

    /// <summary>The active dsh API id, or null unless Codex selects our shared provider table.</summary>
    public string? CurrentApi()
    {
        if (!Available) { return null; }
        var toml = Read();
        return toml.GetTop("model_provider") == ProviderId && toml.HasTable(CodexConfigToml.ProvidersTable, ProviderId)
            ? Str(LoadState()["api"]) : null;
    }

    /// <summary>Historical managed provider ids, retained after APIs are removed for thread migration.</summary>
    public IReadOnlyCollection<string> LegacyProviderIds
        => (LoadState()["legacyProviders"] as JsonArray)?.Select(Str).OfType<string>().ToArray() ?? Array.Empty<string>();

    /// <summary>
    /// Applies <paramref name="edit"/> to the file and writes it when the text changed: encrypted backup first, the file
    /// is replaced atomically and owner-only (it holds API keys), and an edit made by someone else meanwhile stops it.
    /// </summary>
    public bool Update(Func<CodexConfigToml, bool> edit)
    {
        if (!Available) { return false; }
        var before = ReadText();
        var toml = new CodexConfigToml(before);
        if (!edit(toml)) { return false; }
        var after = toml.ToString();
        if (after == (before ?? string.Empty)) { return false; }
        if (ReadText() != before) { throw new DshException("Codex 的 config.toml 正被其它程序修改，已停止写入；请稍后重试。"); }
        if (before is not null) { _backups?.Save(Array.Empty<JsonNode?>(), ConfigPath, before); }
        DshAtomicWrite.WriteBytes(ConfigPath, new UTF8Encoding(false).GetBytes(after), DshAtomicWrite.PrivateFileMode, directoryMode: null);
        if (ReadText() != after) { throw new DshException("Codex 的 config.toml 回读校验失败。"); }
        return true;
    }

    /// <summary>Makes Codex use this API with its remembered (else first of <paramref name="models"/>) model. Returns whether anything changed.</summary>
    public bool Use(string providerId, IReadOnlyList<string>? models = null)
    {
        if (!Available) { return false; }
        var state = LoadState();
        if (!CanUse(providerId) || state["providers"]?[providerId] is not JsonObject)
        {
            throw new DshException("该 API 还没有同步到 Codex。Codex 只支持 Responses 协议的 API；请确认协议后点“读取”再试。");
        }

        models ??= (state["catalog"]?[providerId] as JsonArray)?.Select(Str).OfType<string>().ToList();
        var previousApi = CurrentApi();
        var changed = Update(t => Select(t, providerId, models, state));
        SaveState(state);
        return changed || previousApi != providerId;
    }

    /// <summary>Back to the ChatGPT login: the user's own model / provider come back. Returns whether it changed.</summary>
    public bool LeaveApi(IReadOnlySet<string>? ours = null)
    {
        if (!Available) { return false; }
        var state = LoadState();
        var changed = Update(t => Leave(t, ours, state));
        SaveState(state);
        return changed;
    }

    /// <summary>
    /// Migrates legacy profiles and per-API providers, and updates or leaves the active API.
    /// Runs inside <see cref="Update"/>; inactive APIs are kept only in the private state file.
    /// </summary>
    internal static bool Migrate(CodexConfigToml toml, IReadOnlySet<string> ours, IReadOnlyList<SharedApi> apis, JsonObject state)
    {
        var models = apis.ToDictionary(a => a.Id, a => (IReadOnlyList<string>)a.Models.Select(m => m.Id).ToList(), StringComparer.Ordinal);
        var catalog = new JsonObject();
        foreach (var (id, list) in models) { catalog[id] = new JsonArray(list.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()); }
        state["catalog"] = catalog;
        var providers = new JsonObject();
        foreach (var api in apis)
        {
            providers[api.Id] = new JsonObject
            {
                ["name"] = api.Name,
                ["base_url"] = api.BaseUrl.TrimEnd('/'),
                ["wire_api"] = "responses",
                ["experimental_bearer_token"] = api.Key,
            };
        }
        state["providers"] = providers;
        var legacyIds = (state["legacyProviders"] as JsonArray)?.Select(Str).OfType<string>() ?? Array.Empty<string>();
        state["legacyProviders"] = new JsonArray(legacyIds.Concat(ours).Where(CanUse).Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal).Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());

        var edited = false;
        var legacy = toml.Profile;
        if (legacy is not null && ours.Contains(legacy))
        {
            var model = toml.GetValue(LegacyProfilesTable, legacy, "model");
            if (model is not null) { Models(state)[legacy] = model; }
            edited |= toml.SetTop("profile", null);
            if (models.TryGetValue(legacy, out var list)) { edited |= Select(toml, legacy, list, state); }
        }

        foreach (var id in ours)
        {
            var model = toml.GetValue(LegacyProfilesTable, id, "model");
            if (model is not null && !Models(state).ContainsKey(id)) { Models(state)[id] = model; }
            edited |= toml.SetTable(LegacyProfilesTable, id, null);
        }

        // A legacy top-level selector may exist without a state file. Never restore that deleted provider later.
        var current = toml.GetTop("model_provider");
        if (current is not null && ours.Contains(current))
        {
            if (!models.TryGetValue(current, out var list)) { edited |= Leave(toml, ours, state); }
            else
            {
                var model = toml.GetTop("model");
                state["before"] ??= new JsonObject();
                state["api"] = current;
                edited |= Select(toml, current, list, state);
                if (model is not null)
                {
                    Models(state)[current] = model;
                    edited |= toml.SetTop("model", model);
                }
            }
        }
        else if (current == ProviderId)
        {
            var api = Str(state["api"]);
            if (api is null || !models.TryGetValue(api, out var list)) { edited |= Leave(toml, ours, state); }
            else
            {
                edited |= WriteProvider(toml, api, state);
                if (!list.Contains(toml.GetTop("model") ?? string.Empty)) { edited |= toml.SetTop("model", list[0]); }
            }
        }

        foreach (var id in ours.Where(CanUse)) { edited |= toml.SetTable(CodexConfigToml.ProvidersTable, id, null); }
        return edited;
    }

    internal JsonObject LoadState()
    {
        try { return File.Exists(StatePath) ? JsonNode.Parse(File.ReadAllText(StatePath)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch { return new JsonObject(); }
    }

    internal void SaveState(JsonObject state)
    {
        try
        {
            var text = state.ToJsonString();
            if (File.Exists(StatePath) ? File.ReadAllText(StatePath) == text : state.Count == 0) { return; }
            DshAtomicWrite.WriteText(StatePath, text, DshAtomicWrite.PrivateFileMode, directoryMode: null);
        }
        catch { /* best effort: config.toml remains usable if the state cannot be saved */ }
    }

    private static JsonObject Models(JsonObject state)
    {
        if (state["models"] is not JsonObject models) { models = new JsonObject(); state["models"] = models; }
        return models;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Select(CodexConfigToml toml, string providerId, IReadOnlyList<string>? models, JsonObject state)
    {
        var currentProvider = toml.GetTop("model_provider");
        var currentModel = toml.GetTop("model");
        // Remember what the user had before the first API, and the model used with an API we are leaving.
        var currentApi = Str(state["api"]);
        if (currentApi is null || (currentProvider != ProviderId && currentProvider != currentApi))
        {
            state["before"] = new JsonObject { ["model"] = currentModel, ["model_provider"] = currentProvider };
        }
        else if (currentModel is not null) { Models(state)[currentApi] = currentModel; }

        var model = Str(Models(state)[providerId]);
        if (models is { Count: > 0 } && (model is null || !models.Contains(model))) { model = models[0]; }
        state["api"] = providerId;
        var edited = WriteProvider(toml, providerId, state);
        edited |= toml.SetTop("model_provider", ProviderId);
        if (model is not null) { edited |= toml.SetTop("model", model); }
        return edited;
    }

    private static bool WriteProvider(CodexConfigToml toml, string apiId, JsonObject state)
        => toml.SetTable(CodexConfigToml.ProvidersTable, ProviderId, ((JsonObject)state["providers"]![apiId]!)
            .Select(p => new KeyValuePair<string, string>(p.Key, Str(p.Value)!)).ToArray());

    private static bool Leave(CodexConfigToml toml, IReadOnlySet<string>? ours, JsonObject state)
    {
        var edited = false;
        var legacy = toml.Profile;
        if (legacy is not null && (ours?.Contains(legacy) ?? toml.HasTable(LegacyProfilesTable, legacy))) { edited |= toml.SetTop("profile", null); }

        var provider = toml.GetTop("model_provider");
        var mine = provider == ProviderId || (provider is not null && (Str(state["api"]) == provider || (ours?.Contains(provider) ?? false)));
        if (!mine) { return edited; }

        var model = toml.GetTop("model");
        var api = provider == ProviderId ? Str(state["api"]) : provider;
        if (model is not null && api is not null) { Models(state)[api] = model; }
        var before = state["before"] as JsonObject;
        edited |= toml.SetTop("model_provider", Str(before?["model_provider"]));
        edited |= toml.SetTop("model", Str(before?["model"]));
        edited |= toml.SetTable(CodexConfigToml.ProvidersTable, ProviderId, null);
        state.Remove("api");
        state.Remove("before");
        return edited;
    }
}
