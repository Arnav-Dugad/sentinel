using Sentinel.Analytics;
using Sentinel.Core.Metrics;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Telemetry;

namespace Sentinel.Intelligence;

/// <summary>
/// Transparent, rule-based health assessment. There is deliberately no single numeric score: each category
/// reports a status, a confidence and the exact factors that produced it. Unknown is a first-class answer.
/// </summary>
public sealed class HealthModel(HistoryStore store, ProviderSet providers, LiveMetricStore live, AnomalyEngine anomalies, UnitFormatter units)
{
    public HealthReport Evaluate(DateTimeOffset now)
    {
        var week = store.QueryEvents(now.AddDays(-7), now, null, 5000);
        var categories = new List<HealthCategoryResult>
        {
            Reliability(week, now),
            Thermals(now),
            Storage(week),
        };
        if (providers.HasBattery) categories.Add(Battery());
        categories.Add(Memory(now));
        categories.Add(Drivers(week));
        categories.Add(Network(week, now));
        categories.Add(Power(week));
        categories.Add(Consistency());

        var known = categories.Where(c => c.Status != HealthStatus.Unknown).ToList();
        var worst = known.Count == 0 ? HealthStatus.Unknown : known.Max(c => c.Status);
        var attention = known.Where(c => c.Status >= HealthStatus.Attention).ToList();
        HealthStatus overall;
        string headline, detail;
        if (known.Count == 0)
        {
            overall = HealthStatus.Unknown;
            headline = "Sentinel is still getting to know this PC.";
            detail = "Health assessments appear as soon as enough data has been collected.";
        }
        else if (worst == HealthStatus.Critical)
        {
            overall = HealthStatus.Critical;
            headline = "Your PC needs attention.";
            detail = string.Join(" ", attention.Where(a => a.Status == HealthStatus.Critical).Select(a => a.Summary));
        }
        else if (attention.Count > 0)
        {
            overall = HealthStatus.Attention;
            headline = attention.Count == 1 ? $"Your PC is mostly healthy. {attention[0].Name} needs a look." : $"Your PC is mostly healthy. {attention.Count} areas need a look.";
            detail = string.Join(" ", attention.Select(a => a.Summary));
        }
        else
        {
            overall = known.All(c => c.Status is HealthStatus.Excellent or HealthStatus.Good) ? HealthStatus.Excellent : HealthStatus.Good;
            headline = "Your PC is healthy.";
            detail = anomalies.Active.Count == 0 ? "No unusual behavior detected." : "Minor unusual behavior is being watched.";
        }
        return new HealthReport(now, overall, headline, detail, categories);
    }

    private static HealthCategoryResult Result(string key, string name, HealthStatus status, Confidence confidence, string summary, List<HealthFactor> factors) =>
        new(key, name, status, confidence, summary, factors);

    private HealthCategoryResult Reliability(IReadOnlyList<SystemEvent> week, DateTimeOffset now)
    {
        var factors = new List<HealthFactor>();
        var bugchecks = week.Count(e => e.Category == EventCategory.Bugcheck);
        var unexpected = week.Count(e => e.Category == EventCategory.UnexpectedShutdown);
        var crashes = week.Count(e => e.Category == EventCategory.AppCrash);
        var hangs = week.Count(e => e.Category == EventCategory.AppHang);
        var eventsReadable = providers.Events.Capabilities.Any(c => c.Name == "System" && c.Available);
        if (!eventsReadable)
            return Result("reliability", "Reliability", HealthStatus.Unknown, Confidence.Unknown, "Windows event logs could not be read.",
                [new("The System event log is not readable, so crashes and restarts cannot be assessed.", EvidenceKind.Unknown, HealthStatus.Unknown)]);

        factors.Add(new($"{bugchecks} stop error(s) in the last 7 days.", EvidenceKind.Observed, bugchecks > 0 ? HealthStatus.Attention : HealthStatus.Excellent));
        factors.Add(new($"{unexpected} unexpected shutdown(s) in the last 7 days.", EvidenceKind.Observed, unexpected >= 2 ? HealthStatus.Attention : unexpected == 1 ? HealthStatus.Normal : HealthStatus.Excellent));
        factors.Add(new($"{crashes} app crash(es) and {hangs} app hang(s) in the last 7 days.", EvidenceKind.Observed, crashes + hangs > 15 ? HealthStatus.Normal : HealthStatus.Excellent));

        var status = bugchecks >= 2 ? HealthStatus.Critical
            : bugchecks == 1 || unexpected >= 2 ? HealthStatus.Attention
            : unexpected == 1 || crashes + hangs > 15 ? HealthStatus.Normal
            : crashes + hangs > 3 ? HealthStatus.Good
            : HealthStatus.Excellent;
        var summary = status switch
        {
            HealthStatus.Critical => $"{bugchecks} stop errors this week.",
            HealthStatus.Attention => bugchecks == 1 ? "Windows recorded a stop error this week." : $"{unexpected} unexpected shutdowns this week.",
            HealthStatus.Normal => "Some instability was recorded this week.",
            _ => "No system crashes this week.",
        };
        var history = store.EarliestSample();
        var conf = history is { } h && now - h > TimeSpan.FromDays(7) ? Confidence.High : Confidence.Moderate;
        return Result("reliability", "Reliability", status, conf, summary, factors);
    }

