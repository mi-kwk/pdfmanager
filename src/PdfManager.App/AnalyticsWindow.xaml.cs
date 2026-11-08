using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Analytics;
using PdfManager.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// SC-22 分析(決定ログ DA-6・DA-9)。メイン画面を操作できる状態で開き、PDF を開く・キーワードやタグで検索するのはメイン画面で行う
public partial class AnalyticsWindow : FluentWindow
{
    private readonly AnalyticsViewModel _viewModel;
    private readonly Action<string> _open;
    private readonly Action<string?, string?> _search;
    private readonly Action _openArchive;

    public AnalyticsWindow(DataFolder dataFolder, Action<string> open, Action<string?, string?> search, Action openArchive)
    {
        _openArchive = openArchive;
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        _open = open;
        _search = search;
        _viewModel = new AnalyticsViewModel(dataFolder);
        DataContext = _viewModel;
        ShowUnit();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnalyticsViewModel.Unit))
            {
                ShowUnit();
            }
        };
        // ほかの画面で開いたり検索したりした分を、前に戻ったときに数え直す
        Activated += (_, _) => _viewModel.Reload();
    }

    private void ShowUnit()
    {
        (_viewModel.Unit switch { TrendUnit.Week => WeekRadio, TrendUnit.Month => MonthRadio, _ => DayRadio }).IsChecked = true;
    }

    private void OnUnitChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TrendUnit unit })
        {
            _viewModel.Unit = unit;
        }
    }

    // 決定ログ DA-9: ランキングの PDF をダブルクリックで開く
    private void OnDocumentDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: RankedDocument document })
        {
            _open(document.DocumentId);
        }
    }

    // 決定ログ DA-9: キーワード・タグを押すと、メイン画面でその条件で検索する
    private void OnKeywordSelected(object sender, SelectionChangedEventArgs e) => SearchWith(sender, keyword: true);

    private void OnTagSelected(object sender, SelectionChangedEventArgs e) => SearchWith(sender, keyword: false);

    private void SearchWith(object sender, bool keyword)
    {
        if (sender is ListBox { SelectedItem: RankedText text } list)
        {
            _search(keyword ? text.Text : null, keyword ? null : text.Text);
            list.SelectedItem = null;
        }
    }

    private void OnTagStatSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: TagStatRow row } list)
        {
            _search(null, row.Name);
            list.SelectedItem = null;
        }
    }

    // 決定ログ EA-7: 長く開いていない PDF を外付け HDD へ移す画面を、メイン画面から開く
    private void OnArchiveClick(object sender, RoutedEventArgs e) => _openArchive();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
