using System.Text.Json.Nodes;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.Security;

namespace AccountManager.Core.Dsh;

/// <summary>The outcome of merging routes into <c>subagent-model-selection-settings</c>.</summary>
public sealed record SubagentMergeResult(IReadOnlyList<SubagentRoute> Added, bool EnabledChanged, string Backup, string Note)
{
    public bool Changed => Added.Count > 0 || EnabledChanged;
}

/// <summary>
/// Lets a main agent delegate to the models this program manages. dsh lets a child agent select a provider/model only
/// when the host entry <c>subagent-model-selection-settings</c> has <c>enabled: true</c> and a non-empty
/// <c>allowedModels</c> list of exact routes; the policy is sampled when a NEW top-level conversation starts, so a
/// change applies to new conversations only. This service computes the candidate routes (every user-layer provider
/// with its models, plus the Codex models) and merges them into the entry, never removing what the user added.
/// </summary>
public sealed class SubagentRouteService
{
    /// <summary>Said to the user after every change: dsh samples the policy once per new conversation.</summary>
    public const string NewConversationNote = "改动只对 dsh 里的新对话生效：请在 dsh 中新建对话后，再让主代理调用子代理。";

    /// <summary>
    /// The Codex models this tool offers for ChatGPT sign-in, recommended first. Source: OpenAI's Codex models page
    /// (https://learn.chatgpt.com/docs/models), checked 2026-10-20; the selection of these three was chosen by the user.
    /// Deliberately left out: gpt-6-sol, the gpt-5.6 family, and the retired gpt-5.5 / gpt-5.3-codex-spark that dsh's
    /// bundled catalog still lists. Used for the explicit <c>models</c> list of a newly created (or empty)
    /// <c>openai-codex</c> route and for the subagent <c>allowedModels</c>. Update this list when the page changes.
    /// </summary>
    public static readonly IReadOnlyList<string> OfficialCodexModels = new[] { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna" };

    /// <summary>
    /// The <c>openai-codex</c> provider entry this tool writes: an explicit <c>models</c> list of
    /// <see cref="OfficialCodexModels"/>, ids only (dsh fills in name, limits and input from its catalog).
    /// </summary>
    public static JsonObject OfficialCodexRoute()
    {
        var models = new JsonArray();
        foreach (var id in OfficialCodexModels) { models.Add(new JsonObject { ["id"] = id }); }
        return new JsonObject { ["models"] = models };
    }

    private readonly Func<DshHome> _home;
    private readonly BackupService? _backups;
    private readonly SecretRegistry? _secrets;
    private readonly AppLog? _log;
    private readonly Func<IReadOnlyList<string>> _codexCatalog;

    /// <param name="codexCatalog">Chat model ids of dsh's Codex catalog; defaults to a lookup of the installed dsh runtime.</param>
    public SubagentRouteService(Func<DshHome> home, BackupService? backups = null, SecretRegistry? secrets = null, AppLog? log = null,
        Func<IReadOnlyList<string>>? codexCatalog = null)
    {
        _home = home;
        _backups = backups;
        _secrets = secrets;
        _log = log;
        _codexCatalog = codexCatalog ?? DefaultCatalog;
    }

    private static IReadOnlyList<string> DefaultCatalog()
        => CodexCatalog.ReadChatModelIds(CodexCatalog.DefaultSearchRoots(launcherFile: DshLauncher.Find()?.FileName));

    /// <summary>Whether the user layer lists the built-in Codex route.</summary>
    public bool HasCodexRoute()
    {
        try { return ProfilePatchDocument.Load(_home().PatchPath).GetProviders()?.ContainsKey(ProviderViewMapper.CodexRouteId) == true; }
        catch (DshException) { return false; }
    }

    /// <summary>
    /// The routes worth offering: (a) every provider of the user layer with its listed chat models; (b) Codex: exactly the models
    /// listed under <c>openai-codex</c>, else <see cref="OfficialCodexModels"/> filtered by dsh's catalog when it is found. Duplicates are dropped.
    /// </summary>
    public IReadOnlyList<SubagentRoute> ComputeCandidates(ProfilePatchDocument patch)
    {
        var routes = new List<SubagentRoute>();
        void Add(string provider, string? model)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) { return; }
            var r = new SubagentRoute(provider, model);
            if (!routes.Contains(r)) { routes.Add(r); }
        }

        var providers = patch.GetProviders() ?? new JsonObject();
        foreach (var (id, node) in providers)
        {
            if (id == ProviderViewMapper.CodexRouteId || node is not JsonObject provider) { continue; }
            foreach (var model in ModelIds(provider)) { Add(id, model); }
        }

        if (providers[ProviderViewMapper.CodexRouteId] is JsonObject codex)
        {
            var listed = ModelIds(codex);
            if (listed.Count == 0)
            {
                // An empty route serves dsh's catalog, so offer the current official models the catalog also serves; when the
                // catalog cannot be found (Windows today) the official list is used as it is. Retired ids are never added.
                var catalog = _codexCatalog();
                listed = (catalog.Count == 0 ? OfficialCodexModels : OfficialCodexModels.Where(catalog.Contains)).ToList();
            }

            foreach (var model in listed) { Add(ProviderViewMapper.CodexRouteId, model); }
        }

