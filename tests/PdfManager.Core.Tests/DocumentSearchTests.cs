using Microsoft.Data.Sqlite;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Search;

namespace PdfManager.Core.Tests;

public sealed class DocumentSearchTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly long _iodata;
    private readonly long _manager;

    public DocumentSearchTests()
    {
        _connection = PdfManagerDatabase.Open(_databasePath);
        PdfManagerDatabase.Initialize(_connection);
        var catalog = new CatalogRepository(_connection);
        _iodata = catalog.AddTag("IODATA");
        _manager = catalog.AddTag("■■マネージャー");
        var month = catalog.AddField("年月", FieldType.YearMonth);
        var repository = new DocumentRepository(_connection);

        repository.Insert(Doc("d1", "上野店 売上報告", "IODATA-MPC-WE1.pdf"), [], At(1),
            new Dictionary<long, FieldValue> { [month] = FieldValues.Parse(FieldType.YearMonth, "2026-09")! }, [_iodata, _manager]);
        repository.Insert(Doc("d2", "商品A 取扱説明書", "manual.pdf"), [], At(2), tagIds: [_iodata]);
        repository.Insert(Doc("d3", "秋葉原店 売上報告", "akiba.pdf"), [], At(3), tagIds: [_manager]);
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
    [InlineData("上野", new[] { "d1" })]                // 部分一致
    [InlineData("ｉｏｄａｔａ", new[] { "d2", "d1" })]    // 全角・小文字でタグ名・ファイル名に一致
    [InlineData("まねーじゃー", new[] { "d3", "d1" })]   // ひらがなでカタカナのタグ名に一致
    [InlineData("売上　報告 秋葉原", new[] { "d3" })]     // 空白区切り(全角空白も)はすべてを含むもの
    [InlineData("2026-09", new[] { "d1" })]             // 項目の値
    [InlineData("mpc-we1", new[] { "d1" })]             // ファイル名
    [InlineData("", new[] { "d3", "d2", "d1" })]        // 空なら全件(新しい順)
    [InlineData("存在しない", new string[0])]
    public void Search_MatchesFuzzily(string keywords, string[] expected)
    {
        var index = DocumentSearchIndex.Load(_connection);

        Assert.Equal(expected, index.Search(keywords, []).Select(e => e.Id));
    }

    [Fact]
    public void Update_ReplacesEditableValuesAndMemoIsSearchable()
    {
        var repository = new DocumentRepository(_connection);
        var before = repository.GetDetails(["d2"]).Single();

        repository.Update(new DocumentUpdate("d2", "商品A 取扱説明書 第4版", "倉庫の棚Bに原本あり", 4, "IODATA-MPC-WE1-v4.pdf",
            FileNameSource.Tag, _iodata, "-MPC-WE1-v4", new Dictionary<long, FieldValue>(), [_manager]), At(9));
        var after = repository.GetDetails(["d2"]).Single();
        var index = DocumentSearchIndex.Load(_connection);

        Assert.Equal([_iodata], before.TagIds);
        Assert.Equal(("商品A 取扱説明書 第4版", "倉庫の棚Bに原本あり", 4, "IODATA-MPC-WE1-v4.pdf"), (after.Title, after.Memo, after.Rating, after.FileName));
        Assert.Equal((FileNameSource.Tag, (long?)_iodata, "-MPC-WE1-v4"), (after.FileNameSource, after.FileNameSourceId, after.FileNameSuffix));
        Assert.Equal([_manager], after.TagIds);
        Assert.Equal(("manual.pdf", 0L, (DateTimeOffset?)null), (after.OriginalFileName, after.ViewCount, after.LastViewedAt));
        Assert.Equal(["d2"], index.Search("棚ｂ", []).Select(e => e.Id));
    }

    [Fact]
    public void Update_RemovesFieldValuesThatWereCleared()
    {
        var repository = new DocumentRepository(_connection);
        var d1 = repository.GetDetails(["d1"]).Single();
        Assert.Single(d1.FieldValues);

        repository.Update(new DocumentUpdate("d1", d1.Title, "", 0, d1.FileName, FileNameSource.None, null, "",
            new Dictionary<long, FieldValue>(), d1.TagIds), At(9));

        Assert.Empty(repository.GetDetails(["d1"]).Single().FieldValues);
    }

    [Fact]
    public void Search_CombinesKeywordsAndRequiredTags()
    {
        var index = DocumentSearchIndex.Load(_connection);

        Assert.Equal(["d1"], index.Search("売上", [_iodata]).Select(e => e.Id));
        Assert.Equal(["d1"], index.Search("", [_iodata, _manager]).Select(e => e.Id));
    }

    [Fact]
    public void Entry_HasTagsAndFormattedFieldValues()
    {
        var entry = DocumentSearchIndex.Load(_connection).Search("上野", []).Single();

        Assert.Equal(["IODATA", "■■マネージャー"], entry.TagNames);
        Assert.Equal([("年月", "2026-09")], entry.FieldValues);
    }

    private static NewDocument Doc(string id, string title, string fileName) =>
        new(id, title, "hash-" + id, fileName, 1, 1, fileName);

    private static DateTimeOffset At(int day) => new(2026, 10, day, 0, 0, 0, TimeSpan.Zero);
}
