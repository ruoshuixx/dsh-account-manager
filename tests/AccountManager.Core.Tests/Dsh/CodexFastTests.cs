using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

/// <summary>Codex Fast mode: the embedded component, its install into a dsh profile, the -fast models and the import hook.</summary>
public class CodexFastTests
{
    private const string Package = "{\n  \"name\": \"dsh-profile-tauri\",\n  \"private\": true,\n  \"dependencies\": {},\n  \"dsh\": {\n    \"profile\": {\n      \"bundles\": [\n        \"@deepseek-ai/dsh-base\",\n        \"@deepseek-ai/dsh-headless\"\n      ]\n    }\n  }\n}\n";

    private const string BaseModels = "- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex:\n        models:\n          - id: gpt-6.1-sol\n          - id: gpt-6-astra\n          - id: gpt-6-luna\n";

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    // ---- the embedded component ----

    [Fact]
    public void TheComponentIsEmbeddedWithItsExactFileSet()
    {
        Assert.Equal(new[] { "LICENSE", "README.md", "cordis.patch.yml", "lib/fast.js", "lib/index.js", "package.json" },
            FastComponentInstaller.EmbeddedFiles.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal("0.1.0", FastComponentInstaller.EmbeddedVersion);
        var manifest = JsonNode.Parse(Encoding.UTF8.GetString(FastComponentInstaller.EmbeddedFiles.First(f => f.Path == "package.json").Content))!.AsObject();
        Assert.Equal("dsh-account-manager-fast", manifest["name"]!.GetValue<string>());
        Assert.Equal("./cordis.patch.yml", manifest["dsh"]!["bundle"]!["patch"]!.GetValue<string>());
        Assert.Null(manifest["dependencies"]);
        Assert.Equal("MIT", manifest["license"]!.GetValue<string>());
    }

    // ---- the install ----

    [Fact]
    public void AFreshProfileGetsExactlyTheEmbeddedFilesAndOneBundleNameAppended()
    {
        using var f = new DshFixture(packageJson: Package);

        var result = f.Fast.Installer.Install();

        Assert.True(result.FilesWritten);
        Assert.True(result.BundleAdded);
        Assert.Equal(FastComponentState.Missing, result.Before);
        var written = Directory.EnumerateFiles(f.PluginDir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(f.PluginDir, p).Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(FastComponentInstaller.EmbeddedFiles.Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray(), written);
        foreach (var (path, content) in FastComponentInstaller.EmbeddedFiles) { Assert.Equal(Hex(content), Hex(File.ReadAllBytes(Path.Combine(f.PluginDir, path)))); }

        // Every other byte of package.json is as it was: the name is the only insertion, with the neighbours' indentation.
        var expected = Package.Replace("\"@deepseek-ai/dsh-headless\"\n", "\"@deepseek-ai/dsh-headless\",\n        \"dsh-account-manager-fast\"\n");
        Assert.Equal(expected, File.ReadAllText(f.PackageJsonPath));
        var profile = Path.GetDirectoryName(f.PackageJsonPath)!;
        Assert.False(File.Exists(Path.Combine(profile, "pnpm-lock.yaml")));
        Assert.False(File.Exists(Path.Combine(profile, "node_modules", ".modules.yaml")));
        Assert.False(File.Exists(f.PackageJsonPath + ".lock"));
        Assert.Empty(Directory.GetFiles(profile, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(FastComponentState.Installed, f.Fast.Installer.GetStatus().State);
        Assert.Single(f.Backups.ListBackups()); // package.json was backed up before it changed
    }

    [Fact]
    public void ASecondInstallWritesNothingAndMakesNoBackup()
    {
        using var f = new DshFixture(packageJson: Package);
        f.Fast.Installer.Install();
        var packageBefore = File.ReadAllBytes(f.PackageJsonPath);
        var stamps = Directory.EnumerateFiles(f.PluginDir, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => File.GetLastWriteTimeUtc(p));
        var backups = f.Backups.ListBackups().Count;
        Thread.Sleep(30);

        var again = f.Fast.Installer.Install();

        Assert.False(again.Changed);
        Assert.Equal(FastComponentState.Installed, again.Before);
        Assert.Equal(Hex(packageBefore), Hex(File.ReadAllBytes(f.PackageJsonPath)));
        Assert.Equal(backups, f.Backups.ListBackups().Count);
        foreach (var (path, time) in stamps) { Assert.Equal(time, File.GetLastWriteTimeUtc(path)); }
    }

    [Fact]
    public void AnOlderVersionIsReplacedWithoutTouchingPackageJsonAndANewerOneIsLeftAlone()
    {
        using var f = new DshFixture(packageJson: Package.Replace("\"@deepseek-ai/dsh-headless\"", "\"@deepseek-ai/dsh-headless\",\n        \"dsh-account-manager-fast\""));
        Directory.CreateDirectory(Path.Combine(f.PluginDir, "lib"));
        File.WriteAllText(Path.Combine(f.PluginDir, "package.json"), "{\"name\":\"dsh-account-manager-fast\",\"version\":\"0.0.9\"}");
        File.WriteAllText(Path.Combine(f.PluginDir, "lib", "index.js"), "// old");
        Assert.Equal(FastComponentState.Outdated, f.Fast.Installer.GetStatus().State);
        var packageBefore = File.ReadAllBytes(f.PackageJsonPath);

        var result = f.Fast.Installer.Install();

        Assert.True(result.FilesWritten);
        Assert.False(result.BundleAdded);
        Assert.Equal(Hex(packageBefore), Hex(File.ReadAllBytes(f.PackageJsonPath)));
        foreach (var (path, content) in FastComponentInstaller.EmbeddedFiles) { Assert.Equal(Hex(content), Hex(File.ReadAllBytes(Path.Combine(f.PluginDir, path)))); }
        Assert.Empty(f.Backups.ListBackups());
        Assert.Equal(FastComponentState.Installed, f.Fast.Installer.GetStatus().State);

        // A newer version someone else put there stays.
        File.WriteAllText(Path.Combine(f.PluginDir, "package.json"), "{\"name\":\"dsh-account-manager-fast\",\"version\":\"9.9.9\"}");
        File.WriteAllText(Path.Combine(f.PluginDir, "lib", "index.js"), "// newer");
        Assert.Equal(FastComponentState.Installed, f.Fast.Installer.GetStatus().State);
        Assert.False(f.Fast.Installer.Install().Changed);
        Assert.Equal("// newer", File.ReadAllText(Path.Combine(f.PluginDir, "lib", "index.js")));
    }

    [Fact]
    public void ADamagedSameVersionFolderIsRepaired()
    {
        using var f = new DshFixture(packageJson: Package);
        f.Fast.Installer.Install();
        File.Delete(Path.Combine(f.PluginDir, "lib", "fast.js"));
        Assert.Equal(FastComponentState.Outdated, f.Fast.Installer.GetStatus().State);

        var result = f.Fast.Installer.Install();

        Assert.True(result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(f.PluginDir, "lib", "fast.js")));
        Assert.Equal(FastComponentState.Installed, f.Fast.Installer.GetStatus().State);
    }

    [Fact]
    public void StatusTellsMissingNotListedOutdatedAndInstalled()
    {
        using var f = new DshFixture(packageJson: Package);
        Assert.Equal(FastComponentState.Missing, f.Fast.Installer.GetStatus().State);

        // Files without the bundle name.
        foreach (var (path, content) in FastComponentInstaller.EmbeddedFiles)
        {
            var target = Path.Combine(f.PluginDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }

        Assert.Equal(FastComponentState.NotListed, f.Fast.Installer.GetStatus().State);
        f.Fast.Installer.Install();
        Assert.Equal(FastComponentState.Installed, f.Fast.Installer.GetStatus().State);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"dsh\":{\"profile\":{}}}")]
    [InlineData("{\"dsh\":{\"profile\":{\"bundles\":{}}}}")]
    [InlineData("{\"dsh\":{\"profile\":{\"bundles\":[\"a\",3]}}}")]
    [InlineData("{\"dsh\":{\"profile\":{\"bundles\":[\"a\"],\"bundles\":[\"b\"]}}}")]
    [InlineData("{\"dsh\":{\"profile\":{\"bundles\":[\"a\"]}},} ")]
    public void AnUnexpectedPackageJsonIsRefusedAndNothingIsWritten(string text)
    {
        using var f = new DshFixture(packageJson: text);

        var ex = Assert.Throws<DshException>(() => f.Fast.Installer.Install());

        Assert.Contains("package.json", ex.Message);
        Assert.False(Directory.Exists(f.PluginDir));
        Assert.Equal(text, File.ReadAllText(f.PackageJsonPath));
        Assert.Empty(f.Backups.ListBackups());
    }

    [Fact]
    public void AProfileThatDshNeverStartedIsRefusedWithAClearMessage()
    {
        using var f = new DshFixture(); // a profile directory with a patch file but no package.json

        var ex = Assert.Throws<DshException>(() => f.Fast.Installer.Install());

        Assert.Contains("package.json", ex.Message);
        Assert.Contains("启动", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(f.HomeDir, "profiles", DshFixture.Profile, "node_modules")));
    }

    [Fact]
    public void TheInstallWaitsForDshsPackageJsonLockAndTakesOverADeadHoldersLock()
    {
        using var f = new DshFixture(packageJson: Package);
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = Task.Run(() => DshFileLock.Run(f.PackageJsonPath, () => { held.Set(); release.Wait(5000); }, waitMs: 5000));
        Assert.True(held.Wait(5000));
        Assert.True(File.Exists(f.PackageJsonPath + ".lock")); // the same <file>.lock dsh's plugin manager takes
        var started = DateTime.UtcNow;
        var install = Task.Run(() => f.Fast.Installer.Install());
        Thread.Sleep(400);
        Assert.False(install.IsCompleted); // still waiting for the lock
        Assert.DoesNotContain("dsh-account-manager-fast", File.ReadAllText(f.PackageJsonPath));
        release.Set();
        holder.Wait();
        var result = install.GetAwaiter().GetResult();

        Assert.True(result.BundleAdded);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(350));
        Assert.False(File.Exists(f.PackageJsonPath + ".lock"));

        // A lock left behind by a process that no longer exists is taken over.
        using var g = new DshFixture(packageJson: Package);
        File.WriteAllText(g.PackageJsonPath + ".lock", "999999\n");
        Assert.True(g.Fast.Installer.Install().BundleAdded);
        Assert.False(File.Exists(g.PackageJsonPath + ".lock"));
    }

    [Fact]
    public void FilesThatCannotBeWrittenGiveAMessageThatMentionsQuittingDsh()
    {
        using var f = new DshFixture(packageJson: Package);
        // A file where the plugin folder's parent should be a folder.
        File.WriteAllText(Path.Combine(f.HomeDir, "profiles", DshFixture.Profile, "node_modules"), "in the way");

        var ex = Assert.Throws<DshException>(() => f.Fast.Installer.Install());

        Assert.Contains("退出", ex.Message);
        Assert.DoesNotContain("dsh-account-manager-fast", File.ReadAllText(f.PackageJsonPath)); // never listed without its files
    }

    // ---- the text edit of package.json ----

    [Theory]
    [InlineData("{\n    \"dsh\": {\n        \"profile\": {\n            \"bundles\": [\n                \"a\",\n                \"b\"\n            ]\n        }\n    }\n}", ",\n                \"x\"\n            ]")]
    [InlineData("{\"dsh\":{\"profile\":{\"bundles\":[\"a\",\"b\"]}}}", ",\"x\"]")]
    [InlineData("{\r\n  \"dsh\": {\r\n    \"profile\": {\r\n      \"bundles\": [\r\n        \"a\"\r\n      ]\r\n    }\r\n  }\r\n}", ",\r\n        \"x\"\r\n      ]")]
    [InlineData("{\"dsh\":{\"profile\":{\"bundles\":[]}}}", "[\"x\"]")]
    [InlineData("{\n  \"dsh\": {\n    \"profile\": {\n      \"bundles\": [\n      ]\n    }\n  }\n}", "[\n        \"x\"\n      ]")]
    public void AppendingTheBundleNameKeepsTheFilesOwnLayout(string text, string expectedFragment)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        var next = PackageJsonBundles.Append(bytes, "x");

        Assert.NotNull(next);
        var result = Encoding.UTF8.GetString(next!);
        Assert.Contains(expectedFragment, result);
        Assert.Contains("x", PackageJsonBundles.Read(next));
        Assert.Null(PackageJsonBundles.Append(next, "x")); // already listed
    }

