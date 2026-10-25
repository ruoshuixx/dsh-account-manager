using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.Models;
using AccountManager.Core.Dsh;
using AccountManager.Core.Security;

namespace AccountManager.Core.Import;

/// <summary>UI-facing preview row; never contains secret values.</summary>
public sealed class ImportPreviewRow
{
    public string Key = string.Empty;

    public string Label = string.Empty;

    public string Provider = string.Empty;

    public string Identity = string.Empty;

    public string Workspace = string.Empty;

    public string Plan = string.Empty;

    public string Expires = string.Empty;

    public string Status = string.Empty;

    public AccountKind Kind;
}

/// <summary>Preview result plus the internal rows consumed by <see cref="ImportService.ImportAsync"/>.</summary>
public sealed class ImportPreview
{
    public List<ImportPreviewRow> Rows { get; init; } = new();

    /// <summary>Internal rows (contain secrets); do not display.</summary>
    public object Payload { get; init; } = new();

    /// <summary>
    /// When true, only merges the real subscription expiry into matching existing accounts.
    /// Existing tokens, API keys and provider definitions are never touched, no account is created.
    /// </summary>
    public bool SubscriptionOnly { get; set; }

    /// <summary>
    /// When non-null, only rows whose <see cref="ImportRow.Key"/> is in this set are imported.
    /// Null means import every row. Lets the UI honor the per-row checkboxes.
    /// </summary>
    public HashSet<string>? SelectedKeys { get; set; }

    /// <summary>Planned repairs of dangling model references that this import makes possible (default: all applied).</summary>
    public List<ImportRepair> Repairs { get; init; } = new();

    /// <summary>When non-null, only repairs whose <see cref="ImportRepair.Key"/> is in this set are applied. Null means all.</summary>
    public HashSet<string>? SelectedRepairKeys { get; set; }

    /// <summary>Notes about how API accounts map onto providers dsh already has or had (no secrets).</summary>
    public List<string> Notes { get; init; } = new();
}

/// <summary>A planned repair of one dangling model reference: it will point at a provider this import writes.</summary>
public sealed class ImportRepair
{
    internal ModelReference Reference { get; init; } = null!;

    public string Key => Reference.Key;

    public string TargetProvider { get; init; } = string.Empty;

    public string TargetName { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string Description => "将修复：" + Reference.Label + " " + Reference.Provider + "/" + Reference.Model + " → " + TargetName;
}

/// <summary>Result of an import run. All messages are already redacted.</summary>
public sealed class ImportResult
{
    public string Backup = string.Empty;

    public int Failed;

    public List<string> Messages = new();
}

/// <summary>
/// Imports Cockpit-format account arrays: parse and preview, then write. Backs up before every
/// write, never activates another account (activate index is fixed at -1) and redacts every
/// message. Secrets are never logged or returned.
/// </summary>
public sealed class ImportService
{
    private const long MaxImportBytes = 20L * 1024 * 1024;

    private readonly IAccountBackend _api;
    private readonly BackupService _backups;
    private readonly SecretRegistry _secrets;
    private readonly AppLog _log;
    private readonly SubscriptionStore? _subscriptions;
    private readonly ReferenceService? _references;
    private readonly SubagentRouteService? _subagents;
    private readonly CodexFastService? _fast;

    public ImportService(IAccountBackend api, BackupService backups, SecretRegistry secrets, AppLog log, SubscriptionStore? subscriptions = null,
        ReferenceService? references = null, SubagentRouteService? subagents = null, CodexFastService? fast = null)
    {
        _fast = fast;
        _references = references;
        _subagents = subagents;
        _api = api;
        _backups = backups;
        _secrets = secrets;
        _log = log;
        _subscriptions = subscriptions;
    }

    /// <summary>
    /// Parses a Cockpit export array into preview rows without writing anything. Rejects files
    /// over 20MB, non-array roots, empty arrays and duplicate identities.
    /// </summary>
    public async Task<ImportPreview> PreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        _log.Info("开始读取导入文件：" + path);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new DshException("无法读取 JSON 文件；请检查文件格式。");
        }

