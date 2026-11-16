using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace Backthread;

public sealed partial class MainWindow : Window
{
    private const long IdleMs = 120_000;
    private readonly Trail trail = new();
    private readonly NativeDesktop desktop;
    private readonly DispatcherQueueTimer timer;
    private readonly Dictionary<TextBlock, Segment> durations = [];
    private Segment? candidate;
    private LiveWindow? draft;
    private LiveWindow? interrupted;
    private long lastTick = Environment.TickCount64;
    private bool paused;
    private bool idle;
    private bool actionPending;
    private bool disposed;
    private bool viewDirty;

    public MainWindow()
    {
        InitializeComponent();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Backthread.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 440;
            presenter.PreferredMinimumHeight = 420;
        }
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        int width = Math.Min((int)(980 * scale), workArea.Width - 30);
        int height = Math.Min((int)(750 * scale), workArea.Height - 30);
        AppWindow.MoveAndResize(new RectInt32(workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2, width, height));

        desktop = new NativeDesktop(WinRT.Interop.WindowNative.GetWindowHandle(this));
        desktop.ForegroundChanged += OnForeground;
        desktop.ReturnRequested += OpenDashboard;
        desktop.CheckpointRequested += () => OpenComposer(paused ? null : desktop.GetForeground());
        if (InputIdle() < IdleMs) trail.Observe(desktop.GetForeground(), lastTick);
        Hint.Text = (desktop.ReturnHotkeyRegistered ? "Ctrl+Alt+J 打开 · " : "J 快捷键不可用 · ")
                  + (desktop.CheckpointHotkeyRegistered ? "Ctrl+Alt+K 留线 · " : "K 快捷键不可用 · ")
                  + "只记本次会话 · 最小化后继续记录";

        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => Pulse();
        timer.Start();
        Closed += (_, _) =>
        {
            disposed = true;
            timer.Stop();
            desktop.Dispose();
        };
        Render();
        SetStatus(desktop.IsTracking ? "正在记录窗口切换。" : "无法订阅窗口切换；仅在打开面板时读取当前窗口。");
    }

    internal void OpenDashboard()
    {
        if (disposed) return;
        var current = paused ? null : desktop.GetForeground();
        if (current is not null)
        {
            interrupted = current;
            if (!paused && InputIdle() < IdleMs) trail.Observe(current, Environment.TickCount64);
        }
        ShowDashboard();
        Render();
        if (Composer.Visibility != Visibility.Visible && ReturnButton.IsEnabled)
            ReturnButton.Focus(FocusState.Programmatic);
    }

    private void ShowDashboard()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void OnForeground(LiveWindow? window)
    {
        if (paused || idle || InputIdle() >= IdleMs) return;
        if (window is not null) interrupted = window;
        if (trail.Observe(window, Environment.TickCount64)) viewDirty = true;
    }

    private void Pulse()
    {
        var now = Environment.TickCount64;
        var gap = now - lastTick;
        var sinceInput = InputIdle();
        bool wasIdle = idle;
        idle = sinceInput >= IdleMs;
        if (!paused)
        {
            if (idle || gap > 8000)
            {
                long stop = gap > 8000 ? lastTick : now - Math.Max(0, sinceInput - IdleMs);
                if (trail.Observe(null, stop)) viewDirty = true;
            }
            if (!idle)
            {
                var current = desktop.GetForeground();
                if (current is not null) interrupted = current;
                // A poll refreshes titles changed within the same HWND, and handles waking after idle.
                if ((desktop.IsTracking || wasIdle) && trail.Observe(current, now)) viewDirty = true;
            }
        }
        lastTick = now;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
        if (viewDirty) Render();
        foreach (var (label, segment) in durations) label.Text = FormatDuration(segment.Seconds(now));
        UpdateCandidate(now);
        UpdateStackAction();
    }

    private void Render()
    {
        viewDirty = false;
        var now = Environment.TickCount64;
        UpdateCandidate(now);
        TrailCount.Text = $"最近 {trail.Segments.Count} 段";
        var trailFocus = Root.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as FrameworkElement : null;
        var focusedSegment = trailFocus?.Tag as Segment;
        durations.Clear();
        TrailRows.Children.Clear();
        if (trail.Segments.Count == 0) TrailRows.Children.Add(Quiet(
            "最小化回线，然后照常工作。切换到另一个应用后，这里就会出现第一段记录。"));
        foreach (var segment in trail.Segments.Reverse())
        {
            var row = new Grid { ColumnSpacing = 11 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = segment.Window.Title, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(Quiet(segment.Window.AppName));
            var duration = Quiet(FormatDuration(segment.Seconds(now)));
            duration.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(duration, 1);
            row.Children.Add(text);
            row.Children.Add(duration);
            durations[duration] = segment;
            var button = new Button { Content = row, Tag = segment, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(11, 7, 11, 7) };
            ToolTipService.SetToolTip(button, segment.Window.Title);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,
                $"回到 {segment.Window.Title}，{segment.Window.AppName}，{duration.Text}");
            button.Click += async (_, _) => await Jump(segment.Window, false);
            TrailRows.Children.Add(button);
            if (focusedSegment?.Start == segment.Start && Trail.Same(focusedSegment.Window, segment.Window))
                button.Focus(FocusState.Programmatic);
        }

        StackRows.Children.Clear();
        if (trail.Checkpoints.Count == 0) StackRows.Children.Add(Quiet("栈是空的。试试在要离开的窗口按 Ctrl+Alt+K。"));
        int depth = trail.Checkpoints.Count;
        foreach (var point in trail.Checkpoints.Reverse())
        {
            var details = new StackPanel { Spacing = 3 };
            bool top = depth == trail.Checkpoints.Count;
            details.Children.Add(Quiet(top ? $"第 {depth} 层 · 栈顶 · {point.SavedAt:HH:mm}" : $"第 {depth} 层 · {point.SavedAt:HH:mm}"));
            details.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(point.Note) ? point.Window.Title : point.Note,
                TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(point.Note))
                details.Children.Add(Quiet(point.Window.Title));
            StackRows.Children.Add(new Border { Padding = new Thickness(10), Child = details });
            depth--;
        }
        UpdateStackAction();
    }

    private TextBlock Quiet(string value) => new() { Text = value, FontSize = 12,
        Style = (Style)Root.Resources["Quiet"] };

    private void UpdateCandidate(long now)
    {
        candidate = trail.Suggest(now, desktop.IsAlive);
        if (candidate is { } segment)
        {
            CandidateTitle.Text = segment.Window.Title;
            CandidateDetails.Text = $"{segment.Window.AppName}  ·  停留 {FormatDuration(segment.Seconds(now))}  ·  {FormatDuration((now - segment.End!.Value) / 1000)}前离开";
        }
        else
        {
            CandidateTitle.Text = "被打断了？从这里接着做。";
            CandidateDetails.Text = "过去 15 分钟内，离开过且停留 ≥45 秒的窗口会出现在这里。也可直接点下方轨迹。";
        }
        ReturnButton.IsEnabled = !actionPending && candidate is not null;
    }

    private void UpdateStackAction()
    {
        var top = trail.Checkpoints.LastOrDefault();
        StackActions.Visibility = top is null ? Visibility.Collapsed : Visibility.Visible;
        bool alive = top is not null && desktop.IsAlive(top.Window);
        PopButton.IsEnabled = alive && !actionPending;
        PopButton.Content = top is not null && !alive ? "原窗口已离开" : "返回并出栈";
        ComposeButton.IsEnabled = !paused && !actionPending && trail.Checkpoints.Count < Trail.MaxCheckpoints;
    }

    private void OpenComposerClick(object sender, RoutedEventArgs e) => OpenComposer(interrupted ?? trail.LastExternal);

    private void OpenComposer(LiveWindow? window)
    {
        if (disposed || actionPending) return;
        ShowDashboard();
        if (paused) { SetStatus("先恢复记录，再添加检查点。"); return; }
        if (trail.Checkpoints.Count >= Trail.MaxCheckpoints) { SetStatus("已到 12 层。返回或移除一层后再添加。"); return; }
        if (window is null || !desktop.IsAlive(window)) { SetStatus("先切到想留线的窗口，然后按 Ctrl+Alt+K。"); return; }
        if (!Trail.Same(draft, window)) NoteBox.Text = "";
        draft = window;
        ComposeTarget.Text = window.Title;
        Composer.Visibility = Visibility.Visible;
        NoteBox.Focus(FocusState.Programmatic);
        SetStatus("写一句提示，或直接压入栈顶。");
    }

    private void SaveCheckpoint(object sender, RoutedEventArgs e)
    {
        if (paused || actionPending || draft is not { } window) return;
        if (!desktop.IsAlive(window)) { SetStatus("原窗口已经关闭，这次没有添加。"); return; }
        if (!trail.Push(window, NoteBox.Text, DateTimeOffset.Now)) { SetStatus("复工栈已满。"); return; }
        CloseComposer();
        Render();
        SetStatus($"已留线。复工栈现在有 {trail.Checkpoints.Count} 层。");
    }

    private void CancelCheckpoint(object sender, RoutedEventArgs e) => CloseComposer();

    private void CloseComposer()
    {
        draft = null;
        NoteBox.Text = "";
        Composer.Visibility = Visibility.Collapsed;
    }

    private async void ReturnToCandidate(object sender, RoutedEventArgs e)
    {
        if (candidate is { } segment) await Jump(segment.Window, false);
    }

    private async void ResumeTop(object sender, RoutedEventArgs e)
    {
        if (trail.Checkpoints.LastOrDefault() is { } top) await Jump(top.Window, true);
    }

    private void DiscardTop(object sender, RoutedEventArgs e)
    {
        if (actionPending) return;
        trail.Pop();
        Render();
        SetStatus("已移除栈顶，没有切换窗口。");
    }

    private async Task Jump(LiveWindow window, bool pop)
    {
        if (actionPending) return;
        actionPending = true;
        UpdateCandidate(Environment.TickCount64);
        UpdateStackAction();
        try
        {
            var result = desktop.Activate(window);
            if (result != ActivationResult.Unavailable && Trail.Same(desktop.GetForeground(), window))
                result = ActivationResult.Activated;
            else if (result != ActivationResult.Unavailable)
            {
                // Restore and foreground activation can be delivered asynchronously. Confirm the
                // exact live target actually reached the foreground before modifying the stack.
                await Task.Delay(120);
                if (disposed) return;
                result = Trail.Same(desktop.GetForeground(), window) ? ActivationResult.Activated :
                    desktop.IsAlive(window) ? ActivationResult.Blocked : ActivationResult.Unavailable;
            }
            if (result == ActivationResult.Activated)
            {
                if (pop) trail.Pop();
                SetStatus(pop ? "已返回原窗口，并移除这个检查点。" : "已返回原窗口。");
            }
            else SetStatus(result == ActivationResult.Unavailable
                ? "原窗口已关闭。没有打开同名窗口。"
                : "Windows 未允许切换。请检查窗口是否在其他桌面或有对话框。");
        }
        finally
        {
            actionPending = false;
            if (!disposed) Render();
        }
    }

    private void TogglePause(object sender, RoutedEventArgs e)
    {
        paused = !paused;
        if (paused)
        {
            trail.Observe(null, Environment.TickCount64);
            CloseComposer();
        }
        PauseButton.Content = paused ? "恢复记录" : "暂停记录";
        SetStatus(paused ? "已暂停。现有记录与复工栈仍可使用。" : "已恢复。只记录接下来的窗口活动。");
        Render();
    }

    private async void ClearSession(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "清空本次记录？",
            Content = "切换轨迹和所有复工检查点都会清空。其他窗口不受影响。", PrimaryButtonText = "清空",
            CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || disposed || actionPending) return;
        trail.Clear();
        interrupted = null;
        CloseComposer();
        Render();
        SetStatus("本次记录已清空。");
    }

    private async void ShowHelp(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "快捷键与隐私", CloseButtonText = "知道了",
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
                "Ctrl+Alt+J：打开回线\nCtrl+Alt+K：为当前窗口留线\n\n最小化仍会记录，关闭就退出。数据只在内存中，退出后清空。回线只读取窗口标题、应用名和焦点持续时间。连续 2 分钟没有输入后停止累计。若某个窗口不能安全识别，回线会跳过它。", MaxWidth = 405 } };
        await dialog.ShowAsync();
    }

    private void SetStatus(string message) => Status.Text = message;
    private static string FormatDuration(long seconds) => seconds < 60 ? $"{Math.Max(seconds, 0)} 秒" :
        $"{seconds / 60} 分 {seconds % 60:00} 秒";

    private static long InputIdle()
    {
        var info = new LastInput { Size = 8 };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Tick) : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size, Tick; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
