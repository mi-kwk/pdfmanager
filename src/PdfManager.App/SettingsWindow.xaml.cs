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
        SubLibraryStatus.ChangedElsewhere or SubLibraryStatus.Unavailable => State.Problem ?? "",
        _ => "",
    };

    public bool HasNote => Note.Length > 0;
}

// SC-08 設定
public partial class SettingsWindow : FluentWindow
{
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

        CurrentName.Text = current.Name;
        CurrentFolder.Text = current.RootFolder;
        ShowRegisterAllBox.IsChecked = libraries.ShowRegisterAll;
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
