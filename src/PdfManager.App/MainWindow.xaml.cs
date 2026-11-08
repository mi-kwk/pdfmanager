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
using PdfManager.Core.Search;
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
        _viewModel.RecordUsage = libraries.RecordUsage;
        // ドライブをつないだ・外したときは、少し待ってからサブを探し直す
        _deviceChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _deviceChangeTimer.Tick += async (_, _) =>
        {
            _deviceChangeTimer.Stop();
            await _storage.RefreshAsync();
        };
        // 決定ログ EB-2・EB-3: ほかの PC で変わったサブを読み込んだら一覧を読み直して知らせ、確認が要るものは聞く
        _storage.Pulled += reports => _ = OnSubsPulledAsync(reports);
        _storage.Changed += () => _ = AskPendingPullsAsync();
        _viewModel.Editor.TagSettingRequested += OnTagSettingRequested;
        _viewModel.ResultsReplacing += () => _restoringSelection = true;
        _viewModel.SelectionRestoreRequested += RestoreSelection;
        DataContext = _viewModel;
        Title = $"{library.Name} - PdfManager";
        AppTitleBar.Title = Title;
        InputBindings.Add(new KeyBinding(new RelayCommand(() => SearchBox.Focus()), Key.F, ModifierKeys.Control));
        SystemThemeWatcher.Watch(this);
        Closing += OnClosing;
        // 決定ログ BB-5: 前回のバックアップから日数がたっていたら知らせる(画面が出てから)
        ContentRendered += async (_, _) =>
        {
            _storage.NotifyPulled();
            await AskPendingPullsAsync();
            await BackupActions.RemindIfDueAsync(this, _libraries, _storage, (int)_viewModel.DocumentCount);
            await RemindArchiveIfLowAsync();
        };
        // ライブラリを切り替えたときや終了したときは、ビューアも閉じる(決定ログ K-6)
        Closed += (_, _) =>
        {
            // 決定ログ BD-3: アプリを終えるとき(ライブラリの切り替えではないとき)は、後片付けの前に見張りを始める。
            // 閉じるときには WPF がメイン画面の登録を先に外していることがあるため、「別のメイン画面に切り替わった」ときだけ除く
            if (Application.Current.MainWindow is not MainWindow other || other == this)
            {
                App.StartExitWatchdog(0, "メイン画面を閉じた");
            }
#if DEBUG
            // 試験用:閉じる途中で止まった状態を作る(見張りで終わることを確かめるため。配布用の exe には入らない)
            if (Environment.GetEnvironmentVariable("PDFMANAGER_TEST_HANG_ON_CLOSE") == "1")
            {
                Thread.Sleep(TimeSpan.FromMinutes(1));
            }
#endif
            _deviceChangeTimer.Stop();
            _viewer?.Close();
            _duplicateCheck?.Close();
            _analytics?.Close();
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
        // 決定ログ AA-1・AA-5: 設定画面で変えた順位を使い、タグが付いている欄は畳んだ状態で開く
        _viewModel.Editor.CollapseUpToRank = _libraries.TagCollapseRank;
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

    private async void OpenInViewer(IReadOnlyList<(string Id, string Title, string FileName)> targets) => await OpenInViewerAsync(targets);

    // 決定ログ S-4: つながっていないサブの PDF は開けない(理由を表示する)。開けたものの数を返す
    private async Task<int> OpenInViewerAsync(IReadOnlyList<(string Id, string Title, string FileName)> targets)
    {
        if (targets.Count == 0)
        {
            return 0;
        }
        var paths = await _viewModel.ResolvePdfPathsAsync(targets.Select(t => t.Id).ToList());
        var documents = targets.Where(t => paths.ContainsKey(t.Id)).Select(t => new ViewerDocument(t.Id, t.Title, t.FileName, paths[t.Id])).ToList();
        if (documents.Count == 0)
        {
            return 0;
        }
        if (_viewer is null)
        {
            // 決定ログ V-5: ビューアの「前へ」「次へ」は、ここから続きの相手を開く
            _viewer = new ViewerWindow(_viewModel.RecordView, _viewModel.ChainNeighbors, d => OpenInViewer([(d.Id, d.Title, d.FileName)]));
            _viewer.Closed += (_, _) => _viewer = null;
        }
        _viewer.Open(documents);
        return documents.Count;
    }

    // ==== 2 画面比較(F-05、決定ログ W-1) ====

    private async Task CompareAsync((string Id, string Title, string FileName) left, (string Id, string Title, string FileName) right)
    {
        if (await OpenInViewerAsync([left, right]) == 2)
        {
            _viewer?.Compare(left.Id, right.Id);
        }
    }

    // 一覧で 2 件選んで右クリック「並べて比較」
    private async void OnCompareSelectedClick(object sender, RoutedEventArgs e)
    {
        var cards = DocumentList.SelectedItems.Cast<DocumentCard>().ToList();
        if (cards.Count != 2)
        {
            await new WpfUiDialogs(this).ShowErrorAsync("並べて比較するには、一覧で PDF をちょうど 2 件選んでください(Ctrl・Shift で複数選択)。", "並べて比較");
            return;
        }
        await CompareAsync((cards[0].Entry.Id, cards[0].Title, cards[0].FileName), (cards[1].Entry.Id, cards[1].Title, cards[1].FileName));
    }

    // 版の履歴の「比較」:今選んでいる版と並べる
    private async void OnCompareVersionClick(object sender, RoutedEventArgs e)
    {
        var id = (string?)((FrameworkElement)sender).Tag;
        var current = _viewModel.Versions.FirstOrDefault(v => v.IsCurrent);
        if (_viewModel.Versions.FirstOrDefault(v => v.Info.DocumentId == id) is not { } row || current is null)
        {
            return;
        }
        if (row == current)
        {
            await new WpfUiDialogs(this).ShowErrorAsync("今選んでいる版です。比較したいほかの版の「比較」を押してください。", "比較");
            return;
        }
        // 古い版を左、新しい版を右に並べる
        var (older, newer) = row.Info.VersionNo < current.Info.VersionNo ? (row, current) : (current, row);
        await CompareAsync((older.Info.DocumentId, older.Info.Title, older.Info.FileName), (newer.Info.DocumentId, newer.Info.Title, newer.Info.FileName));
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
        var window = new ImportWindow(_viewModel.DataFolder, [dialog.FileName], previous, _libraries.ShowRegisterAll, _libraries.TagCollapseRank, _libraries.UseTextTitleCandidates, _libraries.DuplicateLooseMatch) { Owner = this };
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

    // ==== 保存した検索(F-16、決定ログ X-3) ====

    private async void OnSaveSearchClick(object sender, RoutedEventArgs e)
    {
        var input = new TextInputWindow("検索条件の保存", "名前(例:上野エリアの売上報告)", "") { Owner = this };
        input.ShowDialog();
        if (input.Result is { } name)
        {
            await _viewModel.SaveSearchAsync(name);
        }
    }

    private static SavedSearch? SavedSearchOf(object sender) => (sender as FrameworkElement)?.DataContext as SavedSearch;

    private async void OnApplySavedSearchClick(object sender, RoutedEventArgs e)
    {
        if (SavedSearchOf(sender) is { } saved)
        {
            await _viewModel.ApplySavedSearchAsync(saved);
        }
    }

    private async void OnRenameSavedSearchClick(object sender, RoutedEventArgs e)
    {
        if (SavedSearchOf(sender) is not { } saved)
        {
            return;
        }
        var input = new TextInputWindow("保存した検索の名前の変更", "新しい名前", saved.Name) { Owner = this };
        input.ShowDialog();
        if (input.Result is { } name)
        {
            await _viewModel.RenameSavedSearchAsync(saved, name);
        }
    }

    private async void OnMoveSavedSearchUpClick(object sender, RoutedEventArgs e)
    {
        if (SavedSearchOf(sender) is { } saved)
        {
            await _viewModel.MoveSavedSearchAsync(saved, -1);
        }
    }

    private async void OnMoveSavedSearchDownClick(object sender, RoutedEventArgs e)
    {
        if (SavedSearchOf(sender) is { } saved)
        {
            await _viewModel.MoveSavedSearchAsync(saved, 1);
        }
    }

    private async void OnDeleteSavedSearchClick(object sender, RoutedEventArgs e)
    {
        if (SavedSearchOf(sender) is { } saved)
        {
            await _viewModel.DeleteSavedSearchAsync(saved);
        }
    }

    // ==== 続き・関連(決定ログ V-1〜V-3) ====

    private async Task PickAndLinkAsync(string kind, string title, string guide)
    {
        if (!await _viewModel.ResolvePendingChangesAsync() || _viewModel.SingleDetail is not { } current)
        {
            return;
        }
        var context = new PickerContext(current.Title, kind switch { "next" => 1, "previous" => -1, _ => 0 }, _viewModel.FirstRankTagsOfCurrent());
        var picker = new DocumentPickerWindow(title, guide.Replace("{0}", current.Title), _viewModel.LinkCandidates(forChain: kind != "related"), context) { Owner = this };
        picker.ShowDialog();
        if (picker.Selected is { } selected && await _viewModel.ConfirmChainCutAsync(kind, selected.Id))
        {
            await _viewModel.LinkAsync(kind, selected.Id);
            SelectDocument(current.Id);
        }
    }

    private async void OnLinkPreviousClick(object sender, RoutedEventArgs e) =>
        await PickAndLinkAsync("previous", "前をつなぐ", "「{0}」の前(前編など)になる PDF を選んでください。別の続きに入っている PDF を選ぶと、その PDF と、それより前をまとめてつなぎます。");

    private async void OnLinkNextClick(object sender, RoutedEventArgs e) =>
        await PickAndLinkAsync("next", "次をつなぐ", "「{0}」の次(後編など)になる PDF を選んでください。別の続きに入っている PDF を選ぶと、その PDF と、それより後ろをまとめてつなぎます。");

    private async void OnAddRelatedClick(object sender, RoutedEventArgs e) =>
        await PickAndLinkAsync("related", "関連を追加", "「{0}」に関連する PDF を選んでください(順番はありません)。");

    private void OnOpenLinkClick(object sender, RoutedEventArgs e)
    {
        var id = (string?)((FrameworkElement)sender).Tag;
        if (_viewModel.ChainRows.Concat(_viewModel.RelatedRows).FirstOrDefault(r => r.Id == id) is { } row)
        {
            OpenInViewer([(row.Id, row.Document.Title, row.Document.FileName)]);
        }
    }

    private async void OnSelectLinkClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string id && await _viewModel.ResolvePendingChangesAsync())
        {
            SelectDocument(id);
        }
    }

    private async void OnRemoveChainClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string id && _viewModel.SingleDetail is { } current && await _viewModel.ResolvePendingChangesAsync())
        {
            await _viewModel.UnlinkAsync(id, fromChain: true);
            SelectDocument(current.Id);
        }
    }

    private async void OnRemoveRelatedClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string id && _viewModel.SingleDetail is { } current && await _viewModel.ResolvePendingChangesAsync())
        {
            await _viewModel.UnlinkAsync(id, fromChain: false);
            SelectDocument(current.Id);
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
        if (await _viewModel.ResolvePendingChangesAsync())
        {
            await MoveDocumentsAsync(ids, targetId, this);
        }
    }

    // 「保存先を移す」と退避の画面(決定ログ EA-1)で共通:確認・空き容量の確認をして、進み具合を出しながら移す。移したら true
    private async Task<bool> MoveDocumentsAsync(IReadOnlyList<string> ids, string? targetId, Window owner)
    {
        if (await _viewModel.PrepareMoveAsync(ids, targetId) is not { } plan)
        {
            return false;
        }
        _viewer?.CloseDocuments(plan.DocumentIds);
        var progress = new ProgressWindow($"「{_storage.Storage.StorageName(targetId)}」へ移しています") { Owner = owner };
        var task = _storage.RunAsync(s => s.Move(plan, progress.Progress, progress.Cancellation));
        progress.ShowUntil(task);
        var result = await task;
        await _viewModel.ReportMoveAsync(result, targetId);
        return result.Moved > 0;
    }

    // 決定ログ EA-7: 退避の画面(設定画面・分析画面・起動時のお知らせから開く)
    public async void OpenArchive()
    {
        if (!await _viewModel.ResolvePendingChangesAsync())
        {
            return;
        }
        await _storage.RefreshAsync();
        new ArchiveWindow(_viewModel.DataFolder, _storage, MoveDocumentsAsync) { Owner = this }.ShowDialog();
    }

    // 決定ログ EA-6: メインのドライブの空きが少なくなっていたら、起動時に知らせて退避の画面を開けるようにする
    // ==== ほかの PC で変わったサブ(決定ログ EB-2・EB-3) ====

    private readonly HashSet<string> _askedPulls = [];
    private bool _askingPulls;

    // 開いている画面(設定画面など)の上に出す
    private IUserDialogs ActiveDialogs() =>
        new WpfUiDialogs(Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible) ?? this);

    private async Task OnSubsPulledAsync(IReadOnlyList<SubPullReport> reports)
    {
        _viewModel.Refresh();
        var lines = reports.Select(r =>
        {
            var removed = r.Plan.RemovedTitles.Count == 0 ? ""
                : "\n  一覧から外した PDF(ほかの PC で移した・削除したもの):" + string.Join("、", r.Plan.RemovedTitles.Take(10))
                  + (r.Plan.RemovedTitles.Count > 10 ? $" ほか {r.Plan.RemovedTitles.Count - 10} 件" : "");
            return $"・「{r.SubName}」:{r.Plan.Summary}{removed}";
        });
        await ActiveDialogs().ShowErrorAsync("ほかの PC で変更されたサブライブラリの内容を読み込みました。\n\n" + string.Join("\n", lines), "サブライブラリを読み込みました");
    }

    // この PC の書き込めていない変更が消えるときは、読み込む前に確かめる(「あとで」にしたものは、このあと聞かない)
    private async Task AskPendingPullsAsync()
    {
        if (_askingPulls)
        {
            return;
        }
        _askingPulls = true;
        try
        {
            foreach (var sub in _storage.Storage.Subs.Where(s => s.PendingPull is not null && _askedPulls.Add(s.Id)).ToList())
            {
                var plan = sub.PendingPull!;
                var lost = string.Join("\n", plan.LostTitles.Take(10).Select(t => "・" + t)) + (plan.LostChanges > 10 ? $"\nほか {plan.LostChanges - 10} 件" : "");
                if (!await ActiveDialogs().ConfirmAsync("ほかの PC で変更されたサブライブラリ",
                        $"サブライブラリ「{sub.Name}」は、ほかの PC で変更されています({plan.Summary})。\n\n" +
                        $"読み込むと、この PC でまだサブへ書き込めていない次の変更が消え、サブの内容に合わせます。\n{lost}\n\n" +
                        "読み込みますか?\n(あとで設定画面の「ライブラリ」の「読み込む」でも読み込めます。それまで、このサブの PDF はこの PC では表示のみです)",
                        "読み込む"))
                {
                    continue;
                }
                await PullSubAsync(sub.Id);
            }
        }
        finally
        {
            _askingPulls = false;
        }
    }

    public async Task PullSubAsync(string subId)
    {
        try
        {
            await _storage.RunAsync(s => s.PullSub(subId));
        }
        catch (Exception ex) when (ex is LibraryException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            await ActiveDialogs().ShowErrorAsync($"読み込めませんでした。\n\n{ex.Message}", "サブライブラリ");
        }
    }

    private async Task RemindArchiveIfLowAsync()
    {
        var percent = _libraries.ArchiveAlertPercent;
        if (!PdfManager.Core.Library.ArchivePlanner.IsSpaceLow(_viewModel.DataFolder.Root, percent)
            || PdfManager.Core.Library.ArchivePlanner.DriveSpace(_viewModel.DataFolder.Root) is not var (free, total))
        {
            return;
        }
        if (await new WpfUiDialogs(this).ConfirmAsync("ドライブの空きが少なくなっています",
                $"ライブラリのあるドライブの空きが {free * 100.0 / total:0}%({ViewModels.ArchiveViewModel.FormatSize(free)})になりました({percent}% を切っています)。\n\n" +
                "長く開いていない PDF を、外付け HDD などのサブライブラリへ移すと空きが増えます。退避の画面を開きますか?\n\n(この知らせは、設定画面の「サブライブラリ」で変えたり、止めたりできます)",
                "退避の画面を開く"))
        {
            OpenArchive();
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
        var dialog = new OpenFileDialog { Title = "取り込むファイルを選択", Filter = ViewModels.ImportViewModel.FileDialogFilter, Multiselect = true };
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
        new ImportWindow(_viewModel.DataFolder, paths, showRegisterAll: _libraries.ShowRegisterAll, tagCollapseRank: _libraries.TagCollapseRank,
            useTextTitleCandidates: _libraries.UseTextTitleCandidates, duplicateLoose: _libraries.DuplicateLooseMatch) { Owner = this }.ShowDialog();
        _viewModel.Refresh();
    }

    // ==== 受け渡しを取り込む(F-52、決定ログ EB-9〜EB-11) ====

    private void OnImportMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnHandoffFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = $"受け渡しのフォルダ(「{Core.Export.HandoffFormat.OutputPrefix}…」)を選択" };
        if (dialog.ShowDialog(this) == true)
        {
            OpenHandoffImport(dialog.FolderName);
        }
    }

    private void OnHandoffZipClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "受け渡しの ZIP を選択", Filter = "ZIP ファイル (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) == true)
        {
            OpenHandoffImport(dialog.FileName);
        }
    }

    public async void OpenHandoffImport(string path)
    {
        if (!await _viewModel.ResolvePendingChangesAsync())
        {
            return;
        }
        var dialogs = new WpfUiDialogs(this);
        Core.Export.HandoffPackage package;
        Core.Export.HandoffImportPlan plan;
        try
        {
            package = await Task.Run(() => Core.Export.HandoffPackage.Open(path));
        }
        catch (Exception ex) when (ex is LibraryException or IOException or UnauthorizedAccessException)
        {
            await dialogs.ShowErrorAsync(ex.Message, "受け渡しを取り込む");
            return;
        }
        using (package)
        {
            plan = await _storage.RunAsync(s => Core.Export.HandoffImporter.Plan(s, package));
            if (plan.NewDocuments.Count == 0 && plan.Different.Count == 0)
            {
                await dialogs.ShowErrorAsync($"取り込むものはありません(受け渡しの {package.Manifest.Documents.Count:N0} 件は、すべてこの PC に同じ情報であります)。", "受け渡しを取り込む");
                return;
            }
            var window = new HandoffImportWindow(package, plan) { Owner = this };
            if (window.ShowDialog() != true)
            {
                return;
            }
            var choices = window.Choices;
            var progress = new ProgressWindow("受け渡しを取り込んでいます") { Owner = this };
            var task = _storage.RunAsync(s => Core.Export.HandoffImporter.ImportAsync(s, package, plan, choices, DateTimeOffset.Now, progress.Progress, progress.Cancellation)
                .GetAwaiter().GetResult());
            progress.ShowUntil(task);
            Core.Export.HandoffImportResult result;
            try
            {
                result = await task;
            }
            catch (Exception ex) when (ex is LibraryException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                _viewModel.Refresh();
                await dialogs.ShowErrorAsync($"取り込めませんでした。それまでに取り込んだ分は残っています。\n\n{ex.Message}", "受け渡しを取り込む");
                return;
            }
            _viewModel.Refresh();
            var message = (result.Cancelled ? "途中で止めました。" : "") + $"新しく {result.Added:N0} 件を登録し、{result.Updated:N0} 件の情報を直しました。"
                + (result.Problems.Count > 0 ? $"\n\n次のものは取り込めませんでした。\n" + string.Join("\n", result.Problems.Take(8)) + (result.Problems.Count > 8 ? $"\nほか {result.Problems.Count - 8} 件" : "") : "");
            await dialogs.ShowErrorAsync(message, "受け渡しを取り込みました");
        }
    }

    // 決定ログ DD-6: 保留の PDF だけを取り込み画面で開く(ファイルは選ばない)
    private void OnHeldClick(object sender, RoutedEventArgs e) => OpenImportWindow([]);

    // 決定ログ DD-7: 削除予定の確認画面
    private void OnDeleteReviewClick(object sender, RoutedEventArgs e)
    {
        new DeleteReviewWindow(_viewModel.DataFolder, _libraries.DuplicateLooseMatch) { Owner = this }.ShowDialog();
        _viewModel.UpdateSetAsideCounts();
    }

    // 決定ログ DH-6: 重複チェックの画面。ビューアで見比べられるよう、メイン画面を操作できる状態で開く(1 つだけ)
    private DuplicateCheckWindow? _duplicateCheck;

    private async void OnDuplicateCheckClick(object sender, RoutedEventArgs e)
    {
        if (_duplicateCheck is not null)
        {
            _duplicateCheck.Activate();
            return;
        }
        if (!await _viewModel.ResolvePendingChangesAsync())
        {
            return;
        }
        _duplicateCheck = new DuplicateCheckWindow(_viewModel.DataFolder, _libraries.DuplicateLooseMatch, CompareAsync,
            ids => _viewModel.DeleteDocumentsAsync(ids, closing => _viewer?.CloseDocuments(closing))) { Owner = this };
        _duplicateCheck.Closed += (_, _) => _duplicateCheck = null;
        _duplicateCheck.Show();
    }

    // 決定ログ DA-6・DA-9: 分析の画面。PDF を開いたり検索したりできるよう、メイン画面を操作できる状態で開く(1 つだけ)
    private AnalyticsWindow? _analytics;

    private void OnAnalyticsClick(object sender, RoutedEventArgs e)
    {
        if (_analytics is not null)
        {
            _analytics.Activate();
            return;
        }
        _analytics = new AnalyticsWindow(_viewModel.DataFolder, OpenFromAnalytics, (keywords, tag) =>
        {
            _viewModel.SearchFromAnalytics(keywords, tag);
            Activate();
        }, () =>
        {
            Activate();
            OpenArchive();
        }) { Owner = this };
        _analytics.Closed += (_, _) => _analytics = null;
        _analytics.Show();
    }

    private void OpenFromAnalytics(string documentId)
    {
        using var connection = PdfManager.Core.Database.PdfManagerDatabase.Open(_viewModel.DataFolder.DatabasePath);
        if (new PdfManager.Core.Documents.DocumentRepository(connection).GetDetails([documentId]).FirstOrDefault() is { } detail)
        {
            OpenInViewer([(detail.Id, detail.Title, detail.FileName)]);
        }
    }

    // 決定ログ DM-1: タグの一括付与の画面。右クリックから開いたときは、選んでいる PDF にチェックを付けて始める
    private void OnTagBulkClick(object sender, RoutedEventArgs e) => OpenTagBulk([]);

    private void OnTagBulkSelectedClick(object sender, RoutedEventArgs e) =>
        OpenTagBulk(DocumentList.SelectedItems.Cast<DocumentCard>().Select(c => c.Entry.Id).ToList());

    private async void OpenTagBulk(IReadOnlyList<string> selectedIds)
    {
        if (!await _viewModel.ResolvePendingChangesAsync())
        {
            return;
        }
        var window = new TagBulkWindow(_viewModel.DataFolder, _storage, selectedIds, _libraries.TagCollapseRank) { Owner = this };
        window.ShowDialog();
        if (window.Saved)
        {
            _viewModel.Refresh();
        }
    }

    // 決定ログ DI-1: タイトルの付け直しの画面
    private async void OnTitleBatchClick(object sender, RoutedEventArgs e)
    {
        if (!await _viewModel.ResolvePendingChangesAsync())
        {
            return;
        }
        var window = new TitleBatchWindow(_viewModel.DataFolder, _storage) { Owner = this };
        window.ShowDialog();
        if (window.Saved)
        {
            _viewModel.Refresh();
        }
    }

    // ==== USB への出力(F-50、決定ログ CC-1〜CC-9) ====

    private void OnExportSelectedClick(object sender, RoutedEventArgs e) => OpenExport(preferSelected: true);

    private void OnExportResultsClick(object sender, RoutedEventArgs e) => OpenExport(preferSelected: false);

    private async void OpenExport(bool preferSelected)
    {
        if (!await _viewModel.ResolvePendingChangesAsync())
        {
            return;
        }
        // 決定ログ CC-7: 一覧に出ているもの(画面の並び)だけを出す
        var results = _viewModel.Results.Select(c => c.Entry.Id).ToList();
        var selectedSet = DocumentList.SelectedItems.Cast<DocumentCard>().Select(c => c.Entry.Id).ToHashSet();
        var selected = results.Where(selectedSet.Contains).ToList();
        IReadOnlyList<Core.Catalog.TagGroup> groups;
        IReadOnlyList<string> changed;
        var lastHandoff = _libraries.LastHandoff(_library.Id);
        using (var connection = Core.Database.PdfManagerDatabase.Open(_viewModel.DataFolder.DatabasePath))
        {
            groups = new Core.Catalog.CatalogRepository(connection).GetGroups();
            changed = Core.Export.HandoffFormat.ChangedSince(connection, lastHandoff);
        }
        var window = new ExportWindow(selected, results, preferSelected, groups, _libraries.ExportFolder, changed, lastHandoff) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }
        _libraries.SetExportFolder(window.Folder);
        var dialogs = new WpfUiDialogs(this);
        var (ids, folder, options) = (window.DocumentIds, window.Folder, window.Options);
        var startedAt = DateTimeOffset.Now;
        var plan = await _storage.RunAsync(s => Core.Export.ExportService.Plan(s, ids, folder, options.Handoff));
        if (plan.Count == 0)
        {
            await dialogs.ShowErrorAsync("出せる PDF がありません(保存先のサブライブラリがつながっていません)。", "USB へ出力");
            return;
        }
        if (!plan.HasEnoughSpace)
        {
            await dialogs.ShowErrorAsync($"出力先の空き容量が足りません。\n出す PDF:{BackupActions.FormatSize(plan.Bytes)}\n空き容量:{BackupActions.FormatSize(plan.FreeBytes ?? 0)}", "USB へ出力");
            return;
        }

        var progress = new ProgressWindow("USB へ出力しています") { Owner = this };
        var task = _storage.RunAsync(s => Core.Export.ExportService.Run(s, ids, folder, options, DateTimeOffset.Now, progress.Progress, progress.Cancellation));
        progress.ShowUntil(task);
        Core.Export.ExportResult result;
        try
        {
            result = await task;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await dialogs.ShowErrorAsync($"出力できませんでした。作りかけのものは消しました。\n\n{ex.Message}", "USB へ出力");
            return;
        }
        if (result.Cancelled)
        {
            await dialogs.ShowErrorAsync("途中で止めました。作りかけのものは消しました。", "USB へ出力");
            return;
        }
        // 決定ログ EB-8: 次の「前回の受け渡しのあと」は、書き出し始めた時から
        if (options.Handoff)
        {
            _libraries.SetLastHandoff(_library.Id, startedAt);
        }
        // 決定ログ CC-8: 出せなかったものは理由と一緒に知らせる
        var message = $"{result.Exported:N0} 件を出力しました。" + (options.Handoff ? "ほかの PC で「取り込み」の「受け渡しを取り込む」で取り込めます。" : "") + $"\n{result.OutputPath}"
            + (result.Skipped.Count > 0 ? $"\n\n次の {result.Skipped.Count:N0} 件は出せませんでした。\n" + string.Join("\n", result.Skipped.Take(5)) + (result.Skipped.Count > 5 ? $"\nほか {result.Skipped.Count - 5:N0} 件" : "") : "");
        if (await dialogs.ConfirmAsync("USB へ出力しました", message, "エクスプローラーで開く"))
        {
            var arguments = options.Zip ? $"/select,\"{result.OutputPath}\"" : $"\"{result.OutputPath}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
    }

    // 決定ログ BC-1: タグの付け漏れ確認。閉じたら一覧と件数を読み直す
    private async void OnTagGapClick(object sender, RoutedEventArgs e)
    {
        if (!await _viewModel.ResolvePendingChangesAsync())
        {
            return;
        }
        var ids = _viewModel.Results.Select(c => c.Entry.Id).ToList();
        new TagGapWindow(_viewModel.DataFolder, _storage, ids, _libraries.TagCollapseRank) { Owner = this }.ShowDialog();
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
        _viewModel.RecordUsage = _libraries.RecordUsage;
        if (settings.SelectedLibrary is { } library)
        {
            App.CurrentApp.SwitchLibrary(library);
            return;
        }
        _viewModel.Refresh();
        if (settings.ArchiveRequested)
        {
            OpenArchive();
        }
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
            // 決定ログ EB-9: 受け渡しのフォルダ・ZIP なら、受け渡しの取り込みにする
            if (paths.FirstOrDefault(Core.Export.HandoffPackage.IsPackage) is { } handoff)
            {
                Dispatcher.BeginInvoke(() => OpenHandoffImport(handoff));
                return;
            }
            // ドロップ元(エクスプローラー)を待たせないよう、ドロップ処理が終わってから画面を開く
            Dispatcher.BeginInvoke(() => OpenImportWindow(paths));
        }
    }
}
