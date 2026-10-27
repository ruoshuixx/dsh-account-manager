using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using AccountManager.Core.Import;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

/// <summary>Letting dsh's main agent delegate: the subagent-model-selection-settings entry and its candidate routes.</summary>
public class SubagentRoutesTests
{
    private static SubagentRoute R(string p, string m) => new(p, m);

    private const string Providers = "- id: llm-pi-ai\n  config:\n    providers:\n      other-gw:\n        api: openai-completions\n        baseURL: https://gw.example.test/v1\n        apiKeyEnv: GW_KEY\n        models:\n          - id: gw-a\n          - id: gw-b\n";

    private static readonly string[] Catalog = { "gpt-5.5", "gpt-5.3-codex-spark", "gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol" };

    // ---- the entry in the file ----

    [Fact]
    public void AFreshProfileGetsTheEntryWithoutANameAndNothingElseMoves()
    {
        var doc = ProfilePatchDocument.Parse(Providers, "p");

        var change = doc.AddSubagentRoutes(new[] { R("other-gw", "gw-a"), R("openai-codex", "gpt-6.1-sol") });

        Assert.Equal(2, change.Added.Count);
        Assert.True(change.EnabledChanged);
        Assert.Equal(Providers + "- id: subagent-model-selection-settings\n  config:\n    enabled: true\n    allowedModels:\n      - provider: other-gw\n        model: gw-a\n      - provider: openai-codex\n        model: gpt-6.1-sol\n", doc.Text);
        Assert.DoesNotContain("name:", doc.Text.Substring(Providers.Length));
        var state = doc.GetSubagentSelection();
        Assert.True(state.Enabled);
        Assert.Equal(new[] { R("other-gw", "gw-a"), R("openai-codex", "gpt-6.1-sol") }, state.Routes);
    }

    [Fact]
    public void ACommentOnlyOrEmptyFileGetsAListWithTheEntry()
    {
        var doc = ProfilePatchDocument.Parse("# nothing yet", "p");

        doc.AddSubagentRoutes(new[] { R("a", "m") });

        Assert.StartsWith("# nothing yet\n- id: subagent-model-selection-settings\n", doc.Text);
        Assert.Equal(new[] { R("a", "m") }, ProfilePatchDocument.Parse(doc.Text, "p").GetSubagentSelection().Routes);
    }

    private const string Existing = """
        # before
        - id: subagent-model-selection-settings
          # about this entry
          config:
            enabled: true   # keep
            allowedModels:
              - provider: openai-codex
                model: gpt-6.1-sol   # the user's own
            extra: 1
          other: x
        # after
        - id: tail
          enabled: true

        """;

    [Fact]
    public void AnExistingEntryOnlyGetsNewListItemsAppendedAndEveryOtherByteStays()
    {
        var doc = ProfilePatchDocument.Parse(Existing, "p");

        var change = doc.AddSubagentRoutes(new[] { R("openai-codex", "gpt-6.1-sol"), R("other-gw", "gw-a"), R("other-gw", "gw-a"), R("other-gw", "gw-b") });

        Assert.Equal(new[] { R("other-gw", "gw-a"), R("other-gw", "gw-b") }, change.Added);
        Assert.False(change.EnabledChanged);
        var expected = Existing.Replace("gpt-6.1-sol   # the user's own\n", "gpt-6.1-sol   # the user's own\n      - provider: other-gw\n        model: gw-a\n      - provider: other-gw\n        model: gw-b\n");
        Assert.Equal(expected, doc.Text);
    }

    [Fact]
    public void NothingToDoLeavesTheTextAlone()
    {
        var doc = ProfilePatchDocument.Parse(Existing, "p");

        var change = doc.AddSubagentRoutes(new[] { R("openai-codex", "gpt-6.1-sol") });

        Assert.False(change.Changed);
        Assert.Equal(Existing, doc.Text);
    }

