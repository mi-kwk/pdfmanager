using Microsoft.Data.Sqlite;
using PdfManager.Core.Text;

namespace PdfManager.Core.Database;

// docs/03_データ設計.md に対応。全文検索用の FTS5 索引は M3(検索)で追加する。
internal static class Schema
{
    // 版の自由な名前(決定ログ N-3)
    public const string V5 = "ALTER TABLE documents ADD COLUMN version_label TEXT NOT NULL DEFAULT '';";

    // 利用者向けのファイル名(決定ログ C-1、H-1、H-2)。既存の文書は元のファイル名をそのまま使う
    public const string V4 = """
        ALTER TABLE documents ADD COLUMN file_name TEXT NOT NULL DEFAULT '';
        ALTER TABLE documents ADD COLUMN file_name_source_type TEXT CHECK (file_name_source_type IN ('title', 'tag', 'field'));
        ALTER TABLE documents ADD COLUMN file_name_source_id INTEGER;
        ALTER TABLE documents ADD COLUMN file_name_suffix TEXT NOT NULL DEFAULT '';
        UPDATE documents SET file_name = original_file_name WHERE file_name = '';
        CREATE INDEX ix_documents_file_name ON documents(file_name COLLATE NOCASE);
        """;

    // 自由タグの属性(決定ログ D-1)。選択肢・タグのどちらを削除してもひも付けは消える
    public const string V3 = """
        CREATE TABLE tag_attributes (
            tag_id    INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            option_id INTEGER NOT NULL REFERENCES field_options(id) ON DELETE CASCADE,
            PRIMARY KEY (tag_id, option_id)
        );
        CREATE INDEX ix_tag_attributes_option ON tag_attributes(option_id);
        """;

    // 表記ゆれを吸収した名前の列を追加する。既存のタグが正規化後に重複する場合は、古い方(id が小さい方)に統合する
    public static void MigrateToV2(SqliteConnection connection, SqliteTransaction transaction)
    {
        PdfManagerDatabase.Execute(connection, "ALTER TABLE tags ADD COLUMN normalized_name TEXT NOT NULL DEFAULT '';", transaction);

        var tags = new List<(long Id, string Name)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, name FROM tags ORDER BY id;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                tags.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        foreach (var group in tags.GroupBy(t => NameNormalizer.Normalize(t.Name)))
        {
            var keepId = group.First().Id;
            foreach (var duplicate in group.Skip(1))
            {
                using var merge = connection.CreateCommand();
                merge.Transaction = transaction;
                merge.CommandText = """
                    INSERT OR IGNORE INTO document_tags (document_id, tag_id) SELECT document_id, $keep FROM document_tags WHERE tag_id = $dup;
                    DELETE FROM tags WHERE id = $dup;
                    """;
                merge.Parameters.AddWithValue("$keep", keepId);
                merge.Parameters.AddWithValue("$dup", duplicate.Id);
                merge.ExecuteNonQuery();
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE tags SET normalized_name = $normalized WHERE id = $id;";
            update.Parameters.AddWithValue("$normalized", group.Key);
            update.Parameters.AddWithValue("$id", keepId);
            update.ExecuteNonQuery();
        }

        PdfManagerDatabase.Execute(connection, "CREATE UNIQUE INDEX ux_tags_normalized_name ON tags(normalized_name);", transaction);
    }

    public const string V1 = """
        CREATE TABLE version_series (
            id   TEXT PRIMARY KEY,
            name TEXT NOT NULL
        );

        CREATE TABLE documents (
            id                 TEXT PRIMARY KEY,
            title              TEXT NOT NULL,
            memo               TEXT NOT NULL DEFAULT '',
            file_hash          TEXT NOT NULL,
            original_file_name TEXT NOT NULL,
            file_size          INTEGER NOT NULL,
            page_count         INTEGER NOT NULL DEFAULT 0,
            rating             INTEGER NOT NULL DEFAULT 0 CHECK (rating BETWEEN 0 AND 5),
            view_count         INTEGER NOT NULL DEFAULT 0,
            last_viewed_at     TEXT,
            series_id          TEXT REFERENCES version_series(id),
            version_no         INTEGER,
            storage_status     TEXT NOT NULL DEFAULT 'local' CHECK (storage_status IN ('local', 'archived')),
            archive_volume_id  TEXT,
            created_at         TEXT NOT NULL,
            updated_at         TEXT NOT NULL,
            CHECK ((series_id IS NULL) = (version_no IS NULL)),
            UNIQUE (series_id, version_no)
        );
        CREATE INDEX ix_documents_file_hash ON documents(file_hash);

        CREATE TABLE fields (
            id         INTEGER PRIMARY KEY,
            name       TEXT NOT NULL UNIQUE,
            type       TEXT NOT NULL CHECK (type IN ('text', 'number', 'date', 'yearmonth', 'select')),
            sort_order INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE field_options (
            id         INTEGER PRIMARY KEY,
            field_id   INTEGER NOT NULL REFERENCES fields(id) ON DELETE CASCADE,
            value      TEXT NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            UNIQUE (field_id, value)
        );

        CREATE TABLE document_field_values (
            document_id  TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            field_id     INTEGER NOT NULL REFERENCES fields(id) ON DELETE CASCADE,
            value_text   TEXT,
            value_number REAL,
            value_date   TEXT,
            PRIMARY KEY (document_id, field_id)
        );
        CREATE INDEX ix_field_values_text   ON document_field_values(field_id, value_text);
        CREATE INDEX ix_field_values_number ON document_field_values(field_id, value_number);
        CREATE INDEX ix_field_values_date   ON document_field_values(field_id, value_date);

        CREATE TABLE tags (
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE
        );

        CREATE TABLE document_tags (
            document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            tag_id      INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            PRIMARY KEY (document_id, tag_id)
        );
        CREATE INDEX ix_document_tags_tag ON document_tags(tag_id);

        CREATE TABLE document_links (
            from_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            to_id   TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            type    TEXT NOT NULL CHECK (type IN ('next', 'related')),
            PRIMARY KEY (from_id, to_id, type),
            CHECK (from_id <> to_id)
        );
        CREATE INDEX ix_document_links_to ON document_links(to_id);

        CREATE TABLE page_texts (
            document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            page_no     INTEGER NOT NULL,
            text        TEXT NOT NULL,
            source      TEXT NOT NULL CHECK (source IN ('embedded', 'ocr')),
            PRIMARY KEY (document_id, page_no)
        );

        CREATE TABLE saved_searches (
            id             INTEGER PRIMARY KEY,
            name           TEXT NOT NULL,
            condition_json TEXT NOT NULL,
            sort_order     INTEGER NOT NULL DEFAULT 0
        );
        """;
}
