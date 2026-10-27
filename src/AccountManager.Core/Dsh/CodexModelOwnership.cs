using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccountManager.Core.Dsh;

/// <summary>
/// Remembers which <c>openai-codex</c> route models this program wrote, per dsh home and profile, so that a model OpenAI stops listing can
/// be taken out again while entries the user wrote stay. A missing record (this program never synced the profile, or 0.1.3/0.1.4 wrote
/// the entries) is reported as such so the caller can recognise the entries those versions wrote.
/// </summary>
public sealed class CodexModelOwnership
{
    public const string FileName = "codex-models-state.json";

    private readonly string? _file;
    private readonly Dictionary<string, HashSet<string>> _memory = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <param name="directory">The app data directory; null keeps the record in memory only (tests).</param>
    public CodexModelOwnership(string? directory)
    {
        _file = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, FileName);
    }

    private static string KeyOf(DshHome home) => Path.GetFullPath(home.Root) + "|" + home.Profile;

    /// <summary>The ids written for this profile, or null when nothing is recorded for it yet.</summary>
    public IReadOnlySet<string>? Load(DshHome home)
    {
        lock (_gate)
        {
            if (_file is null) { return _memory.TryGetValue(KeyOf(home), out var kept) ? new HashSet<string>(kept, StringComparer.Ordinal) : null; }
            try
            {
                if (!File.Exists(_file)) { return null; }
                if (JsonNode.Parse(File.ReadAllText(_file)) is not JsonObject root || root["profiles"] is not JsonObject profiles) { return null; }
                if (profiles[KeyOf(home)] is not JsonArray ids) { return null; }
                return ids.Select(i => i is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
    }

    public void Save(DshHome home, IEnumerable<string> ids)
    {
        var set = ids.Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal).ToList();
        lock (_gate)
        {
            if (_file is null) { _memory[KeyOf(home)] = new HashSet<string>(set, StringComparer.Ordinal); return; }
            JsonObject root;
            try { root = File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject existing ? existing : new JsonObject(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { root = new JsonObject(); }
            var profiles = root["profiles"] as JsonObject ?? new JsonObject();
            profiles[KeyOf(home)] = new JsonArray(set.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
            root["version"] = 1;
            root["profiles"] = profiles;
            DshAtomicWrite.WriteText(_file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
    }
}
