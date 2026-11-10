namespace PdfManager.Core.Catalog;

// 同じグループの中の親子のタグ(サブカテゴリ、決定ログ EC-1〜EC-9)をたどる・並べる
public static class TagTree
{
    // 親のすぐ下に子が来るように並べ、深さ(親がいくつ上にあるか)を付ける。
    // 親が items にないものは一番上として扱う。同じ親の中の並びは order に従う
    public static IReadOnlyList<(T Item, int Depth)> Order<T>(IEnumerable<T> items, Func<T, string> key, Func<T, string?> parent, IComparer<T> order)
    {
        var list = items.ToList();
        var present = list.Select(key).ToHashSet();
        var children = list.Where(i => parent(i) is { } p && present.Contains(p) && p != key(i)).ToLookup(i => parent(i)!);
        var result = new List<(T, int)>();
        var seen = new HashSet<string>();
        void Visit(T item, int depth)
        {
            if (!seen.Add(key(item)))
            {
                return;
            }
            result.Add((item, depth));
            foreach (var child in children[key(item)].OrderBy(c => c, order))
            {
                Visit(child, depth + 1);
            }
        }
        foreach (var root in list.Where(i => parent(i) is not { } p || !present.Contains(p) || p == key(i)).OrderBy(i => i, order))
        {
            Visit(root, 0);
        }
        // 親子が輪になっているものも落とさない
        foreach (var rest in list.Where(i => !seen.Contains(key(i))).OrderBy(i => i, order))
        {
            Visit(rest, 0);
        }
        return result;
    }

    // 親・その上の親…(近い順)。輪になっていても止まる
    public static IReadOnlyList<TKey> Ancestors<TKey>(TKey key, Func<TKey, TKey?> parent) where TKey : class
    {
        var result = new List<TKey>();
        var seen = new HashSet<TKey> { key };
        for (var current = parent(key); current is not null && seen.Add(current); current = parent(current))
        {
            result.Add(current);
        }
        return result;
    }

    public static IReadOnlyList<long> Ancestors(long id, IReadOnlyDictionary<long, long?> parents)
    {
        var result = new List<long>();
        var seen = new HashSet<long> { id };
        for (var current = parents.GetValueOrDefault(id); current is { } value && seen.Add(value); current = parents.GetValueOrDefault(value))
        {
            result.Add(value);
        }
        return result;
    }

    // 子・孫…(すべて)
    public static IReadOnlyList<long> Descendants(long id, IReadOnlyDictionary<long, long?> parents)
    {
        var children = parents.Where(p => p.Value is not null).ToLookup(p => p.Value!.Value, p => p.Key);
        var result = new List<long>();
        var seen = new HashSet<long> { id };
        var queue = new Queue<long>([id]);
        while (queue.Count > 0)
        {
            foreach (var child in children[queue.Dequeue()])
            {
                if (seen.Add(child))
                {
                    result.Add(child);
                    queue.Enqueue(child);
                }
            }
        }
        return result;
    }

    // 「ウィスキー › スコッチ・ウイスキー」の形(決定ログ EC-4)
    public static string Path(IEnumerable<string> ancestorsNearFirst, string name) =>
        string.Join(" › ", ancestorsNearFirst.Reverse().Append(name));
}
