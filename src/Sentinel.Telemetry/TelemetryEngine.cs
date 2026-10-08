using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Metrics;
using Sentinel.Core.Providers;
using Sentinel.Core.Settings;

namespace Sentinel.Telemetry;

public sealed class ProviderState(ITelemetryProvider provider)
{
    public ITelemetryProvider Provider { get; } = provider;
    public ProviderStatus Status { get; internal set; } = ProviderStatus.NotStarted;
    internal DateTimeOffset NextDue { get; set; } = DateTimeOffset.MinValue;
    internal int InFlight;
    internal bool Initialized { get; set; }
    public string Id => Provider.Descriptor.Id;
}

/// <summary>
/// Schedules every provider at its own adaptive interval on a single coalesced timer loop.
/// Providers run isolated: a slow or failing provider can never stall or crash the others,
/// and repeated failures back off exponentially (up to 10 minutes) before retrying.
/// </summary>
public sealed class TelemetryEngine : IAsyncDisposable
{
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    private readonly List<ProviderState> _states = [];
    private readonly HistoryRecorder _recorder;
    private readonly LiveMetricStore _live;
    private readonly ISettingsStore _settings;
    private readonly ILogger<TelemetryEngine> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _visible = true;
    private volatile string? _detailCategory;
    private volatile bool _suspended;

    public TelemetryEngine(IEnumerable<ITelemetryProvider> providers, HistoryRecorder recorder, LiveMetricStore live,
        ISettingsStore settings, ILogger<TelemetryEngine> log, TimeProvider time)
    {
        _recorder = recorder;
        _live = live;
        _settings = settings;
        _log = log;
        _time = time;
        foreach (var p in providers)
        {
            // Structural enforcement of the safety contract: non-compliant providers are never scheduled.
            SafetyContract.Validate(p.Descriptor);
            _states.Add(new ProviderState(p));
        }
    }

    public IReadOnlyList<ProviderState> Providers => _states;

    public event EventHandler<string>? ProviderSampled;

    public bool IsSimulation => _states.Any(s => s.Provider.Descriptor.IsSimulation);

    public SamplingMode GlobalMode => _suspended ? SamplingMode.Paused : _visible ? SamplingMode.Foreground : SamplingMode.Background;

