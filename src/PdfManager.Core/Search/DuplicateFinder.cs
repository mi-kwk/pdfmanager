using Microsoft.Data.Sqlite;
using PdfManager.Core.Database;
using PdfManager.Core.Pdf;
using PdfManager.Core.Storage;

namespace PdfManager.Core.Search;

// 1 ページ目の見た目が似ている登録済みの文書
public sealed record SimilarDocument(string DocumentId, double Similarity);

// 1 ページ目の見た目が似ている文書の組(決定ログ DH-6)。Similarity は組の中で一番似ている 2 件の値
public sealed record DuplicateGroup(IReadOnlyList<string> DocumentIds, double Similarity);

// 決定ログ DH-1〜DH-7: 1 ページ目の見た目の指紋で、重複している(かもしれない)PDF を探す
public static class DuplicateFinder
{
    public static double Threshold(bool loose) => loose ? PageFingerprint.LooseThreshold : PageFingerprint.StrictThreshold;

    // 登録したときの指紋(サムネイルから作る)。作れなくても登録には影響しない(あとで EnsureFingerprintsAsync が作る)
    public static async Task SaveFromThumbnailAsync(DataFolder dataFolder, string documentId)
    {
        var thumbnail = dataFolder.GetThumbnailPath(documentId);
        if (!File.Exists(thumbnail))
        {
            return;
        }
        var data = await PageFingerprint.FromImageFileAsync(thumbnail);
        using var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath);
        Save(connection, documentId, data);
    }

    // 決定ログ DH-1: 指紋のない登録済みの文書(この機能より前に登録したもの)の指紋を、サムネイルからまとめて作る。作った件数を返す
    public static async Task<int> EnsureFingerprintsAsync(DataFolder main, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellation = default)
    {
        List<string> missing;
        using (var connection = PdfManagerDatabase.Open(main.DatabasePath))
        {
            missing = Strings(connection, "SELECT id FROM documents WHERE id NOT IN (SELECT document_id FROM page_fingerprints) ORDER BY id;");
        }
        var made = 0;
        for (var i = 0; i < missing.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report((i, missing.Count));
            var thumbnail = main.GetThumbnailPath(missing[i]);
            if (!File.Exists(thumbnail))
            {
                continue;
            }
            byte[] data;
            try
            {
                data = await PageFingerprint.FromImageFileAsync(thumbnail);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 壊れたサムネイルは飛ばす(その文書は比べない)
                continue;
            }
            using var connection = PdfManagerDatabase.Open(main.DatabasePath);
            Save(connection, missing[i], data);
            made++;
        }
        progress?.Report((missing.Count, missing.Count));
        return made;
    }

    // ids を渡すとその文書だけ。指紋のない文書は入らない
    public static Dictionary<string, byte[]> Load(SqliteConnection connection, IReadOnlyCollection<string>? ids = null)
    {
        var wanted = ids?.ToHashSet();
        var result = new Dictionary<string, byte[]>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_id, data FROM page_fingerprints;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (wanted is null || wanted.Contains(id))
            {
                result[id] = PageFingerprint.Decompress((byte[])reader[1]);
            }
        }
        return result;
    }

    // 決定ログ DH-1: 登録していない PDF(取り込み中・削除予定)と、登録済みの文書を比べる。似ている順
    public static IReadOnlyList<SimilarDocument> FindSimilar(byte[] fingerprint, IReadOnlyDictionary<string, byte[]> candidates, double threshold) => candidates
        .Select(c => new SimilarDocument(c.Key, PageFingerprint.Similarity(fingerprint, c.Value, threshold)))
        .Where(s => s.Similarity >= threshold)
        .OrderByDescending(s => s.Similarity)
        .ToList();

    // 決定ログ DH-6・DH-3・DH-7: 登録済みの文書どうしで似ているものを組にする。同じ版のまとまりどうしと、「重複ではない」とした組は除く
    public static IReadOnlyList<DuplicateGroup> FindGroups(SqliteConnection connection, IReadOnlyCollection<string> documentIds, double threshold,
        CancellationToken cancellation = default)
    {
        var fingerprints = Load(connection, documentIds).ToList();
        var series = SeriesOf(connection);
        var notDuplicate = NotDuplicatePairs(connection);
        var parent = fingerprints.ToDictionary(f => f.Key, f => f.Key);
        var best = new Dictionary<string, double>();

        string Find(string id)
        {
            while (parent[id] != id)
            {
                parent[id] = parent[parent[id]];
                id = parent[id];
            }
            return id;
        }

        // すべての 2 件を比べるので、件数が多いと時間がかかる。CPU のコアに分けて比べ、似ている組だけを集める
        var similarPairs = new System.Collections.Concurrent.ConcurrentBag<(string A, string B, double Similarity)>();
        Parallel.For(0, fingerprints.Count, new ParallelOptions { CancellationToken = cancellation }, i =>
        {
            for (var j = i + 1; j < fingerprints.Count; j++)
            {
                var (a, b) = (fingerprints[i].Key, fingerprints[j].Key);
                if ((series.TryGetValue(a, out var sa) && series.TryGetValue(b, out var sb) && sa == sb) || notDuplicate.Contains(PairKey(a, b)))
                {
                    continue;
                }
                var similarity = PageFingerprint.Similarity(fingerprints[i].Value, fingerprints[j].Value, threshold);
                if (similarity >= threshold)
                {
                    similarPairs.Add((a, b, similarity));
                }
            }
        });
        foreach (var (a, b, similarity) in similarPairs)
        {
            var (ra, rb) = (Find(a), Find(b));
            var top = Math.Max(similarity, Math.Max(best.GetValueOrDefault(ra), best.GetValueOrDefault(rb)));
            if (ra != rb)
            {
                parent[rb] = ra;
            }
            best[ra] = top;
        }
        return fingerprints
            .GroupBy(f => Find(f.Key))
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(g.Select(f => f.Key).ToList(), best.GetValueOrDefault(g.Key)))
            .OrderByDescending(g => g.Similarity)
            .ToList();
    }

    // 決定ログ DH-7: 組の中のすべての 2 件を「重複ではない」として覚える
    public static void MarkNotDuplicate(SqliteConnection connection, IReadOnlyList<string> documentIds)
    {
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < documentIds.Count; i++)
        {
            for (var j = i + 1; j < documentIds.Count; j++)
            {
                var (first, second) = PairKey(documentIds[i], documentIds[j]);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT OR IGNORE INTO not_duplicate_pairs (first_id, second_id) VALUES ($first, $second);";
                command.Parameters.AddWithValue("$first", first);
                command.Parameters.AddWithValue("$second", second);
                command.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    private static (string, string) PairKey(string a, string b) => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

    private static HashSet<(string, string)> NotDuplicatePairs(SqliteConnection connection)
    {
        var pairs = new HashSet<(string, string)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT first_id, second_id FROM not_duplicate_pairs;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            pairs.Add((reader.GetString(0), reader.GetString(1)));
        }
        return pairs;
    }

    private static Dictionary<string, string> SeriesOf(SqliteConnection connection)
    {
        var series = new Dictionary<string, string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, series_id FROM documents WHERE series_id IS NOT NULL;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            series[reader.GetString(0)] = reader.GetString(1);
        }
        return series;
    }

    private static void Save(SqliteConnection connection, string documentId, byte[] data)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO page_fingerprints (document_id, data) VALUES ($id, $data)
            ON CONFLICT(document_id) DO UPDATE SET data = excluded.data;
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$data", PageFingerprint.Compress(data));
        command.ExecuteNonQuery();
    }

    private static List<string> Strings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }
}
