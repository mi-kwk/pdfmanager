using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Library;
using PdfManager.Core.Storage;

namespace PdfManager.App.ViewModels;

// 退避の画面の 1 行
public sealed partial class ArchiveRow(ArchiveCandidate candidate, string storageName, Action changed) : ObservableObject
{
    public ArchiveCandidate Candidate { get; } = candidate;

    public string Title => Candidate.Title;

    public string LastUsedText => Candidate.LastViewedAt is { } viewed
        ? $"最後に開いた日 {viewed.ToLocalTime():yyyy/MM/dd}"
        : $"開いたことがない(登録 {Candidate.CreatedAt.ToLocalTime():yyyy/MM/dd})";

    public string SizeText => ArchiveViewModel.FormatSize(Candidate.Size);

    public string RatingText => Candidate.Rating > 0 ? new string('★', Candidate.Rating) : "";

    public string StorageName => storageName;

    public string RecentViewsText => $"最近 90 日に {Candidate.RecentViews} 回 ・ {storageName}";

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();
}

public sealed record ArchiveTagOption(long? Id, string Label);

public sealed record ArchiveTarget(string Id, string Label);

// SC-23 退避(決定ログ EA-1〜EA-8)。長く開いていない PDF を探してサブへまとめて移し、よく開いているのにサブにある PDF をメインへ戻す
public sealed partial class ArchiveViewModel : ObservableObject
{
    private readonly DataFolder _dataFolder;
    private readonly StorageAccess _storage;
    private bool _loading;

