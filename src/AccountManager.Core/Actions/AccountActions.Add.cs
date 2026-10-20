using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AccountManager.Core.Accounts;
using AccountManager.Core.Dsh;
using AccountManager.Core.Import;
using AccountManager.Core.Json;
using AccountManager.Core.Models;

namespace AccountManager.Core.Actions;

/// <summary>A secret-free public preview with an internal authorization payload. Never log the payload.</summary>
public sealed class CodexAddPreview
{
    public string Label { get; internal init; } = string.Empty;
    public string Workspace { get; internal init; } = string.Empty;
    public bool HasExistingAccount => OriginalCredential is not null;
    internal ImportService.ImportRow Row { get; init; } = new();
    internal JsonNode? OriginalCredential { get; init; }
    internal string NewCredentialId { get; } = VaultCredentialMapper.NewCodexId();
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal AccountAddResult? Result { get; set; }
}

public sealed partial class AccountActions
{
    private readonly SemaphoreSlim _additionGate = new(1, 1);

    public static string NewApiProviderId() => "api-" + Guid.NewGuid().ToString("N");

    private static JsonObject NewAccountMetadata() => new()
    {
        ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["last_used"] = 0L, // No model request has been made for a newly added account.
    };

    /// <summary>Add a unique provider and key; never overwrite an existing provider.</summary>
    public async Task<AccountAddResult> CreateManagedApiProviderAsync(string providerId, string displayName,
        string baseUrl, IReadOnlyList<string> modelIds, string key, bool useResponses = false, CancellationToken ct = default)
    {
        await _additionGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await CreateApiProviderCoreAsync(providerId, displayName, baseUrl, modelIds, key, useResponses, ct).ConfigureAwait(false); }
        finally { _additionGate.Release(); }
    }

    private async Task<AccountAddResult> CreateApiProviderCoreAsync(string providerId, string displayName,
        string baseUrl, IReadOnlyList<string> modelIds, string key, bool useResponses, CancellationToken ct)
    {
        _secrets.RegisterValue(key);
        var name = ApiProviderValidation.Name(displayName);
        var url = ApiProviderValidation.Endpoint(baseUrl);
        var apiKey = ApiProviderValidation.Key(key, required: true);
        _secrets.RegisterValue(apiKey);
        var models = ModelMap(modelIds);
        if (providerId is null || !Regex.IsMatch(providerId, "^api-[a-f0-9]{32}$"))
        {
            throw new DshException("新供应商 ID 无效；请重新打开添加窗口。");
        }

        var credentials = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
        var existing = await GetViewsAsync(ct).ConfigureAwait(false);
        if (existing.ContainsKey(providerId)) { throw new DshException("供应商 ID 已存在，未覆盖；请重新打开添加窗口。"); }
        if (credentials.Any(c => c is not null && Ordinal(GetString(c, "integrationID"), providerId)))
        {
            throw new DshException("供应商已有凭据，未覆盖；请重新读取。");
        }

        var snapshot = await _api.GetConfigSnapshotAsync(ct).ConfigureAwait(false);
        var backup = _backups.Save(credentials, snapshot.Path, snapshot.Raw);
        _log.Info("已生成加密备份（添加 API）：" + backup);
        ct.ThrowIfCancellationRequested();

        var catalog = new JsonObject();
        foreach (var model in models) { catalog[model] = ModelVariantBuilder.NewModelEntry(model, useResponses, url); }
        var view = new JsonObject
        {
            ["name"] = name,
            ["package"] = OpenAiApiProtocol.PackageFor(useResponses),
            ["settings"] = new JsonObject { ["baseURL"] = url },
            ["models"] = catalog,
        };

        var credentialId = ApiCredentialIdOf(providerId);
        try
        {
            // Once the write starts, finish verification even when the dialog is cancelled.
            await _api.WriteApiProviderAsync(providerId, view, apiKey, CancellationToken.None).ConfigureAwait(false);
            await AssertDefinitionSavedAsync(providerId, url, models, OpenAiApiProtocol.PackageFor(useResponses), backup, CancellationToken.None).ConfigureAwait(false);
            await AssertKeyStoredAsync(providerId, apiKey, backup, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The files may be partly written; never claim a rollback that cannot be proven.
            return Added(AccountKind.Api, credentialId, providerId, name, backup, false,
                "添加结果无法完整确认，未强行回滚或重复写入；请先读取并检查。加密备份：" + backup + "；" + _secrets.Redact(ex.Message));
        }

        _log.Info("已添加 API 供应商：" + providerId);
        return Added(AccountKind.Api, credentialId, providerId, name, backup, true, ConfigReloadWarning);
    }

    public async Task<CodexAddPreview> PreviewCodexAuthorizationAsync(JsonObject account, CancellationToken ct = default)
    {
        _secrets.Register(account);
        var row = ImportService.ConvertAccount(account, VaultCredentialMapper.DefaultMethodId);
        var identity = CredentialHelpers.GetIdentityInfo(row.Value);
        if (!CredentialHelpers.IsEmail(identity.Email) || string.IsNullOrWhiteSpace(identity.AccountID))
        {
            throw new DshException("授权结果缺少可确认的邮箱或工作区，未保存账号。");
        }
        var idToken = GetString(GetValue(account, "tokens"), "id_token");
        if (!string.IsNullOrWhiteSpace(idToken)) { ((JsonObject)row.Value["metadata"]!)["id_token"] = idToken; }
        if (!string.IsNullOrWhiteSpace(row.Plan)) { ((JsonObject)row.Value["metadata"]!)["planType"] = row.Plan; }
        var current = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
        var matches = MatchingCodex(current, row.Identity);
        if (matches.Count > 1) { throw new DshException("该身份已对应多份凭据，请先整理重复账号，未保存。"); }
        return new CodexAddPreview
        {
            Label = _secrets.Redact(row.Label),
            Workspace = _secrets.Redact(row.Workspace),
            Row = row,
            OriginalCredential = matches.SingleOrDefault()?.DeepClone(),
        };
    }

    /// <summary>Save explicitly confirmed authorization, preserving dsh's current Codex account.</summary>
    public async Task<AccountAddResult> SaveCodexAuthorizationAsync(CodexAddPreview preview, bool replaceExisting, CancellationToken ct = default)
    {
        await _additionGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await SaveCodexCoreAsync(preview, replaceExisting, ct).ConfigureAwait(false); }
        finally { _additionGate.Release(); }
    }

    private async Task<AccountAddResult> SaveCodexCoreAsync(CodexAddPreview preview, bool replaceExisting, CancellationToken ct)
    {
        await preview.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (preview.Result is not null) { return preview.Result; }
            if (preview.HasExistingAccount && !replaceExisting) { throw new DshException("该账号已存在，须确认更新后才能保存。"); }
            var row = preview.Row;
            var old = preview.OriginalCredential;
            var oldSnapshot = CredentialSnapshot(old);
            var before = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
            var matches = MatchingCodex(before, row.Identity);
            if ((old is null && matches.Count != 0)
                || (old is not null && (matches.Count != 1 || !CredentialSnapshotEqual(matches[0], oldSnapshot))))
            {
                throw new DshException("账号状态在确认期间已变化，未保存；请重新授权并核对。");
            }
            var backup = _backups.Save(before, null, null);
            _log.Info("已生成加密备份（添加 Codex）：" + backup);
            ct.ThrowIfCancellationRequested();
            var newId = preview.NewCredentialId;
            if (before.Any(c => Ordinal(GetString(c, "id"), newId))) { throw new DshException("新账号 ID 已存在，未重复写入；请先读取账号。"); }
            var activate = old is not null && JsonNum.ToBool(GetValue(old, "active"));
            var firstAccount = !before.Any(c => Ordinal(GetString(c, "integrationID"), "openai") && JsonNum.ToBool(GetValue(c, "active")));
            var expectedActive = activate || firstAccount;
            var warning = firstAccount ? "dsh 之前没有当前 Codex 账号，已把这个账号设为当前账号。" : string.Empty;
            // Capture the real creation time at save, not at OAuth preview/export. For an update,
            // preserve known account history rather than replacing it with a new account's date.
            var metadata = (JsonObject)row.Value["metadata"]!;
            var oldMetadata = GetValue(GetValue(old, "value"), "metadata");
            foreach (var field in new[] { "created_at", "last_used" })
            {
                metadata[field] = GetValue(oldMetadata, field)?.DeepClone()
                    ?? (field == "created_at" && old is null ? JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : JsonValue.Create(0L));
            }
            try
            {
                await _api.CreateCredentialAsync("openai", row.Label + " [" + row.Workspace + "]", row.Value, activate, CancellationToken.None, newId).ConfigureAwait(false);
                var verify = await GetFreshCredentialsAsync(CancellationToken.None).ConfigureAwait(false);
                if (!ExactNewCredential(verify, newId, "openai", row.Value, expectedActive)
                    || !PreservedActiveAccounts(before, verify, old, newId, expectedActive))
                {
                    preview.Result = Added(AccountKind.Codex, newId, "openai", preview.Label, backup, false,
                        "新账号或当前账号状态未通过回读校验；保留原账号，不自动切号或重试，请先读取并检查。");
                    return preview.Result;
                }
                if (old is not null)
                {
                    await _api.DeleteCredentialAsync(GetString(old, "id"), CancellationToken.None).ConfigureAwait(false);
                }
                var final = await GetFreshCredentialsAsync(CancellationToken.None).ConfigureAwait(false);
                var complete = ExactNewCredential(final, newId, "openai", row.Value, expectedActive)
                    && PreservedActiveAccounts(before, final, old, newId, expectedActive)
                    && (old is null || !final.Any(c => Ordinal(GetString(c, "id"), GetString(old, "id"))));
                preview.Result = Added(AccountKind.Codex, newId, "openai", preview.Label, backup, complete,
                    complete ? warning : "保存请求已完成，但最终回读未通过；请检查账号和当前账号状态，不要重复添加。");
                _log.Info(complete ? "已保存 Codex 授权账号。" : "Codex 授权保存需手动检查。");
                return preview.Result;
            }
            catch (Exception ex)
            {
                preview.Result = Added(AccountKind.Codex, newId, "openai", preview.Label, backup, false,
                    "保存结果未完整确认；旧账号是否残留需检查，不自动重试，请先读取并核对。加密备份：" + backup + "；" + _secrets.Redact(ex.Message));
                return preview.Result;
            }
        }
        finally { preview.Gate.Release(); }
    }

    private static bool ExactNewCredential(List<JsonNode?> credentials, string id, string provider, JsonObject value, bool active)
    {
        var found = credentials.Where(c => Ordinal(GetString(c, "id"), id)).ToList();
        return found.Count == 1 && Ordinal(GetString(found[0], "integrationID"), provider)
            && JsonNum.ToBool(GetValue(found[0], "active")) == active && DeepEqual(GetValue(found[0], "value"), value);
    }

    private static List<JsonNode> MatchingCodex(List<JsonNode?> credentials, string identity)
        => credentials.Where(c => c is not null && Ordinal(GetString(c, "integrationID"), "openai")
            && GetString(GetValue(c, "value"), "type") == "oauth"
            && CredentialHelpers.IsCodexOAuthMethod(GetString(GetValue(c, "value"), "methodID"))
            && Ordinal(CredentialHelpers.GetIdentityInfo(c).Identity, identity)).Select(c => c!).ToList();

    private static bool PreservedActiveAccounts(List<JsonNode?> before, List<JsonNode?> after, JsonNode? old, string newId, bool expectedActive)
    {
        var expected = before.Where(c => Ordinal(GetString(c, "integrationID"), "openai") && JsonNum.ToBool(GetValue(c, "active")))
            .Select(c => GetString(c, "id")).ToHashSet(StringComparer.Ordinal);
        if (old is not null) { expected.Remove(GetString(old, "id")); }
        if (expectedActive) { expected.Add(newId); }
        var actual = after.Where(c => Ordinal(GetString(c, "integrationID"), "openai") && JsonNum.ToBool(GetValue(c, "active")))
            .Select(c => GetString(c, "id")).ToHashSet(StringComparer.Ordinal);
        return expected.SetEquals(actual);
    }

    private AccountAddResult Added(AccountKind kind, string id, string provider, string label, string backup, bool complete, string warning, bool saved = true)
        => new() { Saved = saved, Complete = complete, Kind = kind, CredentialID = id, ProviderID = provider, Label = _secrets.Redact(label), Backup = backup, Warning = _secrets.Redact(warning) };
}
