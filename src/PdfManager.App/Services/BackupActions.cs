using System.IO;
using System.Windows;
using Microsoft.Win32;
using PdfManager.Core.Backup;
using PdfManager.Core.Library;
using PdfManager.Core.Settings;

namespace PdfManager.App.Services;

// バックアップの操作(決定ログ BB-1〜BB-7)。設定画面と、起動時のお知らせから使う
public static class BackupActions
{
    // 決定ログ BB-2 の補足(2026-11-10):Windows のフォルダーの選択の画面では、「PC」でドライブを選んだだけだと
    // 「フォルダーの選択」が押せないことがあるため、題名で手順を伝える
    public const string DriveRootHint = "ドライブの直下に置くときは、ドライブを開いてから何も選ばずに「フォルダーの選択」を押してください";

    // 保存先を選ぶ。実際にできる場所(「PdfManager-backup」フォルダ)を見せて確かめる。選ばなければ null
    public static async Task<string?> ChooseFolderAsync(Window owner, string? current)
    {
        var dialog = new OpenFolderDialog { Title = $"バックアップの保存先を選択(外付け HDD など。{DriveRootHint})" };
        if (current is not null && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }
        if (dialog.ShowDialog(owner) != true)
        {
            return null;
        }
        var folder = dialog.FolderName;
        // 「PdfManager-backup」そのものを選んだときは、その親を保存先にする(中にもう 1 つ作らないように)
        if (string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), BackupService.RootFolderName, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) is { } parent)
        {
            folder = parent;
        }
        var target = Path.Combine(folder, BackupService.RootFolderName);
        var exists = Directory.Exists(target) ? "\n(この場所にはバックアップがあります。続きから、増えた分だけコピーします)" : "\n(このフォルダは、初めてバックアップするときに作ります)";
        return await new WpfUiDialogs(owner).ConfirmAsync("バックアップの保存先",
                $"次の場所に保存します。\n\n{target}{exists}\n\nこの場所でよいですか?\n(別の場所にするときは「キャンセル」を押して、選び直してください)",
                "この場所にする")
            ? folder
            : null;
    }

    // 今すぐバックアップ。保存先がなければ選んでもらう。終わったら結果を表示し、PC の設定に記録する
    public static async Task RunAsync(Window owner, LibraryActions libraries, StorageAccess storage)
    {
        var dialogs = new WpfUiDialogs(owner);
        var preferences = libraries.Backup;
        var folder = preferences.Folder;
        if (folder is null || !Directory.Exists(folder))
        {
            if (folder is not null)
            {
                await dialogs.ShowErrorAsync($"バックアップの保存先が見つかりません。外付け HDD などをつないでいるか確かめてください。\n{folder}\n\n別の保存先を選ぶこともできます。", "バックアップ");
            }
            folder = await ChooseFolderAsync(owner, folder);
            if (folder is null)
            {
                return;
            }
            if (LibraryService.IsSameOrUnder(folder, storage.Storage.Main.RootFolder))
            {
                await dialogs.ShowErrorAsync("ライブラリのフォルダの中には保存できません。外付け HDD など、別の場所を選んでください。", "バックアップ");
                return;
            }
            preferences = preferences with { Folder = folder };
            libraries.SetBackup(preferences);
        }

        // サブの接続を確かめてから見積もる
        await storage.RefreshAsync();
        var plan = await storage.RunAsync(s => BackupService.Plan(s, folder));
        if (!plan.HasEnoughSpace)
        {
            await dialogs.ShowErrorAsync($"保存先のドライブの空き容量が足りません。\nコピーする量:{FormatSize(plan.Bytes)}\n空き容量:{FormatSize(plan.FreeBytes ?? 0)}", "バックアップ");
            return;
        }
        var notes = plan.UnavailableSubs.Count == 0 ? ""
            : $"\n\n次のサブライブラリがつながっていないため、その中の PDF(まだバックアップしていないもの)はコピーできません。\n・{string.Join("\n・", plan.UnavailableSubs)}";
        if (!await dialogs.ConfirmAsync("バックアップ",
                $"保存先:{plan.Folder}\n\n新しくコピーする PDF:{plan.PdfCount:N0} 件(合計 {FormatSize(plan.Bytes)})\nほかに、文書の情報(データベース)の写しを作ります。古い回は {preferences.KeepOldGenerations} 回分残します。{notes}",
                "バックアップする"))
        {
            return;
        }

        var progress = new ProgressWindow("バックアップしています") { Owner = owner };
        var keep = preferences.KeepOldGenerations;
        var task = storage.RunAsync(s => BackupService.Run(s, folder, keep, progress.Progress, progress.Cancellation));
        progress.ShowUntil(task);
        BackupResult result;
        try
        {
            result = await task;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Record(libraries, storage.Storage.Main, $"失敗:{ex.Message}");
            await dialogs.ShowErrorAsync($"バックアップできませんでした。前回までのバックアップはそのまま残っています。\n\n{ex.Message}", "バックアップ");
            return;
        }
        if (result.Cancelled)
        {
            await dialogs.ShowErrorAsync("途中で止めました。今回の分は残していません(前回までのバックアップはそのまま残っています)。", "バックアップ");
            return;
        }
        var summary = $"{result.Generation!.DocumentCount:N0} 件の文書をバックアップしました(新しくコピーした PDF:{result.CopiedPdfs:N0} 件)。"
            + (result.MissingPdfs > 0 ? $"\nサブがつながっていないため、{result.MissingPdfs:N0} 件の PDF はコピーできませんでした。" : "")
            + (result.RemovedGenerations > 0 ? $"\n古い回を {result.RemovedGenerations} 回分、使われなくなった PDF を {result.RemovedPdfs:N0} 件消しました。" : "");
        Record(libraries, storage.Storage.Main, result.MissingPdfs > 0 ? $"一部の PDF なし({result.MissingPdfs:N0} 件)" : "成功");
        await dialogs.ShowErrorAsync(summary, "バックアップしました");
    }

    private static void Record(LibraryActions libraries, LibraryInfo library, string result)
    {
        var preferences = libraries.Backup;
        var records = new Dictionary<string, BackupRecord>(preferences.LastBackups ?? new Dictionary<string, BackupRecord>())
        {
            [library.Id.ToString("D")] = new BackupRecord(DateTimeOffset.Now, result),
        };
        libraries.SetBackup(preferences with { LastBackups = records });
    }

    // 決定ログ BB-5: 前回(なければライブラリを作った日)から設定の日数たっていたら、起動時に知らせる
    public static async Task RemindIfDueAsync(Window owner, LibraryActions libraries, StorageAccess storage, int documentCount)
    {
        var preferences = libraries.Backup;
        if (preferences.ReminderDays <= 0 || documentCount == 0)
        {
            return;
        }
        var library = storage.Storage.Main;
        var last = preferences.LastFor(library.Id);
        DateTimeOffset since;
        try
        {
            since = last?.At ?? LibraryService.ReadFile(library.LibraryFilePath).CreatedAt;
        }
        catch (Exception ex) when (ex is LibraryException or IOException)
        {
            return;
        }
        var days = (int)(DateTimeOffset.Now - since).TotalDays;
        if (days < preferences.ReminderDays)
        {
            return;
        }
        var message = last is null
            ? "このライブラリはまだ一度もバックアップしていません。"
            : $"前回のバックアップから {days} 日たっています。";
        if (await new WpfUiDialogs(owner).ConfirmAsync("バックアップ",
                message + "\n今すぐバックアップしますか?\n\n知らせる日数は、設定画面の「バックアップ」で変えられます。", "今すぐバックアップ"))
        {
            await RunAsync(owner, libraries, storage);
        }
    }

    public static string LastText(LibraryActions libraries, LibraryInfo library) => libraries.Backup.LastFor(library.Id) is { } last
        ? $"前回:{last.At.LocalDateTime:yyyy/MM/dd HH:mm}({last.Result})"
        : "前回:まだバックアップしていません";

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024.0):0} KB",
    };
}
