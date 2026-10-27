using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

/// <summary>The Codex models follow what OpenAI offers the account (a synthetic list modelled on the fields the real endpoint returns).</summary>
public class CodexModelSyncTests
{
    private const string Package = "{\n  \"name\": \"dsh-profile-tauri\",\n  \"dsh\": {\n    \"profile\": {\n      \"bundles\": [\n        \"@deepseek-ai/dsh-base\"\n      ]\n    }\n  }\n}\n";

    /// <summary>What an account sees: four current models, the excluded old ones, hidden helpers and odd ones.</summary>
    private static string AccountJson() => """
        {"models":[
          {"slug":"gpt-6.1-sol","display_name":"GPT-6.1 Sol","visibility":"list","priority":0,"context_window":500000,"input_modalities":["text","image"],
           "supported_reasoning_levels":[{"effort":"low","description":"x"},{"effort":"medium","description":"x"},{"effort":"high","description":"x"},{"effort":"xhigh","description":"x"},{"effort":"max","description":"x"},{"effort":"ultra","description":"x"}],
           "service_tiers":[{"id":"priority","name":"Fast"}],"additional_speed_tiers":["fast"],"minimal_client_version":"0.150.0"},
          {"slug":"gpt-6.1-terra","display_name":"GPT-6.1 Terra","visibility":"list","priority":1,"context_window":500000,"input_modalities":["text","image"],
           "supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"high"},{"effort":"ultra"}],
           "service_tiers":[{"id":"priority"}],"default_service_tier":"priority"},
          {"slug":"gpt-6-astra","display_name":"GPT-6-Astra","visibility":"list","priority":2,"context_window":272000,"input_modalities":["text","image"],
           "supported_reasoning_levels":[{"effort":"low"},{"effort":"high"}],"service_tiers":[{"id":"priority"}]},
          {"slug":"gpt-6-luna","display_name":"GPT-6-Luna","visibility":"list","priority":3,"context_window":272000,"input_modalities":["text","image"],
           "supported_reasoning_levels":[{"effort":"low"}],"additional_speed_tiers":["fast"]},
          {"slug":"gpt-6-sol","display_name":"GPT-6-Sol","visibility":"list","priority":4,"context_window":272000,"input_modalities":["text","image"],"service_tiers":[{"id":"priority"}]},
          {"slug":"gpt-5.6-sol","display_name":"GPT-5.6 Sol","visibility":"list","priority":5,"context_window":272000,"input_modalities":["text"],"service_tiers":[{"id":"priority"}]},
          {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list","priority":6,"input_modalities":["text"]},
          {"slug":"gpt-6-chat-thinking","display_name":"Chat","visibility":"hide","priority":7,"input_modalities":["text"]},
          {"slug":"gpt-reserve","display_name":"Reserve","visibility":"hide","priority":8,"input_modalities":["text"]},
          {"slug":"codex-auto-review","display_name":"Review","visibility":"hide","priority":9,"input_modalities":["text"]},
          {"slug":"gpt-6.2-mini","display_name":"GPT-6.2 Mini","visibility":"list","priority":10,"context_window":128000,"input_modalities":["text"],
           "supported_reasoning_levels":[{"effort":"medium"}]},
          {"slug":"gpt-image-only","display_name":"Img","visibility":"list","priority":11,"input_modalities":["image"]},
          {"display_name":"no slug","visibility":"list"}
        ]}
        """;

