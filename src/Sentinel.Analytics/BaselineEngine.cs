using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Metrics;
using Sentinel.Data;
using Sentinel.Domain;

namespace Sentinel.Analytics;

/// <summary>Behavioural context in which a baseline is learned. Personal baselines beat global thresholds.</summary>
public enum BaselineContext
{
    All,
    /// <summary>CPU and GPU lightly loaded — "idle" temperatures.</summary>
    LowLoad,
    /// <summary>User away (no input ≥ 5 min) — background activity.</summary>
    Away,
    /// <summary>Sustained CPU or GPU load — gaming/rendering temperatures.</summary>
    HighLoad,
    /// <summary>Running on battery.</summary>
    OnBattery,
}

public sealed record BaselineSpec(string Id, Func<string, bool> Matches, BaselineContext Context, string Description);

public static class BaselineContexts
{
    public const double LowLoadCpu = 12;
    public const double LowLoadGpu = 15;
    public const double AwaySeconds = 300;
    public const double HighLoadCpu = 50;
    public const double HighLoadGpu = 60;

    public static string Name(BaselineContext c) => c.ToString().ToLowerInvariant();

    public static string Describe(BaselineContext c) => c switch
    {
        BaselineContext.LowLoad => "at low load",
        BaselineContext.Away => "while you are away",
        BaselineContext.HighLoad => "under sustained load",
        BaselineContext.OnBattery => "on battery",
        _ => "overall",
    };

    /// <summary>Classifies a minute given the context signals. Missing GPU data does not disqualify a minute.</summary>
    public static bool Matches(BaselineContext c, double? cpu, double? gpu, double? idleSeconds, double? ac) => c switch
    {
        BaselineContext.All => true,
        BaselineContext.LowLoad => cpu is < LowLoadCpu && (gpu is null || gpu < LowLoadGpu),
        BaselineContext.Away => idleSeconds is >= AwaySeconds,
        BaselineContext.HighLoad => cpu is >= HighLoadCpu || gpu is >= HighLoadGpu,
        BaselineContext.OnBattery => ac is < 0.5,
        _ => false,
    };
}

/// <summary>
/// Learns what is normal for this computer from minute history: median, MAD and percentiles per metric and
/// context over a trailing 30-day window (excluding the most recent hours so a live anomaly cannot hide itself).
/// </summary>
public sealed class BaselineEngine(HistoryStore store, LiveMetricStore live, ILogger<BaselineEngine> log)
{
    public static readonly IReadOnlyList<BaselineSpec> Specs =
    [
        new("cpu-away", k => k == MetricKeys.CpuUtil, BaselineContext.Away, "Background CPU usage while idle"),
        new("cpu-all", k => k == MetricKeys.CpuUtil, BaselineContext.All, "CPU usage"),
        new("mem-all", k => k == MetricKeys.MemUsedPct, BaselineContext.All, "Memory in use"),
        new("mem-away", k => k == MetricKeys.MemUsedPct, BaselineContext.Away, "Memory in use while idle"),
        new("thermal-idle", k => k.StartsWith("thermal.", StringComparison.Ordinal), BaselineContext.LowLoad, "Idle temperature"),
        new("thermal-load", k => k.StartsWith("thermal.", StringComparison.Ordinal), BaselineContext.HighLoad, "Temperature under load"),
        new("gpu-temp-idle", k => k.StartsWith("gpu.", StringComparison.Ordinal) && k.EndsWith(".temp", StringComparison.Ordinal), BaselineContext.LowLoad, "Idle GPU temperature"),
        new("gpu-temp-load", k => k.StartsWith("gpu.", StringComparison.Ordinal) && k.EndsWith(".temp", StringComparison.Ordinal), BaselineContext.HighLoad, "GPU temperature under load"),
        new("disk-temp", k => k.StartsWith("disk.", StringComparison.Ordinal) && k.EndsWith(".temp", StringComparison.Ordinal), BaselineContext.All, "Drive temperature"),
        new("bat-drain", k => k == MetricKeys.BatRate, BaselineContext.OnBattery, "Battery discharge rate"),
        new("wifi-signal", k => k == MetricKeys.WifiSignal, BaselineContext.All, "Wi-Fi signal quality"),
        new("disk-write-away", k => k == MetricKeys.DiskWrite, BaselineContext.Away, "Disk writes while idle"),
    ];

    public static readonly TimeSpan Window = TimeSpan.FromDays(30);
    public static readonly TimeSpan Exclusion = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<(string, string), Baseline> _cache = new();

    public void LoadCached()
    {
        foreach (var b in store.GetBaselines()) _cache[(b.MetricKey, b.Context)] = b;
    }

    public Baseline? Get(string metric, BaselineContext context) =>
        _cache.TryGetValue((metric, BaselineContexts.Name(context)), out var b) ? b : null;

    public IReadOnlyCollection<Baseline> All => [.. _cache.Values];

    public async Task RecomputeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var to = now - Exclusion;
        var from = now - Window;
        var cpu = ToDict(store.MinuteValues(MetricKeys.CpuUtil, from, to));
        var gpu = ToDict(store.MinuteValues(MetricKeys.GpuUtilAny, from, to));
        var idle = ToDict(store.MinuteValues(MetricKeys.UserIdleSeconds, from, to));
        var ac = ToDict(store.MinuteValues(MetricKeys.AcOnline, from, to));

        var keys = live.Definitions.Select(d => d.Key).Where(k => live.GetDefinition(k)?.Persist == PersistPolicy.Full).ToList();
        var results = new List<Baseline>();
        foreach (var spec in Specs)
        {
            foreach (var key in keys.Where(spec.Matches))
            {
                ct.ThrowIfCancellationRequested();
                var values = new List<double>();
                var days = new HashSet<long>();
                foreach (var (minute, v) in store.MinuteValues(key, from, to))
                {
                    double? c = cpu.TryGetValue(minute, out var cv) ? cv : null;
                    double? g = gpu.TryGetValue(minute, out var gv) ? gv : null;
                    double? i = idle.TryGetValue(minute, out var iv) ? iv : null;
                    double? a = ac.TryGetValue(minute, out var av) ? av : null;
                    if (!BaselineContexts.Matches(spec.Context, c, g, i, a)) continue;
                    values.Add(v);
                    days.Add(minute / 1440);
                }
                if (values.Count < 30) continue;
                var b = Compute(key, BaselineContexts.Name(spec.Context), values, days.Count, now);
                results.Add(b);
                _cache[(b.MetricKey, b.Context)] = b;
            }
        }
        await store.UpsertBaselinesAsync(results, ct).ConfigureAwait(false);
        log.LogInformation("Recomputed {Count} baselines", results.Count);
    }

    public static Baseline Compute(string key, string context, IReadOnlyList<double> values, int days, DateTimeOffset now) =>
        new(key, context,
            Statistics.Median(values),
            Statistics.Mad(values),
            Statistics.Percentile(values, 0.05),
            Statistics.Percentile(values, 0.95),
            Statistics.Mean(values),
            values.Count,
            days,
            now);

    private static Dictionary<long, double> ToDict(IReadOnlyList<(long Minute, double Value)> rows)
    {
        var d = new Dictionary<long, double>(rows.Count);
        foreach (var (m, v) in rows) d[m] = v;
        return d;
    }
}
