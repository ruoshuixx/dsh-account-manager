using AccountManager.Core.Logging;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.Core.Tests;

public sealed class LogRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ocam-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Make(string name, DateTime written)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTime(path, written);
        return path;
    }

    [Fact]
    public void PruneDeletesOnlyOldRunAndErrorLogs()
    {
        var now = DateTime.Now;
        var oldRun = Make("run-a.log", now.AddDays(-11));
        var oldErr = Make("errors-a.log", now.AddDays(-30));
        var recent = Make("run-b.log", now.AddDays(-9));
        var other = Make("notes.txt", now.AddDays(-99));

        var removed = AppLog.PruneOld(_dir, TimeSpan.FromDays(10), now);

        Assert.Equal(2, removed);
        Assert.False(File.Exists(oldRun));
        Assert.False(File.Exists(oldErr));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void PruneKeepsTheGivenFilesAndToleratesAMissingFolder()
    {
        var now = DateTime.Now;
        var keep = Make("run-current.log", now.AddDays(-20));

        Assert.Equal(0, AppLog.PruneOld(_dir, TimeSpan.FromDays(10), now, keep));
        Assert.True(File.Exists(keep));
        Assert.Equal(0, AppLog.PruneOld(Path.Combine(_dir, "missing"), TimeSpan.FromDays(10), now));
    }

    [Fact]
    public void CreatingTheLogRemovesTenDayOldLogs()
    {
        var old = Make("run-old.log", DateTime.Now.AddDays(-12));

        var log = new AppLog(new SecretRegistry(), _dir);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(log.RunLogPath));
    }
}