        return routes;
    }

    /// <summary>Models a provider entry lists (strings or objects with an id), skipping ones that do not take text input.</summary>
    private static List<string> ModelIds(JsonObject provider)
    {
        var ids = new List<string>();
        if (provider["models"] is not JsonArray models) { return ids; }
        foreach (var node in models)
        {
            string? id = null;
            if (node is JsonValue v && v.TryGetValue<string>(out var s)) { id = s; }
            else if (node is JsonObject o)
            {
                id = ProviderViewMapper.ReadString(o, "id");
                if (o["input"] is JsonArray input && input.Count > 0
                    && !input.Any(x => x is JsonValue iv && iv.TryGetValue<string>(out var t) && t == "text")) { continue; }
            }

            if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id)) { ids.Add(id); }
        }

        return ids;
    }

    private static bool IsEmptyCodexEntry(ProfilePatchDocument doc)
        => doc.GetProviders() is { } providers
           && providers.TryGetPropertyValue(ProviderViewMapper.CodexRouteId, out var node)
           && (node is null || node is JsonObject { Count: 0 });

    /// <summary>
    /// An <c>openai-codex</c> entry that is exactly empty (<c>{}</c>, or a key without children) carries no user choices;
    /// version 0.1.2 wrote such an entry. It is replaced by <see cref="OfficialCodexRoute"/> after an encrypted backup.
    /// An entry with any key at all (models, displayName, ...) is never touched. Returns whether the file changed.
    /// </summary>
    public bool UpgradeEmptyCodexRoute(IEnumerable<JsonNode?>? credentialsForBackup = null)
    {
        var path = _home().PatchPath;
        if (!File.Exists(path)) { return false; }
        var before = ProfilePatchDocument.Load(path);
        if (!IsEmptyCodexEntry(before)) { return false; }

        if (_backups is not null)
        {
            var backup = _backups.Save(credentialsForBackup ?? Array.Empty<JsonNode?>(), path, before.Text);
            _log?.Info("已生成加密备份（补全空的 openai-codex 路由）：" + backup);
        }

        var changed = ProfilePatchDocument.Update(path, doc => IsEmptyCodexEntry(doc) && doc.SetProvider(ProviderViewMapper.CodexRouteId, OfficialCodexRoute()));
        if (!changed) { return false; }

        var verify = ProfilePatchDocument.Load(path).GetProviders()?[ProviderViewMapper.CodexRouteId] as JsonObject;
        var ids = (verify?["models"] as JsonArray)?.Select(m => ProviderViewMapper.ReadString(m as JsonObject ?? new JsonObject(), "id")).ToList();
        if (ids is null || !ids.SequenceEqual(OfficialCodexModels))
        {
            throw new DshException("补全 openai-codex 路由后回读校验失败；请检查 cordis.patch.yml。");
        }

        _log?.Info("openai-codex 路由原来是空条目：已写入官方现行 Codex 模型（" + string.Join("、", OfficialCodexModels) + "）。");
        return true;
    }

    /// <summary>
    /// Merges all candidate routes into the entry (enabled: true) after an encrypted backup, under the patch file's lock,
    /// and reads the result back. <paramref name="credentialsForBackup"/> goes into the backup alongside the file text.
    /// Never throws for "nothing to do"; throws <see cref="DshException"/> for files it must not rewrite.
    /// </summary>
    public SubagentMergeResult Merge(IEnumerable<JsonNode?>? credentialsForBackup = null)
    {
        var home = _home();
        var path = home.PatchPath;
        if (!File.Exists(path)) { return new SubagentMergeResult(Array.Empty<SubagentRoute>(), false, string.Empty, "没有 cordis.patch.yml，未设置子代理可用模型。"); }

        _ = UpgradeEmptyCodexRoute(credentialsForBackup);
        var before = ProfilePatchDocument.Load(path);
        var candidates = ComputeCandidates(before);
        if (candidates.Count == 0) { return new SubagentMergeResult(Array.Empty<SubagentRoute>(), false, string.Empty, "没有可加入的子代理路由。"); }
        var state = before.GetSubagentSelection();
        if (state.Enabled == true && candidates.All(c => state.Routes.Contains(c))) { return new SubagentMergeResult(Array.Empty<SubagentRoute>(), false, string.Empty, string.Empty); }

        var backup = string.Empty;
        if (_backups is not null)
        {
            backup = _backups.Save(credentialsForBackup ?? Array.Empty<JsonNode?>(), path, before.Text);
            _log?.Info("已生成加密备份（设置子代理可用模型）：" + backup);
        }

        SubagentChange? change = null;
        ProfilePatchDocument.Update(path, doc =>
        {
            // Candidates are recomputed from the file as it is under the lock.
            change = doc.AddSubagentRoutes(ComputeCandidates(doc));
            return change.Changed;
        });

        var verify = ProfilePatchDocument.Load(path).GetSubagentSelection();
        if (change is { Changed: true } && (verify.Enabled != true || !change.Added.All(r => verify.Routes.Contains(r))))
        {
            throw new DshException("子代理可用模型写入后回读校验失败；请检查 cordis.patch.yml。");
        }

        var result = new SubagentMergeResult(change?.Added ?? Array.Empty<SubagentRoute>(), change?.EnabledChanged ?? false, backup, string.Empty);
        if (result.Changed)
        {
            _log?.Info("已把 " + result.Added.Count + " 条路由加入子代理可用模型（subagent-model-selection-settings）" + (result.Added.Count > 0 ? "：" + string.Join("、", result.Added.Take(8).Select(r => r.Provider + "/" + r.Model)) + (result.Added.Count > 8 ? "…" : string.Empty) : string.Empty)
                + (result.EnabledChanged ? "；已启用子代理模型选择" : string.Empty) + "。" + NewConversationNote);
        }

        return result;
    }

    /// <summary>The user-facing lines for a change, or none when nothing changed.</summary>
    public static IEnumerable<string> Describe(SubagentMergeResult result)
    {
        if (!result.Changed) { yield break; }
        yield return "已把 " + result.Added.Count + " 条路由加入 dsh 的子代理可用模型" + (result.EnabledChanged ? "（并启用了子代理模型选择）" : string.Empty) + "，主代理可以把任务委派给这些供应商 / 模型。";
        yield return NewConversationNote;
    }
}
