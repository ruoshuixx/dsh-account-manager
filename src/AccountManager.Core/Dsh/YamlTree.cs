using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace AccountManager.Core.Dsh;

internal enum YKind { Scalar, Map, Seq }

/// <summary>
/// A parsed YAML node that remembers its character span in the source text, so a document can be
/// edited by replacing exactly the text of the entries we own and leaving every other byte alone.
/// Anchors, aliases, tags and multi-document streams are rejected (dsh never writes them).
/// </summary>
internal sealed class YNode
{
    public YKind Kind;
    public string Value = string.Empty;
    public ScalarStyle Style;
    public bool Flow;
    public int Start;
    public int End;
    public List<YEntry> Entries = new();
    public List<YNode> Items = new();

    public bool IsPlain => Kind == YKind.Scalar && Style == ScalarStyle.Plain;
    public bool IsEmptyScalar => IsPlain && Value.Length == 0;
}

internal sealed class YEntry
{
    public YNode Key = null!;
    public YNode Value = null!;
}

internal static partial class YamlTree
{
    /// <summary>Parses the single document of <paramref name="text"/>; null root means no content.</summary>
    public static YNode? Parse(string text, string what)
    {
        var parser = new Parser(new StringReader(text));
        var stack = new Stack<YNode>();
        YNode? root = null;
        var documents = 0;
        try
        {
            while (parser.MoveNext())
            {
                var ev = parser.Current!;
                switch (ev)
                {
                    case DocumentStart:
                        if (++documents > 1) { throw new DshFormatException(what + " contains more than one YAML document"); }
                        break;
                    case AnchorAlias:
                        throw new DshFormatException(what + " uses a YAML alias, which is not supported");
                    case Scalar s:
                        {
                            if (!s.Anchor.IsEmpty || !s.Tag.IsEmpty) { throw new DshFormatException(what + " uses a YAML anchor or tag, which is not supported"); }
                            Attach(new YNode
                            {
                                Kind = YKind.Scalar,
                                Value = s.Value,
                                Style = s.Style,
                                Start = (int)s.Start.Index,
                                End = (int)s.End.Index,
                            }, stack, ref root, what);
                            break;
                        }
                    case MappingStart m:
                        {
                            if (!m.Anchor.IsEmpty || !m.Tag.IsEmpty) { throw new DshFormatException(what + " uses a YAML anchor or tag, which is not supported"); }
                            var node = new YNode { Kind = YKind.Map, Flow = m.Style == MappingStyle.Flow, Start = (int)m.Start.Index, End = (int)m.Start.Index };
                            Attach(node, stack, ref root, what);
                            stack.Push(node);
                            break;
                        }
                    case SequenceStart q:
                        {
                            if (!q.Anchor.IsEmpty || !q.Tag.IsEmpty) { throw new DshFormatException(what + " uses a YAML anchor or tag, which is not supported"); }
                            var node = new YNode { Kind = YKind.Seq, Flow = q.Style == SequenceStyle.Flow, Start = (int)q.Start.Index, End = (int)q.Start.Index };
                            Attach(node, stack, ref root, what);
                            stack.Push(node);
                            break;
                        }
                    case MappingEnd me:
                        Close(stack.Pop(), (int)me.Start.Index, text);
                        break;
                    case SequenceEnd se:
                        Close(stack.Pop(), (int)se.Start.Index, text);
                        break;
                }
            }
        }
        catch (YamlException ex)
        {
            // The parser message may quote source text; report only the position.
            throw new DshFormatException(string.Create(CultureInfo.InvariantCulture,
                $"{what} is not valid YAML (line {ex.Start.Line}, column {ex.Start.Column})"));
        }

        return root;
    }

    private static void Attach(YNode node, Stack<YNode> stack, ref YNode? root, string what)
    {
        if (stack.Count == 0)
        {
            root = node;
            return;
        }

        var parent = stack.Peek();
        if (parent.Kind == YKind.Seq)
        {
            parent.Items.Add(node);
            return;
        }

        // Map: alternate key, value.
        if (parent.Entries.Count > 0 && parent.Entries[^1].Value is null)
        {
            parent.Entries[^1].Value = node;
            var key = parent.Entries[^1].Key;
            if (parent.Entries.Take(parent.Entries.Count - 1).Any(e => e.Key.Kind == YKind.Scalar && key.Kind == YKind.Scalar && e.Key.Value == key.Value))
            {
                throw new DshFormatException(what + " has a duplicate mapping key");
            }
        }
        else
        {
            if (node.Kind != YKind.Scalar) { throw new DshFormatException(what + " uses a complex mapping key, which is not supported"); }
            parent.Entries.Add(new YEntry { Key = node, Value = null! });
        }
    }

