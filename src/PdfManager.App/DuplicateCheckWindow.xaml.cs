using System.Windows;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// SC-17 重複チェック(決定ログ DH-6・DH-7)。ビューアで見比べられるよう、メイン画面を操作できる状態で開く
public partial class DuplicateCheckWindow : FluentWindow
{
    public DuplicateCheckWindow(DataFolder dataFolder, bool loose,
        Func<(string Id, string Title, string FileName), (string Id, string Title, string FileName), Task> compare,
        Func<IReadOnlyList<string>, Task<bool>> delete)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        DataContext = new DuplicateCheckViewModel(dataFolder, new WpfUiDialogs(this), loose, compare, delete);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
