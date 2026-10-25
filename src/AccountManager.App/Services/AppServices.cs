using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Dsh;
using AccountManager.Core.Actions;
using AccountManager.Core.Export;
using AccountManager.Core.Import;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.Models;
using AccountManager.Core.Quota;
using AccountManager.Core.Security;
using AccountManager.Core.Sync;

namespace AccountManager.App.Services;

/// <summary>
/// Composition root: builds every core service once and exposes the small set of
/// operations the UI needs.
/// </summary>
public sealed class AppServices : IAddAccountServices
{
    /// <param name="settings">User preferences (DSH home / profile overrides, intervals, WebDAV).</param>
    /// <param name="appDataDirectory">Test seam: where the account vault and the local mirror live.</param>
    /// <param name="log">Test seam: an already-built log.</param>
    /// <param name="backupDirectory">Test seam: where the encrypted backups go.</param>
    public AppServices(AppSettings settings, string? appDataDirectory = null, AppLog? log = null, string? backupDirectory = null)
    {
        Settings = settings;
        Secrets = new SecretRegistry();
        LogDirectory = PortablePaths.Logs;
        Log = log ?? new AppLog(Secrets, LogDirectory);

        Home = ResolveHome(settings);
        Vault = new AccountVault(appDataDirectory ?? AccountVault.DefaultDirectory());
        Backups = backupDirectory is null ? new BackupService() : new BackupService(backupDirectory);
        Subscriptions = new SubscriptionStore(Vault.Directory, Log);
        Verifier = new DshRuntimeVerifier();
        Backend = new DshBackend(Home, Vault, Secrets, Subscriptions, Log, Verifier);
        References = new ReferenceService(Backend, Verifier, Backups, Secrets, Log);
        SubagentRoutes = new SubagentRouteService(() => Backend.Home, Backups, Secrets, Log);
        Backend.Subagents = SubagentRoutes;
        CodexFast = new CodexFastService(() => Backend.Home, Backups, Secrets, Log, SubagentRoutes);
        Backend.Fast = CodexFast;
        Mapper = new AccountMapper(Secrets);
        Snapshot = new AccountSnapshotService(Backend, Mapper, Secrets, Subscriptions);
        Switch = new SwitchService(Backend, Mapper);
        Quota = new QuotaService();
        Subscription = new SubscriptionService();
        SubscriptionRefresher = new SubscriptionRefresher(Subscription, Log);
        QuotaSelector = new AccountQuotaSelector(Backend, Quota, Secrets, Log, SubscriptionRefresher);
        ResetCredits = new ResetCreditService();
        Reset = new AccountResetService(Backend, ResetCredits, QuotaSelector, Secrets, Log);

        Actions = new AccountActions(Backend, Backups, Secrets, Log, Mapper, References);
        Export = new ExportService(Backend, Secrets, Log, Subscriptions);
        Import = new ImportService(Backend, Backups, Secrets, Log, Subscriptions, References, SubagentRoutes, CodexFast);
    }

    public AppSettings Settings { get; }

    public string LogDirectory { get; }

    public SecretRegistry Secrets { get; }

    public AppLog Log { get; }

    /// <summary>The resolved dsh home and profile currently in use.</summary>
    public DshHome Home { get; private set; }

    public AccountVault Vault { get; }

    public DshBackend Backend { get; }

    public DshRuntimeVerifier Verifier { get; }

    public ReferenceService References { get; }

    public SubagentRouteService SubagentRoutes { get; }

    public CodexFastService CodexFast { get; }

    public BackupService Backups { get; }

    public SubscriptionStore Subscriptions { get; }

    public AccountMapper Mapper { get; }

    public AccountSnapshotService Snapshot { get; }

    public SwitchService Switch { get; }

    public QuotaService Quota { get; }

    public SubscriptionService Subscription { get; }

    public SubscriptionRefresher SubscriptionRefresher { get; }

    public AccountQuotaSelector QuotaSelector { get; }

    public ResetCreditService ResetCredits { get; }

    public AccountResetService Reset { get; }

    public AccountActions Actions { get; }

    public ExportService Export { get; }

    public ImportService Import { get; }

    private static DshHome ResolveHome(AppSettings settings) => DshHome.Resolve(settings.DshHome, settings.DshProfile);

    /// <summary>Re-resolves the dsh home / profile after the settings changed and points the backend at it.</summary>
    public void ApplyDshLocation()
    {
        Home = ResolveHome(Settings);
        Backend.SetHome(Home);
    }