    public ArchiveViewModel(DataFolder dataFolder, StorageAccess storage)
    {
        _dataFolder = dataFolder;
        _storage = storage;
        using (var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath))
        {
            var catalog = new CatalogRepository(connection);
            var firstRank = catalog.GetGroups().Where(g => g.Rank == 1).Select(g => g.Id).ToHashSet();
            TagChoices =
            [
                new ArchiveTagOption(null, "すべて"),
                .. catalog.GetTags().Where(t => t.GroupId is { } g && firstRank.Contains(g)).Select(t => new ArchiveTagOption(t.Id, $"{t.Name}({t.GroupLabel})")),
            ];
        }
        _loading = true;
        SelectedTag = TagChoices[0];
        _loading = false;
        Load();
    }

    // ==== 条件(決定ログ EA-2) ====

    [ObservableProperty]
    public partial string Months { get; set; } = ArchivePlanner.DefaultMonths.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public IReadOnlyList<ArchiveTagOption> TagChoices { get; }

    [ObservableProperty]
    public partial ArchiveTagOption? SelectedTag { get; set; }

    [ObservableProperty]
    public partial DateTime? RegisteredFrom { get; set; }

    [ObservableProperty]
    public partial DateTime? RegisteredTo { get; set; }

    partial void OnMonthsChanged(string value) => Load();

    partial void OnSelectedTagChanged(ArchiveTagOption? value) => Load();

    partial void OnRegisteredFromChanged(DateTime? value) => Load();

    partial void OnRegisteredToChanged(DateTime? value) => Load();

    // ==== 移す先(決定ログ EA-4) ====

    [ObservableProperty]
    public partial IReadOnlyList<ArchiveTarget> Targets { get; set; } = [];

    [ObservableProperty]
    public partial ArchiveTarget? SelectedTarget { get; set; }

    [ObservableProperty]
    public partial string TargetNote { get; set; } = "";

    // ==== 結果 ====

    public ObservableCollection<ArchiveRow> Candidates { get; } = [];

    public ObservableCollection<ArchiveRow> Frequent { get; } = [];

    [ObservableProperty]
    public partial string CandidateSummary { get; set; } = "";

    [ObservableProperty]
    public partial string FrequentSummary { get; set; } = "";

    [ObservableProperty]
    public partial string SpaceText { get; set; } = "";

    public IReadOnlyList<string> CheckedCandidateIds => Candidates.Where(r => r.IsChecked).Select(r => r.Candidate.Id).ToList();

    public IReadOnlyList<string> CheckedFrequentIds => Frequent.Where(r => r.IsChecked).Select(r => r.Candidate.Id).ToList();

    public void Load()
    {
        if (_loading)
        {
            return;
        }
        var months = int.TryParse(Months, out var m) ? Math.Clamp(m, 0, 600) : ArchivePlanner.DefaultMonths;
        var now = DateTimeOffset.Now;
        IReadOnlyList<ArchiveCandidate> candidates;
        IReadOnlyList<ArchiveCandidate> frequent;
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            candidates = ArchivePlanner.Candidates(connection, months, now);
            frequent = ArchivePlanner.FrequentInSubs(connection, now.AddDays(-90));
        }
        var from = RegisteredFrom;
        var to = RegisteredTo?.AddDays(1);
        Candidates.Clear();
        foreach (var candidate in candidates.Where(c => (SelectedTag?.Id is not { } tag || c.TagIds.Contains(tag))
                     && (from is null || c.CreatedAt.LocalDateTime >= from) && (to is null || c.CreatedAt.LocalDateTime < to)))
        {
            // 決定ログ EA-3: 評価が ★4 以上のものは、初めはチェックを外す
            Candidates.Add(new ArchiveRow(candidate, "メイン", UpdateSummary) { IsChecked = candidate.Rating < ArchivePlanner.KeepRating });
        }
        Frequent.Clear();
        foreach (var candidate in frequent)
        {
            Frequent.Add(new ArchiveRow(candidate, _storage.Storage.StorageName(candidate.StorageId), UpdateSummary) { IsChecked = true });
        }

        var target = SelectedTarget?.Id;
        Targets = _storage.Storage.Subs.Where(s => s.IsWritable).Select(s => new ArchiveTarget(s.Id, $"{s.Name}({s.Folder})")).ToList();
        SelectedTarget = Targets.FirstOrDefault(t => t.Id == target) ?? Targets.FirstOrDefault();
        TargetNote = Targets.Count > 0 ? ""
            : _storage.Storage.Subs.Count == 0
                ? "移す先のサブライブラリがありません。設定画面の「サブライブラリ」の「新しいサブを作る」で、外付け HDD に作ってください。"
                : "サブライブラリがつながっていません。外付け HDD をつないで、設定画面の「接続を確認」を押してください。";

        SpaceText = ArchivePlanner.DriveSpace(_dataFolder.Root) is var (free, total) && total > 0
            ? $"メインのドライブの空き:{FormatSize(free)} / {FormatSize(total)}({free * 100.0 / total:0}%)"
            : "";
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var picked = Candidates.Where(r => r.IsChecked).ToList();
        CandidateSummary = $"{Candidates.Count:N0} 件のうち {picked.Count:N0} 件(合計 {FormatSize(picked.Sum(r => r.Candidate.Size))})を選択中";
        var back = Frequent.Where(r => r.IsChecked).ToList();
        FrequentSummary = Frequent.Count == 0
            ? "サブにある PDF で、最近 90 日に開いたものはありません。"
            : $"{Frequent.Count:N0} 件のうち {back.Count:N0} 件(合計 {FormatSize(back.Sum(r => r.Candidate.Size))})を選択中";
    }

    [RelayCommand]
    private void CheckAllCandidates() => SetAll(Candidates, true);

    [RelayCommand]
    private void UncheckAllCandidates() => SetAll(Candidates, false);

    [RelayCommand]
    private void CheckAllFrequent() => SetAll(Frequent, true);

    [RelayCommand]
    private void UncheckAllFrequent() => SetAll(Frequent, false);

    private void SetAll(IEnumerable<ArchiveRow> rows, bool value)
    {
        foreach (var row in rows)
        {
            row.IsChecked = value;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(0, bytes / 1024.0):0} KB",
    };
}
