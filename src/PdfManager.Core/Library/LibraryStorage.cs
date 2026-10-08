using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PdfManager.Core.Database;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.Core.Library;

public enum SubLibraryStatus
{
    Connected,
    NotFound,
    // 決定ログ S-15: 同じ ID のサブが 2 か所以上で見つかった
    Duplicate,
    // 決定ログ S-12: ほかの PC などで変更されている。この PC では表示のみ
    ChangedElsewhere,
    // 見つかったが使えない(新しいバージョンのアプリで更新された など)
    Unavailable,
}

public sealed record SubLibraryState(SubLibraryRecord Record, SubLibraryStatus Status, LibraryInfo? Location, IReadOnlyList<string> Candidates, string? Problem)
{
    public string Id => Record.Id;

    public string Name => Record.Name;

    // 決定ログ S-4・S-12: 文書の編集・移動・削除・PDF を開くのは、つながっているときだけ
    public bool IsWritable => Status == SubLibraryStatus.Connected && Location is not null;

    public string StatusText => Status switch
    {
        SubLibraryStatus.Connected => "接続中",
        SubLibraryStatus.NotFound => "未接続",
        SubLibraryStatus.Duplicate => "2 か所で見つかりました",
        SubLibraryStatus.ChangedElsewhere => "ほかの PC で変更されています",
        _ => "使えません",
    };

    // 場所の表示用。見つかっていなければ最後に見つかった場所
    public string Folder => Path.GetDirectoryName(Location?.LibraryFilePath ?? Record.LibraryFilePath) ?? "";
}

// サブの変更の前に作り、変更のあとで CompleteChange に渡す
public sealed record SubChange(IReadOnlyList<string> DocumentIds, IReadOnlyDictionary<string, bool> PreviousNeedsPush);

// 「保存先を移す」の見積もり(決定ログ S-3、S-7)
public sealed record MovePlan(
    string? TargetId,
    int SelectedCount,
    IReadOnlyList<string> DocumentIds,
    long Bytes,
    IReadOnlyList<string> UnavailableStorages,
    long? FreeBytes)
{
    public int Count => DocumentIds.Count;

    // 空き容量には少し余裕を見る
    public bool HasEnoughSpace => FreeBytes is not { } free || free - Bytes >= 100L << 20;
}

public sealed record MoveProgress(int Done, int Total, string Title);

public sealed record MoveResult(int Moved, IReadOnlyList<string> Problems, bool Cancelled);

// メインライブラリと、登録したサブライブラリの場所・状態をまとめて扱う(決定ログ S-1〜S-15)。
// 保存先は null がメイン、文字列がサブの ID
public sealed class LibraryStorage(LibraryInfo main, Func<IEnumerable<string>>? driveRoots = null)
{
    private readonly Func<IEnumerable<string>> _driveRoots = driveRoots ?? DefaultDriveRoots;

    public LibraryInfo Main { get; } = main;

    public IReadOnlyList<SubLibraryState> Subs { get; private set; } = [];

    public SubLibraryState? FindSub(string? id) => id is null ? null : Subs.FirstOrDefault(s => s.Id == id);

    public string StorageName(string? id) => id is null ? "メイン" : FindSub(id)?.Name ?? "(登録のないサブ)";

    public bool IsAvailable(string? storageId) => storageId is null || FindSub(storageId) is { IsWritable: true };

    // PDF 本体などの置き場所。つながっていなければ null
    public DataFolder? GetDataFolder(string? storageId) =>
        storageId is null ? Main.DataFolder : FindSub(storageId) is { IsWritable: true, Location: { } location } ? location.DataFolder : null;

