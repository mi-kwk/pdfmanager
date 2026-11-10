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

    // isStorageAvailable:保存先(null = メイン)がつながっているか(親のタグをまとめて付けるとき、決定ログ EC-6)
    public ManageViewModel(DataFolder dataFolder, IUserDialogs dialogs, Func<string?, bool>? isStorageAvailable = null)
    {
        _dataFolder = dataFolder;
        _dialogs = dialogs;
        _isStorageAvailable = isStorageAvailable ?? (s => s is null);
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
    [NotifyPropertyChangedFor(nameof(IsTagSelected), nameof(IsParentTagDirty), nameof(IsGroupDirty), nameof(ChildTagsText))]
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
        BuildParentChoices();
        BuildParentTagChoices();
    }

    // 決定ログ Q-3: 紐づけられるのは、1 つ上の順位のタググループのタグ
    private void BuildParentChoices()
    {
        var rank = SelectedTag?.GroupId is { } groupId ? Groups.FirstOrDefault(g => g.Id == groupId)?.Rank : null;
        CanHaveParents = SelectedTag is not null && rank is >= 2;
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
        ApplyTagFilter(selectTagId);
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
