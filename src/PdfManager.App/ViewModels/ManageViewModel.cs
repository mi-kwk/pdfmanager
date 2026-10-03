using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.App.ViewModels;

public sealed record FieldTypeChoice(FieldType Type, string Label);

// SC-04 項目・タグ管理
public partial class ManageViewModel : ObservableObject
{
    private readonly DataFolder _dataFolder;
    private readonly IUserDialogs _dialogs;
    private IReadOnlyList<TagSummary> _allTags = [];

    public ManageViewModel(DataFolder dataFolder, IUserDialogs dialogs)
    {
        _dataFolder = dataFolder;
        _dialogs = dialogs;
        NewFieldType = FieldTypeChoices[0];
        ReloadFields();
    }

    public IReadOnlyList<FieldTypeChoice> FieldTypeChoices { get; } =
        FieldTypes.All.Select(t => new FieldTypeChoice(t, FieldTypes.DisplayName(t))).ToList();

    // ---- 項目 ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttributeFields))]
    public partial IReadOnlyList<FieldDefinition> Fields { get; set; } = [];

    public bool HasAttributeFields => Fields.Any(f => f.Type == FieldType.Select);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFieldSelected), nameof(IsSelectField), nameof(CanChangeFieldType))]
    public partial FieldDefinition? SelectedField { get; set; }

    [ObservableProperty]
    public partial string NewFieldName { get; set; } = "";

    [ObservableProperty]
    public partial FieldTypeChoice NewFieldType { get; set; }

    [ObservableProperty]
    public partial string EditFieldName { get; set; } = "";

    [ObservableProperty]
    public partial FieldTypeChoice? EditFieldType { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOptionSelected))]
    public partial FieldOption? SelectedOption { get; set; }

    [ObservableProperty]
    public partial string NewOptionValue { get; set; } = "";

    [ObservableProperty]
    public partial string EditOptionValue { get; set; } = "";

    public bool IsFieldSelected => SelectedField is not null;

    public bool IsSelectField => SelectedField?.Type == FieldType.Select;

    public bool CanChangeFieldType => SelectedField is { UsageCount: 0 };

    public bool IsOptionSelected => SelectedOption is not null;

    partial void OnSelectedFieldChanged(FieldDefinition? value)
    {
        EditFieldName = value?.Name ?? "";
        EditFieldType = value is null ? null : FieldTypeChoices.First(c => c.Type == value.Type);
        SelectedOption = null;
    }

    partial void OnSelectedOptionChanged(FieldOption? value) => EditOptionValue = value?.Value ?? "";

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
        if (SelectedField is not { } field || EditFieldType is null || EditFieldType.Type == field.Type)
        {
            return;
        }
        if (field.Options.Count > 0 && !await _dialogs.ConfirmAsync(
                "型の変更", $"「{field.Name}」の選択肢 {field.Options.Count} 件は削除されます。型を変更しますか?", "変更する"))
        {
            return;
        }
        if (await RunAsync(c => c.ChangeFieldType(field.Id, EditFieldType.Type)))
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
        var impact = field.UsageCount > 0 ? $"\n値が入っている文書 {field.UsageCount} 件から、この項目の値も削除されます。" : "";
        if (await _dialogs.ConfirmAsync("項目の削除", $"項目「{field.Name}」を削除しますか?{impact}", "削除する")
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

    // ---- 選択肢 ----

    [RelayCommand]
    private async Task AddOptionAsync()
    {
        if (SelectedField is not { } field)
        {
            return;
        }
        long id = 0;
        if (await RunAsync(c => id = c.AddOption(field.Id, NewOptionValue)))
        {
            NewOptionValue = "";
            ReloadFields(field.Id, id);
        }
    }

    [RelayCommand]
    private async Task RenameOptionAsync()
    {
        if (SelectedField is not { } field || SelectedOption is not { } option)
        {
            return;
        }
        var impact = option.UsageCount > 0 ? $"\nこの選択肢が入っている文書 {option.UsageCount} 件の値も変わります。" : "";
        if ((option.UsageCount == 0 || await _dialogs.ConfirmAsync("選択肢の名前変更", $"「{option.Value}」を「{NameNormalizer.Clean(EditOptionValue)}」に変更しますか?{impact}", "変更する"))
            && await RunAsync(c => c.RenameOption(option.Id, EditOptionValue)))
        {
            ReloadFields(field.Id, option.Id);
        }
    }

    [RelayCommand]
    private async Task DeleteOptionAsync()
    {
        if (SelectedField is not { } field || SelectedOption is not { } option)
        {
            return;
        }
        var impact = option.UsageCount > 0 ? $"\nこの選択肢が入っている文書 {option.UsageCount} 件から、値が削除されます。" : "";
        if (await _dialogs.ConfirmAsync("選択肢の削除", $"選択肢「{option.Value}」を削除しますか?{impact}", "削除する")
            && await RunAsync(c => c.DeleteOption(option.Id)))
        {
            ReloadFields(field.Id);
        }
    }

    [RelayCommand]
    private Task MoveOptionUpAsync() => MoveOptionAsync(-1);

    [RelayCommand]
    private Task MoveOptionDownAsync() => MoveOptionAsync(1);

    private async Task MoveOptionAsync(int offset)
    {
        if (SelectedField is { } field && SelectedOption is { } option && await RunAsync(c => c.MoveOption(option.Id, offset)))
        {
            ReloadFields(field.Id, option.Id);
        }
    }

    // ---- 自由タグ ----

    [ObservableProperty]
    public partial IReadOnlyList<TagSummary> VisibleTags { get; set; } = [];

    [ObservableProperty]
    public partial string TagFilter { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTagSelected))]
    public partial TagSummary? SelectedTag { get; set; }

    [ObservableProperty]
    public partial string NewTagName { get; set; } = "";

    [ObservableProperty]
    public partial string EditTagName { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<TagSummary> MergeTargets { get; set; } = [];

    [ObservableProperty]
    public partial TagSummary? MergeTarget { get; set; }

    [ObservableProperty]
    public partial int TagCount { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<AttributeGroup> AttributeGroups { get; set; } = [];

    public bool IsTagSelected => SelectedTag is not null;

    partial void OnTagFilterChanged(string value) => ApplyTagFilter(SelectedTag?.Id);

    partial void OnSelectedTagChanged(TagSummary? value)
    {
        EditTagName = value?.Name ?? "";
        MergeTargets = value is null ? [] : _allTags.Where(t => t.Id != value.Id).ToList();
        MergeTarget = null;
        AttributeGroups = value is null
            ? []
            : Fields
                .Where(f => f.Type == FieldType.Select)
                .Select(f => new AttributeGroup(f.Name, f.Options
                    .Select(o => new AttributeChoice(o.Id, o.Value, value.Attributes.Any(a => a.OptionId == o.Id), OnAttributeToggled))
                    .ToList()))
                .ToList();
    }

    private async void OnAttributeToggled()
    {
        if (SelectedTag is not { } tag)
        {
            return;
        }
        var optionIds = AttributeGroups.SelectMany(g => g.Choices).Where(c => c.IsChecked).Select(c => c.OptionId).ToList();
        if (await RunAsync(c => c.SetTagAttributes(tag.Id, optionIds)))
        {
            ReloadTags(tag.Id);
        }
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        long id = 0;
        if (await RunAsync(c => id = c.AddTag(NewTagName)))
        {
            NewTagName = "";
            TagFilter = "";
            ReloadTags(id);
        }
    }

    [RelayCommand]
    private async Task RenameTagAsync()
    {
        if (SelectedTag is { } tag && await RunAsync(c => c.RenameTag(tag.Id, EditTagName)))
        {
            ReloadTags(tag.Id);
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
                $"「{source.Name}」を「{target.Name}」に統合しますか?\n「{source.Name}」が付いた文書 {source.UsageCount} 件には「{target.Name}」が付き、「{source.Name}」は削除されます。",
                "統合する")
            && await RunAsync(c => c.MergeTags(source.Id, target.Id)))
        {
            ReloadTags(target.Id);
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
            ReloadTags();
        }
    }

    // ---- 共通 ----

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

    private void ReloadFields(long? selectFieldId = null, long? selectOptionId = null)
    {
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            Fields = new CatalogRepository(connection).GetFields();
        }
        SelectedField = Fields.FirstOrDefault(f => f.Id == selectFieldId);
        SelectedOption = SelectedField?.Options.FirstOrDefault(o => o.Id == selectOptionId);
        // 選択肢の追加・削除はタグの属性の選択肢や表示にも影響するため、タグも読み直す
        ReloadTags(SelectedTag?.Id);
    }

    private void ReloadTags(long? selectTagId = null)
    {
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            _allTags = new CatalogRepository(connection).GetTags();
        }
        TagCount = _allTags.Count;
        ApplyTagFilter(selectTagId);
    }

    private void ApplyTagFilter(long? selectTagId)
    {
        var filter = NameNormalizer.Normalize(TagFilter);
        VisibleTags = filter.Length == 0
            ? _allTags
            : _allTags.Where(t => NameNormalizer.Normalize(t.Name).Contains(filter, StringComparison.Ordinal)).ToList();
        SelectedTag = VisibleTags.FirstOrDefault(t => t.Id == selectTagId);
    }
}
