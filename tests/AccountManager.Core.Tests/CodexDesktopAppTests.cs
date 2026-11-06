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
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1.2.0_x64__2p2nqsd0c76g0\app\Codex.exe", "OpenAI.Codex_2p2nqsd0c76g0")]
    [InlineData(@"D:\WindowsApps\OpenAI.ChatGPT-Desktop_1.2.3.0_arm64__publisher\app\ChatGPT.exe", "OpenAI.ChatGPT-Desktop_publisher")]
    [InlineData(@"c:\program files\windowsapps\OpenAI.ChatGPT_1.2.3.0_x86_en-us_abc\app\resources\codex.exe", "OpenAI.ChatGPT_abc")]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1.2.0_x64__abc", "OpenAI.Codex_abc")]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\Codex\Codex.exe", null)]
    [InlineData(@"C:\Program Files\WindowsAppsBackup\OpenAI.Codex_26.1.2.0_x64__abc\Codex.exe", null)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex\Codex.exe", null)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1.2.0_x64_abc\Codex.exe", null)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1.2.0_x64__\Codex.exe", null)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex__x64__abc\Codex.exe", null)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1.2.0___abc\Codex.exe", null)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1.2.0_x64__abc_extra\Codex.exe", null)]
    [InlineData(@"C:\Program Files\WindowsApps\", null)]
    [InlineData("", null)]
    public void TheStorePackageFamilyNameOmitsVersionArchitectureAndResources(string path, string? expected)
        => Assert.Equal(expected, CodexDesktopApp.StorePackageFamilyName(path));

    [Theory]
    [InlineData(@"app\ChatGPT.exe", "Main")]
    [InlineData(@"APP\CHATGPT.EXE", "Main")]
    [InlineData("app/chatgpt.exe", "Main")]
    [InlineData(@"TOOLS\SETTINGS.EXE", "Settings")]
    [InlineData(@"app\Codex.exe", "Launcher")]
    [InlineData(null, "Launcher")]
    [InlineData("", "Launcher")]
    public void TheManifestAppMatchesTheExecutableBeforeFallingBackToTheFirst(string? relativeExe, string expected)
    {
        const string manifest = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="Launcher" />
                <Application Id="Settings" Executable="tools/Settings.exe" />
                <Application Id="Main" Executable="app\ChatGPT.exe" />
                <Application Id="Duplicate" Executable="APP\CHATGPT.EXE" />
              </Applications>
            </Package>
            """;

        Assert.Equal(expected, CodexDesktopApp.AppIdFromManifest(manifest, relativeExe));
    }

    [Fact]
    public void TheManifestCanOmitTheDefaultNamespace()
    {
        const string manifest = """
            <Package><Applications><Application Id="App" Executable="app\Codex.exe" /></Applications></Package>
            """;

        Assert.Equal("App", CodexDesktopApp.AppIdFromManifest(manifest, @"APP\CODEX.EXE"));
    }

    [Theory]
    [InlineData("<Package />")]
    [InlineData("<Package><Applications /></Package>")]
    [InlineData("<Package><Applications><Application Executable=\"app\\Codex.exe\" /></Applications></Package>")]
    public void AManifestWithoutAnAppIdHasNoEntry(string manifest)
        => Assert.Null(CodexDesktopApp.AppIdFromManifest(manifest, @"app\Codex.exe"));

    [Fact]
    public void AnInvalidManifestCannotSupplyAnAppId()
        => Assert.Throws<System.Xml.XmlException>(() => CodexDesktopApp.AppIdFromManifest("<Package>", null));

    [Theory]
    [InlineData("""{"Name":"Codex","AppID":"OpenAI.Codex_pub!App"}""", "OpenAI.Codex_pub!App")]
    [InlineData("""{"Name":"ChatGPT","AppID":"OpenAI.ChatGPT_pub!Main"}""", "OpenAI.ChatGPT_pub!Main")]
    [InlineData("""[{"Name":"ChatGPT","AppID":"OpenAI.ChatGPT_pub!Main"},{"Name":"Codex","AppID":"OpenAI.Codex_pub!App"}]""", "OpenAI.Codex_pub!App")]
    [InlineData("""[{"Name":"Codex","AppID":"OpenAI.Codex_pub!App"},{"Name":"ChatGPT","AppID":"OpenAI.ChatGPT_pub!Main"}]""", "OpenAI.Codex_pub!App")]
    [InlineData("""[{"Name":"Other","AppID":"OpenAI.Other_pub!App"},{"Name":"ChatGPT with Codex","AppID":"OpenAI.ChatGPT_pub!App"}]""", "OpenAI.ChatGPT_pub!App")]
    [InlineData("""[{"Name":"Codex","AppID":"Other.Codex_pub!App"},{"Name":"CHATGPT","AppID":"openai.ChatGPT_pub!App"}]""", "openai.ChatGPT_pub!App")]
    [InlineData("""[{"Name":"ChatGPT","AppID":"OpenAI.ChatGPT_pub!First"},{"Name":"ChatGPT","AppID":"OpenAI.ChatGPT_pub!Second"}]""", "OpenAI.ChatGPT_pub!First")]
    [InlineData("""[null,4,{"Name":false,"AppID":"OpenAI.Codex_pub!App"},{"Name":"Codex","AppID":null},{"Name":"codex","AppID":"OpenAI.Codex_pub!App"}]""", "OpenAI.Codex_pub!App")]
    [InlineData("""{"Name":"Codex","AppID":""}""", null)]
    [InlineData("""{"Name":"Unrelated","AppID":"OpenAI.Other_pub!App"}""", null)]
    [InlineData("{}", null)]
    [InlineData("[]", null)]
    [InlineData("null", null)]
    [InlineData("", null)]
    [InlineData("not json", null)]
    public void TheStartMenuEntryPrefersCodexAndAcceptsASingleObjectOrAnArray(string json, string? expected)
        => Assert.Equal(expected, CodexDesktopApp.PickStartApp(json));

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
