using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.Core.Catalog;
using PdfManager.Core.Import;

namespace PdfManager.App.ViewModels;

public sealed record PrefixOption(FileNamePrefixChoice? Choice, string Label)
{
    public static PrefixOption None { get; } = new(null, "(使わない)");
}

// 登録・タグ設定の画面の、タググループ 1 つ分の欄(決定ログ R-11 ②)。Group が null なら自由タグの欄
public sealed partial class TagSection(TagGroup? group, Action onSearchChanged, Func<TagSection, Task> onEnter) : ObservableObject
{
    public TagGroup? Group { get; } = group;

    public string Title => Group?.Name ?? "自由タグ";

    public string RankText => Group?.RankLabel ?? "";

    public bool IsDependent => Group is { Rank: >= 2 };

    public string SearchName => $"{Title}の検索";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<TagChoice> Choices { get; set; } = [];

    [ObservableProperty]
    public partial string Hint { get; set; } = "";

    private bool _resettingSearch;

    partial void OnSearchChanged(string value)
    {
        if (!_resettingSearch)
        {
            onSearchChanged();
        }
    }

    // 別の PDF に移ったとき、前の PDF で入力した検索の文字を消す(候補は呼び出し側で表示し直す)
    public void ResetSearch()
    {
        _resettingSearch = true;
        Search = "";
        _resettingSearch = false;
    }

    // 決定ログ Q-12: 検索欄で Enter を押すと、見つかればそのタグを付け、なければこのグループに新しく作って付ける
    [RelayCommand]
    private Task EnterAsync() => onEnter(this);

    // ==== 畳む(決定ログ AA-1〜AA-6) ====

    // タグを選んだら畳む欄か(設定の順位以下で、下に次の順位の欄があるもの)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent), nameof(ToggleName), nameof(ToggleSymbol))]
    public partial bool CanCollapse { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent), nameof(ToggleName), nameof(ToggleSymbol))]
    public partial bool IsCollapsed { get; set; }

    // 決定ログ AA-2: 畳んだときに 1 行で表示する、付いているタグ
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    public bool ShowContent => !(CanCollapse && IsCollapsed);

    public string ToggleName => IsCollapsed ? $"{Title}を開く" : $"{Title}を畳む";

    public Wpf.Ui.Controls.SymbolRegular ToggleSymbol => IsCollapsed ? Wpf.Ui.Controls.SymbolRegular.ChevronRight24 : Wpf.Ui.Controls.SymbolRegular.ChevronDown24;

    // 決定ログ AA-4: 検索欄に入力の位置を移すよう画面に頼む
    public event Action? FocusSearchRequested;

    public void RequestFocus() => FocusSearchRequested?.Invoke();

    [RelayCommand]
    private void ToggleCollapsed()
    {
        IsCollapsed = !IsCollapsed;
        if (!IsCollapsed)
        {
            RequestFocus();
        }
    }
}

// 詳細エリアに表示する、付いているタグ(グループごとに 1 行、決定ログ R-11 ④)
public sealed record AssignedTagLine(string GroupName, string TagsText);

// 項目 1 つ分の入力欄。複数選択で値がそろっていないときは「(複数の値)」を表示する(決定ログ F-6)
public partial class FieldEditor : ObservableObject
{
    private readonly Action<FieldEditor> _onEdited;
    private bool _loading;

    public FieldEditor(FieldDefinition field, Action<FieldEditor> onEdited)
    {
        Field = field;
        _onEdited = onEdited;
        Options = field.Type == FieldType.Select ? ["", .. field.Options.Select(o => o.Value)] : [];
    }

    public FieldDefinition Field { get; }

    public bool IsSelect => Field.Type == FieldType.Select;

    public IReadOnlyList<string> Options { get; }

    public string Placeholder => IsMixed ? "(複数の値)" : Field.Type switch
    {
        FieldType.YearMonth => "例:2026-09",
        FieldType.Date => "例:2026-09-30",
        FieldType.Number => "数値",
        _ => "",
    };

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    public partial bool IsMixed { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public void Load(string text, bool isMixed)
    {
        _loading = true;
        Text = text;
        IsMixed = isMixed;
        _loading = false;
        Validate();
    }

    partial void OnTextChanged(string value)
    {
        if (_loading)
        {
            return;
        }
        IsMixed = false;
        Validate();
        _onEdited(this);
    }

    private void Validate()
    {
        try
        {
            FieldValues.Parse(Field.Type, Text);
            Error = null;
        }
        catch (CatalogException ex)
        {
            Error = ex.Message;
        }
    }
}

// タグの候補 1 つ分。複数選択で一部の PDF にだけ付いているときは null(薄く表示、決定ログ F-7)
public partial class TagChoice : ObservableObject
{
    private readonly Action<TagChoice> _onToggled;
    private bool _loading;

    public TagChoice(string key, string name, string detail, bool isNew, bool? isChecked, Action<TagChoice> onToggled)
    {
        Key = key;
        Name = name;
        Detail = detail;
        IsNew = isNew;
        _onToggled = onToggled;
        _loading = true;
        IsChecked = isChecked;
        _loading = false;
    }

    public string Key { get; }

    public string Name { get; }

    // 2 行目(紐づけ先の名前など。なければ空)
    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    public bool IsNew { get; }

    public string Label => IsNew ? $"{Name}(新規)" : Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPartial))]
    public partial bool? IsChecked { get; set; }

    public bool IsPartial => IsChecked is null;

    partial void OnIsCheckedChanged(bool? value)
    {
        if (!_loading)
        {
            _onToggled(this);
        }
    }
}
