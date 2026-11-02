using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Library;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.Core.Export;

// Zip:ZIP にする(決定ログ CC-2)。WriteList:一覧表(CSV)も出す(CC-4)。SplitGroupId:このタググループのタグごとにフォルダを分ける(CC-5)
public sealed record ExportOptions(bool Zip = false, bool WriteList = true, long? SplitGroupId = null);

public sealed record ExportPlan(int Count, long Bytes, int UnavailableCount, long? FreeBytes)
{
    // 空き容量には少し余裕を見る
    public bool HasEnoughSpace => FreeBytes is not { } free || free - Bytes >= 50L << 20;
}

// OutputPath は作ったフォルダか ZIP。Skipped は出せなかった PDF(タイトルと理由)
public sealed record ExportResult(string OutputPath, int Exported, IReadOnlyList<string> Skipped, bool Cancelled);

// USB への出力(F-50、決定ログ CC-1〜CC-9)。選んだ文書の PDF を、利用者向けのファイル名で新しいフォルダ(または ZIP)に書き出す
public static class ExportService
{
    public const string OutputPrefix = "PdfManager出力_";
    public const string ListFileName = "一覧.csv";
    private const string UnsetFolder = "未設定";

    public static ExportPlan Plan(LibraryStorage storage, IReadOnlyCollection<string> documentIds, string destinationFolder)
    {
        using var connection = PdfManagerDatabase.Open(storage.Main.DataFolder.DatabasePath);
        var details = new DocumentRepository(connection).GetDetails(documentIds);
        var storages = new SubLibraryRepository(connection).GetStorageIds(documentIds);
        var available = details.Where(d => storage.GetDataFolder(storages.GetValueOrDefault(d.Id)) is not null).ToList();
        return new ExportPlan(available.Count, available.Sum(d => d.FileSize), details.Count - available.Count, FreeBytes(destinationFolder));
    }

    public static ExportResult Run(LibraryStorage storage, IReadOnlyCollection<string> documentIds, string destinationFolder, ExportOptions options,
        DateTimeOffset now, IProgress<MoveProgress>? progress = null, CancellationToken cancellation = default)
    {
        IReadOnlyList<DocumentDetail> details;
        IReadOnlyDictionary<string, string?> storages;
        IReadOnlyList<TagGroup> groups;
        Dictionary<long, TagSummary> tags;
        IReadOnlyList<FieldDefinition> fields;
        using (var connection = PdfManagerDatabase.Open(storage.Main.DataFolder.DatabasePath))
        {
            details = new DocumentRepository(connection).GetDetails(documentIds);
            storages = new SubLibraryRepository(connection).GetStorageIds(documentIds);
            var catalog = new CatalogRepository(connection);
            groups = catalog.GetGroups().OrderBy(g => g.DisplayRank).ToList();
            tags = catalog.GetTags().ToDictionary(t => t.Id);
            fields = catalog.GetFields().Where(f => f.Type != FieldType.Select).ToList();
        }
        // 一覧の並び(画面の並び)のとおりに出す
        var order = documentIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        details = details.OrderBy(d => order.GetValueOrDefault(d.Id)).ToList();

        // 決定ログ CC-6: 新しいフォルダ(または ZIP)を作る。すでにある名前なら「_2」などを付ける
        var baseName = OutputPrefix + now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
        var outputPath = UniquePath(destinationFolder, baseName, options.Zip ? ".zip" : "");
        var workPath = options.Zip ? outputPath + ".tmp" : outputPath;
        var skipped = new List<string>();
        var exported = 0;
        var rows = new List<(DocumentDetail Detail, string RelativePath)>();
        try
        {
            using var zip = options.Zip ? ZipFile.Open(workPath, ZipArchiveMode.Create) : null;
            if (!options.Zip)
            {
                Directory.CreateDirectory(workPath);
            }
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < details.Count; i++)
            {
                if (cancellation.IsCancellationRequested)
                {
                    zip?.Dispose();
                    DeleteOutput(workPath, options.Zip);
                    return new ExportResult(outputPath, exported, skipped, Cancelled: true);
                }
                var detail = details[i];
                progress?.Report(new MoveProgress(i, details.Count, detail.Title));
                // 決定ログ CC-8: つながっていないサブの PDF は出せない
                var storageId = storages.GetValueOrDefault(detail.Id);
                if (storage.GetDataFolder(storageId) is not { } folder || !File.Exists(folder.GetPdfPath(detail.Id)))
                {
                    skipped.Add($"・{detail.Title}:" + (storage.GetDataFolder(storageId) is null ? $"保存先「{storage.StorageName(storageId)}」がつながっていません" : "PDF が見つかりません"));
                    continue;
                }
                var subFolder = options.SplitGroupId is { } groupId ? FolderFor(detail, groupId, tags) : "";
                var relative = UniqueFileName(subFolder, detail.FileName, usedNames);
                if (zip is not null)
                {
                    zip.CreateEntryFromFile(folder.GetPdfPath(detail.Id), relative.Replace('\\', '/'));
                }
                else
                {
                    var target = Path.Combine(workPath, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(folder.GetPdfPath(detail.Id), target + ".tmp");
                    File.Move(target + ".tmp", target);
                }
                rows.Add((detail, relative));
                exported++;
            }
            progress?.Report(new MoveProgress(details.Count, details.Count, ""));

            // 決定ログ CC-4: 一覧表(Excel で開けるよう、UTF-8 の BOM 付き)
            if (options.WriteList)
            {
                var csv = BuildList(rows, groups, tags, fields);
                if (zip is not null)
                {
                    using var stream = zip.CreateEntry(ListFileName).Open();
                    stream.Write(csv);
                }
                else
                {
                    File.WriteAllBytes(Path.Combine(workPath, ListFileName), csv);
                }
            }
        }
        catch
        {
            DeleteOutput(workPath, options.Zip);
            throw;
        }
        if (options.Zip)
        {
            File.Move(workPath, outputPath);
        }
        return new ExportResult(outputPath, exported, skipped, Cancelled: false);
    }

