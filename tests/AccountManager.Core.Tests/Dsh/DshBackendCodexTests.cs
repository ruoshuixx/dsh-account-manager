using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using AccountManager.Core.Json;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

public class DshBackendCodexTests
{
    private const string Foreign = """
        # tools write here too
        version: 1
        refs:
          CODEX_OAUTH_1: '{"access":"third-party","refresh":"tp","expires":1}'
          # keep this comment
          ARK_CODING_API_KEY: fake-ark-key
        records:
          client-connection/browser-session:
            kind: grant
            payload:
              cookie: abc
              nested: {list: [1, 2, true], flag: false}
          openai/api:
            kind: api-key
            key: fake-openai-key
        x-foreign: 1

        """;

    private static JsonObject Cred(JsonArray all, string id) => all.OfType<JsonObject>().Single(c => c["id"]!.GetValue<string>() == id);

    private static bool Active(JsonObject c) => c["active"]!.GetValue<bool>();

    [Fact]
    public async Task EmptyHomeListsNothingAndCreatesNoFiles()
    {
        using var f = new DshFixture();

        var creds = await f.Backend.GetCredentialsAsync();

        Assert.Empty(creds);
        Assert.False(File.Exists(f.CredentialsPath));
        Assert.False(File.Exists(f.Vault.FilePath));
    }

    [Fact]
    public async Task FirstAccountBecomesCurrentAndTheGrantHasExactlyTheDshShape()
    {
        using var f = new DshFixture();

        var id = await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a");

        Assert.StartsWith("codex_", id);
        var grant = f.Grant!;
        Assert.Equal(new[] { "type", "access", "refresh", "expires", "accountId" }, grant.Select(kv => kv.Key).ToArray());
        Assert.Equal("oauth", grant["type"]!.GetValue<string>());
        Assert.Equal("refresh-a", grant["refresh"]!.GetValue<string>());
        Assert.Equal("acct-1", grant["accountId"]!.GetValue<string>());
        Assert.Equal(4102444800000L, grant["expires"]!.GetValue<long>());
        Assert.Contains("expires: 4102444800000", f.Credentials);
        var creds = await f.Backend.GetCredentialsAsync();
        Assert.True(Active(Cred(creds, id)));
    }

    [Fact]
    public async Task SecondAccountDoesNotTouchTheCurrentGrant()
    {
        using var f = new DshFixture();
        var a = await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a");
        var before = f.Credentials;

        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");

        Assert.Equal(before, f.Credentials);
        var creds = await f.Backend.GetCredentialsAsync();
        Assert.True(Active(Cred(creds, a)));
        Assert.False(Active(Cred(creds, b)));
    }

