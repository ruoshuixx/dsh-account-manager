using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using AccountManager.App.Services;
using AccountManager.App.ViewModels;
using AccountManager.Core.Accounts;
using AccountManager.Core.Actions;
using AccountManager.Core.Models;
using AccountManager.Core.Dsh;
using AccountManager.Core.Logging;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AccountManager.App.Tests;

/// <summary>
/// The toolbar's switch target (dsh / Codex / OpenCode tick boxes) at the view-model level, with synthetic accounts in a
/// temp dsh home, a temp Codex home and an in-memory OpenCode. Nothing real is read or written.
/// </summary>
public sealed class SwitchTargetViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshT-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly FakeOpenCode _openCode = new();
    private readonly FakeCodexDesktop _desktop = new();
    private readonly string _settingsPath;

    public SwitchTargetViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "dsh", "profiles", "desktop"));
        File.WriteAllText(Path.Combine(_root, "dsh", "profiles", "desktop", "cordis.patch.yml"), "[]\n");
        _settingsPath = Path.Combine(_root, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).Reverse().ToArray();

    private static byte[] Unprotect(byte[] data) => data.Reverse().Select(b => (byte)(b ^ 0x5A)).ToArray();

    private string CodexHome => Path.Combine(_root, "codex");

    private string CodexAuth => Path.Combine(CodexHome, "auth.json");

    private string DshCredentials => Path.Combine(_root, "dsh", ".credentials.yaml");

    private AppServices NewServices(AppSettings settings)
    {
        settings.DshHome = Path.Combine(_root, "dsh");
        settings.DshProfile = "desktop";
        settings.CodexHome = CodexHome;
        var secrets = new SecretRegistry(false);
        return new AppServices(settings, Path.Combine(_root, "app"), new AppLog(secrets, Path.Combine(_root, "logs")), null, _openCode,
            new AccountVault(Path.Combine(_root, "app"), Protect, Unprotect), new BackupService(Path.Combine(_root, "backups"), Protect, Unprotect), openCodeConfig: _openCode, codexDesktop: _desktop);
    }

    /// <summary>A view model on dsh as the target: accounts are seeded first, then a test selects its target.</summary>
    private (AppServices Services, MainWindowViewModel Vm, ScriptedDialogs Dialogs) NewVm(bool confirm = true)
    {
        var services = NewServices(new AppSettings { SwitchTarget = SwitchTarget.Dsh });
        var dialogs = new ScriptedDialogs { Confirm = confirm };
        return (services, new MainWindowViewModel(services, dialogs, startBackgroundWork: false, settingsStore: new SettingsStore(_settingsPath)), dialogs);
    }

    private static string Jwt(string email, string accountId, long exp = 4102444800L)
    {
        var claims = new JsonObject
        {
            ["exp"] = exp,
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = accountId, ["chatgpt_plan_type"] = "plus" },
            ["https://api.openai.com/profile"] = new JsonObject { ["email"] = email },
        };
        return "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";
    }

    private static JsonObject Value(string email, string accountId, string refresh) => new()
    {
        ["type"] = "oauth",
        ["methodID"] = "chatgpt-browser",
        ["access"] = Jwt(email, accountId),
        ["refresh"] = refresh,
        ["expires"] = 4102444800000L,
        ["metadata"] = new JsonObject { ["accountID"] = accountId, ["cockpitEmail"] = email, ["id_token"] = "fake-id-token" },
    };

    private static async Task<string> AddAsync(AppServices services, string email, string accountId, string refresh, bool activate)
        => await services.Backend.CreateCredentialAsync("openai", email + " [" + accountId + "]", Value(email, accountId, refresh), activate);

    private void WriteCodexLogin(string email, string accountId, string refresh)
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(CodexAuth, new JsonObject
        {
            ["OPENAI_API_KEY"] = null,
            ["tokens"] = new JsonObject { ["id_token"] = "fake-id-token", ["access_token"] = Jwt(email, accountId), ["refresh_token"] = refresh, ["account_id"] = accountId },
            ["last_refresh"] = "2026-10-01T10:00:00Z",
        }.ToJsonString() + "\n");
    }

    private static AccountRowViewModel Row(MainWindowViewModel vm, string id) => vm.Accounts.Single(a => a.Row.CredentialId == id);

    private static async Task<string> AddSwitchRowAsync(AppServices services, bool api)
    {
        if (!api) { return await AddAsync(services, "b@example.test", "acct-2", "refresh-b", activate: false); }
        var added = await services.Actions.CreateManagedApiProviderAsync(AccountActions.NewApiProviderId(), "测试 API",
            "https://api.example.test/v1", new[] { "test-model" }, "fake-api-key", useResponses: true);
        Assert.True(added.Saved && added.Complete, added.Warning);
        await services.ShareApisAsync();
        return added.CredentialID;
    }

    // ---- the tick boxes -------------------------------------------------------

    [AvaloniaFact]
    public void ExactlyOneBoxIsTickedAndUntickingTheSelectedOneIsRefused()
    {
        var (_, vm, _) = NewVm();
        var changed = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        void AssertOnly(bool dsh, bool codex, bool openCode)
        {
            Assert.Equal((dsh, codex, openCode), (vm.SwitchTargetDsh, vm.SwitchTargetCodex, vm.SwitchTargetOpenCode));
        }

        AssertOnly(true, false, false); // these tests start on dsh

        vm.SwitchTargetCodex = true;
        AssertOnly(false, true, false);
        Assert.Equal(SwitchTarget.Codex, vm.SelectedSwitchTarget);

        vm.SwitchTargetOpenCode = true;
        AssertOnly(false, false, true);

        changed.Clear();
        vm.SwitchTargetOpenCode = false; // the user clicks the ticked box: it stays ticked ...
        AssertOnly(false, false, true);
        Assert.Contains(nameof(MainWindowViewModel.SwitchTargetOpenCode), changed); // ... and the binding is told so

        vm.SwitchTargetDsh = false; // un-ticking a box that is not ticked changes nothing
        AssertOnly(false, false, true);

        vm.SwitchTargetDsh = true;
        AssertOnly(true, false, false);
    }

    [AvaloniaFact]
    public void TheChoiceIsPersistedAndSurvivesAReload()
    {
        var (services, vm, _) = NewVm();
        vm.SwitchTargetCodex = true;

        Assert.Equal(SwitchTarget.Codex, new SettingsStore(_settingsPath).Load().SwitchTarget);
        Assert.Contains("\"SwitchTarget\": \"Codex\"", File.ReadAllText(_settingsPath));

        var reloaded = NewServices(new SettingsStore(_settingsPath).Load());
        var vm2 = new MainWindowViewModel(reloaded, new ScriptedDialogs(), startBackgroundWork: false, settingsStore: new SettingsStore(_settingsPath));
        Assert.True(vm2.SwitchTargetCodex);
        Assert.False(vm2.SwitchTargetDsh || vm2.SwitchTargetOpenCode);
        _ = services;
    }

    [AvaloniaFact]
    public void TheApiListHintShowsOnlyForCodexAndOpenCodeAndYieldsToAWarning()
    {
        var (_, vm, _) = NewVm();
        Assert.Equal(string.Empty, vm.TargetHint);

        vm.SwitchTargetOpenCode = true;
        Assert.Contains("OpenCode", vm.TargetHint);

        vm.SwitchTargetCodex = true;
        Assert.Contains("Responses", vm.TargetHint);

        vm.ProviderWarning = "供应商缺少 Key";
        Assert.Equal(string.Empty, vm.TargetHint);

        vm.ProviderWarning = string.Empty;
        vm.SwitchTargetDsh = true;
        Assert.Equal(string.Empty, vm.TargetHint);
    }

    // ---- the "当前" marker -------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(SwitchTarget.Dsh, "A")]
    [InlineData(SwitchTarget.Codex, "B")]
    [InlineData(SwitchTarget.OpenCode, "C")]
    public async Task TheCurrentMarkerFollowsTheSelectedTarget(SwitchTarget target, string expected)
    {
        var (services, vm, _) = NewVm();
        var a = await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await AddAsync(services, "b@example.test", "acct-2", "refresh-b", activate: false);
        var c = await AddAsync(services, "c@example.test", "acct-3", "refresh-c", activate: false);
        WriteCodexLogin("b@example.test", "acct-2", "refresh-b");
        _openCode.AddLogin("c@example.test", "acct-3", "refresh-c", active: true);
        var ids = new Dictionary<string, string> { ["A"] = a, ["B"] = b, ["C"] = c };

        vm.SelectSwitchTargetForTest(target);
        await vm.ReadDshCommand.ExecuteAsync(null);

        var current = Assert.Single(vm.Accounts, r => r.IsCurrent);
        Assert.Equal(ids[expected], current.Row.CredentialId);
        Assert.Contains(target.Display(), vm.CurrentText);
        Assert.Equal(ids[expected], vm.Accounts[0].Row.CredentialId); // the current account is pinned first
    }

    // ---- switching ---------------------------------------------------------------

    [AvaloniaFact]
    public async Task ASharedAccountAsksFirstAndDecliningChangesNothing()
    {
        var (services, vm, dialogs) = NewVm(confirm: false);
        var a = await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true); // dsh uses A
        await AddAsync(services, "b@example.test", "acct-2", "refresh-b", activate: false);
        WriteCodexLogin("b@example.test", "acct-2", "refresh-b");
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        var codexBefore = File.ReadAllBytes(CodexAuth);
        var dshBefore = File.ReadAllBytes(DshCredentials);

        await vm.SwitchAsync(Row(vm, a));

        Assert.Single(dialogs.Confirms);
        Assert.Contains("dsh", dialogs.Confirms[0]);
        Assert.Contains("refresh_token_reused", dialogs.Confirms[0]);
        Assert.Equal(codexBefore, File.ReadAllBytes(CodexAuth));
        Assert.Equal(dshBefore, File.ReadAllBytes(DshCredentials));
        Assert.Contains("已取消", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task ConfirmingTheSharedSwitchWritesOnlyTheSelectedTargetAndSaysWhich()
    {
        var (services, vm, dialogs) = NewVm(confirm: true);
        var a = await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        await AddAsync(services, "b@example.test", "acct-2", "refresh-b", activate: false);
        WriteCodexLogin("b@example.test", "acct-2", "refresh-b");
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        var dshBefore = File.ReadAllBytes(DshCredentials);

        await vm.SwitchAsync(Row(vm, a));

        Assert.Single(dialogs.Confirms);
        Assert.StartsWith("已切换 Codex 账号：", vm.StatusText);
        Assert.Contains("refresh-a", File.ReadAllText(CodexAuth));
        Assert.Equal(dshBefore, File.ReadAllBytes(DshCredentials));
        Assert.True(Row(vm, a).IsCurrent);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACodexSwitchOffersToRestartTheRunningDesktopApp(bool restart)
    {
        var (services, vm, dialogs) = NewVm(confirm: restart);
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await AddAsync(services, "b@example.test", "acct-2", "refresh-b", activate: false);
        WriteCodexLogin("c@example.test", "acct-3", "refresh-c");
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        _desktop.Running = true;

        await vm.SwitchAsync(Row(vm, b));

        Assert.StartsWith("重启 Codex 测试版", Assert.Single(dialogs.Confirms));
        Assert.Contains("refresh-b", File.ReadAllText(CodexAuth)); // switched either way
        Assert.Equal(restart ? 1 : 0, _desktop.Restarts);
        Assert.Equal(0, _desktop.Starts);
        Assert.Contains(restart ? "已重启 Codex 桌面版" : "重启后才生效", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task ClickingTheCurrentCodexAccountStillOffersTheRestart()
    {
        var (services, vm, dialogs) = NewVm(confirm: true);
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await AddAsync(services, "b@example.test", "acct-2", "refresh-b", activate: false);
        WriteCodexLogin("b@example.test", "acct-2", "refresh-b");
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        Assert.True(Row(vm, b).IsCurrent);
        var authBefore = File.ReadAllBytes(CodexAuth);
        _desktop.Running = true;

        await vm.SwitchAsync(Row(vm, b));

        Assert.StartsWith("重启 Codex 测试版", Assert.Single(dialogs.Confirms));
        Assert.Equal(1, _desktop.Restarts);
        Assert.Equal(authBefore, File.ReadAllBytes(CodexAuth)); // nothing switched
        Assert.Contains("已重启 Codex 桌面版", vm.StatusText);

        _desktop.Running = false; // not running: just says it is current
        await vm.SwitchAsync(Row(vm, b));
        Assert.Single(dialogs.Confirms);
        Assert.Contains("已经是 Codex 的当前账号", vm.StatusText);
        Assert.Equal(0, _desktop.Starts);
    }

    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ClickingTheCurrentCodexRowStartsAnInstalledDesktopAppWithoutAsking(bool api, bool installed)
    {
        var (services, vm, dialogs) = NewVm();
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        WriteCodexLogin("c@example.test", "acct-3", "refresh-c");
        var b = await AddSwitchRowAsync(services, api);
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await services.SwitchAsync(b);
        await vm.ReadDshCommand.ExecuteAsync(null);
        var row = Row(vm, b);
        Assert.True(row.IsCurrent);
        var authBefore = File.ReadAllBytes(CodexAuth);
        var configBefore = services.CodexApi.ReadText();
        _desktop.Installed = installed;

        await vm.SwitchAsync(row);

        Assert.Empty(dialogs.Confirms);
        Assert.Equal(installed ? 1 : 0, _desktop.Starts);
        Assert.Equal(0, _desktop.Restarts);
        Assert.Equal(authBefore, File.ReadAllBytes(CodexAuth));
        Assert.Equal(configBefore, services.CodexApi.ReadText());
        Assert.Equal(row.Title + " 已经是 Codex 的当前账号" + (installed ? " · 已启动 Codex 桌面版" : "。"), vm.StatusText);
    }

    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task SwitchingAnotherCodexRowStartsAnInstalledDesktopAppWithoutAsking(bool api, bool installed)
    {
        var (services, vm, dialogs) = NewVm();
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        WriteCodexLogin("c@example.test", "acct-3", "refresh-c");
        var b = await AddSwitchRowAsync(services, api);
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        Assert.False(Row(vm, b).IsCurrent);
        _desktop.Installed = installed;

        await vm.SwitchAsync(Row(vm, b));

        Assert.Empty(dialogs.Confirms);
        Assert.Equal(installed ? 1 : 0, _desktop.Starts);
        Assert.Equal(0, _desktop.Restarts);
        Assert.True(Row(vm, b).IsCurrent);
        Assert.StartsWith("已切换 Codex 账号：", vm.StatusText);
        if (installed)
        {
            Assert.EndsWith(" · 已启动 Codex 桌面版", vm.StatusText);
            Assert.DoesNotContain("重启后才生效", vm.StatusText);
        }
        else { Assert.Contains("重启后才生效", vm.StatusText); }
    }

    [AvaloniaFact]
    public async Task WithoutARunningDesktopAppTheCodexSwitchDoesNotAsk()
    {
        var (services, vm, dialogs) = NewVm();
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await AddAsync(services, "b@example.test", "acct-2", "refresh-b", activate: false);
        WriteCodexLogin("c@example.test", "acct-3", "refresh-c");
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);

        await vm.SwitchAsync(Row(vm, b));

        Assert.Empty(dialogs.Confirms);
        Assert.Equal(0, _desktop.Restarts);
        Assert.Equal(0, _desktop.Starts);
    }

    [AvaloniaFact]
    public async Task AnAccountNobodyElseUsesIsSwitchedWithoutAsking()
    {
        var (services, vm, dialogs) = NewVm();
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await AddAsync(services, "b@example.test", "acct-2", "refresh-b", activate: false);
        _openCode.Available = true;
        vm.SelectSwitchTargetForTest(SwitchTarget.OpenCode);
        await vm.ReadDshCommand.ExecuteAsync(null);

        await vm.SwitchAsync(Row(vm, b));

        Assert.Empty(dialogs.Confirms);
        Assert.StartsWith("已切换 OpenCode 账号：", vm.StatusText);
        Assert.Equal("refresh-b", _openCode.Credentials.Single(c => c["active"]!.GetValue<bool>())["value"]!["refresh"]!.GetValue<string>());
        Assert.True(Row(vm, b).IsCurrent);
    }

    [AvaloniaFact]
    public async Task AMissingOpenCodeStopsTheSwitchWithAClearMessage()
    {
        var (services, vm, dialogs) = NewVm();
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await AddAsync(services, "b@example.test", "acct-2", "refresh-b", activate: false);
        vm.SelectSwitchTargetForTest(SwitchTarget.OpenCode);
        await vm.ReadDshCommand.ExecuteAsync(null);
        Assert.Contains("OpenCode", vm.ProviderWarning); // the list already says why there is no current account
        var dshBefore = File.ReadAllBytes(DshCredentials);

        await vm.SwitchAsync(Row(vm, b));

        Assert.Contains("找不到 OpenCode", vm.StatusText);
        Assert.Contains(dialogs.Warnings, w => w.Contains("找不到 OpenCode"));
        Assert.Equal(dshBefore, File.ReadAllBytes(DshCredentials));
        Assert.False(File.Exists(CodexAuth));
        Assert.Empty(_openCode.Credentials);
    }

    [AvaloniaFact]
    public async Task TheAutomaticSwitchNeverTakesAnAccountAnotherProgramUses()
    {
        var (services, vm, dialogs) = NewVm();
        var a = await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true); // dsh uses A
        await AddAsync(services, "b@example.test", "acct-2", "refresh-b", activate: false);
        WriteCodexLogin("b@example.test", "acct-2", "refresh-b");
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        var codexBefore = File.ReadAllBytes(CodexAuth);

        var ids = await services.GetIdsUsedByOtherTargetsAsync();
        Assert.Equal(new[] { a }, ids.ToArray());

        var switchCore = typeof(MainWindowViewModel).GetMethod("SwitchCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)switchCore.Invoke(vm, new object[] { Row(vm, a), false })!;

        Assert.Empty(dialogs.Confirms); // automatic: never asks ...
        Assert.Equal(codexBefore, File.ReadAllBytes(CodexAuth)); // ... and never switches
        Assert.Contains("已跳过", vm.StatusText);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnAutomaticCodexSwitchNeverStartsOrRestartsTheDesktopApp(bool current, bool running)
    {
        var (services, vm, dialogs) = NewVm();
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await AddAsync(services, "b@example.test", "acct-2", "refresh-b", activate: false);
        WriteCodexLogin(current ? "b@example.test" : "c@example.test", current ? "acct-2" : "acct-3", current ? "refresh-b" : "refresh-c");
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        _desktop.Installed = true;
        _desktop.Running = running;

        var switchCore = typeof(MainWindowViewModel).GetMethod("SwitchCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)switchCore.Invoke(vm, new object[] { Row(vm, b), false })!;

        Assert.Empty(dialogs.Confirms);
        Assert.Equal(0, _desktop.Starts);
        Assert.Equal(0, _desktop.Restarts);
        Assert.True(Row(vm, b).IsCurrent);
        Assert.Contains("refresh-b", File.ReadAllText(CodexAuth));
    }

    // ---- fakes ------------------------------------------------------------------

    private sealed class FakeCodexDesktop : ICodexDesktopApp
    {
        public string Name => "Codex 测试版";
        public bool Running { get; set; }
        public bool Installed { get; set; }
        public int Starts { get; private set; }
        public int Restarts { get; private set; }
        public bool IsRunning() => Running;
        public bool IsInstalled() => Installed;
        public Task<string> StartAsync(CancellationToken cancellationToken = default) { Starts++; return Task.FromResult("已启动 Codex 桌面版"); }
        public Task<string> RestartAsync(CancellationToken cancellationToken = default) { Restarts++; return Task.FromResult("已重启 Codex 桌面版"); }
    }

    private sealed class ScriptedDialogs : IDialogService
    {
        public bool Confirm { get; set; } = true;
        public List<string> Confirms { get; } = new();
        public List<string> Warnings { get; } = new();
        public Task<bool> ConfirmAsync(string title, string message, bool dangerous = false) { Confirms.Add(title + "\n" + message); return Task.FromResult(Confirm); }
        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;
        public Task ShowWarningAsync(string title, string message) { Warnings.Add(message); return Task.CompletedTask; }
        public Task<string?> PickOpenFileAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFileAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
        public Task<ApiConfigResult?> ShowApiConfigAsync(ApiConfigInput input) => Task.FromResult<ApiConfigResult?>(null);
        public Task<AccountAddResult?> ShowAddAccountAsync(AppServices services) => Task.FromResult<AccountAddResult?>(null);
        public Task<bool> ShowResetCreditsAsync(ResetCreditsInput input) => Task.FromResult(false);
        public Task<SettingsResult?> ShowSettingsAsync(SettingsInput input) => Task.FromResult<SettingsResult?>(null);
        public Task<ReferenceGuardResult?> ShowReferenceGuardAsync(ReferenceGuardInput input) => Task.FromResult<ReferenceGuardResult?>(null);
        public Task<IReadOnlyList<RepairChoice>?> ShowReferenceRepairAsync(ReferenceRepairInput input) => Task.FromResult<IReadOnlyList<RepairChoice>?>(null);
    }

    /// <summary>An in-memory OpenCode; unavailable (like a missing CLI) until a test says otherwise.</summary>
    private sealed class FakeOpenCode : IOpenCodeCredentialApi, IOpenCodeConfigFile
    {
        public bool Available { get; set; }
        public List<JsonObject> Credentials { get; } = new();

        private void Check()
        {
            if (!Available) { throw new OpenCodeException("找不到 OpenCode CLI。请先启动桌面版一次，或在设置中手动选择 CLI 文件。"); }
        }

        public Task<OpenCodeConfigDocument> ReadAsync(CancellationToken cancellationToken = default)
            => throw new OpenCodeException("找不到 OpenCode 配置（测试环境）。");

        public Task WriteAsync(OpenCodeConfigDocument document, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The test must not write an OpenCode config.");

        public void AddLogin(string email, string accountId, string refresh, bool active)
        {
            Available = true;
            Credentials.Add(new JsonObject
            {
                ["id"] = "oc_" + Credentials.Count, ["integrationID"] = "openai", ["label"] = email, ["active"] = active,
                ["value"] = Value(email, accountId, refresh),
            });
        }

        public Task<JsonArray> GetCredentialsAsync(CancellationToken cancellationToken = default)
        {
            Check();
            return Task.FromResult(new JsonArray(Credentials.Select(c => (JsonNode?)c.DeepClone()).ToArray()));
        }

        public Task<string> CreateCredentialAsync(string integrationId, string label, JsonObject value, bool activate, CancellationToken cancellationToken = default, string? requestedId = null)
        {
            Check();
            var id = requestedId ?? "oc_" + Credentials.Count;
            if (activate) { foreach (var c in Credentials) { c["active"] = false; } }
            Credentials.Add(new JsonObject { ["id"] = id, ["integrationID"] = integrationId, ["label"] = label, ["active"] = activate, ["value"] = value.DeepClone() });
            return Task.FromResult(id);
        }

        public Task ActivateCredentialAsync(string credentialId, CancellationToken cancellationToken = default)
        {
            Check();
            foreach (var c in Credentials) { c["active"] = c["id"]!.GetValue<string>() == credentialId; }
            return Task.CompletedTask;
        }

        public Task DeleteCredentialAsync(string credentialId, CancellationToken cancellationToken = default)
        {
            Check();
            Credentials.RemoveAll(c => c["id"]!.GetValue<string>() == credentialId);
            return Task.CompletedTask;
        }
    }
}
