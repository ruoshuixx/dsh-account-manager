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
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1.0.0.0_x64__abc\ChatGPT.exe", true, false)]
    [InlineData("", true, false)]
    public void OnlyTheDesktopAppIsRestarted(string path, bool hasWindow, bool expected)
        => Assert.Equal(expected, CodexDesktopApp.IsDesktopPath(path, hasWindow));

    [Theory]
    [InlineData("/home/u/.codex/packages/app-server-daemon/releases/0.166.0/bin/codex|app-server|--listen|unix://|--managed-daemon", true)]
    [InlineData("/home/u/.codex/packages/app-server-daemon/releases/0.166.0/bin/codex|app-server|daemon|pid-update-loop", true)]
    [InlineData("/usr/bin/codex|app-server|--listen|ws://127.0.0.1:18771", false)]
    [InlineData("/usr/bin/codex|exec|-m|gpt-6-astra", false)]
    [InlineData("/usr/bin/node|app-server|daemon", false)]
    public void OnLinuxOnlyCodexsManagedDaemonIsRestarted(string cmdline, bool expected)
        => Assert.Equal(expected, CodexDesktopApp.IsManagedDaemon(cmdline.Split('|')));

    [Fact]
    public void TheNameSaysWhatIsRestarted()
        => Assert.Equal(OperatingSystem.IsWindows() ? "Codex 桌面版" : "Codex 后台服务", new CodexDesktopApp().Name);
}
