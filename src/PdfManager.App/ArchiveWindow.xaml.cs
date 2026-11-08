using System.Windows;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// SC-23 退避(決定ログ EA-1〜EA-8)。移すのはメイン画面の「保存先を移す」と同じ処理(確認・空き容量・進み具合・版のまとまり)
public partial class ArchiveWindow : FluentWindow
{
    private readonly ArchiveViewModel _viewModel;
    private readonly IUserDialogs _dialogs;
    private readonly Func<IReadOnlyList<string>, string?, Window, Task<bool>> _move;

    public ArchiveWindow(DataFolder dataFolder, StorageAccess storage, Func<IReadOnlyList<string>, string?, Window, Task<bool>> move)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        _dialogs = new WpfUiDialogs(this);
        _move = move;
        _viewModel = new ArchiveViewModel(dataFolder, storage);
        DataContext = _viewModel;
    }

    // 一度でも移したか(メイン画面を読み直すため)
    public bool Moved { get; private set; }

    private async void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTarget is not { } target)
        {
            await _dialogs.ShowErrorAsync(_viewModel.TargetNote.Length > 0 ? _viewModel.TargetNote : "移す先を選んでください。", "退避");
            return;
        }
        await MoveAsync(_viewModel.CheckedCandidateIds, target.Id, "移す PDF にチェックを付けてください。");
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e) =>
        await MoveAsync(_viewModel.CheckedFrequentIds, null, "メインへ戻す PDF にチェックを付けてください。");

    private async Task MoveAsync(IReadOnlyList<string> ids, string? targetId, string emptyMessage)
    {
        if (ids.Count == 0)
        {
            await _dialogs.ShowErrorAsync(emptyMessage, "退避");
            return;
        }
        if (await _move(ids, targetId, this))
        {
            Moved = true;
        }
        _viewModel.Load();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
