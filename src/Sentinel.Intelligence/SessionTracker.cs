using Microsoft.Extensions.Logging;
using Sentinel.Core.Metrics;
using Sentinel.Core.Providers;
using Sentinel.Core.Settings;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Telemetry;

namespace Sentinel.Intelligence;

/// <summary>
/// Detects high-performance sessions from load alone (no game hooking, no injection) and battery
/// discharge/charge/sleep sessions from power state, and stores their summaries.
/// </summary>
public sealed class SessionTracker(HistoryStore store, LiveMetricStore live, ProviderSet providers, ISettingsStore settings, IEventSink events,
    ILogger<SessionTracker> log)
{
    private const double StartLoad = 60;
    private const double EndLoad = 30;
    private static readonly TimeSpan StartAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan EndAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MinSession = TimeSpan.FromMinutes(5);

    private DateTimeOffset? _heavySince;
    private DateTimeOffset? _lightSince;
    private DateTimeOffset? _sessionStart;
    private readonly Dictionary<string, double> _appGpu = [];
    private readonly Dictionary<string, double> _appCpu = [];
    private long _memPeak;

    private (DateTimeOffset Start, double? Pct, double? MWh, bool Ac)? _powerSession;
    private (DateTimeOffset At, double? Pct, double? MWh)? _beforeSleep;

    public bool InWorkload => _sessionStart is not null;

    /// <summary>Called once a minute (or more often) by the intelligence service.</summary>
    public async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        await TrackWorkloadAsync(now, ct).ConfigureAwait(false);
        await TrackPowerAsync(now, ct).ConfigureAwait(false);
    }

    private async Task TrackWorkloadAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cpu = live.Get(MetricKeys.CpuUtil)?.Stats(now.AddMinutes(-1));
        var gpu = live.Get(MetricKeys.GpuUtilAny)?.Stats(now.AddMinutes(-1));
        var load = Math.Max(cpu is { N: > 0 } c ? c.Avg : 0, gpu is { N: > 0 } g ? g.Avg : 0);

        if (_sessionStart is null)
        {
            if (load >= StartLoad) _heavySince ??= now;
            else _heavySince = null;
            if (_heavySince is { } hs && now - hs >= StartAfter)
            {
                _sessionStart = hs;
                _lightSince = null;
                _appGpu.Clear();
                _appCpu.Clear();
                _memPeak = 0;
            }
        }
        else
        {
            if (!settings.Current.PrivacyMode)
            {
                foreach (var a in providers.Processes.Latest.Apps.Take(10))
                {
                    _appGpu[a.DisplayName] = _appGpu.GetValueOrDefault(a.DisplayName) + a.GpuPercent;
                    _appCpu[a.DisplayName] = _appCpu.GetValueOrDefault(a.DisplayName) + a.CpuPercent;
                }
            }
            var mem = providers.Memory.Latest.UsedBytes;
            if (mem > _memPeak) _memPeak = mem;

            if (load < EndLoad) _lightSince ??= now;
            else _lightSince = null;
            if (_lightSince is { } ls && now - ls >= EndAfter) await CloseWorkloadAsync(ls, ct).ConfigureAwait(false);
        }
    }

    private async Task CloseWorkloadAsync(DateTimeOffset end, CancellationToken ct)
    {
        var start = _sessionStart!.Value;
        _sessionStart = null;
        _heavySince = null;
        _lightSince = null;
        if (end - start < MinSession) return;

        (double Avg, double Max, int N)? S(string key)
        {
            var s = live.Get(key)?.Stats(start);
            return s is { N: > 0 } v ? (v.Avg, v.Max, v.N) : null;
        }
        // Live buffers hold one hour; fall back to minute history for longer sessions.
        (double Avg, double Max)? Stat(string key)
        {
            if (end - start <= TimeSpan.FromMinutes(55) && S(key) is { } l) return (l.Avg, l.Max);
            var r = store.Stats(key, start, end);
            return r.HasData ? (r.Avg, r.Max) : null;
        }

        var cpu = Stat(MetricKeys.CpuUtil);
        var gpu = Stat(MetricKeys.GpuUtilAny);
        var gpuTemp = Stat(MetricKeys.GpuTempAny);
        var cpuTempKey = live.Keys.FirstOrDefault(k => k.StartsWith("thermal.", StringComparison.Ordinal) && !k.EndsWith(".passive", StringComparison.Ordinal));
        var cpuTemp = cpuTempKey is null ? null : Stat(cpuTempKey);
        var gpuPowerKey = live.Keys.FirstOrDefault(k => k.StartsWith("gpu.", StringComparison.Ordinal) && k.EndsWith(".power", StringComparison.Ordinal));
        var gpuPower = gpuPowerKey is null ? null : Stat(gpuPowerKey);
        var crashes = store.QueryEvents(start, end, [EventCategory.AppCrash, EventCategory.DisplayDriverReset, EventCategory.Bugcheck, EventCategory.AppHang]).Count;
        string? app = null;
        if (!settings.Current.PrivacyMode)
        {
            var byGpu = _appGpu.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).FirstOrDefault();
            app = byGpu.Key ?? _appCpu.OrderByDescending(kv => kv.Value).FirstOrDefault().Key;
        }

        var session = new WorkloadSession(0, start, end, app, cpu?.Avg ?? 0, cpu?.Max ?? 0, gpu?.Avg, gpu?.Max, gpuTemp?.Avg, gpuTemp?.Max,
            cpuTemp?.Avg, cpuTemp?.Max, gpuPower?.Avg, _memPeak, crashes, !providers.Power.AcOnline);
        await store.InsertWorkloadSessionAsync(session, ct).ConfigureAwait(false);
        events.Publish(new SystemEvent(start, EventCategory.WorkloadSession, Severity.Info,
            $"{app ?? "High-performance session"} — {Core.Units.UnitFormatter.Duration(end - start)}",
            $"CPU average {session.CpuAvg:F0}%" + (gpu is { } gg ? $", GPU average {gg.Avg:F0}%" : "") + (crashes > 0 ? $", {crashes} stability event(s)" : ", no crashes detected"),
            "Sentinel", app, $"workload-{start.ToUnixTimeSeconds()}"));
        log.LogInformation("Workload session recorded ({Minutes:F0} min)", (end - start).TotalMinutes);
    }

    private async Task TrackPowerAsync(DateTimeOffset now, CancellationToken ct)
    {
        var bat = providers.Battery.Latest;
        if (!bat.Present) return;
        var ac = bat.AcOnline;
        var pct = bat.Percent.Value;
        var mwh = bat.RemainingMWh.Value;
        if (_powerSession is null)
        {
            _powerSession = (now, pct, mwh, ac);
            return;
        }
        var s = _powerSession.Value;
        if (s.Ac == ac) return;
        await CloseSessionAsync(s.Ac ? PowerSessionKind.Charge : PowerSessionKind.Discharge, s.Start, now, s.Pct, pct, s.MWh, mwh, null, ct).ConfigureAwait(false);
        _powerSession = (now, pct, mwh, ac);
    }

    private async Task CloseSessionAsync(PowerSessionKind kind, DateTimeOffset start, DateTimeOffset end, double? startPct, double? endPct, double? startMwh,
        double? endMwh, string? notes, CancellationToken ct)
    {
        if (end - start < TimeSpan.FromMinutes(kind == PowerSessionKind.Sleep ? 5 : 10)) return;
        double? watts = startMwh is { } a && endMwh is { } b ? (b - a) / 1000.0 / (end - start).TotalHours : null;
        await store.InsertPowerSessionAsync(new PowerSession(0, kind, start, end, startPct, endPct, startMwh, endMwh, watts, notes), ct).ConfigureAwait(false);
    }

    /// <summary>Records charge state before the system sleeps.</summary>
    public void OnSuspending(DateTimeOffset now)
    {
        var bat = providers.Battery.Latest;
        _beforeSleep = bat.Present ? (now, bat.Percent.Value, bat.RemainingMWh.Value) : null;
    }

    /// <summary>On resume, measures drain across the sleep period. Live readings are refreshed first by the caller.</summary>
    public async Task OnResumedAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_beforeSleep is not { } before) return;
        _beforeSleep = null;
        var bat = providers.Battery.Latest;
        if (!bat.Present || bat.Timestamp < now.AddSeconds(-30)) return;
        var acDuringSleep = bat.AcOnline;
        var dropPct = before.Pct is { } bp && bat.Percent.Value is { } ap ? bp - ap : (double?)null;
        var note = acDuringSleep ? "On AC power at wake" : dropPct is { } d ? $"Lost {d:F1}% while asleep" : null;
        await CloseSessionAsync(PowerSessionKind.Sleep, before.At, now, before.Pct, bat.Percent.Value, before.MWh, bat.RemainingMWh.Value, note, ct).ConfigureAwait(false);
        _powerSession = null;
    }
}
