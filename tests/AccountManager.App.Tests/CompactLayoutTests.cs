using AccountManager.App.Services;
using AccountManager.App.ViewModels;
using AccountManager.App.Views;
using AccountManager.Core.Accounts;
using AccountManager.Core.Actions;
using AccountManager.Core.Dsh;
using AccountManager.Core.Logging;
using AccountManager.Core.Models;
using AccountManager.Core.Quota;
using AccountManager.Core.Security;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AccountManager.App.Tests;

/// <summary>
/// The compact layout must never draw text over other text or cut it off: the main window at its design minimum
/// (also the way it is zoomed on a small, high-scale laptop screen) and every dialog inside a small work area.
/// Data is synthetic and never touches a real dsh home or vault.
/// </summary>
public sealed class CompactLayoutTests : IDisposable
{
    private const string LongEmail = "firstname.lastname.with.a.very.long.mailbox.address@example-company-domain.test";
    private const string LongTeamEmail = "team.member.number.two+workspace@big-enterprise-with-a-long-name.example.test";
    private const string LongProvider = "火山引擎 Coding Plan（企业版超长供应商名称用于测试布局，还要更长一些）";
    private const string LongGateway = "Another Gateway With A Very Long Display Name For Layout Testing";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshL-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private AppServices Services()
        => new(new AppSettings { DshHome = Path.Combine(_root, "dsh"), DshProfile = "desktop" }, Path.Combine(_root, "app"),
            new AppLog(new SecretRegistry(), Path.Combine(_root, "logs")), Path.Combine(_root, "backups"));

    // ---- synthetic rows -------------------------------------------------------------------

    private static QuotaCellViewModel Bar(double remaining, string reset)
        => new() { HasBar = true, Percent = remaining, Text = Math.Round(remaining) + "%", Level = QuotaCellViewModel.LevelForRemaining(remaining), Tooltip = "t", ResetText = reset };

    private static AccountRowViewModel Codex(MainWindowViewModel vm, string id, string email, string plan, double fiveHour, double week, bool active = false)
        => new(new AccountRow
        {
            Key = id, CredentialId = id, Kind = AccountKind.Codex, Label = email, Identity = email, Email = email, Plan = plan,
            AccountId = "acct-" + id, Active = active, Status = "就绪", StatusCode = "Ready", SubscriptionExpires = DateTimeOffset.UtcNow.AddDays(28),
        }, vm)
        {
            Quota5h = Bar(fiveHour, "1h8m · 10-20 18:57"),
            QuotaWk = Bar(week, "3d4h · 10-23 09:30"),
            ResetCreditsText = "重置",
        };

    private static AccountRowViewModel ApiRow(MainWindowViewModel vm, string id, string name)
        => new(new AccountRow
        {
            Key = id, Kind = AccountKind.Api, Label = name, Provider = "cockpit-" + id, Plan = "API Key", Status = "仅配置", StatusCode = "ConfiguredOnly",
            BaseUrl = "https://gw.example.test/v1", Models = new List<string> { "a", "b" },
        }, vm);

    private static void Fill(MainWindowViewModel vm)
    {
        vm.Accounts.Add(Codex(vm, "c1", LongEmail, "plus", 86, 62, active: true));
        vm.Accounts.Add(Codex(vm, "c2", LongTeamEmail, "team", 34, 12));
        vm.Accounts.Add(Codex(vm, "c3", "pro@example.test", "pro", 8, 41));
        vm.Accounts.Add(Codex(vm, "c4", "free.user@example.test", "free", 100, 100));
        vm.Accounts.Add(Codex(vm, "c5", "another.quite.long.address.for.layout.testing@subdomain.example.test", "enterprise-business", 55, 3));
        vm.Accounts.Add(ApiRow(vm, "a1", LongProvider));
        vm.Accounts.Add(ApiRow(vm, "a2", LongGateway));
        vm.Accounts.Add(ApiRow(vm, "a3", "短名"));
        vm.SummaryText = "共 8 项（Codex 5 · API 3） · 供应商 3";
        vm.CurrentText = "当前账号：" + LongEmail;
        vm.StatusText = "额度查询完成：5/5 成功。";
        vm.ReferenceWarning = "dsh 设置中有 12 处引用了不存在的供应商";
        vm.HasReferenceWarning = true;
        vm.ProviderWarning = "有 2 个供应商缺少可用的 Key，已在列表中标出，请检查后再使用。";
    }

