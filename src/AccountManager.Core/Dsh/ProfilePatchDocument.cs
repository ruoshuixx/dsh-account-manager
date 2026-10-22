using System.Text;
using System.Text.Json.Nodes;

namespace AccountManager.Core.Dsh;

/// <summary>
/// dsh's per-profile <c>cordis.patch.yml</c>: a YAML sequence of plugin entries (<c>- id: ...</c>). The only
/// part this program owns is <c>config.providers</c> of the <c>llm-pi-ai</c> entry. A write replaces just the
/// text of that mapping entry (located through the parser's character spans) and leaves every other byte of the
/// file, including other entries and comments, identical; a missing entry is appended and a missing file is created.
/// </summary>
public sealed partial class ProfilePatchDocument
{
    public const string PluginId = "llm-pi-ai";

    private string _text;

    private ProfilePatchDocument(string path, string text)
    {
        Path = path;
        _text = text;
        _ = FindItem(); // strict structural check
    }

    public string Path { get; }

    public string Text => _text;

    public static ProfilePatchDocument Empty(string path) => new(path, string.Empty);

    public static ProfilePatchDocument Parse(string text, string path) => new(path, text);

    public static ProfilePatchDocument Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return Empty(path); }
        return new ProfilePatchDocument(path, text);
    }

    private string What => "profile patch " + System.IO.Path.GetFileName(Path);

    private (YNode? Root, YNode? Item) FindItem()
    {
        var root = YamlTree.Parse(_text, What);
        if (root is null || root.IsEmptyScalar) { return (null, null); }
        if (root.Kind != YKind.Seq) { throw new DshFormatException(What + " must be a YAML sequence of plugin entries"); }
        YNode? found = null;
        foreach (var item in root.Items)
        {
            if (item.Kind != YKind.Map) { continue; }
            var id = item.Entries.FirstOrDefault(e => e.Key.Value == "id");
            if (id is { Value.Kind: YKind.Scalar } && id.Value.Value == PluginId) { found = item; break; }
        }

        return (root, found);
    }

    /// <summary>A clone of the <c>llm-pi-ai</c> entry's <c>config.providers</c> map, or null when not present.</summary>
    public JsonObject? GetProviders()
    {
        var (_, item) = FindItem();
        if (item is null) { return null; }
        var json = YamlTree.ToJson(item, What) as JsonObject;
        return (json?["config"] as JsonObject)?["providers"] is JsonObject providers ? (JsonObject)providers.DeepClone() : null;
    }

    /// <summary>A clone of the whole <c>llm-pi-ai</c> entry, or null.</summary>
    public JsonObject? GetEntry()
    {
        var (_, item) = FindItem();
        return item is null ? null : YamlTree.ToJson(item, What) as JsonObject;
    }

    /// <summary>
    /// Sets <c>config.providers</c> of the <c>llm-pi-ai</c> entry. Returns whether the text changed. Everything
    /// outside the replaced <c>providers</c> entry (other entries, other keys of this entry, comments) is untouched.
    /// </summary>
    public bool SetProviders(JsonObject providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var before = _text;
        var nl = YamlText.DetectNewline(_text);
        var (root, item) = FindItem();
        string next;

        if (root is null)
        {
            var prefix = _text.Length > 0 && _text[^1] != '\n' ? nl : string.Empty;
            next = _text + prefix + YamlText.RenderSeqItem(NewEntry(providers), 0, nl) + nl;
        }
        else if (root.Flow)
        {
            // A hand-written flow sequence: re-render the whole file as a block sequence.
            var all = (JsonArray)YamlTree.ToJson(root, What)!;
            JsonObject? target = null;
            foreach (var n in all) { if (n is JsonObject o && o["id"]?.GetValue<string>() == PluginId) { target = o; break; } }
            if (target is null) { all.Add(NewEntry(providers)); }
            else { SetProvidersJson(target, providers); }
            var sb = new StringBuilder();
            foreach (var n in all) { sb.Append(YamlText.RenderSeqItem(n, 0, nl)).Append(nl); }
            next = sb.ToString();
        }
        else if (item is null)
        {
            var indent = YamlText.Column(_text, root.Start);
            var after = YamlText.LineEnd(_text, root.End);
            next = _text.Insert(after, nl + new string(' ', indent) + YamlText.RenderSeqItem(NewEntry(providers), indent, nl));
        }
        else if (item.Flow)
        {
            var json = (JsonObject)YamlTree.ToJson(item, What)!;
            SetProvidersJson(json, providers);
            var indent = YamlText.Column(_text, root.Start);
            var rendered = YamlText.RenderSeqItem(json, indent, nl);
            // The item's own span starts after its dash.
            var dash = _text.LastIndexOf('-', item.Start);
            next = _text.Remove(dash, item.End - dash).Insert(dash, rendered);
        }
        else
        {
            next = YamlText.SetPath(_text, item, new[] { "config", "providers" }, providers, What);
        }

        if (next == before) { return false; }
        _ = ParseCheck(next);
        _text = next;
        return true;
    }

    /// <summary>
    /// Sets (or, with null, removes) one provider below <c>config.providers</c> of the <c>llm-pi-ai</c> entry,
    /// replacing only that provider's text. Other providers, other keys and comments stay byte-identical.
    /// Returns whether the text changed.
    /// </summary>
    public bool SetProvider(string providerId, JsonObject? provider)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        var (root, item) = FindItem();
        if (root is { Flow: false } && item is { Flow: false, Entries.Count: > 0 })
        {
            var next = provider is null
                ? YamlText.RemovePath(_text, item, new[] { "config", "providers", providerId }, What)
                : YamlText.SetPath(_text, item, new[] { "config", "providers", providerId }, provider, What);
            if (next == _text) { return false; }
            _ = ParseCheck(next);
            _text = next;
            return true;
        }

        // No usable block item (absent, empty or flow style): go through the whole providers map.
        var providers = GetProviders() ?? new JsonObject();
        if (provider is null)
        {
            if (!providers.ContainsKey(providerId)) { return false; }
            providers.Remove(providerId);
        }
        else
        {
            providers[providerId] = provider.DeepClone();
        }

        return SetProviders(providers);
    }

    // ---- model references (provider + model pairs elsewhere in the file) ----

    /// <summary>All model references of this file (see <see cref="ReferenceScanner"/>).</summary>
    public IReadOnlyList<ModelReference> FindReferences() => ReferenceScanner.ScanUserLayer(_text);

    private YNode ResolvePath(IReadOnlyList<string> path)
    {
        var node = YamlTree.Parse(_text, What) ?? throw new DshException("配置文件为空，找不到要修改的引用。");
        foreach (var segment in path)
        {
            if (node.Kind == YKind.Seq && int.TryParse(segment, out var index) && index >= 0 && index < node.Items.Count) { node = node.Items[index]; }
            else if (node.Kind == YKind.Map && node.Entries.FirstOrDefault(e => e.Key.Value == segment) is { } entry) { node = entry.Value; }
            else { throw new DshException("配置文件已变化，找不到要修改的引用；请重新检查。"); }
        }

        return node;
    }

    private static YEntry ValueEntry(YNode map, string key, string expected)
    {
        var entry = map.Entries.FirstOrDefault(e => e.Key.Value == key);
        if (entry is null || !YamlTree.IsString(entry.Value) || entry.Value.Value != expected)
        {
            throw new DshException("配置文件已变化（引用的 " + key + " 不再是预期的值）；请重新检查。");
        }

        return entry;
    }

    /// <summary>Points the reference at another provider/model, replacing only those two scalars. Returns whether the text changed.</summary>
    public bool RetargetReference(ModelReference reference, string provider, string model)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) { throw new ArgumentException("provider/model must not be empty."); }
        var node = ResolvePath(reference.Path);
        if (node.Kind != YKind.Map) { throw new DshException("配置文件已变化，引用位置不再是映射；请重新检查。"); }
        var providerEntry = ValueEntry(node, "provider", reference.Provider);
        var modelEntry = ValueEntry(node, "model", reference.Model);
        var edits = new List<(int Start, int End, string Text)>();
        if (provider != reference.Provider) { edits.Add((providerEntry.Value.Start, providerEntry.Value.End, YamlText.RenderString(provider))); }
        if (model != reference.Model) { edits.Add((modelEntry.Value.Start, modelEntry.Value.End, YamlText.RenderString(model))); }
        if (edits.Count == 0) { return false; }
        var next = _text;
        foreach (var (start, end, text) in edits.OrderByDescending(e => e.Start)) { next = next.Remove(start, end - start).Insert(start, text); }
        return Commit(next);
    }

    /// <summary>Removes a list-item reference. A list left empty becomes <c>[]</c>. Single-value references cannot be removed.</summary>
    public bool RemoveReference(ModelReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Path.Count < 2 || !reference.IsListItem) { throw new DshException("这条引用不是列表项，不能直接移除。"); }
        var item = ResolvePath(reference.Path);
        if (item.Kind != YKind.Map) { throw new DshException("配置文件已变化，引用位置不再是映射；请重新检查。"); }
        _ = ValueEntry(item, "provider", reference.Provider);
        _ = ValueEntry(item, "model", reference.Model);
        var parentPath = reference.Path.Take(reference.Path.Count - 1).ToList();
        var seq = ResolvePath(parentPath);
        if (seq.Kind != YKind.Seq || seq.Flow) { throw new DshException("这条引用在行内列表里，本工具不修改；请在 dsh 中处理。"); }

        var nl = YamlText.DetectNewline(_text);
        string next;
        if (seq.Items.Count == 1)
        {
            // Keep the key and make the list explicitly empty.
            var parentOfSeq = ResolvePath(parentPath.Take(parentPath.Count - 1).ToList());
            var entry = parentOfSeq.Kind == YKind.Map ? parentOfSeq.Entries.FirstOrDefault(e => e.Key.Value == parentPath[^1]) : null;
            if (entry is null) { throw new DshException("这条引用的列表没有名字，本工具不修改；请在 dsh 中处理。"); }
            var colon = _text.IndexOf(':', entry.Key.End);
            if (colon < 0) { throw new DshException("无法定位引用所在列表。"); }
            var lastEnd = seq.End;
            while (lastEnd > colon && (_text[lastEnd - 1] == '\n' || _text[lastEnd - 1] == '\r')) { lastEnd--; }
            next = _text.Remove(colon + 1, lastEnd - (colon + 1)).Insert(colon + 1, " []");
        }
        else
        {
            var dash = item.Start;
            while (dash > 0 && _text[dash - 1] is ' ' or '\t') { dash--; }
            if (dash == 0 || _text[dash - 1] != '-') { throw new DshException("无法定位引用所在的列表项。"); }
            dash--;
            var lineStart = YamlText.LineStart(_text, dash);
            if (_text.AsSpan(lineStart, dash - lineStart).Trim().Length != 0) { throw new DshException("这条引用和别的内容在同一行，本工具不修改；请在 dsh 中处理。"); }
            var lineEnd = YamlText.LineEnd(_text, item.End);
            if (lineEnd < _text.Length) { lineEnd += _text[lineEnd] == '\r' && lineEnd + 1 < _text.Length && _text[lineEnd + 1] == '\n' ? 2 : 1; }
            next = _text.Remove(lineStart, lineEnd - lineStart);
        }

        _ = nl;
        return Commit(next);
    }

    private bool Commit(string next)
    {
        if (next == _text) { return false; }
        _ = ParseCheck(next);
        _text = next;
        return true;
    }

    private JsonObject ParseCheck(string text)
    {
        var check = new ProfilePatchDocument(Path, text);
        return check.GetProviders() ?? new JsonObject();
    }

    private static JsonObject NewEntry(JsonObject providers)
        => new() { ["id"] = PluginId, ["config"] = new JsonObject { ["providers"] = providers.DeepClone() } };

    private static void SetProvidersJson(JsonObject entry, JsonObject providers)
    {
        if (entry["config"] is not JsonObject config)
        {
            config = new JsonObject();
            entry["config"] = config;
        }

        config["providers"] = providers.DeepClone();
    }

    // ---- persistence ----

    /// <summary>
    /// Locked read-modify-write (lock file <c>cordis.patch.yml.lock</c>). An existing file is optionally copied to
    /// <paramref name="backupDirectory"/> first. Nothing is written when the mutation returns false or when the
    /// on-disk file fails the structural check.
    /// </summary>
    public static bool Update(string path, Func<ProfilePatchDocument, bool> mutate, string? backupDirectory = null,
        int lockWaitMs = DshFileLock.DocumentWaitMs)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var full = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(full)!;
        if (!Directory.Exists(dir))
        {
            if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(dir); }
            else { Directory.CreateDirectory(dir, DshAtomicWrite.PrivateDirectoryMode); }
        }

        return DshFileLock.Run(full, () =>
        {
            var doc = Load(full);
            if (!mutate(doc)) { return false; }
            if (backupDirectory is not null && File.Exists(full))
            {
                Directory.CreateDirectory(backupDirectory);
                var name = System.IO.Path.GetFileName(full) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + ".bak";
                DshAtomicWrite.WriteBytes(System.IO.Path.Combine(backupDirectory, name), File.ReadAllBytes(full));
            }

            // Keep the file's existing permission bits (it is not a secret store); a new file gets 0644-like defaults.
            var mode = ExistingModeOrDefault(full);
            DshAtomicWrite.WriteText(full, doc.Text, mode, directoryMode: null);
            return true;
        }, lockWaitMs);
    }

    private static UnixFileMode ExistingModeOrDefault(string full)
    {
        const UnixFileMode normal = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        if (OperatingSystem.IsWindows() || !File.Exists(full)) { return normal; }
        try { return File.GetUnixFileMode(full); }
        catch { return normal; }
    }
}
