using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentinel.App.Services;
using Sentinel.Core.Metrics;
using Sentinel.Core.Units;
using Sentinel.Telemetry;

namespace Sentinel.App.ViewModels;

/// <summary>
/// Base for page view models. Pages refresh from in-memory snapshots on the shared UI clock while visible;
/// nothing is polled when the page is not shown.
/// </summary>
public abstract partial class PageViewModel : ObservableObject
{
    private UiClock? _clock;

    protected IServiceProvider Services => App.Services;
    protected ProviderSet P => Services.GetRequiredService<ProviderSet>();
    protected LiveMetricStore Live => Services.GetRequiredService<LiveMetricStore>();
    protected UnitFormatter U => Services.GetRequiredService<UnitFormatter>();

    /// <summary>How often (in UI clock ticks) <see cref="Refresh"/> runs. Most pages use 1 s; some 2–5 s.</summary>
    protected virtual int RefreshEveryTicks => 1;

    private int _ticks;
    private int _failures;

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    public void Activate(object? parameter)
    {
        OnActivated(parameter);
        SafeRefresh();
        IsLoading = false;
        _clock = Services.GetRequiredService<UiClock>();
        _clock.Tick += OnTick;
    }

    public void Deactivate()
    {
        if (_clock is not null) _clock.Tick -= OnTick;
        _clock = null;
        OnDeactivated();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (++_ticks % RefreshEveryTicks == 0) SafeRefresh();
    }

    private void SafeRefresh()
    {
        try
        {
            Refresh();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failing refresh must not take down the page; it is logged (rate-limited) and the next tick tries again.
            if (_failures++ % 120 == 0)
                Services.GetRequiredService<ILoggerFactory>().CreateLogger(GetType().FullName ?? "Page").LogWarning(ex, "Page refresh failed");
        }
    }

    protected virtual void OnActivated(object? parameter)
    {
    }

    protected virtual void OnDeactivated()
    {
    }

    protected abstract void Refresh();
}
