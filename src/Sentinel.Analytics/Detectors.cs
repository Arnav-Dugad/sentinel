using System.Globalization;
using Sentinel.Core.Metrics;
using Sentinel.Data;
using Sentinel.Domain;

namespace Sentinel.Analytics;

/// <summary>Shared helpers for evaluating recent live data in a behavioural context.</summary>
internal static class LiveWindow
{
    /// <summary>
    /// Values of <paramref name="key"/> over the window, restricted to seconds where the context held.
    /// Returns null unless the context held for at least <paramref name="minShare"/> of the window.
    /// </summary>
    public static List<double>? ContextValues(AnomalyContext ctx, string key, BaselineContext context, TimeSpan window, double minShare = 0.8)
    {
        var from = ctx.Now - window;
        var target = ctx.Live.Get(key)?.Since(from);
        if (target is null || target.Length < window.TotalSeconds * 0.3) return null;

        var cpu = BySecond(ctx.Live.Get(MetricKeys.CpuUtil)?.Since(from));
        var gpu = BySecond(ctx.Live.Get(MetricKeys.GpuUtilAny)?.Since(from));
        var idle = BySecond(ctx.Live.Get(MetricKeys.UserIdleSeconds)?.Since(from));
        var ac = BySecond(ctx.Live.Get(MetricKeys.AcOnline)?.Since(from));

        var matched = new List<double>(target.Length);
        var valid = 0;
        foreach (var p in target)
        {
            if (double.IsNaN(p.Value)) continue;
            valid++;
            var s = p.Timestamp.ToUnixTimeSeconds();
            if (BaselineContexts.Matches(context, Near(cpu, s), Near(gpu, s), Near(idle, s), Near(ac, s))) matched.Add(p.Value);
        }
        return valid > 0 && matched.Count >= valid * minShare ? matched : null;
    }

    private static Dictionary<long, double> BySecond(MetricPoint[]? points)
    {
        var d = new Dictionary<long, double>();
        if (points is null) return d;
        foreach (var p in points)
            if (!double.IsNaN(p.Value)) d[p.Timestamp.ToUnixTimeSeconds()] = p.Value;
        return d;
    }

    private static double? Near(Dictionary<long, double> d, long s)
    {
        if (d.Count == 0) return null;
        for (var o = 0; o <= 5; o++)
        {
            if (d.TryGetValue(s - o, out var v)) return v;
            if (d.TryGetValue(s + o, out v)) return v;
        }
        return null;
    }
}

/// <summary>Compares the last 10 minutes against this machine's learned baseline for the same context.</summary>
public sealed class BaselineDeviationDetector : IAnomalyDetector
{
    private sealed record Rule(string SpecId, double MinDelta, bool HigherIsWorse, double MinZ, string Kind);

    private static readonly Rule[] Rules =
    [
        new("thermal-idle", 8, true, 4, "temperature"),
        new("gpu-temp-idle", 8, true, 4, "temperature"),
        new("thermal-load", 6, true, 4, "temperature"),
        new("gpu-temp-load", 6, true, 4, "temperature"),
        new("disk-temp", 8, true, 4, "temperature"),
        new("cpu-away", 10, true, 4, "cpu"),
        new("mem-all", 15, true, 4, "percent"),
        new("bat-drain", 5, false, 4, "drain"),
        new("wifi-signal", 20, false, 4, "percent"),
        new("disk-write-away", 20 * 1024 * 1024, true, 5, "throughput"),
    ];

    public string Id => "baseline";
    public TimeSpan Sustain => TimeSpan.FromMinutes(10);
    public TimeSpan EvaluationInterval => TimeSpan.FromMinutes(1);

