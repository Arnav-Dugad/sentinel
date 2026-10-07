using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Sentinel.App;

/// <summary>
/// Custom entry point: enforces a single running instance. A second launch hands its activation to the
/// running instance (which brings its window forward) and exits.
/// </summary>
public static class Program
{
    public static bool StartInBackground { get; private set; }
    public static bool ForceSimulation { get; private set; }
    public static bool JustUpdated { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // The new version's installer runs before anything else: no UI, no single-instance registration.
        if (args.Contains("--install-update", StringComparer.OrdinalIgnoreCase)) return Updates.UpdateInstaller.Run(args);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        JustUpdated = args.Contains("--updated", StringComparer.OrdinalIgnoreCase);
        StartInBackground = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        ForceSimulation = args.Contains("--simulate", StringComparer.OrdinalIgnoreCase);

        var key = AppInstance.FindOrRegisterForKey("Sentinel-Main-Instance");
        // After a user-requested restart, wait briefly for the previous instance to finish exiting.
        if (args.Contains("--after-restart", StringComparer.OrdinalIgnoreCase))
        {
            for (var i = 0; i < 50 && !key.IsCurrent; i++)
            {
                Thread.Sleep(100);
                key = AppInstance.FindOrRegisterForKey("Sentinel-Main-Instance");
            }
        }
        if (!key.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            key.RedirectActivationToAsync(activation).AsTask().Wait(TimeSpan.FromSeconds(5));
            return 0;
        }
        // A verified update downloaded earlier is installed now, before the app starts using its files.
        if (!JustUpdated && !args.Contains("--no-update", StringComparer.OrdinalIgnoreCase) &&
            Updates.UpdateService.TryInstallStagedAtStartup(StartInBackground))
        {
            key.UnregisterKey();
            return 0;
        }
        key.Activated += (_, _) => App.Current?.OnRedirectedActivation();

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            // The App instance registers itself as Application.Current.
            _ = new App();
        });
        return 0;
    }
}
