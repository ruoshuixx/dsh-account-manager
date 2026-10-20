using System.Diagnostics;
using AccountManager.Core.Dsh;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

public class DshFileLockTests
{
    /// <summary>A pid no process can have (above Linux pid_max, below int.MaxValue).</summary>
    private const int DeadPid = 2147483000;

    [Fact]
    public void LockFileHoldsPidAndNewlineWith0600AndIsRemovedAfterwards()
    {
        using var temp = new TempDir();
        var target = temp.File("doc.yaml");
        string? record = null;
        UnixFileMode? mode = null;

        DshFileLock.Run(target, () =>
        {
            record = File.ReadAllText(target + ".lock");
            if (!OperatingSystem.IsWindows()) { mode = File.GetUnixFileMode(target + ".lock"); }
        });

        Assert.Equal(Environment.ProcessId + "\n", record);
        if (!OperatingSystem.IsWindows()) { Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode); }
        Assert.False(File.Exists(target + ".lock"));
    }

    [Fact]
    public async Task ContendedLockWaitsThenSucceedsWhenReleased()
    {
        using var temp = new TempDir();
        var target = temp.File("doc.yaml");
        var lockPath = target + ".lock";
        File.WriteAllText(lockPath, Environment.ProcessId + "\n"); // held by "someone alive" (own pid is never taken over)
        var releaser = Task.Run(async () => { await Task.Delay(400); File.Delete(lockPath); });
        var clock = Stopwatch.StartNew();

        var result = DshFileLock.Run(target, () => 42, waitMs: 5000);
        await releaser;

        Assert.Equal(42, result);
        Assert.True(clock.ElapsedMilliseconds >= 350, "should have waited for the holder");
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void TimesOutWithAClearErrorAndLeavesTheForeignLockAlone()
    {
        using var temp = new TempDir();
        var target = temp.File("doc.yaml");
        var lockPath = target + ".lock";
        File.WriteAllText(lockPath, Environment.ProcessId + "\n");

        var ex = Assert.Throws<DshLockTimeoutException>(() => DshFileLock.Run(target, () => 1, waitMs: 300));

        Assert.Contains("timed out waiting for the writer lock", ex.Message);
        Assert.Contains(lockPath, ex.Message);
        Assert.True(File.Exists(lockPath));
        Assert.Equal(Environment.ProcessId + "\n", File.ReadAllText(lockPath));
    }

    [Fact]
    public void OwnPidLockIsNeverTakenOver()
    {
        Assert.False(DshFileLock.HolderExited(Environment.ProcessId + "\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("abc\n")]
    [InlineData("123")]            // incomplete record: wait, never take over
    [InlineData("123\n\n")]
    [InlineData("0\n")]
    [InlineData("2147483648\n")]   // above int.MaxValue
    [InlineData("-5\n")]
    [InlineData(" 7\n")]
    public void MalformedRecordsAreNeverTreatedAsExited(string record)
    {
        Assert.False(DshFileLock.HolderExited(record));
    }

    [Fact]
    public void DeadHolderRecordIsExited()
    {
        Assert.True(DshFileLock.HolderExited(DeadPid + "\n"));
    }

    [Fact]
    public void LockOfADeadHolderIsTakenOver()
    {
        using var temp = new TempDir();
        var target = temp.File("doc.yaml");
        var lockPath = target + ".lock";
        File.WriteAllText(lockPath, DeadPid + "\n");
        var clock = Stopwatch.StartNew();

        var ran = DshFileLock.Run(target, () => true, waitMs: 5000);

        Assert.True(ran);
        Assert.True(clock.ElapsedMilliseconds < 2000, "takeover should be immediate");
        Assert.False(File.Exists(lockPath));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.takeover-*"));
    }

    [Fact]
    public void ALiveHoldersLockIsNotTakenOverEvenIfTheClaimWouldBeFree()
    {
        // pid 1 always exists (init), possibly under another user: kill(1,0) is EPERM or 0, never ESRCH.
        if (OperatingSystem.IsWindows()) { return; }
        using var temp = new TempDir();
        var target = temp.File("doc.yaml");
        File.WriteAllText(target + ".lock", "1\n");

        Assert.Throws<DshLockTimeoutException>(() => DshFileLock.Run(target, () => 1, waitMs: 250));
        Assert.Equal("1\n", File.ReadAllText(target + ".lock"));
    }

    [Fact]
    public void ExistingClaimFileBlocksTakeoverUntilItIsGone()
    {
        using var temp = new TempDir();
        var target = temp.File("doc.yaml");
        var lockPath = target + ".lock";
        var record = DeadPid + "\n";
        File.WriteAllText(lockPath, record);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(record))).ToLowerInvariant()[..16];
        var claim = lockPath + ".takeover-" + digest;
        File.WriteAllText(claim, "1\n");

        Assert.Throws<DshLockTimeoutException>(() => DshFileLock.Run(target, () => 1, waitMs: 250));
        Assert.True(File.Exists(lockPath));

        File.Delete(claim);
        Assert.Equal(7, DshFileLock.Run(target, () => 7, waitMs: 2000));
    }

    [Fact]
    public void LockIsRemovedWhenTheOperationThrows()
    {
        using var temp = new TempDir();
        var target = temp.File("doc.yaml");

        Assert.Throws<InvalidOperationException>(() => DshFileLock.Run(target, () => throw new InvalidOperationException("x")));

        Assert.False(File.Exists(target + ".lock"));
    }

    [Fact]
    public void LockSerializesConcurrentWriters()
    {
        using var temp = new TempDir();
        var target = temp.File("counter.txt");
        File.WriteAllText(target, "0");

        Parallel.For(0, 8, _ =>
        {
            DshFileLock.Run(target, () =>
            {
                var n = int.Parse(File.ReadAllText(target));
                Thread.Sleep(5);
                File.WriteAllText(target, (n + 1).ToString());
            }, waitMs: 20000);
        });

        Assert.Equal("8", File.ReadAllText(target));
    }

    [Fact]
    public void MissingDirectoryFailsFastInsteadOfWaiting()
    {
        using var temp = new TempDir();
        var clock = Stopwatch.StartNew();

        Assert.ThrowsAny<IOException>(() => DshFileLock.Run(temp.File("nope/doc.yaml"), () => 1, waitMs: 5000));

        Assert.True(clock.ElapsedMilliseconds < 1000);
    }
}