    // 分けるグループのタグ(複数あれば名前の順で最初のもの)。なければ「未設定」
    private static string FolderFor(DocumentDetail detail, long groupId, Dictionary<long, TagSummary> tags)
    {
        var name = detail.TagIds.Select(id => tags.GetValueOrDefault(id)).OfType<TagSummary>()
            .Where(t => t.GroupId == groupId).Select(t => t.Name).Order(StringComparer.Ordinal).FirstOrDefault();
        return FileNames.Sanitize(name ?? UnsetFolder, UnsetFolder);
    }

    // 決定ログ CC-3: 同じフォルダに同じ名前があれば「名前 (2).pdf」
    private static string UniqueFileName(string folder, string fileName, HashSet<string> used)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(folder, fileName);
        for (var n = 2; !used.Add(candidate); n++)
        {
            candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
        }
        return candidate;
    }

    private static string UniquePath(string folder, string baseName, string extension)
    {
        var path = Path.Combine(Path.GetFullPath(folder), baseName + extension);
        for (var n = 2; File.Exists(path) || Directory.Exists(path) || File.Exists(path + ".tmp"); n++)
        {
            path = Path.Combine(Path.GetFullPath(folder), $"{baseName}_{n}{extension}");
        }
        return path;
    }

    // 列:タイトル、ファイル(出力したもの)、元のファイル名、タググループごと、自由タグ、補助項目ごと、評価、登録日、メモ
    private static byte[] BuildList(IReadOnlyList<(DocumentDetail Detail, string RelativePath)> rows, IReadOnlyList<TagGroup> groups,
        Dictionary<long, TagSummary> tags, IReadOnlyList<FieldDefinition> fields)
    {
        var builder = new StringBuilder();
        var header = new List<string> { "タイトル", "ファイル", "元のファイル名" };
        header.AddRange(groups.Select(g => g.Name));
        header.Add("自由タグ");
        header.AddRange(fields.Select(f => f.Name));
        header.AddRange(["評価", "登録日", "メモ"]);
        AppendLine(builder, header);
        foreach (var (detail, relative) in rows)
        {
            var docTags = detail.TagIds.Select(id => tags.GetValueOrDefault(id)).OfType<TagSummary>().ToList();
            string TagsOf(long? groupId) => string.Join("、", docTags.Where(t => t.GroupId == groupId).Select(t => t.Name).Order(StringComparer.Ordinal));
            var cells = new List<string> { detail.Title, relative, detail.OriginalFileName };
            cells.AddRange(groups.Select(g => TagsOf(g.Id)));
            cells.Add(TagsOf(null));
            cells.AddRange(fields.Select(f => detail.FieldValues.TryGetValue(f.Id, out var value) ? FieldValues.Format(f.Type, value) : ""));
            cells.Add(detail.Rating == 0 ? "" : detail.Rating.ToString(CultureInfo.InvariantCulture));
            cells.Add(detail.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cells.Add(detail.Memo);
            AppendLine(builder, cells);
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(builder.ToString())];
    }

    private static void AppendLine(StringBuilder builder, IEnumerable<string> cells)
    {
        builder.Append(string.Join(",", cells.Select(Quote)));
        builder.Append("\r\n");
    }

    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static void DeleteOutput(string path, bool zip)
    {
        try
        {
            if (zip)
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 作りかけを消せなくても、ライブラリには影響しない
        }
    }

    private static long? FreeBytes(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
