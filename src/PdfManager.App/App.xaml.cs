using System.Windows;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Library;
using PdfManager.Core.Settings;

namespace PdfManager.App;

// 起動引数(開発・テスト用): --library <.pdflib のパス> / --settings <設定フォルダ> / --import <取り込むファイルかフォルダ>
public partial class App : Application
{
    private LibraryActions _libraries = null!;

    public static App CurrentApp => (App)Current;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
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
        try
        {
            _libraries = new LibraryActions(new AppSettingsStore(ArgValue(e.Args, "--settings") ?? AppSettingsStore.DefaultDirectory));
            var library = await OpenInitialAsync(ArgValue(e.Args, "--library")) ?? ShowWelcome();
            if (library is null)
            {
                Shutdown();
                return;
            }
            SwitchLibrary(library);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (ArgValue(e.Args, "--import") is { } importPath)
            {
                ((MainWindow)MainWindow).OpenImportWindow([importPath]);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.ToString(), "PdfManager を起動できませんでした", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public void SwitchLibrary(LibraryInfo library)
    {
        var previous = MainWindow;
        var window = new MainWindow(library, _libraries);
        MainWindow = window;
        window.Show();
        previous?.Close();
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
