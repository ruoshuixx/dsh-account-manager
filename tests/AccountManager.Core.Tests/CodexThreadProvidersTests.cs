using AccountManager.Core.Sharing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AccountManager.Core.Tests;

public sealed class CodexThreadProvidersTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "codex-threads-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] Ours = { "openai", CodexApiConfig.ProviderId, "api-legacy" };

    public CodexThreadProvidersTests() => Directory.CreateDirectory(_home);

    public void Dispose() { try { Directory.Delete(_home, true); } catch { /* best effort */ } }

    private SqliteConnection Open(string relativePath)
    {
        var path = Path.Combine(_home, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string[] Providers(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT model_provider FROM " + table + " ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var providers = new List<string>();
        while (reader.Read()) { providers.Add(reader.GetString(0)); }
        return providers.ToArray();
    }

    [Theory]
    [InlineData("openai")]
    [InlineData(CodexApiConfig.ProviderId)]
    public void RetagsBothDatabasesAndBacksUpTheirOriginalContents(string target)
    {
        foreach (var (path, table) in new[] { ("state_5.sqlite", "threads"), (Path.Combine("sqlite", "codex.db"), "local_thread_catalog") })
        {
            using var connection = Open(path);
            Execute(connection, "PRAGMA journal_mode=WAL; CREATE TABLE " + table + " (model_provider TEXT); INSERT INTO " + table
                + " VALUES ('openai'), ('ai-account-manager'), ('api-legacy'), ('foreign');");
        }

        Assert.Equal(4, CodexThreadProviders.Retag(_home, target, Ours, out var note));
        Assert.Equal("已让 4 条 Codex 对话在当前账号/API 下可见", note);
        foreach (var (path, table) in new[] { ("state_5.sqlite", "threads"), (Path.Combine("sqlite", "codex.db"), "local_thread_catalog") })
        {
            using var connection = Open(path);
            Assert.Equal(new[] { target, target, target, "foreign" }, Providers(connection, table));
            Assert.True(File.Exists(Path.Combine(_home, path + ".ai-account-manager.bak")));
            using var backup = Open(path + ".ai-account-manager.bak");
            Assert.Equal(new[] { "openai", CodexApiConfig.ProviderId, "api-legacy", "foreign" }, Providers(backup, table));
        }
        Assert.Equal(0, CodexThreadProviders.Retag(_home, target, Ours, out note));
        Assert.Null(note);
    }

    [Theory]
    [InlineData("CREATE TABLE other (model_provider TEXT)")]
    [InlineData("CREATE TABLE threads (other TEXT)")]
    [InlineData("CREATE TABLE local_thread_catalog (other TEXT)")]
    public void MissingSchemaIsSkippedSilently(string schema)
    {
        using (var state = Open("state_5.sqlite")) { Execute(state, schema); }
        using (var catalog = Open(Path.Combine("sqlite", "codex.db"))) { Execute(catalog, schema); }
        Assert.Equal(0, CodexThreadProviders.Retag(_home, "openai", Ours, out var note));
        Assert.Null(note);
        Assert.Empty(Directory.GetFiles(_home, "*.bak", SearchOption.AllDirectories));
    }

    [Fact]
    public void MissingFilesAreNotCreated()
    {
        Assert.Equal(0, CodexThreadProviders.Retag(_home, "openai", Ours, out var note));
        Assert.Null(note);
        Assert.Empty(Directory.GetFileSystemEntries(_home));
    }

    [Fact]
    public void ANewRetagOverwritesTheBackup()
    {
        using (var connection = Open("state_5.sqlite"))
        {
            Execute(connection, "CREATE TABLE threads (model_provider TEXT); INSERT INTO threads VALUES ('openai')");
        }
        Assert.Equal(1, CodexThreadProviders.Retag(_home, CodexApiConfig.ProviderId, Ours, out _));
        Assert.Equal(1, CodexThreadProviders.Retag(_home, "openai", Ours, out _));
        using var backup = Open("state_5.sqlite.ai-account-manager.bak");
        Assert.Equal(new[] { CodexApiConfig.ProviderId }, Providers(backup, "threads"));
    }

    [Fact]
    public void BusyDatabaseIsLeftAloneWithANote()
    {
        using var connection = Open("state_5.sqlite");
        Execute(connection, "CREATE TABLE threads (model_provider TEXT); INSERT INTO threads VALUES ('api-legacy')");
        Execute(connection, "BEGIN EXCLUSIVE");
        Assert.Equal(0, CodexThreadProviders.Retag(_home, "openai", Ours, out var note));
        Assert.Contains("正忙", note);
        Execute(connection, "ROLLBACK");
        Assert.Equal(new[] { "api-legacy" }, Providers(connection, "threads"));
    }
}
