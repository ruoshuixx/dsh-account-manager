using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Actions;
using AccountManager.Core.Dsh;
using AccountManager.Core.Models;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

/// <summary>The upper-layer services (snapshot, switch, delete, add, import, export) running on the real dsh backend.</summary>
public class DshServicesTests
{
    private static JsonArray SyntheticBackup() => new(
        TestAccounts.CockpitOauth("cockpit-a", "a@example.test", "acct-a", "refresh-a"),
        TestAccounts.CockpitOauth("cockpit-b", "b@example.test", "acct-b", "refresh-b", plan: "team"),
        TestAccounts.CockpitOauth("cockpit-c", "c@example.test", "acct-team", "refresh-c"),
        TestAccounts.CockpitOauth("cockpit-d", "d@example.test", "acct-team", "refresh-d"),
        TestAccounts.CockpitApi("api-src-1", "Volc Coding", "https://ark.cn-beijing.volces.com/api/coding/v3", "responses", "fake-ark-1", "deepseek-v4-pro", "glm-5.2"),
        TestAccounts.CockpitApi("api-src-2", "Local Gateway", "https://gw.example.test/v1", "chat_completions", "fake-gw-2", "model-x"));

    private static async Task ImportAllAsync(DshFixture f, JsonArray backup)
    {
        var path = DshBackendApiTests.WriteJson(f, "backup.json", backup);
        var preview = await f.Import.PreviewAsync(path);
        var result = await f.Import.ImportAsync(preview);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task ImportFillsTheVaultAndProvidersWithoutActivatingAnythingExceptTheFirstAccount()
    {
        using var f = new DshFixture("version: 1\nrefs:\n  CODEX_OAUTH_1: '{\"x\":1}'\n", "[]\n");

        await ImportAllAsync(f, SyntheticBackup());

        Assert.Equal(4, f.Vault.Load().Count);
        var snapshot = await f.Snapshot.GetSnapshotAsync();
        Assert.Equal(4, snapshot.Accounts.Count(a => a.Kind == AccountKind.Codex));
        Assert.Equal(2, snapshot.Accounts.Count(a => a.Kind == AccountKind.Api));
        Assert.Equal(2, snapshot.ProviderCount);
        Assert.Single(snapshot.Accounts, a => a.Active && a.Kind == AccountKind.Codex);
        Assert.Equal("a@example.test", snapshot.Accounts.Single(a => a.Kind == AccountKind.Codex && a.Active).Email);
        // Foreign refs survive, API keys landed in refs, providers in the patch.
        Assert.Equal("{\"x\":1}", f.CredentialsDoc.GetRef("CODEX_OAUTH_1"));
        var providers = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!;
        Assert.Equal(3, providers.Count); // two API providers and the built-in Codex route the first activation enabled
        Assert.Equal(SubagentRouteService.OfficialCodexModels,
            providers[ProviderViewMapper.CodexRouteId]!["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToArray());
        var volc = providers[DshBackend.CockpitProviderId("api-src-1")]!.AsObject();
        Assert.Equal("openai-responses", volc["api"]!.GetValue<string>());
        Assert.Equal("Volc Coding", volc["displayName"]!.GetValue<string>());
        Assert.Equal("fake-ark-1", f.CredentialsDoc.GetRef(volc["apiKeyEnv"]!.GetValue<string>()));
        Assert.Equal("low", volc["models"]![0]!["reasoningEfforts"]!["low"]!.GetValue<string>());
        Assert.Equal(new[] { "api-src-1", "api-src-2" }.Select(DshBackend.CockpitProviderId).OrderBy(x => x).ToArray(),
            snapshot.Accounts.Where(a => a.Kind == AccountKind.Api).Select(a => a.Provider).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task ImportingTheSameBackupAgainChangesNothing()
    {
        using var f = new DshFixture(null, "[]\n");
        await ImportAllAsync(f, SyntheticBackup());
        var credentials = f.Credentials;
        var patch = f.Patch;
        var vaultIds = f.Vault.Load().Select(a => a["id"]!.GetValue<string>()).ToArray();

        await ImportAllAsync(f, SyntheticBackup());

        Assert.Equal(credentials, f.Credentials);
        Assert.Equal(patch, f.Patch);
        Assert.Equal(vaultIds, f.Vault.Load().Select(a => a["id"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task ReimportWithNewerTokensUpdatesInPlaceAndOlderTokensAreKept()
    {
        using var f = new DshFixture(null, "[]\n");
        await ImportAllAsync(f, new JsonArray(TestAccounts.CockpitOauth("cockpit-a", "a@example.test", "acct-a", "refresh-1", exp: 4102444800L)));
        var id = f.Vault.Load().Single()["id"]!.GetValue<string>();

        await ImportAllAsync(f, new JsonArray(TestAccounts.CockpitOauth("cockpit-a", "a@example.test", "acct-a", "refresh-2", exp: 4102444900L)));
        var updated = f.Vault.Load().Single();
        Assert.Equal("refresh-2", VaultCredentialMapper.RefreshOf(updated));
        Assert.Equal("refresh-2", f.Grant!["refresh"]!.GetValue<string>()); // it was the current account, so it stays current

        await ImportAllAsync(f, new JsonArray(TestAccounts.CockpitOauth("cockpit-a", "a@example.test", "acct-a", "refresh-0", exp: 4102444000L)));
        Assert.Equal("refresh-2", VaultCredentialMapper.RefreshOf(f.Vault.Load().Single()));
        Assert.NotEqual(id, string.Empty);
    }

    [Fact]
    public async Task ImportAndExportAgreeOnEveryBusinessField()
    {
        using var f = new DshFixture(null, "[]\n");
        var backup = SyntheticBackup();
        await ImportAllAsync(f, backup);
        var snapshot = await f.Snapshot.GetSnapshotAsync();
        var path = Path.Combine(f.Root, "exported.json");

        var result = await f.Export.ExportAsync(snapshot.Accounts.Select(a => a.CredentialId).ToList(), path);

        Assert.Equal(6, result.Count);
        var exported = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        foreach (var original in backup.OfType<JsonObject>())
        {
            var mode = original["auth_mode"]!.GetValue<string>();
            JsonObject? match;
            if (mode == "oauth")
            {
                match = exported.OfType<JsonObject>().SingleOrDefault(e => e["account_id"]?.GetValue<string>() == original["account_id"]!.GetValue<string>()
                    && e["email"]?.GetValue<string>() == original["email"]!.GetValue<string>());
                Assert.NotNull(match);
                Assert.Equal(original["tokens"]!["access_token"]!.GetValue<string>(), match!["tokens"]!["access_token"]!.GetValue<string>());
                Assert.Equal(original["tokens"]!["refresh_token"]!.GetValue<string>(), match["tokens"]!["refresh_token"]!.GetValue<string>());
                Assert.Equal(original["tokens"]!["id_token"]!.GetValue<string>(), match["tokens"]!["id_token"]!.GetValue<string>());
                Assert.Equal(original["plan_type"]!.GetValue<string>(), match["plan_type"]!.GetValue<string>());
                Assert.Equal(original["id"]!.GetValue<string>(), match["id"]!.GetValue<string>());
            }
            else
            {
                match = exported.OfType<JsonObject>().SingleOrDefault(e => e["api_provider_id"]?.GetValue<string>() == original["api_provider_id"]!.GetValue<string>());
                Assert.NotNull(match);
                foreach (var field in new[] { "openai_api_key", "api_base_url", "api_provider_name", "api_wire_api" })
                {
                    Assert.Equal(original[field]!.GetValue<string>(), match![field]!.GetValue<string>());
                }

                Assert.Equal(original["api_model_catalog"]!.ToJsonString(), match!["api_model_catalog"]!.ToJsonString());
            }
        }
    }

    [Fact]
    public async Task ExportReflectsTokensDshRefreshedSinceTheImport()
    {
        using var f = new DshFixture(null, "[]\n");
        await ImportAllAsync(f, new JsonArray(TestAccounts.CockpitOauth("cockpit-a", "a@example.test", "acct-a", "refresh-1")));
        var newAccess = TestAccounts.AccessToken("a@example.test", "acct-a", exp: 4102444900L);
        CredentialsDocument.Update(f.CredentialsPath, d => d.SetGrant(DshBackend.CodexGrantKey, new JsonObject
        {
            ["type"] = "oauth", ["access"] = newAccess, ["refresh"] = "refresh-rotated", ["expires"] = 4102444900000L, ["accountId"] = "acct-a",
        }));
        var row = (await f.Snapshot.GetSnapshotAsync()).Accounts.Single();
        var path = Path.Combine(f.Root, "exported.json");

        await f.Export.ExportAsync(new[] { row.CredentialId }, path);

        var exported = JsonNode.Parse(File.ReadAllText(path))!.AsArray().Single()!;
        Assert.Equal("refresh-rotated", exported["tokens"]!["refresh_token"]!.GetValue<string>());
        Assert.Equal(newAccess, exported["tokens"]!["access_token"]!.GetValue<string>());
    }

    [Fact]
    public async Task SwitchServiceActivatesAndReadsBack()
    {
        using var f = new DshFixture();
        await ImportAllAsync(f, SyntheticBackup());
        var accounts = (await f.Snapshot.GetSnapshotAsync()).Accounts;
        var target = accounts.Single(a => a.Email == "b@example.test");

        var first = await f.Switch.SwitchAsync(target.CredentialId);
        var again = await f.Switch.SwitchAsync(target.CredentialId);

        Assert.True(first.Changed);
        Assert.True(first.Active);
        Assert.False(again.Changed);
        Assert.Equal("refresh-b", f.Grant!["refresh"]!.GetValue<string>());
        Assert.Equal("acct-b", f.Grant["accountId"]!.GetValue<string>());
        await Assert.ThrowsAnyAsync<DshException>(() => f.Switch.SwitchAsync("missing"));
        await Assert.ThrowsAnyAsync<DshException>(() => f.Switch.SwitchAsync(accounts.First(a => a.Kind == AccountKind.Api).CredentialId));
    }

    [Fact]
    public async Task DeletingTheCurrentCodexAccountIsAllowedAndExplainsWhatHappened()
    {
        using var f = new DshFixture();
        await ImportAllAsync(f, SyntheticBackup());
        var current = (await f.Snapshot.GetSnapshotAsync()).Accounts.Single(a => a.Kind == AccountKind.Codex && a.Active);

        var result = await f.Actions.RemoveManagedAccountAsync(current.CredentialId, current.Provider);

        Assert.True(result.Removed);
        Assert.Contains("没有可用的 Codex 账号", result.Warning);
        Assert.False(string.IsNullOrEmpty(result.Backup));
        Assert.True(File.Exists(result.Backup));
        Assert.Null(f.CredentialsDoc.GetRecord(DshBackend.CodexGrantKey));
        Assert.Equal(3, f.Vault.Load().Count);
    }

    [Fact]
    public async Task DeletingAnInactiveCodexAccountLeavesTheCurrentOneAlone()
    {
        using var f = new DshFixture();
        await ImportAllAsync(f, SyntheticBackup());
        var accounts = (await f.Snapshot.GetSnapshotAsync()).Accounts;
        var other = accounts.First(a => a.Kind == AccountKind.Codex && !a.Active);
        var grantBefore = f.Grant!.ToJsonString();

        var result = await f.Actions.RemoveManagedAccountAsync(other.CredentialId, other.Provider);

        Assert.True(result.Removed);
        Assert.Equal(string.Empty, result.Warning);
        Assert.Equal(grantBefore, f.Grant!.ToJsonString());
        Assert.Equal(3, f.Vault.Load().Count);
    }

    [Fact]
    public async Task DeletingAnApiAccountRemovesTheProviderAndItsKey()
    {
        using var f = new DshFixture();
        await ImportAllAsync(f, SyntheticBackup());
        var api = (await f.Snapshot.GetSnapshotAsync()).Accounts.First(a => a.Kind == AccountKind.Api);
        var env = ProfilePatchDocument.Load(f.PatchPath).GetProviders()![api.Provider]!["apiKeyEnv"]!.GetValue<string>();

        var result = await f.Actions.RemoveManagedAccountAsync(api.CredentialId, api.Provider);

        Assert.True(result.Removed, result.Warning);
        Assert.Null(f.CredentialsDoc.GetRef(env));
        Assert.DoesNotContain(api.Provider, f.Patch);
        Assert.Equal(1, (await f.Snapshot.GetSnapshotAsync()).Accounts.Count(a => a.Kind == AccountKind.Api));
    }

    [Fact]
    public async Task ApiProviderCanBeCreatedEditedAndRotatedThroughTheActions()
    {
        using var f = new DshFixture("version: 1\nrefs:\n  KEEP_ME: yes-keep\n", "[]\n");
        var id = AccountActions.NewApiProviderId();

        var added = await f.Actions.CreateManagedApiProviderAsync(id, "My Gateway", "https://gw.example.test/v1", new[] { "m1", "m2" }, "fake-key-1");
        Assert.True(added.Saved && added.Complete, added.Warning);
        Assert.Equal("key_" + id, added.CredentialID);

        var row = (await f.Snapshot.GetSnapshotAsync()).Accounts.Single(a => a.Kind == AccountKind.Api);
        Assert.Equal("My Gateway", row.ProviderName);
        Assert.Equal(new[] { "m1", "m2" }, row.ModelIds);
        Assert.Equal("https://gw.example.test/v1", row.BaseUrl);
        Assert.Equal(OpenAiApiProtocol.ChatPackage, row.Definition!.Package);

        var edited = await f.Actions.UpdateManagedApiProviderAsync(row.CredentialId, row.Provider, "Renamed",
            "https://gw2.example.test/v1", new[] { "m2", "m3" }, null, useResponses: true);
        Assert.True(edited.Updated && edited.Complete, edited.Warning);
        var providers = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!;
        var written = providers[id]!.AsObject();
        Assert.Equal("Renamed", written["displayName"]!.GetValue<string>());
        Assert.Equal("openai-responses", written["api"]!.GetValue<string>());
        Assert.Equal(new[] { "m2", "m3" }, written["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToArray());
        Assert.Equal("fake-key-1", f.CredentialsDoc.GetRef(written["apiKeyEnv"]!.GetValue<string>()));

        var rotated = await f.Actions.UpdateManagedApiProviderAsync(row.CredentialId, row.Provider, null,
            "https://gw2.example.test/v1", new[] { "m2", "m3" }, "fake-key-2");
        Assert.True(rotated.Complete, rotated.Warning);
        Assert.Equal("fake-key-2", f.CredentialsDoc.GetRef(written["apiKeyEnv"]!.GetValue<string>()));
        Assert.Equal("yes-keep", f.CredentialsDoc.GetRef("KEEP_ME"));
        Assert.All(new[] { added.Backup, edited.Backup, rotated.Backup }, b => Assert.True(File.Exists(b)));

        var removed = await f.Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider);
        Assert.True(removed.Removed);
        Assert.Equal("yes-keep", f.CredentialsDoc.GetRef("KEEP_ME"));
        Assert.Null(ProfilePatchDocument.Load(f.PatchPath).GetProviders()?[id]);
    }

    [Fact]
    public async Task ApiValidationRejectsUnsafeInputsBeforeAnythingIsWritten()
    {
        using var f = new DshFixture(null, "[]\n");

        await Assert.ThrowsAsync<DshException>(() => f.Actions.CreateManagedApiProviderAsync("not-a-valid-id", "n", "https://x.example.test/v1", new[] { "m" }, "k"));
        await Assert.ThrowsAsync<DshException>(() => f.Actions.CreateManagedApiProviderAsync(AccountActions.NewApiProviderId(), "n", "http://x.example.test/v1", new[] { "m" }, "k"));
        await Assert.ThrowsAsync<DshException>(() => f.Actions.CreateManagedApiProviderAsync(AccountActions.NewApiProviderId(), "n", "https://x.example.test/v1", new[] { "m" }, " "));

        Assert.Equal("[]\n", f.Patch);
        Assert.False(File.Exists(f.CredentialsPath));
    }

    [Fact]
    public async Task AddingACodexAccountKeepsTheCurrentOneAndFirstAccountBecomesCurrent()
    {
        using var f = new DshFixture();

        var first = await AuthorizeAsync(f, "first@example.test", "acct-1", "refresh-1");
        Assert.True(first.Saved && first.Complete, first.Warning);
        Assert.Contains("当前账号", first.Warning);
        Assert.Equal("refresh-1", f.Grant!["refresh"]!.GetValue<string>());

        var second = await AuthorizeAsync(f, "second@example.test", "acct-2", "refresh-2");
        Assert.True(second.Saved && second.Complete, second.Warning);
        Assert.Equal(string.Empty, second.Warning);
        Assert.Equal("refresh-1", f.Grant!["refresh"]!.GetValue<string>());
        Assert.Equal(2, f.Vault.Load().Count);
    }

    [Fact]
    public async Task AuthorizingAnExistingAccountRequiresConfirmationAndReplacesIt()
    {
        using var f = new DshFixture();
        await AuthorizeAsync(f, "first@example.test", "acct-1", "refresh-1");

        var preview = await f.Actions.PreviewCodexAuthorizationAsync(TestAccounts.AuthorizedAccount("first@example.test", "acct-1", "refresh-1b"));
        Assert.True(preview.HasExistingAccount);
        await Assert.ThrowsAsync<DshException>(() => f.Actions.SaveCodexAuthorizationAsync(preview, replaceExisting: false));
        var saved = await f.Actions.SaveCodexAuthorizationAsync(preview, replaceExisting: true);

        Assert.True(saved.Complete, saved.Warning);
        var record = Assert.Single(f.Vault.Load());
        Assert.Equal("refresh-1b", VaultCredentialMapper.RefreshOf(record));
        Assert.Equal("refresh-1b", f.Grant!["refresh"]!.GetValue<string>()); // the replaced account was the current one
    }

    private static async Task<AccountAddResult> AuthorizeAsync(DshFixture f, string email, string accountId, string refresh)
    {
        var preview = await f.Actions.PreviewCodexAuthorizationAsync(TestAccounts.AuthorizedAccount(email, accountId, refresh));
        return await f.Actions.SaveCodexAuthorizationAsync(preview, replaceExisting: false);
    }

    [Fact]
    public async Task SnapshotFailsLoudlyOnAnInvalidCredentialsFileAndReportsAnInvalidPatchAsAWarning()
    {
        using var broken = new DshFixture("refs:\n  A: flat\n", "[]\n");
        await Assert.ThrowsAnyAsync<DshException>(() => broken.Snapshot.GetSnapshotAsync());
        Assert.Equal("refs:\n  A: flat\n", broken.Credentials);

        using var badPatch = new DshFixture("version: 1\n", "id: nope\n");
        await badPatch.AddCodexAsync("a@example.test", "acct-1", "refresh-a");
        var snapshot = await badPatch.Snapshot.GetSnapshotAsync();
        Assert.Single(snapshot.Accounts);
        Assert.False(string.IsNullOrEmpty(snapshot.Warning));
    }

    [Fact]
    public async Task EveryDestructiveOperationWritesAnEncryptedBackupFirst()
    {
        using var f = new DshFixture();
        await ImportAllAsync(f, SyntheticBackup());
        var api = (await f.Snapshot.GetSnapshotAsync()).Accounts.First(a => a.Kind == AccountKind.Api);

        var result = await f.Actions.RemoveManagedAccountAsync(api.CredentialId, api.Provider);

        var bytes = File.ReadAllBytes(result.Backup);
        Assert.DoesNotContain("fake-ark-1", System.Text.Encoding.UTF8.GetString(bytes));
        var plain = f.Backups.Read(result.Backup)!;
        Assert.Contains("configText", plain.ToJsonString());
        Assert.Contains(api.Provider, plain["configText"]!.GetValue<string>());
    }
}
