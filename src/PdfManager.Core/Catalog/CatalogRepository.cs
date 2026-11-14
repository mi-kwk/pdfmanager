using Microsoft.Data.Sqlite;
using PdfManager.Core.Text;

namespace PdfManager.Core.Catalog;

// 項目(fields / field_options)と自由タグ(tags)の定義を管理する
public sealed class CatalogRepository(SqliteConnection connection)
{
    public const int MaxNameLength = 100;

    // ---- 項目 ----

    public IReadOnlyList<FieldDefinition> GetFields()
    {
        var options = new Dictionary<long, List<FieldOption>>();
        foreach (var (fieldId, option) in Query(
            """
            SELECT o.field_id, o.id, o.value,
                   (SELECT COUNT(*) FROM document_field_values v WHERE v.field_id = o.field_id AND v.value_text = o.value)
            FROM field_options o ORDER BY o.sort_order, o.id;
            """,
            r => (r.GetInt64(0), new FieldOption(r.GetInt64(1), r.GetString(2), r.GetInt64(3)))))
        {
            (options.TryGetValue(fieldId, out var list) ? list : options[fieldId] = []).Add(option);
        }

        return Query(
            """
            SELECT f.id, f.name, f.type, (SELECT COUNT(*) FROM document_field_values v WHERE v.field_id = f.id)
            FROM fields f ORDER BY f.sort_order, f.id;
            """,
            r => new FieldDefinition(
                r.GetInt64(0), r.GetString(1), FieldTypes.FromDb(r.GetString(2)), r.GetInt64(3),
                options.TryGetValue(r.GetInt64(0), out var list) ? list : []));
    }

    public long AddField(string name, FieldType type)
    {
        var cleaned = ValidateName(name, "項目名");
        EnsureUnique(cleaned, GetFields().Select(f => (f.Id, f.Name)), exceptId: null, "同じ名前の項目がすでにあります。");
        return InsertReturningId(
            "INSERT INTO fields (name, type, sort_order) VALUES ($name, $type, (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM fields)) RETURNING id;",
            ("$name", cleaned), ("$type", FieldTypes.ToDb(type)));
    }

    public void RenameField(long fieldId, string name)
    {
        var cleaned = ValidateName(name, "項目名");
        EnsureUnique(cleaned, GetFields().Select(f => (f.Id, f.Name)), fieldId, "同じ名前の項目がすでにあります。");
        Execute("UPDATE fields SET name = $name WHERE id = $id;", ("$name", cleaned), ("$id", fieldId));
    }

    // docs/04: 値が 1 件も入っていない項目だけ型を変更できる(データ変換の事故を防ぐため)
    public void ChangeFieldType(long fieldId, FieldType type)
    {
        var field = GetField(fieldId);
        if (field.Type == type)
        {
            return;
        }
        if (field.UsageCount > 0)
        {
            throw new CatalogException($"「{field.Name}」には値が入っている文書が {field.UsageCount} 件あるため、型を変更できません。");
        }

        using var transaction = connection.BeginTransaction();
        Execute("DELETE FROM field_options WHERE field_id = $id;", transaction, ("$id", fieldId));
        Execute("UPDATE fields SET type = $type WHERE id = $id;", transaction, ("$type", FieldTypes.ToDb(type)), ("$id", fieldId));
        transaction.Commit();
    }

    public void DeleteField(long fieldId) => Execute("DELETE FROM fields WHERE id = $id;", ("$id", fieldId));

    public void MoveField(long fieldId, int offset) =>
        Reorder("fields", GetFields().Select(f => f.Id).ToList(), fieldId, offset);

    // ---- 選択肢 ----

    public long AddOption(long fieldId, string value)
    {
        var field = GetField(fieldId);
        if (field.Type != FieldType.Select)
        {
            throw new CatalogException("選択肢を追加できるのは、型が「選択肢」の項目だけです。");
        }
        var cleaned = ValidateName(value, "選択肢");
        EnsureUnique(cleaned, field.Options.Select(o => (o.Id, o.Value)), exceptId: null, "同じ選択肢がすでにあります。");
        return InsertReturningId(
            """
            INSERT INTO field_options (field_id, value, sort_order)
            VALUES ($field, $value, (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM field_options WHERE field_id = $field)) RETURNING id;
            """,
            ("$field", fieldId), ("$value", cleaned));
    }

