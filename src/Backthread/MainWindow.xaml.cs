using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace Backthread;

public sealed partial class MainWindow : Window
{
    private const long IdleMs = 120_000;
    private readonly Trail trail = new();
    private readonly NativeDesktop desktop;
    private readonly TrayIcon tray;
    private readonly DispatcherQueueTimer timer;
    private readonly CancellationTokenSource shutdown = new();
    private readonly nint hwnd;
    private Preferences preferences = Preferences.Load();
    private LiveWindow? source;
    private long lastTick = Environment.TickCount64;
    private bool paused;
    private bool idle;
    private bool actionPending;
    private bool dialogOpen;
    private bool exiting;
    private bool disposed;
    private bool dirty;
    private bool changingSettings = true;

    public MainWindow()
    {
        InitializeComponent();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Backthread.ico");
        AppWindow.SetIcon(icon);
        desktop = new NativeDesktop(hwnd, preferences.SwitchKey, preferences.SaveKey);
        tray = new TrayIcon(hwnd, icon);
        desktop.HandleShellMessage = tray.OnMessage;
        tray.Invoked += OnTrayAction;
        desktop.ForegroundChanged += Observe;
        desktop.ReturnRequested += () => _ = SwitchDirectly();
        desktop.CheckpointRequested += SaveForeground;

        if (InputIdle() < IdleMs) Record(desktop.GetForeground(), lastTick);
        InitializeSettings();
        VersionLabel.Text = $"版本 {Updates.Current} · Windows x64";
        AppWindow.Closing += (_, args) =>
        {
            if (exiting || !preferences.CloseToTray || !tray.Available) return;
            args.Cancel = true;
            AppWindow.Hide();
        };
        timer = DispatcherQueue.CreateTimer();
        Closed += (_, _) =>
        {
            disposed = true;
            shutdown.Cancel();
            shutdown.Dispose();
            timer.Stop();
            tray.Dispose();
            desktop.Dispose();
        };
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => Pulse();
        timer.Start();
        Navigation.SelectedIndex = 0;
        ShowPage();
    }

    internal void Start(bool background)
    {
        if (!background || !tray.Available) OpenDashboard();
        if (preferences.CheckOnLaunch) _ = CheckUpdatesAsync(false);
    }

