using Microsoft.Data.Sqlite;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Text;

namespace PdfManager.Core.Tests;

public sealed class CatalogRepositoryTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly CatalogRepository _catalog;

    public CatalogRepositoryTests()
    {
        _connection = PdfManagerDatabase.Open(_databasePath);
        PdfManagerDatabase.Initialize(_connection);
        _catalog = new CatalogRepository(_connection);
    }

    public void Dispose()
    {
        _connection.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_databasePath + suffix);
        }
    }

    [Theory]
    [InlineData("ＩＯＤＡＴＡ", "iodata")]
    [InlineData("ｿﾊﾞ", "ソバ")]
    [InlineData("  上野　 店 ", "上野 店")]
    public void Normalize_AbsorbsWidthCaseAndSpaces(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Normalize(input));
    }

    [Fact]
    public void AddField_RejectsDuplicateIgnoringWidthAndCase()
    {
        _catalog.AddField("Maker", FieldType.Text);

        var ex = Assert.Throws<CatalogException>(() => _catalog.AddField("ＭＡＫＥＲ", FieldType.Select));

        Assert.Contains("同じ名前", ex.Message);
    }

    [Fact]
    public void AddField_RejectsEmptyName()
    {
        Assert.Throws<CatalogException>(() => _catalog.AddField("   ", FieldType.Text));
    }

    [Fact]
    public void GetFields_ReturnsInSortOrderWithUsageCounts()
    {
        var store = _catalog.AddField("店舗", FieldType.Text);
        var month = _catalog.AddField("年月", FieldType.YearMonth);
        InsertDocument("doc-1");
        SetValue("doc-1", month, dateValue: "2026-09-01");

        _catalog.MoveField(month, -1);
        var fields = _catalog.GetFields();

        Assert.Equal([month, store], fields.Select(f => f.Id));
        Assert.Equal([1L, 0L], fields.Select(f => f.UsageCount));
        Assert.Equal("年月", fields[0].TypeLabel);
    }

    [Fact]
    public void ChangeFieldType_BlockedWhenValuesExist()
    {
        var field = _catalog.AddField("店舗", FieldType.Text);
        InsertDocument("doc-1");
        SetValue("doc-1", field, text: "上野店");

        var ex = Assert.Throws<CatalogException>(() => _catalog.ChangeFieldType(field, FieldType.Number));

        Assert.Contains("1 件", ex.Message);
    }

    [Fact]
    public void ChangeFieldType_FromSelectRemovesOptions()
    {
        var field = _catalog.AddField("区分", FieldType.Select);
        _catalog.AddOption(field, "飲食");

        _catalog.ChangeFieldType(field, FieldType.Text);

        var result = Assert.Single(_catalog.GetFields());
        Assert.Equal(FieldType.Text, result.Type);
        Assert.Empty(result.Options);
    }

    [Fact]
    public void DeleteField_RemovesItsValues()
    {
        var field = _catalog.AddField("店舗", FieldType.Text);
        InsertDocument("doc-1");
        SetValue("doc-1", field, text: "上野店");

        _catalog.DeleteField(field);

        Assert.Empty(_catalog.GetFields());
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM document_field_values;"));
    }

    [Fact]
    public void AddOption_OnlyForSelectFieldsAndUnique()
    {
        var text = _catalog.AddField("店舗", FieldType.Text);
        var select = _catalog.AddField("区分", FieldType.Select);
        _catalog.AddOption(select, "飲食");

        Assert.Throws<CatalogException>(() => _catalog.AddOption(text, "x"));
        Assert.Throws<CatalogException>(() => _catalog.AddOption(select, " 飲食 "));
    }

    [Fact]
    public void RenameOption_UpdatesDocumentValues()
    {
        var field = _catalog.AddField("区分", FieldType.Select);
        var option = _catalog.AddOption(field, "飲食");
        InsertDocument("doc-1");
        SetValue("doc-1", field, text: "飲食");

        _catalog.RenameOption(option, "飲食店");

        Assert.Equal("飲食店", Assert.Single(Assert.Single(_catalog.GetFields()).Options).Value);
        Assert.Equal("飲食店", Scalar("SELECT value_text FROM document_field_values;"));
    }

    [Fact]
    public void DeleteOption_RemovesDocumentValuesUsingIt()
    {
        var field = _catalog.AddField("区分", FieldType.Select);
        var food = _catalog.AddOption(field, "飲食");
        _catalog.AddOption(field, "物販");
        InsertDocument("doc-1");
        SetValue("doc-1", field, text: "飲食");
        Assert.Equal(1L, _catalog.GetFields()[0].Options[0].UsageCount);

        _catalog.DeleteOption(food);

        Assert.Equal(["物販"], _catalog.GetFields()[0].Options.Select(o => o.Value));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM document_field_values;"));
    }

    [Fact]
    public void GetOrCreateTag_ReusesTagIgnoringWidthAndCase()
    {
        var first = _catalog.GetOrCreateTag("IODATA");

        var second = _catalog.GetOrCreateTag("ｉｏｄａｔａ");

        Assert.Equal(first, second);
        Assert.Equal("IODATA", Assert.Single(_catalog.GetTags()).Name);
    }

    [Fact]
    public void RenameTag_AllowsChangingOnlyWidthOrCaseOfSameTag()
    {
        var tag = _catalog.AddTag("iodata");

        _catalog.RenameTag(tag, "IODATA");

        Assert.Equal("IODATA", Assert.Single(_catalog.GetTags()).Name);
    }

    [Fact]
    public void RenameTag_ToExistingNameSuggestsMerge()
    {
        _catalog.AddTag("上野");
        var other = _catalog.AddTag("うえの");

        var ex = Assert.Throws<CatalogException>(() => _catalog.RenameTag(other, "上野"));

        Assert.Contains("統合", ex.Message);
    }

    [Fact]
    public void MergeTags_MovesDocumentsWithoutDuplicates()
    {
        var source = _catalog.AddTag("そば屋");
        var target = _catalog.AddTag("蕎麦屋");
        InsertDocument("doc-1");
        InsertDocument("doc-2");
        LinkTag("doc-1", source);
        LinkTag("doc-1", target);
        LinkTag("doc-2", source);

        _catalog.MergeTags(source, target);

        var tag = Assert.Single(_catalog.GetTags());
        Assert.Equal(("蕎麦屋", 2L), (tag.Name, tag.UsageCount));
    }

    [Fact]
    public void DeleteTag_RemovesLinksOnly()
    {
        var tag = _catalog.AddTag("要確認");
        InsertDocument("doc-1");
        LinkTag("doc-1", tag);

        _catalog.DeleteTag(tag);

        Assert.Empty(_catalog.GetTags());
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM documents;"));
    }

    // ---- タググループ・順位・紐づけ(決定ログ R-11) ----

    // 例:分類１(順位 1)= 漫画・雑誌、分類２(順位 2)= ドラゴンボール・ジャンプ、分類３(順位 3)= 悟空、作者(独立)
    private (long Bunrui1, long Bunrui2, long Bunrui3, long Author, long Manga, long Magazine) CreateBookGroups()
    {
        var bunrui1 = _catalog.AddGroup("分類１", 1);
        var bunrui2 = _catalog.AddGroup("分類２", 2);
        var bunrui3 = _catalog.AddGroup("分類３", 3);
        var author = _catalog.AddGroup("作者", TagGroup.IndependentRank);
        return (bunrui1, bunrui2, bunrui3, author, _catalog.AddTag("漫画", bunrui1), _catalog.AddTag("雑誌", bunrui1));
    }

    private TagSummary Tag(string name) => _catalog.GetTags().Single(t => t.Name == name);

    [Fact]
    public void Groups_HaveRanksAndUniqueNames()
    {
        CreateBookGroups();

        Assert.Equal([("分類１", 1), ("分類２", 2), ("分類３", 3), ("作者", 0)], _catalog.GetGroups().Select(g => (g.Name, g.Rank)));
        Assert.Equal("独立", _catalog.GetGroups()[3].RankLabel);
        Assert.Throws<CatalogException>(() => _catalog.AddGroup(" 分類1 ", 1));
        Assert.Throws<CatalogException>(() => _catalog.AddGroup("分類６", 6));
    }

    [Fact]
    public void Links_OnlyToTagsOneRankAbove_AndAllowMultipleParents()
    {
        var g = CreateBookGroups();
        var jump = _catalog.AddTag("ジャンプ", g.Bunrui2, [g.Manga, g.Magazine]);
        var dragonBall = _catalog.AddTag("ドラゴンボール", g.Bunrui2, [g.Manga]);
        var goku = _catalog.AddTag("悟空", g.Bunrui3, [dragonBall]);

        Assert.Equal(["漫画", "雑誌"], Tag("ジャンプ").ParentNames);
        Assert.Equal("分類２ ・ 漫画、雑誌", Tag("ジャンプ").DetailText);
        Assert.Equal([dragonBall], Tag("悟空").ParentIds);
        // 順位 3 から順位 1 へは紐づけられない。独立・自由タグは紐づけを持てない
        Assert.Throws<CatalogException>(() => _catalog.SetTagParents(goku, [g.Manga]));
        var author = _catalog.AddTag("〇〇〇", g.Author);
        Assert.Throws<CatalogException>(() => _catalog.SetTagParents(author, [g.Manga]));
        Assert.Throws<CatalogException>(() => _catalog.AddTag("バトル", null, [g.Manga]));
        Assert.Equal("自由タグ", Tag("バトル").GroupLabel);
    }

    [Fact]
    public void ChangingRankOrGroup_RemovesLinksThatNoLongerFit()
    {
        var g = CreateBookGroups();
        var dragonBall = _catalog.AddTag("ドラゴンボール", g.Bunrui2, [g.Manga]);
        _catalog.AddTag("悟空", g.Bunrui3, [dragonBall]);

        Assert.Equal(2L, _catalog.CountLinksBrokenByRank(g.Bunrui2, 4));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM tag_links;"));
        _catalog.SetTagGroup(dragonBall, null);

        Assert.Empty(Tag("ドラゴンボール").ParentIds);
        Assert.Empty(Tag("悟空").ParentIds);
    }

    [Fact]
    public void DeleteGroup_KeepsTagsAsFreeTags()
    {
        var g = CreateBookGroups();
        InsertDocument("d1");
        LinkTag("d1", g.Manga);
        _catalog.SetGroupPinned(g.Bunrui1, true);

        _catalog.DeleteGroup(g.Bunrui1);

        Assert.Null(Tag("漫画").GroupId);
        Assert.Equal(1, Tag("漫画").UsageCount);
        Assert.DoesNotContain(_catalog.GetGroups(), x => x.Name == "分類１");
    }

    [Fact]
    public void PinnedGroups_AreItemsInOrder()
    {
        var g = CreateBookGroups();

        _catalog.SetGroupPinned(g.Author, true);
        _catalog.SetGroupPinned(g.Bunrui1, true);
        _catalog.MovePinnedGroup(g.Bunrui1, -1);
        _catalog.SetGroupPinned(g.Bunrui2, true);
        _catalog.SetGroupPinned(g.Bunrui2, false);

        Assert.Equal(["分類１", "作者"], _catalog.GetGroups().Where(x => x.IsPinned).OrderBy(x => x.PinOrder).Select(x => x.Name));
    }

    [Fact]
    public void GetOrCreateTag_NewTagGetsGroupAndParents_ExistingTagIsUnchanged()
    {
        var g = CreateBookGroups();

        var naruto = _catalog.GetOrCreateTag("ナルト", g.Bunrui2, [g.Manga]);
        var again = _catalog.GetOrCreateTag("ナルト", g.Bunrui3, [g.Magazine]);

        Assert.Equal(naruto, again);
        Assert.Equal(g.Bunrui2, Tag("ナルト").GroupId);
        Assert.Equal(["漫画"], Tag("ナルト").ParentNames);
    }

    [Fact]
    public void MergeTags_CarriesOverLinks()
    {
        var g = CreateBookGroups();
        var source = _catalog.AddTag("ドラゴン ボール", g.Bunrui2, [g.Magazine]);
        var target = _catalog.AddTag("ドラゴンボール", g.Bunrui2, [g.Manga]);
        _catalog.AddTag("悟空", g.Bunrui3, [source]);

        _catalog.MergeTags(source, target);

        Assert.Equal(["漫画", "雑誌"], Tag("ドラゴンボール").ParentNames);
        Assert.Equal([target], Tag("悟空").ParentIds);
    }

    private void InsertDocument(string id) => Exec($"""
        INSERT INTO documents (id, title, file_hash, original_file_name, file_size, created_at, updated_at)
        VALUES ('{id}', 't', 'h-{id}', 'f.pdf', 1, '2026-09-30T00:00:00Z', '2026-09-30T00:00:00Z');
        """);

    private void SetValue(string documentId, long fieldId, string? text = null, string? dateValue = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO document_field_values (document_id, field_id, value_text, value_date) VALUES ($d, $f, $t, $date);";
        command.Parameters.AddWithValue("$d", documentId);
        command.Parameters.AddWithValue("$f", fieldId);
        command.Parameters.AddWithValue("$t", (object?)text ?? DBNull.Value);
        command.Parameters.AddWithValue("$date", (object?)dateValue ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void LinkTag(string documentId, long tagId) =>
        Exec($"INSERT INTO document_tags (document_id, tag_id) VALUES ('{documentId}', {tagId});");

    private void Exec(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
