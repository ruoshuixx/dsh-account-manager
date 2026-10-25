using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountManager.Core.Logging;
using AccountManager.Core.Security;

namespace AccountManager.Core.Dsh;

/// <summary>Where the Codex Fast component stands in one dsh profile.</summary>
public enum FastComponentState
{
    /// <summary>Files present with the embedded content and listed in the profile's bundles.</summary>
    Installed,

    /// <summary>The package folder is not there.</summary>
    Missing,

    /// <summary>Files present but older than (or different from) the embedded package.</summary>
    Outdated,

    /// <summary>Files are fine but the profile's <c>dsh.profile.bundles</c> does not list the package.</summary>
    NotListed,
}

/// <summary>The component's status; <see cref="InstalledVersion"/> is null while the folder is missing.</summary>
public sealed record FastComponentStatus(FastComponentState State, string? InstalledVersion, string EmbeddedVersion, string Detail);

/// <summary>What <see cref="FastComponentInstaller.Install"/> did.</summary>
public sealed record FastInstallResult(FastComponentState Before, bool FilesWritten, bool BundleAdded, string Backup, string Version)
{
    public bool Changed => FilesWritten || BundleAdded;
}

/// <summary>
/// Installs the small <c>dsh-account-manager-fast</c> plugin (embedded in this program) into a dsh profile without the dsh CLI,
/// pnpm or a network: real files below <c>profiles/&lt;profile&gt;/node_modules/dsh-account-manager-fast/</c> plus one name appended to
/// <c>dsh.profile.bundles</c> in the profile's <c>package.json</c>. dsh resolves a bundle by name from its installation and then
/// from the profile's <c>node_modules</c>, so no <c>dependencies</c> entry and no lock file edit is needed (a later
/// <c>dsh plugin add/remove/install/prune</c> leaves it alone). The <c>package.json</c> edit is a text edit under dsh's own
/// <c>&lt;file&gt;.lock</c> protocol (the same lock dsh's plugin manager takes), written by atomic replace; every other byte stays.
/// dsh reads its bundle list at start, so a first install takes effect after dsh is fully restarted.
/// </summary>
public sealed class FastComponentInstaller
{
    public const string PackageName = "dsh-account-manager-fast";
    private const string ResourcePrefix = "fast-plugin/";

    private readonly Func<DshHome> _home;
    private readonly BackupService? _backups;
    private readonly AppLog? _log;

    public FastComponentInstaller(Func<DshHome> home, BackupService? backups = null, AppLog? log = null)
    {
        _home = home;
        _backups = backups;
        _log = log;
    }

    public string PackageJsonPath => Path.Combine(_home().ProfileDirectory, "package.json");

    public string PluginDirectory => Path.Combine(_home().ProfileDirectory, "node_modules", PackageName);

    // ---- the embedded package ----

    private static readonly Lazy<IReadOnlyList<(string Path, byte[] Content)>> Embedded = new(LoadEmbedded);

    /// <summary>The embedded package files, relative paths with forward slashes.</summary>
    public static IReadOnlyList<(string Path, byte[] Content)> EmbeddedFiles => Embedded.Value;

    /// <summary>The version in the embedded <c>package.json</c>.</summary>
    public static string EmbeddedVersion => VersionOf(Encoding.UTF8.GetString(EmbeddedFiles.First(f => f.Path == "package.json").Content))
        ?? throw new InvalidOperationException("The embedded Fast component has no version.");

    private static IReadOnlyList<(string Path, byte[] Content)> LoadEmbedded()
    {
        var assembly = typeof(FastComponentInstaller).Assembly;
        var files = new List<(string, byte[])>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            files.Add((name[ResourcePrefix.Length..], buffer.ToArray()));
        }

