using System.Text.Json.Nodes;

namespace AccountManager.Core.Dsh;

/// <summary>An exact LLM route a child agent may select: a configured provider and one of its models.</summary>
public sealed record SubagentRoute(string Provider, string Model);

/// <summary>What the user layer says about <c>subagent-model-selection-settings</c>.</summary>
public sealed class SubagentSelectionState
{
    public bool EntryExists { get; init; }

    /// <summary><c>config.enabled</c>, or null when it is not set.</summary>
    public bool? Enabled { get; init; }

    public IReadOnlyList<SubagentRoute> Routes { get; init; } = Array.Empty<SubagentRoute>();
}

/// <summary>The outcome of <see cref="ProfilePatchDocument.AddSubagentRoutes"/>.</summary>
public sealed record SubagentChange(IReadOnlyList<SubagentRoute> Added, bool EnabledChanged)
{
    public bool Changed => Added.Count > 0 || EnabledChanged;
}

public sealed partial class ProfilePatchDocument
{
    /// <summary>
    /// The host setting entry that lets a main agent pick another provider/model for a sub-agent
    /// (<c>@deepseek-ai/dsh-tool-subagent/model-selection-settings</c>). Only <c>config.enabled</c> and
    /// <c>config.allowedModels</c> are ours to touch.
    /// </summary>
    public const string SubagentEntryId = "subagent-model-selection-settings";

    private YNode? FindSubagentEntry(YNode? root)
    {
        if (root is null || root.IsEmptyScalar) { return null; }
        foreach (var item in root.Items)
        {
            if (item.Kind != YKind.Map) { continue; }
            var id = item.Entries.FirstOrDefault(e => e.Key.Value == "id");
            if (id is { Value.Kind: YKind.Scalar } && id.Value.Value == SubagentEntryId) { return item; }
        }

        return null;
    }

    /// <summary>Reads the entry; an odd shape throws a <see cref="DshException"/> instead of being guessed at.</summary>
    public SubagentSelectionState GetSubagentSelection()
    {
        var (root, _) = FindItem();
        var entry = FindSubagentEntry(root);
        if (entry is null) { return new SubagentSelectionState(); }
        var config = entry.Entries.FirstOrDefault(e => e.Key.Value == "config")?.Value;
        if (config is null || config.IsEmptyScalar) { return new SubagentSelectionState { EntryExists = true }; }
        if (config.Kind != YKind.Map) { throw Odd("config 不是映射"); }

        bool? enabled = null;
        var enabledEntry = config.Entries.FirstOrDefault(e => e.Key.Value == "enabled");
        if (enabledEntry is not null)
        {
            enabled = enabledEntry.Value.IsPlain && enabledEntry.Value.Value is "true" or "True" or "TRUE" ? true
                : enabledEntry.Value.IsPlain && enabledEntry.Value.Value is "false" or "False" or "FALSE" ? false
                : throw Odd("enabled 不是 true/false");
        }

        var routes = new List<SubagentRoute>();
        var allowed = config.Entries.FirstOrDefault(e => e.Key.Value == "allowedModels")?.Value;
        if (allowed is not null && !allowed.IsEmptyScalar)
        {
            if (allowed.Kind != YKind.Seq) { throw Odd("allowedModels 不是列表"); }
            foreach (var item in allowed.Items)
            {
                var provider = item.Kind == YKind.Map ? item.Entries.FirstOrDefault(e => e.Key.Value == "provider")?.Value : null;
                var model = item.Kind == YKind.Map ? item.Entries.FirstOrDefault(e => e.Key.Value == "model")?.Value : null;
                if (provider is null || model is null || !YamlTree.IsString(provider) || !YamlTree.IsString(model)) { throw Odd("allowedModels 里有不是 provider/model 的项"); }
                routes.Add(new SubagentRoute(provider.Value, model.Value));
            }
        }

        return new SubagentSelectionState { EntryExists = true, Enabled = enabled, Routes = routes };
    }

    private static DshException Odd(string what)
        => new("dsh 的 subagent-model-selection-settings 配置不是本工具能安全修改的形态（" + what + "）；请在 dsh 的“子智能体”设置里自行处理。");

