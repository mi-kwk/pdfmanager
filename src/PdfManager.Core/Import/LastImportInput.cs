using System.Text.Encodings.Web;
using System.Text.Json;
using PdfManager.Core.Storage;

namespace PdfManager.Core.Import;

// ファイル名の先頭に使うもの。タグは正規化した名前、項目は ID で覚える(タグは登録前の新しいタグもあるため)
public sealed record FileNamePrefixChoice(string Kind, string Key)
{
    public const string TagKind = "tag";
    public const string FieldKind = "field";
}

// 「前回と同じ」で入れる内容(決定ログ F-10、F-11)。ライブラリごとに data\import-last.json に保存する
public sealed record LastImportInput(
    IReadOnlyDictionary<long, string> FieldInputs,
    IReadOnlyList<string> TagNames,
    FileNamePrefixChoice? Prefix)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string GetPath(DataFolder dataFolder) => Path.Combine(dataFolder.Root, "import-last.json");

    public static LastImportInput? Load(DataFolder dataFolder)
    {
        try
        {
            var path = GetPath(dataFolder);
            var loaded = File.Exists(path) ? JsonSerializer.Deserialize<LastImportInput>(File.ReadAllText(path), JsonOptions) : null;
            return loaded is null ? null : loaded with { FieldInputs = loaded.FieldInputs ?? new Dictionary<long, string>(), TagNames = loaded.TagNames ?? [] };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(DataFolder dataFolder) => File.WriteAllText(GetPath(dataFolder), JsonSerializer.Serialize(this, JsonOptions));
}
