using AccountManager.Core.Dsh;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

public class DshHomeTests
{
    private static Func<string, string?> Env(string? dshHome) => name => name == "DSH_HOME" ? dshHome : null;

    [Fact]
    public void DefaultsToDotDshUnderTheUserHome()
    {
        using var temp = new TempDir();

        var home = DshHome.Resolve(null, null, Env(null), temp.Path);

        Assert.Equal(Path.Combine(temp.Path, ".dsh"), home.Root);
        Assert.Equal(Path.Combine(temp.Path, ".dsh", ".credentials.yaml"), home.CredentialsPath);
        Assert.False(home.ProfileExists);
    }

    [Fact]
    public void EnvironmentVariableBeatsDefaultAndSettingsBeatEnvironment()
    {
        using var temp = new TempDir();
        var fromEnv = DshHome.Resolve(null, null, Env(temp.File("envhome")), temp.Path);
        var fromSettings = DshHome.Resolve(temp.File("settings"), null, Env(temp.File("envhome")), temp.Path);
        var blankIgnored = DshHome.Resolve("  ", null, Env(""), temp.Path);

        Assert.Equal(temp.File("envhome"), fromEnv.Root);
        Assert.Equal(temp.File("settings"), fromSettings.Root);
        Assert.Equal(Path.Combine(temp.Path, ".dsh"), blankIgnored.Root);
    }

    [Fact]
    public void ExpandsALeadingTilde()
    {
        using var temp = new TempDir();
        Assert.Equal(Path.Combine(temp.Path, "x"), DshHome.Resolve("~/x", null, Env(null), temp.Path).Root);
    }

    [Fact]
    public void ProfileOrderIsDesktopThenTauriThenFirstOtherSorted()
    {
        using var temp = new TempDir();
        var root = temp.File("dsh");
        string Detect() => DshHome.Resolve(root, null, Env(null), temp.Path).Profile;

        Directory.CreateDirectory(Path.Combine(root, "profiles", "zeta"));
        Directory.CreateDirectory(Path.Combine(root, "profiles", "alpha"));
        Assert.Equal("alpha", Detect());

        Directory.CreateDirectory(Path.Combine(root, "profiles", "tauri"));
        Assert.Equal("tauri", Detect());

        Directory.CreateDirectory(Path.Combine(root, "profiles", "desktop"));
        Assert.Equal("desktop", Detect());
        var home = DshHome.Resolve(root, null, Env(null), temp.Path);
        Assert.True(home.ProfileExists);
        Assert.Equal(Path.Combine(root, "profiles", "desktop", "cordis.patch.yml"), home.PatchPath);
    }

    [Fact]
    public void ExplicitProfileWinsEvenWhenItDoesNotExist()
    {
        using var temp = new TempDir();
        var root = temp.File("dsh");
        Directory.CreateDirectory(Path.Combine(root, "profiles", "desktop"));

        var home = DshHome.Resolve(root, "custom", Env(null), temp.Path);

        Assert.Equal("custom", home.Profile);
        Assert.False(home.ProfileExists);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void RejectsProfileNamesThatEscapeTheProfilesDirectory(string name)
    {
        using var temp = new TempDir();
        Assert.Throws<ArgumentException>(() => DshHome.Resolve(temp.Path, name, Env(null), temp.Path));
    }

    [Fact]
    public void FilesInsideProfilesAreNotProfiles()
    {
        using var temp = new TempDir();
        var root = temp.File("dsh");
        Directory.CreateDirectory(Path.Combine(root, "profiles"));
        File.WriteAllText(Path.Combine(root, "profiles", "desktop"), "a file, not a directory");

        Assert.Null(DshHome.DetectProfile(root));
    }
}
