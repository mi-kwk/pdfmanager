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

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