    // 決定ログ S-4: 使えないときに表示する理由
    public string UnavailableMessage(string? storageId) => FindSub(storageId) switch
    {
        null => "保存先のサブライブラリが登録されていません。",
        { Status: SubLibraryStatus.ChangedElsewhere } sub => sub.Problem ?? $"サブライブラリ「{sub.Name}」はほかの PC で変更されているため、この PC では表示のみです。",
        { Status: SubLibraryStatus.Duplicate } sub =>
            $"サブライブラリ「{sub.Name}」が 2 か所以上で見つかったため、使う方を決めるまで表示のみにしています。設定画面の「ライブラリ」で選んでください。",
        { Status: SubLibraryStatus.Unavailable } sub => sub.Problem ?? $"サブライブラリ「{sub.Name}」は使えません。",
        var sub => $"保存先「{sub.Name}」がつながっていません。\n{sub.Folder}\n\n外付け HDD などをつないでから、もう一度操作してください。",
    };

    // ==== 状態の確認(決定ログ S-5、S-13、S-15) ====

    // 起動時・ドライブをつないだとき・外したとき・設定画面で呼ぶ
    public void Refresh()
    {
        using var connection = OpenMain();
        var repository = new SubLibraryRepository(connection);
        Subs = repository.GetAll().Select(record => Connect(connection, repository, record)).ToList();
    }

    private SubLibraryState Connect(SqliteConnection connection, SubLibraryRepository repository, SubLibraryRecord record)
    {
        var found = Locate(record);
        if (found.Count == 0)
        {
            return new SubLibraryState(record, SubLibraryStatus.NotFound, null, [], null);
        }
        if (found.Count > 1)
        {
            return new SubLibraryState(record, SubLibraryStatus.Duplicate, null, found.Select(f => f.LibraryFilePath).ToList(), null);
        }

        var sub = found[0];
        if (!string.Equals(sub.LibraryFilePath, record.LibraryFilePath, StringComparison.OrdinalIgnoreCase)
            || SubLibraryRepository.RelativePathFrom(Main, sub.LibraryFilePath) != record.RelativePath)
        {
            repository.UpdateLocation(record.Id, sub.LibraryFilePath, Main);
            record = repository.Find(record.Id)!;
        }
        try
        {
            LibraryService.PrepareSubDatabase(sub);
            if (SubLibrarySync.ReadToken(sub) != record.SyncedToken)
            {
                return new SubLibraryState(record, SubLibraryStatus.ChangedElsewhere, sub, [], null);
            }
            if (sub.Name != record.Name)
            {
                LibraryService.RenameLibraryFile(sub, record.Name);
                sub = sub with { Name = record.Name };
            }
            var state = new SubLibraryState(record, SubLibraryStatus.Connected, sub, [], null);
            if (record.NeedsPush)
            {
                // つないでいない間に項目・タグの定義を変えたときなど(決定ログ S-12)
                SubLibrarySync.Push(connection, record, sub, null);
                repository.SetNeedsPush(record.Id, false);
                state = state with { Record = repository.Find(record.Id)! };
            }
            return state;
        }
        catch (SubLibraryChangedElsewhereException ex)
        {
            return new SubLibraryState(record, SubLibraryStatus.ChangedElsewhere, sub, [], ex.Message);
        }
        catch (Exception ex) when (ex is LibraryException or IOException or UnauthorizedAccessException or SqliteException)
        {
            return new SubLibraryState(record, SubLibraryStatus.Unavailable, sub, [], $"サブライブラリ「{record.Name}」を使えません。{ex.Message}");
        }
    }

