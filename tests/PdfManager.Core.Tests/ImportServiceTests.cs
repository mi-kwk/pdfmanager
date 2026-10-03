using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Import;
using PdfManager.Core.Pdf;
using PdfManager.Core.Storage;

namespace PdfManager.Core.Tests;

public sealed class ImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}");
    private readonly string _sourceDir;
    private readonly string _previewDir;
    private readonly DataFolder _dataFolder;
    private readonly ImportService _service;

    public ImportServiceTests()
    {
        _sourceDir = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        _previewDir = Directory.CreateDirectory(Path.Combine(_root, "preview")).FullName;
        _dataFolder = new DataFolder(Path.Combine(_root, "data"));
        _dataFolder.EnsureCreated();
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            PdfManagerDatabase.Initialize(connection);
        }
        _service = new ImportService(_dataFolder);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Prepare_TextPdf_ReadsTextAndMakesPreviewWithoutRegistering()
    {
        var source = SourcePath("report.pdf");
        TestPdfs.CreateTextPdf(source, [("April 2026", 10), ("Quarterly Sales Report", 24)], pageCount: 3);

        var prepared = await _service.PrepareAsync(source, _previewDir);

        Assert.Equal(PreparationStatus.Ready, prepared.Status);
        Assert.Null(prepared.Message);
        Assert.Equal("Quarterly Sales Report", prepared.TitleCandidates[0]);
        Assert.Equal("report", prepared.TitleCandidates[^1]);
        Assert.Equal(3, prepared.PageTexts.Count);
        Assert.True(File.Exists(prepared.PreviewImagePath));
        Assert.Equal(["0"], Query("SELECT COUNT(*) FROM documents;"));
    }

    [Fact]
    public async Task Register_StoresFileThumbnailFileNameFieldValuesAndTags()
    {
        var source = SourcePath("ueno.pdf");
        TestPdfs.CreateTextPdf(source, [("Ueno Store Sales", 24)]);
        long fieldId, tagId;
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            var catalog = new CatalogRepository(connection);
            fieldId = catalog.AddField("年月", FieldType.YearMonth);
            tagId = catalog.AddTag("IODATA");
        }
        var prepared = await _service.PrepareAsync(source, _previewDir);

        var result = await _service.RegisterAsync(prepared, new ImportInput(
            "上野店 売上報告", "IODATA-MPC-WE1.pdf", FileNameSource.Tag, tagId, "-MPC-WE1",
            new Dictionary<long, FieldValue> { [fieldId] = FieldValues.Parse(FieldType.YearMonth, "202609")! },
            [tagId]));

        var id = result.DocumentId!;
        Assert.Null(result.Warning);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(_dataFolder.GetPdfPath(id)));
        Assert.Equal([0xFF, 0xD8], File.ReadAllBytes(_dataFolder.GetThumbnailPath(id))[..2]);
        Assert.Equal(["上野店 売上報告|IODATA-MPC-WE1.pdf|tag|-MPC-WE1|ueno.pdf"],
            Query("SELECT title || '|' || file_name || '|' || file_name_source_type || '|' || file_name_suffix || '|' || original_file_name FROM documents;"));
        Assert.Equal(["2026-09-01"], Query("SELECT value_date FROM document_field_values;"));
        Assert.Equal([tagId.ToString()], Query("SELECT tag_id FROM document_tags;"));
        Assert.Equal(["1"], Query("SELECT COUNT(*) FROM page_texts;"));
    }

    [Fact]
    public async Task SameContent_IsDuplicateAtPrepareAndAtRegister()
    {
        var first = SourcePath("a.pdf");
        TestPdfs.CreateTextPdf(first, [("Same content", 20)]);
        var copy = SourcePath("copy-of-a.pdf");
        File.Copy(first, copy);
        var preparedFirst = await _service.PrepareAsync(first, _previewDir);
        var preparedCopy = await _service.PrepareAsync(copy, _previewDir);

        await _service.RegisterAsync(preparedFirst, SimpleInput("A"));
        var second = await _service.RegisterAsync(preparedCopy, SimpleInput("A copy"));
        var afterwards = await _service.PrepareAsync(copy, _previewDir);

        Assert.True(second.WasDuplicate);
        Assert.Equal(PreparationStatus.Duplicate, afterwards.Status);
        Assert.Equal(["1"], Query("SELECT COUNT(*) FROM documents;"));
    }

    [Fact]
    public async Task Prepare_BrokenFile_Fails()
    {
        var source = SourcePath("broken.pdf");
        File.WriteAllText(source, "this is not a pdf");

        var prepared = await _service.PrepareAsync(source, _previewDir);

        Assert.Equal(PreparationStatus.Failed, prepared.Status);
        Assert.Contains("PDF として読み込めません", prepared.Message);
    }

    [Fact]
    public async Task ImageOnlyPdf_UsesOcrForTitleCandidateAndText()
    {
        var textPdf = SourcePath("original.pdf");
        TestPdfs.CreateTextPdf(textPdf, [("INSPECTION REPORT", 40)]);
        var image = SourcePath("page.jpg");
        await PdfPageRenderer.RenderFirstPageToJpegAsync(textPdf, image, maxWidth: 1600);
        var scanned = SourcePath("scan001.pdf");
        TestPdfs.CreateImageOnlyPdf(scanned, File.ReadAllBytes(image));

        var prepared = await _service.PrepareAsync(scanned, _previewDir);
        await _service.RegisterAsync(prepared, SimpleInput(prepared.TitleCandidates[0]));

        Assert.Contains("REPORT", prepared.TitleCandidates[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["ocr"], Query("SELECT source FROM page_texts WHERE page_no = 1;"));
    }

    [Fact]
    public void CollectPdfFiles_ExpandsFoldersAndIgnoresOtherFiles()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_sourceDir, "sub")).FullName;
        File.WriteAllText(Path.Combine(_sourceDir, "b.PDF"), "");
        File.WriteAllText(Path.Combine(nested, "a.pdf"), "");
        File.WriteAllText(Path.Combine(nested, "note.txt"), "");

        var files = ImportService.CollectPdfFiles([_sourceDir, Path.Combine(_sourceDir, "b.PDF")]);

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Equal(".pdf", Path.GetExtension(f), ignoreCase: true));
    }

    [Fact]
    public void GetFileNames_IgnoresCase()
    {
        using var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath);
        var repository = new DocumentRepository(connection);
        repository.Insert(new NewDocument("doc-1", "t", "h", "o.pdf", 1, 1, "IODATA-MPC.pdf"), [], DateTimeOffset.UtcNow);

        var names = repository.GetFileNames();

        Assert.Contains("iodata-mpc.PDF", names);
        Assert.DoesNotContain("other.pdf", names);
    }

    [Fact]
    public void LastImportInput_RoundTrips()
    {
        var input = new LastImportInput(
            new Dictionary<long, string> { [3] = "2026-09" },
            ["IODATA", "上野"],
            new FileNamePrefixChoice(FileNamePrefixChoice.TagKind, "iodata"));

        input.Save(_dataFolder);
        var loaded = LastImportInput.Load(_dataFolder)!;

        Assert.Equal("2026-09", loaded.FieldInputs[3]);
        Assert.Equal(["IODATA", "上野"], loaded.TagNames);
        Assert.Equal(input.Prefix, loaded.Prefix);
    }

    private static ImportInput SimpleInput(string title) =>
        new(title, title + ".pdf", FileNameSource.Title, null, "", new Dictionary<long, FieldValue>(), []);

    private string SourcePath(string name) => Path.Combine(_sourceDir, name);

    private List<string> Query(string sql)
    {
        using var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(Convert.ToString(reader.GetValue(0))!);
        }
        return rows;
    }
}
