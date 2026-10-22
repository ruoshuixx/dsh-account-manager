using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Actions;
using AccountManager.Core.Dsh;
using AccountManager.Core.Export;
using AccountManager.Core.Import;
using AccountManager.Core.Logging;
using AccountManager.Core.Security;

namespace AccountManager.Core.Tests;

/// <summary>Synthetic accounts and tokens; nothing here is real.</summary>
internal static class TestAccounts
{
    public static string AccessToken(string email, string accountId = "acct-1", long exp = 4102444800L, string plan = "plus")
    {
        var claims = new JsonObject
        {
            ["exp"] = exp,
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = accountId, ["chatgpt_plan_type"] = plan },
            ["https://api.openai.com/profile"] = new JsonObject { ["email"] = email },
        };
        return "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";
    }

    /// <summary>A Cockpit OAuth account as the browser flow / Cockpit export carries it.</summary>
    public static JsonObject AuthorizedAccount(string email, string accountId = "acct-1", string refresh = "new-refresh", long exp = 4102444800L, string plan = "plus")
        => new()
        {
            ["auth_mode"] = "oauth",
            ["email"] = email,
            ["account_id"] = accountId,
            ["tokens"] = new JsonObject { ["access_token"] = AccessToken(email, accountId, exp, plan), ["refresh_token"] = refresh, ["id_token"] = "fake-id-token" },
        };

    public static JsonObject CockpitOauth(string id, string email, string accountId, string refresh, long exp = 4102444800L, string plan = "plus")
    {
        var account = AuthorizedAccount(email, accountId, refresh, exp, plan);
        account["id"] = id;
        account["plan_type"] = plan;
        account["created_at"] = 1700000000L;
        account["last_used"] = 1700000100L;
        return account;
    }

    public static JsonObject CockpitApi(string sourceId, string name, string url, string wire, string key, params string[] models)
        => new()
        {
            ["id"] = sourceId,
            ["email"] = string.Empty,
            ["auth_mode"] = "apikey",
            ["openai_api_key"] = key,
            ["api_base_url"] = url,
            ["api_provider_mode"] = "custom",
            ["api_provider_id"] = sourceId,
            ["api_provider_name"] = name,
            ["api_model_catalog"] = new JsonArray(models.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
            ["api_wire_api"] = wire,
            ["tokens"] = new JsonObject { ["id_token"] = string.Empty, ["access_token"] = string.Empty },
            ["created_at"] = 123L,
            ["last_used"] = 456L,
        };
}

/// <summary>
/// A throw-away dsh home, app data directory and the full service graph on top of the real
/// <see cref="DshBackend"/>. Nothing touches the real ~/.dsh.
/// </summary>
internal sealed class DshFixture : IDisposable
{
    public const string Profile = "tauri";

    public DshFixture(string? credentials = null, string? patch = null, IProviderRuntimeVerifier? verifier = null, string? composedBundle = null, IReadOnlyList<string>? codexCatalog = null, bool subagents = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "dshF-" + Guid.NewGuid().ToString("N")[..10]);
        HomeDir = Path.Combine(Root, "dsh");
        AppDir = Path.Combine(Root, "app");
        Directory.CreateDirectory(Path.Combine(HomeDir, "profiles", Profile));
        if (credentials is not null) { WriteSecret(CredentialsPath, credentials); }
        File.WriteAllText(PatchPath, patch ?? "[]\n");

