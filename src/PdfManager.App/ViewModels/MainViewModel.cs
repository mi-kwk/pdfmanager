using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Search;
using PdfManager.Core.Settings;
using PdfManager.Core.Storage;

namespace PdfManager.App.ViewModels;

public sealed record DocumentCard(DocumentEntry Entry, string ThumbnailPath)
{
    public string Title => Entry.Title;

    public string FileName => Entry.FileName;

    // リスト表示の列(決定ログ L-9)
    public string RatingText => Entry.Rating == 0 ? "" : new string('★', Entry.Rating) + new string('☆', 5 - Entry.Rating);

    public string TagText => string.Join("、", Entry.TagNames);

    public string RegisteredText => Entry.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd");

    public string ViewCountText => Entry.ViewCount.ToString("N0");

    public string LastViewedText => Entry.LastViewedAt?.ToLocalTime().ToString("yyyy/MM/dd") ?? "";
}

// 決定ログ J-7: 編集できない情報
public sealed record DetailInfo(string OriginalFileName, string PageCount, string FileSize, string Registered, string ViewCount, string LastViewed);

public partial class MainViewModel : ObservableObject
{
    private const int MaxReportedProblems = 5;

    private static readonly IReadOnlyList<RatingFilterOption> RatingChoices =
    [
        new("指定しない", null, false),
        new("★5", 5, false),
        new("★4 以上", 4, false),
        new("★3 以上", 3, false),
        new("★2 以上", 2, false),
        new("★1 以上", 1, false),
        new("未評価のみ", null, true),
    ];

    private readonly IUserDialogs _dialogs;
    private readonly Action<ViewPreferences> _saveView;
    private DocumentSearchIndex? _index;
    private List<DetailItem> _detailItems = [];
    private bool _suspendSearch;

    public MainViewModel(DataFolder dataFolder, IUserDialogs dialogs, ViewPreferences view, Action<ViewPreferences> saveView)
    {
        DataFolder = dataFolder;
        _dialogs = dialogs;
        _saveView = saveView;
        Editor = new DocumentEditor(dialogs, showRatingAndMemo: true);
        Editor.Edited += () =>
        {
            HasChanges = true;
            SaveMessage = null;
        };
        _suspendSearch = true;
        SelectedRatingFilter = RatingChoices[0];
        SortDescending = view.Descending;
        IsListView = view.ListView;
        _suspendSearch = false;
        Refresh(view);
    }

    public DataFolder DataFolder { get; }

    public DocumentEditor Editor { get; }

    // 絞り込みで一覧から消えても、編集中の対象は保つ
    public IReadOnlyList<string> SelectedIds { get; private set; } = [];

    // 一覧を作り直す直前(この間に起きる選択の変化は、利用者の操作ではないので無視してもらう)
    public event Action? ResultsReplacing;

    // 一覧を作り直したあとに、選択を戻すよう画面に頼む
    public event Action<IReadOnlyCollection<string>>? SelectionRestoreRequested;

