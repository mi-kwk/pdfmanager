using System.Windows;
using System.Windows.Automation;
using PdfManager.App.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// 1 行の文字を入力してもらう(サブライブラリの名前の変更など)
public partial class TextInputWindow : FluentWindow
{
    public TextInputWindow(string title, string label, string initialText)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        Title = title;
        TitleBarControl.Title = title;
        LabelText.Text = label;
        InputBox.Text = initialText;
        AutomationProperties.SetName(InputBox, label);
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    // OK を押したときの入力。キャンセルなら null
    public string? Result { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Result = InputBox.Text;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
