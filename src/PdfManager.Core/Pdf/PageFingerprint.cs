using System.IO.Compression;
using Windows.Graphics.Imaging;

namespace PdfManager.Core.Pdf;

// 決定ログ DH-1・DH-2: 1 ページ目の見た目の指紋。サムネイル(幅 320px)を 128×128 の濃淡に縮めたもの。
// 同じ PDF は描き方が同じなので全く同じ値になり、保存し直し・画質の違いがあってもほとんど変わらない。番号 1 文字の違いでも数画素は違う。データベースには圧縮して入れる(ほとんど白なので小さくなる)
public static class PageFingerprint
{
    public const int Size = 128;

    // 似ている度合い(0〜1)の境目。「ほぼ同じ」は違う画素が 2 個以下、「似ている」(設定の「似ているものも出す」)は 30 個以下
    public const double StrictThreshold = 0.99;
    public const double LooseThreshold = 0.85;

    // 濃淡(0〜255)がこれより違う画素を「違う」とみなす
    private const int PixelThreshold = 16;

    // 違う画素 1 個で似ている度合いを 0.005 下げる
    private const double PenaltyPerPixel = 0.005;

    public static async Task<byte[]> FromImageAsync(Stream image)
    {
        using var memory = new MemoryStream();
        await image.CopyToAsync(memory);
        memory.Position = 0;
        var decoder = await BitmapDecoder.CreateAsync(memory.AsRandomAccessStream());
        var transform = new BitmapTransform { ScaledWidth = Size, ScaledHeight = Size, InterpolationMode = BitmapInterpolationMode.Fant };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var data = pixels.DetachPixelData();
        var gray = new byte[Size * Size];
        for (var i = 0; i < gray.Length; i++)
        {
            gray[i] = (byte)((data[i * 4 + 2] * 299 + data[i * 4 + 1] * 587 + data[i * 4] * 114) / 1000);
        }
        return gray;
    }

    public static async Task<byte[]> FromImageFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await FromImageAsync(stream);
    }

    // 登録していない PDF は、サムネイルと同じ大きさで 1 ページ目を描いてから作る(登録済みのものと同じ条件で比べるため)
    public static async Task<byte[]> FromPdfAsync(string pdfPath)
    {
        var (_, jpeg) = await PdfPageRenderer.RenderPageToJpegAsync(pdfPath, 0, PdfPageRenderer.ThumbnailWidth, uint.MaxValue);
        using var stream = new MemoryStream(jpeg);
        return await FromImageAsync(stream);
    }

    // 0〜1。違う画素が少ないほど 1 に近い(0 個なら 1)。minimum を渡すと、それを下回ると決まった時点で数えるのをやめる(多くの組を比べるとき速くするため)
    public static double Similarity(byte[] a, byte[] b, double minimum = 0)
    {
        if (a.Length != Size * Size || b.Length != Size * Size)
        {
            return 0;
        }
        var limit = minimum > 0 ? (int)Math.Floor((1 - minimum) / PenaltyPerPixel) + 1 : int.MaxValue;
        var changed = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i] - b[i]) > PixelThreshold && ++changed >= limit)
            {
                return 0;
            }
        }
        return Math.Clamp(1 - changed * PenaltyPerPixel, 0, 1);
    }

    public static byte[] Compress(byte[] fingerprint)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal))
        {
            deflate.Write(fingerprint);
        }
        return output.ToArray();
    }

    public static byte[] Decompress(byte[] stored)
    {
        using var input = new DeflateStream(new MemoryStream(stored), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
