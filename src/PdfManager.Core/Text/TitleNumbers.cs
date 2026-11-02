namespace PdfManager.Core.Text;

// タイトルの末尾の番号(決定ログ DF-2、DG-1〜DG-4、DI-6)。
// 「シリーズ」は末尾の数字の前が同じタイトルのまとまり。比べるときは、ひらがな・カタカナ、全角・半角、大文字・小文字の違いと、
// 末尾の区切り(空白・「_」・「-」)を無視する(「とんこつラーメン１」と「とんこつらーめん１０」は同じシリーズ)
public static class TitleNumbers
{
    // 末尾の数字(半角・全角)とその前の部分に分ける。末尾が数字でなければ null
    public static (string Stem, string Digits)? SplitTrailingNumber(string title)
    {
        var end = title.Length;
        var start = end;
        while (start > 0 && IsDigit(title[start - 1]))
        {
            start--;
        }
        return start == end ? null : (title[..start], title[start..]);
    }

    // シリーズを比べる形
    public static string SeriesKey(string title) =>
        NameNormalizer.Fold(SplitTrailingNumber(title)?.Stem ?? title).TrimEnd(' ', '_', '-');

    // 決定ログ DF-2: 登録済みのタイトルを候補から選んだとき、末尾の番号を「シリーズの中で一番大きい番号 + 1」にする。末尾に数字がなければそのまま
    public static string NextNumbered(string chosen, IEnumerable<string> existingTitles) =>
        SplitTrailingNumber(chosen) is null ? chosen : Next(chosen, existingTitles);

    // 決定ログ DG-1・DG-3: 同じタイトルが登録済みのときに勧める次のタイトル。
    // シリーズに番号付きのものがあれば一番大きい番号 + 1、なければ「２」(数字のないタイトルの 2 件目)
    public static string Next(string title, IEnumerable<string> existingTitles)
    {
        var split = SplitTrailingNumber(title);
        var stem = split?.Stem ?? title;
        var (max, style) = MaxNumber(SeriesKey(title), existingTitles.Append(title));
        return stem + Format(max == 0 ? 2 : max + 1, style, title);
    }

    // シリーズの一番大きい番号と、そのタイトルの数字(書き方をそろえるため)。番号付きがなければ (0, null)
    public static (long Max, string? Digits) MaxNumber(string seriesKey, IEnumerable<string> titles)
    {
        long max = 0;
        string? style = null;
        foreach (var title in titles)
        {
            if (SplitTrailingNumber(title) is var (_, digits) && SeriesKey(title) == seriesKey)
            {
                var value = Value(digits);
                if (style is null || value > max)
                {
                    max = value;
                    style = digits;
                }
            }
        }
        return (max, style);
    }

    // 決定ログ DG-4: 数字の書き方は styleDigits(シリーズの一番大きい番号のもの)に合わせる。全角は全角、「01」のような桁数もそろえる。
    // シリーズに番号付きがなければ、タイトルに半角以外の文字があれば全角、なければ半角
    public static string Format(long number, string? styleDigits, string title)
    {
        // 桁数は「01」のように 0 から始まる書き方のときだけそろえる(「１０」の次の 1 番は「１」)
        var width = styleDigits is { Length: > 1 } && styleDigits[0] is '0' or '０' ? styleDigits.Length : 1;
        var text = number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0');
        var fullWidth = styleDigits is { Length: > 0 } ? styleDigits[0] >= '０' : title.Any(c => c > '\u007f');
        return fullWidth ? new string(text.Select(c => (char)(c - '0' + '０')).ToArray()) : text;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9' or >= '０' and <= '９';

    // 大きすぎる番号は比べるのに使わない(桁あふれを防ぐ)
    public static long Value(string digits)
    {
        long value = 0;
        foreach (var c in digits)
        {
            var d = c >= '０' ? c - '０' : c - '0';
            if (value > (long.MaxValue - d) / 10)
            {
                return 0;
            }
            value = value * 10 + d;
        }
        return value;
    }
}
