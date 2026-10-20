using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Dsh;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

public class DshBackendApiTests
{
    private const string Credentials = """
        version: 1
        refs:
          ARK_CODING_API_KEY: fake-ark-key
          ANTHROPIC_KEY: fake-anthropic-key
          CODEX_OAUTH_1: '{"third":"party"}'
        records:
          client-connection/browser-session:
            kind: grant
            payload: {cookie: abc}

        """;

    private const string Patch = """
        # user header comment
        - id: other-plugin
          config:
            a: 1   # inline comment
        - id: llm-pi-ai
          name: Pi
          config:
            providers:
              openai-codex:
                models: [gpt-5]
              volcengine-coding:
                displayName: 火山Codingplan
                apiKeyEnv: ARK_CODING_API_KEY
                api: openai-responses
                baseURL: https://ark.cn-beijing.volces.com/api/coding/v3
                models:
                  - id: doubao-seed-code
                    name: Doubao Seed Code
                    contextWindow: 256000
                    maxTokens: 32000
                    input: [text, image]
                    reasoningEfforts: {low: low, high: high}
              native-anthropic:
                api: anthropic-messages
                baseURL: https://api.anthropic.example/v1
                apiKeyEnv: ANTHROPIC_KEY
                models: [{id: claude-x}]
        # between entries
        - id: tail
          enabled: true

        """;

    private static JsonObject View(string name, string package, string url, params string[] models)
    {
        var catalog = new JsonObject();
        foreach (var m in models) { catalog[m] = new JsonObject { ["name"] = m }; }
        return new JsonObject { ["name"] = name, ["package"] = package, ["settings"] = new JsonObject { ["baseURL"] = url }, ["models"] = catalog };
    }

    private static async Task<JsonObject> ViewOf(DshFixture f, string providerId)
    {
        var entries = await f.Backend.GetConfigEntriesAsync();
        return (JsonObject)entries[0]!["info"]!["providers"]![providerId]!.DeepClone();
    }

    /// <summary>The text of one block (its first line and all more-indented lines), for byte comparisons.</summary>
    private static string Block(string patch, string startsWith)
    {
        var lines = patch.Split('\n');
        var from = Array.FindIndex(lines, l => l.StartsWith(startsWith, StringComparison.Ordinal));
        Assert.True(from >= 0, "block not found: " + startsWith);
        var indent = lines[from].Length - lines[from].TrimStart().Length;
        var to = from + 1;
        while (to < lines.Length && lines[to].Length > 0 && lines[to].Length - lines[to].TrimStart().Length > indent) { to++; }
        return string.Join('\n', lines[from..to]);
    }

