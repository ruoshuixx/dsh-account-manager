using AccountManager.Core.Logging;

namespace AccountManager.App.Services;

/// <summary>What <see cref="LegacyDataMigration"/> did, for the log and the one-time notice.</summary>
public sealed record LegacyMigrationReport(string From, string To, bool SettingsMoved, int LogsMoved, int LogsExpired, IReadOnlyList<string> Problems)
{
    public bool Any => SettingsMoved || LogsMoved > 0 || LogsExpired > 0 || Problems.Count > 0;

    public string Describe()
    {
        var lines = new List<string>
        {
            "设置和日志以前放在程序所在的文件夹（" + From + "），现在统一放到：",
            To,
            string.Empty,
        };
        if (SettingsMoved) { lines.Add("· 设置：已复制到新位置，原文件已清除。"); }
        if (LogsMoved > 0) { lines.Add("· 日志：已迁移 " + LogsMoved + " 个，原文件已清除。"); }
        if (LogsExpired > 0) { lines.Add("· 超过 " + (int)AppLog.Retention.TotalDays + " 天的旧日志 " + LogsExpired + " 个已清除。"); }
        if (Problems.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("以下没能处理，仍保留在原位置：");
            lines.AddRange(Problems.Select(p => "· " + p));
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Versions before 0.2.3 kept <c>avalonia-settings.json</c> and <c>logs\</c> next to the executable (on the Desktop for most
/// people). This copies them into the per-user data folder, checks every copy, and only then removes the original. Logs
/// older than the retention period are simply deleted. Only those two kinds of file are ever touched; anything that
/// cannot be moved stays where it is and is reported.
/// </summary>
public static class LegacyDataMigration
{
    public const string SettingsFileName = "avalonia-settings.json";

    private static LegacyMigrationReport? _pending;

    /// <summary>Runs once at startup, before the settings are read. Never throws.</summary>
    public static void RunAtStartup()
    {
        try
        {
            var report = Run(AppContext.BaseDirectory, PortablePaths.Data, DateTime.Now, AppLog.Retention);
            if (report is { Any: true }) { _pending = report; }
        }
        catch
        {
            // Migration must never stop the app from starting.
        }
    }

    /// <summary>The report of this start's migration, once (for the notice dialog and the log).</summary>
    public static LegacyMigrationReport? TakePending()
    {
        var report = _pending;
        _pending = null;
        return report;
    }

    public static LegacyMigrationReport? Run(string legacyDirectory, string newDirectory, DateTime now, TimeSpan retention)
    {
        if (string.IsNullOrWhiteSpace(legacyDirectory) || string.IsNullOrWhiteSpace(newDirectory)) { return null; }
        var from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyDirectory));
        var to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(from, to, comparison)) { return null; }

        var legacySettings = Path.Combine(from, SettingsFileName);
        var legacyLogs = Path.Combine(from, "logs");
        if (!File.Exists(legacySettings) && !Directory.Exists(legacyLogs)) { return null; }

        var problems = new List<string>();
        var settingsMoved = false;
        var logsMoved = 0;
        var logsExpired = 0;

        try { Directory.CreateDirectory(to); }
        catch (Exception ex)
        {
            return new LegacyMigrationReport(from, to, false, 0, 0, new[] { "无法创建新位置：" + ex.Message });
        }

        if (File.Exists(legacySettings))
        {
            try
            {
                MoveSettings(legacySettings, Path.Combine(to, SettingsFileName));
                settingsMoved = true;
            }
            catch (Exception ex) { problems.Add(SettingsFileName + "：" + ex.Message); }
        }

        if (Directory.Exists(legacyLogs))
        {
            var newLogs = Path.Combine(to, "logs");
            foreach (var pattern in new[] { "run-*.log", "errors-*.log" })
            {
                foreach (var file in Directory.EnumerateFiles(legacyLogs, pattern).ToList())
                {
                    try
                    {
                        if (now - File.GetLastWriteTime(file) > retention)
                        {
                            File.Delete(file);
                            logsExpired++;
                            continue;
                        }

                        CopyVerified(file, Path.Combine(newLogs, Path.GetFileName(file)), keepTimestamp: true, skipIfSameSize: true);
                        File.Delete(file);
                        logsMoved++;
                    }
                    catch (Exception ex) { problems.Add("logs\\" + Path.GetFileName(file) + "：" + ex.Message); }
                }
            }

            try { Directory.Delete(legacyLogs, recursive: false); } // only when nothing else is left in it
            catch { /* not empty or in use: leave it */ }
        }

        return new LegacyMigrationReport(from, to, settingsMoved, logsMoved, logsExpired, problems);
    }

    /// <summary>
    /// The settings file of the new location wins unless the old one is strictly newer (older versions also saved a copy there
    /// on every change, so normally they are equal). A replaced file is kept once as <c>.before-migration</c>.
    /// </summary>
    private static void MoveSettings(string source, string destination)
    {
        if (File.Exists(destination))
        {
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(destination))
                && File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(destination))
            {
                File.Copy(destination, destination + ".before-migration", overwrite: true);
                CopyVerified(source, destination, keepTimestamp: false);
            }
        }
        else
        {
            CopyVerified(source, destination, keepTimestamp: false);
        }

        File.Delete(source);
    }

    /// <summary>Copies through a temporary file and compares the bytes before the destination is put in place.</summary>
    private static void CopyVerified(string source, string destination, bool keepTimestamp, bool skipIfSameSize = false)
    {
        // A log of the same name and size is the copy of an earlier, interrupted run; nothing to do.
        if (skipIfSameSize && File.Exists(destination) && new FileInfo(destination).Length == new FileInfo(source).Length) { return; }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".migrating";
        try
        {
            File.Copy(source, temp, overwrite: true);
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(temp))) { throw new IOException("复制后内容不一致"); }
            if (keepTimestamp) { File.SetLastWriteTime(temp, File.GetLastWriteTime(source)); }
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }
}
