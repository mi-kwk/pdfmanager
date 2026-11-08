using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Library;

namespace PdfManager.Core.Tests;

// 決定ログ EB-1〜EB-6(ほかの PC と同じ外付け HDD のサブを使う)
public sealed class SharedSubTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}")).FullName;
    private readonly LibraryInfo _main1;
    private readonly LibraryStorage _pc1;

    public SharedSubTests()
    {
        _main1 = LibraryService.Create(Folder("D", "業務"), "PDF管理");
        _pc1 = new LibraryStorage(_main1, () => [], "PC1");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private static SqliteConnection Open(LibraryInfo library) => PdfManagerDatabase.Open(library.DataFolder.DatabasePath);

    private static void AddDocument(LibraryInfo library, string id, string title, params long[] tagIds)
    {
        var pdf = library.DataFolder.GetPdfPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
        File.WriteAllText(pdf, $"%PDF-1.4 {id}");
        var thumbnail = library.DataFolder.GetThumbnailPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
        File.WriteAllText(thumbnail, "jpeg");
        using var connection = Open(library);
        new DocumentRepository(connection).Insert(
            new NewDocument(id, title, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pdf))), id + ".pdf", new FileInfo(pdf).Length, 1, id + ".pdf"),
            [new PageText(1, "本文 " + title, TextSource.Embedded)], DateTimeOffset.UtcNow, null, tagIds.Length == 0 ? null : tagIds);
    }

    private static void Edit(LibraryStorage storage, LibraryInfo library, string id, string title, params long[] tagIds)
    {
        var change = storage.BeginChange([id]);
        using (var connection = Open(library))
        {
            var detail = new DocumentRepository(connection).GetDetails([id]).Single();
            new DocumentRepository(connection).Update(new DocumentUpdate(id, title, "", 0, detail.FileName, FileNameSource.None, null, "",
                new Dictionary<long, FieldValue>(), tagIds), DateTimeOffset.UtcNow);
        }
        storage.CompleteChange(change);
    }

    private static long AddTag(LibraryInfo library, string name)
    {
        using var connection = Open(library);
        return new CatalogRepository(connection).AddTag(name);
    }

    private static T Scalar<T>(LibraryInfo library, string sql)
    {
        using var connection = Open(library);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static string TagsOf(LibraryInfo library, string id)
    {
        using var connection = Open(library);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT group_concat(t.name, ',') FROM (SELECT t.name FROM document_tags d JOIN tags t ON t.id = d.tag_id WHERE d.document_id = '{id}' ORDER BY t.name) t;";
        return command.ExecuteScalar() as string ?? "";
    }

    private static void Move(LibraryStorage storage, IReadOnlyCollection<string> ids, string? target) =>
        Assert.Empty(storage.Move(storage.PlanMove(ids, target)).Problems);

    // マスター(PC1 のメイン)を PC2 にコピーしたつもり(決定ログ P-3)
    private (LibraryInfo Main, LibraryStorage Storage) CopyToPc2()
    {
        CopyFolder(Path.Combine(_root, "D"), Path.Combine(_root, "E"));
        var main = LibraryService.Open(Path.Combine(_root, "E", "業務", LibraryService.LibraryFolderName, Path.GetFileName(_main1.LibraryFilePath)));
        var storage = new LibraryStorage(main, () => [], "PC2");
        storage.Refresh();
        return (main, storage);
    }

    [Fact]
    public void ChangesOnOtherPc_AreReadWhenConnected_AndTagsAreMatchedByName()
    {
        var common = AddTag(_main1, "共通");
        AddDocument(_main1, "a1", "1巻", common);
        AddDocument(_main1, "b2", "2巻");
        var sub = _pc1.CreateSub(Folder("G", "業務"), "外付けHDD");
        var subId = sub.Id.ToString("D");
        Move(_pc1, ["a1", "b2"], subId);
        var (main2, pc2) = CopyToPc2();

        // 両方の PC で別々にタグを作る(番号が重なる)
        var pc1Tag = AddTag(_main1, "PC1のタグ");
        var pc2Tag = AddTag(main2, "PC2のタグ");
        Assert.Equal(pc1Tag, pc2Tag);
        // PC2:1巻を直してタグを付ける、新しい 3巻をサブへ移す、2巻をメインへ戻す
        Edit(pc2, main2, "a1", "1巻(改)", common, pc2Tag);
        AddDocument(main2, "c3", "3巻");
        Move(pc2, ["c3"], subId);
        Move(pc2, ["b2"], null);

        _pc1.Refresh();

        var state = _pc1.FindSub(subId)!;
        Assert.Equal(SubLibraryStatus.Connected, state.Status);
        var report = Assert.Single(_pc1.TakePullReports());
        Assert.Equal((1, 1, 1), (report.Plan.Added, report.Plan.Updated, report.Plan.Removed));
        Assert.Equal(["2巻"], report.Plan.RemovedTitles);
        Assert.Equal("1巻(改)", Scalar<string>(_main1, "SELECT title FROM documents WHERE id = 'a1';"));
        Assert.Equal("PC2のタグ,共通", TagsOf(_main1, "a1"));
        Assert.Equal(subId, Scalar<string>(_main1, "SELECT archive_volume_id FROM documents WHERE id = 'c3';"));
        Assert.True(File.Exists(_main1.DataFolder.GetThumbnailPath("c3")));
        Assert.Equal(0L, Scalar<long>(_main1, "SELECT COUNT(*) FROM documents WHERE id = 'b2';"));
        // PC1 の書き戻しのあとも、サブのタグは名前のまま(PC1 の番号に直っている)
        Assert.Equal("PC2のタグ,共通", TagsOf(sub, "a1"));
        Assert.Equal(1L, Scalar<long>(_main1, "SELECT COUNT(*) FROM tags WHERE name = 'PC1のタグ';"));
        // 決定ログ EB-5: 使っている PC
        Assert.Equal(["PC2"], _pc1.OtherUsers(subId).Select(u => u.Pc));

        // PC2 につなぎ直しても、PC1 の書き戻しを読み込んで使える
        pc2.Refresh();
        Assert.Equal(SubLibraryStatus.Connected, pc2.FindSub(subId)!.Status);
        Assert.Equal("PC2のタグ,共通", TagsOf(main2, "a1"));
    }

    [Fact]
    public void LocalChangesWouldBeLost_WaitsForConfirmation_ThenSubWins()
    {
        AddDocument(_main1, "a1", "1巻");
        var sub = _pc1.CreateSub(Folder("G", "業務"), "外付けHDD");
        var subId = sub.Id.ToString("D");
        Move(_pc1, ["a1"], subId);
        var (main2, pc2) = CopyToPc2();
        Edit(pc2, main2, "a1", "PC2 で直した");

        // PC1 では HDD を外している間に直す(サブへ書き込めない)
        var away = Path.Combine(_root, "away");
        Directory.Move(sub.RootFolder, away);
        _pc1.Refresh();
        Thread.Sleep(20);
        using (var connection = Open(_main1))
        {
            new SubLibraryRepository(connection).MarkNeedsPush(subId);
            var detail = new DocumentRepository(connection).GetDetails(["a1"]).Single();
            new DocumentRepository(connection).Update(new DocumentUpdate("a1", "PC1 で直した", "", 0, detail.FileName, FileNameSource.None, null, "",
                new Dictionary<long, FieldValue>(), []), DateTimeOffset.UtcNow);
        }
        Directory.Move(away, sub.RootFolder);

        _pc1.Refresh();
        var waiting = _pc1.FindSub(subId)!;
        Assert.Equal(SubLibraryStatus.ChangedElsewhere, waiting.Status);
        Assert.Equal(["PC1 で直した"], waiting.PendingPull!.LostTitles);
        Assert.False(_pc1.IsAvailable(subId));

        _pc1.PullSub(subId);

        Assert.Equal(SubLibraryStatus.Connected, _pc1.FindSub(subId)!.Status);
        Assert.Equal("PC2 で直した", Scalar<string>(_main1, "SELECT title FROM documents WHERE id = 'a1';"));
        Assert.Equal(0L, Scalar<long>(_main1, "SELECT needs_push FROM sub_libraries;"));
    }

    [Fact]
    public void DocumentNotWrittenToSub_IsKeptWhenPdfIsThere()
    {
        AddDocument(_main1, "a1", "1巻");
        AddDocument(_main1, "d4", "4巻");
        var sub = _pc1.CreateSub(Folder("G", "業務"), "外付けHDD");
        var subId = sub.Id.ToString("D");
        Move(_pc1, ["a1", "d4"], subId);
        // 4巻の情報をサブに書き込めないまま、ほかの PC でサブが変わったつもり
        using (var connection = Open(sub))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM documents WHERE id = 'd4';";
            command.ExecuteNonQuery();
        }
        SubLibrarySync.WriteNewToken(sub);

        _pc1.Refresh();

        Assert.Equal(SubLibraryStatus.Connected, _pc1.FindSub(subId)!.Status);
        Assert.Equal(subId, Scalar<string>(_main1, "SELECT archive_volume_id FROM documents WHERE id = 'd4';"));
        Assert.Equal(1L, Scalar<long>(sub, "SELECT COUNT(*) FROM documents WHERE id = 'd4';"));
    }

    [Fact]
    public void RegisterExistingSub_OnlyFromSameMain_AndAlignsStorage()
    {
        AddDocument(_main1, "a1", "1巻");
        // サブを作る前にコピーした PC2
        var (main2, pc2) = CopyToPc2();
        var sub = _pc1.CreateSub(Folder("G", "業務"), "外付けHDD");
        var subId = sub.Id.ToString("D");
        Move(_pc1, ["a1"], subId);
        var other = LibraryService.Create(Folder("X"), "別のライブラリ");
        var otherSub = new LibraryStorage(other, () => [], "PC1").CreateSub(Folder("Y"), "別のサブ");

        Assert.Contains("別に作った", Assert.Throws<LibraryException>(() => pc2.RegisterExistingSub(otherSub.LibraryFilePath)).Message);
        pc2.RegisterExistingSub(sub.LibraryFilePath);

        Assert.Equal(SubLibraryStatus.Connected, pc2.FindSub(subId)!.Status);
        Assert.Equal(subId, Scalar<string>(main2, "SELECT archive_volume_id FROM documents WHERE id = 'a1';"));
        Assert.False(File.Exists(main2.DataFolder.GetPdfPath("a1")));
        Assert.Equal(1, Assert.Single(pc2.TakePullReports()).Plan.Aligned);
        Assert.Contains("すでに登録", Assert.Throws<LibraryException>(() => pc2.RegisterExistingSub(sub.LibraryFilePath)).Message);
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
