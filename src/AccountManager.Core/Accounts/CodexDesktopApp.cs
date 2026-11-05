using System.Diagnostics;

namespace AccountManager.Core.Accounts;

/// <summary>The Codex desktop app keeps its login in memory, so a switch only takes effect after it restarts.</summary>
public interface ICodexDesktopApp
{
    /// <summary>Whether the desktop app is running (always false where there is no desktop app).</summary>
    bool IsRunning();

    /// <summary>Quits every process of the app and opens it again. Returns a short result for the status bar.</summary>
    Task<string> RestartAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Windows: the Microsoft Store app (package <c>OpenAI.Codex</c>, under <c>WindowsApps</c>) or a window process named
/// Codex outside an npm install. It is asked to close, then whatever is left of it (its bundled app-server included) is
/// ended, and it is opened again through its package (Store) or its executable. Other platforms have no desktop app;
/// the CLI reads auth.json at the start of each session.
/// </summary>
public sealed class CodexDesktopApp : ICodexDesktopApp
{
    private const string StorePackage = "OpenAI.Codex";
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(6);

    public bool IsRunning()
    {
        if (!OperatingSystem.IsWindows()) { return false; }
        var found = Find();
        try { return found.Count > 0; }
        finally { foreach (var (p, _) in found) { p.Dispose(); } }
    }

    public async Task<string> RestartAsync(CancellationToken cancellationToken = default)
    {
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

    public static bool IsStorePath(string path)
        => path.IndexOf(@"\WindowsApps\" + StorePackage + "_", StringComparison.OrdinalIgnoreCase) >= 0;

    private static List<(Process Process, string Path)> Find()
    {
        var found = new List<(Process, string)>();
        foreach (var p in Process.GetProcesses())
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* system or elevated process */ }
            if (path is not null && IsDesktopPath(path, HasWindow(p))) { found.Add((p, path)); }
            else { p.Dispose(); }
        }

        // The windowed app found by name: its helpers live in the same folder.
        var folders = found.Where(f => !IsStorePath(f.Item2)).Select(f => Path.GetDirectoryName(f.Item2)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (folders.Count == 0) { return found; }
        foreach (var p in Process.GetProcesses())
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            if (path is not null && found.All(f => f.Item1.Id != p.Id)
                && folders.Any(d => path.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add((p, path));
            }
            else { p.Dispose(); }
        }

        return found;
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
            const string script = "$p = Get-AppxPackage -Name " + StorePackage + " | Select-Object -First 1; "
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
