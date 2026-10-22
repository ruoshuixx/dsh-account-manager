using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AccountManager.Core.Accounts;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.Security;

namespace AccountManager.Core.Dsh;

/// <summary>
/// The dsh implementation of <see cref="IAccountBackend"/>. State lives in three places:
/// <list type="bullet">
/// <item>the encrypted <see cref="AccountVault"/> (every Codex account this program manages),</item>
/// <item><c>$DSH_HOME/.credentials.yaml</c> (dsh's single current Codex grant <c>llm-pi-ai/openai-codex</c> and the
/// key references of API providers),</item>
/// <item><c>$DSH_HOME/profiles/&lt;profile&gt;/cordis.patch.yml</c> (the <c>llm-pi-ai</c> provider definitions).</item>
/// </list>
/// All writes go through the protocol-compatible lock and atomic replace of the storage layer, re-read the file
/// inside the lock and change only their own keys, so dsh and other tools keep working on the same files.
/// </summary>
public sealed class DshBackend : IAccountBackend
{
    /// <summary>Record key of dsh's current Codex account.</summary>
    public const string CodexGrantKey = "llm-pi-ai/openai-codex";

    public const string ApiCredentialPrefix = "key_";

    private static readonly Regex ProviderIdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    private readonly AccountVault _vault;
    private readonly SecretRegistry _secrets;
    private readonly SubscriptionStore? _mirror;
    private readonly AppLog? _log;
    private readonly IProviderRuntimeVerifier _verifier;
    private readonly object _adoptGate = new();
    private volatile DshHome _home;

    public DshBackend(DshHome home, AccountVault vault, SecretRegistry secrets, SubscriptionStore? mirror = null,
        AppLog? log = null, IProviderRuntimeVerifier? verifier = null)
    {
        _home = home ?? throw new ArgumentNullException(nameof(home));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _mirror = mirror;
        _log = log;
        _verifier = verifier ?? new DshRuntimeVerifier();
    }

    public DshHome Home => _home;

    /// <summary>
    /// When set, enabling the built-in Codex route also merges the Codex (and API) routes into dsh's sub-agent model
    /// selection, so a main agent can delegate to them.
    /// </summary>
    public SubagentRouteService? Subagents { get; set; }

    public AccountVault Vault => _vault;

    /// <summary>Points the backend at another DSH home / profile (Settings).</summary>
    public void SetHome(DshHome home) => _home = home ?? throw new ArgumentNullException(nameof(home));

    public static string ApiCredentialId(string providerId) => ApiCredentialPrefix + providerId;

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    public Task<JsonArray> GetCredentialsAsync(CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            var home = Home;
            var doc = LoadCredentials(home);
            var records = _vault.Load().ToList();
            var grant = doc.GetGrantPayload(CodexGrantKey) as JsonObject;
            var activeIndex = VaultCredentialMapper.FindActive(records, grant);
            if (activeIndex < 0 && grant is not null)
            {
                // dsh's current Codex login belongs to no stored account (it was signed in with dsh itself):
                // keep it in the vault so it is listed, can be switched back to and is never overwritten unseen.
                lock (_adoptGate)
                {
                    records = _vault.Load().ToList();
                    activeIndex = VaultCredentialMapper.FindActive(records, grant);
                    if (activeIndex < 0 && AdoptForeignGrant(grant) is { } adopted)
                    {
                        _log?.Info("dsh 当前的 Codex 登录不在账号库中，已保存到账号库：" + VaultCredentialMapper.IdOf(adopted));
                        records.Add(adopted);
                        activeIndex = records.Count - 1;
                    }
                }
            }

            var array = new JsonArray();
            for (var i = 0; i < records.Count; i++)
            {
                array.Add(VaultCredentialMapper.ToCredential(records[i], i == activeIndex, i == activeIndex ? grant : null));
            }

            try
            {
                var sourceIds = await LoadCockpitSourceIdsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var credential in ApiCredentials(home, doc, sourceIds)) { array.Add(credential); }
            }
            catch (DshException ex)
            {
                // A broken provider file must not hide the Codex accounts; GetConfigEntriesAsync reports it.
                _log?.Warn("读取供应商配置失败，API 账号暂不显示：" + _secrets.Redact(ex.Message));
            }

