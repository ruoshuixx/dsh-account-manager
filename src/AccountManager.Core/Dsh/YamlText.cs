using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace AccountManager.Core.Dsh;

/// <summary>Renders JSON values as block-style YAML and edits a YAML document by span replacement.</summary>
internal static class YamlText
{
    // ---- rendering ----

    private static readonly HashSet<string> ReservedPlain = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "true", "false", "yes", "no", "on", "off", "y", "n", "nan", "inf",
    };

    /// <summary>A scalar string, plain when that is unambiguous, otherwise double-quoted.</summary>
    public static string RenderString(string s)
    {
        return CanBePlain(s) ? s : Quote(s);
    }

    private static bool CanBePlain(string s)
    {
        if (s.Length == 0 || !char.IsLetter(s[0])) { return false; }
        if (ReservedPlain.Contains(s)) { return false; }
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsLetterOrDigit(c) || c is '_' or '.' or '/' or '+' or '-' or '=' or '@') { continue; }
            if (c == ' ')
            {
                if (i == s.Length - 1 || s[i + 1] == '#') { return false; }
                continue;
            }

            if (c == ':')
            {
                if (i == s.Length - 1 || s[i + 1] == ' ') { return false; }
                continue;
            }

            return false;
        }

        return true;
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (c < 0x20 || c == 0x7f || c == 0x85 || c == 0x2028 || c == 0x2029)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static bool IsScalar(JsonNode? v) => v is null || v is JsonValue || v is JsonObject { Count: 0 } || v is JsonArray { Count: 0 };

    private static string RenderScalar(JsonNode? v)
    {
        switch (v)
        {
            case null: return "null";
            case JsonObject: return "{}";
            case JsonArray: return "[]";
            case JsonValue jv:
                if (jv.TryGetValue<string>(out var str)) { return RenderString(str); }
                return jv.ToJsonString();
            default: throw new InvalidOperationException();
        }
    }

    /// <summary>
    /// Renders <c>key: value</c> for a mapping entry whose key sits at column <paramref name="indent"/>.
    /// The first line carries no indent (the caller positions it); continuation lines are absolute.
    /// </summary>
    public static string RenderEntry(string key, JsonNode? value, int indent, string nl)
    {
        var sb = new StringBuilder();
        AppendEntry(sb, key, value, indent, nl);
        return sb.ToString();
    }

    private static void AppendEntry(StringBuilder sb, string key, JsonNode? value, int indent, string nl)
    {
        sb.Append(RenderString(key)).Append(':');
        if (IsScalar(value))
        {
            sb.Append(' ').Append(RenderScalar(value));
            return;
        }

        sb.Append(nl);
        if (value is JsonObject obj) { AppendMapBody(sb, obj, indent + 2, nl); }
        else { AppendSeqBody(sb, (JsonArray)value!, indent + 2, nl); }
    }

    private static void AppendMapBody(StringBuilder sb, JsonObject obj, int indent, string nl)
    {
        var first = true;
        foreach (var (k, v) in obj)
        {
            if (!first) { sb.Append(nl); }
            first = false;
            sb.Append(' ', indent);
            AppendEntry(sb, k, v, indent, nl);
        }
    }

    private static void AppendSeqBody(StringBuilder sb, JsonArray arr, int indent, string nl)
    {
        var first = true;
        foreach (var item in arr)
        {
            if (!first) { sb.Append(nl); }
            first = false;
            sb.Append(' ', indent);
            AppendSeqItem(sb, item, indent, nl);
        }
    }

    private static void AppendSeqItem(StringBuilder sb, JsonNode? item, int indent, string nl)
    {
        if (IsScalar(item))
        {
            sb.Append("- ").Append(RenderScalar(item));
        }
        else if (item is JsonObject obj)
        {
            sb.Append("- ");
            var first = true;
            foreach (var (k, v) in obj)
            {
                if (!first) { sb.Append(nl).Append(' ', indent + 2); }
                first = false;
                AppendEntry(sb, k, v, indent + 2, nl);
            }
        }
        else
        {
            sb.Append('-').Append(nl);
            AppendSeqBody(sb, (JsonArray)item!, indent + 2, nl);
        }
    }

    /// <summary>A block sequence item at column <paramref name="indent"/> (first line unindented).</summary>
    public static string RenderSeqItem(JsonNode? item, int indent, string nl)
    {
        var sb = new StringBuilder();
        AppendSeqItem(sb, item, indent, nl);
        return sb.ToString();
    }

    /// <summary>A whole block-style mapping document.</summary>
    public static string RenderMapDocument(JsonObject obj, string nl)
    {
        var sb = new StringBuilder();
        AppendMapBody(sb, obj, 0, nl);
        return sb.Append(nl).ToString();
    }

    // ---- text navigation ----

    public static string DetectNewline(string text)
    {
        var i = text.IndexOf('\n');
        return i > 0 && text[i - 1] == '\r' ? "\r\n" : "\n";
    }

    public static int LineStart(string text, int index)
    {
        while (index > 0 && text[index - 1] != '\n' && text[index - 1] != '\r') { index--; }
        return index;
    }

    public static int LineEnd(string text, int index)
    {
        while (index < text.Length && text[index] != '\n' && text[index] != '\r') { index++; }
        return index;
    }

    public static int Column(string text, int index) => index - LineStart(text, index);

    private static int TrimEndNewlines(string text, int start, int end)
    {
        while (end > start && (text[end - 1] == '\n' || text[end - 1] == '\r')) { end--; }
        return end;
    }

    // ---- editing ----

    /// <summary>
    /// Sets (or, with a null <paramref name="value"/>, removes) the entry at <paramref name="path"/> below the block
    /// mapping <paramref name="map"/>, replacing only the text of that entry. Missing parents are created.
    /// Parents that are empty or flow-style are re-rendered from their parsed content.
    /// </summary>
    public static string SetPath(string text, YNode map, string[] path, JsonNode? value, string what)
    {
        return SetAt(text, map, path, 0, value, what, remove: value is null);
    }

    public static string RemovePath(string text, YNode map, string[] path, string what)
        => SetAt(text, map, path, 0, null, what, remove: true);

    private static string SetAt(string text, YNode map, string[] path, int depth, JsonNode? value, string what, bool remove)
    {
        var nl = DetectNewline(text);
        var key = path[depth];
        var entry = map.Entries.FirstOrDefault(e => e.Key.Value == key);
        var indent = map.Entries.Count > 0 ? Column(text, map.Entries[0].Key.Start) : 0;
        var last = depth == path.Length - 1;

        if (entry is not null && !last && entry.Value.Kind == YKind.Map && !entry.Value.Flow && entry.Value.Entries.Count > 0)
        {
            return SetAt(text, entry.Value, path, depth + 1, value, what, remove);
        }

        if (entry is null)
        {
            if (remove) { return text; }
            var created = BuildNested(path, depth + 1, value);
            var after = LineEnd(text, EntryEnd(text, map.Entries[^1]));
            return text.Insert(after, nl + new string(' ', indent) + RenderEntry(key, created, indent, nl));
        }

        JsonNode? newValue;
        if (last)
        {
            if (remove)
            {
                var ls = LineStart(text, entry.Key.Start);
                if (text.AsSpan(ls, entry.Key.Start - ls).Trim().Length != 0)
                {
                    throw new DshFormatException(what + ": cannot remove an entry that shares its line");
                }

                var le = LineEnd(text, EntryEnd(text, entry));
                if (le < text.Length) { le += text[le] == '\r' && le + 1 < text.Length && text[le + 1] == '\n' ? 2 : 1; }
                return text.Remove(ls, le - ls);
            }

            newValue = value;
        }
        else
        {
            // Existing parent that is null/flow/empty: merge into its parsed content and re-render.
            var existing = YamlTree.ToJson(entry.Value, what);
            if (existing is null) { existing = new JsonObject(); }
            if (existing is not JsonObject obj) { throw new DshFormatException(what + ": '" + key + "' is not a mapping"); }
            if (remove && !ContainsPath(obj, path, depth + 1)) { return text; }
            ApplyJson(obj, path, depth + 1, value, remove);
            newValue = obj;
        }

        var start = entry.Key.Start;
        var end = EntryEnd(text, entry);
        return text.Remove(start, end - start).Insert(start, RenderEntry(key, newValue, indent, nl));
    }

    private static bool ContainsPath(JsonObject obj, string[] path, int depth)
    {
        if (!obj.TryGetPropertyValue(path[depth], out var child)) { return false; }
        return depth == path.Length - 1 || child is JsonObject c && ContainsPath(c, path, depth + 1);
    }

    private static void ApplyJson(JsonObject obj, string[] path, int depth, JsonNode? value, bool remove)
    {
        var key = path[depth];
        if (depth == path.Length - 1)
        {
            if (remove) { obj.Remove(key); } else { obj[key] = value?.DeepClone(); }
            return;
        }

        if (obj[key] is not JsonObject child)
        {
            if (remove) { return; }
            child = new JsonObject();
            obj[key] = child;
        }

        ApplyJson(child, path, depth + 1, value, remove);
    }

    private static JsonNode? BuildNested(string[] path, int depth, JsonNode? value)
    {
        if (depth >= path.Length) { return value?.DeepClone(); }
        var obj = new JsonObject { [path[depth]] = BuildNested(path, depth + 1, value) };
        return obj;
    }

    /// <summary>End of an entry's text: its value's end, trimmed of trailing newlines, covering an empty value's colon.</summary>
    private static int EntryEnd(string text, YEntry e)
    {
        if (e.Value.IsEmptyScalar)
        {
            var i = e.Key.End;
            while (i < text.Length && text[i] != ':' && text[i] != '\n' && text[i] != '\r') { i++; }
            return i < text.Length && text[i] == ':' ? i + 1 : e.Key.End;
        }

        return TrimEndNewlines(text, e.Key.Start, Math.Max(e.Value.End, e.Key.End));
    }
}
