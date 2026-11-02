using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using PdfManager.App.Services;
using PdfManager.Core.Search;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// タグを複数選ぶ(登録・タグ設定の画面の「紐づけを変更」、決定ログ DB-2)
public partial class TagPickWindow : FluentWindow
{
    private readonly List<Row> _rows;
    private readonly ICollectionView _view;

    public TagPickWindow(string title, string message, IReadOnlyList<TagPick> choices, IReadOnlyCollection<string> selectedKeys)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        Title = title;
        TitleBarControl.Title = title;
        MessageText.Text = message;
        // 選んでいるものを先に
        _rows = choices.Select(c => new Row(c, selectedKeys.Contains(c.Key)))
            .OrderByDescending(r => r.IsChecked)
            .ToList();
        _view = CollectionViewSource.GetDefaultView(_rows);
        ChoiceList.ItemsSource = _view;
        Loaded += (_, _) => FilterBox.Focus();
    }

    // OK を押したときに選んでいたキー。キャンセルなら null
    public IReadOnlyList<string>? Result { get; private set; }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        var filter = DocumentSearchIndex.Normalize(FilterBox.Text.Trim());
        // 選んでいるものは絞り込んでも残す(見えないまま外れないように)
        _view.Filter = filter.Length == 0 ? null : o => o is Row row && (row.IsChecked || DocumentSearchIndex.Normalize(row.Name).Contains(filter, StringComparison.Ordinal));
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Result = _rows.Where(r => r.IsChecked).Select(r => r.Key).ToList();
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private sealed class Row(TagPick pick, bool isChecked)
    {
        public string Key { get; } = pick.Key;

        public string Name { get; } = pick.Name;

        public string Detail { get; } = pick.Detail;

        public bool IsChecked { get; set; } = isChecked;
    }
}
