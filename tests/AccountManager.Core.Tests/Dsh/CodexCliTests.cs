using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Dsh;
using AccountManager.Core.Json;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

public class CodexCliTests
{
    private static JsonObject ReadAuth(DshFixture f) => (JsonObject)JsonNode.Parse(File.ReadAllText(f.CodexAuthPath))!;

    private static string Tok(JsonObject auth, string name) => auth["tokens"]![name]!.GetValue<string>();

    /// <summary>Accounts A (current in dsh and in the Codex file) and B in the vault; Codex is the switch target.</summary>
    private static async Task<(DshFixture F, string A, string B)> TwoAccountsAsync(string? cliJson = null, SwitchTarget target = SwitchTarget.Codex)
    {
        var f = new DshFixture();
        var a = await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");
        f.UseCli(authJson: cliJson ?? DshFixture.AuthJson("a@example.test", "acct-1", "refresh-a"));
        f.Target = target; // the accounts were saved while dsh was the target
        return (f, a, b);
    }

    [Fact]
    public async Task CodexTargetWritesOnlyAuthJsonKeepingOtherKeysModeAndBackup()
    {
        var (f, _, b) = await TwoAccountsAsync();
        using var _f = f;
        var before = File.ReadAllText(f.CodexAuthPath);
        var dshBefore = File.ReadAllBytes(f.CredentialsPath);
        var patchBefore = File.ReadAllText(f.PatchPath);

        var result = await f.Switch.SwitchAsync(b);

        Assert.Equal(SwitchTarget.Codex, result.Target);
        Assert.True(result.Changed);
        Assert.Contains("重启", result.Note);
        var auth = ReadAuth(f);
        Assert.Equal("refresh-b", Tok(auth, "refresh_token"));
        Assert.Equal("acct-2", Tok(auth, "account_id"));
        Assert.Equal(TestAccounts.AccessToken("b@example.test", "acct-2"), Tok(auth, "access_token"));
        Assert.Equal("fake-id-token", Tok(auth, "id_token"));
        Assert.Equal(new[] { "OPENAI_API_KEY", "tokens", "last_refresh", "x-unknown" }, auth.Select(kv => kv.Key).ToArray());
        Assert.Null(auth["OPENAI_API_KEY"]);
        Assert.Equal("""[1,2.0,"three"]""", auth["x-unknown"]!["keep"]!.ToJsonString());
        Assert.NotEqual("2026-10-01T10:00:00.123456789Z", auth["last_refresh"]!.GetValue<string>());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{9}Z$", auth["last_refresh"]!.GetValue<string>());
        Assert.EndsWith("\n", File.ReadAllText(f.CodexAuthPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f.CodexAuthPath));
        }

        var backups = f.Backups.ListBackups();
        Assert.Single(backups);
        Assert.Equal(before, f.Backups.Read(backups[0])!["configText"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(f.CodexHome, "*.tmp"));

        // dsh was not touched: its grant still belongs to A, files byte for byte as they were.
        Assert.Equal("refresh-a", f.Grant!["refresh"]!.GetValue<string>());
        Assert.Equal(dshBefore, File.ReadAllBytes(f.CredentialsPath));
        Assert.Equal(patchBefore, File.ReadAllText(f.PatchPath));
    }

    [Fact]
    public async Task DshTargetNeverWritesOrBacksUpTheCodexFile()
    {
        var (f, _, b) = await TwoAccountsAsync(target: SwitchTarget.Dsh);
        using var _f = f;
        var before = File.ReadAllBytes(f.CodexAuthPath);

        var result = await f.Switch.SwitchAsync(b);

        Assert.Equal(SwitchTarget.Dsh, result.Target);
        Assert.Equal("refresh-b", f.Grant!["refresh"]!.GetValue<string>());
        Assert.Equal(before, File.ReadAllBytes(f.CodexAuthPath));
        Assert.Empty(f.Backups.ListBackups());
        Assert.Equal(string.Empty, result.Note);
    }

