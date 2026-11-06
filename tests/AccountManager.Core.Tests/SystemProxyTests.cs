using AccountManager.Core.Net;
using Xunit;

namespace AccountManager.Core.Tests;

/// <summary>The system proxy read from GNOME's and Windows' settings (parsing only; nothing on this machine is read).</summary>
public class SystemProxyTests
{
    private const string GnomeManual = """
        org.gnome.system.proxy autoconfig-url ''
        org.gnome.system.proxy ignore-hosts ['localhost', '127.*', '*.local', '192.168.*', '*jd.com']
        org.gnome.system.proxy mode 'manual'
        org.gnome.system.proxy.http host '127.0.0.1'
        org.gnome.system.proxy.http port 7890
        org.gnome.system.proxy.https host '127.0.0.1'
        org.gnome.system.proxy.https port 7891
        org.gnome.system.proxy.socks host '127.0.0.1'
        org.gnome.system.proxy.socks port 7892
        """;

    [Fact]
    public void GnomeManualProxyIsUsedForHttpsAndHonoursIgnoreHosts()
    {
        var proxy = SystemProxy.ParseGnome(GnomeManual)!;
        Assert.Equal(new Uri("http://127.0.0.1:7891"), proxy.For(new Uri("https://chatgpt.com/backend-api/wham/usage")));
        Assert.Equal(new Uri("http://127.0.0.1:7890"), proxy.For(new Uri("http://example.test/")));
        Assert.False(proxy.Bypasses("chatgpt.com"));
        Assert.True(proxy.Bypasses("www.jd.com"));
        Assert.True(proxy.Bypasses("192.168.1.5"));
        Assert.True(proxy.Bypasses("localhost"));
    }

    [Theory]
    [InlineData("'none'")]
    [InlineData("'auto'")]
    public void GnomeWithoutAManualProxyMeansNone(string mode)
        => Assert.Null(SystemProxy.ParseGnome(GnomeManual.Replace("'manual'", mode, StringComparison.Ordinal)));

    [Fact]
    public void GnomeWithOnlySocksUsesSocks()
    {
        var output = "org.gnome.system.proxy mode 'manual'\norg.gnome.system.proxy.http host ''\norg.gnome.system.proxy.http port 0\n"
            + "org.gnome.system.proxy.socks host '127.0.0.1'\norg.gnome.system.proxy.socks port 7890\n";
        Assert.Equal(new Uri("socks5://127.0.0.1:7890"), SystemProxy.ParseGnome(output)!.For(new Uri("https://chatgpt.com/")));
    }

    [Fact]
    public void WindowsProxyServerInBothFormsAndItsOverrides()
    {
        var plain = SystemProxy.ParseWindows("127.0.0.1:7890", "localhost;127.*;<local>")!;
        Assert.Equal(new Uri("http://127.0.0.1:7890"), plain.For(new Uri("https://chatgpt.com/")));
        Assert.True(plain.Bypasses("intranet"));
        Assert.True(plain.Bypasses("127.0.0.5"));
        Assert.False(plain.Bypasses("chatgpt.com"));

        var split = SystemProxy.ParseWindows("http=127.0.0.1:7890;https=127.0.0.1:7891;socks=127.0.0.1:7892", null)!;
        Assert.Equal(new Uri("http://127.0.0.1:7891"), split.For(new Uri("https://chatgpt.com/")));
        Assert.Equal(new Uri("http://127.0.0.1:7890"), split.For(new Uri("http://example.test/")));

        Assert.Null(SystemProxy.ParseWindows("", null));
    }
}