    private static void Close(YNode node, int endIndex, string text)
    {
        if (node.Kind == YKind.Map && node.Entries.Count > 0 && node.Entries[^1].Value is null)
        {
            node.Entries[^1].Value = new YNode { Kind = YKind.Scalar, Style = ScalarStyle.Plain, Start = endIndex, End = endIndex };
        }

        if (node.Flow)
        {
            // The end event's mark sits on the closing bracket; the node ends just after it.
            var bracket = node.Kind == YKind.Map ? '}' : ']';
            node.End = endIndex < text.Length && text[endIndex] == bracket ? endIndex + 1
                : endIndex > 0 && text[endIndex - 1] == bracket ? endIndex
                : endIndex;
            return;
        }

        // Block collections end where their last descendant ends: the parser's end mark would
        // include trailing blank lines and comments that belong to the next construct.
        if (node.Kind == YKind.Map && node.Entries.Count > 0) { node.End = node.Entries[^1].Value.End; }
        else if (node.Kind == YKind.Seq && node.Items.Count > 0) { node.End = node.Items[^1].End; }
    }

    // ---- scalar typing (YAML 1.2 core schema, as the `yaml` package used by dsh resolves it) ----

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex IntRegex();

    [GeneratedRegex(@"^0o[0-7]+$")]
    private static partial Regex OctRegex();

    [GeneratedRegex(@"^0x[0-9a-fA-F]+$")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatRegex();

    [GeneratedRegex(@"^[-+]?\.(inf|Inf|INF)$|^\.(nan|NaN|NAN)$")]
    private static partial Regex NonFiniteRegex();

    /// <summary>The JSON value a scalar resolves to; throws for non-finite floats.</summary>
    public static JsonNode? ResolveScalar(YNode scalar, string what)
    {
        if (scalar.Style != ScalarStyle.Plain) { return JsonValue.Create(scalar.Value); }
        var v = scalar.Value;
        switch (v)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE": return JsonValue.Create(true);
            case "false" or "False" or "FALSE": return JsonValue.Create(false);
        }

        if (IntRegex().IsMatch(v))
        {
            return JsonNode.Parse(BigInteger.Parse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
        }

        if (OctRegex().IsMatch(v))
        {
            var n = BigInteger.Zero;
            foreach (var c in v.AsSpan(2)) { n = n * 8 + (c - '0'); }
            return JsonNode.Parse(n.ToString(CultureInfo.InvariantCulture));
        }

        if (HexRegex().IsMatch(v))
        {
            return JsonNode.Parse(BigInteger.Parse("0" + v[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
        }

        if (NonFiniteRegex().IsMatch(v)) { throw new DshFormatException(what + " holds a non-finite number"); }
        if (FloatRegex().IsMatch(v))
        {
            var d = double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsInfinity(d)) { throw new DshFormatException(what + " holds a non-finite number"); }
            return JsonValue.Create(d);
        }

        return JsonValue.Create(v);
    }

    /// <summary>Whether a scalar resolves to a YAML string (what dsh requires of refs and keys).</summary>
    public static bool IsString(YNode scalar)
    {
        if (scalar.Kind != YKind.Scalar) { return false; }
        if (scalar.Style != ScalarStyle.Plain) { return true; }
        return ResolveScalarOrNull(scalar) is JsonValue jv && jv.TryGetValue<string>(out _);
    }

    private static JsonNode? ResolveScalarOrNull(YNode s)
    {
        try { return ResolveScalar(s, "scalar"); }
        catch (DshFormatException) { return null; }
    }

    /// <summary>Converts any node to JSON (maps become objects, sequences arrays).</summary>
    public static JsonNode? ToJson(YNode node, string what)
    {
        switch (node.Kind)
        {
            case YKind.Scalar:
                return ResolveScalar(node, what);
            case YKind.Seq:
                {
                    var arr = new JsonArray();
                    foreach (var item in node.Items) { arr.Add(ToJson(item, what)); }
                    return arr;
                }
            default:
                {
                    var obj = new JsonObject();
                    foreach (var e in node.Entries)
                    {
                        if (obj.ContainsKey(e.Key.Value)) { throw new DshFormatException(what + " has a duplicate mapping key"); }
                        obj[e.Key.Value] = ToJson(e.Value, what);
                    }

                    return obj;
                }
        }
    }
}
