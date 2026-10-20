using System.Text.Json.Nodes;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.Security;

namespace AccountManager.Core.Dsh;

/// <summary>
/// Keeps dsh's model references valid: scans them, resolves them before a provider (or one of its models) goes away,
/// repairs dangling ones and restores a deleted provider from the encrypted backup taken before its deletion.
/// Every edit takes a backup first and changes only the reference scalars in the user layer (<c>cordis.patch.yml</c>).
/// </summary>
public sealed class ReferenceService
{
    private readonly DshBackend _backend;
    private readonly ReferenceScanner _scanner;
    private readonly BackupService _backups;
    private readonly SecretRegistry _secrets;
    private readonly AppLog? _log;

    public ReferenceService(DshBackend backend, IDshConfigDumper? dumper, BackupService backups, SecretRegistry secrets, AppLog? log = null)
    {
        _backend = backend;
        _scanner = new ReferenceScanner(dumper);
        _backups = backups;
        _secrets = secrets;
        _log = log;
    }

    public async Task<ReferenceScan> ScanAsync(CancellationToken ct = default)
    {
        var ours = await OursKnownProvidersAsync(ct).ConfigureAwait(false);
        return await Task.Run(() => _scanner.ScanAsync(_backend.Home, ct, ours), ct).ConfigureAwait(false);
    }

    private readonly Dictionary<string, IReadOnlyList<string>> _backupProviders = new(StringComparer.Ordinal);

    /// <summary>
    /// Provider ids this program knows from its own records: every provider in an earlier backup of the provider file
    /// (so also anything it deleted, which is always backed up first) and the providers of imported Cockpit API accounts.
    /// </summary>
    public async Task<IReadOnlySet<string>> OursKnownProvidersAsync(CancellationToken ct = default)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try { foreach (var id in await _backend.GetMirrorProviderIdsAsync(ct).ConfigureAwait(false)) { ids.Add(id); } }
        catch (OperationCanceledException) { throw; }
        catch { /* the mirror is optional */ }

