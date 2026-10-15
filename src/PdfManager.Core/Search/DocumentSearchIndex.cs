using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PdfManager.Core.Catalog;
using PdfManager.Core.Documents;
using PdfManager.Core.Text;

namespace PdfManager.Core.Search;

public sealed record DocumentEntry(
    string Id,
    string Title,
    string FileName,
    DateTimeOffset CreatedAt,
    int PageCount,
    IReadOnlyList<long> TagIds,
    IReadOnlyList<string> TagNames,
    IReadOnlyList<(string Name, string Value)> FieldValues)
{
    public int Rating { get; init; }

    public long ViewCount { get; init; }

    public DateTimeOffset? LastViewedAt { get; init; }

    public IReadOnlyDictionary<long, FieldValue> RawFieldValues { get; init; } = new Dictionary<long, FieldValue>();

    // 補助項目 ID → 画面に表示する形の値(年月は 2026-10)。値での絞り込みに使う(決定ログ Q-6)
    public IReadOnlyDictionary<long, string> FormattedFieldValues { get; init; } = new Dictionary<long, string>();

    // 版管理(決定ログ N-4、N-5)
    public string? SeriesId { get; init; }

    public int VersionNo { get; init; }

    public string VersionLabel { get; init; } = "";

    public bool IsLatest { get; init; } = true;

    public string VersionText => SeriesId is null ? "" : VersionNames.Format(VersionNo, VersionLabel) + (IsLatest ? "" : "・旧版");

    // 保存先(決定ログ S-4、S-6)。null がメイン、文字列がサブライブラリの ID
    public string? StorageId { get; init; }

    // 続き(決定ログ V-4):並びの何番目か / 全体の数。続きがなければ 0
    public int ChainPosition { get; init; }

    public int ChainLength { get; init; }

    public string ChainText => ChainLength == 0 ? "" : $"続き {ChainPosition}/{ChainLength}";
}

// 検索(docs/04「検索」、決定ログ I-1〜I-6、L-1〜L-5)。件数が数万件でも一瞬で探せるよう、検索用の文字と条件に使う値をメモリに持つ
public sealed class DocumentSearchIndex
{
    // タイトルの並び替えは日本語の並び順にする(かな・全角半角・大文字小文字の違いを無視)
    private static readonly StringComparer TitleComparer = StringComparer.Create(CultureInfo.GetCultureInfo("ja-JP"),
        CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreCase);

    private readonly List<(DocumentEntry Entry, string Text)> _entries;

    private DocumentSearchIndex(List<(DocumentEntry, string)> entries) => _entries = entries;

    public int Count => _entries.Count;

