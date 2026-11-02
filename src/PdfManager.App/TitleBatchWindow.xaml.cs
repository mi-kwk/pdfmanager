using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// SC-18 タイトルの付け直し(選ぶ画面)。チェックしたものだけを SC-19 で付け直す
public partial class TitleBatchWindow : FluentWindow
{
    private readonly TitleBatchViewModel _viewModel;
    private readonly DataFolder _dataFolder;
    private readonly StorageAccess _storage;
    private TitleRow? _lastClicked;

    public TitleBatchWindow(DataFolder dataFolder, StorageAccess storage)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        _dataFolder = dataFolder;
        _storage = storage;
        _viewModel = new TitleBatchViewModel(dataFolder, storage);
        DataContext = _viewModel;
        ListRadio.IsChecked = !_viewModel.GroupBySeries;
    }

    // 一度でも保存したか(メイン画面を読み直すため)
    public bool Saved { get; private set; }

    // 決定ログ DJ-3: Shift を押しながら押すと、前に押したものからの範囲を同じ状態にする
    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TitleRow row })
        {
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lastClicked is not null && _lastClicked != row)
        {
            _viewModel.SetRange(_lastClicked, row, row.IsChecked);
        }
        _lastClicked = row;
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        var rows = _viewModel.CheckedRows;
        if (rows.Count == 0)
        {
            await new WpfUiDialogs(this).ShowErrorAsync("付け直す PDF にチェックを付けてください。", "タイトルの付け直し");
            return;
        }
        var window = new TitleBatchEditWindow(_dataFolder, _storage, rows) { Owner = this };
        window.ShowDialog();
        if (window.Saved)
        {
            Saved = true;
            _viewModel.Load();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
