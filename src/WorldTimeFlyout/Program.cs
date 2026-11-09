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
        DiagLog.Detail($"launch: {string.Join(' ', args)}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DiagLog.Write($"fatal (domain): {e.ExceptionObject}");
        if (options.Exit)
        {
            // Installer / scripts: close the running instance (politely, then by force).
            bool closed = ShellMessageWindow.CloseExistingInstance(TimeSpan.FromSeconds(8));
            DiagLog.Write($"--exit: {(closed ? "closed the running instance" : "nothing to close")}");
            return 0;
        }
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

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App(options);
            });
        }
        catch (Exception e)
        {
            DiagLog.Write($"fatal: {e}");
            return 1;
        }
        GC.KeepAlive(s_instance);
        return 0;
    }
}
