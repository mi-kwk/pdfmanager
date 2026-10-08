using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.Core.Catalog;
using PdfManager.Core.Search;

namespace PdfManager.App.ViewModels;

public enum TagFilterState
{
    None,
    Include,
    Exclude,
}

// 絞り込みエリアのタグ 1 つ分。チェック=含む、右クリック=除外(決定ログ L-1)。件数は今の結果の中での数(L-3)
public partial class TagFilterItem(TagSummary tag, TagFilterState state, Action onChanged) : ObservableObject
{
    public TagSummary Tag { get; } = tag;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecked), nameof(IsExcluded), nameof(ExcludeMenuText))]
    public partial TagFilterState State { get; set; } = state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZero))]
    public partial int Count { get; set; }

    public bool IsZero => Count == 0;

    public bool IsExcluded => State == TagFilterState.Exclude;

    public string ExcludeMenuText => IsExcluded ? "除外をやめる" : "除外する";

    public bool IsChecked
    {
        get => State == TagFilterState.Include;
        set => State = value ? TagFilterState.Include : TagFilterState.None;
    }

    partial void OnStateChanged(TagFilterState value) => onChanged();

    [RelayCommand]
    private void ToggleExclude() => State = IsExcluded ? TagFilterState.None : TagFilterState.Exclude;
}

// メイン画面の左に常に表示する「項目」(タググループ)1 つ分(決定ログ Q-14)。検索欄でグループの中のタグを絞り込める
public partial class TagItemSection(TagGroup group, IReadOnlyList<TagFilterItem> items) : ObservableObject
{
    public TagGroup Group { get; } = group;

    public IReadOnlyList<TagFilterItem> Items { get; } = items;

    public string SearchName => $"{Group.Name}の検索";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleItems))]
    public partial string Search { get; set; } = "";

    // 検索中でも、チェック・除外しているタグは残す
    public IReadOnlyList<TagFilterItem> VisibleItems => Search.Trim().Length == 0
        ? Items
        : Items.Where(i => i.State != TagFilterState.None || DocumentSearchIndex.Normalize(i.Tag.Name).Contains(DocumentSearchIndex.Normalize(Search), StringComparison.Ordinal)).ToList();

    public bool HasItems => Items.Count > 0;
}

public partial class OptionFilterItem(string value, bool isChecked, Action onChanged) : ObservableObject
{
    public string Value { get; } = value;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZero))]
    public partial int Count { get; set; }

    public bool IsZero => Count == 0;

    partial void OnIsCheckedChanged(bool value) => onChanged();
}

// 補助項目 1 つ分の条件(決定ログ L-2、Q-6)。入っている値から選ぶ(検索欄で絞り込める)か、文字・範囲で指定する
public partial class FieldFilter : ObservableObject
{
    private readonly Action _onChanged;

    public FieldFilter(FieldDefinition field, FieldFilter? previous, IReadOnlyList<string> values, Action onChanged)
    {
        Field = field;
        _onChanged = onChanged;
        Text = previous?.Text ?? "";
        From = previous?.From ?? "";
        To = previous?.To ?? "";
        ValueSearch = previous?.ValueSearch ?? "";
        var checkedValues = previous?.Options.Where(o => o.IsChecked).Select(o => o.Value).ToHashSet() ?? [];
        Options = values.Select(v => new OptionFilterItem(v, checkedValues.Contains(v), onChanged)).ToList();
    }

    public string ValueSearchName => $"{Field.Name}の値の検索";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleOptions))]
    public partial string ValueSearch { get; set; }

    // 検索中でも、チェックした値は残す
    public IReadOnlyList<OptionFilterItem> VisibleOptions => ValueSearch.Trim().Length == 0
        ? Options
        : Options.Where(o => o.IsChecked || DocumentSearchIndex.Normalize(o.Value).Replace("-", "").Contains(
            DocumentSearchIndex.Normalize(ValueSearch).Replace("-", ""), StringComparison.Ordinal)).ToList();

    public bool HasValues => Options.Count > 0;

    public FieldDefinition Field { get; }

    public bool IsText => Field.Type == FieldType.Text;

    public bool IsRange => Field.Type is FieldType.Number or FieldType.Date or FieldType.YearMonth;


    public string RangeHint => Field.Type switch
    {
        FieldType.YearMonth => "例:2026-04",
        FieldType.Date => "例:2026-04-01",
        _ => "数値",
    };

    public IReadOnlyList<OptionFilterItem> Options { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial string From { get; set; }

    [ObservableProperty]
    public partial string To { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool HasCondition => Text.Trim().Length > 0 || From.Trim().Length > 0 || To.Trim().Length > 0 || Options.Any(o => o.IsChecked);

    partial void OnTextChanged(string value) => _onChanged();

    partial void OnFromChanged(string value) => _onChanged();

    partial void OnToChanged(string value) => _onChanged();

    public void Clear()
    {
        Text = "";
        From = "";
        To = "";
        ValueSearch = "";
        foreach (var option in Options)
        {
            option.IsChecked = false;
        }
    }

    // 入力が正しくなければ Error に理由を入れ、その部分は条件に使わない
    public FieldCondition? ToCondition()
    {
        Error = null;
        if (!HasCondition)
        {
            return null;
        }
        var chosen = Options.Where(o => o.IsChecked).Select(o => o.Value).ToHashSet();
        var condition = new FieldCondition(Field.Id) { AnyOfDisplay = chosen.Count > 0 ? chosen : null };
        try
        {
            switch (Field.Type)
            {
                case FieldType.Text:
                case FieldType.Select:
                    return condition with { Contains = Text.Trim().Length > 0 ? Text.Trim() : null };
                case FieldType.Number:
                    return condition with { MinNumber = FieldValues.Parse(Field.Type, From)?.Number, MaxNumber = FieldValues.Parse(Field.Type, To)?.Number };
                default:
                    return condition with { MinDate = FieldValues.Parse(Field.Type, From)?.Date, MaxDate = FieldValues.Parse(Field.Type, To)?.Date };
            }
        }
        catch (CatalogException ex)
        {
            Error = ex.Message;
            return null;
        }
    }

    public void UpdateCounts(IReadOnlyDictionary<string, int> counts)
    {
        foreach (var option in Options)
        {
            option.Count = counts.GetValueOrDefault(option.Value);
        }
    }
}

public sealed record RatingFilterOption(string Label, int? MinRating, bool UnratedOnly);

public sealed record SortOption(SortKey Key, long? FieldId, string Label)
{
    public string StoredKey => Key.ToString();
}
