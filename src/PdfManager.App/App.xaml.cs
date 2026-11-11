using System.Windows;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Library;
using PdfManager.Core.Settings;

namespace PdfManager.App;

// 起動引数(開発・テスト用): --library <.pdflib のパス> / --settings <設定フォルダ> / --import <取り込むファイルかフォルダ>
// --after-update <プロセス ID> は更新で起動し直したときにアプリが付ける(決定ログ HA-1)
public partial class App : Application
{
    private LibraryActions _libraries = null!;

    public static App CurrentApp => (App)Current;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        // 決定ログ HB-1: 起動・終了の記録
        LifecycleLog.Write($"起動:バージョン {AppUpdater.CurrentVersionText}、{Environment.ProcessPath}、引数 [{string.Join(" ", e.Args)}]");
        LifecycleLog.StartHangWatch(Dispatcher);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => LifecycleLog.Write("プロセスの終了(ProcessExit)");
        // 決定ログ HA-1: 更新の途中(〇〇.new.exe として起動された)。exe を入れ替えて、元の名前で起動し直す
        if (Array.IndexOf(e.Args, AppUpdater.FinishUpdateArg) >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await Task.Run(() => AppUpdater.FinishUpdate(e.Args));
            Shutdown();
            return;
        }
        // 決定ログ R-9: Windows に入っている SQLite を使うため、古すぎないかを先に確かめる
        if (Core.Database.PdfManagerDatabase.GetSqliteVersion() is var sqlite && sqlite < Core.Database.PdfManagerDatabase.MinimumSqliteVersion)
        {
            System.Windows.MessageBox.Show(
                $"この PC の Windows に入っている SQLite(winsqlite3.dll)が古いため、PdfManager を使えません。\n必要なバージョン:{Core.Database.PdfManagerDatabase.MinimumSqliteVersion} 以上 / この PC:{sqlite}\n\nWindows Update で Windows を新しくしてください。",
                "PdfManager を起動できませんでした", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        // ようこそ画面を閉じたときに終了しないよう、メイン画面を出すまでは明示的に終了する
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // 決定ログ HA-7: データベースを今の形にしたバージョンとして記録する
        Core.Database.PdfManagerDatabase.AppVersion = AppUpdater.CurrentVersionText;
        try
        {
            // 決定ログ HA-1: 更新で起動し直したときは、前の PdfManager が終わるのを待つ
            var afterUpdate = ArgValue(e.Args, AppUpdater.AfterUpdateArg) is not null;
            if (afterUpdate)
            {
                await Task.Run(() => AppUpdater.WaitForPrevious(ArgValue(e.Args, AppUpdater.AfterUpdateArg)));
            }
            // 決定ログ BD-1・BD-2: すでに動いていれば、その画面を前に出して終わる
            // 起動を待たせないよう、裏で記録する
            _ = Task.Run(() => LifecycleLog.WriteOthers("起動時"));
            if (!await SingleInstance.EnsureAsync(new WpfUiDialogs(null)))
            {
                LifecycleLog.Write("ほかの PdfManager を前に出したため(または終了しなかったため)、起動をやめます");
                Shutdown();
                return;
            }
            // 入れ替えに使った「〇〇.new.exe」が残っていれば消す
            await Task.Run(AppUpdater.CleanUpStaging);
            _libraries = new LibraryActions(new AppSettingsStore(ArgValue(e.Args, "--settings") ?? AppSettingsStore.DefaultDirectory));
            // 決定ログ HA-6: 前回より新しいバージョンなら、メイン画面に「更新しました」と出す(配布用の exe のときだけ)
            var updatedNotice = AppUpdater.StandaloneExePath is null ? null : _libraries.CheckUpdated(AppUpdater.CurrentVersionText, afterUpdate);
            var library = await OpenInitialAsync(ArgValue(e.Args, "--library")) ?? ShowWelcome();
            if (library is null)
            {
                LifecycleLog.Write("ライブラリを開かずに終わります(ようこそ画面を閉じた など)");
                Shutdown();
                return;
            }
            LifecycleLog.Write($"ライブラリを開きます:{library.LibraryFilePath}");
            SwitchLibrary(library);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (updatedNotice is not null)
            {
                ((MainWindow)MainWindow).ShowNotice("更新", updatedNotice);
            }
            if (ArgValue(e.Args, "--import") is { } importPath)
            {
                ((MainWindow)MainWindow).OpenImportWindow([importPath]);
            }
        }
        catch (Exception ex)
        {
            LifecycleLog.Write($"起動できませんでした:{ex.GetType().Name} {ex.Message}");
            System.Windows.MessageBox.Show(ex.ToString(), "PdfManager を起動できませんでした", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public void SwitchLibrary(LibraryInfo library)
    {
        var previous = MainWindow;
        var window = new MainWindow(library, _libraries);
        MainWindow = window;
        // 決定ログ BD-3: メイン画面を閉じたら、確実にアプリを終える
        window.Closed += (_, _) =>
        {
            if (MainWindow == window)
            {
                Shutdown();
            }
        };
        window.Show();
        previous?.Close();
    }

    // 決定ログ BD-3: 終わる処理のあと、何かが残って 10 秒たっても終わらなければ、強制的に終える(exe の置き換えを妨げないため)
    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        LifecycleLog.Write($"終了の処理(OnExit、終了コード {e.ApplicationExitCode})。残っている画面:{string.Join("、", Windows.Cast<Window>().Select(w => w.GetType().Name))}");
        // 決定ログ HA-1: 更新したばかりなら、新しい exe で起動し直す
        AppUpdater.RestartIfRequested();
        StartExitWatchdog(e.ApplicationExitCode, "終了の処理");
    }

    private static int _watchdogStarted;

    // 決定ログ BD-3(2026-11-03 に強めた): メイン画面を閉じた時点で見張りを始める。ビューアの後片付けなどで画面の処理が止まると
    // 終了の処理(OnExit)まで進まず、画面のない PdfManager が残って exe を上書きできなくなるため。
    // 10 秒で Environment.Exit、それでも残れば 3 秒後にプロセスを終わらせる。強制的に終えたときは error.log に残す
    public static void StartExitWatchdog(int exitCode, string reason)
    {
        if (Interlocked.Exchange(ref _watchdogStarted, 1) == 1)
        {
            return;
        }
        LifecycleLog.Write($"終了の見張りを始めます(きっかけ:{reason})");
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(10));
            WriteLog($"PdfManager を閉じてから 10 秒たっても終わらなかったため、強制的に終了しました(きっかけ:{reason})。");
            LifecycleLog.Write($"10 秒たっても終わらないため、Environment.Exit で終えます(スレッド {ThreadCount()} 本)");
            var killer = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(3));
                LifecycleLog.Write("Environment.Exit でも終わらないため、プロセスを終わらせます(Kill)");
                System.Diagnostics.Process.GetCurrentProcess().Kill();
                LifecycleLog.Write("Kill を呼んだあとも動いています");
            })
            { IsBackground = true, Name = "ExitKiller" };
            killer.Start();
            Environment.Exit(exitCode);
        })
        { IsBackground = true, Name = "ExitWatchdog" }.Start();
    }

    private static string ThreadCount()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.Threads.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "?";
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettingsStore.DefaultDirectory);
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppSettingsStore.DefaultDirectory, "error.log"), $"==== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\n{message}\n\n");
        }
        catch (Exception)
        {
            // 記録できなくても終了は続ける
        }
    }

    // 前回のライブラリ(または --library)を開く。開けなければ理由を表示して null を返す
    private async Task<LibraryInfo?> OpenInitialAsync(string? requestedPath)
    {
        var path = requestedPath ?? _libraries.Settings.LastLibraryFilePath;
        return path is null ? null : await _libraries.OpenAsync(path, new WpfUiDialogs(null));
    }

    private LibraryInfo? ShowWelcome()
    {
        var welcome = new WelcomeWindow(_libraries);
        welcome.ShowDialog();
        return welcome.SelectedLibrary;
    }

    // 予期しない失敗で、何も表示されずにアプリが消えないようにする。内容は PC の中のファイルに残し(ネットワークには送らない)、画面にも表示する
    private void OnUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = System.IO.Path.Combine(AppSettingsStore.DefaultDirectory, "error.log");
        LifecycleLog.Write($"予期しないエラー:{e.Exception.GetType().Name} {e.Exception.Message}");
        try
        {
            System.IO.Directory.CreateDirectory(AppSettingsStore.DefaultDirectory);
            System.IO.File.AppendAllText(logPath, $"==== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\n{e.Exception}\n\n");
        }
        catch (Exception)
        {
            // 記録できなくても、画面への表示は続ける
        }
        System.Windows.MessageBox.Show(
            $"予期しないエラーが発生しました。作業中の内容を確認してください。\n\n{e.Exception.Message}\n\n詳しい内容は次のファイルに記録しました。\n{logPath}",
            "PdfManager", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
