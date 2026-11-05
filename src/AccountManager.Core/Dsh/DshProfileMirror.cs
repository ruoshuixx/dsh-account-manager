using System.Text.Json.Nodes;
using AccountManager.Core.Logging;
using AccountManager.Core.Security;

namespace AccountManager.Core.Dsh;

/// <summary>Which providers were mirrored into each other dsh profile (profile name → provider ids).</summary>
public sealed record ProfileMirrorResult(IReadOnlyDictionary<string, IReadOnlyList<string>> State, IReadOnlyList<string> Notes, bool Changed);

/// <summary>
/// dsh clients keep their providers per profile: the desktop app uses <c>desktop</c> (Windows) or <c>tauri</c>
/// (Ubuntu), the dsh CLI (<c>dsh --profile cli</c>, <c>deepseek</c>) uses <c>cli</c>; credentials are shared by all.
/// This copies the <c>llm-pi-ai</c> providers of the profile the program manages into the other client profiles
/// that exist, so every client sees the same Codex route and APIs. Providers the user added only there stay, and
/// only ids this program mirrored before are ever removed. Fast twins are dropped where the Fast component is not
/// installed, because nothing in that profile would turn them into real requests.
/// </summary>
public sealed class DshProfileMirror
{
    public static readonly string[] ClientProfiles = { "desktop", "tauri", "cli" };

    private const string DefaultModelId = "agent-default-model";
    private const string DefaultModelPackage = "@deepseek-ai/dsh-agent-default-model";

    private readonly Func<DshHome> _home;
    private readonly BackupService? _backups;
    private readonly AppLog? _log;

    public DshProfileMirror(Func<DshHome> home, BackupService? backups = null, AppLog? log = null)
    {
        _home = home ?? throw new ArgumentNullException(nameof(home));
        _backups = backups;
        _log = log;
    }

    public ProfileMirrorResult Sync(IReadOnlyDictionary<string, IReadOnlyList<string>> previous)
    {
        var home = _home();
        var state = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var notes = new List<string>();
        var changed = false;
        if (!File.Exists(home.PatchPath)) { return new ProfileMirrorResult(previous, notes, false); }

        JsonObject providers;
        JsonObject? defaultModel;
        try
        {
            var source = ProfilePatchDocument.Load(home.PatchPath);
            providers = source.GetProviders() ?? new JsonObject();
            defaultModel = source.GetEntryById(DefaultModelId)?["config"] as JsonObject;
        }
        catch (Exception ex)
        {
            notes.Add("读取 dsh 档案 " + home.Profile + " 的供应商失败，未同步到其它档案：" + ex.Message);
            return new ProfileMirrorResult(previous, notes, false);
        }

        foreach (var profile in Targets(home))
        {
            var dir = Path.Combine(home.ProfilesDirectory, profile);
            var wanted = new JsonObject();
            var hasFast = HasFastComponent(dir);
            foreach (var (id, provider) in providers)
            {
                if (provider is not JsonObject p) { continue; }
                wanted[id] = hasFast ? p.DeepClone() : WithoutFastTwins(p);
            }

            var before = previous.TryGetValue(profile, out var ids) ? ids : Array.Empty<string>();
            var gone = before.Where(id => !wanted.ContainsKey(id)).ToList();
            var path = Path.Combine(dir, DshHome.PatchFileName);
            var copiedDefault = false;
            try
            {
                var wrote = ProfilePatchDocument.Update(path, doc =>
                {
                    var current = doc.GetProviders() ?? new JsonObject();
                    var edited = false;
                    foreach (var (id, provider) in wanted)
                    {
                        if (JsonNode.DeepEquals(current[id], provider)) { continue; }
                        edited |= doc.SetProvider(id, (JsonObject)provider!);
                    }

                    foreach (var id in gone.Where(current.ContainsKey)) { edited |= doc.SetProvider(id, null); }
                    if (DefaultFor(defaultModel, wanted) is { } selection && !HasSelection(doc))
                    {
                        edited |= doc.AddEntry(new JsonObject { ["id"] = DefaultModelId, ["name"] = DefaultModelPackage, ["config"] = selection });
                        copiedDefault = true;
                    }

                    if (edited && File.Exists(path)) { _backups?.Save(Array.Empty<JsonNode?>(), path, File.ReadAllText(path)); }
                    return edited;
                });
                if (wrote)
                {
                    changed = true;
                    _log?.Info("已把 dsh 供应商同步到档案 " + profile + "（" + wanted.Count + " 个" + (gone.Count > 0 ? "，移除 " + gone.Count + " 个" : string.Empty) + "）"
                        + (copiedDefault ? "，并设好默认模型" : string.Empty)
                        + (hasFast ? string.Empty : "；该档案没有 Fast 组件，未带 Fast 模型") + "。");
                }

                state[profile] = wanted.Select(kv => kv.Key).ToList();
            }
            catch (Exception ex)
            {
                notes.Add("同步供应商到 dsh 档案 " + profile + " 失败：" + ex.Message);
                if (previous.TryGetValue(profile, out var kept)) { state[profile] = kept; }
            }
        }

        return new ProfileMirrorResult(state, notes, changed);
    }

