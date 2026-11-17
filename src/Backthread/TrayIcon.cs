using System.Runtime.InteropServices;

namespace Backthread;

internal enum TrayAction { Switch = 1, Dashboard, Pause, Exit }

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8042;
    private const uint MessageAndTip = 7;
    private const uint MenuFlags = 0x0182;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly nint hwnd;
    private readonly nint icon;
    private bool paused;

    internal TrayIcon(nint hwnd, string path)
    {
        this.hwnd = hwnd;
        icon = LoadImage(0, path, 1, 0, 0, 0x50);
        Add();
    }

    internal bool Available { get; private set; }
    internal event Action<TrayAction>? Invoked;

    private Notification Data(uint flags) => new()
    {
        Size = Marshal.SizeOf<Notification>(), Window = hwnd, Id = 1, Flags = flags,
        Callback = CallbackMessage, Icon = icon, Tip = paused ? "回线 · 已暂停" : "回线 · Backthread",
        Info = "", InfoTitle = ""
    };

    private void Add()
    {
        var data = Data(MessageAndTip);
        Available = icon != 0 && ShellNotifyIcon(0, ref data);
    }

    internal bool OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == taskbarCreated && taskbarCreated != 0)
        {
            Add();
            return true;
        }
        if (message != CallbackMessage) return false;
        if ((int)lParam == 0x0202) Invoked?.Invoke(TrayAction.Switch);
        if ((int)lParam == 0x0205) ShowMenu();
        return true;
    }

    internal void SetPaused(bool value)
    {
        paused = value;
        var data = Data(4);
        ShellNotifyIcon(1, ref data);
    }

    internal void Feedback(string message)
    {
        var data = Data(16);
        data.Info = message;
        data.InfoTitle = "回线";
        data.InfoFlags = 1;
        ShellNotifyIcon(1, ref data);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        uint selected = 0;
        try
        {
            AppendMenu(menu, 0, (uint)TrayAction.Switch, "快速切换");
            AppendMenu(menu, 0, (uint)TrayAction.Dashboard, "打开工作台");
            AppendMenu(menu, 0, (uint)TrayAction.Pause, paused ? "恢复记录" : "暂停记录");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, (uint)TrayAction.Exit, "退出回线");
            SetForegroundWindow(hwnd);
            GetCursorPos(out var point);
            selected = TrackPopupMenu(menu, MenuFlags, point.X, point.Y, 0, hwnd, 0);
            PostMessage(hwnd, 0, 0, 0);
        }
        finally { DestroyMenu(menu); }
        if (selected != 0) Invoked?.Invoke((TrayAction)selected);
    }

    public void Dispose()
    {
        var data = Data(0);
        ShellNotifyIcon(2, ref data);
        if (icon != 0) DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Notification
    {
        public int Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] private static extern bool ShellNotifyIcon(uint message, ref Notification data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
}
