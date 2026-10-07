using System.Globalization;
using Microsoft.Extensions.Logging;
using Sentinel.Analytics;
using Sentinel.Core.Metrics;
using Sentinel.Core.Settings;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Telemetry;

namespace Sentinel.Intelligence;

/// <summary>
/// The deterministic brain. Owns the background cadence for persistence, baselines, anomalies, sessions,
/// change detection, health and insights. Works entirely without an LLM.
/// </summary>
public sealed class IntelligenceService : IAsyncDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    private readonly TelemetryEngine _engine;
    private readonly HistoryRecorder _recorder;
    private readonly HistoryStore _store;
    private readonly BaselineEngine _baselines;
    private readonly AnomalyEngine _anomalies;
    private readonly ChangeTracker _changes;
    private readonly SessionTracker _sessions;
    private readonly InsightEngine _insights;
    private readonly HealthModel _health;
    private readonly ProviderSet _providers;
    private readonly ISettingsStore _settings;
    private readonly LiveMetricStore _live;
    private readonly UnitFormatter _units;
    private readonly NotificationPolicy _notifications;
    private readonly ILogger<IntelligenceService> _log;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset _lastMinute, _lastChanges, _lastHour, _lastRetention, _lastBaseline = DateTimeOffset.MinValue;

    public IntelligenceService(TelemetryEngine engine, HistoryRecorder recorder, HistoryStore store, BaselineEngine baselines, AnomalyEngine anomalies,
        ChangeTracker changes, SessionTracker sessions, InsightEngine insights, HealthModel health, ProviderSet providers, ISettingsStore settings,
        LiveMetricStore live, UnitFormatter units, NotificationPolicy notifications, ILogger<IntelligenceService> log, TimeProvider time)
    {
        _engine = engine;
        _recorder = recorder;
        _store = store;
        _baselines = baselines;
        _anomalies = anomalies;
        _changes = changes;
        _sessions = sessions;
        _insights = insights;
        _health = health;
        _providers = providers;
        _settings = settings;
        _live = live;
        _units = units;
        _notifications = notifications;
        _log = log;
        _time = time;
        _anomalies.AnomalyOpened += (_, a) =>
        {
            _recorder.Publish(new SystemEvent(a.Start, EventCategory.Anomaly, a.Severity, a.Title, a.Description, "Sentinel anomaly engine", null, "anomaly-" + a.Id));
            _notifications.OnAnomaly(a);
        };
    }

    public HealthReport? Health { get; private set; }
    public IReadOnlyList<Insight> Insights { get; private set; } = [];
    public Insight? Headline { get; private set; }
    public bool HistoryBackfilled { get; private set; }

    /// <summary>Raised (on a background thread) whenever health/insights are recomputed.</summary>
    public event EventHandler? Updated;

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _recorder.Paused = _settings.Current.RecordingPaused;
        _notifications.Enabled = _settings.Current.NotificationsEnabled;
        _settings.Changed += (_, s) =>
        {
            _recorder.Paused = s.RecordingPaused;
            _notifications.Enabled = s.NotificationsEnabled;
        };
        _baselines.LoadCached();
        _providers.Power.Suspending += OnSuspending;
        _providers.Power.Resumed += OnResumed;
        await _engine.StartAsync(token).ConfigureAwait(false);
        _ = Task.Run(() => BackfillAsync(token), token);
        _loop = Task.Run(() => LoopAsync(token), token);
    }

    private async Task BackfillAsync(CancellationToken ct)
    {
        try
        {
            var now = _time.GetLocalNow();
            var since = _store.GetKv("events_backfilled_until") is { } s && long.TryParse(s, CultureInfo.InvariantCulture, out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).AddHours(-1)
                : now.AddDays(-30);
            var events = await _providers.Events.ReadHistoryAsync(since, 20000, ct).ConfigureAwait(false);
            foreach (var e in events) _recorder.Publish(e);
            await _recorder.FlushAsync(now, ct).ConfigureAwait(false);
            await _store.SetKvAsync("events_backfilled_until", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            HistoryBackfilled = true;
            _log.LogInformation("Read {Count} historical events since {Since:d}", events.Count, since);
            Recompute(now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Event history back-fill failed");
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick, _time);
        // First pass shortly after start so Home has health and insights quickly.
        await Task.Delay(TimeSpan.FromSeconds(5), _time, ct).ConfigureAwait(false);
        do
        {
            try
            {
                await RunDueWorkAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Intelligence cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task RunDueWorkAsync(CancellationToken ct)
    {
        var now = _time.GetLocalNow();
        if (_engine.GlobalMode == Core.Providers.SamplingMode.Paused) return;

        if (now - _lastMinute >= TimeSpan.FromSeconds(60))
        {
            _lastMinute = now;
            await _recorder.FlushAsync(now, ct).ConfigureAwait(false);
            await RecordAppUsageAsync(now, ct).ConfigureAwait(false);
            await _sessions.TickAsync(now, ct).ConfigureAwait(false);
            await _anomalies.EvaluateAsync(new AnomalyContext
            {
                Now = now,
                Live = _live,
                History = _store,
                Baselines = _baselines,
                Units = _units,
                CurrentApps = _providers.Processes.Latest.Apps,
                PrivacyMode = _settings.Current.PrivacyMode,
            }, ct).ConfigureAwait(false);
            Recompute(now);
        }
        if (now - _lastChanges >= TimeSpan.FromMinutes(10))
        {
            _lastChanges = now;
            await _changes.RunAsync(now, ct).ConfigureAwait(false);
        }
        if (now - _lastHour >= TimeSpan.FromHours(1))
        {
            _lastHour = now;
            await _store.AggregateHoursAsync(now, ct).ConfigureAwait(false);
        }
        if (now - _lastBaseline >= TimeSpan.FromHours(6))
        {
            _lastBaseline = now;
            await _baselines.RecomputeAsync(now, ct).ConfigureAwait(false);
        }
        if (now - _lastRetention >= TimeSpan.FromDays(1))
        {
            _lastRetention = now;
            await _store.ApplyRetentionAsync(now, _settings.Current.RetentionDays, ct).ConfigureAwait(false);
        }
    }

    private async Task RecordAppUsageAsync(DateTimeOffset now, CancellationToken ct)
    {
        var s = _settings.Current;
        if (s.PrivacyMode || !s.RecordAppHistory || s.RecordingPaused) return;
        var apps = _providers.Processes.Latest.Apps;
        if (apps.Count == 0) return;
        var top = apps.OrderByDescending(a => a.CpuPercent).Take(8)
            .Concat(apps.OrderByDescending(a => a.PrivateBytes).Take(6))
            .Concat(apps.Where(a => a.GpuPercent > 1).OrderByDescending(a => a.GpuPercent).Take(3))
            .DistinctBy(a => a.AppKey)
            .Select(a => new AppUsageRow(now, a.AppKey, a.CpuPercent, a.PrivateBytes, a.GpuPercent, a.DiskBytesPerSec))
            .ToList();
        await _store.InsertAppUsageAsync(top, ct).ConfigureAwait(false);
    }

    /// <summary>Recomputes health and insights now (also used after user actions).</summary>
    public void Recompute(DateTimeOffset now)
    {
        try
        {
            Health = _health.Evaluate(now);
            Insights = _insights.Generate(now);
            Headline = _insights.Headline(now, Insights);
            _notifications.OnHealth(Health);
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or ArgumentException)
        {
            _log.LogWarning(ex, "Health/insight recomputation failed");
        }
    }

    private void OnSuspending(object? sender, EventArgs e)
    {
        _sessions.OnSuspending(_time.GetLocalNow());
        _engine.NotifySuspending();
        // Persist what we have; the process may be frozen for hours. The suspend callback must return quickly.
        try
        {
            _recorder.FlushAsync(_time.GetLocalNow(), CancellationToken.None).Wait(TimeSpan.FromSeconds(1.5));
        }
        catch (AggregateException ex)
        {
            _log.LogDebug(ex, "Flush before sleep failed");
        }
    }

    private void OnResumed(object? sender, EventArgs e)
    {
        _engine.NotifyResumed();
        _ = Task.Run(async () =>
        {
            try
            {
                // Give the battery provider time to take a fresh reading before measuring sleep drain.
                await Task.Delay(TimeSpan.FromSeconds(8), _time).ConfigureAwait(false);
                await _sessions.OnResumedAsync(_time.GetLocalNow(), CancellationToken.None).ConfigureAwait(false);
                var last = _store.QueryPowerSessions(_time.GetLocalNow().AddMinutes(-1), _time.GetLocalNow(), PowerSessionKind.Sleep, 1).FirstOrDefault();
                if (last is not null) _notifications.OnSleepDrain(last);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Post-resume processing failed");
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _providers.Power.Suspending -= OnSuspending;
        _providers.Power.Resumed -= OnResumed;
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
        }
        try
        {
            await _recorder.FlushAsync(_time.GetLocalNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Final flush failed");
        }
        await _engine.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