    [Theory]
    [InlineData("    enabled: false\n", "    enabled: true\n")]
    [InlineData("    enabled: no_such\n", null)]
    public void EnabledIsMadeTrueByReplacingOnlyTheValue(string line, string? expectedLine)
    {
        var text = "- id: subagent-model-selection-settings\n  config:\n" + line + "    allowedModels:\n      - provider: a\n        model: m\n";
        var doc = ProfilePatchDocument.Parse(text, "p");

        if (expectedLine is null) { Assert.Throws<DshException>(() => doc.AddSubagentRoutes(new[] { R("b", "nn") })); Assert.Equal(text, doc.Text); return; }
        var change = doc.AddSubagentRoutes(new[] { R("b", "nn") });

        Assert.True(change.EnabledChanged);
        Assert.Equal("- id: subagent-model-selection-settings\n  config:\n" + expectedLine + "    allowedModels:\n      - provider: a\n        model: m\n      - provider: b\n        model: nn\n", doc.Text);
    }

    [Theory]
    [InlineData("- id: subagent-model-selection-settings\n")]
    [InlineData("- id: subagent-model-selection-settings\n  config:\n")]
    [InlineData("- id: subagent-model-selection-settings\n  config:\n    enabled: true\n")]
    [InlineData("- id: subagent-model-selection-settings\n  config:\n    allowedModels: []\n")]
    [InlineData("- id: subagent-model-selection-settings\n  config:\n    enabled: false\n    allowedModels:\n")]
    public void MissingPiecesAreFilledIn(string text)
    {
        var doc = ProfilePatchDocument.Parse(text, "p");

        doc.AddSubagentRoutes(new[] { R("a", "m") });

        var state = ProfilePatchDocument.Parse(doc.Text, "p").GetSubagentSelection();
        Assert.True(state.Enabled);
        Assert.Equal(new[] { R("a", "m") }, state.Routes);
        Assert.StartsWith(text.TrimEnd('\n').Split('\n')[0], doc.Text);
    }

    [Fact]
    public void DuplicatesAndEmptyEnabledListsAreNeverWritten()
    {
        const string dup = "- id: subagent-model-selection-settings\n  config:\n    enabled: true\n    allowedModels:\n      - provider: a\n        model: m\n      - provider: a\n        model: m\n";
        var doc = ProfilePatchDocument.Parse(dup, "p");
        Assert.Throws<DshException>(() => doc.AddSubagentRoutes(new[] { R("b", "nn") }));
        Assert.Equal(dup, doc.Text);

        var empty = ProfilePatchDocument.Parse("- id: other\n", "p");
        Assert.Throws<DshException>(() => empty.AddSubagentRoutes(Array.Empty<SubagentRoute>()));
        Assert.Throws<DshException>(() => empty.AddSubagentRoutes(new[] { R("", "m"), R("a", " ") }));
        Assert.Equal("- id: other\n", empty.Text);
    }

    [Theory]
    [InlineData("- id: subagent-model-selection-settings\n  config: {enabled: true, allowedModels: [{provider: a, model: m}]}\n")]
    [InlineData("- {id: subagent-model-selection-settings, config: {enabled: true}}\n")]
    [InlineData("- id: subagent-model-selection-settings\n  config:\n    enabled: true\n    allowedModels: [{provider: a, model: m}]\n")]
    [InlineData("- id: subagent-model-selection-settings\n  config:\n    enabled: true\n    allowedModels: not-a-list\n")]
    [InlineData("- id: subagent-model-selection-settings\n  config:\n    enabled: true\n    allowedModels:\n      - just-a-string\n")]
    public void OddShapesAreRefusedWithoutRewriting(string text)
    {
        var doc = ProfilePatchDocument.Parse(text, "p");

        Assert.Throws<DshException>(() => doc.AddSubagentRoutes(new[] { R("b", "nn") }));

        Assert.Equal(text, doc.Text);
    }

    [Fact]
    public void AFlowStyleFileIsNotRewritten()
    {
        var doc = ProfilePatchDocument.Parse("[{id: other}]", "p");

        Assert.Throws<DshException>(() => doc.AddSubagentRoutes(new[] { R("b", "nn") }));

        Assert.Equal("[{id: other}]", doc.Text);
    }

    [Fact]
    public void CrLfFilesKeepTheirLineEndings()
    {
        var text = "- id: subagent-model-selection-settings\r\n  config:\r\n    enabled: true\r\n    allowedModels:\r\n      - provider: a\r\n        model: m\r\n";
        var doc = ProfilePatchDocument.Parse(text, "p");

        doc.AddSubagentRoutes(new[] { R("b", "nn") });

        Assert.Equal(text + "      - provider: b\r\n        model: nn\r\n", doc.Text);
        foreach (var line in doc.Text.Split('\n').SkipLast(1)) { Assert.EndsWith("\r", line); }
    }

