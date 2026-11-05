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

        // Codex: a provider table for the Responses API only, no profiles; nothing selected yet.
        var toml = f.CodexApi.Read();
        Assert.Equal("https://ark.example.test/api/v3", toml.GetValue(CodexConfigToml.ProvidersTable, Responses, "base_url"));
        Assert.Equal("responses", toml.GetValue(CodexConfigToml.ProvidersTable, Responses, "wire_api"));
        Assert.Equal("fake-key-r", toml.GetValue(CodexConfigToml.ProvidersTable, Responses, "experimental_bearer_token"));
        Assert.False(toml.HasTable("profiles", Responses));
        Assert.False(toml.HasTable(CodexConfigToml.ProvidersTable, Chat));
        Assert.Null(toml.Profile);
        Assert.Null(toml.GetTop("model_provider"));
        Assert.Null(f.CodexApi.CurrentApi());
    }

    [Fact]
    public async Task ASecondSyncWithoutChangesWritesNothing()
    {
        using var f = await SetupAsync();
        var first = await f.Share.SyncAsync(SharedState.Empty);
        var toml = File.ReadAllText(f.CodexConfigPath);
        var writes = f.OpenCodeConfig.Writes;
        var calls = f.OpenCodeApi.Calls.Count;

        var second = await f.Share.SyncAsync(first.State);

        Assert.False(second.Changed);
        Assert.Equal(toml, File.ReadAllText(f.CodexConfigPath));
        Assert.Equal(writes, f.OpenCodeConfig.Writes);
        Assert.Equal(calls, f.OpenCodeApi.Calls.Count);
    }

    [Fact]
    public async Task ANewKeyReplacesTheOpenCodeCredentialAndTheCodexToken()
    {
        using var f = await SetupAsync();
        var first = await f.Share.SyncAsync(SharedState.Empty);
        await f.Backend.WriteApiProviderAsync(Responses, View("Ark", OpenAiApiProtocol.ResponsesPackage, "https://ark.example.test/api/v3", "m-r1", "m-r2"), "fake-key-r2");

        await f.Share.SyncAsync(first.State);

        var key = Assert.Single(f.OpenCodeApi.Credentials, c => c["integrationID"]!.GetValue<string>() == Responses);
        Assert.Equal("fake-key-r2", key["value"]!["key"]!.GetValue<string>());
        Assert.Equal("fake-key-r2", f.CodexApi.Read().GetValue(CodexConfigToml.ProvidersTable, Responses, "experimental_bearer_token"));
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
        Assert.Equal(Responses, toml.GetTop("model_provider"));
        Assert.Equal("m-r1", toml.GetTop("model"));
        Assert.Null(toml.Profile); // never the legacy selector
        Assert.Equal("high", toml.GetTop("model_reasoning_effort"));

        // The user picks another model in Codex; it is remembered for the next time.
        f.CodexApi.Update(t => t.SetTop("model", "m-r2"));
        Assert.True(f.CodexApi.LeaveApi());
        toml = f.CodexApi.Read();
        Assert.Null(toml.GetTop("model_provider"));
        Assert.Equal("gpt-6.1-sol", toml.GetTop("model"));

        f.CodexApi.Use(Responses);
        Assert.Equal("m-r2", f.CodexApi.Read().GetTop("model"));
    }

    [Fact]
    public async Task WhatVersion020WroteIsMigratedSoCodexStartsAgain()
    {
        using var f = await SetupAsync();
        var first = await f.Share.SyncAsync(SharedState.Empty);
        // 0.2.0: a profile table per API and the top-level selector Codex 0.134+ refuses.
        File.WriteAllText(f.CodexConfigPath, "profile = \"" + Responses + "\"\n\n" + File.ReadAllText(f.CodexConfigPath)
            + "\n[profiles." + Responses + "]\nmodel_provider = \"" + Responses + "\"\nmodel = \"m-r2\"\n");

        await f.Share.SyncAsync(first.State);

        var toml = f.CodexApi.Read();
        Assert.Null(toml.Profile);
        Assert.False(toml.HasTable("profiles", Responses));
        Assert.Equal(Responses, toml.GetTop("model_provider")); // still on the API it had chosen ...
        Assert.Equal("m-r2", toml.GetTop("model"));             // ... with its model
        Assert.True(f.CodexApi.LeaveApi());
        Assert.Null(f.CodexApi.Read().GetTop("model_provider"));
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
