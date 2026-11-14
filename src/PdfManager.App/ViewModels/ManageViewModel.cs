using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Library;
using PdfManager.Core.Search;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.App.ViewModels;

public sealed record FieldTypeChoice(FieldType Type, string Label);

public sealed record RankChoice(int Rank, string Label);

// タグの所属先の選択肢。Id が null は自由タグ
public sealed record GroupChoice(long? Id, string Label);

// 同じグループの中の親のタグの選択肢(決定ログ EC-5)。Id が null は親なし
public sealed record ParentTagChoice(long? Id, string Label)
{
    public static ParentTagChoice None { get; } = new(null, "(なし)");
}

// 配下のタグの欄の 1 行(決定ログ HC-3)。Detail はグループ名と PDF の件数
public sealed record ChildTagRow(long Id, string Name, string Detail);

// タグの紐づけ先の候補 1 つ分(決定ログ Q-3)。チェックはすぐに保存する
public sealed partial class ParentChoice : ObservableObject
{
    private readonly Action _onToggled;
    private readonly bool _loading;

    public ParentChoice(long tagId, string name, string groupName, bool isChecked, Action onToggled)
    {
        TagId = tagId;
        Name = name;
        GroupName = groupName;
        _onToggled = onToggled;
        _loading = true;
        IsChecked = isChecked;
        _loading = false;
    }

    public long TagId { get; }

    public string Name { get; }

    public string GroupName { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_loading)
        {
            _onToggled();
        }
    }
}

// SC-04 項目・タグ管理(決定ログ R-11、Q-1〜Q-14)
public partial class ManageViewModel : ObservableObject
{
    private static readonly GroupChoice AllGroups = new(-1, "すべてのグループ");
    private static readonly GroupChoice FreeTags = new(null, "自由タグ");

    private readonly DataFolder _dataFolder;
    private readonly IUserDialogs _dialogs;
    private readonly Func<string?, bool> _isStorageAvailable;
    private IReadOnlyList<TagSummary> _allTags = [];

    private readonly Action<bool>? _saveChildPanelExpanded;

    // isStorageAvailable:保存先(null = メイン)がつながっているか(親のタグをまとめて付けるとき、決定ログ EC-6)。
    // childPanelExpanded・saveChildPanelExpanded:配下のタグの欄を開いているか(PC ごとに覚える、決定ログ HC-7)
    public ManageViewModel(DataFolder dataFolder, IUserDialogs dialogs, Func<string?, bool>? isStorageAvailable = null,
        bool childPanelExpanded = true, Action<bool>? saveChildPanelExpanded = null)
    {
        _dataFolder = dataFolder;
        _dialogs = dialogs;
        _isStorageAvailable = isStorageAvailable ?? (s => s is null);
        IsChildPanelExpanded = childPanelExpanded;
        _saveChildPanelExpanded = saveChildPanelExpanded;
        NewFieldType = FieldTypeChoices[0];
        NewGroupRank = RankChoices[0];
        TagGroupFilter = AllGroups;
        NewTagGroup = FreeTags;
        ReloadAll();
        ReloadFields();
    }

    // 補助項目の型(選択肢型はタググループに変わったため作らない、決定ログ Q-6)
    public IReadOnlyList<FieldTypeChoice> FieldTypeChoices { get; } = FieldTypes.All
        .Where(t => t != FieldType.Select)
        .Select(t => new FieldTypeChoice(t, FieldTypes.DisplayName(t)))
        .ToList();

    public IReadOnlyList<RankChoice> RankChoices { get; } = TagRanks.All.Select(r => new RankChoice(r, TagRanks.Label(r))).ToList();

    [ObservableProperty]
    public partial IReadOnlyList<TagGroup> Groups { get; set; } = [];

    // タグの所属先(自由タグ + 各グループ)
    [ObservableProperty]
    public partial IReadOnlyList<GroupChoice> GroupChoices { get; set; } = [];

    // ==== 項目(左に常に表示するタググループ、決定ログ Q-14) ====

