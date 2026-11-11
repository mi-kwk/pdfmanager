using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PdfManager.Core.Library;
using PdfManager.Core.Update;

namespace PdfManager.App.Services;

// アプリの更新(決定ログ HA-1〜HA-6)。設定画面とようこそ画面の「新しいバージョンに更新」から使う
public static class AppUpdater
{
    public const string AfterUpdateArg = "--after-update";
    public const string FinishUpdateArg = "--finish-update";

    private static string? _staged;
    private static string? _target;
    private static DateTime _requestedAt;

    // exe を作った日時から付けたバージョン(年.月.日.時分。PdfManager.App.csproj の Version)
    public static string CurrentVersionText =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "不明";

    public static Version? CurrentVersion => ExeUpdater.ParseVersion(CurrentVersionText);

    // 配布用の exe 1 つで動いているときの exe の場所。開発用の実行(dll と一緒に動くもの)では null
    public static string? StandaloneExePath =>
        !File.Exists(Path.Combine(AppContext.BaseDirectory, "PdfManager.dll")) && Environment.ProcessPath is { } path ? path : null;

    // 更新できたら true(呼んだ側がメイン画面などを閉じると、新しいバージョンで起動し直す)。
    // library・storage:開いているライブラリ(ようこそ画面からは null)。バックアップの期限が過ぎていれば先にすすめる(HA-4)
    public static async Task<bool> RunAsync(Window owner, LibraryActions libraries, StorageAccess? storage)
    {
        const string Title = "新しいバージョンに更新";
        var dialogs = new WpfUiDialogs(owner);
        if (StandaloneExePath is not { } current || CurrentVersion is not { } currentVersion)
        {
            await dialogs.ShowErrorAsync("開発用の実行のため、ここからは更新できません。", Title);
            return false;
        }
        // 決定ログ HA-3: 置き場所に書き込めなければ、手で差し替える手順を出す
        if (!ExeUpdater.CanWrite(current))
        {
            await dialogs.ShowErrorAsync(
                $"PdfManager.exe の置き場所に書き込めないため、ここからは更新できません。\n{current}\n\n" +
                "手で差し替えるときは、PdfManager を閉じてから、新しい PdfManager.exe を上の場所にコピーして上書きしてください" +
                "(書き込みに管理者の許可が必要な場所では、許可を求められます)。\n\nデスクトップやドキュメントなど、自分のフォルダに置くと、次からはここで更新できます。", Title);
            return false;
        }

        var dialog = new OpenFileDialog
        {
            Title = "新しい PdfManager.exe を選択(USB メモリなど)",
            Filter = "PdfManager (*.exe)|*.exe",
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return false;
        }
        var path = dialog.FileName;

        // 決定ログ HA-2: バージョンを比べ、新しいときだけ更新する
        var check = ExeUpdater.Check(path, current, currentVersion);
        var problem = check.Status switch
        {
            ExeCheckStatus.SameFile => "今動いている PdfManager.exe そのものです。USB メモリなどにある新しい PdfManager.exe を選んでください。",
            ExeCheckStatus.NotPdfManager => $"PdfManager の exe ではありません。\n{path}",
            ExeCheckStatus.NotStandalone => $"開発用の exe で、このファイルだけでは動きません。配布用の PdfManager.exe を選んでください。\n{path}",
            ExeCheckStatus.SameOrOlder => $"選んだ PdfManager.exe はバージョン {check.VersionText} で、今の PdfManager(バージョン {CurrentVersionText})と同じか古いため、更新しません。\n{path}",
            _ => null,
        };
        if (problem is not null)
        {
            await dialogs.ShowErrorAsync(problem, Title);
            return false;
        }
        if (!await dialogs.ConfirmAsync(Title,
                $"バージョン {CurrentVersionText} → {check.VersionText} に更新します。\n\n" +
                "PdfManager をいったん閉じて、新しいバージョンで起動し直します。\n" +
                $"前のバージョンの exe は「{Path.GetFileName(ExeUpdater.OldPath(current))}」として同じフォルダに残します。\n\n置き場所:{current}",
                "更新する"))
        {
            return false;
        }

        // 決定ログ HA-4: バックアップの期限が過ぎていれば、先にすすめる
        if (storage is not null && BackupActions.DueMessage(libraries, storage.Storage.Main) is { } due)
        {
            var choice = await dialogs.AskChoiceAsync("更新の前にバックアップ",
                due + "\n更新の前にバックアップしますか?\n\n(新しいバージョンで初めて開いたときにデータベースの形が変わるときは、変える前のデータベースの控えも自動で残します)",
                "バックアップしてから更新", "しないで更新", "キャンセル");
            if (choice == 0)
            {
                return false;
            }
            if (choice == 1)
            {
                await BackupActions.RunAsync(owner, libraries, storage);
            }
        }


        var progress = new ProgressWindow("新しい PdfManager.exe を写しています") { Owner = owner };
        var task = Task.Run(() =>
        {
            progress.Progress.Report(new MoveProgress(0, 1, Path.GetFileName(path)));
            var staged = ExeUpdater.Stage(current, path);
            progress.Progress.Report(new MoveProgress(1, 1, ""));
            return staged;
        });
        progress.ShowUntil(task);
        try
        {
            _staged = await task;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await dialogs.ShowErrorAsync($"更新できませんでした。今の PdfManager はそのままです。\n\n{ex.Message}", Title);
            return false;
        }
        _target = current;
        _requestedAt = DateTime.Now;
        return true;
    }

