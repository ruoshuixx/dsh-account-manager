using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

public class AccountVaultTests
{
    private static JsonObject Account(string id, string accountId, string email = "u@example.com") => new()
    {
        ["id"] = id,
        ["email"] = email,
        ["account_id"] = accountId,
        ["plan_type"] = "plus",
        ["created_at"] = 1760000000,
        ["last_used"] = 1760000100,
        ["tokens"] = new JsonObject
        {
            ["access_token"] = "SECRET-ACCESS-TOKEN-" + id,
            ["refresh_token"] = "SECRET-REFRESH-TOKEN-" + id,
            ["id_token"] = "SECRET-ID-TOKEN-" + id,
        },
        ["expires"] = 1760003600000L,
    };

    /// <summary>A fast reversible stand-in for the OS protector (keeps tests off the real key file).</summary>
    private static AccountVault Make(string dir) => new(dir,
        data => data.Select(b => (byte)(b ^ 0x5A)).Reverse().ToArray(),
        data => data.Reverse().Select(b => (byte)(b ^ 0x5A)).ToArray());

    [Fact]
    public void MissingVaultIsEmpty()
    {
        using var temp = new TempDir();
        Assert.Empty(Make(temp.Path).Load());
    }

    [Fact]
    public void RoundTripsAccountsAndNeverStoresPlaintextTokens()
    {
        using var temp = new TempDir();
        var vault = Make(temp.File("data"));

        Assert.True(vault.Upsert(Account("a1", "acc-1")));
        Assert.True(vault.Upsert(Account("a2", "acc-2", "b@example.com")));

        var bytes = File.ReadAllBytes(vault.FilePath);
        var asText = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("SECRET-ACCESS-TOKEN", asText);
        Assert.DoesNotContain("SECRET-REFRESH-TOKEN", asText);
        Assert.DoesNotContain("u@example.com", asText);
        Assert.DoesNotContain("acc-1", asText);

        var loaded = Make(temp.File("data")).Load();
        Assert.Equal(new[] { "a1", "a2" }, loaded.Select(a => a["id"]!.GetValue<string>()).ToArray());
        Assert.Equal(Account("a2", "acc-2", "b@example.com").ToJsonString(), loaded[1].ToJsonString());
        Assert.Equal(1760003600000L, loaded[0]["expires"]!.GetValue<long>());
    }