    [Theory]
    [InlineData(SwitchTarget.Codex)]
    [InlineData(SwitchTarget.Dsh)]
    public async Task CodexTokensNewerThanTheVaultAreWrittenBackFirst(SwitchTarget target)
    {
        var (f, a, b) = await TwoAccountsAsync(DshFixture.AuthJson("a@example.test", "acct-1", "refresh-a-rotated", exp: 4102444900L), target);
        using var _f = f;

        await f.Switch.SwitchAsync(b);

        var record = f.Vault.FindById(a)!;
        Assert.Equal("refresh-a-rotated", VaultCredentialMapper.RefreshOf(record));
        Assert.Equal(4102444900000L, VaultCredentialMapper.ExpiresOf(record));
        Assert.Equal("cli-id-token", record["tokens"]!["id_token"]!.GetValue<string>());
    }

    [Fact]
    public async Task OlderCodexTokensDoNotOverwriteTheVault()
    {
        var (f, a, b) = await TwoAccountsAsync(DshFixture.AuthJson("a@example.test", "acct-1", "refresh-a-stale", exp: 1700000000L));
        using var _f = f;

        await f.Switch.SwitchAsync(b);

        Assert.Equal("refresh-a", VaultCredentialMapper.RefreshOf(f.Vault.FindById(a)!));
    }

    [Fact]
    public async Task AForeignCodexLoginIsAdoptedIntoTheVaultBeforeItIsReplaced()
    {
        var (f, _, b) = await TwoAccountsAsync(DshFixture.AuthJson("c@example.test", "acct-3", "refresh-c"));
        using var _f = f;

        await f.Switch.SwitchAsync(b);

        var adopted = f.Vault.Load().Single(r => VaultCredentialMapper.RefreshOf(r) == "refresh-c");
        Assert.Equal("c@example.test", VaultCredentialMapper.EmailOf(adopted));
        Assert.Equal("acct-3", VaultCredentialMapper.AccountIdOf(adopted));
        Assert.Equal("cli-id-token", adopted["tokens"]!["id_token"]!.GetValue<string>());
        Assert.Equal("refresh-b", Tok(ReadAuth(f), "refresh_token"));
        Assert.Equal(3, f.Vault.Load().Count);
    }