    private HealthCategoryResult Thermals(DateTimeOffset now)
    {
        var factors = new List<HealthFactor>();
        var sensors = new List<(string Name, double Temp, double? Limit)>();
        foreach (var s in providers.Thermal.Sensors.Where(s => s.Temperature.HasValue && s.Temperature.Confidence >= Confidence.Moderate))
            sensors.Add((s.Name, s.Temperature.Value!.Value, s.LimitCelsius));
        foreach (var g in providers.Gpu.Latest.Where(g => g.Temperature.HasValue))
        {
            var adapter = providers.Gpu.Adapters.FirstOrDefault(a => a.Id == g.AdapterId);
            sensors.Add((adapter?.Name ?? "GPU", g.Temperature.Value!.Value, null));
        }
        foreach (var (id, h) in providers.Storage.Health.Where(kv => kv.Value.Temperature.HasValue))
            sensors.Add((providers.Storage.Disks.FirstOrDefault(d => d.Id == id)?.Model ?? "Drive", h.Temperature.Value!.Value, h.CriticalTemperature.Value));

        var thermalAnomalies = anomalies.Active.Where(a => a.MetricKey.StartsWith("thermal.", StringComparison.Ordinal) || a.MetricKey.EndsWith(".temp", StringComparison.Ordinal)).ToList();
        var perfLimit = live.Get("cpu.perflimit")?.Stats(now.AddMinutes(-10));
        var throttledGpu = providers.Gpu.Latest.FirstOrDefault(g => g.ThrottleReasons?.Contains("thermal", StringComparison.OrdinalIgnoreCase) == true);

        if (sensors.Count == 0 && perfLimit is not { N: > 0 })
            return Result("thermals", "Thermals", HealthStatus.Unknown, Confidence.Unknown, "No temperature sensors are exposed through safe interfaces.",
                [new("This system does not expose temperatures through the documented interfaces Sentinel uses.", EvidenceKind.Unknown, HealthStatus.Unknown)]);

        foreach (var s in sensors.OrderByDescending(s => s.Temp).Take(4))
        {
            var near = s.Limit is { } lim && s.Temp >= lim - 5;
            factors.Add(new($"{s.Name}: {units.Temperature(s.Temp)}" + (s.Limit is { } l ? $" (device limit {units.Temperature(l)})" : ""), EvidenceKind.Observed,
                near ? HealthStatus.Attention : HealthStatus.Normal));
        }
        foreach (var a in thermalAnomalies)
            factors.Add(new(a.Description, EvidenceKind.Inferred, HealthStatus.Attention));
        if (perfLimit is { N: > 0 } pl && pl.Avg > 5)
            factors.Add(new($"Windows reports the processor's performance was limited {pl.Avg:F0}% of the time in the last 10 minutes.", EvidenceKind.Observed, HealthStatus.Normal));
        if (throttledGpu is not null)
            factors.Add(new($"The GPU driver reports clock reductions: {throttledGpu.ThrottleReasons}.", EvidenceKind.Observed, HealthStatus.Attention));

        var critical = sensors.Any(s => s.Limit is { } l && s.Temp >= l);
        var status = critical ? HealthStatus.Critical
            : thermalAnomalies.Count > 0 || throttledGpu is not null || sensors.Any(s => s.Limit is { } l && s.Temp >= l - 5) ? HealthStatus.Attention
            : HealthStatus.Normal;
        var summary = status switch
        {
            HealthStatus.Critical => "A component is at its reported temperature limit.",
            HealthStatus.Attention => thermalAnomalies.FirstOrDefault()?.Title ?? "Temperatures are higher than usual.",
            _ => "Temperatures are within this system's usual range.",
        };
        return Result("thermals", "Thermals", status, sensors.Count >= 2 ? Confidence.High : Confidence.Moderate, summary, factors);
    }

