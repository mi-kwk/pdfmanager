using System.ComponentModel;
using System.Windows;
using PdfManager.App.Services;
using PdfManager.Core.Library;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// 「保存先を移す」の進み具合(決定ログ S-3)。中止すると、今の 1 件(版の系列)を終えてから止まる
public partial class ProgressWindow : FluentWindow
{
    private readonly CancellationTokenSource _cancellation = new();
    private bool _finished;

    public ProgressWindow(string title)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        Title = title;
        TitleBarControl.Title = title;
        Progress = new Progress<MoveProgress>(p =>
        {
            CountText.Text = $"{p.Done:N0} / {p.Total:N0} 件";
            ItemText.Text = p.Title.Length == 0 ? " " : p.Title;
            Bar.Maximum = Math.Max(1, p.Total);
            Bar.Value = p.Done;
        });
        Loaded += (_, _) =>
        {
            if (_finished)
            {
                Close();
            }
        };
        Closing += OnClosing;
    }

    public IProgress<MoveProgress> Progress { get; }

    public CancellationToken Cancellation => _cancellation.Token;

    // 処理が終わるまで表示する(終わったら自動で閉じる)
    public void ShowUntil(Task task)
    {
        task.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            _finished = true;
            if (IsLoaded)
            {
                Close();
            }
        }), TaskScheduler.Default);
        ShowDialog();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cancellation.Cancel();
        CancelButton.IsEnabled = false;
        CancelButton.Content = "止めています…";
    }

    // 途中では閉じない(中止ボタンで止める)
    private void OnClosing(object? sender, CancelEventArgs e) => e.Cancel = !_finished;
}
