using System.Text.RegularExpressions;

namespace PdfManager.Core.Pdf;

// docs/04「タイトル候補」: 最も大きい文字の行 → 先頭の行 → PDF のプロパティ → ファイル名 の順で最大 5 件
public static partial class TitleCandidates
{
    public const int MaxLength = 100;
    private const int LeadingLineCount = 2;

    public static IReadOnlyList<string> Build(IReadOnlyList<TextLine> firstPageLines, string? metadataTitle, string fileName)
    {
        var candidates = new List<string>();

        void Add(string? value)
        {
            var normalized = Normalize(value);
            if (normalized.Length > 0 && !candidates.Contains(normalized))
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
        Add(Path.GetFileNameWithoutExtension(fileName));

        return candidates;
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
