using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Library;
using PdfManager.Core.Search;

namespace PdfManager.Core.Tests;

// 決定ログ S-1〜S-15(メインとサブライブラリ)
public sealed class SubLibraryTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}")).FullName;
    private readonly List<string> _driveRoots = [];
    private LibraryInfo _main;
    private LibraryStorage _storage;

    public SubLibraryTests()
    {
        _main = LibraryService.Create(Folder("D", "業務"), "PDF管理");
        _storage = new LibraryStorage(_main, () => _driveRoots);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private SqliteConnection OpenMain() => PdfManagerDatabase.Open(_main.DataFolder.DatabasePath);

    // メインに PDF 本体・サムネイルと一緒に文書を入れる
    private void AddDocument(string id, string title, long? tagId = null, (long FieldId, string Text)? field = null, DateTimeOffset? created = null)
    {
        var pdf = _main.DataFolder.GetPdfPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
        File.WriteAllText(pdf, $"%PDF-1.4 {id}");
        var thumbnail = _main.DataFolder.GetThumbnailPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
        File.WriteAllText(thumbnail, "jpeg");
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pdf)));
        using var connection = OpenMain();
        new DocumentRepository(connection).Insert(
            new NewDocument(id, title, hash, id + ".pdf", new FileInfo(pdf).Length, 1, id + ".pdf"),
            [new PageText(1, "本文 " + title, TextSource.Embedded)],
            created ?? DateTimeOffset.UtcNow,
            field is { } f ? new Dictionary<long, FieldValue> { [f.FieldId] = new(f.Text, null, null) } : null,
            tagId is { } t ? [t] : null);
    }

    private static T Scalar<T>(LibraryInfo library, string sql)
    {
        using var connection = PdfManagerDatabase.Open(library.DataFolder.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private MoveResult Move(IReadOnlyCollection<string> ids, string? target) => _storage.Move(_storage.PlanMove(ids, target));

    private LibraryInfo CreateSub(string name = "過去", params string[] parent)
    {
        var sub = _storage.CreateSub(Folder(parent.Length == 0 ? ["G", "業務"] : parent), name);
        return sub;
    }

    [Fact]
    public void CreateSub_IsSubKindAndCannotBeOpenedAsMain()
    {
        var sub = CreateSub();

        Assert.Equal(LibraryKind.Sub, LibraryService.ReadFile(sub.LibraryFilePath).Info.Kind);
        Assert.Equal(_main.Id, LibraryService.ReadFile(sub.LibraryFilePath).Info.MainId);
        Assert.Contains("サブライブラリ", Assert.Throws<LibraryException>(() => LibraryService.Open(sub.LibraryFilePath)).Message);
        Assert.Equal(SubLibraryStatus.Connected, _storage.Subs.Single().Status);
        Assert.Equal(LibraryKind.Main, LibraryService.ReadFile(_main.LibraryFilePath).Info.Kind);
    }

    [Fact]
    public void CreateSub_RejectsSameNameAndPlaceInsideMain()
    {
        CreateSub("過去");

        Assert.Throws<LibraryException>(() => _storage.CreateSub(Folder("F", "業務"), " 過去 "));
        Assert.Throws<LibraryException>(() => _storage.CreateSub(Path.Combine(_main.RootFolder, "data"), "中"));
        Assert.Single(_storage.Subs);
    }

    [Fact]
    public void MoveToSub_CopiesPdfThumbnailAndInfo_AndKeepsMainThumbnail()
    {
        long tagId;
        long fieldId;
        using (var connection = OpenMain())
        {
            var catalog = new CatalogRepository(connection);
            tagId = catalog.AddTag("要確認");
            fieldId = catalog.AddField("店舗", FieldType.Text);
        }
        AddDocument("a1", "上野店 売上報告", tagId, (fieldId, "上野店"));
        AddDocument("b2", "別の文書");
        var sub = CreateSub();
        var subId = sub.Id.ToString("D");

        var result = Move(["a1"], subId);

        Assert.Equal(1, result.Moved);
        Assert.Empty(result.Problems);
        Assert.False(File.Exists(_main.DataFolder.GetPdfPath("a1")));
        Assert.True(File.Exists(sub.DataFolder.GetPdfPath("a1")));
        Assert.True(File.Exists(sub.DataFolder.GetThumbnailPath("a1")));
        Assert.True(File.Exists(_main.DataFolder.GetThumbnailPath("a1")));
        Assert.Equal("上野店 売上報告", Scalar<string>(sub, "SELECT title FROM documents WHERE id = 'a1';"));
        Assert.Equal("要確認", Scalar<string>(sub, "SELECT t.name FROM document_tags d JOIN tags t ON t.id = d.tag_id WHERE d.document_id = 'a1';"));
        Assert.Equal("上野店", Scalar<string>(sub, "SELECT value_text FROM document_field_values WHERE document_id = 'a1';"));
        Assert.Equal(1L, Scalar<long>(sub, "SELECT COUNT(*) FROM page_texts WHERE document_id = 'a1';"));
        Assert.Equal(0L, Scalar<long>(sub, "SELECT COUNT(*) FROM documents WHERE id = 'b2';"));

        using var main = OpenMain();
        var index = DocumentSearchIndex.Load(main);
        Assert.Equal(subId, index.Search(new SearchQuery()).Single(e => e.Id == "a1").StorageId);
        Assert.Equal(["a1"], index.Search(new SearchQuery { FilterByStorage = true, StorageId = subId }).Select(e => e.Id));
        Assert.Equal(["b2"], index.Search(new SearchQuery { FilterByStorage = true, StorageId = null }).Select(e => e.Id));
        Assert.Equal(1, DocumentSearchIndex.CountStorages(index.Search(new SearchQuery()))[""]);
    }

    [Fact]
    public void MoveBackToMain_RemovesFromSub()
    {
        AddDocument("a1", "文書");
        var sub = CreateSub();
        Move(["a1"], sub.Id.ToString("D"));

        var result = Move(["a1"], null);

        Assert.Equal(1, result.Moved);
        Assert.True(File.Exists(_main.DataFolder.GetPdfPath("a1")));
        Assert.False(File.Exists(sub.DataFolder.GetPdfPath("a1")));
        Assert.False(File.Exists(sub.DataFolder.GetThumbnailPath("a1")));
        Assert.Equal(0L, Scalar<long>(sub, "SELECT COUNT(*) FROM documents;"));
        Assert.Equal(1L, Scalar<long>(_main, "SELECT COUNT(*) FROM documents WHERE archive_volume_id IS NULL;"));
    }

    [Fact]
    public void Move_TakesWholeVersionSeries()
    {
        AddDocument("v1", "説明書 第1版", created: DateTimeOffset.UtcNow.AddDays(-2));
        AddDocument("v2", "説明書 第2版", created: DateTimeOffset.UtcNow.AddDays(-1));
        using (var connection = OpenMain())
        {
            new VersionRepository(connection).AddNewestVersion("v1", "v2");
        }
        var sub = CreateSub();

        var plan = _storage.PlanMove(["v1"], sub.Id.ToString("D"));
        _storage.Move(plan);

        Assert.Equal(2, plan.Count);
        Assert.Equal(2L, Scalar<long>(sub, "SELECT COUNT(*) FROM documents WHERE series_id IS NOT NULL;"));
        Assert.Equal(1L, Scalar<long>(sub, "SELECT COUNT(*) FROM version_series;"));
    }

    [Fact]
    public void Edit_IsWrittenToSub()
    {
        AddDocument("a1", "古いタイトル");
        var sub = CreateSub();
        Move(["a1"], sub.Id.ToString("D"));

        var change = _storage.BeginChange(["a1"]);
        using (var connection = OpenMain())
        {
            var detail = new DocumentRepository(connection).GetDetails(["a1"]).Single();
            new DocumentRepository(connection).Update(new DocumentUpdate("a1", "新しいタイトル", "メモ", 4, detail.FileName, FileNameSource.None, null, "",
                new Dictionary<long, FieldValue>(), []), DateTimeOffset.UtcNow);
        }
        var problems = _storage.CompleteChange(change);

        Assert.Empty(problems);
        Assert.Equal("新しいタイトル", Scalar<string>(sub, "SELECT title FROM documents WHERE id = 'a1';"));
        Assert.Equal(4L, Scalar<long>(sub, "SELECT rating FROM documents WHERE id = 'a1';"));
        Assert.Equal(0L, Scalar<long>(_main, "SELECT needs_push FROM sub_libraries;"));
    }

    [Fact]
    public void Delete_RemovesFromSubAndMain()
    {
        AddDocument("a1", "削除する文書");
        var sub = CreateSub();
        var subId = sub.Id.ToString("D");
        Move(["a1"], subId);
        var disposed = new List<string>();

        var change = _storage.BeginChange(["a1"]);
        new DocumentRemover(_main.DataFolder, disposed.Add).Delete("a1", "削除する文書.pdf", _storage.GetDataFolder(subId));
        _storage.CompleteChange(change);

        Assert.Single(disposed);
        Assert.StartsWith(sub.DataFolder.Root, disposed[0]);
        Assert.False(File.Exists(sub.DataFolder.GetThumbnailPath("a1")));
        Assert.False(File.Exists(_main.DataFolder.GetThumbnailPath("a1")));
        Assert.Equal(0L, Scalar<long>(sub, "SELECT COUNT(*) FROM documents;"));
        Assert.Equal(0L, Scalar<long>(_main, "SELECT COUNT(*) FROM documents;"));
    }

    [Fact]
    public void Disconnected_ShowsNotFound_AndCatalogChangeIsWrittenWhenConnectedAgain()
    {
        long tagId;
        using (var connection = OpenMain())
        {
            tagId = new CatalogRepository(connection).AddTag("要確認");
        }
        AddDocument("a1", "文書", tagId);
        var sub = CreateSub();
        var subId = sub.Id.ToString("D");
        Move(["a1"], subId);

        // 外付け HDD を外したつもり
        var away = Path.Combine(_root, "away");
        Directory.Move(sub.RootFolder, away);
        _storage.Refresh();
        Assert.Equal(SubLibraryStatus.NotFound, _storage.FindSub(subId)!.Status);
        Assert.False(_storage.IsAvailable(subId));
        Assert.Null(_storage.GetDataFolder(subId));
        Assert.Contains("つながっていません", _storage.UnavailableMessage(subId));

        // つないでいない間にタグの名前を変える
        _storage.BeginCatalogEdit();
        using (var connection = OpenMain())
        {
            new CatalogRepository(connection).RenameTag(tagId, "確認待ち");
        }
        _storage.EndCatalogEdit();
        Assert.Equal(1L, Scalar<long>(_main, "SELECT needs_push FROM sub_libraries;"));

        Directory.Move(away, sub.RootFolder);
        _storage.Refresh();

        Assert.Equal(SubLibraryStatus.Connected, _storage.FindSub(subId)!.Status);
        Assert.Equal("確認待ち", Scalar<string>(sub, "SELECT t.name FROM document_tags d JOIN tags t ON t.id = d.tag_id;"));
        Assert.Equal(0L, Scalar<long>(_main, "SELECT needs_push FROM sub_libraries;"));
    }

    [Fact]
    public void MainAndSubMovedTogether_SubIsFoundByRelativePlace()
    {
        AddDocument("a1", "文書");
        var sub = CreateSub("過去", "D", "sub");
        Move(["a1"], sub.Id.ToString("D"));

        // メインとサブを、まとめて外付け HDD へ移したつもり(決定ログ P-3、S-13)
        var newDrive = Path.Combine(_root, "E");
        Directory.Move(Path.Combine(_root, "D"), newDrive);
        _main = LibraryService.Open(Path.Combine(newDrive, "業務", LibraryService.LibraryFolderName, Path.GetFileName(_main.LibraryFilePath)));
        _storage = new LibraryStorage(_main, () => _driveRoots);
        _storage.Refresh();

        var state = _storage.Subs.Single();
        Assert.Equal(SubLibraryStatus.Connected, state.Status);
        Assert.StartsWith(newDrive, state.Location!.LibraryFilePath);
        Assert.StartsWith(newDrive, _storage.Subs.Single().Record.LibraryFilePath);
        Assert.True(File.Exists(_storage.GetDataFolder(state.Id)!.GetPdfPath("a1")));
    }

    [Fact]
    public void DriveLetterChanged_SubIsFoundOnOtherDrive()
    {
        var sub = CreateSub("過去", "driveB", "業務");
        var subId = sub.Id.ToString("D");
        // 登録した場所は Q:(今はつながっていないドライブ)
        using (var connection = OpenMain())
        {
            new SubLibraryRepository(connection).UpdateLocation(subId, @"Q:\業務\PdfManager\" + Path.GetFileName(sub.LibraryFilePath), _main);
        }
        _driveRoots.Add(Path.Combine(_root, "driveB") + Path.DirectorySeparatorChar);

        _storage.Refresh();

        Assert.Equal(SubLibraryStatus.Connected, _storage.FindSub(subId)!.Status);
        Assert.Equal(Path.GetFullPath(sub.LibraryFilePath), _storage.FindSub(subId)!.Record.LibraryFilePath, ignoreCase: true);
    }

    [Fact]
    public void FoundInTwoPlaces_WarnsUntilOneIsChosen()
    {
        var sub = CreateSub("過去", "driveB", "業務");
        var subId = sub.Id.ToString("D");
        CopyFolder(Path.Combine(_root, "driveB"), Path.Combine(_root, "driveC"));
        using (var connection = OpenMain())
        {
            new SubLibraryRepository(connection).UpdateLocation(subId, @"Q:\業務\PdfManager\" + Path.GetFileName(sub.LibraryFilePath), _main);
        }
        _driveRoots.Add(Path.Combine(_root, "driveB") + Path.DirectorySeparatorChar);
        _driveRoots.Add(Path.Combine(_root, "driveC") + Path.DirectorySeparatorChar);

        _storage.Refresh();
        var duplicate = _storage.FindSub(subId)!;
        var chosen = duplicate.Candidates.Single(c => c.Contains("driveC"));
        _storage.Relocate(subId, chosen);
        _storage.Refresh();

        Assert.Equal(SubLibraryStatus.Duplicate, duplicate.Status);
        Assert.Equal(2, duplicate.Candidates.Count);
        Assert.Equal(SubLibraryStatus.Connected, _storage.FindSub(subId)!.Status);
        Assert.Equal(chosen, _storage.FindSub(subId)!.Location!.LibraryFilePath, ignoreCase: true);
    }

    [Fact]
    public void ChangedElsewhere_IsReadOnlyAndNotOverwritten()
    {
        AddDocument("a1", "文書");
        var sub = CreateSub();
        var subId = sub.Id.ToString("D");
        Move(["a1"], subId);
        // ほかの PC で書き込まれたつもり
        SubLibrarySync.WriteNewToken(sub);

        _storage.Refresh();
        var change = _storage.BeginChange(["a1"]);
        var problems = _storage.CompleteChange(change);

        Assert.Equal(SubLibraryStatus.ChangedElsewhere, _storage.FindSub(subId)!.Status);
        Assert.False(_storage.IsAvailable(subId));
        Assert.Empty(problems);
        Assert.Equal(1L, Scalar<long>(_main, "SELECT needs_push FROM sub_libraries;"));
    }

    [Fact]
    public void RemoveSub_OnlyWhenEmpty_AndRenameUpdatesLibraryFile()
    {
        AddDocument("a1", "文書");
        var sub = CreateSub();
        var subId = sub.Id.ToString("D");
        Move(["a1"], subId);

        Assert.Throws<LibraryException>(() => _storage.RemoveSub(subId));
        _storage.RenameSub(subId, "よく見ない");
        Assert.Equal("よく見ない", LibraryService.ReadFile(sub.LibraryFilePath).Info.Name);

        Move(["a1"], null);
        _storage.RemoveSub(subId);
        Assert.Empty(_storage.Subs);
    }

    [Fact]
    public void PlanMove_ReportsUnavailableStorage()
    {
        AddDocument("a1", "文書");
        var sub = CreateSub();
        var subId = sub.Id.ToString("D");
        Move(["a1"], subId);
        Directory.Move(sub.RootFolder, Path.Combine(_root, "away"));
        _storage.Refresh();

        var plan = _storage.PlanMove(["a1"], null);

        Assert.Equal(["過去"], plan.UnavailableStorages);
        Assert.Throws<LibraryException>(() => _storage.Move(_storage.PlanMove(["a1"], subId)));
    }

    private static void CopyFolder(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
