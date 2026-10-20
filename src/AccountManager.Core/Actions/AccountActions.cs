using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Dsh;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.Models;
using AccountManager.Core.Security;

namespace AccountManager.Core.Actions;

/// <summary>
/// Deletes managed Codex/API accounts, edits API provider definitions (with optional key rotation) and adds
/// accounts. Safety boundaries kept from the original manager: an encrypted backup before any destructive write,
/// exact-ID validation, refusal of ambiguous cases, read-back verification and explicit warnings. The dsh
/// backend serializes every file write with dsh's own lock protocol, so the multi-step compensation the
/// original needed against a server is not repeated here: each step re-reads the files under that lock.
/// </summary>
public sealed partial class AccountActions
{
    private const string ConfigReloadWarning =
        "配置已写入磁盘；运行中的 dsh 会自动热加载更改。本工具不重启 dsh，也不发送模型验证请求。";

    private readonly IAccountBackend _api;
    private readonly BackupService _backups;
    private readonly SecretRegistry _secrets;
    private readonly AppLog _log;
    private readonly AccountMapper _mapper;
    private readonly ReferenceService? _references;

    public AccountActions(IAccountBackend api, BackupService backups, SecretRegistry secrets, AppLog log, AccountMapper mapper,
        ReferenceService? references = null)
    {
        _references = references;
        _api = api;
        _backups = backups;
        _secrets = secrets;
        _log = log;
        _mapper = mapper;
    }

    // ------------------------------------------------------------------
    // Public operations
    // ------------------------------------------------------------------

    /// <summary>Delete a Codex account (vault entry, and dsh's grant when it is the current one), or an API
    /// provider definition plus its key reference.</summary>
    public async Task<DeleteResult> RemoveManagedAccountAsync(string credentialId, string? providerId, CancellationToken ct = default)
        => await RemoveManagedAccountAsync(credentialId, providerId, null, ct).ConfigureAwait(false);

    /// <summary>
    /// Like <see cref="RemoveManagedAccountAsync(string, string?, CancellationToken)"/>, resolving the model references to
    /// an API provider first. Without a <paramref name="resolution"/>, deleting a provider that dsh's settings still
    /// refer to throws <see cref="ReferencesInUseException"/> instead of leaving them dangling.
    /// </summary>
    public async Task<DeleteResult> RemoveManagedAccountAsync(string credentialId, string? providerId, ReferenceResolution? resolution, CancellationToken ct = default)
    {
        var credentialIdText = credentialId ?? string.Empty;
        var providerIdText = providerId ?? string.Empty;

        var credentials = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(credentialIdText))
        {
            var matches = credentials
                .Where(c => c is not null && Ordinal(GetString(c, "id"), credentialIdText))
                .ToList();
            if (matches.Count == 0) { throw new DshException("未找到该凭据；它可能已被移除，请刷新后重试。"); }
            if (matches.Count > 1) { throw new DshException("凭据 ID 不唯一，已停止以避免误删。"); }

            var credential = matches[0]!;
            var integration = GetString(credential, "integrationID");
            if (!string.IsNullOrEmpty(providerIdText))
            {
                if (!Ordinal(integration, providerIdText)) { throw new DshException("凭据与所选供应商不匹配，已停止。"); }
            }
            else
            {
                providerIdText = integration;
            }

            var value = GetValue(credential, "value");
            if (Ordinal(integration, "openai")
                && EqIgnoreCase(GetString(value, "type"), "oauth")
                && CredentialHelpers.IsCodexOAuthMethod(GetString(value, "methodID")))
            {
                return await RemoveCodexCredentialAsync(credential, credentials, ct).ConfigureAwait(false);
            }

            if (EqIgnoreCase(GetString(value, "type"), "key"))
            {
                if (string.IsNullOrWhiteSpace(providerIdText)) { throw new DshException("无法确定 API 凭据所属供应商，已停止。"); }
                return await RemoveApiProviderAsync(credential, credentials, providerIdText, resolution, ct).ConfigureAwait(false);
            }

            throw new DshException("不支持的凭据类型；已停止以避免误删。");
        }

