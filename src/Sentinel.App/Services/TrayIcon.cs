using System.Runtime.InteropServices;

namespace Sentinel.App.Services;

public enum TrayCommand { Open = 1, QuickPanel, ToggleRecording, TogglePrivacy, MiniMonitor, Exit, InstallUpdate }

/// <summary>
/// Notification-area icon implemented with Shell_NotifyIcon and a message-only window. Left click opens the quick
/// panel; right click shows a deliberately short menu. Re-registers itself if Explorer restarts.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8001;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    private const uint MF_STRING = 0x0, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    private readonly WndProc _proc;
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private string _tip = "Sentinel";
    private bool _added;

    public TrayIcon(string iconPath)
    {
        _proc = Proc;
        var className = "SentinelTrayWindow";
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            lpszClassName = className,
            hInstance = Marshal.GetHINSTANCE(typeof(TrayIcon).Module),
        };
        RegisterClassExW(ref wc);
        _hwnd = CreateWindowExW(0, className, "Sentinel tray", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        var size = Math.Max(16, GetSystemMetrics(49 /* SM_CXSMICON */));
        _icon = LoadImageW(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        Add();
    }

    public event EventHandler<TrayCommand>? Command;

    /// <summary>Provides the current menu check states: (recording paused, privacy mode).</summary>
    public Func<(bool RecordingPaused, bool Privacy)>? MenuState { get; set; }

    /// <summary>When set (e.g. "1.0.1"), the menu offers to restart and install that update.</summary>
    public Func<string?>? PendingUpdate { get; set; }

    public static POINT CursorPosition => GetCursorPos(out var p) ? p : default;

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
        uVersion = 4,
    };

    private void Add()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIconW(NIM_ADD, ref d);
        Shell_NotifyIconW(NIM_SETVERSION, ref d);
    }

    /// <summary>Shows a standard notification-area balloon (respects Windows quiet hours / Focus).</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var d = Data(0x10 /* NIF_INFO */);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.dwInfoFlags = 0x1 /* NIIF_INFO */ | 0x80 /* NIIF_RESPECT_QUIET_TIME */;
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    public event EventHandler? BalloonClicked;

    public void SetTooltip(string text)
    {
        _tip = text.Length > 127 ? text[..127] : text;
        if (!_added) return;
        var d = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            var evt = (int)(lParam.ToInt64() & 0xFFFF);
            switch (evt)
            {
                case WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT:
                    Command?.Invoke(this, TrayCommand.QuickPanel);
                    break;
                case WM_CONTEXTMENU or WM_RBUTTONUP:
                    ShowMenu((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                    break;
                case 0x0405: // NIN_BALLOONUSERCLICK
                    BalloonClicked?.Invoke(this, EventArgs.Empty);
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated)
        {
            Add();
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var state = MenuState?.Invoke() ?? (false, false);
        var menu = CreatePopupMenu();
        if (PendingUpdate?.Invoke() is { } version)
        {
            AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.InstallUpdate, $"Restart to update to {version}");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        }
        AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.Open, "Open Sentinel");
        AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.MiniMonitor, "Mini monitor");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING | (state.RecordingPaused ? MF_CHECKED : 0), (UIntPtr)(uint)TrayCommand.ToggleRecording, "Pause history recording");
        AppendMenuW(menu, MF_STRING | (state.Privacy ? MF_CHECKED : 0), (UIntPtr)(uint)TrayCommand.TogglePrivacy, "Privacy mode");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.Exit, "Exit");
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, x, y, _hwnd, IntPtr.Zero);
        PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd > 0) Command?.Invoke(this, (TrayCommand)cmd);
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = Data(0);
            Shell_NotifyIconW(NIM_DELETE, ref d);
            _added = false;
        }
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        DestroyWindow(_hwnd);
    }
}
