using System.IO.Compression;
using System.Text;
using PdfManager.Core.Text;

namespace PdfManager.Core.Images;

// PDF にする画像 1 枚(複数ページの TIFF は 1 ページずつ)
public sealed record ImageFrame(string Path, int FrameIndex, string DisplayName);

// 取り込みで選ばれた画像・ZIP(F-26、決定ログ Y-1)
public static class ImageSources
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff",
    };

    public static string DialogFilter => "画像 (*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff)|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff|ZIP ファイル (*.zip)|*.zip";

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsZip(string path) => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    // ZIP の中の画像を、中の名前の自然な順に取り出す。
    // 名前は日本語の Windows で作った ZIP に合わせて Shift_JIS で読む(UTF-8 の印がある ZIP は UTF-8 で読まれる)。
    // 取り出すときは連番の名前にする(ZIP の中の名前をそのまま使うと、フォルダの外に書き出される ZIP がありうるため)
    public static IReadOnlyList<ImageFrame> ExtractZipImages(string zipPath, string destinationFolder, CancellationToken cancellation = default)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Read, Encoding.GetEncoding(932));
        var entries = zip.Entries
            .Where(e => e.Name.Length > 0 && IsImage(e.Name) && !IsMacMetadata(e.FullName))
            .OrderBy(e => e.FullName.Replace('\\', '/'), NaturalOrder.Instance)
            .ToList();
        Directory.CreateDirectory(destinationFolder);
        var frames = new List<ImageFrame>();
        foreach (var (entry, index) in entries.Select((e, i) => (e, i)))
        {
            cancellation.ThrowIfCancellationRequested();
            var path = Path.Combine(destinationFolder, $"{index + 1:D5}{Path.GetExtension(entry.Name).ToLowerInvariant()}");
            entry.ExtractToFile(path, overwrite: true);
            frames.Add(new ImageFrame(path, 0, entry.FullName.Replace('\\', '/')));
        }
        return frames;
    }

    // Mac で作った ZIP に入る管理用のファイル
    private static bool IsMacMetadata(string fullName) =>
        fullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(fullName).StartsWith("._", StringComparison.Ordinal);
}
