using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfManager.App.ViewModels;

namespace PdfManager.App.Views;

public partial class DocumentEditorView : UserControl
{
    public DocumentEditorView()
    {
        InitializeComponent();
    }

    private DocumentEditor? Editor => DataContext as DocumentEditor;

    // 決定ログ DF-1: 候補は ↑↓ で選び、Enter で入れる(選んでいなければ先頭)。Esc で閉じる
    private void OnTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (Editor is not { UseTitleSuggestions: true } editor)
        {
            return;
        }
        var count = TitleSuggestionList.Items.Count;
        switch (e.Key)
        {
            case Key.Down when !editor.IsTitleSuggestionsOpen:
                editor.ShowTitleSuggestions(all: TitleBox.Text.Trim().Length == 0);
                e.Handled = true;
                break;
            case Key.Down or Key.Up when count > 0:
                var index = TitleSuggestionList.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                TitleSuggestionList.SelectedIndex = Math.Clamp(index, 0, count - 1);
                TitleSuggestionList.ScrollIntoView(TitleSuggestionList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter when editor.IsTitleSuggestionsOpen && count > 0:
                Adopt((TitleSuggestion)(TitleSuggestionList.SelectedItem ?? TitleSuggestionList.Items[0]));
                e.Handled = true;
                break;
            case Key.Escape when editor.IsTitleSuggestionsOpen:
                editor.IsTitleSuggestionsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnTitleSuggestionClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(TitleSuggestionList, source) is ListBoxItem { DataContext: TitleSuggestion suggestion })
        {
            Adopt(suggestion);
        }
    }

    private void Adopt(TitleSuggestion suggestion)
    {
        Editor?.AdoptTitleSuggestion(suggestion);
        TitleBox.Focus();
        TitleBox.CaretIndex = TitleBox.Text.Length;
    }

    private void OnShowAllTitlesClick(object sender, RoutedEventArgs e)
    {
        if (Editor is { } editor)
        {
            editor.ShowTitleSuggestions(all: true);
            TitleBox.Focus();
        }
    }

    // 候補の一覧をクリックしている間は閉じない(閉じるのは Popup の外をクリックしたとき)
    private void OnTitleLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Editor is { IsTitleSuggestionsOpen: true } editor && !(e.NewFocus is DependencyObject focus && TitlePopup.Child.IsAncestorOf(focus)))
        {
            editor.IsTitleSuggestionsOpen = false;
        }
    }
}
