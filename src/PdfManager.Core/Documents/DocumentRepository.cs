using Microsoft.Data.Sqlite;
using PdfManager.Core.Catalog;

namespace PdfManager.Core.Documents;

public enum FileNameSource
{
    None,
    Title,
    Tag,
    Field,
}

public sealed record NewDocument(
    string Id,
    string Title,
    string FileHash,
    string OriginalFileName,
    long FileSize,
    int PageCount,
    string FileName,
    FileNameSource FileNameSource = FileNameSource.None,
    long? FileNameSourceId = null,
    string FileNameSuffix = "",
    string Memo = "");

public sealed record DocumentDetail(
    string Id,
    string Title,
    string Memo,
    int Rating,
    string FileName,
    FileNameSource FileNameSource,
    long? FileNameSourceId,
    string FileNameSuffix,
    string OriginalFileName,
    int PageCount,
    long FileSize,
    DateTimeOffset CreatedAt,
    long ViewCount,
    DateTimeOffset? LastViewedAt,
    IReadOnlyDictionary<long, FieldValue> FieldValues,
    IReadOnlyList<long> TagIds);

public sealed record DocumentUpdate(
    string Id,
    string Title,
    string Memo,
    int Rating,
    string FileName,
    FileNameSource FileNameSource,
    long? FileNameSourceId,
    string FileNameSuffix,
    IReadOnlyDictionary<long, FieldValue> FieldValues,
    IReadOnlyCollection<long> TagIds);

public enum TextSource
{
    Embedded,
    Ocr,
}

public sealed record PageText(int PageNo, string Text, TextSource Source);

