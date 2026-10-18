using PdfManager.Core.Catalog;

namespace PdfManager.Core.Search;

// タグの付け漏れの種類(決定ログ BC-2)
public enum TagGapKind
{
    // ①選んだグループのタグが 1 つも付いていない
    MissingGroup,

    // ②上の順位のタグはあるが、それに紐づく下の順位のタグがない
    MissingChild,

    // ③下の順位のタグはあるが、その紐づき先の上の順位のタグがない
    MissingParent,
}

// GroupId: ①で調べるグループ(null は順位 1 のすべてのグループ)
// UpperRank: ②③で調べる上の順位(UpperRank → UpperRank + 1)
// FilterTagId: 絞り込みのタグ(①はこのタグが付いているもの、②③はこの上の順位のタグについて調べる)
public sealed record TagGapQuery(TagGapKind Kind, long? GroupId = null, int UpperRank = 1, long? FilterTagId = null);

// 付け漏れのある PDF と、足りないものの説明(一覧の 2 行目に出す)
public sealed record TagGap(DocumentEntry Entry, string Description);

// タグの付け漏れを探す(決定ログ BC-2〜BC-8)。版のある文書は最新版だけを調べる
public sealed class TagGapFinder
{
    private readonly Dictionary<long, TagGroup> _groups;
    private readonly Dictionary<long, TagSummary> _tags;
    private readonly Dictionary<long, List<long>> _children = [];

    public TagGapFinder(IReadOnlyList<TagGroup> groups, IReadOnlyList<TagSummary> tags)
    {
        _groups = groups.ToDictionary(g => g.Id);
        _tags = tags.ToDictionary(t => t.Id);
        foreach (var tag in tags)
        {
            foreach (var parent in tag.ParentIds)
            {
                (_children.TryGetValue(parent, out var list) ? list : _children[parent] = []).Add(tag.Id);
            }
        }
    }

    public IReadOnlyList<TagGap> Find(IEnumerable<DocumentEntry> entries, TagGapQuery query)
    {
        var result = new List<TagGap>();
        foreach (var entry in entries.Where(e => e.IsLatest))
        {
            if (Describe(entry, query) is { } description)
            {
                result.Add(new TagGap(entry, description));
            }
        }
        return result;
    }

    public int Count(IEnumerable<DocumentEntry> entries, TagGapQuery query) => entries.Count(e => e.IsLatest && Describe(e, query) is not null);

    // 当てはまらなければ null
    private string? Describe(DocumentEntry entry, TagGapQuery query)
    {
        var tagIds = entry.TagIds.ToHashSet();
        switch (query.Kind)
        {
            case TagGapKind.MissingGroup:
            {
                if (query.FilterTagId is { } filter && !tagIds.Contains(filter))
                {
                    return null;
                }
                var groupIds = query.GroupId is { } id ? new HashSet<long> { id } : _groups.Values.Where(g => g.Rank == 1).Select(g => g.Id).ToHashSet();
                if (groupIds.Count == 0 || tagIds.Any(t => _tags.TryGetValue(t, out var tag) && tag.GroupId is { } g && groupIds.Contains(g)))
                {
                    return null;
                }
                return query.GroupId is { } single && _groups.TryGetValue(single, out var group) ? $"{group.Name}のタグなし" : $"{TagRanks.Label(1)} のタグなし";
            }
            case TagGapKind.MissingChild:
            {
                // 紐づく下のタグがある上のタグについて、そのどれも付いていないもの
                var missing = tagIds
                    .Where(t => RankOf(t) == query.UpperRank && (query.FilterTagId is null || t == query.FilterTagId))
                    .Where(t => _children.TryGetValue(t, out var children) && !children.Any(tagIds.Contains))
                    .Select(t => _tags[t].Name)
                    .Order(StringComparer.Ordinal)
                    .ToList();
                return missing.Count == 0 ? null : $"{string.Join("、", missing)} に紐づく{TagRanks.Label(query.UpperRank + 1)} のタグなし";
            }
            case TagGapKind.MissingParent:
            {
                // 紐づき先がある下のタグについて、紐づき先のどれも付いていないもの
                var missing = tagIds
                    .Where(t => RankOf(t) == query.UpperRank + 1 && _tags[t].ParentIds.Count > 0)
                    .Where(t => query.FilterTagId is not { } filter || _tags[t].ParentIds.Contains(filter))
                    .Where(t => !_tags[t].ParentIds.Any(tagIds.Contains))
                    .Select(t => $"{_tags[t].Name}({string.Join("・", _tags[t].ParentNames)})")
                    .Order(StringComparer.Ordinal)
                    .ToList();
                return missing.Count == 0 ? null : $"{string.Join("、", missing)} の紐づき先なし";
            }
            default:
                return null;
        }
    }

    private int? RankOf(long tagId) =>
        _tags.TryGetValue(tagId, out var tag) && tag.GroupId is { } g && _groups.TryGetValue(g, out var group) ? group.Rank : null;
}
