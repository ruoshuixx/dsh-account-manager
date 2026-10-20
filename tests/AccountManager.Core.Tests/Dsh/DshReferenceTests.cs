using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Actions;
using AccountManager.Core.Dsh;
using AccountManager.Core.Import;
using AccountManager.Core.Models;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

/// <summary>Model references (provider + model pairs) in dsh's settings: scanning, guarding, repairing, restoring.</summary>
public class DshReferenceTests
{
    private const string Credentials = "version: 1\nrefs:\n  ARK_CODING_API_KEY: fake-ark-key\n  OTHER_API_KEY: fake-other-key\n";

    /// <summary>Shaped like the real user layer: the Codex route, the Ark provider, and references to them.</summary>
    private const string Patch = """
        # user layer
        - id: llm-pi-ai
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
                  - id: deepseek-v4.1-flash
                  - id: deepseek-v4-pro
              other-gw:
                displayName: Other Gateway
                apiKeyEnv: OTHER_API_KEY
                api: openai-completions
                baseURL: https://gw.example.test/v1
                models:
                  - id: deepseek-v4.1-flash
                  - id: other-model
        - id: agent-default-model
          config:
            provider: volcengine-coding
            model: deepseek-v4.1-flash
        # who may be spawned
        - id: subagent-model-selection-settings
          config:
            allowedModels:
              - provider: volcengine-coding
                model: deepseek-v4.1-flash
              - provider: openai-codex
                model: gpt-5
              - provider: volcengine-coding
                model: deepseek-v4-pro
        - id: tool-subagent
          config:
            provider: spawn
            toolName: subagent

        """;

    /// <summary>The bundle layers of what <c>dsh --dump-config</c> would print; the fixture appends the current user layer.</summary>
    private const string Bundle = """
        # == @deepseek-ai/dsh-base
        - id: plugin-manager
          disabled: !!js '!ctx.get(''profileContext'')'
        - id: tool-subagent
          name: '@deepseek-ai/dsh-tool-subagent'
          config:
            provider: spawn
            toolName: subagent
        - id: llm-deepseek
          name: '@deepseek-ai/dsh-llm-deepseek-api-key'
        - id: llm-deepseek-account
          name: '@deepseek-ai/dsh-llm-deepseek-account'
        - id: bundle-only
          name: '@deepseek-ai/dsh-bundle-consumer'
          config:
            provider: deepseek-official
            model: deepseek-flash
        - id: bundle-dangling
          name: '@deepseek-ai/dsh-bundle-consumer'
          config:
            provider: gone-provider
            model: some-model
        """;

    // ---- scanner ----

    [Fact]
    public void UserLayerReferencesNeedBothProviderAndModelAndKnowListVersusSingle()
    {
        var refs = ReferenceScanner.ScanUserLayer(Patch);

        Assert.Equal(4, refs.Count);
        var single = refs.Single(r => r.EntryId == "agent-default-model");
        Assert.False(single.IsListItem);
        Assert.False(single.Removable);
        Assert.Equal("默认模型", single.Label);
        Assert.Equal(new[] { "1", "config" }, single.Path);
        var items = refs.Where(r => r.EntryId == "subagent-model-selection-settings").ToList();
        Assert.Equal(3, items.Count);
        Assert.All(items, r => { Assert.True(r.IsListItem); Assert.True(r.Removable); Assert.True(r.Editable); Assert.Equal("子代理模型选择", r.Label); });
        Assert.DoesNotContain(refs, r => r.Provider == "spawn"); // provider without a model is not a model reference
    }

