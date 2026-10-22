using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;

namespace AccountManager.App.Views;

/// <summary>
/// A ComboBox shows its selected item at full width and simply cuts it off at the box edge. This template trims
/// long provider and model names with an ellipsis instead (the full text stays in the drop-down list).
/// </summary>
internal static class ComboBoxTrimming
{
    private static readonly IDataTemplate Template = new FuncDataTemplate<object>(
        (item, _) => new TextBlock { Text = item?.ToString() ?? string.Empty, TextTrimming = TextTrimming.CharacterEllipsis },
        supportsRecycling: false);

    public static T Trimmed<T>(this T box) where T : ComboBox
    {
        box.ItemTemplate = Template;
        return box;
    }
}
