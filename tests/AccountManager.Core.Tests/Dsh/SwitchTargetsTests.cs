using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Dsh;
using AccountManager.Core.OpenCode;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

/// <summary>Switching with OpenCode as the target, absorbing before a switch, and the cross-target bookkeeping. Fakes only.</summary>
public class SwitchTargetsTests
{
    /// <summary>Accounts A (current in dsh) and B in the vault; the fake OpenCode is available and empty.</summary>
    private static async Task<(DshFixture F, string A, string B)> SetupAsync(SwitchTarget target)
    {
        var f = new DshFixture();
        var a = await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");
        f.UseOpenCode();
        f.Target = target;
        return (f, a, b);
    }

    private static string Refresh(JsonObject credential) => credential["value"]!["refresh"]!.GetValue<string>();

    // ---- OpenCode target ----------------------------------------------------

    [Fact]
    public async Task OpenCodeTargetCreatesTheCredentialWithActivateAndTouchesNothingElse()
    {
        var (f, _, b) = await SetupAsync(SwitchTarget.OpenCode);
        using var _f = f;
        f.UseCli(authJson: DshFixture.AuthJson("a@example.test", "acct-1", "refresh-a"));
        var dshBefore = File.ReadAllBytes(f.CredentialsPath);
        var patchBefore = File.ReadAllText(f.PatchPath);
        var codexBefore = File.ReadAllBytes(f.CodexAuthPath);

        var result = await f.Switch.SwitchAsync(b);

        Assert.Equal(SwitchTarget.OpenCode, result.Target);
        Assert.True(result.Changed);
        var credential = Assert.Single(f.OpenCodeApi.Credentials);
        Assert.Equal(f.OpenCodeApi.ActiveId, credential["id"]!.GetValue<string>());
        Assert.Equal("openai", credential["integrationID"]!.GetValue<string>());
        Assert.Equal("refresh-b", Refresh(credential));
        Assert.Equal("chatgpt-browser", credential["value"]!["methodID"]!.GetValue<string>());
        Assert.Equal("acct-2", credential["value"]!["metadata"]!["accountID"]!.GetValue<string>());
        Assert.Contains("create:openai:True", f.OpenCodeApi.Calls);
        // dsh and Codex are exactly as they were.
        Assert.Equal(dshBefore, File.ReadAllBytes(f.CredentialsPath));
        Assert.Equal(patchBefore, File.ReadAllText(f.PatchPath));
        Assert.Equal(codexBefore, File.ReadAllBytes(f.CodexAuthPath));
        Assert.Empty(f.Backups.ListBackups());
    }

    [Fact]
    public async Task AMissingOpenCodeFailsClearlyAndChangesNothing()
    {
        var (f, _, b) = await SetupAsync(SwitchTarget.OpenCode);
        using var _f = f;
        f.OpenCodeApi.Unavailable = true;
        // Newer Codex tokens would be absorbed by a switch; a failed OpenCode switch must not even do that.
        f.UseCli(authJson: DshFixture.AuthJson("a@example.test", "acct-1", "refresh-a-rotated", exp: 4102444900L));
        var dshBefore = File.ReadAllBytes(f.CredentialsPath);
        var vaultBefore = File.ReadAllBytes(f.Vault.FilePath);
        var codexBefore = File.ReadAllBytes(f.CodexAuthPath);

        var ex = await Assert.ThrowsAsync<OpenCodeException>(() => f.Switch.SwitchAsync(b));

        Assert.Contains("OpenCode", ex.Message);
        Assert.Contains("找不到", ex.Message);
        Assert.Equal(dshBefore, File.ReadAllBytes(f.CredentialsPath));
        Assert.Equal(vaultBefore, File.ReadAllBytes(f.Vault.FilePath));
        Assert.Equal(codexBefore, File.ReadAllBytes(f.CodexAuthPath));
        Assert.Empty(f.Backups.ListBackups());
    }