    // アプリを終えるときに呼ぶ。更新を始めたばかりなら、写しておいた新しい exe(〇〇.new.exe)を起動する。
    // 新しい exe は、この PdfManager が終わるのを待ってから入れ替え(FinishUpdate)、元の名前で起動し直す
    public static void RestartIfRequested()
    {
        if (_staged is not { } staged || _target is not { } target || DateTime.Now - _requestedAt > TimeSpan.FromMinutes(5) || !File.Exists(staged))
        {
            return;
        }
        _staged = null;
        var start = new ProcessStartInfo(staged) { UseShellExecute = false };
        AddPassThroughArgs(start, Environment.GetCommandLineArgs().Skip(1).ToList());
        start.ArgumentList.Add(FinishUpdateArg);
        start.ArgumentList.Add(target);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        TryStart(start);
    }

    // 決定ログ HA-1 の ③:〇〇.new.exe として起動されたとき。前の PdfManager が終わってから exe を入れ替え、元の名前で起動し直す。
    // 画面は出さない(失敗したときだけ知らせ、前のバージョンのまま起動する)
    public static void FinishUpdate(string[] args)
    {
        var index = Array.IndexOf(args, FinishUpdateArg);
        if (index < 0 || index + 2 >= args.Length || Environment.ProcessPath is not { } self)
        {
            return;
        }
        var target = args[index + 1];
        LifecycleLog.Write($"更新:前の PdfManager [{args[index + 2]}] が終わるのを待ちます");
        WaitForPrevious(args[index + 2]);
        LifecycleLog.Write("更新:exe を入れ替えます");
        Exception? failure = null;
        // 前の PdfManager の後片付けで、exe がすぐには離されないことがあるため、少し待ってやり直す
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                ExeUpdater.Complete(target, self);
                failure = null;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
                Thread.Sleep(500);
            }
        }
        LifecycleLog.Write(failure is null ? "更新:入れ替えました" : $"更新:入れ替えられませんでした:{failure.Message}");
        if (failure is not null)
        {
            LifecycleLog.WriteOthers("入れ替えられなかったとき");
            System.Windows.MessageBox.Show(
                $"PdfManager.exe を新しいバージョンに入れ替えられませんでした。前のバージョンのまま起動します。\n\n{failure.Message}\n\n手で入れ替えるときは、PdfManager を閉じてから、次のファイルを「{Path.GetFileName(target)}」という名前にしてください。\n{self}",
                "新しいバージョンに更新", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        var start = new ProcessStartInfo(target) { UseShellExecute = false };
        AddPassThroughArgs(start, args.ToList());
        start.ArgumentList.Add(AfterUpdateArg);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        TryStart(start);
    }

    // 決定ログ HA-1: 更新で起動し直したとき、入れ替えをした PdfManager が終わるのを待つ(二重起動の確認に引っかからないように)
    public static void WaitForPrevious(string? processId)
    {
        if (!int.TryParse(processId, out var id))
        {
            return;
        }
        try
        {
            using var previous = Process.GetProcessById(id);
            previous.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // すでに終わっている
        }
    }

    // 起動したとき、入れ替えに使った「〇〇.new.exe」が残っていれば消す(更新の途中でやめたときなど)
    public static void CleanUpStaging()
    {
        if (StandaloneExePath is not { } exe)
        {
            return;
        }
        var staged = ExeUpdater.StagingPath(exe);
        for (var attempt = 0; attempt < 6 && File.Exists(staged); attempt++)
        {
            if (ExeUpdater.TryDelete(staged))
            {
                return;
            }
            Thread.Sleep(500);
        }
    }

    // 開発・テスト用の引数(--settings)は引き継ぐ。--library と --import は引き継がない(前回のライブラリを開く)
    private static void AddPassThroughArgs(ProcessStartInfo start, IReadOnlyList<string> args)
    {
        var index = args.ToList().IndexOf("--settings");
        if (index >= 0 && index + 1 < args.Count)
        {
            start.ArgumentList.Add("--settings");
            start.ArgumentList.Add(args[index + 1]);
        }
    }

    private static void TryStart(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // 起動できなくても、次にダブルクリックすれば起動できる
        }
    }
}