    /// <summary>The status line shown next to the read button.</summary>
    public string DshStatus => "DSH：" + Home.Root + " · 档案 " + Home.Profile + (Home.ProfileExists ? string.Empty : "（未找到）");

    // ---- WebDAV sync (manual upload / download) ----------------------------

    /// <summary>Neutral folder shared by every tool that syncs Cockpit account JSON; the file-name prefix tells them apart.</summary>
    public const string WebDavFolder = "/AccountManager/";

    /// <summary>The folder earlier versions of this tool used. Only read from (when the shared folder is empty), never written.</summary>
    public const string LegacyWebDavFolder = "/DshAccountManager/";

    /// <summary>True when a (protected) WebDAV app password is stored.</summary>
    public bool HasWebDavPassword => !string.IsNullOrEmpty(Settings.WebDavPassword);

    public WebDavOptions BuildWebDavOptions(string folder = WebDavFolder) => new()
    {
        BaseUrl = Settings.WebDavUrl ?? string.Empty,
        Username = Settings.WebDavUsername ?? string.Empty,
        Password = SecretProtector.UnprotectText(Settings.WebDavPassword) ?? string.Empty,
        Folder = folder,
    };

    /// <summary>Stores a new app password (protected); null/empty keeps the existing one.</summary>
    public void SetWebDavPassword(string? plain)
    {
        if (!string.IsNullOrEmpty(plain)) { Settings.WebDavPassword = SecretProtector.ProtectText(plain); }
    }

    public Task<AccountSnapshot> LoadSnapshotAsync(CancellationToken ct = default)
        => Snapshot.GetSnapshotAsync(ct);

    public Task<SwitchResult> SwitchAsync(string credentialId, CancellationToken ct = default)
        => Switch.SwitchAsync(credentialId, ct);

