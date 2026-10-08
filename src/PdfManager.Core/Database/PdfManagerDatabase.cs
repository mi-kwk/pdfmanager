using Microsoft.Data.Sqlite;

namespace PdfManager.Core.Database;

public static class PdfManagerDatabase
{
    public const int SchemaVersion = 7;

    // SQLite 本体は Windows の winsqlite3.dll(決定ログ R-9)。RETURNING を使うため 3.35 以上が必要
    public static readonly Version MinimumSqliteVersion = new(3, 35, 0);

    public static Version GetSqliteVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return Version.Parse((string)command.ExecuteScalar()!);
    }

    public static SqliteConnection Open(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    // サブライブラリは外付け HDD などにあり、取り外したときに -wal ファイルが残らないよう WAL を使わない
    public static void Initialize(SqliteConnection connection, bool useWal = true)
    {
        Execute(connection, useWal ? "PRAGMA journal_mode = WAL;" : "PRAGMA journal_mode = DELETE;");

        var version = GetUserVersion(connection);
        if (version >= SchemaVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        if (version < 1)
        {
            Execute(connection, Schema.V1, transaction);
        }
        if (version < 2)
        {
            Schema.MigrateToV2(connection, transaction);
        }
        if (version < 3)
        {
            Execute(connection, Schema.V3, transaction);
        }
        if (version < 4)
        {
            Execute(connection, Schema.V4, transaction);
        }
        if (version < 5)
        {
            Execute(connection, Schema.V5, transaction);
        }
        if (version < 6)
        {
            Execute(connection, Schema.V6, transaction);
        }
        if (version < 7)
        {
            Schema.MigrateToV7(connection, transaction);
        }
        Execute(connection, $"PRAGMA user_version = {SchemaVersion};", transaction);
        transaction.Commit();
    }

    public static long GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
