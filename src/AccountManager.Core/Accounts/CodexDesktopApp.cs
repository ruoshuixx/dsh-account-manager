using System.Diagnostics;

namespace AccountManager.Core.Accounts;

/// <summary>The Codex desktop app keeps its login in memory, so a switch only takes effect after it restarts.</summary>
public interface ICodexDesktopApp
{
    /// <summary>What it is called in messages: the desktop app (Windows) or the background service (Linux).</summary>
    string Name => "Codex 桌面版";

    /// <summary>Whether the desktop app is running (always false where there is no desktop app).</summary>
    bool IsRunning();

    /// <summary>Quits every process of the app and opens it again. Returns a short result for the status bar.</summary>
    Task<string> RestartAsync(CancellationToken cancellationToken = default);

    /// <summary>For the log when nothing was found: the processes that look like Codex / ChatGPT and their paths.</summary>
    string Describe() => string.Empty;
}

/// <summary>
/// Windows: the Microsoft Store app (package <c>OpenAI.Codex</c>, under <c>WindowsApps</c>) or a window process named
/// Codex outside an npm install. It is asked to close, then whatever is left of it (its bundled app-server included) is
/// ended, and it is opened again through its package (Store) or its executable. Linux: the desktop app (OpenAI's .deb,
/// package <c>chatgpt</c>, ChatGPT with Codex) is found by its installed files, asked to quit (SIGTERM), ended if it
/// lingers and opened again through its .desktop entry; Codex's managed app-server daemon (remote control, IDE and SSH
/// clients) is restarted with its own <c>codex app-server daemon restart</c>. CLI sessions read auth.json when they
/// start and are left alone.
/// </summary>
public sealed class CodexDesktopApp : ICodexDesktopApp
{
    /// <summary>Debian packages of the Linux desktop app.</summary>
    private static readonly string[] LinuxPackages = { "chatgpt", "codex-desktop", "codex" };

    public string Name => OperatingSystem.IsLinux() && LinuxDesktop().Executables.Count == 0 ? "Codex 后台服务" : "Codex 桌面版";

    /// <summary>Store packages are OpenAI.* (the Codex app was OpenAI.Codex; it is being rebranded as ChatGPT with Codex).</summary>
    private const string StorePublisherPrefix = "OpenAI.";
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(6);

    public bool IsRunning()
    {
        if (OperatingSystem.IsLinux()) { return FindLinuxDesktop().Count > 0 || FindDaemon() is not null; }
        if (!OperatingSystem.IsWindows()) { return false; }
        var found = Find();
        try { return found.Count > 0; }
        finally { foreach (var (p, _) in found) { p.Dispose(); } }
    }

