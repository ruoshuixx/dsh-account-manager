using AccountManager.Core.Json;
using AccountManager.Core.Models;
using AccountManager.Core.Dsh;

namespace AccountManager.Core.Accounts;

/// <summary>
/// Manual activation: validate latest state -> write dsh's current Codex grant -> read back and verify.
/// Never edits provider config and never goes online.
/// </summary>
public sealed class SwitchService
{
    private readonly IAccountBackend _api;
    private readonly AccountMapper _mapper;

    public SwitchService(IAccountBackend api, AccountMapper mapper)
    {
        _api = api;
        _mapper = mapper;
    }

    public async Task<SwitchResult> SwitchAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentialId)) { throw new DshException("请选择要激活的账号。"); }
        var id = credentialId.Trim();

        var credentials = await _api.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var matches = credentials.Where(c => c is not null && JsonMerge.GetString(c, "id") == id).ToList();
        if (matches.Count != 1) { throw new DshException("未找到唯一匹配的账号；它可能已被移除或发生变化，请刷新后重试。"); }
        var credential = matches[0]!;

        if (JsonMerge.GetString(credential, "integrationID") != "openai") { throw new DshException("只能激活 OpenAI 账号。"); }
        var value = JsonMerge.Get(credential, "value");
        if (value is null
            || JsonMerge.GetString(value, "type") != "oauth"
            || !CredentialHelpers.IsCodexOAuthMethod(JsonMerge.GetString(value, "methodID")))
        {
            throw new DshException("只能激活 OpenAI Codex OAuth 账号。");
        }

        var account = _mapper.ConvertCredential(credential);
        if (account.Active)
        {
            return new SwitchResult
            {
                Changed = false,
                Label = account.Label,
                CredentialID = account.CredentialId,
                Active = true,
                Status = account.Status,
                ReauthRequired = account.StatusCode == "Expired",
            };
        }

        await _api.ActivateCredentialAsync(id, cancellationToken).ConfigureAwait(false);

        var verified = await _api.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var selected = verified.Where(c => c is not null && JsonMerge.GetString(c, "id") == id).ToList();
        var activeOpenAi = verified.Where(c =>
            c is not null
            && JsonMerge.GetString(c, "integrationID") == "openai"
            && JsonNum.ToBool(JsonMerge.Get(c, "active"))).ToList();

        var selectedActive = selected.Count == 1 && JsonNum.ToBool(JsonMerge.Get(selected[0], "active"));
        if (!selectedActive || activeOpenAi.Count != 1 || JsonMerge.GetString(activeOpenAi[0], "id") != id)
        {
            throw new DshException("激活未成功：回读校验失败。账号可能已被其他程序修改，请在 dsh 中确认后重试。");
        }

        return new SwitchResult
        {
            Changed = true,
            Label = account.Label,
            CredentialID = account.CredentialId,
            Active = true,
            Status = account.Status,
            ReauthRequired = account.StatusCode == "Expired",
        };
    }
}
