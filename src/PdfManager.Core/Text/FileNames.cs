namespace PdfManager.Core.Text;

// docs/04「ファイル名の決め方」: Windows のファイル名に使えない文字は _ に置き換える
public static class FileNames
{
    private static readonly HashSet<char> InvalidChars = [.. Path.GetInvalidFileNameChars()];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static bool ContainsInvalidCharacters(string name) => name.Any(InvalidChars.Contains);

    public static string Sanitize(string name, string fallback)
    {
        var replaced = new string(name.Select(c => InvalidChars.Contains(c) ? '_' : c).ToArray());
        // 末尾の . と空白は Windows が黙って削除するため、あらかじめ取り除く
        var trimmed = NameNormalizer.Clean(replaced).TrimEnd('.', ' ');
        if (trimmed.Length == 0)
        {
            return fallback;
        }
        return ReservedNames.Contains(trimmed) ? "_" + trimmed : trimmed;
    }
}
