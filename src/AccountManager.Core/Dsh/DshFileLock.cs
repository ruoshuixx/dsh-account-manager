using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AccountManager.Core.Dsh;

/// <summary>
/// Cross-process writer lock compatible with dsh's <c>withFileLock</c>
/// (<c>@deepseek-ai/dsh-atomic-write</c>). The lock is the exclusively created sibling
/// <c>&lt;file&gt;.lock</c> (mode 0600) that holds <c>"&lt;pid&gt;\n"</c>. A contender backs off exponentially
/// (20 ms doubling to 200 ms). A lock whose recorded holder process no longer exists is taken over
/// through a <c>&lt;lock&gt;.takeover-&lt;first 16 hex of sha256(record)&gt;</c> claim file, mirroring dsh's
/// <c>takeOverExitedLock</c>; any other lock (including one with an incomplete record) is waited for.
/// </summary>
public static class DshFileLock
{
    public const int RetryInitialMs = 20;
    public const int RetryMaxMs = 200;

    /// <summary>dsh's default wait when a caller states no limit (file work alone).</summary>
    public const int DefaultWaitMs = 2000;

    /// <summary>dsh's <c>DOCUMENT_LOCK_WAIT_MS</c> for the credentials document.</summary>
    public const int DocumentWaitMs = 30000;

    public static string LockPathFor(string filename) => filename + ".lock";

    public static void Run(string filename, Action operation, int waitMs = DefaultWaitMs, CancellationToken cancellationToken = default)
        => Run<object?>(filename, () => { operation(); return null; }, waitMs, cancellationToken);

    public static T Run<T>(string filename, Func<T> operation, int waitMs = DefaultWaitMs, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);
        ArgumentNullException.ThrowIfNull(operation);
        var lockPath = LockPathFor(Path.GetFullPath(filename));
        Acquire(lockPath, waitMs, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return operation();
        }
        finally
        {
            try { File.Delete(lockPath); } catch { /* a failed release must not mask the operation's outcome */ }
        }
    }

    private static void Acquire(string lockPath, int waitMs, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var delay = RetryInitialMs;
        var retriedUnconfirmed = false;
        for (; ; )
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                CreateLockFile(lockPath);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (ex is DirectoryNotFoundException) { throw; }
                if (!PathExists(lockPath))
                {
                    // Not provably contention: the holder may have released before the probe.
                    // Retry once; a repeated unconfirmed failure is a real error.
                    if (retriedUnconfirmed) { throw; }
                    retriedUnconfirmed = true;
                }
                else if (TakeOverExitedLock(lockPath))
                {
                    continue;
                }
            }

            if (clock.ElapsedMilliseconds >= waitMs) { throw new DshLockTimeoutException(lockPath); }
            if (ct.WaitHandle.WaitOne(delay)) { ct.ThrowIfCancellationRequested(); }
            delay = Math.Min(delay * 2, RetryMaxMs);
        }
    }

    private static void CreateLockFile(string lockPath)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = DshAtomicWrite.PrivateFileMode; }
        using var stream = new FileStream(lockPath, options);
        try
        {
            var record = Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            stream.Write(record, 0, record.Length);
            stream.Flush();
        }
        catch
        {
            stream.Dispose();
            try { File.Delete(lockPath); } catch { /* best effort */ }
            throw;
        }
    }

    private static bool PathExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch { return false; }
    }

    private static string? ReadLockRecord(string lockPath)
    {
        try { return File.ReadAllText(lockPath, Encoding.UTF8); }
        catch { return null; }
    }

    /// <summary>
    /// Whether the holder a <c>"&lt;pid&gt;\n"</c> record names is proven gone. Own pid, pid 0, pids above
    /// int.MaxValue and any malformed record are never "exited".
    /// </summary>
    internal static bool HolderExited(string record)
    {
        if (record.Length < 2 || record[^1] != '\n') { return false; }
        var digits = record.AsSpan(0, record.Length - 1);
        foreach (var c in digits) { if (c < '0' || c > '9') { return false; } }
        if (!long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)) { return false; }
        if (pid == 0 || pid > int.MaxValue) { return false; }
        if (pid == Environment.ProcessId) { return false; }
        return !ProcessExists((int)pid);
    }

    private static bool TakeOverExitedLock(string lockPath)
    {
        var record = ReadLockRecord(lockPath);
        if (record is null || !HolderExited(record)) { return false; }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record))).ToLowerInvariant()[..16];
        var claim = lockPath + ".takeover-" + digest;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = DshAtomicWrite.PrivateFileMode; }
            using var stream = new FileStream(claim, options);
            var body = Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            stream.Write(body, 0, body.Length);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        try
        {
            if (ReadLockRecord(lockPath) != record || !HolderExited(record)) { return false; }
            try { File.Delete(lockPath); }
            catch { return false; }
            return true;
        }
        finally
        {
            try { File.Delete(claim); } catch { /* best effort */ }
        }
    }

    private static bool ProcessExists(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch { return true; }
        }

        try
        {
            if (kill(pid, 0) == 0) { return true; }
            var errno = Marshal.GetLastPInvokeError();
            return errno != ESRCH; // EPERM: exists under another user
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Directory.Exists("/proc/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private const int ESRCH = 3;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
