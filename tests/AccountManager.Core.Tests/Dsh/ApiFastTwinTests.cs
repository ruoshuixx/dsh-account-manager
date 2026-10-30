using System.Text.Json.Nodes;
using AccountManager.Core.Actions;
using AccountManager.Core.Dsh;
using AccountManager.Core.Models;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

/// <summary>Fast twins of the official OpenAI API provider: writing, hiding, removing and the component rule.</summary>
public class ApiFastTwinTests
{
    private const string OpenAi = "https://api.openai.com/v1";

    private const string Package = "{\n  \"name\": \"dsh-profile-tauri\",\n  \"private\": true,\n  \"dependencies\": {},\n  \"dsh\": {\n    \"profile\": {\n      \"bundles\": [\n        \"@deepseek-ai/dsh-base\"\n      ]\n    }\n  }\n}\n";

    private static readonly string[] Models = { "gpt-6-astra", "gpt-6.1-sol", "gpt-5.5" };

    private static string[] ModelIdsOf(DshFixture f, string providerId)
        => ProfilePatchDocument.Load(f.PatchPath).GetProviders()![providerId]!["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToArray();

    private static async Task<(string Id, AccountRow Row)> AddAsync(DshFixture f, bool fast, string url = OpenAi, bool responses = true)
    {
        var id = AccountActions.NewApiProviderId();
        var added = await f.Actions.CreateManagedApiProviderAsync(id, "OpenAI", url, Models, "fake-key-1", responses, fast);
        Assert.True(added.Saved && added.Complete, added.Warning);
        var row = (await f.Snapshot.GetSnapshotAsync()).Accounts.Single(a => a.Kind == AccountKind.Api);
        return (id, row);
    }

    [Fact]
    public async Task AddingWithFastWritesTwinsRightAfterTheirBaseWithTheSameTiersAndInstallsTheComponent()
    {
        using var f = new DshFixture(packageJson: Package);

        var (id, _) = await AddAsync(f, fast: true);

        Assert.Equal(new[] { "gpt-6-astra", "gpt-6-astra-fast", "gpt-6.1-sol", "gpt-6.1-sol-fast", "gpt-5.5" }, ModelIdsOf(f, id));
        var models = ProfilePatchDocument.Load(f.PatchPath).GetProviders()![id]!["models"]!.AsArray();
        var astra = models[0]!.AsObject();
        var twin = models[1]!.AsObject();
        Assert.Equal("gpt-6-astra (Fast)", twin["name"]!.GetValue<string>());
        Assert.Equal(astra["reasoningEfforts"]!.ToJsonString(), twin["reasoningEfforts"]!.ToJsonString());
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, twin["reasoningEfforts"]!.AsObject().Select(kv => kv.Key));
        Assert.Null(models[4]!["reasoningEfforts"]); // gpt-5.5 is not guessed
        Assert.Equal(FastComponentState.Installed, f.Fast.Installer.GetStatus().State);
    }

    [Fact]
    public async Task TheAddResultSaysTheFirstInstallNeedsARestart()
    {
        using var f = new DshFixture(packageJson: Package);
        var id = AccountActions.NewApiProviderId();
        var added = await f.Actions.CreateManagedApiProviderAsync(id, "OpenAI", OpenAi, Models, "fake-key-1", true, true);
        Assert.Equal(ApiFastTwins.RestartNote, added.FastNote);
    }

    [Fact]
    public async Task WithoutAProfilePackageJsonNoTwinIsWrittenButTheRestIsSavedAndTheNoteExplains()
    {
        using var f = new DshFixture(); // no package.json: the component cannot be installed
        var id = AccountActions.NewApiProviderId();

        var added = await f.Actions.CreateManagedApiProviderAsync(id, "OpenAI", OpenAi, Models, "fake-key-1", true, true);

        Assert.True(added.Saved && added.Complete, added.Warning);
        Assert.Contains("没有写入 Fast 模型", added.FastNote);
        Assert.Equal(Models, ModelIdsOf(f, id));
        Assert.False(Directory.Exists(f.PluginDir));
    }

