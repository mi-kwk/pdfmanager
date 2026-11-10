using Microsoft.Data.Sqlite;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;

namespace PdfManager.Core.Tests;

// 決定ログ EC-1〜EC-9(同じグループの中の親子のタグ)
public sealed class ParentTagTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly CatalogRepository _catalog;
    private readonly long _drinks;
    private readonly long _whisky;
    private readonly long _scotch;
    private readonly long _single;

    public ParentTagTests()
    {
        _connection = PdfManagerDatabase.Open(_databasePath);
        PdfManagerDatabase.Initialize(_connection);
        _catalog = new CatalogRepository(_connection);
        _drinks = _catalog.AddGroup("お酒", 1);
        _whisky = _catalog.AddTag("ウィスキー", _drinks);
        _scotch = _catalog.AddTag("スコッチ・ウイスキー", _drinks);
        _single = _catalog.AddTag("シングルモルト", _drinks);
    }

    public void Dispose()
    {
        _connection.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_databasePath + suffix);
        }
    }

    private void AddDocument(string id, params long[] tags) =>
        new DocumentRepository(_connection).Insert(new NewDocument(id, id, "hash-" + id, id + ".pdf", 10, 1, id + ".pdf"), [], DateTimeOffset.UtcNow.AddDays(-5), null, tags);

    private IReadOnlyList<long> TagsOf(string id) => new DocumentRepository(_connection).GetDetails([id]).Single().TagIds.Order().ToList();

    private TagSummary Tag(long id) => _catalog.GetTags().Single(t => t.Id == id);

    [Fact]
    public void SetParent_OnlySameGroup_AndNoLoops()
    {
        var other = _catalog.AddTag("ラム", _catalog.AddGroup("別", 1));

        _catalog.SetTagParentTag(_scotch, _whisky);
        _catalog.SetTagParentTag(_single, _scotch);

        Assert.Equal(_whisky, Tag(_scotch).ParentTagId);
        Assert.Equal("ウィスキー", Tag(_scotch).ParentTagName);
        Assert.Contains("親:ウィスキー", Tag(_scotch).DetailText);
        Assert.Contains("同じタググループ", Assert.Throws<CatalogException>(() => _catalog.SetTagParentTag(other, _whisky)).Message);
        Assert.Throws<CatalogException>(() => _catalog.SetTagParentTag(_whisky, _whisky));
        // 孫を親にすると輪になる
        Assert.Contains("下にある", Assert.Throws<CatalogException>(() => _catalog.SetTagParentTag(_whisky, _single)).Message);
        _catalog.SetTagParentTag(_single, null);
        Assert.Null(Tag(_single).ParentTagId);
    }

    [Fact]
    public void MissingAncestors_AreFoundAndAddedForAllLevels()
    {
        AddDocument("a", _single);
        AddDocument("b", _scotch, _whisky);
        AddDocument("c", _whisky);
        _catalog.SetTagParentTag(_scotch, _whisky);
        _catalog.SetTagParentTag(_single, _scotch);

        // シングルモルトの「a」は、スコッチもウィスキーも付いていない
        Assert.Equal(["a"], _catalog.DocumentsMissingAncestors(_whisky));
        Assert.Equal(["a"], _catalog.DocumentsMissingAncestors(_scotch));
        var added = _catalog.AddMissingAncestors(["a", "b", "c"], DateTimeOffset.UtcNow);

        Assert.Equal(2, added);
        Assert.Equal(new[] { _whisky, _scotch, _single }.Order(), TagsOf("a"));
        Assert.Empty(_catalog.DocumentsMissingAncestors(_whisky));
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT updated_at FROM documents WHERE id = 'a';";
        Assert.True(DateTimeOffset.Parse((string)command.ExecuteScalar()!, System.Globalization.CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void GroupChange_DeleteAndMerge_KeepTreeValid()
    {
        _catalog.SetTagParentTag(_scotch, _whisky);
        _catalog.SetTagParentTag(_single, _scotch);

        // 親を消すと、子は親なしになる(子は消えない)
        var canadian = _catalog.AddTag("カナディアン", _drinks, null);
        _catalog.SetTagParentTag(canadian, _scotch);
        _catalog.DeleteTag(_scotch);
        Assert.Null(Tag(_single).ParentTagId);
        Assert.Null(Tag(canadian).ParentTagId);

        // 別のグループへ移すと、親子は外れる
        _catalog.SetTagParentTag(_single, _whisky);
        _catalog.SetTagGroup(_single, _catalog.AddGroup("ほか", 1));
        Assert.Null(Tag(_single).ParentTagId);

        // 統合すると、子は統合先の子になる
        var bourbon = _catalog.AddTag("バーボン", _drinks);
        var whiskey = _catalog.AddTag("ウイスキー(旧)", _drinks);
        _catalog.SetTagParentTag(bourbon, whiskey);
        _catalog.MergeTags(whiskey, _whisky);
        Assert.Equal(_whisky, Tag(bourbon).ParentTagId);
    }

    [Fact]
    public void TagTree_OrdersChildrenUnderParents()
    {
        var items = new[] { ("b", (string?)null), ("a2", "a"), ("a", null), ("a1", "a"), ("x", "missing"), ("a1a", "a1") };
        var ordered = TagTree.Order(items, i => i.Item1, i => i.Item2, Comparer<(string, string?)>.Create((p, q) => string.CompareOrdinal(p.Item1, q.Item1)));

        Assert.Equal(["a", "a1", "a1a", "a2", "b", "x"], ordered.Select(o => o.Item.Item1));
        Assert.Equal([0, 1, 2, 1, 0, 0], ordered.Select(o => o.Depth));
        Assert.Equal("ウィスキー › スコッチ", TagTree.Path(["ウィスキー"], "スコッチ"));
    }
}
