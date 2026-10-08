using System.Globalization;
using System.Windows.Data;

namespace PdfManager.App.Converters;

// true のときに薄く表示する(取り込み画面の「取り込まない」など)
public sealed class BoolToOpacityConverter : IValueConverter
{
    public double TrueOpacity { get; set; } = 0.5;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? TrueOpacity : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
