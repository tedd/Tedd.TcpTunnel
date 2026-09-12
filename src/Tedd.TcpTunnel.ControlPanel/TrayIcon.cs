using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace Tedd.TcpTunnel.ControlPanel;

internal sealed class TrayIcon : IDisposable
{
    private readonly Microsoft.UI.Xaml.Window _window;
    private readonly AppWindow _appWindow;
    private readonly nint _handle;
    private readonly SubclassProc _procedure;
    private readonly Func<Task> _exit;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyData _data;
    private bool _available, _exiting;

    public TrayIcon(Microsoft.UI.Xaml.Window window, Func<Task> exit)
    {
        _window = window; _exit = exit;
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_handle));
        _procedure = HandleMessage;
        _data = new NotifyData
        {
            Size = (uint)Marshal.SizeOf<NotifyData>(), Window = _handle, Id = 1, Flags = 1 | 2 | 4,
            Callback = 0x8001, Icon = LoadIcon(0, 32512), Tip = "Tedd TcpTunnel · Open control panel",
            Info = "", InfoTitle = ""
        };
        _available = SetWindowSubclass(_handle, _procedure, 1, 0) && Shell_NotifyIcon(0, ref _data);
        _appWindow.Closing += OnClosing;
    }

    public void PrepareExit() => _exiting = true;

    public void Hide()
    {
        if (_available) _appWindow.Hide();
    }

    private void Restore() { _appWindow.Show(); ShowWindow(_handle, 9); _window.Activate(); }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        args.Cancel = true;
        if (_available) Hide();
        else _ = ExitAsync();
    }

    private async Task ExitAsync()
    {
        await _exit();
        // The page closes the application only after its exit confirmation and daemon shutdown.
    }

    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == _taskbarCreated) _available = Shell_NotifyIcon(0, ref _data);
        if (message == 5 && wParam == 1 && _available) Hide();
        if (message == 0x8001)
        {
            if (lParam is 0x0202 or 0x0203) Restore();
            if (lParam == 0x0205)
            {
                var menu = CreatePopupMenu();
                try
                {
                    AppendMenu(menu, 0, 1, "Open control panel");
                    AppendMenu(menu, 0, 2, "Exit");
                    GetCursorPos(out var point);
                    SetForegroundWindow(_handle);
                    var selected = TrackPopupMenu(menu, 0x100 | 2, point.X, point.Y, 0, _handle, 0);
                    if (selected == 1) Restore();
                    if (selected == 2) { Restore(); _ = ExitAsync(); }
                }
                finally { DestroyMenu(menu); }
            }
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        _exiting = true;
        _appWindow.Closing -= OnClosing;
        Shell_NotifyIcon(2, ref _data);
        RemoveWindowSubclass(_handle, _procedure, 1);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
}
