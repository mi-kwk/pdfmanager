using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Search;
using PdfManager.Core.Storage;

namespace PdfManager.App.ViewModels;

// 重複チェック画面の、第 1 グループのタグ 1 つ分(チェックしたタグの PDF の中で探す)
public sealed partial class DuplicateTagChoice(TagSummary tag) : ObservableObject
{
    public TagSummary Tag { get; } = tag;

    public string Name => Tag.Name;

    public string Detail => $"{Tag.GroupLabel} ・ {Tag.UsageCount:N0} 件";

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

// 1 ページ目が似ている文書の組の 1 件
public sealed partial class DuplicateCard(DuplicateMatch match) : ObservableObject
{
    public DuplicateMatch Match { get; } = match;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

public sealed partial class DuplicateGroupView(IReadOnlyList<DuplicateCard> cards, double similarity, Func<DuplicateGroupView, Task> compare,
    Func<DuplicateGroupView, Task> delete, Action<DuplicateGroupView> notDuplicate) : ObservableObject
{
    public ObservableCollection<DuplicateCard> Cards { get; } = new(cards);

    public string Header => $"1 ページ目が似ている {Cards.Count} 件({similarity * 100:0}% 同じ)";

    public void Refresh() => OnPropertyChanged(nameof(Header));

    [RelayCommand]
    private Task CompareAsync() => compare(this);

    [RelayCommand]
    private Task DeleteAsync() => delete(this);

    [RelayCommand]
    private void NotDuplicate() => notDuplicate(this);
}

// SC-17 重複チェック(決定ログ DH-6・DH-7)。第 1 グループのタグ(必須)の登録済み PDF の中で、1 ページ目が似ているものを組にして並べる
public sealed partial class DuplicateCheckViewModel : ObservableObject
{
    private readonly DataFolder _dataFolder;
    private readonly IUserDialogs _dialogs;
    private readonly bool _loose;
    private readonly Func<(string Id, string Title, string FileName), (string Id, string Title, string FileName), Task> _compare;
    private readonly Func<IReadOnlyList<string>, Task<bool>> _delete;
    private List<DuplicateTagChoice> _allTags = [];

    public DuplicateCheckViewModel(DataFolder dataFolder, IUserDialogs dialogs, bool loose,
        Func<(string Id, string Title, string FileName), (string Id, string Title, string FileName), Task> compare,
        Func<IReadOnlyList<string>, Task<bool>> delete)
    {
        _dataFolder = dataFolder;
        _dialogs = dialogs;
        _loose = loose;
        _compare = compare;
        _delete = delete;
        using var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath);
        var catalog = new CatalogRepository(connection);
        var firstRank = catalog.GetGroups().Where(g => g.Rank == 1).Select(g => g.Id).ToHashSet();
        _allTags = catalog.GetTags().Where(t => t.GroupId is { } g && firstRank.Contains(g)).Select(t => new DuplicateTagChoice(t)).ToList();
        foreach (var tag in _allTags)
        {
            tag.PropertyChanged += (_, _) => FindCommand.NotifyCanExecuteChanged();
        }
        ApplyTagFilter();
        Status = _allTags.Count == 0
            ? "第 1 グループ(順位 1)のタグがありません。「項目・タグ」でタググループの順位を 1 にしてください。"
            : "左で第 1 グループのタグを選んで(複数可)、「重複を探す」を押してください。";
    }

    [ObservableProperty]
    public partial IReadOnlyList<DuplicateTagChoice> Tags { get; set; } = [];

    [ObservableProperty]
    public partial string TagFilter { get; set; } = "";

    partial void OnTagFilterChanged(string value) => ApplyTagFilter();

    private void ApplyTagFilter()
    {
        var filter = DocumentSearchIndex.Normalize(TagFilter.Trim());
        Tags = _allTags.Where(t => t.IsChecked || filter.Length == 0 || DocumentSearchIndex.Normalize(t.Name).Contains(filter, StringComparison.Ordinal)).ToList();
    }

