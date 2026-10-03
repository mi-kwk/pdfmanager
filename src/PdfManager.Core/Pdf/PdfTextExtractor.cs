using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PdfManager.Core.Pdf;

public sealed record PdfTextContent(
    int PageCount,
    IReadOnlyList<string> PageTexts,
    IReadOnlyList<TextLine> FirstPageLines,
    string? MetadataTitle);

public static class PdfTextExtractor
{
    public static PdfTextContent Extract(string pdfPath)
    {
        using var document = PdfDocument.Open(pdfPath);
        var pageTexts = new List<string>(document.NumberOfPages);
        IReadOnlyList<TextLine> firstPageLines = [];

        foreach (var page in document.GetPages())
        {
            pageTexts.Add(ContentOrderTextExtractor.GetText(page).Trim());
            if (page.Number == 1)
            {
                firstPageLines = GroupIntoLines(page.GetWords());
            }
        }

        return new PdfTextContent(document.NumberOfPages, pageTexts, firstPageLines, document.Information.Title);
    }

    private static List<TextLine> GroupIntoLines(IEnumerable<Word> words)
    {
        var lines = new List<List<Word>>();
        var ordered = words
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ThenBy(w => w.BoundingBox.Left);

        foreach (var word in ordered)
        {
            var current = lines.LastOrDefault();
            var tolerance = Math.Max(FontSize(word), current is null ? 0 : FontSize(current[0])) * 0.5;
            if (current is not null && Math.Abs(current[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= tolerance)
            {
                current.Add(word);
            }
            else
            {
                lines.Add([word]);
            }
        }

        return lines
            .Select(line => line.OrderBy(w => w.BoundingBox.Left).ToList())
            .Select(line => new TextLine(TextLine.JoinWords(line.Select(w => w.Text)), line.Max(FontSize)))
            .ToList();
    }

    private static double FontSize(Word word) => word.Letters.Count == 0 ? 0 : word.Letters.Max(l => l.PointSize);
}
