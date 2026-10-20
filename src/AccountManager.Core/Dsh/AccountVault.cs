using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountManager.Core.Security;

namespace AccountManager.Core.Dsh;

/// <summary>
/// Encrypted store of every Codex account this program manages (dsh itself only keeps the one current grant).
/// The plaintext is <c>{"version":1,"accounts":[ ...Cockpit-shaped objects... ]}</c>, encrypted with
/// <see cref="SecretProtector"/> (Windows DPAPI; elsewhere AES-GCM with a 0600 key file) and written
/// atomically with 0600 permissions after a short magic header. Accounts are addressed by their Cockpit
/// <c>id</c>; <c>account_id</c> lookups are provided because dsh's grant carries the account id.
/// </summary>
public sealed class AccountVault
{
    public const string FileName = "accounts.vault";
    public const int Version = 1;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DSHVAULT1\n");

    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;

    /// <param name="directory">Application data directory; the vault file is <c>&lt;directory&gt;/accounts.vault</c>.</param>
    /// <param name="protect">Encryption; defaults to <see cref="SecretProtector.Protect"/>.</param>
    /// <param name="unprotect">Decryption; defaults to <see cref="SecretProtector.Unprotect"/>.</param>
    public AccountVault(string directory, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = Path.GetFullPath(directory);
        _protect = protect ?? SecretProtector.Protect;
        _unprotect = unprotect ?? SecretProtector.Unprotect;
    }

    public string Directory { get; }

    public string FilePath => Path.Combine(Directory, FileName);

    /// <summary>Linux/macOS <c>~/.config/DshAccountManager</c>; Windows <c>%LOCALAPPDATA%\DshAccountManager</c>.</summary>
    public static string DefaultDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshAccountManager");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "DshAccountManager");
    }

    public static AccountVault CreateDefault() => new(DefaultDirectory());

    /// <summary>Returns deep copies of all stored accounts, in stored order. A missing vault is empty.</summary>
    public IReadOnlyList<JsonObject> Load()
        => ReadAccounts().Select(a => (JsonObject)a.DeepClone()).ToList();

    public JsonObject? FindById(string id)
        => Load().FirstOrDefault(a => IdOf(a) == id);

    /// <summary>The first account whose <c>account_id</c> equals <paramref name="accountId"/>.</summary>
    public JsonObject? FindByAccountId(string accountId)
        => Load().FirstOrDefault(a => AccountIdOf(a) == accountId);

    public IReadOnlyList<JsonObject> FindAllByAccountId(string accountId)
        => Load().Where(a => AccountIdOf(a) == accountId).ToList();

    /// <summary>Adds the account, or replaces the stored one with the same <c>id</c> in place. Returns true when added.</summary>
    public bool Upsert(JsonObject account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var id = IdOf(account) ?? throw new ArgumentException("The account has no id.", nameof(account));
        var added = false;
        Update(accounts =>
        {
            var copy = (JsonObject)account.DeepClone();
            var index = accounts.FindIndex(a => IdOf(a) == id);
            if (index >= 0) { accounts[index] = copy; }
            else { accounts.Add(copy); added = true; }
            return true;
        });
        return added;
    }

    /// <summary>Removes the account with this <c>id</c>; returns whether it existed.</summary>
    public bool Remove(string id)
    {
        var removed = false;
        Update(accounts =>
        {
            removed = accounts.RemoveAll(a => IdOf(a) == id) > 0;
            return removed;
        });
        return removed;
    }

    /// <summary>
    /// Locked read-modify-write over the account list. The mutation returns true to persist. An unreadable
    /// or undecryptable existing vault stops the operation without writing.
    /// </summary>
    public void Update(Func<List<JsonObject>, bool> mutate, int lockWaitMs = DshFileLock.DefaultWaitMs * 5)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        EnsureDirectory();
        DshFileLock.Run(FilePath, () =>
        {
            var accounts = ReadAccounts();
            if (!mutate(accounts)) { return; }
            Write(accounts);
        }, lockWaitMs);
    }

    private void EnsureDirectory()
    {
        if (System.IO.Directory.Exists(Directory)) { return; }
        if (OperatingSystem.IsWindows()) { System.IO.Directory.CreateDirectory(Directory); }
        else { System.IO.Directory.CreateDirectory(Directory, DshAtomicWrite.PrivateDirectoryMode); }
    }

    private List<JsonObject> ReadAccounts()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new List<JsonObject>(); }

        if (bytes.Length < Magic.Length || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new DshFormatException("The account vault is not in the expected format.");
        }

        byte[] plain;
        try { plain = _unprotect(bytes.AsSpan(Magic.Length).ToArray()); }
        catch (Exception ex) { throw new DshFormatException("The account vault could not be decrypted (wrong key or damaged file).", ex); }

        try
        {
            var root = JsonNode.Parse(plain) as JsonObject
                ?? throw new DshFormatException("The account vault content is not a JSON object.");
            if (root["version"]?.GetValue<int>() != Version) { throw new DshFormatException("The account vault has an unsupported version."); }
            var list = new List<JsonObject>();
            foreach (var item in (root["accounts"] as JsonArray) ?? new JsonArray())
            {
                if (item is JsonObject o) { list.Add(o); }
                else { throw new DshFormatException("The account vault holds a non-object account entry."); }
            }

            return list;
        }
        catch (JsonException ex) { throw new DshFormatException("The account vault content is not valid JSON.", ex); }
        catch (InvalidOperationException ex) { throw new DshFormatException("The account vault content is malformed.", ex); }
        catch (FormatException ex) { throw new DshFormatException("The account vault content is malformed.", ex); }
        finally { Array.Clear(plain); }
    }

    private void Write(List<JsonObject> accounts)
    {
        var array = new JsonArray();
        foreach (var a in accounts) { array.Add(a.DeepClone()); }
        var root = new JsonObject { ["version"] = Version, ["accounts"] = array };
        var plain = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        try
        {
            var cipher = _protect(plain);
            var file = new byte[Magic.Length + cipher.Length];
            Magic.CopyTo(file, 0);
            cipher.CopyTo(file, Magic.Length);
            DshAtomicWrite.WriteBytes(FilePath, file);
        }
        finally { Array.Clear(plain); }
    }

    private static string? IdOf(JsonObject account)
        => account["id"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static string? AccountIdOf(JsonObject account)
    {
        if (account["account_id"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) { return s; }
        return account["tokens"] is JsonObject t && t["account_id"] is JsonValue tv && tv.TryGetValue<string>(out var ts) && ts.Length > 0 ? ts : null;
    }
}
