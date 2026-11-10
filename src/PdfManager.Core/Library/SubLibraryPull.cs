using Microsoft.Data.Sqlite;
using PdfManager.Core.Documents;
using PdfManager.Core.Storage;

namespace PdfManager.Core.Library;

// ほかの PC で変わったサブを読み込むときの見積もりと結果(決定ログ EB-2・EB-3)
public sealed record SubPullPlan(
    int Added,
    int Updated,
    int Removed,
    int Aligned,
    IReadOnlyList<string> RemovedTitles,
    IReadOnlyList<string> LostTitles)
{
    public int LostChanges => LostTitles.Count;

    public bool HasChanges => Added + Updated + Removed + Aligned > 0;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Added > 0) parts.Add($"増えた PDF {Added:N0} 件");
            if (Updated > 0) parts.Add($"情報が変わった PDF {Updated:N0} 件");
            if (Removed > 0) parts.Add($"なくなった PDF {Removed:N0} 件");
            if (Aligned > 0) parts.Add($"保存先をこのサブに合わせた PDF {Aligned:N0} 件");
            return parts.Count == 0 ? "変わった PDF はありません" : string.Join("、", parts);
        }
    }
}

// ほかの PC で変わったサブの内容を、この PC のメインの写しへ読み込む(決定ログ EB-2〜EB-4、S-12、S-14)。
// サブの内容を正とする。タグ・グループ・補助項目は番号ではなく名前で突き合わせ、メインにないものは作る。
// メインでこのサブにあることになっているのにサブにない文書は、サブのフォルダに PDF があればこの PC が書き込めなかった分として残し、
// PDF もなければほかの PC で移した・削除したものとしてメインの写しから外す
public static class SubLibraryPull
{
    private sealed record Signature(string Id, string? StorageId, string UpdatedAt, string Text, string Title);

    public static SubPullPlan Plan(SqliteConnection main, SubLibraryRecord record, LibraryInfo sub) =>
        WithSub(main, sub, () => BuildPlan(main, record, sub).Plan);