    [Fact]
    public async Task ADeletedProviderLeavesDanglingReferencesAndBuiltInRoutesAreNeverDangling()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: Bundle);

        var scan = await f.References.ScanAsync();

        Assert.True(scan.ComposedAvailable);
        Assert.DoesNotContain(scan.Dangling, r => r.Provider != "gone-provider");
        var gone = Assert.Single(scan.Dangling);
        Assert.False(gone.Editable); // bundle layer: listed, cannot be changed by us
        Assert.Contains(scan.References, r => r.Provider == "deepseek-official" && !r.Dangling && !r.Editable);
        Assert.Contains("deepseek-official", scan.KnownProviders);
        Assert.Contains("deepseek-account", scan.KnownProviders);
        Assert.Contains("openai-codex", scan.KnownProviders); // configured in the user layer

        await f.Backend.RemoveApiProviderAsync("volcengine-coding");
        scan = await f.References.ScanAsync();
        Assert.Equal(4, scan.Dangling.Count);
        Assert.Equal(3, scan.Dangling.Count(r => r.Provider == "volcengine-coding" && r.Editable));
    }

    [Fact]
    public async Task CatalogRoutesOnlyExistWhenConfiguredSoACodexReferenceWithoutTheRouteIsDangling()
    {
        const string patch = "- id: agent-default-model\n  config:\n    provider: openai-codex\n    model: gpt-5\n";
        using var f = new DshFixture(null, patch);

        var scan = await f.References.ScanAsync();

        Assert.Single(scan.Dangling);
    }

    [Fact]
    public async Task WithoutDshOnlyTheUserLayerIsReadAndTheNoteSaysSo()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: null);

        var scan = await f.References.ScanAsync();

        Assert.False(scan.ComposedAvailable);
        Assert.Contains("可能有未检查到的引用", scan.Note);
        Assert.Equal(4, scan.References.Count);
        Assert.Empty(scan.Dangling);
    }

    [Fact]
    public async Task AnUnknownAdapterPluginMakesTheAnswerUncertainSoNothingIsCalledDangling()
    {
        var bundle = Bundle + "\n- id: mystery\n  name: '@someone/dsh-subscriptions'\n";
        using var f = new DshFixture(Credentials, Patch, composedBundle: bundle);

        var scan = await f.References.ScanAsync();

        Assert.True(scan.Uncertain);
        Assert.Empty(scan.Dangling);
        Assert.Contains("不认识", scan.Note);
    }

    private static readonly string BundleWithThirdPartyAdapter = Bundle
        + "\n- id: subscriptions\n  name: '@goodandready/dsh-subscriptions'\n"
        + "- id: uses-plugin-route\n  name: '@deepseek-ai/dsh-consumer'\n  config:\n    provider: codex\n    model: gpt-x\n";

    private async Task<DshFixture> DeletedByUsWithThirdPartyAdapterAsync()
    {
        var f = new DshFixture(Credentials, Patch, composedBundle: BundleWithThirdPartyAdapter);
        // What 0.1.0 did: back up (credentials + provider file), then delete the provider without looking at references.
        f.Backups.Save(await f.Backend.GetCredentialsAsync(), f.PatchPath, f.Patch);
        await f.Backend.RemoveApiProviderAsync("volcengine-coding");
        return f;
    }

    [Fact]
    public async Task AnUnknownAdapterDoesNotHideReferencesToAProviderThisAppDeleted()
    {
        using var f = await DeletedByUsWithThirdPartyAdapterAsync();

        var scan = await f.References.ScanAsync();

        Assert.True(scan.Uncertain);
        Assert.Equal(3, scan.Dangling.Count);
        Assert.All(scan.Dangling, r => Assert.Equal("volcengine-coding", r.Provider));
        Assert.All(scan.Dangling, r => Assert.True(r.Editable));
        // ... and they are repairable.
        var single = scan.Dangling.Single(r => !r.IsListItem);
        var item = scan.Dangling.First(r => r.IsListItem);
        await f.References.ApplyAsync(new[]
        {
            new ReferenceAction(single, ReferenceActionKind.Retarget, "other-gw", "deepseek-v4.1-flash"),
            new ReferenceAction(item, ReferenceActionKind.Retarget, "other-gw", "deepseek-v4.1-flash"),
        });
        Assert.Single((await f.References.ScanAsync()).Dangling);
    }

    [Fact]
    public async Task AnUnknownAdaptersOwnRouteIsNeverFlagged()
    {
        using var f = await DeletedByUsWithThirdPartyAdapterAsync();

        var scan = await f.References.ScanAsync();

        Assert.Contains(scan.References, r => r.Provider == "codex" && !r.Dangling && !r.Editable);
        Assert.Contains(scan.References, r => r.Provider == "gone-provider" && !r.Dangling); // neither a known route nor ours: not judged
    }

    [Fact]
    public async Task AProviderRecordedInTheMirrorIsFlaggedEvenWithAnUnknownAdapter()
    {
        using var f = new DshFixture(Credentials, "- id: llm-pi-ai\n  config:\n    providers: {}\n", composedBundle: BundleWithThirdPartyAdapter);
        await f.Import.ImportAsync(await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in.json", new JsonArray(ArkAccount(sourceId: "src-1")))));
        var providerId = Assert.Single(ProfilePatchDocument.Load(f.PatchPath).GetProviders()!).Key;
        Assert.StartsWith("cockpit-", providerId);
        // The provider disappears (edited away outside this app) while settings still point at it.
        await f.Backend.RemoveApiProviderAsync(providerId);
        File.WriteAllText(f.PatchPath, f.Patch + "- id: agent-default-model\n  config:\n    provider: " + providerId + "\n    model: deepseek-v4.1-flash\n"
            + "- id: other-entry\n  config:\n    provider: never-heard-of\n    model: m\n");

        var scan = await f.References.ScanAsync();

        Assert.Single(scan.Dangling, r => r.Provider == providerId);
        Assert.DoesNotContain(scan.Dangling, r => r.Provider == "never-heard-of");
    }

    [Fact]
    public async Task AProviderOfAnEarlierProviderFileBackupIsOursEvenIfNeverDeletedHere()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: BundleWithThirdPartyAdapter);
        f.Backups.Save(await f.Backend.GetCredentialsAsync(), f.PatchPath, f.Patch); // taken by some earlier write
        // The user removed it in dsh itself; the references stay.
        File.WriteAllText(f.PatchPath, f.Patch.Replace("      volcengine-coding:", "      renamed-elsewhere:"));

        var scan = await f.References.ScanAsync();

        Assert.Equal(3, scan.Dangling.Count(r => r.Provider == "volcengine-coding"));
    }

    [Fact]
    public void FlowStyleListItemsAreEditableButNotRemovable()
    {
        const string patch = "- id: x\n  config:\n    allowedModels: [{provider: a, model: b}]\n";

        var r = Assert.Single(ReferenceScanner.ScanUserLayer(patch));

        Assert.True(r.IsListItem);
        Assert.False(r.Removable);
    }

    // ---- editing the references in the file ----

    [Fact]
    public void RetargetingReplacesOnlyTheTwoScalars()
    {
        var doc = ProfilePatchDocument.Parse(Patch, "p");
        var r = doc.FindReferences().Single(x => x.EntryId == "agent-default-model");

        Assert.True(doc.RetargetReference(r, "other-gw", "other-model"));

        Assert.Equal(Patch.Replace("provider: volcengine-coding\n    model: deepseek-v4.1-flash\n# who may", "provider: other-gw\n    model: other-model\n# who may"), doc.Text);
        Assert.Contains("# user layer", doc.Text);
        Assert.Contains("# who may be spawned", doc.Text);
    }

    [Fact]
    public void RemovingAListItemRemovesItsLinesOnly()
    {
        var doc = ProfilePatchDocument.Parse(Patch, "p");
        var r = doc.FindReferences().Single(x => x.Model == "deepseek-v4-pro");

        Assert.True(doc.RemoveReference(r));

        var expected = Patch.Replace("      - provider: volcengine-coding\n        model: deepseek-v4-pro\n", string.Empty);
        Assert.Equal(expected, doc.Text);
        Assert.Equal(3, doc.FindReferences().Count);
    }

    [Fact]
    public void RemovingTheLastItemLeavesAnExplicitEmptyList()
    {
        const string patch = "- id: x\n  config:\n    allowedModels:\n      - provider: a\n        model: b\n  other: 1\n";
        var doc = ProfilePatchDocument.Parse(patch, "p");

        doc.RemoveReference(doc.FindReferences().Single());

        Assert.Equal("- id: x\n  config:\n    allowedModels: []\n  other: 1\n", doc.Text);
    }

    [Fact]
    public void StaleReferencesAndSingleValuesAreRefusedWithoutChangingTheText()
    {
        var doc = ProfilePatchDocument.Parse(Patch, "p");
        var single = doc.FindReferences().Single(x => x.EntryId == "agent-default-model");
        var stale = doc.FindReferences().First(x => x.IsListItem);
        doc.RetargetReference(stale, "other-gw", "other-model"); // someone changed it since the scan
        var before = doc.Text;

        Assert.Throws<DshException>(() => doc.RemoveReference(single));
        Assert.Throws<DshException>(() => doc.RetargetReference(stale, "x", "y"));

        Assert.Equal(before, doc.Text);
    }

    [Fact]
    public void CrLfAndQuotedValuesAreHandled()
    {
        var text = "- id: x\r\n  config:\r\n    provider: \"a\"\r\n    model: 'b'\r\n";
        var doc = ProfilePatchDocument.Parse(text, "p");

        doc.RetargetReference(doc.FindReferences().Single(), "cockpit-1", "m.1");

        Assert.Equal("- id: x\r\n  config:\r\n    provider: cockpit-1\r\n    model: m.1\r\n", doc.Text);
    }

    // ---- delete guard ----

    [Fact]
    public async Task DeletingAProviderThatIsStillReferencedIsRefusedUntilTheReferencesAreResolved()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: Bundle);
        var row = await ApiRow(f, "volcengine-coding");

        var ex = await Assert.ThrowsAsync<ReferencesInUseException>(() => f.Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider));

        Assert.Equal(3, ex.References.Count);
        Assert.Equal(Patch, f.Patch);
        Assert.Equal(Credentials, f.Credentials);
    }

    [Fact]
    public async Task ReplaceChoiceRetargetsEveryEditableReferenceThenDeletes()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: Bundle);
        var row = await ApiRow(f, "volcengine-coding");

        var result = await f.Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider, ReferenceResolution.ReplaceWith("other-gw", "other-model"));

        Assert.True(result.Removed, result.Warning);
        var refs = ReferenceScanner.ScanUserLayer(f.Patch);
        Assert.Equal(3, refs.Count); // two list items became the same pair, so one of them was dropped
        Assert.DoesNotContain(refs, r => r.Provider == "volcengine-coding");
        Assert.Equal(2, refs.Count(r => r.Provider == "other-gw" && r.Model == "other-model")); // the single value and one list item
        Assert.Single(refs, r => r.Provider == "openai-codex");
        var scan = await f.References.ScanAsync();
        Assert.DoesNotContain(scan.Dangling, r => r.Provider == "volcengine-coding");
        Assert.Null(ProfilePatchDocument.Load(f.PatchPath).GetProviders()!["volcengine-coding"]);
        Assert.Null(f.CredentialsDoc.GetRef("ARK_CODING_API_KEY"));
    }

    [Fact]
    public async Task RemoveChoiceIsRefusedWhileASingleValueReferenceExistsAndChangesNothing()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: Bundle);
        var row = await ApiRow(f, "volcengine-coding");

        await Assert.ThrowsAsync<DshException>(() => f.Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider, ReferenceResolution.RemoveItems()));

        Assert.Equal(Patch, f.Patch);
        Assert.Equal(Credentials, f.Credentials);
    }

    [Fact]
    public async Task RemoveChoiceDropsListItemsWhenNothingElseReferencesTheProvider()
    {
        const string patch = Patch0;
        using var f = new DshFixture(Credentials, patch, composedBundle: null);
        var row = await ApiRow(f, "volcengine-coding");

        var result = await f.Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider, ReferenceResolution.RemoveItems());

        Assert.True(result.Removed, result.Warning);
        var refs = ReferenceScanner.ScanUserLayer(f.Patch);
        Assert.Equal(new[] { "openai-codex" }, refs.Select(r => r.Provider).ToArray());
        Assert.Contains("# who may be spawned", f.Patch);
    }

    private const string Patch0 = """
        - id: llm-pi-ai
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
                  - id: deepseek-v4.1-flash
        # who may be spawned
        - id: subagent-model-selection-settings
          config:
            allowedModels:
              - provider: volcengine-coding
                model: deepseek-v4.1-flash
              - provider: openai-codex
                model: gpt-5

        """;

    [Fact]
    public async Task BundleLayerReferencesAreListedButCannotBeChangedByTheDeleteChoice()
    {
        const string patch = "- id: llm-pi-ai\n  config:\n    providers:\n      p1:\n        api: openai-completions\n        baseURL: https://p1.example.test/v1\n        apiKeyEnv: P1_KEY\n        models: [{id: m1}]\n      p2:\n        api: openai-completions\n        baseURL: https://p2.example.test/v1\n        models: [{id: m1}]\n";
        const string bundle = "- id: from-bundle\n  name: '@deepseek-ai/dsh-x'\n  config:\n    provider: p1\n    model: m1\n";
        using var f = new DshFixture("version: 1\nrefs:\n  P1_KEY: k\n", patch, composedBundle: bundle);
        var row = await ApiRow(f, "p1");

        var ex = await Assert.ThrowsAsync<ReferencesInUseException>(() => f.Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider));
        Assert.False(Assert.Single(ex.References).Editable);

        var result = await f.Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider, ReferenceResolution.ReplaceWith("p2", "m1"));
        Assert.True(result.Removed);
        Assert.Contains((await f.References.ScanAsync()).Dangling, r => r.EntryId == "from-bundle" && !r.Editable);
    }

    [Fact]
    public async Task RemovingModelsThatAreReferencedFollowsTheSameGuard()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: Bundle);
        var row = await ApiRow(f, "volcengine-coding");

        // Keep only deepseek-v4-pro: deepseek-v4.1-flash disappears (2 references use it).
        var ex = await Assert.ThrowsAsync<ReferencesInUseException>(() => f.Actions.UpdateManagedApiProviderAsync(row.CredentialId, row.Provider,
            null, "https://ark.cn-beijing.volces.com/api/coding/v3", new[] { "deepseek-v4-pro" }, null, null, null));
        Assert.Equal(2, ex.References.Count);
        Assert.Equal(Patch, f.Patch);

        var saved = await f.Actions.UpdateManagedApiProviderAsync(row.CredentialId, row.Provider,
            null, "https://ark.cn-beijing.volces.com/api/coding/v3", new[] { "deepseek-v4-pro" }, null, null, ReferenceResolution.ReplaceWith("volcengine-coding", "deepseek-v4-pro"));

        Assert.True(saved.Complete, saved.Warning);
        Assert.All(ReferenceScanner.ScanUserLayer(f.Patch).Where(r => r.Provider == "volcengine-coding"), r => Assert.Equal("deepseek-v4-pro", r.Model));
    }

    [Fact]
    public async Task ReplacementMustNotBeTheProviderBeingDeleted()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: Bundle);
        var row = await ApiRow(f, "volcengine-coding");

        await Assert.ThrowsAsync<DshException>(() => f.Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider, ReferenceResolution.ReplaceWith("volcengine-coding", "deepseek-v4-pro")));
    }

    // ---- undo from the backup ----

    [Fact]
    public async Task ADeletedProviderCanBeRestoredFromItsBackupWithIdKeyReferenceAndModels()
    {
        using var f = new DshFixture(Credentials, Patch0, composedBundle: null);
        var before = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!["volcengine-coding"]!.ToJsonString();
        var row = await ApiRow(f, "volcengine-coding");
        var result = await f.Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider, ReferenceResolution.RemoveItems());
        Assert.True(result.Removed);
        Assert.Null(f.CredentialsDoc.GetRef("ARK_CODING_API_KEY"));

        var backups = await f.References.FindBackupsContainingAsync("volcengine-coding");
        Assert.Contains(result.Backup, backups);
        await f.References.RestoreProviderAsync(result.Backup, "volcengine-coding");

        Assert.Equal(before, ProfilePatchDocument.Load(f.PatchPath).GetProviders()!["volcengine-coding"]!.ToJsonString());
        Assert.Equal("fake-ark-key", f.CredentialsDoc.GetRef("ARK_CODING_API_KEY"));
        Assert.Equal("fake-other-key", f.CredentialsDoc.GetRef("OTHER_API_KEY"));
        var creds = await f.Backend.GetCredentialsAsync();
        Assert.Contains(creds.OfType<JsonObject>(), c => c["id"]!.GetValue<string>() == "key_volcengine-coding");
    }

    [Fact]
    public async Task RestoreRefusesAnExistingProviderAndAForeignBackup()
    {
        using var f = new DshFixture(Credentials, Patch0);
        var row = await ApiRow(f, "volcengine-coding");
        var result = await f.Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider, ReferenceResolution.RemoveItems());
        await f.References.RestoreProviderAsync(result.Backup, "volcengine-coding");

        await Assert.ThrowsAsync<DshException>(() => f.References.RestoreProviderAsync(result.Backup, "volcengine-coding"));
        await Assert.ThrowsAsync<DshException>(() => f.References.RestoreProviderAsync(result.Backup, "never-existed"));
    }

    // ---- health check repairs ----

    [Fact]
    public async Task EachRepairOptionFixesItsReferenceAndTheRescanComesBackClean()
    {
        var bundle = Bundle.Replace("- id: bundle-dangling", "- id: bundle-fine").Replace("provider: gone-provider", "provider: other-gw").Replace("model: some-model", "model: other-model");
        using var f = new DshFixture(Credentials, Patch, composedBundle: bundle);
        await f.Backend.RemoveApiProviderAsync("volcengine-coding");
        var scan = await f.References.ScanAsync();
        var dangling = scan.Dangling.Where(r => r.Editable).ToList();
        Assert.Equal(3, dangling.Count);
        var single = dangling.Single(r => !r.IsListItem);
        var flash = dangling.Single(r => r.IsListItem && r.Model == "deepseek-v4.1-flash");
        var pro = dangling.Single(r => r.Model == "deepseek-v4-pro");

        // (a) same model on another provider, (c) remove a list item, (d) pick another provider and model for a single value.
        var choices = await f.References.GetProviderChoicesAsync();
        Assert.Contains(choices, c => c.ProviderId == "other-gw" && c.Models.Contains("deepseek-v4.1-flash"));
        var backup = await f.References.ApplyAsync(new[]
        {
            new ReferenceAction(flash, ReferenceActionKind.Retarget, "other-gw", "deepseek-v4.1-flash"),
            new ReferenceAction(pro, ReferenceActionKind.Remove),
            new ReferenceAction(single, ReferenceActionKind.Retarget, "other-gw", "other-model"),
        });

        Assert.True(File.Exists(backup));
        scan = await f.References.ScanAsync();
        Assert.Empty(scan.Dangling);
        var after = ReferenceScanner.ScanUserLayer(f.Patch);
        Assert.Equal(3, after.Count); // single, retargeted item, Codex item
        Assert.Equal("other-model", after.Single(r => !r.IsListItem).Model);
        Assert.Single(after, r => r.IsListItem && r.Provider == "other-gw" && r.Model == "deepseek-v4.1-flash");
    }

    [Fact]
    public async Task RemovingSeveralItemsOfOneListInOneRunKeepsTheIndexesRight()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: null);
        await f.Backend.RemoveApiProviderAsync("volcengine-coding");
        var items = (await f.References.ScanAsync()).Dangling.Where(r => r.IsListItem).ToList();
        Assert.Equal(2, items.Count);

        await f.References.ApplyAsync(items.Select(r => new ReferenceAction(r, ReferenceActionKind.Remove)).ToList());

        var after = ReferenceScanner.ScanUserLayer(f.Patch);
        Assert.Equal(new[] { "openai-codex", "volcengine-coding" }.OrderBy(x => x), after.Select(r => r.Provider).OrderBy(x => x));
    }

    [Fact]
    public async Task RetargetingThatCreatesADuplicateListItemDropsOnlyTheItemWeRetargeted()
    {
        const string patch = "- id: llm-pi-ai\n  config:\n    providers:\n      other-gw:\n        api: openai-completions\n        baseURL: https://gw.example.test/v1\n        models: [{id: m1}]\n"
            + "- id: subagent-model-selection-settings\n  config:\n    allowedModels:\n      - provider: gone\n        model: m1\n      - provider: other-gw\n        model: m1\n      - provider: user-dup\n        model: x\n      - provider: user-dup\n        model: x\n";
        using var f = new DshFixture(null, patch, composedBundle: null);
        var gone = (await f.References.ScanAsync()).Dangling.Where(r => r.Provider == "gone").ToList();
        Assert.Equal(3, (await f.References.ScanAsync()).Dangling.Count); // gone/m1 and the two unknown user-dup items
        await f.References.ApplyAsync(gone.Select(r => new ReferenceAction(r, ReferenceActionKind.Retarget, "other-gw", "m1")).ToList());

        var after = ReferenceScanner.ScanUserLayer(f.Patch);
        Assert.Single(after, r => r.Provider == "other-gw" && r.Model == "m1");
        Assert.Equal(2, after.Count(r => r.Provider == "user-dup")); // the user's own duplicates are never touched
        Assert.Contains("已移除改指向后重复的引用", f.RunLog);
    }

    [Fact]
    public async Task ARepairOfAChangedFileFailsCleanlyAndLeavesTheFileAlone()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: null);
        await f.Backend.RemoveApiProviderAsync("volcengine-coding");
        var dangling = (await f.References.ScanAsync()).Dangling;
        File.WriteAllText(f.PatchPath, File.ReadAllText(f.PatchPath).Replace("model: deepseek-v4-pro", "model: changed-meanwhile"));
        var before = f.Patch;

        await Assert.ThrowsAsync<DshException>(() => f.References.ApplyAsync(dangling.Select(r => new ReferenceAction(r, ReferenceActionKind.Retarget, "other-gw", "other-model")).ToList()));

        Assert.Equal(before, f.Patch);
    }

    // ---- import: matching, restoring and planned repairs ----

    private static JsonArray Backup(params JsonObject[] accounts) => new(accounts.Select(a => (JsonNode?)a).ToArray());

    private static JsonObject ArkAccount(string sourceId = "cockpit-src", string url = "https://ark.cn-beijing.volces.com/api/coding/v3", string key = "fake-ark-key",
        string wire = "chat_completions", params string[] models)
        => TestAccounts.CockpitApi(sourceId, "火山Codingplan (Cockpit)", url, wire, key, models.Length == 0 ? new[] { "deepseek-v4.1-flash", "glm-5.2" } : models);

    private static async Task<ImportResult> ImportAsync(DshFixture f, JsonArray backup, Action<ImportPreview>? tweak = null)
    {
        var preview = await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in" + Guid.NewGuid().ToString("N")[..4] + ".json", backup));
        tweak?.Invoke(preview);
        return await f.Import.ImportAsync(preview);
    }

    [Fact]
    public async Task ImportUpdatesTheProviderWithTheSameEndpointAndKeyInsteadOfCreatingADuplicate()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: Bundle);

        // Same URL (trailing slash, upper-case host) and key; Cockpit says chat_completions, dsh has responses.
        var result = await ImportAsync(f, Backup(ArkAccount(url: "https://ARK.cn-beijing.volces.com/api/coding/v3/")));

        Assert.Equal(0, result.Failed);
        var providers = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!;
        Assert.Equal(new[] { "openai-codex", "volcengine-coding", "other-gw" }, providers.Select(kv => kv.Key).ToArray());
        var updated = providers["volcengine-coding"]!.AsObject();
        Assert.Equal("火山Codingplan", updated["displayName"]!.GetValue<string>()); // name kept
        Assert.Equal("openai-responses", updated["api"]!.GetValue<string>());       // protocol kept
        Assert.Equal("ARK_CODING_API_KEY", updated["apiKeyEnv"]!.GetValue<string>());
        Assert.Equal(new[] { "deepseek-v4.1-flash", "deepseek-v4-pro", "glm-5.2" }, updated["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToArray());
        Assert.Equal("fake-ark-key", f.CredentialsDoc.GetRef("ARK_CODING_API_KEY"));
        Assert.Contains(result.Messages, m => m.Contains("相同") && m.Contains("volcengine-coding"));
        var scan = await f.References.ScanAsync();
        Assert.Equal(new[] { "gone-provider" }, scan.Dangling.Select(r => r.Provider).ToArray()); // only the bundle-layer one from the fixture dump
    }

    [Fact]
    public async Task ImportingTwiceDoesNotCreateAProviderEither()
    {
        using var f = new DshFixture(Credentials, Patch0);
        await ImportAsync(f, Backup(ArkAccount()));
        var once = f.Patch;

        await ImportAsync(f, Backup(ArkAccount()));

        Assert.Equal(once, f.Patch);
    }

    [Fact]
    public async Task AnotherKeyOnTheSameEndpointIsANewProvider()
    {
        using var f = new DshFixture(Credentials, Patch0);

        await ImportAsync(f, Backup(ArkAccount(key: "a-different-key")));

        var providers = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!;
        Assert.Equal(3, providers.Count);
        Assert.Contains(providers, kv => kv.Key.StartsWith("cockpit-"));
    }

    [Fact]
    public async Task ImportRestoresADeletedProviderUnderItsOldIdSoItsReferencesAreValidAgain()
    {
        using var f = new DshFixture(Credentials, Patch0);
        var row = await ApiRow(f, "volcengine-coding");
        await f.Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider, ReferenceResolution.ReplaceWith("openai-codex", "gpt-5"));
        // The dangling reference exists again for the scenario: point something at the deleted provider.
        var doc = ProfilePatchDocument.Load(f.PatchPath);
        File.WriteAllText(f.PatchPath, f.Patch + "- id: agent-default-model\n  config:\n    provider: volcengine-coding\n    model: deepseek-v4.1-flash\n");

        // The Cockpit account's provider id is the old provider id.
        var result = await ImportAsync(f, Backup(ArkAccount(sourceId: "volcengine-coding", key: "restored-key")));

        Assert.Equal(0, result.Failed);
        Assert.Contains(result.Messages, m => m.Contains("恢复") && m.Contains("volcengine-coding"));
        var providers = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!;
        Assert.NotNull(providers["volcengine-coding"]);
        Assert.DoesNotContain(providers, kv => kv.Key.StartsWith("cockpit-"));
        Assert.Empty((await f.References.ScanAsync()).Dangling);
        _ = doc;
    }

    [Fact]
    public async Task PreviewPlansReferenceRepairsAndOnlyCheckedOnesAreApplied()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: null);
        await f.Backend.RemoveApiProviderAsync("volcengine-coding");

        var preview = await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in.json", Backup(ArkAccount(key: "new-ark-key", models: new[] { "deepseek-v4.1-flash", "deepseek-v4-pro" }))));

        Assert.Equal(3, preview.Repairs.Count);
        Assert.Contains(preview.Repairs, r => r.Description.StartsWith("将修复：默认模型 volcengine-coding/deepseek-v4.1-flash → "));
        Assert.Contains(preview.Repairs, r => r.Description.StartsWith("将修复：子代理模型选择 volcengine-coding/deepseek-v4-pro → "));
        Assert.All(preview.Repairs, r => Assert.StartsWith("cockpit-", r.TargetProvider));

        // Unchecked: the account is imported but no reference is touched.
        preview.SelectedRepairKeys = new HashSet<string>();
        var skipped = await f.Import.ImportAsync(preview);
        Assert.DoesNotContain(skipped.Messages, m => m.StartsWith("已修复"));
        Assert.Equal(3, ReferenceScanner.ScanUserLayer(f.Patch).Count(r => r.Provider == "volcengine-coding"));
        Assert.Contains(skipped.Messages, m => m.Contains("仍有 3 处") && m.Contains("不存在的供应商"));
    }

    [Fact]
    public async Task CheckedRepairsAreAppliedAfterTheImportWritesAndReported()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: null);
        await f.Backend.RemoveApiProviderAsync("volcengine-coding");
        var preview = await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in.json", Backup(ArkAccount(key: "new-ark-key", models: new[] { "deepseek-v4.1-flash", "deepseek-v4-pro" }))));
        // Leave one repair unchecked.
        var skip = preview.Repairs.Single(r => r.Reference.IsListItem && r.Model == "deepseek-v4-pro");
        preview.SelectedRepairKeys = preview.Repairs.Where(r => r.Key != skip.Key).Select(r => r.Key).ToHashSet();

        var result = await f.Import.ImportAsync(preview);

        Assert.Equal(0, result.Failed);
        Assert.Equal(2, result.Messages.Count(m => m.StartsWith("已修复：")));
        var refs = ReferenceScanner.ScanUserLayer(f.Patch);
        Assert.Equal(2, refs.Count(r => r.Provider.StartsWith("cockpit-")));
        Assert.Single(refs, r => r.Provider == "volcengine-coding" && r.Model == "deepseek-v4-pro");
        Assert.Contains(result.Messages, m => m.Contains("仍有 1 处"));
    }

    [Fact]
    public async Task ImportReportsNoRemainingDanglingReferencesWhenEverythingIsRepaired()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: null);
        await f.Backend.RemoveApiProviderAsync("volcengine-coding");
        var preview = await f.Import.PreviewAsync(DshBackendApiTests.WriteJson(f, "in.json", Backup(ArkAccount(key: "new-ark-key", models: new[] { "deepseek-v4.1-flash", "deepseek-v4-pro" }))));

        var result = await f.Import.ImportAsync(preview);

        Assert.DoesNotContain(result.Messages, m => m.Contains("仍有"));
        Assert.Empty((await f.References.ScanAsync()).Dangling);
    }

    // ---- logging ----

    [Fact]
    public async Task SwitchingIsLoggedWithoutAnyToken()
    {
        using var f = new DshFixture();
        var a = await f.AddCodexAsync("a@example.test", "acct-aaaaaaaa", "refresh-secret-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-bbbbbbbb", "refresh-secret-b");
        var accessB = VaultCredentialMapper.AccessOf(f.Vault.FindById(b)!);
        // dsh refreshed A's tokens, so the switch also writes them back.
        var newAccess = TestAccounts.AccessToken("a@example.test", "acct-aaaaaaaa", exp: 4102444900L);
        CredentialsDocument.Update(f.CredentialsPath, d => d.SetGrant(DshBackend.CodexGrantKey, new JsonObject
        {
            ["type"] = "oauth", ["access"] = newAccess, ["refresh"] = "refresh-secret-a2", ["expires"] = 4102444900000L, ["accountId"] = "acct-aaaaaaaa",
        }));

        await f.Backend.ActivateCredentialAsync(b);
        await Assert.ThrowsAnyAsync<DshException>(() => f.Backend.ActivateCredentialAsync("missing"));

        var log = f.RunLog;
        Assert.Contains("切换到 b@example.test（bbbbbbbb）", log);
        Assert.Contains("已把 dsh 刷新后的令牌写回账号库", log);
        Assert.Contains("切换账号失败", log);
        Assert.DoesNotContain("refresh-secret", log);
        Assert.DoesNotContain(newAccess, log);
        Assert.DoesNotContain(accessB, log);
        _ = a;
    }

    [Fact]
    public async Task RepairsAndDeletionsAreLogged()
    {
        using var f = new DshFixture(Credentials, Patch, composedBundle: null);
        await f.Backend.RemoveApiProviderAsync("volcengine-coding");
        var single = (await f.References.ScanAsync()).Dangling.Single(r => !r.IsListItem);

        await f.References.ApplyAsync(new[] { new ReferenceAction(single, ReferenceActionKind.Retarget, "other-gw", "other-model") });

        Assert.Contains("模型引用已改指向：默认模型 · volcengine-coding/deepseek-v4.1-flash → other-gw/other-model", f.RunLog);
    }

    private static async Task<AccountRow> ApiRow(DshFixture f, string providerId)
        => (await f.Snapshot.GetSnapshotAsync()).Accounts.Single(a => a.Kind == AccountKind.Api && a.Provider == providerId);
}