    // 選択肢の名前を変えると、その選択肢が入っている文書の値も新しい名前に変わる
    public void RenameOption(long optionId, string value)
    {
        var (field, option) = GetOption(optionId);
        var cleaned = ValidateName(value, "選択肢");
        EnsureUnique(cleaned, field.Options.Select(o => (o.Id, o.Value)), optionId, "同じ選択肢がすでにあります。");

        using var transaction = connection.BeginTransaction();
        Execute("UPDATE document_field_values SET value_text = $new WHERE field_id = $field AND value_text = $old;", transaction,
            ("$new", cleaned), ("$field", field.Id), ("$old", option.Value));
        Execute("UPDATE field_options SET value = $new WHERE id = $id;", transaction, ("$new", cleaned), ("$id", optionId));
        transaction.Commit();
    }

    // 選択肢を削除すると、その選択肢が入っている文書の値も削除する(docs/04)
    public void DeleteOption(long optionId)
    {
        var (field, option) = GetOption(optionId);
        using var transaction = connection.BeginTransaction();
        Execute("DELETE FROM document_field_values WHERE field_id = $field AND value_text = $value;", transaction,
            ("$field", field.Id), ("$value", option.Value));
        Execute("DELETE FROM field_options WHERE id = $id;", transaction, ("$id", optionId));
        transaction.Commit();
    }

    public void MoveOption(long optionId, int offset)
    {
        var (field, _) = GetOption(optionId);
        Reorder("field_options", field.Options.Select(o => o.Id).ToList(), optionId, offset);
    }

    // ---- タググループ(決定ログ R-11、Q-1〜Q-14) ----

