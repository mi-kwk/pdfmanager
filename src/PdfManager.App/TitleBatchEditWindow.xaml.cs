using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// SC-19 タイトルの付け直し(編集)
public partial class TitleBatchEditWindow : FluentWindow
{
    private readonly TitleBatchEditViewModel _viewModel;
    private readonly IUserDialogs _dialogs;
    private bool _closeConfirmed;

    public TitleBatchEditWindow(DataFolder dataFolder, StorageAccess storage, IReadOnlyList<TitleRow> rows)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        _dialogs = new WpfUiDialogs(this);
        _viewModel = new TitleBatchEditViewModel(dataFolder, storage, _dialogs, rows);
        _viewModel.SaveCompleted += () =>
        {
            _closeConfirmed = true;
            Close();
        };
        DataContext = _viewModel;
        Closing += OnClosing;
    }

    public bool Saved => _viewModel.Saved;

    private IReadOnlyList<TitleEditRow> SelectedRows => RowList.SelectedItems.Cast<TitleEditRow>().ToList();

    private void OnUpClick(object sender, RoutedEventArgs e) => _viewModel.Move(SelectedRows, -1);

    private void OnDownClick(object sender, RoutedEventArgs e) => _viewModel.Move(SelectedRows, 1);

    private async void OnNumberClick(object sender, RoutedEventArgs e) => await _viewModel.NumberAsync(SelectedRows);

    // ≡ をドラッグして並べ替える
    private void OnHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TitleEditRow row } element)
        {
            DragDrop.DoDragDrop(element, row, DragDropEffects.Move);
            e.Handled = true;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TitleEditRow)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TitleEditRow)) is not TitleEditRow row)
        {
            return;
        }
        // 落とした場所の行の位置へ(行の外なら最後へ)
        var target = e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(RowList, source) is ListBoxItem { DataContext: TitleEditRow over }
            ? _viewModel.Rows.IndexOf(over)
            : _viewModel.Rows.Count - 1;
        _viewModel.MoveTo(row, target);
        RowList.SelectedItem = row;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // 決定ログ DI-8: 保存していない変更があれば確かめる
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || !_viewModel.HasChanges)
        {
            return;
        }
        e.Cancel = true;
        Dispatcher.BeginInvoke(async () =>
        {
            if (await _dialogs.ConfirmAsync("タイトルの付け直し", $"保存していない変更が {_viewModel.ChangeCount} 件あります。保存せずに閉じますか?", "閉じる"))
            {
                _closeConfirmed = true;
                Close();
            }
        });
    }
}
