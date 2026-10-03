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

    // ---- 自由タグ ----

    public IReadOnlyList<TagSummary> GetTags()
    {
        var attributes = Query(
            """
            SELECT a.tag_id, o.id, f.name, o.value
            FROM tag_attributes a
            JOIN field_options o ON o.id = a.option_id
            JOIN fields f ON f.id = o.field_id
            ORDER BY f.sort_order, f.id, o.sort_order, o.id;
            """,
            r => (TagId: r.GetInt64(0), Attribute: new TagAttribute(r.GetInt64(1), r.GetString(2), r.GetString(3))))
            .ToLookup(x => x.TagId, x => x.Attribute);

        return Query(
            "SELECT t.id, t.name, (SELECT COUNT(*) FROM document_tags d WHERE d.tag_id = t.id) FROM tags t ORDER BY t.normalized_name;",
            r => new TagSummary(r.GetInt64(0), r.GetString(1), r.GetInt64(2), attributes[r.GetInt64(0)].ToList()));
    }

    // 指定した選択肢を属性として持つタグだけにする(決定ログ D-1、docs/04「自由タグの属性」)
    public void SetTagAttributes(long tagId, IEnumerable<long> optionIds)
    {
        using var transaction = connection.BeginTransaction();
        Execute("DELETE FROM tag_attributes WHERE tag_id = $tag;", transaction, ("$tag", tagId));
        foreach (var optionId in optionIds.Distinct())
        {
            Execute("INSERT INTO tag_attributes (tag_id, option_id) VALUES ($tag, $option);", transaction,
                ("$tag", tagId), ("$option", optionId));
        }
        transaction.Commit();
    }

    public long AddTag(string name)
    {
        var cleaned = ValidateName(name, "タグ名");
        if (FindTagId(cleaned) is not null)
        {
            throw new CatalogException("同じ名前のタグがすでにあります。");
        }
        return InsertTag(cleaned);
    }

    // 取り込み時の入力用(決定ログ A-3): 同じ名前のタグがあればそれを使い、なければ登録する
    public long GetOrCreateTag(string name) => GetOrCreateTag(name, []);

    // 決定ログ D-4: 新しく作るときだけ、指定した選択肢を属性として付ける(既存のタグの属性は変えない)
    public long GetOrCreateTag(string name, IReadOnlyCollection<long> attributeOptionIdsForNewTag)
    {
        var cleaned = ValidateName(name, "タグ名");
        if (FindTagId(cleaned) is { } existing)
        {
            return existing;
        }
        var id = InsertTag(cleaned);
        if (attributeOptionIdsForNewTag.Count > 0)
        {
            SetTagAttributes(id, attributeOptionIdsForNewTag);
        }
        return id;
    }

    public void RenameTag(long tagId, string name)
    {
        var cleaned = ValidateName(name, "タグ名");
        if (FindTagId(cleaned) is { } existing && existing != tagId)
        {
            throw new CatalogException("同じ名前のタグがすでにあります。1 つにまとめる場合は「統合」を使ってください。");
        }
        Execute("UPDATE tags SET name = $name, normalized_name = $normalized WHERE id = $id;",
            ("$name", cleaned), ("$normalized", NameNormalizer.Normalize(cleaned)), ("$id", tagId));
    }

    public void MergeTags(long sourceTagId, long targetTagId)
    {
        if (sourceTagId == targetTagId)
        {
            throw new CatalogException("統合先には別のタグを選んでください。");
        }
        using var transaction = connection.BeginTransaction();
        Execute("INSERT OR IGNORE INTO document_tags (document_id, tag_id) SELECT document_id, $target FROM document_tags WHERE tag_id = $source;",
            transaction, ("$target", targetTagId), ("$source", sourceTagId));
        Execute("INSERT OR IGNORE INTO tag_attributes (tag_id, option_id) SELECT $target, option_id FROM tag_attributes WHERE tag_id = $source;",
            transaction, ("$target", targetTagId), ("$source", sourceTagId));
        Execute("DELETE FROM tags WHERE id = $source;", transaction, ("$source", sourceTagId));
        transaction.Commit();
    }

    public void DeleteTag(long tagId) => Execute("DELETE FROM tags WHERE id = $id;", ("$id", tagId));

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

    private long InsertTag(string cleanedName) => InsertReturningId(
        "INSERT INTO tags (name, normalized_name) VALUES ($name, $normalized) RETURNING id;",
        ("$name", cleanedName), ("$normalized", NameNormalizer.Normalize(cleanedName)));

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
