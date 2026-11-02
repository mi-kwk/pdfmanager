using System.IO;
using System.Windows;
using Microsoft.Win32;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Export;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// フォルダ分けの選択肢(null は分けない)
public sealed record ExportSplitChoice(string Label, long? GroupId);

// USB への出力(F-50、決定ログ CC-1〜CC-6)。何を・どこへ・どう出すかを選ぶ
public partial class ExportWindow : FluentWindow
{
    private readonly IReadOnlyList<string> _selectedIds;
    private readonly IReadOnlyList<string> _resultIds;

    // preferSelected:右クリックから開いたときは「選んでいる PDF」、上のボタンからは「今の検索結果すべて」を初めに選ぶ
    public ExportWindow(IReadOnlyList<string> selectedIds, IReadOnlyList<string> resultIds, bool preferSelected,
        IReadOnlyList<TagGroup> groups, string? lastFolder)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        _selectedIds = selectedIds;
        _resultIds = resultIds;
        SelectedChoice.Content = $"選んでいる PDF({selectedIds.Count:N0} 件)";
        ResultsChoice.Content = $"今の検索結果すべて({resultIds.Count:N0} 件)";
        SelectedChoice.IsEnabled = selectedIds.Count > 0;
        (preferSelected && selectedIds.Count > 0 ? SelectedChoice : ResultsChoice).IsChecked = true;
        SplitBox.ItemsSource = new[] { new ExportSplitChoice("分けない", null) }
            .Concat(groups.OrderBy(g => g.DisplayRank).Select(g => new ExportSplitChoice($"「{g.Name}」のタグごと", g.Id)))
            .ToList();
        SplitBox.SelectedIndex = 0;
        FolderBox.Text = lastFolder ?? "";
    }

    public IReadOnlyList<string> DocumentIds { get; private set; } = [];

    public string Folder { get; private set; } = "";

    public ExportOptions Options { get; private set; } = new();

    private void OnChooseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "出力先を選択(USB メモリなど)" };
        if (Directory.Exists(FolderBox.Text))
        {
            dialog.InitialDirectory = FolderBox.Text;
        }
        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            await new WpfUiDialogs(this).ShowErrorAsync("出力先のフォルダを選んでください(USB メモリをつないでいるかも確かめてください)。", "USB へ出力");
            return;
        }
        DocumentIds = SelectedChoice.IsChecked == true ? _selectedIds : _resultIds;
        if (DocumentIds.Count == 0)
        {
            await new WpfUiDialogs(this).ShowErrorAsync("出す PDF がありません。", "USB へ出力");
            return;
        }
        Folder = folder;
        Options = new ExportOptions(ZipFormat.IsChecked == true, ListCheck.IsChecked == true, (SplitBox.SelectedItem as ExportSplitChoice)?.GroupId);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
