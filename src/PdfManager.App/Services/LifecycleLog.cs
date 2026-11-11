using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using PdfManager.Core.Settings;

namespace PdfManager.App.Services;

// 起動・終了の記録(決定ログ HB-1〜HB-4)。閉じたあとに PdfManager が残る原因を調べるため、
// 起動から終了までの各段階を、時刻とプロセスの番号つきで %LOCALAPPDATA%\PdfManager\lifecycle.log に残す。
// PC の中のファイルに書くだけで、ネットワークには送らない。1MB を超えたら lifecycle.old.log に移して書き直す
public static class LifecycleLog
{
    private const long MaxBytes = 1L << 20;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppSettingsStore.DefaultDirectory, "lifecycle.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettingsStore.DefaultDirectory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(FilePath, Path.Combine(AppSettingsStore.DefaultDirectory, "lifecycle.old.log"), overwrite: true);
                }
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 記録できなくても動作は続ける
        }
    }

    // 起動したときに、ほかに動いている PdfManager(更新の途中のものも含む)の様子を残す
    public static void WriteOthers(string when)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var others = Process.GetProcesses()
                .Where(p => p.Id != current.Id && p.ProcessName.StartsWith("PdfManager", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (others.Count == 0)
            {
                Write($"{when}:ほかの PdfManager はありません");
                return;
            }
            foreach (var p in others)
            {
                Write($"{when}:ほかの PdfManager [{p.Id}] {Describe(p)}");
                p.Dispose();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string Describe(Process p)
    {
        var parts = new List<string> { p.ProcessName };
        Try(() => parts.Add($"起動 {p.StartTime:MM-dd HH:mm:ss}"));
        Try(() => parts.Add(p.MainWindowHandle == IntPtr.Zero ? "画面なし" : $"画面あり「{p.MainWindowTitle}」"));
        Try(() => parts.Add(p.Responding ? "応答あり" : "応答なし"));
        Try(() => parts.Add($"スレッド {p.Threads.Count}"));
        Try(() => parts.Add($"メモリ {p.WorkingSet64 >> 20}MB"));
        Try(() => parts.Add($"CPU 時間 {p.TotalProcessorTime.TotalSeconds:0.0} 秒"));
        Try(() => parts.Add($"セッション {p.SessionId}"));
        return string.Join("、", parts);

        static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
        }
    }

    // 決定ログ HB-3: 画面の処理(UI スレッド)が止まっていないかを、別のスレッドから 2 秒おきに確かめる。
    // 5 秒以上応答がなければ記録し、戻ったらかかった時間を記録する。終了の処理に入ったあとも続ける
    public static void StartHangWatch(Dispatcher dispatcher)
    {
        new Thread(() =>
        {
            var hung = false;
            var since = DateTime.Now;
            while (true)
            {
                var operation = dispatcher.BeginInvoke(DispatcherPriority.Send, () => { });
                var started = DateTime.Now;
                var status = operation.Wait(TimeSpan.FromSeconds(5));
                if (status == DispatcherOperationStatus.Completed)
                {
                    if (hung)
                    {
                        Write($"画面の処理が戻りました(約 {(DateTime.Now - since).TotalSeconds:0} 秒止まっていました)");
                        hung = false;
                    }
                    Thread.Sleep(2000);
                    continue;
                }
                if (dispatcher.HasShutdownFinished)
                {
                    Write("画面の処理は終わっています(終了の処理の途中)");
                    return;
                }
                if (!hung)
                {
                    hung = true;
                    since = started;
                    Write("画面の処理が 5 秒以上止まっています");
                }
                // 止まったままなら、30 秒ごとに書く
                operation.Wait(TimeSpan.FromSeconds(25));
                if (operation.Status != DispatcherOperationStatus.Completed)
                {
                    Write($"画面の処理がまだ止まっています(約 {(DateTime.Now - since).TotalSeconds:0} 秒)");
                }
            }
        })
        { IsBackground = true, Name = "HangWatch" }.Start();
    }
}
