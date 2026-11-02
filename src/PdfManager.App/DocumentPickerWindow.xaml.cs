using System.Windows;
using PdfManager.App.Services;
using PdfManager.Core.Search;
using PdfManager.Core.Text;
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

// 選ぶ画面の並べ方と絞り込みの元になる、今の文書の情報(決定ログ DK-2・DK-3)。
// Direction は 1 = 次をつなぐ、-1 = 前をつなぐ、0 = 関連。FirstRankTags は今の文書の第 1 グループのタグ(ID と名前)
public sealed record PickerContext(string CurrentTitle, int Direction, IReadOnlyList<(long Id, string Name)> FirstRankTags);

// SC-07 文書選択(決定ログ V-2・V-3・DK-1〜DK-3)。続き・関連の相手を検索して 1 つ選ぶ
public partial class DocumentPickerWindow : FluentWindow
{
    private readonly IReadOnlyList<PickerItem> _all;
    private readonly PickerContext _context;
    private readonly HashSet<long> _firstRankTagIds;
    private bool _loaded;

    public DocumentPickerWindow(string title, string guide, IReadOnlyList<DocumentEntry> candidates, PickerContext context)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        Title = title;
        TitleBarControl.Title = title;
        GuideText.Text = guide;
        _context = context;
        _firstRankTagIds = context.FirstRankTags.Select(t => t.Id).ToHashSet();
        _all = Order(candidates)
            .Select(e => new PickerItem(e, DocumentSearchIndex.Normalize(string.Join('\n', new[] { e.Title, e.FileName }.Concat(e.TagNames)))))
            .ToList();
        // 今の文書に第 1 グループのタグがなければ、初めから「すべて」
        SameTagBox.Content = context.FirstRankTags.Count == 0
            ? "第 1 グループのタグが同じものだけ(この PDF には第 1 グループのタグがありません)"
            : $"第 1 グループのタグが同じものだけ({string.Join("・", context.FirstRankTags.Select(t => t.Name))})";
        SameTagBox.IsEnabled = context.FirstRankTags.Count > 0;
        SameTagBox.IsChecked = context.FirstRankTags.Count > 0;
        _loaded = true;
        ApplyFilter();
        Loaded += (_, _) => SearchBox.Focus();
    }

    // 選んだ文書。選ばなければ null
    public DocumentEntry? Selected { get; private set; }

    private void OnSearchChanged(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            ApplyFilter();
        }
    }

    // 並びは初めに決めておき、検索・絞り込みではそのまま使う(決定ログ DK-3: 初めの表示・検索したときとも同じ並び)
    private void ApplyFilter()
    {
        var terms = DocumentSearchIndex.Normalize(SearchBox.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sameTag = SameTagBox.IsChecked == true;
        var items = _all
            .Where(i => !sameTag || i.Entry.TagIds.Any(_firstRankTagIds.Contains))
            .Where(i => terms.All(t => i.SearchText.Contains(t, StringComparison.Ordinal)))
            .ToList();
        CandidateList.ItemsSource = items;
        CountText.Text = $"{items.Count:N0} 件";
        if (items.Count > 0)
        {
            CandidateList.SelectedIndex = 0;
            CandidateList.ScrollIntoView(items[0]);
        }
    }

    // 決定ログ DK-3: 同じシリーズ(末尾の番号の前が同じタイトル)を一番上に。次をつなぐなら今の番号の次から、前をつなぐなら前から。
    // その下はタイトルの先頭からの一致が長い順、その次は登録日の新しい順
    private IEnumerable<DocumentEntry> Order(IReadOnlyList<DocumentEntry> candidates)
    {
        var series = TitleNumbers.SeriesKey(_context.CurrentTitle);
        var current = NumberOf(_context.CurrentTitle);
        var folded = NameNormalizer.Fold(_context.CurrentTitle);

        (int Group, long Key) SeriesRank(DocumentEntry entry)
        {
            var number = NumberOf(entry.Title);
            if (current < 0 || number < 0)
            {
                return (1, number);
            }
            return _context.Direction switch
            {
                > 0 => number > current ? (0, number - current) : (1, current - number),
                < 0 => number < current ? (0, current - number) : (1, number - current),
                _ => (0, Math.Abs(number - current)),
            };
        }

        return candidates
            .Select(e => (Entry: e, SameSeries: TitleNumbers.SeriesKey(e.Title) == series))
            .OrderByDescending(x => x.SameSeries)
            .ThenBy(x => x.SameSeries ? SeriesRank(x.Entry).Group : 0)
            .ThenBy(x => x.SameSeries ? SeriesRank(x.Entry).Key : 0)
            .ThenByDescending(x => x.SameSeries ? 0 : CommonPrefix(folded, NameNormalizer.Fold(x.Entry.Title)))
            .ThenByDescending(x => x.Entry.CreatedAt)
            .Select(x => x.Entry);
    }

    private static long NumberOf(string title) => TitleNumbers.SplitTrailingNumber(title) is var (_, digits) ? TitleNumbers.Value(digits) : -1;

    private static int CommonPrefix(string a, string b)
    {
        var length = 0;
        while (length < a.Length && length < b.Length && a[length] == b[length])
        {
            length++;
        }
        return length;
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
