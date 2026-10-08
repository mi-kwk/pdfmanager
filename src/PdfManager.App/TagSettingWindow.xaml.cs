using System.Windows;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// 詳細エリアの「タグ設定」(決定ログ R-11 ④)。詳細エリアと同じ入力内容を直接変え、キャンセルなら元に戻す
public partial class TagSettingWindow : FluentWindow
{
    private readonly DocumentEditor _editor;
    private readonly Dictionary<EditableDocument, HashSet<string>> _before;

    public TagSettingWindow(DocumentEditor editor)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        _editor = editor;
        DataContext = editor;
        _before = editor.Targets.ToDictionary(t => t, t => t.TagKeys.ToHashSet());
        SubjectText.Text = editor.Targets is [var only] ? only.DisplayName : $"{editor.Targets.Count} 件を選択中(付けたタグは選択中の全件に付きます)";
    }

    // OK なら true(キャンセル・閉じるボタンなら、タグは開く前に戻っている)
    public bool Accepted { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (!Accepted)
        {
            foreach (var (target, keys) in _before)
            {
                target.TagKeys.Clear();
                target.TagKeys.UnionWith(keys);
            }
            _editor.Reload();
        }
        base.OnClosed(e);
    }
}
