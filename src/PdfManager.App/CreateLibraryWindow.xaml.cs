using System.IO;
using System.Windows;
using System.Windows.Automation;
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
    private readonly Func<string, string, Task<LibraryInfo>>? _createSub;

    public CreateLibraryWindow()
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        _dialogs = new WpfUiDialogs(this);
        SystemThemeWatcher.Watch(this);
        Loaded += (_, _) => NameBox.Focus();
    }

    // 決定ログ S-1: サブライブラリを作る(場所と名前を受け取って作る処理を渡す)
    public CreateLibraryWindow(Func<string, string, Task<LibraryInfo>> createSub) : this()
    {
        _createSub = createSub;
        Title = "新しいサブライブラリを作る";
        TitleBarControl.Title = Title;
        NameLabel.Text = "サブライブラリの名前";
        NameBox.PlaceholderText = "例:過去、よく見ない";
        AutomationProperties.SetName(NameBox, "サブライブラリの名前");
        HintText.Text = "選んだフォルダの中に「PdfManager」フォルダが作られ、このサブに移した PDF・サムネイル・情報はその中に保存されます。"
            + "内蔵のドライブか外付け HDD を選んでください(USB メモリには作れません)。";
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
        if (_createSub is null && Directory.Exists(parent) && !await LibraryActions.ConfirmRemovableDriveAsync(parent, _dialogs))
        {
            return;
        }

        try
        {
            CreatedLibrary = _createSub is null ? LibraryService.Create(parent, NameBox.Text) : await _createSub(parent, NameBox.Text);
        }
        catch (Exception ex) when (ex is LibraryException or IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync(ex.Message);
            return;
        }
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
