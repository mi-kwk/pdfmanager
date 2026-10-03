using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfManager.Core.Text;

namespace PdfManager.Core.Catalog;

// 型に応じて document_field_values のどの列に入れるかが決まる(docs/03)
public sealed record FieldValue(string? Text, double? Number, string? Date);

// docs/04「項目の値の入力」
public static partial class FieldValues
{
    // 空欄なら null。形式が正しくなければ CatalogException(メッセージはそのまま画面に出す)
    public static FieldValue? Parse(FieldType type, string? input)
    {
        var text = NameNormalizer.Clean(input ?? "");
        if (text.Length == 0)
        {
            return null;
        }
        var halfWidth = text.Normalize(NormalizationForm.FormKC);

        switch (type)
        {
            case FieldType.Text:
            case FieldType.Select:
                return new FieldValue(text, null, null);

            case FieldType.Number:
                return double.TryParse(halfWidth.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? new FieldValue(null, number, null)
                    : throw new CatalogException($"「{text}」は数値として読めません。");

            case FieldType.Date:
                var date = DatePattern().Match(halfWidth);
                return date.Success && TryDate(date.Groups["y"].Value, date.Groups["m"].Value, date.Groups["d"].Value, out var day)
                    ? new FieldValue(null, null, day)
                    : throw new CatalogException($"「{text}」は日付として読めません。2026-09-30 のように入力してください。");

            case FieldType.YearMonth:
                var month = YearMonthPattern().Match(halfWidth);
                return month.Success && TryDate(month.Groups["y"].Value, month.Groups["m"].Value, "1", out var first)
                    ? new FieldValue(null, null, first)
                    : throw new CatalogException($"「{text}」は年月として読めません。2026-09 のように入力してください。");

            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    // 画面に表示する形(年月は 2026-09)
    public static string Format(FieldType type, FieldValue value) => type switch
    {
        FieldType.Number => value.Number?.ToString(CultureInfo.InvariantCulture) ?? "",
        FieldType.Date => value.Date ?? "",
        FieldType.YearMonth => value.Date is { Length: >= 7 } d ? d[..7] : "",
        _ => value.Text ?? "",
    };

    private static bool TryDate(string year, string month, string day, out string formatted)
    {
        formatted = "";
        if (!DateOnly.TryParseExact($"{year}-{int.Parse(month):00}-{int.Parse(day):00}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return false;
        }
        formatted = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    [GeneratedRegex(@"^(?<y>\d{4})(?:[-/.](?<m>\d{1,2})[-/.](?<d>\d{1,2})|年(?<m>\d{1,2})月(?<d>\d{1,2})日|(?<m>\d{2})(?<d>\d{2}))$")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^(?<y>\d{4})(?:[-/.](?<m>\d{1,2})|年(?<m>\d{1,2})月|(?<m>\d{2}))$")]
    private static partial Regex YearMonthPattern();
}