public sealed class DocumentRepository(SqliteConnection connection)
{
    public long Count()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM documents;";
        return (long)command.ExecuteScalar()!;
    }

    public string? FindIdByHash(string fileHash)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM documents WHERE file_hash = $hash LIMIT 1;";
        command.Parameters.AddWithValue("$hash", fileHash);
        return command.ExecuteScalar() as string;
    }

    // 同じファイル名の警告用(決定ログ C-7)。Windows と同じく大文字・小文字は区別しない
    public HashSet<string> GetFileNames()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_name FROM documents;";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    public void Insert(
        NewDocument document,
        IReadOnlyList<PageText> pageTexts,
        DateTimeOffset now,
        IReadOnlyDictionary<long, FieldValue>? fieldValues = null,
        IReadOnlyCollection<long>? tagIds = null)
    {
        var timestamp = now.UtcDateTime.ToString("O");
        using var transaction = connection.BeginTransaction();

        using (var command = Command(transaction, """
            INSERT INTO documents (id, title, memo, file_hash, original_file_name, file_size, page_count,
                                   file_name, file_name_source_type, file_name_source_id, file_name_suffix, created_at, updated_at)
            VALUES ($id, $title, $memo, $hash, $name, $size, $pages, $fileName, $sourceType, $sourceId, $suffix, $now, $now);
            """))
        {
            command.Parameters.AddWithValue("$id", document.Id);
            command.Parameters.AddWithValue("$title", document.Title);
            command.Parameters.AddWithValue("$memo", document.Memo);
            command.Parameters.AddWithValue("$hash", document.FileHash);
            command.Parameters.AddWithValue("$name", document.OriginalFileName);
            command.Parameters.AddWithValue("$size", document.FileSize);
            command.Parameters.AddWithValue("$pages", document.PageCount);
            command.Parameters.AddWithValue("$fileName", document.FileName);
            command.Parameters.AddWithValue("$sourceType", SourceType(document.FileNameSource));
            command.Parameters.AddWithValue("$sourceId", (object?)document.FileNameSourceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$suffix", document.FileNameSuffix);
            command.Parameters.AddWithValue("$now", timestamp);
            command.ExecuteNonQuery();
        }

        using (var command = Command(transaction, "INSERT INTO page_texts (document_id, page_no, text, source) VALUES ($id, $page, $text, $source);"))
        {
            var page = command.Parameters.Add("$page", SqliteType.Integer);
            var text = command.Parameters.Add("$text", SqliteType.Text);
            var source = command.Parameters.Add("$source", SqliteType.Text);
            command.Parameters.AddWithValue("$id", document.Id);
            foreach (var pageText in pageTexts)
            {
                page.Value = pageText.PageNo;
                text.Value = pageText.Text;
                source.Value = pageText.Source == TextSource.Ocr ? "ocr" : "embedded";
                command.ExecuteNonQuery();
            }
        }

        InsertFieldValuesAndTags(transaction, document.Id, fieldValues, tagIds);
        transaction.Commit();
    }

    public IReadOnlyList<DocumentDetail> GetDetails(IReadOnlyCollection<string> ids)
    {
        var details = new List<DocumentDetail>();
        foreach (var id in ids)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT title, memo, rating, file_name, file_name_source_type, file_name_source_id, file_name_suffix,
                       original_file_name, page_count, file_size, created_at, view_count, last_viewed_at
                FROM documents WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                continue;
            }
            details.Add(new DocumentDetail(
                id,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? FileNameSource.None : reader.GetString(4) switch
                {
                    "title" => FileNameSource.Title,
                    "tag" => FileNameSource.Tag,
                    _ => FileNameSource.Field,
                },
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt64(9),
                DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt64(11),
                reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12), System.Globalization.CultureInfo.InvariantCulture),
                ReadFieldValues(id),
                ReadTagIds(id)));
        }
        return details;
    }

    // 項目の値とタグは「渡したものだけにする」(なくなったものは削除)
    public void Update(DocumentUpdate update, DateTimeOffset now)
    {
        using var transaction = connection.BeginTransaction();
        using (var command = Command(transaction, """
            UPDATE documents SET title = $title, memo = $memo, rating = $rating, file_name = $fileName,
                   file_name_source_type = $sourceType, file_name_source_id = $sourceId, file_name_suffix = $suffix, updated_at = $now
            WHERE id = $id;
            """))
        {
            command.Parameters.AddWithValue("$id", update.Id);
            command.Parameters.AddWithValue("$title", update.Title);
            command.Parameters.AddWithValue("$memo", update.Memo);
            command.Parameters.AddWithValue("$rating", update.Rating);
            command.Parameters.AddWithValue("$fileName", update.FileName);
            command.Parameters.AddWithValue("$sourceType", SourceType(update.FileNameSource));
            command.Parameters.AddWithValue("$sourceId", (object?)update.FileNameSourceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$suffix", update.FileNameSuffix);
            command.Parameters.AddWithValue("$now", now.UtcDateTime.ToString("O"));
            command.ExecuteNonQuery();
        }
        using (var command = Command(transaction, "DELETE FROM document_field_values WHERE document_id = $id; DELETE FROM document_tags WHERE document_id = $id;"))
        {
            command.Parameters.AddWithValue("$id", update.Id);
            command.ExecuteNonQuery();
        }
        InsertFieldValuesAndTags(transaction, update.Id, update.FieldValues, update.TagIds);
        transaction.Commit();
    }

    // 決定ログ K-3: ビューアのタブで新しく開いたとき
    public void RecordView(string id, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE documents SET view_count = view_count + 1, last_viewed_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", now.UtcDateTime.ToString("O"));
        command.ExecuteNonQuery();
    }

    // 項目の値・タグの付与・読み取った文字なども一緒に消える(外部キーの ON DELETE CASCADE)
    public void Delete(string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM documents WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private Dictionary<long, FieldValue> ReadFieldValues(string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT field_id, value_text, value_number, value_date FROM document_field_values WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var values = new Dictionary<long, FieldValue>();
        while (reader.Read())
        {
            values[reader.GetInt64(0)] = new FieldValue(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }
        return values;
    }

    private List<long> ReadTagIds(string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tag_id FROM document_tags WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var tagIds = new List<long>();
        while (reader.Read())
        {
            tagIds.Add(reader.GetInt64(0));
        }
        return tagIds;
    }

    private void InsertFieldValuesAndTags(SqliteTransaction transaction, string documentId, IReadOnlyDictionary<long, FieldValue>? fieldValues, IReadOnlyCollection<long>? tagIds)
    {
        foreach (var (fieldId, value) in fieldValues ?? new Dictionary<long, FieldValue>())
        {
            using var command = Command(transaction,
                "INSERT INTO document_field_values (document_id, field_id, value_text, value_number, value_date) VALUES ($id, $field, $text, $number, $date);");
            command.Parameters.AddWithValue("$id", documentId);
            command.Parameters.AddWithValue("$field", fieldId);
            command.Parameters.AddWithValue("$text", (object?)value.Text ?? DBNull.Value);
            command.Parameters.AddWithValue("$number", (object?)value.Number ?? DBNull.Value);
            command.Parameters.AddWithValue("$date", (object?)value.Date ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        foreach (var tagId in tagIds ?? [])
        {
            using var command = Command(transaction, "INSERT OR IGNORE INTO document_tags (document_id, tag_id) VALUES ($id, $tag);");
            command.Parameters.AddWithValue("$id", documentId);
            command.Parameters.AddWithValue("$tag", tagId);
            command.ExecuteNonQuery();
        }
    }

    private static object SourceType(FileNameSource source) => source switch
    {
        FileNameSource.Title => "title",
        FileNameSource.Tag => "tag",
        FileNameSource.Field => "field",
        _ => DBNull.Value,
    };

    private SqliteCommand Command(SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
