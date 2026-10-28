using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

/// <summary>Managed Codex models get the account's <c>max_context_window</c> (never more, never 1,000,000) instead of <c>context_window</c>.</summary>
public class CodexContextWindowTests
{
    private const string Package = "{\n  \"name\": \"dsh-profile-tauri\",\n  \"dsh\": {\n    \"profile\": {\n      \"bundles\": [\n        \"@deepseek-ai/dsh-base\"\n      ]\n    }\n  }\n}\n";

    private static string Account(string sol = "\"max_context_window\":872000,", string terra = "\"max_context_window\":872000,", string astra = "\"max_context_window\":872000,", string luna = "\"max_context_window\":872000,")
        => "{\"models\":[" +
           "{\"slug\":\"gpt-6.1-sol\",\"display_name\":\"GPT-6.1 Sol\",\"visibility\":\"list\",\"priority\":0,\"context_window\":500000," + sol + "\"input_modalities\":[\"text\",\"image\"],\"service_tiers\":[{\"id\":\"priority\"}]}," +
           "{\"slug\":\"gpt-6.1-terra\",\"display_name\":\"GPT-6.1 Terra\",\"visibility\":\"list\",\"priority\":1,\"context_window\":500000," + terra + "\"input_modalities\":[\"text\",\"image\"],\"service_tiers\":[{\"id\":\"priority\"}]}," +
           "{\"slug\":\"gpt-6-astra\",\"display_name\":\"GPT-6-Astra\",\"visibility\":\"list\",\"priority\":2,\"context_window\":272000," + astra + "\"input_modalities\":[\"text\",\"image\"],\"service_tiers\":[{\"id\":\"priority\"}]}," +
           "{\"slug\":\"gpt-6-luna\",\"display_name\":\"GPT-6-Luna\",\"visibility\":\"list\",\"priority\":3,\"context_window\":272000," + luna + "\"input_modalities\":[\"text\",\"image\"],\"service_tiers\":[{\"id\":\"priority\"}]}" +
           "]}";

