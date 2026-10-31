using System.Text;
using AccountManager.Core.Dsh;
using AccountManager.Core.Security;

namespace AccountManager.Core.Sharing;

/// <summary>
/// The API side of Codex's <c>config.toml</c>: each shared API is a <c>[model_providers.&lt;id&gt;]</c> table plus a
/// <c>[profiles.&lt;id&gt;]</c> table, and "Codex uses this API" is the top-level <c>profile = "&lt;id&gt;"</c>. Removing that
/// key brings Codex back to its ChatGPT login in <c>auth.json</c>, which is never touched here.
/// </summary>
public sealed class CodexApiConfig
{
    /// <summary>Provider ids Codex reserves for its built-in providers.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "openai", "ollama", "lmstudio" };

    private readonly CodexCliAuthStore _codex;
    private readonly BackupService? _backups;

    public CodexApiConfig(CodexCliAuthStore codex, BackupService? backups = null)
    {
        _codex = codex ?? throw new ArgumentNullException(nameof(codex));
        _backups = backups;
    }

    public string ConfigPath => _codex.ConfigPath;

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

    /// <summary>
    /// The API provider id Codex currently uses: the top-level profile names a profile of the same name whose
    /// <c>model_provider</c> is that id and whose provider table exists. Null = Codex uses its ChatGPT login.
    /// </summary>
    public string? CurrentApi()
    {
        if (!Available) { return null; }
        var toml = Read();
        var profile = toml.Profile;
        if (string.IsNullOrEmpty(profile)) { return null; }
        return toml.GetValue(CodexConfigToml.ProfilesTable, profile, "model_provider") == profile
            && toml.HasTable(CodexConfigToml.ProvidersTable, profile) ? profile : null;
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
        if (before is not null) { _backups?.Save(Array.Empty<System.Text.Json.Nodes.JsonNode?>(), ConfigPath, before); }
        DshAtomicWrite.WriteBytes(ConfigPath, new UTF8Encoding(false).GetBytes(after), DshAtomicWrite.PrivateFileMode, directoryMode: null);
        if (ReadText() != after) { throw new DshException("Codex 的 config.toml 回读校验失败。"); }
        return true;
    }

    /// <summary>Makes Codex use this API (its tables must exist). Returns whether anything changed.</summary>
    public bool Use(string providerId)
    {
        var toml = Read();
        if (!toml.HasTable(CodexConfigToml.ProvidersTable, providerId) || !toml.HasTable(CodexConfigToml.ProfilesTable, providerId))
        {
            throw new DshException("该 API 还没有同步到 Codex。Codex 只支持 Responses 协议的 API；请确认协议后点“读取”再试。");
        }

        return Update(t => t.SetProfile(providerId));
    }

    /// <summary>Back to the ChatGPT login: removes the top-level profile when it selects an API. Returns whether it changed.</summary>
    public bool LeaveApi() => CurrentApi() is not null && Update(t => t.SetProfile(null));
}
