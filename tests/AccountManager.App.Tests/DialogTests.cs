using AccountManager.App.Services;
using AccountManager.App.Views;
using AccountManager.App.ViewModels;
using AccountManager.Core.Accounts;
using AccountManager.Core.Actions;
using AccountManager.Core.Models;
using AccountManager.Core.Security;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(AccountManager.App.Tests.TestAppBuilder))]

namespace AccountManager.App.Tests;

public static class TestAppBuilder
{
    // Headless setup has no desktop lifetime: App never creates services, reads credentials,
    // starts auto-refresh, or launches the user's real main window.
    public static AppBuilder BuildAvaloniaApp() => WithProductionFonts(AppBuilder.Configure<AccountManager.App.App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont());

    /// <summary>
    /// The same face selection as the Linux app (Program.WithLinuxSystemFont): Inter for Latin text and an installed
    /// CJK face for Chinese glyphs, so layout tests measure text as the real window does. Best effort.
    /// </summary>
    private static AppBuilder WithProductionFonts(AppBuilder builder)
    {
        try
        {
            var installed = SkiaSharp.SKFontManager.Default.GetFontFamilies();
            var fallbacks = AccountManager.App.Services.UiFontSelector.Fallbacks(installed);
            if (fallbacks.Count > 0)
            {
                return builder.With(new Avalonia.Media.FontManagerOptions
                {
                    DefaultFamilyName = "fonts:Inter#Inter",
                    FontFallbacks = fallbacks.Select(name => new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily(name) }).ToArray(),
                });
            }
        }
        catch
        {
            // Keep the bundled face.
        }

        return builder;
    }
}

public class DialogTests
{
    private const string FakeAuthorizationUrl =
        "https://auth.example.test/oauth/authorize?state=fake-state&code_challenge=fake-challenge";

