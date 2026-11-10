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
        int tagCollapseRank = PdfManager.Core.Settings.AppSettings.DefaultTagCollapseRank, bool useTextTitleCandidates = true, bool duplicateLoose = false)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        _dialogs = new WpfUiDialogs(this);
        _viewModel = new ImportViewModel(dataFolder, _dialogs, paths, newVersionOf, showRegisterAll, tagCollapseRank, useTextTitleCandidates, duplicateLoose);
        // 決定ログ DH-4: 1 ページ目が似ている登録済みの PDF を並べて見せる
        _viewModel.ShowDuplicates = (item, matches) =>
        {
            var window = new DuplicateResultWindow(item.DisplayName, item.Preview, matches, item.IsHeld) { Owner = this };
            window.ShowDialog();
            return window.Choice;
        };
        DataContext = _viewModel;
        // 決定ログ DD-5: 保留の PDF を一覧の上にまとめる
        System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.Items).GroupDescriptions
            .Add(new System.Windows.Data.PropertyGroupDescription(nameof(ImportItem.GroupName)));
        // 決定ログ DC-1: 登録して次の PDF に移ったら、右の入力欄を一番上へ戻す
        _viewModel.EditorScrollResetRequested += () => EditorScroll.ScrollToTop();
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
        // 決定ログ ED-3: 登録・保留・削除予定へ移す前に、ビューアのタブを閉じる
        _viewModel.ReleaseFiles = paths => _viewer?.CloseDocuments(paths);
        SystemThemeWatcher.Watch(this);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _viewer?.Close();
            _viewModel.Dispose();
        };
    }

    // ==== PDF を開く(決定ログ ED-1〜ED-4) ====

    // 登録する前の PDF を見るビューア。この画面の上に開き、この画面を閉じたら閉じる
    private ViewerWindow? _viewer;

    private void OnPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && _viewModel.PrimarySelection is { } item)
        {
            e.Handled = true;
            OpenInViewer(item);
        }
    }

    private void OnItemDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is { } source
            && System.Windows.Controls.ItemsControl.ContainerFromElement(ItemList, source) is System.Windows.Controls.ListBoxItem { DataContext: ImportItem item })
        {
            OpenInViewer(item);
        }
    }

    private async void OpenInViewer(ImportItem item)
    {
        if (!System.IO.File.Exists(item.SourcePath))
        {
            await _dialogs.ShowErrorAsync($"PDF が見つかりません。\n{item.SourcePath}", "PDF を開く");
            return;
        }
        if (_viewer is null)
        {
            // 決定ログ ED-4: まだ登録していないので、閲覧の記録は残さない。続きの前後もない
            _viewer = new ViewerWindow(_ => { }, _ => (null, null), _ => { }) { Owner = this };
            _viewer.Closed += (_, _) => _viewer = null;
        }
        _viewer.Open([new ViewerDocument(item.SourcePath, item.DisplayName, item.DisplayName, item.SourcePath)]);
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