    [Fact]
    public async Task ListsOnlyOpenAiCompatibleProvidersAndMapsReasoningEffortsToVariants()
    {
        using var f = new DshFixture(Credentials, Patch);

        var entries = await f.Backend.GetConfigEntriesAsync();
        var providers = (JsonObject)entries[0]!["info"]!["providers"]!;

        Assert.Equal(new[] { "volcengine-coding" }, providers.Select(kv => kv.Key).ToArray());
        var view = (JsonObject)providers["volcengine-coding"]!;
        Assert.Equal("火山Codingplan", view["name"]!.GetValue<string>());
        Assert.Equal(OpenAiApiProtocol.ResponsesPackage, view["package"]!.GetValue<string>());
        Assert.Equal("https://ark.cn-beijing.volces.com/api/coding/v3", view["settings"]!["baseURL"]!.GetValue<string>());
        var model = view["models"]!["doubao-seed-code"]!;
        Assert.Equal("Doubao Seed Code", model["name"]!.GetValue<string>());
        var variants = model["variants"]!.AsArray();
        Assert.Equal(new[] { "low", "high" }, variants.Select(v => v!["id"]!.GetValue<string>()).ToArray());
        Assert.Equal("high", variants[1]!["body"]!["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal(256000L, model["x-dsh"]!["contextWindow"]!.GetValue<long>());
        Assert.Equal("ARK_CODING_API_KEY", view["x-dsh"]!["apiKeyEnv"]!.GetValue<string>());
    }

    [Fact]
    public async Task ApiKeyAccountsComeFromTheKeyReferenceOfManagedProvidersOnly()
    {
        using var f = new DshFixture(Credentials, Patch);

        var creds = await f.Backend.GetCredentialsAsync();

        var key = Assert.Single(creds)!.AsObject();
        Assert.Equal("key_volcengine-coding", key["id"]!.GetValue<string>());
        Assert.Equal("volcengine-coding", key["integrationID"]!.GetValue<string>());
        Assert.Equal("key", key["value"]!["type"]!.GetValue<string>());
        Assert.Equal("fake-ark-key", key["value"]!["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProviderWithoutAStoredKeyIsListedWithoutACredential()
    {
        using var f = new DshFixture("version: 1\nrefs:\n  OTHER: x\n", Patch);

        Assert.Empty(await f.Backend.GetCredentialsAsync());
        Assert.NotNull(await ViewOf(f, "volcengine-coding"));
    }

    [Fact]
    public async Task EditingOneProviderKeepsEveryOtherByteAndEveryUnmanagedField()
    {
        using var f = new DshFixture(Credentials, Patch);
        var view = await ViewOf(f, "volcengine-coding");
        view["name"] = "Renamed";
        view["settings"]!["baseURL"] = "https://ark.cn-beijing.volces.com/api/coding/v4";
        ((JsonObject)view["models"]!)["new-model"] = new JsonObject { ["name"] = "new-model" };

        await f.Backend.WriteApiProviderAsync("volcengine-coding", view, null);

        var patch = f.Patch;
        Assert.Equal(Block(Patch, "      openai-codex:"), Block(patch, "      openai-codex:"));
        Assert.Equal(Block(Patch, "      native-anthropic:"), Block(patch, "      native-anthropic:"));
        Assert.StartsWith("# user header comment\n- id: other-plugin\n  config:\n    a: 1   # inline comment\n- id: llm-pi-ai\n  name: Pi\n", patch);
        Assert.EndsWith("# between entries\n- id: tail\n  enabled: true\n", patch);

        var written = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!["volcengine-coding"]!.AsObject();
        Assert.Equal("Renamed", written["displayName"]!.GetValue<string>());
        Assert.Equal("https://ark.cn-beijing.volces.com/api/coding/v4", written["baseURL"]!.GetValue<string>());
        Assert.Equal("ARK_CODING_API_KEY", written["apiKeyEnv"]!.GetValue<string>());
        Assert.Equal("openai-responses", written["api"]!.GetValue<string>());
        var models = written["models"]!.AsArray();
        Assert.Equal(new[] { "doubao-seed-code", "new-model" }, models.Select(m => m!["id"]!.GetValue<string>()).ToArray());
        var existing = models[0]!.AsObject();
        Assert.Equal(256000L, existing["contextWindow"]!.GetValue<long>());
        Assert.Equal(32000L, existing["maxTokens"]!.GetValue<long>());
        Assert.Equal(new[] { "text", "image" }, existing["input"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Equal("high", existing["reasoningEfforts"]!["high"]!.GetValue<string>());
        Assert.Equal("low", existing["reasoningEfforts"]!["low"]!.GetValue<string>());
        // The key and every foreign credential entry stayed as they were.
        Assert.Equal(Credentials, f.Credentials);
    }

    [Fact]
    public async Task ReplacingTheKeyUpdatesOnlyItsReference()
    {
        using var f = new DshFixture(Credentials, Patch);
        var view = await ViewOf(f, "volcengine-coding");

        await f.Backend.WriteApiProviderAsync("volcengine-coding", view, "fake-new-key");

        var doc = f.CredentialsDoc;
        Assert.Equal("fake-new-key", doc.GetRef("ARK_CODING_API_KEY"));
        Assert.Equal("fake-anthropic-key", doc.GetRef("ANTHROPIC_KEY"));
        Assert.Equal("{\"third\":\"party\"}", doc.GetRef("CODEX_OAUTH_1"));
        Assert.NotNull(doc.GetRecord("client-connection/browser-session"));
        Assert.Equal(Credentials.Replace("fake-ark-key", "fake-new-key"), f.Credentials);
    }

    [Fact]
    public async Task NewProviderGetsAGeneratedReferenceAndTheCodexRouteIsUntouched()
    {
        using var f = new DshFixture(Credentials, Patch);
        var id = "api-0123456789abcdef0123456789abcdef";

        await f.Backend.WriteApiProviderAsync(id, View("My API", OpenAiApiProtocol.ChatPackage, "https://api.example.test/v1", "m1", "m2"), "fake-key-1");

        var providers = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!;
        var written = providers[id]!.AsObject();
        Assert.Equal("API_0123456789ABCDEF0123456789ABCDEF_API_KEY", written["apiKeyEnv"]!.GetValue<string>());
        Assert.Equal("openai-completions", written["api"]!.GetValue<string>());
        Assert.Equal("My API", written["displayName"]!.GetValue<string>());
        Assert.Equal(new[] { "m1", "m2" }, written["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToArray());
        Assert.Equal("fake-key-1", f.CredentialsDoc.GetRef("API_0123456789ABCDEF0123456789ABCDEF_API_KEY"));
        Assert.Equal(Block(Patch, "      openai-codex:"), Block(f.Patch, "      openai-codex:"));
        var creds = await f.Backend.GetCredentialsAsync();
        Assert.Contains(creds.OfType<JsonObject>(), c => c["id"]!.GetValue<string>() == "key_" + id && c["value"]!["key"]!.GetValue<string>() == "fake-key-1");
    }

    [Fact]
    public async Task ReferenceNamesAreDeduplicated()
    {
        using var f = new DshFixture("version: 1\nrefs:\n  API_ABC_API_KEY: taken\n", "[]\n");

        await f.Backend.WriteApiProviderAsync("api-abc", View("A", OpenAiApiProtocol.ChatPackage, "https://a.example.test/v1", "m"), "k1");

        var env = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!["api-abc"]!["apiKeyEnv"]!.GetValue<string>();
        Assert.Equal("API_ABC_API_KEY_2", env);
        Assert.Equal("taken", f.CredentialsDoc.GetRef("API_ABC_API_KEY"));
    }

    [Fact]
    public async Task ReplacingAKeySharedWithAnotherProviderGivesThisProviderItsOwnReference()
    {
        const string patch = """
            - id: llm-pi-ai
              config:
                providers:
                  one:
                    apiKeyEnv: SHARED_KEY
                    api: openai-completions
                    baseURL: https://one.example.test/v1
                  two:
                    apiKeyEnv: SHARED_KEY
                    api: openai-completions
                    baseURL: https://two.example.test/v1

            """;
        using var f = new DshFixture("version: 1\nrefs:\n  SHARED_KEY: shared-value\n", patch);
        var view = await ViewOf(f, "one");

        await f.Backend.WriteApiProviderAsync("one", view, "rotated");

        var providers = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!;
        Assert.Equal("ONE_API_KEY", providers["one"]!["apiKeyEnv"]!.GetValue<string>());
        Assert.Equal("SHARED_KEY", providers["two"]!["apiKeyEnv"]!.GetValue<string>());
        Assert.Equal("rotated", f.CredentialsDoc.GetRef("ONE_API_KEY"));
        Assert.Equal("shared-value", f.CredentialsDoc.GetRef("SHARED_KEY"));
    }

    [Fact]
    public async Task RemovingAProviderRemovesItsReferenceButKeepsEverythingElse()
    {
        using var f = new DshFixture(Credentials, Patch);

        await f.Backend.RemoveApiProviderAsync("volcengine-coding");

        var providers = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!;
        Assert.Equal(new[] { "openai-codex", "native-anthropic" }, providers.Select(kv => kv.Key).ToArray());
        Assert.Equal(Block(Patch, "      native-anthropic:"), Block(f.Patch, "      native-anthropic:"));
        Assert.Null(f.CredentialsDoc.GetRef("ARK_CODING_API_KEY"));
        Assert.Equal("fake-anthropic-key", f.CredentialsDoc.GetRef("ANTHROPIC_KEY"));
        Assert.Equal("{\"third\":\"party\"}", f.CredentialsDoc.GetRef("CODEX_OAUTH_1"));
        Assert.Contains("# between entries", f.Patch);
        Assert.Empty(await f.Backend.GetCredentialsAsync());
    }

    [Fact]
    public async Task RemovingAProviderKeepsAReferenceAnotherProviderStillUses()
    {
        const string patch = """
            - id: llm-pi-ai
              config:
                providers:
                  one:
                    apiKeyEnv: SHARED_KEY
                    api: openai-completions
                    baseURL: https://one.example.test/v1
                  two:
                    apiKeyEnv: SHARED_KEY
                    api: openai-completions
                    baseURL: https://two.example.test/v1

            """;
        using var f = new DshFixture("version: 1\nrefs:\n  SHARED_KEY: shared-value\n", patch);

        await f.Backend.RemoveApiProviderAsync("one");

        Assert.Equal("shared-value", f.CredentialsDoc.GetRef("SHARED_KEY"));
        Assert.Equal(new[] { "two" }, ProfilePatchDocument.Load(f.PatchPath).GetProviders()!.Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public async Task TheBuiltInCodexRouteCanNeitherBeWrittenNorRemoved()
    {
        using var f = new DshFixture(Credentials, Patch);

        await Assert.ThrowsAsync<DshException>(() => f.Backend.WriteApiProviderAsync("openai-codex", View("x", OpenAiApiProtocol.ChatPackage, "https://x.example.test/v1", "m"), "k"));
        await Assert.ThrowsAsync<DshException>(() => f.Backend.RemoveApiProviderAsync("openai-codex"));

        Assert.Equal(Patch, f.Patch);
        Assert.Equal(Credentials, f.Credentials);
    }

    [Fact]
    public async Task UnmanagedProvidersCannotBeRemovedThroughTheApiPath()
    {
        using var f = new DshFixture(Credentials, Patch);

        await Assert.ThrowsAsync<DshException>(() => f.Backend.RemoveApiProviderAsync("native-anthropic"));

        Assert.Equal(Patch, f.Patch);
    }

    [Fact]
    public async Task InvalidPatchFileStopsEveryApiOperationWithoutWriting()
    {
        const string broken = "id: not-a-sequence\n";
        using var f = new DshFixture(Credentials, broken);

        await Assert.ThrowsAsync<DshException>(() => f.Backend.GetConfigEntriesAsync());
        await Assert.ThrowsAsync<DshException>(() => f.Backend.WriteApiProviderAsync("p1", View("x", OpenAiApiProtocol.ChatPackage, "https://x.example.test/v1", "m"), "k"));

        Assert.Equal(broken, f.Patch);
        Assert.Equal(Credentials, f.Credentials);
        // The Codex side keeps working even though the provider file is unreadable.
        Assert.Empty(await f.Backend.GetCredentialsAsync());
    }

    [Fact]
    public async Task FailedProviderWriteRollsTheKeyReferenceBack()
    {
        using var f = new DshFixture(Credentials, "[]\n");
        // A directory where the patch file should be makes the provider write fail after the key was stored.
        File.Delete(f.PatchPath);
        Directory.CreateDirectory(f.PatchPath);

        await Assert.ThrowsAnyAsync<Exception>(() => f.Backend.WriteApiProviderAsync("p1", View("x", OpenAiApiProtocol.ChatPackage, "https://x.example.test/v1", "m"), "k"));

        Assert.Equal(Credentials, f.Credentials);
    }

    [Fact]
    public async Task NewResponsesModelsOnArkPlanEndpointsGetReasoningEfforts()
    {
        using var f = new DshFixture();
        var catalog = new JsonObject { ["deepseek-v4-pro"] = ModelVariantBuilder.NewModelEntry("deepseek-v4-pro", true, "https://ark.cn-beijing.volces.com/api/coding/v3") };
        var view = new JsonObject
        {
            ["name"] = "Ark", ["package"] = OpenAiApiProtocol.ResponsesPackage,
            ["settings"] = new JsonObject { ["baseURL"] = "https://ark.cn-beijing.volces.com/api/coding/v3" }, ["models"] = catalog,
        };

        await f.Backend.WriteApiProviderAsync("ark-new", view, "k");

        var model = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!["ark-new"]!["models"]![0]!.AsObject();
        Assert.Equal("deepseek-v4-pro", model["id"]!.GetValue<string>());
        Assert.Equal(new[] { "low", "high" }, model["reasoningEfforts"]!.AsObject().Select(kv => kv.Key).ToArray());
        Assert.Equal("high", model["reasoningEfforts"]!["high"]!.GetValue<string>());
        // And it reads back as the same variants.
        var back = await ViewOf(f, "ark-new");
        Assert.Equal(new[] { "low", "high" }, back["models"]!["deepseek-v4-pro"]!["variants"]!.AsArray().Select(v => v!["id"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task ReasoningEffortsOfAnyShapeSurviveAnEditUnchanged()
    {
        const string patch = """
            - id: llm-pi-ai
              config:
                providers:
                  p:
                    api: openai-responses
                    baseURL: https://p.example.test/v1
                    apiKeyEnv: P_KEY
                    models:
                      - id: with-off
                        reasoningEfforts: {off: , low: minimal, max: maxwire}
                      - id: no-reasoning
                        reasoningEfforts: false
                      - id: untouched
                        contextWindow: 1000

            """;
        using var f = new DshFixture("version: 1\nrefs:\n  P_KEY: k\n", patch);
        var view = await ViewOf(f, "p");

        await f.Backend.WriteApiProviderAsync("p", view, null);

        var models = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!["p"]!["models"]!.AsArray();
        var withOff = models[0]!["reasoningEfforts"]!.AsObject();
        Assert.Equal(new[] { "off", "low", "max" }, withOff.Select(kv => kv.Key).ToArray());
        Assert.Null(withOff["off"]);
        Assert.Equal("minimal", withOff["low"]!.GetValue<string>());
        Assert.Equal("maxwire", withOff["max"]!.GetValue<string>());
        Assert.False(models[1]!["reasoningEfforts"]!.GetValue<bool>());
        Assert.Equal(1000L, models[2]!["contextWindow"]!.GetValue<long>());
    }

    [Fact]
    public async Task ImportedCockpitProvidersCarryTheirSourceIdFromTheLocalMirror()
    {
        using var f = new DshFixture();
        var api = TestAccounts.CockpitApi("source-1", "Source", "https://api.example.test/v1", "chat_completions", "fake-key", "m1");
        var preview = await f.Import.PreviewAsync(WriteJson(f, "in.json", new JsonArray(api)));
        await f.Import.ImportAsync(preview);

        var creds = await f.Backend.GetCredentialsAsync();

        var cred = Assert.Single(creds)!.AsObject();
        Assert.Equal("source-1", cred["value"]!["metadata"]!["cockpitID"]!.GetValue<string>());
        Assert.Equal(DshBackend.CockpitProviderId("source-1"), cred["integrationID"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProviderVerificationIsDelegatedToTheRuntimeVerifier()
    {
        using var f = new DshFixture();

        var result = await f.Backend.VerifyProviderAsync("p1", OpenAiApiProtocol.ChatPackage);

        Assert.Equal(ProviderRuntimeState.Applied, result.State);
        Assert.Equal(("p1", "openai-completions"), Assert.Single(f.Verifier.Calls));
    }

    internal static string WriteJson(DshFixture f, string name, JsonNode content)
    {
        var path = Path.Combine(f.Root, name);
        File.WriteAllText(path, content.ToJsonString());
        return path;
    }
}
