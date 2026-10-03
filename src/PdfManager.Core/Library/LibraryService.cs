using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfManager.Core.Database;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.Core.Library;

public sealed record LibraryInfo(string LibraryFilePath, Guid Id, string Name)
{
    public string RootFolder => Path.GetDirectoryName(LibraryFilePath)!;

    public DataFolder DataFolder => new(Path.Combine(RootFolder, LibraryService.DataFolderName));
}

// 利用者に見せる失敗。メッセージはそのまま画面に表示する
public sealed class LibraryException(string message) : Exception(message);

// docs/03「フォルダ構成(ライブラリ)」
public static class LibraryService
{
    public const string LibraryFolderName = "PdfManager";
    public const string DataFolderName = "data";
    public const string Extension = ".pdflib";
    private const string Format = "PdfManager.Library";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // ライブラリ名の日本語をそのまま読めるようにする(ローカルのファイルなので HTML 向けのエスケープは不要)
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string GetLibraryFolder(string parentFolder) => Path.Combine(Path.GetFullPath(parentFolder), LibraryFolderName);

    public static LibraryInfo Create(string parentFolder, string name)
    {
        var cleanedName = NameNormalizer.Clean(name);
        if (cleanedName.Length == 0)
        {
            throw new LibraryException("ライブラリ名を入力してください。");
        }
        if (!Directory.Exists(parentFolder))
        {
            throw new LibraryException($"フォルダが見つかりません: {parentFolder}");
        }

        var root = GetLibraryFolder(parentFolder);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new LibraryException(
                Directory.EnumerateFiles(root, "*" + Extension).Any()
                    ? $"このフォルダにはすでにライブラリがあります。「ライブラリを開く」を使ってください。\n{root}"
                    : $"「{LibraryFolderName}」フォルダがすでにあり、中にファイルがあります。別のフォルダを選んでください。\n{root}");
        }

        var info = new LibraryInfo(Path.Combine(root, FileNames.Sanitize(cleanedName, "library") + Extension), Guid.NewGuid(), cleanedName);
        info.DataFolder.EnsureCreated();
        using (var connection = PdfManagerDatabase.Open(info.DataFolder.DatabasePath))
        {
            PdfManagerDatabase.Initialize(connection);
        }

        var file = new LibraryFile(Format, FormatVersion, info.Id, info.Name, DateTimeOffset.UtcNow);
        File.WriteAllText(info.LibraryFilePath, JsonSerializer.Serialize(file, JsonOptions));
        return info;
    }

    public static LibraryInfo Open(string libraryFilePath)
    {
        var path = Path.GetFullPath(libraryFilePath);
        if (!File.Exists(path))
        {
            throw new LibraryException($"ライブラリファイルが見つかりません。移動・削除されたか、USB メモリなどが外れている可能性があります。\n{path}");
        }

        LibraryFile? file;
        try
        {
            file = JsonSerializer.Deserialize<LibraryFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            file = null;
        }
        if (file is null || file.Format != Format)
        {
            throw new LibraryException($"PdfManager のライブラリファイルではありません。\n{path}");
        }
        if (file.FormatVersion > FormatVersion)
        {
            throw new LibraryException("新しいバージョンの PdfManager で作られたライブラリです。アプリを新しいバージョンにしてから開いてください。");
        }

        var info = new LibraryInfo(path, file.Id, file.Name);
        if (!File.Exists(info.DataFolder.DatabasePath))
        {
            throw new LibraryException($"ライブラリのデータが見つかりません。「{LibraryFolderName}」フォルダごとコピーされているか確認してください。\n{info.DataFolder.DatabasePath}");
        }

        info.DataFolder.EnsureCreated();
        using var connection = PdfManagerDatabase.Open(info.DataFolder.DatabasePath);
        if (PdfManagerDatabase.GetUserVersion(connection) > PdfManagerDatabase.SchemaVersion)
        {
            throw new LibraryException("新しいバージョンの PdfManager で作られたライブラリです。アプリを新しいバージョンにしてから開いてください。");
        }
        PdfManagerDatabase.Initialize(connection);
        return info;
    }

    // 決定ログ E-7: 開いている間に取り外されるとデータベースが壊れるおそれがあるため、開く前に警告する
    public static bool IsOnRemovableOrNetworkDrive(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }
        var drive = new DriveInfo(Path.GetPathRoot(fullPath)!);
        return drive.DriveType is DriveType.Removable or DriveType.Network;
    }

    private sealed record LibraryFile(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("formatVersion")] int FormatVersion,
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);
}
