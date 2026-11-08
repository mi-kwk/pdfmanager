using Microsoft.Data.Sqlite;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Library;
using PdfManager.Core.Pdf;
using PdfManager.Core.Search;
using PdfManager.Core.Text;

namespace PdfManager.Core.Export;

// 決定ログ EB-10: 中身が同じ PDF がすでにあるときの扱い
public enum HandoffChoice
{
    // タグは足す・ほかは今のまま(初め)
    AddTags,
    // 相手に合わせる
    Overwrite,
    // そのまま
    Keep,
}

// 中身が同じ PDF がすでにあり、情報が違うもの。Writable が false なら、保存先のサブがつながっていないため直せない
public sealed record HandoffExisting(HandoffDocument Document, string LocalId, string LocalTitle, string? LocalStorageId, IReadOnlyList<string> Differences, bool Writable);

public sealed record HandoffImportPlan(
    IReadOnlyList<HandoffDocument> NewDocuments,
    IReadOnlyList<HandoffExisting> Different,
    int Same,
    IReadOnlyList<string> MissingFiles,
    long NewBytes,
    IReadOnlyList<string> NewGroups,
    IReadOnlyList<string> NewTags,
    IReadOnlyList<string> NewFields,
    bool SameLibrary,
    long? FreeBytes)
{
    // 空き容量には少し余裕を見る
    public bool HasEnoughSpace => FreeBytes is not { } free || free - NewBytes >= 100L << 20;
}

public sealed record HandoffImportResult(int Added, int Updated, IReadOnlyList<string> Problems, bool Cancelled);

// 受け渡しの取り込み(F-52、決定ログ EB-9〜EB-11)。新しい PDF はメインに登録し、中身が同じ PDF は選んだ扱いで情報を直す。
// タグ・グループ・補助項目は名前(表記ゆれ・別名を含む)で突き合わせ、ないものは作る。開いた回数などの記録は持ち込まない
public static class HandoffImporter
{
    private sealed record LocalDocument(string Id, string Title, string Memo, int Rating, string FileName, string FileNameSuffix, string? StorageId,
        HashSet<long> Tags, Dictionary<long, (string? Text, double? Number, string? Date)> Fields);

    // 受け取る側の定義(名前 → 番号)
    private sealed class Definitions
    {
        public Dictionary<string, long> Groups { get; } = [];

        public Dictionary<string, long> Tags { get; } = [];

        public Dictionary<string, (long Id, string Type)> Fields { get; } = [];

        public static Definitions Read(SqliteConnection connection, SqliteTransaction? transaction = null)
        {
            var definitions = new Definitions();
            foreach (var (name, id) in Rows(connection, transaction, "SELECT normalized_name, id FROM tag_groups;", r => (r.GetString(0), r.GetInt64(1))))
            {
                definitions.Groups[name] = id;
            }
            foreach (var (name, id) in Rows(connection, transaction, "SELECT normalized_name, id FROM tags UNION ALL SELECT normalized_name, tag_id FROM tag_aliases;",
                         r => (r.GetString(0), r.GetInt64(1))))
            {
                definitions.Tags.TryAdd(name, id);
            }
            foreach (var (name, id, type) in Rows(connection, transaction, "SELECT name, id, type FROM fields;", r => (r.GetString(0), r.GetInt64(1), r.GetString(2))))
            {
                definitions.Fields[name] = (id, type);
            }
            return definitions;
        }

        public long? Tag(string name) => Tags.TryGetValue(NameNormalizer.Normalize(name), out var id) ? id : null;

        public long? Field(string name, IReadOnlyDictionary<string, string> types) =>
            Fields.TryGetValue(name, out var field) && types.TryGetValue(name, out var type) && field.Type == type ? field.Id : null;
    }

