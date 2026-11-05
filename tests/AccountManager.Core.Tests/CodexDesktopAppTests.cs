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

    [Fact]
    public void OffWindowsThereIsNoDesktopAppToRestart()
    {
        if (OperatingSystem.IsWindows()) { return; }
        Assert.False(new CodexDesktopApp().IsRunning());
    }
}
