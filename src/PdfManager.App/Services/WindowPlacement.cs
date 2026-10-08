using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PdfManager.App.Services;

// 画面(タスクバーを除いた作業領域)より大きいウィンドウを画面に収める。
// 小さい画面や表示の拡大率が大きい PC で、ウィンドウが画面の外にはみ出し、タイトルバーをつかめなくなるのを防ぐ
public static class WindowPlacement
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    // 各ウィンドウのコンストラクターで呼ぶ。maximizeIfTooLarge は、収まらないときに最大化して開く(メイン画面)
    public static void KeepOnScreen(Window window, bool maximizeIfTooLarge = false)
    {
        window.SourceInitialized += (_, _) =>
        {
            var area = WorkArea(window);
            var tooLarge = window.Width > area.Width || window.Height > area.Height;
            window.MinWidth = Math.Min(window.MinWidth, area.Width);
            window.MinHeight = Math.Min(window.MinHeight, area.Height);
            if (window.Width > area.Width)
            {
                window.Width = area.Width;
            }
            if (window.Height > area.Height)
            {
                window.Height = area.Height;
            }
            if (double.IsNaN(window.Height))
            {
                // 中身に合わせて高さが決まるウィンドウ
                window.MaxHeight = area.Height;
            }
            if (tooLarge && maximizeIfTooLarge)
            {
                window.WindowState = WindowState.Maximized;
            }
        };
        // 中央に置いた結果などで画面の外に出ていたら、画面の中へ戻す
        window.Loaded += (_, _) =>
        {
            if (window.WindowState != WindowState.Normal)
            {
                return;
            }
            var area = WorkArea(window);
            window.Left = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.ActualWidth));
            window.Top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.ActualHeight));
        };
    }

    // ウィンドウがある(いちばん近い)モニターの作業領域。WPF の座標(拡大率を考えた単位)で返す
    private static Rect WorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (handle == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST), ref info)
            || PresentationSource.FromVisual(window)?.CompositionTarget is not { } target)
        {
            return SystemParameters.WorkArea;
        }
        var work = info.rcWork;
        var transform = target.TransformFromDevice;
        return new Rect(transform.Transform(new Point(work.Left, work.Top)), transform.Transform(new Point(work.Right, work.Bottom)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
