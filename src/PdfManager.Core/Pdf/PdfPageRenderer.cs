using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PdfManager.Core.Pdf;

public static class PdfPageRenderer
{
    public const uint ThumbnailWidth = 320;

    public static async Task RenderFirstPageToJpegAsync(string pdfPath, string jpegPath, uint maxWidth = ThumbnailWidth)
    {
        using var image = await RenderFirstPageAsync(pdfPath, maxWidth, uint.MaxValue, BitmapEncoder.JpegEncoderId);
        Directory.CreateDirectory(Path.GetDirectoryName(jpegPath)!);
        await using var output = File.Create(jpegPath);
        await image.AsStreamForRead().CopyToAsync(output);
    }

    internal static async Task<InMemoryRandomAccessStream> RenderFirstPageAsync(string pdfPath, uint maxWidth, uint maxHeight, Guid encoderId)
    {
        // LoadFromFileAsync だとファイルのロックが解放されるタイミングを制御できないため、自前のストリームで読む
        await using var input = File.OpenRead(pdfPath);
        var document = await PdfDocument.LoadFromStreamAsync(input.AsRandomAccessStream());
        using var page = document.GetPage(0);

        var scale = Math.Min(maxWidth / page.Size.Width, maxHeight / page.Size.Height);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
            DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale),
            BitmapEncoderId = encoderId,
        };

        var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output, options);
        output.Seek(0);
        return output;
    }
}