    public IEnumerable<AnomalyCandidate> Evaluate(AnomalyContext ctx)
    {
        var keys = ctx.Live.Keys.ToList();
        foreach (var rule in Rules)
        {
            var spec = BaselineEngine.Specs.First(s => s.Id == rule.SpecId);
            foreach (var key in keys.Where(spec.Matches))
            {
                var baseline = ctx.Baselines.Get(key, spec.Context);
                if (baseline is null || !baseline.IsMature) continue;
                var values = LiveWindow.ContextValues(ctx, key, spec.Context, TimeSpan.FromMinutes(10));
                if (values is null || values.Count < 60) continue;

                var observed = Statistics.Median(values);
                var delta = observed - baseline.Median;
                var worse = rule.HigherIsWorse ? delta : -delta;
                var z = Statistics.RobustZ(observed, baseline.Median, baseline.Mad, floor: rule.MinDelta / 4);
                if (worse < rule.MinDelta || Math.Abs(z) < rule.MinZ) continue;

                var name = ctx.MetricName(key);
                var severity = worse >= rule.MinDelta * 2 && Math.Abs(z) >= rule.MinZ * 1.5 ? Severity.Warning : Severity.Notice;
                var confidence = baseline.DaysCovered >= 14 ? Confidence.High : baseline.DaysCovered >= 7 ? Confidence.Moderate : Confidence.Low;
                var contextText = BaselineContexts.Describe(spec.Context);
                var (obsText, baseText, deltaText) = Format(ctx, rule.Kind, observed, baseline.Median, delta);

                var evidence = new List<EvidenceItem>
                {
                    new(EvidenceKind.Observed, $"{name} {contextText} has a median of {obsText} over the last 10 minutes.", "Live telemetry"),
                    new(EvidenceKind.Observed, $"This system's {baseline.DaysCovered}-day baseline {contextText} is {baseText} (typical range {Fmt(ctx, rule.Kind, baseline.P05)}–{Fmt(ctx, rule.Kind, baseline.P95)}).", "Sentinel baseline"),
                };

                string title, description;
                if (rule.SpecId == "cpu-away")
                {
                    var top = ctx.PrivacyMode ? null : ctx.CurrentApps.OrderByDescending(a => a.CpuPercent).FirstOrDefault();
                    title = "Unexpected background CPU activity";
                    description = $"CPU usage while you were away is {obsText}, about {deltaText} above this computer's usual {baseText}.";
                    if (top is { CpuPercent: > 2 })
                        evidence.Add(new(EvidenceKind.Observed, $"{top.DisplayName} is currently the largest CPU consumer at {top.CpuPercent:F0}% of total capacity.", "Process telemetry"));
                    evidence.Add(new(EvidenceKind.Possible, "Scheduled maintenance, indexing, updates or a background app can cause this.", null));
                }
                else if (rule.Kind == "temperature")
                {
                    title = $"{name} higher than usual {contextText}";
                    var cpu = ctx.Live.Get(MetricKeys.CpuUtil)?.Stats(ctx.Now.AddMinutes(-10));
                    description = $"{name} {contextText} is about {deltaText} above this system's {baseline.DaysCovered}-day baseline.";
                    if (cpu is { N: > 0 } c)
                        evidence.Add(new(EvidenceKind.Observed, $"CPU utilization averaged {c.Avg:F0}% in the same window, so the workload itself is {(spec.Context == BaselineContext.LowLoad ? "light" : "comparable to the baseline context")}.", "Live telemetry"));
                    evidence.Add(new(EvidenceKind.Possible, "Restricted airflow, dust build-up, a warm room or a blocked vent can raise temperatures at the same workload.", null));
                }
                else if (rule.SpecId == "bat-drain")
                {
                    title = "Battery draining faster than usual";
                    description = $"The battery is discharging at about {UnitFormatterExtensions.W(-observed)}, compared with a typical {UnitFormatterExtensions.W(-baseline.Median)} on battery.";
                    var top = ctx.PrivacyMode ? null : ctx.CurrentApps.OrderByDescending(a => a.CpuPercent).FirstOrDefault();
                    if (top is { CpuPercent: > 5 })
                        evidence.Add(new(EvidenceKind.Inferred, $"{top.DisplayName} is using {top.CpuPercent:F0}% CPU, which likely contributes to the higher drain.", "Process telemetry"));
                }
                else if (rule.SpecId == "wifi-signal")
                {
                    title = "Wi-Fi signal weaker than usual";
                    description = $"Wi-Fi signal quality is {obsText}, about {deltaText.TrimStart('-')} below this laptop's normal {baseText}.";
                }
                else
                {
                    title = $"{name} unusually high {contextText}";
                    description = $"{name} {contextText} is {obsText}, compared with a usual {baseText}.";
                }

                yield return new AnomalyCandidate(key + "|" + spec.Id, key, title, description, baseline.Median, observed, Math.Abs(z),
                    deltaText, severity, confidence, evidence);
            }
        }
    }

