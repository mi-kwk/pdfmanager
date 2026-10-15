using System.Runtime.InteropServices;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PdfManager.Core.Images;

// PDF の 1 ページにする画像。QuarterTurns は右回りに 90 度ずつ回す回数
public sealed record ImagePage(string Path, int FrameIndex, int QuarterTurns);

// 画像から PDF を作る(F-26、決定ログ Y-3・Y-4)。
// 画像は Windows の機能で読み、縮小・回転してから JPEG にして PDF に入れる(PNG などの透明な部分は白にする)
public static class ImagePdfBuilder
{
    // 決定ログ Y-3: 長辺の大きさの選択肢。null は「そのまま」
    public static IReadOnlyList<int?> MaxLongSideChoices { get; } = [null, 3000, 2000, 1500];

    public const int DefaultMaxLongSide = 3000;

    private const float JpegQuality = 0.9f;
    private const float ThumbnailQuality = 0.8f;
    private const double PointsPerInch = 72;
    private const double DefaultDpi = 96;

    public static async Task<int> GetFrameCountAsync(string path)
    {
        await using var input = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
        return (int)decoder.FrameCount;
    }

    // 画面に並べるサムネイル(JPEG)。回転は画面の側で行う
    public static async Task<byte[]> CreateThumbnailAsync(string path, int frameIndex, int maxSide)
    {
        var image = await DecodeAsync(path, frameIndex, maxSide, 0);
        return await EncodeJpegAsync(image, ThumbnailQuality);
    }

    // 決定ログ Y-4: ページの大きさは画像に合わせる(画像の解像度から実際の大きさにする。余白なし)
    public static async Task BuildAsync(IReadOnlyList<ImagePage> pages, int? maxLongSide, string outputPath,
        IProgress<int>? progress = null, CancellationToken cancellation = default)
    {
        if (pages.Count == 0)
        {
            throw new ArgumentException("画像がありません。", nameof(pages));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        try
        {
            await using var output = File.Create(outputPath);
            using var builder = new PdfDocumentBuilder(output);
            foreach (var (page, index) in pages.Select((p, i) => (p, i)))
            {
                cancellation.ThrowIfCancellationRequested();
                var image = await DecodeAsync(page.Path, page.FrameIndex, maxLongSide, page.QuarterTurns);
                var jpeg = await EncodeJpegAsync(image, JpegQuality);
                var pdfPage = builder.AddPage(image.WidthPoints, image.HeightPoints);
                pdfPage.AddJpeg(jpeg, new PdfRectangle(0, 0, image.WidthPoints, image.HeightPoints));
                progress?.Report(index + 1);
            }
            builder.Build();
        }
        catch
        {
            File.Delete(outputPath);
            throw;
        }
    }

    internal sealed record DecodedImage(byte[] Pixels, int Width, int Height, double WidthPoints, double HeightPoints);

    // BGRA(透明な部分は白にしたもの)で読む。写真の向き(EXIF)に従い、長辺を maxLongSide 以下に縮め、右回りに quarterTurns × 90 度回す
    internal static async Task<DecodedImage> DecodeAsync(string path, int frameIndex, int? maxLongSide, int quarterTurns)
    {
        await using var input = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
        var frame = await decoder.GetFrameAsync((uint)frameIndex);
        var width = (int)frame.OrientedPixelWidth;
        var height = (int)frame.OrientedPixelHeight;
        var widthPoints = width / ValidDpi(frame.DpiX) * PointsPerInch;
        var heightPoints = height / ValidDpi(frame.DpiY) * PointsPerInch;

        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        if (maxLongSide is { } max && Math.Max(width, height) > max)
        {
            var scale = (double)max / Math.Max(width, height);
            width = Math.Max(1, (int)Math.Round(width * scale));
            height = Math.Max(1, (int)Math.Round(height * scale));
            transform.ScaledWidth = (uint)width;
            transform.ScaledHeight = (uint)height;
        }
        var provider = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var pixels = provider.DetachPixelData();
        FillTransparentWithWhite(pixels);

        var turns = ((quarterTurns % 4) + 4) % 4;
        if (turns % 2 == 1)
        {
            (widthPoints, heightPoints) = (heightPoints, widthPoints);
        }
        var (rotated, rotatedWidth, rotatedHeight) = Rotate(pixels, width, height, turns);
        return new DecodedImage(rotated, rotatedWidth, rotatedHeight, widthPoints, heightPoints);
    }

    // 解像度の情報がない・おかしい画像は 96 dpi とみなす
    private static double ValidDpi(double dpi) => dpi is >= 50 and <= 2400 ? dpi : DefaultDpi;

    // 乗算済みの BGRA を白の上に重ねた色にする
    private static void FillTransparentWithWhite(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var transparency = 255 - pixels[i + 3];
            if (transparency == 0)
            {
                continue;
            }
            pixels[i] = (byte)(pixels[i] + transparency);
            pixels[i + 1] = (byte)(pixels[i + 1] + transparency);
            pixels[i + 2] = (byte)(pixels[i + 2] + transparency);
            pixels[i + 3] = 255;
        }
    }

    private static (byte[] Pixels, int Width, int Height) Rotate(byte[] pixels, int width, int height, int turns)
    {
        if (turns == 0)
        {
            return (pixels, width, height);
        }
        var (newWidth, newHeight) = turns == 2 ? (width, height) : (height, width);
        var result = new byte[pixels.Length];
        var source = MemoryMarshal.Cast<byte, int>(pixels.AsSpan());
        var target = MemoryMarshal.Cast<byte, int>(result.AsSpan());
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (newX, newY) = turns switch
                {
                    1 => (height - 1 - y, x),
                    2 => (width - 1 - x, height - 1 - y),
                    _ => (y, width - 1 - x),
                };
                target[newY * newWidth + newX] = source[y * width + x];
            }
        }
        return (result, newWidth, newHeight);
    }

    private static async Task<byte[]> EncodeJpegAsync(DecodedImage image, float quality)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)image.Width, (uint)image.Height, DefaultDpi, DefaultDpi, image.Pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.AsStreamForRead().ReadExactlyAsync(bytes);
        return bytes;
    }
}
