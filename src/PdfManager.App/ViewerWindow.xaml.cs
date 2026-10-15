using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PdfManager.App.Services;
using PdfManager.Core.Documents;
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

    // 比較の左右の選択欄で、読み上げにタイトルを使う
    public override string ToString() => Title;
}

// SC-02 ビューア(決定ログ K-1〜K-4、K-6、docs/04「ビューア」)
public partial class ViewerWindow : FluentWindow
{
    private static readonly TimeSpan ReleaseAfter = TimeSpan.FromMinutes(10);
    private static Task<CoreWebView2Environment>? s_environment;

    private readonly ObservableCollection<ViewerTab> _tabs = [];
    private readonly Action<string> _onOpened;
    private readonly Func<string, (LinkedDocument? Previous, LinkedDocument? Next)> _neighbors;
    private readonly Action<LinkedDocument> _openLinked;
    private readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private (LinkedDocument? Previous, LinkedDocument? Next) _currentNeighbors;
    // 2 画面比較の右側(決定ログ W-1〜W-4)。null なら比較していない。左側は選択中のタブ
    private ViewerTab? _compareRight;
    private bool _syncingChoices;

    // neighbors は続きの前後(決定ログ V-5)、openLinked はそれを開く処理(メイン画面が保存先を確かめて開く)
    public ViewerWindow(Action<string> onOpened, Func<string, (LinkedDocument? Previous, LinkedDocument? Next)> neighbors, Action<LinkedDocument> openLinked)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        _onOpened = onOpened;
        _neighbors = neighbors;
        _openLinked = openLinked;
        TabList.ItemsSource = _tabs;
        LeftChoice.ItemsSource = _tabs;
        RightChoice.ItemsSource = _tabs;
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
        // 比較中に右側の PDF を左に選んだときは、左右を入れ替える
        if (_compareRight is not null && selected == _compareRight)
        {
            _compareRight = e.RemovedItems.OfType<ViewerTab>().FirstOrDefault(t => _tabs.Contains(t)) ?? _tabs.FirstOrDefault(t => t != selected);
        }
        UpdateViews();
        Title = selected is null ? "ビューア" : $"{selected.Title} - ビューア";
        UpdateChainButtons(selected);
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

