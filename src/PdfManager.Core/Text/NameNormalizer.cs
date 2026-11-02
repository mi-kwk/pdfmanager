using System.Text;
using System.Text.RegularExpressions;

namespace PdfManager.Core.Text;

// docs/04「名前の正規化」: 全角・半角、大文字・小文字、空白の違いを吸収する
public static partial class NameNormalizer
{
    public static string Clean(string name) => Whitespace().Replace(name, " ").Trim();

    public static string Normalize(string name) => Clean(name.Normalize(NormalizationForm.FormKC)).ToLowerInvariant();

    // Normalize に加えて、ひらがな・カタカナの違いも吸収する(検索、決定ログ I-1。タイトルのシリーズ、決定ログ DG-2)
    public static string Fold(string text)
    {
        var normalized = Normalize(text);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            // カタカナ(ァ〜ヶ)をひらがなにそろえる
            builder.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c);
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
