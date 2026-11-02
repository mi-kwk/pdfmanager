using System.Text.RegularExpressions;

namespace PdfManager.Core.Pdf;

// docs/04「タイトル候補」: 最も大きい文字の行 → 先頭の行 → PDF のプロパティ → ファイル名 の順で最大 5 件。
// 決定ログ DF-3: PDF の文字から作った候補のうち、記号ばかりの意味のない文字列は外す(ファイル名は外さない)
public static partial class TitleCandidates
{
    public const int MaxLength = 100;
    private const int LeadingLineCount = 2;

    public static IReadOnlyList<string> Build(IReadOnlyList<TextLine> firstPageLines, string? metadataTitle, string fileName)
    {
        var candidates = new List<string>();

        void Add(string? value, bool fromText = true)
        {
            var normalized = Normalize(value);
            if (normalized.Length > 0 && !candidates.Contains(normalized) && (!fromText || IsMeaningful(normalized)))
            {
                candidates.Add(normalized);
            }
        }

        if (firstPageLines.Count > 0)
        {
            Add(firstPageLines.MaxBy(l => l.Size)!.Text);
        }
        foreach (var line in firstPageLines.Take(LeadingLineCount))
        {
            Add(line.Text);
        }
        Add(metadataTitle);
        Add(Path.GetFileNameWithoutExtension(fileName), fromText: false);

        return candidates;
    }

    // 文字・数字が 7 割以上で 2 文字以上あれば、意味のある文字列とみなす(読み取りに失敗した「とん＄＃」「2*@;{」などを外す)
    public static bool IsMeaningful(string text)
    {
        var visible = text.Where(c => !char.IsWhiteSpace(c)).ToList();
        if (visible.Contains('�'))
        {
            return false;
        }
        var letters = visible.Count(char.IsLetterOrDigit);
        return letters >= 2 && letters >= visible.Count * 0.7;
    }

    private static string Normalize(string? value)
    {
        if (value is null)
        {
            return "";
        }
        var collapsed = Whitespace().Replace(value, " ").Trim();
        return collapsed.Length <= MaxLength ? collapsed : collapsed[..MaxLength];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
