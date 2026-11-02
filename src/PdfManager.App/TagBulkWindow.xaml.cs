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

// SC-21 タグの一括付与(決定ログ DM-1〜DM-5)
public partial class TagBulkWindow : FluentWindow
{
    private readonly TagBulkViewModel _viewModel;
    private readonly IUserDialogs _dialogs;
    private TitleRow? _lastClicked;
    private bool _closeConfirmed;

    public TagBulkWindow(DataFolder dataFolder, StorageAccess storage, IReadOnlyCollection<string>? initialChecked, int tagCollapseRank)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        _dialogs = new WpfUiDialogs(this);
        _viewModel = new TagBulkViewModel(dataFolder, storage, _dialogs, initialChecked, tagCollapseRank);
        DataContext = _viewModel;
        ListRadio.IsChecked = !_viewModel.Selector.GroupBySeries;
        Closing += OnClosing;
    }

    public bool Saved => _viewModel.Saved;

    // 決定ログ DJ-3 と同じ: Shift を押しながら押すと範囲をまとめて付け外し
    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TitleRow row })
        {
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lastClicked is not null && _lastClicked != row)
        {
            _viewModel.Selector.SetRange(_lastClicked, row, row.IsChecked);
        }
        _lastClicked = row;
    }

    private async void OnRowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TitleRow row })
        {
            await _viewModel.ShowPreviewAsync(row);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // まだ実行していない「付ける・外す」があれば確かめる
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || !_viewModel.HasIntents)
        {
            return;
        }
        e.Cancel = true;
        Dispatcher.BeginInvoke(async () =>
        {
            if (await _dialogs.ConfirmAsync("タグの一括付与", "まだ実行していない変更があります。実行せずに閉じますか?", "閉じる"))
            {
                _closeConfirmed = true;
                Close();
            }
        });
    }
}
