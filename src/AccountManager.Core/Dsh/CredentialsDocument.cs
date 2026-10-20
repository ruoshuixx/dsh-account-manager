using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AccountManager.Core.Dsh;

/// <summary>One entry of the <c>records</c> section: an api-key record or a grant with a JSON payload.</summary>
public sealed class CredentialRecord
{
    public const string ApiKeyKind = "api-key";
    public const string GrantKind = "grant";

    public string Kind { get; init; } = string.Empty;

    /// <summary>api-key records: the optional key value.</summary>
    public string? Key { get; init; }

    /// <summary>api-key records: the optional provider environment (name to non-empty value), in file order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Env { get; init; }

    /// <summary>grant records: the payload, exactly as stored (a clone is returned by the document accessors).</summary>
    public JsonNode? Payload { get; init; }
}

/// <summary>
/// Strict model of dsh's <c>$DSH_HOME/.credentials.yaml</c> (version 1): <c>refs</c> (name to string),
/// <c>records</c> (<c>&lt;scope&gt;/&lt;id&gt;</c> to an api-key or grant record) and ignored foreign top-level keys.
/// The document keeps its source text and edits it in place: a mutation replaces only the text of the entry it
/// changes, so every other entry (other tools' refs and records, comments, unknown top-level keys) stays
/// byte-identical and in order. Parsing mirrors dsh's rejections: no unversioned non-empty document, only
/// the known record kinds and fields, string refs and POSIX-identifier names.
/// </summary>
public sealed partial class CredentialsDocument
{
    public const int Version = 1;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex RefNameRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*$")]
    private static partial Regex KeySegmentRegex();

    private readonly List<KeyValuePair<string, string>> _refs = new();
    private readonly List<KeyValuePair<string, CredentialRecord>> _records = new();
    private readonly List<string> _ignoredKeys = new();
    private string _text;

    private CredentialsDocument(string path, string text)
    {
        Path = path;
        _text = text;
        Reparse();
    }

    public string Path { get; }

    /// <summary>The current document text (a snapshot of what Save/Update would write).</summary>
    public string Text => _text;

    public IReadOnlyList<KeyValuePair<string, string>> Refs => _refs;

    public IReadOnlyList<KeyValuePair<string, CredentialRecord>> Records => _records;

    /// <summary>Foreign top-level keys; dsh ignores them and so do we, but they are preserved.</summary>
    public IReadOnlyList<string> IgnoredKeys => _ignoredKeys;

    /// <summary>A document with no file behind it yet.</summary>
    public static CredentialsDocument Empty(string path) => new(path, string.Empty);

    /// <summary>Parses <paramref name="text"/> strictly; <paramref name="path"/> is only used in messages.</summary>
    public static CredentialsDocument Parse(string text, string path) => new(path, text);

