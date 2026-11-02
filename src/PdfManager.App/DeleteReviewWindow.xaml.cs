using System.Windows;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// SC-15 削除予定の確認(決定ログ DD-7・DD-8)
public partial class DeleteReviewWindow : FluentWindow
{
    private readonly DeleteReviewViewModel _viewModel;

    public DeleteReviewWindow(DataFolder dataFolder, bool duplicateLoose = false)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        _viewModel = new DeleteReviewViewModel(dataFolder, new WpfUiDialogs(this), duplicateLoose);
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.Load();
    }

    // 何か消した・保留へ戻したか
    public bool Changed => _viewModel.Changed;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