    [Fact]
    public async Task ASameAccountCredentialWithOlderTokensIsReplacedByTheVaultsAndActivated()
    {
        var (f, a, _) = await SetupAsync(SwitchTarget.OpenCode);
        using var _f = f;
        var old = f.OpenCodeApi.AddLogin("a@example.test", "acct-1", "refresh-a-old", active: false, exp: 4000000000L);
        var other = f.OpenCodeApi.AddLogin("z@example.test", "acct-7", "refresh-z", active: true);

        var result = await f.Switch.SwitchAsync(a);

        Assert.True(result.Changed);
        Assert.Null(f.OpenCodeApi.Find(old["id"]!.GetValue<string>()));
        var active = f.OpenCodeApi.Find(f.OpenCodeApi.ActiveId!)!;
        Assert.Equal("refresh-a", Refresh(active));
        Assert.Equal(2, f.OpenCodeApi.Credentials.Count);
        Assert.False(f.OpenCodeApi.Find(other["id"]!.GetValue<string>())!["active"]!.GetValue<bool>());
        Assert.Equal("refresh-a", VaultCredentialMapper.RefreshOf(f.Vault.FindById(a)!)); // the older OpenCode tokens never reached the vault
    }

    [Fact]
    public async Task ANewerOpenCodeCredentialIsAbsorbedIntoTheVaultAndJustActivated()
    {
        var (f, _, b) = await SetupAsync(SwitchTarget.OpenCode);
        using var _f = f;
        var theirs = f.OpenCodeApi.AddLogin("b@example.test", "acct-2", "refresh-b-rotated", active: false, exp: 4102444900L);
        f.OpenCodeApi.AddLogin("z@example.test", "acct-7", "refresh-z", active: true);

        await f.Switch.SwitchAsync(b);

        var record = f.Vault.FindById(b)!;
        Assert.Equal("refresh-b-rotated", VaultCredentialMapper.RefreshOf(record));
        Assert.Equal(4102444900000L, VaultCredentialMapper.ExpiresOf(record));
        Assert.Equal("oc-id-token", record["tokens"]!["id_token"]!.GetValue<string>());
        Assert.Equal(theirs["id"]!.GetValue<string>(), f.OpenCodeApi.ActiveId);
        Assert.DoesNotContain(f.OpenCodeApi.Calls, c => c.StartsWith("create:") || c.StartsWith("delete:"));
        Assert.Contains("activate:" + theirs["id"]!.GetValue<string>(), f.OpenCodeApi.Calls);
    }

    [Fact]
    public async Task AccountAlreadyActiveInOpenCodeIsNotSwitchedAgain()
    {
        var (f, _, b) = await SetupAsync(SwitchTarget.OpenCode);
        using var _f = f;
        f.OpenCodeApi.AddLogin("b@example.test", "acct-2", "refresh-b", active: true);

        var result = await f.Switch.SwitchAsync(b);

        Assert.False(result.Changed);
        Assert.All(f.OpenCodeApi.Calls, c => Assert.False(c.StartsWith("create:") || c.StartsWith("activate:") || c.StartsWith("delete:")));
    }

    [Fact]
    public async Task ADifferentUserOfTheSameWorkspaceGetsHisOwnCredential()
    {
        var (f, _, b) = await SetupAsync(SwitchTarget.OpenCode);
        using var _f = f;
        var colleague = f.OpenCodeApi.AddLogin("colleague@example.test", "acct-2", "refresh-colleague", active: true);

        await f.Switch.SwitchAsync(b);

        Assert.NotNull(f.OpenCodeApi.Find(colleague["id"]!.GetValue<string>()));
        Assert.Equal(2, f.OpenCodeApi.Credentials.Count);
        Assert.Equal("refresh-b", Refresh(f.OpenCodeApi.Find(f.OpenCodeApi.ActiveId!)!));
    }

    [Fact]
    public async Task ARejectedCreateIsReconciledByIdAndNotRetried()
    {
        var (f, _, b) = await SetupAsync(SwitchTarget.OpenCode);
        using var _f = f;
        f.OpenCodeApi.Credentials.Clear();
        var api = new LostResponseApi(f.OpenCodeApi);
        var store = new OpenCodeAccountStore(api, f.Secrets, f.Log);

        var result = await store.ActivateAsync(f.Vault.FindById(b)!);

        Assert.True(result.Changed);
        Assert.Equal(1, api.Creates);
        Assert.Equal(result.CredentialId, f.OpenCodeApi.ActiveId);
    }

