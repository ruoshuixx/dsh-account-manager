using System.Text.Json.Nodes;

namespace AccountManager.Core.Dsh;

/// <summary>One entry of the <c>openai-codex</c> route's explicit <c>models</c> list: a bare id or a map with an <c>id</c>.</summary>
public sealed record CodexRouteModel(string Id, JsonObject? Entry);

public sealed partial class ProfilePatchDocument
{
    private YNode? FindCodexModelsNode()
    {
        var (_, item) = FindItem();
        if (item is null || item.Kind != YKind.Map) { return null; }
        YNode? node = item;
        foreach (var key in new[] { "config", "providers", ProviderViewMapper.CodexRouteId, "models" })
        {
            if (node is null || node.Kind != YKind.Map) { return null; }
            node = node.Entries.FirstOrDefault(e => e.Key.Value == key)?.Value;
        }

        return node;
    }

    /// <summary>The explicit models of the <c>openai-codex</c> route, in order; empty when the route lists none.</summary>
    public IReadOnlyList<CodexRouteModel> GetCodexRouteModels()
    {
        var result = new List<CodexRouteModel>();
        if (GetProviders()?[ProviderViewMapper.CodexRouteId] is not JsonObject route || route["models"] is not JsonArray models) { return result; }
        foreach (var node in models)
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id)) { result.Add(new CodexRouteModel(id, null)); }
            else if (node is JsonObject o && ProviderViewMapper.ReadString(o, "id") is { Length: > 0 } oid) { result.Add(new CodexRouteModel(oid, (JsonObject)o.DeepClone())); }
        }

        return result;
    }

    private int DashColumnOf(YNode first, string text)
    {
        var dash = first.Start;
        while (dash > 0 && text[dash - 1] is ' ' or '\t') { dash--; }
        if (dash == 0 || text[dash - 1] != '-') { throw new DshException("无法定位 openai-codex 路由 models 列表的列表项；请在 dsh 中自行处理。"); }
        return YamlText.Column(text, dash - 1);
    }

    /// <summary>
    /// Appends entries to the <c>models</c> block list of the <c>openai-codex</c> route, after the last existing item and in the
    /// column of the first one. Existing items and everything else in the file stay byte-identical. A route without a block-style
    /// <c>models</c> list is refused. Returns the number of entries added.
    /// </summary>
    public int AppendCodexRouteModels(IEnumerable<JsonObject> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var wanted = entries.ToList();
        if (wanted.Count == 0) { return 0; }
        var models = FindCodexModelsNode();
        if (models is null || models.Kind != YKind.Seq || models.Items.Count == 0) { throw new DshException("openai-codex 路由没有明确列出 models，本工具不添加 Fast 模型。"); }
        if (models.Flow) { throw new DshException("openai-codex 路由的 models 是行内列表，本工具不修改；请在 dsh 中处理。"); }

        var nl = YamlText.DetectNewline(_text);
        var column = DashColumnOf(models.Items[0], _text);
        var sb = new System.Text.StringBuilder();
        foreach (var entry in wanted) { sb.Append(nl).Append(' ', column).Append(YamlText.RenderSeqItem(entry, column, nl)); }
        var next = _text.Insert(YamlText.LineEnd(_text, models.End), sb.ToString());
        _ = ParseCheck(next);
        var before = _text;
        _text = next;
        var ids = GetCodexRouteModels().Select(m => m.Id).ToList();
        if (!wanted.All(w => ids.Contains(ProviderViewMapper.ReadString(w, "id") ?? string.Empty)))
        {
            _text = before;
            throw new DshException("写入后的 openai-codex 路由校验失败，已放弃修改。");
        }

        return wanted.Count;
    }

    /// <summary>
    /// Removes the <c>models</c> items for which <paramref name="predicate"/> is true (whole lines of block items only). At least one
    /// item always stays: a route is never left with an empty list. Returns the number of items removed.
    /// </summary>
    public int RemoveCodexRouteModels(Func<CodexRouteModel, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var models = FindCodexModelsNode();
        if (models is null || models.Kind != YKind.Seq || models.Flow) { return 0; }
        var parsed = GetCodexRouteModels();
        if (parsed.Count != models.Items.Count) { return 0; }

        var doomed = new List<int>();
        for (var i = 0; i < parsed.Count; i++) { if (predicate(parsed[i])) { doomed.Add(i); } }
        if (doomed.Count == 0) { return 0; }
        if (doomed.Count >= models.Items.Count) { return 0; }

        var text = _text;
        foreach (var index in doomed.OrderByDescending(i => i))
        {
            var item = models.Items[index];
            var dash = item.Start;
            while (dash > 0 && text[dash - 1] is ' ' or '\t') { dash--; }
            if (dash == 0 || text[dash - 1] != '-') { throw new DshException("无法定位 openai-codex 路由 models 列表的列表项；请在 dsh 中自行处理。"); }
            dash--;
            var lineStart = YamlText.LineStart(text, dash);
            if (text.AsSpan(lineStart, dash - lineStart).Trim().Length != 0) { throw new DshException("openai-codex 路由的某个 models 项和别的内容在同一行，本工具不修改；请在 dsh 中处理。"); }
            var lineEnd = YamlText.LineEnd(text, item.End);
            if (lineEnd < text.Length) { lineEnd += text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1; }
            text = text.Remove(lineStart, lineEnd - lineStart);
        }

        _ = ParseCheck(text);
        var before = _text;
        _text = text;
        if (GetCodexRouteModels().Count != parsed.Count - doomed.Count)
        {
            _text = before;
            throw new DshException("移除后的 openai-codex 路由校验失败，已放弃修改。");
        }

        return doomed.Count;
    }
}
