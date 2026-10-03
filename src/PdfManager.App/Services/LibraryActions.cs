using System.IO;
using System.Windows;
using Microsoft.Win32;
using PdfManager.Core.Library;
using PdfManager.Core.Settings;

namespace PdfManager.App.Services;

public sealed record RecentLibraryItem(string Name, string LibraryFilePath, bool Exists, bool IsCurrent)
{
    public string Folder => Path.GetDirectoryName(LibraryFilePath) ?? LibraryFilePath;

    public string StatusText => !Exists ? "見つかりません" : IsCurrent ? "使用中" : "";
}

// ライブラリを開く・作るときの共通処理(SC-08 設定 / SC-09 ようこそ)
public sealed class LibraryActions(AppSettingsStore store)
{
    public AppSettings Settings { get; private set; } = store.Load();

    public IReadOnlyList<RecentLibraryItem> GetRecent(LibraryInfo? current) => Settings.RecentLibraries
        .Select(r => new RecentLibraryItem(
            r.Name,
            r.LibraryFilePath,
            File.Exists(r.LibraryFilePath),
            current is not null && string.Equals(r.LibraryFilePath, current.LibraryFilePath, StringComparison.OrdinalIgnoreCase)))
        .ToList();

    public async Task<LibraryInfo?> OpenAsync(string libraryFilePath, IUserDialogs dialogs)
    {
        LibraryInfo library;
        try
        {
            library = LibraryService.Open(libraryFilePath);
        }
        catch (LibraryException ex)
        {
            await dialogs.ShowErrorAsync(ex.Message, "ライブラリを開けません");
            return null;
        }

        if (!await ConfirmRemovableDriveAsync(library.RootFolder, dialogs))
        {
            return null;
        }
        Remember(library);
        return library;
    }

    public async Task<LibraryInfo?> BrowseAndOpenAsync(IUserDialogs dialogs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "開くライブラリ(.pdflib)を選択",
            Filter = $"PdfManager ライブラリ (*{LibraryService.Extension})|*{LibraryService.Extension}",
        };
        return dialog.ShowDialog() == true ? await OpenAsync(dialog.FileName, dialogs) : null;
    }

    public LibraryInfo? Create(Window owner)
    {
        var window = new CreateLibraryWindow { Owner = owner };
        window.ShowDialog();
        if (window.CreatedLibrary is { } library)
        {
            Remember(library);
        }
        return window.CreatedLibrary;
    }

    public ViewPreferences View => Settings.View ?? ViewPreferences.Default;

    public void SaveView(ViewPreferences view)
    {
        Settings = Settings with { View = view };
        store.Save(Settings);
    }

    public void Forget(string libraryFilePath)
    {
        Settings = AppSettingsStore.Forget(Settings, libraryFilePath);
        store.Save(Settings);
    }

    // 決定ログ E-7: 開いている間に取り外すとデータベースが壊れるおそれがあるため確認する
    public static async Task<bool> ConfirmRemovableDriveAsync(string folder, IUserDialogs dialogs) =>
        !LibraryService.IsOnRemovableOrNetworkDrive(folder)
        || await dialogs.ConfirmAsync(
            "取り外せるドライブです",
            $"このライブラリは USB メモリなどの取り外せるドライブにあります。\n{folder}\n\nアプリで開いている間は、ドライブを取り外さないでください(データが壊れるおそれがあります)。動作も遅くなることがあります。\n\n開きますか?",
            "開く");

    private void Remember(LibraryInfo library)
    {
        Settings = AppSettingsStore.RememberOpened(Settings, library.Name, library.LibraryFilePath);
        store.Save(Settings);
    }
}
