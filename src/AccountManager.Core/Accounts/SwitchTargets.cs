using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;

namespace AccountManager.Core.Accounts;

/// <summary>
/// The three programs a vault account can be made current in: dsh (its credentials file), Codex (<c>auth.json</c>) and
/// OpenCode (its active <c>openai</c> credential). Knows which vault account each of them uses, absorbs newer tokens
/// they refreshed back into the vault before a switch, and writes the account into the one selected program only.
/// </summary>
public sealed class SwitchTargets
{
    private readonly DshBackend _dsh;
    private readonly AccountVault _vault;
    private readonly CodexCliAuthStore _codex;
    private readonly IOpenCodeAccounts _openCode;
    private readonly SecretRegistry _secrets;
    private readonly AppLog? _log;
    private readonly Func<SwitchTarget> _selected;

    public SwitchTargets(DshBackend dsh, AccountVault vault, CodexCliAuthStore codex, IOpenCodeAccounts openCode,
        SecretRegistry secrets, AppLog? log, Func<SwitchTarget> selected)
    {
        _dsh = dsh ?? throw new ArgumentNullException(nameof(dsh));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _codex = codex ?? throw new ArgumentNullException(nameof(codex));
        _openCode = openCode ?? throw new ArgumentNullException(nameof(openCode));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _log = log;
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
    }

    public SwitchTarget Selected => _selected();

    // ------------------------------------------------------------------
    // Who is current where
    // ------------------------------------------------------------------

