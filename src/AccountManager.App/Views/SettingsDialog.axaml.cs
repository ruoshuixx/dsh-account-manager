using AccountManager.App.Services;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace AccountManager.App.Views;

/// <summary>DSH home / profile override, auto-refresh interval, auto-switch, and WebDAV sync.</summary>
public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
        ScalingChanged += (_, _) => DialogWindowBounds.Fit(this);
        PositionChanged += (_, _) => DialogWindowBounds.Fit(this);
    }

    public SettingsResult? Result { get; private set; }

    public static async Task<SettingsResult?> ShowAsync(Window owner, SettingsInput input)
    {
        var dialog = new SettingsDialog();
        dialog.HomeBox.Text = input.DshHome;
        dialog.ProfileBox.Text = input.DshProfile;
        dialog.ProfileHint.Text = input.DetectedProfiles.Count == 0
            ? "在该目录下没有检测到配置档案。"
            : "检测到的档案：" + string.Join("、", input.DetectedProfiles);
        dialog.IntervalBox.Value = input.AutoRefreshMinutes;
        dialog.AutoSwitchBox.IsChecked = input.AutoSwitchAccount;
        dialog.WebDavUrlBox.Text = input.WebDavUrl;
        dialog.WebDavUserBox.Text = input.WebDavUsername;
        dialog.WebDavPassBox.Watermark = input.WebDavPasswordStored ? "应用密码（已保存，留空保持不变）" : "应用密码";
        DialogWindowBounds.Fit(dialog, owner, center: true);
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    private void OnHelp(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control) { ToolTip.SetIsOpen(control, !ToolTip.GetIsOpen(control)); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { OnCancel(this, new RoutedEventArgs()); e.Handled = true; }
        base.OnKeyDown(e);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) { return; }
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 DSH 主目录（含 profiles 文件夹）",
            AllowMultiple = false,
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path)) { HomeBox.Text = path; }
    }

    private void OnSave(object? sender, RoutedEventArgs e) => CloseWith(SettingsAction.None);

    private void OnUpload(object? sender, RoutedEventArgs e) => CloseWith(SettingsAction.Upload);

    private void OnDownload(object? sender, RoutedEventArgs e) => CloseWith(SettingsAction.Download);

    private void CloseWith(SettingsAction action)
    {
        Result = new SettingsResult
        {
            DshHome = HomeBox.Text?.Trim() ?? string.Empty,
            DshProfile = ProfileBox.Text?.Trim() ?? string.Empty,
            AutoRefreshMinutes = (int)Math.Max(1, IntervalBox.Value ?? 5),
            AutoSwitchAccount = AutoSwitchBox.IsChecked == true,
            WebDavUrl = WebDavUrlBox.Text?.Trim() ?? string.Empty,
            WebDavUsername = WebDavUserBox.Text?.Trim() ?? string.Empty,
            WebDavPassword = string.IsNullOrEmpty(WebDavPassBox.Text) ? null : WebDavPassBox.Text,
            Action = action,
        };
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