    public static HandoffImportPlan Plan(LibraryStorage storage, HandoffPackage package)
    {
        var manifest = package.Manifest;
        using var connection = PdfManagerDatabase.Open(storage.Main.DataFolder.DatabasePath);
        var definitions = Definitions.Read(connection);
        var types = manifest.Fields.ToDictionary(f => f.Name, f => f.Type);
        var newDocuments = new List<HandoffDocument>();
        var different = new List<HandoffExisting>();
        var missing = new List<string>();
        var same = 0;
        long bytes = 0;
        var matched = new HashSet<string>();
        foreach (var document in manifest.Documents)
        {
            if (FindLocal(connection, document) is { } local && matched.Add(local.Id))
            {
                var differences = Compare(local, document, definitions, types);
                if (differences.Count == 0)
                {
                    same++;
                }
                else
                {
                    different.Add(new HandoffExisting(document, local.Id, local.Title, local.StorageId, differences, storage.IsAvailable(local.StorageId)));
                }
                continue;
            }
            if (package.PdfPath(document) is null)
            {
                missing.Add(document.Title);
                continue;
            }
            newDocuments.Add(document);
            bytes += document.FileSize;
        }

        var newGroups = manifest.Groups.Where(g => !definitions.Groups.ContainsKey(NameNormalizer.Normalize(g.Name))).Select(g => g.Name).ToList();
        var newTags = manifest.Tags.Where(t => definitions.Tag(t.Name) is null).Select(t => t.Name).ToList();
        var newFields = manifest.Fields.Where(f => !definitions.Fields.ContainsKey(f.Name)).Select(f => f.Name).ToList();
        long? free = null;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(storage.Main.DataFolder.Root)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
        return new HandoffImportPlan(newDocuments, different, same, missing, bytes, newGroups, newTags, newFields,
            manifest.LibraryId == storage.Main.Id.ToString("D"), free);
    }

    // 同じ文書(ID が同じ)、なければ中身が同じ PDF
    private static LocalDocument? FindLocal(SqliteConnection connection, HandoffDocument document)
    {
        var id = Rows(connection, null, "SELECT id FROM documents WHERE id = $id UNION ALL SELECT id FROM documents WHERE file_hash = $hash LIMIT 1;",
            r => r.GetString(0), ("$id", document.Id), ("$hash", document.FileHash)).FirstOrDefault();
        if (id is null)
        {
            return null;
        }
        var row = Rows(connection, null, "SELECT title, memo, rating, file_name, file_name_suffix, archive_volume_id FROM documents WHERE id = $id;",
            r => (Title: r.GetString(0), Memo: r.GetString(1), Rating: r.GetInt32(2), FileName: r.GetString(3), Suffix: r.GetString(4), Storage: r.IsDBNull(5) ? null : r.GetString(5)),
            ("$id", id)).Single();
        var tags = Rows(connection, null, "SELECT tag_id FROM document_tags WHERE document_id = $id;", r => r.GetInt64(0), ("$id", id)).ToHashSet();
        var fields = Rows(connection, null, "SELECT field_id, value_text, value_number, value_date FROM document_field_values WHERE document_id = $id;",
            r => (Id: r.GetInt64(0), Text: r.IsDBNull(1) ? null : r.GetString(1), Number: r.IsDBNull(2) ? (double?)null : r.GetDouble(2), Date: r.IsDBNull(3) ? null : r.GetString(3)),
            ("$id", id)).ToDictionary(f => f.Id, f => (f.Text, f.Number, f.Date));
        return new LocalDocument(id, row.Title, row.Memo, row.Rating, row.FileName, row.Suffix, row.Storage, tags, fields);
    }

    private static List<string> Compare(LocalDocument local, HandoffDocument theirs, Definitions definitions, IReadOnlyDictionary<string, string> types)
    {
        var differences = new List<string>();
        if (local.Title != theirs.Title)
        {
            differences.Add($"タイトル「{local.Title}」→「{theirs.Title}」");
        }
        if (local.FileName != theirs.FileName || local.FileNameSuffix != theirs.FileNameSuffix)
        {
            differences.Add($"ファイル名「{local.FileName}」→「{theirs.FileName}」");
        }
        if (local.Rating != theirs.Rating)
        {
            differences.Add($"評価 {Stars(local.Rating)}→{Stars(theirs.Rating)}");
        }
        if (local.Memo != theirs.Memo)
        {
            differences.Add("メモ");
        }
        var theirTags = theirs.Tags.Select(name => (Name: name, Id: definitions.Tag(name))).ToList();
        var added = theirTags.Where(t => t.Id is not { } id || !local.Tags.Contains(id)).Select(t => t.Name).ToList();
        var removed = local.Tags.Count(id => theirTags.All(t => t.Id != id));
        if (added.Count > 0 || removed > 0)
        {
            differences.Add("タグ" + (added.Count > 0 ? $" +{string.Join("・", added)}" : "") + (removed > 0 ? $"(相手にないタグ {removed} 個)" : ""));
        }
        var theirFields = theirs.FieldValues.Select(v => (Id: definitions.Field(v.Field, types), v)).Where(x => x.Id is not null)
            .ToDictionary(x => x.Id!.Value, x => (x.v.Text, x.v.Number, x.v.Date));
        if (theirFields.Count != local.Fields.Count || theirFields.Any(f => !local.Fields.TryGetValue(f.Key, out var mine) || mine != f.Value)
            || theirs.FieldValues.Any(v => definitions.Field(v.Field, types) is null && !definitions.Fields.ContainsKey(v.Field)))
        {
            differences.Add("補助項目");
        }
        return differences;
    }

