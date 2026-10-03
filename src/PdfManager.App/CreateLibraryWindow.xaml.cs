using System.IO;
using System.Windows;
using Microsoft.Win32;
using PdfManager.App.Services;
using PdfManager.Core.Library;
using PdfManager.Core.Text;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// SC-10 ライブラリの作成
public partial class CreateLibraryWindow : FluentWindow
{
    private readonly IUserDialogs _dialogs;

    public CreateLibraryWindow()
    {
        InitializeComponent();
        _dialogs = new WpfUiDialogs(this);
        SystemThemeWatcher.Watch(this);
        Loaded += (_, _) => NameBox.Focus();
    }

    public LibraryInfo? CreatedLibrary { get; private set; }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "ライブラリを作成するフォルダを選択" };
        if (dialog.ShowDialog(this) == true)
        {
            ParentBox.Text = dialog.FolderName;
        }
    }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var name = NameNormalizer.Clean(NameBox.Text);
        var parent = ParentBox.Text.Trim();
        PreviewText.Text = name.Length == 0 || parent.Length == 0
            ? "-"
            : Path.Combine(parent, LibraryService.LibraryFolderName, FileNames.Sanitize(name, "library") + LibraryService.Extension);
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var parent = ParentBox.Text.Trim();
        if (parent.Length == 0)
        {
            await _dialogs.ShowErrorAsync("作成する場所(フォルダ)を選んでください。");
            return;
        }
        if (Directory.Exists(parent) && !await LibraryActions.ConfirmRemovableDriveAsync(parent, _dialogs))
        {
            return;
        }

        try
        {
            CreatedLibrary = LibraryService.Create(parent, NameBox.Text);
        }
        catch (LibraryException ex)
        {
            await _dialogs.ShowErrorAsync(ex.Message);
            return;
        }
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
