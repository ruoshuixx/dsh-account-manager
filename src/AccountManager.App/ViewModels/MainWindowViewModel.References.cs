using System.Collections.ObjectModel;
using AccountManager.App.Services;
using AccountManager.Core.Dsh;
using AccountManager.Core.Import;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccountManager.App.ViewModels;

/// <summary>One planned repair in the import preview, with its checkbox.</summary>
public sealed partial class ImportRepairViewModel : ObservableObject
{
    public ImportRepairViewModel(string key, string text)
    {
        Key = key;
        Text = text;
    }

    public string Key { get; }

    public string Text { get; }

    [ObservableProperty]
    private bool _isSelected = true;
}

/// <summary>Model references (provider + model pairs in dsh's settings): health check, guard, repair and undo.</summary>
public sealed partial class MainWindowViewModel
{
    private int _scanVersion;
    private (string ProviderId, string Title, string Backup)? _lastDeleted;

    /// <summary>Chip text, for example "dsh 设置中有 2 处引用了不存在的供应商".</summary>
    [ObservableProperty]
    private string _referenceWarning = string.Empty;

    [ObservableProperty]
    private bool _hasReferenceWarning;

    [ObservableProperty]
    private string _undoText = string.Empty;

    [ObservableProperty]
    private bool _hasUndo;

    [ObservableProperty]
    private string _importNotes = string.Empty;

    [ObservableProperty]
    private bool _hasImportNotes;

    [ObservableProperty]
    private bool _importRepairsVisible;

    public ObservableCollection<ImportRepairViewModel> ImportRepairs { get; } = new();

    // ---- health check ------------------------------------------------------

    /// <summary>Scans dsh's settings in the background; the chip appears only when something dangles.</summary>
    private async Task CheckReferencesAsync()
    {
        var version = ++_scanVersion;
        try
        {
            var scan = await _services.ScanReferencesAsync().ConfigureAwait(true);
            if (version != _scanVersion) { return; }
            var count = scan.Dangling.Count;
            ReferenceWarning = count > 0 ? "dsh 设置中有 " + count + " 处引用了不存在的供应商" : string.Empty;
            HasReferenceWarning = count > 0;
            if (count > 0) { _services.Log.Warn("发现 " + count + " 处失效的模型引用：" + string.Join("；", scan.Dangling.Take(5).Select(r => r.Describe()))); }
            else if (!string.IsNullOrEmpty(scan.Note)) { _services.Log.Info(scan.Note); }
        }
        catch (Exception ex)
        {
            if (version != _scanVersion) { return; }
            HasReferenceWarning = false;
            _services.Log.Warn("检查 dsh 的模型引用失败：" + _services.Secrets.Redact(ex.Message));
        }
    }

    [RelayCommand]
    private async Task RepairReferencesAsync()
    {
        if (IsBusy) { return; }
        IsBusy = true;
        string? message = null;
        try
        {
            StatusText = "正在检查 dsh 设置里的引用 …";
            var scan = await _services.ScanReferencesAsync().ConfigureAwait(true);
            var dangling = scan.Dangling;
            if (dangling.Count == 0)
            {
                HasReferenceWarning = false;
                ReferenceWarning = string.Empty;
                await _dialogs.ShowInfoAsync("没有失效的引用", string.IsNullOrEmpty(scan.Note) ? "dsh 设置里的供应商引用都有效。" : scan.Note);
                return;
            }

            var choices = await _services.GetProviderChoicesAsync(null).ConfigureAwait(true);
            var backups = await _services.FindBackupsAsync(dangling.Select(r => r.Provider)).ConfigureAwait(true);
            var picks = await _dialogs.ShowReferenceRepairAsync(new ReferenceRepairInput
            {
                References = dangling,
                Choices = choices,
                Backups = backups,
                Note = scan.Note,
            });
            if (picks is null) { return; }

            StatusText = "正在修复 …";
            var restored = 0;
            foreach (var group in picks.Where(p => p.Kind == RepairKind.Restore && p.BackupPath is not null).GroupBy(p => p.Reference.Provider))
            {
                await _services.RestoreProviderAsync(group.First().BackupPath!, group.Key).ConfigureAwait(true);
                restored++;
            }

            var actions = picks.Where(p => p.Kind is RepairKind.Retarget or RepairKind.Remove)
                .Select(p => new ReferenceAction(p.Reference, p.Kind == RepairKind.Remove ? ReferenceActionKind.Remove : ReferenceActionKind.Retarget, p.Provider, p.Model))
                .ToList();
            if (actions.Count > 0) { await _services.ApplyReferenceActionsAsync(actions).ConfigureAwait(true); }
            message = "已处理 " + picks.Count + " 处引用" + (restored > 0 ? "（恢复了 " + restored + " 个供应商）" : string.Empty) + "。";
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            await _dialogs.ShowWarningAsync("修复失败", StatusText);
        }
        finally
        {
            IsBusy = false;
        }

        await ReadDshAsync();
        if (message is not null) { StatusText = message; }
    }

