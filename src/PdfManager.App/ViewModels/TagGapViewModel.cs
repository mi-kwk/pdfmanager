using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Import;
using PdfManager.Core.Library;
using PdfManager.Core.Pdf;
using PdfManager.Core.Search;
using PdfManager.Core.Storage;

namespace PdfManager.App.ViewModels;

// 「探すもの」のボタン 1 つ分(決定ログ BC-2)。件数は今の条件で数えたもの
public sealed partial class TagGapKindOption(TagGapKind kind, string label) : ObservableObject
{
    public TagGapKind Kind { get; } = kind;

    public string Label { get; } = label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string Text => $"{Label}({Count:N0})";
}

// 選択肢(グループ・順位・絞り込みのタグ・対象)
public sealed record TagGapChoice(string Label, long? Id = null, int Rank = 1)
{
    public override string ToString() => Label;
}

// 一覧の 1 行(決定ログ BC-6):タイトル、足りないもの、付いているタグ
public sealed record TagGapRow(DocumentEntry Entry, string Description, string StorageText, bool IsOffline)
{
    public string Title => Entry.Title;

    public string TagsText => Entry.TagNames.Count == 0 ? "未設定" : string.Join("、", Entry.TagNames);
}

// SC-12 タグの付け漏れ確認(決定ログ BC-1〜BC-8)
public sealed partial class TagGapViewModel : ObservableObject, IDisposable
{
    private const string AllGroupsLabel = "順位 1 のすべて";

    private readonly DataFolder _dataFolder;
    private readonly StorageAccess _storage;
    private readonly IUserDialogs _dialogs;
    private readonly IReadOnlyList<string> _mainResultIds;
    private readonly string _previewFolder = Path.Combine(Path.GetTempPath(), "PdfManager", "taggap-" + Guid.NewGuid().ToString("N"));
    private IReadOnlyList<TagGroup> _groups = [];
    private IReadOnlyList<TagSummary> _tags = [];
    private IReadOnlyList<FieldDefinition> _fields = [];
    private IReadOnlyList<DocumentEntry> _entries = [];
    private List<DetailItem> _selection = [];
    private bool _suspend;
    private int _previewVersion;

