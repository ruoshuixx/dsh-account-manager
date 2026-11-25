using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Dsh;
using AccountManager.Core.Models;
using AccountManager.Core.Sharing;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

/// <summary>dsh's API list shared with OpenCode (opencode.json + credential) and Codex (config.toml), and Codex using an API. Fakes and temp files only.</summary>
public class ApiShareTests
{
    private const string Responses = "api-0000000000000000000000000000000a";
    private const string Chat = "api-0000000000000000000000000000000b";

    private static JsonObject View(string name, string package, string url, params string[] models)
    {
        var catalog = new JsonObject();
        foreach (var m in models) { catalog[m] = new JsonObject { ["name"] = m }; }
        return new JsonObject { ["name"] = name, ["package"] = package, ["settings"] = new JsonObject { ["baseURL"] = url }, ["models"] = catalog };
    }

    /// <summary>One Responses API and one Chat API in dsh; Codex and OpenCode exist and are empty.</summary>
    private static async Task<DshFixture> SetupAsync()
    {
        var f = new DshFixture();
        await f.Backend.WriteApiProviderAsync(Responses, View("Ark", OpenAiApiProtocol.ResponsesPackage, "https://ark.example.test/api/v3", "m-r1", "m-r2"), "fake-key-r");
        await f.Backend.WriteApiProviderAsync(Chat, View("Chatty", OpenAiApiProtocol.ChatPackage, "https://chat.example.test/v1", "m-c"), "fake-key-c");
        f.UseCli();
        f.UseOpenCode();
        return f;
    }

    // ---- config.toml editing --------------------------------------------------

    [Fact]
    public void TheTomlEditorTouchesOnlyItsTablesAndTheProfileKey()
    {
        const string original = "# my settings\nmodel = \"gpt-5.5\"\n\n[mcp_servers.x]\ncommand = \"x\" # keep\n\n[model_providers.other]\nname = \"Other\"\n";
        var toml = new CodexConfigToml(original);

        Assert.True(toml.SetTable(CodexConfigToml.ProvidersTable, "api-1", new[] { new KeyValuePair<string, string>("name", "A \"q\" \\ b") }));
        Assert.True(toml.SetTop("model_provider", "api-1"));
        Assert.Equal("api-1", toml.GetTop("model_provider"));
        Assert.Equal("A \"q\" \\ b", toml.GetValue(CodexConfigToml.ProvidersTable, "api-1", "name"));
        Assert.Contains("model_provider = \"api-1\"\n", toml.ToString());
        Assert.Equal("gpt-5.5", toml.GetTop("model"));
        Assert.Contains("[mcp_servers.x]\ncommand = \"x\" # keep\n", toml.ToString());

        // Same content again: no change.
        Assert.False(toml.SetTable(CodexConfigToml.ProvidersTable, "api-1", new[] { new KeyValuePair<string, string>("name", "A \"q\" \\ b") }));

        Assert.True(toml.SetTop("model_provider", null));
        Assert.True(toml.SetTable(CodexConfigToml.ProvidersTable, "api-1", null));
        Assert.Equal(original, toml.ToString());
    }

    [Fact]
    public void ATopLevelKeyIsInsertedBeforeTheFirstTableEvenInAFileThatStartsWithOne()
    {
        var toml = new CodexConfigToml("[features]\nx = true\n");
        toml.SetTop("model_provider", "p");
        Assert.Equal("model_provider = \"p\"\n\n[features]\nx = true\n", toml.ToString());
    }

    // ---- sync -----------------------------------------------------------------

