using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Library;
using PdfManager.Core.Search;
using PdfManager.Core.Settings;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.App.ViewModels;

// StorageName はサブにある文書だけ(決定ログ S-4)
public sealed record DocumentCard(DocumentEntry Entry, string ThumbnailPath, string? StorageName = null, bool IsOffline = false)
{
    public bool HasStorage => StorageName is not null;

    public string StorageText => IsOffline ? $"{StorageName}・未接続" : StorageName ?? "";

    public string Title => Entry.Title;

    public string FileName => Entry.FileName;

    // リスト表示の列(決定ログ L-9)
    public string RatingText => Entry.Rating == 0 ? "" : new string('★', Entry.Rating) + new string('☆', 5 - Entry.Rating);

    // 決定ログ Q-5: タグが 1 つもなければ「未設定」
    public string TagText => Entry.TagNames.Count == 0 ? "未設定" : string.Join("、", Entry.TagNames);

    public string RegisteredText => Entry.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd");

    public string ViewCountText => Entry.ViewCount.ToString("N0");

    public string LastViewedText => Entry.LastViewedAt?.ToLocalTime().ToString("yyyy/MM/dd") ?? "";

    public string VersionText => Entry.VersionText;

    public bool HasVersion => Entry.SeriesId is not null;

    // 決定ログ V-4: 続きの位置(例:続き 2/3)
    public string ChainText => Entry.ChainText;

    public bool HasChain => Entry.ChainLength > 0;
}

// 詳細エリアの「版の履歴」の 1 行(決定ログ N-6)
public sealed record VersionRow(VersionInfo Info, bool IsCurrent, bool IsLatest)
{
    public string Text => Info.DisplayName + (IsLatest ? "(最新)" : "");

    public string Sub => $"{Info.Title} ・ {Info.CreatedAt.ToLocalTime():yyyy/MM/dd}";
}

// 詳細エリアの「続き」「関連」の 1 行(決定ログ V-1)
public sealed record LinkRow(LinkedDocument Document, bool IsCurrent, string Position)
{
    public string Id => Document.Id;

    public string Text => Position.Length == 0 ? Document.Title : $"{Position} {Document.Title}";

    public string Sub => $"{Document.FileName} ・ {Document.CreatedAt.ToLocalTime():yyyy/MM/dd}" + (IsCurrent ? "(この PDF)" : "");
}

// 決定ログ J-7: 編集できない情報
public sealed record DetailInfo(string OriginalFileName, string PageCount, string FileSize, string Registered, string ViewCount, string LastViewed, string Storage);

// 決定ログ S-6: 保存先の絞り込みの選択肢。件数は今の結果(保存先の条件を除く)の中の件数
public sealed partial class StorageFilterOption(string name, bool isAll, string? storageId) : ObservableObject
{
    public bool IsAll => isAll;

    public string? StorageId => storageId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial int Count { get; set; }

    public string Label => $"{name}({Count:N0} 件)";

