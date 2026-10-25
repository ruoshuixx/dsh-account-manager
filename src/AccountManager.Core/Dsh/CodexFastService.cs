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
    /// <summary>The component was installed or updated, so dsh must be restarted before it loads it.</summary>
    public bool ComponentChanged => Component is { Changed: true };

    public bool Changed => ComponentChanged || AddedModels.Count > 0 || RemovedModels.Count > 0;
}

/// <summary>
/// Codex Fast mode without any third-party plugin. dsh has no setting for the service tier, so this tool ships a tiny plugin
/// (<see cref="FastComponentInstaller"/>) that sends a model id ending in <c>-fast</c> as its base model with the priority tier, and
/// lists <c>&lt;base&gt;-fast</c> twins of the official Codex models in the <c>openai-codex</c> route. The twins are only ever present
/// while the component is installed and listed: without it a raw <c>-fast</c> id would be sent to OpenAI, so entries this tool wrote are
/// removed again when the component is not there. Entries the user wrote are never edited or removed.
/// </summary>
public sealed class CodexFastService
{
    public const string FastSuffix = "-fast";
    public const string FastNameSuffix = " (Fast)";

    /// <summary>Said after a change that needs dsh to be restarted, and the cost rule that always applies.</summary>
    public const string RestartAndCostNote = "首次安装需要重启 dsh（完全退出 DSH 再打开）后 Fast 模型才可用；Fast 按官方规则以 2.5 倍消耗订阅额度。";

    /// <summary>Said when only models were added: the component was already installed.</summary>
    public const string CostNote = "Fast 按官方规则以 2.5 倍消耗订阅额度。";

    /// <summary>
    /// Facts of the official models when neither the user's route entry nor dsh's catalog has them, copied from the installed pi-ai
    /// catalog (<c>dist/providers/data/openai-codex.json</c> of @earendil-works/pi-ai 1.3.0 with dsh 0.2.1-rc.2) on 2026-10-25.
    /// <c>Levels</c> is the catalog's <c>thinkingLevelMap</c>: a value of <c>null</c> means the level is not offered.
    /// </summary>
    private static readonly Dictionary<string, (string Name, int ContextWindow, int MaxTokens, string[] Input, (string Level, string? Wire)[] Levels)> BuiltIn = new(StringComparer.Ordinal)
    {
        ["gpt-6.1-sol"] = ("GPT-6.1 Sol", 500000, 128000, new[] { "text", "image" }, new (string, string?)[]
            { ("off", null), ("minimal", "low"), ("low", "low"), ("medium", "medium"), ("high", "high"), ("xhigh", "xhigh"), ("max", "max") }),
        ["gpt-6-astra"] = ("GPT-6 Astra", 272000, 128000, new[] { "text", "image" }, new (string, string?)[]
            { ("off", null), ("minimal", "low"), ("low", "low"), ("medium", "medium"), ("high", "high"), ("xhigh", "xhigh"), ("max", "max") }),
        ["gpt-6-luna"] = ("GPT-6 Luna", 272000, 128000, new[] { "text", "image" }, new (string, string?)[]
            { ("off", "none"), ("minimal", "low"), ("low", "low"), ("medium", "medium"), ("high", "high"), ("xhigh", "xhigh"), ("max", "max") }),
    };

    private static readonly string[] Levels = { "off", "minimal", "low", "medium", "high", "xhigh", "max" };

    private readonly Func<DshHome> _home;
    private readonly BackupService? _backups;
    private readonly AppLog? _log;
    private readonly SubagentRouteService? _routes;
    private readonly FastComponentInstaller _installer;
    private readonly Func<IReadOnlyDictionary<string, JsonObject>> _catalog;

