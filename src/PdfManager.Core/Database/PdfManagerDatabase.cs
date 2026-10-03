using Microsoft.Data.Sqlite;

namespace PdfManager.Core.Database;

public static class PdfManagerDatabase
{
    public const int SchemaVersion = 5;

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

    public static void Initialize(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode = WAL;");

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