    /// <summary>Creates the credential but loses the response (the CLI died after the server had accepted it).</summary>
    private sealed class LostResponseApi : IOpenCodeCredentialApi
    {
        private readonly FakeOpenCodeApi _inner;
        public LostResponseApi(FakeOpenCodeApi inner) => _inner = inner;
        public int Creates { get; private set; }
        public Task<JsonArray> GetCredentialsAsync(CancellationToken cancellationToken = default) => _inner.GetCredentialsAsync(cancellationToken);
        public async Task<string> CreateCredentialAsync(string integrationId, string label, JsonObject value, bool activate, CancellationToken cancellationToken = default, string? requestedId = null)
        {
            Creates++;
            await _inner.CreateCredentialAsync(integrationId, label, value, activate, cancellationToken, requestedId);
            throw new OpenCodeException("OpenCode 请求超时（60 秒）。请检查后台服务。");
        }
        public Task ActivateCredentialAsync(string credentialId, CancellationToken cancellationToken = default) => _inner.ActivateCredentialAsync(credentialId, cancellationToken);
        public Task DeleteCredentialAsync(string credentialId, CancellationToken cancellationToken = default) => _inner.DeleteCredentialAsync(credentialId, cancellationToken);
    }

    // ---- absorbing before a switch -----------------------------------------

    [Fact]
    public async Task DshTargetAbsorbsNewerOpenCodeTokensAndLeavesOpenCodeAlone()
    {
        var (f, a, b) = await SetupAsync(SwitchTarget.Dsh);
        using var _f = f;
        f.OpenCodeApi.AddLogin("a@example.test", "acct-1", "refresh-a-oc", active: true, exp: 4102444900L);
        var calls = f.OpenCodeApi.Calls.Count;

        await f.Switch.SwitchAsync(b);

        Assert.Equal("refresh-a-oc", VaultCredentialMapper.RefreshOf(f.Vault.FindById(a)!));
        Assert.Equal("refresh-b", f.Grant!["refresh"]!.GetValue<string>());
        Assert.Equal(calls, f.OpenCodeApi.Calls.Count);
    }

