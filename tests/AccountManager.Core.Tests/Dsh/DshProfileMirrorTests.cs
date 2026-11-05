using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

/// <summary>The desktop profile's providers copied into the dsh CLI's <c>cli</c> profile. Temp directories only.</summary>
public sealed class DshProfileMirrorTests : IDisposable
{
    private const string DefaultPatch = "# Your patch layer for this dsh profile\n[]\n";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshM-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string Profile(string name, bool fast = false, bool initialized = true)
    {
        var dir = Path.Combine(_root, "profiles", name);
        Directory.CreateDirectory(dir);
        if (initialized)
        {
            var bundles = new JsonArray("@deepseek-ai/dsh-base");
            if (fast) { bundles.Add(FastComponentInstaller.PackageName); }
            File.WriteAllText(Path.Combine(dir, "package.json"), new JsonObject { ["dsh"] = new JsonObject { ["profile"] = new JsonObject { ["bundles"] = bundles } } }.ToJsonString());
            File.WriteAllText(Path.Combine(dir, DshHome.PatchFileName), DefaultPatch);
        }

        return dir;
    }

    private static JsonObject Provider(string url, params string[] models)
        => new() { ["baseURL"] = url, ["models"] = new JsonArray(models.Select(m => (JsonNode)new JsonObject { ["id"] = m }).ToArray()) };

    private static void Set(string profileDir, string id, JsonObject? provider)
        => ProfilePatchDocument.Update(Path.Combine(profileDir, DshHome.PatchFileName), d => d.SetProvider(id, provider));

    private static JsonObject? Providers(string profileDir)
        => ProfilePatchDocument.Load(Path.Combine(profileDir, DshHome.PatchFileName)).GetProviders();

    private static IReadOnlyList<string> ModelIds(JsonNode? provider)
        => ((JsonArray)provider!["models"]!).Select(m => m!["id"]!.GetValue<string>()).ToList();

    private DshProfileMirror Mirror() => new(() => DshHome.Resolve(_root));

    [Fact]
    public void TheCliProfileGetsTheDesktopProvidersWithoutFastTwinsAndKeepsItsOwn()
    {
        var tauri = Profile("tauri", fast: true);
        var cli = Profile("cli");
        Set(tauri, "openai-codex", Provider("https://chatgpt.example.test", "gpt-a", "gpt-a-fast"));
        Set(tauri, "api-1", Provider("https://api.example.test/v1", "m1"));
        Set(cli, "mine", Provider("https://mine.example.test", "x"));

        var result = Mirror().Sync(new Dictionary<string, IReadOnlyList<string>>());

        Assert.True(result.Changed);
        Assert.Empty(result.Notes);
        Assert.Equal(new[] { "api-1", "openai-codex" }, result.State["cli"].OrderBy(x => x, StringComparer.Ordinal));
        var copied = Providers(cli)!;
        Assert.Equal(new[] { "gpt-a" }, ModelIds(copied["openai-codex"])); // no Fast component in cli
        Assert.Equal("https://api.example.test/v1", copied["api-1"]!["baseURL"]!.GetValue<string>());
        Assert.True(copied.ContainsKey("mine"));
        Assert.Equal(new[] { "gpt-a", "gpt-a-fast" }, ModelIds(Providers(tauri)!["openai-codex"])); // the source is untouched

        // Nothing new: nothing written.
        var text = File.ReadAllText(Path.Combine(cli, DshHome.PatchFileName));
        var again = Mirror().Sync(result.State);
        Assert.False(again.Changed);
        Assert.Equal(text, File.ReadAllText(Path.Combine(cli, DshHome.PatchFileName)));
    }

    [Fact]
    public void AProviderRemovedFromTheDesktopProfileIsRemovedOnlyIfItWasMirrored()
    {
        var tauri = Profile("tauri");
        var cli = Profile("cli", fast: true);
        Set(tauri, "api-1", Provider("https://api.example.test/v1", "m1", "m1-fast"));
        var first = Mirror().Sync(new Dictionary<string, IReadOnlyList<string>>());
        Assert.Equal(new[] { "m1", "m1-fast" }, ModelIds(Providers(cli)!["api-1"])); // cli has the Fast component
        Set(cli, "mine", Provider("https://mine.example.test", "x"));

        Set(tauri, "api-1", null);
        var second = Mirror().Sync(first.State);

        Assert.Empty(second.State["cli"]);
        var left = Providers(cli)!;
        Assert.False(left.ContainsKey("api-1"));
        Assert.True(left.ContainsKey("mine"));
    }

    [Fact]
    public void AnUninitializedOrMissingCliProfileIsLeftAlone()
    {
        var tauri = Profile("tauri");
        Profile("cli", initialized: false);
        Set(tauri, "api-1", Provider("https://api.example.test/v1", "m1"));

        var result = Mirror().Sync(new Dictionary<string, IReadOnlyList<string>>());

        Assert.False(result.Changed);
        Assert.Empty(result.State);
        Assert.False(File.Exists(Path.Combine(_root, "profiles", "cli", DshHome.PatchFileName)));
        Assert.Empty(DshProfileMirror.Targets(DshHome.Resolve(_root)));
    }

    [Fact]
    public void WithOnlyTheCliProfileItIsTheManagedOneAndNothingIsMirrored()
    {
        Profile("cli");
        var home = DshHome.Resolve(_root);
        Assert.Equal("cli", home.Profile);
        Assert.Empty(DshProfileMirror.Targets(home));
    }
}
