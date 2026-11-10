using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PdfManager.App.Converters;

// 一覧の字下げ(決定ログ FC-3)。深さ 1 つにつき 20 ピクセル左を空ける
public sealed class DepthIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness((value is int depth ? depth : 0) * 20, 2, 0, 2);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
