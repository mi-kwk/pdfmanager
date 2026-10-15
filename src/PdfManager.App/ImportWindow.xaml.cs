using System.ComponentModel;
using System.Windows;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// SC-03 取り込み
public partial class ImportWindow : FluentWindow
{
    private readonly ImportViewModel _viewModel;
    private readonly IUserDialogs _dialogs;
    private bool _closeConfirmed;

    public ImportWindow(DataFolder dataFolder, IEnumerable<string> paths, PdfManager.Core.Documents.DocumentDetail? newVersionOf = null, bool showRegisterAll = false,
        int tagCollapseRank = PdfManager.Core.Settings.AppSettings.DefaultTagCollapseRank)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        _dialogs = new WpfUiDialogs(this);
        _viewModel = new ImportViewModel(dataFolder, _dialogs, paths, newVersionOf, showRegisterAll, tagCollapseRank);
        DataContext = _viewModel;
        // 決定ログ Y-1: 画像・ZIP は「画像から PDF 作成」の画面で PDF にしてから一覧に入れる
        _viewModel.CreatePdfsFromImages = (images, zips, workFolder) =>
        {
            var window = new ImagePdfWindow(images, zips, workFolder) { Owner = this };
            window.ShowDialog();
            return window.Created;
        };
        // 画面が表示されてから一覧に入れる(「画像から PDF 作成」の画面をこの画面の上に開くため)
        ContentRendered += (_, _) =>
        {
            if (!_viewModel.AddInitialFiles())
            {
                // 画像から PDF を作るのをやめて、取り込むものがなくなった
                Dispatcher.BeginInvoke(Close);
            }
        };
        SystemThemeWatcher.Watch(this);
        Closing += OnClosing;
        Closed += (_, _) => _viewModel.Dispose();
    }

    public int RegisteredCount => _viewModel.RegisteredCount;

    public string? LastRegisteredId => _viewModel.LastRegisteredId;

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        _viewModel.SetSelection(ItemList.SelectedItems.Cast<ImportItem>());

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            e.Handled = true;
            _viewModel.AddFiles(paths);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }
        if (_viewModel.IsRegistering)
        {
            e.Cancel = true;
            return;
        }
        if (_viewModel.ReadyCount == 0)
        {
            return;
        }
        e.Cancel = true;
        // 閉じる途中の処理の中では Close を呼べない(呼ぶと例外でアプリが落ちる)ため、この処理が終わってから確認して閉じ直す
        Dispatcher.BeginInvoke(async () =>
        {
            if (await _dialogs.ConfirmAsync("取り込みを閉じる", $"まだ登録していない PDF が {_viewModel.ReadyCount} 件あります。登録せずに閉じますか?", "閉じる"))
            {
                _closeConfirmed = true;
                Close();
            }
        });
    }
}
