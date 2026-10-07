using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Sentinel.Core.Settings;
using Windows.UI.ViewManagement;

namespace Sentinel.App.Services;

/// <summary>
/// One shared 1 Hz UI refresh timer. It only runs while the main window is visible, so a hidden Sentinel
/// performs no UI work at all (no rendering, no bindings, no timer wake-ups).
/// </summary>
public sealed class UiClock
{
    private readonly DispatcherQueueTimer _timer;

    public UiClock(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Tick;

    public bool Running => _timer.IsRunning;

    public void SetRunning(bool running)
    {
        if (running && !_timer.IsRunning) _timer.Start();
        else if (!running && _timer.IsRunning) _timer.Stop();
    }
}

/// <summary>Whether sensitive identifiers (serials, UUIDs, MAC/IP addresses) are revealed. Never persisted; off by default.</summary>
public sealed partial class SensitiveInfoState : ObservableObject
{
    [ObservableProperty]
    public partial bool ShowSensitive { get; set; }

    public string Mask(string? value) => ShowSensitive ? (string.IsNullOrWhiteSpace(value) ? "—" : value) : Core.Privacy.Redactor.Mask(value);
}

/// <summary>Decides whether decorative motion should run: honours Windows animation settings, the in-app setting and Battery Saver.</summary>
public sealed class MotionPolicy(ISettingsStore settings, Core.Providers.IPowerTelemetryProvider power)
{
    private readonly UISettings _ui = new();

    public bool AnimationsEnabled => _ui.AnimationsEnabled && !settings.Current.ReduceMotion && !power.BatterySaverOn;
}

public sealed class ThemeService(ISettingsStore settings)
{
    public void Apply(FrameworkElement root)
    {
        root.RequestedTheme = settings.Current.Theme switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
