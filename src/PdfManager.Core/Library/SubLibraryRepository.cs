using System.Globalization;
using Microsoft.Data.Sqlite;
using PdfManager.Core.Catalog;
using PdfManager.Core.Text;

namespace PdfManager.Core.Library;

// メインに登録したサブライブラリ(決定ログ S-1、S-11、S-13)
public sealed record SubLibraryRecord(
    string Id,
    string Name,
    string LibraryFilePath,
    string? RelativePath,
    string? SyncedToken,
    bool NeedsPush,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> IgnoredPaths);

// 保存先ごとの件数と合計サイズ
public sealed record StorageUsage(long Count, long Bytes);

// メインのデータベースにある、サブライブラリの登録と文書の置き場所
public sealed class SubLibraryRepository(SqliteConnection connection)
{
    public IReadOnlyList<SubLibraryRecord> GetAll()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, library_file_path, relative_path, synced_token, needs_push, created_at, ignored_paths
            FROM sub_libraries ORDER BY created_at, id;
            """;
        using var reader = command.ExecuteReader();
        var records = new List<SubLibraryRecord>();
        while (reader.Read())
        {
            records.Add(new SubLibraryRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetBoolean(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                reader.GetString(7).Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        }
        return records;
    }

    // 決定ログ S-15: 2 か所で見つかったときに選ばなかった方は、次から探さない
    public void SetIgnoredPaths(string id, IEnumerable<string> paths) =>
        Execute("UPDATE sub_libraries SET ignored_paths = $paths WHERE id = $id;",
            ("$paths", string.Join('\n', paths.Distinct(StringComparer.OrdinalIgnoreCase))), ("$id", id));

    public SubLibraryRecord? Find(string id) => GetAll().FirstOrDefault(r => r.Id == id);

    public void Add(LibraryInfo sub, LibraryInfo main, string syncedToken, DateTimeOffset now)
    {
        EnsureUniqueName(sub.Name, exceptId: null);
        Execute("""
            INSERT INTO sub_libraries (id, name, normalized_name, library_file_path, relative_path, synced_token, created_at)
            VALUES ($id, $name, $normalized, $path, $relative, $token, $now);
            """,
            ("$id", sub.Id.ToString("D")), ("$name", sub.Name), ("$normalized", NameNormalizer.Normalize(sub.Name)),
            ("$path", sub.LibraryFilePath), ("$relative", (object?)RelativePathFrom(main, sub.LibraryFilePath) ?? DBNull.Value),
            ("$token", syncedToken), ("$now", now.UtcDateTime.ToString("O")));
    }

    // 決定ログ S-11: サブの名前はメインの中で重ならない
    public void Rename(string id, string name)
    {
        var cleaned = NameNormalizer.Clean(name);
        if (cleaned.Length == 0)
        {
            throw new LibraryException("サブライブラリの名前を入力してください。");
        }
        if (cleaned.Length > CatalogRepository.MaxNameLength)
        {
            throw new LibraryException($"名前は {CatalogRepository.MaxNameLength} 文字以内で入力してください。");
        }
        EnsureUniqueName(cleaned, id);
        Execute("UPDATE sub_libraries SET name = $name, normalized_name = $normalized WHERE id = $id;",
            ("$name", cleaned), ("$normalized", NameNormalizer.Normalize(cleaned)), ("$id", id));
    }

    // 決定ログ S-8: 中の PDF が 0 件のときだけ
    public void Remove(string id)
    {
        if (CountDocuments(id) > 0)
        {
            throw new LibraryException("中に PDF があるため、登録を解除できません。先にほかの保存先へ移してください。");
        }
        Execute("DELETE FROM sub_libraries WHERE id = $id;", ("$id", id));
    }

    // 決定ログ S-5・S-13: 見つかった場所を覚える(メインからの相対の場所も一緒に)
    public void UpdateLocation(string id, string libraryFilePath, LibraryInfo main) =>
        Execute("UPDATE sub_libraries SET library_file_path = $path, relative_path = $relative WHERE id = $id;",
            ("$path", Path.GetFullPath(libraryFilePath)),
            ("$relative", (object?)RelativePathFrom(main, libraryFilePath) ?? DBNull.Value),
            ("$id", id));

    // 書き込みの途中で止まっても、次につないだときに全体を書き直せるよう、先に印を付ける
    public bool MarkNeedsPush(string id)
    {
        var previous = Find(id)?.NeedsPush ?? false;
        Execute("UPDATE sub_libraries SET needs_push = 1 WHERE id = $id;", ("$id", id));
        return previous;
    }

    public void MarkAllNeedPush() => Execute("UPDATE sub_libraries SET needs_push = 1;");

    public void SetSynced(string id, string token, bool needsPush) =>
        Execute("UPDATE sub_libraries SET synced_token = $token, needs_push = $needs WHERE id = $id;",
            ("$token", token), ("$needs", needsPush ? 1 : 0), ("$id", id));

    public void SetNeedsPush(string id, bool needsPush) =>
        Execute("UPDATE sub_libraries SET needs_push = $needs WHERE id = $id;", ("$needs", needsPush ? 1 : 0), ("$id", id));

    public long CountDocuments(string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM documents WHERE archive_volume_id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (long)command.ExecuteScalar()!;
    }

    // キーは保存先(null = メイン)
    public IReadOnlyDictionary<string, StorageUsage> GetUsage()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(archive_volume_id, ''), COUNT(*), COALESCE(SUM(file_size), 0) FROM documents GROUP BY archive_volume_id;";
        using var reader = command.ExecuteReader();
        var usage = new Dictionary<string, StorageUsage>();
        while (reader.Read())
        {
            usage[reader.GetString(0)] = new StorageUsage(reader.GetInt64(1), reader.GetInt64(2));
        }
        return usage;
    }

    // 文書ごとの今の保存先(null = メイン)
    public IReadOnlyDictionary<string, string?> GetStorageIds(IEnumerable<string> documentIds)
    {
        var result = new Dictionary<string, string?>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT archive_volume_id FROM documents WHERE id = $id;";
        var parameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in documentIds.Distinct())
        {
            parameter.Value = id;
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                result[id] = reader.IsDBNull(0) ? null : reader.GetString(0);
            }
        }
        return result;
    }

    // 決定ログ S-7: 版の系列の文書をすべて含める
    public IReadOnlyList<string> ExpandToSeries(IEnumerable<string> documentIds)
    {
        var result = new List<string>();
        var seen = new HashSet<string>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM documents WHERE id = $id AND series_id IS NULL
            UNION ALL
            SELECT d.id FROM documents d JOIN documents s ON s.series_id = d.series_id WHERE s.id = $id
            ORDER BY 1;
            """;
        var parameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in documentIds)
        {
            parameter.Value = id;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (seen.Add(reader.GetString(0)))
                {
                    result.Add(reader.GetString(0));
                }
            }
        }
        return result;
    }

    public void SetStorage(IReadOnlyCollection<string> documentIds, string? subId, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = subId is null
            ? "UPDATE documents SET storage_status = 'local', archive_volume_id = NULL WHERE id = $id;"
            : "UPDATE documents SET storage_status = 'archived', archive_volume_id = $sub WHERE id = $id;";
        if (subId is not null)
        {
            command.Parameters.AddWithValue("$sub", subId);
        }
        var parameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in documentIds)
        {
            parameter.Value = id;
            command.ExecuteNonQuery();
        }
    }

    // 決定ログ S-13: 同じドライブにあるときだけ、メインのフォルダからの相対の場所を持つ
    public static string? RelativePathFrom(LibraryInfo main, string libraryFilePath)
    {
        var full = Path.GetFullPath(libraryFilePath);
        return string.Equals(Path.GetPathRoot(full), Path.GetPathRoot(main.RootFolder), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(main.RootFolder, full)
            : null;
    }

    private void EnsureUniqueName(string name, string? exceptId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sub_libraries WHERE normalized_name = $normalized AND id <> $except;";
        command.Parameters.AddWithValue("$normalized", NameNormalizer.Normalize(name));
        command.Parameters.AddWithValue("$except", exceptId ?? "");
        if ((long)command.ExecuteScalar()! > 0)
        {
            throw new LibraryException("同じ名前のサブライブラリがすでにあります。別の名前にしてください。");
        }
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.ExecuteNonQuery();
    }
}
