using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PdfManager.Core.Database;
using PdfManager.Core.Library;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.Core.Backup;

// バックアップ 1 回分(データベースの写し)。Name は「20261019-153000」の形
public sealed record BackupGeneration(string Name, DateTimeOffset CreatedAt, int DocumentCount, int MissingPdfCount);

// バックアップ先にある 1 つのライブラリのバックアップ(backup.json)
public sealed record BackupInfo(string Folder, Guid LibraryId, string LibraryName, IReadOnlyList<BackupGeneration> Generations)
{
    public BackupGeneration? Latest => Generations.OrderByDescending(g => g.Name, StringComparer.Ordinal).FirstOrDefault();
}

// 始める前の見積もり(決定ログ BB-2)
public sealed record BackupPlan(string Folder, int PdfCount, long Bytes, long? FreeBytes, IReadOnlyList<string> UnavailableSubs)
{
    // 空き容量には少し余裕を見る(データベースの写しの分も含めて)
    public bool HasEnoughSpace => FreeBytes is not { } free || free - Bytes >= 200L << 20;
}

public sealed record BackupResult(BackupGeneration? Generation, int CopiedPdfs, int MissingPdfs, int RemovedGenerations, int RemovedPdfs, bool Cancelled);

public sealed record RestoreResult(LibraryInfo Library, int RestoredPdfs, int MissingPdfs, int SubCount);

// 決定ログ BB-1〜BB-8: ライブラリのバックアップと、バックアップから戻す処理。
// メインのデータベースには、サブにある文書の情報とサムネイルも入っているため、
// 「メインのデータベースの写し(回ごと)」と「すべての PDF・サムネイル」があれば、メインとサブを作り直せる
public static class BackupService
{
    public const string RootFolderName = "PdfManager-backup";
    public const int DefaultKeepOldGenerations = 5;
    public const int MaxKeepOldGenerations = 30;
    private const string InfoFileName = "backup.json";
    private const string Format = "PdfManager.Backup";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // <選んだフォルダ>\PdfManager-backup\<ライブラリ名>_<ID>
    public static string FolderFor(string rootFolder, LibraryInfo library) =>
        Path.Combine(Path.GetFullPath(rootFolder), RootFolderName, $"{FileNames.Sanitize(library.Name, "library")}_{library.Id:N}");

    // 選んだフォルダ(または PdfManager-backup フォルダ、バックアップのフォルダ)にあるバックアップ
    public static IReadOnlyList<BackupInfo> FindBackups(string folder)
    {
        var candidates = new List<string> { folder };
        foreach (var parent in new[] { folder, Path.Combine(folder, RootFolderName) })
        {
            if (Directory.Exists(parent))
            {
                candidates.AddRange(Directory.EnumerateDirectories(parent));
            }
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).Select(ReadInfo).OfType<BackupInfo>().ToList();
    }

