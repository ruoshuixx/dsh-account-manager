using AccountManager.App.Services;
using AccountManager.App.Views;
using AccountManager.Core.Dsh;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;

namespace AccountManager.App.Tests;

public class ReferenceDialogTests
{
    private static ModelReference Ref(string entry, string provider, string model, bool list, bool editable = true)
        => new()
        {
            EntryId = entry, Label = ReferenceScanner.LabelFor(entry), Provider = provider, Model = model,
            IsListItem = list, Editable = editable, Removable = editable && list, Path = editable ? new[] { "0", "config" } : Array.Empty<string>(),
        };

    private static readonly ProviderChoice[] Choices =
    {
        new("other-gw", "Other Gateway", new[] { "deepseek-v4.1-flash", "other-model" }),
        new("third", "Third", new[] { "third-model" }),
    };

    private static void Click(Window dialog, string content)
        => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == content)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task GuardListsTheReferencesAndReplaceReturnsTheChosenProviderAndModel()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var pending = ReferenceGuardDialog.ShowAsync(owner, new ReferenceGuardInput
            {
                Title = "删除前确认引用",
                Heading = "删除供应商“火山Codingplan”：dsh 的设置还在引用它（2 处）",
                References = new[]
                {
                    Ref("agent-default-model", "volcengine-coding", "deepseek-v4.1-flash", list: false),
                    Ref("subagent-model-selection-settings", "volcengine-coding", "deepseek-v4-pro", list: true),
                },
                Choices = Choices,
                CanRemoveItems = false,
            });
            var dialog = owner.OwnedWindows.OfType<ReferenceGuardDialog>().Single();
            dialog.UpdateLayout();
            var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains(texts, t => t == "· 默认模型 · volcengine-coding/deepseek-v4.1-flash");
            Assert.Contains(texts, t => t == "· 子代理模型选择 · volcengine-coding/deepseek-v4-pro");
            // A single-value reference cannot be removed, so only the replace choice is enabled.
            Assert.False(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "连同引用一起删除").IsEnabled);

            Click(dialog, "改用其他供应商后删除");
            var result = await pending;

            Assert.NotNull(result);
            // The provider offering the first reference's model is preselected.
            Assert.Equal(ReferenceActionKind.Retarget, result!.Resolution.Kind);
            Assert.Equal("other-gw", result.Resolution.Provider);
            Assert.Equal("deepseek-v4.1-flash", result.Resolution.Model);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task GuardCanRemoveListItemsAndCanBeCancelled()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var pending = ReferenceGuardDialog.ShowAsync(owner, new ReferenceGuardInput
            {
                Title = "t", Heading = "h", Choices = Choices, CanRemoveItems = true,
                References = new[] { Ref("subagent-model-selection-settings", "p", "m", list: true) },
            });
            var dialog = owner.OwnedWindows.OfType<ReferenceGuardDialog>().Single();
            Click(dialog, "连同引用一起删除");
            Assert.Equal(ReferenceActionKind.Remove, (await pending)!.Resolution.Kind);

            var second = ReferenceGuardDialog.ShowAsync(owner, new ReferenceGuardInput { Title = "t", Heading = "h", Choices = Choices, References = new[] { Ref("x", "p", "m", true) } });
            Click(owner.OwnedWindows.OfType<ReferenceGuardDialog>().Single(), "取消");
            Assert.Null(await second);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task GuardWithOnlyBundleLayerReferencesOffersAnInformedDeleteAndSaysTheyCannotBeChanged()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var pending = ReferenceGuardDialog.ShowAsync(owner, new ReferenceGuardInput
            {
                Title = "t", Heading = "h", Choices = Choices,
                References = new[] { Ref("from-bundle", "p", "m", false, editable: false) },
            });
            var dialog = owner.OwnedWindows.OfType<ReferenceGuardDialog>().Single();
            dialog.UpdateLayout();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").Contains("无法修改"));
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "改用其他供应商后删除" && b.IsVisible);

            Click(dialog, "仍然删除");
            Assert.NotNull(await pending);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task RepairSuggestsTheOnlyProviderWithTheSameModelAndAppliesTheChosenFixes()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var single = Ref("agent-default-model", "volcengine-coding", "deepseek-v4.1-flash", list: false);
            var item = Ref("subagent-model-selection-settings", "volcengine-coding", "deepseek-v4-pro", list: true);
            var locked = Ref("from-bundle", "gone", "m", list: false, editable: false);
            var pending = ReferenceRepairDialog.ShowAsync(owner, new ReferenceRepairInput
            {
                References = new[] { single, item, locked },
                Choices = Choices,
                Backups = new Dictionary<string, string> { ["volcengine-coding"] = "/backups/one.enc" },
            });
            var dialog = owner.OwnedWindows.OfType<ReferenceRepairDialog>().Single();
            dialog.UpdateLayout();
            var combos = dialog.GetVisualDescendants().OfType<ComboBox>().Where(c => c.ItemsSource is System.Collections.IEnumerable e && e.Cast<object>().Any(o => o.ToString() == "暂不处理")).ToList();
            Assert.Equal(2, combos.Count); // the bundle-layer one is not editable
            // Exactly one provider offers deepseek-v4.1-flash: it is preselected. For the other reference, a backup restore is.
            Assert.Equal("改指向 Other Gateway（other-gw） · deepseek-v4.1-flash", combos[0].SelectedItem!.ToString());
            Assert.Equal("从备份恢复供应商 volcengine-coding", combos[1].SelectedItem!.ToString());
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").Contains("gone/m"));
            var options = ((System.Collections.IEnumerable)combos[1].ItemsSource!).Cast<object>().Select(o => o.ToString()).ToList();
            Assert.Contains("移除这条引用", options);
            Assert.DoesNotContain("移除这条引用", ((System.Collections.IEnumerable)combos[0].ItemsSource!).Cast<object>().Select(o => o.ToString())); // single value

            Click(dialog, "应用");
            var choices = await pending;

            Assert.NotNull(choices);
            Assert.Equal(2, choices!.Count);
            Assert.Equal((RepairKind.Retarget, "other-gw", "deepseek-v4.1-flash"), (choices[0].Kind, choices[0].Provider, choices[0].Model));
            Assert.Equal((RepairKind.Restore, "/backups/one.enc"), (choices[1].Kind, choices[1].BackupPath));
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task RepairWithSeveralSameModelProvidersPreselectsNothingAndNeedsAnExplicitChoice()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var choices = new[] { new ProviderChoice("a", "A", new[] { "m" }), new ProviderChoice("b", "B", new[] { "m" }) };
            var pending = ReferenceRepairDialog.ShowAsync(owner, new ReferenceRepairInput { References = new[] { Ref("agent-default-model", "gone", "m", false) }, Choices = choices });
            var dialog = owner.OwnedWindows.OfType<ReferenceRepairDialog>().Single();
            dialog.UpdateLayout();
            Assert.Equal("暂不处理", dialog.GetVisualDescendants().OfType<ComboBox>().First().SelectedItem!.ToString());

            Click(dialog, "应用"); // nothing chosen: stays open with a message
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "没有选择任何处理方式。");
            Click(dialog, "取消");
            Assert.Null(await pending);
        }
        finally { owner.Close(); }
    }
}
