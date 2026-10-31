using System.Text;
using System.Text.RegularExpressions;

namespace AccountManager.Core.Sharing;

/// <summary>
/// A minimal line editor for Codex's <c>config.toml</c>. It only ever touches three things: whole
/// <c>[model_providers.&lt;id&gt;]</c> and <c>[profiles.&lt;id&gt;]</c> tables of the ids it is given, and the top-level
/// <c>profile</c> key. Every other line, comment and blank line stays exactly as it was.
/// </summary>
public sealed class CodexConfigToml
{
    public const string ProvidersTable = "model_providers";
    public const string ProfilesTable = "profiles";

    private static readonly Regex Header = new(@"^\s*\[\[?\s*([^\]]+?)\s*\]\]?\s*(?:#.*)?$", RegexOptions.Compiled);
    private static readonly Regex ProfileKey = new(@"^\s*profile\s*=\s*(.*)$", RegexOptions.Compiled);

    private readonly List<string> _lines;
    private readonly string _newline;

    public CodexConfigToml(string? text)
    {
        text ??= string.Empty;
        _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _lines = text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();
        if (_lines.Count > 0 && _lines[^1].Length == 0) { _lines.RemoveAt(_lines.Count - 1); }
    }

    public override string ToString() => _lines.Count == 0 ? string.Empty : string.Join(_newline, _lines) + _newline;

    /// <summary>The top-level <c>profile</c> value, or null when unset (or not a plain string).</summary>
    public string? Profile
    {
        get
        {
            var index = ProfileLine();
            return index < 0 ? null : ParseString(ProfileKey.Match(_lines[index]).Groups[1].Value);
        }
    }

    /// <summary>Sets (or with null removes) the top-level <c>profile</c> key. Returns whether the text changed.</summary>
    public bool SetProfile(string? name)
    {
        var index = ProfileLine();
        if (name is null)
        {
            if (index < 0) { return false; }
            _lines.RemoveAt(index);
            return true;
        }

        var line = "profile = " + Quote(name);
        if (index >= 0)
        {
            if (_lines[index] == line) { return false; }
            _lines[index] = line;
            return true;
        }

        // After the last non-blank top-level line, i.e. before the first table and the blank lines leading to it.
        var end = FirstHeader();
        var at = end;
        while (at > 0 && _lines[at - 1].Trim().Length == 0) { at--; }
        _lines.Insert(at, line);
        if (at + 1 < _lines.Count && IsHeader(_lines[at + 1])) { _lines.Insert(at + 1, string.Empty); }
        return true;
    }

    public bool HasTable(string table, string id) => FindTable(table + "." + id) >= 0;

    /// <summary>Reads one string value of a table (null when the table or key is missing).</summary>
    public string? GetValue(string table, string id, string key)
    {
        var start = FindTable(table + "." + id);
        if (start < 0) { return null; }
        var pattern = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=\s*(.*)$");
        for (var i = start + 1; i < _lines.Count && !IsHeader(_lines[i]); i++)
        {
            var match = pattern.Match(_lines[i]);
            if (match.Success) { return ParseString(match.Groups[1].Value); }
        }

        return null;
    }

    /// <summary>
    /// Replaces the table with exactly these string values (in order), or with null removes it. A new table goes at the
    /// end of the file. Returns whether the text changed.
    /// </summary>
    public bool SetTable(string table, string id, IReadOnlyList<KeyValuePair<string, string>>? values)
    {
        var name = table + "." + id;
        var start = FindTable(name);
        var block = values is null ? null : BuildBlock(name, values);
        if (start < 0)
        {
            if (block is null) { return false; }
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) { _lines.Add(string.Empty); }
            _lines.AddRange(block);
            return true;
        }

        var end = start + 1;
        while (end < _lines.Count && !IsHeader(_lines[end])) { end++; }
        // Blank lines at the end of the table separate it from the next one; they stay.
        var contentEnd = end;
        while (contentEnd > start + 1 && _lines[contentEnd - 1].Trim().Length == 0) { contentEnd--; }
        var existing = _lines.GetRange(start, contentEnd - start);
        if (block is not null && existing.SequenceEqual(block)) { return false; }

        _lines.RemoveRange(start, block is null ? end - start : contentEnd - start);
        if (block is not null) { _lines.InsertRange(start, block); }
        else
        {
            // Do not leave two blank lines where the table was.
            while (start > 0 && start < _lines.Count && _lines[start - 1].Trim().Length == 0 && _lines[start].Trim().Length == 0) { _lines.RemoveAt(start); }
            while (_lines.Count > 0 && _lines[^1].Trim().Length == 0) { _lines.RemoveAt(_lines.Count - 1); }
        }

        return true;
    }

    private static List<string> BuildBlock(string name, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        var block = new List<string> { "[" + name + "]" };
        block.AddRange(values.Select(v => v.Key + " = " + Quote(v.Value)));
        return block;
    }

    private int ProfileLine()
    {
        var end = FirstHeader();
        for (var i = 0; i < end; i++)
        {
            if (ProfileKey.IsMatch(_lines[i])) { return i; }
        }

        return -1;
    }

    private int FirstHeader()
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (IsHeader(_lines[i])) { return i; }
        }

        return _lines.Count;
    }

    private int FindTable(string name)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var match = Header.Match(_lines[i]);
            if (match.Success && !_lines[i].TrimStart().StartsWith("[[", StringComparison.Ordinal) && NormalizeName(match.Groups[1].Value) == name) { return i; }
        }

        return -1;
    }

    private static bool IsHeader(string line) => Header.IsMatch(line);

    /// <summary><c>model_providers . "id"</c> and <c>model_providers.id</c> are the same table.</summary>
    private static string NormalizeName(string raw)
        => string.Join(".", raw.Split('.').Select(part => part.Trim().Trim('"', '\'')));

    internal static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) { builder.Append("\\u").Append(((int)c).ToString("X4")); }
                    else { builder.Append(c); }
                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary>A basic or literal TOML string with an optional trailing comment; null for anything else.</summary>
    internal static string? ParseString(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith('\''))
        {
            var close = raw.IndexOf('\'', 1);
            return close > 0 ? raw[1..close] : null;
        }

        if (!raw.StartsWith('"')) { return null; }
        var builder = new StringBuilder();
        for (var i = 1; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '"') { return builder.ToString(); }
            if (c != '\\' || i + 1 >= raw.Length) { builder.Append(c); continue; }
            var next = raw[++i];
            switch (next)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case 'u' when i + 4 < raw.Length:
                    builder.Append((char)Convert.ToInt32(raw.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                default: builder.Append(next); break;
            }
        }

        return null;
    }
}
