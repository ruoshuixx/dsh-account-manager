using AccountManager.App.Services;
using AccountManager.Core.Logging;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.App.Tests;

/// <summary>The composition root resolves the dsh home from settings and never writes into it on its own.</summary>
public sealed class AppServicesDshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshA-" + Guid.NewGuid().ToString("N")[..10]);

    public AppServicesDshTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "dsh", "profiles", "tauri"));
    }

    private AppServices Create(AppSettings settings)
    {
        var secrets = new SecretRegistry();
        return new AppServices(settings, Path.Combine(_root, "app"), new AppLog(secrets, Path.Combine(_root, "logs")), Path.Combine(_root, "backups"));
    }

    [Fact]
    public void StatusLineNamesTheResolvedHomeAndProfile()
    {
        var services = Create(new AppSettings { DshHome = Path.Combine(_root, "dsh") });

        Assert.Equal(Path.Combine(_root, "dsh"), services.Home.Root);
        Assert.Equal("tauri", services.Home.Profile); // detected
        Assert.Equal("DSH：" + Path.Combine(_root, "dsh") + " · 档案 tauri", services.DshStatus);
    }

    [Fact]
    public void ChangingTheSettingsRepointsTheBackendAndMarksAMissingProfile()
    {
        var settings = new AppSettings { DshHome = Path.Combine(_root, "dsh") };
        var services = Create(settings);

        settings.DshProfile = "desktop";
        services.ApplyDshLocation();

        Assert.Equal("desktop", services.Backend.Home.Profile);
        Assert.EndsWith("档案 desktop（未找到）", services.DshStatus);
    }

    [Fact]
    public async Task ReadingAnEmptyHomeFindsNothingAndCreatesNoFilesInIt()
    {
        var services = Create(new AppSettings { DshHome = Path.Combine(_root, "dsh"), DshProfile = "tauri" });

        var snapshot = await services.LoadSnapshotAsync();

        Assert.Empty(snapshot.Accounts);
        Assert.False(File.Exists(Path.Combine(_root, "dsh", ".credentials.yaml")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "dsh", "profiles", "tauri")));
    }

    [Fact]
    public void LegacyCliPathInOldSettingsFilesIsIgnored()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "{\"CliPath\":\"/usr/bin/some-cli\",\"DshHome\":\"/x/y\"}");

        var settings = new SettingsStore(path).Load();

        Assert.Equal("/x/y", settings.DshHome);
        Assert.Null(settings.DshProfile);
    }

    [Fact]
    public void WebDavSyncUsesTheSharedFolderAndKnowsOnlyItsOwnLegacyFolder()
    {
        var services = Create(new AppSettings { DshHome = Path.Combine(_root, "dsh"), WebDavUrl = "https://dav.example.test/dav/", WebDavUsername = "u" });

        Assert.Equal("/AccountManager/", AppServices.WebDavFolder);
        Assert.Equal("/DshAccountManager/", AppServices.LegacyWebDavFolder);
        Assert.Equal("/AccountManager/", services.BuildWebDavOptions().Folder);
        Assert.Equal("/DshAccountManager/", services.BuildWebDavOptions(AppServices.LegacyWebDavFolder).Folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}
