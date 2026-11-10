using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PdfManager.Core.Database;
using PdfManager.Core.Library;
using PdfManager.Core.Text;

namespace PdfManager.Core.Export;

// ==== 受け渡しの情報ファイル(決定ログ EB-7。受け渡しのフォルダ・ZIP の「受け渡し.json」) ====

public sealed record HandoffManifest(
    string Format,
    int Version,
    string LibraryId,
    string LibraryName,
    string Pc,
    DateTimeOffset ExportedAt,
    IReadOnlyList<HandoffGroup> Groups,
    IReadOnlyList<HandoffTag> Tags,
    IReadOnlyList<HandoffField> Fields,
    IReadOnlyList<HandoffDocument> Documents,
    IReadOnlyList<HandoffLink> Links);

public sealed record HandoffGroup(string Name, int Rank, bool IsPinned, int PinOrder, int SortOrder);

// Group は null なら自由タグ。Parents は紐づく上の順位のタグの名前。ParentTag は同じグループの中の親のタグの名前(決定ログ EC-9)
public sealed record HandoffTag(string Name, string? Group, IReadOnlyList<string> Parents, string? ParentTag = null);

public sealed record HandoffField(string Name, string Type);

// File は受け渡しのフォルダの中の PDF(利用者向けのファイル名)。FileNameSourceName はファイル名の元にしたタグ・補助項目の名前
public sealed record HandoffDocument(
    string Id,
    string File,
    string Title,
    string Memo,
    int Rating,
    string FileHash,
    string OriginalFileName,
    long FileSize,
    int PageCount,
    string FileName,
    string? FileNameSourceType,
    string? FileNameSourceName,
    string FileNameSuffix,
    string VersionLabel,
    string? SeriesId,
    string? SeriesName,
    int? VersionNo,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Tags,
    IReadOnlyList<HandoffFieldValue> FieldValues,
    IReadOnlyList<HandoffPageText> PageTexts);

public sealed record HandoffFieldValue(string Field, string? Text, double? Number, string? Date);

public sealed record HandoffPageText(int Page, string Text, string Source);

public sealed record HandoffLink(string From, string To, string Type);

