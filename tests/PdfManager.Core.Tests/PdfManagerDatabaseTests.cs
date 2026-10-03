using Microsoft.Data.Sqlite;
using PdfManager.Core.Database;

namespace PdfManager.Core.Tests;

public sealed class PdfManagerDatabaseTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_databasePath + suffix);
        }
    }

    [Fact]
    public void Initialize_CreatesAllTables()
    {
        using var connection = OpenInitialized();

        var tables = Query(connection, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;");

        Assert.Equal(
            ["document_field_values", "document_links", "document_tags", "documents", "field_options",
             "fields", "page_texts", "saved_searches", "tag_attributes", "tags", "version_series"],
            tables);
        Assert.Equal(PdfManagerDatabase.SchemaVersion, PdfManagerDatabase.GetUserVersion(connection));
    }

    [Fact]
    public void Initialize_CanRunTwiceOnSameDatabase()
    {
        using (var first = OpenInitialized())
        {
            Execute(first, "INSERT INTO tags (name, normalized_name) VALUES ('東京', '東京');");
        }

        using var second = OpenInitialized();

        Assert.Equal(["東京"], Query(second, "SELECT name FROM tags;"));
    }

    [Fact]
    public void Initialize_UsesWalJournalMode()
    {
        using var connection = OpenInitialized();

        Assert.Equal(["wal"], Query(connection, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void DeletingDocument_CascadesToTagsAndFieldValues()
    {
        using var connection = OpenInitialized();
        InsertDocument(connection, "doc-1");
        Execute(connection, """
            INSERT INTO tags (id, name, normalized_name) VALUES (1, '上野', '上野');
            INSERT INTO document_tags (document_id, tag_id) VALUES ('doc-1', 1);
            INSERT INTO fields (id, name, type) VALUES (1, '年月', 'yearmonth');
            INSERT INTO document_field_values (document_id, field_id, value_date) VALUES ('doc-1', 1, '2026-09-01');
            """);

        Execute(connection, "DELETE FROM documents WHERE id = 'doc-1';");

        Assert.Equal(["0"], Query(connection, "SELECT COUNT(*) FROM document_tags;"));
        Assert.Equal(["0"], Query(connection, "SELECT COUNT(*) FROM document_field_values;"));
        Assert.Equal(["1"], Query(connection, "SELECT COUNT(*) FROM tags;"));
    }

    [Fact]
    public void Document_RejectsVersionNumberWithoutSeries()
    {
        using var connection = OpenInitialized();

        var ex = Assert.Throws<SqliteException>(() => Execute(connection, """
            INSERT INTO documents (id, title, file_hash, original_file_name, file_size, version_no, created_at, updated_at)
            VALUES ('doc-1', 't', 'h', 'f.pdf', 1, 2, '2026-09-28T00:00:00Z', '2026-09-28T00:00:00Z');
            """));
        Assert.Contains("CHECK", ex.Message);
    }

    [Fact]
    public void Initialize_UpgradesVersion1AndMergesTagsThatDifferOnlyInWidthOrCase()
    {
        using (var v1 = PdfManagerDatabase.Open(_databasePath))
        {
            Execute(v1, Schema.V1);
            Execute(v1, "PRAGMA user_version = 1;");
            InsertDocument(v1, "doc-1");
            Execute(v1, """
                INSERT INTO tags (id, name) VALUES (1, 'IODATA'), (2, 'ｉｏｄａｔａ'), (3, '上野');
                INSERT INTO document_tags (document_id, tag_id) VALUES ('doc-1', 1), ('doc-1', 2), ('doc-1', 3);
                """);
        }

        using var connection = OpenInitialized();

        Assert.Equal(PdfManagerDatabase.SchemaVersion, PdfManagerDatabase.GetUserVersion(connection));
        Assert.Equal(["IODATA|iodata", "上野|上野"], Query(connection, "SELECT name || '|' || normalized_name FROM tags ORDER BY id;"));
        Assert.Equal(["1", "3"], Query(connection, "SELECT tag_id FROM document_tags ORDER BY tag_id;"));
        Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO tags (name, normalized_name) VALUES ('Iodata', 'iodata');"));
    }

    // 日本語の部分一致検索に FTS5 の trigram トークナイザを使う前提(docs/03)を確認する
    [Fact]
    public void BundledSqlite_SupportsFts5TrigramForJapanese()
    {
        using var connection = OpenInitialized();
        Execute(connection, """
            CREATE VIRTUAL TABLE temp.fts_probe USING fts5(title, tokenize = 'trigram');
            INSERT INTO temp.fts_probe (title) VALUES ('上野●●●店売上202609'), ('商品A取扱説明書 第3版');
            """);

        Assert.Equal(["上野●●●店売上202609"], Query(connection, "SELECT title FROM temp.fts_probe WHERE fts_probe MATCH '店売上';"));
    }

    private SqliteConnection OpenInitialized()
    {
        var connection = PdfManagerDatabase.Open(_databasePath);
        PdfManagerDatabase.Initialize(connection);
        return connection;
    }

    private static void InsertDocument(SqliteConnection connection, string id) => Execute(connection, $"""
        INSERT INTO documents (id, title, file_hash, original_file_name, file_size, created_at, updated_at)
        VALUES ('{id}', 'title', 'hash', 'file.pdf', 100, '2026-09-28T00:00:00Z', '2026-09-28T00:00:00Z');
        """);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> Query(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var results = new List<string>();
        while (reader.Read())
        {
            results.Add(Convert.ToString(reader.GetValue(0))!);
        }
        return results;
    }
}
