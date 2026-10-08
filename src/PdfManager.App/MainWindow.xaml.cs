using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
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
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private readonly MainViewModel _viewModel;
    private readonly LibraryInfo _library;
    private readonly LibraryActions _libraries;
    private readonly StorageAccess _storage;
    private readonly DispatcherTimer _deviceChangeTimer;
    private bool _restoringSelection;
    private bool _closeConfirmed;
    private ViewerWindow? _viewer;

    public MainWindow(LibraryInfo library, LibraryActions libraries)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this, maximizeIfTooLarge: true);
        _library = library;
        _libraries = libraries;
        // 決定ログ S-4・S-5: 起動時にサブライブラリを探す(見つからなくても起動はできる)
        var storage = new LibraryStorage(library);
        try
        {
            storage.Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // サブの状態は「確認」で取り直せる
        }
        _storage = new StorageAccess(storage);
        _viewModel = new MainViewModel(library.DataFolder, _storage, new WpfUiDialogs(this), libraries.View, libraries.SaveView);
        // ドライブをつないだ・外したときは、少し待ってからサブを探し直す
        _deviceChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _deviceChangeTimer.Tick += async (_, _) =>
        {
            _deviceChangeTimer.Stop();
            await _storage.RefreshAsync();
        };
        _viewModel.Editor.TagSettingRequested += OnTagSettingRequested;
        _viewModel.ResultsReplacing += () => _restoringSelection = true;
        _viewModel.SelectionRestoreRequested += RestoreSelection;
        DataContext = _viewModel;
        Title = $"{library.Name} - PdfManager";
        AppTitleBar.Title = Title;
        InputBindings.Add(new KeyBinding(new RelayCommand(() => SearchBox.Focus()), Key.F, ModifierKeys.Control));
        SystemThemeWatcher.Watch(this);
        Closing += OnClosing;
        // ライブラリを切り替えたときや終了したときは、ビューアも閉じる(決定ログ K-6)
        Closed += (_, _) =>
        {
            _deviceChangeTimer.Stop();
            _viewer?.Close();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_DEVICECHANGE && (int)wParam is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
        {
            _deviceChangeTimer.Stop();
            _deviceChangeTimer.Start();
        }
        return IntPtr.Zero;
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

    // ==== タグ設定(決定ログ R-11 ④) ====

    // 詳細エリアの「タグ設定」。OK なら詳細エリアに反映し(保存は「保存」で)、キャンセルなら元のまま
    private void OnTagSettingRequested()
    {
        var hadChanges = _viewModel.HasChanges;
        var window = new TagSettingWindow(_viewModel.Editor) { Owner = this };
        window.ShowDialog();
        if (!window.Accepted)
        {
            _viewModel.HasChanges = hadChanges;
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
        .Select(c => (c.Entry.Id, c.Title, c.FileName))
        .ToList());

    // 決定ログ S-4: つながっていないサブの PDF は開けない(理由を表示する)
    private async void OpenInViewer(IReadOnlyList<(string Id, string Title, string FileName)> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }
        var paths = await _viewModel.ResolvePdfPathsAsync(targets.Select(t => t.Id).ToList());
        var documents = targets.Where(t => paths.ContainsKey(t.Id)).Select(t => new ViewerDocument(t.Id, t.Title, t.FileName, paths[t.Id])).ToList();
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
        if (_viewModel.SingleDetail is not { } previous || !await _viewModel.ResolvePendingChangesAsync()
            || !await _viewModel.EnsureEditableAsync([previous.Id], "新しい版を追加"))
        {
            return;
        }
        var dialog = new OpenFileDialog { Title = $"「{previous.Title}」の新しい版の PDF を選択", Filter = "PDF ファイル (*.pdf)|*.pdf" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var storageId = _viewModel.StorageIdsOf([previous.Id]).GetValueOrDefault(previous.Id);
        var window = new ImportWindow(_viewModel.DataFolder, [dialog.FileName], previous, _libraries.ShowRegisterAll) { Owner = this };
        window.ShowDialog();
        if (window.LastRegisteredId is { } added && storageId is not null)
        {
            // 決定ログ S-7: 版の系列がサブにあるときは、新しい版もそのサブに置く(取り込みはいったんメインに入る)
            var plan = await _storage.RunAsync(s => s.PlanMove([added], storageId));
            var result = await _storage.RunAsync(s => s.Move(plan));
            if (result.Problems.Count > 0)
            {
                await new WpfUiDialogs(this).ShowErrorAsync(
                    $"新しい版はメインに登録しましたが、サブライブラリ「{_storage.Storage.StorageName(storageId)}」へ移せませんでした。「保存先を移す」で移してください。\n\n{string.Join("\n", result.Problems)}",
                    "新しい版を追加");
            }
        }
        _viewModel.Refresh();
        if (window.LastRegisteredId is { } registered)
        {
            SelectDocument(registered);
        }
    }

    private void OnOpenVersionClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Versions.FirstOrDefault(v => v.Info.DocumentId == (string?)((FrameworkElement)sender).Tag) is { } row)
        {
            OpenInViewer([(row.Info.DocumentId, row.Info.Title, row.Info.FileName)]);
        }
    }

    // ==== 保存先を移す(決定ログ S-3、S-7) ====

    // 右クリックメニューを開くたびに、移す先(メインと各サブ)を並べ直す
    private void OnListContextMenuOpened(object sender, RoutedEventArgs e)
    {
        MoveMenu.Items.Clear();
        MoveMenu.Visibility = _viewModel.HasSubs ? Visibility.Visible : Visibility.Collapsed;
        var targets = new List<(string? Id, string Name, bool Available)> { (null, "メイン", true) };
        targets.AddRange(_storage.Storage.Subs.Select(s => ((string?)s.Id, s.Name, s.IsWritable)));
        foreach (var (id, name, available) in targets)
        {
            var item = new System.Windows.Controls.MenuItem { Header = available ? name : $"{name}({_storage.Storage.FindSub(id)?.StatusText})", IsEnabled = available };
            item.Click += async (_, _) => await MoveSelectedAsync(id);
            MoveMenu.Items.Add(item);
        }
    }

    private async Task MoveSelectedAsync(string? targetId)
    {
        var ids = DocumentList.SelectedItems.Cast<DocumentCard>().Select(c => c.Entry.Id).ToList();
        if (!await _viewModel.ResolvePendingChangesAsync() || await _viewModel.PrepareMoveAsync(ids, targetId) is not { } plan)
        {
            return;
        }
        _viewer?.CloseDocuments(plan.DocumentIds);
        var progress = new ProgressWindow($"「{_storage.Storage.StorageName(targetId)}」へ移しています") { Owner = this };
        var task = _storage.RunAsync(s => s.Move(plan, progress.Progress, progress.Cancellation));
        progress.ShowUntil(task);
        await _viewModel.ReportMoveAsync(await task, targetId);
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
        new ImportWindow(_viewModel.DataFolder, paths, showRegisterAll: _libraries.ShowRegisterAll) { Owner = this }.ShowDialog();
        _viewModel.Refresh();
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!await _viewModel.ResolvePendingChangesAsync())
        {
            return;
        }
        var settings = new SettingsWindow(_library, _libraries, _storage) { Owner = this };
        settings.ShowDialog();
        if (settings.SelectedLibrary is { } library)
        {
            App.CurrentApp.SwitchLibrary(library);
            return;
        }
        _viewModel.Refresh();
    }

    // 下の「サブ n/m 接続」から、設定画面の「ライブラリ」を開く(決定ログ S-1)
    private void OnSubStatusClick(object sender, RoutedEventArgs e) => OnSettingsClick(sender, e);

    private async void OnManageCatalogClick(object sender, RoutedEventArgs e)
    {
        if (!await _viewModel.ResolvePendingChangesAsync())
        {
            return;
        }
        // 決定ログ S-12: 項目・タグの定義の変更は、サブの文書の値にも関わる
        await _storage.RunAsync(s => s.BeginCatalogEdit());
        new ManageWindow(_viewModel.DataFolder) { Owner = this }.ShowDialog();
        var problems = await _storage.RunAsync(s => s.EndCatalogEdit());
        _viewModel.Refresh();
        if (problems.Count > 0)
        {
            await new WpfUiDialogs(this).ShowErrorAsync(string.Join("\n\n", problems), "サブライブラリ");
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || !_viewModel.HasChanges)
        {
            return;
        }
        e.Cancel = true;
        // 閉じる途中の処理の中では Close を呼べない(呼ぶと例外でアプリが落ちる)ため、この処理が終わってから確認して閉じ直す
        Dispatcher.BeginInvoke(async () =>
        {
            if (await _viewModel.ResolvePendingChangesAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        });
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
