using Microsoft.Data.Sqlite;
using PdfManager.Core.Database;

namespace PdfManager.Core.Library;

// サブが、メインが最後に書き込んだあとにほかの PC などで変更されていた(決定ログ S-12、S-14)
public sealed class SubLibraryChangedElsewhereException(string message) : Exception(message);

// メインの内容をサブへ書き込む(決定ログ S-11、S-12)。
// この PC ではメインのデータベースを使って表示・検索し、サブにはその写しを書き込んで、サブだけで完結するようにする。
// 項目・タグの定義はメインと同じ ID のまま写す
public static class SubLibrarySync
{
    private const string TokenKey = "sync_token";

    private const string DocumentColumns = """
        id, title, memo, file_hash, original_file_name, file_size, page_count, rating, view_count, last_viewed_at,
        series_id, version_no, storage_status, archive_volume_id, created_at, updated_at,
        file_name, file_name_source_type, file_name_source_id, file_name_suffix, version_label
        """;

    public static string? ReadToken(LibraryInfo sub)
    {
        using var connection = PdfManagerDatabase.Open(sub.DataFolder.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM library_meta WHERE key = '{TokenKey}';";
        return command.ExecuteScalar() as string;
    }

    public static string WriteNewToken(LibraryInfo sub)
    {
        var token = NewToken();
        using var connection = PdfManagerDatabase.Open(sub.DataFolder.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO library_meta (key, value) VALUES ('{TokenKey}', $token) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$token", token);
        command.ExecuteNonQuery();
        return token;
    }

    // documentIds が null なら、そのサブの文書すべてを書き直す。
    // 渡した文書のうち、もうこのサブにないもの(移した・削除した)はサブから消す。版の系列は呼び出し側でまとめて渡すこと
    public static void Push(SqliteConnection main, SubLibraryRecord record, LibraryInfo sub, IReadOnlyCollection<string>? documentIds)
    {
        Execute(main, "ATTACH DATABASE $path AS sub;", null, ("$path", sub.DataFolder.DatabasePath));
        try
        {
            using var transaction = main.BeginTransaction();
            var token = Scalar(main, transaction, $"SELECT value FROM sub.library_meta WHERE key = '{TokenKey}';");
            if (token != record.SyncedToken)
            {
                throw new SubLibraryChangedElsewhereException(
                    $"サブライブラリ「{record.Name}」は、この PC で最後に書き込んだあとに、ほかの PC などで変更されています。この PC では表示のみにしています。");
            }

            PushDefinitions(main, transaction);
            PushDocuments(main, transaction, record.Id, documentIds);

            var newToken = NewToken();
            Execute(main, $"INSERT INTO sub.library_meta (key, value) VALUES ('{TokenKey}', $token) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                transaction, ("$token", newToken));
            Execute(main, "UPDATE main.sub_libraries SET synced_token = $token WHERE id = $id;", transaction, ("$token", newToken), ("$id", record.Id));
            transaction.Commit();
        }
        finally
        {
            Execute(main, "DETACH DATABASE sub;", null);
        }
    }

    // 定義はすべて写す。名前を入れ替えたときに重複の制約に引っかからないよう、いったん仮の名前にしてから上書きする
    private static void PushDefinitions(SqliteConnection main, SqliteTransaction transaction) => Execute(main, """
        DELETE FROM sub.tag_links;
        DELETE FROM sub.fields WHERE id NOT IN (SELECT id FROM main.fields);
        DELETE FROM sub.field_options WHERE id NOT IN (SELECT id FROM main.field_options);
        DELETE FROM sub.tags WHERE id NOT IN (SELECT id FROM main.tags);
        DELETE FROM sub.tag_groups WHERE id NOT IN (SELECT id FROM main.tag_groups);
        UPDATE sub.fields SET name = char(1) || id;
        UPDATE sub.field_options SET value = char(1) || id;
        UPDATE sub.tags SET name = char(1) || id, normalized_name = char(1) || id;
        UPDATE sub.tag_groups SET name = char(1) || id, normalized_name = char(1) || id;
        INSERT INTO sub.tag_groups (id, name, normalized_name, rank, is_pinned, pin_order, sort_order)
            SELECT id, name, normalized_name, rank, is_pinned, pin_order, sort_order FROM main.tag_groups WHERE true
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, normalized_name = excluded.normalized_name, rank = excluded.rank,
                is_pinned = excluded.is_pinned, pin_order = excluded.pin_order, sort_order = excluded.sort_order;
        INSERT INTO sub.fields (id, name, type, sort_order) SELECT id, name, type, sort_order FROM main.fields WHERE true
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, type = excluded.type, sort_order = excluded.sort_order;
        INSERT INTO sub.field_options (id, field_id, value, sort_order) SELECT id, field_id, value, sort_order FROM main.field_options WHERE true
            ON CONFLICT(id) DO UPDATE SET field_id = excluded.field_id, value = excluded.value, sort_order = excluded.sort_order;
        INSERT INTO sub.tags (id, name, normalized_name, group_id) SELECT id, name, normalized_name, group_id FROM main.tags WHERE true
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, normalized_name = excluded.normalized_name, group_id = excluded.group_id;
        INSERT INTO sub.tag_links (child_id, parent_id) SELECT child_id, parent_id FROM main.tag_links;
        """, transaction);

    private static void PushDocuments(SqliteConnection main, SqliteTransaction transaction, string subId, IReadOnlyCollection<string>? documentIds)
    {
        Execute(main, "CREATE TEMP TABLE IF NOT EXISTS push_ids (id TEXT PRIMARY KEY); DELETE FROM temp.push_ids;", transaction);
        if (documentIds is null)
        {
            Execute(main, "INSERT INTO temp.push_ids SELECT id FROM main.documents WHERE archive_volume_id = $sub UNION SELECT id FROM sub.documents;",
                transaction, ("$sub", subId));
        }
        else
        {
            using var insert = main.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO temp.push_ids (id) VALUES ($id);";
            var parameter = insert.Parameters.Add("$id", SqliteType.Text);
            foreach (var id in documentIds)
            {
                parameter.Value = id;
                insert.ExecuteNonQuery();
            }
        }

        var updates = string.Join(", ", DocumentColumns.Split(',').Select(c => c.Trim()).Where(c => c != "id").Select(c => $"{c} = excluded.{c}"));
        Execute(main, $"""
            CREATE TEMP TABLE IF NOT EXISTS push_targets (id TEXT PRIMARY KEY);
            DELETE FROM temp.push_targets;
            INSERT INTO temp.push_targets SELECT id FROM main.documents WHERE archive_volume_id = $sub AND id IN (SELECT id FROM temp.push_ids);

            DELETE FROM sub.documents WHERE id IN (SELECT id FROM temp.push_ids) AND id NOT IN (SELECT id FROM temp.push_targets);
            UPDATE sub.documents SET series_id = NULL, version_no = NULL WHERE id IN (SELECT id FROM temp.push_targets);
            INSERT INTO sub.version_series (id, name)
                SELECT id, name FROM main.version_series WHERE id IN (SELECT series_id FROM main.documents WHERE id IN (SELECT id FROM temp.push_targets))
                ON CONFLICT(id) DO UPDATE SET name = excluded.name;
            INSERT INTO sub.documents ({DocumentColumns})
                SELECT {DocumentColumns} FROM main.documents WHERE id IN (SELECT id FROM temp.push_targets)
                ON CONFLICT(id) DO UPDATE SET {updates};

            DELETE FROM sub.document_field_values WHERE document_id IN (SELECT id FROM temp.push_targets);
            INSERT INTO sub.document_field_values (document_id, field_id, value_text, value_number, value_date)
                SELECT document_id, field_id, value_text, value_number, value_date FROM main.document_field_values
                WHERE document_id IN (SELECT id FROM temp.push_targets);
            DELETE FROM sub.document_tags WHERE document_id IN (SELECT id FROM temp.push_targets);
            INSERT INTO sub.document_tags (document_id, tag_id)
                SELECT document_id, tag_id FROM main.document_tags WHERE document_id IN (SELECT id FROM temp.push_targets);
            DELETE FROM sub.document_links WHERE from_id IN (SELECT id FROM temp.push_targets) OR to_id IN (SELECT id FROM temp.push_targets);
            INSERT OR IGNORE INTO sub.document_links (from_id, to_id, type)
                SELECT from_id, to_id, type FROM main.document_links
                WHERE (from_id IN (SELECT id FROM temp.push_targets) OR to_id IN (SELECT id FROM temp.push_targets))
                  AND from_id IN (SELECT id FROM sub.documents) AND to_id IN (SELECT id FROM sub.documents);
            -- 読み取った文字は取り込み後に変わらないため、まだないものだけ写す
            INSERT OR IGNORE INTO sub.page_texts (document_id, page_no, text, source)
                SELECT p.document_id, p.page_no, p.text, p.source FROM main.page_texts p
                WHERE p.document_id IN (SELECT id FROM temp.push_targets)
                  AND NOT EXISTS (SELECT 1 FROM sub.page_texts s WHERE s.document_id = p.document_id);

            DELETE FROM sub.version_series WHERE id NOT IN (SELECT series_id FROM sub.documents WHERE series_id IS NOT NULL);
            """, transaction, ("$sub", subId));
    }

    private static string NewToken() => Guid.NewGuid().ToString("N");

    private static string? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
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
}
