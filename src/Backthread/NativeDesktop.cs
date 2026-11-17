using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Backthread;

internal sealed record LiveWindow(nint Handle, int ProcessId, nint Identity, string Title, string AppName);

internal enum ActivationResult { Activated, Unavailable, Blocked }

// The HWND, hook, subclass and every call into this object belong to the same UI thread.
internal sealed class NativeDesktop : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0;
    private const uint WmHotkey = 0x0312;
    private const uint WmNcDestroy = 0x0082;
    private const uint ModAltControlNoRepeat = 0x4003;
    private const int ReturnHotkey = 0x5441;
    private const int CheckpointHotkey = 0x5442;
    private const uint SubclassId = 0x4254;
    private const uint GaRoot = 2;
    private const uint DwmwaCloaked = 14;
    private const int SwRestore = 9;
    private const int MarkerLimit = 1024;
    private const int PendingLimit = 256;
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "MultitaskingViewFrame",
        "TaskListThumbnailWnd", "XamlExplorerHostIslandWindow", "#32768"
    };
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost", "TextInputHost", "LockApp"
    };

    private readonly nint _owner;
    private readonly WinEventProc _onWinEvent;
    private readonly SubclassProc _onWindowMessage;
    private readonly string _markerName = "Backthread.Window." + Guid.NewGuid().ToString("N");
    private readonly Queue<LiveWindow?> _pending = new();
    private readonly LinkedList<Marker> _age = new();
    private readonly Dictionary<nint, LinkedListNode<Marker>> _markers = new();
    private readonly uint _dispatchMessage;
    private GCHandle _lifetime;
    private nint _nextMarker;
    private nint _hook;
    private bool _subclassed;
    private bool _draining;
    private bool _disposed;

    internal NativeDesktop(nint ownerHwnd, char returnKey, char checkpointKey)
    {
        _owner = ownerHwnd;
        _onWinEvent = OnWinEvent;
        _onWindowMessage = OnWindowMessage;
        _dispatchMessage = RegisterWindowMessage("Backthread.ForegroundDispatch.v1");
        _subclassed = SetWindowSubclass(_owner, _onWindowMessage, SubclassId, 0);
        if (!_subclassed)
            return;

        // Native code keeps only function pointers. Keep this instance and
        // both managed delegates rooted until the subclass and hook are removed.
        _lifetime = GCHandle.Alloc(this);
        ReturnHotkeyRegistered = RegisterHotKey(_owner, ReturnHotkey, ModAltControlNoRepeat, returnKey);
        CheckpointHotkeyRegistered = RegisterHotKey(_owner, CheckpointHotkey, ModAltControlNoRepeat, checkpointKey);
        if (_dispatchMessage != 0)
            _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, _onWinEvent, 0, 0, WinEventOutOfContext);
    }

    public event Action<LiveWindow?>? ForegroundChanged;
    public event Action? ReturnRequested;
    public event Action? CheckpointRequested;
    internal Func<uint, nuint, nint, bool>? HandleShellMessage { get; set; }

    public bool IsTracking => _hook != 0;
    public bool ReturnHotkeyRegistered { get; private set; }
    public bool CheckpointHotkeyRegistered { get; private set; }

    public LiveWindow? GetForeground() => _disposed ? null : Describe(GetForegroundWindow());

    internal bool ChangeShortcut(bool checkpoint, char previous, char next)
    {
        if (!_subclassed || _disposed) return false;
        int id = checkpoint ? CheckpointHotkey : ReturnHotkey;
        bool registered = checkpoint ? CheckpointHotkeyRegistered : ReturnHotkeyRegistered;
        if (registered && previous == next) return true;
        if (registered) UnregisterHotKey(_owner, id);
        bool changed = RegisterHotKey(_owner, id, ModAltControlNoRepeat, next);
        bool active = changed || (registered && RegisterHotKey(_owner, id, ModAltControlNoRepeat, previous));
        if (checkpoint) CheckpointHotkeyRegistered = active;
        else ReturnHotkeyRegistered = active;
        return changed;
    }

    internal bool CanActivate(LiveWindow window) =>
        IsAlive(window) && IsWindowVisible(window.Handle) && !IsCloaked(window.Handle);

    public bool IsAlive(LiveWindow window)
    {
        if (_disposed || !IsWindow(window.Handle) ||
            GetWindowThreadProcessId(window.Handle, out var pid) == 0 || pid != (uint)window.ProcessId)
            return false;
        return GetProp(window.Handle, _markerName) == window.Identity && window.Identity != 0;
    }

    public ActivationResult Activate(LiveWindow window)
    {
        if (!IsAlive(window))
            return ActivationResult.Unavailable;

        var handle = window.Handle;
        if (!IsWindowVisible(handle) || IsCloaked(handle))
            return ActivationResult.Blocked;
        if (GetForegroundWindow() == handle)
            return ActivationResult.Activated;

        if (IsIconic(handle) && !ShowWindowAsync(handle, SwRestore))
            return ActivationResult.Blocked;
        if (!IsAlive(window))
            return ActivationResult.Unavailable;

        // Windows still owns the foreground decision. If it declines, do not
        // synthesize input, attach input queues, or target a replacement window.
        return SetForegroundWindow(handle) || GetForegroundWindow() == handle
            ? ActivationResult.Activated : ActivationResult.Blocked;
    }

    private LiveWindow? Describe(nint handle)
    {
        if (handle == 0 || handle == GetDesktopWindow() || handle == GetShellWindow())
            return null;
        var root = GetAncestor(handle, GaRoot);
        if (root != 0)
            handle = root;
        if (!IsWindowVisible(handle) || IsCloaked(handle) ||
            GetWindowThreadProcessId(handle, out var pid) == 0 || pid == 0 || pid == (uint)Environment.ProcessId)
            return null;

        var windowClass = new StringBuilder(256);
        GetClassName(handle, windowClass, windowClass.Capacity);
        if (ShellClasses.Contains(windowClass.ToString()))
            return null;

        var title = new StringBuilder(513);
        if (GetWindowText(handle, title, title.Capacity) == 0)
            return null;
        var caption = title.ToString().Trim();
        if (caption.Length == 0)
            return null;

        var appName = ProcessLabel((int)pid);
        if (ShellProcesses.Contains(appName))
            return null;

        // HWND and PID can both be reused. A per-window property survives title
        // changes but is not inherited by a new window at the same HWND.
        nint marker = GetOrSetMarker(handle);
        if (marker == 0 || GetWindowThreadProcessId(handle, out var verifiedPid) == 0 || verifiedPid != pid)
            return null;

        return new LiveWindow(handle, (int)pid, marker, caption, appName);
    }

    private nint GetOrSetMarker(nint handle)
    {
        if (_markers.TryGetValue(handle, out var node))
        {
            if (GetProp(handle, _markerName) == node.Value.Value)
            {
                _age.Remove(node);
                _age.AddLast(node);
                return node.Value.Value;
            }
            ForgetMarker(node, false);
        }
        if (_markers.Count >= MarkerLimit)
            PruneMarkers();

        // Zero cannot be used because GetProp also reports 0 for missing.
        do { _nextMarker = unchecked(_nextMarker + 1); } while (_nextMarker == 0);
        if (!SetProp(handle, _markerName, _nextMarker))
            return 0; // e.g. a higher integrity window; fail closed.
        _markers[handle] = _age.AddLast(new Marker(handle, _nextMarker));
        return _nextMarker;
    }

    private void PruneMarkers()
    {
        for (var node = _age.First; node is not null;)
        {
            var next = node.Next;
            if (GetProp(node.Value.Handle, _markerName) != node.Value.Value)
                ForgetMarker(node, false);
            node = next;
        }
        if (_markers.Count >= MarkerLimit && _age.First is { } oldest)
            ForgetMarker(oldest, true);
    }

    private void ForgetMarker(LinkedListNode<Marker> node, bool removeProperty)
    {
        var marker = node.Value;
        if (removeProperty && GetProp(marker.Handle, _markerName) == marker.Value)
            RemoveProp(marker.Handle, _markerName);
        _markers.Remove(marker.Handle);
        _age.Remove(node);
    }

    private static string ProcessLabel(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return "Windows 应用";
        }
    }

    private static bool IsCloaked(nint handle) =>
        DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private void OnWinEvent(nint hook, uint evt, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || objectId != 0 || childId != 0)
            return;

        InvokeSafely(() =>
        {
            // Snapshot the window before queuing it. Only the posted message
            // notifies subscribers, so UI behavior cannot reenter the hook.
            var window = Describe(hwnd);
            if (_pending.Count == PendingLimit)
                _pending.Dequeue();
            _pending.Enqueue(window);
            if (!_draining && _pending.Count == 1 && !PostMessage(_owner, _dispatchMessage, 0, 0))
                _pending.Clear();
        });
    }

    private nint OnWindowMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmNcDestroy)
            Dispose();

        if (!_disposed)
        {
            if (message == WmHotkey && ((wParam == ReturnHotkey && ReturnHotkeyRegistered) ||
                                       (wParam == CheckpointHotkey && CheckpointHotkeyRegistered)))
            {
                InvokeSafely(wParam == ReturnHotkey ? ReturnRequested : CheckpointRequested);
                return 0;
            }
            if (message == _dispatchMessage && message != 0)
            {
                DrainPending();
                return 0;
            }
            bool handled = false;
            if (HandleShellMessage is { } handler)
                InvokeSafely(() => handled = handler(message, wParam, lParam));
            if (handled) return 0;
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void DrainPending()
    {
        if (_draining)
            return;
        _draining = true;
        try
        {
            while (!_disposed && _pending.TryDequeue(out var window))
                InvokeSafely(() => ForegroundChanged?.Invoke(window));
        }
        finally
        {
            _draining = false;
        }
    }

    // Never unwind a managed exception through a native window procedure.
    private static void InvokeSafely(Action? action)
    {
        try { action?.Invoke(); }
        catch (Exception ex) { Debug.WriteLine($"Backthread UI callback failed: {ex.GetType().Name}"); }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
            _hook = 0;
        }
        if (ReturnHotkeyRegistered)
            UnregisterHotKey(_owner, ReturnHotkey);
        if (CheckpointHotkeyRegistered)
            UnregisterHotKey(_owner, CheckpointHotkey);
        ReturnHotkeyRegistered = CheckpointHotkeyRegistered = false;
        if (_subclassed)
        {
            RemoveWindowSubclass(_owner, _onWindowMessage, SubclassId);
            _subclassed = false;
        }

        _pending.Clear();
        while (_age.First is { } node)
            ForgetMarker(node, true);
        if (_lifetime.IsAllocated)
            _lifetime.Free();
    }

    private readonly record struct Marker(nint Handle, nint Value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProc(nint hook, uint evt, nint hwnd, int objectId, int childId, uint thread, uint time);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowText(nint hwnd, StringBuilder title, int maxCount);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool SetProp(nint hwnd, string name, nint value);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetProp(nint hwnd, string name);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint RemoveProp(nint hwnd, string name);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
