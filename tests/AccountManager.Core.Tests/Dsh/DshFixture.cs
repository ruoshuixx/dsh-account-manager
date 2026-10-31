using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Actions;
using AccountManager.Core.Dsh;
using AccountManager.Core.Export;
using AccountManager.Core.Import;
using AccountManager.Core.Logging;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;
using AccountManager.Core.Sharing;

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

    public DshFixture(string? credentials = null, string? patch = null, IProviderRuntimeVerifier? verifier = null, string? composedBundle = null, IReadOnlyList<string>? codexCatalog = null, bool subagents = false,
        bool fast = false, string? packageJson = null, IReadOnlyDictionary<string, JsonObject>? catalogModels = null,
        CodexModelCatalogClient? codexClient = null, CodexModelOwnership? ownership = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "dshF-" + Guid.NewGuid().ToString("N")[..10]);
        HomeDir = Path.Combine(Root, "dsh");
        AppDir = Path.Combine(Root, "app");
        Directory.CreateDirectory(Path.Combine(HomeDir, "profiles", Profile));
        if (credentials is not null) { WriteSecret(CredentialsPath, credentials); }
        File.WriteAllText(PatchPath, patch ?? "[]\n");
        if (packageJson is not null) { File.WriteAllText(PackageJsonPath, packageJson); }

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
        Fast = new CodexFastService(() => Home, Backups, Secrets, Log, Subagents, () => catalogModels ?? new Dictionary<string, JsonObject>(), codexClient, ownership);
        if (fast) { Backend.Fast = Fast; }
        Mapper = new AccountMapper(Secrets);
        CodexStore = new CodexCliAuthStore(() => CodexHome, Backups, _ => null, () => Root);
        OpenCodeApi = new FakeOpenCodeApi { Unavailable = true };
        OpenCode = new OpenCodeAccountStore(OpenCodeApi, Secrets, Log);
        CodexApi = new CodexApiConfig(CodexStore, Backups);
        OpenCodeConfig = new FakeOpenCodeConfig(Path.Combine(Root, "opencode", "opencode.json"));
        Share = new ApiShareService(Backend, CodexApi, OpenCodeApi, OpenCodeConfig, Secrets, Log, Backups);
        Targets = new SwitchTargets(Backend, Vault, CodexStore, OpenCode, Secrets, Log, () => Target, CodexApi);
        Backend.SelectedTarget = () => Target;
        Backend.TargetRefresher = Targets.RefreshIfCurrentAsync;
        Snapshot = new AccountSnapshotService(Backend, Mapper, Secrets, Subscriptions, Targets);
        Switch = new SwitchService(Backend, Mapper, Targets);
        Actions = new AccountActions(Backend, Backups, Secrets, Log, Mapper, References) { FastInstaller = Fast.Installer };
        Import = new ImportService(Backend, Backups, Secrets, Log, Subscriptions, References, subagents ? Subagents : null, fast ? Fast : null);
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
    public CodexFastService Fast { get; }
    public string PackageJsonPath => Path.Combine(HomeDir, "profiles", Profile, "package.json");
    public string PluginDir => Path.Combine(HomeDir, "profiles", Profile, "node_modules", FastComponentInstaller.PackageName);
    public AccountMapper Mapper { get; }
    public AccountSnapshotService Snapshot { get; }
    public SwitchService Switch { get; }
    public AccountActions Actions { get; }
    public ImportService Import { get; }
    public ExportService Export { get; }

    /// <summary>The selected switch target (the app's setting).</summary>
    public SwitchTarget Target { get; set; } = SwitchTarget.Dsh;
    public CodexCliAuthStore CodexStore { get; }
    public FakeOpenCodeApi OpenCodeApi { get; }
    public IOpenCodeAccounts OpenCode { get; }
    public SwitchTargets Targets { get; }
    public CodexApiConfig CodexApi { get; }
    public FakeOpenCodeConfig OpenCodeConfig { get; }
    public ApiShareService Share { get; }
    public string CodexConfigPath => Path.Combine(CodexHome, "config.toml");

    public string CodexHome => Path.Combine(Root, "codex");
    public string CodexAuthPath => Path.Combine(CodexHome, "auth.json");

    /// <summary>Prepares the temp CODEX_HOME (it exists only when <paramref name="createHome"/>); nothing is switched by this.</summary>
    public CodexCliAuthStore UseCli(bool createHome = true, string? authJson = null)
    {
        if (createHome) { Directory.CreateDirectory(CodexHome); }
        if (authJson is not null) { File.WriteAllText(CodexAuthPath, authJson); }
        return CodexStore;
    }

    /// <summary>Makes the fake OpenCode available (it is "not installed" by default).</summary>
    public FakeOpenCodeApi UseOpenCode()
    {
        OpenCodeApi.Unavailable = false;
        return OpenCodeApi;
    }

    /// <summary>An auth.json as Codex CLI writes it, for a synthetic account.</summary>
    public static string AuthJson(string email, string accountId, string refresh, long exp = 4102444800L, string lastRefresh = "2026-10-01T10:00:00.123456789Z")
        => new JsonObject
        {
            ["OPENAI_API_KEY"] = null,
            ["tokens"] = new JsonObject
            {
                ["id_token"] = "cli-id-token",
                ["access_token"] = TestAccounts.AccessToken(email, accountId, exp),
                ["refresh_token"] = refresh,
                ["account_id"] = accountId,
            },
            ["last_refresh"] = lastRefresh,
            ["x-unknown"] = new JsonObject { ["keep"] = JsonNode.Parse("[1,2.0,\"three\"]") },
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";

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

/// <summary>An in-memory OpenCode credential API; "Unavailable" behaves like a missing CLI. No real OpenCode is ever run.</summary>
internal sealed class FakeOpenCodeApi : IOpenCodeCredentialApi
{
    public bool Unavailable { get; set; }

    public List<JsonObject> Credentials { get; } = new();

    public List<string> Calls { get; } = new();

    public bool FailDelete { get; set; }

    private void Check()
    {
        if (Unavailable) { throw new OpenCodeException("找不到 OpenCode CLI。请先启动桌面版一次，或在设置中手动选择 CLI 文件。"); }
    }

    public JsonObject AddLogin(string email, string accountId, string refresh, bool active = false, long exp = 4102444800L, string id = "", string integration = "openai")
    {
        var credential = new JsonObject
        {
            ["id"] = id.Length > 0 ? id : "oc_" + Guid.NewGuid().ToString("N")[..8],
            ["integrationID"] = integration,
            ["label"] = email,
            ["active"] = active,
            ["value"] = new JsonObject
            {
                ["type"] = "oauth",
                ["methodID"] = "chatgpt-browser",
                ["access"] = TestAccounts.AccessToken(email, accountId, exp),
                ["refresh"] = refresh,
                ["expires"] = exp * 1000,
                ["metadata"] = new JsonObject { ["accountID"] = accountId, ["cockpitEmail"] = email, ["id_token"] = "oc-id-token" },
            },
        };
        Credentials.Add(credential);
        return credential;
    }

    public JsonObject? Find(string id) => Credentials.FirstOrDefault(c => c["id"]!.GetValue<string>() == id);

    public string? ActiveId => Credentials.Where(c => c["integrationID"]!.GetValue<string>() == "openai" && c["active"]!.GetValue<bool>())
        .Select(c => c["id"]!.GetValue<string>()).SingleOrDefault();

    public Task<JsonArray> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        Check();
        return Task.FromResult(new JsonArray(Credentials.Select(c => (JsonNode?)c.DeepClone()).ToArray()));
    }

    public Task<string> CreateCredentialAsync(string integrationId, string label, JsonObject value, bool activate, CancellationToken cancellationToken = default, string? requestedId = null)
    {
        Check();
        Calls.Add("create:" + integrationId + ":" + activate);
        var id = requestedId ?? "oc_" + Guid.NewGuid().ToString("N")[..8];
        if (activate) { foreach (var c in Credentials.Where(c => c["integrationID"]!.GetValue<string>() == integrationId)) { c["active"] = false; } }
        Credentials.Add(new JsonObject { ["id"] = id, ["integrationID"] = integrationId, ["label"] = label, ["active"] = activate, ["value"] = value.DeepClone() });
        return Task.FromResult(id);
    }

    public Task ActivateCredentialAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        Check();
        Calls.Add("activate:" + credentialId);
        var target = Find(credentialId) ?? throw new OpenCodeException("OpenCode 请求失败，退出码 1；分类：NotFound。");
        foreach (var c in Credentials.Where(c => c["integrationID"]!.GetValue<string>() == target["integrationID"]!.GetValue<string>())) { c["active"] = false; }
        target["active"] = true;
        return Task.CompletedTask;
    }

    public Task DeleteCredentialAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        Check();
        Calls.Add("delete:" + credentialId);
        if (FailDelete) { throw new OpenCodeException("OpenCode 请求失败，退出码 1；分类：Conflict。"); }
        Credentials.RemoveAll(c => c["id"]!.GetValue<string>() == credentialId);
        return Task.CompletedTask;
    }
}

