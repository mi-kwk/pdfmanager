using System.IO.Compression;
using System.Text;
using PdfManager.Core.Images;
using PdfManager.Core.Import;
using PdfManager.Core.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PdfManager.Core.Tests;

public sealed class ImagePdfTests : IDisposable
{
    private static readonly byte[] Red = [0, 0, 255, 255];
    private static readonly byte[] Blue = [255, 0, 0, 255];

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Build_PagesFollowImageSizeScaleAndRotation()
    {
        // 200×100 ピクセル・96 dpi → 150×75 ポイント
        var jpeg = Path.Combine(_root, "a.jpg");
        await CreateImageAsync(jpeg, BitmapEncoder.JpegEncoderId, 200, 100);
        var png = Path.Combine(_root, "b.png");
        await CreateImageAsync(png, BitmapEncoder.PngEncoderId, 200, 100, transparent: true);
        var output = Path.Combine(_root, "out", "result.pdf");

        await ImagePdfBuilder.BuildAsync([new ImagePage(jpeg, 0, 0), new ImagePage(png, 0, 1)], maxLongSide: 100, output);

        using var document = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(2, document.NumberOfPages);
        var first = document.GetPage(1);
        Assert.Equal(150, first.Width, 1);
        Assert.Equal(75, first.Height, 1);
        // 縮小しても紙の大きさは変えない。回転すると縦横が入れ替わる
        var second = document.GetPage(2);
        Assert.Equal(75, second.Width, 1);
        Assert.Equal(150, second.Height, 1);
        var image = Assert.Single(second.GetImages());
        Assert.Equal(50, image.WidthInSamples);
        Assert.Equal(100, image.HeightInSamples);
    }

    [Fact]
    public async Task Decode_TransparentPartsBecomeWhite()
    {
        var png = Path.Combine(_root, "t.png");
        await CreateImageAsync(png, BitmapEncoder.PngEncoderId, 10, 10, transparent: true);

        var image = await ImagePdfBuilder.DecodeAsync(png, 0, null, 0);

        Assert.Equal([255, 255, 255, 255], image.Pixels[..4]);
    }

    [Fact]
    public async Task Decode_RespectsPhotoOrientationWhenScaling()
    {
        // 左半分が赤、右半分が青の 200×100。向きの情報 6(右に 90 度回して見る)なので、見た目は上が赤、下が青の 100×200
        var jpeg = Path.Combine(_root, "photo.jpg");
        await CreateImageAsync(jpeg, BitmapEncoder.JpegEncoderId, 200, 100, orientation: 6);

        var image = await ImagePdfBuilder.DecodeAsync(jpeg, 0, maxLongSide: 100, quarterTurns: 0);

        Assert.Equal((50, 100), (image.Width, image.Height));
        Assert.True(IsReddish(PixelAt(image, 25, 20)));
        Assert.False(IsReddish(PixelAt(image, 25, 80)));
    }

    [Fact]
    public async Task Decode_RotatesClockwise()
    {
        var png = Path.Combine(_root, "r.png");
        await CreateImageAsync(png, BitmapEncoder.PngEncoderId, 200, 100);

        var image = await ImagePdfBuilder.DecodeAsync(png, 0, null, quarterTurns: 1);

        // 左(赤)が上になる
        Assert.Equal((100, 200), (image.Width, image.Height));
        Assert.True(IsReddish(PixelAt(image, 50, 20)));
        Assert.False(IsReddish(PixelAt(image, 50, 180)));
    }

    [Fact]
    public async Task MultiPageTiff_EachPageIsAFrame()
    {
        var tiff = Path.Combine(_root, "scan.tif");
        await CreateImageAsync(tiff, BitmapEncoder.TiffEncoderId, 40, 30, frames: 3);

        Assert.Equal(3, await ImagePdfBuilder.GetFrameCountAsync(tiff));
        var output = Path.Combine(_root, "scan.pdf");
        await ImagePdfBuilder.BuildAsync([new ImagePage(tiff, 2, 0), new ImagePage(tiff, 0, 0)], null, output);
        using var document = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(2, document.NumberOfPages);
    }

    [Fact]
    public async Task ExtractZip_ReadsShiftJisNamesInNaturalOrderAndStaysInFolder()
    {
        var png = Path.Combine(_root, "p.png");
        await CreateImageAsync(png, BitmapEncoder.PngEncoderId, 4, 4);
        var zipPath = Path.Combine(_root, "本.zip");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create, Encoding.GetEncoding(932)))
        {
            foreach (var name in new[] { "第10話.png", "第2話.png", "メモ.txt", "__MACOSX/._第2話.png", "../外.png" })
            {
                zip.CreateEntryFromFile(png, name);
            }
        }
        var destination = Path.Combine(_root, "extracted");

        var frames = ImageSources.ExtractZipImages(zipPath, destination);

        Assert.Equal(["../外.png", "第2話.png", "第10話.png"], frames.Select(f => f.DisplayName));
        Assert.All(frames, f => Assert.Equal(destination, Path.GetDirectoryName(f.Path)));
        Assert.All(frames, f => Assert.True(File.Exists(f.Path)));
        Assert.False(File.Exists(Path.Combine(_root, "外.png")));
    }

    [Fact]
    public void CollectFiles_SeparatesPdfsImagesAndZips()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;
        foreach (var name in new[] { "folder/in.pdf", "folder/in.jpg", "10.jpg", "2.PNG", "a.zip", "b.pdf", "c.txt" })
        {
            File.WriteAllText(Path.Combine(_root, name), "");
        }

        var collected = ImportService.CollectFiles([folder, .. new[] { "10.jpg", "2.PNG", "a.zip", "b.pdf", "c.txt" }.Select(n => Path.Combine(_root, n))]);

        Assert.Equal(["in.pdf", "b.pdf"], collected.PdfFiles.Select(Path.GetFileName));
        Assert.Equal(["2.PNG", "10.jpg"], collected.ImageFiles.Select(Path.GetFileName));
        Assert.Equal(["a.zip"], collected.ZipFiles.Select(Path.GetFileName));
    }

    [Fact]
    public void NaturalOrder_ComparesNumbersByValue()
    {
        Assert.Equal(["p1", "p2", "p10"], new[] { "p10", "p2", "p1" }.Order(NaturalOrder.Instance));
    }

    private static byte[] PixelAt(ImagePdfBuilder.DecodedImage image, int x, int y) => image.Pixels.AsSpan((y * image.Width + x) * 4, 4).ToArray();

    // BGRA の赤が強いか(JPEG なので色は少しずれる)
    private static bool IsReddish(byte[] bgra) => bgra[2] > 150 && bgra[0] < 100;

    // 左半分が赤、右半分が青の画像を作る
    private static async Task CreateImageAsync(string path, Guid encoderId, int width, int height,
        bool transparent = false, ushort? orientation = null, int frames = 1)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = transparent && x < width / 2 ? new byte[] { 0, 0, 0, 0 } : x < width / 2 ? Red : Blue;
                color.CopyTo(pixels, (y * width + x) * 4);
            }
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        for (var frame = 0; frame < frames; frame++)
        {
            if (frame > 0)
            {
                await encoder.GoToNextFrameAsync();
            }
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
        }
        if (orientation is { } value)
        {
            await encoder.BitmapProperties.SetPropertiesAsync(
                [new KeyValuePair<string, BitmapTypedValue>("System.Photo.Orientation", new BitmapTypedValue(value, PropertyType.UInt16))]);
        }
        await encoder.FlushAsync();
        stream.Seek(0);
        await using var output = File.Create(path);
        await stream.AsStreamForRead().CopyToAsync(output);
    }
}