    [ObservableProperty]
    public partial IReadOnlyList<TagGroup> Items { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsItemSelected))]
    public partial TagGroup? SelectedItem { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<TagGroup> ItemCandidates { get; set; } = [];

    [ObservableProperty]
    public partial TagGroup? NewItemGroup { get; set; }

    public bool IsItemSelected => SelectedItem is not null;

    [RelayCommand]
    private async Task AddItemAsync()
    {
        if (NewItemGroup is not { } group)
        {
            await _dialogs.ShowErrorAsync("項目にするタググループを選んでください(タググループは「タググループ」タブで作れます)。");
            return;
        }
        if (await RunAsync(c => c.SetGroupPinned(group.Id, true)))
        {
            ReloadAll(selectItemId: group.Id);
        }
    }

    [RelayCommand]
    private async Task RemoveItemAsync()
    {
        if (SelectedItem is { } item && await RunAsync(c => c.SetGroupPinned(item.Id, false)))
        {
            ReloadAll();
        }
    }

    [RelayCommand]
    private Task MoveItemUpAsync() => MoveItemAsync(-1);

    [RelayCommand]
    private Task MoveItemDownAsync() => MoveItemAsync(1);

    private async Task MoveItemAsync(int offset)
    {
        if (SelectedItem is { } item && await RunAsync(c => c.MovePinnedGroup(item.Id, offset)))
        {
            ReloadAll(selectItemId: item.Id);
        }
    }

    // ==== タググループ(決定ログ Q-1、Q-2、Q-13) ====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroupSelected))]
    public partial TagGroup? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial string NewGroupName { get; set; } = "";

    [ObservableProperty]
    public partial RankChoice NewGroupRank { get; set; }

    [ObservableProperty]
    public partial string EditGroupName { get; set; } = "";

    [ObservableProperty]
    public partial RankChoice? EditGroupRank { get; set; }

    [ObservableProperty]
    public partial string GroupTagsText { get; set; } = "";

    public bool IsGroupSelected => SelectedGroup is not null;

    partial void OnSelectedGroupChanged(TagGroup? value)
    {
        EditGroupName = value?.Name ?? "";
        EditGroupRank = value is null ? null : RankChoices.First(r => r.Rank == value.Rank);
        var names = value is null ? [] : _allTags.Where(t => t.GroupId == value.Id).Select(t => t.Name).ToList();
        GroupTagsText = value is null ? "" : names.Count == 0 ? "(タグはまだありません)" : string.Join("、", names);
    }

    [RelayCommand]
    private async Task AddGroupAsync()
    {
        long id = 0;
        if (await RunAsync(c => id = c.AddGroup(NewGroupName, NewGroupRank.Rank)))
        {
            NewGroupName = "";
            ReloadAll(selectGroupId: id);
        }
    }

    [RelayCommand]
    private async Task RenameGroupAsync()
    {
        if (SelectedGroup is { } group && await RunAsync(c => c.RenameGroup(group.Id, EditGroupName)))
        {
            ReloadAll(selectGroupId: group.Id);
        }
    }

    [RelayCommand]
    private async Task ChangeGroupRankAsync()
    {
        if (SelectedGroup is not { } group || EditGroupRank is not { } rank || rank.Rank == group.Rank)
        {
            return;
        }
        long broken = 0;
        if (!await RunAsync(c => broken = c.CountLinksBrokenByRank(group.Id, rank.Rank))
            || (broken > 0 && !await _dialogs.ConfirmAsync("順位の変更",
                $"「{group.Name}」を「{rank.Label}」にすると、合わなくなる紐づけ {broken} 件が外れます。変更しますか?", "変更する")))
        {
            return;
        }
        if (await RunAsync(c => c.ChangeGroupRank(group.Id, rank.Rank)))
        {
            ReloadAll(selectGroupId: group.Id);
        }
    }

    [RelayCommand]
    private async Task DeleteGroupAsync()
    {
        if (SelectedGroup is not { } group)
        {
            return;
        }
        var impact = group.TagCount > 0 ? $"\nこのグループのタグ {group.TagCount} 件は自由タグになります(文書のタグはそのまま)。紐づけは外れます。" : "";
        if (await _dialogs.ConfirmAsync("タググループの削除", $"タググループ「{group.Name}」を削除しますか?{impact}", "削除する")
            && await RunAsync(c => c.DeleteGroup(group.Id)))
        {
            ReloadAll();
        }
    }

    [RelayCommand]
    private Task MoveGroupUpAsync() => MoveGroupAsync(-1);

    [RelayCommand]
    private Task MoveGroupDownAsync() => MoveGroupAsync(1);

    private async Task MoveGroupAsync(int offset)
    {
        if (SelectedGroup is { } group && await RunAsync(c => c.MoveGroup(group.Id, offset)))
        {
            ReloadAll(selectGroupId: group.Id);
        }
    }

    // ==== タグ ====

    [ObservableProperty]
    public partial IReadOnlyList<TagSummary> VisibleTags { get; set; } = [];

    [ObservableProperty]
    public partial string TagFilter { get; set; } = "";

    // タグ一覧の絞り込み(すべて/自由タグ/各グループ)
    [ObservableProperty]
    public partial IReadOnlyList<GroupChoice> TagGroupFilters { get; set; } = [];

    [ObservableProperty]
    public partial GroupChoice TagGroupFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTagSelected), nameof(IsParentTagDirty), nameof(IsGroupDirty), nameof(ChildTagsText), nameof(IsTagNameDirty), nameof(IsAliasDirty))]
    public partial TagSummary? SelectedTag { get; set; }

    [ObservableProperty]
    public partial string NewTagName { get; set; } = "";

    [ObservableProperty]
    public partial GroupChoice NewTagGroup { get; set; }

    [ObservableProperty]
    public partial string EditTagName { get; set; } = "";

    [ObservableProperty]
    public partial GroupChoice? EditTagGroup { get; set; }

    // 決定ログ DE-4: 別名(「、」区切り)
    [ObservableProperty]
    public partial string EditTagAliases { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<TagSummary> MergeTargets { get; set; } = [];

    [ObservableProperty]
    public partial TagSummary? MergeTarget { get; set; }

    [ObservableProperty]
    public partial int TagCount { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ParentChoice> ParentChoices { get; set; } = [];

    [ObservableProperty]
    public partial string ParentFilter { get; set; } = "";

    [ObservableProperty]
    public partial string ParentHint { get; set; } = "";

    // 選んだタグが紐づけを持てる(順位 2〜5 のグループにある)か
    [ObservableProperty]
    public partial bool CanHaveParents { get; set; }

    public bool IsTagSelected => SelectedTag is not null;

    [ObservableProperty]
    public partial bool ShowParentSection { get; set; } = true;

    // ==== 同じグループの中の親のタグ(決定ログ EC-5・EC-6) ====

    [ObservableProperty]
    public partial IReadOnlyList<ParentTagChoice> ParentTagChoices { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsParentTagDirty))]
    public partial ParentTagChoice? EditParentTag { get; set; }

    // 決定ログ FC-4: プルダウンで選んだだけで、まだ保存していない
    public bool IsParentTagDirty => SelectedTag is { } tag && EditParentTag is { } choice && choice.Id != tag.ParentTagId;

    public bool IsGroupDirty => SelectedTag is { } tag && EditTagGroup is { } group && group.Id != tag.GroupId;

    partial void OnEditTagGroupChanged(GroupChoice? value) => OnPropertyChanged(nameof(IsGroupDirty));

    // ==== 保存していない名前・別名(決定ログ HC-8) ====

    public bool IsTagNameDirty => SelectedTag is { } tag && NameNormalizer.Clean(EditTagName) != tag.Name;

    public bool IsAliasDirty => SelectedTag is { } tag && AliasKey(EditTagAliases) != AliasKey(tag.AliasText);

    public bool HasUnsavedTagEdits => IsTagNameDirty || IsAliasDirty;

    partial void OnEditTagNameChanged(string value) => OnPropertyChanged(nameof(IsTagNameDirty));

    partial void OnEditTagAliasesChanged(string value) => OnPropertyChanged(nameof(IsAliasDirty));

    private static string[] SplitAliases(string text) =>
        text.Split(['、', ',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string AliasKey(string text) => string.Join("、", SplitAliases(text).Select(NameNormalizer.Clean));

    // ほかのタグを選ぶ前に、保存していない名前・別名を覚えておく(読み直しのときは覚えない)
    private (TagSummary Tag, string Name, string Aliases, bool NameDirty, bool AliasDirty)? _pendingEdits;
    private bool _reloading;

    partial void OnSelectedTagChanging(TagSummary? oldValue, TagSummary? newValue)
    {
        _pendingEdits = !_reloading && oldValue is not null && newValue?.Id != oldValue.Id && HasUnsavedTagEdits
            ? (oldValue, EditTagName, EditTagAliases, IsTagNameDirty, IsAliasDirty)
            : null;
    }

    // 決定ログ HC-8: 保存していないままほかのタグを選んだら、保存するか聞く
    private async void AskSavePendingEdits()
    {
        if (_pendingEdits is not { } pending)
        {
            return;
        }
        _pendingEdits = null;
        var what = pending.NameDirty && pending.AliasDirty ? "名前と別名" : pending.NameDirty ? "名前" : "別名";
        if (!await _dialogs.ConfirmAsync("保存していない変更",
                $"タグ「{pending.Tag.Name}」の{what}の変更を保存していません。保存しますか?\n(「キャンセル」で変更を捨てます)", "保存する"))
        {
            return;
        }
        await SaveEditsAsync(pending.Tag, pending.NameDirty ? pending.Name : null, pending.AliasDirty ? pending.Aliases : null);
    }

    private async Task<bool> SaveEditsAsync(TagSummary tag, string? name, string? aliases)
    {
        var selected = SelectedTag?.Id;
        var ok = (name is null || await RunAsync(c => c.RenameTag(tag.Id, name)))
            && (aliases is null || await RunAsync(c => c.SetAliases(tag.Id, SplitAliases(aliases))));
        ReloadAll(selectTagId: selected);
        return ok;
    }

    // 決定ログ HC-8: 画面を閉じる前。保存していなければ聞く。閉じてよければ true
    public async Task<bool> ConfirmCloseAsync()
    {
        if (SelectedTag is not { } tag || !HasUnsavedTagEdits)
        {
            return true;
        }
        var what = IsTagNameDirty && IsAliasDirty ? "名前と別名" : IsTagNameDirty ? "名前" : "別名";
        switch (await _dialogs.AskChoiceAsync("保存していない変更", $"タグ「{tag.Name}」の{what}の変更を保存していません。保存しますか?", "保存する", "保存しない", "キャンセル"))
        {
            case 1:
                return await SaveEditsAsync(tag, IsTagNameDirty ? EditTagName : null, IsAliasDirty ? EditTagAliases : null);
            case 2:
                return true;
            default:
                return false;
        }
    }

    // ==== 配下のタグ(1 つ下の順位のタグ、決定ログ HC-1〜HC-7) ====

    // 選んだタグが順位 1〜4 のグループにある
    [ObservableProperty]
    public partial bool HasChildPanel { get; set; }

    [ObservableProperty]
    public partial string ChildPanelTitle { get; set; } = "";

    [ObservableProperty]
    public partial string ChildHint { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<ChildTagRow> ChildTags { get; set; } = [];

    // 1 つ下の順位のタググループ(入れる先)
    [ObservableProperty]
    public partial IReadOnlyList<GroupChoice> ChildGroupChoices { get; set; } = [];

    [ObservableProperty]
    public partial GroupChoice? NewChildGroup { get; set; }

    // グループが複数あるときだけ選ぶ欄を出す
    public bool HasManyChildGroups => ChildGroupChoices.Count > 1;

    public bool CanAddChild => ChildGroupChoices.Count > 0;

    [ObservableProperty]
    public partial string NewChildTagName { get; set; } = "";

    [ObservableProperty]
    public partial bool IsChildPanelExpanded { get; set; }

    partial void OnIsChildPanelExpandedChanged(bool value) => _saveChildPanelExpanded?.Invoke(value);

    partial void OnChildGroupChoicesChanged(IReadOnlyList<GroupChoice> value)
    {
        OnPropertyChanged(nameof(HasManyChildGroups));
        OnPropertyChanged(nameof(CanAddChild));
    }

    private void BuildChildPanel()
    {
        var rank = SelectedTag?.GroupId is { } groupId ? Groups.FirstOrDefault(g => g.Id == groupId)?.Rank : null;
        HasChildPanel = SelectedTag is not null && rank is >= 1 and < TagGroup.MaxRank;
        if (SelectedTag is not { } tag || !HasChildPanel)
        {
            ChildTags = [];
            ChildGroupChoices = [];
            NewChildGroup = null;
            return;
        }
        var childRank = rank!.Value + 1;
        var childGroups = Groups.Where(g => g.Rank == childRank).ToList();
        ChildPanelTitle = $"配下のタグ(「{tag.Name}」に紐づく{TagRanks.Label(childRank)}のタグ)";
        ChildTags = _allTags.Where(t => t.ParentIds.Contains(tag.Id))
            .OrderBy(t => t.GroupName, StringComparer.Ordinal).ThenBy(t => NameNormalizer.Normalize(t.Name), StringComparer.Ordinal)
            .Select(t => new ChildTagRow(t.Id, t.Name, $"{t.GroupLabel} · {t.UsageCount:N0} 件"))
            .ToList();
        var previous = NewChildGroup?.Id;
        ChildGroupChoices = childGroups.Select(g => new GroupChoice(g.Id, g.Name)).ToList();
        NewChildGroup = ChildGroupChoices.FirstOrDefault(c => c.Id == previous) ?? ChildGroupChoices.FirstOrDefault();
        ChildHint = childGroups.Count == 0
            ? $"「{TagRanks.Label(childRank)}」のタググループがありません。「タググループ」タブで作ると、ここで配下のタグを登録できます。"
            : (ChildTags.Count == 0 ? "配下のタグはまだありません。" : $"{ChildTags.Count:N0} 個。")
              + "下の欄にタグ名を入れて「追加」(または Enter)で 1 つずつ登録します。すでにあるタグ名なら、そのタグに紐づけを足します。「×」は紐づけを外すだけです(タグは消えません)。ダブルクリックでそのタグを開きます。";
    }

    [RelayCommand]
    private async Task AddChildTagAsync()
    {
        if (SelectedTag is not { } parent || NewChildGroup?.Id is not { } groupId || NameNormalizer.Clean(NewChildTagName).Length == 0)
        {
            return;
        }
        if (await RunAsync(c => c.AddChildTag(parent.Id, NewChildTagName, groupId)))
        {
            NewChildTagName = "";
            ReloadAll(selectTagId: parent.Id);
        }
    }

    [RelayCommand]
    private async Task RemoveChildTagAsync(ChildTagRow? row)
    {
        if (SelectedTag is { } parent && row is not null && await RunAsync(c => c.RemoveTagParent(row.Id, parent.Id)))
        {
            ReloadAll(selectTagId: parent.Id);
        }
    }

    // ダブルクリックで、そのタグを開く(一覧の絞り込みを外して選ぶ)
    public void OpenChildTag(ChildTagRow row)
    {
        TagFilter = "";
        TagGroupFilter = AllGroups;
        ApplyTagFilter(row.Id);
    }

    // 決定ログ FC-2: このタグの下にある小分類
    public string ChildTagsText => SelectedTag is not { } tag ? ""
        : tag.ChildTagNames.Count == 0 ? "このタグの小分類はありません。"
        : $"このタグの小分類:{string.Join("、", tag.ChildTagNames)}";

    // 選んだタグの親にできるもの:同じグループのタグのうち、自分と自分の子孫以外(「ウィスキー › スコッチ」の形で出す)
    private void BuildParentTagChoices()
    {
        if (SelectedTag is not { } tag)
        {
            ParentTagChoices = [];
            EditParentTag = null;
            return;
        }
        var parents = _allTags.ToDictionary(t => t.Id, t => t.ParentTagId);
        var names = _allTags.ToDictionary(t => t.Id, t => t.Name);
        var excluded = TagTree.Descendants(tag.Id, parents).Append(tag.Id).ToHashSet();
        ParentTagChoices =
        [
            ParentTagChoice.None,
            .. _allTags.Where(t => t.GroupId == tag.GroupId && !excluded.Contains(t.Id))
                .Select(t => new ParentTagChoice(t.Id, TagTree.Path(TagTree.Ancestors(t.Id, parents).Select(id => names[id]), t.Name)))
                .OrderBy(c => c.Label, StringComparer.Ordinal),
        ];
        EditParentTag = ParentTagChoices.FirstOrDefault(c => c.Id == tag.ParentTagId) ?? ParentTagChoice.None;
    }

    [RelayCommand]
    private async Task ChangeTagParentAsync()
    {
        if (SelectedTag is not { } tag || EditParentTag is not { } choice || choice.Id == tag.ParentTagId)
        {
            return;
        }
        if (!await RunAsync(c => c.SetTagParentTag(tag.Id, choice.Id)))
        {
            return;
        }
        // 決定ログ EC-6: このタグが付いているのに親が付いていない PDF があれば、確かめてから親も付ける
        if (choice.Id is not null)
        {
            await AddMissingAncestorsAsync(tag, choice.Label);
        }
        ReloadAll(selectTagId: tag.Id);
    }

    private async Task AddMissingAncestorsAsync(TagSummary tag, string parentLabel)
    {
        IReadOnlyList<string> missing;
        IReadOnlyDictionary<string, string?> storages;
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            missing = new CatalogRepository(connection).DocumentsMissingAncestors(tag.Id);
            storages = new SubLibraryRepository(connection).GetStorageIds(missing);
        }
        if (missing.Count == 0)
        {
            return;
        }
        // つながっていないサブの PDF は直せない(決定ログ S-12)
        var available = missing.Where(id => _isStorageAvailable(storages.GetValueOrDefault(id))).ToList();
        var skipped = missing.Count - available.Count;
        var skippedText = skipped > 0 ? $"\n(つながっていないサブライブラリにある {skipped:N0} 件は、今は付けられません)" : "";
        if (available.Count == 0)
        {
            await _dialogs.ShowErrorAsync($"「{tag.Name}」が付いていて、大分類が付いていない PDF が {missing.Count:N0} 件ありますが、すべてつながっていないサブライブラリにあるため、今は付けられません。", "大分類");
            return;
        }
        if (await _dialogs.ConfirmAsync("大分類",
                $"「{tag.Name}」(またはその小分類)が付いている PDF のうち {available.Count:N0} 件には、大分類「{parentLabel}」が付いていません。付けますか?{skippedText}", "付ける"))
        {
            await RunAsync(c => c.AddMissingAncestors(available, DateTimeOffset.UtcNow));
        }
    }

    partial void OnTagFilterChanged(string value) => ApplyTagFilter(SelectedTag?.Id);

    partial void OnTagGroupFilterChanged(GroupChoice value) => ApplyTagFilter(SelectedTag?.Id);

    partial void OnParentFilterChanged(string value) => BuildParentChoices();

    partial void OnSelectedTagChanged(TagSummary? value)
    {
        EditTagName = value?.Name ?? "";
        EditTagAliases = value?.AliasText ?? "";
        EditTagGroup = value is null ? null : GroupChoices.FirstOrDefault(c => c.Id == value.GroupId);
        MergeTargets = value is null ? [] : _allTags.Where(t => t.Id != value.Id).ToList();
        MergeTarget = null;
        ParentFilter = "";
        NewChildTagName = "";
        BuildParentChoices();
        BuildParentTagChoices();
        BuildChildPanel();
        AskSavePendingEdits();
    }

    // 決定ログ Q-3: 紐づけられるのは、1 つ上の順位のタググループのタグ
    private void BuildParentChoices()
    {
        var rank = SelectedTag?.GroupId is { } groupId ? Groups.FirstOrDefault(g => g.Id == groupId)?.Rank : null;
        CanHaveParents = SelectedTag is not null && rank is >= 2;
        // 決定ログ HC-1: 順位 1 のタグは紐づけを持てないので、紐づけの欄の代わりに配下のタグの欄だけを出す
        ShowParentSection = rank is not 1;
        if (SelectedTag is not { } tag || rank is not >= 2)
        {
            ParentChoices = [];
            ParentHint = SelectedTag is null ? "" : "紐づけは、順位 2〜5 のタググループのタグだけが持てます(1 つ上の順位のタグに紐づけます)。";
            return;
        }
        var parentGroups = Groups.Where(g => g.Rank == rank - 1).ToDictionary(g => g.Id, g => g.Name);
        var filter = NameNormalizer.Normalize(ParentFilter);
        ParentChoices = _allTags
            .Where(t => t.GroupId is { } g && parentGroups.ContainsKey(g))
            .Where(t => tag.ParentIds.Contains(t.Id) || filter.Length == 0 || NameNormalizer.Normalize(t.Name).Contains(filter, StringComparison.Ordinal))
            .OrderByDescending(t => tag.ParentIds.Contains(t.Id))
            .Select(t => new ParentChoice(t.Id, t.Name, parentGroups[t.GroupId!.Value], tag.ParentIds.Contains(t.Id), OnParentToggled))
            .ToList();
        ParentHint = parentGroups.Count == 0
            ? $"「{TagRanks.Label(rank.Value - 1)}」のタググループがありません。「タググループ」タブで作ってください。"
            : $"「{TagRanks.Label(rank.Value - 1)}」のタグから選びます(複数可)。チェックはすぐに保存されます。";
    }

    private async void OnParentToggled()
    {
        if (SelectedTag is not { } tag)
        {
            return;
        }
        var parentIds = ParentChoices.Where(c => c.IsChecked).Select(c => c.TagId)
            // 絞り込みで見えていない紐づけ先は残す
            .Concat(tag.ParentIds.Where(id => ParentChoices.All(c => c.TagId != id)))
            .ToList();
        if (await RunAsync(c => c.SetTagParents(tag.Id, parentIds)))
        {
            ReloadAll(selectTagId: tag.Id, keepParentFilter: true);
        }
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        // 決定ログ DE-3: ひらがな・カタカナだけが違うタグが同じグループにあれば確かめる
        var folded = DocumentSearchIndex.Normalize(NewTagName);
        if (folded.Length > 0 && NameNormalizer.Normalize(NewTagName) is var key
            && _allTags.FirstOrDefault(t => t.GroupId == NewTagGroup.Id && NameNormalizer.Normalize(t.Name) != key
                && (DocumentSearchIndex.Normalize(t.Name) == folded || t.AliasNames.Any(a => DocumentSearchIndex.Normalize(a) == folded))) is { } similar
            && !await _dialogs.ConfirmAsync("似た名前のタグがあります",
                $"「{similar.Name}」があります(ひらがな・カタカナの違いだけです)。それでも「{NameNormalizer.Clean(NewTagName)}」を作りますか?\n同じ意味なら、「{similar.Name}」の別名に入れることもできます。", "作る"))
        {
            return;
        }
        long id = 0;
        if (await RunAsync(c => id = c.AddTag(NewTagName, NewTagGroup.Id)))
        {
            NewTagName = "";
            TagFilter = "";
            ReloadAll(selectTagId: id);
        }
    }

    [RelayCommand]
    private async Task RenameTagAsync()
    {
        if (SelectedTag is { } tag && await RunAsync(c => c.RenameTag(tag.Id, EditTagName)))
        {
            ReloadAll(selectTagId: tag.Id);
        }
    }

    // 決定ログ DE-4: 別名を保存する(「、」やカンマで区切る)
    [RelayCommand]
    private async Task SaveTagAliasesAsync()
    {
        var names = EditTagAliases.Split(['、', ',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (SelectedTag is { } tag && await RunAsync(c => c.SetAliases(tag.Id, names)))
        {
            ReloadAll(selectTagId: tag.Id);
        }
    }

    [RelayCommand]
    private async Task ChangeTagGroupAsync()
    {
        if (SelectedTag is not { } tag || EditTagGroup is not { } group || group.Id == tag.GroupId)
        {
            return;
        }
        long broken = 0;
        if (!await RunAsync(c => broken = c.CountLinksBrokenByTagGroup(tag.Id, group.Id))
            || (broken > 0 && !await _dialogs.ConfirmAsync("グループの変更",
                $"「{tag.Name}」を「{group.Label}」に移すと、合わなくなる紐づけ {broken} 件が外れます。移しますか?", "移す")))
        {
            return;
        }
        if (await RunAsync(c => c.SetTagGroup(tag.Id, group.Id)))
        {
            ReloadAll(selectTagId: tag.Id);
        }
    }

    [RelayCommand]
    private async Task MergeTagAsync()
    {
        if (SelectedTag is not { } source)
        {
            return;
        }
        if (MergeTarget is not { } target)
        {
            await _dialogs.ShowErrorAsync("統合先のタグを選んでください。");
            return;
        }
        if (await _dialogs.ConfirmAsync("タグの統合",
                $"「{source.Name}」を「{target.Name}」に統合しますか?\n「{source.Name}」が付いた文書 {source.UsageCount} 件には「{target.Name}」が付き、「{source.Name}」は削除されます。紐づけも引き継ぎます。\n「{source.Name}」は「{target.Name}」の別名として残るので、その名前で検索・入力しても「{target.Name}」が見つかります。",
                "統合する")
            && await RunAsync(c => c.MergeTags(source.Id, target.Id)))
        {
            ReloadAll(selectTagId: target.Id);
        }
    }

    [RelayCommand]
    private async Task DeleteTagAsync()
    {
        if (SelectedTag is not { } tag)
        {
            return;
        }
        var impact = tag.UsageCount > 0 ? $"\nこのタグが付いた文書 {tag.UsageCount} 件から、タグが外れます(文書は削除されません)。" : "";
        if (await _dialogs.ConfirmAsync("タグの削除", $"タグ「{tag.Name}」を削除しますか?{impact}", "削除する")
            && await RunAsync(c => c.DeleteTag(tag.Id)))
        {
            ReloadAll();
        }
    }

    // ==== 補助項目(年月・日付・数値・文字、決定ログ Q-6) ====

    [ObservableProperty]
    public partial IReadOnlyList<FieldDefinition> Fields { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFieldSelected), nameof(CanChangeFieldType))]
    public partial FieldDefinition? SelectedField { get; set; }

    [ObservableProperty]
    public partial string NewFieldName { get; set; } = "";

    [ObservableProperty]
    public partial FieldTypeChoice NewFieldType { get; set; }

    [ObservableProperty]
    public partial string EditFieldName { get; set; } = "";

    [ObservableProperty]
    public partial FieldTypeChoice? EditFieldType { get; set; }

    public bool IsFieldSelected => SelectedField is not null;

    public bool CanChangeFieldType => SelectedField is { UsageCount: 0 };

    partial void OnSelectedFieldChanged(FieldDefinition? value)
    {
        EditFieldName = value?.Name ?? "";
        EditFieldType = value is null ? null : FieldTypeChoices.FirstOrDefault(c => c.Type == value.Type);
    }

    [RelayCommand]
    private async Task AddFieldAsync()
    {
        long id = 0;
        if (await RunAsync(c => id = c.AddField(NewFieldName, NewFieldType.Type)))
        {
            NewFieldName = "";
            ReloadFields(id);
        }
    }

    [RelayCommand]
    private async Task RenameFieldAsync()
    {
        if (SelectedField is { } field && await RunAsync(c => c.RenameField(field.Id, EditFieldName)))
        {
            ReloadFields(field.Id);
        }
    }

    [RelayCommand]
    private async Task ChangeFieldTypeAsync()
    {
        if (SelectedField is { } field && EditFieldType is { } type && type.Type != field.Type
            && await RunAsync(c => c.ChangeFieldType(field.Id, type.Type)))
        {
            ReloadFields(field.Id);
        }
    }

    [RelayCommand]
    private async Task DeleteFieldAsync()
    {
        if (SelectedField is not { } field)
        {
            return;
        }
        var impact = field.UsageCount > 0 ? $"\n値が入っている文書 {field.UsageCount} 件から、この補助項目の値も削除されます。" : "";
        if (await _dialogs.ConfirmAsync("補助項目の削除", $"補助項目「{field.Name}」を削除しますか?{impact}", "削除する")
            && await RunAsync(c => c.DeleteField(field.Id)))
        {
            ReloadFields();
        }
    }

    [RelayCommand]
    private Task MoveFieldUpAsync() => MoveFieldAsync(-1);

    [RelayCommand]
    private Task MoveFieldDownAsync() => MoveFieldAsync(1);

    private async Task MoveFieldAsync(int offset)
    {
        if (SelectedField is { } field && await RunAsync(c => c.MoveField(field.Id, offset)))
        {
            ReloadFields(field.Id);
        }
    }

    // ==== 共通 ====

    private async Task<bool> RunAsync(Action<CatalogRepository> action)
    {
        try
        {
            using var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath);
            action(new CatalogRepository(connection));
            return true;
        }
        catch (CatalogException ex)
        {
            await _dialogs.ShowErrorAsync(ex.Message);
            return false;
        }
    }

    private void ReloadFields(long? selectFieldId = null)
    {
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            Fields = new CatalogRepository(connection).GetFields().Where(f => f.Type != FieldType.Select).ToList();
        }
        SelectedField = Fields.FirstOrDefault(f => f.Id == selectFieldId);
    }

    // タググループ・項目・タグはお互いに関わるため、まとめて読み直す
    private void ReloadAll(long? selectItemId = null, long? selectGroupId = null, long? selectTagId = null, bool keepParentFilter = false)
    {
        var parentFilter = ParentFilter;
        selectItemId ??= SelectedItem?.Id;
        selectGroupId ??= SelectedGroup?.Id;
        selectTagId ??= SelectedTag?.Id;
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            var catalog = new CatalogRepository(connection);
            Groups = catalog.GetGroups();
            _allTags = catalog.GetTags();
        }
        Items = Groups.Where(g => g.IsPinned).OrderBy(g => g.PinOrder).ThenBy(g => g.Id).ToList();
        ItemCandidates = Groups.Where(g => !g.IsPinned).ToList();
        NewItemGroup = ItemCandidates.FirstOrDefault();
        SelectedItem = Items.FirstOrDefault(g => g.Id == selectItemId);

        GroupChoices = [FreeTags, .. Groups.Select(g => new GroupChoice(g.Id, $"{g.Name}({g.RankLabel})"))];
        TagGroupFilters = [AllGroups, .. GroupChoices];
        TagGroupFilter = TagGroupFilters.FirstOrDefault(c => c.Id == TagGroupFilter?.Id) ?? AllGroups;
        NewTagGroup = GroupChoices.FirstOrDefault(c => c.Id == NewTagGroup?.Id) ?? FreeTags;
        SelectedGroup = Groups.FirstOrDefault(g => g.Id == selectGroupId);

        TagCount = _allTags.Count;
        _reloading = true;
        try
        {
            ApplyTagFilter(selectTagId);
        }
        finally
        {
            _reloading = false;
        }
        if (keepParentFilter)
        {
            ParentFilter = parentFilter;
        }
    }

    private void ApplyTagFilter(long? selectTagId)
    {
        var filter = NameNormalizer.Normalize(TagFilter);
        var matched = _allTags
            .Where(t => TagGroupFilter is null || TagGroupFilter == AllGroups || t.GroupId == TagGroupFilter.Id)
            .Where(t => filter.Length == 0 || NameNormalizer.Normalize(t.Name).Contains(filter, StringComparison.Ordinal)
                || t.AliasNames.Any(a => NameNormalizer.Normalize(a).Contains(filter, StringComparison.Ordinal)))
            .ToList();
        // 決定ログ FC-3: 小分類は大分類のすぐ下に字下げして並べる(同じ階層は名前の順)。名前で絞り込んでいるときは字下げせず道すじを出す
        if (filter.Length == 0)
        {
            var position = matched.Select((t, i) => (t.Id, i)).ToDictionary(x => x.Id, x => x.i);
            VisibleTags = TagTree.Order(matched, t => t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    t => t.ParentTagId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Comparer<TagSummary>.Create((a, b) => position[a.Id].CompareTo(position[b.Id])))
                .Select(x => x.Item with { Depth = x.Depth, Path = null })
                .ToList();
        }
        else
        {
            var parents = _allTags.ToDictionary(t => t.Id, t => t.ParentTagId);
            var names = _allTags.ToDictionary(t => t.Id, t => t.Name);
            VisibleTags = matched.Select(t => TagTree.Ancestors(t.Id, parents) is { Count: > 0 } ancestors
                    ? t with { Depth = 0, Path = TagTree.Path(ancestors.Select(id => names[id]), t.Name) }
                    : t with { Depth = 0, Path = null })
                .ToList();
        }
        SelectedTag = VisibleTags.FirstOrDefault(t => t.Id == selectTagId);
    }
}