    [Fact]
    public void FileAndDirectoryArePrivateAndNoTempFilesRemain()
    {
        using var temp = new TempDir();
        var dir = temp.File("data");
        var vault = Make(dir);

        vault.Upsert(Account("a1", "acc-1"));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(vault.FilePath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        }

        Assert.Equal(new[] { "accounts.vault" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void UpsertReplacesInPlaceByIdAndRemoveDeletes()
    {
        using var temp = new TempDir();
        var vault = Make(temp.Path);
        vault.Upsert(Account("a1", "acc-1"));
        vault.Upsert(Account("a2", "acc-2"));
        var changed = Account("a1", "acc-1", "changed@example.com");

        Assert.False(vault.Upsert(changed));

        var all = vault.Load();
        Assert.Equal(new[] { "a1", "a2" }, all.Select(a => a["id"]!.GetValue<string>()).ToArray());
        Assert.Equal("changed@example.com", all[0]["email"]!.GetValue<string>());
        Assert.True(vault.Remove("a1"));
        Assert.False(vault.Remove("a1"));
        Assert.Equal("a2", Assert.Single(vault.Load())["id"]!.GetValue<string>());
    }

    [Fact]
    public void LooksUpByIdAndByAccountId()
    {
        using var temp = new TempDir();
        var vault = Make(temp.Path);
        vault.Upsert(Account("a1", "acc-1"));
        vault.Upsert(Account("a2", "acc-2"));
        vault.Upsert(Account("a3", "acc-2", "third@example.com"));

        Assert.Equal("a2", vault.FindByAccountId("acc-2")!["id"]!.GetValue<string>());
        Assert.Equal(new[] { "a2", "a3" }, vault.FindAllByAccountId("acc-2").Select(a => a["id"]!.GetValue<string>()).ToArray());
        Assert.Equal("acc-1", vault.FindById("a1")!["account_id"]!.GetValue<string>());
        Assert.Null(vault.FindById("nope"));
        Assert.Null(vault.FindByAccountId("nope"));
    }

    [Fact]
    public void AccountIdFallsBackToTheTokensObject()
    {
        using var temp = new TempDir();
        var vault = Make(temp.Path);
        var acc = Account("a1", "ignored");
        acc.Remove("account_id");
        acc["tokens"]!["account_id"] = "from-tokens";
        vault.Upsert(acc);

        Assert.Equal("a1", vault.FindByAccountId("from-tokens")!["id"]!.GetValue<string>());
    }

    [Fact]
    public void LoadReturnsCopies()
    {
        using var temp = new TempDir();
        var vault = Make(temp.Path);
        vault.Upsert(Account("a1", "acc-1"));

        vault.Load()[0]["email"] = "mutated";

        Assert.Equal("u@example.com", vault.Load()[0]["email"]!.GetValue<string>());
    }

    [Fact]
    public void AccountWithoutIdIsRejected()
    {
        using var temp = new TempDir();
        Assert.Throws<ArgumentException>(() => Make(temp.Path).Upsert(new JsonObject { ["email"] = "x" }));
    }

    [Fact]
    public void ADamagedVaultIsReportedAndNeverOverwritten()
    {
        using var temp = new TempDir();
        var vault = Make(temp.Path);
        vault.Upsert(Account("a1", "acc-1"));
        var good = File.ReadAllBytes(vault.FilePath);
        var damaged = (byte[])good.Clone();
        damaged[^1] ^= 0xFF;
        // The stand-in cipher has no integrity check, so damage the JSON itself instead.
        var garbage = new byte[] { (byte)'D', (byte)'S', (byte)'H', (byte)'V', (byte)'A', (byte)'U', (byte)'L', (byte)'T', (byte)'1', (byte)'\n', 1, 2, 3 };
        File.WriteAllBytes(vault.FilePath, garbage);

        Assert.Throws<DshFormatException>(() => vault.Load());
        Assert.Throws<DshFormatException>(() => vault.Upsert(Account("a2", "acc-2")));
        Assert.Equal(garbage, File.ReadAllBytes(vault.FilePath));

        File.WriteAllBytes(vault.FilePath, Encoding.ASCII.GetBytes("not a vault"));
        Assert.Throws<DshFormatException>(() => vault.Load());
        Assert.False(File.Exists(vault.FilePath + ".lock"));
    }

    [Fact]
    public void WorksWithTheRealUnixProtector()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var temp = new TempDir();
        var store = new UnixSecretStore(temp.File("keys"));
        var vault = new AccountVault(temp.File("data"), store.Protect, store.Unprotect);

        vault.Upsert(Account("a1", "acc-1"));

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(vault.FilePath));
        Assert.DoesNotContain("SECRET-ACCESS-TOKEN", text);
        var again = new AccountVault(temp.File("data"), new UnixSecretStore(temp.File("keys")).Protect, new UnixSecretStore(temp.File("keys")).Unprotect);
        Assert.Equal("SECRET-ACCESS-TOKEN-a1", again.FindById("a1")!["tokens"]!["access_token"]!.GetValue<string>());
    }

    [Fact]
    public void DefaultDirectoryIsThePerUserConfigFolder()
    {
        var dir = AccountVault.DefaultDirectory();
        Assert.EndsWith("DshAccountManager", dir);
        if (!OperatingSystem.IsWindows()) { Assert.Contains(Path.Combine(".config", "DshAccountManager"), dir); }
    }

    [Fact]
    public void ConcurrentUpsertsAreAllKept()
    {
        using var temp = new TempDir();
        var vault = Make(temp.Path);

        Parallel.For(0, 8, i => vault.Upsert(Account("id" + i, "acc" + i)));

        Assert.Equal(8, vault.Load().Count);
    }
}
