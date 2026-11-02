using Microsoft.Data.Sqlite;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Pdf;
using PdfManager.Core.Search;
using PdfManager.Core.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PdfManager.Core.Tests;

// 決定ログ DG-1〜DG-4(重複タイトルの次の番号)、DH-1〜DH-7(1 ページ目の見た目での重複チェック)、DI-4〜DI-7(タイトルの付け直し)
public sealed class DuplicateAndTitleTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}")).FullName;
    private readonly SqliteConnection _connection;

    public DuplicateAndTitleTests()
    {
        _connection = PdfManagerDatabase.Open(Path.Combine(_folder, "test.db"));
        PdfManagerDatabase.Initialize(_connection);
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    // ==== 重複タイトル(DG) ====

    private static readonly string[] Series = ["とんこつラーメン", "とんこつラーメン１", "とんこつラーメン２", "とんこつらーめん３", "とんこつらーめん１０"];

    [Theory]
    [InlineData("とんこつらーめん１", "とんこつらーめん１１")]
    [InlineData("とんこつラーメン２", "とんこつラーメン１１")]
    [InlineData("とんこつラーメン", "とんこつラーメン１１")]
    [InlineData("トンコツラーメン 3", "トンコツラーメン １１")]
    public void Next_UsesLargestNumberOfSeriesIgnoringKanaAndWidth(string title, string expected)
    {
        Assert.Equal(expected, TitleNumbers.Next(title, Series));
    }

    [Theory]
    [InlineData("取扱説明書", new[] { "取扱説明書" }, "取扱説明書２")]
    [InlineData("Manual", new[] { "Manual" }, "Manual2")]
    [InlineData("報告書_09", new[] { "報告書_09", "報告書_3" }, "報告書_10")]
    [InlineData("レシピ1", new[] { "レシピ1", "レシピ０５" }, "レシピ０６")]
    public void Next_NumberStyleFollowsLargestTitle(string title, string[] existing, string expected)
    {
        Assert.Equal(expected, TitleNumbers.Next(title, existing));
    }

    [Fact]
    public void SeriesKey_IgnoresKanaWidthAndSeparators()
    {
        Assert.Equal(TitleNumbers.SeriesKey("とんこつラーメン１"), TitleNumbers.SeriesKey("とんこつらーめん_10"));
        Assert.NotEqual(TitleNumbers.SeriesKey("とんこつらーめん博多店_Ver1"), TitleNumbers.SeriesKey("とんこつらーめん１"));
    }

    // ==== タイトルの付け直し(DI) ====

    [Fact]
    public void TitleBatch_SwapIsAllowed_DuplicatesAreReported_AndFileNamesFollowTitle()
    {
        Insert("d2", "とんこつラーメン２", useTitleAsFileName: true);
        Insert("d4", "とんこつらーめん４", useTitleAsFileName: true);
        Insert("d5", "とんこつらーめん５", useTitleAsFileName: false, fileName: "fixed.pdf");

        // 2 と 4 を入れ替える途中(片方だけ変えた状態)は重なる
        var conflicts = TitleBatch.FindConflicts(_connection, new Dictionary<string, string> { ["d2"] = "とんこつらーめん４" });
        Assert.Equal(["d2"], conflicts);

        var swap = new Dictionary<string, string> { ["d2"] = "とんこつらーめん４", ["d4"] = "とんこつらーめん２", ["d5"] = "とんこつらーめん６" };
        Assert.Empty(TitleBatch.FindConflicts(_connection, swap));

        TitleBatch.Apply(_connection, swap.Select(p => new TitleChange(p.Key, p.Value)).ToList(), DateTimeOffset.UtcNow);

        Assert.Equal(("とんこつらーめん４", "とんこつらーめん４.pdf"), Read("d2"));
        Assert.Equal(("とんこつらーめん２", "とんこつらーめん２.pdf"), Read("d4"));
        Assert.Equal(("とんこつらーめん６", "fixed.pdf"), Read("d5"));
    }

    // 決定ログ DL-1〜DL-3: 評価だけを保存する(ほかの値は変えない)
    [Fact]
    public void SetRating_ChangesOnlyRating()
    {
        Insert("r1", "評価1", useTitleAsFileName: true);
        Insert("r2", "評価2", useTitleAsFileName: true);

        new DocumentRepository(_connection).SetRating(["r1", "r2"], 4, DateTimeOffset.UtcNow);

        var details = new DocumentRepository(_connection).GetDetails(["r1", "r2"]);
        Assert.All(details, d => Assert.Equal(4, d.Rating));
        Assert.Equal(("評価1", "評価1.pdf"), Read("r1"));
    }

    [Fact]
    public void TitleBatch_ConflictWithDocumentOutsideTheBatch()
    {
        Insert("d1", "博多店メニュー", useTitleAsFileName: true);
        Insert("d2", "仮1", useTitleAsFileName: true);

        Assert.Equal(["d2"], TitleBatch.FindConflicts(_connection, new Dictionary<string, string> { ["d2"] = "博多店メニュー" }));
    }

    // ==== 1 ページ目の見た目(DH) ====

    [Fact]
    public async Task Fingerprint_SameLooksSame_DifferentTextDiffers()
    {
        var a = Pdf("a", "RAMEN RECIPE 10", "Pork bone soup|Boil 12 hours");
        var copy = Pdf("copy", "RAMEN RECIPE 10", "Pork bone soup|Boil 12 hours");
        var other = Pdf("other", "SALE 50% OFF", "Pork bone soup|Boil 12 hours");

        var fa = await PageFingerprint.FromPdfAsync(a);
        var fcopy = await PageFingerprint.FromPdfAsync(copy);
        var fother = await PageFingerprint.FromPdfAsync(other);

        Assert.True(PageFingerprint.Similarity(fa, fcopy) >= PageFingerprint.StrictThreshold);
        Assert.True(PageFingerprint.Similarity(fa, fother) < PageFingerprint.StrictThreshold);
        Assert.Equal(fa, PageFingerprint.Decompress(PageFingerprint.Compress(fa)));
    }

    // 表紙が同じで番号 1 文字だけ違うものは「ほぼ同じ」ではなく「似ている」
    [Fact]
    public async Task Fingerprint_OneDigitDifference_IsSimilarButNotSame()
    {
        var one = await PageFingerprint.FromPdfAsync(Pdf("r1", "RAMEN 1", "recipe body"));
        var ten = await PageFingerprint.FromPdfAsync(Pdf("r10", "RAMEN 10", "recipe body"));

        var similarity = PageFingerprint.Similarity(one, ten);

        Assert.InRange(similarity, PageFingerprint.LooseThreshold, PageFingerprint.StrictThreshold - 0.0001);
        Assert.Equal(0, PageFingerprint.Similarity(one, ten, PageFingerprint.StrictThreshold));
    }

    [Fact]
    public async Task FindGroups_SkipsSameSeriesAndNotDuplicatePairs()
    {
        var page = await PageFingerprint.FromPdfAsync(Pdf("p", "POSTER", "x"));
        var different = await PageFingerprint.FromPdfAsync(Pdf("q", "MENU", "a|b|c|d|e|f"));
        foreach (var id in new[] { "a", "b", "c", "d", "e" })
        {
            Insert(id, id, useTitleAsFileName: true);
        }
        Save("a", page);
        Save("b", page);
        Save("c", page);
        Save("d", different);
        Save("e", page);
        // e は a と同じ版のまとまり
        Exec("INSERT INTO version_series (id, name) VALUES ('s', 's'); UPDATE documents SET series_id = 's', version_no = 1 WHERE id = 'a'; UPDATE documents SET series_id = 's', version_no = 2 WHERE id = 'e';");
        DuplicateFinder.MarkNotDuplicate(_connection, ["b", "c"]);

        var groups = DuplicateFinder.FindGroups(_connection, ["a", "b", "c", "d", "e"], PageFingerprint.StrictThreshold);

        // a-b、a-c、e-b、e-c は似ている(b-c は「重複ではない」、a-e は同じ版のまとまり)ので、a・b・c・e が 1 つの組になる
        var group = Assert.Single(groups);
        Assert.Equal(["a", "b", "c", "e"], group.DocumentIds.Order());
        Assert.Empty(DuplicateFinder.FindGroups(_connection, ["b", "c", "d"], PageFingerprint.StrictThreshold));
        Assert.Single(DuplicateFinder.FindSimilar(different, DuplicateFinder.Load(_connection, ["c", "d"]), PageFingerprint.StrictThreshold));
    }

    private string Pdf(string name, string title, string body)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.SetTextAndFillColor(200, 30, 30);
        page.DrawRectangle(new PdfPoint(0, 600), 595, 242, 1, fill: true);
        page.SetTextAndFillColor(255, 255, 255);
        page.AddText(title, 40, new PdfPoint(50, 700), font);
        page.SetTextAndFillColor(0, 0, 0);
        var y = 550.0;
        foreach (var line in body.Split('|'))
        {
            page.AddText(line, 14, new PdfPoint(50, y), font);
            y -= 22;
        }
        var path = Path.Combine(_folder, name + ".pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private void Save(string id, byte[] fingerprint)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO page_fingerprints (document_id, data) VALUES ($id, $data);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$data", PageFingerprint.Compress(fingerprint));
        command.ExecuteNonQuery();
    }

    private void Insert(string id, string title, bool useTitleAsFileName, string? fileName = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO documents (id, title, file_hash, original_file_name, file_size, created_at, updated_at, file_name, file_name_source_type)
            VALUES ($id, $title, 'h-' || $id, 'orig.pdf', 1, '2026-11-03T00:00:00Z', '2026-11-03T00:00:00Z', $fileName, $source);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$fileName", fileName ?? title + ".pdf");
        command.Parameters.AddWithValue("$source", useTitleAsFileName ? "title" : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private (string Title, string FileName) Read(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT title, file_name FROM documents WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetString(0), reader.GetString(1));
    }

    private void Exec(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