    // ---- the Codex catalog ----

    private const string CatalogJson = """
        {
          "openai-codex-responses": {
            "chat:gpt-5.5": {"id": "gpt-5.5", "name": "GPT-5.5", "type": "chat"},
            "chat:gpt-6.1-sol": {"id": "gpt-6.1-sol", "name": "GPT-6.1 Sol", "type": "chat", "contextWindow": 272000},
            "image:gpt-image": {"id": "gpt-image", "type": "image"},
            "classifier:mod": {"id": "mod", "type": "classifier"},
            "chat:gpt-6-luna": {"id": "gpt-6-luna", "type": "chat"},
            "chat:gpt-6.1-sol-dup": {"id": "gpt-6.1-sol", "type": "chat"}
          }
        }
        """;

    [Fact]
    public void CatalogParsingKeepsOnlyChatModelsInOrderWithoutDuplicates()
    {
        Assert.Equal(new[] { "gpt-5.5", "gpt-6.1-sol", "gpt-6-luna" }, CodexCatalog.ParseChatModelIds(CatalogJson));
        Assert.Empty(CodexCatalog.ParseChatModelIds("not json"));
        Assert.Empty(CodexCatalog.ParseChatModelIds("[1,2]"));
    }

    private static string MakeCatalog(string nodeModules, string json = CatalogJson)
    {
        var dir = Path.Combine(nodeModules, "@earendil-works", "pi-ai", "dist", "providers", "data");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "openai-codex.json");
        File.WriteAllText(file, json);
        return file;
    }

    [Fact]
    public void TheCatalogIsFoundDirectlyInPnpmStoresAndBelowAnInstallFolderButNotDeeperThanTheBound()
    {
        using var temp = new TempDir();
        var direct = MakeCatalog(Path.Combine(temp.Path, "a", "node_modules"));
        Assert.Equal(direct, CodexCatalog.Locate(new[] { Path.Combine(temp.Path, "a", "node_modules") }));

        var pnpm = MakeCatalog(Path.Combine(temp.Path, "b", "node_modules", ".pnpm", "@earendil-works+pi-ai@1.2.3", "node_modules"));
        Assert.Equal(pnpm, CodexCatalog.Locate(new[] { Path.Combine(temp.Path, "b", "node_modules") }));

        var app = MakeCatalog(Path.Combine(temp.Path, "DeepSeek Harness", "resources", "app", "node_modules"));
        Assert.Equal(app, CodexCatalog.Locate(new[] { Path.Combine(temp.Path, "DeepSeek Harness") }));

        var deep = Path.Combine(temp.Path, "c", "1", "2", "3", "4", "5", "6", "7", "8");
        MakeCatalog(Path.Combine(deep, "node_modules"));
        Assert.Null(CodexCatalog.Locate(new[] { Path.Combine(temp.Path, "c") }));
        Assert.Null(CodexCatalog.Locate(new[] { Path.Combine(temp.Path, "missing") }));
        Assert.Equal(new[] { "gpt-5.5", "gpt-6.1-sol", "gpt-6-luna" }, CodexCatalog.ReadChatModelIds(new[] { Path.Combine(temp.Path, "a", "node_modules") }));
    }

    [Fact]
    public void TheLauncherScriptPointsAtTheRuntimeToSearch()
    {
        using var temp = new TempDir();
        var nodeModules = Path.Combine(temp.Path, "rt", "node_modules");
        MakeCatalog(nodeModules);
        var shim = temp.File("dsh");
        File.WriteAllText(shim, "#!/bin/sh\nDSH_BIN='" + Path.Combine(nodeModules, "@deepseek-ai", "dsh", "lib", "bin.js") + "'\nexec node \"$DSH_BIN\" \"$@\"\n");

        var roots = CodexCatalog.DefaultSearchRoots(_ => null, temp.File("nohome"), shim);

        Assert.Equal(nodeModules, roots[0]);
        Assert.NotNull(CodexCatalog.Locate(roots));
    }

    // ---- candidate routes ----

    private static SubagentRouteService Service(string patchPath, IReadOnlyList<string>? catalog = null)
        => new(() => DshHome.Resolve(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(patchPath)!)!)!, "tauri", _ => null, "/nonexistent"),
            codexCatalog: () => catalog ?? Array.Empty<string>());

    [Fact]
    public void CandidatesAreEveryProviderModelPlusTheOfficialCodexListFilteredByTheCatalog()
    {
        using var f = new DshFixture(null, Providers + "      openai-codex: {}\n", codexCatalog: Catalog);

        var routes = f.Subagents.ComputeCandidates(ProfilePatchDocument.Load(f.PatchPath));

        // Catalog entries outside the official list (retired gpt-5.5 / gpt-5.3-codex-spark, gpt-6-sol, gpt-5.6-sol) are never offered.
        Assert.Equal(new[]
        {
            R("other-gw", "gw-a"), R("other-gw", "gw-b"),
            R("openai-codex", "gpt-6.1-sol"), R("openai-codex", "gpt-6-astra"), R("openai-codex", "gpt-6-luna"),
        }, routes);
    }

    [Fact]
    public void ACatalogThatLacksAnOfficialModelDropsItBecauseDshCannotRunIt()
    {
        using var f = new DshFixture(null, "- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex: {}\n", codexCatalog: new[] { "gpt-5.5", "gpt-6.1-sol", "gpt-6-luna" });

        var routes = f.Subagents.ComputeCandidates(ProfilePatchDocument.Load(f.PatchPath));

        Assert.Equal(new[] { R("openai-codex", "gpt-6.1-sol"), R("openai-codex", "gpt-6-luna") }, routes);
    }

    [Fact]
    public void WithoutACatalogTheOfficialListIsUsedAsItIs()
    {
        using var f = new DshFixture(null, "- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex: {}\n", codexCatalog: Array.Empty<string>());

        var routes = f.Subagents.ComputeCandidates(ProfilePatchDocument.Load(f.PatchPath));

        Assert.Equal(SubagentRouteService.OfficialCodexModels.Select(m => R("openai-codex", m)), routes);
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-astra", "gpt-6-luna" }, SubagentRouteService.OfficialCodexModels);
        Assert.DoesNotContain(routes, r => r.Model is "gpt-6-sol" or "gpt-5.5" or "gpt-5.3-codex-spark" || r.Model.StartsWith("gpt-5.6", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitCodexModelsOfTheUserLayerAreUsedExactly()
    {
        const string patch = "- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex:\n        models: [gpt-5.6-luna, {id: gpt-6-sol}]\n";
        using var f = new DshFixture(null, patch, codexCatalog: Catalog);

        var routes = f.Subagents.ComputeCandidates(ProfilePatchDocument.Load(f.PatchPath));

        Assert.Equal(new[] { R("openai-codex", "gpt-5.6-luna"), R("openai-codex", "gpt-6-sol") }, routes);
    }

    [Fact]
    public void NoCodexRouteMeansNoCodexCandidatesAndImageOnlyModelsAreSkipped()
    {
        const string patch = "- id: llm-pi-ai\n  config:\n    providers:\n      gw:\n        models:\n          - id: chat-1\n            input: [text, image]\n          - id: vision-only\n            input: [image]\n          - plain-string\n";
        using var f = new DshFixture(null, patch);

        var routes = f.Subagents.ComputeCandidates(ProfilePatchDocument.Load(f.PatchPath));

        Assert.Equal(new[] { R("gw", "chat-1"), R("gw", "plain-string") }, routes);
    }

    // ---- merging (backup, lock, read-back) ----

    [Fact]
    public void MergeWritesAfterABackupKeepsUserEntriesAndASecondMergeDoesNothing()
    {
        var patch = Providers + "      openai-codex: {}\n- id: subagent-model-selection-settings\n  config:\n    enabled: false\n    allowedModels:\n      - provider: deepseek-official\n        model: deepseek-flash\n";
        using var f = new DshFixture(null, patch, codexCatalog: Catalog);

        var first = f.Subagents.Merge();

        Assert.True(first.Changed);
        Assert.True(first.EnabledChanged);
        Assert.Equal(6, first.Added.Count);
        Assert.True(File.Exists(first.Backup));
        var state = ProfilePatchDocument.Load(f.PatchPath).GetSubagentSelection();
        Assert.True(state.Enabled);
        Assert.Equal(R("deepseek-official", "deepseek-flash"), state.Routes[0]); // the user's entry stays first and untouched
        Assert.Equal(7, state.Routes.Count);
        Assert.Contains("新对话", f.RunLog);
        var backupsAfterFirst = f.Backups.ListBackups().Count;

        var second = f.Subagents.Merge();

        Assert.False(second.Changed);
        Assert.Equal(backupsAfterFirst, f.Backups.ListBackups().Count);
        Assert.Equal(7, ProfilePatchDocument.Load(f.PatchPath).GetSubagentSelection().Routes.Count);
    }

    [Fact]
    public void MergeRefusesAnOddEntryWithoutTouchingTheFile()
    {
        var patch = Providers + "- id: subagent-model-selection-settings\n  config: {enabled: true}\n";
        using var f = new DshFixture(null, patch);

        Assert.Throws<DshException>(() => f.Subagents.Merge());

        Assert.Equal(patch, f.Patch);
    }

    // ---- upgrading the empty openai-codex entry 0.1.2 wrote ----

    // The built-in list: ids only where dsh's catalog has the model, explicit facts for gpt-6.1-terra (not in dsh 0.2.1's catalog).
    private const string OfficialBlock = "      openai-codex:\n        models:\n          - id: gpt-6.1-sol\n          - id: gpt-6.1-terra\n";

    [Fact]
    public void AnEmptyMappingCodexEntryIsUpgradedToTheOfficialModelsAfterABackup()
    {
        using var f = new DshFixture(null, Providers + "      openai-codex: {}\n");

        var upgraded = f.Subagents.UpgradeEmptyCodexRoute();

        Assert.True(upgraded);
        Assert.StartsWith(Providers + OfficialBlock, f.Patch);
        Assert.Equal(SubagentRouteService.OfficialCodexModels, ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels().Select(m => m.Id).ToArray());
        Assert.Single(f.Backups.ListBackups());
        Assert.Contains("openai-codex 路由原来是空条目", f.RunLog);
        Assert.False(f.Subagents.UpgradeEmptyCodexRoute()); // second time: nothing to do, no new backup
        Assert.Single(f.Backups.ListBackups());
    }

    [Fact]
    public void ANullValuedCodexKeyIsUpgradedToo()
    {
        using var f = new DshFixture(null, Providers + "      openai-codex:\n");

        Assert.True(f.Subagents.UpgradeEmptyCodexRoute());

        Assert.StartsWith(Providers + OfficialBlock, f.Patch);
        Assert.Equal(SubagentRouteService.OfficialCodexModels, ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels().Select(m => m.Id).ToArray());
    }

    [Fact]
    public void ACodexEntryWithModelsStaysByteIdentical()
    {
        var patch = Providers + "      openai-codex:\n        models:\n          - id: gpt-x\n            name: GPT X\n";
        using var f = new DshFixture(null, patch);

        Assert.False(f.Subagents.UpgradeEmptyCodexRoute());

        Assert.Equal(patch, f.Patch);
        Assert.Empty(f.Backups.ListBackups());
    }

    [Fact]
    public void ACodexEntryWithOnlyADisplayNameStaysByteIdentical()
    {
        var patch = Providers + "      openai-codex:\n        displayName: Mine\n";
        using var f = new DshFixture(null, patch);

        Assert.False(f.Subagents.UpgradeEmptyCodexRoute());

        Assert.Equal(patch, f.Patch);
        Assert.Empty(f.Backups.ListBackups());
    }

    [Fact]
    public void MergeUpgradesAnEmptyCodexEntryFirstSoTheListedModelsDriveTheCandidates()
    {
        using var f = new DshFixture(null, Providers + "      openai-codex: {}\n", codexCatalog: Catalog);

        var result = f.Subagents.Merge();

        Assert.Contains(OfficialBlock, f.Patch);
        Assert.Equal(6, result.Added.Count);
        Assert.Equal(SubagentRouteService.OfficialCodexModels.Select(m => R("openai-codex", m)), result.Added.Where(r => r.Provider == "openai-codex"));
    }

    [Fact]
    public async Task ActivatingWithAnEmptyCodexEntryUpgradesItAndMergesTheSubagentRoutes()
    {
        using var f = new DshFixture(null, Providers + "      openai-codex: {}\n", codexCatalog: Catalog, subagents: true);

        await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);

        Assert.Contains(OfficialBlock, f.Patch);
        Assert.Equal(4, ProfilePatchDocument.Load(f.PatchPath).GetSubagentSelection().Routes.Count(r => r.Provider == "openai-codex"));
    }

    // ---- import and Codex activation ----

    private static JsonArray Backup(params JsonObject[] accounts) => new(accounts.Select(a => (JsonNode?)a).ToArray());

    [Fact]
    public async Task EveryImportMergesTheCandidateRoutesAndSaysItAppliesToNewConversations()
    {
        using var f = new DshFixture(null, "[]\n", codexCatalog: Catalog, subagents: true);
        var api = TestAccounts.CockpitApi("src-1", "Gateway", "https://api.example.test/v1", "chat_completions", "fake-key", "model-x", "model-y");

        var preview = await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in.json", Backup(api)));
        var result = await f.Import.ImportAsync(preview);

        Assert.Equal(0, result.Failed);
        var state = ProfilePatchDocument.Load(f.PatchPath).GetSubagentSelection();
        Assert.True(state.Enabled);
        var providerId = DshBackend.CockpitProviderId("src-1");
        Assert.Equal(new[] { R(providerId, "model-x"), R(providerId, "model-y") }, state.Routes);
        Assert.Contains(result.Messages, m => m.Contains("已把 2 条路由加入 dsh 的子代理可用模型"));
        Assert.Contains(result.Messages, m => m.Contains("新对话"));
        Assert.Contains("新对话", f.RunLog);
    }

    [Fact]
    public async Task ImportingAgainKeepsUserEntriesAndNeverDuplicates()
    {
        using var f = new DshFixture(null, "[]\n", subagents: true);
        var api = TestAccounts.CockpitApi("src-1", "Gateway", "https://api.example.test/v1", "chat_completions", "fake-key", "model-x");
        var path = DshBackendApiTests.WriteJson(f, "in.json", Backup(api));
        await f.Import.ImportAsync(await f.Import.PreviewAsync(path));
        CredentialsDocument.Load(f.CredentialsPath); // untouched by the merge
        File.WriteAllText(f.PatchPath, f.Patch.Replace("      - provider: " + DshBackend.CockpitProviderId("src-1") + "\n        model: model-x\n", "      - provider: " + DshBackend.CockpitProviderId("src-1") + "\n        model: model-x\n      - provider: mine\n        model: custom\n"));

        var again = await f.Import.ImportAsync(await f.Import.PreviewAsync(path));

        Assert.DoesNotContain(again.Messages, m => m.Contains("新对话")); // nothing new to add
        var routes = ProfilePatchDocument.Load(f.PatchPath).GetSubagentSelection().Routes;
        Assert.Equal(2, routes.Count);
        Assert.Contains(R("mine", "custom"), routes);
    }

    [Fact]
    public async Task ACodexOnlyImportOfAnAccountEnablesTheRouteAndAddsTheCodexModels()
    {
        using var f = new DshFixture(null, "[]\n", codexCatalog: Catalog, subagents: true);

        var result = await f.Import.ImportAsync(await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in.json", Backup(TestAccounts.CockpitOauth("c-1", "a@example.test", "acct-1", "refresh-1")))));

        Assert.Equal(0, result.Failed);
        Assert.Contains("openai-codex", ProfilePatchDocument.Load(f.PatchPath).GetProviders()!.Select(kv => kv.Key));
        var routes = ProfilePatchDocument.Load(f.PatchPath).GetSubagentSelection().Routes;
        Assert.Equal(SubagentRouteService.OfficialCodexModels.Select(m => R("openai-codex", m)), routes);
    }

    [Fact]
    public async Task BecomingCurrentEnablesTheCodexRouteAndTheSubagentModelsTogether()
    {
        using var f = new DshFixture(null, "[]\n", codexCatalog: Catalog, subagents: true);

        await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);

        var routes = ProfilePatchDocument.Load(f.PatchPath).GetSubagentSelection();
        Assert.True(routes.Enabled);
        Assert.Equal(4, routes.Routes.Count);
        Assert.Contains("子代理可用模型", f.RunLog);
    }

    [Fact]
    public async Task WithoutTheServiceNothingAboutSubagentsIsWritten()
    {
        using var f = new DshFixture(null, "[]\n", subagents: false);

        await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);

        Assert.DoesNotContain(ProfilePatchDocument.SubagentEntryId, f.Patch);
    }
}