    [Fact]
    public async Task KeyringModeFailsTheSwitchAndChangesNothing()
    {
        var (f, _, b) = await TwoAccountsAsync();
        using var _f = f;
        File.WriteAllText(Path.Combine(f.CodexHome, "config.toml"), "model = \"x\"\ncli_auth_credentials_store = \"keyring\"  # os keyring\n");
        var before = File.ReadAllText(f.CodexAuthPath);
        var dshBefore = File.ReadAllBytes(f.CredentialsPath);

        var ex = await Assert.ThrowsAsync<DshException>(() => f.Switch.SwitchAsync(b));

        Assert.Contains("密钥环", ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.Equal(before, File.ReadAllText(f.CodexAuthPath));
        Assert.Equal(dshBefore, File.ReadAllBytes(f.CredentialsPath));
    }

    [Theory]
    [InlineData("cli_auth_credentials_store = \"file\"", true)]
    [InlineData("cli_auth_credentials_store = 'file'", true)]
    [InlineData("# cli_auth_credentials_store = \"keyring\"", true)]
    [InlineData("[profiles.x]\ncli_auth_credentials_store = \"keyring\"", true)]
    [InlineData("cli_auth_credentials_store=\"keyring\"", false)]
    [InlineData("  cli_auth_credentials_store = 'keyring'", false)]
    public async Task ConfigTomlLookupIsTolerantAndOnlyReadsTopLevelKeys(string toml, bool usable)
    {
        using var f = new DshFixture();
        var store = f.UseCli(authJson: DshFixture.AuthJson("a@example.test", "acct-1", "r"));
        File.WriteAllText(store.ConfigPath, toml + "\n");

        Assert.Equal(usable, store.CheckUsable(forWrite: true) is null);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AutoModeWithoutAuthFileIsSkipped()
    {
        using var f = new DshFixture();
        var store = f.UseCli();
        File.WriteAllText(store.ConfigPath, "cli_auth_credentials_store = \"auto\"\n");

        Assert.NotNull(store.CheckUsable(forWrite: true));
        Assert.Null(store.ReadCurrent());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AMissingCodexDirectoryIsNeverCreatedAndTheSwitchFailsClearly()
    {
        using var f = new DshFixture();
        await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");
        f.Target = SwitchTarget.Codex;
        f.UseCli(createHome: false);
        var dshBefore = File.ReadAllBytes(f.CredentialsPath);

        var ex = await Assert.ThrowsAsync<DshException>(() => f.Switch.SwitchAsync(b));

        Assert.Contains("没有 Codex 目录", ex.Message);
        Assert.False(Directory.Exists(f.CodexHome));
        Assert.Equal(dshBefore, File.ReadAllBytes(f.CredentialsPath));
    }

    [Fact]
    public async Task ADirectoryWithoutAuthFileGetsTheLogin()
    {
        using var f = new DshFixture();
        await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");
        f.Target = SwitchTarget.Codex;
        f.UseCli();

        await f.Switch.SwitchAsync(b);

        Assert.Equal("refresh-b", Tok(ReadAuth(f), "refresh_token"));
        Assert.Empty(f.Backups.ListBackups());
        Assert.Equal("refresh-a", f.Grant!["refresh"]!.GetValue<string>());
    }

    [Fact]
    public async Task AReadOnlyAuthFileFailsTheSwitch()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var (f, _, b) = await TwoAccountsAsync();
        using var _f = f;
        File.SetUnixFileMode(f.CodexAuthPath, UnixFileMode.UserRead);
        var before = File.ReadAllText(f.CodexAuthPath);

        var ex = await Assert.ThrowsAsync<DshException>(() => f.Switch.SwitchAsync(b));

        Assert.Contains("只读", ex.Message);
        Assert.Equal(before, File.ReadAllText(f.CodexAuthPath));
    }

    [Fact]
    public async Task AFailedAtomicWriteLeavesTheOldFileIntact()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") { return; }
        var (f, _, b) = await TwoAccountsAsync();
        using var _f = f;
        var before = File.ReadAllText(f.CodexAuthPath);
        File.SetUnixFileMode(f.CodexHome, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await Assert.ThrowsAsync<DshException>(() => f.Switch.SwitchAsync(b));

            Assert.Equal(before, File.ReadAllText(f.CodexAuthPath));
        }
        finally
        {
            File.SetUnixFileMode(f.CodexHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Empty(Directory.GetFiles(f.CodexHome, "*.tmp"));
    }

    [Fact]
    public async Task UnparseableAuthFileIsLeftAlone()
    {
        var (f, _, b) = await TwoAccountsAsync("{ not json");
        using var _f = f;

        var ex = await Assert.ThrowsAsync<DshException>(() => f.Switch.SwitchAsync(b));

        Assert.Equal("{ not json", File.ReadAllText(f.CodexAuthPath));
        Assert.Contains("格式无法识别", ex.Message);
    }

    [Fact]
    public async Task ApiKeyLoginOfTheCliIsNotReplaced()
    {
        var (f, _, b) = await TwoAccountsAsync("{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"sk-fake\"}");
        using var _f = f;

        var ex = await Assert.ThrowsAsync<DshException>(() => f.Switch.SwitchAsync(b));

        Assert.Contains("API Key", ex.Message);
        Assert.Contains("sk-fake", File.ReadAllText(f.CodexAuthPath));
    }

    [Fact]
    public async Task SwitchingToTheAccountCodexAlreadyHasMakesNoBackupAndNoWrite()
    {
        var (f, a, _) = await TwoAccountsAsync();
        using var _f = f;
        var before = File.ReadAllBytes(f.CodexAuthPath);

        var result = await f.Switch.SwitchAsync(a);

        Assert.False(result.Changed);
        Assert.Equal(before, File.ReadAllBytes(f.CodexAuthPath));
        Assert.Empty(f.Backups.ListBackups());
    }

    [Fact]
    public async Task TheCurrentMarkerFollowsTheSelectedTargetAndReadingWritesNothing()
    {
        var (f, a, b) = await TwoAccountsAsync(DshFixture.AuthJson("b@example.test", "acct-2", "refresh-b"));
        using var _f = f;
        var bytes = File.ReadAllBytes(f.CodexAuthPath);
        var stamp = File.GetLastWriteTimeUtc(f.CodexAuthPath);
        var vaultBefore = File.ReadAllBytes(f.Vault.FilePath);

        var codex = await f.Snapshot.GetSnapshotAsync();
        Assert.True(codex.Accounts.Single(r => r.CredentialId == a).Active);        // dsh uses A ...
        Assert.False(codex.Accounts.Single(r => r.CredentialId == a).TargetCurrent); // ... but the target is Codex: B is current
        Assert.True(codex.Accounts.Single(r => r.CredentialId == b).TargetCurrent);
        Assert.Equal(string.Empty, codex.TargetNote);

        f.Target = SwitchTarget.Dsh;
        var dsh = await f.Snapshot.GetSnapshotAsync();
        Assert.True(dsh.Accounts.Single(r => r.CredentialId == a).TargetCurrent);
        Assert.False(dsh.Accounts.Single(r => r.CredentialId == b).TargetCurrent);

        Assert.Equal(bytes, File.ReadAllBytes(f.CodexAuthPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(f.CodexAuthPath));
        Assert.Equal(vaultBefore, File.ReadAllBytes(f.Vault.FilePath));
        Assert.Empty(f.Backups.ListBackups());
    }

    [Fact]
    public async Task ImportActivatingTheFirstAccountFollowsTheTarget()
    {
        // Dsh target: the first account becomes dsh's current one, Codex's file is not written.
        using (var f = new DshFixture())
        {
            f.UseCli(authJson: DshFixture.AuthJson("old@example.test", "acct-9", "refresh-old"));
            var before = File.ReadAllText(f.CodexAuthPath);
            var file = DshBackendApiTests.WriteJson(f, "in.json", new JsonArray(TestAccounts.CockpitOauth("c-1", "n@example.test", "acct-5", "refresh-n")));

            var result = await f.Import.ImportAsync(await f.Import.PreviewAsync(file));

            Assert.Equal(0, result.Failed);
            Assert.Equal("refresh-n", f.Grant!["refresh"]!.GetValue<string>());
            Assert.Equal(before, File.ReadAllText(f.CodexAuthPath));
        }

        // Codex target: nothing is switched by an import, neither dsh nor Codex.
        using (var f = new DshFixture { Target = SwitchTarget.Codex })
        {
            f.UseCli(authJson: DshFixture.AuthJson("old@example.test", "acct-9", "refresh-old"));
            var before = File.ReadAllText(f.CodexAuthPath);
            var file = DshBackendApiTests.WriteJson(f, "in.json", new JsonArray(TestAccounts.CockpitOauth("c-1", "n@example.test", "acct-5", "refresh-n")));

            var result = await f.Import.ImportAsync(await f.Import.PreviewAsync(file));

            Assert.Equal(0, result.Failed);
            Assert.Null(f.Grant);
            Assert.Equal(before, File.ReadAllText(f.CodexAuthPath));
            Assert.Contains(f.Vault.Load(), r => VaultCredentialMapper.RefreshOf(r) == "refresh-n");
        }
    }

    [Fact]
    public void HomeResolutionPrefersTheSettingThenCodexHomeThenTheDefault()
    {
        var env = new Dictionary<string, string?> { ["CODEX_HOME"] = "/tmp/from-env" };
        string? setting = "/tmp/from-setting";
        var store = new CodexCliAuthStore(() => setting, null, k => env.GetValueOrDefault(k), () => "/home/u");

        Assert.Equal(Path.GetFullPath("/tmp/from-setting"), store.Home);
        setting = "  ";
        Assert.Equal(Path.GetFullPath("/tmp/from-env"), store.Home);
        env.Clear();
        Assert.Equal(Path.Combine("/home/u", ".codex"), store.Home);
        Assert.Equal(Path.Combine("/home/u", ".codex", "auth.json"), store.AuthPath);
    }

    [Theory]
    [InlineData("2026-10-01T10:00:00.123456789Z", @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{9}Z$")]
    [InlineData("2026-10-01T10:00:00Z", @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")]
    [InlineData(null, @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")]
    public void LastRefreshKeepsTheShapeOfTheExistingValue(string? existing, string pattern)
        => Assert.Matches(pattern, CodexCliAuthStore.FormatTime(DateTimeOffset.Parse("2026-10-28T07:12:33.5Z"), existing));
}
