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
    private readonly IReadOnlyList<string> _changedIds;

    // preferSelected:右クリックから開いたときは「選んでいる PDF」、上のボタンからは「今の検索結果すべて」を初めに選ぶ。
    // changedIds・lastHandoff:この PC で前回受け渡したあとに登録・変更した PDF と、前回の日時(決定ログ EB-8)
    public ExportWindow(IReadOnlyList<string> selectedIds, IReadOnlyList<string> resultIds, bool preferSelected,
        IReadOnlyList<TagGroup> groups, string? lastFolder, IReadOnlyList<string> changedIds, DateTimeOffset? lastHandoff)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        _selectedIds = selectedIds;
        _resultIds = resultIds;
        _changedIds = changedIds;
        SelectedChoice.Content = $"選んでいる PDF({selectedIds.Count:N0} 件)";
        ResultsChoice.Content = $"今の検索結果すべて({resultIds.Count:N0} 件)";
        ChangedChoice.Content = lastHandoff is { } last
            ? $"この PC で前回受け渡した {last.ToLocalTime():yyyy/MM/dd HH:mm} のあとに登録・変更した PDF({changedIds.Count:N0} 件)"
            : $"この PC で前回受け渡したあとに登録・変更した PDF(まだ受け渡していないため、すべて {changedIds.Count:N0} 件)";
        SelectedChoice.IsEnabled = selectedIds.Count > 0;
        (preferSelected && selectedIds.Count > 0 ? SelectedChoice : ResultsChoice).IsChecked = true;
        SplitBox.ItemsSource = new[] { new ExportSplitChoice("分けない", null) }
            .Concat(groups.OrderBy(g => g.DisplayRank).Select(g => new ExportSplitChoice($"「{g.Name}」のタグごと", g.Id)))
            .ToList();
        SplitBox.SelectedIndex = 0;
        FolderBox.Text = lastFolder ?? "";
        // 決定ログ HA-8: 初めはオフ。開発用の実行(exe 1 つで動かないもの)では入れられない
        IncludeExeCheck.IsEnabled = AppUpdater.StandaloneExePath is not null;
        IncludeExeNote.Text = AppUpdater.StandaloneExePath is not null
            ? $"バージョン {AppUpdater.CurrentVersionText}(約 {new FileInfo(AppUpdater.StandaloneExePath).Length >> 20:N0} MB)。相手の PC では、設定画面の「新しいバージョンに更新」でこの exe を選ぶと更新できます。"
            : "開発用の実行のため、入れられません。";
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
        var handoff = HandoffCheck.IsChecked == true;
        DocumentIds = handoff && ChangedChoice.IsChecked == true ? _changedIds : SelectedChoice.IsChecked == true ? _selectedIds : _resultIds;
        if (DocumentIds.Count == 0)
        {
            await new WpfUiDialogs(this).ShowErrorAsync("出す PDF がありません。", "USB へ出力");
            return;
        }
        Folder = folder;
        Options = new ExportOptions(ZipFormat.IsChecked == true, ListCheck.IsChecked == true,
            handoff ? null : (SplitBox.SelectedItem as ExportSplitChoice)?.GroupId, Handoff: handoff,
            IncludeExePath: IncludeExeCheck.IsChecked == true ? AppUpdater.StandaloneExePath : null);
        DialogResult = true;
    }

    // 決定ログ EB-7・EB-8: 受け渡しのときはフォルダ分けをせず、「前回の受け渡しのあと」を選べる
    private void OnHandoffChanged(object sender, RoutedEventArgs e)
    {
        var handoff = HandoffCheck.IsChecked == true;
        SplitBox.IsEnabled = !handoff;
        ChangedChoice.IsEnabled = handoff;
        if (!handoff && ChangedChoice.IsChecked == true)
        {
            (SelectedChoice.IsEnabled ? SelectedChoice : ResultsChoice).IsChecked = true;
        }
        Title = handoff ? "受け渡し(USB へ出力)" : "USB へ出力";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