        if (files.Count == 0) { throw new InvalidOperationException("The Codex Fast component is not embedded in this build."); }
        return files;
    }

    internal static string? VersionOf(string packageJson)
    {
        try
        {
            return JsonNode.Parse(packageJson) is JsonObject o && o["version"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Compares the numeric x.y.z parts (a pre-release tail is ignored); null when either is not a version.</summary>
    internal static int? CompareVersions(string left, string right)
    {
        static Version? Parse(string text)
        {
            var core = text.Split('-', '+')[0];
            return Version.TryParse(core, out var v) ? v : null;
        }

        var a = Parse(left);
        var b = Parse(right);
        return a is null || b is null ? null : a.CompareTo(b);
    }

    // ---- status ----

    /// <summary>
    /// Looks at the profile: <see cref="FastComponentState.Missing"/> when the package folder is absent, <see cref="FastComponentState.Outdated"/>
    /// when its version is older or any embedded file differs (a same-version repair), <see cref="FastComponentState.NotListed"/> when the
    /// bundle list lacks it, else <see cref="FastComponentState.Installed"/>. A newer installed version is left alone.
    /// </summary>
    public FastComponentStatus GetStatus()
    {
        var embedded = EmbeddedVersion;
        var dir = PluginDirectory;
        var manifest = Path.Combine(dir, "package.json");
        if (!File.Exists(manifest)) { return new FastComponentStatus(FastComponentState.Missing, null, embedded, "档案里还没有 Fast 组件。"); }

        string? installed;
        try { installed = VersionOf(File.ReadAllText(manifest)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { installed = null; }
        if (installed is null) { return new FastComponentStatus(FastComponentState.Outdated, null, embedded, "已装的 Fast 组件读不出版本号。"); }

        var order = CompareVersions(installed, embedded);
        var newer = order is > 0;
        if (!newer)
        {
            var different = order is null || order < 0 || EmbeddedFiles.Any(f => !SameContent(Path.Combine(dir, f.Path.Replace('/', Path.DirectorySeparatorChar)), f.Content));
            if (different) { return new FastComponentStatus(FastComponentState.Outdated, installed, embedded, "已装的 Fast 组件 " + installed + " 需要更新到 " + embedded + "。"); }
        }

        if (!IsListed(out var problem))
        {
            return new FastComponentStatus(FastComponentState.NotListed, installed, embedded, problem ?? "档案的 package.json 没有列出 Fast 组件。");
        }

        return new FastComponentStatus(FastComponentState.Installed, installed, embedded, "Fast 组件已安装。");
    }

    private static bool SameContent(string file, byte[] expected)
    {
        try { return File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(expected); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private bool IsListed(out string? problem)
    {
        problem = null;
        var path = PackageJsonPath;
        if (!File.Exists(path)) { problem = "档案还没有 package.json。"; return false; }
        try
        {
            var bundles = PackageJsonBundles.Read(File.ReadAllBytes(path));
            return bundles.Contains(PackageName, StringComparer.Ordinal);
        }
        catch (DshException ex) { problem = ex.Message; return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problem = "读不了档案的 package.json：" + ex.Message; return false; }
    }

    // ---- install ----

    /// <summary>
    /// Brings the profile to the embedded version: writes the files when missing or outdated, then lists the bundle when it is not
    /// listed (after an encrypted backup of <c>package.json</c>). Nothing is written when everything is current. Refuses with a
    /// <see cref="DshException"/> when the profile has no <c>package.json</c> (dsh never started it) or its shape is not the expected one.
    /// </summary>
    public FastInstallResult Install(IEnumerable<JsonNode?>? credentialsForBackup = null)
    {
        var home = _home();
        if (!Directory.Exists(home.ProfileDirectory)) { throw new DshException("没有找到 dsh 档案目录 " + home.ProfileDirectory + "；请先启动一次 dsh。"); }
        var packageJson = PackageJsonPath;
        if (!File.Exists(packageJson))
        {
            throw new DshException("档案 " + home.Profile + " 还没有 package.json（dsh 还没有初始化过它）；请先启动一次 DSH，再导入或重试。");
        }

        // Read-only check first: an unexpected shape is refused before any file is written.
        var originalBytes = File.ReadAllBytes(packageJson);
        var bundles = PackageJsonBundles.Read(originalBytes);

        var status = GetStatus();
        var version = EmbeddedVersion;
        var writeFiles = status.State is FastComponentState.Missing or FastComponentState.Outdated;
        if (writeFiles) { WriteFiles(); }

        var backup = string.Empty;
        var listed = false;
        if (!bundles.Contains(PackageName, StringComparer.Ordinal))
        {
            if (_backups is not null)
            {
                backup = _backups.Save(credentialsForBackup ?? Array.Empty<JsonNode?>(), packageJson, Encoding.UTF8.GetString(originalBytes));
                _log?.Info("已生成加密备份（档案 package.json，列出 Fast 组件前）：" + backup);
            }

            try
            {
                listed = DshFileLock.Run(packageJson, ListUnderLock, waitMs: 15000);
            }
            catch (DshLockTimeoutException)
            {
                throw new DshException("档案的 package.json 正被占用（dsh 或它的插件管理器在写它）；请稍后重试。");
            }
        }

        if (writeFiles || listed)
        {
            var after = GetStatus();
            if (after.State != FastComponentState.Installed)
            {
                throw new DshException("安装 Fast 组件后回读校验失败（" + after.Detail + "）；请检查档案目录 " + home.ProfileDirectory + "。");
            }

            _log?.Info((status.State == FastComponentState.Outdated ? "已更新" : "已安装") + " Codex Fast 组件 " + PackageName + " " + version + "（档案 " + home.Profile + "）；需要完全退出并重新打开 dsh 后才会加载。");
        }

        return new FastInstallResult(status.State, writeFiles, listed, backup, version);
    }

    private bool ListUnderLock()
    {
        var path = PackageJsonPath;
        var bytes = File.ReadAllBytes(path); // read again under the lock: another writer may have changed it meanwhile
        var next = PackageJsonBundles.Append(bytes, PackageName);
        if (next is null) { return false; }
        DshAtomicWrite.WriteBytes(path, next);
        return true;
    }

    private void WriteFiles()
    {
        var dir = PluginDirectory;
        try
        {
            // The manifest goes last: a half-written folder is then still seen as outdated and repaired next time.
            foreach (var (relative, content) in EmbeddedFiles.OrderBy(f => f.Path == "package.json" ? 1 : 0))
            {
                var target = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
                if (SameContent(target, content)) { continue; }
                DshAtomicWrite.WriteBytes(target, content, mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DshException("无法写入 Fast 组件文件（" + ex.Message + "）。如果 dsh 正在运行，请完全退出 DSH 后再试一次。");
        }
    }
}

/// <summary>
/// Reads and edits <c>dsh.profile.bundles</c> in a profile <c>package.json</c> as text: appending a name touches only the bytes
/// around the end of that array, so every other field, the indentation and the line endings stay exactly as they were.
/// </summary>
internal static class PackageJsonBundles
{
    private sealed record Layout(int ArrayStart, int ArrayEnd, List<(int Start, int End)> Items, List<string> Names);

    /// <summary>The listed bundle names; throws <see cref="DshException"/> when the file is not a profile manifest of the expected shape.</summary>
    public static IReadOnlyList<string> Read(byte[] json) => Scan(json).Names;

    /// <summary>The manifest with <paramref name="name"/> appended to the bundle list, or null when it is already listed.</summary>
    public static byte[]? Append(byte[] json, string name)
    {
        var layout = Scan(json);
        if (layout.Names.Contains(name, StringComparer.Ordinal)) { return null; }
        var quoted = JsonSerializer.Serialize(name);
        string insert;
        int at;
        if (layout.Items.Count > 0)
        {
            var last = layout.Items[^1];
            at = last.End;
            if (layout.Items.Count >= 2)
            {
                var previous = layout.Items[^2];
                insert = Encoding.UTF8.GetString(json, previous.End, last.Start - previous.End) + quoted; // "," plus the line break and indent between items
            }
            else
            {
                insert = "," + Encoding.UTF8.GetString(json, layout.ArrayStart + 1, last.Start - layout.ArrayStart - 1) + quoted;
            }
        }
        else
        {
            // An empty list: keep a one-line "[]" one line; a multi-line one gets the new name on its own line.
            var inner = Encoding.UTF8.GetString(json, layout.ArrayStart + 1, layout.ArrayEnd - layout.ArrayStart - 1);
            at = layout.ArrayStart + 1;
            if (!inner.Contains('\n'))
            {
                var replacement = Encoding.UTF8.GetBytes(quoted);
                return Splice(json, at, layout.ArrayEnd - at, replacement);
            }

            var keyIndent = LineIndent(json, layout.ArrayStart);
            var newline = inner.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var replacementText = newline + keyIndent + "  " + quoted + newline + keyIndent;
            return Splice(json, at, layout.ArrayEnd - at, Encoding.UTF8.GetBytes(replacementText));
        }

        return Splice(json, at, 0, Encoding.UTF8.GetBytes(insert));
    }

    private static byte[] Splice(byte[] source, int at, int removed, byte[] insert)
    {
        var result = new byte[source.Length - removed + insert.Length];
        Buffer.BlockCopy(source, 0, result, 0, at);
        Buffer.BlockCopy(insert, 0, result, at, insert.Length);
        Buffer.BlockCopy(source, at + removed, result, at + insert.Length, source.Length - at - removed);
        return result;
    }

    private static string LineIndent(byte[] json, int index)
    {
        var start = index;
        while (start > 0 && json[start - 1] != '\n') { start--; }
        var end = start;
        while (end < json.Length && json[end] is (byte)' ' or (byte)'\t') { end++; }
        return Encoding.UTF8.GetString(json, start, end - start);
    }

    private static Layout Scan(byte[] json)
    {
        var bom = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? 3 : 0;
        var memory = json.AsSpan(bom);
        var names = new string?[8];
        Layout? found = null;
        var arrays = 0;
        var itemStart = -1;
        try
        {
            var reader = new Utf8JsonReader(memory, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            var started = false;
            while (reader.Read())
            {
                var depth = reader.CurrentDepth;
                if (!started)
                {
                    if (reader.TokenType != JsonTokenType.StartObject) { throw Unexpected("顶层不是 JSON 对象"); }
                    started = true;
                    continue;
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        if (depth < names.Length) { names[depth] = reader.GetString(); }
                        break;
                    case JsonTokenType.StartArray when depth == 3 && names[1] == "dsh" && names[2] == "profile" && names[3] == "bundles":
                        arrays++;
                        found = new Layout(bom + (int)reader.TokenStartIndex, -1, new List<(int, int)>(), new List<string>());
                        break;
                    case JsonTokenType.String when found is not null && found.ArrayEnd < 0 && depth == 4:
                        itemStart = bom + (int)reader.TokenStartIndex;
                        found.Items.Add((itemStart, bom + (int)reader.BytesConsumed));
                        found.Names.Add(reader.GetString()!);
                        break;
                    case JsonTokenType.EndArray when found is not null && found.ArrayEnd < 0 && depth == 3:
                        found = found with { ArrayEnd = bom + (int)reader.TokenStartIndex };
                        break;
                    default:
                        if (found is not null && found.ArrayEnd < 0 && depth >= 4) { throw Unexpected("dsh.profile.bundles 里有不是字符串的项"); }
                        break;
                }
            }
        }
        catch (JsonException ex) { throw Unexpected("不是合法的 JSON（" + ex.Message + "）"); }

        if (arrays != 1 || found is null || found.ArrayEnd < 0) { throw Unexpected(arrays > 1 ? "dsh.profile.bundles 出现了多次" : "没有 dsh.profile.bundles 列表"); }
        return found;
    }

    private static DshException Unexpected(string what)
        => new("档案的 package.json 不是本工具能安全修改的形态（" + what + "）；Fast 组件没有安装，请检查该文件或在 dsh 里处理。");
}
