using System.Globalization;
using Microsoft.Data.Sqlite;
using PdfManager.Core.Search;

namespace PdfManager.Core.Analytics;

// 集計の期間(From・To はその日を含む、ローカルの日付。null は制限なし)と、記録した PC(null は全体、決定ログ DA-4・DA-8)
public sealed record AnalyticsPeriod(DateOnly? From, DateOnly? To, string? Pc);

public sealed record AnalyticsSummary(int DocumentCount, long TotalBytes, int ViewCount, int RegisteredCount, int SearchCount);

public sealed record RankedDocument(string DocumentId, string Title, string FileName, int Count, DateTimeOffset? LastViewedAt);

public sealed record TrendBucket(string Label, DateOnly Start, int Count);

public sealed record RankedText(string Text, int Count, DateTimeOffset LastAt);

public sealed record TagStat(long TagId, string Name, int DocumentCount, int ViewCount);

public enum TrendUnit
{
    Day,
    Week,
    Month,
}

// 決定ログ DA-1〜DA-7: 分析画面の記録と集計。記録はライブラリのデータベース(view_events・search_events)に置く
public static class UsageAnalytics
{
    public static string CurrentPc => Environment.MachineName;

    // ==== 記録 ====

    public static void RecordView(SqliteConnection connection, string documentId, DateTimeOffset now, string pc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO view_events (document_id, viewed_at, pc) VALUES ($id, $at, $pc);";
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$at", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$pc", pc);
        command.ExecuteNonQuery();
    }

    // 決定ログ DA-1・DA-2: 検索 1 回分(キーワードと、含むタグの名前)
    public static void RecordSearch(SqliteConnection connection, string keywords, IReadOnlyCollection<string> tagNames, int resultCount, DateTimeOffset now, string pc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO search_events (searched_at, pc, keywords, tag_names, result_count) VALUES ($at, $pc, $keywords, $tags, $count);";
        command.Parameters.AddWithValue("$at", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$pc", pc);
        command.Parameters.AddWithValue("$keywords", keywords.Trim());
        command.Parameters.AddWithValue("$tags", string.Join("、", tagNames));
        command.Parameters.AddWithValue("$count", resultCount);
        command.ExecuteNonQuery();
    }

    // 決定ログ DA-5: 記録を消す(文書そのもの・閲覧数は消さない)
    public static void Clear(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM view_events; DELETE FROM search_events;";
        command.ExecuteNonQuery();
    }

    public static IReadOnlyList<string> Pcs(SqliteConnection connection) =>
        Strings(connection, "SELECT pc FROM view_events UNION SELECT pc FROM search_events ORDER BY 1;");

    // ==== 集計 ====

    // ① 概要。文書の件数と容量は今の値(最新版だけでなく全件)、ほかは期間の中
    public static AnalyticsSummary Summary(SqliteConnection connection, AnalyticsPeriod period)
    {
        var (documents, bytes) = Pair(connection, "SELECT COUNT(*), COALESCE(SUM(file_size), 0) FROM documents;");
        var views = Count(connection, $"SELECT COUNT(*) FROM view_events WHERE {Range("viewed_at", period, pc: true)};", period);
        var registered = Count(connection, $"SELECT COUNT(*) FROM documents WHERE {Range("created_at", period, pc: false)};", period);
        var searches = Count(connection, $"SELECT COUNT(*) FROM search_events WHERE {Range("searched_at", period, pc: true)};", period);
        return new AnalyticsSummary((int)documents, bytes, views, registered, searches);
    }

    // ② よく開く PDF
    public static IReadOnlyList<RankedDocument> TopViewed(SqliteConnection connection, AnalyticsPeriod period, int limit = 20) => Rows(connection, $"""
        SELECT d.id, d.title, d.file_name, COUNT(*) AS n, d.last_viewed_at
        FROM view_events v JOIN documents d ON d.id = v.document_id
        WHERE {Range("v.viewed_at", period, pc: true, pcColumn: "v.pc")}
        GROUP BY d.id ORDER BY n DESC, d.title LIMIT {limit};
        """, period, r => new RankedDocument(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), ParseTime(r, 4)));

    // ③ 開いた回数の推移(日・週(月曜から)・月)。期間の始めが決まっていなければ、記録の最初から。空いている区切りも 0 件で出す
    public static IReadOnlyList<TrendBucket> ViewTrend(SqliteConnection connection, AnalyticsPeriod period, TrendUnit unit, DateOnly today)
    {
        var dates = Rows(connection, $"SELECT viewed_at FROM view_events WHERE {Range("viewed_at", period, pc: true)};", period,
                r => DateOnly.FromDateTime(DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture).ToLocalTime().DateTime))
            .ToList();
        var from = period.From ?? (dates.Count == 0 ? today : dates.Min());
        var to = period.To ?? today;
        if (to < from)
        {
            return [];
        }
        var counts = dates.GroupBy(d => BucketStart(d, unit)).ToDictionary(g => g.Key, g => g.Count());
        var buckets = new List<TrendBucket>();
        for (var start = BucketStart(from, unit); start <= to; start = Next(start, unit))
        {
            buckets.Add(new TrendBucket(Label(start, unit), start, counts.GetValueOrDefault(start)));
        }
        return buckets;
    }

    // ④ よく使う検索のキーワード(キーワード全体で数える。ひらがな・カタカナなどの違いはまとめる)
    public static IReadOnlyList<RankedText> TopKeywords(SqliteConnection connection, AnalyticsPeriod period, int limit = 20) =>
        RankTexts(SearchRows(connection, period).Where(s => s.Keywords.Length > 0).Select(s => (s.Keywords, s.At)), limit);

    // ④ よく使う検索のタグ(含むタグ 1 つずつ数える)
    public static IReadOnlyList<RankedText> TopTags(SqliteConnection connection, AnalyticsPeriod period, int limit = 20) =>
        RankTexts(SearchRows(connection, period)
            .SelectMany(s => s.Tags.Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => (t, s.At))), limit);

    // ⑤ 0 件だった検索(キーワードとタグの組み合わせごと)
    public static IReadOnlyList<RankedText> ZeroResultSearches(SqliteConnection connection, AnalyticsPeriod period, int limit = 50) =>
        RankTexts(SearchRows(connection, period)
            .Where(s => s.Count == 0)
            .Select(s => (string.Join(" + ", new[] { s.Keywords, s.Tags.Length > 0 ? $"タグ:{s.Tags}" : "" }.Where(x => x.Length > 0)), s.At)), limit);

    // ⑥ グループのタグごとの件数(今の値)と、期間の中で開いた回数
    public static IReadOnlyList<TagStat> TagStats(SqliteConnection connection, long? groupId, AnalyticsPeriod period) => Rows(connection, $"""
        SELECT t.id, t.name,
               (SELECT COUNT(*) FROM document_tags dt WHERE dt.tag_id = t.id),
               (SELECT COUNT(*) FROM view_events v JOIN document_tags dt ON dt.document_id = v.document_id
                WHERE dt.tag_id = t.id AND {Range("v.viewed_at", period, pc: true, pcColumn: "v.pc")})
        FROM tags t WHERE {(groupId is null ? "t.group_id IS NULL" : "t.group_id = $group")}
        ORDER BY 4 DESC, 3 DESC, t.normalized_name;
        """, period, r => new TagStat(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)), ("$group", (object?)groupId ?? DBNull.Value));

    // 決定ログ FA-1〜FA-3: 評価ごとの件数(添字 0 が未評価、1〜5 が ★1〜★5)。一覧に出る最新版だけ。期間に関係なく今の評価
    public static IReadOnlyList<int> RatingCounts(SqliteConnection connection)
    {
        var counts = new int[6];
        foreach (var (rating, count) in Rows(connection, """
                     SELECT d.rating, COUNT(*) FROM documents d
                     WHERE d.series_id IS NULL OR d.version_no = (SELECT MAX(version_no) FROM documents s WHERE s.series_id = d.series_id)
                     GROUP BY d.rating;
                     """, new AnalyticsPeriod(null, null, null), r => (r.GetInt32(0), r.GetInt32(1))))
        {
            if (rating is >= 0 and <= 5)
            {
                counts[rating] = count;
            }
        }
        return counts;
    }

    // ⑦ 長く開いていない PDF(一度も開いていないものを先に、その中は登録日の古い順。最新版だけ。期間に関係なく今の値)
    public static IReadOnlyList<RankedDocument> LongUnopened(SqliteConnection connection, int limit = 50) => Rows(connection, $"""
        SELECT d.id, d.title, d.file_name, d.view_count, d.last_viewed_at
        FROM documents d
        WHERE d.series_id IS NULL OR d.version_no = (SELECT MAX(version_no) FROM documents s WHERE s.series_id = d.series_id)
        ORDER BY d.last_viewed_at IS NOT NULL, d.last_viewed_at, d.created_at LIMIT {limit};
        """, new AnalyticsPeriod(null, null, null), r => new RankedDocument(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), ParseTime(r, 4)));

    // ==== 共通 ====

    private static IReadOnlyList<RankedText> RankTexts(IEnumerable<(string Text, DateTimeOffset At)> items, int limit) => items
        .GroupBy(i => DocumentSearchIndex.Normalize(i.Text))
        .Select(g => new RankedText(g.OrderByDescending(i => i.At).First().Text, g.Count(), g.Max(i => i.At)))
        .OrderByDescending(r => r.Count).ThenByDescending(r => r.LastAt)
        .Take(limit)
        .ToList();

    private static List<(string Keywords, string Tags, int Count, DateTimeOffset At)> SearchRows(SqliteConnection connection, AnalyticsPeriod period) =>
        Rows(connection, $"SELECT keywords, tag_names, result_count, searched_at FROM search_events WHERE {Range("searched_at", period, pc: true)};", period,
            r => (r.GetString(0), r.GetString(1), r.GetInt32(2), DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture)));

    // 期間(ローカルの日付)を UTC の文字列で比べる条件にする。値は $from・$to・$pc で渡す
    private static string Range(string column, AnalyticsPeriod period, bool pc, string pcColumn = "pc")
    {
        var conditions = new List<string> { "1 = 1" };
        if (period.From is not null)
        {
            conditions.Add($"{column} >= $from");
        }
        if (period.To is not null)
        {
            conditions.Add($"{column} < $to");
        }
        if (pc && period.Pc is not null)
        {
            conditions.Add($"{pcColumn} = $pc");
        }
        return string.Join(" AND ", conditions);
    }

    private static void AddPeriod(SqliteCommand command, AnalyticsPeriod period)
    {
        if (period.From is { } from)
        {
            command.Parameters.AddWithValue("$from", ToUtc(from));
        }
        if (period.To is { } to)
        {
            command.Parameters.AddWithValue("$to", ToUtc(to.AddDays(1)));
        }
        if (period.Pc is { } pc)
        {
            command.Parameters.AddWithValue("$pc", pc);
        }
    }

    private static string ToUtc(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(date.ToDateTime(TimeOnly.MinValue)))
            .UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateOnly BucketStart(DateOnly date, TrendUnit unit) => unit switch
    {
        TrendUnit.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        TrendUnit.Month => new DateOnly(date.Year, date.Month, 1),
        _ => date,
    };

    private static DateOnly Next(DateOnly start, TrendUnit unit) => unit switch
    {
        TrendUnit.Week => start.AddDays(7),
        TrendUnit.Month => start.AddMonths(1),
        _ => start.AddDays(1),
    };

    private static string Label(DateOnly start, TrendUnit unit) => unit switch
    {
        TrendUnit.Month => start.ToString("yyyy/MM", CultureInfo.InvariantCulture),
        TrendUnit.Week => start.ToString("MM/dd", CultureInfo.InvariantCulture) + "〜",
        _ => start.ToString("MM/dd", CultureInfo.InvariantCulture),
    };

    private static DateTimeOffset? ParseTime(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture);

    private static int Count(SqliteConnection connection, string sql, AnalyticsPeriod period) =>
        Rows(connection, sql, period, r => r.GetInt32(0)).Single();

    private static (long, long) Pair(SqliteConnection connection, string sql) =>
        Rows(connection, sql, new AnalyticsPeriod(null, null, null), r => (r.GetInt64(0), r.GetInt64(1))).Single();

    private static List<string> Strings(SqliteConnection connection, string sql) =>
        Rows(connection, sql, new AnalyticsPeriod(null, null, null), r => r.GetString(0));

    private static List<T> Rows<T>(SqliteConnection connection, string sql, AnalyticsPeriod period, Func<SqliteDataReader, T> map, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddPeriod(command, period);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }
        return rows;
    }
}