    private static (string Obs, string Base, string Delta) Format(AnomalyContext ctx, string kind, double observed, double baseline, double delta) => kind switch
    {
        "temperature" => (ctx.Units.Temperature(observed), ctx.Units.Temperature(baseline), ctx.Units.TemperatureDelta(delta)),
        "throughput" => (ctx.Units.Throughput(observed), ctx.Units.Throughput(baseline), ctx.Units.Throughput(Math.Abs(delta))),
        "drain" => (UnitFormatterExtensions.W(-observed), UnitFormatterExtensions.W(-baseline), UnitFormatterExtensions.W(Math.Abs(delta))),
        _ => ($"{observed:F0}%", $"{baseline:F0}%", $"{delta:+0;-0} pts"),
    };

    private static string Fmt(AnomalyContext ctx, string kind, double v) => kind switch
    {
        "temperature" => ctx.Units.Temperature(v),
        "throughput" => ctx.Units.Throughput(v),
        "drain" => UnitFormatterExtensions.W(-v),
        _ => $"{v:F0}%",
    };
}

internal static class UnitFormatterExtensions
{
    public static string W(double watts) => watts.ToString("F1", CultureInfo.CurrentCulture) + " W";
}

/// <summary>Detects clusters of reliability events relative to this machine's own 30-day rate (Poisson tail test).</summary>
public sealed class EventClusterDetector : IAnomalyDetector
{
    private static readonly (EventCategory Category, string Title, int MinCount)[] Watched =
    [
        (EventCategory.DisplayDriverReset, "Cluster of display driver resets", 3),
        (EventCategory.AppCrash, "Application crashes increased", 4),
        (EventCategory.AppHang, "Applications stopped responding more often", 4),
        (EventCategory.NetworkDisconnected, "Recurring network disconnects", 4),
        (EventCategory.UnexpectedShutdown, "Repeated unexpected shutdowns", 2),
        (EventCategory.Bugcheck, "Repeated stop errors", 2),
        (EventCategory.StorageError, "Storage errors recorded", 3),
        (EventCategory.HardwareError, "Hardware error records", 2),
        (EventCategory.ServiceFailure, "Repeated service failures", 4),
    ];

    public string Id => "events";
    public TimeSpan Sustain => TimeSpan.Zero;
    public TimeSpan EvaluationInterval => TimeSpan.FromMinutes(10);

