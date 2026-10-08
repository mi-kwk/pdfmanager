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

// タググループ(決定ログ R-11、Q-1〜Q-14)。順位は 1〜5、0 は「独立」(紐づけなし、順位 1 と同じ位置に表示)。
// IsPinned のグループが「項目」で、メイン画面の左に常に表示する
public sealed record TagGroup(long Id, string Name, int Rank, bool IsPinned, int PinOrder, long TagCount)
{
    public const int IndependentRank = 0;
    public const int MaxRank = 5;

    public bool IsIndependent => Rank == IndependentRank;

    public string RankLabel => TagRanks.Label(Rank);

    // 登録・タグ設定の画面での並び(独立は順位 1 と同じ位置)
    public int DisplayRank => IsIndependent ? 1 : Rank;
}

public static class TagRanks
{
    public static IReadOnlyList<int> All { get; } = [1, 2, 3, 4, 5, TagGroup.IndependentRank];

    public static string Label(int rank) => rank == TagGroup.IndependentRank ? "独立" : $"順位 {rank}";
}

// GroupId が null のタグは自由タグ。ParentIds は 1 つ上の順位の紐づけ先
public sealed record TagSummary(long Id, string Name, long UsageCount, long? GroupId, string? GroupName, IReadOnlyList<long> ParentIds, IReadOnlyList<string> ParentNames)
{
    public string GroupLabel => GroupName ?? "自由タグ";

    public string ParentText => string.Join("、", ParentNames);

    public bool HasParents => ParentNames.Count > 0;

    // 一覧などの 2 行目:「分類２ ・ 漫画、雑誌」
    public string DetailText => ParentNames.Count == 0 ? GroupLabel : $"{GroupLabel} ・ {string.Join("、", ParentNames)}";
}

// 利用者の入力が原因の失敗。メッセージはそのまま画面に表示する
public sealed class CatalogException(string message) : Exception(message);
