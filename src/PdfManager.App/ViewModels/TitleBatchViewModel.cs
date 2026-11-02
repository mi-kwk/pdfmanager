using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Search;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.App.ViewModels;

// タイトルの付け直しの画面の 1 件(登録済みの最新版)
public sealed partial class TitleRow(string id, string title, string fileName, string? storageId, bool canEdit, DataFolder dataFolder) : ObservableObject
{
    private System.Windows.Media.ImageSource? _thumbnail;
    private bool _thumbnailLoaded;

    public string Id { get; } = id;

    public string Title { get; } = title;

    public string FileName { get; } = fileName;

    public string? StorageId { get; } = storageId;

    // 決定ログ S-12: つながっていないサブの文書は編集できない
    public bool CanEdit { get; } = canEdit;

    public string Note => CanEdit ? FileName : "保存先がつながっていないため選べません";

    // 一覧に出たときに読み込む(件数が多くても開くのが遅くならないように)
    public System.Windows.Media.ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnailLoaded = true;
                _thumbnail = DuplicateLookup.LoadImage(dataFolder.GetThumbnailPath(Id));
            }
            return _thumbnail;
        }
    }

    public event Action<TitleRow>? CheckedChanged;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => CheckedChanged?.Invoke(this);
}

// シリーズ(末尾の番号の前が同じタイトル)のまとまり(決定ログ DJ-1)
public sealed partial class TitleSeries : ObservableObject
{
    private bool _updating;

    public TitleSeries(string header, IReadOnlyList<TitleRow> rows, bool isExpanded)
    {
        Header = header;
        Rows = rows;
        IsExpanded = isExpanded;
        foreach (var row in rows)
        {
            row.CheckedChanged += _ =>
            {
                if (!_updating)
                {
                    OnPropertyChanged(nameof(IsChecked));
                }
            };
        }
    }

    public string Header { get; }

    public IReadOnlyList<TitleRow> Rows { get; }

    public void RefreshChecked() => OnPropertyChanged(nameof(IsChecked));

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    // 全部選んでいれば true、一部なら null。押すと選べるものを全部選ぶ・外す
    public bool? IsChecked
    {
        get
        {
            var editable = Rows.Where(r => r.CanEdit).ToList();
            var count = editable.Count(r => r.IsChecked);
            return count == 0 ? false : count == editable.Count ? true : null;
        }
        set
        {
            _updating = true;
            foreach (var row in Rows.Where(r => r.CanEdit))
            {
                row.IsChecked = value == true;
            }
            _updating = false;
            OnPropertyChanged();
        }
    }
}

// SC-18 タイトルの付け直し(選ぶ画面、決定ログ DI-1・DI-2・DJ-1〜DJ-3)。第 1 グループのタグ(必須)の登録済み PDF から、付け直すものを選ぶ
public sealed partial class TitleBatchViewModel : ObservableObject
{
    private const string OthersHeader = "番号のないタイトル・1 件だけのもの";

    private readonly DataFolder _dataFolder;
    private readonly StorageAccess _storage;
    private List<TitleRow> _rows = [];
    private readonly HashSet<string> _initialChecked;
    private readonly HashSet<long> _firstRankTagIds = [];
    private const long AllDocuments = -1;
    private const long WithoutFirstRankTag = -2;
    private const long InitialDocuments = -3;

    // チェックが変わったとき(タグの一括付与で、対象を入れ替えるため)
    public event Action? CheckedChanged;