    // 画面の読み上げなどで、選択肢の名前として使われる
    public override string ToString() => Label;
}

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
    private readonly StorageAccess _storage;
    private DocumentSearchIndex? _index;
    private List<DetailItem> _detailItems = [];
    private IReadOnlyDictionary<string, string?> _selectedStorages = new Dictionary<string, string?>();
    private bool _suspendSearch;
    private bool _loadingDetails;

    public MainViewModel(DataFolder dataFolder, StorageAccess storage, IUserDialogs dialogs, ViewPreferences view, Action<ViewPreferences> saveView)
    {
        DataFolder = dataFolder;
        _storage = storage;
        _storage.Changed += OnStorageChanged;
        _dialogs = dialogs;
        _saveView = saveView;
        Editor = new DocumentEditor(dialogs, showRatingAndMemo: true, showTagSections: false);
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

    // すべてのタグの絞り込みの状態(項目の欄と「タグを検索」の欄で同じものを使う)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags))]
    public partial IReadOnlyList<TagFilterItem> TagFilters { get; set; } = [];

    // 決定ログ Q-14: 左に常に表示する項目(タググループ)
    [ObservableProperty]
    public partial IReadOnlyList<TagItemSection> ItemSections { get; set; } = [];

    // 決定ログ Q-7: タグ名・グループ名での検索。空のときは、項目以外で絞り込みに使っているタグだけを表示する
    [ObservableProperty]
    public partial string OtherTagSearch { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<TagFilterItem> OtherTags { get; set; } = [];

    [ObservableProperty]
    public partial string OtherTagHint { get; set; } = "";

    // 決定ログ Q-5: タグが 1 つも付いていない PDF(未設定)だけ
    [ObservableProperty]
    public partial bool UntaggedOnly { get; set; }

    [ObservableProperty]
    public partial int UntaggedCount { get; set; }

    partial void OnOtherTagSearchChanged(string value) => UpdateOtherTags();

    partial void OnUntaggedOnlyChanged(bool value) => ApplySearch();

    private void UpdateOtherTags()
    {
        var search = DocumentSearchIndex.Normalize(OtherTagSearch.Trim());
        var pinned = ItemSections.Select(s => s.Group.Id).ToHashSet();
        OtherTags = search.Length == 0
            ? TagFilters.Where(t => t.State != TagFilterState.None && (t.Tag.GroupId is not { } g || !pinned.Contains(g))).ToList()
            : TagFilters
                .Where(t => t.State != TagFilterState.None
                            || DocumentSearchIndex.Normalize(t.Tag.Name).Contains(search, StringComparison.Ordinal)
                            || DocumentSearchIndex.Normalize(t.Tag.GroupLabel).Contains(search, StringComparison.Ordinal))
                .ToList();
        OtherTagHint = search.Length == 0
            ? "項目にないタグは、タグ名・グループ名で検索して絞り込めます。"
            : OtherTags.Count == 0 ? "見つかりません。" : "";
    }

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

    [ObservableProperty]
    public partial bool IncludeOldVersions { get; set; }

    // 決定ログ S-6: 初めは「すべて」
    [ObservableProperty]
    public partial IReadOnlyList<StorageFilterOption> StorageFilters { get; set; } = [];

    [ObservableProperty]
    public partial StorageFilterOption? SelectedStorageFilter { get; set; }

    // ==== サブライブラリ(決定ログ S-1、S-4) ====

    public LibraryStorage Storage => _storage.Storage;

    public bool HasSubs => Storage.Subs.Count > 0;

    public string SubStatusText => $"サブ {Storage.Subs.Count(s => s.IsWritable)}/{Storage.Subs.Count} 接続";

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
        || TagFilters.Any(t => t.State != TagFilterState.None) || UntaggedOnly
        || FieldFilters.Any(f => f.HasCondition)
        || SelectedRatingFilter != RatingChoices[0]
        || RegisteredFrom.Trim().Length > 0 || RegisteredTo.Trim().Length > 0
        || LastViewedFrom.Trim().Length > 0 || LastViewedTo.Trim().Length > 0 || NeverViewedOnly || IncludeOldVersions
        || SelectedStorageFilter is { IsAll: false };

    public bool HasNoResults => HasDocuments && Results.Count == 0;

    public string ResultText => IsFiltering ? $"{Results.Count:N0} 件(全 {DocumentCount:N0} 件)" : $"{Results.Count:N0} 件";

    public string DocumentSummaryText => DocumentCount == 0
        ? "PDF はまだ登録されていません"
        : $"{DocumentCount:N0} 件の PDF が登録されています";

    // ==== 詳細 ====

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(CancelCommand))]
    public partial bool HasChanges { get; set; }

    // 決定ログ S-4・S-12: つながっていないサブの文書は表示のみ
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string? ReadOnlyMessage { get; set; }

    public bool IsEditable => ReadOnlyMessage is null;

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
        only.Detail.LastViewedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "(まだ開いていません)",
        StorageText(_selectedStorages.GetValueOrDefault(only.Detail.Id))) : null;

    private string StorageText(string? storageId) => Storage.FindSub(storageId) is { } sub
        ? $"{sub.Name}({sub.StatusText})\n{sub.Folder}"
        : "メイン";

    // ==== 版の履歴(決定ログ N-1〜N-8) ====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersions), nameof(OldVersionNote))]
    public partial IReadOnlyList<VersionRow> Versions { get; set; } = [];

    [ObservableProperty]
    public partial string VersionLabel { get; set; } = "";

    public bool IsSingleSelection => _detailItems.Count == 1;

    public bool HasVersions => Versions.Count > 0;

    public DocumentDetail? SingleDetail => _detailItems is [var only] ? only.Detail : null;

    public string? OldVersionNote => Versions.FirstOrDefault(v => v.IsCurrent) is { IsLatest: false }
        ? $"これは旧版です(最新は{Versions[^1].Info.DisplayName})。"
        : null;

    partial void OnVersionLabelChanged(string value)
    {
        if (!_loadingDetails)
        {
            HasChanges = true;
            SaveMessage = null;
        }
    }

    // ==== 保存した検索(F-16、決定ログ X-1〜X-4) ====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedSearches))]
    public partial IReadOnlyList<SavedSearch> SavedSearches { get; set; } = [];

    public bool HasSavedSearches => SavedSearches.Count > 0;

    private void LoadSavedSearches()
    {
        using var connection = PdfManagerDatabase.Open(DataFolder.DatabasePath);
        SavedSearches = new SavedSearchRepository(connection).GetAll();
    }

    // 決定ログ X-1: 今の絞り込みの条件(画面に入力した形のまま)
    private SavedSearchCondition CurrentCondition() => new()
    {
        Keywords = SearchText,
        TagAny = IsTagAny,
        IncludedTagIds = TagFilters.Where(t => t.State == TagFilterState.Include).Select(t => t.Tag.Id).ToList(),
        ExcludedTagIds = TagFilters.Where(t => t.State == TagFilterState.Exclude).Select(t => t.Tag.Id).ToList(),
        UntaggedOnly = UntaggedOnly,
        Fields = FieldFilters.Where(f => f.HasCondition)
            .Select(f => new SavedFieldCondition(f.Field.Id, f.Text, f.From, f.To, f.Options.Where(o => o.IsChecked).Select(o => o.Value).ToList()))
            .ToList(),
        MinRating = SelectedRatingFilter.MinRating,
        UnratedOnly = SelectedRatingFilter.UnratedOnly,
        RegisteredFrom = RegisteredFrom,
        RegisteredTo = RegisteredTo,
        LastViewedFrom = LastViewedFrom,
        LastViewedTo = LastViewedTo,
        NeverViewedOnly = NeverViewedOnly,
        IncludeOldVersions = IncludeOldVersions,
        FilterByStorage = SelectedStorageFilter is { IsAll: false },
        StorageId = SelectedStorageFilter is { IsAll: false } storage ? storage.StorageId : null,
    };

    public async Task SaveSearchAsync(string name)
    {
        if (!IsFiltering && !await _dialogs.ConfirmAsync("検索条件の保存", "絞り込みの条件がありません(すべての PDF を表示する条件になります)。保存しますか?", "保存する"))
        {
            return;
        }
        if (await RunSavedSearchAsync(r => r.Add(name, CurrentCondition())))
        {
            ShowListMessage("保存した検索", $"今の条件を「{NameNormalizer.Clean(name)}」として保存しました。");
        }
    }

    // 決定ログ X-4: 今の条件をすべて置き換える。消えたタグなどは外して知らせる
    public async Task ApplySavedSearchAsync(SavedSearch saved)
    {
        var condition = saved.Condition;
        var missing = 0;
        _suspendSearch = true;
        try
        {
            foreach (var tag in TagFilters)
            {
                tag.State = TagFilterState.None;
            }
            foreach (var field in FieldFilters)
            {
                field.Clear();
            }
            SearchText = condition.Keywords;
            IsTagAny = condition.TagAny;
            var tags = TagFilters.ToDictionary(t => t.Tag.Id);
            foreach (var (ids, state) in new[] { (condition.IncludedTagIds, TagFilterState.Include), (condition.ExcludedTagIds, TagFilterState.Exclude) })
            {
                foreach (var id in ids)
                {
                    if (tags.TryGetValue(id, out var tag))
                    {
                        tag.State = state;
                    }
                    else
                    {
                        missing++;
                    }
                }
            }
            UntaggedOnly = condition.UntaggedOnly;
            foreach (var savedField in condition.Fields)
            {
                if (FieldFilters.FirstOrDefault(f => f.Field.Id == savedField.FieldId) is not { } field)
                {
                    missing++;
                    continue;
                }
                field.Text = savedField.Text;
                field.From = savedField.From;
                field.To = savedField.To;
                foreach (var value in savedField.Values)
                {
                    if (field.Options.FirstOrDefault(o => o.Value == value) is { } option)
                    {
                        option.IsChecked = true;
                    }
                    else
                    {
                        missing++;
                    }
                }
            }
            SelectedRatingFilter = RatingChoices.FirstOrDefault(r => r.MinRating == condition.MinRating && r.UnratedOnly == condition.UnratedOnly) ?? RatingChoices[0];
            RegisteredFrom = condition.RegisteredFrom;
            RegisteredTo = condition.RegisteredTo;
            LastViewedFrom = condition.LastViewedFrom;
            LastViewedTo = condition.LastViewedTo;
            NeverViewedOnly = condition.NeverViewedOnly;
            IncludeOldVersions = condition.IncludeOldVersions;
            var storage = condition.FilterByStorage ? StorageFilters.FirstOrDefault(o => !o.IsAll && o.StorageId == condition.StorageId) : null;
            if (condition.FilterByStorage && storage is null)
            {
                missing++;
            }
            SelectedStorageFilter = storage ?? StorageFilters.FirstOrDefault();
        }
        finally
        {
            _suspendSearch = false;
        }
        UpdateOtherTags();
        ApplySearch();
        ShowListMessage("保存した検索", $"「{saved.Name}」の条件で絞り込みました。");
        if (missing > 0)
        {
            await _dialogs.ShowErrorAsync($"一部の条件は使えませんでした({missing} 件)。保存したあとで、タグ・補助項目・サブライブラリなどが削除された可能性があります。", "保存した検索");
        }
    }

    public Task RenameSavedSearchAsync(SavedSearch saved, string name) => RunSavedSearchAsync(r => r.Rename(saved.Id, name));

    public Task MoveSavedSearchAsync(SavedSearch saved, int offset) => RunSavedSearchAsync(r => r.Move(saved.Id, offset));

    public async Task DeleteSavedSearchAsync(SavedSearch saved)
    {
        if (await _dialogs.ConfirmAsync("保存した検索の削除", $"「{saved.Name}」を削除しますか?(PDF は削除されません)", "削除する"))
        {
            await RunSavedSearchAsync(r => r.Delete(saved.Id));
        }
    }

    private async Task<bool> RunSavedSearchAsync(Action<SavedSearchRepository> action)
    {
        try
        {
            using (var connection = PdfManagerDatabase.Open(DataFolder.DatabasePath))
            {
                action(new SavedSearchRepository(connection));
            }
            LoadSavedSearches();
            return true;
        }
        catch (CatalogException ex)
        {
            await _dialogs.ShowErrorAsync(ex.Message, "保存した検索");
            return false;
        }
    }

    // ==== 続き・関連(決定ログ V-1〜V-8) ====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChain))]
    public partial IReadOnlyList<LinkRow> ChainRows { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRelated))]
    public partial IReadOnlyList<LinkRow> RelatedRows { get; set; } = [];

    public bool HasChain => ChainRows.Count > 0;

    public bool HasRelated => RelatedRows.Count > 0;

    private void LoadLinks(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        if (_detailItems is not [var only])
        {
            ChainRows = [];
            RelatedRows = [];
            return;
        }
        var links = new LinkRepository(connection);
        var chain = links.GetChain(only.Detail.Id);
        ChainRows = chain.Select((d, i) => new LinkRow(d, d.Id == only.Detail.Id, $"{i + 1}/{chain.Count}")).ToList();
        RelatedRows = links.GetRelated(only.Detail.Id).Select(d => new LinkRow(d, false, "")).ToList();
    }

    // 文書選択の画面の候補(決定ログ V-3)。自分自身と、すでにつながっているものは除く
    public IReadOnlyList<DocumentEntry> LinkCandidates(bool forChain)
    {
        if (_index is null || SingleDetail is not { } current)
        {
            return [];
        }
        var related = RelatedRows.Select(r => r.Id).ToHashSet();
        return _index.Search(new SearchQuery { IncludeOldVersions = true })
            .Where(e => e.Id != current.Id && (forChain ? e.ChainLength == 0 : !related.Contains(e.Id)))
            .ToList();
    }

    // kind は "previous"(前をつなぐ)・"next"(次をつなぐ)・"related"(関連を追加)
    public async Task LinkAsync(string kind, string otherId)
    {
        if (SingleDetail is not { } current || !await EnsureEditableAsync([current.Id, otherId], "続き・関連"))
        {
            return;
        }
        try
        {
            await ChangeAsync(AffectedByLinks(current.Id, otherId), connection =>
            {
                var links = new LinkRepository(connection);
                switch (kind)
                {
                    case "previous":
                        links.InsertPrevious(current.Id, otherId);
                        break;
                    case "next":
                        links.InsertNext(current.Id, otherId);
                        break;
                    default:
                        links.AddRelated(current.Id, otherId);
                        break;
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            await _dialogs.ShowErrorAsync(ex.Message, "続き・関連");
        }
        Refresh();
    }

    // 決定ログ V-1・V-6: 続きから外す(前後をつなぎ直す)/関連から外す
    public async Task UnlinkAsync(string otherId, bool fromChain)
    {
        if (SingleDetail is not { } current)
        {
            return;
        }
        var row = (fromChain ? ChainRows : RelatedRows).First(r => r.Id == otherId);
        var message = fromChain
            ? $"「{row.Document.Title}」を続きから外しますか?\n前後の PDF は、そのままつなぎ直します。"
            : $"「{row.Document.Title}」を関連から外しますか?";
        if (!await EnsureEditableAsync([current.Id, otherId], "続き・関連") || !await _dialogs.ConfirmAsync("続き・関連", message, "外す"))
        {
            return;
        }
        await ChangeAsync(AffectedByLinks(current.Id, otherId), connection =>
        {
            var links = new LinkRepository(connection);
            if (fromChain)
            {
                links.RemoveFromChain(otherId);
            }
            else
            {
                links.RemoveRelated(current.Id, otherId);
            }
        });
        Refresh();
    }

    // つなぎ方が変わる文書(サブライブラリへの書き込み用):両方と、それぞれの前後
    private IReadOnlyCollection<string> AffectedByLinks(params string[] ids)
    {
        using var connection = PdfManagerDatabase.Open(DataFolder.DatabasePath);
        var links = new LinkRepository(connection);
        return ids.Concat(ids.Select(links.PreviousOf)).Concat(ids.Select(links.NextOf)).OfType<string>().Distinct().ToList();
    }

    // 決定ログ V-5: ビューアの「前へ」「次へ」
    public (LinkedDocument? Previous, LinkedDocument? Next) ChainNeighbors(string documentId)
    {
        using var connection = PdfManagerDatabase.Open(DataFolder.DatabasePath);
        var links = new LinkRepository(connection);
        var chain = links.GetChain(documentId).ToList();
        var index = chain.FindIndex(d => d.Id == documentId);
        return index < 0 ? (null, null) : (index > 0 ? chain[index - 1] : null, index < chain.Count - 1 ? chain[index + 1] : null);
    }

    // 決定ログ N-8
    public async Task DetachVersionAsync(string documentId)
    {
        var row = Versions.First(v => v.Info.DocumentId == documentId);
        if (!await EnsureEditableAsync([documentId], "版から外す")
            || !await _dialogs.ConfirmAsync("版から外す", $"{row.Info.DisplayName}「{row.Info.Title}」を版から外し、独立した PDF に戻しますか?\n残りの版は第 1 版から番号を付け直します。", "外す"))
        {
            return;
        }
        await ChangeAsync([documentId], connection => new VersionRepository(connection).Detach(documentId));
        Refresh();
    }

    // 決定ログ N-1②
    public async Task GroupAsVersionsAsync(IReadOnlyList<string> documentIds)
    {
        if (documentIds.Count < 2)
        {
            await _dialogs.ShowErrorAsync("版としてまとめるには、一覧で 2 件以上を選んでください(Ctrl・Shift で複数選択)。", "版としてまとめる");
            return;
        }
        // 決定ログ S-7: 版の系列がメインとサブにまたがらないようにする
        if (StorageIdsOf(documentIds).Values.Distinct().Count() > 1)
        {
            await _dialogs.ShowErrorAsync("保存先(メイン・サブ)が違う PDF は、版としてまとめられません。先に「保存先を移す」で同じ保存先にそろえてください。", "版としてまとめる");
            return;
        }
        if (!await EnsureEditableAsync(documentIds, "版としてまとめる")
            || !await _dialogs.ConfirmAsync("版としてまとめる",
                $"選んだ {documentIds.Count} 件(と、それらがすでに属している版)を 1 つの版にまとめますか?\n登録日の古い順に第 1 版、第 2 版…と番号を付けます。", "まとめる"))
        {
            return;
        }
        await ChangeAsync(documentIds, connection => new VersionRepository(connection).Group(documentIds));
        Refresh();
        // 旧版になって一覧から消えたものは選択から外す(詳細エリアに最新版の版の履歴が出るように)
        var visible = SelectedIds.Where(id => Results.Any(c => c.Entry.Id == id)).ToList();
        if (visible.Count != SelectedIds.Count)
        {
            SetSelection(visible);
        }
    }

    // ==== サブライブラリにある文書の扱い(決定ログ S-4、S-10、S-12) ====

    public IReadOnlyDictionary<string, string?> StorageIdsOf(IEnumerable<string> documentIds)
    {
        using var connection = PdfManagerDatabase.Open(DataFolder.DatabasePath);
        return new SubLibraryRepository(connection).GetStorageIds(documentIds);
    }

    // つながっていないサブの文書が含まれていれば、理由を表示して false を返す
    public async Task<bool> EnsureEditableAsync(IEnumerable<string> documentIds, string title)
    {
        if (StorageIdsOf(documentIds).Values.Distinct().FirstOrDefault(s => !Storage.IsAvailable(s)) is { } unavailable)
        {
            await _dialogs.ShowErrorAsync(Storage.UnavailableMessage(unavailable), title);
            return false;
        }
        return true;
    }

    // 変更の前後で、サブに印を付けて書き込む。書き込めなかったことは知らせるが、メインの変更はそのまま
    private async Task ChangeAsync(IReadOnlyCollection<string> documentIds, Action<Microsoft.Data.Sqlite.SqliteConnection> change)
    {
        var token = await _storage.RunAsync(s => s.BeginChange(documentIds));
        using (var connection = PdfManagerDatabase.Open(DataFolder.DatabasePath))
        {
            change(connection);
        }
        await ReportSubProblemsAsync(await _storage.RunAsync(s => s.CompleteChange(token)));
    }

    private async Task ReportSubProblemsAsync(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0)
        {
            await _dialogs.ShowErrorAsync(string.Join("\n\n", problems.Take(MaxReportedProblems)), "サブライブラリ");
        }
    }

    // ビューアで開く PDF の場所。つながっていなければ理由を表示して null(決定ログ S-4)
    public async Task<IReadOnlyDictionary<string, string>> ResolvePdfPathsAsync(IReadOnlyCollection<string> documentIds)
    {
        var storages = StorageIdsOf(documentIds);
        var paths = new Dictionary<string, string>();
        foreach (var (id, storageId) in storages)
        {
            if (Storage.GetDataFolder(storageId) is { } folder)
            {
                paths[id] = folder.GetPdfPath(id);
            }
        }
        if (storages.FirstOrDefault(s => !paths.ContainsKey(s.Key)) is { Key: not null } missing)
        {
            await _dialogs.ShowErrorAsync(Storage.UnavailableMessage(missing.Value), "開けません");
        }
        return paths;
    }

    // 決定ログ S-3・S-7: 見積もりと確認。移さないときは null
    public async Task<MovePlan?> PrepareMoveAsync(IReadOnlyList<string> documentIds, string? targetId)
    {
        if (documentIds.Count == 0)
        {
            return null;
        }
        var plan = await _storage.RunAsync(s => s.PlanMove(documentIds, targetId));
        var targetName = Storage.StorageName(targetId);
        if (plan.Count == 0)
        {
            await _dialogs.ShowErrorAsync($"選んだ PDF はすでに「{targetName}」にあります。", "保存先を移す");
            return null;
        }
        if (plan.UnavailableStorages.Count > 0)
        {
            await _dialogs.ShowErrorAsync(
                $"次の保存先がつながっていないため、移せません。\n\n・{string.Join("\n・", plan.UnavailableStorages)}\n\n外付け HDD などをつないでから、もう一度操作してください。",
                "保存先を移す");
            return null;
        }
        if (!plan.HasEnoughSpace)
        {
            await _dialogs.ShowErrorAsync($"「{targetName}」のドライブの空き容量が足りません。\n移す PDF:{FormatSize(plan.Bytes)}\n空き容量:{FormatSize(plan.FreeBytes ?? 0)}", "保存先を移す");
            return null;
        }
        var series = plan.Count > plan.SelectedCount ? $"\n版の系列ごと移すため、選んだ {plan.SelectedCount} 件より多くなります。" : "";
        return await _dialogs.ConfirmAsync("保存先を移す",
                $"{plan.Count} 件の PDF(合計 {FormatSize(plan.Bytes)})を「{targetName}」へ移しますか?{series}\n\nコピーして中身が同じことを確かめてから、元の場所の PDF を消します。",
                "移す")
            ? plan
            : null;
    }

    public async Task ReportMoveAsync(MoveResult result, string? targetId)
    {
        Refresh();
        ShowListMessage("保存先を移す", (result.Cancelled ? "途中で止めました。" : "") + $"{result.Moved} 件を「{Storage.StorageName(targetId)}」へ移しました。");
        if (result.Problems.Count > 0)
        {
            await _dialogs.ShowErrorAsync("次の PDF は移せませんでした。\n\n" + string.Join("\n", result.Problems.Take(MaxReportedProblems)), "保存先を移す");
        }
    }

    private void OnStorageChanged()
    {
        UpdateStorageFilters();
        OnPropertyChanged(nameof(HasSubs));
        OnPropertyChanged(nameof(SubStatusText));
        ApplySearch();
        UpdateReadOnly();
        OnPropertyChanged(nameof(SelectedInfo));
    }

    private void UpdateStorageFilters()
    {
        var previous = SelectedStorageFilter;
        _suspendSearch = true;
        try
        {
            StorageFilters =
            [
                new("すべて", true, null),
                new("メイン", false, null),
                .. Storage.Subs.Select(s => new StorageFilterOption(s.Name, false, s.Id)),
            ];
            SelectedStorageFilter = StorageFilters.FirstOrDefault(o => previous is not null && o.IsAll == previous.IsAll && o.StorageId == previous.StorageId)
                ?? StorageFilters[0];
        }
        finally
        {
            _suspendSearch = false;
        }
    }

    private void UpdateReadOnly()
    {
        var unavailable = _selectedStorages.Values.Where(s => !Storage.IsAvailable(s)).ToList();
        ReadOnlyMessage = unavailable.Count == 0
            ? null
            : Storage.FindSub(unavailable[0]) is { } sub
                ? $"保存先「{sub.Name}」が{(sub.Status == SubLibraryStatus.NotFound ? "つながっていない" : "使えない")}ため、表示のみです(編集・削除・開くことはできません)。"
                : "保存先のサブライブラリが登録されていないため、表示のみです。";
    }

    // 起動時と、取り込み画面・項目タグ管理画面を閉じたとき、保存したときに呼ぶ
    public void Refresh() => Refresh(null);

    private void Refresh(ViewPreferences? initialView)
    {
        _suspendSearch = true;
        try
        {
            using var connection = PdfManagerDatabase.Open(DataFolder.DatabasePath);
            var catalog = new CatalogRepository(connection);
            // 補助項目(年月・日付・数値・文字、決定ログ Q-6)。選択肢型はタググループに変わったため使わない
            var fields = catalog.GetFields().Where(f => f.Type != FieldType.Select).ToList();
            var tags = catalog.GetTags();
            var groups = catalog.GetGroups();
            var states = TagFilters.ToDictionary(t => t.Tag.Id, t => t.State);
            TagFilters = tags.Select(t => new TagFilterItem(t, states.GetValueOrDefault(t.Id), ApplySearch)).ToList();
            // 決定ログ Q-14: 項目(左に常に表示するタググループ)
            var searches = ItemSections.ToDictionary(s => s.Group.Id, s => s.Search);
            ItemSections = groups.Where(g => g.IsPinned).OrderBy(g => g.PinOrder).ThenBy(g => g.Id)
                .Select(g => new TagItemSection(g, TagFilters.Where(t => t.Tag.GroupId == g.Id).ToList()) { Search = searches.GetValueOrDefault(g.Id, "") })
                .ToList();
            UpdateOtherTags();

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
                .. fields.Select(f => new SortOption(SortKey.Field, f.Id, $"補助項目:{f.Name}")),
            ];
            // 項目での並び替えは、項目が見つからなければ登録日に戻す
            SelectedSort = SortOptions.FirstOrDefault(o => (o.Key, o.FieldId) == currentSort) ?? SortOptions[0];

            Editor.SetCatalog(fields, groups, tags, new DocumentRepository(connection).GetFileNames());
            _index = DocumentSearchIndex.Load(connection);
            // 補助項目は、入っている値から選べるようにする(決定ログ Q-6)
            var previous = FieldFilters.ToDictionary(f => f.Field.Id);
            FieldFilters = fields.Select(f => new FieldFilter(f, previous.GetValueOrDefault(f.Id), _index.DistinctFieldValues(f.Id), ApplySearch)).ToList();
        }
        finally
        {
            _suspendSearch = false;
        }
        UpdateStorageFilters();
        LoadSavedSearches();
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

    partial void OnIncludeOldVersionsChanged(bool value) => ApplySearch();

    partial void OnSelectedStorageFilterChanged(StorageFilterOption? value) => ApplySearch();

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
            IncludeOldVersions = false;
            UntaggedOnly = false;
            SelectedStorageFilter = StorageFilters.FirstOrDefault();
        }
        finally
        {
            _suspendSearch = false;
        }
        UpdateOtherTags();
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
            UntaggedOnly = UntaggedOnly,
            FieldConditions = FieldFilters.Select(f => f.ToCondition()).OfType<FieldCondition>().ToList(),
            MinRating = SelectedRatingFilter.MinRating,
            UnratedOnly = SelectedRatingFilter.UnratedOnly,
            RegisteredFrom = ParseDate(RegisteredFrom, out var registeredFromError),
            RegisteredTo = ParseDate(RegisteredTo, out var registeredToError),
            LastViewedFrom = ParseDate(LastViewedFrom, out var viewedFromError),
            LastViewedTo = ParseDate(LastViewedTo, out var viewedToError),
            NeverViewedOnly = NeverViewedOnly,
            IncludeOldVersions = IncludeOldVersions,
            Sort = SelectedSort?.Key ?? SortKey.Registered,
            SortFieldId = SelectedSort?.FieldId,
            Descending = SortDescending,
        };
        RegisteredError = registeredFromError ?? registeredToError;
        LastViewedError = viewedFromError ?? viewedToError;

        // 決定ログ S-6: 保存先の件数は、保存先の条件だけを除いた結果の中で数える
        var withoutStorage = _index.Search(query);
        var storageCounts = DocumentSearchIndex.CountStorages(withoutStorage);
        foreach (var option in StorageFilters)
        {
            option.Count = option.IsAll ? withoutStorage.Count : storageCounts.GetValueOrDefault(option.StorageId ?? "");
        }
        var results = SelectedStorageFilter is { IsAll: false } storage
            ? withoutStorage.Where(e => e.StorageId == storage.StorageId).ToList()
            : withoutStorage;
        ResultsReplacing?.Invoke();
        Results = results.Select(e => e.StorageId is null
                ? new DocumentCard(e, DataFolder.GetThumbnailPath(e.Id))
                : new DocumentCard(e, DataFolder.GetThumbnailPath(e.Id), Storage.StorageName(e.StorageId), !Storage.IsAvailable(e.StorageId)))
            .ToList();

        // 決定ログ L-3: 今の結果の中での件数
        var tagCounts = DocumentSearchIndex.CountTags(results);
        foreach (var tag in TagFilters)
        {
            tag.Count = tagCounts.GetValueOrDefault(tag.Tag.Id);
        }
        UntaggedCount = DocumentSearchIndex.CountUntagged(results);
        foreach (var field in FieldFilters)
        {
            field.UpdateCounts(DocumentSearchIndex.CountFieldValues(results, field.Field.Id));
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
            _selectedStorages = new SubLibraryRepository(connection).GetStorageIds(_detailItems.Select(d => d.Detail.Id));
        }
        UpdateReadOnly();
        Editor.SetTargets(_detailItems);
        _loadingDetails = true;
        using (var connection = PdfManagerDatabase.Open(DataFolder.DatabasePath))
        {
            var versions = _detailItems is [var only] ? new VersionRepository(connection).GetVersions(only.Detail.Id) : [];
            Versions = versions.Select(v => new VersionRow(v, v.DocumentId == _detailItems[0].Detail.Id, v == versions[^1])).ToList();
            VersionLabel = Versions.FirstOrDefault(v => v.IsCurrent)?.Info.Label ?? "";
            LoadLinks(connection);
        }
        _loadingDetails = false;
        HasChanges = false;
        // 保存したときは、この後で「保存しました」を表示し直す
        SaveMessage = null;
        OnPropertyChanged(nameof(IsSingleSelection));
        OnPropertyChanged(nameof(SingleDetail));
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

    private bool CanSave() => HasChanges && IsEditable;

    [RelayCommand(CanExecute = nameof(CanSave))]
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
        var label = VersionLabel;
        var hasVersions = HasVersions;
        var saved = await DetailSaver.SaveAsync(DataFolder, _storage, _dialogs, Editor, items, connection =>
        {
            if (items is [var single] && hasVersions)
            {
                new VersionRepository(connection).SetLabel(single.Detail.Id, label);
            }
        });
        if (!saved)
        {
            return false;
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

    [ObservableProperty]
    public partial string ListMessageTitle { get; set; } = "";

    private void ShowListMessage(string title, string message)
    {
        ListMessageTitle = title;
        ListMessage = message;
        IsListMessageOpen = true;
    }

    // 決定ログ K-3: タブで新しく開いたとき
    public async void RecordView(string documentId)
    {
        await ChangeAsync([documentId], connection => new DocumentRepository(connection).RecordView(documentId, DateTimeOffset.UtcNow));
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
        // 決定ログ S-10: つながっていないサブの PDF はごみ箱に入れられないため、削除できない
        if (targets.Count == 0 || !await EnsureEditableAsync(targets.Select(t => t.Id), "削除できません"))
        {
            return;
        }
        var remover = new DocumentRemover(DataFolder);
        var storages = StorageIdsOf(targets.Select(t => t.Id));
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
        // 決定ログ V-6: 続きの前後をつなぎ直すため、前後の文書もサブへ書き込む対象にする
        await ChangeAsync(AffectedByLinks([.. ids]), _ =>
        {
            foreach (var target in targets)
            {
                try
                {
                    remover.Delete(target.Id, target.FileName, Storage.GetDataFolder(storages.GetValueOrDefault(target.Id)));
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"・{target.Title}:{ex.Message}");
                }
            }
        });

        HasChanges = false;
        SelectedIds = [];
        Refresh();
        ShowListMessage("削除", $"{deleted} 件を削除しました。" + (remover.UsesRecycleBin && deleted > 0 ? "PDF 本体は Windows のごみ箱にあります。" : ""));
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