    /// <summary>
    /// The vault id of the account <paramref name="target"/> currently uses (null = none, or unknown) and, when it could
    /// not be determined, a short reason. Read only; an unavailable OpenCode is a note, never an error.
    /// </summary>
    public async Task<(string? Id, string Note)> GetCurrentAsync(SwitchTarget target, CancellationToken ct = default)
    {
        try
        {
            switch (target)
            {
                case SwitchTarget.Codex:
                {
                    if (_codex.CheckUsable(forWrite: false) is { } unusable) { return (null, "Codex：" + unusable.Message); }
                    if (!File.Exists(_codex.AuthPath)) { return (null, "Codex 未登录（没有 " + _codex.AuthPath + "）"); }
                    var cli = _codex.ReadCurrent();
                    if (cli is null) { return (null, "Codex：无法读取 " + _codex.AuthPath); }
                    if (!cli.HasLogin)
                    {
                        return (null, string.Equals(cli.AuthMode, "apikey", StringComparison.OrdinalIgnoreCase)
                            ? "Codex 使用 API Key 登录，不是 ChatGPT 账号"
                            : "Codex 的 auth.json 里没有 ChatGPT 登录");
                    }

                    var grant = cli.ToGrant();
                    var id = IdOf(grant);
                    if (id is not null) { return (id, string.Empty); }
                    var email = VaultCredentialMapper.GrantEmail(grant);
                    return (null, "Codex 当前登录的 " + (email.Length > 0 ? email : "账号") + " 不在列表中");
                }
                case SwitchTarget.OpenCode:
                {
                    var active = (await _openCode.ListAsync(ct).ConfigureAwait(false)).Where(l => l.Active).ToList();
                    return (active.Count == 1 ? IdOf(active[0].Grant) : null, string.Empty);
                }
                default:
                    return (_dsh.CurrentVaultId(), string.Empty);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Warn("无法确定 " + target.Display() + " 当前账号：" + _secrets.Redact(ex.Message));
            return (null, target.Display() + "：" + _secrets.Redact(ex.Message));
        }
    }

    private string? IdOf(JsonObject grant)
    {
        var records = _vault.Load().ToList();
        var index = VaultCredentialMapper.FindActiveVerified(records, grant);
        return index >= 0 ? VaultCredentialMapper.IdOf(records[index]) : null;
    }

    /// <summary>The vault ids that every target other than <paramref name="selected"/> currently uses (OpenCode: best effort).</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<SwitchTarget>>> GetUsedByOthersAsync(SwitchTarget selected, CancellationToken ct = default)
    {
        var map = new Dictionary<string, List<SwitchTarget>>(StringComparer.Ordinal);
        foreach (var other in Enum.GetValues<SwitchTarget>().Where(t => t != selected))
        {
            var (id, _) = await GetCurrentAsync(other, ct).ConfigureAwait(false);
            if (id is null) { continue; }
            if (!map.TryGetValue(id, out var list)) { map[id] = list = new List<SwitchTarget>(); }
            list.Add(other);
        }

        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SwitchTarget>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>The other programs (not <paramref name="selected"/>) that currently use this account.</summary>
    public async Task<IReadOnlyList<SwitchTarget>> GetOtherUsersAsync(string vaultId, SwitchTarget selected, CancellationToken ct = default)
        => (await GetUsedByOthersAsync(selected, ct).ConfigureAwait(false)).TryGetValue(vaultId, out var list) ? list : Array.Empty<SwitchTarget>();

    /// <summary>
    /// Before the list is read with Codex selected: Codex's current login (e.g. signed in or refreshed by the desktop app)
    /// is written back into the vault, and stored as a new account when the vault does not have it, so it can be marked.
    /// </summary>
    public void AbsorbCurrent(SwitchTarget target)
    {
        if (target == SwitchTarget.Codex) { _dsh.AbsorbCodexCliLogin(_codex); }
    }

    // ------------------------------------------------------------------
    // Switching
    // ------------------------------------------------------------------

    /// <summary>
    /// Before any switch: tokens that dsh, the Codex CLI or OpenCode refreshed since the vault saw them are written back
    /// (newest wins), so the vault always holds the newest refresh token. OpenCode is read first when it is the target,
    /// so a missing OpenCode stops the switch before anything at all changed; otherwise it is best effort.
    /// </summary>
    public async Task AbsorbAsync(SwitchTarget target, CancellationToken ct = default)
    {
        IReadOnlyList<OpenCodeLogin>? logins = null;
        if (target == SwitchTarget.OpenCode)
        {
            logins = await _openCode.ListAsync(ct).ConfigureAwait(false);
        }
        else
        {
            try { logins = await _openCode.ListAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Info("OpenCode 不可用，已跳过其令牌写回：" + _secrets.Redact(ex.Message));
            }
        }

        _dsh.AbsorbDshGrant();
        _dsh.AbsorbCodexCliLogin(_codex);
        if (logins is null) { return; }
        foreach (var login in logins)
        {
            if (login.Access.Length == 0 || login.Refresh.Length == 0 || login.AccountId.Length == 0) { continue; }
            try { _dsh.AbsorbLogin(login.Grant, login.IdToken, "OpenCode", adoptUnknown: false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Warn("写回 OpenCode 的令牌失败，已跳过：" + _secrets.Redact(ex.Message));
            }
        }
    }

    /// <summary>Writes the vault account into Codex's <c>auth.json</c> only. Anything but a write / "already this account" is a failure.</summary>
    public SwitchOutcome WriteCodex(string vaultId)
    {
        var record = _vault.FindById(vaultId) ?? throw new DshException("未找到该账号；它可能已被移除，请刷新后重试。");
        var result = _codex.Write(record, DateTimeOffset.UtcNow);
        switch (result.Kind)
        {
            case CodexCliSyncKind.Written:
                _log?.Info("Codex：已把该账号写入 " + _codex.AuthPath + "（已先做加密备份）");
                return new SwitchOutcome(true, "已打开的 Codex 桌面版需要重启才生效");
            case CodexCliSyncKind.Unchanged:
                _log?.Info("Codex 已是该账号，无需改动");
                return new SwitchOutcome(false, string.Empty);
            default:
                throw new DshException("未能切换 Codex：" + result.Message + "。");
        }
    }

    public async Task<SwitchOutcome> ActivateOpenCodeAsync(string vaultId, CancellationToken ct = default)
    {
        var record = _vault.FindById(vaultId) ?? throw new DshException("未找到该账号；它可能已被移除，请刷新后重试。");
        _secrets.Register(record);
        var result = await _openCode.ActivateAsync(record, ct).ConfigureAwait(false);
        return new SwitchOutcome(result.Changed, result.Note);
    }

    /// <summary>
    /// After an account was saved with Codex or OpenCode as the switch target: when that program is signed in as this
    /// very account (same account id and e-mail), it gets the saved tokens. Never switches it to another account.
    /// </summary>
    public async Task RefreshIfCurrentAsync(JsonObject record, CancellationToken ct = default)
    {
        var accountId = VaultCredentialMapper.AccountIdOf(record);
        var email = VaultCredentialMapper.EmailOf(record);
        if (accountId.Length == 0) { return; }
        switch (Selected)
        {
            case SwitchTarget.Codex:
            {
                if (_codex.CheckUsable(forWrite: false) is not null) { return; }
                var cli = _codex.ReadCurrent();
                if (cli is not { HasLogin: true } || !SameAccount(cli.ToGrant(), accountId, email)) { return; }
                var result = _codex.Write(record, DateTimeOffset.UtcNow);
                if (result.Kind is CodexCliSyncKind.Written) { _log?.Info("Codex：已用新保存的令牌更新同一账号"); }
                else if (result.StatusNote.Length > 0) { _log?.Warn(result.StatusNote); }
                break;
            }
            case SwitchTarget.OpenCode:
            {
                var active = (await _openCode.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(l => l.Active);
                if (active is null || !SameAccount(active.Grant, accountId, email)) { return; }
                await _openCode.ActivateAsync(record, ct).ConfigureAwait(false);
                _log?.Info("OpenCode：已用新保存的令牌更新同一账号");
                break;
            }
        }
    }

    private static bool SameAccount(JsonObject grant, string accountId, string email)
    {
        if (JsonMerge.GetString(grant, "accountId") != accountId) { return false; }
        var grantEmail = VaultCredentialMapper.GrantEmail(grant);
        return email.Length == 0 || grantEmail.Length == 0 || string.Equals(grantEmail, email, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record SwitchOutcome(bool Changed, string Note);
