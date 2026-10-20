using System.Diagnostics;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace AccountManager.Core.Dsh;

/// <summary>How to start the dsh command line: an executable plus leading arguments (a shim or <c>node bin.js</c>).</summary>
public sealed record DshLauncher(string FileName, IReadOnlyList<string> LeadingArguments)
{
    /// <summary>
    /// Finds the <c>dsh</c> command: <c>~/.local/bin/dsh</c> and PATH on Linux; <c>dsh.cmd</c>/<c>dsh.exe</c> on PATH on Windows.
    /// </summary>
    public static DshLauncher? Find()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "dsh.cmd", "dsh.exe", "dsh.bat" } : new[] { "dsh" };
        var directories = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path)) { directories.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)); }

        foreach (var directory in directories)
        {
            if (!Path.IsPathRooted(directory)) { continue; }
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (OperatingSystem.IsWindows())
                {
                    if (File.Exists(candidate)) { return new DshLauncher(candidate, Array.Empty<string>()); }
                }
                else if (UnixExecutable.ResolveOrdinaryExecutable(candidate) is not null)
                {
                    return new DshLauncher(candidate, Array.Empty<string>());
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Confirms a provider write the way dsh itself sees it: runs <c>dsh --profile &lt;profile&gt; --dump-config</c> with
/// <c>DSH_HOME</c> set and reads the composed <c>llm-pi-ai</c> provider. Never starts a session or sends a
/// request. A missing dsh or a failing command is reported as unavailable (a warning, not an error): the
/// write itself already succeeded.
/// </summary>
public sealed class DshRuntimeVerifier : IProviderRuntimeVerifier, IDshConfigDumper
{
    public const string AppliedMessage = "dsh 已识别该供应商配置（dsh --dump-config 校验通过）；运行中的 dsh 会自动热加载，无需重启。";
    public const string PendingMessage = "配置已写入，但 dsh --dump-config 中未看到预期的供应商配置；请确认 DSH 主目录与配置档案和正在运行的 dsh 一致，不要重复添加。";
    public const string UnavailableMessage = "已写入，未能校验：找不到 dsh 命令或校验失败。这不代表保存失败，也不会自动重试写入。";

    private readonly Func<DshLauncher?> _locator;
    private readonly TimeSpan _timeout;

    public DshRuntimeVerifier(Func<DshLauncher?>? locator = null, TimeSpan? timeout = null)
    {
        _locator = locator ?? DshLauncher.Find;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task<ProviderRuntimeVerification> VerifyAsync(DshHome home, string providerId, string expectedApi, CancellationToken ct = default)
    {
        try
        {
            var launcher = _locator();
            if (launcher is null) { return Unavailable(); }

            var output = await RunAsync(launcher, home, ct).ConfigureAwait(false);
            if (output is null) { return Unavailable(); }
            var api = ReadProviderApi(output, providerId, out var found);
            if (!found) { return new(ProviderRuntimeState.Pending, PendingMessage); }
            return string.Equals(api, expectedApi, StringComparison.Ordinal)
                ? new(ProviderRuntimeState.Applied, AppliedMessage)
                : new(ProviderRuntimeState.Pending, PendingMessage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Unavailable(); } // Never expose raw output.
    }

    /// <summary>The composed configuration (<c>--dump-config</c> output), or null when dsh cannot be run.</summary>
    public async Task<string?> DumpConfigAsync(DshHome home, CancellationToken ct = default)
    {
        try
        {
            var launcher = _locator();
            return launcher is null ? null : await RunAsync(launcher, home, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static ProviderRuntimeVerification Unavailable() => new(ProviderRuntimeState.Unavailable, UnavailableMessage);

    private async Task<string?> RunAsync(DshLauncher launcher, DshHome home, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(launcher.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in launcher.LeadingArguments) { psi.ArgumentList.Add(arg); }
        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add(home.Profile);
        psi.ArgumentList.Add("--dump-config");
        psi.Environment["DSH_HOME"] = home.Root;
        psi.Environment["DSH_TELEMETRY_DISABLED"] = "1";

        using var process = Process.Start(psi);
        if (process is null) { return null; }
        process.StandardInput.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        _ = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            return process.ExitCode == 0 ? text : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return null;
        }
    }

    /// <summary>Reads <c>api</c> of the composed <c>llm-pi-ai</c> provider from a <c>--dump-config</c> YAML text.</summary>
    internal static string? ReadProviderApi(string yaml, string providerId, out bool found)
    {
        found = false;
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlSequenceNode entries) { return null; }
        string? api = null;
        foreach (var entry in entries.Children.OfType<YamlMappingNode>())
        {
            if (Scalar(entry, "id") != "llm-pi-ai") { continue; }
            if (Child(entry, "config") is not YamlMappingNode config || Child(config, "providers") is not YamlMappingNode providers) { continue; }
            if (Child(providers, providerId) is not YamlMappingNode provider) { continue; }
            found = true;
            api = Scalar(provider, "api");
        }

        return api;
    }

    private static YamlNode? Child(YamlMappingNode map, string key)
    {
        foreach (var (k, v) in map.Children) { if (k is YamlScalarNode s && s.Value == key) { return v; } }
        return null;
    }

    private static string? Scalar(YamlMappingNode map, string key) => (Child(map, key) as YamlScalarNode)?.Value;
}
