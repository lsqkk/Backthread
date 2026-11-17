using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace Backthread;

public sealed partial class MainWindow : Window
{
    private const long IdleMs = 120_000;
    private const int PaletteLimit = 8;
    private const string PaletteKeys = "↑↓ 选择　↵ 返回　Ctrl+Tab 换页　Esc 关闭";
    private readonly Trail trail = new();
    private readonly NativeDesktop desktop;
    private readonly TrayIcon tray;
    private readonly DispatcherQueueTimer timer;
    private readonly CancellationTokenSource shutdown = new();
    private readonly nint hwnd;
    private Preferences preferences = Preferences.Load();
    private LiveWindow? source;
    private Result? suggestion;
    private long lastTick = Environment.TickCount64;
    private bool paletteOpen;
    private bool paletteFocused;
    private bool showStack;
    private bool fromWorkspace;
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
        desktop.ReturnRequested += () => TogglePalette();
        desktop.CheckpointRequested += SaveForeground;

        if (InputIdle() < IdleMs) Record(desktop.GetForeground(), lastTick);
        InitializeSettings();
        VersionLabel.Text = $"版本 {Updates.Current} · Windows x64";
        Root.PreviewKeyDown += PaletteKeyDown;
        Activated += (_, args) =>
        {
            if (!paletteOpen) return;
            if (args.WindowActivationState != WindowActivationState.Deactivated) paletteFocused = true;
            else if (paletteFocused && !actionPending) HidePalette(false);
        };
        AppWindow.Closing += (_, args) =>
        {
            if (exiting || !preferences.CloseToTray || !tray.Available) return;
            args.Cancel = true;
            if (paletteOpen) HidePalette(false);
            else AppWindow.Hide();
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
        bool resize = paletteOpen || !AppWindow.IsVisible;
        CaptureSource();
        paletteOpen = false;
        fromWorkspace = false;
        Palette.Visibility = Visibility.Collapsed;
        Workspace.Visibility = Visibility.Visible;
        AppWindow.IsShownInSwitchers = true;
        var presenter = Presenter();
        presenter.IsAlwaysOnTop = false;
        presenter.SetBorderAndTitleBar(true, true);
        presenter.IsResizable = true;
        presenter.IsMaximizable = true;
        presenter.PreferredMinimumWidth = 0;
        presenter.PreferredMinimumHeight = 0;
        if (presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        if (resize || !preferences.Welcomed) PlaceWindow(960, 690);
        ApplyTheme();
        ShowAndActivate();
        RenderWorkspace();

        if (!preferences.Welcomed)
        {
            SetStatus($"按 {Shortcut(preferences.SwitchKey)} 呼出浮窗，按 {Shortcut(preferences.SaveKey)} 为当前窗口留线。");
            Store(preferences with { Welcomed = true });
        }
    }

    private void TogglePalette(bool returnToWorkspace = false)
    {
        if (disposed || dialogOpen || actionPending) return;
        if (paletteOpen)
        {
            HidePalette(true);
            return;
        }

        CaptureSource();
        fromWorkspace = returnToWorkspace;
        paletteFocused = false;
        showStack = false;
        Query.Text = "";
        Workspace.Visibility = Visibility.Collapsed;
        Palette.Visibility = Visibility.Visible;
        AppWindow.IsShownInSwitchers = false;
        var presenter = Presenter();
        if (presenter.State != OverlappedPresenterState.Restored) presenter.Restore();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsAlwaysOnTop = true;
        PlaceWindow(540, 510);
        paletteOpen = true;
        SetStatus(PaletteKeys);
        RenderPalette();
        ShowAndActivate();
        paletteFocused = GetForegroundWindow() == hwnd;
        Query.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (paletteOpen && Query.Text.Length == 0) Query.Focus(FocusState.Programmatic);
        });
    }

    private OverlappedPresenter Presenter() => (OverlappedPresenter)AppWindow.Presenter;

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

    private void HidePalette(bool restoreWorkspace)
    {
        if (!paletteOpen) return;
        if (restoreWorkspace && fromWorkspace)
        {
            OpenDashboard();
            return;
        }
        paletteOpen = false;
        paletteFocused = false;
        fromWorkspace = false;
        Palette.Visibility = Visibility.Collapsed;
        AppWindow.Hide();
    }

    private void OnTrayAction(TrayAction action)
    {
        switch (action)
        {
            case TrayAction.Switch: TogglePalette(); break;
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

        if (!AppWindow.IsVisible || paletteOpen || actionPending || dialogOpen) return;
        if (dirty && Navigation.SelectedIndex == 0) RenderHistory();
        else if (Navigation.SelectedIndex == 1) RenderCheckpoints(dirty);
    }

    private void SaveForeground()
    {
        if (actionPending || dialogOpen) return;
        if (paused) { CheckpointFeedback("记录已暂停。恢复后即可留线。"); return; }
        var target = desktop.GetForeground();
        if (target is null) { CheckpointFeedback("先切到要留线的窗口。"); return; }
        SaveCheckpoint(target, "");
    }

    private void SaveCheckpoint(LiveWindow target, string note)
    {
        if (!desktop.CanActivate(target)) { CheckpointFeedback("原窗口已不可用。"); return; }
        if (Trail.Same(trail.Checkpoints.LastOrDefault()?.Window, target) && note.Trim().Length == 0)
        {
            CheckpointFeedback("这个窗口已经在栈顶。");
            return;
        }
        if (!trail.Push(target, note, DateTimeOffset.Now))
        {
            CheckpointFeedback("复工栈已满，请先处理栈顶。");
            return;
        }

        CheckpointFeedback($"已留线 · 复工栈 {trail.Checkpoints.Count} 层");
        if (AppWindow.IsVisible && !paletteOpen) RenderWorkspace();
    }

    private void CheckpointFeedback(string message)
    {
        SetStatus(message);
        if (!AppWindow.IsVisible) tray.Feedback(message);
    }

    private List<Result> Results()
    {
        var now = Environment.TickCount64;
        var suggested = trail.Suggest(now, desktop.CanActivate);
        List<Result> rows = [];
        if (suggested is not null && !Trail.Same(suggested.Window, source))
            rows.Add(WindowResult(suggested, now, "建议返回"));
        foreach (var segment in trail.Recent(source, desktop.CanActivate))
        {
            if (rows.Count == 0 || !Trail.Same(rows[0].Window, segment.Window))
                rows.Add(WindowResult(segment, now));
        }
        return rows;
    }

    private static Result WindowResult(Segment segment, long now, string badge = "") =>
        new(segment.Window, segment.Window.Title,
            $"{segment.Window.AppName}  ·  停留 {Duration(segment.Seconds(now))}", badge, "\uE737", false);

    private List<Result> CheckpointResults()
    {
        var rows = new List<Result>();
        for (int i = trail.Checkpoints.Count - 1; i >= 0; i--)
        {
            var point = trail.Checkpoints[i];
            bool top = i == trail.Checkpoints.Count - 1;
            bool live = desktop.CanActivate(point.Window);
            string detail = point.Note.Length == 0 ? point.Window.AppName : $"{point.Window.Title} · {point.Window.AppName}";
            rows.Add(new(point.Window, point.Note.Length == 0 ? point.Window.Title : point.Note,
                $"{point.SavedAt:HH:mm}  ·  {detail}", !live ? "不可用" : top ? "栈顶" : "", "\uE718", top));
        }
        return rows;
    }

    private void RenderPalette()
    {
        if (!paletteOpen || PaletteList is null) return;
        string query = Query.Text.Trim();
        IEnumerable<Result> rows = showStack
            ? CheckpointResults().Where(row => row.Pop && desktop.CanActivate(row.Window))
            : Results();

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = rows.Where(row => terms
            .All(part => row.Title.Contains(part, StringComparison.OrdinalIgnoreCase) ||
                         row.Detail.Contains(part, StringComparison.OrdinalIgnoreCase))).Take(PaletteLimit).ToArray();
        PaletteList.ItemsSource = matches;
        PaletteList.SelectedIndex = matches.Length == 0 ? -1 : 0;
        PaletteCount.Text = matches.Length == 0 ? "" : $"{matches.Length} 项";
        PaletteEmpty.Text = matches.Length > 0 ? "" : query.Length > 0 ? "没有匹配的窗口。"
            : showStack ? "没有可返回的栈顶。可在工作台管理复工栈。"
            : paused ? "记录已暂停。可从托盘恢复。"
            : "还没有可返回的窗口。切换到其他应用后再试。";
        RecentTab.IsChecked = !showStack;
        StackTab.IsChecked = showStack;
        PaletteReturn.IsEnabled = matches.Length > 0 && !actionPending;
    }

    private void RenderWorkspace()
    {
        if (PageHeading is null) return;
        RecordingState.Text = paused ? "●  记录已暂停" : "●  正在记录本次会话";
        PauseButton.Content = paused ? "恢复记录" : "暂停记录";
        WorkspaceQuick.Content = "快速切换   " + Shortcut(preferences.SwitchKey);
        if (Navigation.SelectedIndex == 0) RenderHistory();
        if (Navigation.SelectedIndex == 1) RenderCheckpoints(true);
    }

    private void RenderHistory()
    {
        dirty = false;
        var now = Environment.TickCount64;
        suggestion = Results().FirstOrDefault();
        var rows = trail.Segments.Reverse().Select(segment => WindowResult(segment, now,
            desktop.CanActivate(segment.Window) ? "" : "不可用")).ToList();
        SuggestionHeading.Text = suggestion?.Badge == "建议返回" ? "建议返回" : "最近窗口";
        SuggestionTitle.Text = suggestion?.Title ?? "刚才的窗口会出现在这里";
        SuggestionDetail.Text = suggestion?.Detail ?? "切换到第二个窗口后，即可从这里返回。";
        SuggestionButton.IsEnabled = suggestion is not null && !actionPending;
        if (HistoryList.ItemsSource is not List<Result> previous || !previous.SequenceEqual(rows))
            HistoryList.ItemsSource = rows;
        HistoryEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryHeading.Text = rows.Count == 0 ? "最近切换" : $"最近切换 · {rows.Count} 段";
    }

    private void RenderCheckpoints(bool refresh)
    {
        dirty = false;
        var rows = CheckpointResults();
        if (refresh || CheckpointList.ItemsSource is not List<Result> previous || !previous.SequenceEqual(rows))
            CheckpointList.ItemsSource = rows;
        CheckpointHint.Text = $"{Shortcut(preferences.SaveKey)} 留下当前窗口。返回成功后自动移除栈顶。";
        ResumeButton.IsEnabled = !actionPending && rows.Count > 0 && desktop.CanActivate(rows[0].Window);
        DiscardButton.IsEnabled = !actionPending && rows.Count > 0;
        NoteButton.IsEnabled = !actionPending && !paused && source is not null && desktop.CanActivate(source) &&
            rows.Count < Trail.MaxCheckpoints;
        CheckpointEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPage()
    {
        if (AboutPage is null) return;
        FrameworkElement[] pages = [HistoryPage, CheckpointsPage, SettingsPage, AboutPage];
        string[] headings = ["窗口轨迹", "复工栈", "设置", "关于"];
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

    private void PaletteKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!paletteOpen || actionPending) return;
        switch (e.Key)
        {
            case VirtualKey.Tab when GetKeyState(0x11) < 0:
                e.Handled = true;
                showStack = !showStack;
                Query.Text = "";
                SelectTab();
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                HidePalette(true);
                break;
            case VirtualKey.Up:
            case VirtualKey.Down:
                if (PaletteList.Items.Count == 0) return;
                e.Handled = true;
                PaletteList.SelectedIndex = Math.Clamp(PaletteList.SelectedIndex +
                    (e.Key == VirtualKey.Up ? -1 : 1), 0, PaletteList.Items.Count - 1);
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                break;
            case VirtualKey.Enter:
                if (FocusManager.GetFocusedElement(Root.XamlRoot) is Button or ToggleButton) return;
                e.Handled = true;
                if (PaletteList.SelectedItem is Result row) _ = Jump(row);
                break;
        }
    }

    private async Task Jump(Result row)
    {
        if (actionPending || dialogOpen) return;
        if (row.Pop && !Trail.Same(trail.Checkpoints.LastOrDefault()?.Window, row.Window))
        {
            SetStatus("栈顶已变化，请重新选择。");
            return;
        }

        actionPending = true;
        try
        {
            var result = desktop.Activate(row.Window);
            if (result != ActivationResult.Unavailable && !Trail.Same(desktop.GetForeground(), row.Window))
            {
                for (int attempt = 0; attempt < 4 && !disposed; attempt++)
                {
                    await Task.Delay(50);
                    if (disposed || Trail.Same(desktop.GetForeground(), row.Window)) break;
                }
            }
            if (disposed) return;
            if (Trail.Same(desktop.GetForeground(), row.Window))
            {
                if (row.Pop)
                {
                    trail.Pop();
                    WorkspaceStatus.Text = $"已返回原窗口 · 复工栈剩余 {trail.Checkpoints.Count} 层";
                }
                else WorkspaceStatus.Text = "";
                if (paletteOpen) HidePalette(false);
                else AppWindow.Hide();
                return;
            }

            SetStatus(desktop.IsAlive(row.Window) ? "Windows 未允许切换。目标可能在另一个桌面。"
                : "原窗口已关闭，请重新选择。");
            if (paletteOpen && GetForegroundWindow() != hwnd) HidePalette(false);
        }
        finally
        {
            actionPending = false;
            if (!disposed && AppWindow.IsVisible)
            {
                if (paletteOpen)
                {
                    if (!desktop.CanActivate(row.Window)) RenderPalette();
                }
                else RenderWorkspace();
            }
        }
    }

    private void PaletteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PaletteReturn is not null) PaletteReturn.IsEnabled = !actionPending && PaletteList.SelectedItem is Result;
    }

    private void OpenWorkspaceClick(object sender, RoutedEventArgs e) => OpenDashboard();
    private void ClosePaletteClick(object sender, RoutedEventArgs e) => HidePalette(true);
    private void OpenPaletteClick(object sender, RoutedEventArgs e) => TogglePalette(true);
    private void SearchChanged(object sender, TextChangedEventArgs e) => RenderPalette();
    private void RecentClick(object sender, RoutedEventArgs e) { showStack = false; SelectTab(); }
    private void StackClick(object sender, RoutedEventArgs e) { showStack = true; SelectTab(); }
    private void SelectTab() { SetStatus(PaletteKeys); RenderPalette(); Query.Focus(FocusState.Programmatic); }
    private async void PaletteItemClick(object sender, ItemClickEventArgs e) { if (e.ClickedItem is Result row) await Jump(row); }
    private async void PaletteReturnClick(object sender, RoutedEventArgs e) { if (PaletteList.SelectedItem is Result row) await Jump(row); }
    private async void HistoryItemClick(object sender, ItemClickEventArgs e) { if (e.ClickedItem is Result row) await Jump(row); }
    private async void HistoryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && FocusManager.GetFocusedElement(Root.XamlRoot) is ListViewItem { Content: Result row })
        {
            e.Handled = true;
            await Jump(row);
        }
    }
    private async void ReturnSuggestion(object sender, RoutedEventArgs e) { if (suggestion is { } row) await Jump(row); }

    private async void ResumeClick(object sender, RoutedEventArgs e)
    {
        if (CheckpointResults().FirstOrDefault() is { } row) await Jump(row);
    }

    private void DiscardClick(object sender, RoutedEventArgs e)
    {
        if (actionPending) return;
        trail.Pop();
        SetStatus("已移除栈顶。");
        RenderCheckpoints(true);
    }

    private async void ComposeClick(object sender, RoutedEventArgs e)
    {
        if (actionPending || paused || source is not { } target || !desktop.CanActivate(target)) return;
        var note = new TextBox { Header = "备注（可选）", PlaceholderText = "例如：从第 3 段继续改", MaxLength = 120 };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = target.Title, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(note);
        if (await ConfirmAsync("为这个窗口留线", content, "添加检查点", ContentDialogButton.Primary) && !disposed && !paused)
            SaveCheckpoint(target, note.Text);
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
        if (paletteOpen) RenderPalette();
        else RenderWorkspace();
    }

    private async void ClearSession(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("清空本次会话？", "窗口轨迹和复工检查点都会清空。", "清空",
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

    private void SetStatus(string message)
    {
        if (paletteOpen) PaletteStatus.Text = message;
        else WorkspaceStatus.Text = message;
    }

    private static string Shortcut(char key) => $"Ctrl+Alt+{key}";
    private static string Duration(long seconds) => seconds < 60 ? $"{seconds} 秒" : $"{seconds / 60} 分 {seconds % 60:00} 秒";

    private static long InputIdle()
    {
        var info = new LastInput { Size = 8 };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Tick) : 0;
    }

    private sealed record Result(LiveWindow Window, string Title, string Detail, string Badge, string Icon, bool Pop)
    {
        public override string ToString() => Title;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size, Tick; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetKeyState(int virtualKey);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(nint monitor, out int scale);
}