    [Fact]
    public async Task EveryApiGoesToOpenCodeAndOnlyResponsesOnesGoToCodex()
    {
        using var f = await SetupAsync();

        var result = await f.Share.SyncAsync(SharedState.Empty);

        Assert.True(result.Changed);
        Assert.Empty(result.Notes);
        Assert.Equal(new[] { Responses, Chat }.OrderBy(x => x), result.State.OpenCode.OrderBy(x => x));
        Assert.Equal(new[] { Responses }, result.State.Codex);

        // OpenCode: provider entries with OpenCode's packages, keys as active credentials of that provider.
        var providers = f.OpenCodeConfig.Read()!["providers"]!.AsObject();
        Assert.Equal(ApiShareService.OpenCodeResponsesPackage, providers[Responses]!["package"]!.GetValue<string>());
        Assert.Equal("http", providers[Responses]!["settings"]!["transport"]!.GetValue<string>());
        Assert.Equal(ApiShareService.OpenCodeChatPackage, providers[Chat]!["package"]!.GetValue<string>());
        Assert.Equal("https://chat.example.test/v1", providers[Chat]!["settings"]!["baseURL"]!.GetValue<string>());
        Assert.Equal(new[] { "m-r1", "m-r2" }, providers[Responses]!["models"]!.AsObject().Select(kv => kv.Key));
        var key = Assert.Single(f.OpenCodeApi.Credentials, c => c["integrationID"]!.GetValue<string>() == Chat);
        Assert.Equal("fake-key-c", key["value"]!["key"]!.GetValue<string>());
        Assert.True(key["active"]!.GetValue<bool>());

        // Codex: only the private catalog is populated until an API is selected.
        var toml = f.CodexApi.Read();
        Assert.False(toml.HasTable(CodexConfigToml.ProvidersTable, Responses));
        Assert.False(toml.HasTable(CodexConfigToml.ProvidersTable, CodexApiConfig.ProviderId));
        Assert.Equal(new[] { "m-r1", "m-r2" }, f.CodexApi.LoadState()["catalog"]![Responses]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(toml.HasTable("profiles", Responses));
        Assert.False(toml.HasTable(CodexConfigToml.ProvidersTable, Chat));
        Assert.Null(toml.Profile);
        Assert.Null(toml.GetTop("model_provider"));
        Assert.Null(f.CodexApi.CurrentApi());
    }

    // ---- OpenCode speed tiers (experimental.model_speeds) ----------------------

    private static ApiShareService WithOpenCodeVersion(DshFixture f, Func<Version?> version)
        => new(f.Backend, f.CodexApi, f.OpenCodeApi, f.OpenCodeConfig, f.Secrets, f.Log, f.Backups, _ => Task.FromResult(version()));

    [Fact]
    public async Task ASpeedAwareOpenCodeGetsTheSpeedSwitchOnceAndKeepsItsOtherSettings()
    {
        using var f = await SetupAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(f.OpenCodeConfig.Path)!);
        File.WriteAllText(f.OpenCodeConfig.Path, "{ \"experimental\": { \"artifacts\": true } }");
        var share = WithOpenCodeVersion(f, () => new Version(2, 4, 1));

        var first = await share.SyncAsync(SharedState.Empty);

        var experimental = f.OpenCodeConfig.Read()!["experimental"]!.AsObject();
        Assert.True(experimental["model_speeds"]!.GetValue<bool>());
        Assert.True(experimental["artifacts"]!.GetValue<bool>());
        var writes = f.OpenCodeConfig.Writes;
        var second = await share.SyncAsync(first.State);
        Assert.False(second.Changed);
        Assert.Equal(writes, f.OpenCodeConfig.Writes);
    }

    [Fact]
    public async Task ASpeedSwitchTheUserAlreadySetIsNeverChanged()
    {
        using var f = await SetupAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(f.OpenCodeConfig.Path)!);
        File.WriteAllText(f.OpenCodeConfig.Path, "{ \"experimental\": { \"model_speeds\": false } }");

        await WithOpenCodeVersion(f, () => new Version(2, 5, 0)).SyncAsync(SharedState.Empty);

