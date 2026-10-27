using System.Text.Json.Nodes;
using AccountManager.Core.Logging;
using AccountManager.Core.Security;

namespace AccountManager.Core.Dsh;

/// <summary>The outcome of <see cref="CodexFastService.Sync"/>.</summary>
public sealed record FastSyncResult(
    FastInstallResult? Component,
    IReadOnlyList<string> AddedModels,
    IReadOnlyList<string> RemovedModels,
    string Note)
{
    /// <summary>The model ids OpenAI lists for the account (after this program's selection); null when the list was not fetched.</summary>
    public IReadOnlyList<string>? FetchedModels { get; init; }

    /// <summary>Why the account's model list was not used (token, network, response); empty when it was, or when no attempt was made.</summary>
    public string FetchNote { get; init; } = string.Empty;

    /// <summary>Models this program wanted to remove but left in place because a setting still points at them.</summary>
    public IReadOnlyList<string> KeptBecauseReferenced { get; init; } = Array.Empty<string>();

    /// <summary>The component was installed or updated, so dsh must be restarted before it loads it.</summary>
    public bool ComponentChanged => Component is { Changed: true };

    public bool Changed => ComponentChanged || AddedModels.Count > 0 || RemovedModels.Count > 0;
}

/// <summary>
/// Keeps the <c>openai-codex</c> route's models in line with what OpenAI offers the account, and gives the Fast-capable ones a Fast twin.
/// <list type="bullet">
/// <item>The account's list comes from <see cref="CodexModelCatalogClient"/> (the user's rule: everything listed except old models, see
/// <see cref="CodexModelPolicy"/>). Missing models are appended to the route; ids dsh's own catalog knows are written id-only, others
/// with explicit facts. Models this program wrote earlier (<see cref="CodexModelOwnership"/>, and what versions 0.1.3/0.1.4 wrote) that
/// OpenAI no longer offers are taken out again, unless a setting such as the default model still points at them. Entries the user wrote
/// are never touched. When the list cannot be fetched an existing route is left alone.</item>
/// <item>Fast: dsh has no setting for the service tier, so this program ships a tiny plugin (<see cref="FastComponentInstaller"/>) that
/// sends a model id ending in <c>-fast</c> as its base model with the priority tier. A twin <c>&lt;base&gt;-fast</c> exists only while that
/// component is installed and listed; without it a raw <c>-fast</c> id would reach OpenAI, so the twins this program wrote are removed.</item>
/// </list>
/// </summary>
public sealed class CodexFastService
{
    public const string FastSuffix = "-fast";
    public const string FastNameSuffix = " (Fast)";

    /// <summary>Said after a change that needs dsh to be restarted, and the cost rule that always applies.</summary>
    public const string RestartAndCostNote = "首次安装需要重启 dsh（完全退出 DSH 再打开）后 Fast 模型才可用；Fast 按官方规则以 2.5 倍消耗订阅额度。";

    /// <summary>Said when only models were added: the component was already installed.</summary>
    public const string CostNote = "Fast 按官方规则以 2.5 倍消耗订阅额度。";

    private static readonly string[] Levels = { "off", "minimal", "low", "medium", "high", "xhigh", "max" };

    /// <summary>The thinking levels dsh's llm-pi-ai accepts in <c>reasoningEfforts</c> (OpenAI also reports <c>ultra</c>, which dsh does not know).</summary>
    private static readonly HashSet<string> AcceptedLevels = new(Levels, StringComparer.Ordinal);

