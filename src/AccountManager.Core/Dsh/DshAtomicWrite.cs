using System.Security.Cryptography;
using System.Text;

namespace AccountManager.Core.Dsh;

/// <summary>
/// Atomic file replacement compatible with dsh's <c>writeFileAtomic</c>
/// (<c>@deepseek-ai/dsh-atomic-write</c>): the content goes to an exclusively created sibling
/// <c>&lt;file&gt;.&lt;12 hex&gt;.tmp</c> that carries the final permission bits, then the sibling is renamed
/// over the target, so readers see either the old or the new complete content.
/// </summary>
public static class DshAtomicWrite
{
    public const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // Windows replacement retry cadence, mirrored from dsh.
    internal const int WindowsRenameRetryInitialMs = 20;
    internal const int WindowsRenameRetryMaxMs = 200;
    internal const int WindowsRenameRetryLimit = 8;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static void WriteText(string filename, string content,
        UnixFileMode mode = PrivateFileMode, UnixFileMode? directoryMode = PrivateDirectoryMode)
        => WriteBytes(filename, Utf8NoBom.GetBytes(content), mode, directoryMode);

    public static void WriteBytes(string filename, byte[] content,
        UnixFileMode mode = PrivateFileMode, UnixFileMode? directoryMode = PrivateDirectoryMode)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);
        ArgumentNullException.ThrowIfNull(content);
        var full = Path.GetFullPath(filename);
        var directory = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(directory))
        {
            if (directoryMode is { } dirMode && !OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory, dirMode); }
            else { Directory.CreateDirectory(directory); }
        }

        var temp = full + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = mode; }
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(true);
            }

            // The fresh inode already carries the requested mode; re-assert it in case a
            // restrictive-umask-independent platform widened it.
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(temp, mode); }
            RenameOver(temp, full);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    private static void RenameOver(string temp, string target)
    {
        var delay = WindowsRenameRetryInitialMs;
        for (var retries = 0; ; retries++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (OperatingSystem.IsWindows()
                && (ex is IOException || ex is UnauthorizedAccessException)
                && ex is not DirectoryNotFoundException
                && retries < WindowsRenameRetryLimit)
            {
                // Transient sharing violation (antivirus, a reader holding the file): retry.
            }

            Thread.Sleep(delay);
            delay = Math.Min(delay * 2, WindowsRenameRetryMaxMs);
        }
    }
}
