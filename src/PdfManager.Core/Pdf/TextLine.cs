namespace PdfManager.Core.Pdf;

// Size は同じページ内での大小比較だけに使う(埋め込み文字はポイント数、OCR は行の高さのピクセル数)
public sealed record TextLine(string Text, double Size)
{
    // 日本語の単語の間には空白を入れず、英数字どうしの間だけ空白を入れる
    public static string JoinWords(IEnumerable<string> words)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            if (word.Length == 0)
            {
                continue;
            }
            if (builder.Length > 0 && char.IsAsciiLetterOrDigit(builder[^1]) && char.IsAsciiLetterOrDigit(word[0]))
            {
                builder.Append(' ');
            }
            builder.Append(word);
        }
        return builder.ToString();
    }
}