/// <summary>An opencode.json in the temp root; "not installed" follows the fake OpenCode API.</summary>
internal sealed class FakeOpenCodeConfig : IOpenCodeConfigFile
{
    private readonly OpenCodeConfigFileOnDisk _disk;

    public FakeOpenCodeConfig(string path) => _disk = new OpenCodeConfigFileOnDisk(path);

    public string Path => _disk.Path;

    public int Writes { get; private set; }

    public JsonObject? Read() => File.Exists(Path) ? (JsonObject)JsonNode.Parse(File.ReadAllText(Path))! : null;

    public Task<OpenCodeConfigDocument> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_disk.Read());

    public Task WriteAsync(OpenCodeConfigDocument document, CancellationToken cancellationToken = default)
    {
        Writes++;
        _disk.Write(document);
        return Task.CompletedTask;
    }

    private sealed class OpenCodeConfigFileOnDisk
    {
        public OpenCodeConfigFileOnDisk(string path) => Path = path;

        public string Path { get; }

        public OpenCodeConfigDocument Read()
        {
            var raw = File.Exists(Path) ? File.ReadAllText(Path) : null;
            return new OpenCodeConfigDocument { Path = Path, Raw = raw, Config = raw is null ? new JsonObject() : (JsonObject)JsonNode.Parse(raw)! };
        }

        public void Write(OpenCodeConfigDocument document)
        {
            var now = File.Exists(Path) ? File.ReadAllText(Path) : null;
            if (now != document.Raw) { throw new OpenCodeException("changed meanwhile"); }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, document.Config.ToJsonString());
        }
    }
}
