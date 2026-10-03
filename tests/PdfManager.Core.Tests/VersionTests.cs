using Microsoft.Data.Sqlite;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Import;
using PdfManager.Core.Library;
using PdfManager.Core.Search;
using PdfManager.Core.Settings;

namespace PdfManager.Core.Tests;

public sealed class VersionTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}")).FullName;
    private readonly LibraryInfo _library;
    private readonly SqliteConnection _connection;
    private readonly VersionRepository _versions;

    public VersionTests()
    {
        _library = LibraryService.Create(_root, "版テスト");
        _connection = PdfManagerDatabase.Open(_library.DataFolder.DatabasePath);
        _versions = new VersionRepository(_connection);
        var repository = new DocumentRepository(_connection);
        foreach (var (id, day) in new[] { ("v1", 1), ("v2", 2), ("v3", 3), ("other", 4) })
        {
            repository.Insert(new NewDocument(id, "商品A 取扱説明書 " + id, "h" + id, id + ".pdf", 1, 1, id + ".pdf"), [],
                new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.Zero));
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string[] Versions(string id) => _versions.GetVersions(id).Select(v => $"{v.DocumentId}:{v.VersionNo}").ToArray();

    private string[] Visible(bool includeOld = false) =>
        DocumentSearchIndex.Load(_connection).Search(new SearchQuery { IncludeOldVersions = includeOld, Descending = false }).Select(e => e.Id).ToArray();

    [Fact]
    public void AddNewestVersion_CreatesSeriesAndNumbers()
    {
        _versions.AddNewestVersion("v1", "v2");
        _versions.AddNewestVersion("v2", "v3");

        Assert.Equal(["v1:1", "v2:2", "v3:3"], Versions("v1"));
        Assert.Equal(["v3", "other"], Visible());
        Assert.Equal(["v1", "v2", "v3", "other"], Visible(includeOld: true));
    }

    [Fact]
    public void VersionText_ShowsNumberLabelAndOldMark()
    {
        _versions.AddNewestVersion("v1", "v2");
        _versions.SetLabel("v2", " Rev.C ");

        var entries = DocumentSearchIndex.Load(_connection).Search(new SearchQuery { IncludeOldVersions = true }).ToDictionary(e => e.Id);

        Assert.Equal("第2版(Rev.C)", entries["v2"].VersionText);
        Assert.Equal("第1版・旧版", entries["v1"].VersionText);
        Assert.Equal("", entries["other"].VersionText);
    }

    [Fact]
    public void Group_OrdersByRegisteredDateAndMergesExistingSeries()
    {
        _versions.AddNewestVersion("v3", "other");   // v3 → 第1版, other → 第2版

        _versions.Group(["v1", "other"]);             // v1 と、other の版系列(v3)もまとめる

        Assert.Equal(["v1:1", "v3:2", "other:3"], Versions("v1"));
        Assert.Equal(1L, (long)new SqliteCommand("SELECT COUNT(*) FROM version_series;", _connection).ExecuteScalar()!);
    }

    [Fact]
    public void Detach_RenumbersRestAndDissolvesWhenOnlyOneLeft()
    {
        _versions.Group(["v1", "v2", "v3"]);

        _versions.Detach("v2");
        var afterFirst = Versions("v1");
        _versions.Detach("v3");

        Assert.Equal(["v1:1", "v3:2"], afterFirst);
        Assert.Empty(Versions("v1"));
        Assert.Equal(0L, (long)new SqliteCommand("SELECT COUNT(*) FROM version_series;", _connection).ExecuteScalar()!);
    }

    [Fact]
    public void DeletingLatest_MakesPreviousVersionLatest()
    {
        _versions.Group(["v1", "v2", "v3"]);
        var remover = new DocumentRemover(_library.DataFolder, _ => { });

        remover.Delete("v3", "v3.pdf");

        Assert.Equal(["v2", "other"], Visible());
        Assert.Equal(["v1:1", "v2:2"], Versions("v1"));
    }

    [Fact]
    public async Task Register_AsNewVersionOfExistingDocument()
    {
        var source = Path.Combine(_root, "manual-v2.pdf");
        TestPdfs.CreateTextPdf(source, [("Product A Manual v2", 20)]);
        var service = new ImportService(_library.DataFolder);
        var prepared = await service.PrepareAsync(source, _root);

        var result = await service.RegisterAsync(prepared, new ImportInput("商品A 取扱説明書", "manual.pdf", FileNameSource.None, null, "",
            new Dictionary<long, Catalog.FieldValue>(), []) { Memo = "前の版から引き継いだメモ", NewVersionOf = "v1" });

        Assert.Equal(["v1:1", $"{result.DocumentId}:2"], Versions("v1"));
        Assert.Equal("前の版から引き継いだメモ", new DocumentRepository(_connection).GetDetails([result.DocumentId!]).Single().Memo);
    }

    [Fact]
    public void MovedLibraryFolder_OpensFromNewPlace()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        var movedParent = Directory.CreateDirectory(Path.Combine(_root, "moved", "業務")).FullName;
        Directory.Move(_library.RootFolder, Path.Combine(movedParent, LibraryService.LibraryFolderName));

        var reopened = LibraryService.Open(Path.Combine(movedParent, LibraryService.LibraryFolderName, Path.GetFileName(_library.LibraryFilePath)));

        Assert.Equal(_library.Id, reopened.Id);
        using var connection = PdfManagerDatabase.Open(reopened.DataFolder.DatabasePath);
        Assert.Equal(4L, new DocumentRepository(connection).Count());
    }

    [Fact]
    public void RecentList_ReplacesOldPlaceOfSameLibrary()
    {
        var id = Guid.NewGuid();
        var settings = AppSettingsStore.RememberOpened(AppSettings.Empty, "PDF管理", @"D:\業務\PDF管理\PdfManager\PDF管理.pdflib", id);
        settings = AppSettingsStore.RememberOpened(settings, "別", @"C:\x\PdfManager\別.pdflib", Guid.NewGuid());

        settings = AppSettingsStore.RememberOpened(settings, "PDF管理", @"E:\業務\PdfManager\PDF管理.pdflib", id);

        Assert.Equal([@"E:\業務\PdfManager\PDF管理.pdflib", @"C:\x\PdfManager\別.pdflib"], settings.RecentLibraries.Select(r => r.LibraryFilePath));
    }
}
