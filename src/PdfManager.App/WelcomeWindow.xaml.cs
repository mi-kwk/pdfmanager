using System.Windows;
using PdfManager.App.Services;
using PdfManager.Core.Library;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// SC-09 ようこそ(初回起動、または前回のライブラリを開けなかったとき)
public partial class WelcomeWindow : FluentWindow
{
    private readonly LibraryActions _libraries;
    private readonly IUserDialogs _dialogs;

    public WelcomeWindow(LibraryActions libraries)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        _libraries = libraries;
        _dialogs = new WpfUiDialogs(this);
        SystemThemeWatcher.Watch(this);
        RefreshRecent();
    }

    public LibraryInfo? SelectedLibrary { get; private set; }

    private void OnCreateClick(object sender, RoutedEventArgs e) => Finish(_libraries.Create(this));

    private async void OnOpenClick(object sender, RoutedEventArgs e) => Finish(await _libraries.BrowseAndOpenAsync(_dialogs));

    private async void OnOpenRecentClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentLibraryItem item)
        {
            Finish(await _libraries.OpenAsync(item.LibraryFilePath, _dialogs));
        }
    }

    private void OnForgetClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentLibraryItem item)
        {
            _libraries.Forget(item.LibraryFilePath);
            RefreshRecent();
        }
    }

    private void Finish(LibraryInfo? library)
    {
        if (library is null)
        {
            return;
        }
        SelectedLibrary = library;
        Close();
    }

    private void RefreshRecent()
    {
        var recent = _libraries.GetRecent(current: null);
        RecentList.ItemsSource = recent;
        RecentList.Visibility = recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoRecentText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
