using PdfManager.Core.Catalog;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.Core.Tests;

public class FieldValuesAndFileNameTests
{
    [Theory]
    [InlineData("2026-09")]
    [InlineData("2026/9")]
    [InlineData("202609")]
    [InlineData("2026年9月")]
    [InlineData("２０２６－０９")]
    public void YearMonth_AcceptsSeveralFormats(string input)
    {
        var value = FieldValues.Parse(FieldType.YearMonth, input)!;

        Assert.Equal("2026-09-01", value.Date);
        Assert.Equal("2026-09", FieldValues.Format(FieldType.YearMonth, value));
    }

    [Theory]
    [InlineData("2026-09-30")]
    [InlineData("2026/9/30")]
    [InlineData("20260930")]
    [InlineData("2026年9月30日")]
    public void Date_AcceptsSeveralFormats(string input)
    {
        Assert.Equal("2026-09-30", FieldValues.Parse(FieldType.Date, input)!.Date);
    }

    [Theory]
    [InlineData(FieldType.YearMonth, "2026-13")]
    [InlineData(FieldType.Date, "2026-02-30")]
    [InlineData(FieldType.Number, "十")]
    public void InvalidValues_ExplainTheExpectedFormat(FieldType type, string input)
    {
        Assert.Throws<CatalogException>(() => FieldValues.Parse(type, input));
    }

    [Fact]
    public void Number_AcceptsCommasAndFullWidth()
    {
        Assert.Equal(1234.5, FieldValues.Parse(FieldType.Number, "１,２３４.５")!.Number);
    }

    [Fact]
    public void EmptyInput_MeansNoValue()
    {
        Assert.Null(FieldValues.Parse(FieldType.Text, "   "));
    }

    [Fact]
    public void FileName_FromPrefixAndSuffix()
    {
        var built = FileNameBuilder.Build("上野店 売上", useTitle: false, "IODATA", "-MPC-WE1", "scan.pdf");

        Assert.Equal(new BuiltFileName("IODATA-MPC-WE1.pdf", false), built);
    }

    [Fact]
    public void FileName_SameAsTitle_ReplacesInvalidCharacters()
    {
        var built = FileNameBuilder.Build("売上/報告 2026:09", useTitle: true, "IODATA", "-x", "scan.pdf");

        Assert.Equal(new BuiltFileName("売上_報告 2026_09.pdf", true), built);
    }

    [Fact]
    public void FileName_SameAsEmptyTitle_UsesUntitled()
    {
        Assert.Equal("未入力.pdf", FileNameBuilder.Build("  ", useTitle: true, null, "", "scan.pdf").FileName);
    }

    [Fact]
    public void FileName_WithoutPrefixAndSuffix_UsesOriginalName()
    {
        Assert.Equal("scan001.pdf", FileNameBuilder.Build("タイトル", useTitle: false, null, "", "scan001.pdf").FileName);
    }

    [Fact]
    public void RecycleBin_IsAvailableOnFixedDrive()
    {
        Assert.True(RecycleBin.IsAvailableFor(Path.GetTempPath()));
    }
}
