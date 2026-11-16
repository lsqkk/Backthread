using Microsoft.UI.Xaml;

namespace Backthread;

public partial class App : Application
{
    private Mutex? gate;
    private EventWaitHandle? signal;
    private RegisteredWaitHandle? listener;
    private MainWindow? window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One dashboard per interactive session; relaunching its shortcut brings it forward.
        const string key = @"Local\Backthread.Relaunch";
        signal = new EventWaitHandle(false, EventResetMode.AutoReset, key + ".Signal");
        gate = new Mutex(false, key + ".Gate", out bool first);
        if (!first)
        {
            signal.Set();
            signal.Dispose();
            gate.Dispose();
            Exit();
            return;
        }

        window = new MainWindow();
        var dispatcher = window.DispatcherQueue;
        listener = ThreadPool.RegisterWaitForSingleObject(signal,
            (_, _) => dispatcher.TryEnqueue(() => window?.OpenDashboard()), null, -1, false);
        window.Closed += (_, _) =>
        {
            listener?.Unregister(null);
            signal.Dispose();
            gate.Dispose();
            window = null;
        };
        window.OpenDashboard();
    }
}