    [Fact]
    public async Task CredentialRoundTripsExactlyWhatWasCreated()
    {
        using var f = new DshFixture();
        var row = AccountManager.Core.Import.ImportService.ConvertAccount(TestAccounts.CockpitOauth("src-1", "a@example.test", "acct-1", "refresh-a"), "chatgpt-browser");
        row.Value["metadata"]!["subscriptionActiveUntil"] = "2027-01-01T00:00:00Z";

        var id = await f.Backend.CreateCredentialAsync("openai", "label", row.Value, activate: false, requestedId: "cred-xyz");

        Assert.Equal("cred-xyz", id);
        var stored = Cred(await f.Backend.GetCredentialsAsync(), id);
        Assert.True(JsonNode.DeepEquals(row.Value, stored["value"]));
        Assert.Equal("label", stored["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task SwitchWritesTheTargetGrantAndLeavesEveryForeignEntryByteIdentical()
    {
        using var f = new DshFixture(Foreign);
        var a = await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");
        var beforeSwitch = f.Credentials;

        await f.Backend.ActivateCredentialAsync(b);

        var grant = f.Grant!;
        Assert.Equal("refresh-b", grant["refresh"]!.GetValue<string>());
        Assert.Equal("acct-2", grant["accountId"]!.GetValue<string>());
        // Everything outside the one record is untouched.
        string WithoutGrant(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"  llm-pi-ai/openai-codex:\n(    .*\n)+", string.Empty);
        Assert.Equal(WithoutGrant(beforeSwitch), WithoutGrant(f.Credentials));
        Assert.Contains("  llm-pi-ai/openai-codex:\n", beforeSwitch);
        Assert.NotEqual(beforeSwitch, f.Credentials);
        Assert.Contains("# keep this comment", f.Credentials);
        Assert.Contains("CODEX_OAUTH_1: '{\"access\":\"third-party\",\"refresh\":\"tp\",\"expires\":1}'", f.Credentials);
        Assert.Contains("x-foreign: 1", f.Credentials);
        var creds = await f.Backend.GetCredentialsAsync();
        Assert.True(Active(Cred(creds, b)));
        Assert.False(Active(Cred(creds, a)));
        Assert.Equal(new[] { "ARK_CODING_API_KEY", "CODEX_OAUTH_1" }, f.CredentialsDoc.Refs.Select(r => r.Key).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task SwitchWritesBackTokensDshRefreshedBeforeLeavingTheAccount()
    {
        using var f = new DshFixture();
        var a = await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");
        // dsh refreshed account A's tokens in place: new refresh token, later expiry.
        var newAccess = TestAccounts.AccessToken("a@example.test", "acct-1", exp: 4102444900L);
        CredentialsDocument.Update(f.CredentialsPath, d => d.SetGrant(DshBackend.CodexGrantKey, new JsonObject
        {
            ["type"] = "oauth", ["access"] = newAccess, ["refresh"] = "refresh-a-rotated", ["expires"] = 4102444900000L, ["accountId"] = "acct-1",
        }));

        await f.Backend.ActivateCredentialAsync(b);

        var stored = f.Vault.FindById(a)!;
        Assert.Equal("refresh-a-rotated", VaultCredentialMapper.RefreshOf(stored));
        Assert.Equal(newAccess, VaultCredentialMapper.AccessOf(stored));
        Assert.Equal(4102444900000L, VaultCredentialMapper.ExpiresOf(stored));
        Assert.Equal("refresh-b", f.Grant!["refresh"]!.GetValue<string>());
        // Switching back restores the rotated tokens, not the stale ones.
        await f.Backend.ActivateCredentialAsync(a);
        Assert.Equal("refresh-a-rotated", f.Grant!["refresh"]!.GetValue<string>());
    }

    [Fact]
    public async Task SwitchNeverOverwritesNewerVaultTokensWithOlderGrantTokens()
    {
        using var f = new DshFixture();
        var a = await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a-new", activate: true, exp: 4102444900L);
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");
        // The live grant is older (earlier expiry) than the stored record of the same account.
        CredentialsDocument.Update(f.CredentialsPath, d => d.SetGrant(DshBackend.CodexGrantKey, new JsonObject
        {
            ["type"] = "oauth", ["access"] = TestAccounts.AccessToken("a@example.test", "acct-1", exp: 4102444800L), ["refresh"] = "refresh-a-new", ["expires"] = 4102444000000L, ["accountId"] = "acct-1",
        }));

        await f.Backend.ActivateCredentialAsync(b);

        Assert.Equal(4102444900000L, VaultCredentialMapper.ExpiresOf(f.Vault.FindById(a)!));
    }

    [Fact]
    public async Task ActiveAccountShowsTheFresherTokensDshHolds()
    {
        using var f = new DshFixture();
        var a = await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        var newAccess = TestAccounts.AccessToken("a@example.test", "acct-1", exp: 4102444900L);
        CredentialsDocument.Update(f.CredentialsPath, d => d.SetGrant(DshBackend.CodexGrantKey, new JsonObject
        {
            ["type"] = "oauth", ["access"] = newAccess, ["refresh"] = "refresh-a-2", ["expires"] = 4102444900000L, ["accountId"] = "acct-1",
        }));

        var cred = Cred(await f.Backend.GetCredentialsAsync(), a);

        Assert.True(Active(cred));
        Assert.Equal(newAccess, cred["value"]!["access"]!.GetValue<string>());
        Assert.Equal("refresh-a-2", cred["value"]!["refresh"]!.GetValue<string>());
        Assert.Equal(4102444900000L, cred["value"]!["expires"]!.GetValue<long>());
    }

    [Fact]
    public async Task ADshLoginThatIsNotInTheVaultIsAdoptedNotOverwritten()
    {
        using var f = new DshFixture();
        CredentialsDocument.Update(f.CredentialsPath, d => d.SetGrant(DshBackend.CodexGrantKey, new JsonObject
        {
            ["type"] = "oauth", ["access"] = TestAccounts.AccessToken("own@example.test", "acct-own"), ["refresh"] = "refresh-own", ["expires"] = 4102444800000L, ["accountId"] = "acct-own",
        }));

        var creds = await f.Backend.GetCredentialsAsync();

        var own = Assert.Single(creds)!.AsObject();
        Assert.True(Active(own));
        Assert.Equal("own@example.test", own["value"]!["metadata"]!["cockpitEmail"]!.GetValue<string>());
        Assert.Single(f.Vault.Load());

        // Reading again does not duplicate it, and switching away keeps it recoverable.
        Assert.Single(await f.Backend.GetCredentialsAsync());
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");
        await f.Backend.ActivateCredentialAsync(b);
        Assert.Equal("refresh-b", f.Grant!["refresh"]!.GetValue<string>());
        await f.Backend.ActivateCredentialAsync(own["id"]!.GetValue<string>());
        Assert.Equal("refresh-own", f.Grant!["refresh"]!.GetValue<string>());
    }

    [Fact]
    public async Task SameWorkspaceAccountsAreToldApartByTheirTokens()
    {
        using var f = new DshFixture();
        var a = await f.AddCodexAsync("a@example.test", "acct-team", "refresh-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-team", "refresh-b");

        var creds = await f.Backend.GetCredentialsAsync();
        Assert.True(Active(Cred(creds, a)));
        Assert.False(Active(Cred(creds, b)));

        await f.Backend.ActivateCredentialAsync(b);
        creds = await f.Backend.GetCredentialsAsync();
        Assert.True(Active(Cred(creds, b)));
        Assert.False(Active(Cred(creds, a)));
    }

    [Fact]
    public async Task DeletingTheCurrentAccountRemovesTheGrantAndNothingElse()
    {
        using var f = new DshFixture(Foreign);
        var a = await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");

        await f.Backend.DeleteCredentialAsync(a);

        Assert.Null(f.CredentialsDoc.GetRecord(DshBackend.CodexGrantKey));
        Assert.Null(f.Vault.FindById(a));
        Assert.NotNull(f.Vault.FindById(b));
        Assert.NotNull(f.CredentialsDoc.GetRecord("client-connection/browser-session"));
        Assert.Contains("# keep this comment", f.Credentials);
        var creds = await f.Backend.GetCredentialsAsync();
        Assert.All(creds.OfType<JsonObject>(), c => Assert.False(Active(c)));
    }

    [Fact]
    public async Task DeletingAnInactiveAccountKeepsTheGrant()
    {
        using var f = new DshFixture();
        var a = await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");
        var before = f.Credentials;

        await f.Backend.DeleteCredentialAsync(b);

        Assert.Equal(before, f.Credentials);
        Assert.Null(f.Vault.FindById(b));
        Assert.True(Active(Cred(await f.Backend.GetCredentialsAsync(), a)));
    }

    [Fact]
    public async Task InvalidCredentialsFileBlocksEveryOperationAndIsNeverModified()
    {
        const string legacy = "refs:\n  A_REF: unversioned-flat-layout\n";
        using var f = new DshFixture(legacy);
        var id = await f.Vault_AddOnlyAsync("a@example.test", "acct-1", "refresh-a");

        await Assert.ThrowsAnyAsync<DshException>(() => f.Backend.GetCredentialsAsync());
        await Assert.ThrowsAnyAsync<DshException>(() => f.Backend.ActivateCredentialAsync(id));
        await Assert.ThrowsAnyAsync<DshException>(() => f.Backend.DeleteCredentialAsync(id));

        Assert.Equal(legacy, f.Credentials);
        Assert.NotNull(f.Vault.FindById(id));
    }

    [Fact]
    public async Task ActivatingAnUnknownAccountFailsWithoutWriting()
    {
        using var f = new DshFixture();
        await Assert.ThrowsAsync<DshException>(() => f.Backend.ActivateCredentialAsync("missing"));
        Assert.False(File.Exists(f.CredentialsPath));
    }

    [Fact]
    public async Task ApiCredentialsAreNotCreatableThroughTheCodexBoundary()
    {
        using var f = new DshFixture();
        await Assert.ThrowsAsync<DshException>(() => f.Backend.CreateCredentialAsync("some-provider", "x",
            new JsonObject { ["type"] = "key", ["key"] = "k" }, true));
    }

    [Fact]
    public async Task ActivatingOnAFreshProfileEnablesTheBuiltInCodexRoute()
    {
        // A fresh profile (Windows desktop) lists no routes, so the grant alone shows no Codex models in dsh.
        using var f = new DshFixture();

        await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a");

        var providers = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!;
        Assert.Equal(new[] { "openai-codex" }, providers.Select(kv => kv.Key).ToArray());
        var models = providers["openai-codex"]!["models"]!.AsArray();
        Assert.Equal(SubagentRouteService.OfficialCodexModels, models.Select(m => m!["id"]!.GetValue<string>()).ToArray());
        Assert.All(models, m => Assert.Single(m!.AsObject())); // ids only
        Assert.Contains("      openai-codex:\n        models:\n          - id: gpt-6.1-sol\n", f.Patch);
    }

    [Fact]
    public async Task AnExistingCodexRouteAndOtherProvidersStayByteIdentical()
    {
        const string patch = "# mine\n- id: llm-pi-ai\n  config:\n    providers:\n      openai-codex:\n        models:\n          - id: gpt-x\n            name: GPT X\n      other:\n        api: openai-completions\n        baseURL: https://example.test/v1\n        models:\n          - id: m1\n";
        using var f = new DshFixture(patch: patch);

        await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a");

        Assert.Equal(patch, f.Patch);
    }

    [Fact]
    public async Task TheCodexRouteIsAddedBesideApiProvidersWithoutChangingThem()
    {
        const string patch = "- id: llm-pi-ai\n  config:\n    providers:\n      other:\n        api: openai-completions\n        baseURL: https://example.test/v1\n        models:\n          - id: m1\n";
        using var f = new DshFixture(patch: patch);

        await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a");

        var providers = ProfilePatchDocument.Load(f.PatchPath).GetProviders()!;
        Assert.True(providers.ContainsKey("openai-codex"));
        Assert.Equal("https://example.test/v1", providers["other"]!["baseURL"]!.GetValue<string>());
        Assert.StartsWith(patch, f.Patch); // the new route is appended after the existing providers
        Assert.Equal(SubagentRouteService.OfficialCodexModels, providers["openai-codex"]!["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToArray());
    }
}

internal static class DshFixtureExtensions
{
    /// <summary>Adds a vault account without touching dsh's files.</summary>
    public static Task<string> Vault_AddOnlyAsync(this DshFixture f, string email, string accountId, string refresh)
    {
        var row = AccountManager.Core.Import.ImportService.ConvertAccount(TestAccounts.AuthorizedAccount(email, accountId, refresh), "chatgpt-browser");
        var id = VaultCredentialMapper.NewCodexId();
        f.Vault.Upsert(VaultCredentialMapper.ToRecord(id, row.Label, row.Value, null));
        return Task.FromResult(id);
    }
}
