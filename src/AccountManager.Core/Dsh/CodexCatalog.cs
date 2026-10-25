using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AccountManager.Core.Dsh;

/// <summary>
/// The Codex model catalog that dsh serves for an <c>openai-codex</c> route without explicit models. It ships with dsh as
/// <c>node_modules/@earendil-works/pi-ai/dist/providers/data/openai-codex.json</c>: an object per wire api whose keys look
/// like <c>chat:gpt-6.1-sol</c> and whose values carry <c>id</c>, <c>name</c> and <c>type</c> (chat, image or classifier).
/// Everything here is read-only and bounded.
/// </summary>
public static partial class CodexCatalog
{
    public static readonly string[] RelativeFile = { "@earendil-works", "pi-ai", "dist", "providers", "data", "openai-codex.json" };

    [GeneratedRegex(@"DSH_BIN='([^']+)'")]
    private static partial Regex DshBinPattern();

    /// <summary>Chat model ids in file order (image and classifier models are skipped); empty when the text is not the catalog.</summary>
    public static IReadOnlyList<string> ParseChatModelIds(string json)
    {
        var ids = new List<string>();
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) { return ids; }
            foreach (var (_, group) in root)
            {
                if (group is not JsonObject models) { continue; }
                foreach (var (key, model) in models)
                {
                    if (model is not JsonObject m) { continue; }
                    var type = (m["type"] as JsonValue)?.TryGetValue<string>(out var t) == true ? t : null;
                    var isChat = type is null ? key.StartsWith("chat:", StringComparison.Ordinal) : type == "chat";
                    if (!isChat) { continue; }
                    var id = (m["id"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
                    if (string.IsNullOrWhiteSpace(id) && key.StartsWith("chat:", StringComparison.Ordinal)) { id = key["chat:".Length..]; }
                    if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id)) { ids.Add(id); }
                }
            }
        }
        catch (JsonException) { ids.Clear(); }
        return ids;
    }

    /// <summary>The chat models of the catalog by id (a clone of each catalog entry); empty when the text is not the catalog.</summary>
    public static IReadOnlyDictionary<string, JsonObject> ParseChatModels(string json)
    {
        var models = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) { return models; }
            foreach (var (_, group) in root)
            {
                if (group is not JsonObject entries) { continue; }
                foreach (var (key, model) in entries)
                {
                    if (model is not JsonObject m) { continue; }
                    var type = (m["type"] as JsonValue)?.TryGetValue<string>(out var t) == true ? t : null;
                    var isChat = type is null ? key.StartsWith("chat:", StringComparison.Ordinal) : type == "chat";
                    if (!isChat) { continue; }
                    var id = (m["id"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
                    if (string.IsNullOrWhiteSpace(id) && key.StartsWith("chat:", StringComparison.Ordinal)) { id = key["chat:".Length..]; }
                    if (!string.IsNullOrWhiteSpace(id) && !models.ContainsKey(id)) { models[id] = (JsonObject)m.DeepClone(); }
                }
            }
        }
        catch (JsonException) { models.Clear(); }
        return models;
    }

    /// <summary>The catalog file below a <c>node_modules</c> directory, or null. Also looks inside pnpm's <c>.pnpm</c> store.</summary>
    public static string? FindInNodeModules(string nodeModules)
    {
        try
        {
            var direct = Path.Combine(new[] { nodeModules }.Concat(RelativeFile).ToArray());
            if (File.Exists(direct)) { return direct; }
            var store = Path.Combine(nodeModules, ".pnpm");
            if (Directory.Exists(store))
            {
                foreach (var dir in Directory.EnumerateDirectories(store, "@earendil-works+pi-ai@*").Take(8))
                {
                    var candidate = Path.Combine(new[] { dir, "node_modules" }.Concat(RelativeFile).ToArray());
                    if (File.Exists(candidate)) { return candidate; }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// Breadth-first search for a <c>node_modules</c> directory holding the catalog below <paramref name="root"/>, never
    /// descending into <c>node_modules</c> itself, at most <paramref name="maxDepth"/> levels and a few hundred directories.
    /// </summary>
    public static string? FindBelow(string root, int maxDepth = 6, int maxDirectories = 400)
    {
        try
        {
            if (!Directory.Exists(root)) { return null; }
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((root, 0));
            var seen = 0;
            while (queue.Count > 0 && seen++ < maxDirectories)
            {
                var (dir, depth) = queue.Dequeue();
                if (string.Equals(Path.GetFileName(dir), "node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    if (FindInNodeModules(dir) is { } found) { return found; }
                    continue;
                }

                var nested = Path.Combine(dir, "node_modules");
                if (Directory.Exists(nested) && FindInNodeModules(nested) is { } inner) { return inner; }
                if (depth >= maxDepth) { continue; }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) { continue; }
                    queue.Enqueue((sub, depth + 1));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// Where dsh's runtime may live on this machine, most specific first: the install the <c>dsh</c> launcher points at, the
    /// dsh-tauri dependency folder (Linux), and on Windows folders named like DeepSeek / dsh under the usual install roots.
    /// </summary>
    public static IReadOnlyList<string> DefaultSearchRoots(Func<string, string?>? getEnv = null, string? userHome = null, string? launcherFile = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        userHome ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>();

        if (launcherFile is not null)
        {
            try
            {
                var info = new FileInfo(launcherFile);
                if (info.Exists && info.Length < 16 * 1024)
                {
                    var m = DshBinPattern().Match(File.ReadAllText(launcherFile));
                    if (m.Success)
                    {
                        var bin = m.Groups[1].Value;
                        var marker = bin.IndexOf("node_modules", StringComparison.Ordinal);
                        if (marker > 0) { roots.Add(bin[..(marker + "node_modules".Length)]); }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        roots.Add(Path.Combine(userHome, ".local", "share", "dsh-tauri", "dependencies", "dsh", "node_modules"));

        if (OperatingSystem.IsWindows())
        {
            foreach (var baseDir in new[]
            {
                Path.Combine(getEnv("LOCALAPPDATA") ?? string.Empty, "Programs"),
                getEnv("ProgramFiles") ?? string.Empty,
                getEnv("LOCALAPPDATA") ?? string.Empty,
                getEnv("APPDATA") ?? string.Empty,
            })
            {
                if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) { continue; }
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(baseDir).Take(300))
                    {
                        var name = Path.GetFileName(dir);
                        if (name.Contains("deepseek", StringComparison.OrdinalIgnoreCase) || name.Contains("dsh", StringComparison.OrdinalIgnoreCase)) { roots.Add(dir); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        return roots;
    }

    /// <summary>The first catalog file found under <paramref name="roots"/> (a root may itself be a <c>node_modules</c> folder), or null.</summary>
    public static string? Locate(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var found = string.Equals(Path.GetFileName(root), "node_modules", StringComparison.OrdinalIgnoreCase)
                ? FindInNodeModules(root)
                : FindBelow(root);
            if (found is not null) { return found; }
        }

        return null;
    }

    /// <summary>Reads the chat model ids from the located catalog; empty when it cannot be found or read.</summary>
    public static IReadOnlyList<string> ReadChatModelIds(IEnumerable<string> roots)
    {
        var file = Locate(roots);
        if (file is null) { return Array.Empty<string>(); }
        try
        {
            var info = new FileInfo(file);
            return info.Length > 8 * 1024 * 1024 ? Array.Empty<string>() : ParseChatModelIds(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>Reads the chat models of the located catalog by id; empty when it cannot be found or read.</summary>
    public static IReadOnlyDictionary<string, JsonObject> ReadChatModels(IEnumerable<string> roots)
    {
        var file = Locate(roots);
        if (file is null) { return new Dictionary<string, JsonObject>(); }
        try
        {
            var info = new FileInfo(file);
            return info.Length > 8 * 1024 * 1024 ? new Dictionary<string, JsonObject>() : ParseChatModels(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new Dictionary<string, JsonObject>(); }
    }
}
