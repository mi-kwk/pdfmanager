using System.ComponentModel;
using System.Windows;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// SC-05 画像から PDF 作成(F-26、決定ログ Y-1〜Y-5)
public partial class ImagePdfWindow : FluentWindow
{
    private readonly ImagePdfViewModel _viewModel;
    private bool _cancelRequested;

    public ImagePdfWindow(IReadOnlyList<string> images, IReadOnlyList<string> zips, string workFolder)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        _viewModel = new ImagePdfViewModel(images, zips, workFolder);
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
        Closing += OnClosing;
        Closed += (_, _) => _viewModel.Cancel();
    }

    // 作った PDF。キャンセルしたときは空
    public IReadOnlyList<CreatedPdf> Created { get; private set; } = [];

    private List<ImagePdfPage> SelectedPages => PageList.SelectedItems.Cast<ImagePdfPage>().ToList();

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => MoveSelected(_viewModel.MoveUp);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => MoveSelected(_viewModel.MoveDown);

    // 動かした後も同じ画像を選んだままにする(続けて押せるように)
    private void MoveSelected(Action<IReadOnlyList<ImagePdfPage>> move)
    {
        var selected = SelectedPages;
        move(selected);
        foreach (var page in selected)
        {
            PageList.SelectedItems.Add(page);
        }
        if (selected.Count > 0)
        {
            PageList.ScrollIntoView(selected[0]);
        }
    }

    private void OnRotateRightClick(object sender, RoutedEventArgs e) => ImagePdfViewModel.Rotate(SelectedPages, 1);

    private void OnRotateLeftClick(object sender, RoutedEventArgs e) => ImagePdfViewModel.Rotate(SelectedPages, -1);

    private void OnExcludeClick(object sender, RoutedEventArgs e) => _viewModel.Exclude(SelectedPages);

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PdfCount == 0)
        {
            await new WpfUiDialogs(this).ShowErrorAsync("PDF にする画像がありません。", "画像から PDF 作成");
            return;
        }
        var problems = await _viewModel.CreateAsync();
        if (_cancelRequested)
        {
            Close();
            return;
        }
        if (problems.Count > 0)
        {
            await new WpfUiDialogs(this).ShowErrorAsync(
                $"次の PDF は作れませんでした。{(_viewModel.Created.Count > 0 ? "作れたものだけを取り込み画面に入れます。" : "")}\n\n" + string.Join("\n", problems.Take(5)),
                "画像から PDF 作成");
        }
        Created = _viewModel.Created;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    // 作成中に閉じるときは、作成を止めてから閉じる
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.IsCreating)
        {
            e.Cancel = true;
            _cancelRequested = true;
            _viewModel.Cancel();
        }
    }
}