    /// <param name="routes">Used to upgrade an empty <c>openai-codex</c> route to the official models list first.</param>
    /// <param name="catalog">The Codex catalog's chat models by id; defaults to a lookup of the installed dsh runtime.</param>
    public CodexFastService(Func<DshHome> home, BackupService? backups = null, SecretRegistry? secrets = null, AppLog? log = null,
        SubagentRouteService? routes = null, Func<IReadOnlyDictionary<string, JsonObject>>? catalog = null)
    {
        _ = secrets;
        _home = home;
        _backups = backups;
        _log = log;
        _routes = routes;
        _installer = new FastComponentInstaller(home, backups, log);
        _catalog = catalog ?? DefaultCatalog;
    }

    public FastComponentInstaller Installer => _installer;

    private readonly object _pendingGate = new();
    private readonly List<FastSyncResult> _pending = new();

    /// <summary>
    /// Remembers a change made outside an import (the account activation creates the route and syncs Fast there) so the import that
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

    /// <summary>Whether an entry is one this tool wrote: id <c>&lt;official base&gt;-fast</c> and a name ending in " (Fast)".</summary>
    public static bool IsOurFastEntry(CodexRouteModel model)
        => model.Entry is { } entry
           && model.Id.EndsWith(FastSuffix, StringComparison.Ordinal)
           && SubagentRouteService.OfficialCodexModels.Contains(model.Id[..^FastSuffix.Length])
           && ProviderViewMapper.ReadString(entry, "name") is { } name && name.EndsWith(FastNameSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Installs or updates the component and appends the missing <c>&lt;base&gt;-fast</c> models when the <c>openai-codex</c> route lists
    /// official base models; removes this tool's own Fast entries when the component is not installed and listed. Safe to call any time:
    /// it returns an empty result when there is nothing to do and never throws for a file it must not touch (the reason is in the result's
    /// note and the log).
    /// </summary>
    public FastSyncResult Sync(IEnumerable<JsonNode?>? credentialsForBackup = null)
    {
        var path = _home().PatchPath;
        if (!File.Exists(path)) { return Empty(string.Empty); }
        try { _ = _routes?.UpgradeEmptyCodexRoute(credentialsForBackup); }
        catch (DshException ex) { _log?.Warn("未能补全 openai-codex 路由：" + ex.Message); }

        ProfilePatchDocument doc;
        try { doc = ProfilePatchDocument.Load(path); }
        catch (DshException ex) { return Empty("没有处理 Fast 模型：" + ex.Message); }
        var models = doc.GetCodexRouteModels();
        var ours = models.Where(IsOurFastEntry).ToList();
        var baseIds = SubagentRouteService.OfficialCodexModels.Where(b => models.Any(m => m.Id == b)).ToList();
        var missing = baseIds.Where(b => models.All(m => m.Id != b + FastSuffix)).ToList();
        if (baseIds.Count == 0 && ours.Count == 0) { return Empty(string.Empty); }

        // 1) the component: only worth having while the route offers an official base model
        FastInstallResult? component = null;
        var usable = false;
        var note = string.Empty;
        var status = _installer.GetStatus();
        if (baseIds.Count > 0)
        {
            try
            {
                component = _installer.Install(credentialsForBackup);
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
            usable = status.State == FastComponentState.Installed;
        }

        // 2) the models
        var added = new List<string>();
        var removed = new List<string>();
        if (usable && missing.Count > 0)
        {
            var catalog = _catalog();
            var entries = missing.Select(b => BuildEntry(b, models.First(m => m.Id == b).Entry, catalog.GetValueOrDefault(b))).ToList();
            var backup = Backup(path, doc.Text, credentialsForBackup, "加入 Fast 模型");
            var count = 0;
            ProfilePatchDocument.Update(path, d =>
            {
                // Recomputed from the file as it is under the lock.
                var current = d.GetCodexRouteModels();
                var fresh = entries.Where(e => current.All(m => m.Id != ProviderViewMapper.ReadString(e, "id"))).ToList();
                count = fresh.Count == 0 ? 0 : d.AppendCodexRouteModels(fresh);
                added.AddRange(fresh.Select(e => ProviderViewMapper.ReadString(e, "id")!));
                return count > 0;
            });
            if (added.Count > 0) { _log?.Info("已为 openai-codex 路由加入 Fast 模型：" + string.Join("、", added) + "（备份：" + backup + "）。"); }
        }

        // Entries of ours that cannot work: no component, or their base model is no longer listed (the plugin runs the base model).
        var unusable = ours.Where(o => !usable || models.All(m => m.Id != o.Id[..^FastSuffix.Length])).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        if (unusable.Count > 0)
        {
            Backup(path, doc.Text, credentialsForBackup, "移除 Fast 模型");
            ProfilePatchDocument.Update(path, d =>
            {
                bool Doomed(CodexRouteModel m) => IsOurFastEntry(m) && unusable.Contains(m.Id);
                var gone = d.GetCodexRouteModels().Where(Doomed).Select(m => m.Id).ToList();
                var count = d.RemoveCodexRouteModels(Doomed);
                if (count > 0) { removed.AddRange(gone); }
                return count > 0;
            });
            if (removed.Count > 0)
            {
                _log?.Warn((usable ? "它们的基础模型已不在 openai-codex 路由里" : "Fast 组件没有安装或没有列在档案里") + "，已移除本工具写入的 Fast 模型（" + string.Join("、", removed) + "），避免把 -fast 模型名原样发给 OpenAI。");
            }
        }

        return new FastSyncResult(component, added, removed, note);
    }

    private static FastSyncResult Empty(string note) => new(null, Array.Empty<string>(), Array.Empty<string>(), note);

    private string Backup(string path, string text, IEnumerable<JsonNode?>? credentials, string what)
    {
        if (_backups is null) { return string.Empty; }
        var backup = _backups.Save(credentials ?? Array.Empty<JsonNode?>(), path, text);
        _log?.Info("已生成加密备份（" + what + "）：" + backup);
        return backup;
    }

    /// <summary>The user-facing lines for a result, or none when nothing changed.</summary>
    public static IEnumerable<string> Describe(FastSyncResult result)
    {
        if (result.Component is { Changed: true } c)
        {
            yield return (c.Before == FastComponentState.Outdated ? "已更新" : "已安装") + " Codex Fast 组件 " + FastComponentInstaller.PackageName + " " + c.Version + "。";
        }

        if (result.AddedModels.Count > 0) { yield return "已为 Codex 路由加入 Fast 模型：" + string.Join("、", result.AddedModels) + "。"; }
        if (result.RemovedModels.Count > 0) { yield return "Fast 组件没有安装，已移除本工具写入的 Fast 模型：" + string.Join("、", result.RemovedModels) + "。"; }
        if (result.ComponentChanged) { yield return RestartAndCostNote; }
        else if (result.AddedModels.Count > 0) { yield return CostNote; }
    }

    // ---- model entries ----

    /// <summary>
    /// The <c>&lt;base&gt;-fast</c> entry: name with " (Fast)", and the base model's contextWindow, maxTokens, input and reasoningEfforts, each
    /// taken from the user's own base entry when it has it, else from dsh's catalog, else from the built-in table.
    /// </summary>
    internal static JsonObject BuildEntry(string baseId, JsonObject? userEntry, JsonObject? catalog)
    {
        var table = BuiltIn[baseId];
        string name = Str(userEntry, "name") ?? Str(catalog, "name") ?? table.Name;
        var contextWindow = Int(userEntry, "contextWindow") ?? Int(catalog, "contextWindow") ?? table.ContextWindow;
        var maxTokens = Int(userEntry, "maxTokens") ?? Int(catalog, "maxTokens") ?? table.MaxTokens;
        var input = Strings(userEntry?["input"]) ?? Strings(catalog?["input"]) ?? table.Input;

        JsonNode? efforts;
        if (userEntry?["reasoningEfforts"] is { } declared) { efforts = declared.DeepClone(); }
        else if (catalog is not null) { efforts = EffortsOf(catalog); }
        else { efforts = EffortsFromLevels(table.Levels.ToDictionary(l => l.Level, l => l.Wire)); }

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

    private static JsonObject? EffortsFromLevels(IReadOnlyDictionary<string, string?> map)
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