    private HealthCategoryResult Storage(IReadOnlyList<SystemEvent> week)
    {
        var factors = new List<HealthFactor>();
        var status = HealthStatus.Excellent;
        if (providers.Storage.Disks.Count == 0)
            return Result("storage", "Storage", HealthStatus.Unknown, Confidence.Unknown, "No drives were discovered.", []);
        foreach (var d in providers.Storage.Disks)
        {
            providers.Storage.Health.TryGetValue(d.Id, out var h);
            if (h?.HealthStatus is "Unhealthy")
            {
                status = Max(status, HealthStatus.Critical);
                factors.Add(new($"Windows reports {d.Model} as unhealthy.", EvidenceKind.Observed, HealthStatus.Critical));
            }
            else if (h?.HealthStatus is "Warning")
            {
                status = Max(status, HealthStatus.Attention);
                factors.Add(new($"Windows reports a health warning for {d.Model}.", EvidenceKind.Observed, HealthStatus.Attention));
            }
            if (h?.CriticalWarning is > 0 and var cw)
            {
                status = Max(status, HealthStatus.Critical);
                factors.Add(new($"{d.Model} reports an NVMe critical warning (0x{cw:X2}).", EvidenceKind.Observed, HealthStatus.Critical));
            }
            if (h?.PercentageUsed.Value is { } used)
            {
                var s = used >= 100 ? HealthStatus.Attention : used >= 80 ? HealthStatus.Normal : HealthStatus.Excellent;
                status = Max(status, s);
                factors.Add(new($"{d.Model}: {used:F0}% of rated endurance used.", EvidenceKind.Observed, s));
            }
            if (h?.AvailableSpare.Value is { } spare && h.AvailableSpareThreshold.Value is { } thr && spare <= thr)
            {
                status = Max(status, HealthStatus.Critical);
                factors.Add(new($"{d.Model}: available spare {spare:F0}% is at or below its {thr:F0}% threshold.", EvidenceKind.Observed, HealthStatus.Critical));
            }
            if (h is null || (h.HealthStatus is null && !h.PercentageUsed.HasValue))
                factors.Add(new($"{d.Model}: detailed health is not exposed to standard queries.", EvidenceKind.Unknown, HealthStatus.Unknown));
        }
        foreach (var v in providers.Storage.Volumes.Where(v => v.IsSystem && v.TotalBytes > 0))
        {
            var free = v.FreeBytes * 100.0 / v.TotalBytes;
            var s = free < 5 ? HealthStatus.Attention : free < 10 ? HealthStatus.Normal : HealthStatus.Excellent;
            status = Max(status, s);
            factors.Add(new($"System drive {v.Name} has {free:F0}% free ({units.Bytes(v.FreeBytes)}).", EvidenceKind.Observed, s));
        }
        var errors = week.Count(e => e.Category == EventCategory.StorageError);
        if (errors > 0)
        {
            status = Max(status, errors >= 3 ? HealthStatus.Attention : HealthStatus.Normal);
            factors.Add(new($"{errors} storage error event(s) in the last 7 days.", EvidenceKind.Observed, errors >= 3 ? HealthStatus.Attention : HealthStatus.Normal));
        }
        var summary = status switch
        {
            HealthStatus.Critical => "A drive reports a critical health condition.",
            HealthStatus.Attention => factors.FirstOrDefault(f => f.Impact == HealthStatus.Attention)?.Text ?? "A drive needs attention.",
            HealthStatus.Normal => "Drives are working; minor items noted.",
            _ => "Drives report healthy.",
        };
        var conf = providers.Storage.Health.Values.Any(h => h.PercentageUsed.HasValue) ? Confidence.High : Confidence.Moderate;
        return Result("storage", "Storage", status, conf, summary, factors);
    }

