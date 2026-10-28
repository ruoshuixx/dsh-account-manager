using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AccountManager.Core.Accounts;
using AccountManager.Core.Json;
using AccountManager.Core.Security;

namespace AccountManager.Core.Dsh;

/// <summary>What the Codex CLI auth file currently holds (tokens of a ChatGPT login).</summary>
public sealed class CodexCliAuth
{
    public string Access { get; init; } = string.Empty;

    public string Refresh { get; init; } = string.Empty;

    public string IdToken { get; init; } = string.Empty;

    public string AccountId { get; init; } = string.Empty;

    public string LastRefresh { get; init; } = string.Empty;

    /// <summary>The file's <c>auth_mode</c> value when present.</summary>
    public string AuthMode { get; init; } = string.Empty;

    public bool HasLogin => Access.Length > 0 && Refresh.Length > 0;

    /// <summary>The login in dsh's grant shape, so the vault matching rules apply to it unchanged.</summary>
    public JsonObject ToGrant() => new()
    {
        ["type"] = "oauth",
        ["access"] = Access,
        ["refresh"] = Refresh,
        ["expires"] = VaultCredentialMapper.ExpiryFromJwt(Access),
        ["accountId"] = AccountId.Length > 0 ? AccountId : AccountIdFromAccess(Access),
    };

    private static string AccountIdFromAccess(string access)
        => JsonMerge.GetString(JsonMerge.Get(Jwt.ReadPayload(access), "https://api.openai.com/auth"), "chatgpt_account_id") ?? string.Empty;
}

public enum CodexCliSyncKind
{
    /// <summary>Nothing was asked of the Codex CLI file (setting off, no Codex directory): not worth a message.</summary>
    Silent,

    /// <summary>The file already held this login.</summary>
    Unchanged,

    Written,

    /// <summary>Not written, the user should hear why (keyring, read-only, unreadable, no id_token ...).</summary>
    Skipped,

    /// <summary>A write was attempted and failed; the old file is intact.</summary>
    Failed,
}

public sealed record CodexCliSyncResult(CodexCliSyncKind Kind, string Message)
{
    public static readonly CodexCliSyncResult Off = new(CodexCliSyncKind.Silent, string.Empty);

    /// <summary>The short status-line text, or empty when there is nothing to tell the user.</summary>
    public string StatusNote => Kind is CodexCliSyncKind.Skipped or CodexCliSyncKind.Failed
        ? "Codex CLI 未同步：" + Message
        : string.Empty;
}

/// <summary>
/// The Codex CLI credential file <c>$CODEX_HOME/auth.json</c> (default <c>~/.codex/auth.json</c>), file storage mode only.
/// Location: settings override, then <c>CODEX_HOME</c>, then the default. The Codex directory is never created: a user
/// without Codex CLI keeps none. A keyring-backed store (<c>cli_auth_credentials_store = "keyring"</c> in
/// <c>config.toml</c>) is left alone. Writes keep every other key of the file in order, make an encrypted backup
/// first, replace the file atomically with mode 0600 and read the result back.
/// </summary>
public sealed class CodexCliAuthStore
{
    public const string AuthFileName = "auth.json";
    public const string ConfigFileName = "config.toml";

    private static readonly Regex StoreKey = new(@"^\s*cli_auth_credentials_store\s*=\s*(?:""([^""]*)""|'([^']*)'|([A-Za-z_-]+))\s*(?:#.*)?$", RegexOptions.Compiled);

    private readonly Func<string?> _override;
    private readonly Func<string, string?> _env;
    private readonly Func<string> _userHome;
    private readonly BackupService? _backups;

