using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace AccountManager.Core.Net;

/// <summary>
/// The proxy the system is set to <em>now</em>, for every request. .NET alone reads only the <c>*_proxy</c> environment
/// variables on Linux (a desktop launcher has none, while GNOME's "system proxy" lives in gsettings) and reads Windows'
/// Internet Settings once at start. So a proxy client switched to "system proxy" after the app opened was missed and
/// only a TUN mode worked. Order: environment variables (as .NET), then Windows Internet Settings / GNOME proxy
/// settings, then a direct connection. A PAC script is left to .NET's own default proxy. Re-read every few seconds.
/// </summary>
public sealed class SystemProxy : IWebProxy
{
    public static readonly SystemProxy Instance = new();

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private (ProxySettings? Settings, DateTime ReadAt) _cache;

    public ICredentials? Credentials { get; set; }

    /// <summary>A handler that uses the current system proxy.</summary>
    public static HttpClientHandler Handler(bool allowRedirect = false, DecompressionMethods decompression = DecompressionMethods.GZip | DecompressionMethods.Deflate)
        => new() { AllowAutoRedirect = allowRedirect, AutomaticDecompression = decompression, UseProxy = true, Proxy = Instance };

    public Uri? GetProxy(Uri destination)
    {
        var settings = Current();
        if (settings is null) { return Fallback(destination); }
        return settings.Bypasses(destination.Host) ? null : settings.For(destination);
    }

    public bool IsBypassed(Uri host) => GetProxy(host) is null;

    /// <summary>The proxy URL for <paramref name="url"/>, or null for a direct connection (for curl's <c>--proxy</c>).</summary>
    public static string? For(string url)
    {
        try
        {
            var uri = new Uri(url);
            var proxy = Instance.GetProxy(uri);
            return proxy is null || proxy == uri ? null : proxy.ToString().TrimEnd('/');
        }
        catch { return null; }
    }

    private static Uri? Fallback(Uri destination)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy.GetProxy(destination);
            return proxy is null || proxy == destination ? null : proxy;
        }
        catch { return null; }
    }

    private ProxySettings? Current()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _cache.ReadAt < CacheFor) { return _cache.Settings; }
            ProxySettings? settings;
            try { settings = HasEnvironmentProxy() ? null : OperatingSystem.IsWindows() ? ReadWindows() : OperatingSystem.IsLinux() ? ReadGnome() : null; }
            catch { settings = null; }
            _cache = (settings, DateTime.UtcNow);
            return settings;
        }
    }

    private static bool HasEnvironmentProxy()
        => new[] { "https_proxy", "HTTPS_PROXY", "http_proxy", "HTTP_PROXY", "all_proxy", "ALL_PROXY" }
            .Any(n => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n)));

    // ---- Windows ------------------------------------------------------------------------

    private static ProxySettings? ReadWindows()
    {
        if (!OperatingSystem.IsWindows()) { return null; }
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key is null) { return null; }
        if (key.GetValue("ProxyEnable") is not int enabled || enabled == 0) { return null; }
        return ParseWindows(key.GetValue("ProxyServer") as string, key.GetValue("ProxyOverride") as string);
    }

    /// <summary><c>ProxyServer</c> ("host:port" or "http=…;https=…;socks=…") and <c>ProxyOverride</c> ("a;*.b;&lt;local&gt;"). Public for tests.</summary>
    public static ProxySettings? ParseWindows(string? server, string? overrides)
    {
        if (string.IsNullOrWhiteSpace(server)) { return null; }
        Uri? http = null, https = null, socks = null;
        if (!server.Contains('='))
        {
            http = https = ToUri("http", server);
        }
        else
        {
            foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) { continue; }
                var scheme = part[..eq].Trim().ToLowerInvariant();
                var value = part[(eq + 1)..].Trim();
                switch (scheme)
                {
                    case "http": http = ToUri("http", value); break;
                    case "https": https = ToUri("http", value); break;
                    case "socks": socks = ToUri("socks5", value); break;
                }
            }
        }

        var bypass = (overrides ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ProxySettings.Create(http, https, socks, bypass);
    }

    // ---- GNOME ----------------------------------------------------------------------------

    private static ProxySettings? ReadGnome()
    {
        var psi = new ProcessStartInfo("gsettings") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "list-recursively", "org.gnome.system.proxy" }) { psi.ArgumentList.Add(a); }
        using var p = Process.Start(psi);
        if (p is null) { return null; }
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(3000) || p.ExitCode != 0) { return null; }
        return ParseGnome(output);
    }

    /// <summary>The output of <c>gsettings list-recursively org.gnome.system.proxy</c>. Public for tests.</summary>
    public static ProxySettings? ParseGnome(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', 3);
            if (parts.Length == 3) { values[parts[0] + " " + parts[1]] = parts[2].Trim(); }
        }

        string Get(string schema, string key) => values.TryGetValue(schema + " " + key, out var v) ? v.Trim('\'') : string.Empty;
        if (Get("org.gnome.system.proxy", "mode") != "manual") { return null; }

        Uri? Endpoint(string kind, string scheme)
        {
            var host = Get("org.gnome.system.proxy." + kind, "host");
            var port = Get("org.gnome.system.proxy." + kind, "port");
            return host.Length == 0 || !int.TryParse(port, out var n) || n <= 0 ? null : ToUri(scheme, host + ":" + n);
        }

        var ignore = values.TryGetValue("org.gnome.system.proxy ignore-hosts", out var list)
            ? Regex.Matches(list, "'([^']*)'").Select(m => m.Groups[1].Value).ToArray()
            : Array.Empty<string>();
        return ProxySettings.Create(Endpoint("http", "http"), Endpoint("https", "http"), Endpoint("socks", "socks5"), ignore);
    }

    private static Uri? ToUri(string scheme, string value)
    {
        value = value.Trim();
        if (value.Length == 0) { return null; }
        if (!value.Contains("://", StringComparison.Ordinal)) { value = scheme + "://" + value; }
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }
}

/// <summary>Resolved system proxy endpoints and the hosts that go direct.</summary>
public sealed class ProxySettings
{
    private readonly Uri? _http;
    private readonly Uri? _https;
    private readonly Uri? _socks;
    private readonly Regex[] _bypass;
    private readonly bool _bypassLocal;

    private ProxySettings(Uri? http, Uri? https, Uri? socks, Regex[] bypass, bool bypassLocal)
    {
        _http = http;
        _https = https;
        _socks = socks;
        _bypass = bypass;
        _bypassLocal = bypassLocal;
    }

    public static ProxySettings? Create(Uri? http, Uri? https, Uri? socks, IEnumerable<string> bypass)
    {
        if (http is null && https is null && socks is null) { return null; }
        var patterns = bypass.Where(b => b.Length > 0).ToList();
        var regexes = patterns.Where(b => b != "<local>")
            .Select(b => new Regex("^" + Regex.Escape(b).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();
        return new ProxySettings(http, https, socks, regexes, patterns.Contains("<local>"));
    }

    /// <summary>The proxy for a destination: https → the https (else http) proxy, http → the http proxy; socks when that is all there is.</summary>
    public Uri? For(Uri destination)
        => destination.Scheme == Uri.UriSchemeHttps ? _https ?? _http ?? _socks : _http ?? _socks;

    public bool Bypasses(string host)
    {
        if (host is "localhost" or "127.0.0.1" or "::1" or "[::1]") { return true; }
        if (_bypassLocal && !host.Contains('.')) { return true; }
        return _bypass.Any(r => r.IsMatch(host));
    }
}