    private HealthCategoryResult Battery()
    {
        var b = providers.Battery.Latest;
        var factors = new List<HealthFactor>();
        var info = b.Batteries.FirstOrDefault();
        var health = info?.EstimatedHealthPercent;
        if (health is null)
            return Result("battery", "Battery", HealthStatus.Unknown, Confidence.Low, "The battery does not report its design capacity.",
                [new("Capacity values are not reported in absolute units by this battery.", EvidenceKind.Unknown, HealthStatus.Unknown)]);
        var status = health >= 85 ? HealthStatus.Excellent : health >= 70 ? HealthStatus.Good : health >= 55 ? HealthStatus.Normal : HealthStatus.Attention;
        factors.Add(new($"Full-charge capacity is {health:F0}% of design capacity (estimate from firmware-reported values).", EvidenceKind.Observed, status));
        if (info?.CycleCount is { } cycles) factors.Add(new($"{cycles} charge cycles reported.", EvidenceKind.Observed, HealthStatus.Normal));
        var history = store.QueryCapacity();
        if (history.Count >= 2)
        {
            var first = history[0];
            var last = history[^1];
            var days = (last.Date - first.Date).TotalDays;
            if (days >= 14)
            {
                var change = (last.FullChargeMWh - first.FullChargeMWh) / first.FullChargeMWh * 100;
                factors.Add(new($"Full-charge capacity changed {change:+0.0;-0.0}% over {days:F0} days.", EvidenceKind.Observed, change < -10 ? HealthStatus.Attention : HealthStatus.Normal));
            }
        }
        return Result("battery", "Battery", status, Confidence.Moderate, status switch
        {
            HealthStatus.Excellent => "Battery capacity is close to new.",
            HealthStatus.Good => "Battery shows normal wear.",
            HealthStatus.Normal => "Battery shows noticeable wear.",
            _ => "Battery capacity is substantially reduced.",
        }, factors);
    }

    private HealthCategoryResult Memory(DateTimeOffset now)
    {
        var m = providers.Memory.Latest;
        if (m.TotalBytes == 0) return Result("memory", "Memory", HealthStatus.Unknown, Confidence.Unknown, "No memory data yet.", []);
        var used = live.Get(MetricKeys.MemUsedPct)?.Stats(now.AddMinutes(-10));
        var hard = live.Get(MetricKeys.MemHardFaults)?.Stats(now.AddMinutes(-10));
        var factors = new List<HealthFactor>
        {
            new($"{units.Bytes(m.UsedBytes)} of {units.Bytes(m.TotalBytes)} in use ({m.UsedPercent:F0}%).", EvidenceKind.Observed, m.UsedPercent > 90 ? HealthStatus.Attention : HealthStatus.Normal),
            new($"Committed memory is {m.CommitPercent:F0}% of the commit limit.", EvidenceKind.Observed, m.CommitPercent > 90 ? HealthStatus.Attention : HealthStatus.Normal),
        };
        if (hard is { N: > 0 } hf) factors.Add(new($"Hard faults averaged {hf.Avg:F0}/s over 10 minutes.", EvidenceKind.Observed, hf.Avg > 500 ? HealthStatus.Attention : HealthStatus.Normal));
        var leaks = anomalies.Active.Where(a => a.MetricKey.EndsWith(".mem", StringComparison.Ordinal)).ToList();
        foreach (var l in leaks) factors.Add(new(l.Description, EvidenceKind.Inferred, HealthStatus.Normal));
        var pressure = used is { N: > 0 } u && u.Avg > 90 && hard is { N: > 0 } h && h.Avg > 300 || m.CommitPercent > 92;
        var status = pressure ? HealthStatus.Attention : m.UsedPercent > 80 ? HealthStatus.Normal : HealthStatus.Good;
        return Result("memory", "Memory", status, Confidence.High, pressure ? "Memory pressure is high." : "Memory usage is normal.", factors);
    }

