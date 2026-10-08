using Microsoft.Data.Sqlite;
using PdfManager.Core.Text;

namespace PdfManager.Core.Database;

// docs/03_データ設計.md に対応。全文検索用の FTS5 索引は M3(検索)で追加する。
internal static class Schema
{
    // タググループ・順位・紐づけ(決定ログ R-11、Q-1〜Q-14)。
    // 選択肢型の項目は「順位 1 のタググループ(項目として左に表示)」に、選択肢はそのグループのタグに、文書の値はタグの付与に変える。
    // 属性(tag_attributes)は紐づけに変え、属性の付いたタグは「(項目名)に紐づくタグ」(順位 2)に入れる(Q-11)
    public static void MigrateToV7(SqliteConnection connection, SqliteTransaction transaction)
    {
        PdfManagerDatabase.Execute(connection, """
            CREATE TABLE tag_groups (
                id              INTEGER PRIMARY KEY,
                name            TEXT NOT NULL,
                normalized_name TEXT NOT NULL UNIQUE,
                rank            INTEGER NOT NULL CHECK (rank BETWEEN 0 AND 5),
                is_pinned       INTEGER NOT NULL DEFAULT 0,
                pin_order       INTEGER NOT NULL DEFAULT 0,
                sort_order      INTEGER NOT NULL DEFAULT 0
            );
            ALTER TABLE tags ADD COLUMN group_id INTEGER REFERENCES tag_groups(id) ON DELETE SET NULL;
            CREATE INDEX ix_tags_group ON tags(group_id);
            CREATE TABLE tag_links (
                child_id  INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                parent_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY (child_id, parent_id),
                CHECK (child_id <> parent_id)
            );
            CREATE INDEX ix_tag_links_parent ON tag_links(parent_id);
            """, transaction);

        // 項目を削除すると属性も消える(外部キー)ため、先に読んでおく
        var attributes = Rows(connection, transaction, "SELECT tag_id, option_id FROM tag_attributes ORDER BY tag_id, option_id;",
            r => (TagId: r.GetInt64(0), OptionId: r.GetInt64(1)));
        var fields = Rows(connection, transaction, "SELECT id, name FROM fields WHERE type = 'select' ORDER BY sort_order, id;",
            r => (Id: r.GetInt64(0), Name: r.GetString(1)));
        var optionTags = new Dictionary<long, long>();
        var optionFields = new Dictionary<long, (long FieldId, string FieldName)>();
        var order = 0;
        foreach (var field in fields)
        {
            var groupId = InsertGroup(connection, transaction, field.Name, rank: 1, pinned: true, order++);
            var options = Rows(connection, transaction, $"SELECT id, value FROM field_options WHERE field_id = {field.Id} ORDER BY sort_order, id;",
                r => (Id: r.GetInt64(0), Value: r.GetString(1)));
            foreach (var option in options)
            {
                optionTags[option.Id] = TagForGroup(connection, transaction, option.Value, groupId, field.Name);
                optionFields[option.Id] = (field.Id, field.Name);
            }

            var values = Rows(connection, transaction, $"SELECT document_id, value_text FROM document_field_values WHERE field_id = {field.Id} AND value_text IS NOT NULL;",
                r => (DocumentId: r.GetString(0), Value: r.GetString(1)));
            foreach (var value in values)
            {
                var index = options.FindIndex(o => o.Value == value.Value);
                var tagId = index >= 0 ? optionTags[options[index].Id] : TagForGroup(connection, transaction, value.Value, groupId, field.Name);
                Execute(connection, transaction, "INSERT OR IGNORE INTO document_tags (document_id, tag_id) VALUES ($doc, $tag);",
                    ("$doc", value.DocumentId), ("$tag", tagId));
                // ファイル名の先頭にこの項目を使っていた文書は、同じ名前のタグを使うように変える
                Execute(connection, transaction, """
                    UPDATE documents SET file_name_source_type = 'tag', file_name_source_id = $tag
                    WHERE id = $doc AND file_name_source_type = 'field' AND file_name_source_id = $field;
                    """, ("$doc", value.DocumentId), ("$tag", tagId), ("$field", field.Id));
            }
            Execute(connection, transaction, "DELETE FROM fields WHERE id = $id;", ("$id", field.Id));
        }

        var linkedGroups = new Dictionary<long, long>();
        foreach (var attribute in attributes)
        {
            if (!optionTags.TryGetValue(attribute.OptionId, out var parentId) || parentId == attribute.TagId)
            {
                continue;
            }
            var childGroup = Rows(connection, transaction, $"SELECT group_id FROM tags WHERE id = {attribute.TagId};", r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0)).Single();
            if (childGroup is null)
            {
                var (fieldId, fieldName) = optionFields[attribute.OptionId];
                if (!linkedGroups.TryGetValue(fieldId, out var groupId))
                {
                    groupId = linkedGroups[fieldId] = InsertGroup(connection, transaction, $"{fieldName}に紐づくタグ", rank: 2, pinned: false, order++);
                }
                Execute(connection, transaction, "UPDATE tags SET group_id = $group WHERE id = $id;", ("$group", groupId), ("$id", attribute.TagId));
                childGroup = groupId;
            }
            // 紐づけられるのは、順位 2 以降のタグから 1 つ上の順位のタグだけ
            Execute(connection, transaction, """
                INSERT OR IGNORE INTO tag_links (child_id, parent_id)
                SELECT $child, $parent
                WHERE (SELECT rank FROM tag_groups WHERE id = $childGroup) >= 2
                  AND (SELECT g.rank FROM tags t JOIN tag_groups g ON g.id = t.group_id WHERE t.id = $parent)
                      = (SELECT rank FROM tag_groups WHERE id = $childGroup) - 1;
                """, ("$child", attribute.TagId), ("$parent", parentId), ("$childGroup", childGroup));
        }