    [Fact]
    public void ABomAndForeignFieldsSurviveTheEdit()
    {
        var text = "{\"name\":\"p\",\"dsh\":{\"x\":[1,2],\"profile\":{\"y\":null,\"bundles\":[\"a\"]}},\"z\":{\"bundles\":[\"no\"]}}";
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();

        var next = PackageJsonBundles.Append(bytes, "dsh-account-manager-fast")!;

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, next.Take(3).ToArray());
        Assert.Equal(text.Replace("[\"a\"]", "[\"a\",\"dsh-account-manager-fast\"]"), Encoding.UTF8.GetString(next, 3, next.Length - 3));
    }

    // ---- the -fast models ----

    private static JsonObject Entry(string id, string name, int context, int max, string[] input, JsonObject efforts)
        => new() { ["id"] = id, ["name"] = name, ["contextWindow"] = context, ["maxTokens"] = max, ["input"] = new JsonArray(input.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()), ["reasoningEfforts"] = efforts };

    [Fact]
    public void FastEntriesCopyTheBaseFromTheBuiltInTableWhenNothingElseKnowsIt()
    {
        var sol = CodexFastService.BuildEntry("gpt-6.1-sol", null, null);
        Assert.Equal("gpt-6.1-sol-fast", sol["id"]!.GetValue<string>());
        Assert.Equal("GPT-6.1 Sol (Fast)", sol["name"]!.GetValue<string>());
        Assert.Equal(500000, sol["contextWindow"]!.GetValue<int>());
        Assert.Equal(128000, sol["maxTokens"]!.GetValue<int>());
        Assert.Equal(new[] { "text", "image" }, sol["input"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        // off is not offered (null in the catalog); the rest keeps its wire spelling.
        Assert.Equal("{\"minimal\":\"low\",\"low\":\"low\",\"medium\":\"medium\",\"high\":\"high\",\"xhigh\":\"xhigh\",\"max\":\"max\"}", sol["reasoningEfforts"]!.ToJsonString());

        var luna = CodexFastService.BuildEntry("gpt-6-luna", null, null);
        Assert.Equal(272000, luna["contextWindow"]!.GetValue<int>());
        Assert.Equal("none", luna["reasoningEfforts"]!["off"]!.GetValue<string>());
        Assert.Equal("GPT-6 Astra (Fast)", CodexFastService.BuildEntry("gpt-6-astra", null, null)["name"]!.GetValue<string>());
    }

    [Fact]
    public void FastEntriesPreferTheUsersOwnBaseEntryThenTheCatalogThenTheTable()
    {
        var catalog = JsonNode.Parse("{\"name\":\"Catalog Name\",\"reasoning\":true,\"input\":[\"text\"],\"contextWindow\":111,\"maxTokens\":22,\"thinkingLevelMap\":{\"off\":null,\"xhigh\":\"xhigh\",\"minimal\":\"low\"}}")!.AsObject();

        // Only the catalog: absent levels low/medium/high default to their own names, max is not offered, off is null.
        var fromCatalog = CodexFastService.BuildEntry("gpt-6-astra", null, catalog);
        Assert.Equal("Catalog Name (Fast)", fromCatalog["name"]!.GetValue<string>());
        Assert.Equal(111, fromCatalog["contextWindow"]!.GetValue<int>());
        Assert.Equal(22, fromCatalog["maxTokens"]!.GetValue<int>());
        Assert.Equal(new[] { "text" }, fromCatalog["input"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal("{\"minimal\":\"low\",\"low\":\"low\",\"medium\":\"medium\",\"high\":\"high\",\"xhigh\":\"xhigh\"}", fromCatalog["reasoningEfforts"]!.ToJsonString());

        // The user's entry wins field by field; what it lacks comes from the catalog.
        var user = JsonNode.Parse("{\"id\":\"gpt-6-astra\",\"name\":\"My Astra\",\"contextWindow\":999,\"reasoningEfforts\":{\"low\":\"low\"}}")!.AsObject();
        var merged = CodexFastService.BuildEntry("gpt-6-astra", user, catalog);
        Assert.Equal("My Astra (Fast)", merged["name"]!.GetValue<string>());
        Assert.Equal(999, merged["contextWindow"]!.GetValue<int>());
        Assert.Equal(22, merged["maxTokens"]!.GetValue<int>());
        Assert.Equal("{\"low\":\"low\"}", merged["reasoningEfforts"]!.ToJsonString());

        // A catalog model that does not reason says so.
        var plain = JsonNode.Parse("{\"name\":\"X\",\"reasoning\":false}")!.AsObject();
        Assert.False(CodexFastService.BuildEntry("gpt-6-astra", null, plain)["reasoningEfforts"]!.GetValue<bool>());
    }

    [Fact]
    public void SyncInstallsTheComponentAndAppendsTheMissingFastModelsAfterTheUsersEntries()
    {
        var patch = "# mine\n" + BaseModels + "      other-gw:\n        api: openai-completions\n        baseURL: https://gw.example.test/v1\n        models:\n          - id: gw-a\n- id: last-plugin\n";
        using var f = new DshFixture(null, patch, fast: true, packageJson: Package);

        var result = f.Fast.Sync();

        Assert.True(result.ComponentChanged);
        Assert.Equal(new[] { "gpt-6.1-sol-fast", "gpt-6-astra-fast", "gpt-6-luna-fast" }, result.AddedModels);
        Assert.Empty(result.RemovedModels);
        var after = f.Patch;
        // The user's text is untouched; the three entries sit right after the last base model.
        Assert.StartsWith("# mine\n" + BaseModels, after);
        Assert.EndsWith("      other-gw:\n        api: openai-completions\n        baseURL: https://gw.example.test/v1\n        models:\n          - id: gw-a\n- id: last-plugin\n", after);
        var models = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels();
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna", "gpt-6.1-sol-fast", "gpt-6-astra-fast", "gpt-6-luna-fast" }, models.Select(m => m.Id).ToArray());
        var fast = models.Single(m => m.Id == "gpt-6-astra-fast").Entry!;
        Assert.Equal("GPT-6 Astra (Fast)", fast["name"]!.GetValue<string>());
        Assert.True(models.Where(m => m.Id.EndsWith("-fast")).All(CodexFastService.IsOurFastEntry));
        Assert.Equal(FastComponentState.Installed, f.Fast.Installer.GetStatus().State);
        Assert.Contains("首次安装需要重启 dsh", string.Join("\n", CodexFastService.Describe(result)));
        Assert.Contains("2.5 倍", string.Join("\n", CodexFastService.Describe(result)));

        // Nothing more to do the second time.
        var again = f.Fast.Sync();
        Assert.False(again.Changed);
        Assert.Equal(after, f.Patch);
        Assert.Empty(CodexFastService.Describe(again));
    }

    [Fact]
    public void SyncTakesMetadataFromTheUsersBaseEntryAndTheCatalog()
    {
        var patch = "- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex:\n        models:\n          - id: gpt-6.1-sol\n            name: My Sol\n            contextWindow: 123456\n            maxTokens: 4321\n            input: [text]\n            reasoningEfforts:\n              high: high\n          - id: gpt-6-luna\n";
        var catalog = new Dictionary<string, JsonObject>
        {
            ["gpt-6-luna"] = JsonNode.Parse("{\"id\":\"gpt-6-luna\",\"name\":\"Catalog Luna\",\"contextWindow\":55555,\"maxTokens\":4444,\"input\":[\"text\",\"image\"],\"reasoning\":true,\"thinkingLevelMap\":{\"off\":\"none\"}}")!.AsObject(),
        };
        using var f = new DshFixture(null, patch, fast: true, packageJson: Package, catalogModels: catalog);

        var result = f.Fast.Sync();

        Assert.Equal(new[] { "gpt-6.1-sol-fast", "gpt-6-luna-fast" }, result.AddedModels);
        var models = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels();
        var sol = models.Single(m => m.Id == "gpt-6.1-sol-fast").Entry!;
        Assert.Equal("My Sol (Fast)", sol["name"]!.GetValue<string>());
        Assert.Equal(123456, sol["contextWindow"]!.GetValue<int>());
        Assert.Equal(4321, sol["maxTokens"]!.GetValue<int>());
        Assert.Equal("{\"high\":\"high\"}", sol["reasoningEfforts"]!.ToJsonString());
        var luna = models.Single(m => m.Id == "gpt-6-luna-fast").Entry!;
        Assert.Equal("Catalog Luna (Fast)", luna["name"]!.GetValue<string>());
        Assert.Equal(55555, luna["contextWindow"]!.GetValue<int>());
        Assert.Equal("{\"off\":\"none\",\"minimal\":\"minimal\",\"low\":\"low\",\"medium\":\"medium\",\"high\":\"high\"}", luna["reasoningEfforts"]!.ToJsonString());
    }

    [Fact]
    public void OnlyOfficialBasesPresentInTheRouteGetATwinAndUserEntriesAreNeverTouched()
    {
        // gpt-5.5 is not an official base; gpt-6-astra-fast is the user's own entry (no " (Fast)" name), so no twin is added for it.
        var patch = "- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex:\n        models:\n          - id: gpt-5.5\n          - id: gpt-6-astra\n          - id: gpt-6-astra-fast\n            name: Mine\n          - id: gpt-6.1-sol\n";
        using var f = new DshFixture(null, patch, fast: true, packageJson: Package);

        var result = f.Fast.Sync();

        Assert.Equal(new[] { "gpt-6.1-sol-fast" }, result.AddedModels);
        var after = f.Patch;
        Assert.StartsWith(patch, after);
        Assert.DoesNotContain("gpt-5.5-fast", after);
        Assert.Contains("name: Mine", after);
    }

    [Fact]
    public void WithoutAnOpenaiCodexRouteNothingIsInstalled()
    {
        using var f = new DshFixture(null, "- id: llm-pi-ai\n  config:\n    providers:\n      other-gw:\n        api: openai-completions\n        baseURL: https://gw.example.test/v1\n        models:\n          - id: gw-a\n", fast: true, packageJson: Package);

        var result = f.Fast.Sync();

        Assert.False(result.Changed);
        Assert.False(Directory.Exists(f.PluginDir));
        Assert.DoesNotContain("dsh-account-manager-fast", File.ReadAllText(f.PackageJsonPath));
    }

    [Fact]
    public void AnEmptyOpenaiCodexRouteIsUpgradedFirstAndThenGetsItsTwins()
    {
        using var f = new DshFixture(null, "- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex: {}\n", fast: true, packageJson: Package);

        var result = f.Fast.Sync();

        Assert.Equal(3, result.AddedModels.Count);
        var ids = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels().Select(m => m.Id).ToArray();
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna", "gpt-6.1-sol-fast", "gpt-6-astra-fast", "gpt-6-luna-fast" }, ids);
    }

    [Fact]
    public void WhenTheComponentCannotBeInstalledOurFastEntriesAreRemovedAndTheUsersStay()
    {
        var patch = BaseModels + "          - id: gpt-6.1-sol-fast\n            name: GPT-6.1 Sol (Fast)\n            contextWindow: 500000\n          - id: gpt-6-astra-fast\n            name: Mine, not ours\n";
        // No package.json: the profile was never started, the component cannot be installed.
        using var f = new DshFixture(null, patch, fast: true);

        var result = f.Fast.Sync();

        Assert.Equal(new[] { "gpt-6.1-sol-fast" }, result.RemovedModels);
        Assert.Contains("没有安装 Fast 组件", result.Note);
        var ids = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels().Select(m => m.Id).ToArray();
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna", "gpt-6-astra-fast" }, ids);
        Assert.Contains("name: Mine, not ours", f.Patch);
        Assert.Contains("避免把 -fast", f.RunLog);
    }

    [Fact]
    public void WhenTheComponentFolderIsGoneOurFastEntriesAreRemovedAgain()
    {
        using var f = new DshFixture(null, BaseModels, fast: true, packageJson: Package);
        f.Fast.Sync();
        Assert.Contains("gpt-6-luna-fast", f.Patch);
        // dsh's own plugin manager or the user removed the folder and the bundle name.
        Directory.Delete(f.PluginDir, true);
        File.WriteAllText(f.PackageJsonPath, Package);
        File.Delete(f.PackageJsonPath); // and then the package.json is gone as well: the component cannot be reinstalled

        var result = f.Fast.Sync();

        Assert.Equal(3, result.RemovedModels.Count);
        Assert.Equal(BaseModels, f.Patch);
    }

    [Fact]
    public void ATwinWhoseBaseModelIsGoneIsRemovedBecauseThePluginRunsTheBaseModel()
    {
        var patch = "- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex:\n        models:\n          - id: gpt-6-luna\n          - id: gpt-6.1-sol-fast\n            name: GPT-6.1 Sol (Fast)\n";
        using var f = new DshFixture(null, patch, fast: true, packageJson: Package);

        var result = f.Fast.Sync();

        Assert.Equal(new[] { "gpt-6.1-sol-fast" }, result.RemovedModels);
        Assert.DoesNotContain("gpt-6.1-sol-fast", f.Patch);
    }

    // ---- subagents and import ----

    [Fact]
    public void SubagentCandidatesLeaveTheFastTwinsOutAndKeepTheUsersAllowedEntries()
    {
        var patch = BaseModels + "          - id: gpt-6.1-sol-fast\n            name: GPT-6.1 Sol (Fast)\n- id: subagent-model-selection-settings\n  config:\n    enabled: true\n    allowedModels:\n      - provider: openai-codex\n        model: gpt-6.1-sol-fast\n";
        using var f = new DshFixture(null, patch);

        var candidates = f.Subagents.ComputeCandidates(ProfilePatchDocument.Load(f.PatchPath));

        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna" }, candidates.Select(c => c.Model).ToArray());
        var merge = f.Subagents.Merge();
        Assert.Equal(3, merge.Added.Count); // the three bases; the user's own fast route is kept, not re-added
        Assert.Contains("model: gpt-6.1-sol-fast", f.Patch); // the user's entry stays
        Assert.DoesNotContain(ProfilePatchDocument.Load(f.PatchPath).GetSubagentSelection().Routes, r => r.Model == "gpt-6-astra-fast");
    }

    private static JsonArray Backup(params JsonObject[] accounts) => new(accounts.Select(a => (JsonNode?)a).ToArray());

    [Fact]
    public async Task AnImportEnablesTheRouteInstallsTheComponentAndAddsTheTwinsWithoutTouchingTheDefaultModel()
    {
        using var f = new DshFixture(null, "[]\n", subagents: true, fast: true, packageJson: Package);

        var result = await f.Import.ImportAsync(await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in.json", Backup(TestAccounts.CockpitOauth("c-1", "a@example.test", "acct-1", "refresh-1")))));

        Assert.Equal(0, result.Failed);
        var ids = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels().Select(m => m.Id).ToArray();
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna", "gpt-6.1-sol-fast", "gpt-6-astra-fast", "gpt-6-luna-fast" }, ids);
        Assert.Equal(FastComponentState.Installed, f.Fast.Installer.GetStatus().State);
        Assert.DoesNotContain("agent-default-model", f.Patch);
        var routes = ProfilePatchDocument.Load(f.PatchPath).GetSubagentSelection().Routes;
        Assert.Equal(3, routes.Count);
        Assert.DoesNotContain(routes, r => r.Model.EndsWith("-fast"));

        // The activation already did the work (before the import's own step), so the report may be in the log; the message is in one of them.
        var text = string.Join("\n", result.Messages) + "\n" + f.RunLog;
        Assert.Contains("首次安装需要重启 dsh（完全退出 DSH 再打开）后 Fast 模型才可用；Fast 按官方规则以 2.5 倍消耗订阅额度。", text);
        Assert.Contains("Fast 组件", text);

        // A second import changes nothing about Fast.
        var patchBefore = f.Patch;
        var packageBefore = File.ReadAllText(f.PackageJsonPath);
        var second = await f.Import.ImportAsync(await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in2.json", Backup(TestAccounts.CockpitOauth("c-2", "b@example.test", "acct-2", "refresh-2")))));
        Assert.Equal(0, second.Failed);
        Assert.Equal(packageBefore, File.ReadAllText(f.PackageJsonPath));
        Assert.DoesNotContain(second.Messages, m => m.Contains("Fast"));
        Assert.Equal(patchBefore, f.Patch);
    }

    [Fact]
    public async Task AnImportIntoAProfileWithoutPackageJsonStillWorksAndSaysWhyFastWasSkipped()
    {
        using var f = new DshFixture(null, "[]\n", subagents: true, fast: true);

        var result = await f.Import.ImportAsync(await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in.json", Backup(TestAccounts.CockpitOauth("c-1", "a@example.test", "acct-1", "refresh-1")))));

        Assert.Equal(0, result.Failed);
        Assert.DoesNotContain("-fast", f.Patch);
        Assert.False(Directory.Exists(f.PluginDir));
        Assert.Contains("没有安装 Fast 组件", string.Join("\n", result.Messages) + f.RunLog);
    }
}