    // 登録した場所、メインからの相対の場所、ほかのドライブの同じ場所を探す
    private List<LibraryInfo> Locate(SubLibraryRecord record)
    {
        var candidates = new List<string> { record.LibraryFilePath };
        if (record.RelativePath is { } relative)
        {
            candidates.Add(Path.GetFullPath(Path.Combine(Main.RootFolder, relative)));
        }
        var withoutRoot = record.LibraryFilePath[(Path.GetPathRoot(record.LibraryFilePath)?.Length ?? 0)..];
        foreach (var root in _driveRoots())
        {
            candidates.Add(Path.Combine(root, withoutRoot));
        }

        var found = new List<LibraryInfo>();
        foreach (var path in candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(p => !record.IgnoredPaths.Contains(p, StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                if (File.Exists(path) && LibraryService.ReadFile(path).Info is { Kind: LibraryKind.Sub } info && info.Id.ToString("D") == record.Id)
                {
                    found.Add(info);
                }
            }
            catch (Exception ex) when (ex is LibraryException or IOException or UnauthorizedAccessException)
            {
                // 別のファイルや読めないファイルは候補にしない
            }
        }
        return found;
    }

    private static IEnumerable<string> DefaultDriveRoots() => DriveInfo.GetDrives()
        .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
        .Select(d => d.RootDirectory.FullName);

    // ==== サブの登録・名前・解除(決定ログ S-1、S-5、S-8、S-15) ====

    public LibraryInfo CreateSub(string parentFolder, string name)
    {
        using var connection = OpenMain();
        var repository = new SubLibraryRepository(connection);
        if (repository.GetAll().Any(r => NameNormalizer.Normalize(r.Name) == NameNormalizer.Normalize(name)))
        {
            throw new LibraryException("同じ名前のサブライブラリがすでにあります。別の名前にしてください。");
        }
        var sub = LibraryService.CreateSub(parentFolder, name, Main);
        var token = SubLibrarySync.WriteNewToken(sub);
        repository.Add(sub, Main, token, DateTimeOffset.UtcNow);
        // 項目・タグの定義を写しておく(文書はまだない)
        var record = repository.Find(sub.Id.ToString("D"))!;
        SubLibrarySync.Push(connection, record, sub, []);
        Refresh();
        return sub;
    }

    public void RenameSub(string id, string name)
    {
        using (var connection = OpenMain())
        {
            new SubLibraryRepository(connection).Rename(id, name);
        }
        Refresh();
    }

    public void RemoveSub(string id)
    {
        using (var connection = OpenMain())
        {
            new SubLibraryRepository(connection).Remove(id);
        }
        Refresh();
    }

    // 決定ログ S-5(見つからないときに場所を指定し直す)・S-15(2 か所で見つかったときに選ぶ)
    public void Relocate(string id, string libraryFilePath)
    {
        var (info, _) = LibraryService.ReadFile(libraryFilePath);
        if (info.Kind != LibraryKind.Sub || info.Id.ToString("D") != id)
        {
            throw new LibraryException("選んだファイルは、このサブライブラリではありません。サブライブラリの .pdflib ファイルを選んでください。");
        }
        using (var connection = OpenMain())
        {
            var repository = new SubLibraryRepository(connection);
            repository.UpdateLocation(id, info.LibraryFilePath, Main);
            // 2 か所で見つかっていたときは、選ばなかった方を次から探さない。選んだ方は(以前外していても)探す
            var ignored = (repository.Find(id)?.IgnoredPaths ?? [])
                .Concat(FindSub(id)?.Candidates ?? [])
                .Where(p => !string.Equals(p, info.LibraryFilePath, StringComparison.OrdinalIgnoreCase));
            repository.SetIgnoredPaths(id, ignored);
        }
        Refresh();
    }

    // ==== サブにある文書の変更(決定ログ S-12) ====

    // 変更の前に呼ぶ。対象の文書があるサブに「書き直しが必要」の印を付けておく(途中で止まっても次につないだときに直る)
    public SubChange BeginChange(IEnumerable<string> documentIds, IEnumerable<string?>? extraStorageIds = null)
    {
        using var connection = OpenMain();
        var repository = new SubLibraryRepository(connection);
        var ids = repository.ExpandToSeries(documentIds.ToList()).Union(documentIds).ToList();
        var subIds = repository.GetStorageIds(ids).Values.Concat(extraStorageIds ?? []).OfType<string>().Distinct();
        return new SubChange(ids, subIds.ToDictionary(id => id, repository.MarkNeedsPush));
    }

    // 変更のあとに呼ぶ。つながっているサブへ書き込む。書き込めなかったサブは印が残り、次につないだときに書き直す
    public IReadOnlyList<string> CompleteChange(SubChange change)
    {
        if (change.PreviousNeedsPush.Count == 0)
        {
            return [];
        }
        var problems = new List<string>();
        using var connection = OpenMain();
        var repository = new SubLibraryRepository(connection);
        var ids = repository.ExpandToSeries(change.DocumentIds).Union(change.DocumentIds).ToList();
        foreach (var (subId, previous) in change.PreviousNeedsPush)
        {
            if (FindSub(subId) is not { IsWritable: true, Location: { } location } || repository.Find(subId) is not { } record)
            {
                continue;
            }
            try
            {
                // 前から書き直しが必要だったサブは、ついでに全体を書き直す
                SubLibrarySync.Push(connection, record, location, previous ? null : ids);
                repository.SetNeedsPush(subId, false);
                Subs = Subs.Select(s => s.Id == subId ? s with { Record = repository.Find(subId)! } : s).ToList();
            }
            catch (SubLibraryChangedElsewhereException ex)
            {
                Subs = Subs.Select(s => s.Id == subId ? s with { Status = SubLibraryStatus.ChangedElsewhere, Problem = ex.Message } : s).ToList();
                problems.Add(ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                problems.Add($"サブライブラリ「{record.Name}」に書き込めませんでした。次につないだときに書き直します。({ex.Message})");
            }
        }
        return problems;
    }

    // 項目・タグの管理画面を開く前(定義の変更はサブの文書の値にも関わるため、すべてのサブに印を付ける)
    public void BeginCatalogEdit()
    {
        using var connection = OpenMain();
        new SubLibraryRepository(connection).MarkAllNeedPush();
    }

    // 項目・タグの管理画面を閉じたあと
    public IReadOnlyList<string> EndCatalogEdit()
    {
        var problems = new List<string>();
        using var connection = OpenMain();
        var repository = new SubLibraryRepository(connection);
        foreach (var sub in Subs.Where(s => s.IsWritable))
        {
            try
            {
                SubLibrarySync.Push(connection, repository.Find(sub.Id)!, sub.Location!, null);
                repository.SetNeedsPush(sub.Id, false);
            }
            catch (Exception ex) when (ex is SubLibraryChangedElsewhereException or IOException or UnauthorizedAccessException or SqliteException)
            {
                problems.Add(ex is SubLibraryChangedElsewhereException ? ex.Message : $"サブライブラリ「{sub.Name}」に書き込めませんでした。次につないだときに書き直します。({ex.Message})");
            }
        }
        var records = repository.GetAll().ToDictionary(r => r.Id);
        Subs = Subs.Select(s => records.TryGetValue(s.Id, out var record) ? s with { Record = record } : s).ToList();
        return problems;
    }

    // ==== 保存先を移す(決定ログ S-3、S-7) ====

    public MovePlan PlanMove(IReadOnlyCollection<string> documentIds, string? targetId)
    {
        using var connection = OpenMain();
        var repository = new SubLibraryRepository(connection);
        var ids = repository.ExpandToSeries(documentIds);
        var storages = repository.GetStorageIds(ids);
        var moving = ids.Where(id => storages.TryGetValue(id, out var storage) && storage != targetId).ToList();
        var unavailable = moving.Select(id => storages[id]).Append(targetId).Distinct()
            .Where(s => !IsAvailable(s)).Select(StorageName).ToList();

        long bytes = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT file_size FROM documents WHERE id = $id;";
            var parameter = command.Parameters.Add("$id", SqliteType.Text);
            foreach (var id in moving)
            {
                parameter.Value = id;
                bytes += (long)command.ExecuteScalar()!;
            }
        }
        long? free = null;
        if (GetDataFolder(targetId) is { } target)
        {
            free = new DriveInfo(Path.GetPathRoot(target.Root)!).AvailableFreeSpace;
        }
        return new MovePlan(targetId, documentIds.Count, moving, bytes, unavailable, free);
    }

    // コピーして中身が同じか確かめてから、データベースの保存先を変え、元を消す。版の系列ごとに 1 つのまとまりで移す
    public MoveResult Move(MovePlan plan, IProgress<MoveProgress>? progress = null, CancellationToken cancellation = default)
    {
        var target = GetDataFolder(plan.TargetId) ?? throw new LibraryException(UnavailableMessage(plan.TargetId));
        var change = BeginChange(plan.DocumentIds, [plan.TargetId]);
        var problems = new List<string>();
        var moved = 0;
        var cancelled = false;
        try
        {
            using var connection = OpenMain();
            var repository = new SubLibraryRepository(connection);
            var rows = ReadMoveRows(connection, plan.DocumentIds);
            var done = 0;
            foreach (var group in rows.GroupBy(r => r.SeriesId ?? r.Id))
            {
                if (cancellation.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                var members = group.ToList();
                progress?.Report(new MoveProgress(done, plan.Count, members[0].Title));
                try
                {
                    MoveGroup(connection, repository, members, plan.TargetId, target);
                    moved += members.Count;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibraryException)
                {
                    problems.Add($"・{members[0].Title}:{ex.Message}");
                }
                done += members.Count;
            }
            progress?.Report(new MoveProgress(done, plan.Count, ""));
        }
        finally
        {
            problems.AddRange(CompleteChange(change));
        }
        return new MoveResult(moved, problems, cancelled);
    }

    private sealed record MoveRow(string Id, string Title, string FileHash, string? SeriesId, string? StorageId);

    private static List<MoveRow> ReadMoveRows(SqliteConnection connection, IEnumerable<string> ids)
    {
        var rows = new List<MoveRow>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, file_hash, series_id, archive_volume_id FROM documents WHERE id = $id;";
        var parameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids)
        {
            parameter.Value = id;
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                rows.Add(new MoveRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }
        return rows;
    }

    private void MoveGroup(SqliteConnection connection, SubLibraryRepository repository, List<MoveRow> members, string? targetId, DataFolder target)
    {
        var sources = members.ToDictionary(m => m.Id, m => GetDataFolder(m.StorageId) ?? throw new LibraryException(UnavailableMessage(m.StorageId)));
        var copied = new List<string>();
        try
        {
            foreach (var member in members)
            {
                CopyVerified(sources[member.Id].GetPdfPath(member.Id), target.GetPdfPath(member.Id), member.FileHash);
                copied.Add(target.GetPdfPath(member.Id));
                // サブにもサムネイルを置く(決定ログ S-11)。メインのサムネイルはいつも残す
                var thumbnail = Main.DataFolder.GetThumbnailPath(member.Id);
                if (targetId is not null && File.Exists(thumbnail))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target.GetThumbnailPath(member.Id))!);
                    File.Copy(thumbnail, target.GetThumbnailPath(member.Id), overwrite: true);
                }
            }
        }
        catch
        {
            foreach (var path in copied)
            {
                TryDelete(path);
            }
            throw;
        }

        using (var transaction = connection.BeginTransaction())
        {
            repository.SetStorage(members.Select(m => m.Id).ToList(), targetId, transaction);
            transaction.Commit();
        }

        foreach (var member in members)
        {
            TryDelete(sources[member.Id].GetPdfPath(member.Id));
            if (member.StorageId is not null)
            {
                TryDelete(sources[member.Id].GetThumbnailPath(member.Id));
            }
        }
    }

    private static void CopyVerified(string source, string destination, string expectedHash)
    {
        if (!File.Exists(source))
        {
            throw new IOException("PDF 本体が見つかりません。");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".copying";
        File.Copy(source, temporary, overwrite: true);
        string hash;
        using (var stream = File.OpenRead(temporary))
        {
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        if (hash != expectedHash)
        {
            TryDelete(temporary);
            throw new IOException("コピーした PDF の中身が元と一致しませんでした。移していません。");
        }
        File.Move(temporary, destination, overwrite: true);
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
            // 残っても動作には影響しない(使われないファイルが残るだけ)
        }
    }

    private SqliteConnection OpenMain() => PdfManagerDatabase.Open(Main.DataFolder.DatabasePath);
}
