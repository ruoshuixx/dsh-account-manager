using AccountManager.Core.Json;
using AccountManager.Core.Models;
using AccountManager.Core.Dsh;

namespace AccountManager.Core.Accounts;

/// <summary>
/// Manual activation: validate latest state -> write the account into the selected switch target only (dsh's current
/// Codex grant, Codex's <c>auth.json</c> or OpenCode's active credential) -> read back and verify.
/// Never edits provider config and never goes online. Without <see cref="SwitchTargets"/> the target is always dsh.
/// </summary>
public sealed class SwitchService
{
    private readonly IAccountBackend _api;
    private readonly AccountMapper _mapper;
    private readonly SwitchTargets? _targets;

    public SwitchService(IAccountBackend api, AccountMapper mapper, SwitchTargets? targets = null)
    {
        _api = api;
        _mapper = mapper;
        _targets = targets;
    }

    public SwitchTarget Target => _targets?.Selected ?? SwitchTarget.Dsh;

    public async Task<SwitchResult> SwitchAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentialId)) { throw new DshException("请选择要激活的账号。"); }
        var id = credentialId.Trim();

        var credentials = await _api.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var matches = credentials.Where(c => c is not null && JsonMerge.GetString(c, "id") == id).ToList();
        if (matches.Count != 1) { throw new DshException("未找到唯一匹配的账号；它可能已被移除或发生变化，请刷新后重试。"); }
        var credential = matches[0]!;

        if (id.StartsWith(DshBackend.ApiCredentialPrefix, StringComparison.Ordinal) && JsonMerge.GetString(JsonMerge.Get(credential, "value"), "type") == "key")
        {
            return await UseApiAsync(credential, id, cancellationToken).ConfigureAwait(false);
        }

        if (JsonMerge.GetString(credential, "integrationID") != "openai") { throw new DshException("只能激活 OpenAI 账号。"); }
        var value = JsonMerge.Get(credential, "value");
        if (value is null
            || JsonMerge.GetString(value, "type") != "oauth"
            || !CredentialHelpers.IsCodexOAuthMethod(JsonMerge.GetString(value, "methodID")))
        {
            throw new DshException("只能激活 OpenAI Codex OAuth 账号。");
        }

        var account = _mapper.ConvertCredential(credential);
        var target = Target;
        SwitchResult Result(bool changed, string note = "") => new()
        {
            Changed = changed,
            Label = account.Label,
            CredentialID = account.CredentialId,
            Active = true,
            Status = account.Status,
            ReauthRequired = account.StatusCode == "Expired",
            Target = target,
            Note = note,
        };

        // Already the target's current account: nothing is read or written.
        var current = target == SwitchTarget.Dsh ? (account.Active ? id : null) : (await _targets!.GetCurrentAsync(target, cancellationToken).ConfigureAwait(false)).Id;
        if (current == id) { return Result(false); }

        if (_targets is not null) { await _targets.AbsorbAsync(target, cancellationToken).ConfigureAwait(false); }

        switch (target)
        {
            case SwitchTarget.Codex:
            {
                var outcome = _targets!.WriteCodex(id);
                return Result(outcome.Changed, outcome.Note);
            }
            case SwitchTarget.OpenCode:
            {
                var outcome = await _targets!.ActivateOpenCodeAsync(id, cancellationToken).ConfigureAwait(false);
                return Result(outcome.Changed, outcome.Note);
            }
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

        return Result(true);
    }

    /// <summary>An API is "switched to" only by Codex, which uses one provider at a time; dsh and OpenCode use them all.</summary>
    private async Task<SwitchResult> UseApiAsync(System.Text.Json.Nodes.JsonNode credential, string id, CancellationToken cancellationToken)
    {
        if (Target != SwitchTarget.Codex) { throw new DshException("dsh 和 OpenCode 可以直接选用所有 API 的模型，无需切换。"); }
        var label = JsonMerge.GetString(credential, "label") ?? id;
        SwitchResult Result(bool changed, string note = "") => new()
        {
            Changed = changed,
            Label = label,
            CredentialID = id,
            Active = true,
            Target = SwitchTarget.Codex,
            Note = note,
        };

        if ((await _targets!.GetCurrentAsync(SwitchTarget.Codex, cancellationToken).ConfigureAwait(false)).Id == id) { return Result(false); }
        var outcome = _targets.UseCodexApi(JsonMerge.GetString(credential, "integrationID") ?? string.Empty);
        return Result(outcome.Changed, outcome.Note);
    }
}
