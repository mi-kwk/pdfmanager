namespace PdfManager.Core.Search;

public enum TagMatchMode
{
    All,
    Any,
}

public enum SortKey
{
    Registered,
    Title,
    Rating,
    ViewCount,
    LastViewed,
    Field,
}

// 項目 1 つ分の条件(決定ログ L-2)。型に合わせて使う値が決まる
public sealed record FieldCondition(long FieldId)
{
    // 文字:部分一致
    public string? Contains { get; init; }

    // 数値:範囲(片方だけでも可)
    public double? MinNumber { get; init; }

    public double? MaxNumber { get; init; }

    // 日付・年月:範囲(YYYY-MM-DD。年月は 1 日で表す)
    public string? MinDate { get; init; }

    public string? MaxDate { get; init; }

    // 選択肢:どれかに一致
    public IReadOnlySet<string>? AnyOf { get; init; }
}

public sealed record SearchQuery
{
    public string Keywords { get; init; } = "";

    public TagMatchMode TagMode { get; init; } = TagMatchMode.All;

    public IReadOnlyCollection<long> IncludedTagIds { get; init; } = [];

    public IReadOnlyCollection<long> ExcludedTagIds { get; init; } = [];

    public IReadOnlyList<FieldCondition> FieldConditions { get; init; } = [];

    // 「★N 以上」(決定ログ L-4)
    public int? MinRating { get; init; }

    public bool UnratedOnly { get; init; }

    public DateOnly? RegisteredFrom { get; init; }

    public DateOnly? RegisteredTo { get; init; }

    public DateOnly? LastViewedFrom { get; init; }

    public DateOnly? LastViewedTo { get; init; }

    public bool NeverViewedOnly { get; init; }

    public SortKey Sort { get; init; } = SortKey.Registered;

    public long? SortFieldId { get; init; }

    public bool Descending { get; init; } = true;
}