    /// <summary>The other client profiles that exist and have been initialized (the client writes package.json on first start).</summary>
    public static IReadOnlyList<string> Targets(DshHome home)
        => ClientProfiles
            .Where(p => !string.Equals(p, home.Profile, StringComparison.Ordinal)
                && File.Exists(Path.Combine(home.ProfilesDirectory, p, "package.json")))
            .ToList();

    /// <summary>Whether the profile already has its own default model (the CLI's /model saves one); that is never replaced.</summary>
    private static bool HasSelection(ProfilePatchDocument doc)
        => doc.GetEntryById(DefaultModelId)?["config"] is JsonObject c
            && (c["provider"] is JsonValue || c["model"] is JsonValue);

    /// <summary>
    /// The source profile's default model as it can be used in the target: its provider must have been copied and list
    /// the model; a Fast twin that was left out falls back to its base model.
    /// </summary>
    private static JsonObject? DefaultFor(JsonObject? source, JsonObject wanted)
    {
        if (source?["provider"] is not JsonValue pv || !pv.TryGetValue<string>(out var provider)) { return null; }
        if (source["model"] is not JsonValue mv || !mv.TryGetValue<string>(out var model)) { return null; }
        if (wanted[provider] is not JsonObject target) { return null; }
        if (target["models"] is JsonArray models)
        {
            var ids = models.Select(m => m is JsonObject o ? o["id"]?.GetValue<string>() : m is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToHashSet(StringComparer.Ordinal);
            if (!ids.Contains(model) && model.EndsWith("-fast", StringComparison.Ordinal) && ids.Contains(model[..^"-fast".Length])) { model = model[..^"-fast".Length]; }
            if (!ids.Contains(model)) { return null; }
        }

        var selection = new JsonObject { ["provider"] = provider, ["model"] = model };
        if (source["reasoningEffort"] is JsonValue effort) { selection["reasoningEffort"] = effort.DeepClone(); }
        return selection;
    }

    private static bool HasFastComponent(string profileDirectory)
    {
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(profileDirectory, "package.json")));
            return json?["dsh"]?["profile"]?["bundles"] is JsonArray bundles
                && bundles.Any(b => b is JsonValue v && v.TryGetValue<string>(out var s) && s == FastComponentInstaller.PackageName);
        }
        catch { return false; }
    }

    /// <summary>A copy without the <c>&lt;id&gt;-fast</c> models whose base model is in the same list.</summary>
    private static JsonObject WithoutFastTwins(JsonObject provider)
    {
        var copy = (JsonObject)provider.DeepClone();
        if (copy["models"] is not JsonArray models) { return copy; }
        static string? IdOf(JsonNode? m) => m is JsonObject o ? o["id"]?.GetValue<string>() : m is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var ids = models.Select(IdOf).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        for (var i = models.Count - 1; i >= 0; i--)
        {
            var id = IdOf(models[i]);
            if (id is not null && id.EndsWith("-fast", StringComparison.Ordinal) && ids.Contains(id[..^"-fast".Length])) { models.RemoveAt(i); }
        }

        return copy;
    }
}