    public void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        Reschedule();
    }

    /// <summary>The category of the detailed page currently shown (e.g. "CPU"), enabling faster sampling for it only.</summary>
    public void SetDetailCategory(string? category)
    {
        _detailCategory = category;
        Reschedule();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        await Task.WhenAll(_states.Select(s => InitializeAsync(s, token))).ConfigureAwait(false);
        _loop = Task.Run(() => LoopAsync(token), token);
    }

    private async Task InitializeAsync(ProviderState s, CancellationToken ct)
    {
        if (IsDisabled(s))
        {
            s.Status = s.Status with { Health = ProviderHealth.Disabled, Message = "Disabled in Settings" };
            return;
        }
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await Task.Run(() => s.Provider.InitializeAsync(timeout.Token), timeout.Token).ConfigureAwait(false);
            s.Initialized = true;
            var anyAvailable = s.Provider.Capabilities.Count == 0 || s.Provider.Capabilities.Any(c => c.Available);
            s.Status = new ProviderStatus(anyAvailable ? ProviderHealth.Healthy : ProviderHealth.Unavailable,
                anyAvailable ? null : "Not exposed by this system", null, 0, sw.Elapsed, 0);
            _log.LogInformation("Provider {Id} initialized in {Ms} ms ({Health})", s.Id, sw.ElapsedMilliseconds, s.Status.Health);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            s.Status = new ProviderStatus(ProviderHealth.Failed, "Initialization failed: " + ex.Message, null, 1, sw.Elapsed, 0);
            s.NextDue = _time.GetUtcNow() + TimeSpan.FromMinutes(2);
            _log.LogWarning(ex, "Provider {Id} failed to initialise", s.Id);
        }
    }

    private bool IsDisabled(ProviderState s) => _settings.Current.DisabledProviders.Contains(s.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// User-initiated, time-limited high-resolution sampling (Performance Investigation). All providers sample at
    /// their detail rate until switched off; callers must always switch it off (the investigation does so in finally).
    /// </summary>
    public void SetInvestigationMode(bool on)
    {
        _investigation = on;
        Reschedule();
    }

    private volatile bool _investigation;

    private SamplingMode ModeFor(ProviderState s)
    {
        var mode = GlobalMode;
        if (_investigation && mode != SamplingMode.Paused) return SamplingMode.Detail;
        if (mode == SamplingMode.Foreground && _detailCategory is { } c &&
            string.Equals(c, s.Provider.Descriptor.Category, StringComparison.OrdinalIgnoreCase))
            return SamplingMode.Detail;
        return mode;
    }

    private void Reschedule()
    {
        var now = _time.GetUtcNow();
        foreach (var s in _states)
        {
            if (!s.Initialized || s.Status.ConsecutiveFailures > 0) continue;
            var interval = s.Provider.GetInterval(ModeFor(s));
            if (interval == Timeout.InfiniteTimeSpan) continue;
            // Pull the next sample forward when switching to a faster mode.
            if (s.NextDue - now > interval) s.NextDue = now;
        }
        _wake.Release();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = _time.GetUtcNow();
            var nextWake = now + TimeSpan.FromSeconds(5);
            if (!_suspended)
            {
                foreach (var s in _states)
                {
                    if (IsDisabled(s))
                    {
                        if (s.Status.Health != ProviderHealth.Disabled) s.Status = s.Status with { Health = ProviderHealth.Disabled, Message = "Disabled in Settings" };
                        continue;
                    }
                    if (s.Status.Health == ProviderHealth.Disabled)
                    {
                        s.Status = s.Status with { Health = ProviderHealth.NotStarted, Message = null };
                        if (!s.Initialized) _ = InitializeAsync(s, ct);
                    }
                    if (!s.Initialized)
                    {
                        if (s.Status.Health == ProviderHealth.Failed && now >= s.NextDue) _ = RetryInitAsync(s, ct);
                        continue;
                    }
                    var interval = s.Provider.GetInterval(ModeFor(s));
                    if (interval == Timeout.InfiniteTimeSpan) continue;
                    if (now >= s.NextDue && Interlocked.CompareExchange(ref s.InFlight, 1, 0) == 0)
                    {
                        s.NextDue = AlignNext(now, interval);
                        _ = Task.Run(() => SampleAsync(s, ct), ct);
                    }
                    if (s.NextDue < nextWake) nextWake = s.NextDue;
                }
            }

            var delay = nextWake - _time.GetUtcNow();
            if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);
            try
            {
                await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Aligning to interval boundaries coalesces provider wake-ups into as few timer ticks as possible.
    private static DateTimeOffset AlignNext(DateTimeOffset now, TimeSpan interval)
    {
        var ticks = interval.Ticks;
        var next = (now.UtcTicks / ticks + 1) * ticks;
        return new DateTimeOffset(next, TimeSpan.Zero);
    }

    private async Task RetryInitAsync(ProviderState s, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref s.InFlight, 1, 0) != 0) return;
        try
        {
            s.NextDue = _time.GetUtcNow() + MaxBackoff;
            await InitializeAsync(s, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref s.InFlight, 0);
        }
    }

    private async Task SampleAsync(ProviderState s, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SampleTimeout);
            var ctx = new SampleContext(_time.GetUtcNow().ToLocalTime(), ModeFor(s), _recorder, _recorder);
            await s.Provider.SampleAsync(ctx, timeout.Token).ConfigureAwait(false);
            s.Status = new ProviderStatus(
                s.Status.Health is ProviderHealth.Unavailable ? ProviderHealth.Unavailable : ProviderHealth.Healthy,
                s.Status.Health is ProviderHealth.Unavailable ? s.Status.Message : null,
                _time.GetUtcNow(), 0, sw.Elapsed, s.Status.SampleCount + 1);
            ProviderSampled?.Invoke(this, s.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var failures = s.Status.ConsecutiveFailures + 1;
            var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 5 * Math.Pow(2, Math.Min(failures, 10))));
            s.NextDue = _time.GetUtcNow() + backoff;
            s.Status = s.Status with
            {
                Health = failures >= 3 ? ProviderHealth.Failed : ProviderHealth.Degraded,
                Message = ex is OperationCanceledException ? "Timed out" : ex.Message,
                ConsecutiveFailures = failures,
                LastDuration = sw.Elapsed,
            };
            // Log the first failure and then only occasionally, so a broken sensor cannot flood the log.
            if (failures is 1 or 3 or 10 || failures % 50 == 0)
                _log.LogWarning(ex, "Provider {Id} sample failed ({Failures} consecutive); retrying in {Backoff}", s.Id, failures, backoff);
        }
        finally
        {
            Interlocked.Exchange(ref s.InFlight, 0);
        }
    }

    /// <summary>Called before the system sleeps. Sampling pauses and charts get a gap marker.</summary>
    public void NotifySuspending()
    {
        _suspended = true;
        _live.MarkGapAll(_time.GetUtcNow());
    }

    /// <summary>Called after resume: every provider rebuilds native state, then sampling restarts immediately.</summary>
    public void NotifyResumed()
    {
        _live.MarkGapAll(_time.GetUtcNow());
        foreach (var s in _states)
        {
            try
            {
                s.Provider.OnSystemResumed();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Provider {Id} failed to handle resume", s.Id);
            }
            s.NextDue = DateTimeOffset.MinValue;
        }
        _suspended = false;
        _wake.Release();
    }

    public ProviderState? Find(string id) => _states.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
            {
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            _cts.Dispose();
        }
        foreach (var s in _states)
        {
            try
            {
                s.Provider.Dispose();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Provider {Id} dispose failed", s.Id);
            }
        }
        _wake.Dispose();
    }
}
