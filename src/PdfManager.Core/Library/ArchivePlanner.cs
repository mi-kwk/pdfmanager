using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PdfManager.Core.Library;

// 退避の画面の 1 件。LastUsed は最後に開いた日(開いたことがなければ登録日)。RecentViews は呼び戻しの候補で、最近開いた回数
public sealed record ArchiveCandidate(string Id, string Title, string FileName, long Size, int Rating, DateTimeOffset CreatedAt,
    DateTimeOffset? LastViewedAt, string? StorageId, IReadOnlyList<long> TagIds, int RecentViews = 0)
{
    public DateTimeOffset LastUsed => LastViewedAt ?? CreatedAt;
}

// 決定ログ EA-1〜EA-8: 外付け HDD への退避。どれを移すかの候補を探す(移すのは「保存先を移す」と同じ処理)
public static class ArchivePlanner
{
    public const int DefaultMonths = 12;

    // 決定ログ EA-3: 評価がこれ以上のものは、初めはチェックを外す
    public const int KeepRating = 4;

    // 決定ログ EA-2: メインにある最新版のうち、最後に開いた日(なければ登録日)から months か月以上たったもの。古い順
    public static IReadOnlyList<ArchiveCandidate> Candidates(SqliteConnection connection, int months, DateTimeOffset now)
    {
        var border = now.AddMonths(-Math.Max(0, months)).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        return Read(connection, """
            SELECT d.id, d.title, d.file_name, d.file_size, d.rating, d.created_at, d.last_viewed_at, d.archive_volume_id, 0
            FROM documents d
            WHERE d.archive_volume_id IS NULL
              AND (d.series_id IS NULL OR d.version_no = (SELECT MAX(version_no) FROM documents s WHERE s.series_id = d.series_id))
              AND COALESCE(d.last_viewed_at, d.created_at) <= $border
            ORDER BY COALESCE(d.last_viewed_at, d.created_at), d.title;
            """, ("$border", border));
    }

    // 決定ログ EA-8: サブにあるのに、最近(since から)よく開いている最新版。開いた回数の多い順
    public static IReadOnlyList<ArchiveCandidate> FrequentInSubs(SqliteConnection connection, DateTimeOffset since, int limit = 50) => Read(connection, $"""
        SELECT d.id, d.title, d.file_name, d.file_size, d.rating, d.created_at, d.last_viewed_at, d.archive_volume_id, COUNT(v.id) AS n
        FROM documents d JOIN view_events v ON v.document_id = d.id
        WHERE d.archive_volume_id IS NOT NULL AND v.viewed_at >= $since
          AND (d.series_id IS NULL OR d.version_no = (SELECT MAX(version_no) FROM documents s WHERE s.series_id = d.series_id))
        GROUP BY d.id ORDER BY n DESC, d.title LIMIT {limit};
        """, ("$since", since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));

    // 決定ログ EA-6: フォルダのあるドライブの空き容量と全体の容量。わからなければ null
    public static (long Free, long Total)? DriveSpace(string folder)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
            return drive.IsReady ? (drive.AvailableFreeSpace, drive.TotalSize) : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // 決定ログ EA-6: 空きが全体の percent % を切っていれば true(percent が 0 なら知らせない)
    public static bool IsSpaceLow(string folder, int percent) =>
        percent > 0 && DriveSpace(folder) is var (free, total) && total > 0 && free * 100 < total * percent;

    private static List<ArchiveCandidate> Read(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var tags = new Dictionary<string, List<long>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT document_id, tag_id FROM document_tags;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                (tags.TryGetValue(id, out var list) ? list : tags[id] = []).Add(reader.GetInt64(1));
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
            using var reader = command.ExecuteReader();
            var rows = new List<ArchiveCandidate>();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                rows.Add(new ArchiveCandidate(id, reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                    reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                    reader.IsDBNull(7) ? null : reader.GetString(7), tags.GetValueOrDefault(id) ?? [], reader.GetInt32(8)));
            }
            return rows;
        }
    }
}
