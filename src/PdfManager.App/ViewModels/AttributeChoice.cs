using CommunityToolkit.Mvvm.ComponentModel;

namespace PdfManager.App.ViewModels;

public sealed record AttributeGroup(string FieldName, IReadOnlyList<AttributeChoice> Choices);

public partial class AttributeChoice : ObservableObject
{
    private readonly Action _onChanged;
    private readonly bool _initialized;

    public AttributeChoice(long optionId, string value, bool isChecked, Action onChanged)
    {
        OptionId = optionId;
        Value = value;
        _onChanged = onChanged;
        IsChecked = isChecked;
        _initialized = true;
    }

    public long OptionId { get; }

    public string Value { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value)
    {
        if (_initialized)
        {
            _onChanged();
        }
    }
}