        Secrets = new SecretRegistry(applyLongValueFilter: false);
        Log = new AppLog(Secrets, Path.Combine(Root, "logs"));
        Home = DshHome.Resolve(HomeDir, Profile, _ => null, Root);
        Vault = new AccountVault(AppDir, Protect, Unprotect);
        Backups = new BackupService(Path.Combine(Root, "backups"), Protect, Unprotect);
        Subscriptions = new SubscriptionStore(AppDir, Log);
        Verifier = verifier as RecordingVerifier ?? new RecordingVerifier();
        Backend = new DshBackend(Home, Vault, Secrets, Subscriptions, Log, verifier ?? Verifier);
        Dumper = new FakeDumper { Compose = composedBundle is null ? null : () => composedBundle + "\n" + Patch.Replace("- id: llm-pi-ai\n", "- id: llm-pi-ai\n  name: '@deepseek-ai/dsh-llm-pi-ai'\n") };
        References = new ReferenceService(Backend, Dumper, Backups, Secrets, Log);
        Subagents = new SubagentRouteService(() => Home, Backups, Secrets, Log, () => codexCatalog ?? Array.Empty<string>());
        if (subagents) { Backend.Subagents = Subagents; }
        Mapper = new AccountMapper(Secrets);
        Snapshot = new AccountSnapshotService(Backend, Mapper, Secrets, Subscriptions);
        Switch = new SwitchService(Backend, Mapper);
        Actions = new AccountActions(Backend, Backups, Secrets, Log, Mapper, References);
        Import = new ImportService(Backend, Backups, Secrets, Log, Subscriptions, References, subagents ? Subagents : null);
        Export = new ExportService(Backend, Secrets, Log, Subscriptions);
    }

    public string Root { get; }
    public string HomeDir { get; }
    public string AppDir { get; }
    public string CredentialsPath => Path.Combine(HomeDir, ".credentials.yaml");
    public string PatchPath => Path.Combine(HomeDir, "profiles", Profile, "cordis.patch.yml");
    public DshHome Home { get; }
    public SecretRegistry Secrets { get; }
    public AppLog Log { get; }
    public AccountVault Vault { get; }
    public BackupService Backups { get; }
    public SubscriptionStore Subscriptions { get; }
    public RecordingVerifier Verifier { get; }
    public DshBackend Backend { get; }
    public FakeDumper Dumper { get; }
    public ReferenceService References { get; }
    public SubagentRouteService Subagents { get; }
    public AccountMapper Mapper { get; }
    public AccountSnapshotService Snapshot { get; }
    public SwitchService Switch { get; }
    public AccountActions Actions { get; }
    public ImportService Import { get; }
    public ExportService Export { get; }

    public string Credentials => File.Exists(CredentialsPath) ? File.ReadAllText(CredentialsPath) : string.Empty;
    public string Patch => File.ReadAllText(PatchPath);
    public CredentialsDocument CredentialsDoc => CredentialsDocument.Load(CredentialsPath);
    public JsonObject? Grant => CredentialsDoc.GetGrantPayload(DshBackend.CodexGrantKey) as JsonObject;

    private static byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).Reverse().ToArray();

    private static byte[] Unprotect(byte[] data) => data.Reverse().Select(b => (byte)(b ^ 0x5A)).ToArray();

    public static void WriteSecret(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DshAtomicWrite.WriteText(path, text);
    }

    /// <summary>Stores a Codex account through the credential boundary; returns its id.</summary>
    public async Task<string> AddCodexAsync(string email, string accountId, string refresh, bool activate = false, long exp = 4102444800L)
    {
        var row = ImportService.ConvertAccount(TestAccounts.AuthorizedAccount(email, accountId, refresh, exp), "chatgpt-browser");
        return await Backend.CreateCredentialAsync("openai", row.Label + " [" + row.Workspace + "]", row.Value, activate);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { /* best effort */ }
    }

    /// <summary>Stands in for <c>dsh --dump-config</c>: no composer means "dsh is not available".</summary>
    public sealed class FakeDumper : IDshConfigDumper
    {
        public Func<string?>? Compose { get; set; }

        public Task<string?> DumpConfigAsync(DshHome home, CancellationToken ct = default) => Task.FromResult(Compose?.Invoke());
    }

    public string RunLog => File.Exists(Log.RunLogPath) ? File.ReadAllText(Log.RunLogPath) : string.Empty;

    /// <summary>Records verification requests instead of starting dsh.</summary>
    public sealed class RecordingVerifier : IProviderRuntimeVerifier
    {
        public List<(string ProviderId, string Api)> Calls { get; } = new();

        public ProviderRuntimeState State { get; set; } = ProviderRuntimeState.Applied;

        public Task<ProviderRuntimeVerification> VerifyAsync(DshHome home, string providerId, string expectedApi, CancellationToken ct = default)
        {
            Calls.Add((providerId, expectedApi));
            return Task.FromResult(new ProviderRuntimeVerification(State, State == ProviderRuntimeState.Applied ? DshRuntimeVerifier.AppliedMessage : DshRuntimeVerifier.UnavailableMessage));
        }
    }
}
