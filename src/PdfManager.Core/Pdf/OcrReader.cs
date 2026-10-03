using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PdfManager.Core.Pdf;

public static class OcrReader
{
    private const uint RenderLongEdge = 2400;

    public static async Task<IReadOnlyList<TextLine>> ReadFirstPageAsync(string pdfPath)
    {
        var engine = CreateEngine();
        if (engine is null)
        {
            return [];
        }

        var limit = Math.Min(RenderLongEdge, OcrEngine.MaxImageDimension);
        using var image = await PdfPageRenderer.RenderFirstPageAsync(pdfPath, limit, limit, BitmapEncoder.PngEncoderId);
        var decoder = await BitmapDecoder.CreateAsync(image);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);

        return result.Lines
            .Where(line => line.Words.Count > 0)
            .Select(line => new TextLine(
                TextLine.JoinWords(line.Words.Select(w => w.Text)),
                line.Words.Max(w => w.BoundingRect.Height)))
            .ToList();
    }

    private static OcrEngine? CreateEngine() =>
        OcrEngine.TryCreateFromLanguage(new Language("ja")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
}
