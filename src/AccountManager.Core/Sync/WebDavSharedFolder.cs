namespace AccountManager.Core.Sync;

/// <summary>The newest sync file found, the client that can download it and whether it came from the legacy folder.</summary>
public sealed record WebDavSource(WebDavClient Client, WebDavFile File, bool FromLegacyFolder)
{
    /// <summary>Full cloud path, e.g. <c>/AccountManager/name.json</c>.</summary>
    public string Path => Client.Folder + File.Name;
}

/// <summary>
/// Picks the file a download should use. The neutral sync folder is shared by every tool that writes Cockpit
/// account JSON, so the newest <c>.json</c> file there wins whatever its name prefix is. Only when that folder is
/// missing or holds no JSON file is the tool's own legacy folder looked at (read-only: it is never written to,
/// deleted from or created).
/// </summary>
public static class WebDavSharedFolder
{
    /// <summary>Newest first by modification time; unknown times fall back to the file name.</summary>
    public static WebDavFile Newest(IEnumerable<WebDavFile> files)
        => files
            .OrderByDescending(f => f.LastModified ?? DateTimeOffset.MinValue)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .First();

    /// <summary>The newest file of the shared folder, else of the legacy folder, else null when both are empty.</summary>
    public static async Task<WebDavSource?> FindNewestAsync(WebDavClient shared, WebDavClient? legacy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shared);

        // ListAsync only returns .json files (folders and other entries are dropped) and treats a missing folder (404) as empty.
        var files = await shared.ListAsync(cancellationToken).ConfigureAwait(false);
        if (files.Count > 0) { return new WebDavSource(shared, Newest(files), FromLegacyFolder: false); }

        if (legacy is null) { return null; }
        var older = await legacy.ListAsync(cancellationToken).ConfigureAwait(false);
        return older.Count > 0 ? new WebDavSource(legacy, Newest(older), FromLegacyFolder: true) : null;
    }
}
