using AccountManager.App.Services;
using AccountManager.Core.Dsh;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AccountManager.App.Views;

/// <summary>
/// Shown before a provider (or some of its models) is deleted while dsh's settings still refer to it:
/// cancel, point the references at another provider and model, or delete the list-item references too.
/// </summary>
public partial class ReferenceGuardDialog : Window
{
    private ReferenceGuardInput _input = new();

    public ReferenceGuardDialog()
    {
        InitializeComponent();
        ProviderBox.Trimmed();
        ModelBox.Trimmed();
        DialogWindowBounds.KeepOnScreen(this);
    }

    public ReferenceGuardResult? Result { get; private set; }

    public static async Task<ReferenceGuardResult?> ShowAsync(Window owner, ReferenceGuardInput input)
    {
        var dialog = new ReferenceGuardDialog();
        dialog.Init(input);
        DialogWindowBounds.Fit(dialog, owner, center: true);
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    internal void Init(ReferenceGuardInput input)
    {
        _input = input;
        Title = input.Title;
        HeaderText.Text = input.Heading;
        var editable = input.References.Where(r => r.Editable).ToList();
        var locked = input.References.Where(r => !r.Editable).ToList();
        foreach (var r in input.References)
        {
            ReferenceList.Children.Add(new TextBlock
            {
                Text = "· " + r.Describe() + (r.Editable ? string.Empty : "（dsh 内置配置层）"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        }

        if (locked.Count > 0)
        {
            LockedText.IsVisible = true;
            LockedText.Text = "其中 " + locked.Count + " 处在 dsh 的内置配置里，本工具无法修改它们；删除后这些引用会失效，需要在 dsh 里自行处理。";
        }

        HintText.Text = editable.Count == 0
            ? "这些引用都在 dsh 的内置配置里，本工具改不了。你可以取消，或仍然删除。"
            : "删除后这些设置会找不到供应商。请选择如何处理，本工具不会悄悄留下失效的引用。";

        ProviderBox.ItemsSource = input.Choices.Select(c => new ChoiceItem(c)).ToList();
        ReplacePanel.IsVisible = editable.Count > 0;
        if (input.Choices.Count > 0)
        {
            // Prefer a provider that offers the model the first reference uses.
            var first = editable.FirstOrDefault()?.Model;
            var index = first is null ? 0 : Math.Max(0, input.Choices.ToList().FindIndex(c => c.Models.Contains(first)));
            ProviderBox.SelectedIndex = index;
            if (first is not null && input.Choices[index].Models.Contains(first)) { ModelBox.SelectedItem = first; }
        }

        if (editable.Count == 0)
        {
            ReplaceButton.IsVisible = false;
            RemoveButton.Content = "仍然删除";
            RemoveButton.IsEnabled = true;
        }
        else
        {
            ReplaceButton.Content = input.ReplaceText;
            ReplaceButton.IsEnabled = input.Choices.Count > 0;
            RemoveButton.Content = input.RemoveText;
            RemoveButton.IsEnabled = input.CanRemoveItems;
            if (!input.CanRemoveItems) { ToolTip.SetTip(RemoveButton, "其中有单个设置值（例如默认模型），不能直接移除，只能改用其他供应商。"); }
            if (input.Choices.Count == 0) { ErrorText.Text = "没有其他带模型的供应商可以改用；可以先添加一个，或只在能移除时选择“" + input.RemoveText + "”。"; }
        }
    }

    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedItem is not ChoiceItem item) { ModelBox.ItemsSource = null; return; }
        ModelBox.ItemsSource = item.Choice.Models;
        ModelBox.SelectedIndex = item.Choice.Models.Count > 0 ? 0 : -1;
    }

    private void OnReplace(object? sender, RoutedEventArgs e)
    {
        if (ProviderBox.SelectedItem is not ChoiceItem item || ModelBox.SelectedItem is not string model)
        {
            ErrorText.Text = "请先选择要改用的供应商和模型。";
            return;
        }

        Result = new ReferenceGuardResult { Resolution = ReferenceResolution.ReplaceWith(item.Choice.ProviderId, model) };
        Close();
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        Result = new ReferenceGuardResult { Resolution = ReferenceResolution.RemoveItems() };
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    internal sealed class ChoiceItem(ProviderChoice choice)
    {
        public ProviderChoice Choice { get; } = choice;

        public override string ToString() => Choice.DisplayName == Choice.ProviderId ? Choice.ProviderId : Choice.DisplayName + "（" + Choice.ProviderId + "）";
    }
}
