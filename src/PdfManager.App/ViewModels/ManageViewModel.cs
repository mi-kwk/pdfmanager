using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.App.ViewModels;

public sealed record FieldTypeChoice(FieldType Type, string Label);

public sealed record RankChoice(int Rank, string Label);

// タグの所属先の選択肢。Id が null は自由タグ
public sealed record GroupChoice(long? Id, string Label);

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
    private IReadOnlyList<TagSummary> _allTags = [];

    public ManageViewModel(DataFolder dataFolder, IUserDialogs dialogs)
    {
        _dataFolder = dataFolder;
        _dialogs = dialogs;
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
    [NotifyPropertyChangedFor(nameof(IsTagSelected))]
    public partial TagSummary? SelectedTag { get; set; }

    [ObservableProperty]
    public partial string NewTagName { get; set; } = "";

    [ObservableProperty]
    public partial GroupChoice NewTagGroup { get; set; }

    [ObservableProperty]
    public partial string EditTagName { get; set; } = "";

    [ObservableProperty]
    public partial GroupChoice? EditTagGroup { get; set; }

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

    partial void OnTagFilterChanged(string value) => ApplyTagFilter(SelectedTag?.Id);

    partial void OnTagGroupFilterChanged(GroupChoice value) => ApplyTagFilter(SelectedTag?.Id);

    partial void OnParentFilterChanged(string value) => BuildParentChoices();

    partial void OnSelectedTagChanged(TagSummary? value)
    {
        EditTagName = value?.Name ?? "";
        EditTagGroup = value is null ? null : GroupChoices.FirstOrDefault(c => c.Id == value.GroupId);
        MergeTargets = value is null ? [] : _allTags.Where(t => t.Id != value.Id).ToList();
        MergeTarget = null;
        ParentFilter = "";
        BuildParentChoices();
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
                $"「{source.Name}」を「{target.Name}」に統合しますか?\n「{source.Name}」が付いた文書 {source.UsageCount} 件には「{target.Name}」が付き、「{source.Name}」は削除されます。紐づけも引き継ぎます。",
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
        VisibleTags = _allTags
            .Where(t => TagGroupFilter is null || TagGroupFilter == AllGroups || t.GroupId == TagGroupFilter.Id)
            .Where(t => filter.Length == 0 || NameNormalizer.Normalize(t.Name).Contains(filter, StringComparison.Ordinal))
            .ToList();
        SelectedTag = VisibleTags.FirstOrDefault(t => t.Id == selectTagId);
    }
}