public static class HandoffFormat
{
    public const string Name = "PdfManagerHandoff";
    public const int Version = 1;
    public const string ManifestFileName = "受け渡し.json";
    public const string OutputPrefix = "PdfManager受け渡し_";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // 決定ログ EB-8: この PC で前回受け渡したあと(since が null なら、すべて)に登録・変更した PDF
    public static IReadOnlyList<string> ChangedSince(SqliteConnection connection, DateTimeOffset? since)
    {
        using var command = connection.CreateCommand();
        command.CommandText = since is null
            ? "SELECT id FROM documents ORDER BY created_at;"
            : "SELECT id FROM documents WHERE created_at > $since OR updated_at > $since ORDER BY created_at;";
        if (since is { } value)
        {
            command.Parameters.AddWithValue("$since", value.UtcDateTime.ToString("O"));
        }
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }
        return ids;
    }

    // 書き出した文書(と、そのファイルの場所)から情報ファイルを作る。タグ・グループ・補助項目は使っているものと、紐づく上の順位のタグだけ
    internal static HandoffManifest Build(SqliteConnection connection, LibraryInfo library, string pc, DateTimeOffset now,
        IReadOnlyList<(string Id, string File)> exported)
    {
        var documents = new List<HandoffDocument>();
        var usedTags = new HashSet<long>();
        var usedFields = new HashSet<string>();
        foreach (var (id, file) in exported)
        {
            var row = Single(connection, """
                SELECT d.title, d.memo, d.rating, d.file_hash, d.original_file_name, d.file_size, d.page_count, d.file_name,
                       d.file_name_source_type,
                       CASE d.file_name_source_type WHEN 'tag' THEN (SELECT name FROM tags WHERE id = d.file_name_source_id)
                                                    WHEN 'field' THEN (SELECT name FROM fields WHERE id = d.file_name_source_id) END,
                       d.file_name_suffix, d.version_label, d.series_id, s.name, d.version_no, d.created_at, d.updated_at
                FROM documents d LEFT JOIN version_series s ON s.id = d.series_id WHERE d.id = $id;
                """, id);
            if (row is null)
            {
                continue;
            }
            var tags = Rows(connection, "SELECT t.id, t.name FROM document_tags dt JOIN tags t ON t.id = dt.tag_id WHERE dt.document_id = $id ORDER BY t.name;",
                id, r => (Id: r.GetInt64(0), Name: r.GetString(1)));
            usedTags.UnionWith(tags.Select(t => t.Id));
            var values = Rows(connection, """
                SELECT f.name, v.value_text, v.value_number, v.value_date FROM document_field_values v JOIN fields f ON f.id = v.field_id
                WHERE v.document_id = $id ORDER BY f.name;
                """, id, r => new HandoffFieldValue(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetDouble(2), r.IsDBNull(3) ? null : r.GetString(3)));
            usedFields.UnionWith(values.Select(v => v.Field));
            var texts = Rows(connection, "SELECT page_no, text, source FROM page_texts WHERE document_id = $id ORDER BY page_no;",
                id, r => new HandoffPageText(r.GetInt32(0), r.GetString(1), r.GetString(2)));
            documents.Add(Read(row, id, file, tags.Select(t => t.Name).ToList(), values, texts));
        }

        // 紐づく上の順位のタグと、同じグループの中の親のタグもたどる
        var links = Rows(connection, "SELECT child_id, parent_id FROM tag_links UNION SELECT id, parent_tag_id FROM tags WHERE parent_tag_id IS NOT NULL;", null,
            r => (Child: r.GetInt64(0), Parent: r.GetInt64(1)));
        var parentTags = Rows(connection, "SELECT id, parent_tag_id FROM tags WHERE parent_tag_id IS NOT NULL;", null, r => (Id: r.GetInt64(0), Parent: r.GetInt64(1)))
            .ToDictionary(x => x.Id, x => x.Parent);
        var rankLinks = Rows(connection, "SELECT child_id, parent_id FROM tag_links;", null, r => (Child: r.GetInt64(0), Parent: r.GetInt64(1)));
        var queue = new Queue<long>(usedTags);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var parent in links.Where(l => l.Child == current).Select(l => l.Parent))
            {
                if (usedTags.Add(parent))
                {
                    queue.Enqueue(parent);
                }
            }
        }
        var tagRows = Rows(connection, "SELECT t.id, t.name, g.name FROM tags t LEFT JOIN tag_groups g ON g.id = t.group_id ORDER BY t.name;", null,
            r => (Id: r.GetInt64(0), Name: r.GetString(1), Group: r.IsDBNull(2) ? null : r.GetString(2))).Where(t => usedTags.Contains(t.Id)).ToList();
        var names = tagRows.ToDictionary(t => t.Id, t => t.Name);
        var tagsOut = tagRows.Select(t => new HandoffTag(t.Name, t.Group,
            rankLinks.Where(l => l.Child == t.Id && names.ContainsKey(l.Parent)).Select(l => names[l.Parent]).Order(StringComparer.Ordinal).ToList(),
            parentTags.TryGetValue(t.Id, out var parent) && names.TryGetValue(parent, out var parentName) ? parentName : null)).ToList();
        var usedGroups = tagRows.Select(t => t.Group).OfType<string>().ToHashSet();
        var groups = Rows(connection, "SELECT name, rank, is_pinned, pin_order, sort_order FROM tag_groups ORDER BY sort_order, id;", null,
            r => new HandoffGroup(r.GetString(0), r.GetInt32(1), r.GetBoolean(2), r.GetInt32(3), r.GetInt32(4))).Where(g => usedGroups.Contains(g.Name)).ToList();
        var fields = Rows(connection, "SELECT name, type FROM fields ORDER BY sort_order, id;", null, r => new HandoffField(r.GetString(0), r.GetString(1)))
            .Where(f => usedFields.Contains(f.Name)).ToList();

        var ids = exported.Select(e => e.Id).ToHashSet();
        var docLinks = Rows(connection, "SELECT from_id, to_id, type FROM document_links;", null, r => new HandoffLink(r.GetString(0), r.GetString(1), r.GetString(2)))
            .Where(l => ids.Contains(l.From) && ids.Contains(l.To)).ToList();

        return new HandoffManifest(Name, Version, library.Id.ToString("D"), library.Name, pc, now, groups, tagsOut, fields, documents, docLinks);
    }

    private static HandoffDocument Read(object[] row, string id, string file, IReadOnlyList<string> tags, IReadOnlyList<HandoffFieldValue> values, IReadOnlyList<HandoffPageText> texts) =>
        new(id, file,
            (string)row[0], (string)row[1], Convert.ToInt32(row[2], CultureInfo.InvariantCulture), (string)row[3], (string)row[4],
            Convert.ToInt64(row[5], CultureInfo.InvariantCulture), Convert.ToInt32(row[6], CultureInfo.InvariantCulture), (string)row[7],
            row[8] as string, row[9] as string, (string)row[10], (string)row[11], row[12] as string, row[13] as string,
            row[14] is DBNull or null ? null : Convert.ToInt32(row[14], CultureInfo.InvariantCulture),
            DateTimeOffset.Parse((string)row[15], CultureInfo.InvariantCulture), DateTimeOffset.Parse((string)row[16], CultureInfo.InvariantCulture),
            tags, values, texts);

    private static object[]? Single(SqliteConnection connection, string sql, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        var values = new object[reader.FieldCount];
        reader.GetValues(values);
        return values;
    }

    internal static List<T> Rows<T>(SqliteConnection connection, string sql, string? id, Func<SqliteDataReader, T> map)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null)
        {
            command.Parameters.AddWithValue("$id", id);
        }
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }
        return rows;
    }

    internal static byte[] Serialize(HandoffManifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);

    public static HandoffManifest Deserialize(string json)
    {
        var manifest = JsonSerializer.Deserialize<HandoffManifest>(json, JsonOptions);
        if (manifest is null || manifest.Format != Name)
        {
            throw new LibraryException("PdfManager の受け渡しのファイルではありません。");
        }
        if (manifest.Version > Version)
        {
            throw new LibraryException("新しいバージョンの PdfManager で作った受け渡しです。この PdfManager では取り込めません。");
        }
        return manifest;
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

// 受け渡しのフォルダ、または ZIP(作業用のフォルダに展開する)。使い終わったら Dispose で作業用のフォルダを消す
public sealed class HandoffPackage : IDisposable
{
    private readonly string? _temporary;

    private HandoffPackage(string folder, HandoffManifest manifest, string? temporary, string source)
    {
        Folder = folder;
        Manifest = manifest;
        _temporary = temporary;
        Source = source;
    }

    public string Folder { get; }

    public string Source { get; }

    public HandoffManifest Manifest { get; }

    // 受け渡しのフォルダ・ZIP か(ドラッグ&ドロップで見分けるため)
    public static bool IsPackage(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return File.Exists(Path.Combine(path, HandoffFormat.ManifestFileName));
            }
            if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(path);
                return zip.GetEntry(HandoffFormat.ManifestFileName) is not null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
        }
        return false;
    }

    public static HandoffPackage Open(string path)
    {
        string? temporary = null;
        var folder = path;
        try
        {
            if (File.Exists(path))
            {
                temporary = Path.Combine(Path.GetTempPath(), "PdfManager", "handoff-" + Guid.NewGuid().ToString("N"));
                // 展開先の外に書き出すような名前は、ZipFile が拒む
                ZipFile.ExtractToDirectory(path, temporary);
                folder = temporary;
            }
            var manifestPath = Path.Combine(folder, HandoffFormat.ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                throw new LibraryException($"受け渡しの情報ファイル({HandoffFormat.ManifestFileName})が見つかりません。PdfManager の受け渡しのフォルダか ZIP を選んでください。");
            }
            return new HandoffPackage(folder, HandoffFormat.Deserialize(File.ReadAllText(manifestPath)), temporary, path);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            DeleteTemporary(temporary);
            throw new LibraryException($"受け渡しのファイルを読めませんでした。({ex.Message})");
        }
        catch
        {
            DeleteTemporary(temporary);
            throw;
        }
    }

    // 情報ファイルに書かれた場所が、受け渡しのフォルダの外を指していたら使わない
    public string? PdfPath(HandoffDocument document)
    {
        var path = Path.GetFullPath(Path.Combine(Folder, document.File));
        return LibraryService.IsSameOrUnder(path, Folder) && File.Exists(path) ? path : null;
    }

    public void Dispose() => DeleteTemporary(_temporary);

    private static void DeleteTemporary(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