    [Fact]
    public async Task NoTwinsOnOtherEndpointsOrWithTheChatProtocol()
    {
        using (var f = new DshFixture(packageJson: Package))
        {
            var (id, _) = await AddAsync(f, fast: true, url: "https://gw.example.test/v1");
            Assert.Equal(Models, ModelIdsOf(f, id));
        }

        using (var f = new DshFixture(packageJson: Package))
        {
            var (id, _) = await AddAsync(f, fast: true, responses: false);
            Assert.Equal(Models, ModelIdsOf(f, id));
        }
    }

    [Fact]
    public async Task TheEditDialogSeesTheStoredAddressAndNoTwinsAndEditingAloneChangesNothing()
    {
        using var f = new DshFixture(packageJson: Package);
        var (id, _) = await AddAsync(f, fast: true);
        f.Secrets.RegisterValue(OpenAi); // a registered "secret" equal to the address must not hide it from the edit form
        var row = (await f.Snapshot.GetSnapshotAsync()).Accounts.Single(a => a.Kind == AccountKind.Api);

        Assert.Equal(OpenAi, row.Definition!.EditableBaseUrl);
        Assert.Equal(new[] { "gpt-6-astra-fast", "gpt-6.1-sol-fast" }, row.Definition.FastTwinIds);
        Assert.Equal(OpenAi, row.BaseUrl);

        // Saving what the dialog shows (twins hidden, tick on) leaves the file as it is.
        var before = f.Patch;
        var visible = row.ModelIds.Where(m => !row.Definition.FastTwinIds.Contains(m)).ToList();
        var saved = await f.Actions.UpdateManagedApiProviderAsync(row.CredentialId, id, "OpenAI", OpenAi, visible, null, true, null, true);
        Assert.True(saved.Updated && saved.Complete, saved.Warning);
        Assert.Equal(before, f.Patch);
        Assert.Equal(string.Empty, saved.FastNote);
    }

    [Fact]
    public async Task UntickingRemovesTheTwinsAndAUserTypedFastIdWithoutAListedBaseStaysAModel()
    {
        using var f = new DshFixture(packageJson: Package);
        var (id, row) = await AddAsync(f, fast: true);

        var removed = await f.Actions.UpdateManagedApiProviderAsync(row.CredentialId, id, "OpenAI", OpenAi, new[] { "gpt-6-astra", "gpt-6.1-sol", "gpt-5.5", "foo-fast" }, null, true, null, false);
        Assert.True(removed.Complete, removed.Warning);
        Assert.Equal(new[] { "gpt-6-astra", "gpt-6.1-sol", "gpt-5.5", "foo-fast" }, ModelIdsOf(f, id));

        var again = (await f.Snapshot.GetSnapshotAsync()).Accounts.Single(a => a.Kind == AccountKind.Api);
        Assert.Empty(again.Definition!.FastTwinIds); // foo-fast has no listed base: not a twin
    }

    [Fact]
    public async Task TickingOnAnExistingProviderBackfillsTiersAndAddsTwinsAndAnOutdatedComponentIsUpdated()
    {
        using var f = new DshFixture(packageJson: Package);
        var (id, row) = await AddAsync(f, fast: false);
        Assert.Equal(Models, ModelIdsOf(f, id));

        // An old component copy in the profile gets replaced.
        f.Fast.Installer.Install();
        File.WriteAllText(Path.Combine(f.PluginDir, "package.json"), "{ \"name\": \"dsh-account-manager-fast\", \"version\": \"0.1.0\" }");
        Assert.Equal(FastComponentState.Outdated, f.Fast.Installer.GetStatus().State);

        var saved = await f.Actions.UpdateManagedApiProviderAsync(row.CredentialId, id, "OpenAI", OpenAi, Models, null, true, null, true);

        Assert.True(saved.Complete, saved.Warning);
        Assert.Equal(ApiFastTwins.RestartNote, saved.FastNote);
        Assert.Equal(new[] { "gpt-6-astra", "gpt-6-astra-fast", "gpt-6.1-sol", "gpt-6.1-sol-fast", "gpt-5.5" }, ModelIdsOf(f, id));
        Assert.Equal(FastComponentState.Installed, f.Fast.Installer.GetStatus().State);
    }

