using CommunityToolkit.Mvvm.ComponentModel;
using PdfManager.Core.Catalog;
using PdfManager.Core.Import;

namespace PdfManager.App.ViewModels;

public sealed record PrefixOption(FileNamePrefixChoice? Choice, string Label)
{
    public static PrefixOption None { get; } = new(null, "(使わない)");
}

public sealed record AttributeFilterOption(long? OptionId, string Label)
{
    public static AttributeFilterOption All { get; } = new(null, "すべての属性");
}

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

    public TagChoice(string key, string name, string attributeText, bool isNew, bool? isChecked, Action<TagChoice> onToggled)
    {
        Key = key;
        Name = name;
        AttributeText = attributeText;
        IsNew = isNew;
        _onToggled = onToggled;
        _loading = true;
        IsChecked = isChecked;
        _loading = false;
    }

    public string Key { get; }

    public string Name { get; }

    public string AttributeText { get; }

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
