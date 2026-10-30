using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Dsh;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.Security;

namespace AccountManager.Core.OpenCode;

/// <summary>One ChatGPT OAuth credential of OpenCode's <c>openai</c> integration, in dsh's grant shape for matching.</summary>
public sealed class OpenCodeLogin
{
    public string CredentialId { get; init; } = string.Empty;

    public bool Active { get; init; }

    /// <summary><c>{ type, access, refresh, expires, accountId }</c> like dsh's grant, so the vault matching rules apply unchanged.</summary>
    public JsonObject Grant { get; init; } = new();

    public string IdToken { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string Access => JsonMerge.GetString(Grant, "access") ?? string.Empty;

    public string Refresh => JsonMerge.GetString(Grant, "refresh") ?? string.Empty;

    public string AccountId => JsonMerge.GetString(Grant, "accountId") ?? string.Empty;

    public long Expires => VaultCredentialMapper.GrantExpires(Grant);
}

public sealed record OpenCodeActivation(bool Changed, string CredentialId, string Note);

/// <summary>What the switching layer needs from OpenCode; replaceable in tests so no real OpenCode is touched.</summary>
public interface IOpenCodeAccounts
{
    /// <summary>
    /// OpenCode's <c>openai</c> ChatGPT OAuth credentials. Throws <see cref="OpenCodeException"/> (with a user-facing
    /// Chinese message) when OpenCode cannot be reached; a read failure is never treated as "no accounts".
    /// </summary>
    Task<IReadOnlyList<OpenCodeLogin>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Makes the vault account OpenCode's active <c>openai</c> credential (reusing the credential of the same account when there is one).</summary>
    Task<OpenCodeActivation> ActivateAsync(JsonObject vaultRecord, CancellationToken cancellationToken = default);
}

/// <summary>
/// The production <see cref="IOpenCodeAccounts"/>, on top of the OpenCode credential API. OpenCode keeps several
/// credentials per integration with one <c>active</c>; activating an account reuses the credential of the same
/// account (replacing it when the vault holds newer tokens) and otherwise creates one with <c>activate</c>.
/// Everything the server says is data: errors never echo raw responses.
/// </summary>
public sealed class OpenCodeAccountStore : IOpenCodeAccounts
{
    private readonly IOpenCodeCredentialApi _api;
    private readonly SecretRegistry _secrets;
    private readonly AppLog? _log;

    public OpenCodeAccountStore(IOpenCodeCredentialApi api, SecretRegistry secrets, AppLog? log = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _log = log;
    }

    public async Task<IReadOnlyList<OpenCodeLogin>> ListAsync(CancellationToken cancellationToken = default)
        => Parse(await _api.GetCredentialsAsync(cancellationToken).ConfigureAwait(false));

    private IReadOnlyList<OpenCodeLogin> Parse(JsonArray credentials)
    {
        _secrets.Register(credentials);
        var logins = new List<OpenCodeLogin>();
        foreach (var credential in credentials)
        {
            if (credential is null || JsonMerge.GetString(credential, "integrationID") != VaultCredentialMapper.IntegrationId) { continue; }
            var id = JsonMerge.GetString(credential, "id");
            var value = JsonMerge.Get(credential, "value");
            if (string.IsNullOrWhiteSpace(id) || value is null
                || JsonMerge.GetString(value, "type") != "oauth"
                || !CredentialHelpers.IsCodexOAuthMethod(JsonMerge.GetString(value, "methodID"))) { continue; }

            var info = CredentialHelpers.GetIdentityInfo(value);
            var access = JsonMerge.GetString(value, "access") ?? string.Empty;
            var expires = CredentialHelpers.GetTokenExpiryMs(value).ExpiresMs;
            logins.Add(new OpenCodeLogin
            {
                CredentialId = id,
                Active = JsonNum.ToBool(JsonMerge.Get(credential, "active")),
                Email = info.Email,
                IdToken = JsonMerge.GetString(JsonMerge.Get(value, "metadata"), "id_token") ?? string.Empty,
                Grant = new JsonObject
                {
                    ["type"] = "oauth",
                    ["access"] = access,
                    ["refresh"] = JsonMerge.GetString(value, "refresh") ?? string.Empty,
                    ["expires"] = expires,
                    ["accountId"] = info.AccountID,
                },
            });
        }

        return logins;
    }

