using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

public partial class ManageWindow : FluentWindow
{
    private readonly ManageViewModel _viewModel;
    private bool _closeConfirmed;

    // libraries:配下のタグの欄を畳んだかを PC ごとに覚える(決定ログ HC-7)。null なら覚えない
    public ManageWindow(DataFolder dataFolder, Func<string?, bool>? isStorageAvailable = null, LibraryActions? libraries = null)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        _viewModel = new ManageViewModel(dataFolder, new WpfUiDialogs(this), isStorageAvailable,
            childPanelExpanded: !(libraries?.ChildTagsCollapsed ?? false),
            saveChildPanelExpanded: libraries is null ? null : expanded => libraries.SetChildTagsCollapsed(!expanded));
        DataContext = _viewModel;
        SystemThemeWatcher.Watch(this);
        Closing += OnClosing;
    }

    // 決定ログ HC-8: 保存していないタグ名・別名があれば聞く。閉じる途中の処理の中では閉じ直せないため、終わってから確認する(R-8)
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || !_viewModel.HasUnsavedTagEdits)
        {
            return;
        }
        e.Cancel = true;
        Dispatcher.BeginInvoke(async () =>
        {
            if (await _viewModel.ConfirmCloseAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        });
    }

    // 決定ログ HC-6: 配下のタグをダブルクリックすると、そのタグを開く
    private void OnChildTagDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ChildTagRow row })
        {
            _viewModel.OpenChildTag(row);
        }
    }

    // 決定ログ HC-4: 追加したあとも入力欄にカーソルを残す
    private void OnAddChildClick(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(() => ChildTagNameBox.Focus());
}
