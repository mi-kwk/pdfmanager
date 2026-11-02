using Microsoft.Data.Sqlite;
using PdfManager.Core.Text;

namespace PdfManager.Core.Documents;

public sealed record TitleChange(string DocumentId, string NewTitle);

// 決定ログ DI-4〜DI-7: 複数の PDF のタイトルをまとめて付け直す。保存するまでは下書きで、重なりが残っている間は保存しない
public static class TitleBatch
{
    // newTitles(ID → 新しいタイトル。変えないものは入れない)を当てはめたとき、タイトルかファイル名がほかの文書と重なる文書の ID。
    // 比べる相手は最新版だけ(旧版は前の版と同じ名前で続けるため)。全角・半角、大文字・小文字の違いは同じとみなす(決定ログ T-1)
    public static HashSet<string> FindConflicts(SqliteConnection connection, IReadOnlyDictionary<string, string> newTitles)
    {
        var rows = ReadLatest(connection)
            .Select(r => newTitles.TryGetValue(r.Id, out var title)
                ? r with { Title = NameNormalizer.Clean(title), FileName = r.TitleIsFileName ? FileNameFromTitle(title, r.OriginalFileName) : r.FileName }
                : r)
            .ToList();
        var conflicts = new HashSet<string>();
        foreach (var group in rows.GroupBy(r => NameNormalizer.Normalize(r.Title)).Where(g => g.Count() > 1)
                     .Concat(rows.GroupBy(r => UniqueNames.Key(r.FileName)).Where(g => g.Count() > 1)))
        {
            conflicts.UnionWith(group.Select(r => r.Id).Where(newTitles.ContainsKey));
        }
        return conflicts;
    }

    // ファイル名を「タイトルと同じ」にしている文書は、ファイル名も一緒に変える。まとめて 1 回で保存する(入れ替えも途中で止まらない)
    public static void Apply(SqliteConnection connection, IReadOnlyList<TitleChange> changes, DateTimeOffset now)
    {
        var rows = ReadLatest(connection).ToDictionary(r => r.Id);
        using var transaction = connection.BeginTransaction();
        foreach (var change in changes)
        {
            if (!rows.TryGetValue(change.DocumentId, out var row))
            {
                continue;
            }
            var title = NameNormalizer.Clean(change.NewTitle);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE documents SET title = $title, file_name = $fileName, updated_at = $now WHERE id = $id;";
            command.Parameters.AddWithValue("$id", change.DocumentId);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$fileName", row.TitleIsFileName ? FileNameFromTitle(title, row.OriginalFileName) : row.FileName);
            command.Parameters.AddWithValue("$now", now.UtcDateTime.ToString("O"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public static string FileNameFromTitle(string title, string originalFileName) =>
        FileNameBuilder.Build(NameNormalizer.Clean(title), useTitle: true, null, "", originalFileName).FileName;

    private sealed record Row(string Id, string Title, string FileName, bool TitleIsFileName, string OriginalFileName);

    private static List<Row> ReadLatest(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.title, d.file_name, d.file_name_source_type = 'title', d.original_file_name
            FROM documents d
            WHERE d.series_id IS NULL OR d.version_no = (SELECT MAX(version_no) FROM documents s WHERE s.series_id = d.series_id);
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
        {
            rows.Add(new Row(reader.GetString(0), reader.GetString(1), reader.GetString(2), !reader.IsDBNull(3) && reader.GetBoolean(3), reader.GetString(4)));
        }
        return rows;
    }
}