    public Task<DeleteResult> DeleteAsync(AccountRow row, CancellationToken ct = default)
        => Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider, ct);

    /// <summary>Deletes an account; an API provider that dsh's settings still refer to is resolved with <paramref name="resolution"/> first.</summary>
    public Task<DeleteResult> DeleteAsync(AccountRow row, ReferenceResolution? resolution, CancellationToken ct = default)
        => Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider, resolution, ct);

    // ---- model references (health check, guard, repair, undo) ----

    public Task<ReferenceScan> ScanReferencesAsync(CancellationToken ct = default) => References.ScanAsync(ct);

    public Task<IReadOnlyList<ModelReference>> FindReferencesAsync(string providerId, IReadOnlyCollection<string>? models, CancellationToken ct = default)
        => Actions.FindReferencesAsync(providerId, models, ct);

    public Task<IReadOnlyList<ProviderChoice>> GetProviderChoicesAsync(string? excludeProviderId, CancellationToken ct = default)
        => References.GetProviderChoicesAsync(excludeProviderId, ct);

    /// <summary>The newest backup that contains each provider id (key = provider id), for "restore from backup".</summary>
    public async Task<Dictionary<string, string>> FindBackupsAsync(IEnumerable<string> providerIds, CancellationToken ct = default)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in providerIds.Distinct(StringComparer.Ordinal))
        {
            var backups = await References.FindBackupsContainingAsync(id, ct: ct).ConfigureAwait(false);
            if (backups.Count > 0) { found[id] = backups[0]; }
        }

        return found;
    }

    public Task RestoreProviderAsync(string backupPath, string providerId, CancellationToken ct = default)
        => References.RestoreProviderAsync(backupPath, providerId, ct);

    public Task<string> ApplyReferenceActionsAsync(IReadOnlyList<ReferenceAction> actions, CancellationToken ct = default)
        => References.ApplyAsync(actions, ct);

    public async Task<ApiSaveResult> SaveApiAsync(AccountRow row, string displayName, string baseUrl, IReadOnlyList<string> models, string? newKey, CancellationToken ct = default)
    {
        var save = await Actions.UpdateManagedApiProviderAsync(row.CredentialId, row.Provider, displayName, baseUrl, models, newKey, ct).ConfigureAwait(false);
        return await VerifyApiSaveAsync(save, OpenAiApiProtocol.PackageFor(OpenAiApiProtocol.UsesResponses(row.Definition?.Package))).ConfigureAwait(false);
    }

    public async Task<ApiSaveResult> SaveApiAsync(AccountRow row, string displayName, string baseUrl, IReadOnlyList<string> models, string? newKey, bool useResponses, CancellationToken ct = default)
        => await SaveApiAsync(row, displayName, baseUrl, models, newKey, useResponses, null, ct).ConfigureAwait(false);

    public async Task<ApiSaveResult> SaveApiAsync(AccountRow row, string displayName, string baseUrl, IReadOnlyList<string> models, string? newKey, bool useResponses, ReferenceResolution? resolution, CancellationToken ct = default)
    {
        var save = await Actions.UpdateManagedApiProviderAsync(row.CredentialId, row.Provider, displayName, baseUrl, models, newKey, useResponses, resolution, ct).ConfigureAwait(false);
        return await VerifyApiSaveAsync(save, OpenAiApiProtocol.PackageFor(useResponses)).ConfigureAwait(false);
    }

    private async Task<ApiSaveResult> VerifyApiSaveAsync(ApiSaveResult save, string expectedPackage)
    {
        if (!save.Updated || !save.Complete) { return save; }
        // After a completed write, a read timeout/cancel is not a failed save. Never redo a write.
        var runtime = await Backend.VerifyProviderAsync(save.ProviderID, expectedPackage).ConfigureAwait(false);
        save.RuntimeVerified = runtime.State == ProviderRuntimeState.Applied;
        save.Warning = runtime.Message;
        return save;
    }

    /// <summary>The OAuth client follows the platform's default proxy settings, like the quota queries do.</summary>
    public CodexOAuthService CreateOAuthService() => new(Secrets, useProxy: true);

    public Task<CodexAddPreview> PreviewCodexAuthorizationAsync(JsonObject account)
        => Actions.PreviewCodexAuthorizationAsync(account);

    public Task<AccountAddResult> SaveCodexAuthorizationAsync(CodexAddPreview preview, bool replaceExisting)
        => Actions.SaveCodexAuthorizationAsync(preview, replaceExisting);

    public async Task<AccountAddResult> CreateApiProviderAsync(string id, string name, string url, IReadOnlyList<string> models, string key, bool useResponses)
    {
        var added = await Actions.CreateManagedApiProviderAsync(id, name, url, models, key, useResponses).ConfigureAwait(false);
        if (!added.Saved || !added.Complete) { return added; }
        var runtime = await Backend.VerifyProviderAsync(id, OpenAiApiProtocol.PackageFor(useResponses)).ConfigureAwait(false);
        return new AccountAddResult
        {
            Saved = added.Saved, Complete = added.Complete, Kind = added.Kind,
            CredentialID = added.CredentialID, ProviderID = added.ProviderID, Label = added.Label,
            Backup = added.Backup, Warning = runtime.Message, RuntimeVerified = runtime.State == ProviderRuntimeState.Applied,
        };
    }

    public Task<ExportResult> ExportAsync(IReadOnlyList<string> credentialIds, string path, CancellationToken ct = default)
        => Export.ExportAsync(credentialIds, path, ct);

    public Task<ImportPreview> PreviewAsync(string path, CancellationToken ct = default)
        => Import.PreviewAsync(path, ct);

    public Task<ImportResult> ImportAsync(ImportPreview preview, CancellationToken ct = default)
        => Import.ImportAsync(preview, ct);

    public Task<List<AccountQuotaResult>> QueryAsync(IReadOnlyList<AccountRow> rows, IProgress<AccountQuotaResult>? progress, CancellationToken ct = default, bool forceSubscription = false)
        => QuotaSelector.RunBatchAsync(rows, progress, ct, forceSubscription);

    /// <summary>Persists a live subscription expiry into the local mirror (best effort).</summary>
    public Task SaveSubscriptionAsync(string accountId, string email, DateTimeOffset activeUntil, CancellationToken ct = default)
        => Subscriptions.UpdateActiveUntilAsync(accountId, email, activeUntil, ct);

    public Task ClearSubscriptionAsync(string accountId, string email, CancellationToken ct = default)
        => Subscriptions.ClearActiveUntilAsync(accountId, email, ct);

    public Task<ResetCreditList> GetResetCreditsAsync(AccountRow row, CancellationToken ct = default)
        => Reset.GetListAsync(row, ct);

    public Task<ResetConsumeResult> ConsumeResetCreditAsync(AccountRow row, string creditId, CancellationToken ct = default)
        => Reset.ConsumeAsync(row, creditId, ct);

    /// <summary>All credential ids currently saved (for reference only).</summary>
    public async Task<List<JsonNode>> GetCredentialsAsync(CancellationToken ct = default)
    {
        var array = await Backend.GetCredentialsAsync(ct).ConfigureAwait(false);
        return array.Where(n => n is not null).Select(n => n!).ToList();
    }
}
