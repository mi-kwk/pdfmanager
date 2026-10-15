namespace PdfManager.Core.Text;

// 決定ログ R-10・T-1〜T-3: 同じファイル名がすでにあれば、タイトルとファイル名の末尾に同じ「_連番」を付ける
public static class UniqueNames
{
    // 比べるときの形(全角・半角、大文字・小文字の違いは同じとみなす、T-1)
    public static string Key(string fileName) => NameNormalizer.Normalize(fileName);

    // taken(Key の形)と重なれば、空いている一番小さい番号(_2 から)を付ける。決めた名前は taken に加える
    public static (string Title, string FileName, int Number) Assign(string title, string fileName, ISet<string> taken)
    {
        if (taken.Add(Key(fileName)))
        {
            return (title, fileName, 0);
        }
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var number = 2; ; number++)
        {
            var candidate = $"{stem}_{number}{extension}";
            if (taken.Add(Key(candidate)))
            {
                return ($"{title}_{number}", candidate, number);
            }
        }
    }
}