    private sealed class Fake(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(responder());
        }
    }

    private static (CodexModelCatalogClient, Fake) Client(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new Fake(() => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        return (new CodexModelCatalogClient("t", new HttpClient(handler)), handler);
    }

    private static JsonArray Creds()
        => new(new JsonObject
        {
            ["id"] = "c1", ["integrationID"] = "openai", ["active"] = true,
            ["value"] = new JsonObject { ["access"] = "tok", ["expires"] = DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeMilliseconds(), ["metadata"] = new JsonObject { ["accountID"] = "acct-1" } },
        });

    private static readonly IReadOnlyDictionary<string, JsonObject> Bundled = new Dictionary<string, JsonObject>
    {
        ["gpt-6.1-sol"] = new() { ["id"] = "gpt-6.1-sol" }, ["gpt-6-astra"] = new() { ["id"] = "gpt-6-astra" }, ["gpt-6-luna"] = new() { ["id"] = "gpt-6-luna" },
    };

    private static DshFixture Fixture(string patch, CodexModelCatalogClient? client, CodexModelOwnership? ownership = null)
        => new(null, patch, fast: true, packageJson: Package, catalogModels: Bundled, codexClient: client, ownership: ownership ?? new CodexModelOwnership(null));

    private static string Route(string models) => "# mine\n- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex:\n        models:\n" + models + "- id: last-plugin\n";

    // ---- parsing ----

    [Fact]
    public void MaxContextWindowIsParsedDefensively()
    {
        static CodexAccountModel One(string fields) => CodexModelCatalogClient.Parse("{\"models\":[{\"slug\":\"m\",\"visibility\":\"list\"," + fields + "}]}")!.Single();

        Assert.Equal(872000, One("\"context_window\":500000,\"max_context_window\":872000").TargetContextWindow);
        Assert.Equal(500000, One("\"context_window\":500000").TargetContextWindow);                                     // absent: the plain window
        Assert.Equal(500000, One("\"context_window\":500000,\"max_context_window\":400000").TargetContextWindow);     // below the plain window: nonsense
        Assert.Equal(500000, One("\"context_window\":500000,\"max_context_window\":2000001").TargetContextWindow);     // absurd
        Assert.Equal(500000, One("\"context_window\":500000,\"max_context_window\":-5").TargetContextWindow);
        Assert.Equal(500000, One("\"context_window\":500000,\"max_context_window\":\"lots\"").TargetContextWindow);
        Assert.Equal(2_000_000, One("\"context_window\":500000,\"max_context_window\":2000000").TargetContextWindow); // the bound itself is allowed
        Assert.Equal(872000, One("\"max_context_window\":872000").TargetContextWindow);                                // no plain window to compare with
        Assert.Null(One("\"x\":1").TargetContextWindow);
    }

    // ---- writing ----

    [Fact]
    public void IdOnlyEntriesOfOursGainAContextWindowLineAndEverythingElseStaysByteIdentical()
    {
        var before = Route("          - id: gpt-6.1-sol\n          # a comment between entries\n          - id: gpt-6-astra\n          - id: gpt-6-luna\n");
        var (client, handler) = Client(Account());
        using var f = Fixture(before, client);

        var result = f.Fast.Sync(Creds());

        Assert.Equal(1, handler.Calls);
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna" }, result.ContextWindowsSet.Keys.OrderBy(k => Array.IndexOf(new[] { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna" }, k)));
        Assert.All(result.ContextWindowsSet.Values, v => Assert.Equal(872000, v));
        var expectedStart = "# mine\n- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex:\n        models:\n"
            + "          - id: gpt-6.1-sol\n            contextWindow: 872000\n          # a comment between entries\n"
            + "          - id: gpt-6-astra\n            contextWindow: 872000\n          - id: gpt-6-luna\n            contextWindow: 872000\n";
        Assert.StartsWith(expectedStart, f.Patch);
        Assert.EndsWith("- id: last-plugin\n", f.Patch);
        var models = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels();
        // They stay id-only apart from the window, so dsh still fills the rest (and the compat flags) from its catalog.
        Assert.Equal(new[] { "contextWindow", "id" }, models.Single(m => m.Id == "gpt-6-astra").Entry!.Select(kv => kv.Key).OrderBy(k => k).ToArray());
        Assert.Contains("已把 Codex 模型上下文设为账号允许的最大值（872K）：gpt-6.1-sol、gpt-6-astra、gpt-6-luna；上下文越长，每条消息消耗的订阅额度越多。", CodexFastService.Describe(result));
    }

    [Fact]
    public void ExplicitEntriesOfOursAndOurTwinsAreUpdatedInPlaceAndNothingElseMoves()
    {
        var before = Route(
            "          - id: gpt-6.1-sol\n            contextWindow: 500000\n"
            + "          - id: gpt-6.1-terra\n            name: \"GPT-6.1 Terra\"\n            contextWindow: 500000   # old\n            maxTokens: 128000\n"
            + "          - id: gpt-6.1-sol-fast\n            name: \"GPT-6.1 Sol (Fast)\"\n            contextWindow: 500000\n            maxTokens: 128000\n"
            + "          - id: gpt-6.1-terra-fast\n            name: \"GPT-6.1 Terra (Fast)\"\n            contextWindow: 500000\n");
        var ownership = new CodexModelOwnership(null);
        var (client, _) = Client(Account());
        using var f = Fixture(before, client, ownership);
        ownership.Save(f.Home, new[] { "gpt-6.1-sol", "gpt-6.1-terra" }); // what 0.1.5 recorded (twins are recognised by their shape)

        var result = f.Fast.Sync(Creds());

        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6.1-sol-fast", "gpt-6.1-terra-fast" }, result.ContextWindowsSet.Keys.OrderBy(k => Array.IndexOf(new[] { "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6.1-sol-fast", "gpt-6.1-terra-fast" }, k)));
        // Compare with the old text: only the numbers 500000 changed, the trailing comment survives, plus what the sync appended after the list.
        var expected = before.Replace("contextWindow: 500000", "contextWindow: 872000");
        var cut = f.Patch.IndexOf("- id: last-plugin", StringComparison.Ordinal);
        var listBefore = expected[..expected.IndexOf("- id: last-plugin", StringComparison.Ordinal)];
        Assert.StartsWith(listBefore.TrimEnd('\n'), f.Patch[..cut]);
        Assert.Contains("contextWindow: 872000   # old", f.Patch);
        Assert.DoesNotContain("500000", f.Patch);
    }

    [Fact]
    public void EntriesTheUserWroteAreNeverTouchedAndATwinOfAUserBaseCopiesTheUsersWindow()
    {
        var before = Route(
            "          - id: gpt-6.1-sol\n"
            + "          - id: gpt-6-astra\n            name: Mine\n            contextWindow: 111111\n            maxTokens: 5000\n"
            + "          - id: my-own\n            contextWindow: 123\n"
            + "          - id: gpt-6.1-sol-fast\n            name: Mine fast\n            contextWindow: 222222\n");
        var (client, _) = Client(Account());
        using var f = Fixture(before, client);

        var result = f.Fast.Sync(Creds());

        var after = f.Patch;
        Assert.Contains("          - id: gpt-6-astra\n            name: Mine\n            contextWindow: 111111\n            maxTokens: 5000\n", after);
        Assert.Contains("          - id: my-own\n            contextWindow: 123\n", after);
        Assert.Contains("          - id: gpt-6.1-sol-fast\n            name: Mine fast\n            contextWindow: 222222\n", after);
        Assert.DoesNotContain(result.ContextWindowsSet, kv => kv.Key is "gpt-6-astra" or "my-own" or "gpt-6.1-sol-fast");
        // The id-only gpt-6.1-sol was claimed as ours (0.1.4 wrote it that way): it gets the window.
        Assert.Contains("          - id: gpt-6.1-sol\n            contextWindow: 872000\n", after);
        // Twins this run creates for a user's base entry take the user's value.
        var astraFast = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels().Single(m => m.Id == "gpt-6-astra-fast").Entry!;
        Assert.Equal(111111, astraFast["contextWindow"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("\"max_context_window\":400000,", 500000)]   // below context_window: ignored
    [InlineData("\"max_context_window\":9999999,", 500000)]   // absurd: ignored
    [InlineData("", 500000)]                                  // absent
    public void AnAbsentOrInvalidMaximumFallsBackToTheAccountsPlainContextWindow(string max, int expected)
    {
        var before = Route("          - id: gpt-6.1-sol\n");
        var (client, _) = Client(Account(sol: max));
        using var f = Fixture(before, client);

        var result = f.Fast.Sync(Creds());

        Assert.Equal(expected, result.ContextWindowsSet["gpt-6.1-sol"]);
        Assert.Contains("- id: gpt-6.1-sol\n            contextWindow: " + expected + "\n", f.Patch);
    }

    [Fact]
    public void WhenOpenAICannotBeReachedExistingValuesStayAsTheyAre()
    {
        var before = Route("          - id: gpt-6.1-sol\n            contextWindow: 500000\n          - id: gpt-6-astra\n");
        var (client, _) = Client("{}", HttpStatusCode.InternalServerError);
        using var f = Fixture(before, client);
        var patchBefore = f.Patch;

        var result = f.Fast.Sync(Creds());

        Assert.Empty(result.ContextWindowsSet);
        Assert.Contains("500", result.FetchNote);
        // Only the Fast twins (built-in rule) were appended; the existing entries keep their text exactly.
        Assert.StartsWith(patchBefore[..patchBefore.IndexOf("- id: last-plugin", StringComparison.Ordinal)].TrimEnd('\n'), f.Patch);
        Assert.Contains("          - id: gpt-6.1-sol\n            contextWindow: 500000\n          - id: gpt-6-astra\n", f.Patch);
        Assert.DoesNotContain(ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels().Where(m => m.Id is "gpt-6.1-sol" or "gpt-6-astra"), m => m.Entry?["contextWindow"]?.GetValue<int>() == 872000);
    }

    [Fact]
    public void ASecondSyncChangesNothingAndMakesNoBackup()
    {
        var before = Route("          - id: gpt-6.1-sol\n          - id: gpt-6-astra\n");
        var (client, _) = Client(Account());
        using var f = Fixture(before, client);
        f.Fast.Sync(Creds());
        var patch = f.Patch;
        var backups = f.Backups.ListBackups().Count;

        var again = f.Fast.Sync(Creds());

        Assert.False(again.Changed);
        Assert.Empty(again.ContextWindowsSet);
        Assert.Equal(patch, f.Patch);
        Assert.Equal(backups, f.Backups.ListBackups().Count);
        Assert.Empty(CodexFastService.Describe(again));
    }

    [Fact]
    public void AValueSomeoneElseChangedOnOneOfOursIsSetBackToTheAccountsMaximumAndOnlyOnce()
    {
        var before = Route("          - id: gpt-6.1-sol\n            contextWindow: 300000\n");
        var (client, _) = Client(Account());
        using var f = Fixture(before, client);

        f.Fast.Sync(Creds());
        Assert.Contains("- id: gpt-6.1-sol\n            contextWindow: 872000\n", f.Patch);
        Assert.DoesNotContain("300000", f.Patch);
        Assert.False(f.Fast.Sync(Creds()).Changed);
    }

    // ---- the document operation ----

    [Fact]
    public void SetNumberReplacesAddsAndRefusesWithoutDisturbingAnythingElse()
    {
        var text = "- id: llm-pi-ai\r\n  config:\r\n    providers:\r\n      openai-codex:\r\n        models:\r\n          - id: a\r\n            contextWindow: 1\r\n          - id: b\r\n            name: B\r\n          - plain-string\r\n          - {id: flow}\r\n";
        var doc = ProfilePatchDocument.Parse(text, "x.yml");

        Assert.False(doc.SetCodexRouteModelNumber("a", "contextWindow", 1));    // already equal
        Assert.True(doc.SetCodexRouteModelNumber("a", "contextWindow", 872000));
        Assert.True(doc.SetCodexRouteModelNumber("b", "contextWindow", 872000)); // added after the last line of b, CRLF kept
        Assert.False(doc.SetCodexRouteModelNumber("missing", "contextWindow", 5));
        Assert.False(doc.SetCodexRouteModelNumber("plain-string", "contextWindow", 5));
        Assert.False(doc.SetCodexRouteModelNumber("flow", "contextWindow", 5));

        Assert.Equal(text.Replace("contextWindow: 1\r\n", "contextWindow: 872000\r\n").Replace("            name: B\r\n", "            name: B\r\n            contextWindow: 872000\r\n"), doc.Text);
    }

    // ---- ownership migration and the import ----

    [Fact]
    public async Task AnImportMigratesWhatVersion015WroteToTheAccountsMaximumAndSaysSo()
    {
        var patch = "- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex:\n        models:\n"
            + "          - id: gpt-6.1-sol\n          - id: gpt-6-astra\n          - id: gpt-6-luna\n"
            + "          - id: gpt-6.1-terra\n            name: \"GPT-6.1 Terra\"\n            contextWindow: 500000\n            maxTokens: 128000\n            input:\n              - text\n              - image\n"
            + "          - id: gpt-6.1-sol-fast\n            name: \"GPT-6.1 Sol (Fast)\"\n            contextWindow: 500000\n";
        var ownership = new CodexModelOwnership(null);
        var (client, _) = Client(Account());
        using var f = new DshFixture(null, patch, subagents: true, fast: true, packageJson: Package, catalogModels: Bundled, codexClient: client, ownership: ownership);
        ownership.Save(f.Home, new[] { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna", "gpt-6.1-terra", "gpt-6.1-sol-fast" }); // the 0.1.5 record

        var result = await f.Import.ImportAsync(await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in.json", new JsonArray(TestAccounts.CockpitOauth("c-1", "a@example.test", "acct-1", "refresh-1")))));

        Assert.Equal(0, result.Failed);
        var models = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels();
        Assert.All(models, m => Assert.Equal(872000, m.Entry!["contextWindow"]!.GetValue<int>()));
        Assert.Contains(result.Messages, m => m.StartsWith("已把 Codex 模型上下文设为账号允许的最大值（872K）："));
        Assert.DoesNotContain(models, m => m.Entry!["contextWindow"]!.GetValue<int>() == 1_000_000);
    }
}