    [Fact]
    public async Task AbsorbingFromAnUnavailableOpenCodeIsSilent()
    {
        var (f, _, b) = await SetupAsync(SwitchTarget.Dsh);
        using var _f = f;
        f.OpenCodeApi.Unavailable = true;

        var result = await f.Switch.SwitchAsync(b);

        Assert.True(result.Changed);
        Assert.Equal("refresh-b", f.Grant!["refresh"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenCodeTokensOfAnUnknownAccountAreNotAdopted()
    {
        var (f, _, b) = await SetupAsync(SwitchTarget.Dsh);
        using var _f = f;
        f.OpenCodeApi.AddLogin("stranger@example.test", "acct-99", "refresh-stranger", active: true);

        await f.Switch.SwitchAsync(b);

        Assert.DoesNotContain(f.Vault.Load(), r => VaultCredentialMapper.RefreshOf(r) == "refresh-stranger");
    }

    [Fact]
    public async Task DshGrantTokensRefreshedByDshAreAbsorbedBeforeACodexSwitch()
    {
        var (f, a, b) = await SetupAsync(SwitchTarget.Codex);
        using var _f = f;
        f.UseCli(authJson: DshFixture.AuthJson("a@example.test", "acct-1", "refresh-a"));
        // dsh refreshed A's tokens since the vault saw them.
        var credentials = CredentialsDocument.Update(f.CredentialsPath, doc => doc.SetGrant(DshBackend.CodexGrantKey, new JsonObject
        {
            ["type"] = "oauth",
            ["access"] = TestAccounts.AccessToken("a@example.test", "acct-1", 4102444900L),
            ["refresh"] = "refresh-a-dsh",
            ["expires"] = 4102444900000L,
            ["accountId"] = "acct-1",
        }));
        Assert.True(credentials);

        await f.Switch.SwitchAsync(b);

        Assert.Equal("refresh-a-dsh", VaultCredentialMapper.RefreshOf(f.Vault.FindById(a)!));
        Assert.Equal("refresh-a-dsh", f.Grant!["refresh"]!.GetValue<string>()); // dsh itself untouched
    }

    // ---- who is current where ----------------------------------------------

    [Fact]
    public async Task EachTargetReportsItsOwnCurrentAccount()
    {
        var (f, a, b) = await SetupAsync(SwitchTarget.Dsh);
        using var _f = f;
        f.UseCli(authJson: DshFixture.AuthJson("b@example.test", "acct-2", "refresh-b"));
        f.OpenCodeApi.AddLogin("a@example.test", "acct-1", "refresh-a", active: true);

        Assert.Equal(a, (await f.Targets.GetCurrentAsync(SwitchTarget.Dsh)).Id);
        Assert.Equal(b, (await f.Targets.GetCurrentAsync(SwitchTarget.Codex)).Id);
        Assert.Equal(a, (await f.Targets.GetCurrentAsync(SwitchTarget.OpenCode)).Id);
    }

    [Theory]
    [InlineData(SwitchTarget.Dsh)]
    [InlineData(SwitchTarget.Codex)]
    [InlineData(SwitchTarget.OpenCode)]
    public async Task TheCurrentMarkerOfTheSnapshotFollowsTheSelectedTarget(SwitchTarget target)
    {
        var (f, a, b) = await SetupAsync(target);
        using var _f = f;
        f.UseCli(authJson: DshFixture.AuthJson("b@example.test", "acct-2", "refresh-b"));
        f.OpenCodeApi.AddLogin("b@example.test", "acct-2", "refresh-b", active: true);

        var snapshot = await f.Snapshot.GetSnapshotAsync();

        var expected = target == SwitchTarget.Dsh ? a : b;
        Assert.Equal(expected, Assert.Single(snapshot.Accounts, r => r.TargetCurrent).CredentialId);
        Assert.True(snapshot.Accounts.Single(r => r.CredentialId == a).Active); // dsh's own flag is independent of the target
        Assert.Equal(string.Empty, snapshot.TargetNote);
    }

    [Fact]
    public async Task ReadingWithCodexSelectedAdoptsALoginTheListDoesNotHaveAndMarksIt()
    {
        var (f, _, _) = await SetupAsync(SwitchTarget.Codex);
        using var _f = f;
        // Signed in in the Codex desktop app with an account the tool never saw.
        f.UseCli(authJson: DshFixture.AuthJson("c@example.test", "acct-3", "refresh-c"));

        var snapshot = await f.Snapshot.GetSnapshotAsync();

        var current = Assert.Single(snapshot.Accounts, r => r.TargetCurrent);
        Assert.Equal("acct-3", current.AccountId);
        Assert.Equal(string.Empty, snapshot.TargetNote);
    }

    [Fact]
    public async Task ReadingWithCodexSelectedTakesOverTokensTheDesktopAppRefreshed()
    {
        var (f, _, b) = await SetupAsync(SwitchTarget.Codex);
        using var _f = f;
        f.UseCli(authJson: DshFixture.AuthJson("b@example.test", "acct-2", "refresh-b-rotated", exp: 4102444900L));

        var snapshot = await f.Snapshot.GetSnapshotAsync();

        Assert.Equal(b, Assert.Single(snapshot.Accounts, r => r.TargetCurrent).CredentialId);
        Assert.Equal("refresh-b-rotated", VaultCredentialMapper.RefreshOf(f.Vault.FindById(b)!));
    }

    [Fact]
    public async Task ACodexWithoutLoginLeavesNoCurrentAccountAndSaysWhy()
    {
        var (f, _, _) = await SetupAsync(SwitchTarget.Codex);
        using var _f = f;
        f.UseCli();

        var snapshot = await f.Snapshot.GetSnapshotAsync();

        Assert.DoesNotContain(snapshot.Accounts, r => r.TargetCurrent);
        Assert.Contains("Codex 未登录", snapshot.TargetNote);
        Assert.Contains("auth.json", snapshot.TargetNote);
    }

    [Fact]
    public async Task AnUnavailableOpenCodeLeavesNoCurrentAccountAndAnExplainingNote()
    {
        var (f, _, _) = await SetupAsync(SwitchTarget.OpenCode);
        using var _f = f;
        f.OpenCodeApi.Unavailable = true;

        var snapshot = await f.Snapshot.GetSnapshotAsync();

        Assert.DoesNotContain(snapshot.Accounts, r => r.TargetCurrent);
        Assert.Contains("OpenCode", snapshot.TargetNote);
        Assert.Contains("找不到", snapshot.TargetNote);
    }

    // ---- cross-target bookkeeping ------------------------------------------

    [Fact]
    public async Task OtherProgramsUsingTheAccountAreReported()
    {
        var (f, a, b) = await SetupAsync(SwitchTarget.Codex);
        using var _f = f;
        f.UseCli(authJson: DshFixture.AuthJson("b@example.test", "acct-2", "refresh-b"));
        f.OpenCodeApi.AddLogin("a@example.test", "acct-1", "refresh-a", active: true);

        // Codex is selected: A is in use by dsh and OpenCode, B only by Codex itself.
        Assert.Equal(new[] { SwitchTarget.Dsh, SwitchTarget.OpenCode }, await f.Targets.GetOtherUsersAsync(a, SwitchTarget.Codex));
        Assert.Empty(await f.Targets.GetOtherUsersAsync(b, SwitchTarget.Codex));
        var used = await f.Targets.GetUsedByOthersAsync(SwitchTarget.Codex);
        Assert.Equal(new[] { a }, used.Keys.ToArray());
    }

    [Fact]
    public async Task AnUnavailableOpenCodeIsSimplyNotCountedAsAUser()
    {
        var (f, a, _) = await SetupAsync(SwitchTarget.Codex);
        using var _f = f;
        f.OpenCodeApi.Unavailable = true;

        Assert.Equal(new[] { SwitchTarget.Dsh }, await f.Targets.GetOtherUsersAsync(a, SwitchTarget.Codex));
    }

    // ---- saving an account never switches another program -------------------

    [Fact]
    public async Task SavingTheCurrentAccountWithNewerTokensKeepsDshAndRefreshesTheTargetOnly()
    {
        var (f, a, _) = await SetupAsync(SwitchTarget.OpenCode);
        using var _f = f;
        f.OpenCodeApi.AddLogin("a@example.test", "acct-1", "refresh-a", active: true);
        f.OpenCodeApi.AddLogin("z@example.test", "acct-7", "refresh-z");
        var row = AccountManager.Core.Import.ImportService.ConvertAccount(TestAccounts.AuthorizedAccount("a@example.test", "acct-1", "refresh-a-new", 4102444900L), "chatgpt-browser");

        var newId = await f.Backend.CreateCredentialAsync("openai", row.Label, row.Value, activate: true);
        await f.Backend.DeleteCredentialAsync(a);

        Assert.Equal("refresh-a-new", f.Grant!["refresh"]!.GetValue<string>()); // dsh keeps pointing at the (replaced) account
        Assert.Equal("refresh-a-new", Refresh(f.OpenCodeApi.Find(f.OpenCodeApi.ActiveId!)!));
        Assert.Equal(2, f.OpenCodeApi.Credentials.Count); // A replaced by its newer tokens, the other account untouched
        Assert.NotEmpty(newId);
    }

    [Fact]
    public async Task SavingAnAccountOtherThanTheTargetsCurrentOneLeavesTheTargetAlone()
    {
        var (f, _, _) = await SetupAsync(SwitchTarget.OpenCode);
        using var _f = f;
        f.OpenCodeApi.AddLogin("z@example.test", "acct-7", "refresh-z", active: true);
        var row = AccountManager.Core.Import.ImportService.ConvertAccount(TestAccounts.AuthorizedAccount("n@example.test", "acct-5", "refresh-n"), "chatgpt-browser");

        await f.Backend.CreateCredentialAsync("openai", row.Label, row.Value, activate: false);

        Assert.Equal("refresh-z", Refresh(f.OpenCodeApi.Find(f.OpenCodeApi.ActiveId!)!));
        Assert.DoesNotContain(f.OpenCodeApi.Calls, c => c.StartsWith("create:") || c.StartsWith("activate:"));
        Assert.Equal("refresh-a", f.Grant!["refresh"]!.GetValue<string>());
        Assert.False(f.Backend.FirstAccountBecomesCurrent);
    }
}
