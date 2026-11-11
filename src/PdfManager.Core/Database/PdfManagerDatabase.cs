using Microsoft.Data.Sqlite;

namespace PdfManager.Core.Database;

public static class PdfManagerDatabase
{
    public const int SchemaVersion = 11;

    // 決定ログ HA-7: データベースを今の形にしたアプリのバージョン(library_meta に残す)。古いアプリで開かれたときに「何以降が必要か」を出すため
    public const string SchemaAppVersionKey = "schema_app_version";

    // 動いているアプリのバージョン。アプリの起動時に入れる(テストなどでは null のまま)
    public static string? AppVersion { get; set; }

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
        if (version < 8)
        {
            Execute(connection, Schema.V8, transaction);
        }
        if (version < 9)
        {
            Execute(connection, Schema.V9, transaction);
        }
        if (version < 10)
        {
            Execute(connection, Schema.V10, transaction);
        }
        if (version < 11)
        {
            Execute(connection, Schema.V11, transaction);
        }
        if (AppVersion is { } appVersion)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO library_meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$key", SchemaAppVersionKey);
            command.Parameters.AddWithValue("$value", appVersion);
            command.ExecuteNonQuery();
        }
        Execute(connection, $"PRAGMA user_version = {SchemaVersion};", transaction);
        transaction.Commit();
    }

    // 決定ログ HA-7: データベースを今の形にしたアプリのバージョン。記録がなければ null
    public static string? ReadSchemaAppVersion(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM library_meta WHERE key = $key;";
            command.Parameters.AddWithValue("$key", SchemaAppVersionKey);
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    // 決定ログ HA-4: データベースの形を変える前に、今のデータベースの控えを 1 つだけ取る(db フォルダの「pdfmanager.db.v11.bak」など)
    public static string SaveBeforeUpgrade(SqliteConnection connection, string databasePath, long version)
    {
        var folder = Path.GetDirectoryName(databasePath)!;
        var name = Path.GetFileName(databasePath);
        foreach (var previous in Directory.EnumerateFiles(folder, name + ".v*.bak"))
        {
            File.Delete(previous);
        }
        var copyPath = Path.Combine(folder, $"{name}.v{version}.bak");
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copyPath, Pooling = false }.ToString()))
        {
            copy.Open();
            connection.BackupDatabase(copy);
            // 控えはそれだけで開けるよう、WAL を使わない形にしておく
            using var command = copy.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = DELETE;";
            command.ExecuteNonQuery();
        }
        return copyPath;
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