    /// <summary>The ids 0.1.3/0.1.4 wrote id-only, recognised as this program's when no record exists yet.</summary>
    private static readonly string[] LegacyIdOnly = { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna" };

    private readonly Func<DshHome> _home;
    private readonly BackupService? _backups;
    private readonly AppLog? _log;
    private readonly SubagentRouteService? _routes;
    private readonly FastComponentInstaller _installer;
    private readonly Func<IReadOnlyDictionary<string, JsonObject>> _catalog;
    private readonly CodexModelCatalogClient? _client;
    private readonly CodexModelOwnership _ownership;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="routes">Used to upgrade an empty <c>openai-codex</c> route to the built-in models list first.</param>
    /// <param name="catalog">dsh's bundled Codex catalog (chat models by id); defaults to a lookup of the installed dsh runtime.</param>
    /// <param name="client">Fetches the account's model list; null means "never ask OpenAI" (tests, or a build without it).</param>
    /// <param name="ownership">Which route models this program wrote; in memory when null.</param>
    public CodexFastService(Func<DshHome> home, BackupService? backups = null, SecretRegistry? secrets = null, AppLog? log = null,
        SubagentRouteService? routes = null, Func<IReadOnlyDictionary<string, JsonObject>>? catalog = null,
        CodexModelCatalogClient? client = null, CodexModelOwnership? ownership = null, Func<DateTimeOffset>? clock = null)
    {
        _ = secrets;
        _home = home;
        _backups = backups;
        _log = log;
        _routes = routes;
        _installer = new FastComponentInstaller(home, backups, log);
        _catalog = catalog ?? DefaultCatalog;
        _client = client;
        _ownership = ownership ?? new CodexModelOwnership(null);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public FastComponentInstaller Installer => _installer;

    private readonly object _pendingGate = new();
    private readonly List<FastSyncResult> _pending = new();

    /// <summary>
    /// Remembers a change made outside an import (the account activation creates the route and syncs there) so the import that
    /// triggered it can put it in its result.
    /// </summary>
    public void Remember(FastSyncResult result)
    {
        if (!result.Changed) { return; }
        lock (_pendingGate) { _pending.Add(result); }
    }

    /// <summary>The report lines of the remembered changes (each line once), and forgets them.</summary>
    public IReadOnlyList<string> TakePendingLines()
    {
        lock (_pendingGate)
        {
            var lines = _pending.SelectMany(Describe).Distinct(StringComparer.Ordinal).ToList();
            _pending.Clear();
            return lines;
        }
    }

    private static IReadOnlyDictionary<string, JsonObject> DefaultCatalog()
        => CodexCatalog.ReadChatModels(CodexCatalog.DefaultSearchRoots(launcherFile: DshLauncher.Find()?.FileName));

    /// <summary>Whether an entry has the shape of the Fast twins this program writes: id <c>&lt;official base&gt;-fast</c>, name ending in " (Fast)".</summary>
    public static bool IsOurFastEntry(CodexRouteModel model)
        => IsFastShape(model) && CodexBuiltInModels.Ids.Contains(model.Id[..^FastSuffix.Length]);

    private static bool IsFastShape(CodexRouteModel model)
        => model.Entry is { } entry
           && model.Id.Length > FastSuffix.Length
           && model.Id.EndsWith(FastSuffix, StringComparison.Ordinal)
           && ProviderViewMapper.ReadString(entry, "name") is { } name && name.EndsWith(FastNameSuffix, StringComparison.Ordinal);

    private static bool IsTwin(CodexRouteModel model, IReadOnlySet<string> owned)
        => IsFastShape(model) && (owned.Contains(model.Id) || CodexBuiltInModels.Ids.Contains(model.Id[..^FastSuffix.Length]));

    private static bool IsIdOnly(CodexRouteModel model) => model.Entry is null || model.Entry.Count == 1;

    /// <summary>
    /// Makes the route follow the account's models and gives them Fast twins. Safe to call any time: it returns an empty result when
    /// there is nothing to do and never throws for a file it must not touch (the reason is in the result and the log).
    /// <paramref name="credentials"/> are the manager's credential objects: they supply the token for the model list (the current dsh
    /// account's when it is valid for 10 more minutes, else another account's unexpired one; nothing is ever refreshed) and go into
    /// the encrypted backup. With none, OpenAI is not asked.
    /// </summary>
    public FastSyncResult Sync(IEnumerable<JsonNode?>? credentials = null)
    {
        var path = _home().PatchPath;
        if (!File.Exists(path)) { return Empty(string.Empty); }
        var creds = credentials?.ToList();
        try { _ = _routes?.UpgradeEmptyCodexRoute(creds); }
        catch (DshException ex) { _log?.Warn("未能补全 openai-codex 路由：" + ex.Message); }

        ProfilePatchDocument doc;
        try { doc = ProfilePatchDocument.Load(path); }
        catch (DshException ex) { return Empty("没有处理 Codex 模型：" + ex.Message); }
        var present = doc.GetCodexRouteModels();
        if (present.Count == 0) { return Empty(string.Empty); }
        var presentIds = present.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

        // 1) what OpenAI offers this account
        var (selected, fetchNote) = Fetch(creds);
        var catalog = _catalog();

        // 2) what is ours: the record, or (nothing recorded yet) what 0.1.3/0.1.4 wrote; Fast twins are recognised by their shape
        var recorded = _ownership.Load(_home());
        var owned = new HashSet<string>(StringComparer.Ordinal);
        if (recorded is not null) { owned.UnionWith(recorded.Where(presentIds.Contains)); }
        else { owned.UnionWith(present.Where(m => LegacyIdOnly.Contains(m.Id) && IsIdOnly(m)).Select(m => m.Id)); }
        owned.UnionWith(present.Where(m => IsTwin(m, new HashSet<string>())).Select(m => m.Id));
        var ownedBefore = owned.ToHashSet(StringComparer.Ordinal);

        // 3) what should be there
        var selectedIds = selected?.Select(m => m.Slug).ToList();
        var twinBases = selected is not null
            ? selected.Where(m => m.SupportsFast).Select(m => m.Slug).ToList()
            : CodexBuiltInModels.Ids.Where(presentIds.Contains).ToList();
        var baseAdds = selected?.Where(m => !presentIds.Contains(m.Slug)).ToList() ?? new List<CodexAccountModel>();

        // 4) the component: only worth having while there is a model to give a twin
        FastInstallResult? component = null;
        var usable = false;
        var note = string.Empty;
        if (twinBases.Count > 0)
        {
            try
            {
                component = _installer.Install(creds);
                usable = true;
            }
            catch (DshException ex)
            {
                note = "没有安装 Fast 组件：" + ex.Message;
                _log?.Warn(note);
            }
        }
        else
        {
            usable = _installer.GetStatus().State == FastComponentState.Installed;
        }

        // 5) entries to append: new base models first, then the missing twins
        var apiBySlug = selected?.ToDictionary(m => m.Slug, StringComparer.Ordinal) ?? new Dictionary<string, CodexAccountModel>();
        var entries = new List<JsonObject>();
        foreach (var m in baseAdds) { entries.Add(BaseEntry(m, catalog)); }
        if (usable)
        {
            foreach (var b in twinBases)
            {
                if (presentIds.Contains(b + FastSuffix)) { continue; }
                var userEntry = present.FirstOrDefault(p => p.Id == b)?.Entry;
                entries.Add(BuildEntry(b, userEntry, catalog.GetValueOrDefault(b), apiBySlug.GetValueOrDefault(b)));
            }
        }

        // 6) entries to remove: ours that no longer belong
        var doomed = new HashSet<string>(StringComparer.Ordinal);
        if (selectedIds is { Count: > 0 })
        {
            foreach (var m in present)
            {
                if (IsFastShape(m)) { continue; } // twins are decided below
                if (owned.Contains(m.Id) && !selectedIds.Contains(m.Id)) { doomed.Add(m.Id); }
            }
        }

        var remainingBases = presentIds.Concat(baseAdds.Select(a => a.Slug)).Where(i => !doomed.Contains(i)).ToHashSet(StringComparer.Ordinal);
        foreach (var m in present.Where(m => IsTwin(m, owned)))
        {
            var baseId = m.Id[..^FastSuffix.Length];
            var unusable = !usable || !remainingBases.Contains(baseId) || selected is not null && !twinBases.Contains(baseId);
            if (unusable) { doomed.Add(m.Id); }
        }

        var keptReferenced = new List<string>();
        if (doomed.Count > 0)
        {
            var referenced = ReferencedCodexModels(doc.Text);
            foreach (var id in doomed.Where(referenced.Contains).ToList())
            {
                doomed.Remove(id);
                keptReferenced.Add(id);
                // A kept twin runs its base model, so the base stays with it.
                if (id.EndsWith(FastSuffix, StringComparison.Ordinal)) { doomed.Remove(id[..^FastSuffix.Length]); }
            }
        }

        // 7) write
        var added = new List<string>();
        var removed = new List<string>();
        if (entries.Count > 0 || doomed.Count > 0)
        {
            var backup = _backups is null ? string.Empty : _backups.Save(creds ?? new List<JsonNode?>(), path, doc.Text);
            if (backup.Length > 0) { _log?.Info("已生成加密备份（调整 Codex 模型）：" + backup); }
            ProfilePatchDocument.Update(path, d =>
            {
                var changed = false;
                var current = d.GetCodexRouteModels();
                var fresh = entries.Where(e => current.All(m => m.Id != ProviderViewMapper.ReadString(e, "id"))).ToList();
                if (fresh.Count > 0)
                {
                    d.AppendCodexRouteModels(fresh);
                    added.AddRange(fresh.Select(e => ProviderViewMapper.ReadString(e, "id")!));
                    changed = true;
                }

                if (doomed.Count > 0)
                {
                    var gone = d.GetCodexRouteModels().Where(m => doomed.Contains(m.Id)).Select(m => m.Id).ToList();
                    if (d.RemoveCodexRouteModels(m => doomed.Contains(m.Id)) > 0)
                    {
                        removed.AddRange(gone);
                        RemoveSubagentRoutesTo(d, gone);
                        changed = true;
                    }
                }

                return changed;
            });
        }

        foreach (var id in keptReferenced) { _log?.Warn("没有移除 Codex 模型 " + id + "：dsh 的设置（例如默认模型）还在引用它。"); }
        if (added.Count > 0) { _log?.Info("已为 openai-codex 路由加入模型：" + string.Join("、", added) + "。"); }
        if (removed.Count > 0) { _log?.Info("已从 openai-codex 路由移除本工具写入的模型：" + string.Join("、", removed) + "（OpenAI 不再提供，或 Fast 组件不可用）。"); }

        // 8) remember what is ours now
        var ownedAfter = ownedBefore.Where(i => !removed.Contains(i)).Concat(added).ToHashSet(StringComparer.Ordinal);
        if (recorded is null || !ownedAfter.SetEquals(recorded)) { _ownership.Save(_home(), ownedAfter); }

        return new FastSyncResult(component, added, removed, note)
        {
            FetchedModels = selectedIds,
            FetchNote = selected is null ? fetchNote : string.Empty,
            KeptBecauseReferenced = keptReferenced,
        };
    }

    private static FastSyncResult Empty(string note) => new(null, Array.Empty<string>(), Array.Empty<string>(), note);

    /// <summary>
    /// The sub-agent list this program fills (<c>allowedModels</c>) must not keep pointing at a model it just took out of the route, so
    /// those items go with it (a list left empty is refilled by the sub-agent merge that follows).
    /// </summary>
    private static void RemoveSubagentRoutesTo(ProfilePatchDocument doc, IReadOnlyCollection<string> models)
    {
        for (var guard = 0; guard < 64; guard++)
        {
            var next = doc.FindReferences().FirstOrDefault(r => r.EntryId == ProfilePatchDocument.SubagentEntryId && r.IsListItem
                && r.Provider == ProviderViewMapper.CodexRouteId && models.Contains(r.Model));
            if (next is null) { return; }
            doc.RemoveReference(next);
        }
    }

    // ---- the account's models ----

    private (IReadOnlyList<CodexAccountModel>? Selected, string Note) Fetch(List<JsonNode?>? credentials)
    {
        if (_client is null || credentials is null) { return (null, string.Empty); }
        var pick = CodexModelCatalogClient.PickToken(credentials, _clock());
        if (pick is null) { return (null, "没有可用的访问令牌（本工具不会刷新令牌）"); }
        CodexModelListResult result;
        try { result = _client.FetchAsync(pick.Access, pick.AccountId).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (null, "请求失败"); }
        if (!result.Ok) { return (null, result.Reason); }
        var selected = CodexModelPolicy.Select(result.Models);
        if (selected.Count == 0) { return (null, "账号的模型清单里没有可提供的模型"); }
        _log?.Info("已从 OpenAI 获取账号可用模型：" + string.Join("、", selected.Select(m => m.Slug)) + "。");
        return (selected, string.Empty);
    }

    /// <summary>Models some setting points at, except this program's own sub-agent list (which is cleaned up with the model).</summary>
    private static HashSet<string> ReferencedCodexModels(string patchText)
    {
        try
        {
            return ReferenceScanner.ScanUserLayer(patchText)
                .Where(r => r.Provider == ProviderViewMapper.CodexRouteId && r.EntryId != ProfilePatchDocument.SubagentEntryId)
                .Select(r => r.Model).ToHashSet(StringComparer.Ordinal);
        }
        catch (DshException) { return new HashSet<string>(StringComparer.Ordinal); }
    }

    /// <summary>The route entry of a base model the route lacks: id only when dsh's catalog knows it, else with explicit facts from OpenAI.</summary>
    internal static JsonObject BaseEntry(CodexAccountModel model, IReadOnlyDictionary<string, JsonObject> catalog)
    {
        var known = catalog.Count > 0 ? catalog.ContainsKey(model.Slug) : CodexBuiltInModels.BundledCatalogIds.Contains(model.Slug);
        if (known) { return new JsonObject { ["id"] = model.Slug }; }
        var built = CodexBuiltInModels.Find(model.Slug);
        var entry = new JsonObject
        {
            ["id"] = model.Slug,
            ["name"] = CodexModelPolicy.NormalizeName(model.DisplayName, model.Slug),
            ["contextWindow"] = model.ContextWindow ?? built?.ContextWindow ?? 272000,
            ["maxTokens"] = built?.MaxTokens ?? CodexBuiltInModels.DefaultMaxTokens,
            ["input"] = new JsonArray(model.Input.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
        };
        var efforts = EffortsFromApi(model.ReasoningEfforts);
        if (efforts is not null) { entry["reasoningEfforts"] = efforts; }
        return entry;
    }

    /// <summary>Levels OpenAI reports, as dsh's <c>reasoningEfforts</c> (each level spelled as itself); levels dsh does not know (ultra) are dropped.</summary>
    internal static JsonObject? EffortsFromApi(IEnumerable<string> efforts)
    {
        var list = efforts.ToList();
        var result = new JsonObject();
        foreach (var level in Levels)
        {
            if (list.Contains(level, StringComparer.Ordinal) && AcceptedLevels.Contains(level)) { result[level] = level; }
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>The user-facing lines for a result, or none when nothing changed and nothing failed.</summary>
    public static IEnumerable<string> Describe(FastSyncResult result)
    {
        if (result.Component is { Changed: true } c)
        {
            yield return (c.Before == FastComponentState.Outdated ? "已更新" : "已安装") + " Codex Fast 组件 " + FastComponentInstaller.PackageName + " " + c.Version + "。";
        }

        var changed = result.AddedModels.Count > 0 || result.RemovedModels.Count > 0;
        if (result.FetchedModels is not null)
        {
            if (changed)
            {
                yield return "已从 OpenAI 获取账号可用模型：" + string.Join("、", result.FetchedModels)
                    + "；新增 " + (result.AddedModels.Count == 0 ? "无" : string.Join("、", result.AddedModels))
                    + "；移除 " + (result.RemovedModels.Count == 0 ? "无" : string.Join("、", result.RemovedModels)) + "。";
            }
        }
        else
        {
            if (result.FetchNote.Length > 0) { yield return "未能获取账号模型清单（" + result.FetchNote + "），保持现有模型不变。"; }
            if (result.AddedModels.Count > 0) { yield return "已为 Codex 路由加入 Fast 模型：" + string.Join("、", result.AddedModels) + "。"; }
            if (result.RemovedModels.Count > 0) { yield return "Fast 组件没有安装，已移除本工具写入的 Fast 模型：" + string.Join("、", result.RemovedModels) + "。"; }
        }

        foreach (var kept in result.KeptBecauseReferenced) { yield return "没有移除 Codex 模型 " + kept + "：dsh 的设置（例如默认模型）还在引用它。"; }
        if (result.ComponentChanged) { yield return RestartAndCostNote; }
        else if (result.AddedModels.Any(a => a.EndsWith(FastSuffix, StringComparison.Ordinal))) { yield return CostNote; }
    }

    // ---- model entries ----

    /// <summary>
    /// The <c>&lt;base&gt;-fast</c> entry: name with " (Fast)", and the base model's contextWindow, maxTokens, input and reasoningEfforts, each
    /// taken from the user's own base entry when it has it, else from dsh's catalog, else from OpenAI's list, else from the built-in table.
    /// </summary>
    internal static JsonObject BuildEntry(string baseId, JsonObject? userEntry, JsonObject? catalog, CodexAccountModel? api = null)
    {
        var table = CodexBuiltInModels.Find(baseId);
        var name = Str(userEntry, "name") ?? Str(catalog, "name") ?? (api is null ? null : CodexModelPolicy.NormalizeName(api.DisplayName, baseId)) ?? table?.Name ?? baseId;
        var contextWindow = Int(userEntry, "contextWindow") ?? Int(catalog, "contextWindow") ?? api?.ContextWindow ?? table?.ContextWindow ?? 272000;
        var maxTokens = Int(userEntry, "maxTokens") ?? Int(catalog, "maxTokens") ?? table?.MaxTokens ?? CodexBuiltInModels.DefaultMaxTokens;
        var input = Strings(userEntry?["input"]) ?? Strings(catalog?["input"]) ?? (api is { Input.Count: > 0 } ? api.Input.ToArray() : null) ?? table?.Input ?? new[] { "text" };

        JsonNode? efforts;
        if (userEntry?["reasoningEfforts"] is { } declared) { efforts = declared.DeepClone(); }
        else if (catalog is not null) { efforts = EffortsOf(catalog); }
        else if (api is not null) { efforts = EffortsFromApi(api.ReasoningEfforts); }
        else if (table is not null) { efforts = EffortsFromLevels(table.Levels.ToDictionary(l => l.Level, l => l.Wire)); }
        else { efforts = null; }

        var entry = new JsonObject
        {
            ["id"] = baseId + FastSuffix,
            ["name"] = name + FastNameSuffix,
            ["contextWindow"] = contextWindow,
            ["maxTokens"] = maxTokens,
            ["input"] = new JsonArray(input.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
        };
        if (efforts is not null) { entry["reasoningEfforts"] = efforts; }
        return entry;
    }

    /// <summary>
    /// dsh's <c>reasoningEfforts</c> for a catalog model: <c>false</c> for a model that does not reason, else every level its
    /// <c>thinkingLevelMap</c> offers with its wire spelling. A level the map maps to null is not offered; a level it does not mention is
    /// offered under its own name when it is one of minimal, low, medium, high, and not offered for off, xhigh, max (pi-ai's own defaulting).
    /// </summary>
    internal static JsonNode? EffortsOf(JsonObject catalogModel)
    {
        if (catalogModel["reasoning"] is JsonValue r && r.TryGetValue<bool>(out var reasoning) && !reasoning) { return JsonValue.Create(false); }
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (catalogModel["thinkingLevelMap"] is JsonObject levels)
        {
            foreach (var (level, wire) in levels) { map[level] = wire is JsonValue v && v.TryGetValue<string>(out var s) ? s : null; }
        }

        return EffortsFromLevels(map);
    }

    internal static JsonObject? EffortsFromLevels(IReadOnlyDictionary<string, string?> map)
    {
        var efforts = new JsonObject();
        foreach (var level in Levels)
        {
            if (map.TryGetValue(level, out var wire))
            {
                if (wire is not null) { efforts[level] = wire; }
            }
            else if (level is "minimal" or "low" or "medium" or "high")
            {
                efforts[level] = level;
            }
        }

        return efforts.Count == 0 ? null : efforts;
    }

    private static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int? Int(JsonObject? o, string key)
        => o?[key] is JsonValue v && v.TryGetValue<long>(out var n) && n is > 0 and <= int.MaxValue ? (int)n : null;

    private static string[]? Strings(JsonNode? node)
    {
        if (node is not JsonArray array) { return null; }
        var list = array.Select(i => i is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToList();
        return list.Count > 0 && list.All(s => !string.IsNullOrWhiteSpace(s)) ? list.Select(s => s!).ToArray() : null;
    }
}