    public IReadOnlyList<TagGroup> GetGroups() => Query(
        """
        SELECT g.id, g.name, g.rank, g.is_pinned, g.pin_order, (SELECT COUNT(*) FROM tags t WHERE t.group_id = g.id)
        FROM tag_groups g ORDER BY g.sort_order, g.id;
        """,
        r => new TagGroup(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3), r.GetInt32(4), r.GetInt64(5)));

    public long AddGroup(string name, int rank)
    {
        var cleaned = ValidateName(name, "タググループ名");
        ValidateRank(rank);
        EnsureUnique(cleaned, GetGroups().Select(g => (g.Id, g.Name)), exceptId: null, "同じ名前のタググループがすでにあります。");
        return InsertReturningId(
            """
            INSERT INTO tag_groups (name, normalized_name, rank, sort_order)
            VALUES ($name, $normalized, $rank, (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM tag_groups)) RETURNING id;
            """,
            ("$name", cleaned), ("$normalized", NameNormalizer.Normalize(cleaned)), ("$rank", rank));
    }

    public void RenameGroup(long groupId, string name)
    {
        var cleaned = ValidateName(name, "タググループ名");
        EnsureUnique(cleaned, GetGroups().Select(g => (g.Id, g.Name)), groupId, "同じ名前のタググループがすでにあります。");
        Execute("UPDATE tag_groups SET name = $name, normalized_name = $normalized WHERE id = $id;",
            ("$name", cleaned), ("$normalized", NameNormalizer.Normalize(cleaned)), ("$id", groupId));
    }

    // 順位を変えると、合わなくなった紐づけは外れる(件数は CountLinksBrokenByRank で事前に確かめる)
    public void ChangeGroupRank(long groupId, int rank)
    {
        ValidateRank(rank);
        using var transaction = connection.BeginTransaction();
        Execute("UPDATE tag_groups SET rank = $rank WHERE id = $id;", transaction, ("$rank", rank), ("$id", groupId));
        RemoveInvalidLinks(transaction);
        transaction.Commit();
    }

    public long CountLinksBrokenByRank(long groupId, int rank) =>
        CountBrokenLinks(t => Execute("UPDATE tag_groups SET rank = $rank WHERE id = $id;", t, ("$rank", rank), ("$id", groupId)));

    // グループを削除しても、タグは自由タグとして残す(文書のタグはそのまま)。紐づけは外れる
    public void DeleteGroup(long groupId)
    {
        using var transaction = connection.BeginTransaction();
        Execute("UPDATE tags SET group_id = NULL WHERE group_id = $id;", transaction, ("$id", groupId));
        Execute("DELETE FROM tag_groups WHERE id = $id;", transaction, ("$id", groupId));
        RemoveInvalidLinks(transaction);
        transaction.Commit();
    }

    public void MoveGroup(long groupId, int offset) =>
        Reorder("tag_groups", GetGroups().Select(g => g.Id).ToList(), groupId, offset);

    // 決定ログ Q-14: 項目(左に常に表示するタググループ)にする・やめる
    public void SetGroupPinned(long groupId, bool pinned) => Execute(
        pinned
            ? "UPDATE tag_groups SET is_pinned = 1, pin_order = (SELECT COALESCE(MAX(pin_order), 0) + 1 FROM tag_groups WHERE is_pinned = 1) WHERE id = $id AND is_pinned = 0;"
            : "UPDATE tag_groups SET is_pinned = 0 WHERE id = $id;",
        ("$id", groupId));

    public void MovePinnedGroup(long groupId, int offset)
    {
        var ordered = GetGroups().Where(g => g.IsPinned).OrderBy(g => g.PinOrder).ThenBy(g => g.Id).Select(g => g.Id).ToList();
        var index = ordered.IndexOf(groupId);
        var newIndex = index + offset;
        if (index < 0 || newIndex < 0 || newIndex >= ordered.Count)
        {
            return;
        }
        ordered.RemoveAt(index);
        ordered.Insert(newIndex, groupId);
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < ordered.Count; i++)
        {
            Execute("UPDATE tag_groups SET pin_order = $order WHERE id = $id;", transaction, ("$order", i + 1), ("$id", ordered[i]));
        }
        transaction.Commit();
    }

    // ---- タグ ----

    public IReadOnlyList<TagSummary> GetTags()
    {
        var parents = Query(
            "SELECT l.child_id, p.id, p.name FROM tag_links l JOIN tags p ON p.id = l.parent_id ORDER BY p.normalized_name;",
            r => (ChildId: r.GetInt64(0), ParentId: r.GetInt64(1), ParentName: r.GetString(2)))
            .ToLookup(x => x.ChildId);

        var aliases = Query("SELECT tag_id, name FROM tag_aliases ORDER BY normalized_name;", r => (TagId: r.GetInt64(0), Name: r.GetString(1)))
            .ToLookup(x => x.TagId, x => x.Name);

        // 決定ログ FC-2: すぐ下の小分類の名前
        var children = Query("SELECT parent_tag_id, name FROM tags WHERE parent_tag_id IS NOT NULL ORDER BY normalized_name;",
            r => (ParentId: r.GetInt64(0), Name: r.GetString(1))).ToLookup(x => x.ParentId, x => x.Name);

        return Query(
            """
            SELECT t.id, t.name, (SELECT COUNT(*) FROM document_tags d WHERE d.tag_id = t.id), t.group_id, g.name, t.parent_tag_id, pt.name
            FROM tags t LEFT JOIN tag_groups g ON g.id = t.group_id LEFT JOIN tags pt ON pt.id = t.parent_tag_id ORDER BY t.normalized_name;
            """,
            r =>
            {
                var links = parents[r.GetInt64(0)].ToList();
                return new TagSummary(r.GetInt64(0), r.GetString(1), r.GetInt64(2),
                    r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4),
                    links.Select(l => l.ParentId).ToList(), links.Select(l => l.ParentName).ToList(),
                    aliases[r.GetInt64(0)].ToList())
                {
                    ParentTagId = r.IsDBNull(5) ? null : r.GetInt64(5),
                    ParentTagName = r.IsDBNull(6) ? null : r.GetString(6),
                    ChildTagNames = children[r.GetInt64(0)].ToList(),
                };
            });
    }

    // ---- 同じグループの中の親のタグ(サブカテゴリ、決定ログ EC-1〜EC-9) ----

    // 親は同じグループ(自由タグなら自由タグ)のタグ 1 つだけ。自分や自分の子孫は親にできない。null で親なし
    public void SetTagParentTag(long tagId, long? parentTagId)
    {
        if (parentTagId is { } parentId)
        {
            var tags = GetTags().ToDictionary(t => t.Id);
            if (!tags.TryGetValue(tagId, out var tag) || !tags.TryGetValue(parentId, out var parent))
            {
                throw new CatalogException("タグが見つかりません。");
            }
            if (parentId == tagId)
            {
                throw new CatalogException("自分自身は大分類にできません。");
            }
            if (tag.GroupId != parent.GroupId)
            {
                throw new CatalogException("大分類にできるのは、同じタググループのタグだけです。");
            }
            if (TagTree.Descendants(tagId, tags.ToDictionary(t => t.Key, t => t.Value.ParentTagId)).Contains(parentId))
            {
                throw new CatalogException($"「{parent.Name}」は「{tag.Name}」の下にある小分類のため、大分類にできません。");
            }
        }
        Execute("UPDATE tags SET parent_tag_id = $parent WHERE id = $id;", ("$parent", (object?)parentTagId ?? DBNull.Value), ("$id", tagId));
    }

    // 決定ログ EC-6: このタグ(とその子孫)が付いているのに、その親(上の親も)が付いていない PDF
    public IReadOnlyList<string> DocumentsMissingAncestors(long tagId)
    {
        var subtree = TagTree.Descendants(tagId, ParentTagMap()).Append(tagId).ToHashSet();
        return Query($"""
            {AncestorsCte}
            SELECT DISTINCT dt.document_id FROM document_tags dt JOIN anc a ON a.tag_id = dt.tag_id
            WHERE dt.tag_id IN ({string.Join(",", subtree)})
              AND NOT EXISTS (SELECT 1 FROM document_tags x WHERE x.document_id = dt.document_id AND x.tag_id = a.ancestor_id)
            ORDER BY dt.document_id;
            """, r => r.GetString(0));
    }

    // 決定ログ EC-3・EC-6: PDF に付いているタグの親(上の親も)を付ける。付けた PDF の変更日を now にする
    public int AddMissingAncestors(IReadOnlyCollection<string> documentIds, DateTimeOffset now)
    {
        if (documentIds.Count == 0)
        {
            return 0;
        }
        using var transaction = connection.BeginTransaction();
        Execute("CREATE TEMP TABLE IF NOT EXISTS ancestor_docs (id TEXT PRIMARY KEY); DELETE FROM temp.ancestor_docs;", transaction);
        foreach (var id in documentIds.Distinct())
        {
            Execute("INSERT INTO temp.ancestor_docs (id) VALUES ($id);", transaction, ("$id", id));
        }
        Execute($"""
            {AncestorsCte}
            UPDATE documents SET updated_at = $now WHERE id IN (
                SELECT dt.document_id FROM document_tags dt JOIN anc a ON a.tag_id = dt.tag_id
                WHERE dt.document_id IN (SELECT id FROM temp.ancestor_docs)
                  AND NOT EXISTS (SELECT 1 FROM document_tags x WHERE x.document_id = dt.document_id AND x.tag_id = a.ancestor_id));
            """, transaction, ("$now", now.UtcDateTime.ToString("O")));
        using var command = CreateCommand($"""
            {AncestorsCte}
            INSERT OR IGNORE INTO document_tags (document_id, tag_id)
            SELECT DISTINCT dt.document_id, a.ancestor_id FROM document_tags dt JOIN anc a ON a.tag_id = dt.tag_id
            WHERE dt.document_id IN (SELECT id FROM temp.ancestor_docs);
            """, transaction, []);
        var added = command.ExecuteNonQuery();
        transaction.Commit();
        return added;
    }

    // タグ → 親・その上の親…(すべての組)
    private const string AncestorsCte = """
        WITH RECURSIVE anc(tag_id, ancestor_id) AS (
            SELECT id, parent_tag_id FROM tags WHERE parent_tag_id IS NOT NULL
            UNION
            SELECT a.tag_id, t.parent_tag_id FROM anc a JOIN tags t ON t.id = a.ancestor_id WHERE t.parent_tag_id IS NOT NULL AND t.parent_tag_id <> a.tag_id)
        """;

    private Dictionary<long, long?> ParentTagMap() =>
        Query("SELECT id, parent_tag_id FROM tags;", r => (Id: r.GetInt64(0), Parent: r.IsDBNull(1) ? (long?)null : r.GetInt64(1)))
            .ToDictionary(x => x.Id, x => x.Parent);

    // グループが変わって親と子のグループが合わなくなったら、親を外す。輪になったものも外す
    private void RemoveInvalidParentTags(SqliteTransaction transaction)
    {
        Execute("""
            UPDATE tags SET parent_tag_id = NULL WHERE parent_tag_id IS NOT NULL
              AND (parent_tag_id = id OR COALESCE(group_id, -1) <> COALESCE((SELECT p.group_id FROM tags p WHERE p.id = tags.parent_tag_id), -1));
            """, transaction);
        var parents = new Dictionary<long, long?>();
        using (var command = CreateCommand("SELECT id, parent_tag_id FROM tags;", transaction, []))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                parents[reader.GetInt64(0)] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
        }
        foreach (var (id, parent) in parents.ToList())
        {
            if (parent is not null && TagTree.Ancestors(id, parents).Count > 0 && LoopsBack(id, parents))
            {
                parents[id] = null;
                Execute("UPDATE tags SET parent_tag_id = NULL WHERE id = $id;", transaction, ("$id", id));
            }
        }
    }

    private static bool LoopsBack(long id, IReadOnlyDictionary<long, long?> parents)
    {
        var seen = new HashSet<long>();
        for (long? current = id; current is { } value; current = parents.GetValueOrDefault(value))
        {
            if (!seen.Add(value))
            {
                return value == id;
            }
        }
        return false;
    }

    public long AddTag(string name, long? groupId = null, IReadOnlyCollection<long>? parentIds = null)
    {
        var cleaned = ValidateName(name, "タグ名");
        if (FindTagId(cleaned) is not null)
        {
            throw new CatalogException("同じ名前のタグがすでにあります(タググループが違っても同じ名前は使えません)。");
        }
        EnsureNotAlias(cleaned, exceptTagId: null);
        var id = InsertTag(cleaned, groupId);
        if (parentIds is { Count: > 0 })
        {
            SetTagParents(id, parentIds);
        }
        return id;
    }

    // 取り込み時の入力用(決定ログ A-3): 同じ名前のタグがあればそれを使い(グループ・紐づけは変えない)、なければ作る(Q-12)。
    // 別名と同じ名前なら、その別名を持つ正式なタグを使う(決定ログ DE-2)
    public long GetOrCreateTag(string name, long? groupId = null, IReadOnlyCollection<long>? parentIdsForNewTag = null)
    {
        var cleaned = ValidateName(name, "タグ名");
        if ((FindTagId(cleaned) ?? FindAliasOwner(cleaned)) is { } existing)
        {
            return existing;
        }
        var id = InsertTag(cleaned, groupId);
        if (parentIdsForNewTag is { Count: > 0 })
        {
            SetTagParents(id, parentIdsForNewTag, ignoreInvalid: true);
        }
        return id;
    }

    // グループを変えると、合わなくなった紐づけは外れる
    public void SetTagGroup(long tagId, long? groupId)
    {
        using var transaction = connection.BeginTransaction();
        Execute("UPDATE tags SET group_id = $group WHERE id = $id;", transaction, ("$group", (object?)groupId ?? DBNull.Value), ("$id", tagId));
        RemoveInvalidLinks(transaction);
        // 決定ログ EC-1: 親子は同じグループの中だけ
        RemoveInvalidParentTags(transaction);
        transaction.Commit();
    }

    public long CountLinksBrokenByTagGroup(long tagId, long? groupId) =>
        CountBrokenLinks(t => Execute("UPDATE tags SET group_id = $group WHERE id = $id;", t, ("$group", (object?)groupId ?? DBNull.Value), ("$id", tagId)));

    // 決定ログ Q-3: 紐づけ先は 1 つ上の順位のタグだけ(複数可)
    public void SetTagParents(long tagId, IEnumerable<long> parentIds, bool ignoreInvalid = false)
    {
        var tags = GetTags().ToDictionary(t => t.Id);
        var ranks = GetGroups().ToDictionary(g => g.Id, g => g.Rank);
        int? RankOf(long id) => tags.TryGetValue(id, out var tag) && tag.GroupId is { } group ? ranks[group] : null;

        var valid = new List<long>();
        foreach (var parentId in parentIds.Distinct().Where(p => p != tagId))
        {
            if (RankOf(tagId) is { } rank && rank >= 2 && RankOf(parentId) == rank - 1)
            {
                valid.Add(parentId);
            }
            else if (!ignoreInvalid)
            {
                throw new CatalogException("紐づけられるのは、1 つ上の順位のタググループのタグだけです。");
            }
        }
        using var transaction = connection.BeginTransaction();
        Execute("DELETE FROM tag_links WHERE child_id = $id;", transaction, ("$id", tagId));
        foreach (var parentId in valid)
        {
            Execute("INSERT INTO tag_links (child_id, parent_id) VALUES ($child, $parent);", transaction, ("$child", tagId), ("$parent", parentId));
        }
        transaction.Commit();
    }

    // 決定ログ HC-4・HC-5: 配下のタグの欄から 1 つ足す。parentTagId のタグに紐づく、1 つ下の順位のタグ(childGroupId のグループ)を作る。
    // 同じ名前のタグ(別名も)が 1 つ下の順位のグループに既にあれば、新しく作らずに紐づけを足す。戻り値は (タグ ID, 新しく作ったか)
    public (long TagId, bool Created) AddChildTag(long parentTagId, string name, long childGroupId)
    {
        var cleaned = ValidateName(name, "タグ名");
        var ranks = GetGroups().ToDictionary(g => g.Id, g => g.Rank);
        var tags = GetTags().ToDictionary(t => t.Id);
        if (!tags.TryGetValue(parentTagId, out var parent) || parent.GroupId is not { } parentGroup || ranks[parentGroup] is < 1 or >= TagGroup.MaxRank)
        {
            throw new CatalogException("配下のタグを足せるのは、順位 1〜4 のタググループのタグだけです。");
        }
        var childRank = ranks[parentGroup] + 1;
        if (!ranks.TryGetValue(childGroupId, out var groupRank) || groupRank != childRank)
        {
            throw new CatalogException($"入れるグループは「{TagRanks.Label(childRank)}」のタググループから選んでください。");
        }
        if ((FindTagId(cleaned) ?? FindAliasOwner(cleaned)) is { } existingId)
        {
            var existing = tags[existingId];
            if (existing.GroupId is not { } existingGroup || ranks[existingGroup] != childRank)
            {
                throw new CatalogException($"「{existing.Name}」は「{existing.GroupLabel}」のタグとしてすでにあります。「{TagRanks.Label(childRank)}」のタグにするには、先にそのタグのグループを変えてください(タググループが違っても同じ名前は使えません)。");
            }
            if (!existing.ParentIds.Contains(parentTagId))
            {
                SetTagParents(existingId, existing.ParentIds.Append(parentTagId));
            }
            return (existingId, false);
        }
        var id = InsertTag(cleaned, childGroupId);
        SetTagParents(id, [parentTagId]);
        return (id, true);
    }

    // 決定ログ HC-6: 配下のタグの欄の「×」。紐づけだけを外す(タグとほかの紐づけ、PDF のタグはそのまま)
    public void RemoveTagParent(long tagId, long parentTagId) =>
        Execute("DELETE FROM tag_links WHERE child_id = $child AND parent_id = $parent;", null, ("$child", tagId), ("$parent", parentTagId));

    public void RenameTag(long tagId, string name)
    {
        var cleaned = ValidateName(name, "タグ名");
        if (FindTagId(cleaned) is { } existing && existing != tagId)
        {
            throw new CatalogException("同じ名前のタグがすでにあります。1 つにまとめる場合は「統合」を使ってください。");
        }
        EnsureNotAlias(cleaned, exceptTagId: tagId);
        using var transaction = connection.BeginTransaction();
        // 自分の別名と同じ名前に変えたときは、その別名はいらなくなる
        Execute("DELETE FROM tag_aliases WHERE tag_id = $id AND normalized_name = $normalized;", transaction,
            ("$id", tagId), ("$normalized", NameNormalizer.Normalize(cleaned)));
        Execute("UPDATE tags SET name = $name, normalized_name = $normalized WHERE id = $id;", transaction,
            ("$name", cleaned), ("$normalized", NameNormalizer.Normalize(cleaned)), ("$id", tagId));
        transaction.Commit();
    }

    // 決定ログ DE-1: 統合すると、消えるタグの名前と別名は、統合先の別名として残す(その名前で検索・入力しても統合先が見つかる)
    public void MergeTags(long sourceTagId, long targetTagId)
    {
        if (sourceTagId == targetTagId)
        {
            throw new CatalogException("統合先には別のタグを選んでください。");
        }
        var sourceName = GetTags().FirstOrDefault(t => t.Id == sourceTagId)?.Name ?? throw new CatalogException("タグが見つかりません。");
        using var transaction = connection.BeginTransaction();
        Execute("UPDATE tag_aliases SET tag_id = $target WHERE tag_id = $source;", transaction, ("$target", targetTagId), ("$source", sourceTagId));
        // tags の normalized_name と同じ値になるが、このあと消すので重ならない
        Execute("INSERT INTO tag_aliases (tag_id, name, normalized_name) VALUES ($target, $name, $normalized) ON CONFLICT(normalized_name) DO NOTHING;", transaction,
            ("$target", targetTagId), ("$name", sourceName), ("$normalized", NameNormalizer.Normalize(sourceName)));
        Execute("INSERT OR IGNORE INTO document_tags (document_id, tag_id) SELECT document_id, $target FROM document_tags WHERE tag_id = $source;",
            transaction, ("$target", targetTagId), ("$source", sourceTagId));
        // 紐づけも統合先に引き継ぐ(統合先のグループで合わなくなるものは外れる)
        Execute("""
            INSERT OR IGNORE INTO tag_links (child_id, parent_id) SELECT $target, parent_id FROM tag_links WHERE child_id = $source AND parent_id <> $target;
            INSERT OR IGNORE INTO tag_links (child_id, parent_id) SELECT child_id, $target FROM tag_links WHERE parent_id = $source AND child_id <> $target;
            """, transaction, ("$target", targetTagId), ("$source", sourceTagId));
        // 決定ログ EC-8: 親子も引き継ぐ(子は統合先の子に、統合先に親がなければ消えるタグの親を使う)
        Execute("""
            UPDATE tags SET parent_tag_id = $target WHERE parent_tag_id = $source AND id <> $target;
            UPDATE tags SET parent_tag_id = (SELECT parent_tag_id FROM tags WHERE id = $source)
                WHERE id = $target AND parent_tag_id IS NULL AND (SELECT parent_tag_id FROM tags WHERE id = $source) <> $target;
            """, transaction, ("$target", targetTagId), ("$source", sourceTagId));
        Execute("DELETE FROM tags WHERE id = $source;", transaction, ("$source", sourceTagId));
        RemoveInvalidLinks(transaction);
        RemoveInvalidParentTags(transaction);
        transaction.Commit();
    }

    public void DeleteTag(long tagId) => Execute("DELETE FROM tags WHERE id = $id;", ("$id", tagId));

    // 決定ログ DE-4: 別名をまとめて置き換える。別名はタグ名やほかのタグの別名と重ならない
    public void SetAliases(long tagId, IEnumerable<string> names)
    {
        var cleaned = new List<string>();
        foreach (var name in names.Where(n => NameNormalizer.Clean(n).Length > 0))
        {
            var alias = ValidateName(name, "別名");
            var normalized = NameNormalizer.Normalize(alias);
            if (cleaned.Any(c => NameNormalizer.Normalize(c) == normalized))
            {
                continue;
            }
            if (FindTagId(alias) is { } owner)
            {
                throw new CatalogException(owner == tagId
                    ? $"「{alias}」はこのタグの名前です。別名には別の名前を入れてください。"
                    : $"「{alias}」というタグがあります。1 つにまとめる場合は「統合」を使ってください。");
            }
            EnsureNotAlias(alias, exceptTagId: tagId);
            cleaned.Add(alias);
        }
        using var transaction = connection.BeginTransaction();
        Execute("DELETE FROM tag_aliases WHERE tag_id = $id;", transaction, ("$id", tagId));
        foreach (var alias in cleaned)
        {
            Execute("INSERT INTO tag_aliases (tag_id, name, normalized_name) VALUES ($id, $name, $normalized);", transaction,
                ("$id", tagId), ("$name", alias), ("$normalized", NameNormalizer.Normalize(alias)));
        }
        transaction.Commit();
    }

    // 別名と同じ名前のタグは作れない(別名を持つタグの名前を知らせる)
    private void EnsureNotAlias(string name, long? exceptTagId)
    {
        if (FindAliasOwner(name) is { } owner && owner != exceptTagId)
        {
            var ownerName = GetTags().First(t => t.Id == owner).Name;
            throw new CatalogException($"「{NameNormalizer.Clean(name)}」は「{ownerName}」の別名です。そのタグを使うか、先に別名を外してください。");
        }
    }

    private long? FindAliasOwner(string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tag_id FROM tag_aliases WHERE normalized_name = $normalized;";
        command.Parameters.AddWithValue("$normalized", NameNormalizer.Normalize(name));
        return command.ExecuteScalar() as long?;
    }

    // ---- 共通 ----

    private FieldDefinition GetField(long fieldId) =>
        GetFields().FirstOrDefault(f => f.Id == fieldId) ?? throw new CatalogException("項目が見つかりません。");

    private (FieldDefinition Field, FieldOption Option) GetOption(long optionId)
    {
        foreach (var field in GetFields())
        {
            if (field.Options.FirstOrDefault(o => o.Id == optionId) is { } option)
            {
                return (field, option);
            }
        }
        throw new CatalogException("選択肢が見つかりません。");
    }

    private long? FindTagId(string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM tags WHERE normalized_name = $normalized;";
        command.Parameters.AddWithValue("$normalized", NameNormalizer.Normalize(name));
        return command.ExecuteScalar() as long?;
    }

    private long InsertTag(string cleanedName, long? groupId) => InsertReturningId(
        "INSERT INTO tags (name, normalized_name, group_id) VALUES ($name, $normalized, $group) RETURNING id;",
        ("$name", cleanedName), ("$normalized", NameNormalizer.Normalize(cleanedName)), ("$group", (object?)groupId ?? DBNull.Value));

    private static void ValidateRank(int rank)
    {
        if (rank is < TagGroup.IndependentRank or > TagGroup.MaxRank)
        {
            throw new CatalogException($"順位は 1〜{TagGroup.MaxRank} か「独立」を選んでください。");
        }
    }

    // 紐づけは「順位 2 以降のタグ → 1 つ上の順位のタグ」だけが有効。グループや順位が変わって合わなくなったものを外す
    private const string InvalidLinks = """
        SELECT l.child_id, l.parent_id FROM tag_links l
        JOIN tags c ON c.id = l.child_id LEFT JOIN tag_groups cg ON cg.id = c.group_id
        JOIN tags p ON p.id = l.parent_id LEFT JOIN tag_groups pg ON pg.id = p.group_id
        WHERE cg.rank IS NULL OR pg.rank IS NULL OR cg.rank < 2 OR pg.rank <> cg.rank - 1
        """;

    private void RemoveInvalidLinks(SqliteTransaction transaction) => Execute(
        $"DELETE FROM tag_links WHERE (child_id, parent_id) IN ({InvalidLinks});", transaction);

    // 変更を試しに行い、外れる紐づけの件数を数えて元に戻す
    private long CountBrokenLinks(Action<SqliteTransaction> change)
    {
        using var transaction = connection.BeginTransaction();
        change(transaction);
        using var command = CreateCommand($"SELECT COUNT(*) FROM ({InvalidLinks});", transaction, []);
        var count = (long)command.ExecuteScalar()!;
        transaction.Rollback();
        return count;
    }

    private static string ValidateName(string name, string label)
    {
        var cleaned = NameNormalizer.Clean(name);
        if (cleaned.Length == 0)
        {
            throw new CatalogException($"{label}を入力してください。");
        }
        if (cleaned.Length > MaxNameLength)
        {
            throw new CatalogException($"{label}は {MaxNameLength} 文字以内で入力してください。");
        }
        return cleaned;
    }

    private static void EnsureUnique(string name, IEnumerable<(long Id, string Name)> existing, long? exceptId, string message)
    {
        var normalized = NameNormalizer.Normalize(name);
        if (existing.Any(e => e.Id != exceptId && NameNormalizer.Normalize(e.Name) == normalized))
        {
            throw new CatalogException(message);
        }
    }

    private void Reorder(string table, List<long> orderedIds, long id, int offset)
    {
        var index = orderedIds.IndexOf(id);
        var newIndex = index + offset;
        if (index < 0 || newIndex < 0 || newIndex >= orderedIds.Count)
        {
            return;
        }
        orderedIds.RemoveAt(index);
        orderedIds.Insert(newIndex, id);

        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            Execute($"UPDATE {table} SET sort_order = $order WHERE id = $id;", transaction, ("$order", i + 1), ("$id", orderedIds[i]));
        }
        transaction.Commit();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map)
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

    private long InsertReturningId(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(sql, null, parameters);
        return (long)command.ExecuteScalar()!;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters) => Execute(sql, null, parameters);

    private void Execute(string sql, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(sql, transaction, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand CreateCommand(string sql, SqliteTransaction? transaction, (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command;
    }
}