    public IEnumerable<AnomalyCandidate> Evaluate(AnomalyContext ctx)
    {
        var dayAgo = ctx.Now.AddDays(-1);
        var baselineFrom = ctx.Now.AddDays(-31);
        var all = ctx.History.QueryEvents(baselineFrom, ctx.Now, Watched.Select(w => w.Category).ToArray(), 20000);
        foreach (var (category, title, minCount) in Watched)
        {
            var recent = all.Where(e => e.Category == category && e.Timestamp >= dayAgo).ToList();
            if (recent.Count < minCount) continue;
            var prior = all.Count(e => e.Category == category && e.Timestamp < dayAgo);
            var earliest = ctx.History.EarliestSample() ?? baselineFrom;
            var priorDays = Math.Clamp((dayAgo - (earliest > baselineFrom ? earliest : baselineFrom)).TotalDays, 1, 30);
            var lambda = Math.Max(prior / priorDays, 0.05);
            var p = Statistics.PoissonUpperTail(recent.Count, lambda);
            if (p > 0.01) continue;

            var subjects = recent.Where(e => e.Subject is not null).GroupBy(e => e.Subject!).OrderByDescending(g => g.Count()).Take(3).ToList();
            var evidence = new List<EvidenceItem>
            {
                new(EvidenceKind.Observed, $"{recent.Count} × {SystemEvent.CategoryLabel(category).ToLowerInvariant()} in the last 24 hours.", "Windows event logs"),
                new(EvidenceKind.Observed, priorDays >= 7
                    ? $"The preceding {priorDays:F0}-day baseline averaged {lambda:F2} per day."
                    : $"Only {priorDays:F0} days of history are available for comparison.", "Sentinel history"),
            };
            foreach (var g in subjects)
                evidence.Add(new(EvidenceKind.Observed, $"{g.Key}: {g.Count()} occurrence(s).", "Windows event logs"));

            var confidence = priorDays >= 14 ? Confidence.High : priorDays >= 5 ? Confidence.Moderate : Confidence.Low;
            var severity = category is EventCategory.Bugcheck or EventCategory.UnexpectedShutdown or EventCategory.StorageError or EventCategory.HardwareError
                ? Severity.Warning : Severity.Notice;
            yield return new AnomalyCandidate(category.ToString(), "events." + category, title,
                $"{recent.Count} events in 24 hours against a usual rate of {lambda:F2} per day.", lambda, recent.Count, recent.Count / lambda,
                $"{recent.Count / lambda:F0}× usual rate", severity, confidence, evidence, recent.Min(e => e.Timestamp));
        }
    }
}

/// <summary>Heuristic memory-growth detector. Never claims a leak with certainty.</summary>
public sealed class MemoryGrowthDetector : IAnomalyDetector
{
    public string Id => "memgrowth";
    public TimeSpan Sustain => TimeSpan.Zero;
    public TimeSpan EvaluationInterval => TimeSpan.FromMinutes(15);

    public IEnumerable<AnomalyCandidate> Evaluate(AnomalyContext ctx)
    {
        if (ctx.PrivacyMode) yield break;
        foreach (var app in ctx.CurrentApps.OrderByDescending(a => a.PrivateBytes).Take(6))
        {
            var rows = ctx.History.AppSeries(app.AppKey, ctx.Now.AddHours(-8), ctx.Now);
            if (rows.Count < 120) continue;
            var first = rows[0].Timestamp;
            var x = rows.Select(r => (r.Timestamp - first).TotalHours).ToList();
            var y = rows.Select(r => r.MemoryBytes).ToList();
            var fit = Statistics.LinearRegression(x, y);
            var start = Statistics.Median(y.Take(15).ToList());
            var end = Statistics.Median(y.TakeLast(15).ToList());
            var growth = end - start;
            if (fit.Slope <= 0 || fit.RSquared < 0.85 || growth < 1024L * 1024 * 1024 || end < start * 1.5) continue;

            // Require no meaningful release along the way.
            double runningMax = 0, maxDrop = 0;
            foreach (var v in y)
            {
                runningMax = Math.Max(runningMax, v);
                maxDrop = Math.Max(maxDrop, (runningMax - v) / Math.Max(runningMax, 1));
            }
            if (maxDrop > 0.15) continue;

            var hours = x[^1];
            yield return new AnomalyCandidate(app.AppKey, "app." + app.AppKey + ".mem", $"{app.DisplayName} memory growing steadily",
                $"{app.DisplayName}'s private memory increased continuously from {ctx.Units.Bytes(start)} to {ctx.Units.Bytes(end)} over {hours:F1} hours " +
                "without a corresponding decrease. This pattern may indicate a memory leak or an unusually memory-heavy workload.",
                start, end, fit.RSquared, $"+{ctx.Units.Bytes(growth)}", Severity.Notice, fit.RSquared > 0.95 ? Confidence.Moderate : Confidence.Low,
                [
                    new(EvidenceKind.Observed, $"Linear trend of {ctx.Units.Bytes(fit.Slope)} per hour (R² = {fit.RSquared:F2}) across {rows.Count} minutes of samples.", "Sentinel app history"),
                    new(EvidenceKind.Observed, $"The largest release during the period was {maxDrop * 100:F0}% of peak.", "Sentinel app history"),
                    new(EvidenceKind.Possible, "Long-running browsers, editors and games can legitimately accumulate caches.", null),
                ], rows[0].Timestamp);
        }
    }
}

