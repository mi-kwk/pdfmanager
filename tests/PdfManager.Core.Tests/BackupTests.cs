using System.Security.Cryptography;
using PdfManager.Core.Backup;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Library;

namespace PdfManager.Core.Tests;

// 決定ログ BB-1〜BB-8(バックアップと、バックアップから戻す)
public sealed class BackupTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}")).FullName;
    private readonly List<string> _driveRoots = [];
    private readonly LibraryInfo _main;
    private readonly LibraryStorage _storage;
    private readonly string _backupRoot;
    private DateTimeOffset _clock = new(2026, 10, 19, 9, 0, 0, TimeSpan.FromHours(9));

    public BackupTests()
    {
        _main = LibraryService.Create(Folder("D", "業務"), "PDF管理");
        _storage = new LibraryStorage(_main, () => _driveRoots);
        _backupRoot = Folder("H", "バックアップ");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private void AddDocument(string id)
    {
        var pdf = _main.DataFolder.GetPdfPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
        File.WriteAllText(pdf, $"%PDF-1.4 {id}");
        var thumbnail = _main.DataFolder.GetThumbnailPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
        File.WriteAllText(thumbnail, "jpeg " + id);
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pdf)));
        using var connection = PdfManagerDatabase.Open(_main.DataFolder.DatabasePath);
        new DocumentRepository(connection).Insert(
            new NewDocument(id, "文書 " + id, hash, id + ".pdf", new FileInfo(pdf).Length, 1, id + ".pdf"),
            [new PageText(1, "本文 " + id, TextSource.Embedded)], DateTimeOffset.UtcNow, null, null);
    }

    private BackupResult Backup(int keepOld = BackupService.DefaultKeepOldGenerations, CancellationToken cancellation = default)
    {
        _clock = _clock.AddMinutes(1);
        return BackupService.Run(_storage, _backupRoot, keepOld, null, cancellation, _clock);
    }

    private string BackupFolder => BackupService.FolderFor(_backupRoot, _main);

    private bool BackedUp(string id) => File.Exists(new Storage.DataFolder(BackupFolder).GetPdfPath(id));

    // 試験ではごみ箱を使わず、その場で消す
    private void Delete(string id) => new DocumentRemover(_main.DataFolder, File.Delete).Delete(id, id + ".pdf");

    private LibraryInfo CreateSubWith(params string[] ids)
    {
        var sub = _storage.CreateSub(Folder("G", "業務"), "過去");
        _storage.Move(_storage.PlanMove(ids, sub.Id.ToString("D")));
        return sub;
    }

    [Fact]
    public void Run_CopiesEverythingOnceIncludingConnectedSub()
    {
        AddDocument("a1");
        AddDocument("b2");
        CreateSubWith("b2");

        var plan = BackupService.Plan(_storage, _backupRoot);
        var first = Backup();
        var second = Backup();

        Assert.Equal(2, plan.PdfCount);
        Assert.Empty(plan.UnavailableSubs);
        Assert.Equal((2, 0), (first.CopiedPdfs, first.MissingPdfs));
        Assert.True(BackedUp("a1") && BackedUp("b2"));
        // 2 回目は増えた分だけ(今回はなし)
        Assert.Equal(0, second.CopiedPdfs);
        Assert.Equal(2, BackupService.ReadInfo(BackupFolder)!.Generations.Count);
        Assert.Equal(_main.Id, BackupService.FindBackups(_backupRoot).Single().LibraryId);
        Assert.Equal(0, BackupService.Plan(_storage, _backupRoot).PdfCount);
    }

    [Fact]
    public void Run_KeepsLatestPlusOldGenerationsAndRemovesUnusedPdfsLater()
    {
        AddDocument("a1");
        AddDocument("b2");
        Backup(keepOld: 2);
        Delete("b2");
        Backup(keepOld: 2);
        Backup(keepOld: 2);
        // まだ b2 が載っている古い回が残っているので、b2 の PDF も残す
        Assert.True(BackedUp("b2"));
        Assert.Equal(3, BackupService.ReadInfo(BackupFolder)!.Generations.Count);

        var fourth = Backup(keepOld: 2);
        // b2 が載っていた最初の回が消えたので、b2 の PDF も消える。アプリの PDF には触らない
        Assert.Equal((1, 1), (fourth.RemovedGenerations, fourth.RemovedPdfs));
        Assert.False(BackedUp("b2"));
        Assert.True(BackedUp("a1"));
        Assert.True(File.Exists(_main.DataFolder.GetPdfPath("a1")));
        Assert.Equal(3, BackupService.ReadInfo(BackupFolder)!.Generations.Count);
    }

    [Fact]
    public void Run_KeepZero_KeepsOnlyLatestAndMirrorsDeletions()
    {
        AddDocument("a1");
        AddDocument("b2");
        Backup(keepOld: 0);
        Delete("b2");
        var second = Backup(keepOld: 0);

        Assert.Single(BackupService.ReadInfo(BackupFolder)!.Generations);
        Assert.Equal(1, second.RemovedPdfs);
        Assert.False(BackedUp("b2"));
        Assert.Single(Directory.GetFiles(Path.Combine(BackupFolder, "db")));
    }

    [Fact]
    public void Run_SkipsDisconnectedSubAndReportsIt()
    {
        AddDocument("a1");
        AddDocument("b2");
        var sub = CreateSubWith("b2");
        Directory.Move(sub.RootFolder, sub.RootFolder + "-away");
        _storage.Refresh();

        var plan = BackupService.Plan(_storage, _backupRoot);
        var result = Backup();

        Assert.Equal(["過去"], plan.UnavailableSubs);
        Assert.Equal((1, 1), (result.CopiedPdfs, result.MissingPdfs));
        Assert.Equal(1, result.Generation!.MissingPdfCount);
    }

    [Fact]
    public void Run_Cancelled_KeepsPreviousGeneration()
    {
        AddDocument("a1");
        Backup();
        AddDocument("b2");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var result = Backup(cancellation: cancel.Token);

        Assert.True(result.Cancelled);
        Assert.Single(BackupService.ReadInfo(BackupFolder)!.Generations);
        Assert.Single(Directory.GetFiles(Path.Combine(BackupFolder, "db")));
    }

    [Fact]
    public void Restore_RebuildsMainAndSubWithoutTouchingOriginal()
    {
        AddDocument("a1");
        AddDocument("b2");
        CreateSubWith("b2");
        var generation = Backup().Generation!;
        AddDocument("c3");
        var backup = BackupService.ReadInfo(BackupFolder)!;

        var result = BackupService.Restore(backup, generation.Name, Folder("E", "戻した"));

        Assert.Equal((2, 0, 1), (result.RestoredPdfs, result.MissingPdfs, result.SubCount));
        var restored = LibraryService.Open(result.Library.LibraryFilePath);
        Assert.Equal(_main.Id, restored.Id);
        var storage = new LibraryStorage(restored, () => []);
        storage.Refresh();
        // サブは開いたときにメインの内容で書き直され、つながった状態になる
        var subState = Assert.Single(storage.Subs);
        Assert.Equal(SubLibraryStatus.Connected, subState.Status);
        Assert.True(File.Exists(subState.Location!.DataFolder.GetPdfPath("b2")));
        Assert.True(File.Exists(restored.DataFolder.GetPdfPath("a1")));
        Assert.False(File.Exists(restored.DataFolder.GetPdfPath("c3")));
        using (var connection = PdfManagerDatabase.Open(subState.Location.DataFolder.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT group_concat(id) FROM documents;";
            Assert.Equal("b2", command.ExecuteScalar());
        }
        // 元のライブラリはそのまま
        Assert.True(File.Exists(_main.DataFolder.GetPdfPath("c3")));
    }

    [Fact]
    public void Restore_RejectsFolderThatAlreadyHasLibrary()
    {
        AddDocument("a1");
        var generation = Backup().Generation!;
        var target = Folder("E", "戻した");
        Directory.CreateDirectory(Path.Combine(target, LibraryService.LibraryFolderName));

        Assert.Throws<LibraryException>(() => BackupService.Restore(BackupService.ReadInfo(BackupFolder)!, generation.Name, target));
    }
}