        Assert.False(f.OpenCodeConfig.Read()!["experimental"]!["model_speeds"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("2.3.2")]
    [InlineData("1.19.6")]
    [InlineData("")]
    public async Task AnOlderOrUnknownOpenCodeIsLeftWithoutTheSpeedSwitch(string version)
    {
        using var f = await SetupAsync();
        var share = WithOpenCodeVersion(f, () => version.Length == 0 ? null : Version.Parse(version));

        var result = await share.SyncAsync(SharedState.Empty);

        Assert.Empty(result.Notes);
        Assert.Null(f.OpenCodeConfig.Read()!["experimental"]);
    }

    [Fact]
    public async Task AFailingVersionQueryNeitherBlocksTheSyncNorWritesTheSwitch()
    {
        using var f = await SetupAsync();
        var share = new ApiShareService(f.Backend, f.CodexApi, f.OpenCodeApi, f.OpenCodeConfig, f.Secrets, f.Log, f.Backups,
            _ => throw new AccountManager.Core.OpenCode.OpenCodeException("no version"));

        var result = await share.SyncAsync(SharedState.Empty);

        Assert.Empty(result.Notes);
        Assert.Equal(2, result.State.OpenCode.Count);
        Assert.Null(f.OpenCodeConfig.Read()!["experimental"]);
    }

    private static JsonObject OpenAiView(bool fast)
    {
        static JsonArray Tiers(params string[] efforts) => new(efforts.Select(e => (JsonNode)new JsonObject
        {
            ["id"] = e,
            ["body"] = new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = e } },
        }).ToArray());
        var models = new JsonObject
        {
            ["gpt-6.1-sol"] = new JsonObject { ["name"] = "gpt-6.1-sol", ["variants"] = Tiers("low", "high") },
            ["gpt-6-luna"] = new JsonObject { ["name"] = "gpt-6-luna" },
            ["gpt-6.1-terra"] = new JsonObject { ["name"] = "gpt-6.1-terra", ["variants"] = Tiers("low") },
        };
        if (fast)
        {
            models["gpt-6.1-sol-fast"] = new JsonObject { ["name"] = "gpt-6.1-sol (Fast)", ["variants"] = Tiers("low", "high") };
            models["gpt-6-luna-fast"] = new JsonObject { ["name"] = "gpt-6-luna (Fast)" };
        }

