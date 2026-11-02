using System.Windows;
using System.Windows.Media;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// 決定ログ DH-4: 取り込み中の PDF と、1 ページ目が似ている登録済みの PDF を並べ、削除予定へ・保留へ・そのまま続けるを選ぶ
public partial class DuplicateResultWindow : FluentWindow
{
    public DuplicateResultWindow(string fileName, ImageSource? preview, IReadOnlyList<DuplicateMatch> matches, bool isHeld)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        NewName.Text = $"取り込み中:{fileName}";
        NewImage.Source = preview;
        MatchList.ItemsSource = matches;
        MatchList.SelectedIndex = 0;
        MessageText.Text = $"同じ第 1 グループのタグの登録済み PDF に、1 ページ目が似ているものが {matches.Count} 件あります。同じ PDF なら「削除予定へ」、確かめたいときは「保留へ」を押してください。";
        // 保留の PDF をもう一度保留へは移さない
        HoldButton.Visibility = isHeld ? Visibility.Collapsed : Visibility.Visible;
    }

    public DuplicateChoice Choice { get; private set; } = DuplicateChoice.Continue;

    private void OnDeleteClick(object sender, RoutedEventArgs e) => CloseWith(DuplicateChoice.Delete);

    private void OnHoldClick(object sender, RoutedEventArgs e) => CloseWith(DuplicateChoice.Hold);

    private void OnContinueClick(object sender, RoutedEventArgs e) => CloseWith(DuplicateChoice.Continue);

    private void CloseWith(DuplicateChoice choice)
    {
        Choice = choice;
        Close();
    }
}