        if (string.IsNullOrWhiteSpace(providerIdText)) { throw new DshException("缺少凭据 ID 和供应商 ID，无法确定要删除的内容。"); }
        return await RemoveApiProviderAsync(null, credentials, providerIdText, resolution, ct).ConfigureAwait(false);
    }

    /// <summary>Edit an API provider's HTTPS endpoint and model list, optionally replacing its key.</summary>
    public async Task<ApiSaveResult> UpdateManagedApiProviderAsync(string credentialId, string providerId,
        string baseUrl, IReadOnlyList<string> modelIds, string? newKey, CancellationToken ct = default)
        => await UpdateManagedApiProviderAsync(credentialId, providerId, null, baseUrl, modelIds, newKey, ct).ConfigureAwait(false);

    /// <summary>Changes the display name without changing provider/model reference IDs.</summary>
    public async Task<ApiSaveResult> UpdateManagedApiProviderAsync(string credentialId, string providerId,
        string? displayName, string baseUrl, IReadOnlyList<string> modelIds, string? newKey, CancellationToken ct = default)
        => await UpdateManagedApiProviderAsync(credentialId, providerId, displayName, baseUrl, modelIds, newKey, null, ct).ConfigureAwait(false);

    /// <summary>Choose Chat Completions/Responses; null preserves the existing protocol.</summary>
    public async Task<ApiSaveResult> UpdateManagedApiProviderAsync(string credentialId, string providerId,
        string? displayName, string baseUrl, IReadOnlyList<string> modelIds, string? newKey, bool? useResponses, CancellationToken ct = default)
        => await UpdateManagedApiProviderAsync(credentialId, providerId, displayName, baseUrl, modelIds, newKey, useResponses, null, ct).ConfigureAwait(false);

    /// <summary>Also resolves model references to models this edit removes (see <see cref="ReferencesInUseException"/>).</summary>
    public async Task<ApiSaveResult> UpdateManagedApiProviderAsync(string credentialId, string providerId,
        string? displayName, string baseUrl, IReadOnlyList<string> modelIds, string? newKey, bool? useResponses,
        ReferenceResolution? resolution, CancellationToken ct = default)
    {
        var credentialIdText = credentialId ?? string.Empty;
        var providerIdText = providerId ?? string.Empty;
        var newKeyText = newKey ?? string.Empty;

        // Register the new key immediately so no later error text can leak it.
        if (!string.IsNullOrEmpty(newKeyText)) { _secrets.RegisterValue(newKeyText); }

        var name = displayName is null ? null : ApiProviderValidation.Name(displayName);
        var baseUrlText = (baseUrl ?? string.Empty).Trim();
        if (!HttpsEndpoint(baseUrlText)) { throw new DshException("接口地址必须是无内嵌凭据、查询参数和片段的 HTTPS 地址。"); }
        var modelMap = ModelMap(modelIds);

        var credentials = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(credentialIdText))
        {
            var matches = credentials
                .Where(c => c is not null && Ordinal(GetString(c, "id"), credentialIdText))
                .ToList();
            if (matches.Count != 1) { throw new DshException("未找到唯一匹配的凭据；请刷新后重试。"); }
            var integration = GetString(matches[0], "integrationID");
            if (string.IsNullOrEmpty(providerIdText)) { providerIdText = integration; }
            else if (!Ordinal(integration, providerIdText)) { throw new DshException("凭据与所选供应商不匹配，已停止。"); }
            if (!EqIgnoreCase(GetString(GetValue(matches[0], "value"), "type"), "key")) { throw new DshException("仅支持编辑 API Key 供应商。"); }
        }
        else if (string.IsNullOrWhiteSpace(providerIdText))
        {
            throw new DshException("缺少凭据 ID 和供应商 ID，无法确定要编辑的供应商。");
        }

        var definitions = await GetViewsAsync(ct).ConfigureAwait(false);
        if (!definitions.TryGetValue(providerIdText, out var existing) || existing is not JsonObject definition)
        {
            throw new DshException("dsh 配置中找不到该供应商定义；已停止，未修改任何内容。");
        }

        var oldPackage = GetString(definition, "package");
        var expectedPackage = useResponses.HasValue ? OpenAiApiProtocol.PackageFor(useResponses.Value) : oldPackage;
        if (!OpenAiApiProtocol.IsManagedPackage(expectedPackage))
        {
            throw new DshException("该供应商不是本工具支持的 OpenAI 兼容 / Responses 协议；已停止，避免破坏未知配置。");
        }

        // Build the new definition: update name/protocol/baseURL/models.
        var newDefinition = (JsonObject)definition.DeepClone();
        if (name is not null) { newDefinition["name"] = name; }
        newDefinition["package"] = expectedPackage;
        if (newDefinition["settings"] is not JsonObject settings)
        {
            settings = new JsonObject();
            newDefinition["settings"] = settings;
        }

        settings["baseURL"] = baseUrlText;
        newDefinition["models"] = MergeModelCatalog(GetValue(newDefinition, "models"), modelMap, OpenAiApiProtocol.UsesResponses(expectedPackage), baseUrlText);

        var removedModels = ProviderKeys(GetValue(definition, "models")).Where(m => !modelMap.Contains(m, StringComparer.Ordinal)).ToList();
        if (removedModels.Count > 0) { await ResolveReferencesAsync(providerIdText, removedModels, resolution, ct).ConfigureAwait(false); }

        var snapshot = await _api.GetConfigSnapshotAsync(ct).ConfigureAwait(false);
        var backup = _backups.Save(credentials, snapshot.Path, snapshot.Raw);
        _log.Info("已生成加密备份（API 更新）：" + backup);

        try
        {
            await _api.WriteApiProviderAsync(providerIdText, newDefinition, string.IsNullOrEmpty(newKeyText) ? null : newKeyText, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DshException("保存供应商配置失败，未做可确认的修改。加密备份：" + backup + "；" + _secrets.Redact(ex.Message));
        }

        // Read back what dsh will see.
        try
        {
            await AssertDefinitionSavedAsync(providerIdText, baseUrlText, modelMap, expectedPackage, backup, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(newKeyText)) { await AssertKeyStoredAsync(providerIdText, newKeyText, backup, ct).ConfigureAwait(false); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UpdateResult(ApiCredentialIdOf(providerIdText), providerIdText, backup, false,
                _secrets.Redact(ex.Message) + " 请手动检查 dsh 配置。");
        }

        _log.Info(string.IsNullOrEmpty(newKeyText)
            ? "已更新 API 供应商配置（保留原 Key）：" + providerIdText
            : "已更新 API 供应商并替换 Key：" + providerIdText);
        return UpdateResult(ApiCredentialIdOf(providerIdText), providerIdText, backup, true, ConfigReloadWarning);
    }

    // ------------------------------------------------------------------
    // Delete: Codex
    // ------------------------------------------------------------------

    private async Task<DeleteResult> RemoveCodexCredentialAsync(JsonNode credential, List<JsonNode?> credentials, CancellationToken ct)
    {
        var id = GetString(credential, "id");
        var label = SafeCodexLabel(credential);
        var wasActive = JsonNum.ToBool(GetValue(credential, "active"));

        var backup = _backups.Save(credentials, null, null);
        _log.Info("已生成加密备份（Codex 删除）：" + backup);

        try { await _api.DeleteCredentialAsync(id, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("Codex 删除异常：" + ex.Message);
            return RemoveResult(false, AccountKind.Codex, id, "openai", label, backup,
                "删除失败，无法确认是否完成；请先读取核对。加密备份：" + backup + "；" + _secrets.Redact(ex.Message));
        }

        List<JsonNode?> after;
        try { after = await GetFreshCredentialsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("Codex 删除后回读失败：" + ex.Message);
            return RemoveResult(false, AccountKind.Codex, id, "openai", label, backup,
                "删除后无法回读确认；请手动检查。加密备份：" + backup);
        }

        if (after.Any(c => c is not null && Ordinal(GetString(c, "id"), id)))
        {
            return RemoveResult(false, AccountKind.Codex, id, "openai", label, backup,
                "删除后回读仍发现该账号，无法确认已删除；请重试或手动检查。加密备份：" + backup);
        }

        _log.Info("已删除 Codex 账号：" + id);
        var warning = wasActive && !after.Any(c => c is not null && JsonNum.ToBool(GetValue(c, "active")) && Ordinal(GetString(c, "integrationID"), "openai"))
            ? "该账号是 dsh 当前使用的 Codex 账号，已一并移除 dsh 的当前登录；在切换到其它账号之前 dsh 没有可用的 Codex 账号。"
            : string.Empty;
        return RemoveResult(true, AccountKind.Codex, id, "openai", label, backup, warning);
    }

    // ------------------------------------------------------------------
    // Delete: API provider (+ its key reference)
    // ------------------------------------------------------------------

    private async Task<DeleteResult> RemoveApiProviderAsync(JsonNode? credential, List<JsonNode?> credentials, string providerId, ReferenceResolution? resolution, CancellationToken ct)
    {
        var targetId = credential is null ? string.Empty : GetString(credential, "id");

        var definitions = await GetViewsAsync(ct).ConfigureAwait(false);
        if (!definitions.TryGetValue(providerId, out var found) || found is not JsonObject definition)
        {
            throw new DshException("dsh 配置中找不到该供应商定义；已停止，未删除任何内容。");
        }

        if (!OpenAiApiProtocol.IsManagedPackage(GetString(definition, "package")))
        {
            throw new DshException("该供应商不是本工具支持的 OpenAI 兼容 / Responses 协议；已停止，避免破坏未知配置。");
        }

        var label = _secrets.Redact(GetString(definition, "name"));
        if (string.IsNullOrWhiteSpace(label)) { label = _secrets.Redact(providerId); }

        await ResolveReferencesAsync(providerId, null, resolution, ct).ConfigureAwait(false);

        var snapshot = await _api.GetConfigSnapshotAsync(ct).ConfigureAwait(false);
        var backup = _backups.Save(credentials, snapshot.Path, snapshot.Raw);
        _log.Info("已生成加密备份（API 删除）：" + backup);

        try { await _api.RemoveApiProviderAsync(providerId, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup,
                "删除失败，请读取核对后重试。加密备份：" + backup + "；" + _secrets.Redact(ex.Message));
        }

        // Confirm from disk (fresh read) that the definition and its key are gone.
        try
        {
            var after = await GetViewsAsync(ct).ConfigureAwait(false);
            if (after.ContainsKey(providerId))
            {
                return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup,
                    "删除后磁盘配置中仍存在该供应商定义，未完成；请手动检查。加密备份：" + backup);
            }

            var fresh = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
            if (fresh.Any(c => c is not null && Ordinal(GetString(c, "integrationID"), providerId)))
            {
                return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup,
                    "删除后仍存在该供应商的 Key，未完成；请手动检查。加密备份：" + backup);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup,
                "删除后无法回读确认；请手动检查。加密备份：" + backup);
        }

        _log.Info("已删除 API 供应商配置：" + providerId);
        return RemoveResult(true, AccountKind.Api, targetId, providerId, label, backup, ConfigReloadWarning);
    }

    // ------------------------------------------------------------------
    // Model references
    // ------------------------------------------------------------------

    /// <summary>The references to a provider (or to some of its models) that would dangle.</summary>
    public async Task<IReadOnlyList<ModelReference>> FindReferencesAsync(string providerId, IReadOnlyCollection<string>? models, CancellationToken ct = default)
        => _references is null ? Array.Empty<ModelReference>() : await _references.FindReferencesToAsync(providerId, models, ct).ConfigureAwait(false);

    private async Task ResolveReferencesAsync(string providerId, IReadOnlyCollection<string>? models, ReferenceResolution? resolution, CancellationToken ct)
    {
        if (_references is null) { return; }
        var refs = await _references.FindReferencesToAsync(providerId, models, ct).ConfigureAwait(false);
        if (refs.Count == 0) { return; }
        if (resolution is null) { throw new ReferencesInUseException(providerId, refs); }
        if (resolution.Kind == ReferenceActionKind.Retarget && string.Equals(resolution.Provider, providerId, StringComparison.Ordinal)
            && (models is null || models.Contains(resolution.Model ?? string.Empty)))
        {
            throw new DshException("不能把引用改指向即将删除的供应商或模型。");
        }

        var actions = ReferenceService.PlanResolution(refs, resolution);
        if (actions.Count > 0)
        {
            await _references.ApplyAsync(actions, ct).ConfigureAwait(false);
            _log.Info("已处理 " + actions.Count + " 处模型引用（供应商 " + providerId + "）。");
        }
    }

    // ------------------------------------------------------------------
    // Read-back helpers
    // ------------------------------------------------------------------

    private async Task AssertDefinitionSavedAsync(string providerId, string expectedBaseUrl, List<string> modelMap,
        string expectedPackage, string backup, CancellationToken ct)
    {
        var views = await GetViewsAsync(ct).ConfigureAwait(false);
        if (!views.TryGetValue(providerId, out var node) || node is not JsonObject definition)
        {
            throw new DshException("配置回读失败：供应商定义未保存。加密备份：" + backup);
        }

        if (!Ordinal(GetString(definition, "package"), expectedPackage))
        {
            throw new DshException("配置回读失败：API 协议与预期不一致。加密备份：" + backup);
        }

        if (!Ordinal(GetString(GetValue(definition, "settings"), "baseURL"), expectedBaseUrl))
        {
            throw new DshException("配置回读失败：接口地址与预期不一致。加密备份：" + backup);
        }

        var models = GetValue(definition, "models");
        foreach (var modelId in modelMap)
        {
            if (ProviderProperty(models, modelId) is null) { throw new DshException("配置回读失败：缺少模型 " + modelId + "。加密备份：" + backup); }
        }

        if (ProviderKeys(models).Count != modelMap.Count) { throw new DshException("配置回读失败：模型数量与预期不一致。加密备份：" + backup); }
    }

    private async Task AssertKeyStoredAsync(string providerId, string expectedKey, string backup, CancellationToken ct)
    {
        var credentials = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
        var found = credentials.Where(c => c is not null && Ordinal(GetString(c, "integrationID"), providerId)).ToList();
        if (found.Count != 1 || !Ordinal(GetString(GetValue(found[0], "value"), "key"), expectedKey))
        {
            throw new DshException("回读失败：新 Key 未按预期保存。加密备份：" + backup);
        }
    }

    private async Task<List<JsonNode?>> GetFreshCredentialsAsync(CancellationToken ct)
    {
        var array = await _api.GetCredentialsAsync(ct).ConfigureAwait(false);
        _secrets.Register(array);
        return array.Select(node => (JsonNode?)node).ToList();
    }

    /// <summary>The provider views by id, read fresh from the dsh patch file.</summary>
    private async Task<Dictionary<string, JsonNode>> GetViewsAsync(CancellationToken ct)
    {
        var entries = await _api.GetConfigEntriesAsync(ct).ConfigureAwait(false);
        _secrets.Register(entries);
        return AccountMapper.GetProviderDefinitions(entries);
    }

    private static string ApiCredentialIdOf(string providerId) => DshBackend.ApiCredentialId(providerId);

    // ------------------------------------------------------------------
    // Model / snapshot / comparison helpers
    // ------------------------------------------------------------------

    private static List<string> ModelMap(IReadOnlyList<string>? modelIds)
    {
        var map = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in modelIds ?? Array.Empty<string>())
        {
            if (raw is null) { continue; }
            foreach (var line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) { continue; }
                foreach (var character in trimmed)
                {
                    if (char.IsControl(character)) { throw new DshException("模型 ID 含有控制字符，已停止。"); }
                }
                if (!seen.Add(trimmed)) { throw new DshException("模型 ID 重复：" + trimmed); }
                map.Add(trimmed);
            }
        }
        if (map.Count == 0) { throw new DshException("模型列表不能为空，请至少保留一个模型。"); }
        return map;
    }

    private static JsonObject MergeModelCatalog(JsonNode? oldModels, List<string> modelMap, bool useResponses, string? baseUrl)
    {
        var result = new JsonObject();
        foreach (var modelId in modelMap)
        {
            var oldEntry = ProviderProperty(oldModels, modelId);
            if (oldEntry is not null)
            {
                var kept = (JsonObject)oldEntry.DeepClone();
                // Backfill tiers for a documented model that has none yet, so an existing
                // Responses Ark plan provider gains them on re-save. Custom variants are kept.
                ModelVariantBuilder.TryAddMissingVariants(kept, modelId, useResponses, baseUrl);
                result[modelId] = kept;
            }
            else { result[modelId] = ModelVariantBuilder.NewModelEntry(modelId, useResponses, baseUrl); }
        }
        return result;
    }

    private sealed record Snapshot(string Id, string Integration, bool Active, string Type, string MethodId,
        string Key, string Access, string Refresh, string Expires, JsonNode? Metadata);

    private static Snapshot? CredentialSnapshot(JsonNode? credential)
    {
        if (credential is null) { return null; }
        var value = GetValue(credential, "value");
        return new Snapshot(
            GetString(credential, "id"),
            GetString(credential, "integrationID"),
            JsonNum.ToBool(GetValue(credential, "active")),
            GetString(value, "type"),
            GetString(value, "methodID"),
            GetString(value, "key"),
            GetString(value, "access"),
            GetString(value, "refresh"),
            NodeText(GetValue(value, "expires")),
            GetValue(value, "metadata")?.DeepClone());
    }

    private static bool CredentialSnapshotEqual(JsonNode? credential, Snapshot? snapshot)
    {
        if (credential is null || snapshot is null) { return false; }
        if (!Ordinal(GetString(credential, "id"), snapshot.Id)) { return false; }
        if (!Ordinal(GetString(credential, "integrationID"), snapshot.Integration)) { return false; }
        if (JsonNum.ToBool(GetValue(credential, "active")) != snapshot.Active) { return false; }

        var value = GetValue(credential, "value");
        if (!Ordinal(GetString(value, "type"), snapshot.Type)) { return false; }
        if (!Ordinal(GetString(value, "methodID"), snapshot.MethodId)) { return false; }
        if (!Ordinal(GetString(value, "key"), snapshot.Key)) { return false; }
        if (!Ordinal(GetString(value, "access"), snapshot.Access)) { return false; }
        if (!Ordinal(GetString(value, "refresh"), snapshot.Refresh)) { return false; }
        if (!Ordinal(NodeText(GetValue(value, "expires")), snapshot.Expires)) { return false; }
        if (!DeepEqual(GetValue(value, "metadata"), snapshot.Metadata)) { return false; }
        return true;
    }

    // ------------------------------------------------------------------
    // Low-level JSON node helpers
    // ------------------------------------------------------------------

    private static JsonNode? GetValue(JsonNode? obj, string name)
    {
        if (obj is not JsonObject root || string.IsNullOrEmpty(name)) { return null; }
        if (root.TryGetPropertyValue(name, out var exact)) { return exact; }
        foreach (var kv in root)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { return kv.Value; }
        }
        return null;
    }

    private static string GetString(JsonNode? obj, string name)
    {
        var node = GetValue(obj, name);
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) { return text; }
        return node is null ? string.Empty : node.ToJsonString();
    }

    private static JsonNode? ProviderProperty(JsonNode? providers, string providerId)
    {
        if (providers is not JsonObject obj) { return null; }
        return obj.TryGetPropertyValue(providerId, out var node) ? node : null;
    }

    private static List<string> ProviderKeys(JsonNode? providers)
        => providers is JsonObject obj ? obj.Select(kv => kv.Key).ToList() : new List<string>();

    private static string NodeText(JsonNode? node) => node?.ToJsonString() ?? string.Empty;

    private static bool DeepEqual(JsonNode? left, JsonNode? right, int depth = 0)
    {
        if (depth > 40) { return false; }
        if (left is null && right is null) { return true; }
        if (left is null || right is null) { return false; }

        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            if (leftObject.Count != rightObject.Count) { return false; }
            foreach (var kv in leftObject)
            {
                if (!rightObject.TryGetPropertyValue(kv.Key, out var rightValue)) { return false; }
                if (!DeepEqual(kv.Value, rightValue, depth + 1)) { return false; }
            }
            return true;
        }

        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            if (leftArray.Count != rightArray.Count) { return false; }
            for (var index = 0; index < leftArray.Count; index++)
            {
                if (!DeepEqual(leftArray[index], rightArray[index], depth + 1)) { return false; }
            }
            return true;
        }

        return Ordinal(left.ToJsonString(), right.ToJsonString());
    }

    private static bool Ordinal(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static bool EqIgnoreCase(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool HttpsEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) { return false; }
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) { return false; }
        return uri.Scheme == "https"
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    private string SafeCodexLabel(JsonNode credential)
    {
        try
        {
            var mapped = _mapper.ConvertCredential(credential);
            if (!string.IsNullOrWhiteSpace(mapped.Label)) { return _secrets.Redact(mapped.Label); }
        }
        catch
        {
            // Fall through to the raw label.
        }
        return _secrets.Redact(GetString(credential, "label"));
    }

    private static DeleteResult RemoveResult(bool removed, AccountKind kind, string credentialId, string providerId,
        string label, string backup, string warning) => new()
        {
            Removed = removed,
            Kind = kind,
            CredentialID = credentialId,
            ProviderID = providerId,
            Label = label,
            Backup = backup,
            Warning = warning,
        };

    private static ApiSaveResult UpdateResult(string credentialId, string providerId, string backup, bool complete, string warning) => new()
    {
        Updated = true,
        Complete = complete,
        CredentialID = credentialId,
        ProviderID = providerId,
        Backup = backup,
        Warning = warning,
    };
}
