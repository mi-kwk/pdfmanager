using System.IO.Compression;
using System.Text;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Export;
using PdfManager.Core.Library;

namespace PdfManager.Core.Tests;

// 決定ログ CC-1〜CC-9(USB への出力)
public sealed class ExportTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 15, 30, 0, TimeSpan.FromHours(9));

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}")).FullName;
    private readonly LibraryInfo _main;
    private readonly LibraryStorage _storage;
    private readonly string _usb;
    private readonly long _maker;
    private readonly long _iodata;
    private readonly long _sony;

    public ExportTests()
    {
        _main = LibraryService.Create(Folder("D", "業務"), "PDF管理");
        _storage = new LibraryStorage(_main, () => []);
        _usb = Folder("U");
        using var connection = PdfManagerDatabase.Open(_main.DataFolder.DatabasePath);
        var catalog = new CatalogRepository(connection);
        _maker = catalog.AddGroup("メーカー", 1);
        _iodata = catalog.AddTag("IODATA", _maker);
        _sony = catalog.AddTag("SONY", _maker);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private void AddDocument(string id, string fileName, string title, params long[] tags)
    {
        var pdf = _main.DataFolder.GetPdfPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
        File.WriteAllText(pdf, $"%PDF-1.4 {id}");
        using var connection = PdfManagerDatabase.Open(_main.DataFolder.DatabasePath);
        new DocumentRepository(connection).Insert(
            new NewDocument(id, title, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pdf))), id + ".pdf", new FileInfo(pdf).Length, 1, fileName),
            [new PageText(1, title, TextSource.Embedded)], Now, null, tags.ToHashSet());
    }

    private ExportResult Export(IReadOnlyCollection<string> ids, ExportOptions? options = null, CancellationToken cancellation = default) =>
        ExportService.Run(_storage, ids, _usb, options ?? new ExportOptions(), Now, null, cancellation);

    [Fact]
    public void Folder_UsesFileNamesNumbersDuplicatesAndWritesList()
    {
        AddDocument("a1", "説明書.pdf", "説明書 A", _iodata);
        AddDocument("b2", "説明書.pdf", "説明書, B", _sony);

        var result = Export(["a1", "b2"]);

        Assert.Equal(Path.Combine(_usb, "PdfManager出力_2026-10-19_1530"), result.OutputPath);
        Assert.Equal(2, result.Exported);
        Assert.Equal(["一覧.csv", "説明書 (2).pdf", "説明書.pdf"], Directory.GetFiles(result.OutputPath).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var bytes = File.ReadAllBytes(Path.Combine(result.OutputPath, ExportService.ListFileName));
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("タイトル,ファイル,元のファイル名,メーカー,自由タグ,評価,登録日,メモ", lines[0]);
        Assert.Equal("説明書 A,説明書.pdf,a1.pdf,IODATA,,,2026-10-19,", lines[1]);
        // カンマを含むタイトルは "" で囲む
        Assert.StartsWith("\"説明書, B\",説明書 (2).pdf,", lines[2]);
    }

    [Fact]
    public void Folder_SplitsByTagGroup()
    {
        AddDocument("a1", "a.pdf", "A", _iodata);
        AddDocument("b2", "b.pdf", "B", _sony);
        AddDocument("c3", "c.pdf", "C");

        var result = Export(["a1", "b2", "c3"], new ExportOptions(SplitGroupId: _maker, WriteList: false));

        Assert.True(File.Exists(Path.Combine(result.OutputPath, "IODATA", "a.pdf")));
        Assert.True(File.Exists(Path.Combine(result.OutputPath, "SONY", "b.pdf")));
        Assert.True(File.Exists(Path.Combine(result.OutputPath, "未設定", "c.pdf")));
        Assert.False(File.Exists(Path.Combine(result.OutputPath, ExportService.ListFileName)));
    }

    [Fact]
    public void Zip_ContainsPdfsAndList()
    {
        AddDocument("a1", "説明書.pdf", "A", _iodata);

        var result = Export(["a1"], new ExportOptions(Zip: true));

        Assert.Equal(Path.Combine(_usb, "PdfManager出力_2026-10-19_1530.zip"), result.OutputPath);
        using var zip = ZipFile.OpenRead(result.OutputPath);
        Assert.Equal(["一覧.csv", "説明書.pdf"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetFiles(_usb, "*.tmp"));
    }

    [Fact]
    public void Output_NeverOverwritesExistingFolder()
    {
        AddDocument("a1", "a.pdf", "A");
        var first = Export(["a1"]);
        var second = Export(["a1"]);

        Assert.Equal(first.OutputPath + "_2", second.OutputPath);
        Assert.True(File.Exists(Path.Combine(first.OutputPath, "a.pdf")));
    }

    [Fact]
    public void DisconnectedSub_IsSkippedWithReason()
    {
        AddDocument("a1", "a.pdf", "A");
        AddDocument("b2", "b.pdf", "B");
        var sub = _storage.CreateSub(Folder("G", "業務"), "過去");
        _storage.Move(_storage.PlanMove(["b2"], sub.Id.ToString("D")));
        Directory.Move(sub.RootFolder, sub.RootFolder + "-away");
        _storage.Refresh();

        var plan = ExportService.Plan(_storage, ["a1", "b2"], _usb);
        var result = Export(["a1", "b2"]);

        Assert.Equal((1, 1), (plan.Count, plan.UnavailableCount));
        Assert.Equal(1, result.Exported);
        Assert.Contains("過去", Assert.Single(result.Skipped));
    }

    [Fact]
    public void Cancelled_RemovesPartialOutput()
    {
        AddDocument("a1", "a.pdf", "A");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var result = Export(["a1"], cancellation: cancel.Token);

        Assert.True(result.Cancelled);
        Assert.Empty(Directory.GetFileSystemEntries(_usb));
    }
}