    public async Task<string> RestartAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsLinux())
        {
            var results = new List<string>();
            if (FindLinuxDesktop().Count > 0) { results.Add(await RestartLinuxDesktopAsync(cancellationToken).ConfigureAwait(false)); }
            if (FindDaemon() is not null) { results.Add(await RestartDaemonAsync(cancellationToken).ConfigureAwait(false)); }
            return results.Count == 0 ? "Codex 没有在运行" : string.Join("；", results);
        }
        if (!OperatingSystem.IsWindows()) { return "这台电脑没有 Codex 桌面版"; }
        var found = Find();
        if (found.Count == 0) { return "Codex 桌面版没有在运行"; }

        // What to open again: the windowed process's executable.
        var main = found.FirstOrDefault(f => HasWindow(f.Process)).Path ?? found[0].Path;
        try
        {
            foreach (var (p, _) in found.Where(f => HasWindow(f.Process)))
            {
                try { p.CloseMainWindow(); } catch { /* it may already be gone */ }
            }

            var deadline = DateTime.UtcNow + CloseWait;
            while (DateTime.UtcNow < deadline && found.Any(f => !Exited(f.Process)))
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            // The app may stay in the tray instead of quitting: end what is left.
            foreach (var (p, _) in found.Where(f => !Exited(f.Process)))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* exited meanwhile or not ours */ }
            }

            foreach (var (p, _) in found)
            {
                try { await p.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { /* not ours to wait for, or still closing: opening again is still right */ }
            }
        }
        finally
        {
            foreach (var (p, _) in found) { p.Dispose(); }
        }

        if (!IsStorePath(main) && !File.Exists(main)) { return "已关闭 Codex 桌面版，但没找到它的程序文件，请手动打开"; }
        try
        {
            Open(main);
            return "已重启 Codex 桌面版";
        }
        catch (Exception ex)
        {
            return "已关闭 Codex 桌面版，但没能重新打开（" + ex.Message + "），请手动打开";
        }
    }

    // ---- Linux desktop app ----------------------------------------------------------

    private static (IReadOnlySet<string> Executables, string? DesktopEntry) _linuxDesktop = (new HashSet<string>(), null);
    private static DateTime _linuxDesktopRead;

    /// <summary>The installed desktop app's executables and .desktop entry (from dpkg; re-read at most once a minute).</summary>
    private static (IReadOnlySet<string> Executables, string? DesktopEntry) LinuxDesktop()
    {
        if (DateTime.UtcNow - _linuxDesktopRead < TimeSpan.FromMinutes(1)) { return _linuxDesktop; }
        var files = new List<string>();
        foreach (var package in LinuxPackages)
        {
            try
            {
                var psi = new ProcessStartInfo("dpkg-query") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "-L", package }) { psi.ArgumentList.Add(a); }
                using var p = Process.Start(psi);
                if (p is null) { continue; }
                var output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(5000) || p.ExitCode != 0) { continue; }
                files.AddRange(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            catch { /* no dpkg */ }
        }

        _linuxDesktop = SelectLinuxDesktop(files, f => File.Exists(f) && !Directory.Exists(f) && IsExecutable(f));
        _linuxDesktopRead = DateTime.UtcNow;
        return _linuxDesktop;
    }

    /// <summary>From a package's file list: its executables (not the bundled codex CLI) and its .desktop entry. Public for tests.</summary>
    public static (IReadOnlySet<string> Executables, string? DesktopEntry) SelectLinuxDesktop(IEnumerable<string> files, Func<string, bool> isExecutable)
    {
        var list = files.ToList();
        var executables = list
            .Where(f => !f.EndsWith(".so", StringComparison.Ordinal) && !f.Contains(".so.", StringComparison.Ordinal)
                && Path.GetFileName(f) is not ("codex" or "chrome-sandbox" or "chrome_crashpad_handler")
                && isExecutable(f))
            .ToHashSet(StringComparer.Ordinal);
        var entry = list.FirstOrDefault(f => f.StartsWith("/usr/share/applications/", StringComparison.Ordinal) && f.EndsWith(".desktop", StringComparison.Ordinal));
        return (executables, entry);
    }

    private static bool IsExecutable(string path)
    {
        try { return !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0; }
        catch { return false; }
    }

    /// <summary>Running processes of the desktop app (pid, executable).</summary>
    private static List<(int Pid, string Exe)> FindLinuxDesktop()
    {
        var found = new List<(int, string)>();
        var executables = LinuxDesktop().Executables;
        if (executables.Count == 0) { return found; }
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) { continue; }
                try
                {
                    var exe = new FileInfo(Path.Combine(dir, "exe")).ResolveLinkTarget(true)?.FullName;
                    if (exe is not null && executables.Contains(exe)) { found.Add((pid, exe)); }
                }
                catch { /* gone, or another user's process */ }
            }
        }
        catch { /* no /proc */ }

        return found;
    }

    private static async Task<string> RestartLinuxDesktopAsync(CancellationToken cancellationToken)
    {
        var found = FindLinuxDesktop();
        if (found.Count == 0) { return "Codex 桌面版没有在运行"; }
        var main = found.MinBy(f => f.Pid).Exe;
        foreach (var (pid, _) in found) { Signal(pid, "TERM"); }

        var deadline = DateTime.UtcNow + CloseWait;
        while (DateTime.UtcNow < deadline && FindLinuxDesktop().Count > 0) { await Task.Delay(250, cancellationToken).ConfigureAwait(false); }
        foreach (var (pid, _) in FindLinuxDesktop()) { Signal(pid, "KILL"); }
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);

        try
        {
            var entry = LinuxDesktop().DesktopEntry;
            // The .desktop entry starts it the way the menu does; setsid detaches it from this program either way.
            var psi = entry is not null && File.Exists("/usr/bin/gtk-launch")
                ? new ProcessStartInfo("setsid") { ArgumentList = { "-f", "gtk-launch", Path.GetFileNameWithoutExtension(entry) } }
                : new ProcessStartInfo("setsid") { ArgumentList = { "-f", main } };
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动");
            p.WaitForExit(10000);
            return "已重启 Codex 桌面版";
        }
        catch (Exception ex)
        {
            return "已关闭 Codex 桌面版，但没能重新打开（" + ex.Message + "），请手动打开";
        }
    }

    private static void Signal(int pid, string signal)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-" + signal, pid.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false, RedirectStandardError = true });
            p?.WaitForExit(3000);
        }
        catch { /* already gone */ }
    }

    // ---- Linux managed app-server daemon --------------------------------------------------

    /// <summary>Whether a process command line is Codex's managed app-server daemon (not a plain app-server someone started). Public for tests.</summary>
    public static bool IsManagedDaemon(IReadOnlyList<string> args)
        => args.Count >= 3
            && Path.GetFileName(args[0]) == "codex"
            && args.Contains("app-server")
            && (args.Contains("--managed-daemon") || args.Contains("daemon"));

    /// <summary>The codex executable of the running managed daemon, or null when none runs.</summary>
    private static string? FindDaemon()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out _)) { continue; }
                try
                {
                    var args = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    if (!IsManagedDaemon(args)) { continue; }
                    var exe = new FileInfo(Path.Combine(dir, "exe")).ResolveLinkTarget(true)?.FullName ?? args[0];
                    if (File.Exists(exe)) { return exe; }
                }
                catch { /* gone, or another user's process */ }
            }
        }
        catch { /* no /proc */ }

        return null;
    }

    private static async Task<string> RestartDaemonAsync(CancellationToken cancellationToken)
    {
        var exe = FindDaemon();
        if (exe is null) { return "Codex 后台服务没有在运行"; }
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "app-server", "daemon", "restart" }) { psi.ArgumentList.Add(a); }
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 codex");
            var output = p.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = p.StandardError.ReadToEndAsync(cancellationToken);
            await p.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            _ = await output.ConfigureAwait(false);
            var err = (await error.ConfigureAwait(false)).Trim();
            return p.ExitCode == 0 ? "已重启 Codex 后台服务" : "重启 Codex 后台服务失败（退出码 " + p.ExitCode + (err.Length > 0 ? "：" + err.Split('\n')[^1] : string.Empty) + "）";
        }
        catch (TimeoutException) { return "重启 Codex 后台服务超时，请稍后检查"; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return "重启 Codex 后台服务失败：" + ex.Message; }
    }

    /// <summary>Whether this executable path belongs to the desktop app (not the npm CLI). Public for tests.</summary>
    public static bool IsDesktopPath(string path, bool hasWindow)
    {
        if (string.IsNullOrWhiteSpace(path)) { return false; }
        if (IsStorePath(path)) { return true; }
        var name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]);
        return hasWindow
            && string.Equals(name, "Codex", StringComparison.OrdinalIgnoreCase)
            && path.IndexOf("node_modules", StringComparison.OrdinalIgnoreCase) < 0;
    }

    public static bool IsStorePath(string path) => StorePackageName(path) is { } name
        && (name.Contains("Codex", StringComparison.OrdinalIgnoreCase) || BundlesCodex(path));

    /// <summary>The package name ("OpenAI.Codex") of a path under <c>WindowsApps\OpenAI.*_…</c>, else null. Public for tests.</summary>
    public static string? StorePackageName(string path)
    {
        const string marker = @"\WindowsApps\";
        var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) { return null; }
        var folder = path[(at + marker.Length)..].Split('\\')[0];
        if (!folder.StartsWith(StorePublisherPrefix, StringComparison.OrdinalIgnoreCase)) { return null; }
        var underscore = folder.IndexOf('_');
        return underscore > 0 ? folder[..underscore] : null;
    }

    /// <summary>An OpenAI package that ships the codex engine (the ChatGPT app with Codex) counts too.</summary>
    private static bool BundlesCodex(string path)
    {
        const string marker = @"\WindowsApps\";
        var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var root = path[..(at + marker.Length)] + path[(at + marker.Length)..].Split('\\')[0];
        try
        {
            return File.Exists(Path.Combine(root, "app", "resources", "codex.exe"))
                || File.Exists(Path.Combine(root, "app", "resources", "bin", "codex.exe"))
                || File.Exists(Path.Combine(root, "resources", "codex.exe"));
        }
        catch { return false; }
    }

    private static List<(Process Process, string Path)> Find()
    {
        var found = new List<(Process, string)>();
        foreach (var p in Process.GetProcesses())
        {
            var path = ImagePath(p);
            if (path is not null && IsDesktopPath(path, HasWindow(p))) { found.Add((p, path)); }
            else { p.Dispose(); }
        }

        // The windowed app found by name: its helpers live in the same folder.
        var folders = found.Where(f => !IsStorePath(f.Item2)).Select(f => Path.GetDirectoryName(f.Item2)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (folders.Count == 0) { return found; }
        foreach (var p in Process.GetProcesses())
        {
            var path = ImagePath(p);
            if (path is not null && found.All(f => f.Item1.Id != p.Id)
                && folders.Any(d => path.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add((p, path));
            }
            else { p.Dispose(); }
        }

        return found;
    }

    /// <summary>
    /// The process's executable. QueryFullProcessImageName needs only limited query rights, which packaged (Store) and
    /// sandboxed processes still grant; MainModule needs more and is the fallback.
    /// </summary>
    private static string? ImagePath(Process p)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, p.Id);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var buffer = new char[1024];
                    var size = buffer.Length;
                    if (QueryFullProcessImageName(handle, 0, buffer, ref size)) { return new string(buffer, 0, size); }
                }
                finally { CloseHandle(handle); }
            }
        }

        try { return p.MainModule?.FileName; } catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, char[] name, ref int size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public string Describe()
    {
        if (!OperatingSystem.IsWindows()) { return string.Empty; }
        var lines = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string name;
                try { name = p.ProcessName; } catch { continue; }
                if (name.IndexOf("codex", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("chatgpt", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                lines.Add(name + "（" + (ImagePath(p) ?? "路径不可读") + (HasWindow(p) ? "，有窗口" : string.Empty) + "）");
            }
        }

        return lines.Count == 0 ? "没有名字含 Codex / ChatGPT 的进程" : string.Join("；", lines.Distinct().Take(8));
    }

    private static bool HasWindow(Process p)
    {
        try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; }
    }

    private static bool Exited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    private static void Open(string path)
    {
        if (IsStorePath(path))
        {
            // Store apps cannot be started from their WindowsApps folder; open the package's app instead.
            var script = "$p = Get-AppxPackage -Name " + StorePackageName(path) + " | Select-Object -First 1; "
                + "$id = (Get-AppxPackageManifest $p).Package.Applications.Application | Select-Object -First 1 -ExpandProperty Id; "
                + "Start-Process ('shell:AppsFolder\\' + $p.PackageFamilyName + '!' + $id)";
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) { psi.ArgumentList.Add(a); }
            using var ps = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 PowerShell");
            if (!ps.WaitForExit(15000)) { throw new TimeoutException("打开超时"); }
            if (ps.ExitCode != 0) { throw new InvalidOperationException("PowerShell 退出码 " + ps.ExitCode); }
            return;
        }

        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! });
    }
}
