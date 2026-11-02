using System.Globalization;
using PdfManager.Core.Database;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.Core.Import;

public enum SetAsideKind
{
    Hold,
    Delete,
}

// 保留・削除予定のフォルダにある PDF 1 件。OriginalPath は移す前の場所(記録がなければ null)
public sealed record SetAsideFile(SetAsideKind Kind, string Path, string? OriginalPath, DateTimeOffset? MovedAt, long Size)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

// 決定ログ DD-1〜DD-10: 取り込み画面で「保留へ」「削除予定へ」送った PDF を置くフォルダ。ライブラリのフォルダ(.pdflib がある所)の中に作る。
// 元のフォルダからは移す(切り取り)。フォルダの中の PDF が正で、元の場所はデータベースに記録する
public sealed class SetAsideStore(DataFolder dataFolder)
{
    public const string HoldFolderName = "保留";
    public const string DeleteFolderName = "削除予定";

    public static string FolderName(SetAsideKind kind) => kind == SetAsideKind.Hold ? HoldFolderName : DeleteFolderName;

    private static string KindText(SetAsideKind kind) => kind == SetAsideKind.Hold ? "hold" : "delete";

    // data フォルダの 1 つ上がライブラリのフォルダ
    public string FolderOf(SetAsideKind kind) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dataFolder.Root)!, FolderName(kind));

    public int Count(SetAsideKind kind) => EnumeratePdfs(kind).Count();

    public IReadOnlyList<SetAsideFile> List(SetAsideKind kind)
    {
        var files = EnumeratePdfs(kind).Order(NaturalOrder.Instance).ToList();
        if (files.Count == 0)
        {
            return [];
        }
        var records = ReadRecords(kind);
        return files.Select(path =>
        {
            records.TryGetValue(System.IO.Path.GetFileName(path), out var record);
            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                size = 0;
            }
            return new SetAsideFile(kind, path, record.OriginalPath, record.MovedAt, size);
        }).ToList();
    }

    // そのファイルが保留・削除予定のどちらかのフォルダにあれば、その種類
    public SetAsideKind? KindOf(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        foreach (var kind in Enum.GetValues<SetAsideKind>())
        {
            if (string.Equals(folder, System.IO.Path.GetFullPath(FolderOf(kind)), StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }
        return null;
    }

    // ファイルをフォルダへ移す。同じ名前があれば「名前 (2).pdf」にする(決定ログ DD-2)。
    // 保留から削除予定へ移すときなどは、最初の元の場所を引き継ぐ。移した先のパスを返す
    public string Move(string sourcePath, SetAsideKind kind, DateTimeOffset? now = null)
    {
        var source = System.IO.Path.GetFullPath(sourcePath);
        var fromKind = KindOf(source);
        if (fromKind == kind)
        {
            return source;
        }
        string? original = source;
        if (fromKind is { } from)
        {
            original = ReadRecords(from).TryGetValue(System.IO.Path.GetFileName(source), out var record) ? record.OriginalPath : null;
        }

        var folder = FolderOf(kind);
        Directory.CreateDirectory(folder);
        var target = UniquePath(folder, System.IO.Path.GetFileName(source));
        // ドライブが違うときは、コピーしてから元を消す(File.Move がまとめて行う)
        File.Move(source, target);

        using var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath);
        if (fromKind is { } previous)
        {
            DeleteRecord(connection, previous, System.IO.Path.GetFileName(source));
        }
        if (original is not null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO set_aside_files (kind, file_name, original_path, moved_at) VALUES ($kind, $name, $original, $at)
                ON CONFLICT(kind, file_name) DO UPDATE SET original_path = excluded.original_path, moved_at = excluded.moved_at;
                """;
            command.Parameters.AddWithValue("$kind", KindText(kind));
            command.Parameters.AddWithValue("$name", System.IO.Path.GetFileName(target));
            command.Parameters.AddWithValue("$original", original);
            command.Parameters.AddWithValue("$at", (now ?? DateTimeOffset.Now).ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
        return target;
    }

    // 登録した・削除したファイルの記録を消す(フォルダの外のファイルなら何もしない)
    public void Forget(string path)
    {
        if (KindOf(path) is not { } kind)
        {
            return;
        }
        using var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath);
        DeleteRecord(connection, kind, System.IO.Path.GetFileName(path));
    }

    // 決定ログ DD-8: 削除予定の PDF を消す(初めはごみ箱へ。ごみ箱が使えない場所なら完全に削除)。消せなかったものの理由を返す
    public IReadOnlyList<string> Delete(IEnumerable<string> paths, Action<string>? deleteFile = null)
    {
        deleteFile ??= DeleteToRecycleBin;
        var problems = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    deleteFile(path);
                }
                Forget(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{System.IO.Path.GetFileName(path)}:{ex.Message}");
            }
        }
        return problems;
    }

    public static void DeleteToRecycleBin(string path)
    {
        if (RecycleBin.IsAvailableFor(path))
        {
            RecycleBin.MoveFile(path);
        }
        else
        {
            File.Delete(path);
        }
    }

    // ごみ箱が使えない場所(USB メモリなど)にあるか。消す前の確認に使う
    public bool IsRecycleBinAvailable(SetAsideKind kind) => RecycleBin.IsAvailableFor(System.IO.Path.Combine(FolderOf(kind), "x.pdf"));

    private IEnumerable<string> EnumeratePdfs(SetAsideKind kind)
    {
        var folder = FolderOf(kind);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.pdf", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true })
            : [];
    }

    private Dictionary<string, (string? OriginalPath, DateTimeOffset? MovedAt)> ReadRecords(SetAsideKind kind)
    {
        var records = new Dictionary<string, (string?, DateTimeOffset?)>(StringComparer.OrdinalIgnoreCase);
        using var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_name, original_path, moved_at FROM set_aside_files WHERE kind = $kind;";
        command.Parameters.AddWithValue("$kind", KindText(kind));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records[reader.GetString(0)] = (reader.GetString(1),
                DateTimeOffset.TryParse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null);
        }
        return records;
    }

    private static void DeleteRecord(Microsoft.Data.Sqlite.SqliteConnection connection, SetAsideKind kind, string fileName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM set_aside_files WHERE kind = $kind AND file_name = $name;";
        command.Parameters.AddWithValue("$kind", KindText(kind));
        command.Parameters.AddWithValue("$name", fileName);
        command.ExecuteNonQuery();
    }

    // 決定ログ DD-2: 同じ名前があれば「名前 (2).pdf」
    private static string UniquePath(string folder, string fileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var extension = System.IO.Path.GetExtension(fileName);
        var path = System.IO.Path.Combine(folder, fileName);
        for (var number = 2; File.Exists(path); number++)
        {
            path = System.IO.Path.Combine(folder, $"{stem} ({number}){extension}");
        }
        return path;
    }
}