    [Fact]
    public async Task ResavingAnOpenAiProviderWithoutTiersBackfillsThem()
    {
        using var f = new DshFixture();
        var id = AccountActions.NewApiProviderId();
        // A provider as an older version wrote it: Responses, official address, models without tiers.
        f.Backend.WriteApiProviderAsync(id, new JsonObject
        {
            ["name"] = "OpenAI",
            ["package"] = OpenAiApiProtocol.ResponsesPackage,
            ["settings"] = new JsonObject { ["baseURL"] = OpenAi },
            ["models"] = new JsonObject { ["gpt-6-astra"] = new JsonObject { ["name"] = "gpt-6-astra" }, ["gpt-5.5"] = new JsonObject { ["name"] = "gpt-5.5" } },
        }, "fake-key-1").GetAwaiter().GetResult();
        var row = (await f.Snapshot.GetSnapshotAsync()).Accounts.Single(a => a.Kind == AccountKind.Api);

        var saved = await f.Actions.UpdateManagedApiProviderAsync(row.CredentialId, id, "OpenAI", OpenAi, new[] { "gpt-6-astra", "gpt-5.5" }, null, true, null, false);

        Assert.True(saved.Complete, saved.Warning);
        var models = ProfilePatchDocument.Load(f.PatchPath).GetProviders()![id]!["models"]!.AsArray();
        Assert.Equal(5, models[0]!["reasoningEfforts"]!.AsObject().Count);
        Assert.Null(models[1]!["reasoningEfforts"]);
    }

    [Fact]
    public async Task TheSweepRemovesOurTwinsWhenTheComponentIsGoneAndLeavesThemWhileItIsThere()
    {
        using var f = new DshFixture(packageJson: Package);
        var (id, _) = await AddAsync(f, fast: true);
        Assert.Empty(f.Fast.SweepApiTwins());
        Assert.Equal(5, ModelIdsOf(f, id).Length);

        Directory.Delete(f.PluginDir, recursive: true);
        var swept = f.Fast.SweepApiTwins();

        Assert.Equal(new[] { id + "/gpt-6-astra-fast", id + "/gpt-6.1-sol-fast" }, swept.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(Models, ModelIdsOf(f, id));
    }

    [Fact]
    public async Task TheSweepKeepsATwinThatASettingStillPointsAt()
    {
        using var f = new DshFixture(packageJson: Package);
        var (id, _) = await AddAsync(f, fast: true);
        var text = f.Patch + "- id: some-plugin\n  config:\n    model:\n      provider: " + id + "\n      model: gpt-6-astra-fast\n";
        File.WriteAllText(f.PatchPath, text);
        Directory.Delete(f.PluginDir, recursive: true);

        var swept = f.Fast.SweepApiTwins();

        Assert.Equal(new[] { id + "/gpt-6.1-sol-fast" }, swept);
        Assert.Contains("gpt-6-astra-fast", ModelIdsOf(f, id));
    }

    [Fact]
    public async Task ChildAgentsAreNotOfferedTheApiTwins()
    {
        using var f = new DshFixture(packageJson: Package);
        var (id, _) = await AddAsync(f, fast: true);

        var routes = f.Subagents.ComputeCandidates(ProfilePatchDocument.Load(f.PatchPath));

        Assert.Contains(new SubagentRoute(id, "gpt-6-astra"), routes);
        Assert.DoesNotContain(routes, r => r.Model.EndsWith("-fast", StringComparison.Ordinal));
    }

    [Fact]
    public void FindTwinsNeedsAListedGpt6BaseTheOurNameShapeAndAQualifyingProvider()
    {
        var models = new JsonObject
        {
            ["gpt-6-astra"] = new JsonObject { ["name"] = "gpt-6-astra" },
            ["gpt-6-astra-fast"] = new JsonObject { ["name"] = "gpt-6-astra (Fast)" },
            ["gpt-5.5"] = new JsonObject { ["name"] = "gpt-5.5" },
            ["gpt-5.5-fast"] = new JsonObject { ["name"] = "gpt-5.5 (Fast)" },
            ["gpt-6-luna-fast"] = new JsonObject { ["name"] = "gpt-6-luna (Fast)" },
            ["gpt-6.1-sol"] = new JsonObject { ["name"] = "x" },
            ["gpt-6.1-sol-fast"] = new JsonObject { ["name"] = "mine" },
        };
        Assert.Equal(new[] { "gpt-6-astra-fast" }, ApiFastTwins.FindTwins(models, OpenAi, true));
        Assert.Empty(ApiFastTwins.FindTwins(models, OpenAi, false));
        Assert.Empty(ApiFastTwins.FindTwins(models, "https://gw.example.test/v1", true));
    }
}
