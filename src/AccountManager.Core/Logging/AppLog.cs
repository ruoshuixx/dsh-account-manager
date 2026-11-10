using System.Text;

namespace AccountManager.Core.Logging;

public enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>
/// Writes redacted run/error logs to a logs directory and echoes every entry to the UI.
/// Equivalent to <c>Initialize-Logging</c> / <c>Write-LocalLog</c> / <c>Write-LocalError</c>.
/// </summary>
public sealed class AppLog
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Security.SecretRegistry _secrets;
    private DateTime _nextPrune;

    /// <summary>Run and error logs older than this are deleted (checked at start and once a day while running).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(10);

    public AppLog(Security.SecretRegistry secrets, string directory)
    {
        _secrets = secrets;
        _directory = directory;
        Directory.CreateDirectory(directory);
        PruneOld(directory, Retention, DateTime.Now);
        _nextPrune = DateTime.Now.AddDays(1);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..6];
        RunLogPath = Path.Combine(directory, "run-" + stamp + ".log");
        ErrorLogPath = Path.Combine(directory, "errors-" + stamp + ".log");
        File.WriteAllText(RunLogPath, string.Empty, new UTF8Encoding(false));
        File.WriteAllText(ErrorLogPath, string.Empty, new UTF8Encoding(false));
    }

    /// <summary>
    /// Deletes <c>run-*.log</c> / <c>errors-*.log</c> files not written for longer than <paramref name="maxAge"/>.
    /// Other files are never touched. Best effort: returns how many were removed and never throws.
    /// </summary>
    public static int PruneOld(string directory, TimeSpan maxAge, DateTime now, params string[] keep)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(directory)) { return 0; }
            foreach (var pattern in new[] { "run-*.log", "errors-*.log" })
            {
                foreach (var file in Directory.EnumerateFiles(directory, pattern))
                {
                    try
                    {
                        if (keep.Any(k => string.Equals(Path.GetFullPath(k), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))) { continue; }
                        if (now - File.GetLastWriteTime(file) <= maxAge) { continue; }
                        File.Delete(file);
                        removed++;
                    }
                    catch
                    {
                        // A log in use or without permission stays; try again next time.
                    }
                }
            }
        }
        catch
        {
            // Cleanup is best-effort.
        }

        return removed;
    }

    public string RunLogPath { get; }

    public string ErrorLogPath { get; }

    public event EventHandler<LogEntry>? EntryWritten;

    public void Info(string message) => Write(message, LogLevel.Info);

    public void Warn(string message) => Write(message, LogLevel.Warn);

    public void Error(string message, Exception? ex = null)
    {
        var line = ex is null ? message : message + "：" + ex.Message;
        Write(line, LogLevel.Error);
    }

    private void Write(string message, LogLevel level)
    {
        var safe = _secrets.Redact(message);
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level.ToString().ToUpperInvariant() + "] " + safe + Environment.NewLine;
        lock (_gate)
        {
            if (DateTime.Now >= _nextPrune)
            {
                _nextPrune = DateTime.Now.AddDays(1);
                PruneOld(_directory, Retention, DateTime.Now, RunLogPath, ErrorLogPath);
            }

            try
            {
                File.AppendAllText(RunLogPath, line, new UTF8Encoding(false));
                if (level == LogLevel.Error) { File.AppendAllText(ErrorLogPath, line, new UTF8Encoding(false)); }
            }
            catch
            {
                // Disk logging is best-effort; never let it break an operation.
            }
        }

        EntryWritten?.Invoke(this, new LogEntry(DateTime.Now, level, safe));
    }
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);
