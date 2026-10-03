using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Storage;

namespace PdfManager.Core.Tests;

public sealed class DocumentRemoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}");
    private readonly string _bin;
    private readonly DataFolder _dataFolder;

    public DocumentRemoverTests()
    {
        _bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        _dataFolder = new DataFolder(Path.Combine(_root, "data"));
        _dataFolder.EnsureCreated();
        using var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath);
        PdfManagerDatabase.Initialize(connection);
        var tag = new CatalogRepository(connection).AddTag("IODATA");
        var repository = new DocumentRepository(connection);
        foreach (var id in new[] { "aa000000-0000-0000-0000-000000000001", "aa000000-0000-0000-0000-000000000002" })
        {
            repository.Insert(new NewDocument(id, "t", "h" + id, "o.pdf", 1, 1, "IODATA-MPC-WE1.pdf"), [new PageText(1, "x", TextSource.Embedded)],
                DateTimeOffset.UtcNow, tagIds: [tag]);
            Directory.CreateDirectory(Path.GetDirectoryName(_dataFolder.GetPdfPath(id))!);
            File.WriteAllText(_dataFolder.GetPdfPath(id), "pdf " + id);
            Directory.CreateDirectory(Path.GetDirectoryName(_dataFolder.GetThumbnailPath(id))!);
            File.WriteAllText(_dataFolder.GetThumbnailPath(id), "jpg");
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Delete_RenamesToUserFileNameBeforeDisposingAndRemovesEverything()
    {
        var disposed = new List<string>();
        var remover = new DocumentRemover(_dataFolder, path => { disposed.Add(Path.GetFileName(path)); File.Move(path, Path.Combine(_bin, Guid.NewGuid() + ".pdf")); });

        remover.Delete("aa000000-0000-0000-0000-000000000001", "IODATA-MPC-WE1.pdf");

        Assert.Equal(["IODATA-MPC-WE1.pdf"], disposed);
        Assert.False(File.Exists(_dataFolder.GetPdfPath("aa000000-0000-0000-0000-000000000001")));
        Assert.False(File.Exists(_dataFolder.GetThumbnailPath("aa000000-0000-0000-0000-000000000001")));
        using var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath);
        Assert.Equal(1L, new DocumentRepository(connection).Count());
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM document_tags;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM page_texts;"));
    }

    [Fact]
    public void Delete_AddsNumberWhenSameNameIsWaitingInFolder()
    {
        // 前の削除で同じ名前のファイルが残っている場合(ごみ箱へ移す処理が遅れたなど)
        var id = "aa000000-0000-0000-0000-000000000002";
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dataFolder.GetPdfPath(id))!, "IODATA-MPC-WE1.pdf"), "other");
        var disposed = new List<string>();
        var remover = new DocumentRemover(_dataFolder, path => { disposed.Add(Path.GetFileName(path)); File.Delete(path); });

        remover.Delete(id, "IODATA-MPC-WE1.pdf");

        Assert.Equal(["IODATA-MPC-WE1 (2).pdf"], disposed);
    }

    [Fact]
    public void Delete_KeepsDocumentWhenFileCannotBeDisposed()
    {
        var id = "aa000000-0000-0000-0000-000000000001";
        var remover = new DocumentRemover(_dataFolder, _ => throw new IOException("ロック中"));

        Assert.Throws<IOException>(() => remover.Delete(id, "IODATA-MPC-WE1.pdf"));

        Assert.True(File.Exists(_dataFolder.GetPdfPath(id)));
        using var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath);
        Assert.Equal(2L, new DocumentRepository(connection).Count());
    }

    [Fact]
    public void RecordView_CountsAndStampsLastViewed()
    {
        using var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath);
        var repository = new DocumentRepository(connection);
        var at = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero);

        repository.RecordView("aa000000-0000-0000-0000-000000000001", at);
        repository.RecordView("aa000000-0000-0000-0000-000000000001", at.AddMinutes(5));

        var detail = repository.GetDetails(["aa000000-0000-0000-0000-000000000001"]).Single();
        Assert.Equal((2L, (DateTimeOffset?)at.AddMinutes(5)), (detail.ViewCount, detail.LastViewedAt));
    }

    private static object? Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