/// <summary>Detects devices that repeatedly disconnect and reconnect.</summary>
public sealed class DeviceFlappingDetector : IAnomalyDetector
{
    public string Id => "deviceflap";
    public TimeSpan Sustain => TimeSpan.Zero;
    public TimeSpan EvaluationInterval => TimeSpan.FromMinutes(2);

    public IEnumerable<AnomalyCandidate> Evaluate(AnomalyContext ctx)
    {
        var recent = ctx.History.QueryEvents(ctx.Now.AddMinutes(-30), ctx.Now, [EventCategory.DeviceDisconnected], 500);
        foreach (var g in recent.Where(e => e.Subject is not null).GroupBy(e => e.Subject!))
        {
            var n = g.Count();
            if (n < 3) continue;
            yield return new AnomalyCandidate(g.Key, "device." + g.Key, $"{g.Key} keeps disconnecting",
                $"{g.Key} disconnected {n} times in the last 30 minutes.", 0, n, n, $"{n} disconnects / 30 min",
                n >= 6 ? Severity.Warning : Severity.Notice, Confidence.High,
                [
                    new(EvidenceKind.Observed, $"{n} disconnect events recorded between {g.Min(e => e.Timestamp):HH:mm} and {g.Max(e => e.Timestamp):HH:mm}.", "Windows device notifications"),
                    new(EvidenceKind.Possible, "A loose cable, a failing port or hub, USB power management or low peripheral battery can cause repeated reconnects.", null),
                ], g.Min(e => e.Timestamp));
        }
    }
}

/// <summary>Compares the last 24 hours of disk writes with the previous two weeks.</summary>
public sealed class DiskWriteVolumeDetector : IAnomalyDetector
{
    private const double MinBytes = 30d * 1000 * 1000 * 1000;

    public string Id => "diskwrites";
    public TimeSpan Sustain => TimeSpan.Zero;
    public TimeSpan EvaluationInterval => TimeSpan.FromHours(1);

    public IEnumerable<AnomalyCandidate> Evaluate(AnomalyContext ctx)
    {
        var rows = ctx.History.MinuteValues(MetricKeys.DiskWrite, ctx.Now.AddDays(-15), ctx.Now);
        if (rows.Count < 1440 * 4) yield break;
        var nowMinute = ctx.Now.ToUnixTimeSeconds() / 60;
        var byDay = new Dictionary<long, double>();
        double last24 = 0;
        foreach (var (minute, v) in rows)
        {
            var age = nowMinute - minute;
            if (age < 1440) last24 += v * 60;
            else byDay[age / 1440] = byDay.GetValueOrDefault(age / 1440) + v * 60;
        }
        var prior = byDay.Values.Where(v => v > 0).ToList();
        if (prior.Count < 4) yield break;
        var p95 = Statistics.Percentile(prior, 0.95);
        var median = Statistics.Median(prior);
        if (last24 < Math.Max(p95 * 1.5, MinBytes)) yield break;
        yield return new AnomalyCandidate("daily", MetricKeys.DiskWrite, "Unusually high disk writes today",
            $"About {ctx.Units.Bytes(last24)} were written in the last 24 hours, compared with a typical {ctx.Units.Bytes(median)} per day.",
            median, last24, last24 / Math.Max(median, 1), $"{last24 / Math.Max(median, 1):F1}× typical", Severity.Notice, Confidence.Moderate,
            [
                new(EvidenceKind.Observed, $"Daily writes over the previous {prior.Count} days: median {ctx.Units.Bytes(median)}, 95th percentile {ctx.Units.Bytes(p95)}.", "Sentinel history"),
                new(EvidenceKind.Possible, "Large downloads, game installs, backups, updates or video editing can produce this legitimately.", null),
            ]);
    }
}
