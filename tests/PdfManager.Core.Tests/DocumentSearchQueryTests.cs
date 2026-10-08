using Microsoft.Data.Sqlite;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Search;

namespace PdfManager.Core.Tests;

public sealed class DocumentSearchQueryTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly long _iodata, _manager, _check;
    private readonly long _month, _price, _area, _store, _issued;

    public DocumentSearchQueryTests()
    {
        _connection = PdfManagerDatabase.Open(_databasePath);
        PdfManagerDatabase.Initialize(_connection);
        var catalog = new CatalogRepository(_connection);
        _iodata = catalog.AddTag("IODATA");
        _manager = catalog.AddTag("マネージャー");
        _check = catalog.AddTag("要確認");
        _month = catalog.AddField("年月", FieldType.YearMonth);
        _price = catalog.AddField("金額", FieldType.Number);
        _area = catalog.AddField("エリア", FieldType.Select);
        catalog.AddOption(_area, "上野エリア");
        catalog.AddOption(_area, "秋葉原エリア");
        _store = catalog.AddField("店舗", FieldType.Text);
        _issued = catalog.AddField("発行日", FieldType.Date);

        var repository = new DocumentRepository(_connection);
        Add(repository, "a", "あおい報告", 1, [_iodata], new() { [_month] = "2026-07", [_price] = "500", [_area] = "上野エリア", [_store] = "上野店" }, rating: 3);
        Add(repository, "b", "かもめ報告", 2, [_iodata, _manager], new() { [_month] = "2026-08", [_price] = "1500", [_area] = "秋葉原エリア", [_issued] = "2026-08-15" }, rating: 5);
        Add(repository, "c", "イカ報告", 3, [_manager, _check], new() { [_month] = "2026-09", [_store] = "秋葉原店" });
        Add(repository, "d", "うみ報告", 4, [], new() { [_price] = "900" }, rating: 1);
        repository.RecordView("b", At(10));
        repository.RecordView("d", At(20));
    }

    public void Dispose()
    {
        _connection.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_databasePath + suffix);
        }
    }

    private string[] Ids(SearchQuery query) => DocumentSearchIndex.Load(_connection).Search(query).Select(e => e.Id).ToArray();

    [Fact]
    public void Tags_AllAnyAndExclude()
    {
        Assert.Equal(["b"], Ids(new SearchQuery { IncludedTagIds = [_iodata, _manager] }));
        Assert.Equal(["c", "b", "a"], Ids(new SearchQuery { IncludedTagIds = [_iodata, _manager], TagMode = TagMatchMode.Any }));
        Assert.Equal(["b", "a"], Ids(new SearchQuery { IncludedTagIds = [_iodata, _manager], TagMode = TagMatchMode.Any, ExcludedTagIds = [_check] }));
        Assert.Equal(["d", "b", "a"], Ids(new SearchQuery { ExcludedTagIds = [_check] }));
    }

    [Fact]
    public void Fields_RangesAnyOfAndContains()
    {
        Assert.Equal(["c", "b"], Ids(new SearchQuery { FieldConditions = [new FieldCondition(_month) { MinDate = "2026-08-01" }] }));
        Assert.Equal(["b", "a"], Ids(new SearchQuery { FieldConditions = [new FieldCondition(_month) { MaxDate = "2026-08-01" }] }));
        Assert.Equal(["d", "a"], Ids(new SearchQuery { FieldConditions = [new FieldCondition(_price) { MinNumber = 500, MaxNumber = 1000 }] }));
        Assert.Equal(["b"], Ids(new SearchQuery { FieldConditions = [new FieldCondition(_area) { AnyOf = new HashSet<string> { "秋葉原エリア" } }] }));
        Assert.Equal(["c"], Ids(new SearchQuery { FieldConditions = [new FieldCondition(_store) { Contains = "秋葉原" }] }));
        Assert.Equal(["a"], Ids(new SearchQuery { FieldConditions = [new FieldCondition(_store) { Contains = "上野" }] }));
        Assert.Equal(["b"], Ids(new SearchQuery { FieldConditions = [new FieldCondition(_issued) { MinDate = "2026-08-01", MaxDate = "2026-08-31" }] }));
    }

    [Fact]
    public void RatingAndViewedConditions()
    {
        Assert.Equal(["b", "a"], Ids(new SearchQuery { MinRating = 3 }));
        Assert.Equal(["c"], Ids(new SearchQuery { UnratedOnly = true }));
        Assert.Equal(["c", "a"], Ids(new SearchQuery { NeverViewedOnly = true }));
        Assert.Equal(["d"], Ids(new SearchQuery { LastViewedFrom = DateOnly.FromDateTime(At(15).ToLocalTime().DateTime) }));
        Assert.Equal(["b", "a"], Ids(new SearchQuery { RegisteredTo = DateOnly.FromDateTime(At(2).ToLocalTime().DateTime) }));
    }

    [Fact]
    public void Sort_PutsMissingValuesLastInBothDirections()
    {
        Assert.Equal(["b", "a", "d", "c"], Ids(new SearchQuery { Sort = SortKey.Rating }));
        Assert.Equal(["d", "a", "b", "c"], Ids(new SearchQuery { Sort = SortKey.Rating, Descending = false }));
        Assert.Equal(["d", "b", "c", "a"], Ids(new SearchQuery { Sort = SortKey.LastViewed }));
        Assert.Equal(["a", "d", "b", "c"], Ids(new SearchQuery { Sort = SortKey.Field, SortFieldId = _price, Descending = false }));
        Assert.Equal(["a", "b", "c", "d"], Ids(new SearchQuery { Sort = SortKey.Registered, Descending = false }));
    }

    [Fact]
    public void Sort_TitleUsesJapaneseOrderIgnoringKanaType()
    {
        // あおい・イカ・うみ・かもめ(ひらがな・カタカナを区別せず五十音順)
        Assert.Equal(["a", "c", "d", "b"], Ids(new SearchQuery { Sort = SortKey.Title, Descending = false }));
    }

    [Fact]
    public void Counts_AreWithinCurrentResults()
    {
        var index = DocumentSearchIndex.Load(_connection);
        var results = index.Search(new SearchQuery { IncludedTagIds = [_manager] });

        var tags = DocumentSearchIndex.CountTags(results);
        var areas = DocumentSearchIndex.CountFieldTexts(results, _area);

        Assert.Equal(2, tags[_manager]);
        Assert.Equal(1, tags[_iodata]);
        Assert.Equal(1, tags[_check]);
        Assert.Equal(new Dictionary<string, int> { ["秋葉原エリア"] = 1 }, areas);
    }

    // 決定ログ Q-5: タグが 1 つも付いていない PDF は「未設定」
    [Fact]
    public void UntaggedOnly_FindsDocumentsWithoutTags()
    {
        var index = DocumentSearchIndex.Load(_connection);

        Assert.Equal(["d"], Ids(new SearchQuery { UntaggedOnly = true }));
        Assert.Equal(1, DocumentSearchIndex.CountUntagged(index.Search(new SearchQuery())));
    }

    // 決定ログ Q-6: 補助項目は、入っている値を選んで絞り込める(年月は 2026-08 の形)
    [Fact]
    public void FieldValues_CanBeChosenFromRegisteredValues()
    {
        var index = DocumentSearchIndex.Load(_connection);

        Assert.Equal(["2026-09", "2026-08", "2026-07"], index.DistinctFieldValues(_month));
        Assert.Equal(["1500", "900", "500"], index.DistinctFieldValues(_price));
        Assert.Equal(["b"], Ids(new SearchQuery { FieldConditions = [new FieldCondition(_month) { AnyOfDisplay = new HashSet<string> { "2026-08" } }] }));
        Assert.Equal(["c", "a"], Ids(new SearchQuery { FieldConditions = [new FieldCondition(_month) { AnyOfDisplay = new HashSet<string> { "2026-07", "2026-09" } }] }));
        Assert.Equal(1, DocumentSearchIndex.CountFieldValues(index.Search(new SearchQuery { IncludedTagIds = [_manager] }), _month)["2026-09"]);
    }

    private void Add(DocumentRepository repository, string id, string title, int day, long[] tags, Dictionary<long, string> fields, int rating = 0)
    {
        var catalog = new CatalogRepository(_connection).GetFields();
        var values = fields.ToDictionary(f => f.Key, f => FieldValues.Parse(catalog.Single(d => d.Id == f.Key).Type, f.Value)!);
        repository.Insert(new NewDocument(id, title, "h" + id, id + ".pdf", 1, 1, id + ".pdf"), [], At(day), values, tags);
        if (rating > 0)
        {
            var detail = repository.GetDetails([id]).Single();
            repository.Update(new DocumentUpdate(id, title, "", rating, detail.FileName, FileNameSource.None, null, "", detail.FieldValues, detail.TagIds), At(day));
        }
    }

    private static DateTimeOffset At(int day) => new(2026, 10, day, 3, 0, 0, TimeSpan.Zero);
}
