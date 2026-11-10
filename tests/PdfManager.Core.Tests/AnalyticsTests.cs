using Microsoft.Data.Sqlite;
using PdfManager.Core.Analytics;
using PdfManager.Core.Database;

namespace PdfManager.Core.Tests;

// 決定ログ DA-1〜DA-7(分析画面の記録と集計)
public sealed class AnalyticsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private static readonly TimeSpan Jst = TimeSpan.FromHours(9);

    public AnalyticsTests()
    {
        _connection = PdfManagerDatabase.Open(_path);
        PdfManagerDatabase.Initialize(_connection);
        Exec("""
            INSERT INTO tag_groups (name, normalized_name, rank) VALUES ('メーカー', 'メーカー', 1);
            INSERT INTO tags (name, normalized_name, group_id) VALUES ('とんこつ屋', 'とんこつ屋', 1), ('みそ屋', 'みそ屋', 1);
            """);
        foreach (var (id, title, created) in new[] { ("a", "とんこつ1", "2026-10-01T00:00:00Z"), ("b", "とんこつ2", "2026-10-20T00:00:00Z"), ("c", "みそ1", "2026-11-01T00:00:00Z") })
        {
            Exec($"""
                INSERT INTO documents (id, title, file_hash, original_file_name, file_size, created_at, updated_at, file_name)
                VALUES ('{id}', '{title}', 'h{id}', '{id}.pdf', 100, '{created}', '{created}', '{title}.pdf');
                """);
        }
        Exec("INSERT INTO document_tags (document_id, tag_id) VALUES ('a', 1), ('b', 1), ('c', 2);");
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    private static DateTimeOffset At(int month, int day, int hour = 12) => new(2026, month, day, hour, 0, 0, Jst);

    // 決定ログ FA-1〜FA-3: 評価ごとの件数(最新版だけ)
    [Fact]
    public void RatingCounts_CountLatestVersionsOnly()
    {
        Exec("UPDATE documents SET rating = 5 WHERE id IN ('a', 'b');");
        Exec("INSERT INTO version_series (id, name) VALUES ('s', '版'); UPDATE documents SET series_id = 's', version_no = 1 WHERE id = 'a'; UPDATE documents SET series_id = 's', version_no = 2 WHERE id = 'b';");

        var counts = UsageAnalytics.RatingCounts(_connection);

        Assert.Equal([1, 0, 0, 0, 0, 1], counts);
    }

    [Fact]
    public void ViewsAndSearches_AreCountedWithinPeriodAndPc()
    {
        UsageAnalytics.RecordView(_connection, "a", At(10, 5), "PC1");
        UsageAnalytics.RecordView(_connection, "a", At(10, 25), "PC1");
        UsageAnalytics.RecordView(_connection, "a", At(10, 26), "PC2");
        UsageAnalytics.RecordView(_connection, "b", At(10, 26), "PC1");
        UsageAnalytics.RecordSearch(_connection, "とんこつ", ["とんこつ屋"], 2, At(10, 25), "PC1");
        UsageAnalytics.RecordSearch(_connection, "トンコツ", [], 2, At(10, 26), "PC1");
        UsageAnalytics.RecordSearch(_connection, "しょうゆ", [], 0, At(10, 27), "PC2");

        var october = new AnalyticsPeriod(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 31), null);
        var summary = UsageAnalytics.Summary(_connection, october);
        Assert.Equal((3, 300L, 3, 1, 3), (summary.DocumentCount, summary.TotalBytes, summary.ViewCount, summary.RegisteredCount, summary.SearchCount));

        var top = UsageAnalytics.TopViewed(_connection, october);
        Assert.Equal([("a", 2), ("b", 1)], top.Select(t => (t.DocumentId, t.Count)));
        Assert.Equal([("a", 1), ("b", 1)], UsageAnalytics.TopViewed(_connection, october with { Pc = "PC1" }).Select(t => (t.DocumentId, t.Count)));

        // ひらがな・カタカナの違いはまとめる
        var keyword = Assert.Single(UsageAnalytics.TopKeywords(_connection, october with { Pc = "PC1" }));
        Assert.Equal(2, keyword.Count);
        Assert.Equal(["とんこつ屋"], UsageAnalytics.TopTags(_connection, october).Select(t => t.Text));
        Assert.Equal(["しょうゆ"], UsageAnalytics.ZeroResultSearches(_connection, october).Select(t => t.Text));

        var tags = UsageAnalytics.TagStats(_connection, 1, october);
        Assert.Equal([("とんこつ屋", 2, 3), ("みそ屋", 1, 0)], tags.Select(t => (t.Name, t.DocumentCount, t.ViewCount)));
        Assert.Equal(["PC1", "PC2"], UsageAnalytics.Pcs(_connection));
    }

    [Fact]
    public void ViewTrend_FillsEmptyBuckets()
    {
        UsageAnalytics.RecordView(_connection, "a", At(10, 5), "PC1");
        UsageAnalytics.RecordView(_connection, "b", At(10, 7), "PC1");
        UsageAnalytics.RecordView(_connection, "b", At(10, 7, 23), "PC1");

        var days = UsageAnalytics.ViewTrend(_connection, new AnalyticsPeriod(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 7), null), TrendUnit.Day, new DateOnly(2026, 11, 3));
        Assert.Equal([1, 0, 2], days.Select(d => d.Count));

        var months = UsageAnalytics.ViewTrend(_connection, new AnalyticsPeriod(null, null, null), TrendUnit.Month, new DateOnly(2026, 11, 3));
        Assert.Equal([("2026/10", 3), ("2026/11", 0)], months.Select(m => (m.Label, m.Count)));
    }

    [Fact]
    public void LongUnopened_ListsNeverOpenedFirst_AndClearRemovesRecords()
    {
        Exec("UPDATE documents SET last_viewed_at = '2026-10-30T00:00:00Z', view_count = 3 WHERE id = 'a';");
        UsageAnalytics.RecordView(_connection, "a", At(10, 30), "PC1");

        Assert.Equal(["b", "c", "a"], UsageAnalytics.LongUnopened(_connection).Select(d => d.DocumentId));

        UsageAnalytics.Clear(_connection);
        Assert.Equal(0, UsageAnalytics.Summary(_connection, new AnalyticsPeriod(null, null, null)).ViewCount);
    }

    private void Exec(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
