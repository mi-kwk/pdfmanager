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

    // 決定ログ R-5: 取り込み画面の「すべて登録」ボタン
    public bool ShowRegisterAll => Settings.ShowRegisterAll;

    public void SetShowRegisterAll(bool value)
    {
        Settings = Settings with { ShowRegisterAll = value };
        store.Save(Settings);
    }

    // 決定ログ AA-1: タグの欄を畳む順位(0 は畳まない)
    public int TagCollapseRank => Math.Clamp(Settings.TagCollapseRank, 0, AppSettings.MaxTagCollapseRank);

    public void SetTagCollapseRank(int value)
    {
        Settings = Settings with { TagCollapseRank = Math.Clamp(value, 0, AppSettings.MaxTagCollapseRank) };
        store.Save(Settings);
    }

    // 決定ログ BB-2・BB-4・BB-5: バックアップの設定
    public BackupPreferences Backup => Settings.Backup ?? BackupPreferences.Default;

    public void SetBackup(BackupPreferences value)
    {
        Settings = Settings with { Backup = value };
        store.Save(Settings);
    }

    // 決定ログ DF-3: タイトルの候補に PDF の文字から作ったものも出すか
    public bool UseTextTitleCandidates => Settings.UseTextTitleCandidates;

    public void SetUseTextTitleCandidates(bool value)
    {
        Settings = Settings with { UseTextTitleCandidates = value };
        store.Save(Settings);
    }

    // 決定ログ DH-2: 1 ページ目の重複チェックで「似ている」ものも出すか
    public bool DuplicateLooseMatch => Settings.DuplicateLooseMatch;

    public void SetDuplicateLooseMatch(bool value)
    {
        Settings = Settings with { DuplicateLooseMatch = value };
        store.Save(Settings);
    }

    // 決定ログ DA-5: 検索・閲覧の記録を残すか
    public bool RecordUsage => Settings.RecordUsage;

    public void SetRecordUsage(bool value)
    {
        Settings = Settings with { RecordUsage = value };
        store.Save(Settings);
    }

    // USB への出力で前回選んだ出力先(次に開いたときの初めの値)
    public string? ExportFolder => Settings.ExportFolder;

    public void SetExportFolder(string folder)
    {
        Settings = Settings with { ExportFolder = folder };
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
        Settings = AppSettingsStore.RememberOpened(Settings, library.Name, library.LibraryFilePath, library.Id);
        store.Save(Settings);
    }
}
