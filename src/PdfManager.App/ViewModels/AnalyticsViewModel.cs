using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.Core.Analytics;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Storage;

namespace PdfManager.App.ViewModels;

// 棒グラフの 1 本(決定ログ DA-10: アプリの中で描く簡単な棒グラフ)
public sealed record TrendBar(string Label, int Count, double Height, string Tooltip);

public sealed record TagStatRow(long TagId, string Name, int DocumentCount, int ViewCount, double DocumentWidth, double ViewWidth);

public sealed record GroupOption(long? Id, string Label);

// 決定ログ FA-1: 評価ごとの件数の 1 行。Rating は 0 が未評価
public sealed record RatingRow(int Rating, string Label, int Count, string PercentText, double Width);

// SC-22 分析(決定ログ DA-1〜DA-11)。期間と PC を選び、記録から集計して並べる
public sealed partial class AnalyticsViewModel : ObservableObject
{
    private const double BarMaxHeight = 140;
    private const double TagBarMaxWidth = 160;

    private readonly DataFolder _dataFolder;
    private bool _loading;

    public AnalyticsViewModel(DataFolder dataFolder)
    {
        _dataFolder = dataFolder;
        using (var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath))
        {
            var groups = new CatalogRepository(connection).GetGroups().OrderBy(g => g.DisplayRank).ThenBy(g => g.Name, StringComparer.Ordinal).ToList();
            GroupOptions = [.. groups.Select(g => new GroupOption(g.Id, $"{g.Name}({g.RankLabel})")), new GroupOption(null, "自由タグ")];
        }
        _loading = true;
        SelectedGroup = GroupOptions.FirstOrDefault();
        _loading = false;
        SelectPeriod("30");
    }

    public string ThisPc => $"この PC({UsageAnalytics.CurrentPc})だけ";

    // ==== 条件(決定ログ DA-4・DA-8) ====

    [ObservableProperty]
    public partial DateTime? From { get; set; }

    [ObservableProperty]
    public partial DateTime? To { get; set; }

    [ObservableProperty]
    public partial bool OnlyThisPc { get; set; }

    [ObservableProperty]
    public partial TrendUnit Unit { get; set; } = TrendUnit.Day;

    public IReadOnlyList<GroupOption> GroupOptions { get; }

    [ObservableProperty]
    public partial GroupOption? SelectedGroup { get; set; }

    partial void OnFromChanged(DateTime? value) => Reload();

    partial void OnToChanged(DateTime? value) => Reload();

    partial void OnOnlyThisPcChanged(bool value) => Reload();

    partial void OnUnitChanged(TrendUnit value) => Reload();

    partial void OnSelectedGroupChanged(GroupOption? value) => Reload();

    // 決定ログ DA-8: 30 日 / 90 日 / 1 年 / すべて
    [RelayCommand]
    private void SelectPeriod(string days)
    {
        _loading = true;
        To = days == "all" ? null : DateTime.Today;
        From = days switch
        {
            "all" => null,
            "365" => DateTime.Today.AddYears(-1).AddDays(1),
            _ => DateTime.Today.AddDays(1 - int.Parse(days, System.Globalization.CultureInfo.InvariantCulture)),
        };
        // 日ごとは期間が長いと本数が多すぎるので、合わせて変える
        Unit = days switch { "30" => TrendUnit.Day, "90" => TrendUnit.Week, _ => TrendUnit.Month };
        _loading = false;
        Reload();
    }

    // ==== 結果 ====

    [ObservableProperty]
    public partial string PeriodText { get; set; } = "";

    [ObservableProperty]
    public partial AnalyticsSummary? Summary { get; set; }

    [ObservableProperty]
    public partial string SizeText { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<TrendBar> Trend { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RankedDocument> TopViewed { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RankedText> TopKeywords { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RankedText> TopTags { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RankedText> ZeroResults { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TagStatRow> TagStats { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RankedDocument> LongUnopened { get; set; } = [];

    [ObservableProperty]
    public partial string Note { get; set; } = "";

    // 決定ログ FA-1〜FA-3: 評価ごとの件数(★5 から順に、最後に未評価)
    [ObservableProperty]
    public partial IReadOnlyList<RatingRow> Ratings { get; set; } = [];

    private void LoadRatings(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var counts = UsageAnalytics.RatingCounts(connection);
        var total = Math.Max(1, counts.Sum());
        var max = Math.Max(1, counts.Max());
        Ratings = new[] { 5, 4, 3, 2, 1, 0 }
            .Select(r => new RatingRow(r, r == 0 ? "未評価" : new string('★', r), counts[r],
                $"{counts[r] * 100.0 / total:0.#}%", counts[r] * TagBarMaxWidth / max))
            .ToList();
    }

    public void Reload()
    {
        if (_loading)
        {
            return;
        }
        var from = From is { } f ? DateOnly.FromDateTime(f) : (DateOnly?)null;
        var to = To is { } t ? DateOnly.FromDateTime(t) : (DateOnly?)null;
        var period = new AnalyticsPeriod(from, to, OnlyThisPc ? UsageAnalytics.CurrentPc : null);
        using var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath);
        Summary = UsageAnalytics.Summary(connection, period);
        SizeText = Summary.TotalBytes >= 1L << 30 ? $"{Summary.TotalBytes / (double)(1L << 30):0.0} GB" : $"{Summary.TotalBytes / (double)(1L << 20):0.0} MB";
        PeriodText = (from, to) switch
        {
            (null, null) => "すべての期間",
            _ => $"{from?.ToString("yyyy/MM/dd") ?? "はじめ"} 〜 {to?.ToString("yyyy/MM/dd") ?? "今日"}",
        } + (OnlyThisPc ? $"・{UsageAnalytics.CurrentPc} だけ" : "・全体");

        var buckets = UsageAnalytics.ViewTrend(connection, period, Unit, DateOnly.FromDateTime(DateTime.Today));
        var max = Math.Max(1, buckets.Count == 0 ? 1 : buckets.Max(b => b.Count));
        Trend = buckets.Select(b => new TrendBar(b.Label, b.Count, Math.Max(2, b.Count * BarMaxHeight / max), $"{b.Label}:{b.Count} 回")).ToList();

        TopViewed = UsageAnalytics.TopViewed(connection, period);
        TopKeywords = UsageAnalytics.TopKeywords(connection, period);
        TopTags = UsageAnalytics.TopTags(connection, period);
        ZeroResults = UsageAnalytics.ZeroResultSearches(connection, period);
        IReadOnlyList<TagStat> stats = SelectedGroup is null ? [] : UsageAnalytics.TagStats(connection, SelectedGroup.Id, period);
        var maxDocuments = Math.Max(1, stats.Count == 0 ? 1 : stats.Max(s => s.DocumentCount));
        var maxViews = Math.Max(1, stats.Count == 0 ? 1 : stats.Max(s => s.ViewCount));
        TagStats = stats.Select(s => new TagStatRow(s.TagId, s.Name, s.DocumentCount, s.ViewCount,
            s.DocumentCount * TagBarMaxWidth / maxDocuments, s.ViewCount * TagBarMaxWidth / maxViews)).ToList();
        LongUnopened = UsageAnalytics.LongUnopened(connection);
        LoadRatings(connection);
        Note = Summary.ViewCount == 0 && Summary.SearchCount == 0
            ? "この期間には、開いた・検索した記録がありません(記録は分析画面を入れたときから残り始めます。設定で「検索・閲覧の記録を残す」がオフのときは残りません)。"
            : "";
    }
}
