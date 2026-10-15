using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PdfManager.App.ViewModels;

namespace PdfManager.App.Views;

public partial class TagSectionsView : UserControl
{
    private readonly Dictionary<FrameworkElement, (TagSection Section, Action Handler)> _focusHandlers = [];

    public TagSectionsView()
    {
        InitializeComponent();
    }

    // 決定ログ AA-4: 欄を畳んだら、次の順位の欄の検索欄に入力の位置を移す
    private void OnSearchLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement box || box.DataContext is not TagSection section || _focusHandlers.ContainsKey(box))
        {
            return;
        }
        // 開いた直後は表示が間に合っていないため、画面の更新が終わってから移す
        void Handler() => box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.BringIntoView();
            box.Focus();
        });
        section.FocusSearchRequested += Handler;
        _focusHandlers[box] = (section, Handler);
    }

    private void OnSearchUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement box && _focusHandlers.Remove(box, out var entry))
        {
            entry.Section.FocusSearchRequested -= entry.Handler;
        }
    }
}
