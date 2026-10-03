using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PdfManager.Core.Tests;

internal static class TestPdfs
{
    public static void CreateTextPdf(string path, IReadOnlyList<(string Text, double Size)> firstPageLines, int pageCount = 1, string? metadataTitle = null)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        if (metadataTitle is not null)
        {
            builder.DocumentInformation.Title = metadataTitle;
        }

        var page = builder.AddPage(PageSize.A4);
        var y = 780.0;
        foreach (var (text, size) in firstPageLines)
        {
            page.AddText(text, size, new PdfPoint(50, y), font);
            y -= size * 2;
        }
        for (var i = 2; i <= pageCount; i++)
        {
            builder.AddPage(PageSize.A4).AddText($"Page {i} body text", 12, new PdfPoint(50, 780), font);
        }

        File.WriteAllBytes(path, builder.Build());
    }

    // スキャンした PDF の代わりに、文字を持たず画像だけの PDF を作る
    public static void CreateImageOnlyPdf(string path, byte[] jpeg)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        page.AddJpeg(jpeg, new PdfRectangle(0, 0, page.PageSize.Width, page.PageSize.Height));
        File.WriteAllBytes(path, builder.Build());
    }
}
