using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using AccountManager.Core.Security;

namespace AccountManager.Core.Sharing;

/// <summary>
/// The API side of Codex's <c>config.toml</c>: each shared API is a <c>[model_providers.&lt;id&gt;]</c> table, and
/// "Codex uses this API" is the top-level <c>model_provider = "&lt;id&gt;"</c> plus <c>model</c> (Codex 0.134+ no longer
/// accepts a top-level <c>profile</c>, and the desktop app cannot be started with <c>--profile</c>). The top-level
/// <c>model</c> / <c>model_provider</c> the user had before, and the model last used with each API, are kept in a small
/// state file of this program, so going back to the ChatGPT login restores them. <c>auth.json</c> is never touched here.
/// </summary>
public sealed class CodexApiConfig
{
    /// <summary>Provider ids Codex reserves for its built-in providers.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "openai", "ollama", "lmstudio" };

    private const string LegacyProfilesTable = "profiles";

    private readonly CodexCliAuthStore _codex;
    private readonly BackupService? _backups;
    private readonly string? _statePath;

    /// <param name="statePath">Where the remembered models live (the app's data folder); null keeps them next to config.toml.</param>
    public CodexApiConfig(CodexCliAuthStore codex, BackupService? backups = null, string? statePath = null)
    {
        _codex = codex ?? throw new ArgumentNullException(nameof(codex));
        _backups = backups;
        _statePath = statePath;
    }

    public string ConfigPath => _codex.ConfigPath;

    private string StatePath => _statePath ?? Path.Combine(_codex.Home, "ai-account-manager.json");

    /// <summary>Whether Codex is installed here at all (its home directory exists; it is never created).</summary>
    public bool Available => Directory.Exists(_codex.Home);

    public static bool CanUse(string providerId) => !Reserved.Contains(providerId);

    /// <summary>The raw file text, or null when there is no file.</summary>
    public string? ReadText()
    {
        try { return File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath, Encoding.UTF8) : null; }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public CodexConfigToml Read() => new(ReadText());

    /// <summary>The API provider id Codex currently uses (top-level <c>model_provider</c> with a provider table), or null for the ChatGPT login.</summary>
    public string? CurrentApi()
    {
        if (!Available) { return null; }
        var toml = Read();
        var provider = toml.GetTop("model_provider");
        return provider is not null && CanUse(provider) && toml.HasTable(CodexConfigToml.ProvidersTable, provider) ? provider : null;
    }

    /// <summary>
    /// Applies <paramref name="edit"/> to the file and writes it when the text changed: encrypted backup first, the file
    /// is replaced atomically and owner-only (it holds API keys), and an edit made by someone else meanwhile stops it.
    /// </summary>
    public bool Update(Func<CodexConfigToml, bool> edit)
    {
        if (!Available) { return false; }
        var before = ReadText();
        var toml = new CodexConfigToml(before);
        if (!edit(toml)) { return false; }
        var after = toml.ToString();
        if (after == (before ?? string.Empty)) { return false; }
        if (ReadText() != before) { throw new DshException("Codex 的 config.toml 正被其它程序修改，已停止写入；请稍后重试。"); }
        if (before is not null) { _backups?.Save(Array.Empty<JsonNode?>(), ConfigPath, before); }
        DshAtomicWrite.WriteBytes(ConfigPath, new UTF8Encoding(false).GetBytes(after), DshAtomicWrite.PrivateFileMode, directoryMode: null);
        if (ReadText() != after) { throw new DshException("Codex 的 config.toml 回读校验失败。"); }
        return true;
    }

    /// <summary>Makes Codex use this API with its remembered (else first of <paramref name="models"/>) model. Returns whether anything changed.</summary>
    public bool Use(string providerId, IReadOnlyList<string>? models = null)
    {
        var toml = Read();
        if (!toml.HasTable(CodexConfigToml.ProvidersTable, providerId))
        {
            throw new DshException("该 API 还没有同步到 Codex。Codex 只支持 Responses 协议的 API；请确认协议后点“读取”再试。");
        }

        var state = LoadState();
        models ??= (state["catalog"]?[providerId] as JsonArray)?.Select(Str).OfType<string>().ToList();
        var changed = Update(t => Select(t, providerId, models, state));
        SaveState(state);
        return changed;
    }

    /// <summary>Back to the ChatGPT login: the user's own model / provider come back. Returns whether it changed.</summary>
    public bool LeaveApi(IReadOnlySet<string>? ours = null)
    {
        if (!Available) { return false; }
        var state = LoadState();
        var changed = Update(t => Leave(t, ours, state));
        SaveState(state);
        return changed;
    }