    public static BackupInfo? ReadInfo(string backupFolder)
    {
        var path = Path.Combine(backupFolder, InfoFileName);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<InfoFile>(File.ReadAllText(path), JsonOptions) is { Format: Format } file
                && file.FormatVersion <= FormatVersion)
            {
                return new BackupInfo(Path.GetFullPath(backupFolder), file.LibraryId, file.LibraryName, file.Generations ?? []);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 読めないものはバックアップとして扱わない
        }
        return null;
    }

    // ==== バックアップ ====

    public static BackupPlan Plan(LibraryStorage storage, string rootFolder)
    {
        var folder = FolderFor(rootFolder, storage.Main);
        var pdfCount = 0;
        long bytes = new FileInfo(storage.Main.DataFolder.DatabasePath).Length;
        var unavailable = new HashSet<string>();
        using (var connection = PdfManagerDatabase.Open(storage.Main.DataFolder.DatabasePath))
        {
            foreach (var (id, size, storageId) in ReadDocuments(connection))
            {
                if (IsBackedUp(folder, id, size))
                {
                    continue;
                }
                if (storage.GetDataFolder(storageId) is null)
                {
                    unavailable.Add(storage.StorageName(storageId));
                    continue;
                }
                pdfCount++;
                bytes += size;
            }
        }
        return new BackupPlan(folder, pdfCount, bytes, FreeBytes(rootFolder), unavailable.Order().ToList());
    }

    // keepOldGenerations: 最新に加えて残す古い回の数(0 なら最新の 1 回分だけ。決定ログ BB-4)
    public static BackupResult Run(LibraryStorage storage, string rootFolder, int keepOldGenerations,
        IProgress<MoveProgress>? progress = null, CancellationToken cancellation = default, DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.Now;
        var folder = FolderFor(rootFolder, storage.Main);
        var data = new DataFolder(folder);
        data.EnsureCreated();
        var name = time.ToString("yyyyMMdd-HHmmss");
        var finalDb = DatabasePathOf(folder, name);
        var tempDb = finalDb + ".tmp";

        // 1. データベースの写し(使っている最中でも、壊れていない状態の写しを取る)
        File.Delete(tempDb);
        using (var source = PdfManagerDatabase.Open(storage.Main.DataFolder.DatabasePath))
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempDb, Pooling = false }.ToString()))
        {
            target.Open();
            source.BackupDatabase(target);
            PdfManagerDatabase.Execute(target, "PRAGMA journal_mode = DELETE;");
        }

        try
        {
            // 2. 写しに載っている文書の PDF とサムネイルのうち、まだないものをコピーする
            List<(string Id, long Size, string? StorageId)> documents;
            using (var snapshot = OpenReadOnly(tempDb))
            {
                documents = ReadDocuments(snapshot).ToList();
            }
            var copied = 0;
            var missing = 0;
            for (var i = 0; i < documents.Count; i++)
            {
                if (cancellation.IsCancellationRequested)
                {
                    File.Delete(tempDb);
                    return new BackupResult(null, copied, missing, 0, 0, Cancelled: true);
                }
                var (id, size, storageId) = documents[i];
                progress?.Report(new MoveProgress(i, documents.Count, ""));
                CopyIfMissing(storage.Main.DataFolder.GetThumbnailPath(id), data.GetThumbnailPath(id), null);
                if (IsBackedUp(folder, id, size))
                {
                    continue;
                }
                if (storage.GetDataFolder(storageId) is not { } source || !File.Exists(source.GetPdfPath(id)))
                {
                    missing++;
                    continue;
                }
                CopyIfMissing(source.GetPdfPath(id), data.GetPdfPath(id), size);
                copied++;
            }
            progress?.Report(new MoveProgress(documents.Count, documents.Count, ""));

            // 3. この回を確定する(最後まで書けてから名前を付けるので、途中で止まっても前の回は残る)
            File.Move(tempDb, finalDb, overwrite: true);
            var generation = new BackupGeneration(name, time, documents.Count, missing);
            var info = ReadInfo(folder);
            var generations = (info?.Generations ?? []).Where(g => g.Name != name).Append(generation).ToList();
            WriteInfo(folder, storage.Main, generations);

            // 4. 古い回と、どの回からも使われなくなった PDF を消す
            var (removedGenerations, removedPdfs) = Prune(folder, storage.Main, keepOldGenerations);
            return new BackupResult(generation, copied, missing, removedGenerations, removedPdfs, Cancelled: false);
        }
        catch
        {
            File.Delete(tempDb);
            throw;
        }
    }

    // 決定ログ BB-4: 最新に加えて keepOld 回分を残す。残しているどの回のデータベースにも載っていない PDF・サムネイルは消す
    public static (int Generations, int Pdfs) Prune(string folder, LibraryInfo library, int keepOld)
    {
        var info = ReadInfo(folder);
        if (info is null)
        {
            return (0, 0);
        }
        var ordered = info.Generations.OrderByDescending(g => g.Name, StringComparer.Ordinal).ToList();
        var keep = ordered.Take(1 + Math.Clamp(keepOld, 0, MaxKeepOldGenerations)).ToList();
        foreach (var old in ordered.Skip(keep.Count))
        {
            File.Delete(DatabasePathOf(folder, old.Name));
        }
        WriteInfo(folder, library, keep);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var generation in keep)
        {
            using var connection = OpenReadOnly(DatabasePathOf(folder, generation.Name));
            referenced.UnionWith(ReadDocuments(connection).Select(d => d.Id));
        }
        var removed = 0;
        foreach (var kind in new[] { "pdf", "thumbs" })
        {
            var root = Path.Combine(folder, kind);
            if (!Directory.Exists(root))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList())
            {
                if (!referenced.Contains(Path.GetFileNameWithoutExtension(file)))
                {
                    File.Delete(file);
                    removed += kind == "pdf" ? 1 : 0;
                }
            }
        }
        return (ordered.Count - keep.Count, removed);
    }

    // ==== 戻す(決定ログ BB-7) ====

    // destinationFolder の中に、メインを「PdfManager」、サブを「サブライブラリ\<名前>\PdfManager」として作る。今のライブラリには触らない
    public static RestoreResult Restore(BackupInfo backup, string generationName, string destinationFolder,
        IProgress<MoveProgress>? progress = null, CancellationToken cancellation = default)
    {
        var sourceDb = DatabasePathOf(backup.Folder, generationName);
        if (!File.Exists(sourceDb))
        {
            throw new LibraryException($"バックアップのデータベースが見つかりません。\n{sourceDb}");
        }
        var mainRoot = LibraryService.GetLibraryFolder(destinationFolder);
        var subsRoot = Path.Combine(Path.GetFullPath(destinationFolder), "サブライブラリ");
        if (Directory.Exists(mainRoot) || Directory.Exists(subsRoot))
        {
            throw new LibraryException($"戻す先のフォルダに、すでに「{LibraryService.LibraryFolderName}」か「サブライブラリ」フォルダがあります。空のフォルダを選んでください。\n{destinationFolder}");
        }

        var main = new LibraryInfo(Path.Combine(mainRoot, FileNames.Sanitize(backup.LibraryName, "library") + LibraryService.Extension), backup.LibraryId, backup.LibraryName);
        var created = new List<string> { mainRoot };
        try
        {
            main.DataFolder.EnsureCreated();
            File.Copy(sourceDb, main.DataFolder.DatabasePath);
            LibraryService.WriteLibraryFile(main, DateTimeOffset.UtcNow);
            using (var connection = PdfManagerDatabase.Open(main.DataFolder.DatabasePath))
            {
                PdfManagerDatabase.Initialize(connection);
            }

            // サブを作り直す。文書の情報は、開いたときにメインから書き込む(サブ全体の書き直しの印を付ける、決定ログ S-12)
            var subs = new Dictionary<string, LibraryInfo>();
            List<(string Id, long Size, string? StorageId)> documents;
            using (var connection = PdfManagerDatabase.Open(main.DataFolder.DatabasePath))
            {
                var repository = new SubLibraryRepository(connection);
                foreach (var record in repository.GetAll())
                {
                    var subRoot = LibraryService.GetLibraryFolder(Path.Combine(subsRoot, FileNames.Sanitize(record.Name, "sub")));
                    created.Add(subsRoot);
                    var sub = new LibraryInfo(Path.Combine(subRoot, FileNames.Sanitize(record.Name, "sub") + LibraryService.Extension),
                        Guid.Parse(record.Id), record.Name, LibraryKind.Sub, main.Id);
                    sub.DataFolder.EnsureCreated();
                    using (var subConnection = PdfManagerDatabase.Open(sub.DataFolder.DatabasePath))
                    {
                        PdfManagerDatabase.Initialize(subConnection, useWal: false);
                    }
                    LibraryService.WriteLibraryFile(sub, DateTimeOffset.UtcNow);
                    var token = SubLibrarySync.WriteNewToken(sub);
                    repository.UpdateLocation(record.Id, sub.LibraryFilePath, main);
                    repository.SetSynced(record.Id, token, needsPush: true);
                    repository.SetIgnoredPaths(record.Id, []);
                    subs[record.Id] = sub;
                }
                documents = ReadDocuments(connection).ToList();
            }

            var source = new DataFolder(backup.Folder);
            var restored = 0;
            var missing = 0;
            for (var i = 0; i < documents.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var (id, size, storageId) = documents[i];
                progress?.Report(new MoveProgress(i, documents.Count, ""));
                // サムネイルはメインにいつも置き、サブの文書はサブにも置く(「保存先を移す」と同じ)
                var inSub = storageId is not null && subs.ContainsKey(storageId);
                var target = inSub ? subs[storageId!].DataFolder : main.DataFolder;
                CopyIfMissing(source.GetThumbnailPath(id), main.DataFolder.GetThumbnailPath(id), null);
                if (inSub)
                {
                    CopyIfMissing(source.GetThumbnailPath(id), target.GetThumbnailPath(id), null);
                }
                if (File.Exists(source.GetPdfPath(id)))
                {
                    CopyIfMissing(source.GetPdfPath(id), target.GetPdfPath(id), size);
                    restored++;
                }
                else
                {
                    missing++;
                }
            }
            progress?.Report(new MoveProgress(documents.Count, documents.Count, ""));
            return new RestoreResult(main, restored, missing, subs.Count);
        }
        catch
        {
            // 途中で止めた・失敗したときは、作りかけのものを消す(元のライブラリとバックアップには触らない)
            foreach (var folder in created.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (IOException)
                {
                    // 消せなくても、元のライブラリとバックアップは無事
                }
            }
            throw;
        }
    }

    // ==== 共通 ====

    private static string DatabasePathOf(string folder, string generationName) => Path.Combine(folder, "db", generationName + ".db");

    private static bool IsBackedUp(string folder, string id, long size)
    {
        var file = new FileInfo(new DataFolder(folder).GetPdfPath(id));
        return file.Exists && file.Length == size;
    }

    // 書き終わってから名前を付ける(途中で止まっても、壊れたファイルが残らないように)
    private static void CopyIfMissing(string source, string target, long? size)
    {
        if (!File.Exists(source) || (File.Exists(target) && (size is null || new FileInfo(target).Length == size)))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, target, overwrite: true);
    }

    private static IEnumerable<(string Id, long Size, string? StorageId)> ReadDocuments(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, file_size, archive_volume_id FROM documents;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return (reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static long? FreeBytes(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteInfo(string folder, LibraryInfo library, IReadOnlyList<BackupGeneration> generations)
    {
        var file = new InfoFile(Format, FormatVersion, library.Id, library.Name,
            generations.OrderBy(g => g.Name, StringComparer.Ordinal).ToList());
        var path = Path.Combine(folder, InfoFileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(file, JsonOptions));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private sealed record InfoFile(string Format, int FormatVersion, Guid LibraryId, string LibraryName, IReadOnlyList<BackupGeneration>? Generations);
}