        if (new FileInfo(path).Length > MaxImportBytes)
        {
            throw new DshException("JSON 超过 20MB，已拒绝读取。");
        }

        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new DshException("无法读取 JSON 文件；请检查文件格式。");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            throw new DshException("无法读取 JSON 文件；请检查文件格式。");
        }

        if (!raw.TrimStart().StartsWith("[", StringComparison.Ordinal))
        {
            throw new DshException("当前试用版需要 Cockpit 导出的账号数组。");
        }

        if (parsed is not JsonArray accounts)
        {
            throw new DshException("当前试用版需要 Cockpit 导出的账号数组。");
        }

        _secrets.Register(accounts);
        if (accounts.Count == 0)
        {
            throw new DshException("导出文件没有账号。");
        }

        var oauthMethod = VaultCredentialMapper.DefaultMethodId;

        var rows = new List<ImportRow>();
        for (var i = 0; i < accounts.Count; i++)
        {
            ImportRow row;
            try
            {
                row = ConvertAccount(accounts[i], oauthMethod);
            }
            catch (Exception ex)
            {
                throw new DshException("第 " + (i + 1) + " 个账号：" + ex.Message);
            }

            _secrets.Register(row.Value);
            row.Key = "preview-" + row.Provider + "|" + Sha256Hex(row.Identity);
            rows.Add(row);
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!identities.Add(row.Provider + "\u0000" + row.Identity))
            {
                throw new DshException("导出文件中存在重复账号身份，请先去重；不会猜测哪份令牌更新。");
            }
        }

        _log.Info("解析成功：" + rows.Count + " 项；未执行写入。");

        var result = new ImportPreview
        {
            Rows = rows.Select(ToPreviewRow).ToList(),
            Payload = rows,
        };
        await PlanRepairsAsync(rows, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Writes the previewed rows: update-or-create each credential (create then delete old),
    /// after an encrypted backup. Activation index is always -1.
    /// </summary>
    public async Task<ImportResult> ImportAsync(ImportPreview preview, CancellationToken cancellationToken = default)
    {
        if (preview is null) { throw new DshException("缺少导入预览数据。"); }
        _ = _fast?.TakePendingLines(); // changes remembered from earlier activations belong to earlier operations
        if (preview.Payload is not List<ImportRow> rows) { throw new DshException("导入预览数据无效；请重新预览。"); }

        if (preview.SelectedKeys is not null)
        {
            rows = rows.Where(r => preview.SelectedKeys.Contains(r.Key)).ToList();
            if (rows.Count == 0) { throw new DshException("没有勾选任何账号，已取消导入。"); }
        }

        foreach (var row in rows)
        {
            _secrets.Register(row.Value);
            if (row.Definition is not null) { _secrets.Register(row.Definition); }
        }

        _log.Info("开始导入：" + rows.Count + " 项；激活选择索引=-1");

        var messages = new List<string>();
        var credentials = await _api.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        _secrets.Register(credentials);

        // Capture every source entry into the local mirror before any filtering, so the cache
        // keeps Cockpit fields dsh never stores (id_token claim, plan, org, quota history…).
        var cacheRecords = BuildCacheRecords(rows);

        if (preview.SubscriptionOnly)
        {
            var apiSkipped = rows.Count(r => r.Kind == AccountKind.Api);
            if (apiSkipped > 0)
            {
                Report(messages, "仅更新订阅模式：跳过 " + apiSkipped + " 个 API Key 账号，配置不变。", LogLevel.Info);
            }

            var merged = new List<ImportRow>();
            foreach (var row in rows)
            {
                if (row.Kind != AccountKind.Codex) { continue; }

                var desiredNode = JsonMerge.Get(JsonMerge.Get(row.Value, "metadata"), "subscriptionActiveUntil");
                if (desiredNode is null)
                {
                    Report(messages, row.Label + "：源数据没有订阅到期时间，跳过。", LogLevel.Info);
                    continue;
                }

                var matches = FindMatches(row, credentials);
                if (matches.Count == 0)
                {
                    Report(messages, row.Label + "：未找到匹配的现有账号，跳过（不新建）。", LogLevel.Info);
                    continue;
                }

                if (matches.Count > 1)
                {
                    throw new DshException("存在多个匹配账号，停止操作以免误删；请先整理重复账号。");
                }

                // Take the existing credential as-is (tokens, keys, method, expiry) and only
                // overlay the subscription metadata; the source tokens are never written back.
                var oldValue = JsonMerge.Get(matches[0], "value");
                var mergedValue = oldValue is JsonObject existing ? (JsonObject)existing.DeepClone() : new JsonObject();
                if (JsonMerge.Get(mergedValue, "metadata") is not JsonObject metadata)
                {
                    metadata = new JsonObject();
                    mergedValue["metadata"] = metadata;
                }

                var currentToken = JsonMerge.Get(metadata, "subscriptionActiveUntil")?.ToJsonString() ?? string.Empty;
                if (string.Equals(currentToken, desiredNode.ToJsonString(), StringComparison.Ordinal))
                {
                    Report(messages, row.Label + "：订阅到期无变化，跳过。", LogLevel.Info);
                    continue;
                }

                metadata["subscriptionActiveUntil"] = desiredNode.DeepClone();
                row.Value = mergedValue;
                merged.Add(row);
            }

            rows = merged;
            _log.Info("仅更新订阅模式：仅合并订阅到期，准备写回 " + rows.Count + " 个 Codex 账号。");
        }

        // Which provider each API account lands on, and its final definition (view), merged onto the existing one.
        // Computed before anything is written.
        var apiDefinitions = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var apiTargets = new Dictionary<ImportRow, ApiTarget>();
        if (rows.Any(r => r.Kind == AccountKind.Api))
        {
            var entries = await _api.GetConfigEntriesAsync(cancellationToken).ConfigureAwait(false);
            _secrets.Register(entries);
            var existingDefinitions = AccountMapper.GetProviderDefinitions(entries);
            var dangling = await DanglingProviderIdsAsync(cancellationToken).ConfigureAwait(false);
            apiTargets = ResolveApiTargets(rows, credentials, existingDefinitions, dangling);
            foreach (var (row, target) in apiTargets)
            {
                if (target.Note.Length > 0) { Report(messages, row.Label + "：" + target.Note, LogLevel.Info); }
                row.Provider = target.ProviderId;
            }

            foreach (var row in rows)
            {
                if (row.Kind != AccountKind.Api) { continue; }
                var target = apiTargets[row];

                JsonObject definition;
                if (existingDefinitions.TryGetValue(row.Provider, out var existingNode))
                {
                    if (existingNode is not JsonObject existing)
                    {
                        throw new DshException("匹配供应商配置不是对象，停止更新。");
                    }

                    // Compare the original URL and credential before applying imported settings.
                    var refreshExistingVariants = CanRefreshImportedVariants(row, existing, credentials);
                    // Preserve unrelated settings and model catalog when a compact export omitted them.
                    definition = (JsonObject)existing.DeepClone();
                    var oldPackage = JsonMerge.GetString(definition, "package");
                    if (target.KeepExisting)
                    {
                        // An account that matched a provider dsh already has (same endpoint and key): keep its name,
                        // protocol and models, and only add the models it lacks.
                        if (string.IsNullOrWhiteSpace(JsonMerge.GetString(definition, "name"))) { definition["name"] = JsonMerge.Get(row.Definition, "name")?.DeepClone(); }
                    }
                    else
                    {
                        definition["name"] = JsonMerge.Get(row.Definition, "name")?.DeepClone();
                        if (row.ProtocolSupplied || string.IsNullOrEmpty(oldPackage))
                        {
                            definition["package"] = JsonMerge.Get(row.Definition, "package")?.DeepClone();
                        }
                    }

                    if (JsonMerge.Get(definition, "settings") is null)
                    {
                        definition["settings"] = new JsonObject();
                    }

                    if (JsonMerge.Get(definition, "settings") is not JsonObject settings)
                    {
                        throw new DshException("供应商 settings 配置不是对象，停止更新。");
                    }

                    if (!target.KeepExisting) { settings["baseURL"] = JsonMerge.Get(JsonMerge.Get(row.Definition, "settings"), "baseURL")?.DeepClone(); }
                    var package = JsonMerge.GetString(definition, "package");
                    if (target.KeepExisting)
                    {
                        definition["models"] = AddMissingModels(JsonMerge.Get(definition, "models"), JsonMerge.Get(row.Definition, "models"), OpenAiApiProtocol.UsesResponses(package), JsonMerge.GetString(settings, "baseURL"));
                    }
                    else if (row.ModelsSupplied || JsonMerge.Get(definition, "models") is null)
                    {
                        definition["models"] = MergeImportedModels(JsonMerge.Get(definition, "models"), JsonMerge.Get(row.Definition, "models"), OpenAiApiProtocol.UsesResponses(package), JsonMerge.GetString(settings, "baseURL"));
                    }
                    if (refreshExistingVariants && definition["models"] is JsonObject refreshModels)
                    {
                        foreach (var (modelId, model) in refreshModels)
                        {
                            if (model is not JsonObject entry) { continue; }
                            var generated = ModelVariantBuilder.NewModelEntry(modelId, useResponses: true,
                                baseUrl: JsonMerge.GetString(settings, "baseURL"));
                            if (generated["variants"] is { } variants) { entry["variants"] = variants.DeepClone(); }
                        }
                    }
                }
                else
                {
                    definition = (JsonObject)row.Definition!.DeepClone();
                }

                apiDefinitions[row.Provider] = definition;
            }
        }

        foreach (var row in rows)
        {
            if (FindMatches(row, credentials).Count > 1)
            {
                throw new DshException("存在多个匹配旧账号，停止操作以免误删；请先整理重复账号。");
            }
        }

        var snapshot = await _api.GetConfigSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var backup = _backups.Save(credentials, snapshot.Path, snapshot.Raw);
        Report(messages, "加密备份：" + backup, LogLevel.Info);

        var failed = 0;
        var apiWritten = new List<ImportRow>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            try
            {
                // Recheck the latest state before each account update.
                var current = await _api.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);

                if (row.Kind == AccountKind.Api)
                {
                    // The provider definition and its key reference are written together; the key is only
                    // sent when it differs from the stored one.
                    var stored = current.OfType<JsonNode>().FirstOrDefault(c =>
                        JsonMerge.GetString(c, "integrationID") == row.Provider
                        && string.Equals(JsonMerge.GetString(JsonMerge.Get(c, "value"), "type"), "key", StringComparison.OrdinalIgnoreCase));
                    var incomingKey = JsonMerge.GetString(row.Value, "key");
                    var sameKey = stored is not null && JsonMerge.GetString(JsonMerge.Get(stored, "value"), "key") == incomingKey;
                    await _api.WriteApiProviderAsync(row.Provider, apiDefinitions[row.Provider], sameKey ? null : incomingKey, cancellationToken).ConfigureAwait(false);
                    apiWritten.Add(row);
                    Report(messages, row.Label + "：" + (apiTargets[row].Existing ? "更新成功" : "导入成功"), LogLevel.Info);
                    continue;
                }

                var matches = FindMatches(row, current);
                if (matches.Count > 1) { throw new DshException("匹配账号数量发生变化，跳过此账号。"); }

                var old = matches.Count > 0 ? matches[0] : null;
                var oldId = old is null ? null : JsonMerge.GetString(old, "id");
                var oldValue = old is null ? null : JsonMerge.Get(old, "value");

                if (oldValue is not null && SameCredential(oldValue, row.Value))
                {
                    Report(messages, row.Label + "：凭据相同，跳过更新。", LogLevel.Info);
                    continue;
                }

                if (oldValue is not null)
                {
                    var oldExpires = JsonNum.ToLong(JsonMerge.Get(oldValue, "expires")) ?? 0;
                    var newExpires = JsonNum.ToLong(JsonMerge.Get(row.Value, "expires")) ?? 0;
                    if (oldExpires > newExpires)
                    {
                        Report(messages, row.Label + "：备份令牌到期时间早于现有令牌，为避免回滚，保留现有凭据。", LogLevel.Warn);
                        continue;
                    }
                }

                // Activate index is fixed at -1, so only an already-active old credential stays active.
                var activate = old is not null && JsonNum.ToBool(JsonMerge.Get(old, "active"));
                var label = row.Label + " [" + row.Workspace + "]";

                var createdId = await _api.CreateCredentialAsync(row.Provider, label, row.Value, activate, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(createdId)) { throw new DshException("没有收到新账号 ID，保留旧账号。"); }

                if (old is not null && !string.IsNullOrWhiteSpace(oldId))
                {
                    try
                    {
                        await _api.DeleteCredentialAsync(oldId!, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _log.Error("移除旧凭据失败", ex);
                        Report(messages, row.Label + "：新账号已保存，但旧凭据未移除；请检查重复项。", LogLevel.Error);
                        continue;
                    }
                }

                Report(messages, row.Label + "：" + (old is not null ? "更新成功" : "导入成功"), LogLevel.Info);
            }
            catch (Exception ex)
            {
                failed++;
                _log.Error("账号写入异常", ex);
                Report(messages, row.Label + "：失败。" + ex.Message, LogLevel.Error);
            }
        }

        if (apiWritten.Count > 0)
        {
            Report(messages, "已更新 API 接口和模型配置；运行中的 dsh 会自动热加载，不重启。", LogLevel.Info);
        }

        if (_subscriptions is not null && cacheRecords.Count > 0)
        {
            try
            {
                await _subscriptions.SaveAsync(cacheRecords, cancellationToken).ConfigureAwait(false);
                Report(messages, "已把 " + cacheRecords.Count + " 条 Cockpit 账户信息缓存到应用数据目录（" + SubscriptionStore.FileName + "）。", LogLevel.Info);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn("写入账户缓存失败：" + ex.Message);
            }
        }

        foreach (var row in apiWritten)
        {
            var package = JsonMerge.GetString(apiDefinitions[row.Provider], "package");
            if (package is null || !OpenAiApiProtocol.IsManagedPackage(package)) { continue; }
            var runtime = await _api.VerifyProviderAsync(row.Provider, package, CancellationToken.None).ConfigureAwait(false);
            Report(messages, row.Label + "：" + runtime.Message,
                runtime.State == ProviderRuntimeState.Applied ? LogLevel.Info : LogLevel.Warn);
        }

        await ApplyRepairsAsync(preview, apiTargets, messages, cancellationToken).ConfigureAwait(false);
        await SyncFastModelsAsync(preview, messages, cancellationToken).ConfigureAwait(false);
        await MergeSubagentRoutesAsync(preview, credentials, messages, cancellationToken).ConfigureAwait(false);

        Report(messages, "完成。失败或需检查：" + failed + "。保存成功不代表令牌/API 已联网验证。", failed > 0 ? LogLevel.Warn : LogLevel.Info);
        return new ImportResult { Backup = backup, Failed = failed, Messages = messages };
    }

    // ------------------------------------------------------------------
    // API accounts -> providers; dangling model references
    // ------------------------------------------------------------------

    private sealed class ApiTarget
    {
        public string ProviderId = string.Empty;

        /// <summary>The provider already exists in dsh.</summary>
        public bool Existing;

        /// <summary>Matched a provider that was not created by this account's own id: its name, protocol and models are kept.</summary>
        public bool KeepExisting;

        public string Note = string.Empty;
    }

    private static readonly System.Text.RegularExpressions.Regex ProviderIdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$");

    /// <summary>scheme://host[:port]/path with a lower-case host, no default port, no trailing slash, no query or fragment.</summary>
    internal static string NormalizeEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) { return string.Empty; }
        var path = uri.AbsolutePath.TrimEnd('/');
        return uri.Scheme.ToLowerInvariant() + "://" + uri.Host.ToLowerInvariant() + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port) + path;
    }

    /// <summary>
    /// Decides the provider every API account lands on: a provider dsh already has under the account's own id, one that has
    /// the same endpoint and key (whatever its id, name or protocol), the id of a provider dsh's settings still refer to but
    /// that no longer exists (so those references are valid again), or a new <c>cockpit-</c> provider.
    /// </summary>
    private static Dictionary<ImportRow, ApiTarget> ResolveApiTargets(IEnumerable<ImportRow> rows, JsonArray credentials,
        IReadOnlyDictionary<string, JsonNode> views, IReadOnlySet<string> danglingIds)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in credentials.OfType<JsonNode>())
        {
            var value = JsonMerge.Get(c, "value");
            if (!string.Equals(JsonMerge.GetString(value, "type"), "key", StringComparison.OrdinalIgnoreCase)) { continue; }
            var id = JsonMerge.GetString(c, "integrationID");
            var key = JsonMerge.GetString(value, "key");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(key)) { keys[id] = key; }
        }

        var targets = new Dictionary<ImportRow, ApiTarget>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.Kind == AccountKind.Api))
        {
            var sourceId = row.Identity;
            var incomingKey = JsonMerge.GetString(row.Value, "key") ?? string.Empty;
            var incomingUrl = NormalizeEndpoint(JsonMerge.GetString(JsonMerge.Get(row.Definition, "settings"), "baseURL"));
            ApiTarget? target = null;

            if (views.ContainsKey(row.Provider) && !claimed.Contains(row.Provider))
            {
                target = new ApiTarget { ProviderId = row.Provider, Existing = true };
            }
            else if (ProviderIdPattern.IsMatch(sourceId) && views.ContainsKey(sourceId) && !claimed.Contains(sourceId))
            {
                target = new ApiTarget { ProviderId = sourceId, Existing = true, KeepExisting = true, Note = "与 dsh 里已有的供应商 " + sourceId + " 同名，更新它而不是新建。" };
            }
            else
            {
                var byEndpoint = views.Where(kv => !claimed.Contains(kv.Key)
                        && incomingUrl.Length > 0
                        && NormalizeEndpoint(JsonMerge.GetString(JsonMerge.Get(kv.Value, "settings"), "baseURL")) == incomingUrl
                        && keys.TryGetValue(kv.Key, out var existingKey) && existingKey == incomingKey)
                    .Select(kv => kv.Key).ToList();
                if (byEndpoint.Count > 0)
                {
                    var id = byEndpoint[0];
                    var name = JsonMerge.GetString(views[id], "name");
                    target = new ApiTarget
                    {
                        ProviderId = id, Existing = true, KeepExisting = true,
                        Note = "接口地址和密钥与 dsh 里已有的供应商 " + (string.IsNullOrWhiteSpace(name) ? id : name + "（" + id + "）") + " 相同，更新它而不是新建。",
                    };
                }
                else if (ProviderIdPattern.IsMatch(sourceId) && danglingIds.Contains(sourceId) && !claimed.Contains(sourceId))
                {
                    target = new ApiTarget { ProviderId = sourceId, Note = "dsh 的设置仍在引用已不存在的供应商 " + sourceId + "，将按这个 ID 恢复它。" };
                }
            }

            target ??= new ApiTarget { ProviderId = row.Provider };
            claimed.Add(target.ProviderId);
            targets[row] = target;
        }

        return targets;
    }

    private async Task<HashSet<string>> DanglingProviderIdsAsync(CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (_references is null) { return ids; }
        try
        {
            foreach (var r in (await _references.ScanAsync(ct).ConfigureAwait(false)).Dangling) { ids.Add(r.Provider); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Warn("检查 dsh 的模型引用失败：" + _secrets.Redact(ex.Message)); }
        return ids;
    }

    /// <summary>Existing models stay exactly as they are; models the account lists and the provider lacks are added.</summary>
    private static JsonObject AddMissingModels(JsonNode? existing, JsonNode? incoming, bool useResponses, string? baseUrl)
    {
        var result = existing is JsonObject e ? (JsonObject)e.DeepClone() : new JsonObject();
        if (incoming is not JsonObject models) { return result; }
        foreach (var (id, value) in models)
        {
            if (result.ContainsKey(id)) { continue; }
            var entry = value?.DeepClone() as JsonObject ?? new JsonObject { ["name"] = id };
            entry.Remove("variants");
            ModelVariantBuilder.TryAddMissingVariants(entry, id, useResponses, baseUrl);
            result[id] = entry;
        }

        return result;
    }

    /// <summary>Plans repairs of dangling model references that the API accounts of this import make possible.</summary>
    private async Task PlanRepairsAsync(List<ImportRow> rows, ImportPreview preview, CancellationToken ct)
    {
        var apiRows = rows.Where(r => r.Kind == AccountKind.Api).ToList();
        if (_references is null || apiRows.Count == 0) { return; }
        try
        {
            var credentials = await _api.GetCredentialsAsync(ct).ConfigureAwait(false);
            var entries = await _api.GetConfigEntriesAsync(ct).ConfigureAwait(false);
            var views = AccountMapper.GetProviderDefinitions(entries);
            var scan = await _references.ScanAsync(ct).ConfigureAwait(false);
            var dangling = scan.Dangling.Where(r => r.Editable).ToList();
            var danglingIds = new HashSet<string>(scan.Dangling.Select(r => r.Provider), StringComparer.Ordinal);
            var targets = ResolveApiTargets(apiRows, credentials, views, danglingIds);
            foreach (var (row, target) in targets)
            {
                if (target.Note.Length > 0) { preview.Notes.Add(row.Label + "：" + target.Note); }
            }

            // Models each target will offer after the import.
            var offered = new Dictionary<string, (string Name, HashSet<string> Models)>(StringComparer.Ordinal);
            foreach (var (row, target) in targets)
            {
                var models = new HashSet<string>(StringComparer.Ordinal);
                if (views.TryGetValue(target.ProviderId, out var view) && JsonMerge.Get(view, "models") is JsonObject existingModels)
                {
                    foreach (var kv in existingModels) { models.Add(kv.Key); }
                }

                if (!target.KeepExisting || !target.Existing)
                {
                    if (JsonMerge.Get(row.Definition, "models") is JsonObject incoming) { foreach (var kv in incoming) { models.Add(kv.Key); } }
                }
                else if (JsonMerge.Get(row.Definition, "models") is JsonObject added)
                {
                    foreach (var kv in added) { models.Add(kv.Key); }
                }

                var name = views.TryGetValue(target.ProviderId, out var v2) && target.KeepExisting
                    ? JsonMerge.GetString(v2, "name") ?? row.Label
                    : row.Label;
                offered[target.ProviderId] = (string.IsNullOrWhiteSpace(name) ? target.ProviderId : name, models);
            }

            foreach (var reference in dangling)
            {
                if (offered.ContainsKey(reference.Provider)) { continue; } // the provider comes back under its own id
                var candidates = offered.Where(kv => kv.Value.Models.Contains(reference.Model)).ToList();
                if (candidates.Count != 1) { continue; }
                preview.Repairs.Add(new ImportRepair
                {
                    Reference = reference,
                    TargetProvider = candidates[0].Key,
                    TargetName = candidates[0].Value.Name,
                    Model = reference.Model,
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Warn("无法预先规划引用修复：" + _secrets.Redact(ex.Message)); }
    }

    /// <summary>
    /// Codex Fast mode after every import that touched accounts or providers: the small component is installed or updated in the dsh
    /// profile and the missing <c>-fast</c> models are appended to the <c>openai-codex</c> route (see <see cref="CodexFastService"/>).
    /// The route work above has already run, so a route the import just created is picked up. Runs before the subagent merge, which
    /// leaves Fast models out. The default model is never changed.
    /// </summary>
    private async Task SyncFastModelsAsync(ImportPreview preview, List<string> messages, CancellationToken ct)
    {
        if (_fast is null || preview.SubscriptionOnly) { return; }
        try
        {
            var current = await _api.GetCredentialsAsync(ct).ConfigureAwait(false);
            var result = await Task.Run(() => _fast.Sync(current), ct).ConfigureAwait(false);
            var lines = _fast.TakePendingLines().Concat(CodexFastService.Describe(result)).Distinct(StringComparer.Ordinal).ToList();
            foreach (var line in lines) { Report(messages, line, LogLevel.Info); }
            if (result.Note.Length > 0 && !result.Changed) { Report(messages, result.Note, LogLevel.Warn); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report(messages, "未能设置 Codex Fast：" + ex.Message, LogLevel.Warn);
        }
    }

    /// <summary>
    /// Every import lets dsh's main agent delegate to the providers and models now configured: the candidate routes are
    /// merged into <c>subagent-model-selection-settings</c> (kept entries stay, no duplicates). The change applies to new
    /// dsh conversations only, which the result says.
    /// </summary>
    private async Task MergeSubagentRoutesAsync(ImportPreview preview, JsonArray credentials, List<string> messages, CancellationToken ct)
    {
        if (_subagents is null || preview.SubscriptionOnly) { return; }
        try
        {
            var current = await _api.GetCredentialsAsync(ct).ConfigureAwait(false);
            var merge = await Task.Run(() => _subagents.Merge(current), ct).ConfigureAwait(false);
            foreach (var line in SubagentRouteService.Describe(merge)) { Report(messages, line, LogLevel.Info); }
            if (merge.Note.Length > 0 && !merge.Changed) { _log.Info(merge.Note); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report(messages, "未能设置子代理可用模型：" + ex.Message, LogLevel.Warn);
        }
    }

    private async Task ApplyRepairsAsync(ImportPreview preview, Dictionary<ImportRow, ApiTarget> targets, List<string> messages, CancellationToken ct)
    {
        if (_references is null) { return; }
        try
        {
            var selected = preview.SelectedRepairKeys is null ? preview.Repairs : preview.Repairs.Where(r => preview.SelectedRepairKeys.Contains(r.Key)).ToList();
            if (selected.Count > 0)
            {
                var actions = selected.Select(r => new ReferenceAction(r.Reference, ReferenceActionKind.Retarget, r.TargetProvider, r.Model)).ToList();
                await _references.ApplyAsync(actions, ct).ConfigureAwait(false);
                foreach (var r in selected) { Report(messages, "已修复：" + r.Description.Replace("将修复：", string.Empty), LogLevel.Info); }
            }

            if (targets.Count == 0 && selected.Count == 0) { return; }
            var scan = await _references.ScanAsync(ct).ConfigureAwait(false);
            var remaining = scan.Dangling;
            if (remaining.Count > 0)
            {
                Report(messages, "仍有 " + remaining.Count + " 处设置引用了不存在的供应商（" + string.Join("；", remaining.Take(5).Select(r => r.Describe())) + (remaining.Count > 5 ? "…" : string.Empty) + "）；可在主界面点「修复」处理。", LogLevel.Warn);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report(messages, "修复模型引用失败：" + ex.Message, LogLevel.Warn);
        }
    }

    private ImportPreviewRow ToPreviewRow(ImportRow row) => new()
    {
        Key = row.Key,
        Label = _secrets.Redact(row.Label),
        Provider = _secrets.Redact(row.Provider),
        Identity = _secrets.Redact(row.Identity),
        Workspace = _secrets.Redact(row.Workspace),
        Plan = _secrets.Redact(row.Plan),
        Expires = _secrets.Redact(row.Expires),
        Status = _secrets.Redact(row.Status),
        Kind = row.Kind,
    };

    /// <summary>Normalizes one account: pass through Cockpit shape or convert the legacy codex token shape.</summary>
    private static JsonNode NormalizeAccount(JsonNode? account)
    {
        if (account is null)
        {
            throw new DshException("不支持的账号结构：需要 Cockpit auth_mode，或 type=codex 的顶层令牌格式。");
        }

        var authMode = JsonMerge.GetString(account, "auth_mode");
        if (!string.IsNullOrEmpty(authMode))
        {
            if (account is not JsonObject nativeAccount)
            {
                throw new DshException("不支持的账号结构：需要 Cockpit auth_mode，或 type=codex 的顶层令牌格式。");
            }

            var canonical = CockpitSecretFields.Canonicalize(nativeAccount);
            if (canonical["api_wire_api"] is JsonValue wireNode && wireNode.TryGetValue<string>(out var wireText))
            {
                var normalizedWire = wireText.ToLowerInvariant();
                if (normalizedWire is "responses" or "chat_completions") { canonical["api_wire_api"] = normalizedWire; }
            }

            if (CockpitAccountSchema.GetOptionalFieldInvalidReason(canonical) is not null)
            {
                throw new DshException("账号字段格式不符合 Cockpit Codex 结构。");
            }

            foreach (var timeName in new[] { "created_at", "last_used" })
            {
                if (canonical.ContainsKey(timeName) && !CockpitAccountSchema.IsInt64Value(canonical[timeName]))
                {
                    throw new DshException("账号时间字段格式无效；需要整数 Unix 时间。");
                }
            }

            return canonical;
        }

        var type = JsonMerge.GetString(account, "type");
        var accessToken = JsonMerge.GetString(account, "access_token");
        var refreshToken = JsonMerge.GetString(account, "refresh_token");
        var idToken = JsonMerge.GetString(account, "id_token");
        if (string.Equals(type, "codex", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(accessToken) && !string.IsNullOrEmpty(refreshToken))
        {
            var claims = Jwt.ReadPayload(accessToken);
            var idClaims = Jwt.ReadPayload(idToken);
            var plan = JsonMerge.GetString(JsonMerge.Get(claims, "https://api.openai.com/auth"), "chatgpt_plan_type") ?? string.Empty;
            if (string.IsNullOrEmpty(plan))
            {
                plan = JsonMerge.GetString(JsonMerge.Get(idClaims, "https://api.openai.com/auth"), "chatgpt_plan_type") ?? string.Empty;
            }

            var legacy = new JsonObject
            {
                ["auth_mode"] = "oauth",
                ["email"] = JsonMerge.GetString(account, "email") ?? string.Empty,
                ["account_id"] = JsonMerge.GetString(account, "account_id") ?? string.Empty,
                ["plan_type"] = plan,
                ["tokens"] = new JsonObject
                {
                    ["access_token"] = accessToken,
                    ["refresh_token"] = refreshToken,
                    ["id_token"] = idToken ?? string.Empty,
                },
            };
            var legacySubscription = JsonMerge.Get(account, "subscription_active_until")
                ?? JsonMerge.Get(account, "subscriptionActiveUntil");
            if (legacySubscription is not null) { legacy["subscription_active_until"] = legacySubscription.DeepClone(); }
            return legacy;
        }

        throw new DshException("不支持的账号结构：需要 Cockpit auth_mode，或 type=codex 的顶层令牌格式。");
    }

    /// <summary>Converts one Cockpit account to an internal import row.</summary>
    internal static ImportRow ConvertAccount(JsonNode? account, string oauthMethod)
    {
        var normalized = NormalizeAccount(account);
        var authMode = JsonMerge.GetString(normalized, "auth_mode");

        if (string.Equals(authMode, "oauth", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = JsonMerge.Get(normalized, "tokens");
            var access = JsonMerge.GetString(tokens, "access_token");
            var refresh = JsonMerge.GetString(tokens, "refresh_token");
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
            {
                throw new DshException("OAuth 账号缺少访问令牌或刷新令牌。");
            }

            var claims = Jwt.ReadPayload(access);
            var exp = JsonNum.ToLong(JsonMerge.Get(claims, "exp"));
            if (exp is null || exp.Value == 0)
            {
                throw new DshException("无法从 OAuth 访问令牌读取到期时间。");
            }

            var accountId = JsonMerge.GetString(normalized, "account_id");
            if (string.IsNullOrEmpty(accountId))
            {
                accountId = JsonMerge.GetString(JsonMerge.Get(claims, "https://api.openai.com/auth"), "chatgpt_account_id");
            }

            if (string.IsNullOrEmpty(accountId))
            {
                throw new DshException("OAuth 账号缺少 account_id，不能安全导入。");
            }

            var email = JsonMerge.GetString(normalized, "email");
            if (string.IsNullOrEmpty(email))
            {
                email = JsonMerge.GetString(JsonMerge.Get(claims, "https://api.openai.com/profile"), "email");
            }

            if (string.IsNullOrEmpty(email)) { email = "Codex " + accountId; }

            var expired = exp.Value <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var workspace = accountId.Length > 8 ? accountId[^8..] : accountId;

            var metadata = new JsonObject
            {
                ["accountID"] = accountId,
                ["cockpitEmail"] = email,
            };
            CopyStoredTimes(normalized, metadata);
            var idToken = JsonMerge.GetString(tokens, "id_token");
            if (!string.IsNullOrWhiteSpace(idToken)) { metadata["id_token"] = idToken; }
            var plan = JsonMerge.GetString(normalized, "plan_type");
            if (!string.IsNullOrWhiteSpace(plan)) { metadata["planType"] = plan; }

            // Preserve a real subscription expiry when the source data provides one (Cockpit field),
            // otherwise fall back to the id_token/access_token claim chatgpt_subscription_active_until.
            var subscription = JsonMerge.Get(normalized, "subscription_active_until")
                ?? JsonMerge.Get(normalized, "subscriptionActiveUntil")
                ?? ReadSubscriptionFromTokens(normalized);
            if (subscription is not null) { metadata["subscriptionActiveUntil"] = subscription.DeepClone(); }

            return new ImportRow
            {
                Kind = AccountKind.Codex,
                Label = email,
                Provider = "openai",
                Identity = accountId + "|" + email.ToLowerInvariant(),
                Plan = JsonMerge.GetString(normalized, "plan_type") ?? string.Empty,
                Workspace = workspace,
                Expires = DateTimeOffset.FromUnixTimeSeconds(exp.Value).LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                Status = expired ? "访问令牌已过期（刷新是否有效未验证）" : "可导入（未联网验证）",
                Value = new JsonObject
                {
                    ["type"] = "oauth",
                    ["methodID"] = oauthMethod,
                    ["access"] = access,
                    ["refresh"] = refresh,
                    ["expires"] = exp.Value * 1000,
                    ["metadata"] = metadata,
                },
                Definition = null,
                Raw = (JsonObject)normalized.DeepClone(),
            };
        }

        if (!string.Equals(authMode, "apikey", StringComparison.OrdinalIgnoreCase)) { throw new DshException("不支持的登录方式。"); }

        // Cockpit writes lowercase openai_api_key. Legacy environment/generic aliases were
        // collapsed (and conflicts rejected) before this raw account can enter the mirror.
        var apiKey = JsonMerge.GetString(normalized, "openai_api_key");
        if (string.IsNullOrEmpty(apiKey)) { throw new DshException("API 账号缺少 openai_api_key。"); }

        var baseUrl = JsonMerge.GetString(normalized, "api_base_url");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new DshException("API 地址必须是无内嵌凭据、查询参数和片段的 HTTPS 地址。");
        }

        var sourceId = JsonMerge.GetString(normalized, "api_provider_id");
        if (string.IsNullOrEmpty(sourceId)) { sourceId = JsonMerge.GetString(normalized, "id"); }
        if (string.IsNullOrEmpty(sourceId)) { throw new DshException("API 账号缺少稳定的供应商 ID。"); }

        var provider = "cockpit-" + Sha256Hex(sourceId)[..12];
        var name = JsonMerge.GetString(normalized, "api_provider_name");
        if (string.IsNullOrEmpty(name)) { name = sourceId; }

        var catalogItems = new List<JsonNode?>();
        var catalogNode = JsonMerge.Get(normalized, "api_model_catalog");
        if (catalogNode is JsonArray catalogArray) { catalogItems.AddRange(catalogArray); }
        else if (catalogNode is not null) { throw new DshException("模型列表格式不受支持；需要字符串数组。"); }

        var modelsSupplied = catalogItems.Count > 0;
        var knownArk = string.Equals(uri.Host, "ark.cn-beijing.volces.com", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(uri.AbsolutePath.TrimEnd('/'), "/api/coding/v3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.AbsolutePath.TrimEnd('/'), "/api/plan/v3", StringComparison.OrdinalIgnoreCase));

        if (!modelsSupplied && knownArk)
        {
            catalogItems = new List<JsonNode?> { JsonValue.Create("ark-code-latest") };
        }

        var models = new JsonObject();
        foreach (var model in catalogItems)
        {
            if (model is not JsonValue value || !value.TryGetValue<string>(out var modelId) || string.IsNullOrWhiteSpace(modelId))
            {
                throw new DshException("模型列表格式不受支持；需要字符串数组。");
            }

            models[modelId] = new JsonObject { ["name"] = modelId };
        }

        if (models.Count == 0)
        {
            throw new DshException("API 账号没有模型列表；只有已识别的火山 Coding/Agent Plan 接口可使用最小默认模型。");
        }

        var wireText = JsonMerge.GetString(normalized, "api_wire_api");
        var protocolSupplied = !string.IsNullOrWhiteSpace(wireText);
        var wire = wireText?.ToLowerInvariant() ?? string.Empty;
        if (!protocolSupplied && knownArk) { wire = "chat_completions"; }

        var package = wire.ToLowerInvariant() switch
        {
            "chat_completions" => OpenAiApiProtocol.ChatPackage,
            "responses" => OpenAiApiProtocol.ResponsesPackage,
            _ => throw new DshException("不支持的 API 协议，不能猜测请求格式。"),
        };

        var apiSettings = new JsonObject { ["baseURL"] = uri.AbsoluteUri };
        if (wire == "responses")
        {
            foreach (var (modelId, model) in models)
            {
                if (model is JsonObject modelEntry) { ModelVariantBuilder.TryAddMissingVariants(modelEntry, modelId, useResponses: true, baseUrl: uri.AbsoluteUri); }
            }
        }
        var apiMetadata = new JsonObject { ["cockpitID"] = sourceId };
        CopyStoredTimes(normalized, apiMetadata);

        return new ImportRow
        {
            Kind = AccountKind.Api,
            Label = name,
            Provider = provider,
            Identity = sourceId,
            Workspace = "不适用",
            Plan = "API Key",
            Expires = "不适用",
            Status = modelsSupplied ? "可导入（未联网验证）" : "缺少模型列表：保留已有模型，否则仅 ark-code-latest",
            ModelsSupplied = modelsSupplied,
            ProtocolSupplied = protocolSupplied,
            Value = new JsonObject
            {
                ["type"] = "key",
                ["key"] = apiKey,
                ["metadata"] = apiMetadata,
            },
            Definition = new JsonObject
            {
                ["name"] = name,
                ["package"] = package,
                ["settings"] = apiSettings,
                ["models"] = models,
            },
            Raw = (JsonObject)normalized.DeepClone(),
        };
    }

    private static void CopyStoredTimes(JsonNode account, JsonObject metadata)
    {
        foreach (var field in new[] { "created_at", "last_used" })
        {
            if (CockpitAccountSchema.TryGetInt64Value(JsonMerge.Get(account, field), out var timestamp)) { metadata[field] = timestamp; }
        }
    }

    private static bool CanRefreshImportedVariants(ImportRow row, JsonObject existing, JsonArray credentials)
    {
        if (!OpenAiApiProtocol.UsesResponses(JsonMerge.GetString(row.Definition, "package"))) { return false; }
        var incomingUrl = JsonMerge.GetString(JsonMerge.Get(row.Definition, "settings"), "baseURL");
        var existingUrl = JsonMerge.GetString(JsonMerge.Get(existing, "settings"), "baseURL");
        if (!ModelVariantBuilder.IsArkPlanEndpoint(incomingUrl)
            || !ModelVariantBuilder.IsArkPlanEndpoint(existingUrl)) { return false; }
        // Host/scheme/port are already constrained. A trailing slash is not a different plan.
        if (!string.Equals(new Uri(incomingUrl!).AbsolutePath.TrimEnd('/'),
            new Uri(existingUrl!).AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) { return false; }

        var incomingKey = JsonMerge.GetString(row.Value, "key");
        if (string.IsNullOrWhiteSpace(incomingKey)) { return false; }
        var matches = FindMatches(row, credentials);
        if (matches.Count != 1) { return false; }
        var value = JsonMerge.Get(matches[0], "value");
        return string.Equals(JsonMerge.GetString(value, "type"), "key", StringComparison.OrdinalIgnoreCase)
            && string.Equals(incomingKey, JsonMerge.GetString(value, "key"), StringComparison.Ordinal);
    }

    private static JsonObject MergeImportedModels(JsonNode? existing, JsonNode? incoming, bool useResponses, string? baseUrl)
    {
        var result = new JsonObject();
        if (incoming is not JsonObject models) { return result; }
        foreach (var (id, value) in models)
        {
            var oldEntry = JsonMerge.Get(existing, id);
            // ConvertAccount synthesises variants for its preview. Remove those before merging:
            // existing models retain their exact variants; new ones use the final URL/protocol.
            var incomingEntry = value?.DeepClone();
            if (incomingEntry is JsonObject incomingObject) { incomingObject.Remove("variants"); }
            var merged = JsonMerge.Merge(oldEntry, incomingEntry);
            // Backfill tiers only for documented models that have none; a user's custom
            // variants are never replaced here (the same-URL+Key path does the refresh).
            if (merged is JsonObject mergedEntry)
            {
                ModelVariantBuilder.TryAddMissingVariants(mergedEntry, id, useResponses, baseUrl);
            }
            result[id] = merged;
        }
        return result;
    }

    /// <summary>
    /// Cockpit reads the real subscription expiry from the OAuth id_token JWT claim
    /// <c>https://api.openai.com/auth.chatgpt_subscription_active_until</c> (falling back to the
    /// access token). This recovers it for exports that only carry the raw tokens.
    /// </summary>
    private static JsonNode? ReadSubscriptionFromTokens(JsonNode? normalized)
    {
        var tokens = JsonMerge.Get(normalized, "tokens");
        foreach (var name in new[] { "id_token", "access_token" })
        {
            var payload = Jwt.ReadPayload(JsonMerge.GetString(tokens, name));
            var node = JsonMerge.Get(
                JsonMerge.Get(payload, "https://api.openai.com/auth"),
                "chatgpt_subscription_active_until");
            if (node is not null) { return node.DeepClone(); }
        }

        return null;
    }

    /// <summary>
    /// Builds the local mirror records for all source rows: every Cockpit field is preserved,
    /// token/key fields are sealed. Keyed like the mapper (accountID|email for Codex, source id
    /// for API providers).
    /// </summary>
    private static List<SubscriptionRecord> BuildCacheRecords(IEnumerable<ImportRow> rows)
    {
        var records = new List<SubscriptionRecord>();
        foreach (var row in rows)
        {
            if (row.Raw is null) { continue; }
            var metadata = JsonMerge.Get(row.Value, "metadata");
            var accountId = row.Kind == AccountKind.Codex
                ? (JsonMerge.GetString(metadata, "accountID") ?? JsonMerge.GetString(row.Raw, "account_id") ?? string.Empty)
                : row.Identity;
            if (string.IsNullOrWhiteSpace(accountId)) { continue; }

            var email = JsonMerge.GetString(metadata, "cockpitEmail")
                ?? JsonMerge.GetString(row.Raw, "email")
                ?? string.Empty;

            var activeUntil = TextOf(JsonMerge.Get(metadata, "subscriptionActiveUntil"))
                ?? TextOf(JsonMerge.Get(row.Raw, "subscription_active_until"))
                ?? TextOf(ReadSubscriptionFromTokens(row.Raw));

            var (cockpit, secrets) = SubscriptionStore.SplitSecrets(row.Raw);
            records.Add(new SubscriptionRecord
            {
                AccountId = accountId,
                Email = email,
                ActiveUntil = activeUntil,
                PlanType = row.Plan,
                Source = "import",
                Cockpit = cockpit,
                SealedSecrets = SubscriptionStore.SealSecrets(secrets),
            });
        }

        return records;
    }

    /// <summary>Reads a JSON string value (or its JSON text for non-strings); null when absent.</summary>
    private static string? TextOf(JsonNode? node)
    {
        if (node is null) { return null; }
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) { return text; }
        return node.ToJsonString();
    }

    /// <summary>Finds the credentials whose exact identity equals the row's.</summary>
    private static List<JsonNode> FindMatches(ImportRow row, JsonArray credentials)
    {
        var matches = new List<JsonNode>();
        foreach (var credential in credentials)
        {
            if (credential is null) { continue; }
            if (!string.Equals(JsonMerge.GetString(credential, "integrationID"), row.Provider, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (row.Kind == AccountKind.Codex)
            {
                var info = CredentialHelpers.GetIdentityInfo(credential);
                if (string.IsNullOrWhiteSpace(info.Identity)) { continue; }
                if (string.Equals(info.Identity, row.Identity, StringComparison.Ordinal)) { matches.Add(credential); }
            }
            else
            {
                var cockpitId = JsonMerge.GetString(JsonMerge.Get(JsonMerge.Get(credential, "value"), "metadata"), "cockpitID");
                if (string.Equals(cockpitId, row.Identity, StringComparison.Ordinal)) { matches.Add(credential); }
            }
        }

        return matches;
    }

    /// <summary>Compares two credential value objects.</summary>
    private static bool SameCredential(JsonNode? left, JsonNode? right)
    {
        var leftType = JsonMerge.GetString(left, "type");
        if (!string.Equals(leftType, JsonMerge.GetString(right, "type"), StringComparison.OrdinalIgnoreCase)) { return false; }

        if (string.Equals(leftType, "key", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(JsonMerge.GetString(left, "key"), JsonMerge.GetString(right, "key"), StringComparison.Ordinal);
        }

        return string.Equals(JsonMerge.GetString(left, "access"), JsonMerge.GetString(right, "access"), StringComparison.Ordinal)
            && string.Equals(JsonMerge.GetString(left, "refresh"), JsonMerge.GetString(right, "refresh"), StringComparison.Ordinal)
            && string.Equals(JsonMerge.GetString(left, "methodID"), JsonMerge.GetString(right, "methodID"), StringComparison.OrdinalIgnoreCase)
            && (JsonNum.ToLong(JsonMerge.Get(left, "expires")) ?? 0) == (JsonNum.ToLong(JsonMerge.Get(right, "expires")) ?? 0)
            && string.Equals(
                JsonMerge.GetString(JsonMerge.Get(left, "metadata"), "accountID"),
                JsonMerge.GetString(JsonMerge.Get(right, "metadata"), "accountID"),
                StringComparison.Ordinal)
            && string.Equals(SubscriptionToken(left), SubscriptionToken(right), StringComparison.Ordinal);
    }

    /// <summary>Subscription expiry as stored in credential metadata, for change detection.</summary>
    private static string SubscriptionToken(JsonNode? value)
    {
        var metadata = JsonMerge.Get(value, "metadata");
        var node = JsonMerge.Get(metadata, "subscriptionActiveUntil") ?? JsonMerge.Get(metadata, "subscription_active_until");
        return node?.ToJsonString() ?? string.Empty;
    }

    private static string Sha256Hex(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private void Report(List<string> messages, string message, LogLevel level)
    {
        var safe = _secrets.Redact(message);
        messages.Add(safe);
        switch (level)
        {
            case LogLevel.Warn:
                _log.Warn(safe);
                break;
            case LogLevel.Error:
                _log.Error(safe);
                break;
            default:
                _log.Info(safe);
                break;
        }
    }

    /// <summary>Internal preview/import row. Contains secrets; never shown or logged.</summary>
    internal sealed class ImportRow
    {
        public AccountKind Kind;
        public string Key = string.Empty;
        public string Label = string.Empty;
        public string Provider = string.Empty;
        public string Identity = string.Empty;
        public string Workspace = string.Empty;
        public string Plan = string.Empty;
        public string Expires = string.Empty;
        public string Status = string.Empty;
        public bool ModelsSupplied;
        public bool ProtocolSupplied;
        public JsonObject Value = new();
        public JsonObject? Definition;

        /// <summary>Full source entry (for the local account cache); contains secrets, never shown.</summary>
        public JsonObject? Raw;
    }
}