    // ==== 絞り込み(決定ログ I-1〜I-6、L-1〜L-4) ====

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentSummaryText), nameof(HasDocuments))]
    public partial long DocumentCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFields))]
    public partial IReadOnlyList<FieldFilter> FieldFilters { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags))]
    public partial IReadOnlyList<TagFilterItem> TagFilters { get; set; } = [];

    [ObservableProperty]
    public partial bool IsTagAny { get; set; }

    public IReadOnlyList<RatingFilterOption> RatingFilters => RatingChoices;

    [ObservableProperty]
    public partial RatingFilterOption SelectedRatingFilter { get; set; }

    [ObservableProperty]
    public partial string RegisteredFrom { get; set; } = "";

    [ObservableProperty]
    public partial string RegisteredTo { get; set; } = "";

    [ObservableProperty]
    public partial string? RegisteredError { get; set; }

    [ObservableProperty]
    public partial string LastViewedFrom { get; set; } = "";

    [ObservableProperty]
    public partial string LastViewedTo { get; set; } = "";

    [ObservableProperty]
    public partial string? LastViewedError { get; set; }

    [ObservableProperty]
    public partial bool NeverViewedOnly { get; set; }

    // ==== 一覧(決定ログ L-5、L-9) ====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText), nameof(HasNoResults))]
    public partial IReadOnlyList<DocumentCard> Results { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<SortOption> SortOptions { get; set; } = [];

    [ObservableProperty]
    public partial SortOption? SelectedSort { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionText))]
    public partial bool SortDescending { get; set; }

    public string SortDirectionText => SortDescending ? "降順" : "昇順";

    [ObservableProperty]
    public partial bool IsListView { get; set; }

    public bool HasFields => FieldFilters.Count > 0;

    public bool HasTags => TagFilters.Count > 0;

    public bool HasDocuments => DocumentCount > 0;

    public bool IsFiltering => SearchText.Trim().Length > 0
        || TagFilters.Any(t => t.State != TagFilterState.None)
        || FieldFilters.Any(f => f.HasCondition)
        || SelectedRatingFilter != RatingChoices[0]
        || RegisteredFrom.Trim().Length > 0 || RegisteredTo.Trim().Length > 0
        || LastViewedFrom.Trim().Length > 0 || LastViewedTo.Trim().Length > 0 || NeverViewedOnly;

    public bool HasNoResults => HasDocuments && Results.Count == 0;

    public string ResultText => IsFiltering ? $"{Results.Count:N0} 件(全 {DocumentCount:N0} 件)" : $"{Results.Count:N0} 件";

    public string DocumentSummaryText => DocumentCount == 0
        ? "PDF はまだ登録されていません"
        : $"{DocumentCount:N0} 件の PDF が登録されています";

    // ==== 詳細 ====

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(CancelCommand))]
    public partial bool HasChanges { get; set; }

    [ObservableProperty]
    public partial string? SaveMessage { get; set; }

    public bool HasSelection => _detailItems.Count > 0;

    public string SelectionText => _detailItems.Count switch
    {
        0 => "",
        1 => "選択中の PDF",
        _ => $"{_detailItems.Count} 件を選択中(入力した値は選択中の全件に入ります)",
    };

    public string? SelectedThumbnailPath => _detailItems is [var only] ? DataFolder.GetThumbnailPath(only.Detail.Id) : null;

    public DetailInfo? SelectedInfo => _detailItems is [var only] ? new DetailInfo(
        only.Detail.OriginalFileName,
        $"{only.Detail.PageCount:N0} ページ",
        FormatSize(only.Detail.FileSize),
        only.Detail.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm"),
        $"{only.Detail.ViewCount:N0} 回",
        only.Detail.LastViewedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "(まだ開いていません)") : null;

    // 起動時と、取り込み画面・項目タグ管理画面を閉じたとき、保存したときに呼ぶ
    public void Refresh() => Refresh(null);

    private void Refresh(ViewPreferences? initialView)
    {
        _suspendSearch = true;
        try
        {
            using var connection = PdfManagerDatabase.Open(DataFolder.DatabasePath);
            var catalog = new CatalogRepository(connection);
            var fields = catalog.GetFields();
            var tags = catalog.GetTags();
            var states = TagFilters.ToDictionary(t => t.Tag.Id, t => t.State);
            TagFilters = tags.Select(t => new TagFilterItem(t, states.GetValueOrDefault(t.Id), ApplySearch)).ToList();
            var previous = FieldFilters.ToDictionary(f => f.Field.Id);
            FieldFilters = fields.Select(f => new FieldFilter(f, previous.GetValueOrDefault(f.Id), ApplySearch)).ToList();

            var currentSort = SelectedSort is { } sort ? (sort.Key, sort.FieldId)
                : initialView is not null && Enum.TryParse<SortKey>(initialView.Sort, out var key) ? (key, initialView.SortFieldId)
                : (SortKey.Registered, null);
            SortOptions =
            [
                new(SortKey.Registered, null, "登録日"),
                new(SortKey.Title, null, "タイトル"),
                new(SortKey.Rating, null, "評価"),
                new(SortKey.ViewCount, null, "閲覧数"),
                new(SortKey.LastViewed, null, "最終閲覧日"),
                .. fields.Select(f => new SortOption(SortKey.Field, f.Id, $"項目:{f.Name}")),
            ];
            // 項目での並び替えは、項目が見つからなければ登録日に戻す
            SelectedSort = SortOptions.FirstOrDefault(o => (o.Key, o.FieldId) == currentSort) ?? SortOptions[0];

            Editor.SetCatalog(fields, tags, new DocumentRepository(connection).GetFileNames());
            _index = DocumentSearchIndex.Load(connection);
        }
        finally
        {
            _suspendSearch = false;
        }
        DocumentCount = _index.Count;
        ApplySearch();
        LoadDetails();
    }

    // 決定ログ I-5: 入力するたびにすぐ結果を更新する
    partial void OnSearchTextChanged(string value) => ApplySearch();

    partial void OnIsTagAnyChanged(bool value) => ApplySearch();

    partial void OnSelectedRatingFilterChanged(RatingFilterOption value) => ApplySearch();

    partial void OnRegisteredFromChanged(string value) => ApplySearch();

    partial void OnRegisteredToChanged(string value) => ApplySearch();

    partial void OnLastViewedFromChanged(string value) => ApplySearch();

    partial void OnLastViewedToChanged(string value) => ApplySearch();

    partial void OnNeverViewedOnlyChanged(bool value) => ApplySearch();

    partial void OnSelectedSortChanged(SortOption? value) => OnViewChanged();

    partial void OnSortDescendingChanged(bool value) => OnViewChanged();

    partial void OnIsListViewChanged(bool value) => SaveView();

    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    [RelayCommand]
    private void ShowList(string mode) => IsListView = mode == "list";

    private void OnViewChanged()
    {
        SaveView();
        ApplySearch();
    }

    private void SaveView()
    {
        if (!_suspendSearch && SelectedSort is { } sort)
        {
            _saveView(new ViewPreferences(sort.StoredKey, sort.FieldId, SortDescending, IsListView));
        }
    }

    [RelayCommand]
    private void ClearConditions()
    {
        _suspendSearch = true;
        try
        {
            foreach (var tag in TagFilters.Where(t => t.State != TagFilterState.None))
            {
                tag.State = TagFilterState.None;
            }
            foreach (var field in FieldFilters)
            {
                field.Clear();
            }
            SearchText = "";
            IsTagAny = false;
            SelectedRatingFilter = RatingChoices[0];
            RegisteredFrom = RegisteredTo = LastViewedFrom = LastViewedTo = "";
            NeverViewedOnly = false;
        }
        finally
        {
            _suspendSearch = false;
        }
        ApplySearch();
    }

    private void ApplySearch()
    {
        if (_index is null || _suspendSearch)
        {
            return;
        }
        var query = new SearchQuery
        {
            Keywords = SearchText,
            TagMode = IsTagAny ? TagMatchMode.Any : TagMatchMode.All,
            IncludedTagIds = TagFilters.Where(t => t.State == TagFilterState.Include).Select(t => t.Tag.Id).ToList(),
            ExcludedTagIds = TagFilters.Where(t => t.State == TagFilterState.Exclude).Select(t => t.Tag.Id).ToList(),
            FieldConditions = FieldFilters.Select(f => f.ToCondition()).OfType<FieldCondition>().ToList(),
            MinRating = SelectedRatingFilter.MinRating,
            UnratedOnly = SelectedRatingFilter.UnratedOnly,
            RegisteredFrom = ParseDate(RegisteredFrom, out var registeredFromError),
            RegisteredTo = ParseDate(RegisteredTo, out var registeredToError),
            LastViewedFrom = ParseDate(LastViewedFrom, out var viewedFromError),
            LastViewedTo = ParseDate(LastViewedTo, out var viewedToError),
            NeverViewedOnly = NeverViewedOnly,
            Sort = SelectedSort?.Key ?? SortKey.Registered,
            SortFieldId = SelectedSort?.FieldId,
            Descending = SortDescending,
        };
        RegisteredError = registeredFromError ?? registeredToError;
        LastViewedError = viewedFromError ?? viewedToError;

        var results = _index.Search(query);
        ResultsReplacing?.Invoke();
        Results = results.Select(e => new DocumentCard(e, DataFolder.GetThumbnailPath(e.Id))).ToList();

        // 決定ログ L-3: 今の結果の中での件数
        var tagCounts = DocumentSearchIndex.CountTags(results);
        foreach (var tag in TagFilters)
        {
            tag.Count = tagCounts.GetValueOrDefault(tag.Tag.Id);
        }
        foreach (var field in FieldFilters.Where(f => f.IsSelect))
        {
            field.UpdateCounts(DocumentSearchIndex.CountFieldTexts(results, field.Field.Id));
        }

        OnPropertyChanged(nameof(IsFiltering));
        OnPropertyChanged(nameof(ResultText));
        SelectionRestoreRequested?.Invoke(SelectedIds.ToList());
    }

    private static DateOnly? ParseDate(string text, out string? error)
    {
        error = null;
        try
        {
            return FieldValues.Parse(FieldType.Date, text)?.Date is { } date ? DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        catch (CatalogException ex)
        {
            error = ex.Message;
            return null;
        }
    }
    public void SetSelection(IReadOnlyList<string> ids)
    {
        SelectedIds = ids;
        LoadDetails();
    }

    private void LoadDetails()
    {
        using (var connection = PdfManagerDatabase.Open(DataFolder.DatabasePath))
        {
            _detailItems = new DocumentRepository(connection).GetDetails(SelectedIds.ToList())
                .Select(d => new DetailItem(d, FieldFilters.Select(f => f.Field).ToList(), Editor.TagKeyOf))
                .ToList();
        }
        Editor.SetTargets(_detailItems);
        HasChanges = false;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(SelectedThumbnailPath));
        OnPropertyChanged(nameof(SelectedInfo));
    }

    // 選択を変える前・別の画面を開く前・閉じる前に呼ぶ。false なら操作をやめる(決定ログ J-1)
    public async Task<bool> ResolvePendingChangesAsync()
    {
        if (!HasChanges)
        {
            return true;
        }
        switch (await _dialogs.AskPendingChangesAsync())
        {
            case PendingChangesChoice.Save:
                return await SaveChangesAsync();
            case PendingChangesChoice.Discard:
                LoadDetails();
                return true;
            default:
                return false;
        }
    }

    private bool CanSaveOrCancel() => HasChanges;

    [RelayCommand(CanExecute = nameof(CanSaveOrCancel))]
    private Task SaveAsync() => SaveChangesAsync();

    [RelayCommand(CanExecute = nameof(CanSaveOrCancel))]
    private void Cancel()
    {
        LoadDetails();
        SaveMessage = null;
    }

    private async Task<bool> SaveChangesAsync()
    {
        var items = _detailItems.ToList();
        var parsed = new Dictionary<EditableDocument, Dictionary<long, FieldValue>>();
        var errors = Editor.ParseFieldValues(items, parsed);
        if (errors.Count > 0)
        {
            await _dialogs.ShowErrorAsync("次の項目の入力を直してから保存してください。\n\n" + string.Join("\n", errors.Take(MaxReportedProblems)));
            return false;
        }
        var changedNames = items.Where(i => i.FileNameChanged).ToList();
        var sameNames = changedNames.Count == 0 ? 0 : Editor.CountSameFileNames(changedNames);
        if (sameNames > 0 && !await _dialogs.ConfirmAsync("同じファイル名があります",
                $"同じファイル名になる PDF が {sameNames} 件あります。このまま保存しますか?\n(USB へ出力するときは「(2)」などを付けて区別します)", "保存する"))
        {
            return false;
        }

        using (var connection = PdfManagerDatabase.Open(DataFolder.DatabasePath))
        {
            var tagIds = Editor.ResolveTagIds(new CatalogRepository(connection), items.SelectMany(i => i.TagKeys));
            var repository = new DocumentRepository(connection);
            foreach (var item in items)
            {
                var (source, sourceId) = Editor.FileNameSourceFor(item, tagIds);
                repository.Update(new DocumentUpdate(
                    item.Detail.Id, DocumentEditor.TitleFor(item), item.Memo, item.Rating, Editor.FileNameFor(item),
                    source, sourceId, item.UseTitleAsFileName ? "" : item.Suffix, parsed[item],
                    item.TagKeys.Select(k => tagIds[k]).ToList()), DateTimeOffset.UtcNow);
            }
        }
        HasChanges = false;
        Refresh();
        SaveMessage = items.Count == 1 ? "保存しました。" : $"{items.Count} 件を保存しました。";
        return true;
    }

    // ==== ビューア・削除(決定ログ K-1〜K-5) ====

    [ObservableProperty]
    public partial bool IsListMessageOpen { get; set; }

    [ObservableProperty]
    public partial string ListMessage { get; set; } = "";

    // 決定ログ K-3: タブで新しく開いたとき
    public void RecordView(string documentId)
    {
        using (var connection = PdfManagerDatabase.Open(DataFolder.DatabasePath))
        {
            new DocumentRepository(connection).RecordView(documentId, DateTimeOffset.UtcNow);
        }
        // 編集中の内容を消さないよう、保存していない変更がないときだけ表示を更新する
        if (!HasChanges && SelectedIds.Contains(documentId))
        {
            LoadDetails();
        }
    }

    // 決定ログ K-5: 確認してすぐ削除する。beforeDelete でビューアのタブを閉じてもらう
    public async Task DeleteSelectedAsync(Action<IReadOnlyCollection<string>> beforeDelete)
    {
        var targets = _detailItems.Select(i => i.Detail).ToList();
        if (targets.Count == 0)
        {
            return;
        }
        var remover = new DocumentRemover(DataFolder);
        var subject = targets.Count == 1 ? $"「{targets[0].Title}」" : $"{targets.Count} 件の PDF";
        var detail = remover.UsesRecycleBin
            ? "PDF 本体はファイル名を付けて Windows のごみ箱に移します(ごみ箱から元に戻せます)。"
            : "ライブラリが USB メモリなどにあるため、ごみ箱は使えません。PDF 本体も完全に削除され、元に戻せません。";
        if (!await _dialogs.ConfirmAsync("削除の確認",
                $"{subject}を削除しますか?\n\n{detail}\nタイトル・タグ・項目・メモなどの情報は元に戻せません。", "削除する"))
        {
            return;
        }

        var ids = targets.Select(t => t.Id).ToList();
        beforeDelete(ids);
        var problems = new List<string>();
        var deleted = 0;
        foreach (var target in targets)
        {
            try
            {
                remover.Delete(target.Id, target.FileName);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"・{target.Title}:{ex.Message}");
            }
        }

        HasChanges = false;
        SelectedIds = [];
        Refresh();
        ListMessage = $"{deleted} 件を削除しました。" + (remover.UsesRecycleBin && deleted > 0 ? "PDF 本体は Windows のごみ箱にあります。" : "");
        IsListMessageOpen = true;
        if (problems.Count > 0)
        {
            await _dialogs.ShowErrorAsync("次の PDF は削除できませんでした(ほかのアプリで開いている可能性があります)。\n\n" + string.Join("\n", problems.Take(MaxReportedProblems)), "削除できませんでした");
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024.0):0} KB",
    };
}
