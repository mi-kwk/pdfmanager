using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PdfManager.App.Services;

// 二重起動の防止(決定ログ BD-1・BD-2)。同じ Windows のログオン中に、ほかの PdfManager が動いていないかを確かめる
public static class SingleInstance
{
    private const int SW_RESTORE = 9;
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(10);

    // 起動を続けてよければ true。すでに画面のある PdfManager が動いていれば、それを前に出して false
    public static async Task<bool> EnsureAsync(IUserDialogs dialogs)
    {
        using var current = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(current.ProcessName)
            .Where(p => p.Id != current.Id && p.SessionId == current.SessionId)
            .ToList();
        var deadline = DateTime.Now + StartupWait;
        while (true)
        {
            others = others.Where(p => !HasExited(p)).ToList();
            if (others.Count == 0)
            {
                return true;
            }
            foreach (var process in others)
            {
                process.Refresh();
            }
            if (others.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) is { } running)
            {
                BringToFront(running.MainWindowHandle);
                return false;
            }
            // 起動したばかりなら、画面が出るまで少し待つ(続けて 2 回ダブルクリックしたときなど)
            if (DateTime.Now > deadline || !others.Any(IsStarting))
            {
                break;
            }
            await Task.Delay(500);
        }

        // 決定ログ BD-2: 画面のない PdfManager が残っている(前に閉じたときに終わりきらなかったものなど)
        if (!await dialogs.ConfirmAsync("PdfManager が残っています",
                $"画面の表示されていない PdfManager が {others.Count} 個動いています(前に閉じたときに終わりきらなかった可能性があります)。\n" +
                "これを終了してから起動しますか?\n\nexe を新しいものに置き換えられないときも、これが原因です。",
                "終了して起動"))
        {
            return false;
        }
        foreach (var process in others)
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // すでに終わっていた、または終了できなかった。起動は続ける
            }
        }
        return true;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static bool IsStarting(Process process)
    {
        try
        {
            return DateTime.Now - process.StartTime < StartupWait;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void BringToFront(IntPtr window)
    {
        if (IsIconic(window))
        {
            ShowWindow(window, SW_RESTORE);
        }
        SetForegroundWindow(window);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
}
