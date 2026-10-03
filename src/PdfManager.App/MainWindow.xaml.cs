using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Library;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly LibraryInfo _library;
    private readonly LibraryActions _libraries;
    private bool _restoringSelection;
    private bool _closeConfirmed;
    private ViewerWindow? _viewer;

    public MainWindow(LibraryInfo library, LibraryActions libraries)
    {
        InitializeComponent();
        _library = library;
        _libraries = libraries;
        _viewModel = new MainViewModel(library.DataFolder, new WpfUiDialogs(this), libraries.View, libraries.SaveView);
        _viewModel.ResultsReplacing += () => _restoringSelection = true;
        _viewModel.SelectionRestoreRequested += RestoreSelection;
        DataContext = _viewModel;
        Title = $"{library.Name} - PdfManager";
        AppTitleBar.Title = Title;
        InputBindings.Add(new KeyBinding(new RelayCommand(() => SearchBox.Focus()), Key.F, ModifierKeys.Control));
        SystemThemeWatcher.Watch(this);
        Closing += OnClosing;
        // ライブラリを切り替えたときや終了したときは、ビューアも閉じる(決定ログ K-6)
        Closed += (_, _) => _viewer?.Close();
    }

    // ==== 一覧の選択(決定ログ J-1・J-2) ====

    private void OnDocumentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection)
        {
            return;
        }
        var newIds = DocumentList.SelectedItems.Cast<DocumentCard>().Select(c => c.Entry.Id).ToList();
        if (!_viewModel.HasChanges)
        {
            _viewModel.SetSelection(newIds);
            return;
        }
        // 確認の画面は、選択の処理が終わってから開く(入力の処理の途中でモーダル画面を開かない)
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (await _viewModel.ResolvePendingChangesAsync())
            {
                _viewModel.SetSelection(newIds);
                RestoreSelection(newIds);
            }
            else
            {
                RestoreSelection(_viewModel.SelectedIds);
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RestoreSelection(IReadOnlyCollection<string> ids)
    {
        _restoringSelection = true;
        try
        {
            DocumentList.SelectedItems.Clear();
            foreach (var card in _viewModel.Results.Where(c => ids.Contains(c.Entry.Id)))
            {
                DocumentList.SelectedItems.Add(card);
            }
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    // ==== ビューアで開く・削除(決定ログ K-1〜K-5) ====

    private void OnDocumentDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(DocumentList, (DependencyObject)e.OriginalSource) is ListBoxItem)
        {
            OpenSelectedInViewer();
        }
    }

    private void OnDocumentListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OpenSelectedInViewer();
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            OnDeleteClick(sender, e);
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenSelectedInViewer();

    private void OpenSelectedInViewer() => OpenInViewer(DocumentList.SelectedItems.Cast<DocumentCard>()
        .Select(c => new ViewerDocument(c.Entry.Id, c.Title, c.FileName, _viewModel.DataFolder.GetPdfPath(c.Entry.Id)))
        .ToList());

    private void OpenInViewer(IReadOnlyList<ViewerDocument> documents)
    {
        if (documents.Count == 0)
        {
            return;
        }
        if (_viewer is null)
        {
            _viewer = new ViewerWindow(_viewModel.RecordView);
            _viewer.Closed += (_, _) => _viewer = null;
        }
        _viewer.Open(documents);
    }

    // ==== 版管理(決定ログ N-1〜N-8) ====

    private async void OnGroupVersionsClick(object sender, RoutedEventArgs e)
    {
        var ids = DocumentList.SelectedItems.Cast<DocumentCard>().Select(c => c.Entry.Id).ToList();
        if (await _viewModel.ResolvePendingChangesAsync())
        {
            await _viewModel.GroupAsVersionsAsync(ids);
        }
    }

    private async void OnAddVersionClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SingleDetail is not { } previous || !await _viewModel.ResolvePendingChangesAsync())
        {
            return;
        }
        var dialog = new OpenFileDialog { Title = $"「{previous.Title}」の新しい版の PDF を選択", Filter = "PDF ファイル (*.pdf)|*.pdf" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var window = new ImportWindow(_viewModel.DataFolder, [dialog.FileName], previous) { Owner = this };
        window.ShowDialog();
        _viewModel.Refresh();
        if (window.LastRegisteredId is { } added)
        {
            SelectDocument(added);
        }
    }

    private void OnOpenVersionClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Versions.FirstOrDefault(v => v.Info.DocumentId == (string?)((FrameworkElement)sender).Tag) is { } row)
        {
            OpenInViewer([new ViewerDocument(row.Info.DocumentId, row.Info.Title, row.Info.FileName, _viewModel.DataFolder.GetPdfPath(row.Info.DocumentId))]);
        }
    }

    private async void OnSelectVersionClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string id && await _viewModel.ResolvePendingChangesAsync())
        {
            SelectDocument(id);
        }
    }

    private async void OnDetachVersionClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string id && await _viewModel.ResolvePendingChangesAsync())
        {
            await _viewModel.DetachVersionAsync(id);
        }
    }

    // 旧版のように一覧に出ていない文書も、詳細エリアでは選べる
    private void SelectDocument(string id)
    {
        _viewModel.SetSelection([id]);
        RestoreSelection([id]);
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e) =>
        await _viewModel.DeleteSelectedAsync(ids => _viewer?.CloseDocuments(ids));

    // ==== 他の画面 ====

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "取り込む PDF を選択", Filter = "PDF ファイル (*.pdf)|*.pdf", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
        {
            OpenImportWindow(dialog.FileNames);
        }
    }

    // 決定ログ F-1: 取り込み画面を開いている間はメイン画面を操作できない
    public async void OpenImportWindow(IEnumerable<string> paths)
    {
        if (!await _viewModel.ResolvePendingChangesAsync())
        {
            return;
        }
        new ImportWindow(_viewModel.DataFolder, paths) { Owner = this }.ShowDialog();
        _viewModel.Refresh();
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!await _viewModel.ResolvePendingChangesAsync())
        {
            return;
        }
        var settings = new SettingsWindow(_library, _libraries) { Owner = this };
        settings.ShowDialog();
        if (settings.SelectedLibrary is { } library)
        {
            App.CurrentApp.SwitchLibrary(library);
        }
    }

    private async void OnManageCatalogClick(object sender, RoutedEventArgs e)
    {
        if (!await _viewModel.ResolvePendingChangesAsync())
        {
            return;
        }
        new ManageWindow(_viewModel.DataFolder) { Owner = this }.ShowDialog();
        _viewModel.Refresh();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || !_viewModel.HasChanges)
        {
            return;
        }
        e.Cancel = true;
        if (await _viewModel.ResolvePendingChangesAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    // ==== ドラッグ&ドロップ ====

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
            // ドロップ元(エクスプローラー)を待たせないよう、ドロップ処理が終わってから画面を開く
            Dispatcher.BeginInvoke(() => OpenImportWindow(paths));
        }
    }
}