    private sealed class Fake : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AccountJson(), Encoding.UTF8, "application/json") };
        public List<(Uri Uri, Dictionary<string, string> Headers)> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!, request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value))));
            return Task.FromResult(Responder(request));
        }
    }

    private static (CodexModelCatalogClient Client, Fake Handler) Client(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new Fake();
        if (responder is not null) { handler.Responder = responder; }
        return (new CodexModelCatalogClient("dsh-account-manager/9.9.9", new HttpClient(handler)), handler);
    }

    private static JsonObject Cred(string id, bool active, string access, DateTimeOffset expires)
        => new()
        {
            ["id"] = id,
            ["integrationID"] = "openai",
            ["active"] = active,
            ["value"] = new JsonObject { ["access"] = access, ["expires"] = expires.ToUnixTimeMilliseconds(), ["metadata"] = new JsonObject { ["accountID"] = "acct-" + id } },
        };

    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static JsonArray ValidCreds() => new(Cred("c1", true, "tok-current", Now.AddHours(5)));

    private static readonly IReadOnlyDictionary<string, JsonObject> Bundled = new Dictionary<string, JsonObject>
    {
        ["gpt-6.1-sol"] = new() { ["id"] = "gpt-6.1-sol", ["name"] = "GPT-6.1 Sol", ["contextWindow"] = 500000, ["maxTokens"] = 128000, ["input"] = new JsonArray("text", "image"), ["reasoning"] = true, ["thinkingLevelMap"] = new JsonObject { ["off"] = null, ["minimal"] = "low", ["max"] = "max", ["xhigh"] = "xhigh" } },
        ["gpt-6-astra"] = new() { ["id"] = "gpt-6-astra", ["name"] = "GPT-6 Astra", ["contextWindow"] = 272000, ["maxTokens"] = 128000, ["input"] = new JsonArray("text", "image"), ["reasoning"] = true, ["thinkingLevelMap"] = new JsonObject { ["off"] = null } },
        ["gpt-6-luna"] = new() { ["id"] = "gpt-6-luna", ["name"] = "GPT-6 Luna", ["contextWindow"] = 272000, ["maxTokens"] = 128000, ["input"] = new JsonArray("text", "image"), ["reasoning"] = true, ["thinkingLevelMap"] = new JsonObject { ["off"] = "none" } },
    };

    private const string OldList = "- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex:\n        models:\n          - id: gpt-6.1-sol\n          - id: gpt-6-astra\n          - id: gpt-6-luna\n";

    private static DshFixture Fixture(string patch, CodexModelCatalogClient? client, CodexModelOwnership? ownership = null, bool package = true, bool subagents = false)
        => new(null, patch, fast: true, subagents: subagents, packageJson: package ? Package : null, catalogModels: Bundled, codexClient: client, ownership: ownership ?? new CodexModelOwnership(null));

    // ---- parsing and selection ----

    [Fact]
    public void TheAccountsListIsParsedDefensivelyAndSelectedByTheUsersRule()
    {
        var models = CodexModelCatalogClient.Parse(AccountJson())!;
        Assert.Equal(12, models.Count); // the entry without a slug is skipped
        var terra = models.Single(m => m.Slug == "gpt-6.1-terra");
        Assert.True(terra.SupportsFast);
        Assert.Contains("ultra", terra.ReasoningEfforts);
        Assert.False(models.Single(m => m.Slug == "gpt-6.2-mini").SupportsFast);
        Assert.True(models.Single(m => m.Slug == "gpt-6-luna").SupportsFast); // additional_speed_tiers: fast

        var selected = CodexModelPolicy.Select(models).Select(m => m.Slug).ToArray();
        // visibility list, takes text, not gpt-6-sol, not gpt-5*; OpenAI's own order.
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-astra", "gpt-6-luna", "gpt-6.2-mini" }, selected);
        Assert.True(CodexModelPolicy.IsExcluded("gpt-6-sol"));
        Assert.True(CodexModelPolicy.IsExcluded("gpt-5.6-terra"));
        Assert.True(CodexModelPolicy.IsExcluded("gpt-5"));
        Assert.False(CodexModelPolicy.IsExcluded("gpt-6-solar"));
        Assert.False(CodexModelPolicy.IsExcluded("gpt-6.1-sol"));
    }

    [Fact]
    public void SelectionIsOrderedByPriorityAndNamesAreNormalised()
    {
        var shuffled = CodexModelCatalogClient.Parse("{\"models\":[{\"slug\":\"b\",\"visibility\":\"list\",\"priority\":2},{\"slug\":\"a\",\"visibility\":\"LIST\",\"priority\":0},{\"slug\":\"c\",\"visibility\":\"list\"},{\"slug\":\"d\",\"visibility\":\"list\",\"priority\":2}]}")!;

        Assert.Equal(new[] { "a", "b", "d", "c" }, CodexModelPolicy.Select(shuffled).Select(m => m.Slug).ToArray()); // missing priority sorts last
        Assert.Equal("GPT-6 Astra", CodexModelPolicy.NormalizeName("GPT-6-Astra", "x"));
        Assert.Equal("GPT-6.1 Terra", CodexModelPolicy.NormalizeName("GPT-6.1 Terra", "x"));
        Assert.Equal("GPT-6.1 Sol", CodexModelPolicy.NormalizeName("GPT-6.1-Sol", "x"));
        Assert.Equal("slug", CodexModelPolicy.NormalizeName("  ", "slug"));
        Assert.Null(CodexModelCatalogClient.Parse("not json"));
        Assert.Null(CodexModelCatalogClient.Parse("{\"data\":[]}"));
    }

    // ---- the request ----

    [Fact]
    public async Task TheRequestIsAReadOnlyGetWithTheCodexHeadersAndNeverEchoesTheToken()
    {
        var (client, handler) = Client();

        var ok = await client.FetchAsync("secret-access-token", "acct-1");

        Assert.True(ok.Ok);
        var call = Assert.Single(handler.Calls);
        Assert.Equal("https://chatgpt.com/backend-api/codex/models?client_version=0.159.0", call.Uri.ToString());
        Assert.Equal("Bearer secret-access-token", call.Headers["authorization"]);
        Assert.Equal("acct-1", call.Headers["chatgpt-account-id"]);
        Assert.Equal("application/json", call.Headers["accept"]);
        Assert.Equal("pi", call.Headers["originator"]);
        Assert.Equal("dsh-account-manager/9.9.9", call.Headers["user-agent"]);

        foreach (var (status, expected) in new[] { (401, "令牌被拒绝"), (403, "访问被拒绝"), (429, "429"), (500, "500"), (302, "重定向"), (404, "404") })
        {
            var (failing, _) = Client(_ => new HttpResponseMessage((HttpStatusCode)status));
            var result = await failing.FetchAsync("secret-access-token", "acct-1");
            Assert.False(result.Ok);
            Assert.Contains(expected, result.Reason);
            Assert.DoesNotContain("secret-access-token", result.Reason);
        }

        var (garbage, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") });
        Assert.Contains("无法识别", (await garbage.FetchAsync("t", "a")).Reason);
        var (broken, _) = Client(_ => throw new HttpRequestException("secret-access-token leaked?"));
        var failure = await broken.FetchAsync("secret-access-token", "a");
        Assert.False(failure.Ok);
        Assert.DoesNotContain("secret-access-token", failure.Reason);
    }

    // ---- which token ----

    [Fact]
    public void TheCurrentAccountsTokenIsUsedWhenItHasTenMinutesLeftElseAnotherUnexpiredOne_AndNothingIsRefreshed()
    {
        var current = Cred("cur", true, "tok-current", Now.AddMinutes(30));
        var other = Cred("oth", false, "tok-other", Now.AddHours(2));
        Assert.Equal("tok-current", CodexModelCatalogClient.PickToken(new[] { other, current }, Now)!.Access);
        Assert.True(CodexModelCatalogClient.PickToken(new[] { other, current }, Now)!.IsCurrentAccount);

        var expiring = Cred("cur", true, "tok-current", Now.AddMinutes(5));
        var pick = CodexModelCatalogClient.PickToken(new[] { expiring, other }, Now)!;
        Assert.Equal("tok-other", pick.Access);
        Assert.Equal("acct-oth", pick.AccountId);

        var expiredOther = Cred("oth", false, "tok-other", Now.AddMinutes(-1));
        Assert.Null(CodexModelCatalogClient.PickToken(new[] { Cred("cur", true, "t", Now.AddMinutes(-5)), expiredOther }, Now));
        Assert.Null(CodexModelCatalogClient.PickToken(Array.Empty<JsonNode?>(), Now));
        Assert.Null(CodexModelCatalogClient.PickToken(null, Now));
        // API providers and accounts without an id are not candidates.
        var api = new JsonObject { ["integrationID"] = "cockpit-x", ["active"] = false, ["value"] = new JsonObject { ["access"] = "k" } };
        Assert.Null(CodexModelCatalogClient.PickToken(new JsonNode?[] { api }, Now));
    }

    [Fact]
    public void WithNoUsableTokenOpenAIIsNotAskedAndTheRouteStaysAsItIs()
    {
        var (client, handler) = Client();
        using var f = Fixture(OldList, client);
        var expired = new JsonArray(Cred("c1", true, "tok", DateTimeOffset.UtcNow.AddHours(-1)));

        var result = f.Fast.Sync(expired);

        Assert.Empty(handler.Calls); // no refresh, no request
        Assert.Contains("没有可用的访问令牌", result.FetchNote);
        Assert.Contains("未能获取账号模型清单（没有可用的访问令牌（本工具不会刷新令牌）），保持现有模型不变。", CodexFastService.Describe(result));
        Assert.DoesNotContain("gpt-6.1-terra", f.Patch);
        Assert.Null(result.FetchedModels);
    }

    // ---- writing the route ----

    [Fact]
    public void ANewModelUnknownToDshGetsExplicitFactsKnownOnesStayIdOnlyAndFastTwinsFollowTheFastCapable()
    {
        var (client, handler) = Client();
        var creds = new JsonArray(Cred("c1", true, "tok-current", DateTimeOffset.UtcNow.AddHours(5)));
        using var f = Fixture("# mine\n" + OldList, client);

        var result = f.Fast.Sync(creds);

        Assert.Single(handler.Calls);
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-astra", "gpt-6-luna", "gpt-6.2-mini" }, result.FetchedModels);
        // terra and mini are not in dsh's catalog; the three old ones stay as they were. Twins only for the Fast-capable (not mini).
        Assert.Equal(new[] { "gpt-6.1-terra", "gpt-6.2-mini", "gpt-6.1-sol-fast", "gpt-6.1-terra-fast", "gpt-6-astra-fast", "gpt-6-luna-fast" }, result.AddedModels);
        Assert.StartsWith("# mine\n" + OldList, f.Patch);
        var models = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels();
        var terra = models.Single(m => m.Id == "gpt-6.1-terra").Entry!;
        Assert.Equal("GPT-6.1 Terra", terra["name"]!.GetValue<string>());
        Assert.Equal(500000, terra["contextWindow"]!.GetValue<int>());
        Assert.Equal(128000, terra["maxTokens"]!.GetValue<int>()); // OpenAI has no output limit: the constant
        Assert.Equal(new[] { "text", "image" }, terra["input"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal("{\"low\":\"low\",\"medium\":\"medium\",\"high\":\"high\"}", terra["reasoningEfforts"]!.ToJsonString()); // ultra is not a dsh level
        var mini = models.Single(m => m.Id == "gpt-6.2-mini").Entry!;
        Assert.Equal(128000, mini["contextWindow"]!.GetValue<int>());
        Assert.Equal("{\"medium\":\"medium\"}", mini["reasoningEfforts"]!.ToJsonString());
        Assert.DoesNotContain("gpt-6-sol", f.Patch);
        Assert.DoesNotContain("gpt-5", f.Patch);
        Assert.DoesNotContain("gpt-6.2-mini-fast", f.Patch);
        // The twin of a model dsh knows takes its facts from dsh's catalog; terra's twin from OpenAI's entry.
        var terraFast = models.Single(m => m.Id == "gpt-6.1-terra-fast").Entry!;
        Assert.Equal("GPT-6.1 Terra (Fast)", terraFast["name"]!.GetValue<string>());
        Assert.Equal(500000, terraFast["contextWindow"]!.GetValue<int>());
        Assert.Equal("GPT-6 Astra (Fast)", models.Single(m => m.Id == "gpt-6-astra-fast").Entry!["name"]!.GetValue<string>());
        Assert.Contains("已从 OpenAI 获取账号可用模型：gpt-6.1-sol、gpt-6.1-terra、gpt-6-astra、gpt-6-luna、gpt-6.2-mini；新增 gpt-6.1-terra", string.Join("\n", CodexFastService.Describe(result)));
        Assert.Contains("首次安装需要重启 dsh", string.Join("\n", CodexFastService.Describe(result)));
    }

    [Fact]
    public void WhenDshsCatalogCannotBeReadTheKnownIdsAreStillWrittenIdOnly()
    {
        var (client, _) = Client();
        var creds = new JsonArray(Cred("c1", true, "tok", DateTimeOffset.UtcNow.AddHours(5)));
        using var f = new DshFixture(null, "- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex:\n        models:\n          - id: gpt-6-astra\n", fast: true, packageJson: Package, codexClient: client);

        f.Fast.Sync(creds);

        var models = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels();
        Assert.Null(models.Single(m => m.Id == "gpt-6.1-sol").Entry is { Count: > 1 } ? "explicit" : null); // id-only
        Assert.NotNull(models.Single(m => m.Id == "gpt-6.1-terra").Entry!["contextWindow"]);
    }

    [Fact]
    public void AModelOpenAIStopsListingIsRemovedWithItsTwinWhenItIsOursButAReferencedOneStaysAndUserEntriesAreNeverTouched()
    {
        var calls = 0;
        var (client, _) = Client(_ =>
        {
            calls++;
            // Second time the account lost gpt-6-astra and gpt-6-luna (and still offers the rest).
            var root = JsonNode.Parse(AccountJson())!.AsObject();
            if (calls > 1)
            {
                var models = root["models"]!.AsArray();
                foreach (var gone in models.Where(m => m is JsonObject o && o["slug"]?.GetValue<string>() is "gpt-6-astra" or "gpt-6-luna").ToList()) { models.Remove(gone); }
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json") };
        });
        var patch = OldList + "          - id: my-own-model\n            name: Mine\n          - id: gpt-5.5\n            name: user wrote this old one\n"
            + "- id: agent-default-model\n  config:\n    provider: openai-codex\n    model: gpt-6-luna\n"
            + "- id: subagent-model-selection-settings\n  config:\n    enabled: true\n    allowedModels:\n      - provider: openai-codex\n        model: gpt-6-astra\n      - provider: openai-codex\n        model: gpt-6.1-sol\n";
        using var f = Fixture(patch, client);
        var creds = new JsonArray(Cred("c1", true, "tok", DateTimeOffset.UtcNow.AddHours(5)));

        f.Fast.Sync(creds);
        var afterFirst = f.Patch;
        Assert.Contains("gpt-6-luna-fast", afterFirst);
        var second = f.Fast.Sync(creds);

        var ids = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels().Select(m => m.Id).ToArray();
        Assert.DoesNotContain("gpt-6-astra", ids);            // ours (0.1.4 wrote it id-only) and no longer offered
        Assert.DoesNotContain("gpt-6-astra-fast", ids);       // its twin goes with it
        Assert.Contains("gpt-6-luna", ids);                   // referenced by agent-default-model: kept
        Assert.DoesNotContain("gpt-6-luna-fast", ids);        // the twin is not referenced, and luna lost Fast support in the list
        Assert.Contains("my-own-model", ids);                 // the user's
        Assert.Contains("gpt-5.5", ids);                      // the user's, even though the rule would exclude it
        Assert.Equal(new[] { "gpt-6-luna" }, second.KeptBecauseReferenced);
        Assert.Contains("没有移除 Codex 模型 gpt-6-luna", string.Join("\n", CodexFastService.Describe(second)));
        Assert.Contains("model: gpt-6-luna\n", f.Patch);      // the default model is unchanged
        // The sub-agent list we fill does not keep pointing at the removed model.
        var routes = ProfilePatchDocument.Load(f.PatchPath).GetSubagentSelection().Routes;
        Assert.DoesNotContain(routes, r => r.Model == "gpt-6-astra");
        Assert.Contains(routes, r => r.Model == "gpt-6.1-sol");
        Assert.Contains("name: user wrote this old one", f.Patch);
        Assert.Contains("移除 gpt-6-astra", string.Join("\n", CodexFastService.Describe(second)));
    }

    [Fact]
    public void WhatTheUserWroteAfterwardsIsNotOursEvenWhenItLooksTheSame()
    {
        var (client, _) = Client();
        var ownership = new CodexModelOwnership(null);
        var creds = new JsonArray(Cred("c1", true, "tok", DateTimeOffset.UtcNow.AddHours(5)));
        using var f = Fixture(OldList, client, ownership);
        f.Fast.Sync(creds); // records what was written

        var recorded = ownership.Load(f.Home)!;
        Assert.Contains("gpt-6.1-terra", recorded);
        Assert.Contains("gpt-6-luna", recorded); // 0.1.4 had written it id-only: claimed on the first run
        // The user adds an id-only gpt-6.2-nano by hand; it is not recorded, so it is never removed.
        File.WriteAllText(f.PatchPath, File.ReadAllText(f.PatchPath).Replace("          - id: gpt-6-astra\n", "          - id: gpt-6-astra\n          - id: gpt-6.2-nano\n"));
        var (smaller, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[{\"slug\":\"gpt-6.1-sol\",\"visibility\":\"list\",\"priority\":0,\"input_modalities\":[\"text\"]}]}") });
        var f2 = new CodexFastService(() => f.Home, f.Backups, null, f.Log, f.Subagents, () => Bundled, smaller, ownership);

        var result = f2.Sync(creds);

        var ids = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels().Select(m => m.Id).ToArray();
        Assert.Contains("gpt-6.2-nano", ids);
        Assert.DoesNotContain("gpt-6.1-terra", ids);
        Assert.DoesNotContain("gpt-6-luna", ids);
        Assert.Contains("gpt-6.1-sol", ids);
        Assert.Equal(new[] { "gpt-6.1-sol" }, ids.Where(i => !i.EndsWith("-fast") && i != "gpt-6.2-nano")); // nothing else of ours is left
        Assert.Contains(result.RemovedModels, r => r == "gpt-6.1-terra");
    }

    [Fact]
    public void WithoutAFetchedListAnExistingRouteIsNotChangedAndTwinsFollowTheBuiltInRule()
    {
        // No client at all (never ask OpenAI), and a failing server: the base list stays exactly as it is.
        using var none = Fixture(OldList, null);
        var viaNone = none.Fast.Sync(ValidCreds());
        Assert.Equal(new[] { "gpt-6.1-sol-fast", "gpt-6-astra-fast", "gpt-6-luna-fast" }, viaNone.AddedModels);
        Assert.Empty(viaNone.RemovedModels);
        Assert.DoesNotContain("terra", none.Patch);

        var (failing, handler) = Client(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var f = Fixture(OldList, failing);
        var result = f.Fast.Sync(new JsonArray(Cred("c1", true, "tok", DateTimeOffset.UtcNow.AddHours(5))));
        Assert.Single(handler.Calls);
        Assert.Contains("500", result.FetchNote);
        Assert.Contains("未能获取账号模型清单（服务暂时不可用（HTTP 500）），保持现有模型不变。", CodexFastService.Describe(result));
        Assert.DoesNotContain("terra", f.Patch);
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna", "gpt-6.1-sol-fast", "gpt-6-astra-fast", "gpt-6-luna-fast" }, ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels().Select(m => m.Id).ToArray());
    }

    [Fact]
    public void AnAccountWithNothingToOfferChangesNothing()
    {
        var (client, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[{\"slug\":\"gpt-6-sol\",\"visibility\":\"list\",\"input_modalities\":[\"text\"]}]}") });
        using var f = Fixture(OldList, client);

        var result = f.Fast.Sync(ValidCreds().Select(c => (JsonNode?)c!.DeepClone()).ToList().Select(c => { c!["value"]!["expires"] = DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeMilliseconds(); return c; }));

        Assert.Empty(result.RemovedModels);
        Assert.Null(result.FetchedModels);
        Assert.Contains("没有可提供的模型", result.FetchNote);
        Assert.StartsWith(OldList, f.Patch);
    }

    // ---- ownership record ----

    [Fact]
    public void TheOwnershipRecordIsKeptPerHomeAndProfileInTheAppDataFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dshO-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var ownership = new CodexModelOwnership(dir);
            using var a = new DshFixture();
            using var b = new DshFixture();

            Assert.Null(ownership.Load(a.Home));
            ownership.Save(a.Home, new[] { "x", "y", "x" });
            ownership.Save(b.Home, new[] { "z" });

            Assert.Equal(new[] { "x", "y" }, new CodexModelOwnership(dir).Load(a.Home)!.OrderBy(i => i).ToArray());
            Assert.Equal(new[] { "z" }, ownership.Load(b.Home)!.ToArray());
            Assert.True(File.Exists(Path.Combine(dir, CodexModelOwnership.FileName)));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ---- import and subagents ----

    [Fact]
    public async Task AnImportReportsWhatOpenAIOfferedWhatWasAddedAndWhatWasRemoved()
    {
        var (client, handler) = Client();
        using var f = new DshFixture(null, "[]\n", subagents: true, fast: true, packageJson: Package, catalogModels: Bundled, codexClient: client, ownership: new CodexModelOwnership(null));

        var result = await f.Import.ImportAsync(await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in.json", new JsonArray(TestAccounts.CockpitOauth("c-1", "a@example.test", "acct-1", "refresh-1")))));

        Assert.Equal(0, result.Failed);
        Assert.NotEmpty(handler.Calls); // the imported account's own (synthetic) token was used
        Assert.Contains(handler.Calls, c => c.Headers["authorization"].StartsWith("Bearer ") && c.Headers["chatgpt-account-id"] == "acct-1");
        var ids = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels().Select(m => m.Id).ToArray();
        // The activation created the route with the built-in list and its twins (no token yet); the import's own step added the rest.
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-astra", "gpt-6-luna", "gpt-6.1-sol-fast", "gpt-6.1-terra-fast", "gpt-6-astra-fast", "gpt-6-luna-fast", "gpt-6.2-mini" }, ids);
        var text = string.Join("\n", result.Messages);
        Assert.Contains("已从 OpenAI 获取账号可用模型：gpt-6.1-sol、gpt-6.1-terra、gpt-6-astra、gpt-6-luna、gpt-6.2-mini", text);
        Assert.Contains("新增 gpt-6.2-mini", text);
        Assert.Contains("首次安装需要重启 dsh（完全退出 DSH 再打开）后 Fast 模型才可用", text);
        Assert.DoesNotContain("agent-default-model", f.Patch);

        // The sub-agent candidates are the base models (including the new ones), never a Fast twin.
        var routes = ProfilePatchDocument.Load(f.PatchPath).GetSubagentSelection().Routes;
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-astra", "gpt-6-luna", "gpt-6.2-mini" }, routes.Select(r => r.Model).ToArray());

        // Importing again changes nothing and is quiet about models.
        var patch = f.Patch;
        var second = await f.Import.ImportAsync(await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in2.json", new JsonArray(TestAccounts.CockpitOauth("c-2", "b@example.test", "acct-2", "refresh-2")))));
        Assert.Equal(patch, f.Patch);
        Assert.DoesNotContain(second.Messages, m => m.Contains("已从 OpenAI 获取账号可用模型"));
    }

    [Fact]
    public async Task AnImportThatCannotReachOpenAIKeepsTheModelsAndSaysSo()
    {
        var (client, _) = Client(_ => throw new HttpRequestException("offline"));
        using var f = new DshFixture(null, "[]\n", subagents: true, fast: true, packageJson: Package, catalogModels: Bundled, codexClient: client, ownership: new CodexModelOwnership(null));

        var result = await f.Import.ImportAsync(await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in.json", new JsonArray(TestAccounts.CockpitOauth("c-1", "a@example.test", "acct-1", "refresh-1")))));

        Assert.Equal(0, result.Failed);
        Assert.Contains("未能获取账号模型清单（网络不可达），保持现有模型不变", string.Join("\n", result.Messages));
        // The new route holds the built-in list, each with its twin (the component is installed).
        var ids = ProfilePatchDocument.Load(f.PatchPath).GetCodexRouteModels().Select(m => m.Id).ToArray();
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-astra", "gpt-6-luna", "gpt-6.1-sol-fast", "gpt-6.1-terra-fast", "gpt-6-astra-fast", "gpt-6-luna-fast" }, ids);
    }
}
