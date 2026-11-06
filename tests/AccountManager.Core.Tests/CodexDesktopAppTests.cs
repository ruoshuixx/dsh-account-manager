using AccountManager.Core.Accounts;
using Xunit;

namespace AccountManager.Core.Tests;

public class CodexDesktopAppTests
{
    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1.2.0_x64__2p2nqsd0c76g0\app\Codex.exe", false, true)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1.2.0_x64__2p2nqsd0c76g0\app\resources\codex.exe", false, true)]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\Codex\Codex.exe", true, true)]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\Codex\Codex.exe", false, false)]
    [InlineData(@"C:\Users\u\AppData\Roaming\npm\node_modules\@openai\codex\vendor\codex.exe", true, false)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1.2.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe", true, true)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.ChatGPTCodex_1.0.0.0_x64__abc\app\ChatGPT.exe", true, true)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1.0.0.0_x64__abc\ChatGPT.exe", true, false)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.Codex_1.0.0.0_x64__abc\Codex.exe", false, false)]
    [InlineData("", true, false)]
    public void OnlyTheDesktopAppIsRestarted(string path, bool hasWindow, bool expected)
        => Assert.Equal(expected, CodexDesktopApp.IsDesktopPath(path, hasWindow));

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1.2.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe", "OpenAI.Codex")]
    [InlineData(@"D:\WindowsApps\OpenAI.ChatGPT-Desktop_1.2.3.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe", "OpenAI.ChatGPT-Desktop")]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\Codex\Codex.exe", null)]
    public void TheStorePackageNameComesFromItsFolder(string path, string? expected)
        => Assert.Equal(expected, CodexDesktopApp.StorePackageName(path));

    [Theory]
    [InlineData("/home/u/.codex/packages/app-server-daemon/releases/0.166.0/bin/codex|app-server|--listen|unix://|--managed-daemon", true)]
    [InlineData("/home/u/.codex/packages/app-server-daemon/releases/0.166.0/bin/codex|app-server|daemon|pid-update-loop", true)]
    [InlineData("/usr/bin/codex|app-server|--listen|ws://127.0.0.1:18771", false)]
    [InlineData("/usr/bin/codex|exec|-m|gpt-6-astra", false)]
    [InlineData("/usr/bin/node|app-server|daemon", false)]
    public void OnLinuxOnlyCodexsManagedDaemonIsRestarted(string cmdline, bool expected)
        => Assert.Equal(expected, CodexDesktopApp.IsManagedDaemon(cmdline.Split('|')));

    [Fact]
    public void OnLinuxTheDesktopAppIsKnownByItsPackagesFiles()
    {
        var files = new[]
        {
            "/opt/ChatGPT", "/opt/ChatGPT/chatgpt", "/opt/ChatGPT/chrome-sandbox", "/opt/ChatGPT/libffmpeg.so",
            "/opt/ChatGPT/resources/bin/codex", "/opt/ChatGPT/resources/app.asar",
            "/usr/share/applications/chatgpt.desktop", "/usr/share/icons/hicolor/512x512/apps/chatgpt.png",
        };
        var executable = new HashSet<string> { "/opt/ChatGPT/chatgpt", "/opt/ChatGPT/chrome-sandbox", "/opt/ChatGPT/libffmpeg.so", "/opt/ChatGPT/resources/bin/codex" };

        var (exes, entry) = CodexDesktopApp.SelectLinuxDesktop(files, executable.Contains);

        Assert.Equal(new[] { "/opt/ChatGPT/chatgpt" }, exes);
        Assert.Equal("/usr/share/applications/chatgpt.desktop", entry);
    }

    [Fact]
    public void TheNameSaysWhatIsRestarted()
        => Assert.Contains(new CodexDesktopApp().Name, new[] { "Codex 桌面版", "Codex 后台服务" });
}