        PdfManagerDatabase.Execute(connection, """
            DROP TABLE tag_attributes;
            UPDATE sub_libraries SET needs_push = 1;
            """, transaction);
    }

    private static long InsertGroup(SqliteConnection connection, SqliteTransaction transaction, string name, int rank, bool pinned, int order)
    {
        var unique = name;
        for (var n = 2; Rows(connection, transaction, "SELECT 1 FROM tag_groups WHERE normalized_name = $n;", r => 1,
                 ("$n", NameNormalizer.Normalize(unique))).Count > 0; n++)
        {
            unique = $"{name}({n})";
        }
        return Rows(connection, transaction, """
            INSERT INTO tag_groups (name, normalized_name, rank, is_pinned, pin_order, sort_order)
            VALUES ($name, $normalized, $rank, $pinned, $order, $order) RETURNING id;
            """, r => r.GetInt64(0),
            ("$name", unique), ("$normalized", NameNormalizer.Normalize(unique)), ("$rank", rank), ("$pinned", pinned ? 1 : 0), ("$order", order)).Single();
    }

    // 同じ名前のタグがあればそれを使う(ほかのグループに入っていれば「名前(項目名)」で作る)
    private static long TagForGroup(SqliteConnection connection, SqliteTransaction transaction, string name, long groupId, string fieldName)
    {
        foreach (var candidate in new[] { name, $"{name}({fieldName})" })
        {
            var existing = Rows(connection, transaction, "SELECT id, group_id FROM tags WHERE normalized_name = $n;",
                r => (Id: r.GetInt64(0), GroupId: r.IsDBNull(1) ? (long?)null : r.GetInt64(1)), ("$n", NameNormalizer.Normalize(candidate))).SingleOrDefault();
            if (existing.Id == 0)
            {
                return Rows(connection, transaction, "INSERT INTO tags (name, normalized_name, group_id) VALUES ($name, $n, $group) RETURNING id;",
                    r => r.GetInt64(0), ("$name", candidate), ("$n", NameNormalizer.Normalize(candidate)), ("$group", groupId)).Single();
            }
            if (existing.GroupId is null || existing.GroupId == groupId)
            {
                Execute(connection, transaction, "UPDATE tags SET group_id = $group WHERE id = $id;", ("$group", groupId), ("$id", existing.Id));
                return existing.Id;
            }
        }
        throw new InvalidOperationException($"タグ「{name}」をタググループに移せませんでした。");
    }

    private static List<T> Rows<T>(SqliteConnection connection, SqliteTransaction transaction, string sql, Func<SqliteDataReader, T> map, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }
        return rows;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.ExecuteNonQuery();
    }

    // サブライブラリ(決定ログ S-1〜S-16)。サブにある文書は documents.storage_status = 'archived'、archive_volume_id = サブの ID で表す。
    // library_meta はサブ側で「最後に書き込んだときの合言葉」(sync_token)を持つ。メインとサブは同じ形のデータベースを使う。
    // ignored_paths は、2 か所で見つかったときに選ばなかった方(改行区切り、決定ログ S-15)
    public const string V6 = """
        CREATE TABLE sub_libraries (
            id                TEXT PRIMARY KEY,
            name              TEXT NOT NULL,
            normalized_name   TEXT NOT NULL UNIQUE,
            library_file_path TEXT NOT NULL,
            relative_path     TEXT,
            synced_token      TEXT,
            needs_push        INTEGER NOT NULL DEFAULT 0,
            ignored_paths     TEXT NOT NULL DEFAULT '',
            created_at        TEXT NOT NULL
        );
        CREATE TABLE library_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        CREATE INDEX ix_documents_archive_volume ON documents(archive_volume_id);
        """;

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
