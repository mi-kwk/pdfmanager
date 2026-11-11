using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PdfManager.App.Services;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Library;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// 設定画面のサブライブラリの 1 行(決定ログ S-1)
public sealed record SubLibraryRow(SubLibraryState State, string Usage)
{
    public string Id => State.Id;

    public string Name => State.Name;

    public string StatusText => State.StatusText;

    public bool IsConnected => State.IsWritable;

    public string Folder => State.Folder;

    public string Note => State.Status switch
    {
        SubLibraryStatus.Duplicate => "次の場所で見つかりました。「場所を指定し直す」で使う方を選んでください。\n" + string.Join("\n", State.Candidates),
        SubLibraryStatus.ChangedElsewhere when State.PendingPull is { } plan =>
            $"ほかの PC で変更されています({plan.Summary})。「読み込む」で読み込むと、この PC でまだ書き込めていない次の変更が消えます:"
            + string.Join("、", plan.LostTitles.Take(5)) + (plan.LostChanges > 5 ? $" ほか {plan.LostChanges - 5} 件" : ""),
        SubLibraryStatus.ChangedElsewhere or SubLibraryStatus.Unavailable => State.Problem ?? "",
        _ => "",
    };

    public bool HasNote => Note.Length > 0;

    // 決定ログ EB-5: このサブを使った PC(つながっているときだけわかる)
    public string UsersText => State.Users.Count == 0 ? ""
        : "使っている PC:" + string.Join("、", State.Users.Select(u => $"{u.Pc}({u.LastUsed.ToLocalTime():yyyy/MM/dd})"));

    public bool HasUsers => UsersText.Length > 0;
}

// SC-08 設定
public partial class SettingsWindow : FluentWindow
{
    // exe を作った日時から付けたバージョン(年.月.日.時分。PdfManager.App.csproj の Version)
    public static string AppVersion => AppUpdater.CurrentVersionText;

    private readonly LibraryInfo _current;
    private readonly LibraryActions _libraries;
    private readonly StorageAccess _storage;
    private readonly IUserDialogs _dialogs;

    public SettingsWindow(LibraryInfo current, LibraryActions libraries, StorageAccess storage)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        _current = current;
        _libraries = libraries;
        _storage = storage;
        _dialogs = new WpfUiDialogs(this);
        SystemThemeWatcher.Watch(this);