    public async Task<OpenCodeActivation> ActivateAsync(JsonObject vaultRecord, CancellationToken cancellationToken = default)
    {
        var accountId = VaultCredentialMapper.BuildGrantPayload(vaultRecord)["accountId"]?.GetValue<string>() ?? string.Empty;
        var refresh = VaultCredentialMapper.RefreshOf(vaultRecord);
        if (accountId.Length == 0 || VaultCredentialMapper.AccessOf(vaultRecord).Length == 0 || refresh.Length == 0)
        {
            throw new OpenCodeException("该账号缺少账号 ID、访问令牌或刷新令牌，无法写入 OpenCode。");
        }

        var logins = await ListAsync(cancellationToken).ConfigureAwait(false);
        var existing = FindSameAccount(logins, vaultRecord, accountId);
        var credential = VaultCredentialMapper.ToCredential(vaultRecord, false, null);
        var value = (JsonObject)credential["value"]!;
        var label = JsonMerge.GetString(credential, "label") ?? "Codex " + accountId;
        _secrets.Register(value);

        string targetId;
        string note = string.Empty;
        if (existing is null)
        {
            targetId = "cred_" + Guid.NewGuid().ToString("N");
            await CreateExactAsync(targetId, label, value, cancellationToken).ConfigureAwait(false);
            _log?.Info("OpenCode：已为该账号新建凭据并设为当前（" + targetId + "）");
        }
        else if (VaultCredentialMapper.ExpiresOf(vaultRecord) > existing.Expires && existing.Refresh != refresh)
        {
            // The vault holds newer tokens than OpenCode's credential: store them as a new credential and drop the old one.
            targetId = "cred_" + Guid.NewGuid().ToString("N");
            await CreateExactAsync(targetId, label, value, cancellationToken).ConfigureAwait(false);
            try { await _api.DeleteCredentialAsync(existing.CredentialId, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Warn("OpenCode：新凭据已保存，但旧凭据未移除：" + _secrets.Redact(ex.Message));
                note = "OpenCode 中留有该账号的旧凭据，可在 OpenCode 里移除";
            }

            _log?.Info("OpenCode：已用账号库里较新的令牌更新该账号的凭据并设为当前（" + targetId + "）");
        }
        else
        {
            targetId = existing.CredentialId;
            if (existing.Active && logins.Count(l => l.Active) == 1)
            {
                _log?.Info("OpenCode 已是该账号，无需改动");
                return new OpenCodeActivation(false, targetId, string.Empty);
            }

            await _api.ActivateCredentialAsync(targetId, cancellationToken).ConfigureAwait(false);
            _log?.Info("OpenCode：已激活该账号现有的凭据（" + targetId + "）");
        }

        var verified = await ListAsync(cancellationToken).ConfigureAwait(false);
        var active = verified.Where(l => l.Active).ToList();
        if (active.Count != 1 || active[0].CredentialId != targetId)
        {
            throw new OpenCodeException("激活未成功：回读校验失败。账号可能已被其他程序修改，请在 OpenCode 中确认后重试。");
        }

        return new OpenCodeActivation(true, targetId, note);
    }

    /// <summary>
    /// The OpenCode credential of the vault account: same refresh token, else same account id and e-mail; with no usable
    /// e-mail on either side the only (or the newest) credential of that account id.
    /// </summary>
    internal static OpenCodeLogin? FindSameAccount(IReadOnlyList<OpenCodeLogin> logins, JsonObject record, string accountId)
    {
        var refresh = VaultCredentialMapper.RefreshOf(record);
        var byRefresh = logins.FirstOrDefault(l => l.Refresh.Length > 0 && l.Refresh == refresh);
        if (byRefresh is not null) { return byRefresh; }

        var sameAccount = logins.Where(l => l.AccountId == accountId).ToList();
        if (sameAccount.Count == 0) { return null; }
        var email = VaultCredentialMapper.EmailOf(record);
        if (email.Length > 0)
        {
            var byEmail = sameAccount.Where(l => string.Equals(l.Email, email, StringComparison.OrdinalIgnoreCase)).OrderByDescending(l => l.Expires).ToList();
            if (byEmail.Count > 0) { return byEmail[0]; }
            if (sameAccount.Any(l => l.Email.Length > 0)) { return null; } // a different user of the same workspace
        }

        return sameAccount.OrderByDescending(l => l.Expires).First();
    }

    /// <summary>
    /// Creates the credential under an id chosen up front and activates it. A lost response is not proof of failure:
    /// the id is looked up before giving up, and nothing is retried.
    /// </summary>
    private async Task CreateExactAsync(string id, string label, JsonObject value, CancellationToken ct)
    {
        try
        {
            var returned = await _api.CreateCredentialAsync(VaultCredentialMapper.IntegrationId, label, value, true, ct, id).ConfigureAwait(false);
            if (!string.Equals(returned, id, StringComparison.Ordinal)) { throw new OpenCodeException("OpenCode 没有确认新凭据 ID。"); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                var current = await ListAsync(CancellationToken.None).ConfigureAwait(false);
                if (current.Any(l => l.CredentialId == id && l.Refresh == (JsonMerge.GetString(value, "refresh") ?? string.Empty))) { return; }
            }
            catch { /* Failure to reconcile is not proof that the request did not reach OpenCode. */ }

            if (ex is OpenCodeException) { throw; }
            throw new OpenCodeException("写入 OpenCode 凭据失败，未确认结果；请在 OpenCode 中检查后再试。", ex);
        }
    }
}
