using System.Security.Cryptography;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Pdf;
using PdfManager.Core.Storage;
using UglyToad.PdfPig.Exceptions;

namespace PdfManager.Core.Import;

public enum PreparationStatus
{
    Ready,
    Duplicate,
    Failed,
}

// 取り込み画面に並べる前の準備結果(docs/04「取り込み画面の流れ」1.)
public sealed record PreparedImport(
    string SourcePath,
    PreparationStatus Status,
    string? Message,
    string FileHash,
    long FileSize,
    int PageCount,
    IReadOnlyList<PageText> PageTexts,
    IReadOnlyList<string> TitleCandidates,
    string? PreviewImagePath)
{
    public string OriginalFileName => Path.GetFileName(SourcePath);

    // パスワード付きなどで中身を読めなかった PDF はサムネイルを作らない
    public bool CanRender => PreviewImagePath is not null;
}

public sealed record ImportInput(
    string Title,
    string FileName,
    FileNameSource FileNameSource,
    long? FileNameSourceId,
    string FileNameSuffix,
    IReadOnlyDictionary<long, FieldValue> FieldValues,
    IReadOnlyCollection<long> TagIds);

public sealed record RegisteredImport(string? DocumentId, string? Warning, bool WasDuplicate);

public sealed class ImportService(DataFolder dataFolder)
{
    // 1 ページ目の埋め込み文字がこれ未満なら、スキャンした PDF とみなして OCR する
    public const int OcrThresholdCharacters = 10;
    public const uint PreviewWidth = 900;

    private static readonly EnumerationOptions FolderEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public static IReadOnlyList<string> CollectPdfFiles(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.EnumerateFiles(path, "*.pdf", FolderEnumeration).Order(StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(path);
            }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<PreparedImport> PrepareAsync(string sourcePath, string previewFolder)
    {
        string fileHash;
        long fileSize;
        try
        {
            fileSize = new FileInfo(sourcePath).Length;
            fileHash = await ComputeHashAsync(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(sourcePath, $"ファイルを読み込めません: {ex.Message}");
        }

        using (var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath))
        {
            if (new DocumentRepository(connection).FindIdByHash(fileHash) is not null)
            {
                return new PreparedImport(sourcePath, PreparationStatus.Duplicate, "同じ内容の PDF が登録済みです", fileHash, fileSize, 0, [], [], null);
            }
        }

        PdfTextContent content;
        try
        {
            content = PdfTextExtractor.Extract(sourcePath);
        }
        catch (PdfDocumentEncryptedException)
        {
            return new PreparedImport(sourcePath, PreparationStatus.Ready, "パスワード付きのため、プレビュー・サムネイル・文字の読み取りを省略します",
                fileHash, fileSize, 0, [], TitleCandidates.Build([], null, Path.GetFileName(sourcePath)), null);
        }
        // 壊れた PDF に対して PdfPig は様々な種類の例外を投げるため、ここでは種類を問わず「読み込めない PDF」として扱う
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed(sourcePath, $"PDF として読み込めません: {ex.Message}");
        }

        var warnings = new List<string>();
        var firstPageLines = content.FirstPageLines;
        var pageTexts = content.PageTexts.Select((text, index) => new PageText(index + 1, text, TextSource.Embedded)).ToList();
        if (CountVisibleCharacters(content.PageTexts.FirstOrDefault()) < OcrThresholdCharacters)
        {
            try
            {
                var ocrLines = await OcrReader.ReadFirstPageAsync(sourcePath);
                if (ocrLines.Count > 0)
                {
                    firstPageLines = ocrLines;
                    pageTexts[0] = new PageText(1, string.Join('\n', ocrLines.Select(l => l.Text)), TextSource.Ocr);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"OCR に失敗しました: {ex.Message}");
            }
        }

        string? previewPath = Path.Combine(previewFolder, Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            await PdfPageRenderer.RenderFirstPageToJpegAsync(sourcePath, previewPath, PreviewWidth);
        }
        catch (Exception ex)
        {
            previewPath = null;
            warnings.Add($"プレビューを作成できませんでした: {ex.Message}");
        }

        return new PreparedImport(
            sourcePath,
            PreparationStatus.Ready,
            warnings.Count == 0 ? null : string.Join(" / ", warnings),
            fileHash,
            fileSize,
            content.PageCount,
            pageTexts.Where(p => p.Text.Length > 0).ToList(),
            TitleCandidates.Build(firstPageLines, content.MetadataTitle, Path.GetFileName(sourcePath)),
            previewPath);
    }

    public async Task<RegisteredImport> RegisterAsync(PreparedImport prepared, ImportInput input)
    {
        if (prepared.Status != PreparationStatus.Ready)
        {
            throw new InvalidOperationException("準備が完了していない PDF は登録できません。");
        }

        var documentId = Guid.NewGuid().ToString("D");
        var storedPath = dataFolder.GetPdfPath(documentId);
        using (var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath))
        {
            var repository = new DocumentRepository(connection);
            // 準備のあとに同じ内容の PDF が登録された場合(同じファイルを 2 回追加したなど)
            if (repository.FindIdByHash(prepared.FileHash) is not null)
            {
                return new RegisteredImport(null, "同じ内容の PDF が登録済みです", WasDuplicate: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(storedPath)!);
            File.Copy(prepared.SourcePath, storedPath);
            try
            {
                repository.Insert(
                    new NewDocument(documentId, input.Title, prepared.FileHash, prepared.OriginalFileName, prepared.FileSize, prepared.PageCount,
                        input.FileName, input.FileNameSource, input.FileNameSourceId, input.FileNameSuffix),
                    prepared.PageTexts,
                    DateTimeOffset.UtcNow,
                    input.FieldValues,
                    input.TagIds);
            }
            catch
            {
                File.Delete(storedPath);
                throw;
            }
        }

        string? warning = null;
        if (prepared.CanRender)
        {
            try
            {
                await PdfPageRenderer.RenderFirstPageToJpegAsync(storedPath, dataFolder.GetThumbnailPath(documentId));
            }
            catch (Exception ex)
            {
                warning = $"サムネイルを作成できませんでした: {ex.Message}";
            }
        }
        return new RegisteredImport(documentId, warning, WasDuplicate: false);
    }

    private static PreparedImport Failed(string sourcePath, string message) =>
        new(sourcePath, PreparationStatus.Failed, message, "", 0, 0, [], [], null);

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static int CountVisibleCharacters(string? text) => text?.Count(c => !char.IsWhiteSpace(c)) ?? 0;
}