    private HealthCategoryResult Drivers(IReadOnlyList<SystemEvent> week)
    {
        var tdr = week.Count(e => e.Category == EventCategory.DisplayDriverReset);
        var problems = week.Where(e => e.Category == EventCategory.DeviceProblem).Select(e => e.Subject).Distinct().Count();
        var failures = week.Count(e => e.Category == EventCategory.DriverFailure);
        var basicDisplay = providers.Gpu.Adapters.Any(a => a.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase));
        var factors = new List<HealthFactor>
        {
            new($"{tdr} display driver reset(s) in the last 7 days.", EvidenceKind.Observed, tdr >= 3 ? HealthStatus.Attention : tdr > 0 ? HealthStatus.Normal : HealthStatus.Good),
            new($"{problems} device(s) reported problems in the last 7 days.", EvidenceKind.Observed, problems > 0 ? HealthStatus.Normal : HealthStatus.Good),
        };
        if (basicDisplay) factors.Add(new("Microsoft Basic Display Adapter is in use: the graphics driver appears not to be installed.", EvidenceKind.Observed, HealthStatus.Attention));
        var status = tdr >= 3 || basicDisplay ? HealthStatus.Attention : tdr > 0 || problems > 0 || failures > 0 ? HealthStatus.Normal : HealthStatus.Good;
        return Result("drivers", "Drivers", status, Confidence.Moderate, status switch
        {
            HealthStatus.Attention => basicDisplay ? "The graphics driver appears to be missing." : "The display driver reset several times this week.",
            HealthStatus.Normal => "Minor driver events this week.",
            _ => "Drivers are stable.",
        }, factors);
    }

    private HealthCategoryResult Network(IReadOnlyList<SystemEvent> week, DateTimeOffset now)
    {
        var genuine = NetworkEvents.GenuineDisconnects(week, week);
        var day = genuine.Count(e => e.Timestamp >= now.AddDays(-1));
        var weekCount = genuine.Count;
        var signal = live.Get(MetricKeys.WifiSignal)?.Stats(now.AddMinutes(-10));
        var factors = new List<HealthFactor>
        {
            new($"{day} disconnect(s) in the last 24 hours, {weekCount} in 7 days.", EvidenceKind.Observed, day >= 5 ? HealthStatus.Attention : day > 1 ? HealthStatus.Normal : HealthStatus.Good),
        };
        if (signal is { N: > 0 } s) factors.Add(new($"Wi-Fi signal quality averaged {s.Avg:F0}% over 10 minutes.", EvidenceKind.Observed, s.Avg < 40 ? HealthStatus.Normal : HealthStatus.Good));
        var status = day >= 5 ? HealthStatus.Attention : day > 1 || signal is { N: > 0, Avg: < 40 } ? HealthStatus.Normal : HealthStatus.Good;
        return Result("network", "Network", status, Confidence.Moderate,
            status == HealthStatus.Attention ? $"{day} network disconnects in the last day." : status == HealthStatus.Normal ? "Network mostly stable." : "Network is stable.", factors);
    }

    private HealthCategoryResult Power(IReadOnlyList<SystemEvent> week)
    {
        var wakes = week.Where(e => e.Category == EventCategory.Wake).ToList();
        var sleeps = store.QueryPowerSessions(DateTimeOffset.Now.AddDays(-7), DateTimeOffset.Now, PowerSessionKind.Sleep);
        var factors = new List<HealthFactor> { new($"{wakes.Count} wake event(s) recorded in 7 days.", EvidenceKind.Observed, HealthStatus.Normal) };
        var status = HealthStatus.Good;
        var heavy = sleeps.Where(s => s.PercentDelta is < 0 && s.Duration.TotalHours >= 1).Select(s => -s.PercentDelta!.Value / s.Duration.TotalHours).ToList();
        if (heavy.Count > 0)
        {
            var avg = heavy.Average();
            var s = avg > 3 ? HealthStatus.Attention : avg > 1.5 ? HealthStatus.Normal : HealthStatus.Good;
            status = Max(status, s);
            factors.Add(new($"Sleep drain averaged {avg:F1}% per hour across {heavy.Count} sleep session(s).", EvidenceKind.Observed, s));
        }
        return Result("power", "Power & sleep", status, heavy.Count > 0 ? Confidence.Moderate : Confidence.Low,
            status == HealthStatus.Attention ? "The battery drains noticeably during sleep." : "Power behavior looks normal.", factors);
    }

    private HealthCategoryResult Consistency()
    {
        var active = anomalies.Active;
        var factors = active.Select(a => new HealthFactor(a.Title, EvidenceKind.Inferred, a.Severity >= Severity.Warning ? HealthStatus.Attention : HealthStatus.Normal)).ToList();
        if (factors.Count == 0) factors.Add(new("No metric is currently outside this PC's learned normal range.", EvidenceKind.Inferred, HealthStatus.Good));
        var status = active.Any(a => a.Severity >= Severity.Warning) ? HealthStatus.Attention : active.Count > 0 ? HealthStatus.Normal : HealthStatus.Good;
        var mature = providers.Cpu.Latest.Timestamp != DateTimeOffset.MinValue;
        return Result("consistency", "Performance consistency", status, mature ? Confidence.Moderate : Confidence.Low,
            active.Count == 0 ? "Behavior matches this PC's normal patterns." : $"{active.Count} unusual pattern(s) detected.", factors);
    }

    private static HealthStatus Max(HealthStatus a, HealthStatus b) => (HealthStatus)Math.Max((int)a, (int)b);
}