    public static DocumentSearchIndex Load(SqliteConnection connection)
    {
        var tags = new Dictionary<string, List<(long Id, string Name)>>();
        foreach (var (documentId, tagId, name) in Query(connection,
            "SELECT dt.document_id, t.id, t.name FROM document_tags dt JOIN tags t ON t.id = dt.tag_id ORDER BY t.normalized_name;",
            r => (r.GetString(0), r.GetInt64(1), r.GetString(2))))
        {
            (tags.TryGetValue(documentId, out var list) ? list : tags[documentId] = []).Add((tagId, name));
        }

        var fields = new Dictionary<string, List<(long FieldId, string Name, FieldType Type, FieldValue Value)>>();
        foreach (var row in Query(connection,
            """
            SELECT v.document_id, f.id, f.name, f.type, v.value_text, v.value_number, v.value_date
            FROM document_field_values v JOIN fields f ON f.id = v.field_id ORDER BY f.sort_order, f.id;
            """,
            r => (DocumentId: r.GetString(0), FieldId: r.GetInt64(1), Name: r.GetString(2), Type: FieldTypes.FromDb(r.GetString(3)),
                  Value: new FieldValue(r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetDouble(5), r.IsDBNull(6) ? null : r.GetString(6)))))
        {
            (fields.TryGetValue(row.DocumentId, out var list) ? list : fields[row.DocumentId] = []).Add((row.FieldId, row.Name, row.Type, row.Value));
        }

        var chains = LinkRepository.ChainPositions(connection);
        var entries = Query(connection,
            """
            SELECT d.id, d.title, d.file_name, d.created_at, d.page_count, d.memo, d.rating, d.view_count, d.last_viewed_at,
                   d.series_id, d.version_no, d.version_label,
                   d.series_id IS NULL OR d.version_no = (SELECT MAX(version_no) FROM documents s WHERE s.series_id = d.series_id),
                   d.archive_volume_id
            FROM documents d ORDER BY d.created_at DESC, d.id;
            """,
            r =>
            {
                var id = r.GetString(0);
                var documentTags = tags.GetValueOrDefault(id) ?? [];
                var documentFields = fields.GetValueOrDefault(id) ?? [];
                var entry = new DocumentEntry(
                    id, r.GetString(1), r.GetString(2),
                    DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture),
                    r.GetInt32(4),
                    documentTags.Select(t => t.Id).ToList(),
                    documentTags.Select(t => t.Name).ToList(),
                    documentFields.Select(f => (f.Name, FieldValues.Format(f.Type, f.Value))).ToList())
                {
                    Rating = r.GetInt32(6),
                    ViewCount = r.GetInt64(7),
                    LastViewedAt = r.IsDBNull(8) ? null : DateTimeOffset.Parse(r.GetString(8), CultureInfo.InvariantCulture),
                    RawFieldValues = documentFields.ToDictionary(f => f.FieldId, f => f.Value),
                    FormattedFieldValues = documentFields.ToDictionary(f => f.FieldId, f => FieldValues.Format(f.Type, f.Value)),
                    SeriesId = r.IsDBNull(9) ? null : r.GetString(9),
                    VersionNo = r.IsDBNull(10) ? 0 : r.GetInt32(10),
                    VersionLabel = r.GetString(11),
                    IsLatest = r.GetBoolean(12),
                    StorageId = r.IsDBNull(13) ? null : r.GetString(13),
                    ChainPosition = chains.TryGetValue(id, out var chain) ? chain.Position : 0,
                    ChainLength = chains.TryGetValue(id, out var length) ? length.Length : 0,
                };
                // 決定ログ I-2・J-6: タイトル・タグ名・ファイル名・項目の値・メモを対象にする
                var text = Normalize(string.Join('\n',
                    new[] { entry.Title, entry.FileName, r.GetString(5) }.Concat(entry.TagNames).Concat(entry.FieldValues.Select(f => f.Value))));
                return (entry, text);
            });
        return new DocumentSearchIndex(entries);
    }

    // 簡易版:キーワードと「すべて含む」タグだけ(決定ログ I-6)
    public IReadOnlyList<DocumentEntry> Search(string keywords, IReadOnlyCollection<long> requiredTagIds) =>
        Search(new SearchQuery { Keywords = keywords, IncludedTagIds = requiredTagIds });

    public IReadOnlyList<DocumentEntry> Search(SearchQuery query)
    {
        var terms = Normalize(query.Keywords).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = _entries
            .Where(e => terms.All(t => e.Text.Contains(t, StringComparison.Ordinal)))
            .Select(e => e.Entry)
            .Where(e => (e.IsLatest || query.IncludeOldVersions) && (!query.FilterByStorage || e.StorageId == query.StorageId)
                        && MatchesTags(e, query) && MatchesFields(e, query.FieldConditions) && MatchesRatingAndDates(e, query));
        return Sort(matches, query).ToList();
    }

    // 決定ログ S-6: 保存先ごとの件数。キーは "" がメイン、それ以外はサブの ID
    public static IReadOnlyDictionary<string, int> CountStorages(IEnumerable<DocumentEntry> results) => results
        .GroupBy(e => e.StorageId ?? "")
        .ToDictionary(g => g.Key, g => g.Count());

    // 決定ログ L-3: 今の結果の中での件数
    public static IReadOnlyDictionary<long, int> CountTags(IEnumerable<DocumentEntry> results) => results
        .SelectMany(e => e.TagIds)
        .GroupBy(id => id)
        .ToDictionary(g => g.Key, g => g.Count());

    public static IReadOnlyDictionary<string, int> CountFieldTexts(IEnumerable<DocumentEntry> results, long fieldId) => results
        .Select(e => e.RawFieldValues.GetValueOrDefault(fieldId)?.Text)
        .OfType<string>()
        .GroupBy(v => v)
        .ToDictionary(g => g.Key, g => g.Count());

    // 補助項目の値(表示する形)ごとの件数
    public static IReadOnlyDictionary<string, int> CountFieldValues(IEnumerable<DocumentEntry> results, long fieldId) => results
        .Select(e => e.FormattedFieldValues.GetValueOrDefault(fieldId))
        .OfType<string>()
        .GroupBy(v => v)
        .ToDictionary(g => g.Key, g => g.Count());

    // 決定ログ Q-5: タグが 1 つも付いていない(未設定)文書の数
    public static int CountUntagged(IEnumerable<DocumentEntry> results) => results.Count(e => e.TagIds.Count == 0);

    // 補助項目に入っている値(表示する形)の一覧。年月・日付・数値は新しい・大きい順、文字は五十音順
    public IReadOnlyList<string> DistinctFieldValues(long fieldId)
    {
        var values = _entries.Select(e => e.Entry)
            .Where(e => e.RawFieldValues.ContainsKey(fieldId))
            .Select(e => (Display: e.FormattedFieldValues[fieldId], Raw: e.RawFieldValues[fieldId]))
            .DistinctBy(v => v.Display)
            .ToList();
        return values.Any(v => v.Raw.Number is not null)
            ? values.OrderByDescending(v => v.Raw.Number).Select(v => v.Display).ToList()
            : values.Any(v => v.Raw.Date is not null)
                ? values.OrderByDescending(v => v.Raw.Date, StringComparer.Ordinal).Select(v => v.Display).ToList()
                : values.Select(v => v.Display).Order(TitleComparer).ToList();
    }

    private static bool MatchesTags(DocumentEntry entry, SearchQuery query)
    {
        if (query.UntaggedOnly && entry.TagIds.Count > 0)
        {
            return false;
        }
        if (query.ExcludedTagIds.Any(entry.TagIds.Contains))
        {
            return false;
        }
        if (query.IncludedTagIds.Count == 0)
        {
            return true;
        }
        return query.TagMode == TagMatchMode.All
            ? query.IncludedTagIds.All(entry.TagIds.Contains)
            : query.IncludedTagIds.Any(entry.TagIds.Contains);
    }

    private static bool MatchesFields(DocumentEntry entry, IReadOnlyList<FieldCondition> conditions)
    {
        foreach (var condition in conditions)
        {
            if (!entry.RawFieldValues.TryGetValue(condition.FieldId, out var value))
            {
                return false;
            }
            if (condition.Contains is { Length: > 0 } contains
                && !Normalize(value.Text ?? "").Contains(Normalize(contains), StringComparison.Ordinal))
            {
                return false;
            }
            if ((condition.MinNumber is { } min && !(value.Number >= min)) || (condition.MaxNumber is { } max && !(value.Number <= max)))
            {
                return false;
            }
            if ((condition.MinDate is { } from && string.CompareOrdinal(value.Date, from) < 0)
                || (condition.MaxDate is { } to && (value.Date is null || string.CompareOrdinal(value.Date, to) > 0)))
            {
                return false;
            }
            if (condition.AnyOf is { Count: > 0 } anyOf && (value.Text is null || !anyOf.Contains(value.Text)))
            {
                return false;
            }
            if (condition.AnyOfDisplay is { Count: > 0 } anyOfDisplay && !anyOfDisplay.Contains(entry.FormattedFieldValues[condition.FieldId]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool MatchesRatingAndDates(DocumentEntry entry, SearchQuery query)
    {
        if ((query.UnratedOnly && entry.Rating != 0) || (query.MinRating is { } minRating && entry.Rating < minRating))
        {
            return false;
        }
        if (!InRange(LocalDate(entry.CreatedAt), query.RegisteredFrom, query.RegisteredTo))
        {
            return false;
        }
        if (query.NeverViewedOnly)
        {
            return entry.LastViewedAt is null;
        }
        if (query.LastViewedFrom is not null || query.LastViewedTo is not null)
        {
            return entry.LastViewedAt is { } viewed && InRange(LocalDate(viewed), query.LastViewedFrom, query.LastViewedTo);
        }
        return true;
    }

    private static DateOnly LocalDate(DateTimeOffset value) => DateOnly.FromDateTime(value.ToLocalTime().DateTime);

    private static bool InRange(DateOnly date, DateOnly? from, DateOnly? to) => (from is null || date >= from) && (to is null || date <= to);

    // 決定ログ L-5: 値がないものは昇順・降順どちらでも最後。同じ値どうしは登録日の新しい順
    private static IEnumerable<DocumentEntry> Sort(IEnumerable<DocumentEntry> matches, SearchQuery query)
    {
        var list = matches.ToList();
        Comparison<DocumentEntry> compare = query.Sort switch
        {
            SortKey.Title => (a, b) => TitleComparer.Compare(a.Title, b.Title),
            SortKey.Rating => CompareOptional(e => e.Rating == 0 ? null : (IComparable)e.Rating),
            SortKey.ViewCount => (a, b) => a.ViewCount.CompareTo(b.ViewCount),
            SortKey.LastViewed => CompareOptional(e => e.LastViewedAt),
            SortKey.Field when query.SortFieldId is { } fieldId => CompareOptional(e => FieldSortValue(e, fieldId)),
            _ => (a, b) => a.CreatedAt.CompareTo(b.CreatedAt),
        };
        var hasValue = (DocumentEntry e) => query.Sort switch
        {
            SortKey.Rating => e.Rating != 0,
            SortKey.LastViewed => e.LastViewedAt is not null,
            SortKey.Field when query.SortFieldId is { } fieldId => FieldSortValue(e, fieldId) is not null,
            _ => true,
        };
        list.Sort((a, b) =>
        {
            var (aHas, bHas) = (hasValue(a), hasValue(b));
            if (aHas != bHas)
            {
                return aHas ? -1 : 1;
            }
            var result = aHas ? compare(a, b) : 0;
            if (query.Descending)
            {
                result = -result;
            }
            return result != 0 ? result : b.CreatedAt.CompareTo(a.CreatedAt);
        });
        return list;

        Comparison<DocumentEntry> CompareOptional(Func<DocumentEntry, IComparable?> get) =>
            (a, b) => Comparer<IComparable?>.Default.Compare(get(a), get(b));
    }

    private static IComparable? FieldSortValue(DocumentEntry entry, long fieldId) =>
        entry.RawFieldValues.TryGetValue(fieldId, out var value)
            ? value.Number is { } number ? number : value.Date ?? (IComparable?)(value.Text is { } text ? new TitleKey(text) : null)
            : null;

    // 文字の項目もタイトルと同じ並び順にするための包み
    private sealed record TitleKey(string Text) : IComparable
    {
        public int CompareTo(object? obj) => TitleComparer.Compare(Text, (obj as TitleKey)?.Text);
    }

    // 決定ログ I-1: 全角・半角、大文字・小文字、ひらがな・カタカナの違いを無視する
    public static string Normalize(string text)
    {
        var normalized = NameNormalizer.Normalize(text);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            // カタカナ(ァ〜ヶ)をひらがなにそろえる
            builder.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c);
        }
        return builder.ToString();
    }

    private static List<T> Query<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(map(reader));
        }
        return results;
    }
}