    /// <summary>
    /// Adds routes to <c>allowedModels</c> and makes sure <c>enabled</c> is true, creating the entry (without a
    /// <c>name</c>) when it is missing. Existing items are never changed or removed and nothing else in the file moves.
    /// Duplicates are never written, and an enabled entry is never left with an empty list. Flow-style or otherwise odd
    /// shapes are refused.
    /// </summary>
    public SubagentChange AddSubagentRoutes(IEnumerable<SubagentRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var state = GetSubagentSelection();
        if (state.Routes.GroupBy(r => r).Any(g => g.Count() > 1))
        {
            throw new DshException("dsh 的 allowedModels 里已经有重复的路由，dsh 会拒绝它；请先在 dsh 的“子智能体”设置里去掉重复项。");
        }

        var existing = state.Routes.ToHashSet();
        var added = new List<SubagentRoute>();
        foreach (var r in routes)
        {
            if (string.IsNullOrWhiteSpace(r.Provider) || string.IsNullOrWhiteSpace(r.Model)) { continue; }
            if (existing.Add(r)) { added.Add(r); }
        }

        if (added.Count == 0 && state.Enabled == true) { return new SubagentChange(added, false); }
        if (existing.Count == 0) { throw new DshException("没有可用的子代理路由，不会启用一个空的 allowedModels。"); }

        var nl = YamlText.DetectNewline(_text);
        var next = _text;
        var (root, _) = FindItem();
        var entry = FindSubagentEntry(root);
        if (entry is null)
        {
            var item = new JsonObject
            {
                ["id"] = SubagentEntryId,
                ["config"] = new JsonObject { ["enabled"] = true, ["allowedModels"] = RouteArray(added) },
            };
            if (root is null)
            {
                var prefix = _text.Length > 0 && _text[^1] != '\n' ? nl : string.Empty;
                next = _text + prefix + YamlText.RenderSeqItem(item, 0, nl) + nl;
            }
            else
            {
                if (root.Flow) { throw Odd("文件是行内列表"); }
                var indent = YamlText.Column(_text, root.Start);
                var after = YamlText.LineEnd(_text, root.End);
                next = _text.Insert(after, nl + new string(' ', indent) + YamlText.RenderSeqItem(item, indent, nl));
            }
        }
        else
        {
            if (entry.Flow) { throw Odd("条目是行内映射"); }
            next = EditExistingSubagentEntry(entry, added, state, nl);
        }

        _ = ParseCheck(next);
        var before = _text;
        _text = next;
        var after2 = GetSubagentSelection();
        if (after2.Enabled != true || !added.All(r => after2.Routes.Contains(r)) || after2.Routes.GroupBy(r => r).Any(g => g.Count() > 1))
        {
            _text = before;
            throw new DshException("写入后的 subagent-model-selection-settings 校验失败，已放弃修改。");
        }

        return new SubagentChange(added, state.Enabled != true);
    }

    private static JsonArray RouteArray(IEnumerable<SubagentRoute> routes)
    {
        var array = new JsonArray();
        foreach (var r in routes) { array.Add(new JsonObject { ["provider"] = r.Provider, ["model"] = r.Model }); }
        return array;
    }

    private string EditExistingSubagentEntry(YNode entry, List<SubagentRoute> added, SubagentSelectionState state, string nl)
    {
        var config = entry.Entries.FirstOrDefault(e => e.Key.Value == "config")?.Value;
        var all = state.Routes.Concat(added).ToList();
        if (config is null || config.IsEmptyScalar || config.Kind == YKind.Map && config.Flow && config.Entries.Count == 0)
        {
            // No config yet: it holds exactly the new settings.
            var value = new JsonObject { ["enabled"] = true, ["allowedModels"] = RouteArray(all) };
            return YamlText.SetPath(_text, entry, new[] { "config" }, value, What);
        }

        if (config.Kind != YKind.Map || config.Flow) { throw Odd("config 是行内映射"); }
        var text = _text;

        // 1) enabled: true
        var enabledEntry = config.Entries.FirstOrDefault(e => e.Key.Value == "enabled");
        if (enabledEntry is null) { text = YamlText.SetPath(text, entry, new[] { "config", "enabled" }, JsonValue.Create(true), What); }
        else if (state.Enabled != true)
        {
            var v = enabledEntry.Value;
            text = text.Remove(v.Start, v.End - v.Start).Insert(v.Start, "true");
        }

        // 2) allowedModels: append to a block list, or write the list when there is none yet
        var parsed = YamlTree.Parse(text, What)!;
        var entry2 = FindSubagentEntry(parsed)!;
        var config2 = entry2.Entries.First(e => e.Key.Value == "config").Value;
        var allowed = config2.Entries.FirstOrDefault(e => e.Key.Value == "allowedModels")?.Value;
        if (added.Count == 0) { return text; }
        if (allowed is null || allowed.IsEmptyScalar || allowed.Kind == YKind.Seq && allowed.Items.Count == 0 && allowed.Flow)
        {
            return YamlText.SetPath(text, entry2, new[] { "config", "allowedModels" }, RouteArray(all), What);
        }

        if (allowed.Kind != YKind.Seq || allowed.Flow || allowed.Items.Any(i => i.Kind != YKind.Map || i.Flow)) { throw Odd("allowedModels 是行内列表"); }
        var first = allowed.Items[0];
        var dash = first.Start;
        while (dash > 0 && text[dash - 1] is ' ' or '\t') { dash--; }
        if (dash == 0 || text[dash - 1] != '-') { throw Odd("无法定位 allowedModels 的列表项"); }
        dash--;
        var dashColumn = YamlText.Column(text, dash);
        var sb = new System.Text.StringBuilder();
        foreach (var r in added)
        {
            sb.Append(nl).Append(' ', dashColumn).Append("- provider: ").Append(YamlText.RenderString(r.Provider))
              .Append(nl).Append(' ', dashColumn + 2).Append("model: ").Append(YamlText.RenderString(r.Model));
        }

        var at = YamlText.LineEnd(text, allowed.End);
        return text.Insert(at, sb.ToString());
    }
}
