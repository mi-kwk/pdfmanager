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

    [Fact]
    public void SetTagAttributes_ReplacesAttributesAndAllowsMultiple()
    {
        var area = _catalog.AddField("エリア", FieldType.Select);
        var ueno = _catalog.AddOption(area, "上野エリア");
        var akiba = _catalog.AddOption(area, "秋葉原エリア");
        var manager = _catalog.AddTag("■■マネージャー");

        _catalog.SetTagAttributes(manager, [akiba, ueno]);
        var both = Assert.Single(_catalog.GetTags());
        _catalog.SetTagAttributes(manager, [akiba]);
        var one = Assert.Single(_catalog.GetTags());

        Assert.Equal(["上野エリア", "秋葉原エリア"], both.Attributes.Select(a => a.Value));
        Assert.Equal("上野エリア、秋葉原エリア", both.AttributeText);
        Assert.Equal([("エリア", "秋葉原エリア")], one.Attributes.Select(a => (a.FieldName, a.Value)));
    }

    [Fact]
    public void TagAttribute_FollowsOptionRenameAndDisappearsWithOption()
    {
        var area = _catalog.AddField("エリア", FieldType.Select);
        var ueno = _catalog.AddOption(area, "上野");
        var manager = _catalog.AddTag("〇〇マネージャー");
        _catalog.SetTagAttributes(manager, [ueno]);

        _catalog.RenameOption(ueno, "上野エリア");
        var renamed = Assert.Single(Assert.Single(_catalog.GetTags()).Attributes).Value;
        _catalog.DeleteOption(ueno);

        Assert.Equal("上野エリア", renamed);
        Assert.Empty(Assert.Single(_catalog.GetTags()).Attributes);
    }

    [Fact]
    public void TagAttribute_DisappearsWhenFieldStopsBeingSelectType()
    {
        var area = _catalog.AddField("エリア", FieldType.Select);
        var ueno = _catalog.AddOption(area, "上野エリア");
        var manager = _catalog.AddTag("■■マネージャー");
        _catalog.SetTagAttributes(manager, [ueno]);

        _catalog.ChangeFieldType(area, FieldType.Text);

        Assert.Empty(Assert.Single(_catalog.GetTags()).Attributes);
    }

    [Fact]
    public void MergeTags_CarriesOverAttributes()
    {
        var area = _catalog.AddField("エリア", FieldType.Select);
        var ueno = _catalog.AddOption(area, "上野エリア");
        var akiba = _catalog.AddOption(area, "秋葉原エリア");
        var source = _catalog.AddTag("■■MGR");
        var target = _catalog.AddTag("■■マネージャー");
        _catalog.SetTagAttributes(source, [ueno, akiba]);
        _catalog.SetTagAttributes(target, [ueno]);

        _catalog.MergeTags(source, target);

        Assert.Equal(["上野エリア", "秋葉原エリア"], Assert.Single(_catalog.GetTags()).Attributes.Select(a => a.Value));
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