    // ---- undo a provider deletion ------------------------------------------

    private void SetUndo(string providerId, string title, string backup)
    {
        if (string.IsNullOrWhiteSpace(backup)) { return; }
        _lastDeleted = (providerId, title, backup);
        UndoText = "已删除供应商“" + title + "”";
        HasUndo = true;
    }

    [RelayCommand]
    private void DismissUndo()
    {
        _lastDeleted = null;
        HasUndo = false;
    }

    [RelayCommand]
    private async Task UndoDeleteAsync()
    {
        if (_lastDeleted is not { } deleted || IsBusy) { return; }
        IsBusy = true;
        string? message = null;
        try
        {
            StatusText = "正在从备份恢复 …";
            await _services.RestoreProviderAsync(deleted.Backup, deleted.ProviderId).ConfigureAwait(true);
            message = "已恢复供应商“" + deleted.Title + "”。";
            DismissUndo();
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            await _dialogs.ShowWarningAsync("恢复失败", StatusText);
        }
        finally
        {
            IsBusy = false;
        }

        await ReadDshAsync();
        if (message is not null) { StatusText = message; }
    }

    // ---- guard before deleting a provider or models -------------------------

    /// <summary>
    /// Finds the references to a provider (or to the given models) and, when there are any, asks how to resolve them.
    /// Proceed=false means the user cancelled; a null resolution with Proceed=true means nothing refers to it.
    /// </summary>
    private async Task<(bool Proceed, ReferenceResolution? Resolution)> GuardReferencesAsync(string providerId, string title, IReadOnlyCollection<string>? removedModels)
    {
        var references = await _services.FindReferencesAsync(providerId, removedModels).ConfigureAwait(true);
        if (references.Count == 0) { return (true, null); }

        var choices = (await _services.GetProviderChoicesAsync(removedModels is null ? providerId : null).ConfigureAwait(true)).ToList();
        if (removedModels is not null)
        {
            choices = choices
                .Select(c => c.ProviderId == providerId ? c with { Models = c.Models.Where(m => !removedModels.Contains(m)).ToList() } : c)
                .Where(c => c.Models.Count > 0)
                .ToList();
        }

        var heading = removedModels is null
            ? "删除供应商“" + title + "”：dsh 的设置还在引用它（" + references.Count + " 处）"
            : "“" + title + "”要去掉的模型还被 dsh 的设置引用（" + references.Count + " 处）";
        var result = await _dialogs.ShowReferenceGuardAsync(new ReferenceGuardInput
        {
            Title = removedModels is null ? "删除前确认引用" : "去掉模型前确认引用",
            Heading = heading,
            References = references,
            Choices = choices,
            CanRemoveItems = references.Where(r => r.Editable).All(r => r.Removable),
            ReplaceText = removedModels is null ? "改用其他供应商后删除" : "改用其他模型后保存",
            RemoveText = removedModels is null ? "连同引用一起删除" : "连同引用一起移除",
        });
        return result is null ? (false, null) : (true, result.Resolution);
    }

    // ---- import preview ----------------------------------------------------

    private void LoadImportPlan(ImportPreview preview)
    {
        ImportRepairs.Clear();
        foreach (var repair in preview.Repairs) { ImportRepairs.Add(new ImportRepairViewModel(repair.Key, repair.Description)); }
        ImportRepairsVisible = ImportRepairs.Count > 0;
        ImportNotes = string.Join("\n", preview.Notes);
        HasImportNotes = ImportNotes.Length > 0;
    }

    private void ClearImportPlan()
    {
        ImportRepairs.Clear();
        ImportRepairsVisible = false;
        ImportNotes = string.Empty;
        HasImportNotes = false;
    }
}