    internal void OpenDashboard()
    {
        if (disposed || dialogOpen || actionPending) return;
        bool resize = !AppWindow.IsVisible;
        CaptureSource();
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        if (presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        if (resize || !preferences.Welcomed) PlaceWindow(960, 690);
        ApplyTheme();
        ShowAndActivate();
        RenderWorkspace();

        if (!preferences.Welcomed)
        {
            SetStatus($"按 {Shortcut(preferences.SwitchKey)} 直接返回，按 {Shortcut(preferences.SaveKey)} 标记当前窗口优先返回。");
            Store(preferences with { Welcomed = true });
        }
    }

    private void ShowAndActivate()
    {
        if (!AppWindow.IsVisible) AppWindow.Show();
        Activate();
    }

    private void PlaceWindow(int width, int height)
    {
        var window = source is { } current && desktop.IsAlive(current) ? current.Handle : hwnd;
        var display = DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(window), DisplayAreaFallback.Primary);
        var area = display.WorkArea;
        var monitor = MonitorFromWindow(window, 2);
        var scale = GetScaleFactorForMonitor(monitor, out int factor) == 0 ? factor / 100.0 : 1;
        if (scale <= 0) scale = 1;

        int w = Math.Min((int)(width * scale), area.Width - 16);
        int h = Math.Min((int)(height * scale), area.Height - 16);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2,
            area.Y + (area.Height - h) / 2, w, h));
    }

    private void OnTrayAction(TrayAction action)
    {
        switch (action)
        {
            case TrayAction.Dashboard: OpenDashboard(); break;
            case TrayAction.Pause: TogglePause(); break;
            case TrayAction.Exit:
                exiting = true;
                Close();
                break;
        }
    }

    private void CaptureSource()
    {
        var current = desktop.GetForeground();
        if (current is not null) source = current;
        if (current is not null && !paused && InputIdle() < IdleMs)
            Record(current, Environment.TickCount64);
    }

    private void Record(LiveWindow? window, long tick)
    {
        if (window is not null) source = window;
        if (trail.Observe(window, tick)) dirty = true;
    }

    private void Observe(LiveWindow? window)
    {
        if (!paused && InputIdle() < IdleMs) Record(window, Environment.TickCount64);
    }

    private void Pulse()
    {
        var now = Environment.TickCount64;
        var sinceInput = InputIdle();
        bool wasIdle = idle;
        idle = sinceInput >= IdleMs;
        if (!paused)
        {
            if (idle || now - lastTick > 8000)
                Record(null, now - lastTick > 8000 ? lastTick : now - (sinceInput - IdleMs));
            if (!idle && (wasIdle || desktop.IsTracking || !AppWindow.IsVisible))
                Record(desktop.GetForeground(), now);
        }
        lastTick = now;

        if (!AppWindow.IsVisible || actionPending || dialogOpen) return;
        if (dirty && Navigation.SelectedIndex == 0) RenderHistory();
    }

    private void SaveForeground()
    {
        if (actionPending || dialogOpen) return;
        var current = desktop.GetForeground();
        if (current is null || !desktop.CanActivate(current)) return;

        if (!paused) Record(current, Environment.TickCount64);
        trail.Mark(current);
        dirty = true;
        SetStatus($"已标记「{current.Title}」。按 {Shortcut(preferences.SwitchKey)} 优先返回。");
        if (AppWindow.IsVisible && Navigation.SelectedIndex == 0) RenderHistory();
    }

    private async Task SwitchDirectly()
    {
        if (disposed || actionPending || dialogOpen) return;
        var current = desktop.GetForeground();
        var now = Environment.TickCount64;
        if (current is not null && !paused) Record(current, now);

        var target = trail.ReturnTarget(now, current, desktop.CanActivate);
        if (target is not null) await Jump(target);
    }

    private void RenderWorkspace()
    {
        if (PageHeading is null) return;
        RecordingState.Text = paused ? "●  记录已暂停" : "●  正在记录本次会话";
        PauseButton.Content = paused ? "恢复记录" : "暂停记录";
        if (Navigation.SelectedIndex == 0) RenderHistory();
    }

    private void RenderHistory()
    {
        dirty = false;
        ShortcutHint.Text = $"{Shortcut(preferences.SwitchKey)} 直接返回  ·  {Shortcut(preferences.SaveKey)} 标记当前窗口";
        var now = Environment.TickCount64;
        var marked = trail.MarkedTarget(null, desktop.CanActivate);
        var recent = trail.Recent(null, desktop.CanActivate).ToList();
        if (marked is not null)
        {
            int index = recent.FindIndex(segment => Trail.Same(segment.Window, marked));
            var markedSegment = index >= 0 ? recent[index] : new Segment(marked, now, now);
            if (index >= 0) recent.RemoveAt(index);
            recent.Insert(0, markedSegment);
        }

        var terms = HistoryQuery.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = recent.Where(segment => terms.All(term =>
                segment.Window.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                segment.Window.AppName.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(segment => new Result(segment.Window, segment.Window.Title,
                $"{segment.Window.AppName}  ·  停留 {Duration(segment.Seconds(now))}",
                Trail.Same(marked, segment.Window) ? "优先返回" : "", "\uE737")).ToList();

        if (HistoryList.ItemsSource is not List<Result> previous || !previous.SequenceEqual(rows))
            HistoryList.ItemsSource = rows;
        HistoryEmpty.Text = terms.Length == 0 ? "切换几个窗口后，就能从这里返回。" : "没有匹配的窗口。";
        HistoryEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPage()
    {
        if (AboutPage is null) return;
        FrameworkElement[] pages = [HistoryPage, SettingsPage, AboutPage];
        string[] headings = ["最近窗口", "设置", "关于"];
        int index = Math.Clamp(Navigation.SelectedIndex, 0, pages.Length - 1);
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = index == i ? Visibility.Visible : Visibility.Collapsed;
        PageHeading.Text = headings[index];
        RenderWorkspace();
    }

    private void Navigate(object sender, SelectionChangedEventArgs args)
    {
        if (!changingSettings) ShowPage();
    }

    private async Task Jump(LiveWindow target)
    {
        if (actionPending || dialogOpen) return;
        // Clicking an already active window should preserve its mark for a later return.
        if (desktop.IsForeground(target)) return;

        actionPending = true;
        try
        {
            var result = desktop.Activate(target);
            if (result != ActivationResult.Unavailable && !desktop.IsForeground(target))
            {
                for (int attempt = 0; attempt < 4 && !disposed; attempt++)
                {
                    await Task.Delay(50);
                    if (disposed || desktop.IsForeground(target)) break;
                }
            }
            if (disposed) return;
            if (desktop.IsForeground(target))
            {
                trail.ConsumeMark(target);
                if (!paused) Record(target, Environment.TickCount64);
                dirty = true;
                SetStatus("");
                if (AppWindow.IsVisible) AppWindow.Hide();
                return;
            }

            SetStatus(desktop.IsAlive(target) ? "Windows 未允许切换。目标可能在另一个桌面。"
                : "原窗口已关闭。");
        }
        finally
        {
            actionPending = false;
            if (!disposed && AppWindow.IsVisible) RenderWorkspace();
        }
    }

    private void HistorySearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!changingSettings && !actionPending) RenderHistory();
    }

    private async void HistoryItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Result row) await Jump(row.Window);
    }

    private async void HistoryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var row = FocusManager.GetFocusedElement(Root.XamlRoot) switch
        {
            ListViewItem { Content: Result item } => item,
            TextBox => HistoryList.Items.FirstOrDefault() as Result,
            _ => null
        };
        if (row is null) return;
        e.Handled = true;
        await Jump(row.Window);
    }

    private async Task<bool> ConfirmAsync(string title, object content, string command, ContentDialogButton preferred)
    {
        if (dialogOpen || actionPending) return false;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = content,
            PrimaryButtonText = command, CloseButtonText = "取消", DefaultButton = preferred };
        dialogOpen = true;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { dialogOpen = false; }
    }

    private void TogglePauseClick(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
    {
        if (actionPending || dialogOpen) return;
        paused = !paused;
        if (paused) Record(null, Environment.TickCount64);
        else if (InputIdle() < IdleMs) Record(desktop.GetForeground(), Environment.TickCount64);
        tray.SetPaused(paused);
        SetStatus(paused ? "已暂停记录。已有记录仍可返回。" : "已恢复记录。");
        RenderWorkspace();

    }

    private async void ClearSession(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("清空本次会话？", "最近窗口和优先标记都会清空。", "清空",
                ContentDialogButton.Close) || disposed) return;
        trail.Clear();
        source = null;
        RenderWorkspace();
        SetStatus("已清空本次会话。");
    }

    private void InitializeSettings()
    {
        var letters = Enumerable.Range('A', 26).Select(code => ((char)code).ToString()).ToArray();
        SwitchKey.ItemsSource = letters;
        SaveKey.ItemsSource = letters;
        SwitchKey.SelectedItem = preferences.SwitchKey.ToString();
        SaveKey.SelectedItem = preferences.SaveKey.ToString();
        KeepRunning.IsOn = preferences.CloseToTray;
        Startup.IsOn = Preferences.StartsWithWindows();
        AutoCheck.IsOn = preferences.CheckOnLaunch;
        Theme.SelectedIndex = preferences.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        ApplyTheme();
        ShortcutFeedback();
        changingSettings = false;
    }

    private void SwitchKeyChanged(object sender, SelectionChangedEventArgs e) => ChangeShortcut(SwitchKey, false);
    private void SaveKeyChanged(object sender, SelectionChangedEventArgs e) => ChangeShortcut(SaveKey, true);

    private void ChangeShortcut(ComboBox control, bool checkpoint)
    {
        if (changingSettings || control.SelectedItem is not string { Length: 1 } key) return;
        char previous = checkpoint ? preferences.SaveKey : preferences.SwitchKey;
        char other = checkpoint ? preferences.SwitchKey : preferences.SaveKey;
        if (key[0] != other && desktop.ChangeShortcut(checkpoint, previous, key[0]))
        {
            Store(checkpoint ? preferences with { SaveKey = key[0] } : preferences with { SwitchKey = key[0] });
            ShortcutFeedback();
            RenderWorkspace();
            return;
        }
        changingSettings = true;
        control.SelectedItem = previous.ToString();
        changingSettings = false;
        ShortcutFeedback(key[0] == other ? "两项快捷键需要使用不同的字母。" : $"{Shortcut(key[0])} 已被占用。");
    }

    private void RetryShortcutClick(object sender, RoutedEventArgs e)
    {
        desktop.ChangeShortcut(false, preferences.SwitchKey, preferences.SwitchKey);
        desktop.ChangeShortcut(true, preferences.SaveKey, preferences.SaveKey);
        ShortcutFeedback();
    }

    private void ShortcutFeedback(string? message = null)
    {
        bool conflict = !desktop.ReturnHotkeyRegistered || !desktop.CheckpointHotkeyRegistered;
        RetryShortcuts.Visibility = conflict ? Visibility.Visible : Visibility.Collapsed;
        var missing = new List<string>();
        if (!desktop.ReturnHotkeyRegistered) missing.Add(Shortcut(preferences.SwitchKey));
        if (!desktop.CheckpointHotkeyRegistered) missing.Add(Shortcut(preferences.SaveKey));
        ShortcutStatus.Text = message ?? (conflict ? $"{string.Join("、", missing)} 注册失败，可改用其他字母或使用托盘。"
            : "快捷键可用。");
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (changingSettings) return;
        try { Preferences.SetStartup(Startup.IsOn); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            changingSettings = true;
            Startup.IsOn = Preferences.StartsWithWindows();
            changingSettings = false;
            SetStatus("无法修改开机启动。请检查 Windows 对启动项的限制。");
        }
    }

    private void KeepRunningChanged(object sender, RoutedEventArgs e)
    {
        if (!changingSettings) Store(preferences with { CloseToTray = KeepRunning.IsOn });
    }

    private void AutoCheckChanged(object sender, RoutedEventArgs e)
    {
        if (!changingSettings) Store(preferences with { CheckOnLaunch = AutoCheck.IsOn });
    }

    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingSettings || Theme.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        Store(preferences with { Theme = value });
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = Enum.Parse<ElementTheme>(preferences.Theme);
        if (AppWindowTitleBar.IsCustomizationSupported())
            AppWindow.TitleBar.PreferredTheme = Root.RequestedTheme switch
            {
                ElementTheme.Dark => TitleBarTheme.Dark,
                ElementTheme.Light => TitleBarTheme.Light,
                _ => TitleBarTheme.UseDefaultAppMode
            };
    }

    private void Store(Preferences value)
    {
        preferences = value;
        try { preferences.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetStatus("无法写入设置。修改在本次运行期间有效。");
        }
    }

    private async void CheckUpdateClick(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(true);

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (!CheckButton.IsEnabled) return;
        CheckButton.IsEnabled = false;
        UpdateStatus.Text = "正在检查 GitHub 发行版…";
        DownloadUpdate.Visibility = UpdateNotes.Visibility = Visibility.Collapsed;
        try
        {
            var update = await Updates.CheckAsync(shutdown.Token);
            if (disposed) return;
            if (update is null)
            {
                UpdateStatus.Text = "当前没有更新的稳定安装包。";
                return;
            }
            UpdateStatus.Text = $"版本 {update.Version} 已发布。";
            DownloadUpdate.NavigateUri = update.Download;
            UpdateNotes.NavigateUri = update.Notes;
            DownloadUpdate.Visibility = UpdateNotes.Visibility = Visibility.Visible;
            if (!manual)
            {
                SetStatus($"回线 {update.Version} 已发布，可在「关于」下载。");
                if (!AppWindow.IsVisible) tray.Feedback($"版本 {update.Version} 已发布，可在工作台的「关于」下载。");
            }
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or OperationCanceledException or
            JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (!disposed) UpdateStatus.Text = "检查更新失败，请检查网络后重试。";
        }
        finally { if (!disposed) CheckButton.IsEnabled = true; }
    }

    private void SetStatus(string message) => WorkspaceStatus.Text = message;

    private static string Shortcut(char key) => $"Ctrl+Alt+{key}";
    private static string Duration(long seconds) => seconds < 60 ? $"{seconds} 秒" : $"{seconds / 60} 分 {seconds % 60:00} 秒";

    private static long InputIdle()
    {
        var info = new LastInput { Size = 8 };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Tick) : 0;
    }

    private sealed record Result(LiveWindow Window, string Title, string Detail, string Badge, string Icon)
    {
        public override string ToString() => Title;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size, Tick; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(nint monitor, out int scale);
}
