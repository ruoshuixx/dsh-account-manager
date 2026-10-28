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

    /// <summary>Accounts A (current in dsh and in the CLI) and B in the vault, CLI step on.</summary>
    private static async Task<(DshFixture F, string A, string B)> TwoAccountsAsync(string? cliJson = null)
    {
        var f = new DshFixture();
        var a = await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");
        f.UseCli(authJson: cliJson ?? DshFixture.AuthJson("a@example.test", "acct-1", "refresh-a"));
        return (f, a, b);
    }

    [Fact]
    public async Task SwitchWritesDshAndCodexCliKeepingOtherKeysModeAndBackup()
    {
        var (f, _, b) = await TwoAccountsAsync();
        using var _f = f;
        var before = File.ReadAllText(f.CodexAuthPath);

        await f.Switch.SwitchAsync(b);

        Assert.Equal("refresh-b", f.Grant!["refresh"]!.GetValue<string>());
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
        Assert.Equal(string.Empty, f.Backend.LastCodexCliNote);
    }

    [Fact]
    public async Task CodexCliTokensNewerThanTheVaultAreWrittenBackFirst()
    {
        var (f, a, b) = await TwoAccountsAsync(DshFixture.AuthJson("a@example.test", "acct-1", "refresh-a-rotated", exp: 4102444900L));
        using var _f = f;

        await f.Switch.SwitchAsync(b);

        var record = f.Vault.FindById(a)!;
        Assert.Equal("refresh-a-rotated", VaultCredentialMapper.RefreshOf(record));
        Assert.Equal(4102444900000L, VaultCredentialMapper.ExpiresOf(record));
        Assert.Equal("cli-id-token", record["tokens"]!["id_token"]!.GetValue<string>());
        Assert.Equal("refresh-b", Tok(ReadAuth(f), "refresh_token"));
    }

    [Fact]
    public async Task OlderCodexCliTokensDoNotOverwriteTheVault()
    {
        var (f, a, b) = await TwoAccountsAsync(DshFixture.AuthJson("a@example.test", "acct-1", "refresh-a-stale", exp: 1700000000L));
        using var _f = f;

        await f.Switch.SwitchAsync(b);

        Assert.Equal("refresh-a", VaultCredentialMapper.RefreshOf(f.Vault.FindById(a)!));
    }

    [Fact]
    public async Task AForeignCodexCliLoginIsAdoptedIntoTheVaultBeforeItIsReplaced()
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
    public async Task KeyringModeIsSkippedWithAOneLineNoteAndDshStillSwitches()
    {
        var (f, _, b) = await TwoAccountsAsync();
        using var _f = f;
        File.WriteAllText(Path.Combine(f.CodexHome, "config.toml"), "model = \"x\"\ncli_auth_credentials_store = \"keyring\"  # os keyring\n");
        var before = File.ReadAllText(f.CodexAuthPath);

        var result = await f.Switch.SwitchAsync(b);

        Assert.Equal("refresh-b", f.Grant!["refresh"]!.GetValue<string>());
        Assert.Equal(before, File.ReadAllText(f.CodexAuthPath));
        Assert.StartsWith("Codex CLI 未同步：", result.CliNote);
        Assert.Contains("密钥环", result.CliNote);
        Assert.DoesNotContain('\n', result.CliNote);
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
    public async Task AMissingCodexDirectoryIsNeverCreatedAndNotMentioned()
    {
        using var f = new DshFixture();
        var a = await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");
        f.UseCli(createHome: false);

        var result = await f.Switch.SwitchAsync(b);

        Assert.False(Directory.Exists(f.CodexHome));
        Assert.Equal(string.Empty, result.CliNote);
        Assert.Equal("refresh-b", f.Grant!["refresh"]!.GetValue<string>());
        Assert.Contains("没有 Codex 目录", f.RunLog);
        _ = a;
    }

    [Fact]
    public async Task ADirectoryWithoutAuthFileGetsTheLogin()
    {
        using var f = new DshFixture();
        await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");
        f.UseCli();

        await f.Switch.SwitchAsync(b);

        Assert.Equal("refresh-b", Tok(ReadAuth(f), "refresh_token"));
        Assert.Empty(f.Backups.ListBackups());
    }

    [Fact]
    public async Task AReadOnlyAuthFileIsAWarningNotAFailure()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var (f, _, b) = await TwoAccountsAsync();
        using var _f = f;
        File.SetUnixFileMode(f.CodexAuthPath, UnixFileMode.UserRead);
        var before = File.ReadAllText(f.CodexAuthPath);

        var result = await f.Switch.SwitchAsync(b);

        Assert.Equal("refresh-b", f.Grant!["refresh"]!.GetValue<string>());
        Assert.Equal(before, File.ReadAllText(f.CodexAuthPath));
        Assert.Contains("只读", result.CliNote);
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
            var result = await f.Switch.SwitchAsync(b);

            Assert.Equal("refresh-b", f.Grant!["refresh"]!.GetValue<string>());
            Assert.StartsWith("Codex CLI 未同步：", result.CliNote);
            Assert.Equal(before, File.ReadAllText(f.CodexAuthPath));
        }
        finally
        {
            File.SetUnixFileMode(f.CodexHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Empty(Directory.GetFiles(f.CodexHome, "*.tmp"));
    }

    [Fact]
    public async Task WithTheSettingOffTheCliFileIsNeverTouchedOrRead()
    {
        var (f, _, b) = await TwoAccountsAsync();
        using var _f = f;
        f.CliEnabled = false;
        var before = File.ReadAllText(f.CodexAuthPath);

        var result = await f.Switch.SwitchAsync(b);

        Assert.Equal(before, File.ReadAllText(f.CodexAuthPath));
        Assert.Equal(string.Empty, result.CliNote);
        Assert.Empty(f.Backups.ListBackups());
        Assert.Equal(2, f.Vault.Load().Count);
    }

    [Fact]
    public async Task UnparseableAuthFileIsLeftAlone()
    {
        var (f, _, b) = await TwoAccountsAsync("{ not json");
        using var _f = f;

        var result = await f.Switch.SwitchAsync(b);

        Assert.Equal("{ not json", File.ReadAllText(f.CodexAuthPath));
        Assert.Contains("格式无法识别", result.CliNote);
        Assert.Equal("refresh-b", f.Grant!["refresh"]!.GetValue<string>());
    }

    [Fact]
    public async Task ApiKeyLoginOfTheCliIsNotReplaced()
    {
        var (f, _, b) = await TwoAccountsAsync("{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"sk-fake\"}");
        using var _f = f;

        var result = await f.Switch.SwitchAsync(b);

        Assert.Contains("API Key", result.CliNote);
        Assert.Contains("sk-fake", File.ReadAllText(f.CodexAuthPath));
    }

    [Fact]
    public async Task SwitchingToTheAccountTheCliAlreadyHasMakesNoBackupAndNoWrite()
    {
        using var f = new DshFixture();
        await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        var b = await f.AddCodexAsync("b@example.test", "acct-2", "refresh-b");
        f.UseCli();
        await f.Switch.SwitchAsync(b);
        var written = File.ReadAllText(f.CodexAuthPath);
        var a = f.Vault.Load().Single(r => VaultCredentialMapper.EmailOf(r) == "a@example.test");

        await f.Switch.SwitchAsync(VaultCredentialMapper.IdOf(a)!);
        await f.Switch.SwitchAsync(b); // back: CLI holds A now, so this writes B again
        var again = File.ReadAllText(f.CodexAuthPath);
        var backupsBefore = f.Backups.ListBackups().Count;
        // dsh and CLI both already on B: asking again changes nothing
        await f.Switch.SwitchAsync(b);

        Assert.Equal(again, File.ReadAllText(f.CodexAuthPath));
        Assert.Equal(backupsBefore, f.Backups.ListBackups().Count);
        Assert.NotEqual(string.Empty, written);
    }

    [Fact]
    public async Task ReadingAccountsMarksTheCliAccountAndWritesNothing()
    {
        var (f, a, b) = await TwoAccountsAsync(DshFixture.AuthJson("b@example.test", "acct-2", "refresh-b"));
        using var _f = f;
        var bytes = File.ReadAllBytes(f.CodexAuthPath);
        var stamp = File.GetLastWriteTimeUtc(f.CodexAuthPath);
        var vaultBefore = File.ReadAllBytes(f.Vault.FilePath);

        var snapshot = await f.Snapshot.GetSnapshotAsync();

        var rowA = snapshot.Accounts.Single(r => r.CredentialId == a);
        var rowB = snapshot.Accounts.Single(r => r.CredentialId == b);
        Assert.True(rowA.Active);
        Assert.False(rowA.CliCurrent);
        Assert.True(rowA.CliPending);
        Assert.True(rowB.CliCurrent);
        Assert.False(rowB.Active);
        Assert.Equal(bytes, File.ReadAllBytes(f.CodexAuthPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(f.CodexAuthPath));
        Assert.Equal(vaultBefore, File.ReadAllBytes(f.Vault.FilePath));
        Assert.Empty(f.Backups.ListBackups());
    }

    [Fact]
    public async Task NoChipsWhenBothUseTheSameAccountOrTheSettingIsOff()
    {
        var (f, a, _) = await TwoAccountsAsync();
        using var _f = f;

        var same = await f.Snapshot.GetSnapshotAsync();
        Assert.All(same.Accounts, r => Assert.False(r.CliPending));
        Assert.True(same.Accounts.Single(r => r.CredentialId == a).CliCurrent);

        f.CliEnabled = false;
        var off = await f.Snapshot.GetSnapshotAsync();
        Assert.All(off.Accounts, r => Assert.False(r.CliCurrent || r.CliPending));
    }

    [Fact]
    public async Task SwitchingTheAccountDshAlreadyUsesStillSyncsTheCli()
    {
        var (f, a, _) = await TwoAccountsAsync(DshFixture.AuthJson("b@example.test", "acct-2", "refresh-b"));
        using var _f = f;

        var result = await f.Switch.SwitchAsync(a);

        Assert.False(result.Changed);
        Assert.Equal("refresh-a", Tok(ReadAuth(f), "refresh_token"));
    }

    [Fact]
    public async Task ImportActivatingTheFirstAccountWritesTheCliToo()
    {
        using var f = new DshFixture();
        f.UseCli(authJson: DshFixture.AuthJson("old@example.test", "acct-9", "refresh-old"));
        var file = DshBackendApiTests.WriteJson(f, "in.json", new JsonArray(TestAccounts.CockpitOauth("c-1", "n@example.test", "acct-5", "refresh-n")));

        var result = await f.Import.ImportAsync(await f.Import.PreviewAsync(file));

        Assert.Equal(0, result.Failed);
        Assert.Equal("refresh-n", f.Grant!["refresh"]!.GetValue<string>());
        Assert.Equal("refresh-n", Tok(ReadAuth(f), "refresh_token"));
        Assert.Contains(f.Vault.Load(), r => VaultCredentialMapper.RefreshOf(r) == "refresh-old"); // the CLI's old login was adopted
    }

    [Fact]
    public async Task ImportThatDoesNotActivateLeavesTheCliAlone()
    {
        using var f = new DshFixture();
        await f.AddCodexAsync("a@example.test", "acct-1", "refresh-a", activate: true);
        f.UseCli(authJson: DshFixture.AuthJson("a@example.test", "acct-1", "refresh-a"));
        var before = File.ReadAllText(f.CodexAuthPath);
        var file = DshBackendApiTests.WriteJson(f, "in.json", new JsonArray(TestAccounts.CockpitOauth("c-1", "n@example.test", "acct-5", "refresh-n")));

        await f.Import.ImportAsync(await f.Import.PreviewAsync(file));

        Assert.Equal(before, File.ReadAllText(f.CodexAuthPath));
        Assert.DoesNotContain(f.Backups.ListBackups(), p => f.Backups.Read(p)!["configPath"]?.GetValue<string>() == f.CodexAuthPath);
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