    /// <summary>
    /// Edits for the share service: migrates what 0.2.0 wrote (<c>profile</c> and <c>[profiles.&lt;id&gt;]</c> of our ids)
    /// and leaves or fixes an API that is gone or whose model is. Runs inside <see cref="Update"/>.
    /// </summary>
    internal static bool Migrate(CodexConfigToml toml, IReadOnlySet<string> ours, IReadOnlyDictionary<string, IReadOnlyList<string>> models, JsonObject state)
    {
        // The shared APIs' models, for a later switch.
        var catalog = new JsonObject();
        foreach (var (id, list) in models) { catalog[id] = new JsonArray(list.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()); }
        state["catalog"] = catalog;

        var edited = false;
        var legacy = toml.Profile;
        if (legacy is not null && ours.Contains(legacy))
        {
            var model = toml.GetValue(LegacyProfilesTable, legacy, "model");
            if (model is not null) { Models(state)[legacy] = model; }
            edited |= toml.SetTop("profile", null);
            if (models.TryGetValue(legacy, out var list)) { edited |= Select(toml, legacy, list, state); }
        }

        foreach (var id in ours)
        {
            var model = toml.GetValue(LegacyProfilesTable, id, "model");
            if (model is not null && !Models(state).ContainsKey(id)) { Models(state)[id] = model; }
            edited |= toml.SetTable(LegacyProfilesTable, id, null);
        }

        // The API Codex uses is gone, or its model is: back to the login / a valid model.
        var current = toml.GetTop("model_provider");
        if (current is not null && ours.Contains(current))
        {
            if (!models.TryGetValue(current, out var list)) { edited |= Leave(toml, ours, state); }
            else if (!list.Contains(toml.GetTop("model") ?? string.Empty)) { edited |= toml.SetTop("model", list[0]); }
        }

        return edited;
    }

    internal JsonObject LoadState()
    {
        try { return File.Exists(StatePath) ? JsonNode.Parse(File.ReadAllText(StatePath)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch { return new JsonObject(); }
    }

    internal void SaveState(JsonObject state)
    {
        try
        {
            var text = state.ToJsonString();
            if (File.Exists(StatePath) ? File.ReadAllText(StatePath) == text : state.Count == 0) { return; }
            DshAtomicWrite.WriteText(StatePath, text, DshAtomicWrite.PrivateFileMode, directoryMode: null);
        }
        catch { /* only a convenience: the next switch picks the first model */ }
    }

    private static JsonObject Models(JsonObject state)
    {
        if (state["models"] is not JsonObject models) { models = new JsonObject(); state["models"] = models; }
        return models;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Select(CodexConfigToml toml, string providerId, IReadOnlyList<string>? models, JsonObject state)
    {
        var currentProvider = toml.GetTop("model_provider");
        var currentModel = toml.GetTop("model");
        // Remember what the user had before the first API, and the model used with an API we are leaving.
        if (state["api"] is null) { state["before"] = new JsonObject { ["model"] = currentModel, ["model_provider"] = currentProvider }; }
        else if (currentProvider is not null && currentModel is not null) { Models(state)[currentProvider] = currentModel; }

        var model = Str(Models(state)[providerId]);
        if (models is { Count: > 0 } && (model is null || !models.Contains(model))) { model = models[0]; }
        state["api"] = providerId;
        var edited = toml.SetTop("model_provider", providerId);
        if (model is not null) { edited |= toml.SetTop("model", model); }
        return edited;
    }

    private static bool Leave(CodexConfigToml toml, IReadOnlySet<string>? ours, JsonObject state)
    {
        var edited = false;
        var legacy = toml.Profile;
        if (legacy is not null && (ours?.Contains(legacy) ?? toml.HasTable(LegacyProfilesTable, legacy))) { edited |= toml.SetTop("profile", null); }

        var provider = toml.GetTop("model_provider");
        var mine = provider is not null && (Str(state["api"]) == provider || (ours?.Contains(provider) ?? false));
        if (!mine) { return edited; }

        var model = toml.GetTop("model");
        if (model is not null) { Models(state)[provider!] = model; }
        var before = state["before"] as JsonObject;
        edited |= toml.SetTop("model_provider", Str(before?["model_provider"]));
        edited |= toml.SetTop("model", Str(before?["model"]));
        state.Remove("api");
        state.Remove("before");
        return edited;
    }
}