    private (MainWindow Window, MainWindowViewModel Vm) OpenMain(double width, double height, double zoom)
    {
        var vm = new MainWindowViewModel(Services(), new NoDialogs(), startBackgroundWork: false);
        Fill(vm);
        var window = new MainWindow { Width = width, Height = height, MinWidth = 100, MinHeight = 100 };
        window.FindControl<LayoutTransformControl>("ZoomHost")!.LayoutTransform = zoom < 1 ? new ScaleTransform(zoom, zoom) : null;
        window.DataContext = vm;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, vm);
    }

    // ---- main window -------------------------------------------------------------------------

    [AvaloniaFact]
    public void MainWindowAtItsDesignMinimumHasNoOverlappingOrCutOffText()
    {
        var (window, _) = OpenMain(MainWindow.DesignMinWidth, MainWindow.DesignMinHeight, 1.0);
        try { LayoutProbe.AssertClean(window); }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.75)]
    public void MainWindowZoomedForASmallHighScaleScreenHasNoOverlappingOrCutOffText(double scaling)
    {
        // The user's laptop: a 1368x768 work area. The window is the fitted size and the content is zoomed.
        var fitted = DialogWindowBounds.FitMainBounds(1368, 768, scaling, MainWindow.DesignMinWidth, MainWindow.DesignMinHeight,
            new AppSettings().WindowWidth, new AppSettings().WindowHeight);
        var (window, _) = OpenMain(fitted.MinWidth, fitted.MinHeight, fitted.Zoom);
        try
        {
            LayoutProbe.AssertClean(window);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MainWindowWithEveryPanelOpenAtTheDefaultSizeHasNoOverlappingOrCutOffText()
    {
        var settings = new AppSettings();
        var (window, vm) = OpenMain(settings.WindowWidth, settings.WindowHeight, 1.0);
        try
        {
            vm.HasUpdate = true;
            vm.UpdateStatusText = "发现新版本 v0.1.4";
            vm.ImportPanelVisible = true;
            vm.ImportSummary = "预览 3 条；选择要导入的账号后点击“确认导入”。";
            foreach (var (id, email) in new[] { ("i1", LongEmail), ("i2", "brand.new@example.test") })
            {
                var row = Codex(vm, id, email, "plus", 50, 50);
                vm.ImportRows.Add(new AccountRowViewModel(new AccountRow { Key = id, Kind = AccountKind.Codex, Label = email, Plan = "plus", Status = "可导入（未联网验证，需要核对一下）", StatusCode = "Import" }, vm) { IsImportSelected = true });
                _ = row;
            }

            for (var i = 0; i < 12; i++) { vm.LogLines.Add("2026-10-20 18:0" + (i % 10) + ":00 [INFO] 示例日志行 " + i + "：已读取 dsh 账号并更新列表"); }
            vm.LogExpanded = true;
            vm.HasUndo = true;
            vm.UndoText = "已删除供应商“" + LongProvider + "”";
            Dispatcher.UIThread.RunJobs();

            LayoutProbe.AssertClean(window);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.75)]
    public void LongEmailsAndProviderNamesAreTrimmedInsideTheirOwnColumn(double scaling)
    {
        var fitted = DialogWindowBounds.FitMainBounds(1368, 768, scaling, MainWindow.DesignMinWidth, MainWindow.DesignMinHeight,
            MainWindow.DesignMinWidth, MainWindow.DesignMinHeight);
        var (window, vm) = OpenMain(fitted.MinWidth, fitted.MinHeight, fitted.Zoom);
        try
        {
            window.UpdateLayout();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
            foreach (var title in new[] { LongEmail, LongTeamEmail, LongProvider, LongGateway })
            {
                var block = Assert.Single(texts, t => t.Text == title);
                // It is genuinely longer than its column, so the ellipsis and the tooltip are what keep it tidy.
                Assert.Equal(TextTrimming.CharacterEllipsis, block.TextTrimming);
                Assert.Equal(title, ToolTip.GetTip(block));
                var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
                var full = new FormattedText(title, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, block.FontSize, Brushes.White).Width;
                Assert.True(full > block.Bounds.Width, "the test data must be longer than its column: " + title);

                // Nothing else of the same row starts before the end of the title.
                var rect = LayoutProbe.ToWindow(block, window, new Rect(block.Bounds.Size));
                var row = block.FindAncestorOfType<Grid>(includeSelf: false)!.FindAncestorOfType<Grid>()!;
                foreach (var other in row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !ReferenceEquals(t, block) && !string.IsNullOrEmpty(t.Text)))
                {
                    var otherRect = LayoutProbe.ToWindow(other, window, new Rect(other.Bounds.Size));
                    if (otherRect.Y < rect.Bottom && otherRect.Bottom > rect.Y && otherRect.X >= rect.X - 0.5 && !otherRect.Intersects(rect))
                    {
                        Assert.True(otherRect.X >= rect.Right - 0.5, $"\"{other.Text}\" starts inside the title \"{title}\"");
                    }
                }
            }

            LayoutProbe.AssertClean(window);
            Assert.Equal(8, vm.Accounts.Count);
        }
        finally { window.Close(); }
    }

    // ---- dialogs -------------------------------------------------------------------------------

    private static ModelReference Ref(string entry, string provider, string model, bool list, bool editable = true)
        => new()
        {
            EntryId = entry, Label = ReferenceScanner.LabelFor(entry), Provider = provider, Model = model, Dangling = true,
            IsListItem = list, Editable = editable, Removable = editable && list, Path = editable ? new[] { "0", "config" } : Array.Empty<string>(),
        };

    private static readonly ProviderChoice[] Choices =
    {
        new("api-1", LongProvider, new[] { "deepseek-v4.1-flash", "deepseek-v4-pro", "model-12-with-a-very-long-identifier-0123456789" }),
        new("api-2", LongGateway, new[] { "gw-a", "gw-b" }),
    };

    /// <summary>The limits <see cref="DialogWindowBounds.Fit"/> applies on a 1368x768 work area at the given display scale.</summary>
    private static void ApplyLimits(Window dialog, double scaling, double minWidth = DialogWindowBounds.DefaultMinWidth, double minHeight = DialogWindowBounds.DefaultMinHeight)
    {
        var fit = DialogWindowBounds.ComputeDialogBounds(1368, 768, scaling, dialog.Width, dialog.Height, minWidth, minHeight);
        dialog.MinWidth = fit.MinWidth;
        dialog.MinHeight = fit.MinHeight;
        dialog.MaxWidth = fit.MaxWidth;
        dialog.MaxHeight = fit.MaxHeight;
        dialog.Width = fit.Width;
        dialog.Height = fit.Height;
    }

    private static async Task CheckDialog<T>(Window owner, double scaling, Func<Task> start, double minWidth = DialogWindowBounds.DefaultMinWidth,
        double minHeight = DialogWindowBounds.DefaultMinHeight) where T : Window
    {
        var pending = start();
        var dialog = owner.OwnedWindows.OfType<T>().Single();
        ApplyLimits(dialog, scaling, minWidth, minHeight);
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();

        var limits = DialogWindowBounds.ComputeDialogBounds(1368, 768, scaling, double.NaN, double.NaN);
        Assert.True(dialog.Bounds.Width <= limits.MaxWidth + 0.5, typeof(T).Name + " is wider than the work area");
        Assert.True(dialog.Bounds.Height <= limits.MaxHeight + 0.5, typeof(T).Name + " is taller than the work area");
        LayoutProbe.AssertClean(dialog);
        // The action buttons stay inside the window.
        foreach (var button in dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Content is string && !b.GetVisualAncestors().OfType<ScrollViewer>().Any()))
        {
            var rect = LayoutProbe.ToWindow(button, dialog, new Rect(button.Bounds.Size));
            Assert.True(rect.Bottom <= dialog.Bounds.Height + 0.5 && rect.Right <= dialog.Bounds.Width + 0.5, typeof(T).Name + ": button " + button.Content + " is outside the window");
        }

        dialog.Close();
        await Task.WhenAny(pending, Task.Delay(500));
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.75)]
    public async Task EveryDialogFitsTheWorkAreaWithoutOverlappingOrCutOffText(double scaling)
    {
        var owner = new Window { Width = 1368, Height = 768 };
        owner.Show();
        try
        {
            await CheckDialog<ApiConfigDialog>(owner, scaling, () => ApiConfigDialog.ShowAsync(owner, new ApiConfigInput
            {
                ProviderId = "api-1", DisplayName = LongProvider, Title = LongProvider,
                BaseUrl = "https://ark.cn-beijing.volces.example.test/api/coding/v3", Models = new[] { "deepseek-v4.1-flash", "deepseek-v4-pro", "doubao-seed-1.6", "kimi-k2", "glm-4.6", "qwen3-coder" },
                HasCredential = true, UseResponses = true,
            }));

            await CheckDialog<SettingsDialog>(owner, scaling, () => SettingsDialog.ShowAsync(owner, new SettingsInput
            {
                DetectedProfiles = new[] { "desktop", "tauri" }, WebDavUrl = "https://dav.example.test/dav/", WebDavUsername = "user", WebDavPasswordStored = true,
            }));

            await CheckDialog<ReferenceGuardDialog>(owner, scaling, () => ReferenceGuardDialog.ShowAsync(owner, new ReferenceGuardInput
            {
                Title = "删除前确认引用", Heading = "删除供应商“" + LongProvider + "”：dsh 的设置还在引用它（4 处）",
                References = new[]
                {
                    Ref("agent-default-model", "api-1", "deepseek-v4.1-flash", false),
                    Ref("subagent-model-selection-settings", "api-1", "deepseek-v4-pro", true),
                    Ref("subagent-model-selection-settings", "api-1", "model-12-with-a-very-long-identifier-0123456789", true),
                    Ref("builtin-layer", "api-1", "x", false, editable: false),
                },
                Choices = Choices, CanRemoveItems = true,
            }));

            await CheckDialog<ReferenceRepairDialog>(owner, scaling, () => ReferenceRepairDialog.ShowAsync(owner, new ReferenceRepairInput
            {
                References = new[]
                {
                    Ref("agent-default-model", "ghost-provider", "ghost-model-1", false),
                    Ref("subagent-model-selection-settings", "ghost-provider", "ghost-model-with-a-long-name-0123456789", true),
                    Ref("builtin-layer", "ghost-provider", "x", false, editable: false),
                },
                Choices = Choices, Backups = new Dictionary<string, string>(),
                Note = "这些引用指向已不存在的供应商。可以改用现有的供应商 / 模型，或者在有备份时恢复被删除的供应商。",
            }));

            await CheckDialog<ResetCreditsDialog>(owner, scaling, () => ResetCreditsDialog.ShowAsync(owner, new ResetCreditsInput
            {
                AccountTitle = LongEmail,
                List = new ResetCreditList
                {
                    AvailableCount = 3,
                    Credits = Enumerable.Range(1, 5).Select(i => new ResetCredit
                    {
                        Id = "c" + i, Status = i <= 3 ? "available" : "redeemed", Title = i == 1 ? "Codex 周限额重置券（活动赠送，有效期较短请尽快使用，说明文字比较长）" : "",
                        GrantedAt = DateTimeOffset.UtcNow.AddDays(-i), ExpiresAt = DateTimeOffset.UtcNow.AddDays(10 - i),
                    }).ToList(),
                },
                Target = new ResetCredit { Id = "c1", Status = "available", ExpiresAt = DateTimeOffset.UtcNow.AddDays(9) },
            }), minHeight: 0);

            await CheckDialog<MessageDialog>(owner, scaling, () => MessageDialog.ShowAsync(owner, "导入完成",
                "导入完成：新增 2 个账号、1 个 API 供应商，跳过 1 个已存在。\n已把 7 条路由加入 dsh 的子代理可用模型（并启用了子代理模型选择），主代理可以把任务委派给这些供应商 / 模型。\n改动只对 dsh 里的新对话生效：请在 dsh 中新建对话后，再让主代理调用子代理。\n已生成加密备份：/home/user/.config/DshAccountManager/backups/backup-20261020-180000.enc",
                confirm: false, dangerous: false), minWidth: 300, minHeight: 0);
        }
        finally { owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1.0, 0)]
    [InlineData(1.0, 1)]
    [InlineData(1.75, 0)]
    [InlineData(1.75, 1)]
    public void AddAccountDialogFitsTheWorkAreaOnBothTabs(double scaling, int tab)
    {
        var dialog = new AddAccountDialog();
        var vm = new AddAccountViewModel(new FakeAddServices(), new NoDialogs()) { SelectedTabIndex = tab };
        dialog.DataContext = vm;
        if (tab == 0)
        {
            vm.AuthorizationUrl = "https://auth.example.test/oauth/authorize?response_type=code&client_id=fake&redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback&state=fake-state";
            vm.StatusText = "等待浏览器授权完成（10 分钟内有效）。无法自动回调时，请粘贴完整回调地址。";
        }
        else
        {
            vm.DisplayName = LongProvider;
            vm.BaseUrl = "https://ark.cn-beijing.volces.example.test/api/coding/v3";
            vm.ModelsText = "deepseek-v4.1-flash\ndeepseek-v4-pro";
            vm.ErrorText = "URL 必须使用 HTTPS，且不要追加接口路径。";
        }

        var owner = new Window { Width = 1368, Height = 768 };
        owner.Show();
        dialog.Show(owner);
        try
        {
            ApplyLimits(dialog, scaling);
            dialog.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();
            Assert.True(dialog.Bounds.Height <= DialogWindowBounds.ComputeDialogBounds(1368, 768, scaling, double.NaN, double.NaN).MaxHeight + 0.5);
            LayoutProbe.AssertClean(dialog);
        }
        finally { dialog.Close(); owner.Close(); }
    }

    // ---- the fitting maths ------------------------------------------------------------------

    [AvaloniaFact]
    public void TheSmallerDesignMinimumZoomsLessOnASmallHighScaleScreen()
    {
        var old = DialogWindowBounds.FitMainBounds(1368, 768, 1.75, 1000, 560, 1140, 720);
        var now = DialogWindowBounds.FitMainBounds(1368, 768, 1.75, MainWindow.DesignMinWidth, MainWindow.DesignMinHeight, 980, 620);

        Assert.True(now.Zoom > old.Zoom + 0.1, $"zoom {old.Zoom:0.000} -> {now.Zoom:0.000}");
        Assert.True(MainWindow.DesignMinWidth <= 860 && MainWindow.DesignMinHeight <= 480);
        Assert.Equal(new AppSettings().WindowWidth, 980);
        Assert.Equal(new AppSettings().WindowHeight, 620);
    }

    [Fact]
    public void DialogBoundsNeverExceedTheWorkAreaAndKeepSelfSizedDialogsUnclamped()
    {
        var fit = DialogWindowBounds.ComputeDialogBounds(1368, 768, 1.75, 580, 560);
        Assert.Equal(1368 / 1.75 - 32, fit.MaxWidth, 6);
        Assert.Equal(768 / 1.75 - 64, fit.MaxHeight, 6);
        Assert.Equal(fit.MaxHeight, fit.Height, 6);
        Assert.Equal(580, fit.Width);

        var selfSized = DialogWindowBounds.ComputeDialogBounds(1368, 768, 1.0, 440, double.NaN, minHeight: 0);
        Assert.True(double.IsNaN(selfSized.Height));
        Assert.Equal(0, selfSized.MinHeight);
    }

    // ---- fakes ----------------------------------------------------------------------------------

    private sealed class NoDialogs : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, bool dangerous = false) => Task.FromResult(false);
        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;
        public Task ShowWarningAsync(string title, string message) => Task.CompletedTask;
        public Task<string?> PickOpenFileAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFileAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
        public Task<ApiConfigResult?> ShowApiConfigAsync(ApiConfigInput input) => Task.FromResult<ApiConfigResult?>(null);
        public Task<AccountAddResult?> ShowAddAccountAsync(AppServices services) => Task.FromResult<AccountAddResult?>(null);
        public Task<bool> ShowResetCreditsAsync(ResetCreditsInput input) => Task.FromResult(false);
        public Task<SettingsResult?> ShowSettingsAsync(SettingsInput input) => Task.FromResult<SettingsResult?>(null);
        public Task<ReferenceGuardResult?> ShowReferenceGuardAsync(ReferenceGuardInput input) => Task.FromResult<ReferenceGuardResult?>(null);
        public Task<IReadOnlyList<RepairChoice>?> ShowReferenceRepairAsync(ReferenceRepairInput input) => Task.FromResult<IReadOnlyList<RepairChoice>?>(null);
    }

    private sealed class FakeAddServices : IAddAccountServices
    {
        public SecretRegistry Secrets { get; } = new(false);
        public CodexOAuthService CreateOAuthService() => throw new InvalidOperationException();
        public IBrowserOAuthSession StartBrowserOAuthSession() => throw new InvalidOperationException();
        public Task<CodexAddPreview> PreviewCodexAuthorizationAsync(System.Text.Json.Nodes.JsonObject account) => throw new InvalidOperationException();
        public Task<AccountAddResult> SaveCodexAuthorizationAsync(CodexAddPreview preview, bool replaceExisting) => throw new InvalidOperationException();
        public Task<AccountAddResult> CreateApiProviderAsync(string id, string name, string url, IReadOnlyList<string> models, string key, bool useResponses)
            => Task.FromResult(new AccountAddResult { Saved = true, ProviderID = id, Label = name });
    }
}
