using System.ComponentModel;
using System.Windows;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// SC-12 タグの付け漏れ確認(決定ログ BC-1〜BC-8)
public partial class TagGapWindow : FluentWindow
{
    private readonly TagGapViewModel _viewModel;
    private bool _restoring;
    private bool _closeConfirmed;
    private int? _nextIndex;

    public TagGapWindow(DataFolder dataFolder, StorageAccess storage, IReadOnlyList<string> mainResultIds, int tagCollapseRank)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        // 一覧を作り直している間の選択の変化は、利用者の操作ではないので無視する
        _restoring = true;
        _viewModel = new TagGapViewModel(dataFolder, storage, new WpfUiDialogs(this), mainResultIds, tagCollapseRank);
        _restoring = false;
        _viewModel.RowsReplacing += () => _restoring = true;
        _viewModel.RowsReplaced += OnRowsReplaced;
        _viewModel.Saved += () => _nextIndex = RowList.SelectedItems.Count == 0 ? null : RowList.SelectedItems.Cast<TagGapRow>().Min(r => _viewModel.Rows.IndexOf(r));
        DataContext = _viewModel;
        Loaded += (_, _) =>
        {
            if (_viewModel.Rows.Count > 0)
            {
                RowList.SelectedIndex = 0;
            }
        };
        Closing += OnClosing;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private List<TagGapRow> SelectedRows => RowList.SelectedItems.Cast<TagGapRow>().ToList();

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_restoring)
        {
            return;
        }
        var rows = SelectedRows;
        if (!_viewModel.HasChanges)
        {
            _viewModel.SetSelection(rows);
            return;
        }
        // 決定ログ J-1・BC-7: 保存していない変更があれば、確かめてから選び直す(確認の画面は選択の処理が終わってから開く)
        var ids = rows.Select(r => r.Entry.Id).ToHashSet();
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (await _viewModel.ResolvePendingChangesAsync())
            {
                Select(_viewModel.Rows.Where(r => ids.Contains(r.Entry.Id)).ToList());
                _viewModel.SetSelection(SelectedRows);
            }
            else
            {
                Select(_viewModel.Rows.Where(r => _viewModel.SelectedIds.Contains(r.Entry.Id)).ToList());
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // 一覧を作り直したあと:保存した直後は次の PDF を、それ以外は同じ PDF を選び直す
    private void OnRowsReplaced(IReadOnlyList<TagGapRow> kept)
    {
        if (_nextIndex is { } index)
        {
            _nextIndex = null;
            Select(_viewModel.Rows.Count == 0 ? [] : [_viewModel.Rows[Math.Min(index, _viewModel.Rows.Count - 1)]]);
            _viewModel.SetSelection(SelectedRows);
            return;
        }
        // 編集中の内容は消さない(条件を変えて一覧から外れても、入力欄はそのまま保存できる)
        if (_viewModel.HasChanges)
        {
            Select(kept);
            return;
        }
        // 選んでいた PDF が一覧から外れたら、先頭の PDF を選ぶ(取り込み画面と同じ)
        Select(kept.Count > 0 || _viewModel.Rows.Count == 0 ? kept : [_viewModel.Rows[0]]);
        _viewModel.SetSelection(SelectedRows);
    }

    private void Select(IReadOnlyList<TagGapRow> rows)
    {
        _restoring = true;
        try
        {
            RowList.SelectedItems.Clear();
            foreach (var row in rows)
            {
                RowList.SelectedItems.Add(row);
            }
            if (rows.Count > 0)
            {
                RowList.ScrollIntoView(rows[0]);
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

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
}
