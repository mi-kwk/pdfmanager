using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace PdfManager.App.Converters;

// サムネイルのパス → 画像。表示サイズに合わせて縮小して読み、ファイルはロックしない
public sealed class ThumbnailConverter : IValueConverter
{
    public int DecodeWidth { get; set; } = 240;

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path))
        {
            return null;
        }
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = DecodeWidth;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
