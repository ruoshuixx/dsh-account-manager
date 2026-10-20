using AccountManager.Core.Dsh;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

public class DshAtomicWriteTests
{
    [Fact]
    public void WritesContentWith0600AndCreatesDirectoriesAt0700()
    {
        using var temp = new TempDir();
        var path = temp.File("a/b/file.txt");

        DshAtomicWrite.WriteText(path, "héllo 中文");

        Assert.Equal("héllo 中文", File.ReadAllText(path));
        Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "no BOM");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(temp.File("a/b")));
        }
    }

    [Fact]
    public void ReplacesAWiderExistingFileWithTheNarrowMode()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var temp = new TempDir();
        var path = temp.File("file.txt");
        File.WriteAllText(path, "old");
        File.SetUnixFileMode(path, (UnixFileMode)0b110_110_110);

        DshAtomicWrite.WriteText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void LeavesNoTempFilesAndReplacesASymlinkInsteadOfWritingThrough()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var temp = new TempDir();
        var referent = temp.File("referent.txt");
        File.WriteAllText(referent, "untouched");
        var link = temp.File("link.txt");
        File.CreateSymbolicLink(link, referent);

        DshAtomicWrite.WriteText(link, "via link");

        Assert.Equal("untouched", File.ReadAllText(referent));
        Assert.Null(new FileInfo(link).LinkTarget);
        Assert.Equal("via link", File.ReadAllText(link));
        Assert.DoesNotContain(Directory.GetFiles(temp.Path), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void FailureRemovesTheTempFileAndKeepsTheOldContent()
    {
        using var temp = new TempDir();
        var target = temp.File("dir-target");
        Directory.CreateDirectory(target); // a rename of a file over a directory must fail

        Assert.ThrowsAny<Exception>(() => DshAtomicWrite.WriteText(target, "x"));

        Assert.True(Directory.Exists(target));
        Assert.DoesNotContain(Directory.GetFiles(temp.Path), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }
}
