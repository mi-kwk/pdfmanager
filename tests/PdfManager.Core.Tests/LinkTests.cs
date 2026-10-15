using Microsoft.Data.Sqlite;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Library;
using PdfManager.Core.Search;

namespace PdfManager.Core.Tests;

// 続き・関連(決定ログ V-1〜V-8)
public sealed class LinkTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}")).FullName;
    private readonly LibraryInfo _library;
    private readonly SqliteConnection _connection;
    private readonly LinkRepository _links;

    public LinkTests()
    {
        _library = LibraryService.Create(_root, "続きテスト");
        _connection = PdfManagerDatabase.Open(_library.DataFolder.DatabasePath);
        _links = new LinkRepository(_connection);
        var repository = new DocumentRepository(_connection);
        foreach (var (id, day) in new[] { ("zen", 1), ("chu", 2), ("kou", 3), ("shiryo", 4), ("other", 5) })
        {
            repository.Insert(new NewDocument(id, "物語 " + id, "h" + id, id + ".pdf", 1, 1, id + ".pdf"), [], new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.Zero));
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string[] Chain(string id) => _links.GetChain(id).Select(d => d.Id).ToArray();

    [Fact]
    public void InsertNextAndPrevious_BuildOrderedChain_AndInsertInTheMiddle()
    {
        _links.InsertNext("zen", "kou");
        _links.InsertNext("zen", "chu");          // 前編と後編の間に中編を入れる

        Assert.Equal(["zen", "chu", "kou"], Chain("kou"));
        Assert.Equal("zen", _links.PreviousOf("chu"));
        Assert.Equal("kou", _links.NextOf("chu"));

        _links.InsertPrevious("zen", "other");    // 先頭の前に足す
        Assert.Equal(["other", "zen", "chu", "kou"], Chain("zen"));
        Assert.Empty(Chain("shiryo"));
    }

    [Fact]
    public void Join_RejectsDocumentAlreadyInAnotherChain()
    {
        _links.InsertNext("zen", "chu");
        _links.InsertNext("kou", "shiryo");

        Assert.Throws<InvalidOperationException>(() => _links.InsertNext("zen", "kou"));
        Assert.Throws<InvalidOperationException>(() => _links.InsertNext("other", "other"));
    }

    [Fact]
    public void RemovingMiddle_ReconnectsPreviousAndNext()
    {
        _links.InsertNext("zen", "chu");
        _links.InsertNext("chu", "kou");

        _links.RemoveFromChain("chu");

        Assert.Equal(["zen", "kou"], Chain("zen"));
        Assert.Empty(Chain("chu"));
    }

    [Fact]
    public void DeletingMiddleDocument_ReconnectsChain()
    {
        _links.InsertNext("zen", "chu");
        _links.InsertNext("chu", "kou");
        _links.AddRelated("chu", "shiryo");

        new DocumentRemover(_library.DataFolder, _ => { }).Delete("chu", "chu.pdf");

        Assert.Equal(["zen", "kou"], Chain("zen"));
        Assert.Empty(_links.GetRelated("shiryo"));
    }

    [Fact]
    public void Related_IsSymmetricAndUnordered()
    {
        _links.AddRelated("kou", "shiryo");
        _links.AddRelated("shiryo", "kou");
        _links.AddRelated("zen", "shiryo");

        Assert.Equal(["zen", "kou"], _links.GetRelated("shiryo").Select(d => d.Id));
        Assert.Equal(["shiryo"], _links.GetRelated("kou").Select(d => d.Id));
        _links.RemoveRelated("shiryo", "kou");
        Assert.Empty(_links.GetRelated("kou"));
    }

    [Fact]
    public void NewVersion_TakesOverChainAndRelated()
    {
        _links.InsertNext("zen", "chu");
        _links.InsertNext("chu", "kou");
        _links.AddRelated("chu", "shiryo");

        _links.TransferLinks("chu", "other");

        Assert.Equal(["zen", "other", "kou"], Chain("zen"));
        Assert.Equal(["other"], _links.GetRelated("shiryo").Select(d => d.Id));
        Assert.Empty(Chain("chu"));
    }

    [Fact]
    public void SearchIndex_ShowsPositionInChain()
    {
        _links.InsertNext("zen", "chu");
        _links.InsertNext("chu", "kou");

        var entries = DocumentSearchIndex.Load(_connection).Search(new SearchQuery()).ToDictionary(e => e.Id);

        Assert.Equal("続き 1/3", entries["zen"].ChainText);
        Assert.Equal("続き 3/3", entries["kou"].ChainText);
        Assert.Equal("", entries["shiryo"].ChainText);
    }
}