    /// <summary>Loads the file strictly; a missing file is an empty document.</summary>
    public static CredentialsDocument Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return Empty(path); }
        return new CredentialsDocument(path, text);
    }

    // ---- read helpers ----

    public string? GetRef(string name)
    {
        foreach (var r in _refs) { if (r.Key == name) { return r.Value; } }
        return null;
    }

    public CredentialRecord? GetRecord(string key)
    {
        foreach (var r in _records) { if (r.Key == key) { return r.Value; } }
        return null;
    }

    /// <summary>A deep copy of a grant record's payload, or null when absent or not a grant.</summary>
    public JsonNode? GetGrantPayload(string key)
        => GetRecord(key) is { Kind: CredentialRecord.GrantKind } rec ? rec.Payload?.DeepClone() : null;

    public static bool IsValidRefName(string name) => RefNameRegex().IsMatch(name);

    public static bool IsValidRecordKey(string key)
    {
        var parts = key.Split('/');
        return parts.Length == 2 && KeySegmentRegex().IsMatch(parts[0]) && KeySegmentRegex().IsMatch(parts[1]);
    }

    // ---- mutations (each returns whether the text changed) ----

    public bool SetRef(string name, string value)
    {
        if (!IsValidRefName(name)) { throw new ArgumentException("Invalid credential ref name.", nameof(name)); }
        if (string.IsNullOrEmpty(value)) { throw new ArgumentException("A ref value must not be empty; remove the ref instead.", nameof(value)); }
        return Apply(new[] { "refs", name }, JsonValue.Create(value));
    }

    public bool RemoveRef(string name) => Apply(new[] { "refs", name }, null);

    public bool SetGrant(string key, JsonNode? payload)
    {
        RequireValidKey(key);
        AssertStorablePayload(payload);
        var rec = new JsonObject { ["kind"] = CredentialRecord.GrantKind, ["payload"] = payload?.DeepClone() };
        return Apply(new[] { "records", key }, rec);
    }

    public bool SetApiKey(string key, string? apiKey, IEnumerable<KeyValuePair<string, string>>? env = null)
    {
        RequireValidKey(key);
        if (apiKey is { Length: 0 }) { throw new ArgumentException("An api-key must not be empty; pass null to omit it.", nameof(apiKey)); }
        var rec = new JsonObject { ["kind"] = CredentialRecord.ApiKeyKind };
        if (apiKey is not null) { rec["key"] = apiKey; }
        if (env is not null)
        {
            var e = new JsonObject();
            foreach (var (name, value) in env)
            {
                if (!IsValidRefName(name) || string.IsNullOrEmpty(value)) { throw new ArgumentException("Invalid env entry.", nameof(env)); }
                e[name] = value;
            }

            if (e.Count > 0) { rec["env"] = e; }
        }

        return Apply(new[] { "records", key }, rec);
    }

    public bool RemoveRecord(string key) => Apply(new[] { "records", key }, null);

    private static void RequireValidKey(string key)
    {
        if (!IsValidRecordKey(key)) { throw new ArgumentException("Credential key must be \"<scope>/<id>\" of lowercase hyphenated segments.", nameof(key)); }
    }

    private static void AssertStorablePayload(JsonNode? payload)
    {
        if (payload is null) { return; }
        if (payload is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d) && !double.IsFinite(d)) { throw new ArgumentException("Payload holds a non-finite number."); }
            return;
        }

        if (payload is JsonObject o) { foreach (var kv in o) { AssertStorablePayload(kv.Value); } }
        else if (payload is JsonArray a) { foreach (var item in a) { AssertStorablePayload(item); } }
    }

    private bool Apply(string[] path, JsonNode? value)
    {
        var before = _text;
        var what = DescribePath();
        var work = _text;
        var root = YamlTree.Parse(work, what);

        if (root is null || root.Kind == YKind.Scalar && root.IsEmptyScalar || root.Kind == YKind.Map && root.Entries.Count == 0)
        {
            if (value is null) { return false; }
            var nl = YamlText.DetectNewline(work);
            if (work.Length > 0 && work[^1] != '\n') { work += nl; }
            work += "version: " + Version + nl;
            root = YamlTree.Parse(work, what);
        }

        if (root is null || root.Kind != YKind.Map) { throw new DshFormatException(what + " must be a mapping"); }

        if (root.Flow)
        {
            // A hand-written flow document: re-render it as block style (comments in it are lost).
            var json = (JsonObject)YamlTree.ToJson(root, what)!;
            work = YamlText.RenderMapDocument(json, YamlText.DetectNewline(work));
            root = YamlTree.Parse(work, what)!;
        }

        if (value is not null && CurrentAt(root, path, what) is { } current && JsonNode.DeepEquals(current, value)) { return false; }
        work = YamlText.SetPath(work, root, path, value, what);
        if (work == before) { return false; }

        // Never keep an edit we could not read back (dsh would refuse the file).
        Validate(work, Path);
        _text = work;
        Reparse();
        return true;
    }

    private static JsonNode? CurrentAt(YNode root, string[] path, string what)
    {
        var node = root;
        foreach (var key in path)
        {
            if (node.Kind != YKind.Map) { return null; }
            var entry = node.Entries.FirstOrDefault(e => e.Key.Value == key);
            if (entry is null) { return null; }
            node = entry.Value;
        }

        return YamlTree.ToJson(node, what);
    }

    private string DescribePath() => "credentials document " + System.IO.Path.GetFileName(Path);

    // ---- persistence ----

    /// <summary>
    /// Locked read-modify-write: acquires the lock (dsh's 30 s document wait), re-reads the file, hands the fresh
    /// document to <paramref name="mutate"/> and writes atomically (0600, directory 0700) only when it returns true.
    /// A file that fails strict parsing is never overwritten.
    /// </summary>
    public static T Update<T>(string path, Func<CredentialsDocument, (bool Changed, T Result)> mutate, int lockWaitMs = DshFileLock.DocumentWaitMs)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var full = System.IO.Path.GetFullPath(path);
        EnsureDirectory(full);
        return DshFileLock.Run(full, () =>
        {
            var doc = Load(full);
            var (changed, result) = mutate(doc);
            if (changed) { doc.WriteLocked(); }
            return result;
        }, lockWaitMs);
    }

    public static bool Update(string path, Func<CredentialsDocument, bool> mutate, int lockWaitMs = DshFileLock.DocumentWaitMs)
        => Update(path, doc => { var changed = mutate(doc); return (changed, changed); }, lockWaitMs);

    private static void EnsureDirectory(string full)
    {
        var dir = System.IO.Path.GetDirectoryName(full)!;
        if (Directory.Exists(dir)) { return; }
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(dir); }
        else { Directory.CreateDirectory(dir, DshAtomicWrite.PrivateDirectoryMode); }
    }

    private void WriteLocked()
    {
        Validate(_text, Path);
        DshAtomicWrite.WriteText(Path, _text);
    }

    // ---- strict parsing ----

    private void Reparse()
    {
        var parsed = ParseStrict(_text, Path);
        _refs.Clear();
        _refs.AddRange(parsed.Refs);
        _records.Clear();
        _records.AddRange(parsed.Records);
        _ignoredKeys.Clear();
        _ignoredKeys.AddRange(parsed.Ignored);
    }

    private static void Validate(string text, string path) => _ = ParseStrict(text, path);

    private static (List<KeyValuePair<string, string>> Refs, List<KeyValuePair<string, CredentialRecord>> Records, List<string> Ignored)
        ParseStrict(string text, string path)
    {
        var file = System.IO.Path.GetFileName(path);
        var what = "credentials document " + file;
        var refs = new List<KeyValuePair<string, string>>();
        var records = new List<KeyValuePair<string, CredentialRecord>>();
        var ignored = new List<string>();

        var root = YamlTree.Parse(text, what);
        if (root is null || root.IsEmptyScalar) { return (refs, records, ignored); }
        if (root.Kind != YKind.Map) { throw new DshFormatException(what + " must be a mapping"); }
        if (root.Entries.Count == 0) { return (refs, records, ignored); }

        var version = root.Entries.FirstOrDefault(e => e.Key.Value == "version");
        if (version is null)
        {
            throw new DshFormatException(what + " uses the pre-release flat layout. Add `version: 1` and nest the existing entries under `refs:`.");
        }

        if (version.Value.Kind != YKind.Scalar || YamlTree.ResolveScalar(version.Value, what) is not JsonValue vv
            || !vv.TryGetValue<long>(out var ver) || ver != Version)
        {
            throw new DshFormatException(what + " declares an unsupported version; this build reads version 1");
        }

        foreach (var e in root.Entries)
        {
            switch (e.Key.Value)
            {
                case "version": break;
                case "refs": ParseRefs(e.Value, what, refs); break;
                case "records": ParseRecords(e.Value, what, records); break;
                default: ignored.Add(e.Key.Value); break;
            }
        }

        return (refs, records, ignored);
    }

    private static bool IsEmptySection(YNode n) => n.Kind == YKind.Scalar && (n.IsEmptyScalar || n.IsPlain && n.Value is "~" or "null" or "Null" or "NULL");

    private static void ParseRefs(YNode section, string what, List<KeyValuePair<string, string>> into)
    {
        if (IsEmptySection(section)) { return; }
        if (section.Kind != YKind.Map) { throw new DshFormatException(what + ": \"refs\" must be a mapping"); }
        foreach (var e in section.Entries)
        {
            var name = e.Key.Value;
            if (!RefNameRegex().IsMatch(name)) { throw new DshFormatException(what + ": ref name \"" + name + "\" is not a valid identifier"); }
            if (!YamlTree.IsString(e.Value)) { throw new DshFormatException(what + ": the value for ref \"" + name + "\" must be a string"); }
            if (e.Value.Value.Length == 0) { throw new DshFormatException(what + ": the value for ref \"" + name + "\" is empty; remove the key instead"); }
            into.Add(new(name, e.Value.Value));
        }
    }

    private static void ParseRecords(YNode section, string what, List<KeyValuePair<string, CredentialRecord>> into)
    {
        if (IsEmptySection(section)) { return; }
        if (section.Kind != YKind.Map) { throw new DshFormatException(what + ": \"records\" must be a mapping"); }
        foreach (var e in section.Entries)
        {
            var key = e.Key.Value;
            if (!IsValidRecordKey(key)) { throw new DshFormatException(what + ": credential key \"" + key + "\" must be \"<scope>/<id>\""); }
            into.Add(new(key, ParseRecord(key, e.Value, what)));
        }
    }

    private static CredentialRecord ParseRecord(string key, YNode node, string what)
    {
        if (node.Kind != YKind.Map) { throw new DshFormatException(what + ": record \"" + key + "\" must be a mapping"); }
        var kindEntry = node.Entries.FirstOrDefault(e => e.Key.Value == "kind");
        if (kindEntry is null) { throw new DshFormatException(what + ": record \"" + key + "\" has no kind"); }
        var kind = kindEntry.Value.Kind == YKind.Scalar ? kindEntry.Value.Value : string.Empty;

        if (kind == CredentialRecord.ApiKeyKind)
        {
            AssertFields(key, node, new[] { "kind", "key", "env" }, what);
            string? apiKey = null;
            var ke = node.Entries.FirstOrDefault(e => e.Key.Value == "key");
            if (ke is not null)
            {
                if (!YamlTree.IsString(ke.Value) || ke.Value.Value.Length == 0)
                {
                    throw new DshFormatException(what + ": record \"" + key + "\" has a non-string or empty key");
                }

                apiKey = ke.Value.Value;
            }

            List<KeyValuePair<string, string>>? env = null;
            var ee = node.Entries.FirstOrDefault(e => e.Key.Value == "env");
            if (ee is not null)
            {
                if (ee.Value.Kind != YKind.Map) { throw new DshFormatException(what + ": record \"" + key + "\" has a non-mapping env"); }
                env = new();
                foreach (var pair in ee.Value.Entries)
                {
                    if (!RefNameRegex().IsMatch(pair.Key.Value) || !YamlTree.IsString(pair.Value) || pair.Value.Value.Length == 0)
                    {
                        throw new DshFormatException(what + ": record \"" + key + "\" env \"" + pair.Key.Value + "\" must be a non-empty string");
                    }

                    env.Add(new(pair.Key.Value, pair.Value.Value));
                }
            }

            return new CredentialRecord { Kind = CredentialRecord.ApiKeyKind, Key = apiKey, Env = env };
        }

        if (kind == CredentialRecord.GrantKind)
        {
            AssertFields(key, node, new[] { "kind", "payload" }, what);
            var pe = node.Entries.FirstOrDefault(e => e.Key.Value == "payload");
            if (pe is null) { throw new DshFormatException(what + ": record \"" + key + "\" has no payload"); }
            return new CredentialRecord { Kind = CredentialRecord.GrantKind, Payload = YamlTree.ToJson(pe.Value, what + " record \"" + key + "\" payload") };
        }

        if (kind.Length == 0) { throw new DshFormatException(what + ": record \"" + key + "\" has no kind"); }
        throw new DshFormatException(what + ": record \"" + key + "\" has an unknown kind");
    }

    private static void AssertFields(string key, YNode node, string[] allowed, string what)
    {
        foreach (var e in node.Entries)
        {
            if (!allowed.Contains(e.Key.Value)) { throw new DshFormatException(what + ": record \"" + key + "\" has unknown field \"" + e.Key.Value + "\""); }
        }
    }
}