    public ObservableCollection<DuplicateGroupView> Groups { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand))]
    public partial bool IsBusy { get; set; }

    private bool CanFind() => !IsBusy && _allTags.Any(t => t.IsChecked);

    [RelayCommand(CanExecute = nameof(CanFind))]
    private async Task FindAsync()
    {
        IsBusy = true;
        Groups.Clear();
        try
        {
            Status = "登録済みの PDF を調べています…";
            await DuplicateLookup.PrepareAsync(_dataFolder, new Progress<(int Done, int Total)>(p =>
            {
                if (p.Total > 20)
                {
                    Status = $"はじめに、登録済みの PDF の 1 ページ目を調べています({p.Done:N0} / {p.Total:N0} 件)…";
                }
            }));
            var tagIds = _allTags.Where(t => t.IsChecked).Select(t => t.Tag.Id).ToList();
            Status = "1 ページ目を比べています…";
            var (groups, count) = await Task.Run(() =>
            {
                using var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath);
                var ids = DuplicateLookup.DocumentsWithTags(connection, tagIds);
                var found = DuplicateFinder.FindGroups(connection, ids, DuplicateFinder.Threshold(_loose));
                // 組ごとに、似ている度合いの順に並べる(先頭を基準に)
                return (found.Select(g => (Matches: DuplicateLookup.Describe(_dataFolder, connection,
                    g.DocumentIds.Select(id => new SimilarDocument(id, g.Similarity)).ToList()), g.Similarity)).ToList(), ids.Count);
            });
            foreach (var (matches, similarity) in groups)
            {
                Groups.Add(new DuplicateGroupView(matches.OrderBy(m => m.CreatedAt).Select(m => new DuplicateCard(m)).ToList(), similarity,
                    CompareAsync, DeleteAsync, MarkNotDuplicate));
            }
            Status = Groups.Count == 0
                ? $"選んだタグの {count:N0} 件の中に、1 ページ目が似ている PDF はありませんでした。"
                : $"選んだタグの {count:N0} 件の中に、1 ページ目が似ている組が {Groups.Count} 組あります。見比べて、いらないものを選んで削除するか、「重複ではない」を押してください。";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = $"調べられませんでした: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 組の中の 2 件をビューアで並べる(2 件の組ならその 2 件、それより多ければ選んだ 2 件)
    private async Task CompareAsync(DuplicateGroupView group)
    {
        var cards = group.Cards.Count == 2 ? group.Cards.ToList() : group.Cards.Where(c => c.IsChecked).ToList();
        if (cards.Count != 2)
        {
            await _dialogs.ShowErrorAsync("比べる 2 件にチェックを付けてください。", "ビューアで比べる");
            return;
        }
        await _compare((cards[0].Match.DocumentId, cards[0].Match.Title, cards[0].Match.FileName),
            (cards[1].Match.DocumentId, cards[1].Match.Title, cards[1].Match.FileName));
    }

    private async Task DeleteAsync(DuplicateGroupView group)
    {
        var cards = group.Cards.Where(c => c.IsChecked).ToList();
        if (cards.Count == 0)
        {
            await _dialogs.ShowErrorAsync("削除するものにチェックを付けてください。", "削除");
            return;
        }
        if (cards.Count == group.Cards.Count && !await _dialogs.ConfirmAsync("削除", "組のすべてにチェックが付いています。すべて削除すると、どれも残りません。続けますか?", "続ける"))
        {
            return;
        }
        if (!await _delete(cards.Select(c => c.Match.DocumentId).ToList()))
        {
            return;
        }
        foreach (var card in cards)
        {
            group.Cards.Remove(card);
        }
        if (group.Cards.Count < 2)
        {
            Groups.Remove(group);
        }
        group.Refresh();
    }

    // 決定ログ DH-7: 次からこの組を出さない
    private void MarkNotDuplicate(DuplicateGroupView group)
    {
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            DuplicateFinder.MarkNotDuplicate(connection, group.Cards.Select(c => c.Match.DocumentId).ToList());
        }
        Groups.Remove(group);
        Status = $"「重複ではない」として覚えました(次からは出しません)。残り {Groups.Count} 組です。";
    }
}