    [AvaloniaFact]
    public void AddAccountWindowContainsExactlyTheTwoRequestedTabs()
    {
        var dialog = new AddAccountDialog();
        dialog.Show();
        try
        {
            var tabs = dialog.GetVisualDescendants().OfType<TabControl>().Single();
            Assert.Equal(new[] { "OpenAI OAuth", "添加 API" }, tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString()));
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "回调地址（没有自动回调时使用）");
            tabs.SelectedIndex = 1;
            dialog.UpdateLayout();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "供应商名称");
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "API Key");
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task ConfigOnlyEditorAllowsNameAndFirstKeyAndParsesCommaSeparatedModels()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var pending = ApiConfigDialog.ShowAsync(owner, new ApiConfigInput
            {
                ProviderId = "stable-id", DisplayName = "Original", Title = "Original",
                BaseUrl = "https://example.test", Models = new[] { "m" }, HasCredential = false,
            });
            var dialog = owner.OwnedWindows.OfType<ApiConfigDialog>().Single();
            Assert.True(dialog.FindControl<TextBox>("KeyBox")!.IsEnabled);
            Assert.Equal("Original", dialog.FindControl<ComboBox>("NameBox")!.Text);
            dialog.FindControl<ComboBox>("NameBox")!.Text = "Renamed";
            dialog.FindControl<TextBox>("ModelsBox")!.Text = "a,b\nc,a";
            dialog.FindControl<TextBox>("KeyBox")!.Text = "fake-key";
            Click(dialog, "保存");
            var result = await pending;
            Assert.NotNull(result);
            Assert.Equal("Renamed", result.DisplayName);
            Assert.Equal(new[] { "a", "b", "c" }, result.Models);
            Assert.Equal("fake-key", result.NewKey);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task InvalidApiInputDoesNotCloseEditorOrLoseForm()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var pending = ApiConfigDialog.ShowAsync(owner, new ApiConfigInput
            {
                ProviderId = "id", DisplayName = "Name", BaseUrl = "https://example.test", Models = new[] { "m" }, HasCredential = true,
            });
            var dialog = owner.OwnedWindows.OfType<ApiConfigDialog>().Single();
            dialog.FindControl<TextBox>("BaseUrlBox")!.Text = "http://example.test";
            Click(dialog, "保存");
            Assert.False(pending.IsCompleted);
            Assert.Contains("HTTPS", dialog.FindControl<TextBlock>("ErrorText")!.Text);
            Assert.Equal("http://example.test", dialog.FindControl<TextBox>("BaseUrlBox")!.Text);
            Click(dialog, "取消");
            Assert.Null(await pending);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ApiEditorDisplaysAndPreservesBothExistingProtocolsWithBlankKey()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            foreach (var useResponses in new[] { false, true })
            {
                var pending = ApiConfigDialog.ShowAsync(owner, new ApiConfigInput
                {
                    ProviderId = "stable-id", DisplayName = "示例供应商", Title = "示例供应商",
                    BaseUrl = "https://example.test/v1", Models = new[] { "model-a", "model-b" },
                    HasCredential = true, UseResponses = useResponses,
                });
                var dialog = owner.OwnedWindows.OfType<ApiConfigDialog>().Single();
                Assert.Equal(useResponses, dialog.FindControl<RadioButton>("ResponsesRadio")!.IsChecked);
                Assert.Equal(!useResponses, dialog.FindControl<RadioButton>("ChatRadio")!.IsChecked);
                Assert.True(string.IsNullOrEmpty(dialog.FindControl<TextBox>("KeyBox")!.Text));
                dialog.UpdateLayout();
                CapturePreview(dialog, useResponses ? "api-config-responses.png" : "api-config-chat.png");
                Click(dialog, "保存");
                var result = await pending;
                Assert.NotNull(result);
                Assert.Equal(useResponses, result.UseResponses);
                Assert.True(string.IsNullOrEmpty(result.NewKey));
                Assert.Equal("示例供应商", result.DisplayName);
                Assert.Equal("https://example.test/v1", result.BaseUrl);
                Assert.Equal(new[] { "model-a", "model-b" }, result.Models);
            }
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ApiEditorCanSwitchProtocolInEitherDirectionAndCancelDiscardsSelection()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            foreach (var useResponses in new[] { false, true })
            {
                var input = new ApiConfigInput
                {
                    ProviderId = "stable-id", DisplayName = "Name", BaseUrl = "https://example.test", Models = new[] { "m" },
                    HasCredential = true, UseResponses = useResponses,
                };
                var pending = ApiConfigDialog.ShowAsync(owner, input);
                var dialog = owner.OwnedWindows.OfType<ApiConfigDialog>().Single();
                dialog.FindControl<RadioButton>("ResponsesRadio")!.IsChecked = !useResponses;
                Click(dialog, "保存");
                var result = await pending;
                Assert.NotNull(result);
                Assert.Equal(!useResponses, result.UseResponses);
                Assert.True(string.IsNullOrEmpty(result.NewKey));

                pending = ApiConfigDialog.ShowAsync(owner, input);
                dialog = owner.OwnedWindows.OfType<ApiConfigDialog>().Single();
                dialog.FindControl<RadioButton>("ResponsesRadio")!.IsChecked = !useResponses;
                Click(dialog, "取消");
                Assert.Null(await pending);
            }
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task AddApiViewModelValidatesAndPreventsDoubleSubmitAndUsesStableId()
    {
        var services = new FakeAddServices();
        await using var vm = new AddAccountViewModel(services, new DialogService(() => null));
        vm.SelectedTabIndex = 1;
        await vm.SaveApiCommand.ExecuteAsync(null);
        Assert.Contains("供应商名称", vm.ErrorText);
        Assert.Equal(0, services.Writes);
        vm.DisplayName = "Test API";
        vm.BaseUrl = "https://example.test/v1";
        vm.ModelsText = "a,b\na";
        vm.ApiKey = "fake-secret";
        vm.UseResponses = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        services.WaitBeforeSave = release.Task;
        var first = vm.SaveApiCommand.ExecuteAsync(null);
        Assert.True(vm.IsSaving);
        Assert.False(vm.CanOperate);
        await vm.SaveApiCommand.ExecuteAsync(null);
        Assert.Equal(1, services.Writes);
        release.SetResult();
        await first;
        Assert.NotNull(vm.Result);
        Assert.StartsWith("api-", vm.Result.ProviderID);
        Assert.True(services.UseResponses);
        Assert.Equal(new[] { "a", "b" }, services.Models);
    }

    [AvaloniaFact]
    public async Task RenderAddAccountBothTabsWithoutAnyRealAccountsOrBrowser()
    {
        await using var vm = new AddAccountViewModel(new FakeAddServices(), new DialogService(() => null), _ => throw new InvalidOperationException("No browser in tests"))
        {
            DisplayName = "示例供应商", BaseUrl = "https://api.example.com/v1", ModelsText = "model-a\nmodel-b", ApiKey = "fake-preview-key",
        };
        var dialog = new AddAccountDialog { DataContext = vm };
        dialog.Show();
        try
        {
            dialog.UpdateLayout();
            CapturePreview(dialog, "add-account-oauth.png");
            vm.SelectedTabIndex = 1;
            dialog.UpdateLayout();
            CapturePreview(dialog, "add-account-api.png");
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("fake-preview-key") == true);
        }
        finally { dialog.Close(); }
    }

    // ── Task 5: the browser opener is awaitable, keeps the pending OAuth session, and never
    //    leaks the authorization URL. These use a synthetic session seam and never touch a real
    //    loopback listener, token endpoint, or browser. ──────────────────────────────────────

    [AvaloniaFact]
    public async Task FailedBrowserOpenKeepsThePendingAuthorizationAndNeverLeaksTheUrl()
    {
        var session = new FakeBrowserOAuthSession(FakeAuthorizationUrl);
        var services = new FakeAddServices { OAuthAvailable = true, PendingSession = session };
        await using var vm = new AddAccountViewModel(services, new DialogService(() => null),
            openAsync: (_, _) => throw new InvalidOperationException("xdg-open failed: " + FakeAuthorizationUrl));

        await vm.OpenBrowserCommand.ExecuteAsync(null);

        Assert.Equal(FakeAuthorizationUrl, vm.AuthorizationUrl);
        Assert.True(vm.WaitingForAuthorization);
        Assert.False(session.Disposed);
        Assert.DoesNotContain(FakeAuthorizationUrl, vm.ErrorText);
        Assert.DoesNotContain("state=fake-state", vm.ErrorText);
        Assert.DoesNotContain("xdg-open failed", vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task UserCancellingTheBrowserOpenDoesNotClearTheAuthorizationSession()
    {
        var session = new FakeBrowserOAuthSession(FakeAuthorizationUrl);
        var services = new FakeAddServices { OAuthAvailable = true, PendingSession = session };
        await using var vm = new AddAccountViewModel(services, new DialogService(() => null),
            openAsync: (_, _) => throw new OperationCanceledException());

        await vm.OpenBrowserCommand.ExecuteAsync(null);

        Assert.Equal(FakeAuthorizationUrl, vm.AuthorizationUrl);
        Assert.True(vm.WaitingForAuthorization);
        Assert.False(session.Disposed);
    }

    [AvaloniaFact]
    public async Task TheExistingOpenBrowserActionIsStillInvokedThroughTheAsyncWrapper()
    {
        string? opened = null;
        var session = new FakeBrowserOAuthSession(FakeAuthorizationUrl);
        var services = new FakeAddServices { OAuthAvailable = true, PendingSession = session };
        await using var vm = new AddAccountViewModel(services, new DialogService(() => null),
            openBrowser: url => opened = url);

        await vm.OpenBrowserCommand.ExecuteAsync(null);

        Assert.Equal(FakeAuthorizationUrl, opened);
        Assert.Equal(FakeAuthorizationUrl, vm.AuthorizationUrl);
        Assert.False(session.Disposed);
    }

    [AvaloniaFact]
    public async Task OpenBrowserAwaitsTheInjectedAsyncOpener()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeBrowserOAuthSession(FakeAuthorizationUrl);
        var services = new FakeAddServices { OAuthAvailable = true, PendingSession = session };
        await using var vm = new AddAccountViewModel(services, new DialogService(() => null),
            openAsync: (_, _) => gate.Task);

        var run = vm.OpenBrowserCommand.ExecuteAsync(null);

        Assert.False(run.IsCompleted);
        gate.SetResult();
        await run;
        Assert.Equal(FakeAuthorizationUrl, vm.AuthorizationUrl);
    }

    [AvaloniaFact]
    public async Task FailedManualCallbackKeepsThePendingAuthorization()
    {
        var session = new FakeBrowserOAuthSession(FakeAuthorizationUrl);
        var services = new FakeAddServices { OAuthAvailable = true, PendingSession = session };
        await using var vm = new AddAccountViewModel(services, new DialogService(() => null));

        await vm.PrimeAuthorizationAsync();
        vm.CallbackUrl = "http://localhost:1455/auth/callback?code=fake&state=fake-state";
        await vm.SubmitCallbackCommand.ExecuteAsync(null);

        Assert.Equal(FakeAuthorizationUrl, vm.AuthorizationUrl);
        Assert.False(session.Disposed);
    }

    [AvaloniaFact]
    public async Task SettingsDialogExposesWebDavSyncFieldsAndReturnsThem()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var pending = SettingsDialog.ShowAsync(owner, new SettingsInput
            {
                DshHome = string.Empty,
                DshProfile = string.Empty,
                AutoRefreshMinutes = 5,
                AutoSwitchAccount = false,
                WebDavUrl = "https://dav.example.com/dav/",
                WebDavUsername = "alice@example.com",
                WebDavPasswordStored = true,
            });
            var dialog = owner.OwnedWindows.OfType<SettingsDialog>().Single();
            dialog.UpdateLayout();
            Assert.Equal("https://dav.example.com/dav/", dialog.FindControl<TextBox>("WebDavUrlBox")!.Text);
            Assert.Equal("alice@example.com", dialog.FindControl<TextBox>("WebDavUserBox")!.Text);
            Assert.Null(dialog.FindControl<TextBox>("WebDavPathBox"));
            Assert.True(string.IsNullOrEmpty(dialog.FindControl<TextBox>("WebDavPassBox")!.Text));
            // Sync actions live inside the settings dialog now.
            Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "上传到云端");
            Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "从云端下载");

            dialog.FindControl<TextBox>("WebDavPassBox")!.Text = "app-pass";
            Click(dialog, "保存");
            var result = await pending;
            Assert.NotNull(result);
            Assert.Equal("https://dav.example.com/dav/", result!.WebDavUrl);
            Assert.Equal("alice@example.com", result.WebDavUsername);
            Assert.Equal("app-pass", result.WebDavPassword);
            Assert.Equal(SettingsAction.None, result.Action);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task SettingsDialogShowsAndReturnsTheCodexAndOpenCodeRows()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var pending = SettingsDialog.ShowAsync(owner, new SettingsInput { CodexHome = "/tmp/codex-x", OpenCodeCliPath = "/opt/oc/opencode" });
            var dialog = owner.OwnedWindows.OfType<SettingsDialog>().Single();
            dialog.UpdateLayout();
            Assert.Equal("/tmp/codex-x", dialog.FindControl<TextBox>("CodexHomeBox")!.Text);
            Assert.Equal("/opt/oc/opencode", dialog.FindControl<TextBox>("OpenCodeCliBox")!.Text);
            Assert.Null(dialog.FindControl<CheckBox>("CliSwitchBox")); // the toolbar's switch target replaced the old tick
            Assert.NotNull(ToolTip.GetTip(dialog.FindControl<TextBox>("CodexHomeBox")!));
            Assert.NotNull(ToolTip.GetTip(dialog.FindControl<TextBox>("OpenCodeCliBox")!));
            Assert.Equal(3, dialog.GetVisualDescendants().OfType<Button>().Count(b => b.Content?.ToString() == "浏览…"));
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "OpenCode 命令路径");
            dialog.FindControl<TextBox>("CodexHomeBox")!.Text = " /tmp/other ";
            dialog.FindControl<TextBox>("OpenCodeCliBox")!.Text = "  ";
            Click(dialog, "保存");
            var result = await pending;
            Assert.NotNull(result);
            Assert.Equal("/tmp/other", result!.CodexHome);
            Assert.Equal(string.Empty, result.OpenCodeCliPath);
        }
        finally { owner.Close(); }
    }

    [Fact]
    public void SettingsDefaultToTheDshSwitchTargetAndAnOldFileWithTheLegacyTickStillLoads()
    {
        var settings = new AppSettings();
        Assert.Equal(AccountManager.Core.Accounts.SwitchTarget.Dsh, settings.SwitchTarget);
        Assert.Null(settings.CodexHome);
        Assert.Null(settings.OpenCodeCliPath);
        Assert.Equal(AccountManager.Core.Accounts.SwitchTarget.Dsh, System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"DshHome\":\"x\"}")!.SwitchTarget);

        var path = Path.Combine(Path.GetTempPath(), "setOld-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(path, "{\"DshHome\":\"/h\",\"CodexHome\":\"/c\",\"SwitchCodexCliToo\":true,\"AutoRefreshMinutes\":7}");
            var store = new SettingsStore(path);
            var loaded = store.Load();
            Assert.Equal("/h", loaded.DshHome);
            Assert.Equal("/c", loaded.CodexHome);
            Assert.Equal(7, loaded.AutoRefreshMinutes);
            Assert.Equal(AccountManager.Core.Accounts.SwitchTarget.Dsh, loaded.SwitchTarget);
            store.Save(loaded);
            Assert.DoesNotContain("SwitchCodexCliToo", File.ReadAllText(path)); // the legacy flag is dropped on the next save
            Assert.Contains("\"SwitchTarget\": \"Dsh\"", File.ReadAllText(path));

            loaded.SwitchTarget = AccountManager.Core.Accounts.SwitchTarget.OpenCode;
            store.Save(loaded);
            Assert.Equal(AccountManager.Core.Accounts.SwitchTarget.OpenCode, store.Load().SwitchTarget);
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    [AvaloniaTheory]
    [InlineData("上传到云端", SettingsAction.Upload)]
    [InlineData("从云端下载", SettingsAction.Download)]
    public async Task SettingsDialogSyncButtons_ReturnTheChosenAction(string button, SettingsAction expected)
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var pending = SettingsDialog.ShowAsync(owner, new SettingsInput
            {
                WebDavUrl = "https://dav.example.com/dav/",
                WebDavUsername = "alice@example.com",
                WebDavPasswordStored = true,
            });
            var dialog = owner.OwnedWindows.OfType<SettingsDialog>().Single();
            dialog.UpdateLayout();

            Click(dialog, button);
            var result = await pending;
            Assert.NotNull(result);
            Assert.Equal(expected, result!.Action);
        }
        finally { owner.Close(); }
    }

    // ── Provider templates: the editable name dropdown, the hint line and the Fast tick ──────────

    private static async Task<(Window Owner, ApiConfigDialog Dialog, Task<ApiConfigResult?> Pending)> OpenEditorAsync(ApiConfigInput input)
    {
        var owner = new Window();
        owner.Show();
        var pending = ApiConfigDialog.ShowAsync(owner, input);
        await Task.Yield();
        return (owner, owner.OwnedWindows.OfType<ApiConfigDialog>().Single(), pending);
    }

    [AvaloniaFact]
    public async Task EditorShowsTheStoredAddressUntouchedAndPickingATemplateFillsOnlyEmptyFields()
    {
        var (owner, dialog, pending) = await OpenEditorAsync(new ApiConfigInput
        {
            ProviderId = "id", DisplayName = "Mine", BaseUrl = "https://gw.example.test/v1", Models = new[] { "m" }, HasCredential = true,
        });
        try
        {
            var name = dialog.FindControl<ComboBox>("NameBox")!;
            var url = dialog.FindControl<TextBox>("BaseUrlBox")!;
            var models = dialog.FindControl<TextBox>("ModelsBox")!;
            Assert.Equal(new[] { "OpenAI", "DeepSeek", "火山 Coding Plan", "火山 Agent Plan" }, name.Items.OfType<string>());
            Assert.Equal("Mine", name.Text);
            Assert.Equal("https://gw.example.test/v1", url.Text);
            Assert.Null(name.SelectedItem); // nothing is applied until a template is picked

            name.SelectedItem = "火山 Coding Plan";
            Assert.Equal("火山 Coding Plan", name.Text);
            Assert.Equal("https://gw.example.test/v1", url.Text); // non-empty: left alone
            Assert.Equal("m", models.Text);
            Assert.False(dialog.FindControl<RadioButton>("ResponsesRadio")!.IsChecked); // protocol only follows a filled-in URL

            url.Text = "";
            models.Text = "";
            name.SelectedItem = null;
            name.SelectedItem = "OpenAI";
            Assert.Equal("https://api.openai.com/v1", url.Text);
            Assert.Equal("gpt-6-astra", models.Text!.Split('\n', '\r').First(l => l.Length > 0));
            Assert.True(dialog.FindControl<RadioButton>("ResponsesRadio")!.IsChecked);
            Assert.Contains("xhigh", dialog.FindControl<TextBlock>("AutoConfigText")!.Text);
            Assert.True(dialog.FindControl<CheckBox>("FastBox")!.IsVisible);
        }
        finally { dialog.Close(); owner.Close(); }
        _ = await pending;
    }

    [AvaloniaFact]
    public async Task TypingACustomProviderNameStillWorks()
    {
        var (owner, dialog, pending) = await OpenEditorAsync(new ApiConfigInput
        {
            ProviderId = "id", DisplayName = "Mine", BaseUrl = "https://gw.example.test/v1", Models = new[] { "m" }, HasCredential = true,
        });
        try
        {
            dialog.FindControl<ComboBox>("NameBox")!.Text = "我的网关";
            Click(dialog, "保存");
            var result = await pending;
            Assert.Equal("我的网关", result!.DisplayName);
            Assert.False(result.Fast);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task TypingOnlyPartOfATemplateNameAppliesNothing()
    {
        var (owner, dialog, pending) = await OpenEditorAsync(new ApiConfigInput
        {
            ProviderId = "id", DisplayName = "Mine", BaseUrl = "https://gw.example.test/v1", Models = new[] { "m" }, HasCredential = true,
        });
        try
        {
            dialog.FindControl<TextBox>("BaseUrlBox")!.Text = "";
            // (Avalonia selects the item when the typed text equals it exactly, which counts as picking it.)
            dialog.FindControl<ComboBox>("NameBox")!.Text = "OpenA";
            dialog.UpdateLayout();
            Assert.Equal(string.Empty, dialog.FindControl<TextBox>("BaseUrlBox")!.Text);
            Assert.Equal("m", dialog.FindControl<TextBox>("ModelsBox")!.Text);
        }
        finally { dialog.Close(); owner.Close(); }
        _ = await pending;
    }

    [AvaloniaFact]
    public async Task FastTickIsOfferedOnlyForTheOfficialOpenAiEndpointAndStartsFromTheStoredTwins()
    {
        var (owner, dialog, pending) = await OpenEditorAsync(new ApiConfigInput
        {
            ProviderId = "id", DisplayName = "OpenAI", BaseUrl = "https://api.openai.com/v1", Models = new[] { "gpt-6-astra" },
            UseResponses = true, HasCredential = true, HasFast = true, FastTwinIds = new[] { "gpt-6-astra-fast" },
        });
        try
        {
            var fast = dialog.FindControl<CheckBox>("FastBox")!;
            Assert.True(fast.IsVisible);
            Assert.True(fast.IsChecked);
            dialog.FindControl<TextBox>("BaseUrlBox")!.Text = "https://gw.example.test/v1";
            Assert.False(fast.IsVisible);
            Assert.Equal(string.Empty, dialog.FindControl<TextBlock>("AutoConfigText")!.Text);
            dialog.FindControl<TextBox>("BaseUrlBox")!.Text = "https://api.openai.com/v1";
            dialog.FindControl<RadioButton>("ChatRadio")!.IsChecked = true;
            Assert.False(fast.IsVisible);
            dialog.FindControl<RadioButton>("ResponsesRadio")!.IsChecked = true;
            Assert.True(fast.IsVisible);
            Click(dialog, "保存");
            var result = await pending;
            Assert.True(result!.Fast);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task AddingFromATemplateFillsTheFormAndPassesTheFastTick()
    {
        var services = new FakeAddServices();
        await using var vm = new AddAccountViewModel(services, new DialogService(() => null), _ => throw new InvalidOperationException("No browser in tests"));
        var dialog = new AddAccountDialog { DataContext = vm };
        dialog.Show();
        try
        {
            vm.SelectedTabIndex = 1;
            dialog.UpdateLayout();
            Assert.False(vm.ShowFastOption);
            var combo = dialog.GetVisualDescendants().OfType<ComboBox>().Single();
            Assert.True(combo.IsEditable);

            combo.SelectedItem = "OpenAI";
            dialog.UpdateLayout();
            Assert.Equal("OpenAI", vm.DisplayName);
            Assert.Equal("https://api.openai.com/v1", vm.BaseUrl);
            Assert.StartsWith("gpt-6-astra", vm.ModelsText);
            Assert.True(vm.UseResponses);
            Assert.Equal(string.Empty, vm.ApiKey); // the key is never touched
            Assert.True(vm.ShowFastOption);
            Assert.True(vm.UseFast);
            Assert.Contains("max", vm.AutoConfigHint);
            Assert.Contains(dialog.GetVisualDescendants().OfType<CheckBox>(), c => c.IsVisible && c.Content?.ToString()?.StartsWith("附带 Fast 模型") == true);

            vm.ApiKey = "fake-key";
            await vm.SaveApiCommand.ExecuteAsync(null);
            Assert.True(services.Fast);
            Assert.Equal(1, services.Writes);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task PickingATemplateInTheAddFormKeepsWhatWasAlreadyTyped()
    {
        await using var vm = new AddAccountViewModel(new FakeAddServices(), new DialogService(() => null), _ => throw new InvalidOperationException("No browser in tests"))
        {
            BaseUrl = "https://gw.example.test/v1", ModelsText = "mine", UseResponses = false,
        };
        vm.SelectedTemplate = "火山 Agent Plan";
        Assert.Equal("火山 Agent Plan", vm.DisplayName);
        Assert.Equal("https://gw.example.test/v1", vm.BaseUrl);
        Assert.Equal("mine", vm.ModelsText);
        Assert.False(vm.UseResponses);
        Assert.False(vm.ShowFastOption);
    }

    private static void CapturePreview(Window dialog, string name)
    {
        var directory = Environment.GetEnvironmentVariable("ACCOUNT_MANAGER_UI_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) { return; }
        Directory.CreateDirectory(directory);
        using var bitmap = dialog.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        bitmap.Save(Path.Combine(directory, name));
    }

    private sealed class FakeAddServices : IAddAccountServices
    {
        public SecretRegistry Secrets { get; } = new(false);
        public int Writes;
        public bool UseResponses;
        public bool Fast;
        public IReadOnlyList<string> Models = Array.Empty<string>();
        public Task? WaitBeforeSave;

        /// <summary>Set true to let the synthetic OAuth session be created in Task 5 tests.</summary>
        public bool OAuthAvailable;

        /// <summary>Synthetic in-flight session for the Task 5 seam; never a real loopback listener.</summary>
        public IBrowserOAuthSession? PendingSession;

        /// <summary>Retained production API; the fake must never start a real OAuth service.</summary>
        public CodexOAuthService CreateOAuthService()
            => throw new InvalidOperationException("No real OAuth in UI tests");

        public IBrowserOAuthSession StartBrowserOAuthSession()
            => PendingSession ?? throw new InvalidOperationException("No real OAuth in UI tests");

        public Task<CodexAddPreview> PreviewCodexAuthorizationAsync(JsonObject account) => throw new InvalidOperationException();
        public Task<AccountAddResult> SaveCodexAuthorizationAsync(CodexAddPreview preview, bool replaceExisting) => throw new InvalidOperationException();
        public async Task<AccountAddResult> CreateApiProviderAsync(string id, string name, string url, IReadOnlyList<string> models, string key, bool useResponses, bool fast)
        {
            Writes++;
            Models = models;
            UseResponses = useResponses;
            Fast = fast;
            if (WaitBeforeSave is not null) { await WaitBeforeSave; }
            return new AccountAddResult { Saved = true, ProviderID = id, Label = name };
        }
    }

    /// <summary>
    /// Minimal synthetic pending authorization. The callback always fails and the completion never
    /// resolves, so the view model keeps the session alive without any real network or listener.
    /// </summary>
    private sealed class FakeBrowserOAuthSession : IBrowserOAuthSession
    {
        private readonly TaskCompletionSource<JsonObject> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeBrowserOAuthSession(string authorizationUrl) => AuthorizationUrl = authorizationUrl;

        public string AuthorizationUrl { get; }

        public bool Disposed { get; private set; }

        public Task<JsonObject> Completion => _completion.Task;

        public Task SubmitCallbackAsync(string callbackUrl)
            => Task.FromException(new InvalidOperationException("callback rejected in tests"));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _completion.TrySetException(new OperationCanceledException());
            return ValueTask.CompletedTask;
        }
    }

    private static void Click(Window dialog, string content)
        => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == content)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
