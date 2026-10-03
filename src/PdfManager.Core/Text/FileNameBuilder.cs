namespace PdfManager.Core.Text;

public sealed record BuiltFileName(string FileName, bool ReplacedInvalidCharacters);

// docs/04「ファイル名の決め方」
public static class FileNameBuilder
{
    public const string UntitledTitle = "未入力";
    private const int MaxBaseLength = 150;

    public static BuiltFileName Build(string title, bool useTitle, string? prefix, string suffix, string originalFileName)
    {
        var raw = useTitle
            ? (NameNormalizer.Clean(title).Length == 0 ? UntitledTitle : title)
            : (prefix ?? "") + suffix;
        var original = Path.GetFileNameWithoutExtension(originalFileName);
        if (NameNormalizer.Clean(raw).Length == 0)
        {
            raw = original;
        }

        var sanitized = FileNames.Sanitize(raw, original.Length > 0 ? original : UntitledTitle);
        if (sanitized.Length > MaxBaseLength)
        {
            sanitized = sanitized[..MaxBaseLength].TrimEnd('.', ' ');
        }
        return new BuiltFileName(sanitized + ".pdf", FileNames.ContainsInvalidCharacters(raw));
    }
}
