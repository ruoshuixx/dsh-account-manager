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
using AccountManager.Core.Quota;
using AccountManager.Core.Sharing;
using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
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

    private string CodexState => Path.Combine(CodexHome, ".codex-global-state.json");

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
    private (AppServices Services, MainWindowViewModel Vm, ScriptedDialogs Dialogs) NewVm(bool confirm = true, AccountManager.Core.Updates.GitHubUpdateService? updates = null)
    {
        var services = NewServices(new AppSettings { SwitchTarget = SwitchTarget.Dsh });
        var dialogs = new ScriptedDialogs { Confirm = confirm };
        return (services, new MainWindowViewModel(services, dialogs, startBackgroundWork: false, settingsStore: new SettingsStore(_settingsPath), updates: updates), dialogs);
    }

    // ---- the reminder at every start ----------------------------------------------------------

    private static AccountManager.Core.Updates.GitHubUpdateService UpdatesReturning(Func<System.Net.Http.HttpResponseMessage> response)
        => new(new System.Net.Http.HttpClient(new FakeReleaseHandler(response)), AppUpdateInfo.Owner, AppUpdateInfo.Repository, AccountManager.Core.Updates.UpdatePlatform.WindowsX64);

    private static System.Net.Http.HttpResponseMessage ReleaseResponse(string version)
    {
        var tag = "v" + version;
        var exe = "AIAccountManager-" + version + "-win-x64.exe";
        var baseUrl = $"https://github.com/{AppUpdateInfo.Owner}/{AppUpdateInfo.Repository}/releases/download/{tag}/";
        var json = new JsonObject
        {
            ["tag_name"] = tag,
            ["draft"] = false,
            ["prerelease"] = false,
            ["html_url"] = $"https://github.com/{AppUpdateInfo.Owner}/{AppUpdateInfo.Repository}/releases/tag/{tag}",
            ["body"] = "notes",
            ["assets"] = new JsonArray
            {
                new JsonObject { ["name"] = exe, ["size"] = 9, ["browser_download_url"] = baseUrl + exe },
                new JsonObject { ["name"] = exe + ".sha256", ["size"] = 80, ["browser_download_url"] = baseUrl + exe + ".sha256" },
            },
        };
        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private sealed class FakeReleaseHandler(Func<System.Net.Http.HttpResponseMessage> response) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response());
    }

    [Fact]
    public async Task ANewerReleaseIsAskedAboutAtStart()
    {
        var (_, vm, dialogs) = NewVm(confirm: false, updates: UpdatesReturning(() => ReleaseResponse("99.0.0")));

        await vm.RemindUpdateAsync();

        Assert.True(vm.HasUpdate);
        // Where this build can update itself the same question as the "检查更新" button is asked; elsewhere the download page is shown.
        if (DebInstallUpdater.IsSupported || SelfUpdater.IsSupported)
        {
            Assert.Contains("v99.0.0", Assert.Single(dialogs.Confirms));
        }
        else
        {
            Assert.Contains(AppUpdateInfo.ReleaseUrl, Assert.Single(dialogs.Infos));
        }
    }

    [Fact]
    public async Task NoNewerReleaseOrAFailedCheckAsksNothingAtStart()
    {
        var (_, upToDate, upToDateDialogs) = NewVm(updates: UpdatesReturning(() => ReleaseResponse("0.0.1")));
        await upToDate.RemindUpdateAsync();
        Assert.False(upToDate.HasUpdate);
        Assert.Empty(upToDateDialogs.Confirms);
        Assert.Empty(upToDateDialogs.Infos);
        Assert.Empty(upToDateDialogs.Warnings);

        var (_, failed, failedDialogs) = NewVm(updates: UpdatesReturning(() => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));
        await failed.RemindUpdateAsync();
        Assert.False(failed.HasUpdate);
        Assert.Empty(failedDialogs.Confirms);
        Assert.Empty(failedDialogs.Infos);
        Assert.Empty(failedDialogs.Warnings);
    }

    private static string Jwt(string email, string accountId, long exp = 4102444800L, string plan = "plus", long? iat = null)
    {
        var claims = new JsonObject
        {
            ["exp"] = exp,
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = accountId, ["chatgpt_plan_type"] = plan },
            ["https://api.openai.com/profile"] = new JsonObject { ["email"] = email },
        };
        if (iat is not null) { claims["iat"] = iat; }
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

    private byte[] WriteProjectState()
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(CodexState, """
            {"local-projects":{"p-active":{}},"project-order":["p-active"],
             "electron-account-renderer-state-v1":{"owner":"scope-a","inactive":{
               "scope-b":{"atoms":{},"globals":{"local-projects":{"p-inactive":{}},"project-order":["p-inactive"]}}
             }}}
            """);
        return File.ReadAllBytes(CodexState);
    }

    private void AssertProjectsShared()
    {
        Assert.False(_desktop.Running);
        var root = JsonNode.Parse(File.ReadAllText(CodexState))!;
        Assert.Equal(2, root["local-projects"]!.AsObject().Count);
        Assert.True(JsonNode.DeepEquals(root["local-projects"], root["electron-account-renderer-state-v1"]!["inactive"]!["scope-b"]!["globals"]!["local-projects"]));
    }

    private void TrackCodexOrder(AppServices services)
    {
        services.Log.EntryWritten += (_, entry) =>
        {
            if (entry.Message.StartsWith("Codex：已把该账号写入 ", StringComparison.Ordinal)
                || entry.Message.StartsWith("Codex：已改用 API ", StringComparison.Ordinal)) { _desktop.Order.Add("Write"); }
            if (entry.Message.StartsWith("已让各账号共用 ", StringComparison.Ordinal)) { _desktop.Order.Add("Share"); }
        };
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
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ARunningDesktopIsStoppedBeforeWritingAndStartedAfterSharingOnlyWhenConfirmed(bool api, bool restart)
    {
        var (services, vm, dialogs) = NewVm(confirm: restart);
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        WriteCodexLogin("c@example.test", "acct-3", "refresh-c");
        var b = await AddSwitchRowAsync(services, api);
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        var authBefore = File.ReadAllBytes(CodexAuth);
        var configBefore = services.CodexApi.ReadText();
        var stateBefore = WriteProjectState();
        TrackCodexOrder(services);
        dialogs.OnConfirm = () =>
        {
            Assert.Equal(authBefore, File.ReadAllBytes(CodexAuth));
            Assert.Equal(configBefore, services.CodexApi.ReadText());
            Assert.Equal(stateBefore, File.ReadAllBytes(CodexState));
        };
        _desktop.OnStop = () =>
        {
            Assert.Equal(authBefore, File.ReadAllBytes(CodexAuth));
            Assert.Equal(configBefore, services.CodexApi.ReadText());
            Assert.Equal(stateBefore, File.ReadAllBytes(CodexState));
        };
        _desktop.OnStart = () =>
        {
            if (api) { Assert.NotEqual(configBefore, services.CodexApi.ReadText()); }
            else { Assert.NotEqual(authBefore, File.ReadAllBytes(CodexAuth)); }
            AssertProjectsShared();
        };
        _desktop.Running = true;

        await vm.SwitchAsync(Row(vm, b));

        var prompt = Assert.Single(dialogs.Confirms);
        Assert.StartsWith("关闭 Codex 测试版", prompt);
        Assert.Contains("切换需要先关闭 Codex 测试版，再写入新账号并重新打开。现在关闭吗？正在进行的对话或任务会被中断。", prompt);
        if (api) { Assert.NotEqual(configBefore, services.CodexApi.ReadText()); }
        else { Assert.Contains("refresh-b", File.ReadAllText(CodexAuth)); }
        Assert.Equal(restart ? 1 : 0, _desktop.Stops);
        Assert.Equal(restart ? 1 : 0, _desktop.Starts);
        Assert.Equal(0, _desktop.Restarts);
        Assert.Equal(restart ? new[] { "Stop", "Write", "Share", "Start" } : new[] { "Write" }, _desktop.Order);
        Assert.Empty(dialogs.Warnings);
        Assert.Contains(restart ? "已启动 Codex 桌面版" : "重启后才生效", vm.StatusText);
        if (!restart)
        {
            Assert.Equal(stateBefore, File.ReadAllBytes(CodexState));
            Assert.False(File.Exists(CodexState + ".ai-account-manager.bak"));
        }
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
        WriteProjectState();
        TrackCodexOrder(services);
        _desktop.OnStart = AssertProjectsShared;
        _desktop.Running = true;

        await vm.SwitchAsync(Row(vm, b));

        Assert.StartsWith("关闭 Codex 测试版", Assert.Single(dialogs.Confirms));
        Assert.Equal(1, _desktop.Stops);
        Assert.Equal(1, _desktop.Starts);
        Assert.Equal(0, _desktop.Restarts);
        Assert.Equal(new[] { "Stop", "Share", "Start" }, _desktop.Order);
        Assert.Equal(authBefore, File.ReadAllBytes(CodexAuth)); // nothing switched
        Assert.Contains("已启动 Codex 桌面版", vm.StatusText);

        _desktop.Running = false; // not running: just says it is current
        await vm.SwitchAsync(Row(vm, b));
        Assert.Single(dialogs.Confirms);
        Assert.Contains("已经是 Codex 的当前账号", vm.StatusText);
        Assert.Equal(1, _desktop.Starts);
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
        var stateBefore = WriteProjectState();
        TrackCodexOrder(services);
        _desktop.OnStart = AssertProjectsShared;
        _desktop.Installed = installed;

        await vm.SwitchAsync(row);

        Assert.Empty(dialogs.Confirms);
        Assert.Equal(installed ? 1 : 0, _desktop.Starts);
        Assert.Equal(0, _desktop.Stops);
        Assert.Equal(0, _desktop.Restarts);
        Assert.Equal(installed ? new[] { "Share", "Start" } : Array.Empty<string>(), _desktop.Order);
        if (!installed) { Assert.Equal(stateBefore, File.ReadAllBytes(CodexState)); }
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
        var stateBefore = WriteProjectState();
        TrackCodexOrder(services);
        _desktop.OnStart = AssertProjectsShared;
        _desktop.Installed = installed;

        await vm.SwitchAsync(Row(vm, b));

        Assert.Empty(dialogs.Confirms);
        Assert.Equal(installed ? 1 : 0, _desktop.Starts);
        Assert.Equal(0, _desktop.Stops);
        Assert.Equal(0, _desktop.Restarts);
        Assert.Equal(installed ? new[] { "Write", "Share", "Start" } : new[] { "Write" }, _desktop.Order);
        if (!installed) { Assert.Equal(stateBefore, File.ReadAllBytes(CodexState)); }
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
        var stateBefore = WriteProjectState();

        var switchCore = typeof(MainWindowViewModel).GetMethod("SwitchCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)switchCore.Invoke(vm, new object[] { Row(vm, b), false })!;

        Assert.Empty(dialogs.Confirms);
        Assert.Equal(0, _desktop.Starts);
        Assert.Equal(0, _desktop.Stops);
        Assert.Equal(0, _desktop.Restarts);
        Assert.Equal(stateBefore, File.ReadAllBytes(CodexState));
        Assert.False(File.Exists(CodexState + ".ai-account-manager.bak"));
        Assert.True(Row(vm, b).IsCurrent);
        Assert.Contains("refresh-b", File.ReadAllText(CodexAuth));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AManagedDaemonStillOffersToRestartAfterWritingAndIsNeverStopped(bool restart)
    {
        var (services, vm, dialogs) = NewVm(confirm: restart);
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await AddSwitchRowAsync(services, api: false);
        WriteCodexLogin("c@example.test", "acct-3", "refresh-c");
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        var stateBefore = WriteProjectState();
        TrackCodexOrder(services);
        _desktop.DaemonRunning = true;
        dialogs.OnConfirm = () => Assert.Contains("refresh-b", File.ReadAllText(CodexAuth));

        await vm.SwitchAsync(Row(vm, b));

        Assert.StartsWith("重启 Codex 测试版", Assert.Single(dialogs.Confirms));
        Assert.Equal(0, _desktop.Stops);
        Assert.Equal(0, _desktop.Starts);
        Assert.Equal(restart ? 1 : 0, _desktop.Restarts);
        Assert.Equal(restart ? new[] { "Write", "Restart" } : new[] { "Write" }, _desktop.Order);
        Assert.Equal(stateBefore, File.ReadAllBytes(CodexState));
        Assert.True(Row(vm, b).IsCurrent);
    }

    [AvaloniaFact]
    public async Task ADesktopAndManagedDaemonStopOnlyTheDesktopBeforeWritingAndRestartTheDaemonAfterSharing()
    {
        var (services, vm, dialogs) = NewVm();
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await AddSwitchRowAsync(services, api: false);
        WriteCodexLogin("c@example.test", "acct-3", "refresh-c");
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        WriteProjectState();
        TrackCodexOrder(services);
        _desktop.Running = true;
        _desktop.Installed = true;
        _desktop.DaemonRunning = true;
        _desktop.OnStop = () => Assert.True(_desktop.DaemonRunning);
        _desktop.OnRestart = () =>
        {
            Assert.Contains("refresh-b", File.ReadAllText(CodexAuth));
            AssertProjectsShared();
        };

        await vm.SwitchAsync(Row(vm, b));

        Assert.StartsWith("关闭 Codex 测试版", Assert.Single(dialogs.Confirms));
        Assert.Equal(new[] { "Stop", "Write", "Share", "Restart" }, _desktop.Order);
        Assert.Empty(dialogs.Warnings);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADesktopThatWillNotStopPreventsTheAuthWrite(bool stopThrows)
    {
        var (services, vm, dialogs) = NewVm();
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await AddSwitchRowAsync(services, api: false);
        WriteCodexLogin("c@example.test", "acct-3", "refresh-c");
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        var authBefore = File.ReadAllBytes(CodexAuth);
        var stateBefore = WriteProjectState();
        TrackCodexOrder(services);
        _desktop.Running = true;
        _desktop.RefusesStop = true;
        if (stopThrows) { _desktop.OnStop = () => throw new InvalidOperationException("程序仍未退出，已取消切换"); }

        await vm.SwitchAsync(Row(vm, b));

        Assert.Single(dialogs.Confirms);
        Assert.Contains("已取消切换", Assert.Single(dialogs.Warnings));
        Assert.Equal(new[] { "Stop" }, _desktop.Order);
        Assert.Equal(authBefore, File.ReadAllBytes(CodexAuth));
        Assert.Equal(stateBefore, File.ReadAllBytes(CodexState));
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task ASharingFailureIsLoggedAndDoesNotPreventSwitchingOrStarting()
    {
        var (services, vm, dialogs) = NewVm();
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await AddSwitchRowAsync(services, api: false);
        WriteCodexLogin("c@example.test", "acct-3", "refresh-c");
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        var stateBefore = WriteProjectState();
        Directory.CreateDirectory(CodexState + ".ai-account-manager.bak");
        TrackCodexOrder(services);
        _desktop.Installed = true;

        await vm.SwitchAsync(Row(vm, b));

        Assert.Empty(dialogs.Confirms);
        Assert.Empty(dialogs.Warnings);
        Assert.Equal(new[] { "Write", "Start" }, _desktop.Order);
        Assert.True(Row(vm, b).IsCurrent);
        Assert.Equal(stateBefore, File.ReadAllBytes(CodexState));
        Assert.Empty(Directory.GetFiles(CodexHome, "*.tmp"));
        Assert.Contains("共享 Codex 本地项目失败（不影响切换）", File.ReadAllText(services.Log.RunLogPath));
        Assert.Contains("已启动 Codex 桌面版", vm.StatusText);
    }

    // ---- fakes ------------------------------------------------------------------

    private void WriteThreads()
    {
        using var connection = OpenThreads();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE threads (model_provider TEXT); INSERT INTO threads VALUES ('openai'), ('ai-account-manager'), ('api-legacy'), ('foreign')";
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenThreads()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(CodexHome, "state_5.sqlite"), Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private string[] ThreadProviders()
    {
        using var connection = OpenThreads();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT model_provider FROM threads ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var providers = new List<string>();
        while (reader.Read()) { providers.Add(reader.GetString(0)); }
        return providers.ToArray();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodexProviderSwitchRetagsThreadsAfterStoppingAndBeforeStarting(bool api)
    {
        var (services, vm, dialogs) = NewVm();
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        WriteCodexLogin("c@example.test", "acct-3", "refresh-c");
        var apiId = await AddSwitchRowAsync(services, api: true);
        var targetId = api ? apiId : await AddSwitchRowAsync(services, api: false);
        if (!api)
        {
            var provider = (await services.Backend.GetCredentialsAsync()).Single(c => c!["id"]!.GetValue<string>() == apiId)!["integrationID"]!.GetValue<string>();
            services.CodexApi.Use(provider);
        }
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        services.Settings.SharedCodexApis.Add("api-legacy");
        WriteThreads();
        _desktop.Installed = true;
        _desktop.Running = true;
        var target = api ? CodexApiConfig.ProviderId : "openai";
        _desktop.OnStop = () => Assert.Equal(new[] { "openai", CodexApiConfig.ProviderId, "api-legacy", "foreign" }, ThreadProviders());
        _desktop.OnStart = () => Assert.Equal(new[] { target, target, target, "foreign" }, ThreadProviders());

        await vm.SwitchAsync(Row(vm, targetId));

        Assert.Empty(dialogs.Warnings);
        Assert.Equal(1, _desktop.Stops);
        Assert.Equal(1, _desktop.Starts);
        Assert.Equal(new[] { target, target, target, "foreign" }, ThreadProviders());
        Assert.True(File.Exists(Path.Combine(CodexHome, "state_5.sqlite.ai-account-manager.bak")));
        Assert.Contains("已让 2 条 Codex 对话在当前账号/API 下可见", File.ReadAllText(services.Log.RunLogPath));
    }

    [AvaloniaFact]
    public async Task DecliningTheDesktopStopSkipsThreadEditsOnlyOnWindows()
    {
        var (services, vm, dialogs) = NewVm(confirm: false);
        await AddAsync(services, "a@example.test", "acct-1", "refresh-a", activate: true);
        WriteCodexLogin("c@example.test", "acct-3", "refresh-c");
        var apiId = await AddSwitchRowAsync(services, api: true);
        vm.SelectSwitchTargetForTest(SwitchTarget.Codex);
        await vm.ReadDshCommand.ExecuteAsync(null);
        services.Settings.SharedCodexApis.Add("api-legacy");
        WriteThreads();
        _desktop.Running = true;
        _desktop.Installed = true;

        await vm.SwitchAsync(Row(vm, apiId));

        Assert.Single(dialogs.Confirms);
        Assert.Equal(0, _desktop.Stops);
        Assert.Equal(0, _desktop.Starts);
        Assert.Equal(OperatingSystem.IsWindows()
            ? new[] { "openai", CodexApiConfig.ProviderId, "api-legacy", "foreign" }
            : new[] { CodexApiConfig.ProviderId, CodexApiConfig.ProviderId, CodexApiConfig.ProviderId, "foreign" }, ThreadProviders());
        var log = File.ReadAllText(services.Log.RunLogPath);
        Assert.Equal(!OperatingSystem.IsWindows(), log.Contains("条 Codex 对话在当前账号/API 下可见", StringComparison.Ordinal));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuotaResultsUpdatePlanLabelsAndCachedResultsRespectNewerTokens(bool newerToken)
    {
        var (_, vm, _) = NewVm();
        var observed = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var value = Value("a@example.test", "acct-1", "synthetic-refresh");
        value["access"] = Jwt("a@example.test", "acct-1", plan: "self_serve_business_prolite", iat: observed.AddSeconds(newerToken ? 1 : -1).ToUnixTimeSeconds());
        var row = new AccountRowViewModel(new AccountRow
        {
            Kind = AccountKind.Codex, AccountId = "acct-1", Plan = "self_serve_business_prolite", Value = value,
        }, vm);
        var background = row.PlanBackground;
        var foreground = row.PlanForeground;
        var changed = new List<string>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        var result = new AccountQuotaResult
        {
            State = QuotaResultState.Success, CheckedAt = observed, Usage = new QuotaUsage { Plan = "team" },
        };

        var apply = typeof(MainWindowViewModel).GetMethod("ApplyQuota", BindingFlags.Instance | BindingFlags.NonPublic)!;
        apply.Invoke(vm, new object[] { row, result, newerToken });

        Assert.Equal(newerToken ? "self_serve_business_prolite" : "team", row.Row.Plan);
        Assert.Equal(newerToken ? "team 高级" : "team", row.PlanText);
        if (row.PlanText != row.Row.Plan) { Assert.Contains(row.Row.Plan, row.PlanTooltip); } // the raw type shows only when the label differs
        if (!newerToken)
        {
            Assert.Contains(nameof(AccountRowViewModel.PlanText), changed);
            Assert.Contains(nameof(AccountRowViewModel.PlanTooltip), changed);
        }
        Assert.Same(background, row.PlanBackground);
        Assert.Same(foreground, row.PlanForeground);
    }

    private sealed class FakeCodexDesktop : ICodexDesktopApp
    {
        public string Name => "Codex 测试版";
        public bool Running { get; set; }
        public bool Installed { get; set; }
        public bool DaemonRunning { get; set; }
        public bool RefusesStop { get; set; }
        public Action? OnStop { get; set; }
        public Action? OnStart { get; set; }
        public Action? OnRestart { get; set; }
        public List<string> Order { get; } = new();
        public int Stops { get; private set; }
        public int Starts { get; private set; }
        public int Restarts { get; private set; }
        public bool IsRunning() => Running || DaemonRunning;
        public bool IsDesktopRunning() => Running;
        public bool IsInstalled() => Installed;
        public Task<string> StopAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            Order.Add("Stop");
            OnStop?.Invoke();
            if (!RefusesStop) { Running = false; }
            return Task.FromResult("已关闭 Codex 桌面版");
        }
        public Task<string> StartAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            Order.Add("Start");
            OnStart?.Invoke();
            Running = true;
            return Task.FromResult("已启动 Codex 桌面版");
        }
        public Task<string> RestartAsync(CancellationToken cancellationToken = default)
        {
            Restarts++;
            Order.Add("Restart");
            OnRestart?.Invoke();
            return Task.FromResult("已重启 Codex 桌面版");
        }
    }

    private sealed class ScriptedDialogs : IDialogService
    {
        public bool Confirm { get; set; } = true;
        public Action? OnConfirm { get; set; }
        public List<string> Confirms { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Infos { get; } = new();
        public Task<bool> ConfirmAsync(string title, string message, bool dangerous = false) { Confirms.Add(title + "\n" + message); OnConfirm?.Invoke(); return Task.FromResult(Confirm); }
        public Task ShowInfoAsync(string title, string message) { Infos.Add(title + "\n" + message); return Task.CompletedTask; }
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