            _secrets.Register(array);
            return array;
        }, cancellationToken);

    public Task<JsonArray> GetConfigEntriesAsync(CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            var home = Home;
            var patch = LoadPatch(home);
            var views = new JsonObject();
            if (patch.GetProviders() is { } providers)
            {
                foreach (var (id, node) in providers)
                {
                    if (node is JsonObject provider && ProviderViewMapper.ToView(id, provider) is { } view) { views[id] = view; }
                }
            }

            var entries = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "file",
                    ["path"] = home.PatchPath,
                    ["info"] = new JsonObject { ["providers"] = views },
                },
            };
            _secrets.Register(entries);
            return Task.FromResult(entries);
        }, cancellationToken);

    public Task<ConfigSnapshot> GetConfigSnapshotAsync(CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            var path = Home.PatchPath;
            string? raw = null;
            try { raw = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            return Task.FromResult(new ConfigSnapshot(path, raw));
        }, cancellationToken);

    private IEnumerable<JsonObject> ApiCredentials(DshHome home, CredentialsDocument doc, IReadOnlyDictionary<string, string> sourceIds)
    {
        var patch = LoadPatch(home);
        if (patch.GetProviders() is not { } providers) { yield break; }
        foreach (var (id, node) in providers)
        {
            if (node is not JsonObject provider || !ProviderViewMapper.IsManaged(id, provider)) { continue; }
            var env = ProviderViewMapper.ReadString(provider, "apiKeyEnv");
            var key = env is null ? null : doc.GetRef(env);
            if (string.IsNullOrEmpty(key)) { continue; }

            var metadata = new JsonObject();
            if (sourceIds.TryGetValue(id, out var sourceId)) { metadata["cockpitID"] = sourceId; }
            yield return new JsonObject
            {
                ["id"] = ApiCredentialId(id),
                ["integrationID"] = id,
                ["label"] = ProviderViewMapper.ReadString(provider, "displayName") ?? id,
                ["active"] = true,
                ["value"] = new JsonObject { ["type"] = "key", ["key"] = key, ["metadata"] = metadata },
            };
        }
    }

    /// <summary>Provider id (<c>cockpit-&lt;sha256 prefix&gt;</c>) to the Cockpit source id, from the local mirror.</summary>
    private async Task<IReadOnlyDictionary<string, string>> LoadCockpitSourceIdsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_mirror is null) { return map; }
        try
        {
            foreach (var record in (await _mirror.LoadAsync(ct).ConfigureAwait(false)).Values)
            {
                if (!string.Equals(JsonMerge.GetString(record.Cockpit, "auth_mode"), "apikey", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (string.IsNullOrWhiteSpace(record.AccountId)) { continue; }
                map[CockpitProviderId(record.AccountId)] = record.AccountId;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log?.Warn("读取账户缓存失败，已忽略：" + ex.Message); }
        return map;
    }

    /// <summary>Provider ids of imported Cockpit API accounts, from the local mirror (empty when there is none).</summary>
    public async Task<IReadOnlyCollection<string>> GetMirrorProviderIdsAsync(CancellationToken ct = default)
        => (await LoadCockpitSourceIdsAsync(ct).ConfigureAwait(false)).Keys.ToList();

    /// <summary>The provider id an imported Cockpit API account gets.</summary>
    public static string CockpitProviderId(string sourceId)
        => "cockpit-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceId))).ToLowerInvariant()[..12];

    // ------------------------------------------------------------------
    // Codex accounts
    // ------------------------------------------------------------------

    public Task<string> CreateCredentialAsync(string integrationId, string label, JsonObject value, bool activate,
        CancellationToken cancellationToken = default, string? requestedId = null)
        => RunAsync(() =>
        {
            if (!string.Equals(integrationId, VaultCredentialMapper.IntegrationId, StringComparison.Ordinal)
                || !string.Equals(JsonMerge.GetString(value, "type"), "oauth", StringComparison.OrdinalIgnoreCase))
            {
                throw new DshException("只能创建 ChatGPT Codex OAuth 账号；API 供应商请通过供应商配置保存。");
            }

            _secrets.Register(value);
            var id = string.IsNullOrWhiteSpace(requestedId) ? VaultCredentialMapper.NewCodexId() : requestedId.Trim();
            if (_vault.FindById(id) is not null) { throw new DshException("账号 ID 已存在，未覆盖。"); }
            var record = VaultCredentialMapper.ToRecord(id, label, value, null);
            _vault.Upsert(record);

            if (activate || NoCurrentGrant())
            {
                try { ActivateCore(id, allowFirstAccount: true); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The account is stored; only making it current failed.
                    _log?.Warn("账号已保存，但未能设为 dsh 当前账号：" + _secrets.Redact(ex.Message));
                    if (activate) { throw; }
                }
            }

            return Task.FromResult(id);
        }, cancellationToken);

    public Task DeleteCredentialAsync(string credentialId, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(credentialId)) { throw new DshException("缺少要删除的账号 ID。"); }
            if (credentialId.StartsWith(ApiCredentialPrefix, StringComparison.Ordinal))
            {
                throw new DshException("API 供应商的 Key 请随供应商一起删除。");
            }

            var home = Home;
            CredentialsDocument.Update(home.CredentialsPath, doc =>
            {
                var records = _vault.Load().ToList();
                var index = records.FindIndex(r => VaultCredentialMapper.IdOf(r) == credentialId);
                if (index < 0) { return false; }
                var grant = doc.GetGrantPayload(CodexGrantKey) as JsonObject;
                var wasActive = VaultCredentialMapper.FindActive(records, grant) == index;
                _vault.Remove(credentialId);
                var removedGrant = wasActive && doc.RemoveRecord(CodexGrantKey);
                _log?.Info("已从账号库删除账号 " + _secrets.Redact(VaultCredentialMapper.EmailOf(records[index])) + (wasActive ? "，并移除了 dsh 的当前 Codex 凭据" : string.Empty));
                return removedGrant;
            });
            return Task.FromResult(0);
        }, cancellationToken);

    public Task ActivateCredentialAsync(string credentialId, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            try { ActivateCore(credentialId, allowFirstAccount: false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Error("切换账号失败：" + _secrets.Redact(ex.Message));
                throw;
            }

            return Task.FromResult(0);
        }, cancellationToken);

    private bool NoCurrentGrant()
    {
        try { return LoadCredentials(Home).GetRecord(CodexGrantKey) is null; }
        catch (DshStorageException) { return false; }
    }

    /// <summary>
    /// Makes a vault account dsh's current Codex account inside one credentials-document update: first any tokens
    /// dsh refreshed since are written back into the vault (only when newer), a foreign current grant is
    /// adopted into the vault so it is never lost, then the target's grant is written and read back.
    /// </summary>
    private void ActivateCore(string id, bool allowFirstAccount)
    {
        if (string.IsNullOrWhiteSpace(id)) { throw new DshException("请选择要激活的账号。"); }
        var home = Home;
        JsonObject? expected = null;

        CredentialsDocument.Update(home.CredentialsPath, doc =>
        {
            var grant = doc.GetGrantPayload(CodexGrantKey) as JsonObject;
            var records = _vault.Load().ToList();
            var targetIndex = records.FindIndex(r => VaultCredentialMapper.IdOf(r) == id);
            if (targetIndex < 0) { throw new DshException("未找到该账号；它可能已被移除，请刷新后重试。"); }

            if (grant is not null)
            {
                var currentIndex = VaultCredentialMapper.FindActive(records, grant);
                if (currentIndex >= 0)
                {
                    if (VaultCredentialMapper.GrantIsNewer(grant, records[currentIndex]))
                    {
                        var refreshed = (JsonObject)records[currentIndex].DeepClone();
                        if (VaultCredentialMapper.ApplyGrantTokens(refreshed, grant))
                        {
                            _vault.Upsert(refreshed);
                            records[currentIndex] = refreshed;
                            _log?.Info("已把 dsh 刷新后的令牌写回账号库：" + VaultCredentialMapper.IdOf(refreshed));
                        }
                    }
                }
                else if (AdoptForeignGrant(grant) is { } adopted)
                {
                    _log?.Info("dsh 当前的 Codex 登录不在账号库中，已先保存到账号库：" + VaultCredentialMapper.IdOf(adopted));
                }
            }

            var target = records[targetIndex];
            var payload = VaultCredentialMapper.BuildGrantPayload(target);
            if (string.IsNullOrEmpty(JsonMerge.GetString(payload, "access")) || string.IsNullOrEmpty(JsonMerge.GetString(payload, "refresh")))
            {
                throw new DshException("该账号缺少访问令牌或刷新令牌，无法激活。");
            }

            expected = payload;
            _secrets.Register(target);
            var changedGrant = doc.SetGrant(CodexGrantKey, payload);
            var accountId = VaultCredentialMapper.AccountIdOf(target);
            var shortWorkspace = accountId.Length > 8 ? accountId[^8..] : accountId;
            var who = VaultCredentialMapper.EmailOf(target);
            if (who.Length == 0) { who = JsonMerge.GetString(target, "label") ?? id; }
            _log?.Info(changedGrant
                ? "切换到 " + who + "（" + shortWorkspace + "）：已写入 dsh 当前 Codex 凭据"
                : "切换到 " + who + "（" + shortWorkspace + "）：dsh 当前凭据已是该账号，无需改动");
            return changedGrant;
        });

        // Read back from disk, as dsh will see it.
        var verify = LoadCredentials(home).GetGrantPayload(CodexGrantKey) as JsonObject;
        if (expected is not null && (verify is null
            || JsonMerge.GetString(verify, "refresh") != JsonMerge.GetString(expected, "refresh")
            || JsonMerge.GetString(verify, "accountId") != JsonMerge.GetString(expected, "accountId")))
        {
            throw new DshException("激活未成功：回读校验失败。凭据文件可能已被其他程序修改，请在 dsh 中确认后重试。");
        }

        EnsureCodexRoute(home);
    }

    /// <summary>
    /// dsh only serves the routes the user layer lists under <c>llm-pi-ai</c> <c>config.providers</c>; a fresh
    /// profile lists none, so a written Codex grant alone shows no Codex models. A missing <c>openai-codex</c> entry
    /// is created with an explicit <c>models</c> list of the official current ids
    /// (<see cref="SubagentRouteService.OfficialCodexModels"/>, ids only; dsh fills in the rest from its catalog), so
    /// the model picker shows exactly these and the retired ids of the shipped catalog never appear. An existing
    /// entry is left alone, except one that is exactly empty (what 0.1.2 wrote): it carries no user choices and is
    /// upgraded to the same list by <see cref="SubagentRouteService.UpgradeEmptyCodexRoute"/> (after a backup).
    /// </summary>
    private void EnsureCodexRoute(DshHome home)
    {
        try
        {
            var added = ProfilePatchDocument.Update(home.PatchPath, d =>
                d.GetProviders()?.ContainsKey(ProviderViewMapper.CodexRouteId) != true
                && d.SetProvider(ProviderViewMapper.CodexRouteId, SubagentRouteService.OfficialCodexRoute()));
            if (added)
            {
                _log?.Info("已在 dsh 供应商配置中启用内置 Codex 路由（openai-codex）并写入官方现行 Codex 模型，dsh 会自动热加载。");
                MergeSubagentRoutes();
            }
            else if (Subagents?.UpgradeEmptyCodexRoute() == true)
            {
                MergeSubagentRoutes();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The grant is written; only the route registration failed.
            _log?.Warn("Codex 凭据已写入，但未能在供应商配置中启用 openai-codex 路由：" + _secrets.Redact(ex.Message));
        }
    }

    private void MergeSubagentRoutes()
    {
        if (Subagents is null) { return; }
        try { _ = Subagents.Merge(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn("未能把 Codex 路由加入子代理可用模型：" + _secrets.Redact(ex.Message));
        }
    }

    /// <summary>Stores a grant no vault account owns as a new vault account; null when it cannot be stored.</summary>
    private JsonObject? AdoptForeignGrant(JsonObject grant)
    {
        var access = JsonMerge.GetString(grant, "access") ?? string.Empty;
        var refresh = JsonMerge.GetString(grant, "refresh") ?? string.Empty;
        var accountId = JsonMerge.GetString(grant, "accountId") ?? string.Empty;
        if (access.Length == 0 || refresh.Length == 0 || accountId.Length == 0) { return null; }

        var email = VaultCredentialMapper.GrantEmail(grant);
        var claims = Jwt.ReadPayload(access);
        var plan = JsonMerge.GetString(JsonMerge.Get(claims, "https://api.openai.com/auth"), "chatgpt_plan_type") ?? string.Empty;
        var workspace = accountId.Length > 8 ? accountId[^8..] : accountId;
        var metadata = new JsonObject
        {
            ["accountID"] = accountId,
            ["cockpitEmail"] = email,
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["last_used"] = 0L,
        };
        if (plan.Length > 0) { metadata["planType"] = plan; }
        var value = new JsonObject
        {
            ["type"] = "oauth",
            ["methodID"] = VaultCredentialMapper.DefaultMethodId,
            ["access"] = access,
            ["refresh"] = refresh,
            ["expires"] = VaultCredentialMapper.GrantExpires(grant),
            ["metadata"] = metadata,
        };
        var label = email.Length > 0 ? email + " [" + workspace + "]" : "Codex " + accountId;
        var record = VaultCredentialMapper.ToRecord(VaultCredentialMapper.NewCodexId(), label, value, null);
        _secrets.Register(value);
        _vault.Upsert(record);
        return record;
    }

    // ------------------------------------------------------------------
    // API providers
    // ------------------------------------------------------------------

    public Task WriteApiProviderAsync(string providerId, JsonObject view, string? newKey, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            ValidateProviderId(providerId);
            ArgumentNullException.ThrowIfNull(view);
            if (!string.IsNullOrEmpty(newKey)) { _secrets.RegisterValue(newKey); }
            var home = Home;
            var patchPath = home.PatchPath;

            var existing = LoadPatch(home);
            var providers = existing.GetProviders() ?? new JsonObject();
            var current = providers[providerId] as JsonObject;
            var env = ProviderViewMapper.ReadString(current, "apiKeyEnv") ?? ProviderViewMapper.ReadString(view[ProviderViewMapper.ExtraKey], "apiKeyEnv");

            string? previousValue = null;
            var refTouched = false;
            if (!string.IsNullOrEmpty(newKey))
            {
                CredentialsDocument.Update(home.CredentialsPath, doc =>
                {
                    if (env is null || !CredentialsDocument.IsValidRefName(env) || UsedByOtherProvider(env, providerId, providers))
                    {
                        env = NewRefName(providerId, doc, providers);
                    }

                    previousValue = doc.GetRef(env);
                    refTouched = true;
                    return doc.SetRef(env, newKey);
                });
            }

            var provider = ProviderViewMapper.FromView(view, env);
            try
            {
                ProfilePatchDocument.Update(patchPath, d => d.SetProvider(providerId, provider));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (refTouched && env is not null)
                {
                    try
                    {
                        CredentialsDocument.Update(home.CredentialsPath, doc => previousValue is null ? doc.RemoveRef(env) : doc.SetRef(env, previousValue));
                    }
                    catch (Exception rollback) { _log?.Warn("回滚 Key 引用失败：" + _secrets.Redact(rollback.Message)); }
                }

                throw;
            }

            // Read back from disk.
            var written = LoadPatch(home).GetProviders()?[providerId] as JsonObject;
            if (written is null
                || ProviderViewMapper.ReadString(written, "baseURL") != ProviderViewMapper.ReadString(provider, "baseURL")
                || ProviderViewMapper.ReadString(written, "api") != ProviderViewMapper.ReadString(provider, "api"))
            {
                throw new DshException("配置回读失败：供应商定义未按预期保存。");
            }

            return Task.FromResult(0);
        }, cancellationToken);

    public Task RemoveApiProviderAsync(string providerId, CancellationToken cancellationToken = default)
        => RunAsync(() =>
        {
            ValidateProviderId(providerId);
            var home = Home;
            string? env = null;
            JsonObject? remaining = null;
            ProfilePatchDocument.Update(home.PatchPath, d =>
            {
                var providers = d.GetProviders();
                if (providers?[providerId] is not JsonObject provider) { return false; }
                if (!ProviderViewMapper.IsManaged(providerId, provider)) { throw new DshException("该供应商不是本工具管理的 OpenAI 兼容供应商，已停止。"); }
                env = ProviderViewMapper.ReadString(provider, "apiKeyEnv");
                var changed = d.SetProvider(providerId, null);
                remaining = d.GetProviders();
                return changed;
            });

            if (env is not null && !UsedByOtherProvider(env, providerId, remaining))
            {
                CredentialsDocument.Update(home.CredentialsPath, doc => doc.RemoveRef(env));
            }

            if (LoadPatch(home).GetProviders()?[providerId] is not null)
            {
                throw new DshException("删除后磁盘配置中仍存在该供应商定义，未完成；请手动检查。");
            }

            return Task.FromResult(0);
        }, cancellationToken);

    public async Task<ProviderRuntimeVerification> VerifyProviderAsync(string providerId, string expectedApi, CancellationToken cancellationToken = default)
        => await _verifier.VerifyAsync(Home, providerId, expectedApi, cancellationToken).ConfigureAwait(false);

    private static void ValidateProviderId(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || !ProviderIdPattern.IsMatch(providerId) || providerId.Length > 100)
        {
            throw new DshException("供应商 ID 无效。");
        }

        if (string.Equals(providerId, ProviderViewMapper.CodexRouteId, StringComparison.Ordinal))
        {
            throw new DshException("openai-codex 是 dsh 内置的 Codex 路由，本工具不会修改它。");
        }
    }

    private static bool UsedByOtherProvider(string env, string providerId, JsonObject? providers)
    {
        if (providers is null) { return false; }
        foreach (var (id, node) in providers)
        {
            if (id == providerId || node is not JsonObject p) { continue; }
            if (ProviderViewMapper.ReadString(p, "apiKeyEnv") == env) { return true; }
        }

        return false;
    }

    /// <summary>
    /// A fresh key reference name for a provider: <c>&lt;PROVIDER_ID&gt;_API_KEY</c> with every character outside
    /// <c>[A-Z0-9_]</c> replaced by an underscore, de-duplicated against existing refs and other providers.
    /// </summary>
    public static string NewRefName(string providerId, CredentialsDocument doc, JsonObject? providers)
    {
        var sb = new StringBuilder();
        foreach (var c in providerId.ToUpperInvariant()) { sb.Append(c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? c : '_'); }
        if (sb.Length == 0 || char.IsDigit(sb[0])) { sb.Insert(0, "P_"); }
        var baseName = sb + "_API_KEY";
        var name = baseName;
        for (var n = 2; doc.GetRef(name) is not null || UsedByAny(name, providers); n++) { name = baseName + "_" + n; }
        return name;
    }

    private static bool UsedByAny(string env, JsonObject? providers)
        => providers is not null && providers.Any(kv => kv.Value is JsonObject p && ProviderViewMapper.ReadString(p, "apiKeyEnv") == env);

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static CredentialsDocument LoadCredentials(DshHome home)
    {
        try { return CredentialsDocument.Load(home.CredentialsPath); }
        catch (DshFormatException ex) { throw new DshException("无法读取 dsh 凭据文件（" + ex.Message + "）；为避免覆盖，未做任何修改。", ex); }
        catch (IOException ex) { throw new DshException("无法读取 dsh 凭据文件：" + ex.Message, ex); }
        catch (UnauthorizedAccessException ex) { throw new DshException("没有权限读取 dsh 凭据文件：" + ex.Message, ex); }
    }

    private static ProfilePatchDocument LoadPatch(DshHome home)
    {
        try { return ProfilePatchDocument.Load(home.PatchPath); }
        catch (DshFormatException ex) { throw new DshException("无法读取 dsh 配置文件（" + ex.Message + "）；为避免覆盖，未做任何修改。", ex); }
        catch (IOException ex) { throw new DshException("无法读取 dsh 配置文件：" + ex.Message, ex); }
        catch (UnauthorizedAccessException ex) { throw new DshException("没有权限读取 dsh 配置文件：" + ex.Message, ex); }
    }

    private static Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
        => Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await work().ConfigureAwait(false);
            }
            catch (DshLockTimeoutException ex)
            {
                throw new DshException("dsh 的文件正被其他程序占用（等待写锁超时）；未做任何修改，请稍后重试。", ex);
            }
            catch (DshFormatException ex)
            {
                throw new DshException("dsh 的文件格式不符合预期（" + ex.Message + "）；为避免损坏，未做任何修改。", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DshException("读写 dsh 文件失败：" + ex.Message, ex);
            }
        }, ct);
}
