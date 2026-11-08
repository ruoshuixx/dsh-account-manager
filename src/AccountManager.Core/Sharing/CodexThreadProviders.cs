using Microsoft.Data.Sqlite;

namespace AccountManager.Core.Sharing;

/// <summary>Keeps managed Codex conversations visible when the active provider changes.</summary>
public static class CodexThreadProviders
{
    public static int Retag(string codexHome, string targetProvider, IReadOnlyCollection<string> ours, out string? note)
    {
        note = null;
        var changed = 0;
        string? failure = null;
        var providers = ours.Where(p => p != targetProvider).Distinct(StringComparer.Ordinal).ToArray();
        if (providers.Length == 0 || string.IsNullOrWhiteSpace(targetProvider)) { return 0; }

        foreach (var (relativePath, table) in new[]
        {
            ("state_5.sqlite", "threads"),
            (Path.Combine("sqlite", "codex.db"), "local_thread_catalog"),
        })
        {
            try
            {
                var path = Path.Combine(codexHome, relativePath);
                if (!File.Exists(path)) { continue; }
                changed += RetagDatabase(path, table, targetProvider, providers);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            {
                failure = "Codex 对话数据库正忙，已跳过可见性更新。";
            }
            catch (Exception)
            {
                failure ??= "无法更新 Codex 对话可见性（不影响切换）。";
            }
        }

        if (changed > 0) { note = "已让 " + changed + " 条 Codex 对话在当前账号/API 下可见"; }
        if (failure is not null) { note = note is null ? failure : note + "；" + failure; }
        return changed;
    }

    private static int RetagDatabase(string path, string table, string targetProvider, IReadOnlyList<string> ours)
    {
        using var connection = Open(path, SqliteOpenMode.ReadWrite);
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(\"" + table + "\")";
            using var reader = schema.ExecuteReader();
            var found = false;
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "model_provider", StringComparison.OrdinalIgnoreCase)) { found = true; break; }
            }
            if (!found) { return 0; }
        }

        using var command = connection.CreateCommand();
        var parameters = ours.Select((_, i) => "$p" + i).ToArray();
        for (var i = 0; i < ours.Count; i++) { command.Parameters.AddWithValue(parameters[i], ours[i]); }
        command.Parameters.AddWithValue("$target", targetProvider);
        var predicate = "model_provider <> $target AND model_provider IN (" + string.Join(",", parameters) + ")";
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM \"" + table + "\" WHERE " + predicate + ")";
        if (Convert.ToInt64(command.ExecuteScalar()) == 0) { return 0; }

        // Back up the live database, including WAL contents, before opening a write transaction.
        using (var backup = Open(path + ".ai-account-manager.bak", SqliteOpenMode.ReadWriteCreate))
        {
            connection.BackupDatabase(backup);
        }

        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "UPDATE \"" + table + "\" SET model_provider = $target WHERE " + predicate;
        var changed = command.ExecuteNonQuery();
        transaction.Commit();
        return changed;
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 5000";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
