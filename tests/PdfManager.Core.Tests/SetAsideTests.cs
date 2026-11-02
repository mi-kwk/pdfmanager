using PdfManager.Core.Backup;
using PdfManager.Core.Import;
using PdfManager.Core.Library;
using PdfManager.Core.Pdf;
using PdfManager.Core.Text;

namespace PdfManager.Core.Tests;

// 決定ログ DD-1〜DD-10(保留・削除予定のフォルダ)、DF-2・DF-3(タイトルの番号と候補)
public sealed class SetAsideTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}")).FullName;
    private readonly LibraryInfo _main;
    private readonly SetAsideStore _store;
    private readonly string _source;

    public SetAsideTests()
    {
        _main = LibraryService.Create(Folder("D", "業務"), "PDF管理");
        _store = new SetAsideStore(_main.DataFolder);
        _source = Folder("C", "受け取った");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private string SourceFile(string name, string content = "%PDF-1.4")
    {
        var path = Path.Combine(_source, name);
        File.WriteAllText(path, content + name);
        return path;
    }

    [Fact]
    public void Move_CutsFromOriginalFolder_AndRemembersOriginalPath()
    {
        var original = SourceFile("見積.pdf");

        var moved = _store.Move(original, SetAsideKind.Hold);

        Assert.False(File.Exists(original));
        Assert.Equal(Path.Combine(_main.RootFolder, "保留", "見積.pdf"), moved);
        var held = Assert.Single(_store.List(SetAsideKind.Hold));
        Assert.Equal((moved, original), (held.Path, held.OriginalPath));
        Assert.Equal(1, _store.Count(SetAsideKind.Hold));
        Assert.Equal(0, _store.Count(SetAsideKind.Delete));
    }

    [Fact]
    public void Move_SameName_AddsNumber()
    {
        _store.Move(SourceFile("見積.pdf"), SetAsideKind.Delete);
        File.WriteAllText(Path.Combine(_source, "見積.pdf"), "2 回目");

        var second = _store.Move(Path.Combine(_source, "見積.pdf"), SetAsideKind.Delete);

        Assert.Equal("見積 (2).pdf", Path.GetFileName(second));
        Assert.Equal(2, _store.List(SetAsideKind.Delete).Count);
    }

    [Fact]
    public void Move_FromHoldToDelete_KeepsFirstOriginalPath()
    {
        var original = SourceFile("見積.pdf");
        var held = _store.Move(original, SetAsideKind.Hold);

        var deleting = _store.Move(held, SetAsideKind.Delete);

        Assert.Empty(_store.List(SetAsideKind.Hold));
        Assert.Equal(original, Assert.Single(_store.List(SetAsideKind.Delete)).OriginalPath);
        Assert.Equal(SetAsideKind.Delete, _store.KindOf(deleting));
        Assert.Null(_store.KindOf(original));
    }

    [Fact]
    public void FilesPutDirectly_AreListedWithoutOriginalPath()
    {
        Directory.CreateDirectory(_store.FolderOf(SetAsideKind.Hold));
        File.WriteAllText(Path.Combine(_store.FolderOf(SetAsideKind.Hold), "直接.pdf"), "x");
        File.WriteAllText(Path.Combine(_store.FolderOf(SetAsideKind.Hold), "メモ.txt"), "x");

        var file = Assert.Single(_store.List(SetAsideKind.Hold));

        Assert.Equal(("直接.pdf", null), (file.FileName, file.OriginalPath));
    }

    [Fact]
    public void Delete_RemovesFilesAndRecords()
    {
        var deleting = _store.Move(SourceFile("不要.pdf"), SetAsideKind.Delete);

        var problems = _store.Delete([deleting], File.Delete);

        Assert.Empty(problems);
        Assert.Empty(_store.List(SetAsideKind.Delete));
        // 同じ名前でもう一度送っても、前の記録は残っていない
        var again = _store.Move(SourceFile("不要.pdf"), SetAsideKind.Delete);
        Assert.Equal("不要.pdf", Path.GetFileName(again));
    }

    [Fact]
    public void Backup_CopiesHoldFolder_AndRestoreBringsItBack()
    {
        var storage = new LibraryStorage(_main, () => []);
        var held = _store.Move(SourceFile("確認中.pdf"), SetAsideKind.Hold);
        _store.Move(SourceFile("消す.pdf"), SetAsideKind.Delete);
        var backupRoot = Folder("H");

        Assert.True(BackupService.Plan(storage, backupRoot).Bytes > new FileInfo(held).Length);
        BackupService.Run(storage, backupRoot, 5, now: new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(9)));
        var backupFolder = BackupService.FolderFor(backupRoot, _main);
        Assert.True(File.Exists(Path.Combine(backupFolder, "保留", "確認中.pdf")));
        Assert.False(Directory.Exists(Path.Combine(backupFolder, "削除予定")));

        // 保留から無くなったものは、次のバックアップで消える
        _store.Delete([held], File.Delete);
        _store.Move(SourceFile("次の.pdf"), SetAsideKind.Hold);
        BackupService.Run(storage, backupRoot, 5, now: new DateTimeOffset(2026, 11, 2, 9, 5, 0, TimeSpan.FromHours(9)));
        Assert.Equal(["次の.pdf"], Directory.GetFiles(Path.Combine(backupFolder, "保留")).Select(Path.GetFileName));

        var backup = BackupService.ReadInfo(backupFolder)!;
        var restored = BackupService.Restore(backup, backup.Latest!.Name, Folder("E", "戻した"));
        var restoredHold = new SetAsideStore(restored.Library.DataFolder);
        var file = Assert.Single(restoredHold.List(SetAsideKind.Hold));
        Assert.Equal("次の.pdf", file.FileName);
        Assert.Equal(Path.Combine(_source, "次の.pdf"), file.OriginalPath);
    }

    [Theory]
    [InlineData("とんこつらーめんレシピ１０", new[] { "とんこつらーめんレシピ１０" }, "とんこつらーめんレシピ１１")]
    [InlineData("とんこつらーめんレシピ１０", new[] { "とんこつらーめんレシピ１０", "とんこつらーめんレシピ15", "しょうゆレシピ30" }, "とんこつらーめんレシピ16")]
    [InlineData("報告書_09", new[] { "報告書_09" }, "報告書_10")]
    [InlineData("報告書_01", new[] { "報告書_01" }, "報告書_02")]
    [InlineData("取扱説明書", new[] { "取扱説明書" }, "取扱説明書")]
    public void NextNumbered_UsesLargestNumberPlusOne(string chosen, string[] existing, string expected)
    {
        Assert.Equal(expected, TitleNumbers.NextNumbered(chosen, existing));
    }

    [Theory]
    [InlineData("とん＄＃", false)]
    [InlineData("2*@;{", false)]
    [InlineData("あ", false)]
    [InlineData("使用注意ＸＸＸ", true)]
    [InlineData("商品A 取扱説明書", true)]
    public void IsMeaningful_DropsMostlySymbols(string text, bool expected)
    {
        Assert.Equal(expected, TitleCandidates.IsMeaningful(text));
    }

    [Fact]
    public void Build_DropsMeaninglessTextButKeepsFileName()
    {
        var candidates = TitleCandidates.Build([new TextLine("とん＄＃", 30), new TextLine("2*@;{", 10)], null, "#&.pdf");

        Assert.Equal(["#&"], candidates);
    }
}