    public CodexCliAuthStore(Func<string?>? homeOverride = null, BackupService? backups = null,
        Func<string, string?>? env = null, Func<string>? userHome = null)
    {
        _override = homeOverride ?? (() => null);
        _backups = backups;
        _env = env ?? Environment.GetEnvironmentVariable;
        _userHome = userHome ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>The Codex home directory in use (it may not exist).</summary>
    public string Home
    {
        get
        {
            var configured = _override();
            if (!string.IsNullOrWhiteSpace(configured)) { return Path.GetFullPath(configured.Trim()); }
            var env = _env("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(env)) { return Path.GetFullPath(env.Trim()); }
            return Path.Combine(_userHome(), ".codex");
        }
    }

    public string AuthPath => Path.Combine(Home, AuthFileName);

    public string ConfigPath => Path.Combine(Home, ConfigFileName);

    /// <summary>The <c>cli_auth_credentials_store</c> value of config.toml (lower case), or empty when unset/unreadable.</summary>
    public string StorageMode()
    {
        try
        {
            if (!File.Exists(ConfigPath)) { return string.Empty; }
            foreach (var raw in File.ReadLines(ConfigPath))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { break; } // only top-level keys, before the first table
                var match = StoreKey.Match(line);
                if (!match.Success) { continue; }
                for (var i = 1; i <= 3; i++)
                {
                    if (match.Groups[i].Success) { return match.Groups[i].Value.Trim().ToLowerInvariant(); }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return string.Empty;
    }

    /// <summary>Whether the file can be used, and when not why (null = usable). Never creates anything.</summary>
    public CodexCliSyncResult? CheckUsable(bool forWrite)
    {
        if (!Directory.Exists(Home)) { return new CodexCliSyncResult(CodexCliSyncKind.Silent, "没有 Codex 目录：" + Home); }
        var mode = StorageMode();
        if (mode == "keyring") { return new CodexCliSyncResult(CodexCliSyncKind.Skipped, "Codex 把凭据存在系统密钥环（cli_auth_credentials_store = keyring），本工具只处理 auth.json"); }
        if (mode == "auto" && !File.Exists(AuthPath)) { return new CodexCliSyncResult(CodexCliSyncKind.Skipped, "Codex 的凭据存储为 auto 且没有 auth.json（可能在密钥环里）"); }
        if (forWrite && File.Exists(AuthPath) && IsReadOnly(AuthPath)) { return new CodexCliSyncResult(CodexCliSyncKind.Skipped, "auth.json 是只读文件"); }
        return null;
    }

    private static bool IsReadOnly(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0; }
            return (File.GetUnixFileMode(path) & UnixFileMode.UserWrite) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The current login in auth.json; null when there is no file, it is unreadable or holds no ChatGPT login.</summary>
    public CodexCliAuth? ReadCurrent()
    {
        if (CheckUsable(forWrite: false) is not null) { return null; }
        return TryRead(AuthPath, out var root, out _) ? Parse(root!) : null;
    }

    private static CodexCliAuth Parse(JsonObject root)
    {
        var tokens = JsonMerge.Get(root, "tokens");
        return new CodexCliAuth
        {
            Access = JsonMerge.GetString(tokens, "access_token") ?? string.Empty,
            Refresh = JsonMerge.GetString(tokens, "refresh_token") ?? string.Empty,
            IdToken = JsonMerge.GetString(tokens, "id_token") ?? string.Empty,
            AccountId = JsonMerge.GetString(tokens, "account_id") ?? string.Empty,
            LastRefresh = JsonMerge.GetString(root, "last_refresh") ?? string.Empty,
            AuthMode = JsonMerge.GetString(root, "auth_mode") ?? string.Empty,
        };
    }

    private static bool TryRead(string path, out JsonObject? root, out string text)
    {
        root = null;
        text = string.Empty;
        try
        {
            if (!File.Exists(path)) { return false; }
            text = File.ReadAllText(path);
            root = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            return root is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>Makes the vault account <paramref name="record"/> the Codex CLI's login.</summary>
    public CodexCliSyncResult Write(JsonObject record, DateTimeOffset now)
    {
        try
        {
            if (CheckUsable(forWrite: true) is { } unusable) { return unusable; }

            var access = VaultCredentialMapper.AccessOf(record);
            var refresh = VaultCredentialMapper.RefreshOf(record);
            var idToken = JsonMerge.GetString(JsonMerge.Get(record, "tokens"), "id_token") ?? string.Empty;
            var accountId = VaultCredentialMapper.BuildGrantPayload(record)["accountId"]?.GetValue<string>() ?? string.Empty;
            if (access.Length == 0 || refresh.Length == 0) { return new CodexCliSyncResult(CodexCliSyncKind.Skipped, "该账号缺少访问令牌或刷新令牌"); }
            if (idToken.Length == 0) { return new CodexCliSyncResult(CodexCliSyncKind.Skipped, "该账号没有 id_token（Codex CLI 需要它）"); }

            var exists = File.Exists(AuthPath);
            JsonObject root;
            var original = string.Empty;
            if (exists)
            {
                if (!TryRead(AuthPath, out var parsed, out original)) { return new CodexCliSyncResult(CodexCliSyncKind.Skipped, "auth.json 格式无法识别，没有改动它"); }
                root = parsed!;
                var current = Parse(root);
                if (string.Equals(current.AuthMode, "apikey", StringComparison.OrdinalIgnoreCase)) { return new CodexCliSyncResult(CodexCliSyncKind.Skipped, "Codex CLI 使用 API Key 登录"); }
                if (current.Refresh == refresh && current.Access == access && current.AccountId == accountId && current.IdToken == idToken)
                {
                    return new CodexCliSyncResult(CodexCliSyncKind.Unchanged, "Codex CLI 已是该账号");
                }
            }
            else
            {
                root = new JsonObject { ["OPENAI_API_KEY"] = null };
            }

            if (root["tokens"] is not JsonObject tokens) { tokens = new JsonObject(); root["tokens"] = tokens; }
            tokens["id_token"] = idToken;
            tokens["access_token"] = access;
            tokens["refresh_token"] = refresh;
            tokens["account_id"] = accountId;
            root["last_refresh"] = FormatTime(now, JsonMerge.GetString(root, "last_refresh"));

            if (exists) { _backups?.Save(Array.Empty<JsonNode?>(), AuthPath, original); }
            var bytes = Serialize(root, original);
            DshAtomicWrite.WriteBytes(AuthPath, bytes, DshAtomicWrite.PrivateFileMode, directoryMode: null);

            var verify = ReadCurrent();
            if (verify is null || verify.Refresh != refresh || verify.Access != access || verify.AccountId != accountId)
            {
                return new CodexCliSyncResult(CodexCliSyncKind.Failed, "回读校验失败，auth.json 可能被其他程序改动");
            }

            return new CodexCliSyncResult(CodexCliSyncKind.Written, "已写入 Codex CLI 的 auth.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return new CodexCliSyncResult(CodexCliSyncKind.Failed, ex is UnauthorizedAccessException ? "没有写入权限" : "无法写入（" + ex.GetType().Name + "）");
        }
    }

    /// <summary>The existing value's shape wins: same number of fractional digits, or none.</summary>
    internal static string FormatTime(DateTimeOffset now, string? existing)
    {
        var utc = now.UtcDateTime;
        var digits = 0;
        if (existing is not null)
        {
            var m = Regex.Match(existing, @"\.(\d+)(?:Z|[+-]\d\d:\d\d)$");
            if (m.Success) { digits = Math.Min(m.Groups[1].Length, 9); }
        }

        var text = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        if (digits > 0) { text += "." + utc.ToString("fffffff", System.Globalization.CultureInfo.InvariantCulture).PadRight(9, '0')[..digits]; }
        return text + "Z";
    }

    private static byte[] Serialize(JsonObject root, string original)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var text = root.ToJsonString(options);
        if (original.EndsWith('\n')) { text += "\n"; }
        return new UTF8Encoding(false).GetBytes(text);
    }
}
