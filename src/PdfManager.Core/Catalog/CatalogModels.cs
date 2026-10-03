namespace PdfManager.Core.Catalog;

public enum FieldType
{
    Text,
    Number,
    Date,
    YearMonth,
    Select,
}

public static class FieldTypes
{
    public static IReadOnlyList<FieldType> All { get; } = Enum.GetValues<FieldType>();

    public static string ToDb(FieldType type) => type switch
    {
        FieldType.Text => "text",
        FieldType.Number => "number",
        FieldType.Date => "date",
        FieldType.YearMonth => "yearmonth",
        FieldType.Select => "select",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static FieldType FromDb(string value) => value switch
    {
        "text" => FieldType.Text,
        "number" => FieldType.Number,
        "date" => FieldType.Date,
        "yearmonth" => FieldType.YearMonth,
        "select" => FieldType.Select,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string DisplayName(FieldType type) => type switch
    {
        FieldType.Text => "文字",
        FieldType.Number => "数値",
        FieldType.Date => "日付",
        FieldType.YearMonth => "年月",
        FieldType.Select => "選択肢",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

public sealed record FieldOption(long Id, string Value, long UsageCount);

public sealed record FieldDefinition(long Id, string Name, FieldType Type, long UsageCount, IReadOnlyList<FieldOption> Options)
{
    public string TypeLabel => FieldTypes.DisplayName(Type);
}

// 自由タグの属性 = 選択肢型の項目の選択肢(決定ログ D-1)
public sealed record TagAttribute(long OptionId, string FieldName, string Value);

public sealed record TagSummary(long Id, string Name, long UsageCount, IReadOnlyList<TagAttribute> Attributes)
{
    public string AttributeText => string.Join("、", Attributes.Select(a => a.Value));
}

// 利用者の入力が原因の失敗。メッセージはそのまま画面に表示する
public sealed class CatalogException(string message) : Exception(message);
