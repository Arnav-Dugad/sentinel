using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Sentinel.App.Services;

public static class WindowHelper
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    public static IntPtr Hwnd(Window w) => WinRT.Interop.WindowNative.GetWindowHandle(w);

    public static double Scale(Window w) => Math.Max(1, GetDpiForWindow(Hwnd(w)) / 96.0);

    public static void SetIcon(Window w)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sentinel.ico");
        if (File.Exists(path)) w.AppWindow.SetIcon(path);
    }

    /// <summary>Sizes the window in device-independent pixels, clamped to the current display's work area.</summary>
    public static void Resize(Window w, double width, double height)
    {
        var scale = Scale(w);
        var area = DisplayArea.GetFromWindowId(w.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var pw = (int)Math.Min(width * scale, area.Width * 0.95);
        var ph = (int)Math.Min(height * scale, area.Height * 0.95);
        w.AppWindow.Resize(new SizeInt32(pw, ph));
    }

    public static void Center(Window w)
    {
        var area = DisplayArea.GetFromWindowId(w.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = w.AppWindow.Size;
        w.AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }

    /// <summary>Places a small window above the notification area (bottom-right of the work area, or nearest the cursor's display).</summary>
    public static void PlaceNearTray(Window w)
    {
        var cursor = TrayIcon.CursorPosition;
        var display = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary);
        var area = display.WorkArea;
        var size = w.AppWindow.Size;
        var margin = (int)(12 * Scale(w));
        w.AppWindow.Move(new PointInt32(area.X + area.Width - size.Width - margin, area.Y + area.Height - size.Height - margin));
    }

    public static void BringToFront(Window w)
    {
        w.AppWindow.Show(true);
        if (w.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        w.Activate();
        SetForegroundWindow(Hwnd(w));
    }
}
