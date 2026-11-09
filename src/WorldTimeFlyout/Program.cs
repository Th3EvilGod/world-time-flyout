using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WorldTime.Shell;

namespace WorldTimeFlyout;

public static class Program
{
    private static Mutex? s_instance;

    [STAThread]
    public static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        DiagLog.Init(options.Diagnostics);
        if (!options.Screenshot)
        {
            s_instance = new Mutex(true, @"Local\WorldTimeFlyout.SingleInstance", out bool created);
            if (!created)
            {
                // Already running: ask that instance to open its panel, then quit.
                if (!options.Background) ShellMessageWindow.SignalExistingInstance();
                return 0;
            }
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(options);
        });
        GC.KeepAlive(s_instance);
        return 0;
    }
}
