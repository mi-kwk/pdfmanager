using System.Windows;
using PdfManager.App.Services;
using PdfManager.Core.Search;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// 文書選択の画面 1 行分
public sealed record PickerItem(DocumentEntry Entry, string SearchText)
{
    public string Title => Entry.Title;

    public string Sub => string.Join(" ・ ", new[]
    {
        Entry.FileName,
        Entry.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd"),
        Entry.TagNames.Count == 0 ? "" : string.Join("、", Entry.TagNames),
        Entry.VersionText,
    }.Where(s => s.Length > 0));
}

// SC-07 文書選択(決定ログ V-2・V-3)。続き・関連の相手を検索して 1 つ選ぶ
public partial class DocumentPickerWindow : FluentWindow
{
    private readonly IReadOnlyList<PickerItem> _all;

    public DocumentPickerWindow(string title, string guide, IReadOnlyList<DocumentEntry> candidates)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        Title = title;
        TitleBarControl.Title = title;
        GuideText.Text = guide;
        _all = candidates
            .Select(e => new PickerItem(e, DocumentSearchIndex.Normalize(string.Join('\n', new[] { e.Title, e.FileName }.Concat(e.TagNames)))))
            .ToList();
        ApplyFilter();
        Loaded += (_, _) => SearchBox.Focus();
    }

    // 選んだ文書。選ばなければ null
    public DocumentEntry? Selected { get; private set; }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var terms = DocumentSearchIndex.Normalize(SearchBox.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var items = _all.Where(i => terms.All(t => i.SearchText.Contains(t, StringComparison.Ordinal))).ToList();
        CandidateList.ItemsSource = items;
        CountText.Text = $"{items.Count:N0} 件";
    }

    private void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OnOkClick(sender, e);

    private async void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is not PickerItem item)
        {
            await new WpfUiDialogs(this).ShowErrorAsync("一覧から文書を選んでください。", "文書の選択");
            return;
        }
        Selected = item.Entry;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
