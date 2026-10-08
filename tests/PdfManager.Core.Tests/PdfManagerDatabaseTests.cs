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
             "fields", "library_meta", "page_texts", "saved_searches", "sub_libraries", "tag_groups", "tag_links", "tags", "version_series"],
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

    // 決定ログ R-11・Q-11: 選択肢型の項目はタググループ(順位 1、項目)に、属性は紐づけに変わる
    [Fact]
    public void Initialize_UpgradesVersion6_SelectFieldsBecomeTagGroupsAndAttributesBecomeLinks()
    {
        using (var v6 = PdfManagerDatabase.Open(_databasePath))
        {
            Execute(v6, Schema.V1);
            using (var transaction = v6.BeginTransaction())
            {
                Schema.MigrateToV2(v6, transaction);
                transaction.Commit();
            }
            Execute(v6, Schema.V3 + Schema.V4 + Schema.V5 + Schema.V6 + "PRAGMA user_version = 6;");
            InsertDocument(v6, "doc-1");
            Execute(v6, """
                INSERT INTO fields (id, name, type, sort_order) VALUES (1, 'エリア', 'select', 1), (2, '年月', 'yearmonth', 2);
                INSERT INTO field_options (id, field_id, value, sort_order) VALUES (1, 1, '上野エリア', 1), (2, 1, '秋葉原エリア', 2);
                INSERT INTO tags (id, name, normalized_name) VALUES (1, '■■マネージャー', '■■マネージャー'), (2, 'IODATA', 'iodata');
                INSERT INTO tag_attributes (tag_id, option_id) VALUES (1, 1);
                INSERT INTO document_field_values (document_id, field_id, value_text) VALUES ('doc-1', 1, '上野エリア');
                INSERT INTO document_field_values (document_id, field_id, value_date) VALUES ('doc-1', 2, '2026-09-01');
                UPDATE documents SET file_name_source_type = 'field', file_name_source_id = 1 WHERE id = 'doc-1';
                """);
        }

        using var connection = OpenInitialized();

        Assert.Equal(["エリア|1|1", "エリアに紐づくタグ|2|0"], Query(connection, "SELECT name || '|' || rank || '|' || is_pinned FROM tag_groups ORDER BY id;"));
        Assert.Equal(["IODATA|", "■■マネージャー|エリアに紐づくタグ", "上野エリア|エリア", "秋葉原エリア|エリア"],
            Query(connection, "SELECT t.name || '|' || COALESCE(g.name, '') FROM tags t LEFT JOIN tag_groups g ON g.id = t.group_id ORDER BY t.name;"));
        Assert.Equal(["■■マネージャー→上野エリア"],
            Query(connection, "SELECT c.name || '→' || p.name FROM tag_links l JOIN tags c ON c.id = l.child_id JOIN tags p ON p.id = l.parent_id;"));
        Assert.Equal(["上野エリア"], Query(connection, "SELECT t.name FROM document_tags d JOIN tags t ON t.id = d.tag_id WHERE d.document_id = 'doc-1';"));
        Assert.Equal(["tag|上野エリア"], Query(connection, "SELECT d.file_name_source_type || '|' || t.name FROM documents d JOIN tags t ON t.id = d.file_name_source_id;"));
        Assert.Equal(["年月"], Query(connection, "SELECT name FROM fields;"));
        Assert.Equal(["2026-09-01"], Query(connection, "SELECT value_date FROM document_field_values;"));
        Assert.Empty(Query(connection, "SELECT name FROM sqlite_master WHERE name = 'tag_attributes';"));
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