        return new JsonObject
        {
            ["name"] = "OpenAI",
            ["package"] = OpenAiApiProtocol.ResponsesPackage,
            ["settings"] = new JsonObject { ["baseURL"] = "https://api.openai.com/v1" },
            ["models"] = models,
        };
    }

    [Fact]
    public async Task AnApiWithFastTwinsInDshGetsFastVariantsInOpenCodeAndLosesThemWhenFastIsOff()
    {
        using var f = await SetupAsync();
        const string id = "api-0000000000000000000000000000000c";
        await f.Backend.WriteApiProviderAsync(id, OpenAiView(fast: true), "fake-key-o");

        var first = await f.Share.SyncAsync(SharedState.Empty);

        var models = f.OpenCodeConfig.Read()!["providers"]![id]!["models"]!.AsObject();
        // The twin models themselves are not shared; Fast is a variant of the base model.
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6-luna", "gpt-6.1-terra" }.OrderBy(x => x), models.Select(kv => kv.Key).OrderBy(x => x));
        var sol = models["gpt-6.1-sol"]!["variants"]!.AsArray();
        Assert.Equal(new[] { "low", "high", "low-fast", "high-fast" }, sol.Select(v => v!["id"]!.GetValue<string>()));
        Assert.Equal("high", sol[3]!["body"]!["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("priority", sol[3]!["body"]!["service_tier"]!.GetValue<string>());
        Assert.Null(sol[1]!["body"]!["service_tier"]);
        var luna = models["gpt-6-luna"]!["variants"]!.AsArray();
        Assert.Equal("fast", Assert.Single(luna)!["id"]!.GetValue<string>());
        Assert.Equal("priority", luna[0]!["body"]!["service_tier"]!.GetValue<string>());

        // Fast unticked in dsh: the twins go and so do the OpenCode variants.
        await f.Backend.WriteApiProviderAsync(id, OpenAiView(fast: false), "fake-key-o");
        await f.Share.SyncAsync(first.State);
        var after = f.OpenCodeConfig.Read()!["providers"]![id]!["models"]!.AsObject();
        Assert.Equal(new[] { "low", "high" }, after["gpt-6.1-sol"]!["variants"]!.AsArray().Select(v => v!["id"]!.GetValue<string>()));
        Assert.Null(after["gpt-6-luna"]!["variants"]);
    }

    [Fact]
    public async Task TheChatGptPlanGptModelsGetFastVariantsThatKeepTheEffortAndAreWrittenOnce()
    {
        using var f = await SetupAsync();
        var share = WithOpenCodeVersion(f, () => new Version(2, 4, 1));

        var first = await share.SyncAsync(SharedState.Empty);

        var models = f.OpenCodeConfig.Read()!["providers"]!["openai"]!["models"]!.AsObject();
        Assert.Equal(AccountManager.Core.OpenCode.OpenCodeFastVariants.Models.OrderBy(x => x), models.Select(kv => kv.Key).OrderBy(x => x));
        var variants = models["gpt-6.2-astra"]!["variants"]!.AsArray();
        Assert.Equal(new[] { "low-fast", "medium-fast", "high-fast", "xhigh-fast", "max-fast" }, variants.Select(v => v!["id"]!.GetValue<string>()));
        var xhigh = variants.Single(v => v!["id"]!.GetValue<string>() == "xhigh-fast")!;
        Assert.Equal("xhigh", xhigh["settings"]!["reasoningEffort"]!.GetValue<string>());
        Assert.Equal("auto", xhigh["settings"]!["reasoningSummary"]!.GetValue<string>());
        Assert.Equal("reasoning.encrypted_content", xhigh["settings"]!["include"]![0]!.GetValue<string>());
        Assert.Equal("priority", xhigh["body"]!["service_tier"]!.GetValue<string>());

        var writes = f.OpenCodeConfig.Writes;
        var second = await share.SyncAsync(first.State);
        Assert.False(second.Changed);
        Assert.Equal(writes, f.OpenCodeConfig.Writes);
    }

    [Fact]
    public async Task FastVariantsKeepWhatTheUserAlreadyHasInTheOpenAiProvider()
    {
        using var f = await SetupAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(f.OpenCodeConfig.Path)!);
        File.WriteAllText(f.OpenCodeConfig.Path, """
            { "providers": { "openai": { "name": "mine", "models": { "gpt-6.2-astra": { "name": "Astra", "variants": [
              { "id": "xhigh-fast", "body": { "service_tier": "flex" } }, { "id": "batch", "body": { "service_tier": "flex" } } ] } } } } }
            """);

        await WithOpenCodeVersion(f, () => new Version(2, 4, 1)).SyncAsync(SharedState.Empty);

        var openai = f.OpenCodeConfig.Read()!["providers"]!["openai"]!;
        Assert.Equal("mine", openai["name"]!.GetValue<string>());
        var astra = openai["models"]!["gpt-6.2-astra"]!;
        Assert.Equal("Astra", astra["name"]!.GetValue<string>());
        var variants = astra["variants"]!.AsArray();
        Assert.Equal(new[] { "xhigh-fast", "batch", "low-fast", "medium-fast", "high-fast", "max-fast" }, variants.Select(v => v!["id"]!.GetValue<string>()));
        Assert.Equal("flex", variants[0]!["body"]!["service_tier"]!.GetValue<string>());
    }

    [Fact]
    public async Task NoFastVariantsForAnOpenCodeBeforeVersion2OrOfAnUnknownVersion()
    {
        using var f = await SetupAsync();
        await WithOpenCodeVersion(f, () => new Version(1, 19, 6)).SyncAsync(SharedState.Empty);
        Assert.Null(f.OpenCodeConfig.Read()!["providers"]!["openai"]);

        await WithOpenCodeVersion(f, () => null).SyncAsync(SharedState.Empty);
        Assert.Null(f.OpenCodeConfig.Read()!["providers"]!["openai"]);
    }

    [Fact]
    public async Task ASecondSyncWithoutChangesWritesNothing()
    {
        using var f = await SetupAsync();
        var first = await f.Share.SyncAsync(SharedState.Empty);
        var toml = f.CodexApi.ReadText();
        var writes = f.OpenCodeConfig.Writes;
        var calls = f.OpenCodeApi.Calls.Count;

        var second = await f.Share.SyncAsync(first.State);

        Assert.False(second.Changed);
        Assert.Equal(toml, f.CodexApi.ReadText());
        Assert.Equal(writes, f.OpenCodeConfig.Writes);
        Assert.Equal(calls, f.OpenCodeApi.Calls.Count);
    }

    [Fact]
    public async Task ANewKeyReplacesTheOpenCodeCredentialAndTheCodexToken()
    {
        using var f = await SetupAsync();
        var first = await f.Share.SyncAsync(SharedState.Empty);
        f.CodexApi.Use(Responses);
        await f.Backend.WriteApiProviderAsync(Responses, View("Renamed", OpenAiApiProtocol.ResponsesPackage, "https://new.example.test/v1/", "m-r1", "m-r2"), "fake-key-r2");

        await f.Share.SyncAsync(first.State);

        var key = Assert.Single(f.OpenCodeApi.Credentials, c => c["integrationID"]!.GetValue<string>() == Responses);
        Assert.Equal("fake-key-r2", key["value"]!["key"]!.GetValue<string>());
        var toml = f.CodexApi.Read();
        Assert.Equal("fake-key-r2", toml.GetValue(CodexConfigToml.ProvidersTable, CodexApiConfig.ProviderId, "experimental_bearer_token"));
        Assert.Equal("Renamed", toml.GetValue(CodexConfigToml.ProvidersTable, CodexApiConfig.ProviderId, "name"));
        Assert.Equal("https://new.example.test/v1", toml.GetValue(CodexConfigToml.ProvidersTable, CodexApiConfig.ProviderId, "base_url"));
        Assert.Equal("responses", toml.GetValue(CodexConfigToml.ProvidersTable, CodexApiConfig.ProviderId, "wire_api"));
        Assert.Equal(Responses, f.CodexApi.CurrentApi());
    }

    [Fact]
    public async Task AnApiRemovedFromDshIsRemovedThereButForeignOnesStay()
    {
        using var f = await SetupAsync();
        // Someone else's provider in both programs, with the same id pattern.
        const string foreign = "api-0000000000000000000000000000000f";
        File.WriteAllText(f.CodexConfigPath, "[model_providers." + foreign + "]\nname = \"Mine\"\n");
        f.OpenCodeApi.AddLogin("x@example.test", "acct-x", "r", integration: foreign);
        var first = await f.Share.SyncAsync(SharedState.Empty);
        // Codex is using the API that is about to go.
        f.CodexApi.Use(Responses);

        await f.Backend.RemoveApiProviderAsync(Responses);
        var second = await f.Share.SyncAsync(first.State);

        Assert.Equal(new[] { Chat }, second.State.OpenCode);
        Assert.Empty(second.State.Codex);
        Assert.False(f.OpenCodeConfig.Read()!["providers"]!.AsObject().ContainsKey(Responses));
        Assert.DoesNotContain(f.OpenCodeApi.Credentials, c => c["integrationID"]!.GetValue<string>() == Responses);
        Assert.Contains(f.OpenCodeApi.Credentials, c => c["integrationID"]!.GetValue<string>() == foreign);
        var toml = f.CodexApi.Read();
        Assert.False(toml.HasTable(CodexConfigToml.ProvidersTable, Responses));
        Assert.False(toml.HasTable(CodexConfigToml.ProvidersTable, CodexApiConfig.ProviderId));
        Assert.True(toml.HasTable(CodexConfigToml.ProvidersTable, foreign));
        Assert.Null(toml.GetTop("model_provider")); // back to the ChatGPT login
    }

    [Fact]
    public async Task WithoutOpenCodeOrCodexNothingIsWrittenAndNothingComplains()
    {
        using var f = new DshFixture();
        await f.Backend.WriteApiProviderAsync(Responses, View("Ark", OpenAiApiProtocol.ResponsesPackage, "https://ark.example.test/api/v3", "m"), "fake-key-r");

        var result = await f.Share.SyncAsync(SharedState.Empty);

        Assert.Empty(result.Notes);
        Assert.False(result.Changed);
        Assert.False(Directory.Exists(f.CodexHome));
        Assert.Empty(result.State.OpenCode);
    }

    // ---- Codex using an API -----------------------------------------------------

    [Fact]
    public async Task CodexSwitchesToAnApiAndBackToAChatGptAccount()
    {
        using var f = await SetupAsync();
        var a = await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a");
        f.UseCli(authJson: DshFixture.AuthJson("a@example.test", "acct-1", "refresh-a"));
        await f.Share.SyncAsync(SharedState.Empty);
        f.Target = SwitchTarget.Codex;
        var authBefore = File.ReadAllBytes(f.CodexAuthPath);

        var api = (await f.Snapshot.GetSnapshotAsync()).Accounts.Single(r => r.Kind == AccountKind.Api && r.Provider == Responses);
        var used = await f.Switch.SwitchAsync(api.CredentialId);

        Assert.True(used.Changed);
        Assert.Equal(Responses, f.CodexApi.CurrentApi());
        Assert.Equal(authBefore, File.ReadAllBytes(f.CodexAuthPath)); // the ChatGPT login stays
        var snapshot = await f.Snapshot.GetSnapshotAsync();
        Assert.Equal(api.CredentialId, Assert.Single(snapshot.Accounts, r => r.TargetCurrent).CredentialId);

        // Same account as in auth.json: only the API is left.
        var back = await f.Switch.SwitchAsync(a);

        Assert.True(back.Changed);
        Assert.Null(f.CodexApi.CurrentApi());
        var after = await f.Snapshot.GetSnapshotAsync();
        Assert.Equal(a, Assert.Single(after.Accounts, r => r.TargetCurrent).CredentialId);
    }

    [Fact]
    public async Task UsingAnApiSetsModelProviderAndModelAndLeavingRestoresTheUsersOwn()
    {
        using var f = await SetupAsync();
        File.WriteAllText(f.CodexConfigPath, "model = \"gpt-6.1-sol\"\nmodel_reasoning_effort = \"high\"\n");
        await f.Share.SyncAsync(SharedState.Empty);

        Assert.True(f.CodexApi.Use(Responses));
        var toml = f.CodexApi.Read();
        Assert.Equal(CodexApiConfig.ProviderId, toml.GetTop("model_provider"));
        Assert.Equal("m-r1", toml.GetTop("model"));
        Assert.Null(toml.Profile); // never the legacy selector
        Assert.Equal("high", toml.GetTop("model_reasoning_effort"));

        // The user picks another model in Codex; it is remembered for the next time.
        f.CodexApi.Update(t => t.SetTop("model", "m-r2"));
        Assert.True(f.CodexApi.LeaveApi());
        toml = f.CodexApi.Read();
        Assert.Null(toml.GetTop("model_provider"));
        Assert.Equal("gpt-6.1-sol", toml.GetTop("model"));
        Assert.False(toml.HasTable(CodexConfigToml.ProvidersTable, CodexApiConfig.ProviderId));

        f.CodexApi.Use(Responses);
        Assert.Equal("m-r2", f.CodexApi.Read().GetTop("model"));
    }

    [Fact]
    public async Task WhatVersion020WroteIsMigratedSoCodexStartsAgain()
    {
        using var f = await SetupAsync();
        var first = await f.Share.SyncAsync(SharedState.Empty);
        // 0.2.0: a profile table per API and the top-level selector Codex 0.134+ refuses.
        File.WriteAllText(f.CodexConfigPath, "profile = \"" + Responses + "\"\n\n"
            + "\n[profiles." + Responses + "]\nmodel_provider = \"" + Responses + "\"\nmodel = \"m-r2\"\n");

        await f.Share.SyncAsync(first.State);

        var toml = f.CodexApi.Read();
        Assert.Null(toml.Profile);
        Assert.False(toml.HasTable("profiles", Responses));
        Assert.Equal(CodexApiConfig.ProviderId, toml.GetTop("model_provider")); // still on the API it had chosen ...
        Assert.Equal(Responses, f.CodexApi.CurrentApi());
        Assert.Equal("m-r2", toml.GetTop("model"));             // ... with its model
        Assert.True(f.CodexApi.LeaveApi());
        Assert.Null(f.CodexApi.Read().GetTop("model_provider"));
    }

    [Fact]
    public async Task AllApisUseOneTableAndRememberTheirOwnModels()
    {
        using var f = await SetupAsync();
        const string second = "api-second";
        await f.Backend.WriteApiProviderAsync(second, View("Second", OpenAiApiProtocol.ResponsesPackage, "https://second.example.test/v1", "s1", "s2"), "fake-second-key");
        File.WriteAllText(f.CodexConfigPath, "model_provider = \"user-provider\"\nmodel = \"user-model\"\n\n[model_providers.user-provider]\nname = \"Mine\"\n");
        var sync = await f.Share.SyncAsync(SharedState.Empty);
        Assert.Equal(new[] { Responses, second }.OrderBy(x => x), sync.State.Codex.OrderBy(x => x));

        Assert.True(f.CodexApi.Use(Responses));
        f.CodexApi.Update(t => t.SetTop("model", "m-r2"));
        Assert.True(f.CodexApi.Use(second));
        Assert.Equal("s1", f.CodexApi.Read().GetTop("model"));
        Assert.Equal(second, f.CodexApi.LoadState()["api"]!.GetValue<string>());
        f.CodexApi.Update(t => t.SetTop("model", "s2"));
        Assert.True(f.CodexApi.Use(Responses));
        Assert.Equal("m-r2", f.CodexApi.Read().GetTop("model"));
        Assert.True(f.CodexApi.Use(second));
        Assert.Equal("s2", f.CodexApi.Read().GetTop("model"));
        Assert.Equal(CodexApiConfig.ProviderId, f.CodexApi.Read().GetTop("model_provider"));
        Assert.Equal(1, f.CodexApi.ReadText()!.Split("[model_providers." + CodexApiConfig.ProviderId + "]").Length - 1);
        Assert.False(f.CodexApi.Read().HasTable(CodexConfigToml.ProvidersTable, Responses));
        Assert.False(f.CodexApi.Read().HasTable(CodexConfigToml.ProvidersTable, second));
        Assert.False(f.CodexApi.Use(second));

        await f.Backend.RemoveApiProviderAsync(second);
        await f.Share.SyncAsync(sync.State);
        Assert.Equal("user-provider", f.CodexApi.Read().GetTop("model_provider"));
        Assert.Equal("user-model", f.CodexApi.Read().GetTop("model"));
        Assert.True(f.CodexApi.Read().HasTable(CodexConfigToml.ProvidersTable, "user-provider"));
        Assert.False(f.CodexApi.Read().HasTable(CodexConfigToml.ProvidersTable, CodexApiConfig.ProviderId));
        Assert.Contains(second, f.CodexApi.LegacyProviderIds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyApiSelectorsAndTablesAreMigratedWithTheModelIntact(bool hasState)
    {
        using var f = await SetupAsync();
        if (hasState)
        {
            f.CodexApi.SaveState(new JsonObject
            {
                ["api"] = Responses,
                ["before"] = new JsonObject { ["model_provider"] = "openai", ["model"] = "original-model" },
            });
        }
        File.WriteAllText(f.CodexConfigPath, "model_provider = \"" + Responses + "\"\nmodel = \"legacy-model\"\n\n[model_providers."
            + Responses + "]\nname = \"Old\"\n\n[model_providers.removed-api]\nname = \"Removed\"\n\n[model_providers.foreign]\nname = \"Mine\"\n");

        var result = await f.Share.SyncAsync(new SharedState(Array.Empty<string>(), new[] { Responses, "removed-api" }));

        Assert.Empty(result.Notes);
        Assert.Equal(Responses, f.CodexApi.CurrentApi());
        Assert.Equal(CodexApiConfig.ProviderId, f.CodexApi.Read().GetTop("model_provider"));
        Assert.Equal("legacy-model", f.CodexApi.Read().GetTop("model"));
        Assert.False(f.CodexApi.Read().HasTable(CodexConfigToml.ProvidersTable, Responses));
        Assert.False(f.CodexApi.Read().HasTable(CodexConfigToml.ProvidersTable, "removed-api"));
        Assert.True(f.CodexApi.Read().HasTable(CodexConfigToml.ProvidersTable, "foreign"));
        Assert.True(f.CodexApi.LeaveApi());
        Assert.Equal(hasState ? "openai" : null, f.CodexApi.Read().GetTop("model_provider"));
        Assert.Equal(hasState ? "original-model" : null, f.CodexApi.Read().GetTop("model"));
    }

    [Fact]
    public async Task CurrentApiRequiresBothOurSelectorAndTable()
    {
        using var f = await SetupAsync();
        await f.Share.SyncAsync(SharedState.Empty);
        f.CodexApi.Use(Responses);
        Assert.Equal(Responses, f.CodexApi.CurrentApi());
        f.CodexApi.Update(t => t.SetTop("model_provider", "openai"));
        Assert.Null(f.CodexApi.CurrentApi());
        f.CodexApi.Update(t => { t.SetTop("model_provider", CodexApiConfig.ProviderId); return t.SetTable(CodexConfigToml.ProvidersTable, CodexApiConfig.ProviderId, null); });
        Assert.Null(f.CodexApi.CurrentApi());
    }

    [Fact]
    public void SwitchingToALoginRemovesALegacyProfileEvenWithoutASync()
    {
        using var f = new DshFixture();
        f.UseCli();
        File.WriteAllText(f.CodexConfigPath, "profile = \"cockpit-1\"\n\n[model_providers.cockpit-1]\nname = \"x\"\n\n[profiles.cockpit-1]\nmodel_provider = \"cockpit-1\"\n");

        Assert.True(f.CodexApi.LeaveApi());
        Assert.Null(f.CodexApi.Read().Profile);
    }

    [Fact]
    public async Task AChatApiCannotBeUsedByCodexAndApisAreNotSwitchedForDsh()
    {
        using var f = await SetupAsync();
        await f.Share.SyncAsync(SharedState.Empty);
        var chat = (await f.Snapshot.GetSnapshotAsync()).Accounts.Single(r => r.Kind == AccountKind.Api && r.Provider == Chat);

        var forDsh = await Assert.ThrowsAsync<DshException>(() => f.Switch.SwitchAsync(chat.CredentialId));
        Assert.Contains("无需切换", forDsh.Message);

        f.Target = SwitchTarget.Codex;
        var forCodex = await Assert.ThrowsAsync<DshException>(() => f.Switch.SwitchAsync(chat.CredentialId));
        Assert.Contains("Responses", forCodex.Message);
        Assert.Null(f.CodexApi.Read().GetTop("model_provider"));
    }
}
