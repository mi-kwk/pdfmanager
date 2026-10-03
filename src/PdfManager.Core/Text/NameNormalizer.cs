using System.Text;
using System.Text.RegularExpressions;

namespace PdfManager.Core.Text;

// docs/04「名前の正規化」: 全角・半角、大文字・小文字、空白の違いを吸収する
public static partial class NameNormalizer
{
    public static string Clean(string name) => Whitespace().Replace(name, " ").Trim();

    public static string Normalize(string name) => Clean(name.Normalize(NormalizationForm.FormKC)).ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