    // includeAll は「すべての PDF」「第 1 グループのタグがない PDF」も選べるようにする(タグの一括付与、決定ログ DM-2)。
    // initialChecked は初めからチェックしておく PDF(一覧の右クリックから開いたとき)
    public TitleBatchViewModel(DataFolder dataFolder, StorageAccess storage, bool includeAll = false, IReadOnlyCollection<string>? initialChecked = null)
    {
        _dataFolder = dataFolder;
        _storage = storage;
        _initialChecked = initialChecked?.ToHashSet() ?? [];
        using var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath);
        var catalog = new CatalogRepository(connection);
        var groups = catalog.GetGroups().Where(g => g.Rank == 1).ToDictionary(g => g.Id);
        TagChoices = catalog.GetTags()
            .Where(t => t.GroupId is { } g && groups.ContainsKey(g))
            .OrderBy(t => groups[t.GroupId!.Value].Name, StringComparer.Ordinal).ThenBy(t => t.Name, NaturalOrder.Instance)
            .Select(t => new TagOption(t.Id, $"{t.Name}({t.GroupLabel}・{t.UsageCount:N0} 件)"))
            .ToList();
        _firstRankTagIds = TagChoices.Select(t => t.Id).ToHashSet();
        if (includeAll)
        {
            TagChoices =
            [
                .. _initialChecked.Count > 0 ? [new TagOption(InitialDocuments, $"一覧で選んでいた PDF({_initialChecked.Count} 件)")] : Array.Empty<TagOption>(),
                new TagOption(AllDocuments, "すべての PDF"),
                new TagOption(WithoutFirstRankTag, "第 1 グループのタグがない PDF"),
                .. TagChoices,
            ];
            if (_initialChecked.Count > 0)
            {
                SelectedTag = TagChoices[0];
            }
        }
    }

    public sealed record TagOption(long Id, string Label);

    public IReadOnlyList<TagOption> TagChoices { get; }

    [ObservableProperty]
    public partial TagOption? SelectedTag { get; set; }

    [ObservableProperty]
    public partial string Keyword { get; set; } = "";

    // 決定ログ DJ-2: 「〇〇＋番号だけ」
    [ObservableProperty]
    public partial bool NumberOnly { get; set; }

    // 決定ログ DJ-1: シリーズでまとめる(false なら一覧)
    [ObservableProperty]
    public partial bool GroupBySeries { get; set; } = true;

    public ObservableCollection<TitleSeries> Groups { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } = "第 1 グループのタグを選んでください。";

    public int CheckedCount => _rows.Count(r => r.IsChecked);

    public string CheckedText => $"{CheckedCount} 件を選択中";

    // 表示している順(Shift で範囲を選ぶときに使う)
    public IReadOnlyList<TitleRow> VisibleRows => Groups.SelectMany(g => g.Rows).ToList();

    public IReadOnlyList<TitleRow> CheckedRows => VisibleRows.Where(r => r.IsChecked).ToList();

    partial void OnSelectedTagChanged(TagOption? value) => Load();

    partial void OnKeywordChanged(string value) => Rebuild();

    partial void OnNumberOnlyChanged(bool value) => Rebuild();

    partial void OnGroupBySeriesChanged(bool value) => Rebuild();

    // タグの PDF(最新版)を読み直す。保存のあとにも呼ぶ。選んでいたものは選んだまま
    public void Load()
    {
        var checkedIds = _rows.Where(r => r.IsChecked).Select(r => r.Id).ToHashSet();
        _rows = [];
        if (SelectedTag is { } tag)
        {
            using var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath);
            var index = DocumentSearchIndex.Load(connection);
            var entries = tag.Id switch
            {
                AllDocuments => index.Search("", []),
                WithoutFirstRankTag => index.Search("", []).Where(e => !e.TagIds.Any(_firstRankTagIds.Contains)).ToList(),
                InitialDocuments => index.Search("", []).Where(e => _initialChecked.Contains(e.Id)).ToList(),
                _ => index.Search("", [tag.Id]),
            };
            _rows = entries
                .Where(e => e.IsLatest)
                .Select(e => new TitleRow(e.Id, e.Title, e.FileName, e.StorageId, _storage.Storage.IsAvailable(e.StorageId), _dataFolder))
                .ToList();
            foreach (var row in _rows)
            {
                row.IsChecked = row.CanEdit && (checkedIds.Contains(row.Id) || _initialChecked.Contains(row.Id));
                row.CheckedChanged += _ => NotifyChecked();
            }
        }
        Rebuild();
    }

    private void Rebuild()
    {
        Groups.Clear();
        var keyword = NameNormalizer.Fold(Keyword.Trim());
        var seriesKeyword = keyword.TrimEnd(' ', '_', '-');
        var rows = _rows.Where(r => keyword.Length == 0
            || (NumberOnly ? TitleNumbers.SeriesKey(r.Title) == seriesKeyword : NameNormalizer.Fold(r.Title).Contains(keyword, StringComparison.Ordinal)))
            .ToList();
        if (GroupBySeries)
        {
            // 決定ログ DJ-1: 末尾の番号の前が同じタイトルが 2 件以上あればシリーズ。番号のないものを先に、番号の順
            var series = rows.GroupBy(r => TitleNumbers.SeriesKey(r.Title)).ToList();
            foreach (var group in series.Where(g => g.Count() > 1).OrderBy(g => g.Key, NaturalOrder.Instance))
            {
                var members = group.OrderBy(r => NumberOf(r.Title)).ThenBy(r => r.Title, NaturalOrder.Instance).ToList();
                var name = TitleNumbers.SplitTrailingNumber(members[0].Title)?.Stem ?? members[0].Title;
                Groups.Add(new TitleSeries($"{name.TrimEnd()} ＋番号({members.Count} 件)", members, isExpanded: true));
            }
            var others = series.Where(g => g.Count() == 1).SelectMany(g => g).OrderBy(r => r.Title, NaturalOrder.Instance).ToList();
            if (others.Count > 0)
            {
                Groups.Add(new TitleSeries($"{OthersHeader}({others.Count} 件)", others, isExpanded: false));
            }
        }
        else if (rows.Count > 0)
        {
            Groups.Add(new TitleSeries($"すべて({rows.Count} 件)", rows.OrderBy(r => r.Title, NaturalOrder.Instance).ToList(), isExpanded: true));
        }
        Status = SelectedTag is null ? "第 1 グループのタグを選んでください。"
            : _rows.Count == 0 ? "このタグの PDF はありません。"
            : rows.Count == 0 ? "条件に合う PDF はありません。"
            : $"{rows.Count} 件(シリーズのチェックでまとめて選べます。Shift を押しながら押すと範囲で選べます)";
        NotifyChecked();
    }

    private static long NumberOf(string title) => TitleNumbers.SplitTrailingNumber(title) is var (_, digits) ? TitleNumbers.Value(digits) : -1;

    // Shift を押しながら押したとき:前に押したものから今押したものまでを、同じ状態にする(決定ログ DJ-3)
    public void SetRange(TitleRow from, TitleRow to, bool value)
    {
        var visible = VisibleRows;
        var (a, b) = (visible.ToList().IndexOf(from), visible.ToList().IndexOf(to));
        if (a < 0 || b < 0)
        {
            return;
        }
        foreach (var row in visible.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1).Where(r => r.CanEdit))
        {
            row.IsChecked = value;
        }
    }

    [RelayCommand]
    private void CheckAll()
    {
        foreach (var row in VisibleRows.Where(r => r.CanEdit))
        {
            row.IsChecked = true;
        }
    }

    [RelayCommand]
    private void UncheckAll()
    {
        foreach (var row in _rows)
        {
            row.IsChecked = false;
        }
    }

    private void NotifyChecked()
    {
        CheckedChanged?.Invoke();
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(CheckedText));
        foreach (var group in Groups)
        {
            group.RefreshChecked();
        }
    }
}