    // 選択中のタブ(比較中は左)と、比較中の右のタブだけを表示する
    private void UpdateViews()
    {
        var left = TabList.SelectedItem as ViewerTab;
        var comparing = _compareRight is not null && _compareRight != left;
        foreach (var tab in _tabs.Where(t => t.View is not null))
        {
            var column = tab == left ? 0 : comparing && tab == _compareRight ? 2 : -1;
            tab.View!.Visibility = column < 0 ? Visibility.Collapsed : Visibility.Visible;
            System.Windows.Controls.Grid.SetColumn(tab.View, Math.Max(column, 0));
        }
        SplitterColumn.Width = new GridLength(comparing ? 6 : 0);
        if (!comparing)
        {
            LeftColumn.Width = new GridLength(1, GridUnitType.Star);
            RightColumn.Width = new GridLength(0);
        }
        else if (RightColumn.Width.Value == 0)
        {
            RightColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        Splitter.Visibility = comparing ? Visibility.Visible : Visibility.Collapsed;
        CompareBar.Visibility = comparing ? Visibility.Visible : Visibility.Collapsed;
        CompareButton.Visibility = comparing ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _syncingChoices = true;
        LeftChoice.SelectedItem = left;
        RightChoice.SelectedItem = comparing ? _compareRight : null;
        _syncingChoices = false;
    }

    // 決定ログ W-1: メイン画面(2 件の並べて比較、版の比較)から、開いた 2 つを左右に並べる
    public void Compare(string leftId, string rightId)
    {
        var left = _tabs.FirstOrDefault(t => t.Document.Id == leftId);
        var right = _tabs.FirstOrDefault(t => t.Document.Id == rightId);
        if (left is null || right is null || left == right)
        {
            return;
        }
        StartCompare(left, right);
    }

    private void StartCompare(ViewerTab left, ViewerTab right)
    {
        // 左右を半分ずつに戻してから始める(決定ログ W-4)
        LeftColumn.Width = new GridLength(1, GridUnitType.Star);
        RightColumn.Width = new GridLength(1, GridUnitType.Star);
        _compareRight = right;
        if (TabList.SelectedItem == left)
        {
            UpdateViews();
        }
        else
        {
            TabList.SelectedItem = left;
        }
        if (right.View is null)
        {
            _ = CreateViewAsync(right);
        }
    }

    private async void OnCompareClick(object sender, RoutedEventArgs e)
    {
        if (TabList.SelectedItem is not ViewerTab left || _tabs.Count < 2)
        {
            await new WpfUiDialogs(this).ShowErrorAsync("比較するには、PDF を 2 つ以上開いてください(メイン画面で 2 件選んで右クリック「並べて比較」でも始められます)。", "比較");
            return;
        }
        StartCompare(left, _tabs[(_tabs.IndexOf(left) + 1) % _tabs.Count]);
    }

    private void OnEndCompareClick(object sender, RoutedEventArgs e)
    {
        _compareRight = null;
        UpdateViews();
    }

    private void OnLeftChoiceChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_syncingChoices && LeftChoice.SelectedItem is ViewerTab tab)
        {
            TabList.SelectedItem = tab;
        }
    }

    private void OnRightChoiceChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingChoices || RightChoice.SelectedItem is not ViewerTab tab || tab == _compareRight)
        {
            return;
        }
        var left = TabList.SelectedItem as ViewerTab;
        var oldRight = _compareRight;
        _compareRight = tab;
        if (tab == left && oldRight is not null)
        {
            // 左と同じものを選んだときは入れ替える
            TabList.SelectedItem = oldRight;
        }
        else
        {
            UpdateViews();
        }
        if (tab.View is null)
        {
            _ = CreateViewAsync(tab);
        }
    }

    // 決定ログ V-5: 表示中の PDF に続きの前後があれば「前へ」「次へ」を出す
    private void UpdateChainButtons(ViewerTab? selected)
    {
        _currentNeighbors = selected is null ? (null, null) : _neighbors(selected.Document.Id);
        PreviousButton.Visibility = _currentNeighbors.Previous is null ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Visibility = _currentNeighbors.Next is null ? Visibility.Collapsed : Visibility.Visible;
        PreviousButton.ToolTip = _currentNeighbors.Previous is { } previous ? $"前:{previous.Title}" : null;
        NextButton.ToolTip = _currentNeighbors.Next is { } next ? $"次:{next.Title}" : null;
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        if (_currentNeighbors.Previous is { } previous)
        {
            _openLinked(previous);
        }
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_currentNeighbors.Next is { } next)
        {
            _openLinked(next);
        }
    }

    private async Task CreateViewAsync(ViewerTab tab)
    {
        var view = new WebView2 { Visibility = Visibility.Collapsed };
        tab.View = view;
        Host.Children.Add(view);
        // 比較中は左右のどちらに出すかを決めてから表示する
        UpdateViews();
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
        // 比較の右側を閉じたら、比較をやめる
        if (tab == _compareRight)
        {
            _compareRight = null;
        }
        TabList.SelectedItem ??= _tabs[Math.Min(index, _tabs.Count - 1)];
        if (_compareRight == TabList.SelectedItem)
        {
            _compareRight = null;
        }
        UpdateViews();
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
        foreach (var tab in _tabs.Where(t => t.View is not null && t != TabList.SelectedItem && t != _compareRight && DateTime.Now - t.LastShown > ReleaseAfter))
        {
            Host.Children.Remove(tab.View!);
            tab.View!.Dispose();
            tab.View = null;
        }
    }
}
