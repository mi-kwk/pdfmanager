using Microsoft.Data.Sqlite;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Search;

namespace PdfManager.Core.Tests;

// 検索条件の保存(F-16、決定ログ X-1〜X-4)
public sealed class SavedSearchTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SavedSearchRepository _searches;

    public SavedSearchTests()
    {
        _connection = PdfManagerDatabase.Open(_databasePath);
        PdfManagerDatabase.Initialize(_connection);
        _searches = new SavedSearchRepository(_connection);
    }

    public void Dispose()
    {
        _connection.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_databasePath + suffix);
        }
    }

    [Fact]
    public void Condition_RoundTripsThroughDatabase()
    {
        var condition = new SavedSearchCondition
        {
            Keywords = "売上",
            TagAny = true,
            IncludedTagIds = [3, 5],
            ExcludedTagIds = [7],
            Fields = [new SavedFieldCondition(2, "", "2026-04", "2026-09", ["2026-10"])],
            MinRating = 3,
            RegisteredFrom = "2026-01-01",
            IncludeOldVersions = true,
            FilterByStorage = true,
            StorageId = null,
        };

        _searches.Add("上野の売上", condition);
        var loaded = Assert.Single(_searches.GetAll());

        Assert.Equal("上野の売上", loaded.Name);
        Assert.Equal(condition.Keywords, loaded.Condition.Keywords);
        Assert.Equal([3L, 5L], loaded.Condition.IncludedTagIds);
        Assert.Equal([7L], loaded.Condition.ExcludedTagIds);
        Assert.Equal("2026-04", loaded.Condition.Fields.Single().From);
        Assert.Equal(["2026-10"], loaded.Condition.Fields.Single().Values);
        Assert.Equal(3, loaded.Condition.MinRating);
        Assert.True(loaded.Condition.FilterByStorage);
        Assert.Null(loaded.Condition.StorageId);
    }

    [Fact]
    public void Names_AreUniqueAndCanBeRenamedMovedAndDeleted()
    {
        var a = _searches.Add("A", new SavedSearchCondition());
        var b = _searches.Add("B", new SavedSearchCondition());

        Assert.Throws<CatalogException>(() => _searches.Add(" ａ ", new SavedSearchCondition()));
        Assert.Throws<CatalogException>(() => _searches.Rename(b, "a"));
        _searches.Rename(b, "B2");
        _searches.Move(b, -1);
        Assert.Equal(["B2", "A"], _searches.GetAll().Select(s => s.Name));
        _searches.Delete(a);
        Assert.Equal(["B2"], _searches.GetAll().Select(s => s.Name));
    }

    [Fact]
    public void BrokenJson_IsTreatedAsNoCondition()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO saved_searches (name, condition_json) VALUES ('壊れた', '{not json');";
        command.ExecuteNonQuery();

        Assert.Equal("", Assert.Single(_searches.GetAll()).Condition.Keywords);
    }
}
