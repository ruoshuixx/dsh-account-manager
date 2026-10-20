using System.Runtime.InteropServices;

namespace AccountManager.Core.Dsh;

/// <summary>Linux-only executable resolution shared by the curl and dsh launcher lookups.</summary>
internal static class UnixExecutable
{
    /// <summary>
    /// The production executable check: canonicalize the whole path (parent components included)
    /// with the real <c>realpath(3)</c>, require an existing, non-empty ordinary file, then confirm
    /// *effective* execute access for the current user with
    /// <c>faccessat(AT_FDCWD, path, X_OK, AT_EACCESS)</c> — the permission an <c>execve</c> would
    /// actually be granted, not merely the presence of some execute bit. Linux only; null on other
    /// hosts. Best-effort, deliberately not claimed to be race-proof.
    /// </summary>
    internal static string? ResolveOrdinaryExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !OperatingSystem.IsLinux()) { return null; }

        try
        {
            var resolved = RealPath(path);
            if (resolved is null) { return null; }

            var info = new FileInfo(resolved);
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0) { return null; }

            // A usable executable is a non-empty ordinary file; device/FIFO/socket entries report
            // no usable length and are rejected.
            long length;
            try { length = info.Length; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            if (length <= 0) { return null; }

            return HasEffectiveExecuteAccess(resolved) ? resolved : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static bool HasEffectiveExecuteAccess(string path)
        => faccessat(AtFdcwd, path, XOk, AtEaccess) == 0;

    private const int AtFdcwd = -100;
    private const int XOk = 1;
    private const int AtEaccess = 0x200;

    [DllImport("libc", SetLastError = true)]
    private static extern int faccessat(int dirfd, string pathname, int mode, int flags);

    private static string? RealPath(string path)
    {
        var buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.AllocHGlobal(4096);
            return realpath(path, buffer) == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(buffer);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath(string path, IntPtr resolved);
}
