using System.Text.Encodings.Web;
using System.Text.Json;

namespace PdfManager.Core.Settings;

public sealed record RecentLibrary(string Name, string LibraryFilePath);

// 一覧の並び替えと表示の種類(決定ログ L-5、L-9)。項目での並び替えはライブラリごとの項目 ID なので、見つからなければ登録日に戻す
public sealed record ViewPreferences(string Sort = "Registered", long? SortFieldId = null, bool Descending = true, bool ListView = false)
{
    public static ViewPreferences Default { get; } = new();
}

public sealed record AppSettings(string? LastLibraryFilePath, IReadOnlyList<RecentLibrary> RecentLibraries, ViewPreferences? View = null)
{
    public static AppSettings Empty { get; } = new(null, []);
}

// 決定ログ E-3: PC ごとの設定(%LOCALAPPDATA%\PdfManager\settings.json)
public sealed class AppSettingsStore(string directory)
{
    public const int MaxRecentLibraries = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfManager");

    public string FilePath => Path.Combine(directory, "settings.json");

    // 設定ファイルが壊れていても起動できるように、読めなければ初期状態として扱う
    public AppSettings Load()
    {
        try
        {
            var settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions)
                : null;
            // JSON に recentLibraries が無い・null の場合も空の一覧として扱う
            return settings is null ? AppSettings.Empty : settings with { RecentLibraries = settings.RecentLibraries ?? [] };
        }
        catch (JsonException)
        {
            return AppSettings.Empty;
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public static AppSettings RememberOpened(AppSettings settings, string name, string libraryFilePath)
    {
        var recent = settings.RecentLibraries
            .Where(r => !SamePath(r.LibraryFilePath, libraryFilePath))
            .Prepend(new RecentLibrary(name, libraryFilePath))
            .Take(MaxRecentLibraries)
            .ToList();
        return settings with { LastLibraryFilePath = libraryFilePath, RecentLibraries = recent };
    }

    public static AppSettings Forget(AppSettings settings, string libraryFilePath) => settings with
    {
        LastLibraryFilePath = SamePath(settings.LastLibraryFilePath, libraryFilePath) ? null : settings.LastLibraryFilePath,
        RecentLibraries = settings.RecentLibraries.Where(r => !SamePath(r.LibraryFilePath, libraryFilePath)).ToList(),
    };

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
