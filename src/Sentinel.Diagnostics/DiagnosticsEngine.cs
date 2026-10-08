using System.Globalization;
using Sentinel.Analytics;
using Sentinel.Core.Knowledge;
using Sentinel.Core.Metrics;
using Sentinel.Core.Settings;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Intelligence;
using Sentinel.Telemetry;

namespace Sentinel.Diagnostics;

/// <summary>
/// Read-only guided diagnostics. Each handler queries recorded telemetry and events for the requested time range,
/// applies deterministic reasoning, and separates what was observed from what is inferred or merely possible.
/// </summary>
public sealed class DiagnosticsEngine(
    HistoryStore store,
    LiveMetricStore live,
    ProviderSet providers,
    BaselineEngine baselines,
    AnomalyEngine anomalies,
    IntelligenceService intelligence,
    TimelineService timeline,
    ISettingsStore settings,
    UnitFormatter units)
{
    private readonly record struct Point(DateTimeOffset T, double Avg, double Max);

    public DiagnosticResult Run(ParsedQuery q) => q.Intent switch
    {
        QueryIntent.Heat => Heat(q),
        QueryIntent.BatteryDrain => BatteryDrain(q),
        QueryIntent.BatteryHealth => BatteryHealth(q),
        QueryIntent.Storage => Storage(q),
        QueryIntent.Slowdown => Slowdown(q),
        QueryIntent.Restart => Restart(q),
        QueryIntent.Crashes => Crashes(q),
        QueryIntent.Memory => Memory(q),
        QueryIntent.DriverChange => DriverChange(q),
        QueryIntent.TopApps => TopApps(q),
        QueryIntent.Network => Network(q),
        QueryIntent.Gaming => Gaming(q),
        QueryIntent.Sleep => Sleep(q),
        QueryIntent.WhatChanged => WhatChanged(q),
        QueryIntent.Overview => Overview(q),
        _ => Unknown(q),
    };

    // ------------------------------------------------------------------ data helpers

    private List<Point> Series(string key, TimeRange range)
    {
        var rows = store.QuerySeries(key, range.From, range.To);
        var points = rows.Select(r => new Point(r.Timestamp, r.Avg, r.Max)).ToList();
        // Fill the most recent minutes (not yet flushed to disk) from the live buffer.
        var lastStored = points.Count > 0 ? points[^1].T : range.From;
        var recent = live.Get(key)?.Since(lastStored.AddSeconds(1)) ?? [];
        foreach (var p in recent)
            if (p.Timestamp <= range.To && !double.IsNaN(p.Value)) points.Add(new Point(p.Timestamp.ToLocalTime(), p.Value, p.Value));
        return points;
    }

    private (double Avg, double Max, DateTimeOffset PeakAt, int N)? Summarize(string key, TimeRange range)
    {
        var s = Series(key, range);
        if (s.Count == 0) return null;
        var peak = s.MaxBy(p => p.Max);
        return (s.Average(p => p.Avg), peak.Max, peak.T, s.Count);
    }

    private IEnumerable<string> TemperatureKeys() =>
        live.Definitions.Where(d => d.Unit == MetricUnit.Celsius && d.Persist == PersistPolicy.Full).Select(d => d.Key)
            .Concat(live.Keys.Where(k => k == MetricKeys.GpuTempAny)).Distinct();

    private string Name(string key) => live.GetDefinition(key)?.Name ?? key;

    private bool Private => settings.Current.PrivacyMode;

    // ------------------------------------------------------------------ heat

    private DiagnosticResult Heat(ParsedQuery q)
    {
        var range = q.Range;
        var findings = new List<Finding>();
        var facts = new List<Fact>();
        var temps = TemperatureKeys().Select(k => (Key: k, S: Summarize(k, range))).Where(t => t.S is not null).ToList();
        if (temps.Count == 0)
        {
            return new DiagnosticResult(q.Text, q.Intent, range, "No temperature data", "I don't have temperature readings for this period.", Confidence.Low,
                [
                    new(EvidenceKind.Unknown, "This PC does not expose temperatures through the documented interfaces Sentinel uses, or Sentinel was not running then."),
                    .. LoadFindings(range, null),
                ], [], []);
        }

        var hottest = temps.OrderByDescending(t => t.S!.Value.Max).First();
        var (avg, max, peakAt, _) = hottest.S!.Value;
        var at = q.PointInTime ?? peakAt;
        findings.Add(new(EvidenceKind.Observed, $"{Name(hottest.Key)} peaked at {units.Temperature(max)} at {peakAt:t} (average {units.Temperature(avg)} over {range.Label}).", "Sentinel history"));
        foreach (var t in temps.Where(t => t.Key != hottest.Key).Take(3))
            facts.Add(new(Name(t.Key), $"peak {units.Temperature(t.S!.Value.Max)}, avg {units.Temperature(t.S.Value.Avg)}"));

        var window = new TimeRange(at.AddMinutes(-10), at.AddMinutes(5), "around the peak");
        var cpu = Summarize(MetricKeys.CpuUtil, window);
        var gpu = Summarize(MetricKeys.GpuUtilAny, window);
        var heavy = cpu is { Avg: > 50 } || gpu is { Avg: > 50 };
        if (cpu is { } c) findings.Add(new(EvidenceKind.Observed, $"CPU utilization averaged {c.Avg:F0}% (peak {c.Max:F0}%) in the 15 minutes around {at:t}.", "Sentinel history"));
        if (gpu is { } g) findings.Add(new(EvidenceKind.Observed, $"GPU utilization averaged {g.Avg:F0}% (peak {g.Max:F0}%) in the same window.", "Sentinel history"));
        var gpuPower = live.Keys.Where(k => k.StartsWith("gpu.", StringComparison.Ordinal) && k.EndsWith(".power", StringComparison.Ordinal)).Select(k => Summarize(k, window)).FirstOrDefault(s => s is not null);
        if (gpuPower is { } gp) findings.Add(new(EvidenceKind.Observed, $"GPU power averaged {gp.Avg:F0} W.", "NVIDIA NVML"));
        if (Summarize(MetricKeys.CpuPower, window) is { } cpw) findings.Add(new(EvidenceKind.Observed, $"CPU package power averaged {cpw.Avg:F0} W.", "Windows Energy Meter"));
        if (Summarize(MetricKeys.AcOnline, window) is { } ac) facts.Add(new("Power source", ac.Avg > 0.5 ? "Plugged in" : "On battery"));

        if (!Private)
        {
            var apps = store.AppUsageAt(at, 3);
            if (apps.Count > 0)
                findings.Add(new(EvidenceKind.Observed, "Top CPU consumers at that time: " + string.Join(", ", apps.Select(a => $"{AppNames.Display(a.App, providers)} ({a.Cpu:F0}%)")) + ".", "Sentinel app history"));
        }

        var perfLimit = Summarize("cpu.perflimit", window);
        var throttled = perfLimit is { Avg: > 10 };
        if (perfLimit is { } pl)
            findings.Add(new(EvidenceKind.Observed, $"Windows reported the processor's performance was limited {pl.Avg:F0}% of the time (peak {pl.Max:F0}%).", "Processor performance counters"));

        var b = baselines.Get(hottest.Key, heavy ? BaselineContext.HighLoad : BaselineContext.LowLoad);
        if (b is { IsMature: true })
        {
            var delta = max - b.P95;
            findings.Add(new(EvidenceKind.Observed, $"This system's {b.DaysCovered}-day typical range {(heavy ? "under load" : "at low load")} is {units.Temperature(b.P05)}–{units.Temperature(b.P95)}.", "Sentinel baseline"));
            if (delta > 3) findings.Add(new(EvidenceKind.Inferred, $"The peak was {units.TemperatureDelta(delta).TrimStart('+')} above the usual upper range for this kind of workload.", "Sentinel baseline"));
            else findings.Add(new(EvidenceKind.Inferred, "The temperature was within this PC's normal range for this kind of workload.", "Sentinel baseline"));
        }
        else
        {
            findings.Add(new(EvidenceKind.Unknown, "There is not yet enough history to say what is normal for this PC (baselines need a few days)."));
        }

        if (heavy)
            findings.Add(new(EvidenceKind.Inferred, "The temperature rise is strongly associated with sustained processor/graphics load at that time.", "Correlation"));
        else
        {
            findings.Add(new(EvidenceKind.Inferred, "The load was light, so the heat is not explained by CPU/GPU work alone.", "Correlation"));
            findings.Add(new(EvidenceKind.Possible, "Charging, a warm environment, restricted airflow (soft surface, blocked vents) or dust can raise temperatures at light load."));
        }
        if (throttled && heavy)
            findings.Add(new(EvidenceKind.Inferred, "Performance-limit counters were elevated at the same time as high temperature and load, which is evidence of thermal or power limiting.", "Processor performance counters"));
        else if (!throttled)
            findings.Add(new(EvidenceKind.Observed, "No reliable throttling evidence was detected in the performance-limit counters.", "Processor performance counters"));
        findings.Add(new(EvidenceKind.Possible, "Ambient temperature and airflow also affect temperatures but are not measured."));

        var summary = heavy
            ? $"{Name(hottest.Key)} reached {units.Temperature(max)} at {peakAt:t} while the system was under sustained load" + (throttled ? ", with evidence of performance limiting." : "; no reliable throttling evidence was detected.")
            : $"{Name(hottest.Key)} reached {units.Temperature(max)} at {peakAt:t} although the load was light.";
        return new DiagnosticResult(q.Text, q.Intent, range, "Why it got hot", summary, b is { IsMature: true } ? Confidence.Moderate : Confidence.Low, findings, facts,
            [new RecommendedAction("Keep vents clear and use the laptop on a hard surface. Check that your manufacturer's cooling mode suits the task.", null)]);
    }

    private IEnumerable<Finding> LoadFindings(TimeRange range, DateTimeOffset? at)
    {
        var r = at is { } a ? new TimeRange(a.AddMinutes(-10), a.AddMinutes(5), "around then") : range;
        if (Summarize(MetricKeys.CpuUtil, r) is { } c) yield return new(EvidenceKind.Observed, $"CPU utilization averaged {c.Avg:F0}% (peak {c.Max:F0}%).", "Sentinel history");
        if (Summarize(MetricKeys.GpuUtilAny, r) is { } g) yield return new(EvidenceKind.Observed, $"GPU utilization averaged {g.Avg:F0}% (peak {g.Max:F0}%).", "Sentinel history");
    }

    // ------------------------------------------------------------------ battery drain

    private DiagnosticResult BatteryDrain(ParsedQuery q)
    {
        if (!providers.HasBattery)
            return new DiagnosticResult(q.Text, q.Intent, q.Range, "No battery", "This PC does not have a battery.", Confidence.High, [new(EvidenceKind.Observed, "No system battery was detected.")], [], []);
        var range = q.Range;
        var findings = new List<Finding>();
        var facts = new List<Fact>();
        var rate = Series(MetricKeys.BatRate, range);
        var ac = Series(MetricKeys.AcOnline, range);
        var onBattery = rate.Where(p => p.Avg < -0.3 && !ac.Any(a => Math.Abs((a.T - p.T).TotalSeconds) < 60 && a.Avg > 0.5)).ToList();
        if (onBattery.Count == 0)
        {
            var sessions = store.QueryPowerSessions(range.From, range.To, PowerSessionKind.Discharge);
            if (sessions.Count == 0)
                return DiagnosticResult.NotEnoughEvidence(q.Text, q.Intent, range, "why the battery drained " + range.Label + " — no time on battery was recorded");
        }
        var avgW = onBattery.Count > 0 ? -onBattery.Average(p => p.Avg) : 0;
        if (onBattery.Count > 0)
            findings.Add(new(EvidenceKind.Observed, $"While on battery {range.Label}, Sentinel observed approximately {avgW:F1} W average system discharge.", "Battery firmware via Windows"));
        var discharge = store.QueryPowerSessions(range.From, range.To, PowerSessionKind.Discharge).FirstOrDefault();
        if (discharge is { AverageWatts: { } w })
            findings.Add(new(EvidenceKind.Observed, $"Last battery session: {discharge.Start:t}–{discharge.End:t}, {(discharge.PercentDelta is { } pd ? $"{-pd:F0}% used" : "")} at about {-w:F1} W.", "Sentinel sessions"));

        var b = baselines.Get(MetricKeys.BatRate, BaselineContext.OnBattery);
        if (b is { IsMature: true } && onBattery.Count > 0)
        {
            var typical = -b.Median;
            findings.Add(new(EvidenceKind.Observed, $"This laptop's typical discharge on battery is {typical:F1} W ({b.DaysCovered}-day baseline).", "Sentinel baseline"));
            findings.Add(avgW > typical * 1.3
                ? new(EvidenceKind.Inferred, $"Drain was about {(avgW / typical - 1) * 100:F0}% higher than usual.", "Sentinel baseline")
                : new(EvidenceKind.Inferred, "Drain was within the usual range for this laptop.", "Sentinel baseline"));
        }

        var batteryMinutes = new TimeRange(onBattery.Count > 0 ? onBattery.Min(p => p.T) : range.From, onBattery.Count > 0 ? onBattery.Max(p => p.T) : range.To, range.Label);
        if (Summarize(MetricKeys.CpuUtil, batteryMinutes) is { } c) findings.Add(new(EvidenceKind.Observed, $"CPU averaged {c.Avg:F0}% while on battery.", "Sentinel history"));
        if (Summarize(MetricKeys.GpuUtilAny, batteryMinutes) is { } g && g.Avg > 5) findings.Add(new(EvidenceKind.Observed, $"GPU averaged {g.Avg:F0}% while on battery.", "Sentinel history"));
        if (Summarize(MetricKeys.NetRx, batteryMinutes) is { } n && n.Avg > 500_000) findings.Add(new(EvidenceKind.Observed, $"Network download averaged {units.Throughput(n.Avg)}.", "Sentinel history"));
        if (!Private)
        {
            var apps = store.TopApps(batteryMinutes.From, batteryMinutes.To, 4);
            if (apps.Count > 0)
            {
                findings.Add(new(EvidenceKind.Observed, "Largest CPU consumers during that time: " + string.Join(", ", apps.Select(a => $"{AppNames.Display(a.App, providers)} ({a.AvgCpu:F1}% avg)")) + ".", "Sentinel app history"));
                if (apps[0].AvgCpu > 8) findings.Add(new(EvidenceKind.Inferred, $"{AppNames.Display(apps[0].App, providers)} is the most likely software contributor to the drain.", "Correlation"));
            }
        }
        findings.Add(new(EvidenceKind.Unknown, "Display brightness and per-component power are not exposed, so their share of the drain cannot be measured."));
        var health = providers.Battery.Latest.Batteries.FirstOrDefault()?.EstimatedHealthPercent;
        if (health is { } h) facts.Add(new("Estimated battery health", $"{h:F0}% of design capacity"));
        var summary = onBattery.Count > 0 ? $"Average discharge on battery {range.Label} was about {avgW:F1} W." : "Battery sessions were recorded, but no detailed discharge rate.";
        return new DiagnosticResult(q.Text, q.Intent, range, "Battery drain", summary, b is { IsMature: true } ? Confidence.Moderate : Confidence.Low, findings, facts,
            [new RecommendedAction("Review which apps use the most battery in Windows Settings.", "ms-settings:batterysaver-usagedetails", "Open Battery usage")]);
    }

    private DiagnosticResult BatteryHealth(ParsedQuery q)
    {
        if (!providers.HasBattery)
            return new DiagnosticResult(q.Text, q.Intent, q.Range, "No battery", "This PC does not have a battery.", Confidence.High, [new(EvidenceKind.Observed, "No system battery was detected.")], [], []);
        var info = providers.Battery.Latest.Batteries.FirstOrDefault();
        var findings = new List<Finding>();
        var facts = new List<Fact>();
        if (info?.EstimatedHealthPercent is { } hp)
        {
            findings.Add(new(EvidenceKind.Observed, $"Full-charge capacity {info.FullChargeCapacityMWh / 1000.0:F1} Wh vs design {info.DesignCapacityMWh / 1000.0:F1} Wh — estimated health {hp:F0}%.", "Battery firmware"));
            facts.Add(new("Estimated health", $"{hp:F0}%", "Full-charge ÷ design capacity"));
        }
        if (info?.CycleCount is { } cc) facts.Add(new("Cycle count", cc.ToString(CultureInfo.CurrentCulture)));
        var history = store.QueryCapacity();
        if (history.Count >= 3 && (history[^1].Date - history[0].Date).TotalDays >= 21)
        {
            var x = history.Select(h => (h.Date - history[0].Date).TotalDays).ToList();
            var fitAll = Statistics.LinearRegression(x, history.Select(h => h.FullChargeMWh).ToList());
            findings.Add(new(EvidenceKind.Observed, $"Capacity changed from {history[0].FullChargeMWh / 1000:F1} Wh ({history[0].Date:d}) to {history[^1].FullChargeMWh / 1000:F1} Wh ({history[^1].Date:d}).", string.Join(", ", history.Select(h => h.Source).Distinct())));
            var recent = history.Where(h => h.Date >= DateTimeOffset.Now.AddDays(-90)).ToList();
            var older = history.Where(h => h.Date < DateTimeOffset.Now.AddDays(-90)).ToList();
            if (recent.Count >= 3 && older.Count >= 3)
            {
                var fr = Statistics.LinearRegression(recent.Select(h => (h.Date - recent[0].Date).TotalDays).ToList(), recent.Select(h => h.FullChargeMWh).ToList());
                var fo = Statistics.LinearRegression(older.Select(h => (h.Date - older[0].Date).TotalDays).ToList(), older.Select(h => h.FullChargeMWh).ToList());
                var rRecent = -fr.Slope * 30 / 1000;
                var rOld = -fo.Slope * 30 / 1000;
                findings.Add(new(EvidenceKind.Inferred, $"Recent wear rate is about {rRecent:F2} Wh/month versus {rOld:F2} Wh/month earlier — {(rRecent > rOld * 1.5 && rRecent > 0.1 ? "faster than before" : "not faster than before")}.", "Linear trends"));
            }
            else
            {
                findings.Add(new(EvidenceKind.Inferred, $"Overall trend: about {-fitAll.Slope * 30 / 1000:F2} Wh per month.", "Linear trend"));
            }
        }
        else
        {
            findings.Add(new(EvidenceKind.Unknown, "Not enough capacity history yet. Import Windows' battery report on the Battery page to see months of history immediately."));
        }
        findings.Add(new(EvidenceKind.Possible, "Capacity figures are firmware estimates and can shift a few percent after recalibration."));
        return new DiagnosticResult(q.Text, q.Intent, q.Range, "Battery health",
            info?.EstimatedHealthPercent is { } h2 ? $"The battery holds about {h2:F0}% of its design capacity." : "The battery does not report its design capacity.",
            history.Count >= 3 ? Confidence.Moderate : Confidence.Low, findings, facts, []);
    }

    // ------------------------------------------------------------------ storage

    private DiagnosticResult Storage(ParsedQuery q)
    {
        var findings = new List<Finding>();
        var facts = new List<Fact>();
        foreach (var d in providers.Storage.Disks)
        {
            providers.Storage.Health.TryGetValue(d.Id, out var h);
            if (h is null) continue;
            if (h.HealthStatus is { } hs) findings.Add(new(EvidenceKind.Observed, $"{d.Model}: Windows reports '{hs}'.", "Storage Management API"));
            var hist = store.QueryDiskHealth(d.Id).Where(x => x.PercentUsed is not null).ToList();
            if (hist.Count >= 2)
            {
                var first = hist.First(x => x.Day >= q.Range.From.Date || x == hist[^1]);
                findings.Add(new(EvidenceKind.Observed, $"{d.Model}: Percentage Used went from {first.PercentUsed:F0}% ({first.Day:d}) to {hist[^1].PercentUsed:F0}% ({hist[^1].Day:d}).", "NVMe health log"));
            }
            else if (h.PercentageUsed.Value is { } pu)
            {
                findings.Add(new(EvidenceKind.Observed, $"{d.Model}: {pu:F0}% of rated endurance used; spare {h.AvailableSpare.Value:F0}%.", "NVMe health log"));
            }
            if (h.CriticalWarning is > 0 and var cw) findings.Add(new(EvidenceKind.Observed, $"{d.Model} reports an NVMe critical warning (0x{cw:X2}).", "NVMe health log", Severity.Critical));
            if (h.MediaErrors.Value is > 0 and var me) findings.Add(new(EvidenceKind.Observed, $"{d.Model} has recorded {me:F0} media/data integrity errors over its lifetime.", "NVMe health log", Severity.Warning));
            if (h.Temperature.Value is { } t) facts.Add(new($"{d.Model} temperature", units.Temperature(t)));
            if (h.DataWrittenBytes.Value is { } w) facts.Add(new($"{d.Model} lifetime writes", units.Bytes(w)));
        }
        var errors = store.QueryEvents(q.Range.From, q.Range.To, [EventCategory.StorageError], 200);
        findings.Add(new(EvidenceKind.Observed, $"{errors.Count} storage error event(s) recorded {q.Range.Label}.", "Windows event logs", errors.Count > 0 ? Severity.Notice : Severity.Info));
        if (findings.Count == 1 && errors.Count == 0)
            findings.Insert(0, new(EvidenceKind.Unknown, "None of the drives returned detailed health data to a standard query."));
        var bad = findings.Any(f => f.Severity >= Severity.Warning);
        return new DiagnosticResult(q.Text, q.Intent, q.Range, "Drive health", bad ? "A drive reports a condition that needs attention." : "Drive health looks stable.",
            Confidence.High, findings, facts, []);
    }

    // ------------------------------------------------------------------ slowdown

    private DiagnosticResult Slowdown(ParsedQuery q)
    {
        var range = q.Range;
        var findings = new List<Finding>();
        var cpu = Series(MetricKeys.CpuUtil, range);
        var mem = Series(MetricKeys.MemUsedPct, range);
        var hard = Series(MetricKeys.MemHardFaults, range);
        var disk = Series(MetricKeys.DiskActive, range);
        if (cpu.Count == 0) return DiagnosticResult.NotEnoughEvidence(q.Text, q.Intent, range, "why the PC was slow");

        var minutesPer = Math.Max(range.Span.TotalMinutes / Math.Max(cpu.Count, 1), 1.0 / 6);
        var cpuSat = cpu.Count(p => p.Avg > 90) * minutesPer;
        var memPress = mem.Count(p => p.Avg > 90) * minutesPer;
        var paging = hard.Count(p => p.Avg > 500) * minutesPer;
        var diskBusy = disk.Count(p => p.Avg > 90) * minutesPer;
        findings.Add(new(EvidenceKind.Observed, $"CPU above 90% for about {cpuSat:F0} min; peak {cpu.Max(p => p.Max):F0}%.", "Sentinel history", cpuSat > 10 ? Severity.Notice : Severity.Info));
        if (mem.Count > 0) findings.Add(new(EvidenceKind.Observed, $"Memory above 90% for about {memPress:F0} min; peak {mem.Max(p => p.Max):F0}%.", "Sentinel history", memPress > 10 ? Severity.Notice : Severity.Info));
        if (hard.Count > 0) findings.Add(new(EvidenceKind.Observed, $"Heavy paging (over 500 hard faults/s) for about {paging:F0} min.", "Sentinel history", paging > 5 ? Severity.Notice : Severity.Info));
        if (disk.Count > 0) findings.Add(new(EvidenceKind.Observed, $"A disk was more than 90% busy for about {diskBusy:F0} min.", "Sentinel history", diskBusy > 10 ? Severity.Notice : Severity.Info));
        if (Summarize("cpu.perflimit", range) is { Avg: > 10 } pl)
            findings.Add(new(EvidenceKind.Observed, $"Processor performance was limited {pl.Avg:F0}% of the time.", "Processor performance counters", Severity.Notice));

        if (!Private)
        {
            var apps = store.TopApps(range.From, range.To, 3);
            if (apps.Count > 0) findings.Add(new(EvidenceKind.Observed, "Heaviest apps: " + string.Join(", ", apps.Select(a => $"{AppNames.Display(a.App, providers)} ({a.AvgCpu:F0}% avg CPU, {units.Bytes(a.PeakMemoryBytes)} peak)")) + ".", "Sentinel app history"));
        }
        var updates = store.QueryEvents(range.From, range.To, [EventCategory.UpdateInstalled], 20);
        if (updates.Count > 0) findings.Add(new(EvidenceKind.Possible, $"{updates.Count} update(s) installed in this period; installation work can temporarily load the disk and CPU.", "Windows Update"));
        var drv = store.QueryEvents(range.From, range.To, [EventCategory.DisplayDriverReset, EventCategory.DriverFailure, EventCategory.StorageError], 20);
        if (drv.Count > 0) findings.Add(new(EvidenceKind.Possible, $"{drv.Count} driver/storage error event(s) occurred in this period and can cause pauses.", "Windows event logs"));

        string summary;
        if (memPress > 10 && paging > 5) { summary = "Memory pressure with heavy paging is the most likely cause of slowness."; findings.Add(new(EvidenceKind.Inferred, summary)); }
        else if (cpuSat > 10) { summary = "The CPU was saturated for extended periods."; findings.Add(new(EvidenceKind.Inferred, summary)); }
        else if (diskBusy > 10) { summary = "Disk activity was saturated for extended periods."; findings.Add(new(EvidenceKind.Inferred, summary)); }
        else { summary = "No sustained resource bottleneck was recorded."; findings.Add(new(EvidenceKind.Unknown, "Short stalls (under a few seconds) are not visible at Sentinel's history resolution. Use a 30-second Performance Investigation while the slowdown happens.")); }
        return new DiagnosticResult(q.Text, q.Intent, range, "Why it was slow", summary, Confidence.Moderate, findings, [], []);
    }

    // ------------------------------------------------------------------ restarts & crashes

    private DiagnosticResult Restart(ParsedQuery q)
    {
        var range = q.Range;
        var events = store.QueryEvents(range.From, range.To,
            [EventCategory.Boot, EventCategory.Shutdown, EventCategory.UnexpectedShutdown, EventCategory.Bugcheck, EventCategory.UpdateInstalled], 500)
            .OrderBy(e => e.Timestamp).ToList();
        var boots = events.Where(e => e.Category == EventCategory.Boot && e.Code?.StartsWith("boottime", StringComparison.Ordinal) != true).ToList();
        if (boots.Count == 0 && !events.Any(e => e.Category is EventCategory.UnexpectedShutdown or EventCategory.Bugcheck))
            return new DiagnosticResult(q.Text, q.Intent, range, "No restarts", $"No restart was recorded {range.Label}.", Confidence.High,
                [new(EvidenceKind.Observed, $"Windows event logs show no startup events {range.Label}.", "Windows event logs")], [], []);

        var lastBoot = boots.LastOrDefault() ?? events.Last();
        var before = events.Where(e => e.Timestamp <= lastBoot.Timestamp.AddMinutes(5) && e.Timestamp >= lastBoot.Timestamp.AddHours(-24)).ToList();
        var findings = new List<Finding> { new(EvidenceKind.Observed, $"Windows started at {UnitFormatter.Absolute(lastBoot.Timestamp)}.", "Kernel-General 12") };
        var bug = before.LastOrDefault(e => e.Category == EventCategory.Bugcheck);
        var unexpected = before.LastOrDefault(e => e.Category == EventCategory.UnexpectedShutdown);
        var requested = before.LastOrDefault(e => e.Category == EventCategory.Shutdown && e.Title.Contains("requested", StringComparison.OrdinalIgnoreCase));
        var update = before.LastOrDefault(e => e.Category == EventCategory.UpdateInstalled);
        string summary;
        Confidence confidence;
        if (bug is not null)
        {
            var entry = bug.Code is null ? null : BugcheckCatalog.Lookup(bug.Code);
            findings.Add(new(EvidenceKind.Observed, $"Windows recorded stop code {bug.Code}{(entry is null ? "" : $" ({entry.Name})")} at {UnitFormatter.Absolute(bug.Timestamp)}.", "WER system error report", Severity.Critical));
            if (entry is not null) findings.Add(new(EvidenceKind.Inferred, $"Category: {entry.Category}. {entry.Explanation}", "Microsoft bug check reference"));
            findings.Add(new(EvidenceKind.Unknown, "Identifying the exact faulting driver requires analyzing the memory dump, which Sentinel does not upload or parse."));
            summary = $"The restart was caused by a stop error ({entry?.Name ?? bug.Code}).";
            confidence = Confidence.High;
        }
        else if (unexpected is not null)
        {
            findings.Add(new(EvidenceKind.Observed, unexpected.Detail ?? "Windows recorded an unexpected shutdown.", "Kernel-Power 41"));
            findings.Add(new(EvidenceKind.Possible, "Without a stop code, common causes are power loss, a held power button, a hard hang or a battery that ran out."));
            summary = "Windows restarted without a clean shutdown and no stop error was recorded.";
            confidence = Confidence.Moderate;
        }
        else if (requested is not null)
        {
            findings.Add(new(EvidenceKind.Observed, $"{requested.Title} at {UnitFormatter.Absolute(requested.Timestamp)}." + (requested.Detail is null ? "" : $" Reason: {requested.Detail}."), "User32 1074"));
            if (update is not null) findings.Add(new(EvidenceKind.Inferred, $"An update finished installing shortly before: {update.Title}.", "Windows Update"));
            summary = $"The restart was requested by {requested.Subject ?? "a process"}" + (update is not null ? ", following an update." : ".");
            confidence = Confidence.High;
        }
        else
        {
            summary = "Windows started, but the logs do not record why the previous session ended.";
            findings.Add(new(EvidenceKind.Unknown, "No shutdown, stop error or restart request was found before this startup."));
            confidence = Confidence.Low;
        }
        var count = boots.Count;
        if (count > 1) findings.Add(new(EvidenceKind.Observed, $"{count} startups were recorded {range.Label}.", "Windows event logs"));
        return new DiagnosticResult(q.Text, q.Intent, range, "Why it restarted", summary, confidence, findings, [], []);
    }

    private DiagnosticResult Crashes(ParsedQuery q)
    {
        var range = q.Range;
        var events = store.QueryEvents(range.From, range.To, [EventCategory.AppCrash, EventCategory.AppHang], 2000);
        var prior = store.QueryEvents(range.Previous("before").From, range.From, [EventCategory.AppCrash, EventCategory.AppHang], 2000);
        if (events.Count == 0)
            return new DiagnosticResult(q.Text, q.Intent, range, "No crashes", $"No application crashes or hangs were recorded {range.Label}.", Confidence.High,
                [new(EvidenceKind.Observed, $"Windows Error Reporting logged no crashes {range.Label}.", "Application event log")], [], []);
        var findings = new List<Finding>
        {
            new(EvidenceKind.Observed, $"{events.Count} crash/hang event(s) {range.Label}, compared with {prior.Count} in the preceding period of the same length.", "Application event log"),
        };
        foreach (var g in events.GroupBy(e => e.Subject ?? "Unknown").OrderByDescending(g => g.Count()).Take(5))
        {
            var modules = g.Select(e => e.Detail).Where(d => d is not null).GroupBy(d => d).OrderByDescending(m => m.Count()).FirstOrDefault()?.Key;
            findings.Add(new(EvidenceKind.Observed, $"{g.Key}: {g.Count()} time(s)" + (modules is null ? "." : $". Most common: {modules}"), "Application event log"));
        }
        var top = events.GroupBy(e => e.Subject).OrderByDescending(g => g.Count()).First();
        if (top.Count() >= 3)
            findings.Add(new(EvidenceKind.Inferred, $"Crashes are concentrated in {top.Key}, which points to a problem with that app (or a component it loads) rather than the whole system."));
        return new DiagnosticResult(q.Text, q.Intent, range, "App crashes", $"{events.Count} app crash/hang events {range.Label}.", Confidence.High, findings, [], []);
    }

    // ------------------------------------------------------------------ memory, apps, drivers

    private DiagnosticResult Memory(ParsedQuery q)
    {
        var m = providers.Memory.Latest;
        var findings = new List<Finding>
        {
            new(EvidenceKind.Observed, $"{units.Bytes(m.UsedBytes)} of {units.Bytes(m.TotalBytes)} physical memory in use ({m.UsedPercent:F0}%).", "Windows memory status"),
            new(EvidenceKind.Observed, $"Committed memory: {units.Bytes(m.CommittedBytes)} of {units.Bytes(m.CommitLimitBytes)} limit ({m.CommitPercent:F0}%).", "Windows memory status"),
            new(EvidenceKind.Observed, $"Cached (reclaimable) memory: {units.Bytes(m.CachedBytes)}.", "Windows memory status"),
        };
        if (!Private)
        {
            var apps = providers.Processes.Latest.Apps.OrderByDescending(a => a.PrivateBytes).Take(5).ToList();
            if (apps.Count > 0)
                findings.Add(new(EvidenceKind.Observed, "Largest memory users: " + string.Join(", ", apps.Select(a => $"{a.DisplayName} {units.Bytes(a.PrivateBytes)} ({a.MemoryShareOfApps * 100:F0}% of app memory)")) + ".", "Process telemetry"));
        }
        foreach (var a in anomalies.Active.Where(a => a.MetricKey.EndsWith(".mem", StringComparison.Ordinal)))
            findings.Add(new(EvidenceKind.Inferred, a.Description, "Memory growth heuristic"));
        findings.Add(m.UsedPercent > 85
            ? new(EvidenceKind.Inferred, "Memory usage is high; if hard faults are also high, apps may feel slow.")
            : new(EvidenceKind.Inferred, "Memory usage is normal. Windows deliberately uses spare RAM as cache, which is released on demand."));
        return new DiagnosticResult(q.Text, q.Intent, q.Range, "Memory", $"Memory is {m.UsedPercent:F0}% in use.", Confidence.High, findings, [], []);
    }

    private DiagnosticResult TopApps(ParsedQuery q)
    {
        if (Private) return new DiagnosticResult(q.Text, q.Intent, q.Range, "Privacy mode", "Application history is not collected while Privacy Mode is on.", Confidence.High, [], [], []);
        var apps = store.TopApps(q.Range.From, q.Range.To, 8);
        if (apps.Count == 0) return DiagnosticResult.NotEnoughEvidence(q.Text, q.Intent, q.Range, "which apps used the most resources");
        var findings = apps.Select(a => new Finding(EvidenceKind.Observed,
            $"{AppNames.Display(a.App, providers)}: average {a.AvgCpu:F1}% CPU (peak {a.PeakCpu:F0}%), average {units.Bytes(a.AvgMemoryBytes)} memory, seen in {a.Minutes} recorded minutes.", "Sentinel app history")).ToList();
        findings.Add(new(EvidenceKind.Unknown, "Per-app network and power use are not measured."));
        return new DiagnosticResult(q.Text, q.Intent, q.Range, "Top apps", $"{AppNames.Display(apps[0].App, providers)} used the most CPU {q.Range.Label}.", Confidence.High, findings, [], []);
    }

    private DiagnosticResult DriverChange(ParsedQuery q)
    {
        var changes = store.QueryEvents(q.Range.From, q.Range.To, [EventCategory.DriverChanged], 200)
            .Concat(store.QueryChanges(q.Range.From, q.Range.To).Where(c => c.Kind == "Driver")
                .Select(c => new SystemEvent(c.Timestamp, EventCategory.DriverChanged, Severity.Info, c.Title + (c.After is null ? "" : $" ({c.Before} → {c.After})"), null, c.Source)))
            .Where(e => q.Subject is null || e.Title.Contains(q.Subject, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Timestamp).ToList();
        if (changes.Count == 0)
            return new DiagnosticResult(q.Text, q.Intent, q.Range, "No driver changes", $"No {(q.Subject is null ? "" : q.Subject.ToUpperInvariant() + " ")}driver changes were recorded {q.Range.Label}.", Confidence.Moderate,
                [new(EvidenceKind.Observed, "Neither the Kernel-PnP log nor Sentinel's driver snapshots show a change in this period.", "Windows event logs, driver inventory")], [], []);
        var change = changes[0];
        var findings = new List<Finding> { new(EvidenceKind.Observed, $"{change.Title} on {UnitFormatter.Absolute(change.Timestamp)}.", change.Source) };
        var afterRange = new TimeRange(change.Timestamp, DateTimeOffset.Now, "since the change");
        var beforeRange = new TimeRange(change.Timestamp.AddDays(-30), change.Timestamp, "30 days before");
        var cats = new[] { EventCategory.DisplayDriverReset, EventCategory.Bugcheck, EventCategory.AppCrash, EventCategory.UnexpectedShutdown };
        foreach (var c in cats)
        {
            var after = store.QueryEvents(afterRange.From, afterRange.To, [c], 500).Count;
            var before = store.QueryEvents(beforeRange.From, beforeRange.To, [c], 500).Count;
            if (after == 0 && before == 0) continue;
            findings.Add(new(EvidenceKind.Observed, $"{SystemEvent.CategoryLabel(c)}: {after} since the change vs {before} in the 30 days before.", "Windows event logs"));
            if (after >= 3 && before == 0)
                findings.Add(new(EvidenceKind.Inferred, $"{SystemEvent.CategoryLabel(c)} events began after this change (strong correlation; timing alone does not prove the driver is the cause).", "Correlation"));
        }
        var deltas = timeline.Compare(new TimeRange(change.Timestamp.AddDays(-7), change.Timestamp, "before"), new TimeRange(change.Timestamp, change.Timestamp.AddDays(7), "after"));
        foreach (var d in deltas.Where(d => d.Before is not null && d.After is not null).Take(4))
            findings.Add(new(EvidenceKind.Observed, $"{d.Name}: {d.BeforeText} before → {d.AfterText} after ({d.DeltaText}).", "Sentinel history"));
        findings.Add(new(EvidenceKind.Possible, "Workloads differ week to week, so averages before and after are only indicative."));
        return new DiagnosticResult(q.Text, q.Intent, q.Range, "After the driver change", $"Most recent change: {change.Title} ({change.Timestamp:d}).", Confidence.Moderate, findings, [],
            [new RecommendedAction("Driver updates and rollbacks are done in Device Manager or your GPU vendor's app; Sentinel never changes drivers.", "ms-settings:windowsupdate-history", "Open Update history")]);
    }

    // ------------------------------------------------------------------ network, gaming, sleep

    private DiagnosticResult Network(ParsedQuery q)
    {
        var findings = new List<Finding>();
        var drops = store.QueryEvents(q.Range.From, q.Range.To, [EventCategory.NetworkDisconnected], 500);
        findings.Add(new(EvidenceKind.Observed, $"{drops.Count} disconnect(s) recorded {q.Range.Label}.", "WLAN AutoConfig log / adapter state"));
        foreach (var g in drops.Where(d => d.Detail is not null).GroupBy(d => d.Detail).OrderByDescending(g => g.Count()).Take(2))
            findings.Add(new(EvidenceKind.Observed, $"{g.Count()} × {g.Key}", "WLAN AutoConfig log"));
        if (Summarize(MetricKeys.WifiSignal, q.Range) is { } s)
        {
            findings.Add(new(EvidenceKind.Observed, $"Wi-Fi signal quality averaged {s.Avg:F0}%.", "Native Wi-Fi API"));
            var b = baselines.Get(MetricKeys.WifiSignal, BaselineContext.All);
            if (b is { IsMature: true } && s.Avg < b.P05) findings.Add(new(EvidenceKind.Inferred, "Signal was weaker than this PC's normal range, which commonly causes disconnects.", "Sentinel baseline"));
        }
        foreach (var a in providers.Network.Latest.Adapters.Where(a => a.IsUp && a.Kind != "Virtual"))
            findings.Add(new(EvidenceKind.Observed, $"{a.Name}: {units.LinkSpeed(a.LinkSpeedBps)} link" + (a.Wifi is { } w ? $", {w.PhyType}, {w.Band}" : "") + $"; {a.InErrors + a.OutErrors} errors, {a.InDiscards + a.OutDiscards} discards since boot.", "IP Helper API"));
        findings.Add(new(EvidenceKind.Unknown, "Sentinel does not ping the internet in the background. Run a network test on the Network diagnostics page for latency."));
        return new DiagnosticResult(q.Text, q.Intent, q.Range, "Network", drops.Count == 0 ? "No disconnects were recorded." : $"{drops.Count} disconnects were recorded {q.Range.Label}.",
            Confidence.Moderate, findings, [], []);
    }

    private DiagnosticResult Gaming(ParsedQuery q)
    {
        var sessions = store.QueryWorkloadSessions(q.Range.From, q.Range.To, 100);
        var prev = store.QueryWorkloadSessions(q.Range.Previous("before").From, q.Range.From, 100);
        if (sessions.Count == 0) return DiagnosticResult.NotEnoughEvidence(q.Text, q.Intent, q.Range, "gaming performance — no high-performance sessions were recorded");
        var findings = new List<Finding>
        {
            new(EvidenceKind.Observed, $"{sessions.Count} high-performance session(s) {q.Range.Label} totalling {UnitFormatter.Duration(TimeSpan.FromTicks(sessions.Sum(s => s.Duration.Ticks)))}.", "Sentinel sessions"),
        };
        double? AvgOf(IEnumerable<WorkloadSession> s, Func<WorkloadSession, double?> f) => s.Select(f).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Average() is var a && !double.IsNaN(a) ? a : null;
        var tNow = AvgOf(sessions, s => s.GpuTempAvg);
        var tPrev = AvgOf(prev, s => s.GpuTempAvg);
        if (tNow is { } tn) findings.Add(new(EvidenceKind.Observed, $"Average GPU temperature in sessions: {units.Temperature(tn)}" + (tPrev is { } tp ? $" vs {units.Temperature(tp)} in the previous period ({units.TemperatureDelta(tn - tp)})." : "."), "Sentinel sessions"));
        var uNow = AvgOf(sessions, s => s.GpuAvg);
        var uPrev = AvgOf(prev, s => s.GpuAvg);
        if (uNow is { } un) findings.Add(new(EvidenceKind.Observed, $"Average GPU utilization: {un:F0}%" + (uPrev is { } up ? $" vs {up:F0}% previously." : "."), "Sentinel sessions"));
        var crashes = sessions.Sum(s => s.CrashesDuring);
        findings.Add(new(EvidenceKind.Observed, crashes == 0 ? "No crashes or driver resets during sessions." : $"{crashes} stability event(s) during sessions.", "Windows event logs"));
        findings.Add(new(EvidenceKind.Unknown, "Frame rate is not measured: there is no safe, non-invasive source for it (Sentinel never hooks or injects into games)."));
        return new DiagnosticResult(q.Text, q.Intent, q.Range, "Gaming sessions", "Sentinel compared high-performance sessions by temperature, load and stability.", Confidence.Moderate, findings, [], []);
    }

    private DiagnosticResult Sleep(ParsedQuery q)
    {
        var sleeps = store.QueryPowerSessions(q.Range.From, q.Range.To, PowerSessionKind.Sleep);
        var wakes = store.QueryEvents(q.Range.From, q.Range.To, [EventCategory.Wake, EventCategory.Sleep], 500).OrderBy(e => e.Timestamp).ToList();
        var findings = new List<Finding>();
        foreach (var s in sleeps.Take(5))
            findings.Add(new(EvidenceKind.Observed, s.PercentDelta is { } d
                ? $"Slept {UnitFormatter.Absolute(s.Start)} for {UnitFormatter.Duration(s.Duration)}: lost {-d:F0}% ({-d / Math.Max(s.Duration.TotalHours, 0.01):F1}%/h)."
                : $"Slept {UnitFormatter.Absolute(s.Start)} for {UnitFormatter.Duration(s.Duration)}.", "Sentinel sleep tracking"));
        foreach (var w in wakes.Where(w => w.Category == EventCategory.Wake && w.Detail is not null).Take(5))
            findings.Add(new(EvidenceKind.Observed, $"{UnitFormatter.Absolute(w.Timestamp)}: {w.Title}. {w.Detail}", "Power-Troubleshooter log"));
        if (findings.Count == 0) return DiagnosticResult.NotEnoughEvidence(q.Text, q.Intent, q.Range, "sleep behavior");
        findings.Add(new(EvidenceKind.Unknown, "Which apps prevent sleep is only visible to administrators (powercfg /requests) and is not collected."));
        return new DiagnosticResult(q.Text, q.Intent, q.Range, "Sleep", $"{sleeps.Count} sleep session(s) and {wakes.Count(w => w.Category == EventCategory.Wake)} wake event(s) {q.Range.Label}.", Confidence.Moderate, findings, [], []);
    }

    private DiagnosticResult WhatChanged(ParsedQuery q)
    {
        var groups = timeline.WhatChanged(q.Range);
        if (groups.Count == 0) return new DiagnosticResult(q.Text, q.Intent, q.Range, "No changes", $"No system changes were recorded {q.Range.Label}.", Confidence.Moderate, [], [], []);
        var findings = groups.SelectMany(g => g.Items.Take(5).Select(i => new Finding(EvidenceKind.Observed,
            $"{UnitFormatter.Absolute(i.Timestamp)} — {i.Title}" + (i.Before is not null || i.After is not null ? $" ({i.Before ?? "—"} → {i.After ?? "—"})" : ""), i.Source))).ToList();
        return new DiagnosticResult(q.Text, q.Intent, q.Range, "What changed", string.Join(", ", groups.Select(g => $"{g.Items.Count} {g.Kind.ToLowerInvariant()} change(s)")) + ".",
            Confidence.High, findings, [], []);
    }

    private DiagnosticResult Overview(ParsedQuery q)
    {
        var h = intelligence.Health;
        if (h is null) return DiagnosticResult.NotEnoughEvidence(q.Text, q.Intent, q.Range, "overall health yet");
        var findings = h.Categories.Select(c => new Finding(c.Status == HealthStatus.Unknown ? EvidenceKind.Unknown : EvidenceKind.Inferred,
            $"{c.Name}: {c.Status.Label()} — {c.Summary}", null, c.Status >= HealthStatus.Attention ? Severity.Notice : Severity.Info)).ToList();
        findings.AddRange(anomalies.Active.Select(a => new Finding(EvidenceKind.Inferred, a.Description, "Anomaly engine", a.Severity)));
        return new DiagnosticResult(q.Text, q.Intent, q.Range, "Overall health", h.Headline + " " + h.Detail, Confidence.Moderate, findings, [], []);
    }

    private DiagnosticResult Unknown(ParsedQuery q) =>
        new(q.Text, q.Intent, q.Range, "Not sure what to look at", "I couldn't match this question to the telemetry Sentinel collects.", Confidence.Low,
            [new(EvidenceKind.Unknown, "Try asking about temperature, battery, crashes, restarts, storage health, memory, network, drivers, sleep or what changed.")], [], []);
}