        await Task.Run(() =>
        {
            foreach (var path in _backups.ListBackups().Take(200))
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<string> providers;
                lock (_backupProviders)
                {
                    if (!_backupProviders.TryGetValue(path, out providers!))
                    {
                        providers = ReadBackupProviderIds(path);
                        _backupProviders[path] = providers; // backups never change once written
                    }
                }

                foreach (var id in providers) { ids.Add(id); }
            }
        }, ct).ConfigureAwait(false);
        return ids;
    }

    private IReadOnlyList<string> ReadBackupProviderIds(string backupPath)
    {
        try
        {
            if (_backups.Read(backupPath) is not JsonObject payload) { return Array.Empty<string>(); }
            var text = payload["configText"]?.GetValue<string>();
            if (string.IsNullOrEmpty(text)) { return Array.Empty<string>(); }
            return ProfilePatchDocument.Parse(text, "backup").GetProviders()?.Select(kv => kv.Key).ToList() ?? new List<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>References to a provider (optionally only to some of its models), editable or not.</summary>
    public async Task<IReadOnlyList<ModelReference>> FindReferencesToAsync(string providerId, IReadOnlyCollection<string>? models = null, CancellationToken ct = default)
        => (await ScanAsync(ct).ConfigureAwait(false)).ReferencesTo(providerId, models);

    /// <summary>The providers that can be pointed at: existing managed providers that list at least one model.</summary>
    public async Task<IReadOnlyList<ProviderChoice>> GetProviderChoicesAsync(string? excludeProviderId = null, CancellationToken ct = default)
    {
        var entries = await _backend.GetConfigEntriesAsync(ct).ConfigureAwait(false);
        var choices = new List<ProviderChoice>();
        if (entries.Count > 0 && entries[0]?["info"]?["providers"] is JsonObject providers)
        {
            foreach (var (id, node) in providers)
            {
                if (id == excludeProviderId || node is not JsonObject view) { continue; }
                var models = (view["models"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? new List<string>();
                if (models.Count == 0) { continue; }
                choices.Add(new ProviderChoice(id, ProviderViewMapper.ReadString(view, "name") ?? id, models));
            }
        }

        return choices;
    }

    /// <summary>Builds the actions that resolve the given references the way <paramref name="resolution"/> says.</summary>
    public static IReadOnlyList<ReferenceAction> PlanResolution(IEnumerable<ModelReference> references, ReferenceResolution resolution)
    {
        var actions = new List<ReferenceAction>();
        foreach (var r in references)
        {
            if (!r.Editable) { continue; }
            if (resolution.Kind == ReferenceActionKind.Remove)
            {
                if (!r.Removable) { throw new DshException("“" + r.Describe() + "”是单个设置值，不能直接移除；请改选其它供应商和模型。"); }
                actions.Add(new ReferenceAction(r, ReferenceActionKind.Remove));
            }
            else
            {
                actions.Add(new ReferenceAction(r, ReferenceActionKind.Retarget, resolution.Provider, resolution.Model));
            }
        }

        return actions;
    }

    /// <summary>
    /// Applies the actions under the patch file's lock after an encrypted backup. A list item removed earlier in the
    /// run shifts later indexes, so removals of one list run from the last item to the first. Returns the backup path.
    /// </summary>
    public async Task<string> ApplyAsync(IReadOnlyList<ReferenceAction> actions, CancellationToken ct = default)
    {
        if (actions.Count == 0) { return string.Empty; }
        var credentials = await _backend.GetCredentialsAsync(ct).ConfigureAwait(false);
        var snapshot = await _backend.GetConfigSnapshotAsync(ct).ConfigureAwait(false);
        var backup = _backups.Save(credentials, snapshot.Path, snapshot.Raw);
        _log?.Info("已生成加密备份（修改模型引用）：" + backup);

        // Retargets first (they do not shift anything); removals last, deepest index first.
        var ordered = actions.Where(a => a.Kind == ReferenceActionKind.Retarget)
            .Concat(actions.Where(a => a.Kind == ReferenceActionKind.Remove)
                .OrderBy(a => string.Join("/", a.Reference.Path.Take(a.Reference.Path.Count - 1)), StringComparer.Ordinal)
                .ThenByDescending(a => int.TryParse(a.Reference.Path[^1], out var i) ? i : -1))
            .ToList();

        await Task.Run(() => ProfilePatchDocument.Update(_backend.Home.PatchPath, doc =>
        {
            var changed = false;
            var retargeted = new List<(ModelReference Reference, string Provider, string Model)>();
            foreach (var action in ordered)
            {
                var r = action.Reference;
                if (action.Kind == ReferenceActionKind.Retarget)
                {
                    changed |= doc.RetargetReference(r, action.Provider!, action.Model!);
                    if (r.IsListItem) { retargeted.Add((r, action.Provider!, action.Model!)); }
                    _log?.Info("模型引用已改指向：" + r.Label + " · " + r.Provider + "/" + r.Model + " → " + action.Provider + "/" + action.Model);
                }
                else
                {
                    changed |= doc.RemoveReference(r);
                    _log?.Info("已移除模型引用：" + r.Describe());
                }
            }

            changed |= RemoveDuplicatesCreated(doc, retargeted);
            return changed;
        }), ct).ConfigureAwait(false);
        return backup;
    }

    /// <summary>
    /// Retargeting list items can leave two identical entries in one list. Only an item this run retargeted is ever
    /// removed for that reason, and only when another item of the same list has the same provider and model.
    /// </summary>
    private bool RemoveDuplicatesCreated(ProfilePatchDocument doc, List<(ModelReference Reference, string Provider, string Model)> retargeted)
    {
        if (retargeted.Count == 0) { return false; }
        var changed = false;
        var all = doc.FindReferences();
        var ours = retargeted.Select(t => string.Join("/", t.Reference.Path)).ToHashSet(StringComparer.Ordinal);
        var duplicates = all.Where(r => r.IsListItem && r.Removable)
            .GroupBy(r => (Parent: string.Join("/", r.Path.Take(r.Path.Count - 1)), r.Provider, r.Model))
            .Where(g => g.Count() > 1)
            .SelectMany(g =>
            {
                // Drop the items we retargeted; if every item in the group is ours, keep the first of them.
                var mine = g.Where(r => ours.Contains(string.Join("/", r.Path))).ToList();
                return mine.Count == g.Count() ? mine.Skip(1) : mine;
            })
            .OrderByDescending(r => int.TryParse(r.Path[^1], out var i) ? i : -1)
            .ToList();
        foreach (var r in duplicates)
        {
            changed |= doc.RemoveReference(r);
            _log?.Info("已移除改指向后重复的引用：" + r.Describe());
        }

        return changed;
    }

    // ---- restoring a deleted provider ----

    /// <summary>Backups (newest first) whose provider file contains <paramref name="providerId"/> together with its key.</summary>
    public Task<IReadOnlyList<string>> FindBackupsContainingAsync(string providerId, int maxBackups = 60, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<string>>(() =>
        {
            var found = new List<string>();
            foreach (var path in _backups.ListBackups().Take(maxBackups))
            {
                ct.ThrowIfCancellationRequested();
                if (TryReadProvider(path, providerId, out _, out _)) { found.Add(path); }
            }

            return found;
        }, ct);

    private bool TryReadProvider(string backupPath, string providerId, out JsonObject? provider, out string? key)
    {
        provider = null;
        key = null;
        try
        {
            if (_backups.Read(backupPath) is not JsonObject payload) { return false; }
            var text = payload["configText"]?.GetValue<string>();
            if (string.IsNullOrEmpty(text)) { return false; }
            var found = ProfilePatchDocument.Parse(text, "backup").GetProviders()?[providerId] as JsonObject;
            if (found is null || !ProviderViewMapper.IsManaged(providerId, found)) { return false; }
            provider = (JsonObject)found.DeepClone();
            foreach (var credential in (payload["credentials"] as JsonArray) ?? new JsonArray())
            {
                if (JsonMerge.GetString(credential, "integrationID") == providerId
                    && JsonMerge.GetString(JsonMerge.Get(credential, "value"), "type") == "key")
                {
                    key = JsonMerge.GetString(JsonMerge.Get(credential, "value"), "key");
                    break;
                }
            }

            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Writes the provider back exactly as the backup had it (same id, <c>apiKeyEnv</c>, models and key). Refuses when a
    /// provider of that id exists. A key reference name taken by something else in the meantime is replaced by a fresh one.
    /// </summary>
    public async Task RestoreProviderAsync(string backupPath, string providerId, CancellationToken ct = default)
    {
        if (!TryReadProvider(backupPath, providerId, out var provider, out var key) || provider is null)
        {
            throw new DshException("这份备份里没有供应商 " + providerId + "，无法恢复。");
        }

        var entries = await _backend.GetConfigEntriesAsync(ct).ConfigureAwait(false);
        if (entries.Count > 0 && entries[0]?["info"]?["providers"] is JsonObject existing && existing.ContainsKey(providerId))
        {
            throw new DshException("供应商 " + providerId + " 已经存在，不覆盖；请先检查。");
        }

        if (key is not null) { _secrets.RegisterValue(key); }
        var credentials = await _backend.GetCredentialsAsync(ct).ConfigureAwait(false);
        var snapshot = await _backend.GetConfigSnapshotAsync(ct).ConfigureAwait(false);
        var backup = _backups.Save(credentials, snapshot.Path, snapshot.Raw);
        _log?.Info("已生成加密备份（恢复供应商）：" + backup);

        var view = ProviderViewMapper.ToView(providerId, provider)!;
        await _backend.WriteApiProviderAsync(providerId, view, key, ct).ConfigureAwait(false);
        _log?.Info("已从备份恢复供应商：" + providerId);
    }
}
