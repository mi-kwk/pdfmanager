using PdfManager.Core.Pdf;

namespace PdfManager.Core.Tests;

public class TitleCandidatesTests
{
    [Fact]
    public void Build_OrdersLargestLineThenLeadingLinesThenMetadataThenFileName()
    {
        var lines = new[]
        {
            new TextLine("2026年9月", 10),
            new TextLine("上野●●●店 売上報告", 24),
            new TextLine("本文", 10),
        };

        var candidates = TitleCandidates.Build(lines, "Microsoft Word - 売上.docx", "上野●●●店売上202609.pdf");

        Assert.Equal(["上野●●●店 売上報告", "2026年9月", "Microsoft Word - 売上.docx", "上野●●●店売上202609"], candidates);
    }

    [Fact]
    public void Build_WithoutText_FallsBackToFileName()
    {
        var candidates = TitleCandidates.Build([], null, "scan001.pdf");

        Assert.Equal(["scan001"], candidates);
    }

    [Fact]
    public void Build_NormalizesWhitespaceAndTruncatesLongLines()
    {
        var longLine = new string('あ', 150);

        var candidates = TitleCandidates.Build([new TextLine("  商品A \n 取扱説明書  ", 20), new TextLine(longLine, 10)], "  ", "a.pdf");

        Assert.Equal("商品A 取扱説明書", candidates[0]);
        Assert.Equal(TitleCandidates.MaxLength, candidates[1].Length);
        Assert.Equal("a", candidates[2]);
    }

    [Theory]
    [InlineData(new[] { "上野", "店" }, "上野店")]
    [InlineData(new[] { "Quarterly", "Report" }, "Quarterly Report")]
    [InlineData(new[] { "第", "3", "版" }, "第3版")]
    public void JoinWords_AddsSpaceOnlyBetweenAsciiWords(string[] words, string expected)
    {
        Assert.Equal(expected, TextLine.JoinWords(words));
    }
}
