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

    // 決定ログ DD-8: 削除予定の確認画面のプレビュー。ページ数と、指定したページの JPEG を返す(ページは 0 から)
    public static async Task<(int PageCount, byte[] Jpeg)> RenderPageToJpegAsync(string pdfPath, int pageIndex, uint maxWidth, uint maxHeight)
    {
        await using var input = File.OpenRead(pdfPath);
        var document = await PdfDocument.LoadFromStreamAsync(input.AsRandomAccessStream());
        var count = (int)document.PageCount;
        using var image = await RenderPageAsync(document, (uint)Math.Clamp(pageIndex, 0, Math.Max(0, count - 1)), maxWidth, maxHeight, BitmapEncoder.JpegEncoderId);
        using var memory = new MemoryStream();
        await image.AsStreamForRead().CopyToAsync(memory);
        return (count, memory.ToArray());
    }

    internal static async Task<InMemoryRandomAccessStream> RenderFirstPageAsync(string pdfPath, uint maxWidth, uint maxHeight, Guid encoderId)
    {
        // LoadFromFileAsync だとファイルのロックが解放されるタイミングを制御できないため、自前のストリームで読む
        await using var input = File.OpenRead(pdfPath);
        var document = await PdfDocument.LoadFromStreamAsync(input.AsRandomAccessStream());
        return await RenderPageAsync(document, 0, maxWidth, maxHeight, encoderId);
    }

    private static async Task<InMemoryRandomAccessStream> RenderPageAsync(PdfDocument document, uint pageIndex, uint maxWidth, uint maxHeight, Guid encoderId)
    {
        using var page = document.GetPage(pageIndex);

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