        AppVersionText.Text = $"PdfManager バージョン {AppVersion}";
        AppPathText.Text = AppUpdater.StandaloneExePath is { } exe ? $"置き場所:{exe}" : "開発用の実行";
        CurrentName.Text = current.Name;
        CurrentFolder.Text = current.RootFolder;
        ShowRegisterAllBox.IsChecked = libraries.ShowRegisterAll;
        TagCollapseRankBox.SelectedIndex = libraries.TagCollapseRank;
        UseTextTitleCandidatesBox.IsChecked = libraries.UseTextTitleCandidates;
        DuplicateLooseMatchBox.IsChecked = libraries.DuplicateLooseMatch;
        RecordUsageBox.IsChecked = libraries.RecordUsage;
        ArchiveAlertBox.SelectedItem = ArchiveAlertBox.Items.Cast<System.Windows.Controls.ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == libraries.ArchiveAlertPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? ArchiveAlertBox.Items[2];
        LoadBackup();
        RefreshRecent();
        Loaded += async (_, _) =>
        {
            await RefreshSubsAsync();
            CurrentStats.Text = await Task.Run(DescribeCurrent);
        };
    }

    // 切り替え先。null なら切り替えない
    public LibraryInfo? SelectedLibrary { get; private set; }

    private string DescribeCurrent()
    {
        long count;
        long mainCount;
        using (var connection = PdfManagerDatabase.Open(_current.DataFolder.DatabasePath))
        {
            count = new DocumentRepository(connection).Count();
            mainCount = new SubLibraryRepository(connection).GetUsage().GetValueOrDefault("")?.Count ?? 0;
        }
        var bytes = Directory.EnumerateFiles(_current.DataFolder.Root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var subs = _storage.Storage.Subs.Count == 0 ? "" : $"(メインに {mainCount:N0} 件)";
        return $"PDF {count:N0} 件{subs} / 使用容量 {FormatSize(bytes)} / 空き容量 {FreeSpace(_current.RootFolder)}";
    }

    private static string FreeSpace(string folder)
    {
        try
        {
            return FormatSize(new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return "不明";
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private void OnCreateClick(object sender, RoutedEventArgs e) => Finish(_libraries.Create(this));

    private async void OnOpenClick(object sender, RoutedEventArgs e) => Finish(await _libraries.BrowseAndOpenAsync(_dialogs));

    private async void OnOpenRecentClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentLibraryItem { IsCurrent: false } item)
        {
            Finish(await _libraries.OpenAsync(item.LibraryFilePath, _dialogs));
        }
    }

    private void OnForgetClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentLibraryItem { IsCurrent: false } item)
        {
            _libraries.Forget(item.LibraryFilePath);
            RefreshRecent();
        }
    }

    private void OnExplorerClick(object sender, RoutedEventArgs e) => OpenInExplorer(_current.RootFolder);

    private static void OpenInExplorer(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });

    private void Finish(LibraryInfo? library)
    {
        if (library is null)
        {
            RefreshRecent();
            return;
        }
        if (!string.Equals(library.LibraryFilePath, _current.LibraryFilePath, StringComparison.OrdinalIgnoreCase))
        {
            SelectedLibrary = library;
        }
        Close();
    }

    private void RefreshRecent() => RecentList.ItemsSource = _libraries.GetRecent(_current);

    // 決定ログ R-5: 切り替えたらすぐ保存する(次に開く取り込み画面から使われる)
    private void OnShowRegisterAllChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            _libraries.SetShowRegisterAll(ShowRegisterAllBox.IsChecked == true);
        }
    }

    // 決定ログ EA-6: 切り替えたらすぐ保存する(次の起動から使われる)
    private void OnArchiveAlertChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded && ArchiveAlertBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag })
        {
            _libraries.SetArchiveAlertPercent(int.Parse(tag, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    // 決定ログ HA-1: exe を差し替えたら設定画面を閉じ、メイン画面が閉じる(終わるときに新しいバージョンで起動し直す)
    public bool Updated { get; private set; }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (await AppUpdater.RunAsync(this, _libraries, _storage))
        {
            Updated = true;
            Close();
        }
    }

    // 決定ログ EA-7: 設定画面を閉じてから、メイン画面が退避の画面を開く
    public bool ArchiveRequested { get; private set; }

    private void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        ArchiveRequested = true;
        Close();
    }

    // 決定ログ DA-5: 切り替えたらすぐ保存する(メイン画面に戻ると使われる)
    private void OnRecordUsageChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            _libraries.SetRecordUsage(RecordUsageBox.IsChecked == true);
        }
    }

    // 決定ログ DA-5: 開いているライブラリの検索・閲覧の記録をすべて消す
    private async void OnClearUsageClick(object sender, RoutedEventArgs e)
    {
        if (!await _dialogs.ConfirmAsync("記録を消す",
                $"「{_current.Name}」の検索・閲覧の記録をすべて消しますか?\n\n分析画面の数字が 0 から数え直しになります。PDF や、一覧の閲覧数・最終閲覧日は消えません。", "消す"))
        {
            return;
        }
        using (var connection = PdfManagerDatabase.Open(_current.DataFolder.DatabasePath))
        {
            PdfManager.Core.Analytics.UsageAnalytics.Clear(connection);
        }
        await _dialogs.ShowErrorAsync("検索・閲覧の記録を消しました。", "記録を消す");
    }

    // 決定ログ DH-2: 切り替えたらすぐ保存する
    private void OnDuplicateLooseMatchChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            _libraries.SetDuplicateLooseMatch(DuplicateLooseMatchBox.IsChecked == true);
        }
    }

    // 決定ログ DF-3: 切り替えたらすぐ保存する(次に開く取り込み画面から使われる)
    private void OnUseTextTitleCandidatesChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            _libraries.SetUseTextTitleCandidates(UseTextTitleCandidatesBox.IsChecked == true);
        }
    }

    // ==== バックアップ(決定ログ BB-1〜BB-7) ====

    private void LoadBackup()
    {
        var backup = _libraries.Backup;
        BackupFolderText.Text = backup.Folder is null ? "保存先:まだ選んでいません(外付け HDD などを選んでください)" : $"保存先:{backup.Folder}";
        BackupLastText.Text = BackupActions.LastText(_libraries, _current);
        KeepOldBox.Value = backup.KeepOldGenerations;
        ReminderBox.SelectedItem = ReminderBox.Items.Cast<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == backup.ReminderDays.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?? ReminderBox.Items[2];
    }

    private async void OnBackupClick(object sender, RoutedEventArgs e)
    {
        await BackupActions.RunAsync(this, _libraries, _storage);
        LoadBackup();
    }

    private async void OnChooseBackupFolderClick(object sender, RoutedEventArgs e)
    {
        if (await BackupActions.ChooseFolderAsync(this, _libraries.Backup.Folder) is not { } folder)
        {
            return;
        }
        if (LibraryService.IsSameOrUnder(folder, _current.RootFolder))
        {
            await _dialogs.ShowErrorAsync("ライブラリのフォルダの中には保存できません。外付け HDD など、別の場所を選んでください。", "バックアップ");
            return;
        }
        _libraries.SetBackup(_libraries.Backup with { Folder = folder });
        LoadBackup();
    }

    private void OnKeepOldChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && KeepOldBox.Value is { } value)
        {
            _libraries.SetBackup(_libraries.Backup with { KeepOldGenerations = (int)Math.Clamp(value, 0, Core.Backup.BackupService.MaxKeepOldGenerations) });
        }
    }

    private void OnReminderChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded && ReminderBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag })
        {
            _libraries.SetBackup(_libraries.Backup with { ReminderDays = int.Parse(tag, System.Globalization.CultureInfo.InvariantCulture) });
        }
    }

    // 決定ログ BB-7: バックアップのあるフォルダを選び、どの回をどこへ戻すかを選ぶ。戻したら、開くかを聞く
    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = $"バックアップのあるフォルダを選択(バックアップの保存先。{BackupActions.DriveRootHint})" };
        if (_libraries.Backup.Folder is { } folder && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var backups = Core.Backup.BackupService.FindBackups(dialog.FolderName);
        if (backups.Count == 0)
        {
            await _dialogs.ShowErrorAsync($"選んだフォルダにバックアップが見つかりません。バックアップの保存先(「{Core.Backup.BackupService.RootFolderName}」フォルダがあるところ)を選んでください。\n{dialog.FolderName}", "バックアップから戻す");
            return;
        }
        var restore = new RestoreWindow(backups) { Owner = this };
        if (restore.ShowDialog() != true)
        {
            return;
        }
        var progress = new ProgressWindow("バックアップから戻しています") { Owner = this };
        var task = Task.Run(() => Core.Backup.BackupService.Restore(restore.SelectedBackup!, restore.SelectedGeneration!.Name, restore.Destination!,
            progress.Progress, progress.Cancellation));
        progress.ShowUntil(task);
        Core.Backup.RestoreResult result;
        try
        {
            result = await task;
        }
        catch (OperationCanceledException)
        {
            await _dialogs.ShowErrorAsync("途中で止めました。作りかけのものは消しました。", "バックアップから戻す");
            return;
        }
        catch (Exception ex) when (ex is LibraryException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            await _dialogs.ShowErrorAsync($"戻せませんでした。今のライブラリとバックアップはそのままです。\n\n{ex.Message}", "バックアップから戻す");
            return;
        }
        var summary = $"{restore.Destination} に戻しました。\nPDF:{result.RestoredPdfs:N0} 件" + (result.SubCount > 0 ? $"(サブライブラリ {result.SubCount} 個を含む)" : "")
            + (result.MissingPdfs > 0 ? $"\nバックアップに PDF がなかった文書:{result.MissingPdfs:N0} 件(情報は戻しましたが、開けません)" : "");
        if (await _dialogs.ConfirmAsync("バックアップから戻しました",
                summary + "\n\n戻したライブラリを開きますか?\n(元のライブラリと同じライブラリとして扱うため、「最近使ったライブラリ」では元のライブラリと入れ替わります。元のライブラリは「ライブラリを開く」で開けます。)",
                "開く"))
        {
            Finish(await _libraries.OpenAsync(result.Library.LibraryFilePath, _dialogs));
        }
    }

    // 決定ログ AA-1: 項目の並び順がそのまま順位(0 = 畳まない)
    private void OnTagCollapseRankChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded && TagCollapseRankBox.SelectedIndex >= 0)
        {
            _libraries.SetTagCollapseRank(TagCollapseRankBox.SelectedIndex);
        }
    }

    // ==== サブライブラリ(決定ログ S-1、S-5、S-8、S-15) ====

    private async Task RefreshSubsAsync(bool search = false)
    {
        if (search)
        {
            await _storage.RefreshAsync();
        }
        var selectedId = (SubList.SelectedItem as SubLibraryRow)?.Id;
        var rows = await Task.Run(() =>
        {
            IReadOnlyDictionary<string, StorageUsage> usage;
            using (var connection = PdfManagerDatabase.Open(_current.DataFolder.DatabasePath))
            {
                usage = new SubLibraryRepository(connection).GetUsage();
            }
            return _storage.Storage.Subs.Select(s =>
            {
                var used = usage.GetValueOrDefault(s.Id) ?? new StorageUsage(0, 0);
                var free = s.IsWritable ? $" / 空き容量 {FreeSpace(s.Location!.RootFolder)}" : "";
                return new SubLibraryRow(s, $"PDF {used.Count:N0} 件({FormatSize(used.Bytes)}){free}");
            }).ToList();
        });
        SubList.ItemsSource = rows;
        SubList.SelectedItem = rows.FirstOrDefault(r => r.Id == selectedId) ?? rows.FirstOrDefault();
        NoSubsText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SubList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnCheckSubsClick(object sender, RoutedEventArgs e) => await RefreshSubsAsync(search: true);

    private async void OnCreateSubClick(object sender, RoutedEventArgs e)
    {
        var window = new CreateLibraryWindow((parent, name) => _storage.RunAsync(s => s.CreateSub(parent, name))) { Owner = this };
        window.ShowDialog();
        await RefreshSubsAsync();
        if (window.CreatedLibrary is { } created)
        {
            SubList.SelectedItem = SubList.Items.Cast<SubLibraryRow>().FirstOrDefault(r => r.Id == created.Id.ToString("D"));
        }
    }

    // 決定ログ EB-6
    private async void OnRegisterSubClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "登録するサブライブラリの .pdflib ファイルを選択",
            Filter = $"PdfManager ライブラリ (*{LibraryService.Extension})|*{LibraryService.Extension}",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        LibraryInfo? registered = null;
        await RunSubActionAsync(s => registered = s.RegisterExistingSub(dialog.FileName));
        if (registered is { } info)
        {
            SubList.SelectedItem = SubList.Items.Cast<SubLibraryRow>().FirstOrDefault(r => r.Id == info.Id.ToString("D"));
        }
    }

    // 決定ログ EB-3
    private async void OnPullSubClick(object sender, RoutedEventArgs e)
    {
        if (await SelectedSubAsync() is not { } row)
        {
            return;
        }
        if (row.State.PendingPull is not { } plan)
        {
            await _dialogs.ShowErrorAsync(row.State.Status == SubLibraryStatus.Connected
                ? $"サブライブラリ「{row.Name}」は読み込み済みです(ほかの PC での変更は、つないだときに自動で読み込みます)。"
                : _storage.Storage.UnavailableMessage(row.Id), "読み込む");
            return;
        }
        if (await _dialogs.ConfirmAsync("読み込む",
                $"サブライブラリ「{row.Name}」の内容を読み込みますか?({plan.Summary})\n\nこの PC でまだサブへ書き込めていない次の変更は消え、サブの内容に合わせます。\n"
                + string.Join("\n", plan.LostTitles.Take(10).Select(t => "・" + t)) + (plan.LostChanges > 10 ? $"\nほか {plan.LostChanges - 10} 件" : ""),
                "読み込む"))
        {
            await RunSubActionAsync(s => s.PullSub(row.Id));
        }
    }

    private async void OnRenameSubClick(object sender, RoutedEventArgs e)
    {
        if (await SelectedSubAsync() is not { } row)
        {
            return;
        }
        var input = new TextInputWindow("サブライブラリの名前の変更", "新しい名前", row.Name) { Owner = this };
        input.ShowDialog();
        if (input.Result is { } name)
        {
            await RunSubActionAsync(s => s.RenameSub(row.Id, name));
        }
    }

    // 決定ログ S-5・S-15
    private async void OnRelocateSubClick(object sender, RoutedEventArgs e)
    {
        if (await SelectedSubAsync() is not { } row)
        {
            return;
        }
        string? path;
        if (row.State is { Status: SubLibraryStatus.Duplicate, Candidates: [var first, var second] })
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Owner = this,
                Title = "使う方を選ぶ",
                Content = $"サブライブラリ「{row.Name}」が 2 か所で見つかりました。この PC で使う方を選んでください。\n\n1:{first}\n2:{second}\n\n選ばなかった方は、次から使いません(フォルダは消しません)。",
                PrimaryButtonText = "1 を使う",
                SecondaryButtonText = "2 を使う",
                CloseButtonText = "キャンセル",
            };
            path = await box.ShowDialogAsync() switch
            {
                Wpf.Ui.Controls.MessageBoxResult.Primary => first,
                Wpf.Ui.Controls.MessageBoxResult.Secondary => second,
                _ => null,
            };
        }
        else
        {
            var dialog = new OpenFileDialog
            {
                Title = $"サブライブラリ「{row.Name}」の .pdflib ファイルを選択",
                Filter = $"PdfManager ライブラリ (*{LibraryService.Extension})|*{LibraryService.Extension}",
            };
            path = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (path is not null)
        {
            await RunSubActionAsync(s => s.Relocate(row.Id, path));
        }
    }

    private async void OnExplorerSubClick(object sender, RoutedEventArgs e)
    {
        if (await SelectedSubAsync() is not { } row)
        {
            return;
        }
        if (row.State.Location is { } location && Directory.Exists(location.RootFolder))
        {
            OpenInExplorer(location.RootFolder);
        }
        else
        {
            await _dialogs.ShowErrorAsync(_storage.Storage.UnavailableMessage(row.Id), "エクスプローラーで開く");
        }
    }

    // 決定ログ S-8
    private async void OnRemoveSubClick(object sender, RoutedEventArgs e)
    {
        if (await SelectedSubAsync() is not { } row
            || !await _dialogs.ConfirmAsync("登録の解除", $"サブライブラリ「{row.Name}」の登録を解除しますか?\nフォルダとその中のファイルは消しません。", "解除する"))
        {
            return;
        }
        await RunSubActionAsync(s => s.RemoveSub(row.Id));
    }

    private async Task<SubLibraryRow?> SelectedSubAsync()
    {
        if (SubList.SelectedItem is SubLibraryRow row)
        {
            return row;
        }
        await _dialogs.ShowErrorAsync("サブライブラリを一覧から選んでください。", "サブライブラリ");
        return null;
    }

    private async Task RunSubActionAsync(Action<LibraryStorage> action)
    {
        try
        {
            await _storage.RunAsync(action);
        }
        catch (Exception ex) when (ex is LibraryException or IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync(ex.Message, "サブライブラリ");
        }
        await RefreshSubsAsync();
    }
}