    // mainResultIds: メイン画面の今の検索結果(対象を「メイン画面の今の検索結果」にしたときに使う、BC-5)
    public TagGapViewModel(DataFolder dataFolder, StorageAccess storage, IUserDialogs dialogs, IReadOnlyList<string> mainResultIds, int tagCollapseRank)
    {
        _dataFolder = dataFolder;
        _storage = storage;
        _dialogs = dialogs;
        _mainResultIds = mainResultIds;
        Directory.CreateDirectory(_previewFolder);
        Editor = new DocumentEditor(dialogs, showRatingAndMemo: false) { CollapseUpToRank = tagCollapseRank };
        Editor.Edited += () => HasChanges = true;
        Kinds =
        [
            new(TagGapKind.MissingGroup, "①グループのタグがない") { IsSelected = true },
            new(TagGapKind.MissingChild, "②下の順位のタグがない"),
            new(TagGapKind.MissingParent, "③上の順位のタグがない"),
        ];
        foreach (var kind in Kinds)
        {
            kind.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TagGapKindOption.IsSelected) && kind.IsSelected)
                {
                    OnKindChanged(kind);
                }
            };
        }
        Targets = [new("ライブラリ全体"), new($"メイン画面の今の検索結果({mainResultIds.Count:N0} 件)", Id: 1)];
        _suspend = true;
        SelectedTarget = Targets[0];
        _suspend = false;
        LoadCatalog();
        Reload();
    }

    public DocumentEditor Editor { get; }

    public IReadOnlyList<TagGapKindOption> Kinds { get; }

    public IReadOnlyList<TagGapChoice> Targets { get; }

    public ObservableCollection<TagGapRow> Rows { get; } = [];

    public TagGapKind SelectedKind => Kinds.First(k => k.IsSelected).Kind;

    public bool ShowGroupChoice => SelectedKind == TagGapKind.MissingGroup;

    public bool ShowRankChoice => SelectedKind != TagGapKind.MissingGroup;

    [ObservableProperty]
    public partial IReadOnlyList<TagGapChoice> GroupChoices { get; set; } = [];

    [ObservableProperty]
    public partial TagGapChoice? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<TagGapChoice> RankChoices { get; set; } = [];

    [ObservableProperty]
    public partial TagGapChoice? SelectedRank { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<TagGapChoice> FilterChoices { get; set; } = [];

    [ObservableProperty]
    public partial TagGapChoice? SelectedFilter { get; set; }

    [ObservableProperty]
    public partial string FilterLabel { get; set; } = "タグ";

    [ObservableProperty]
    public partial string Keyword { get; set; } = "";

    [ObservableProperty]
    public partial TagGapChoice? SelectedTarget { get; set; }

    [ObservableProperty]
    public partial string ResultText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool HasChanges { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsReadOnly { get; set; }

    [ObservableProperty]
    public partial string? ReadOnlyMessage { get; set; }

    [ObservableProperty]
    public partial string SelectionText { get; set; } = "左の一覧から PDF を選んでください(Ctrl・Shift で複数選択)";

    [ObservableProperty]
    public partial ImageSource? PreviewImage { get; set; }

    [ObservableProperty]
    public partial string? SaveMessage { get; set; }

    public bool HasSelection => _selection.Count > 0;

    partial void OnSelectedGroupChanged(TagGapChoice? value) => OnConditionChanged();

    partial void OnSelectedRankChanged(TagGapChoice? value)
    {
        if (!_suspend)
        {
            UpdateFilterChoices();
        }
        OnConditionChanged();
    }

    partial void OnSelectedFilterChanged(TagGapChoice? value) => OnConditionChanged();

    partial void OnKeywordChanged(string value) => OnConditionChanged();

    partial void OnSelectedTargetChanged(TagGapChoice? value) => OnConditionChanged();

    // 押したボタンだけを選んだ状態にする(ほかのボタンの選択は先に外す)
    private void OnKindChanged(TagGapKindOption selected)
    {
        foreach (var other in Kinds.Where(k => k != selected && k.IsSelected))
        {
            other.IsSelected = false;
        }
        OnPropertyChanged(nameof(ShowGroupChoice));
        OnPropertyChanged(nameof(ShowRankChoice));
        UpdateFilterChoices();
        OnConditionChanged();
    }

    private void OnConditionChanged()
    {
        if (!_suspend)
        {
            ApplyConditions();
        }
    }

    // ==== 読み込みと検索 ====

    private void LoadCatalog()
    {
        using var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath);
        var catalog = new CatalogRepository(connection);
        _groups = catalog.GetGroups();
        _tags = catalog.GetTags();
        _fields = catalog.GetFields().Where(f => f.Type != FieldType.Select).ToList();
        Editor.SetCatalog(_fields, _groups, _tags, new DocumentRepository(connection).GetFileNames());

        _suspend = true;
        try
        {
            var group = SelectedGroup?.Id;
            GroupChoices =
            [
                new(AllGroupsLabel),
                .. _groups.Where(g => !g.IsIndependent).OrderBy(g => g.Rank).ThenBy(g => g.DisplayRank).Select(g => new TagGapChoice($"{g.Name}({g.RankLabel})", g.Id)),
                .. _groups.Where(g => g.IsIndependent).Select(g => new TagGapChoice($"{g.Name}({g.RankLabel})", g.Id)),
            ];
            SelectedGroup = GroupChoices.FirstOrDefault(c => c.Id == group) ?? GroupChoices[0];

            // 上の順位と下の順位の両方のグループがあるものだけ(初めは 1 → 2)
            var ranks = _groups.Select(g => g.Rank).ToHashSet();
            var rank = SelectedRank?.Rank ?? 1;
            RankChoices = Enumerable.Range(1, TagGroup.MaxRank - 1)
                .Where(r => r == 1 || (ranks.Contains(r) && ranks.Contains(r + 1)))
                .Select(r => new TagGapChoice($"{TagRanks.Label(r)} → {TagRanks.Label(r + 1)}", Rank: r))
                .ToList();
            SelectedRank = RankChoices.FirstOrDefault(c => c.Rank == rank) ?? RankChoices[0];
            UpdateFilterChoices();
        }
        finally
        {
            _suspend = false;
        }
    }

    // 決定ログ BC-5: ①は順位 1 のタグ(このタグが付いているもの)、②③は調べる上の順位のタグ
    private void UpdateFilterChoices()
    {
        var wasSuspended = _suspend;
        _suspend = true;
        try
        {
            var rank = SelectedKind == TagGapKind.MissingGroup ? 1 : SelectedRank?.Rank ?? 1;
            var groupNames = _groups.ToDictionary(g => g.Id, g => g.Name);
            var previous = SelectedFilter?.Id;
            FilterLabel = $"{TagRanks.Label(rank)} のタグ";
            FilterChoices =
            [
                new("すべて"),
                .. _tags.Where(t => t.GroupId is { } g && _groups.Any(x => x.Id == g && x.Rank == rank))
                    .OrderBy(t => t.GroupId).ThenBy(t => t.Name, StringComparer.Ordinal)
                    .Select(t => new TagGapChoice($"{t.Name}({groupNames[t.GroupId!.Value]})", t.Id)),
            ];
            SelectedFilter = FilterChoices.FirstOrDefault(c => c.Id == previous) ?? FilterChoices[0];
        }
        finally
        {
            _suspend = wasSuspended;
        }
    }

    // 文書の情報を読み直して、今の条件で探し直す(開いたとき・保存したあと)
    private void Reload()
    {
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            _entries = DocumentSearchIndex.Load(connection).Search(new SearchQuery());
        }
        ApplyConditions();
    }

    private TagGapQuery QueryFor(TagGapKind kind, bool withFilter) => new(
        kind,
        kind == TagGapKind.MissingGroup ? SelectedGroup?.Id : null,
        SelectedRank?.Rank ?? 1,
        withFilter ? SelectedFilter?.Id : null);

    private void ApplyConditions()
    {
        IEnumerable<DocumentEntry> targets = _entries;
        if (SelectedTarget?.Id is not null)
        {
            var ids = _mainResultIds.ToHashSet();
            targets = targets.Where(e => ids.Contains(e.Id));
        }
        if (Keyword.Trim().Length > 0)
        {
            var terms = DocumentSearchIndex.Normalize(Keyword).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            targets = targets.Where(e => terms.All(t => DocumentSearchIndex.Normalize(e.Title + "\n" + e.FileName).Contains(t, StringComparison.Ordinal)));
        }
        var list = targets.ToList();
        var finder = new TagGapFinder(_groups, _tags);
        foreach (var kind in Kinds)
        {
            kind.Count = finder.Count(list, QueryFor(kind.Kind, withFilter: kind.IsSelected));
        }
        var gaps = finder.Find(list, QueryFor(SelectedKind, withFilter: true));
        var keep = _selection.Select(s => s.Detail.Id).ToHashSet();
        RowsReplacing?.Invoke();
        Rows.Clear();
        foreach (var gap in gaps)
        {
            var offline = !_storage.Storage.IsAvailable(gap.Entry.StorageId);
            var storageText = gap.Entry.StorageId is null ? "" : _storage.Storage.StorageName(gap.Entry.StorageId) + (offline ? "・未接続" : "");
            Rows.Add(new TagGapRow(gap.Entry, gap.Description, storageText, offline));
        }
        ResultText = $"{Rows.Count:N0} 件";
        RowsReplaced?.Invoke(Rows.Where(r => keep.Contains(r.Entry.Id)).ToList());
    }

    // 一覧を作り直す前と、作り直したあと(選び直す行を渡す。画面側で選択を戻す)
    public event Action? RowsReplacing;

    public event Action<IReadOnlyList<TagGapRow>>? RowsReplaced;

    public IReadOnlySet<string> SelectedIds => _selection.Select(s => s.Detail.Id).ToHashSet();

    // ==== 選択と編集 ====

    public void SetSelection(IReadOnlyList<TagGapRow> rows)
    {
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            _selection = new DocumentRepository(connection).GetDetails(rows.Select(r => r.Entry.Id).ToList())
                .Select(d => new DetailItem(d, _fields, Editor.TagKeyOf))
                .ToList();
        }
        Editor.SetTargets(_selection);
        HasChanges = false;
        SaveMessage = null;
        var offline = rows.FirstOrDefault(r => r.IsOffline);
        IsReadOnly = offline is not null;
        ReadOnlyMessage = offline is null ? null : $"保存先「{_storage.Storage.StorageName(offline.Entry.StorageId)}」がつながっていないため、表示のみです(保存できません)。";
        SelectionText = rows.Count switch
        {
            0 => "左の一覧から PDF を選んでください(Ctrl・Shift で複数選択)",
            1 => rows[0].Title,
            _ => $"{rows.Count} 件を選択中(付けたタグは選択中の全件に付きます)",
        };
        OnPropertyChanged(nameof(HasSelection));
        _ = LoadPreviewAsync(rows.FirstOrDefault());
    }

    // 1 ページ目を大きめに描く。サブがつながっていなければ一覧用のサムネイルを使う
    private async Task LoadPreviewAsync(TagGapRow? row)
    {
        var version = ++_previewVersion;
        PreviewImage = null;
        if (row is null)
        {
            return;
        }
        var id = row.Entry.Id;
        var path = _dataFolder.GetThumbnailPath(id);
        if (_storage.Storage.GetDataFolder(row.Entry.StorageId) is { } folder && File.Exists(folder.GetPdfPath(id)))
        {
            var preview = Path.Combine(_previewFolder, id + ".jpg");
            try
            {
                if (!File.Exists(preview))
                {
                    await Task.Run(() => PdfPageRenderer.RenderFirstPageToJpegAsync(folder.GetPdfPath(id), preview, ImportService.PreviewWidth));
                }
                path = preview;
            }
            catch (Exception)
            {
                // パスワード付きなどで描けなければ、一覧用のサムネイルを使う
            }
        }
        if (version == _previewVersion && File.Exists(path))
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            PreviewImage = image;
        }
    }

    private bool CanSave() => HasChanges && !IsReadOnly && _selection.Count > 0;

    // 決定ログ BC-7: 選んでいる PDF を保存し、当てはまらなくなったものは一覧から消して次の PDF を選ぶ
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync() => await SaveChangesAsync();

    public async Task<bool> SaveChangesAsync()
    {
        var count = _selection.Count;
        if (!await DetailSaver.SaveAsync(_dataFolder, _storage, _dialogs, Editor, _selection))
        {
            return false;
        }
        HasChanges = false;
        Saved?.Invoke();
        LoadCatalog();
        Reload();
        SaveMessage = count == 1 ? "保存しました。" : $"{count} 件を保存しました。";
        return true;
    }

    // 保存したあと、画面側で次の PDF を選ぶ
    public event Action? Saved;

    // 保存していない変更を、選択を変える前・閉じる前に確かめる(決定ログ J-1)。false なら操作をやめる
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
                HasChanges = false;
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_previewFolder, recursive: true);
        }
        catch (IOException)
        {
            // 一時フォルダなので、消せなくても Windows の一時ファイル整理で消える
        }
    }
}
