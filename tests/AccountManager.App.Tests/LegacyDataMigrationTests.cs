using AccountManager.App.Services;
using Xunit;

namespace AccountManager.App.Tests;

/// <summary>Moving pre-0.2.3 settings/logs from beside the exe to the per-user folder. Temp folders only.</summary>
public sealed class LegacyDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ocam-migrate-" + Guid.NewGuid().ToString("N"));
    private string Old => Path.Combine(_root, "Desktop");
    private string New => Path.Combine(_root, "data");
    private static readonly TimeSpan Ten = TimeSpan.FromDays(10);

    public LegacyDataMigrationTests() { Directory.CreateDirectory(Old); }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string WriteOld(string relative, string text, DateTime? written = null)
    {
        var path = Path.Combine(Old, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (written is { } w) { File.SetLastWriteTime(path, w); }
        return path;
    }

    [Fact]
    public void CopiesSettingsAndRecentLogsThenRemovesTheOriginals()
    {
        var now = DateTime.Now;
        WriteOld("avalonia-settings.json", "{\"a\":1}");
        WriteOld("logs/run-1.log", "run", now.AddDays(-2));
        WriteOld("logs/errors-1.log", "err", now.AddDays(-2));

        var report = LegacyDataMigration.Run(Old, New, now, Ten)!;

        Assert.True(report.SettingsMoved);
        Assert.Equal(2, report.LogsMoved);
        Assert.Empty(report.Problems);
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(New, "avalonia-settings.json")));
        Assert.Equal("run", File.ReadAllText(Path.Combine(New, "logs", "run-1.log")));
        Assert.False(File.Exists(Path.Combine(Old, "avalonia-settings.json")));
        Assert.False(Directory.Exists(Path.Combine(Old, "logs")));
        Assert.True(Directory.Exists(Old)); // the folder itself is never touched
    }

    [Fact]
    public void LogsOlderThanTenDaysAreDeletedNotCopied()
    {
        var now = DateTime.Now;
        WriteOld("logs/run-old.log", "old", now.AddDays(-11));
        WriteOld("logs/run-new.log", "new", now.AddDays(-9));

        var report = LegacyDataMigration.Run(Old, New, now, Ten)!;

        Assert.Equal(1, report.LogsExpired);
        Assert.Equal(1, report.LogsMoved);
        Assert.False(File.Exists(Path.Combine(New, "logs", "run-old.log")));
        Assert.True(File.Exists(Path.Combine(New, "logs", "run-new.log")));
    }

    [Fact]
    public void OtherFilesInTheOldFolderAreNeverTouched()
    {
        var now = DateTime.Now;
        var keep = WriteOld("notes.txt", "mine");
        var keepLog = WriteOld("logs/other.txt", "mine too");
        WriteOld("logs/run-1.log", "run", now);

        LegacyDataMigration.Run(Old, New, now, Ten);

        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(keepLog)); // logs\ is not empty, so it stays
    }

    [Fact]
    public void ANewerSettingsFileAtTheNewLocationWinsAndTheOldOneIsRemoved()
    {
        var now = DateTime.Now;
        var oldFile = WriteOld("avalonia-settings.json", "old", now.AddMinutes(-5));
        Directory.CreateDirectory(New);
        var current = Path.Combine(New, "avalonia-settings.json");
        File.WriteAllText(current, "current");
        File.SetLastWriteTime(current, now);

        var report = LegacyDataMigration.Run(Old, New, now, Ten)!;

        Assert.True(report.SettingsMoved);
        Assert.Equal("current", File.ReadAllText(current));
        Assert.False(File.Exists(oldFile));
    }

    [Fact]
    public void ANewerOldSettingsFileReplacesTheCurrentOneWhichIsKeptOnce()
    {
        var now = DateTime.Now;
        WriteOld("avalonia-settings.json", "fresh", now);
        Directory.CreateDirectory(New);
        var current = Path.Combine(New, "avalonia-settings.json");
        File.WriteAllText(current, "stale");
        File.SetLastWriteTime(current, now.AddHours(-1));

        LegacyDataMigration.Run(Old, New, now, Ten);

        Assert.Equal("fresh", File.ReadAllText(current));
        Assert.Equal("stale", File.ReadAllText(current + ".before-migration"));
    }

    [Fact]
    public void NothingToMigrateOrTheSameFolderDoesNothing()
    {
        Assert.Null(LegacyDataMigration.Run(Old, New, DateTime.Now, Ten));
        WriteOld("avalonia-settings.json", "x");
        Assert.Null(LegacyDataMigration.Run(Old, Old, DateTime.Now, Ten));
        Assert.True(File.Exists(Path.Combine(Old, "avalonia-settings.json")));
    }

    [Fact]
    public void RunningTwiceIsHarmless()
    {
        var now = DateTime.Now;
        WriteOld("avalonia-settings.json", "x");
        LegacyDataMigration.Run(Old, New, now, Ten);

        Assert.Null(LegacyDataMigration.Run(Old, New, now, Ten));
        Assert.Equal("x", File.ReadAllText(Path.Combine(New, "avalonia-settings.json")));
    }
}
