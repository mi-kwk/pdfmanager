using Microsoft.Data.Sqlite;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Search;

namespace PdfManager.Core.Tests;

// 決定ログ DE-1〜DE-4(タグの別名)
public sealed class TagAliasTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly CatalogRepository _catalog;

    public TagAliasTests()
    {
        _connection = PdfManagerDatabase.Open(_databasePath);
        PdfManagerDatabase.Initialize(_connection);
        _catalog = new CatalogRepository(_connection);
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_databasePath + suffix);
        }
    }

    private TagSummary Tag(long id) => _catalog.GetTags().Single(t => t.Id == id);

    [Fact]
    public void MergeTags_KeepsSourceNameAndAliasesAsAliasesOfTarget()
    {
        var source = _catalog.AddTag("とんこつ");
        _catalog.SetAliases(source, ["豚骨"]);
        var target = _catalog.AddTag("とんこつらーめん");
        InsertDocument("doc-1");
        LinkTag("doc-1", source);

        _catalog.MergeTags(source, target);

        var merged = Assert.Single(_catalog.GetTags());
        Assert.Equal(("とんこつらーめん", 1L), (merged.Name, merged.UsageCount));
        Assert.Equal(["とんこつ", "豚骨"], merged.AliasNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void GetOrCreateTag_WithAliasName_ReturnsOfficialTag()
    {
        var target = _catalog.AddTag("ラーメン");
        _catalog.SetAliases(target, ["中華そば"]);

        Assert.Equal(target, _catalog.GetOrCreateTag("中華そば"));
        Assert.Single(_catalog.GetTags());
    }

    [Fact]
    public void AddAndRenameTag_RejectAliasOfAnotherTag()
    {
        var official = _catalog.AddTag("ラーメン");
        _catalog.SetAliases(official, ["らーめん屋"]);
        var other = _catalog.AddTag("うどん");

        Assert.Contains("「ラーメン」の別名", Assert.Throws<CatalogException>(() => _catalog.AddTag("らーめん屋")).Message);
        Assert.Contains("「ラーメン」の別名", Assert.Throws<CatalogException>(() => _catalog.RenameTag(other, "らーめん屋")).Message);
    }

    [Fact]
    public void RenameTag_ToOwnAlias_RemovesThatAlias()
    {
        var tag = _catalog.AddTag("とんこつ");
        _catalog.SetAliases(tag, ["とんこつらーめん", "豚骨"]);

        _catalog.RenameTag(tag, "とんこつらーめん");

        Assert.Equal(["豚骨"], Tag(tag).AliasNames);
        Assert.Equal("とんこつらーめん", Tag(tag).Name);
    }

    [Fact]
    public void SetAliases_RejectsTagNamesAndOtherAliases_AndIgnoresDuplicates()
    {
        var first = _catalog.AddTag("ラーメン");
        var second = _catalog.AddTag("うどん");
        _catalog.SetAliases(second, ["饂飩"]);

        Assert.Throws<CatalogException>(() => _catalog.SetAliases(first, ["うどん"]));
        Assert.Throws<CatalogException>(() => _catalog.SetAliases(first, ["ラーメン"]));
        Assert.Throws<CatalogException>(() => _catalog.SetAliases(first, ["饂飩"]));

        _catalog.SetAliases(first, ["中華そば", "ＡＢＣ", "中華ソバ", "  ", "abc", "中華そば"]);

        Assert.Equal(["中華そば", "中華ソバ", "ＡＢＣ"], Tag(first).AliasNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void KeywordSearch_FindsDocumentsByAlias()
    {
        var tag = _catalog.AddTag("とんこつらーめん");
        _catalog.SetAliases(tag, ["豚骨"]);
        InsertDocument("doc-1");
        InsertDocument("doc-2");
        LinkTag("doc-1", tag);

        var index = DocumentSearchIndex.Load(_connection);

        Assert.Equal(["doc-1"], index.Search("豚骨", []).Select(e => e.Id));
    }

    private void InsertDocument(string id) => Exec($"""
        INSERT INTO documents (id, title, file_hash, original_file_name, file_size, created_at, updated_at)
        VALUES ('{id}', 't', 'h-{id}', 'f.pdf', 1, '2026-09-30T00:00:00Z', '2026-09-30T00:00:00Z');
        """);

    private void LinkTag(string documentId, long tagId) =>
        Exec($"INSERT INTO document_tags (document_id, tag_id) VALUES ('{documentId}', {tagId});");

    private void Exec(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
