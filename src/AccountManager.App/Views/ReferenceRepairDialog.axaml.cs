using AccountManager.App.Services;
using AccountManager.Core.Dsh;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace AccountManager.App.Views;

/// <summary>
/// Lists every dangling model reference with the fixes that apply to it: point it at another provider that offers the
/// same model, restore the deleted provider from a backup, remove the list item, or pick another provider and model.
/// </summary>
public partial class ReferenceRepairDialog : Window
{
    private readonly List<RowState> _rows = new();

    public ReferenceRepairDialog()
    {
        InitializeComponent();
        DialogWindowBounds.KeepOnScreen(this);
    }

    public IReadOnlyList<RepairChoice>? Result { get; private set; }

    public static async Task<IReadOnlyList<RepairChoice>?> ShowAsync(Window owner, ReferenceRepairInput input)
    {
        var dialog = new ReferenceRepairDialog();
        dialog.Init(input);
        DialogWindowBounds.Fit(dialog, owner, center: true);
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    internal void Init(ReferenceRepairInput input)
    {
        var editable = input.References.Where(r => r.Editable).ToList();
        var locked = input.References.Where(r => !r.Editable).ToList();
        HeaderText.Text = "dsh 设置里有 " + input.References.Count + " 处引用了不存在的供应商";
        NoteText.Text = string.IsNullOrEmpty(input.Note)
            ? "为每一处选一种处理方式，应用前会先做加密备份。暂不处理的保持原样。"
            : input.Note;

        foreach (var reference in editable)
        {
            var state = BuildRow(reference, input);
            _rows.Add(state);
            RowList.Children.Add(state.Panel);
        }

        if (locked.Count > 0)
        {
            LockedHeader.IsVisible = true;
            foreach (var r in locked)
            {
                LockedList.Children.Add(new TextBlock { Text = "· " + r.Describe(), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } });
            }
        }

        if (editable.Count == 0) { ApplyButton.IsEnabled = false; }
    }

    private static RowState BuildRow(ModelReference reference, ReferenceRepairInput input)
    {
        var items = new List<ActionItem> { new(RepairKind.Skip, "暂不处理") };
        var sameModel = input.Choices.Where(c => c.Models.Contains(reference.Model)).ToList();
        foreach (var choice in sameModel)
        {
            items.Add(new ActionItem(RepairKind.Retarget, "改指向 " + Label(choice) + " · " + reference.Model, choice.ProviderId, reference.Model));
        }

        if (input.Backups.TryGetValue(reference.Provider, out var backup))
        {
            items.Add(new ActionItem(RepairKind.Restore, "从备份恢复供应商 " + reference.Provider, BackupPath: backup));
        }

        if (reference.Removable) { items.Add(new ActionItem(RepairKind.Remove, "移除这条引用")); }
        var pick = new ActionItem(RepairKind.Retarget, "改选其他供应商和模型…", IsPick: true);
        items.Add(pick);

        var combo = new ComboBox { ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Stretch }.Trimmed();
        // Auto-suggest only when exactly one provider offers the same model; otherwise a restore if possible, else nothing.
        combo.SelectedItem = sameModel.Count == 1
            ? items.First(i => i.Provider == sameModel[0].ProviderId && !i.IsPick && i.Kind == RepairKind.Retarget)
            : items.FirstOrDefault(i => i.Kind == RepairKind.Restore) ?? items[0];

        var providerBox = new ComboBox { ItemsSource = input.Choices.Select(c => new ProviderItem(c)).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "供应商" }.Trimmed();
        var modelBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "模型" }.Trimmed();
        providerBox.SelectionChanged += (_, _) =>
        {
            modelBox.ItemsSource = (providerBox.SelectedItem as ProviderItem)?.Choice.Models;
            modelBox.SelectedIndex = modelBox.ItemCount > 0 ? 0 : -1;
        };
        var pickRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), IsVisible = false };
        Grid.SetColumn(providerBox, 0);
        Grid.SetColumn(modelBox, 2);
        pickRow.Children.Add(providerBox);
        pickRow.Children.Add(modelBox);
        combo.SelectionChanged += (_, _) => pickRow.IsVisible = (combo.SelectedItem as ActionItem)?.IsPick == true;
        pickRow.IsVisible = (combo.SelectedItem as ActionItem)?.IsPick == true;

        var panel = new Border { Classes = { "card" }, Padding = new Avalonia.Thickness(10, 8) };
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = reference.Label + " · " + reference.Provider + " / " + reference.Model, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = reference.IsListItem ? "列表中的一项" : "单个设置值（只能改指向，不能移除）", Classes = { "subtitle" } });
        stack.Children.Add(combo);
        stack.Children.Add(pickRow);
        panel.Child = stack;
        return new RowState(reference, combo, providerBox, modelBox, panel);
    }

    private static string Label(ProviderChoice c) => c.DisplayName == c.ProviderId ? c.ProviderId : c.DisplayName + "（" + c.ProviderId + "）";

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        var choices = new List<RepairChoice>();
        foreach (var row in _rows)
        {
            if (row.Combo.SelectedItem is not ActionItem item || item.Kind == RepairKind.Skip) { continue; }
            if (item.IsPick)
            {
                if (row.ProviderBox.SelectedItem is not ProviderItem provider || row.ModelBox.SelectedItem is not string model)
                {
                    ErrorText.Text = "“" + row.Reference.Describe() + "”选了“改选其他供应商和模型”，请把供应商和模型选完整。";
                    return;
                }

                choices.Add(new RepairChoice { Reference = row.Reference, Kind = RepairKind.Retarget, Provider = provider.Choice.ProviderId, Model = model });
            }
            else
            {
                choices.Add(new RepairChoice { Reference = row.Reference, Kind = item.Kind, Provider = item.Provider, Model = item.Model, BackupPath = item.BackupPath });
            }
        }

        if (choices.Count == 0) { ErrorText.Text = "没有选择任何处理方式。"; return; }
        Result = choices;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    private sealed record RowState(ModelReference Reference, ComboBox Combo, ComboBox ProviderBox, ComboBox ModelBox, Border Panel);

    private sealed record ActionItem(RepairKind Kind, string Text, string? Provider = null, string? Model = null, string? BackupPath = null, bool IsPick = false)
    {
        public override string ToString() => Text;
    }

    private sealed class ProviderItem(ProviderChoice choice)
    {
        public ProviderChoice Choice { get; } = choice;

        public override string ToString() => Label(Choice);
    }
}
