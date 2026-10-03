using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

public sealed record ViewerDocument(string Id, string Title, string FileName, string PdfPath);

// 開いている PDF 1 つ分。表示部品(WebView2)は、しばらく表示していなければ解放する
public sealed class ViewerTab(ViewerDocument document)
{
    public ViewerDocument Document { get; } = document;

    public string Title => Document.Title;

    public string FileName => Document.FileName;

    public WebView2? View { get; set; }

    public DateTime LastShown { get; set; } = DateTime.Now;
}

// SC-02 ビューア(決定ログ K-1〜K-4、K-6、docs/04「ビューア」)
public partial class ViewerWindow : FluentWindow
{
    private static readonly TimeSpan ReleaseAfter = TimeSpan.FromMinutes(10);
    private static Task<CoreWebView2Environment>? s_environment;

    private readonly ObservableCollection<ViewerTab> _tabs = [];
    private readonly Action<string> _onOpened;
    private readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    public ViewerWindow(Action<string> onOpened)
    {
        InitializeComponent();
        _onOpened = onOpened;
        TabList.ItemsSource = _tabs;
        SystemThemeWatcher.Watch(this);
        InputBindings.Add(new KeyBinding(new RelayCommand(CloseSelectedTab), Key.W, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MoveSelection(1)), Key.Tab, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MoveSelection(-1)), Key.Tab, ModifierKeys.Control | ModifierKeys.Shift));
        _releaseTimer.Tick += (_, _) => ReleaseIdleViews();
        _releaseTimer.Start();
        Closed += (_, _) =>
        {
            _releaseTimer.Stop();
            foreach (var tab in _tabs)
            {
                tab.View?.Dispose();
            }
        };
    }

    // すでに開いている PDF はそのタブに切り替えるだけ(閲覧回数は数えない、決定ログ K-3)
    public void Open(IReadOnlyList<ViewerDocument> documents)
    {
        ViewerTab? last = null;
        foreach (var document in documents)
        {
            last = _tabs.FirstOrDefault(t => t.Document.Id == document.Id);
            if (last is null)
            {
                last = new ViewerTab(document);
                _tabs.Add(last);
                _onOpened(document.Id);
            }
        }
        if (last is not null)
        {
            TabList.SelectedItem = last;
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Show();
        Activate();
    }

    // 削除する前に呼ぶ(ファイルのロックを外すため)
    public void CloseDocuments(IReadOnlyCollection<string> ids)
    {
        foreach (var tab in _tabs.Where(t => ids.Contains(t.Document.Id)).ToList())
        {
            CloseTab(tab);
        }
    }

    private async void OnTabSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // 表示しなくなった時刻から数えて、しばらくしたら解放する
        foreach (var hidden in e.RemovedItems.OfType<ViewerTab>())
        {
            hidden.LastShown = DateTime.Now;
        }
        var selected = TabList.SelectedItem as ViewerTab;
        foreach (var tab in _tabs.Where(t => t.View is not null))
        {
            tab.View!.Visibility = tab == selected ? Visibility.Visible : Visibility.Collapsed;
        }
        EmptyText.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Title = selected is null ? "ビューア" : $"{selected.Title} - ビューア";
        if (selected is null)
        {
            return;
        }
        selected.LastShown = DateTime.Now;
        if (selected.View is null)
        {
            await CreateViewAsync(selected);
        }
    }

    private async Task CreateViewAsync(ViewerTab tab)
    {
        var view = new WebView2 { Visibility = Visibility.Visible };
        tab.View = view;
        Host.Children.Add(view);
        try
        {
            await view.EnsureCoreWebView2Async(await GetEnvironmentAsync());
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException)
        {
            Host.Children.Remove(view);
            tab.View = null;
            await new Services.WpfUiDialogs(this).ShowErrorAsync($"PDF を表示できませんでした。Microsoft Edge WebView2 ランタイムが必要です。\n{ex.Message}", "表示できません");
            return;
        }
        if (tab.View != view)
        {
            return;
        }

        var core = view.CoreWebView2;
        var settings = core.Settings;
        // 決定ログ K-4: 保存すると ID 名のファイルになるため隠す(USB への書き出しは M6)
        settings.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.Save | CoreWebView2PdfToolbarItems.SaveAs;
        // ネットワークを使わない(CLAUDE.md):SmartScreen の確認などを止める
        settings.IsReputationCheckingRequired = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = false;

        var allowed = new Uri(tab.Document.PdfPath).AbsoluteUri;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.Navigate(allowed);
    }

    private static Task<CoreWebView2Environment> GetEnvironmentAsync() =>
        s_environment ??= CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfManager", "WebView2"),
            options: new CoreWebView2EnvironmentOptions(
                "--disable-background-networking --disable-component-update --disable-sync --no-first-run --disable-features=msSmartScreenProtection")
            {
                AllowSingleSignOnUsingOSPrimaryAccount = false,
            });

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ViewerTab tab)
        {
            CloseTab(tab);
        }
    }

    private void CloseSelectedTab()
    {
        if (TabList.SelectedItem is ViewerTab tab)
        {
            CloseTab(tab);
        }
    }

    private void CloseTab(ViewerTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (tab.View is not null)
        {
            Host.Children.Remove(tab.View);
            tab.View.Dispose();
            tab.View = null;
        }
        _tabs.Remove(tab);
        if (_tabs.Count == 0)
        {
            Close();
            return;
        }
        TabList.SelectedItem ??= _tabs[Math.Min(index, _tabs.Count - 1)];
    }

    private void MoveSelection(int offset)
    {
        if (_tabs.Count > 0)
        {
            TabList.SelectedIndex = (TabList.SelectedIndex + offset + _tabs.Count) % _tabs.Count;
        }
    }

    private void ReleaseIdleViews()
    {
        foreach (var tab in _tabs.Where(t => t.View is not null && t != TabList.SelectedItem && DateTime.Now - t.LastShown > ReleaseAfter))
        {
            Host.Children.Remove(tab.View!);
            tab.View!.Dispose();
            tab.View = null;
        }
    }
}
