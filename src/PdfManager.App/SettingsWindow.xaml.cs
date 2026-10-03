using System.Diagnostics;
using System.IO;
using System.Windows;
using PdfManager.App.Services;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Library;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// SC-08 設定
public partial class SettingsWindow : FluentWindow
{
    private readonly LibraryInfo _current;
    private readonly LibraryActions _libraries;
    private readonly IUserDialogs _dialogs;

    public SettingsWindow(LibraryInfo current, LibraryActions libraries)
    {
        InitializeComponent();
        _current = current;
        _libraries = libraries;
        _dialogs = new WpfUiDialogs(this);
        SystemThemeWatcher.Watch(this);

        CurrentName.Text = current.Name;
        CurrentFolder.Text = current.RootFolder;
        RefreshRecent();
        Loaded += async (_, _) => CurrentStats.Text = await Task.Run(DescribeCurrent);
    }

    // 切り替え先。null なら切り替えない
    public LibraryInfo? SelectedLibrary { get; private set; }

    private string DescribeCurrent()
    {
        long count;
        using (var connection = PdfManagerDatabase.Open(_current.DataFolder.DatabasePath))
        {
            count = new DocumentRepository(connection).Count();
        }
        var bytes = Directory.EnumerateFiles(_current.DataFolder.Root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        return $"PDF {count:N0} 件 / 使用容量 {FormatSize(bytes)}";
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

    private void OnExplorerClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_current.RootFolder}\"") { UseShellExecute = true });

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
}
