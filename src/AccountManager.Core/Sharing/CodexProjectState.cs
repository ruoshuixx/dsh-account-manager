using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccountManager.Core.Sharing;

/// <summary>Shares local sidebar data between Codex accounts. The caller must keep the desktop app stopped.</summary>
public static class CodexProjectState
{
    private const string ScopesKey = "electron-account-renderer-state-v1";
    private static readonly string[] CloudMarkers = { "codex.chatGptBacking", "codex.chatGptBackingCreation", "codex.retiredChatGptBacking" };
    private static readonly string[] SharedKeys =
    {
        "local-projects", "project-order", "thread-project-assignments", "thread-project-membership-host-ids",
        "app-server-project-id-by-legacy-project-id-by-host", "app-server-projects-migration-by-host",
        "pinned-project-ids", "sidebar-project-thread-orders", "project-appearances", "projectless-thread-ids",
        "electron-saved-workspace-roots", "electron-workspace-root-labels", "project-writable-roots",
        "thread-writable-roots", "thread-workspace-root-hints",
    };

    /// <summary>Returns true only when a validated, account-scoped file was changed; keeps the previous file beside it.</summary>
    public static bool ShareLocalProjects(string globalStatePath, out string? note)
    {
        note = null;
        if (!File.Exists(globalStatePath)) { return false; }
        var before = File.ReadAllText(globalStatePath);
        JsonObject? root;
        try { root = JsonNode.Parse(before) as JsonObject; }
        catch (JsonException) { return false; }
        if (root?[ScopesKey] is not JsonObject scopes || !scopes.TryGetPropertyValue("owner", out var owner)
            || (owner is not null && !IsString(owner)) || scopes["inactive"] is not JsonObject inactive) { return false; }

        var sources = new List<JsonObject> { root };
        foreach (var entry in inactive)
        {
            if (entry.Value is not JsonObject scope || scope["atoms"] is not JsonObject
                || scope["globals"] is not JsonObject globals) { return false; }
            sources.Add(globals);
        }

        var deleted = PendingDeletions(sources);
        var merged = new JsonObject();
        foreach (var source in sources)
        {
            foreach (var key in SharedKeys)
            {
                if (!source.TryGetPropertyValue(key, out var value)) { continue; }
                var filtered = Filter(key, value, deleted);
                if (merged.TryGetPropertyValue(key, out var first)) { merged[key] = Union(first, filtered, IsEntryMap(key)); }
                else { merged[key] = filtered; }
            }
        }

        var changed = false;
        foreach (var target in sources)
        {
            foreach (var (key, shared) in merged)
            {
                // Original excluded entries survive only in the scope that already held them.
                var exists = target.TryGetPropertyValue(key, out var original);
                var next = exists ? Union(shared, original, IsEntryMap(key)) : shared?.DeepClone();
                if (IsEntryMap(key) && original is JsonObject entries && next is JsonObject result)
                {
                    foreach (var (id, entry) in entries)
                    {
                        if (!IsLocalEntry(key, entry)) { result[id] = entry?.DeepClone(); }
                    }
                }
                if (exists && JsonNode.DeepEquals(original, next)) { continue; }
                target[key] = next;
                changed = true;
            }
        }
        if (!changed) { return false; }

        var after = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (after == before) { return false; }
        var path = Path.GetFullPath(globalStatePath);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, after, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(temporary, File.GetUnixFileMode(path)); }
            File.Copy(path, path + ".ai-account-manager.bak", overwrite: true);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
        note = "已让各账号共用 " + ((merged["local-projects"] as JsonObject)?.Count ?? 0) + " 个本地项目";
        return true;
    }

    private static bool IsString(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out _);

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool IsEntryMap(string key)
        => key is "local-projects" or "thread-project-assignments" or "thread-project-membership-host-ids";

    private static bool IsLocalEntry(string key, JsonNode? value) => key switch
    {
        "local-projects" => !HasCloudBacking(value),
        "thread-project-assignments" => value is JsonObject assignment && Text(assignment["projectKind"]) == "local",
        "thread-project-membership-host-ids" => Text(value) == "local",
        _ => true,
    };

    private static bool HasCloudBacking(JsonNode? value) => value switch
    {
        JsonObject obj => obj.Any(p => CloudMarkers.Contains(p.Key)
            || (p.Key == "codex" && p.Value is JsonObject codex
                && codex.Any(c => CloudMarkers.Contains("codex." + c.Key)))
            || HasCloudBacking(p.Value)),
        JsonArray array => array.Any(HasCloudBacking),
        JsonValue scalar when Text(scalar) is { } text => CloudMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal)),
        _ => false,
    };

    private static HashSet<string> PendingDeletions(IEnumerable<JsonObject> sources)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source["app-server-pending-project-deletions-by-host"] is not JsonObject hosts) { continue; }
            foreach (var host in hosts)
            {
                if (host.Value is JsonObject entries)
                {
                    foreach (var (id, entry) in entries)
                    {
                        ids.Add(id);
                        if (entry is JsonObject deletion && Text(deletion["projectId"]) is { } projectId) { ids.Add(projectId); }
                    }
                }
                else if (host.Value is JsonArray array)
                {
                    foreach (var entry in array.OfType<JsonObject>())
                    {
                        if (Text(entry["projectId"]) is { } projectId) { ids.Add(projectId); }
                    }
                }
            }
        }
        return ids;
    }

    private static bool IsDeleted(JsonNode? value, HashSet<string> deleted)
        => (Text(value) is { } id && deleted.Contains(id))
            || (value is JsonObject obj && Text(obj["projectId"]) is { } projectId && deleted.Contains(projectId));

    private static JsonNode? Filter(string key, JsonNode? value, HashSet<string> deleted)
    {
        if (value is JsonObject obj)
        {
            var filtered = new JsonObject();
            foreach (var (id, entry) in obj)
            {
                if (deleted.Contains(id) || IsDeleted(entry, deleted) || !IsLocalEntry(key, entry)) { continue; }
                filtered[id] = Filter(string.Empty, entry, deleted);
            }
            return filtered;
        }
        if (value is JsonArray array)
        {
            return new JsonArray(array.Where(entry => !IsDeleted(entry, deleted)).Select(entry => Filter(string.Empty, entry, deleted)).ToArray());
        }
        return value?.DeepClone();
    }

    /// <summary>Map values and array items use source order; project and assignment records are indivisible conflicts.</summary>
    private static JsonNode? Union(JsonNode? first, JsonNode? later, bool entryMap = false)
    {
        if (first is JsonObject a && later is JsonObject b)
        {
            var result = (JsonObject)a.DeepClone();
            foreach (var (key, value) in b)
            {
                if (!result.TryGetPropertyValue(key, out var existing)) { result[key] = value?.DeepClone(); }
                else if (!entryMap) { result[key] = Union(existing, value); }
            }
            return result;
        }
        if (first is JsonArray x && later is JsonArray y)
        {
            var result = new JsonArray();
            foreach (var entry in x.Concat(y))
            {
                if (!result.Any(item => JsonNode.DeepEquals(item, entry))) { result.Add(entry?.DeepClone()); }
            }
            return result;
        }
        return first?.DeepClone();
    }
}
