using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfManager.Core.Database;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.Core.Library;

// 決定ログ S-11: ライブラリの種類。メインとサブは同じ形で、種類だけが違う
public enum LibraryKind
{
    Main,
    Sub,
}

public sealed record LibraryInfo(string LibraryFilePath, Guid Id, string Name, LibraryKind Kind = LibraryKind.Main, Guid? MainId = null)
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
    // サブは 2(古いアプリでメインとして開かれないよう、古いアプリには「新しいバージョン」に見せる)
    private const int SubFormatVersion = 2;
    private const string SubKind = "sub";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // ライブラリ名の日本語をそのまま読めるようにする(ローカルのファイルなので HTML 向けのエスケープは不要)
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string GetLibraryFolder(string parentFolder) => Path.Combine(Path.GetFullPath(parentFolder), LibraryFolderName);

    public static LibraryInfo Create(string parentFolder, string name) => Create(parentFolder, name, null);

    // 決定ログ S-1・S-11: サブライブラリを作る。メインと同じ形で、種類と、どのメインのサブかを記録する
    public static LibraryInfo CreateSub(string parentFolder, string name, LibraryInfo main)
    {
        if (Directory.Exists(parentFolder) && IsOnRemovableOrNetworkDrive(parentFolder))
        {
            // 決定ログ S-9
            throw new LibraryException("USB メモリなどの取り外せるドライブや、ネットワーク上のフォルダには、サブライブラリを作れません。内蔵のドライブか外付け HDD を選んでください。");
        }
        var root = GetLibraryFolder(parentFolder);
        if (IsSameOrUnder(root, main.RootFolder) || IsSameOrUnder(main.RootFolder, root))
        {
            throw new LibraryException($"メインライブラリのフォルダと重なる場所には作れません。別のフォルダを選んでください。\n{main.RootFolder}");
        }
        return Create(parentFolder, name, main);
    }

    private static LibraryInfo Create(string parentFolder, string name, LibraryInfo? mainOfSub)
    {
        var cleanedName = NameNormalizer.Clean(name);
        if (cleanedName.Length == 0)
        {
            throw new LibraryException(mainOfSub is null ? "ライブラリ名を入力してください。" : "サブライブラリの名前を入力してください。");
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
                    ? $"このフォルダにはすでにライブラリがあります。{(mainOfSub is null ? "「ライブラリを開く」を使ってください。" : "別のフォルダを選んでください。")}\n{root}"
                    : $"「{LibraryFolderName}」フォルダがすでにあり、中にファイルがあります。別のフォルダを選んでください。\n{root}");
        }

        var info = new LibraryInfo(Path.Combine(root, FileNames.Sanitize(cleanedName, "library") + Extension), Guid.NewGuid(), cleanedName,
            mainOfSub is null ? LibraryKind.Main : LibraryKind.Sub, mainOfSub?.Id);
        info.DataFolder.EnsureCreated();
        using (var connection = PdfManagerDatabase.Open(info.DataFolder.DatabasePath))
        {
            PdfManagerDatabase.Initialize(connection, useWal: mainOfSub is null);
        }
        WriteLibraryFile(info, DateTimeOffset.UtcNow);
        return info;
    }

    // サブの名前を変えたとき(決定ログ S-1)。作成日時は元のまま
    public static void WriteLibraryFile(LibraryInfo info, DateTimeOffset createdAt)
    {
        var file = info.Kind == LibraryKind.Sub
            ? new LibraryFile(Format, SubFormatVersion, info.Id, info.Name, createdAt, SubKind, info.MainId)
            : new LibraryFile(Format, FormatVersion, info.Id, info.Name, createdAt);
        File.WriteAllText(info.LibraryFilePath, JsonSerializer.Serialize(file, JsonOptions));
    }

    public static void RenameLibraryFile(LibraryInfo info, string newName)
    {
        var createdAt = ReadFile(info.LibraryFilePath).CreatedAt;
        WriteLibraryFile(info with { Name = newName }, createdAt);
    }

    // メインライブラリを開く。サブは開けない(決定ログ S-11)
    public static LibraryInfo Open(string libraryFilePath)
    {
        var path = Path.GetFullPath(libraryFilePath);
        if (!File.Exists(path))
        {
            throw new LibraryException($"ライブラリファイルが見つかりません。移動・削除されたか、USB メモリなどが外れている可能性があります。\n{path}");
        }
        var (info, _) = ReadFile(path);
        if (info.Kind == LibraryKind.Sub)
        {
            throw new LibraryException($"これはサブライブラリです。サブライブラリは単独では開けません。登録しているメインライブラリを開いてください。\n{path}");
        }
        OpenDatabase(info, useWal: true);
        return info;
    }

    // サブライブラリのデータベースを、今のアプリの形にそろえる
    public static void PrepareSubDatabase(LibraryInfo sub) => OpenDatabase(sub, useWal: false);

    // ライブラリファイルだけを読む(データベースは確かめない)
    public static (LibraryInfo Info, DateTimeOffset CreatedAt) ReadFile(string libraryFilePath)
    {
        var path = Path.GetFullPath(libraryFilePath);
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
        var isSub = file.Kind == SubKind;
        if (file.FormatVersion > (isSub ? SubFormatVersion : FormatVersion))
        {
            throw new LibraryException("新しいバージョンの PdfManager で作られたライブラリです。アプリを新しいバージョンにしてから開いてください。");
        }
        return (new LibraryInfo(path, file.Id, file.Name, isSub ? LibraryKind.Sub : LibraryKind.Main, file.MainId), file.CreatedAt);
    }

    private static void OpenDatabase(LibraryInfo info, bool useWal)
    {
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
        PdfManagerDatabase.Initialize(connection, useWal);
    }

    public static bool IsSameOrUnder(string path, string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(full, parent, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
        [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
        [property: JsonPropertyName("mainId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? MainId = null);
}