    private static string Stars(int rating) => rating == 0 ? "未評価" : "★" + rating;

    public static async Task<HandoffImportResult> ImportAsync(LibraryStorage storage, HandoffPackage package, HandoffImportPlan plan,
        IReadOnlyDictionary<string, HandoffChoice> choices, DateTimeOffset now, IProgress<MoveProgress>? progress = null, CancellationToken cancellation = default)
    {
        var manifest = package.Manifest;
        var main = storage.Main.DataFolder;
        var problems = new List<string>();
        var added = 0;
        var updated = 0;
        var types = manifest.Fields.ToDictionary(f => f.Name, f => f.Type);
        // 受け渡しの文書 ID → この PC の文書 ID
        var mapped = new Dictionary<string, string>();

        using var connection = PdfManagerDatabase.Open(main.DatabasePath);
        Definitions definitions;
        using (var transaction = connection.BeginTransaction())
        {
            AddDefinitions(connection, transaction, manifest);
            definitions = Definitions.Read(connection, transaction);
            transaction.Commit();
        }

        // 新しい PDF(メインに登録する、決定ログ S-2・EB-11)。版は番号の順に入れる
        var total = plan.NewDocuments.Count + plan.Different.Count;
        var done = 0;
        var thumbnails = new List<string>();
        foreach (var document in plan.NewDocuments.OrderBy(d => d.SeriesId ?? d.Id).ThenBy(d => d.VersionNo ?? 0))
        {
            if (cancellation.IsCancellationRequested)
            {
                return new HandoffImportResult(added, updated, problems, Cancelled: true);
            }
            progress?.Report(new MoveProgress(done++, total, document.Title));
            var source = package.PdfPath(document);
            if (source is null)
            {
                problems.Add($"・{document.Title}:受け渡しに PDF が見つかりません");
                continue;
            }
            if (HandoffFormat.Hash(source) != document.FileHash)
            {
                problems.Add($"・{document.Title}:PDF の中身が受け渡しの情報と一致しません(コピーの途中で壊れた可能性があります)");
                continue;
            }
            var stored = main.GetPdfPath(document.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
            File.Copy(source, stored + ".copying", overwrite: true);
            File.Move(stored + ".copying", stored, overwrite: true);
            try
            {
                using var transaction = connection.BeginTransaction();
                InsertDocument(connection, transaction, document, definitions, types);
                transaction.Commit();
            }
            catch
            {
                TryDelete(stored);
                throw;
            }
            mapped[document.Id] = document.Id;
            thumbnails.Add(document.Id);
            added++;
        }

        // 中身が同じ PDF(決定ログ EB-10)。サブにある文書は、サブにも書き込む(決定ログ S-12)
        foreach (var existing in plan.Different)
        {
            mapped[existing.Document.Id] = existing.LocalId;
        }
        foreach (var document in manifest.Documents.Where(d => !mapped.ContainsKey(d.Id)))
        {
            if (FindLocal(connection, document) is { } local)
            {
                mapped[document.Id] = local.Id;
            }
        }
        var updates = plan.Different.Where(e => choices.GetValueOrDefault(e.Document.Id, HandoffChoice.AddTags) != HandoffChoice.Keep).ToList();
        var links = manifest.Links.Where(l => mapped.ContainsKey(l.From) && mapped.ContainsKey(l.To)).ToList();
        var touched = updates.Select(u => u.LocalId).Concat(links.SelectMany(l => new[] { mapped[l.From], mapped[l.To] })).Distinct().ToList();
        var change = storage.BeginChange(touched);
        try
        {
            foreach (var existing in updates)
            {
                progress?.Report(new MoveProgress(done++, total, existing.Document.Title));
                if (!storage.IsAvailable(existing.LocalStorageId))
                {
                    problems.Add($"・{existing.LocalTitle}:{storage.UnavailableMessage(existing.LocalStorageId)}");
                    continue;
                }
                using var transaction = connection.BeginTransaction();
                UpdateDocument(connection, transaction, existing, choices.GetValueOrDefault(existing.Document.Id, HandoffChoice.AddTags), definitions, types, now);
                transaction.Commit();
                updated++;
            }
            AddLinks(connection, storage, links, mapped);
        }
        finally
        {
            problems.AddRange(storage.CompleteChange(change).Select(p => "・" + p));
        }
        progress?.Report(new MoveProgress(total, total, ""));

        // サムネイルと 1 ページ目の指紋は、取り込む側で作り直す(決定ログ EB-11)
        foreach (var id in thumbnails)
        {
            try
            {
                await PdfPageRenderer.RenderFirstPageToJpegAsync(main.GetPdfPath(id), main.GetThumbnailPath(id));
                await DuplicateFinder.SaveFromThumbnailAsync(main, id);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // サムネイルがなくても登録はできている(一覧では仮の表示になる)
            }
        }
        return new HandoffImportResult(added, updated, problems, Cancelled: false);
    }

    // 決定ログ EB-11: ないグループ・タグ・補助項目を作る。タグの紐づけも足す
    private static void AddDefinitions(SqliteConnection connection, SqliteTransaction transaction, HandoffManifest manifest)
    {
        var definitions = Definitions.Read(connection, transaction);
        var order = Rows(connection, transaction, "SELECT COALESCE(MAX(sort_order), 0) FROM tag_groups;", r => r.GetInt64(0)).Single();
        foreach (var group in manifest.Groups)
        {
            var normalized = NameNormalizer.Normalize(group.Name);
            if (!definitions.Groups.ContainsKey(normalized))
            {
                definitions.Groups[normalized] = Rows(connection, transaction, """
                    INSERT INTO tag_groups (name, normalized_name, rank, is_pinned, pin_order, sort_order) VALUES ($name, $n, $rank, $pinned, $pin, $sort) RETURNING id;
                    """, r => r.GetInt64(0), ("$name", group.Name), ("$n", normalized), ("$rank", Math.Clamp(group.Rank, 0, 5)), ("$pinned", group.IsPinned ? 1 : 0),
                    ("$pin", group.PinOrder), ("$sort", ++order)).Single();
            }
        }
        foreach (var tag in manifest.Tags)
        {
            if (definitions.Tag(tag.Name) is null)
            {
                object group = tag.Group is { } g && definitions.Groups.TryGetValue(NameNormalizer.Normalize(g), out var id) ? id : DBNull.Value;
                definitions.Tags[NameNormalizer.Normalize(tag.Name)] = Rows(connection, transaction,
                    "INSERT INTO tags (name, normalized_name, group_id) VALUES ($name, $n, $group) RETURNING id;", r => r.GetInt64(0),
                    ("$name", tag.Name), ("$n", NameNormalizer.Normalize(tag.Name)), ("$group", group)).Single();
            }
        }
        foreach (var tag in manifest.Tags)
        {
            foreach (var parent in tag.Parents)
            {
                if (definitions.Tag(tag.Name) is { } child && definitions.Tag(parent) is { } parentId && child != parentId)
                {
                    Execute(connection, transaction, "INSERT OR IGNORE INTO tag_links (child_id, parent_id) VALUES ($c, $p);", ("$c", child), ("$p", parentId));
                }
            }
        }
        var fieldOrder = Rows(connection, transaction, "SELECT COALESCE(MAX(sort_order), 0) FROM fields;", r => r.GetInt64(0)).Single();
        foreach (var field in manifest.Fields.Where(f => !definitions.Fields.ContainsKey(f.Name)))
        {
            Execute(connection, transaction, "INSERT INTO fields (name, type, sort_order) VALUES ($name, $type, $sort);",
                ("$name", field.Name), ("$type", field.Type), ("$sort", ++fieldOrder));
        }
    }

    private static void InsertDocument(SqliteConnection connection, SqliteTransaction transaction, HandoffDocument document, Definitions definitions,
        IReadOnlyDictionary<string, string> types)
    {
        object seriesId = DBNull.Value;
        object versionNo = DBNull.Value;
        if (document.SeriesId is { } series && document.VersionNo is { } version)
        {
            Execute(connection, transaction, "INSERT OR IGNORE INTO version_series (id, name) VALUES ($id, $name);", ("$id", series), ("$name", document.SeriesName ?? document.Title));
            var taken = Rows(connection, transaction, "SELECT COUNT(*) FROM documents WHERE series_id = $s AND version_no = $v;", r => r.GetInt64(0),
                ("$s", series), ("$v", version)).Single() > 0;
            seriesId = series;
            // 同じ番号がこの PC にもあれば、一番新しい版にする
            versionNo = taken
                ? Rows(connection, transaction, "SELECT MAX(version_no) + 1 FROM documents WHERE series_id = $s;", r => r.GetInt64(0), ("$s", series)).Single()
                : version;
        }
        var (sourceType, sourceId) = FileNameSource(document, definitions, types);
        Execute(connection, transaction, """
            INSERT INTO documents (id, title, memo, file_hash, original_file_name, file_size, page_count, rating, series_id, version_no,
                                   created_at, updated_at, file_name, file_name_source_type, file_name_source_id, file_name_suffix, version_label)
            VALUES ($id, $title, $memo, $hash, $original, $size, $pages, $rating, $series, $version, $created, $updated,
                    $fileName, $sourceType, $sourceId, $suffix, $label);
            """,
            ("$id", document.Id), ("$title", document.Title), ("$memo", document.Memo), ("$hash", document.FileHash), ("$original", document.OriginalFileName),
            ("$size", document.FileSize), ("$pages", document.PageCount), ("$rating", Math.Clamp(document.Rating, 0, 5)), ("$series", seriesId), ("$version", versionNo),
            ("$created", document.CreatedAt.UtcDateTime.ToString("O")), ("$updated", document.UpdatedAt.UtcDateTime.ToString("O")),
            ("$fileName", document.FileName), ("$sourceType", sourceType), ("$sourceId", sourceId), ("$suffix", document.FileNameSuffix), ("$label", document.VersionLabel));
        foreach (var text in document.PageTexts)
        {
            Execute(connection, transaction, "INSERT OR IGNORE INTO page_texts (document_id, page_no, text, source) VALUES ($d, $p, $t, $s);",
                ("$d", document.Id), ("$p", text.Page), ("$t", text.Text), ("$s", text.Source is "ocr" ? "ocr" : "embedded"));
        }
        SetTags(connection, transaction, document.Id, document.Tags, definitions, replace: true);
        SetFields(connection, transaction, document.Id, document.FieldValues, definitions, types);
    }

    private static void UpdateDocument(SqliteConnection connection, SqliteTransaction transaction, HandoffExisting existing, HandoffChoice choice,
        Definitions definitions, IReadOnlyDictionary<string, string> types, DateTimeOffset now)
    {
        var document = existing.Document;
        if (choice == HandoffChoice.Overwrite)
        {
            var (sourceType, sourceId) = FileNameSource(document, definitions, types);
            Execute(connection, transaction, """
                UPDATE documents SET title = $title, memo = $memo, rating = $rating, file_name = $fileName, file_name_source_type = $sourceType,
                    file_name_source_id = $sourceId, file_name_suffix = $suffix, version_label = $label, updated_at = $now WHERE id = $id;
                """,
                ("$title", document.Title), ("$memo", document.Memo), ("$rating", Math.Clamp(document.Rating, 0, 5)), ("$fileName", document.FileName),
                ("$sourceType", sourceType), ("$sourceId", sourceId), ("$suffix", document.FileNameSuffix), ("$label", document.VersionLabel),
                ("$now", now.UtcDateTime.ToString("O")), ("$id", existing.LocalId));
            SetTags(connection, transaction, existing.LocalId, document.Tags, definitions, replace: true);
            Execute(connection, transaction, "DELETE FROM document_field_values WHERE document_id = $id;", ("$id", existing.LocalId));
            SetFields(connection, transaction, existing.LocalId, document.FieldValues, definitions, types);
        }
        else
        {
            SetTags(connection, transaction, existing.LocalId, document.Tags, definitions, replace: false);
            Execute(connection, transaction, "UPDATE documents SET updated_at = $now WHERE id = $id;", ("$now", now.UtcDateTime.ToString("O")), ("$id", existing.LocalId));
        }
    }

    private static (object Type, object Id) FileNameSource(HandoffDocument document, Definitions definitions, IReadOnlyDictionary<string, string> types)
    {
        long? id = document.FileNameSourceType switch
        {
            "tag" when document.FileNameSourceName is { } name => definitions.Tag(name),
            "field" when document.FileNameSourceName is { } name => definitions.Field(name, types),
            _ => null,
        };
        return document.FileNameSourceType switch
        {
            "title" => ("title", DBNull.Value),
            "tag" or "field" when id is not null => (document.FileNameSourceType, id.Value),
            _ => (DBNull.Value, DBNull.Value),
        };
    }

    private static void SetTags(SqliteConnection connection, SqliteTransaction transaction, string documentId, IEnumerable<string> names, Definitions definitions, bool replace)
    {
        if (replace)
        {
            Execute(connection, transaction, "DELETE FROM document_tags WHERE document_id = $id;", ("$id", documentId));
        }
        foreach (var name in names)
        {
            if (definitions.Tag(name) is { } tagId)
            {
                Execute(connection, transaction, "INSERT OR IGNORE INTO document_tags (document_id, tag_id) VALUES ($d, $t);", ("$d", documentId), ("$t", tagId));
            }
        }
    }

    private static void SetFields(SqliteConnection connection, SqliteTransaction transaction, string documentId, IEnumerable<HandoffFieldValue> values,
        Definitions definitions, IReadOnlyDictionary<string, string> types)
    {
        foreach (var value in values)
        {
            if (definitions.Field(value.Field, types) is { } fieldId)
            {
                Execute(connection, transaction, """
                    INSERT OR REPLACE INTO document_field_values (document_id, field_id, value_text, value_number, value_date) VALUES ($d, $f, $t, $n, $dt);
                    """, ("$d", documentId), ("$f", fieldId), ("$t", (object?)value.Text ?? DBNull.Value), ("$n", (object?)value.Number ?? DBNull.Value),
                    ("$dt", (object?)value.Date ?? DBNull.Value));
            }
        }
    }

    // 決定ログ EB-7: 一緒に渡した文書同士の続き・関連。今のつながりを切らないものだけ足す
    private static void AddLinks(SqliteConnection connection, LibraryStorage storage, IReadOnlyList<HandoffLink> links, Dictionary<string, string> mapped)
    {
        var repository = new LinkRepository(connection);
        var subs = new SubLibraryRepository(connection);
        foreach (var link in links)
        {
            var from = mapped[link.From];
            var to = mapped[link.To];
            if (from == to || subs.GetStorageIds([from, to]).Values.Any(s => !storage.IsAvailable(s)))
            {
                continue;
            }
            if (link.Type == "next")
            {
                if (repository.NextOf(from) is not null || repository.PreviousOf(to) is not null || repository.SameChain(to).Contains(from))
                {
                    continue;
                }
                Execute(connection, null, "INSERT OR IGNORE INTO document_links (from_id, to_id, type) VALUES ($f, $t, 'next');", ("$f", from), ("$t", to));
            }
            else if (repository.GetRelated(from).All(r => r.Id != to))
            {
                Execute(connection, null, "INSERT OR IGNORE INTO document_links (from_id, to_id, type) VALUES ($f, $t, 'related');", ("$f", from), ("$t", to));
            }
        }
    }

    private static List<T> Rows<T>(SqliteConnection connection, SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> map, params (string Name, object Value)[] parameters)
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

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