    // 読み込んだあと、呼び出し側でサブの合言葉を覚え直し、メインの内容をサブへ書き戻す(定義と、この PC で残した文書のため)。
    // 戻り値の DefinitionsAdded が true なら、ほかのサブにも定義を書き直す印を付ける
    public static (SubPullPlan Plan, bool DefinitionsAdded) Apply(SqliteConnection main, SubLibraryRecord record, LibraryInfo sub, DataFolder mainFolder)
    {
        List<(string Id, string Title)> removing = [];
        List<string> localPdfsToDelete = [];
        var definitionsAdded = false;
        var built = WithSub(main, sub, () =>
        {
            var plan = BuildPlan(main, record, sub);
            using var transaction = main.BeginTransaction();
            var maps = MapDefinitions(main, transaction, out definitionsAdded);
            CopyDocuments(main, transaction, record.Id, maps, plan.SubIds);
            transaction.Commit();
            removing = plan.Removing;
            localPdfsToDelete = plan.AlignedFromMain;
            return plan;
        });

        // サムネイルはメインにもいつも置く(決定ログ S-11)
        foreach (var id in built.SubIds)
        {
            var target = mainFolder.GetThumbnailPath(id);
            var source = sub.DataFolder.GetThumbnailPath(id);
            if (!File.Exists(target) && File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
        }
        // メインに置いていた同じ文書の PDF は、サブに同じものがあるので消す
        foreach (var id in localPdfsToDelete)
        {
            TryDelete(mainFolder.GetPdfPath(id));
        }
        if (removing.Count > 0)
        {
            var links = new LinkRepository(main);
            var documents = new DocumentRepository(main);
            foreach (var (id, _) in removing)
            {
                links.RemoveFromChain(id);
                documents.Delete(id);
                TryDelete(mainFolder.GetThumbnailPath(id));
            }
            new VersionRepository(main).DeleteEmptySeries();
        }
        return (built.Plan, definitionsAdded);
    }

    private sealed record Built(SubPullPlan Plan, IReadOnlyList<string> SubIds, List<(string Id, string Title)> Removing, List<string> AlignedFromMain);

    private static Built BuildPlan(SqliteConnection main, SubLibraryRecord record, LibraryInfo sub)
    {
        var inSub = ReadSignatures(main, "sub", "1 = 1").ToDictionary(s => s.Id);
        var inMain = ReadSignatures(main, "main", "d.id IN (SELECT id FROM sub.documents) OR d.archive_volume_id = $sub", ("$sub", record.Id))
            .ToDictionary(s => s.Id);

        int added = 0, updated = 0, aligned = 0;
        var lost = new List<string>();
        var alignedFromMain = new List<string>();
        foreach (var (id, theirs) in inSub)
        {
            if (!inMain.TryGetValue(id, out var ours))
            {
                added++;
                continue;
            }
            if (ours.StorageId != record.Id)
            {
                aligned++;
                if (ours.StorageId is null)
                {
                    alignedFromMain.Add(id);
                }
                continue;
            }
            if (ours.Text != theirs.Text)
            {
                updated++;
                // 決定ログ EB-3: この PC にサブへ書き込めていない変更があり、それがサブの内容より新しい
                if (record.NeedsPush && string.CompareOrdinal(ours.UpdatedAt, theirs.UpdatedAt) > 0)
                {
                    lost.Add(ours.Title);
                }
            }
        }

        var removing = new List<(string Id, string Title)>();
        foreach (var ours in inMain.Values.Where(s => s.StorageId == record.Id && !inSub.ContainsKey(s.Id)))
        {
            if (!File.Exists(sub.DataFolder.GetPdfPath(ours.Id)))
            {
                removing.Add((ours.Id, ours.Title));
            }
        }
        var plan = new SubPullPlan(added, updated, removing.Count, aligned, removing.Select(r => r.Title).ToList(), lost);
        return new Built(plan, inSub.Keys.ToList(), removing, alignedFromMain);
    }

    // 番号に左右されないよう、タグ・補助項目は名前で並べて比べる
    private static List<Signature> ReadSignatures(SqliteConnection connection, string schema, string where, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.id, d.archive_volume_id, d.updated_at, d.title,
                   d.title || char(30) || d.memo || char(30) || d.rating || char(30) || d.file_name || char(30) || d.file_name_suffix || char(30)
                   || d.version_label || char(30) || COALESCE(d.series_id, '') || char(30) || COALESCE(d.version_no, '') || char(30)
                   || COALESCE((SELECT group_concat(n, char(31)) FROM (SELECT t.normalized_name AS n FROM {schema}.document_tags dt
                                JOIN {schema}.tags t ON t.id = dt.tag_id WHERE dt.document_id = d.id ORDER BY 1)), '') || char(30)
                   || COALESCE((SELECT group_concat(v, char(31)) FROM (SELECT f.name || '=' || COALESCE(fv.value_text, '') || '|'
                                || COALESCE(fv.value_number, '') || '|' || COALESCE(fv.value_date, '') AS v FROM {schema}.document_field_values fv
                                JOIN {schema}.fields f ON f.id = fv.field_id WHERE fv.document_id = d.id ORDER BY 1)), '')
            FROM {schema}.documents d WHERE {where};
            """;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        using var reader = command.ExecuteReader();
        var rows = new List<Signature>();
        while (reader.Read())
        {
            rows.Add(new Signature(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(4), reader.GetString(3)));
        }
        return rows;
    }

    private sealed record DefinitionMaps(Dictionary<long, long> Tags, Dictionary<long, long> Fields);

    // 決定ログ EB-4: グループ・タグ(別名を含む)・補助項目を名前で突き合わせ、メインにないものは作る
    private static DefinitionMaps MapDefinitions(SqliteConnection main, SqliteTransaction transaction, out bool added)
    {
        added = false;
        var groups = new Dictionary<long, long>();
        var mainGroups = Rows(main, transaction, "SELECT normalized_name, id FROM main.tag_groups;", r => (r.GetString(0), r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2);
        foreach (var g in Rows(main, transaction, "SELECT id, name, normalized_name, rank, is_pinned, pin_order, sort_order FROM sub.tag_groups;",
                     r => (Id: r.GetInt64(0), Name: r.GetString(1), Normalized: r.GetString(2), Rank: r.GetInt64(3), Pinned: r.GetInt64(4), PinOrder: r.GetInt64(5), Sort: r.GetInt64(6))))
        {
            if (!mainGroups.TryGetValue(g.Normalized, out var id))
            {
                id = Scalar(main, transaction, """
                    INSERT INTO main.tag_groups (name, normalized_name, rank, is_pinned, pin_order, sort_order)
                    VALUES ($name, $n, $rank, $pinned, $pinOrder, $sort) RETURNING id;
                    """, ("$name", g.Name), ("$n", g.Normalized), ("$rank", g.Rank), ("$pinned", g.Pinned), ("$pinOrder", g.PinOrder), ("$sort", g.Sort));
                mainGroups[g.Normalized] = id;
                added = true;
            }
            groups[g.Id] = id;
        }

        var tags = new Dictionary<long, long>();
        var mainTags = Rows(main, transaction, "SELECT normalized_name, id FROM main.tags UNION ALL SELECT normalized_name, tag_id FROM main.tag_aliases;",
            r => (r.GetString(0), r.GetInt64(1))).GroupBy(x => x.Item1).ToDictionary(x => x.Key, x => x.First().Item2);
        foreach (var t in Rows(main, transaction, "SELECT id, name, normalized_name, group_id FROM sub.tags;",
                     r => (Id: r.GetInt64(0), Name: r.GetString(1), Normalized: r.GetString(2), GroupId: r.IsDBNull(3) ? (long?)null : r.GetInt64(3))))
        {
            if (!mainTags.TryGetValue(t.Normalized, out var id))
            {
                object group = t.GroupId is { } g && groups.TryGetValue(g, out var mapped) ? mapped : DBNull.Value;
                id = Scalar(main, transaction, "INSERT INTO main.tags (name, normalized_name, group_id) VALUES ($name, $n, $group) RETURNING id;",
                    ("$name", t.Name), ("$n", t.Normalized), ("$group", group));
                mainTags[t.Normalized] = id;
                added = true;
            }
            tags[t.Id] = id;
        }
        // 決定ログ EC-9: 同じグループの中の親。この PC で親を決めていないタグだけ、サブに合わせる
        foreach (var (child, parent) in Rows(main, transaction, "SELECT id, parent_tag_id FROM sub.tags WHERE parent_tag_id IS NOT NULL;", r => (r.GetInt64(0), r.GetInt64(1))))
        {
            if (tags.TryGetValue(child, out var c) && tags.TryGetValue(parent, out var p) && c != p)
            {
                added |= Execute(main, transaction, """
                    UPDATE main.tags SET parent_tag_id = $p WHERE id = $c AND parent_tag_id IS NULL
                      AND COALESCE(group_id, -1) = COALESCE((SELECT group_id FROM main.tags WHERE id = $p), -1)
                      AND NOT EXISTS (WITH RECURSIVE up(id) AS (SELECT $p UNION SELECT t.parent_tag_id FROM main.tags t JOIN up ON t.id = up.id WHERE t.parent_tag_id IS NOT NULL)
                                      SELECT 1 FROM up WHERE id = $c);
                    """, ("$c", c), ("$p", p)) > 0;
            }
        }
        foreach (var (child, parent) in Rows(main, transaction, "SELECT child_id, parent_id FROM sub.tag_links;", r => (r.GetInt64(0), r.GetInt64(1))))
        {
            if (tags.TryGetValue(child, out var c) && tags.TryGetValue(parent, out var p) && c != p)
            {
                added |= Execute(main, transaction, "INSERT OR IGNORE INTO main.tag_links (child_id, parent_id) VALUES ($c, $p);", ("$c", c), ("$p", p)) > 0;
            }
        }

        // 補助項目は名前と型が同じものに合わせる。名前が同じで型が違うものは値を読み込まない
        var fields = new Dictionary<long, long>();
        var mainFields = Rows(main, transaction, "SELECT name, id, type FROM main.fields;", r => (r.GetString(0), r.GetInt64(1), r.GetString(2)))
            .ToDictionary(x => x.Item1, x => (Id: x.Item2, Type: x.Item3));
        foreach (var f in Rows(main, transaction, "SELECT id, name, type, sort_order FROM sub.fields;",
                     r => (Id: r.GetInt64(0), Name: r.GetString(1), Type: r.GetString(2), Sort: r.GetInt64(3))))
        {
            if (mainFields.TryGetValue(f.Name, out var existing))
            {
                if (existing.Type == f.Type)
                {
                    fields[f.Id] = existing.Id;
                }
                continue;
            }
            var id = Scalar(main, transaction, "INSERT INTO main.fields (name, type, sort_order) VALUES ($name, $type, $sort) RETURNING id;",
                ("$name", f.Name), ("$type", f.Type), ("$sort", f.Sort));
            mainFields[f.Name] = (id, f.Type);
            fields[f.Id] = id;
            added = true;
        }
        foreach (var o in Rows(main, transaction, "SELECT field_id, value, sort_order FROM sub.field_options;",
                     r => (FieldId: r.GetInt64(0), Value: r.GetString(1), Sort: r.GetInt64(2))))
        {
            if (fields.TryGetValue(o.FieldId, out var fieldId))
            {
                added |= Execute(main, transaction, "INSERT OR IGNORE INTO main.field_options (field_id, value, sort_order) VALUES ($f, $v, $s);",
                    ("$f", fieldId), ("$v", o.Value), ("$s", o.Sort)) > 0;
            }
        }
        return new DefinitionMaps(tags, fields);
    }

    private const string CopiedColumns = """
        id, title, memo, file_hash, original_file_name, file_size, page_count, rating, view_count, last_viewed_at,
        series_id, version_no, storage_status, archive_volume_id, created_at, updated_at,
        file_name, file_name_source_type, file_name_source_id, file_name_suffix, version_label
        """;

    private static void CopyDocuments(SqliteConnection main, SqliteTransaction transaction, string subId, DefinitionMaps maps, IReadOnlyList<string> ids)
    {
        Execute(main, transaction, "CREATE TEMP TABLE IF NOT EXISTS pull_ids (id TEXT PRIMARY KEY); DELETE FROM temp.pull_ids;");
        foreach (var id in ids)
        {
            Execute(main, transaction, "INSERT INTO temp.pull_ids (id) VALUES ($id);", ("$id", id));
        }

        // 版の番号が途中で重ならないよう、いったん外してから入れ直す
        Execute(main, transaction, """
            UPDATE main.documents SET series_id = NULL, version_no = NULL WHERE id IN (SELECT id FROM temp.pull_ids);
            INSERT INTO main.version_series (id, name) SELECT id, name FROM sub.version_series WHERE true
                ON CONFLICT(id) DO UPDATE SET name = excluded.name;
            """);
        // 開いた回数と最後に開いた日は、この PC の記録と大きい方・新しい方を残す
        var updates = string.Join(", ", CopiedColumns.Split(',').Select(c => c.Trim())
            .Where(c => c is not ("id" or "view_count" or "last_viewed_at" or "storage_status" or "archive_volume_id"))
            .Select(c => $"{c} = excluded.{c}"));
        Execute(main, transaction, $"""
            INSERT INTO main.documents ({CopiedColumns})
                SELECT {CopiedColumns.Replace("storage_status", "'archived'").Replace("archive_volume_id", "$sub")}
                FROM sub.documents WHERE id IN (SELECT id FROM temp.pull_ids)
                ON CONFLICT(id) DO UPDATE SET {updates},
                    storage_status = 'archived', archive_volume_id = $sub,
                    view_count = MAX(view_count, excluded.view_count),
                    last_viewed_at = CASE WHEN excluded.last_viewed_at IS NULL THEN last_viewed_at
                                          WHEN last_viewed_at IS NULL OR excluded.last_viewed_at > last_viewed_at THEN excluded.last_viewed_at
                                          ELSE last_viewed_at END;
            DELETE FROM main.document_tags WHERE document_id IN (SELECT id FROM temp.pull_ids);
            DELETE FROM main.document_field_values WHERE document_id IN (SELECT id FROM temp.pull_ids);
            DELETE FROM main.document_links WHERE from_id IN (SELECT id FROM temp.pull_ids) AND to_id IN (SELECT id FROM temp.pull_ids);
            INSERT OR IGNORE INTO main.document_links (from_id, to_id, type) SELECT from_id, to_id, type FROM sub.document_links
                WHERE from_id IN (SELECT id FROM temp.pull_ids) AND to_id IN (SELECT id FROM temp.pull_ids);
            INSERT OR IGNORE INTO main.page_texts (document_id, page_no, text, source)
                SELECT p.document_id, p.page_no, p.text, p.source FROM sub.page_texts p
                WHERE p.document_id IN (SELECT id FROM temp.pull_ids)
                  AND NOT EXISTS (SELECT 1 FROM main.page_texts m WHERE m.document_id = p.document_id);
            """, ("$sub", subId));

        foreach (var (documentId, tagId) in Rows(main, transaction, "SELECT document_id, tag_id FROM sub.document_tags;", r => (r.GetString(0), r.GetInt64(1))))
        {
            if (maps.Tags.TryGetValue(tagId, out var mapped))
            {
                Execute(main, transaction, "INSERT OR IGNORE INTO main.document_tags (document_id, tag_id) VALUES ($d, $t);", ("$d", documentId), ("$t", mapped));
            }
        }
        foreach (var v in Rows(main, transaction, "SELECT document_id, field_id, value_text, value_number, value_date FROM sub.document_field_values;",
                     r => (Doc: r.GetString(0), Field: r.GetInt64(1), Text: r.GetValue(2), Number: r.GetValue(3), Date: r.GetValue(4))))
        {
            if (maps.Fields.TryGetValue(v.Field, out var mapped))
            {
                Execute(main, transaction, """
                    INSERT OR REPLACE INTO main.document_field_values (document_id, field_id, value_text, value_number, value_date)
                    VALUES ($d, $f, $t, $n, $dt);
                    """, ("$d", v.Doc), ("$f", mapped), ("$t", v.Text), ("$n", v.Number), ("$dt", v.Date));
            }
        }
        // ファイル名の元にしたタグ・補助項目の番号もメインの番号に直す
        foreach (var (documentId, type, sourceId) in Rows(main, transaction,
                     "SELECT id, file_name_source_type, file_name_source_id FROM sub.documents WHERE file_name_source_type IN ('tag', 'field') AND file_name_source_id IS NOT NULL;",
                     r => (r.GetString(0), r.GetString(1), r.GetInt64(2))))
        {
            var map = type == "tag" ? maps.Tags : maps.Fields;
            object mapped = map.TryGetValue(sourceId, out var m) ? m : DBNull.Value;
            Execute(main, transaction, "UPDATE main.documents SET file_name_source_id = $s WHERE id = $id;", ("$s", mapped), ("$id", documentId));
        }
    }

    private static T WithSub<T>(SqliteConnection main, LibraryInfo sub, Func<T> action)
    {
        Execute(main, null, "ATTACH DATABASE $path AS sub;", ("$path", sub.DataFolder.DatabasePath));
        try
        {
            return action();
        }
        finally
        {
            Execute(main, null, "DETACH DATABASE sub;");
        }
    }

    private static List<T> Rows<T>(SqliteConnection connection, SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> map)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }
        return rows;
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return (long)command.ExecuteScalar()!;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command.ExecuteNonQuery();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 残っても動作には影響しない
        }
    }
}
