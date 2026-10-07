using System.Globalization;
using Sentinel.Analytics;
using Sentinel.Core.Metrics;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Telemetry;

namespace Sentinel.Intelligence;

/// <summary>
/// Turns baselines, trends and events into calm, specific, evidence-backed statements.
/// An insight is only produced when the data supports it; otherwise nothing is said.
/// </summary>
public sealed class InsightEngine(HistoryStore store, LiveMetricStore live, BaselineEngine baselines, AnomalyEngine anomalies, ProviderSet providers, UnitFormatter units)
{
    public IReadOnlyList<Insight> Generate(DateTimeOffset now)
    {
        var list = new List<Insight>();
        foreach (var a in anomalies.Active.OrderByDescending(a => a.Severity))
            list.Add(new Insight("anomaly:" + a.Id, a.Title, a.Description, a.Severity, a.Confidence, a.Evidence, null, "Anomaly", a.Start));

        TryAdd(list, () => RecentStopError(now));
        TryAdd(list, () => IdleTemperature(now));
        TryAdd(list, () => BatteryTrend());
        TryAdd(list, () => DriveTrend());
        TryAdd(list, () => BootTrend(now));
        TryAdd(list, () => LastWorkload(now));
        TryAdd(list, () => WifiDrops(now));
        TryAdd(list, () => LowDisk());
        return list.OrderByDescending(i => i.Severity).ThenByDescending(i => i.Confidence).ToList();
    }

    /// <summary>One sentence for the Home screen.</summary>
    public Insight Headline(DateTimeOffset now, IReadOnlyList<Insight> insights)
    {
        var top = insights.FirstOrDefault(i => i.Severity >= Severity.Notice);
        if (top is not null) return top;
        var info = insights.FirstOrDefault();
        var text = "Everything appears normal." + (info is not null ? " " + info.Summary : "");
        return new Insight("headline", "Everything appears normal", text, Severity.Info, info?.Confidence ?? Confidence.Moderate, info?.Evidence ?? []);
    }

    private static void TryAdd(List<Insight> list, Func<Insight?> f)
    {
        try
        {
            if (f() is { } i) list.Add(i);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            // An insight that cannot be computed is simply omitted.
        }
    }

    private Insight? RecentStopError(DateTimeOffset now)
    {
        var bugchecks = store.QueryEvents(now.AddDays(-7), now, [EventCategory.Bugcheck], 50);
        if (bugchecks.Count == 0) return null;
        var last = bugchecks[0];
        var entry = last.Code is null ? null : Core.Knowledge.BugcheckCatalog.Lookup(last.Code);
        var when = last.Timestamp.Date == now.Date ? $"today at {last.Timestamp:t}" : $"on {last.Timestamp:ddd d MMM} at {last.Timestamp:t}";
        var text = $"Windows stopped unexpectedly {when}" + (entry is null ? $" (stop code {last.Code})." : $" with {entry.Name} ({last.Code}).");
        if (entry is not null) text += $" This stop code is in the \"{entry.Category}\" category: {entry.Explanation}";
        if (bugchecks.Count > 1) text += $" {bugchecks.Count} stop errors were recorded in the last 7 days.";
        return new Insight("bugcheck:" + last.DedupeKey, "Stop error", text, bugchecks.Count > 1 ? Severity.Critical : Severity.Warning, Confidence.High,
            [
                new(EvidenceKind.Observed, $"Windows Error Reporting recorded stop code {last.Code} at {last.Timestamp:g}.", "System event log"),
                new(EvidenceKind.Unknown, "The specific driver involved is recorded in the memory dump, which Sentinel does not upload or analyse."),
            ], new RecommendedAction("Keep Windows and device drivers up to date through Windows Update or your PC manufacturer's support app.", "ms-settings:windowsupdate", "Open Windows Update"),
            "Reliability", last.Timestamp);
    }

    private Insight? IdleTemperature(DateTimeOffset now)
    {
        var cpu = live.Get(MetricKeys.CpuUtil)?.Stats(now.AddMinutes(-10));
        if (cpu is not { N: > 120 } c || c.Avg >= BaselineContexts.LowLoadCpu) return null;
        foreach (var key in live.Keys.Where(k => (k.StartsWith("thermal.", StringComparison.Ordinal) && !k.EndsWith(".passive", StringComparison.Ordinal))
                                                 || (k.StartsWith("gpu.", StringComparison.Ordinal) && k.EndsWith(".temp", StringComparison.Ordinal))))
        {
            var b = baselines.Get(key, BaselineContext.LowLoad);
            var now10 = live.Get(key)?.Stats(now.AddMinutes(-10));
            if (b is null || !b.IsMature || now10 is not { N: > 30 } t) continue;
            var diff = t.Avg - b.Median;
            var name = live.GetDefinition(key)?.Name ?? key;
            var summary = Math.Abs(diff) < 2
                ? $"{name} at idle is in line with this system's {b.DaysCovered}-day baseline ({units.Temperature(b.Median)})."
                : $"{name} at idle is {units.TemperatureDelta(Math.Abs(diff)).TrimStart('+')} {(diff < 0 ? "lower" : "higher")} than this system's {b.DaysCovered}-day average.";
            return new Insight("idle-temp:" + key, "Idle temperature", summary, Severity.Info, b.DaysCovered >= 14 ? Confidence.High : Confidence.Moderate,
                [
                    new(EvidenceKind.Observed, $"Last 10 minutes at low load: {units.Temperature(t.Avg)} average, CPU {c.Avg:F0}%.", "Live telemetry"),
                    new(EvidenceKind.Observed, $"Baseline at low load: median {units.Temperature(b.Median)}, typical {units.Temperature(b.P05)}–{units.Temperature(b.P95)}.", "Sentinel baseline"),
                ], null, "Thermals", now);
        }
        return null;
    }

    private Insight? BatteryTrend()
    {
        if (!providers.HasBattery) return null;
        var history = store.QueryCapacity();
        if (history.Count < 2) return null;
        var first = history[0];
        var last = history[^1];
        var days = (last.Date - first.Date).TotalDays;
        if (days < 21) return null;
        var change = (last.FullChargeMWh - first.FullChargeMWh) / first.FullChargeMWh * 100;
        var text = Math.Abs(change) < 1
            ? $"Battery full-charge capacity has been stable at about {last.FullChargeMWh / 1000:F1} Wh over {days:F0} days."
            : $"Battery full-charge capacity {(change < 0 ? "declined" : "rose")} from {first.FullChargeMWh / 1000:F1} Wh to {last.FullChargeMWh / 1000:F1} Wh over {days:F0} days, a {Math.Abs(change):F1}% {(change < 0 ? "reduction" : "increase")}.";
        var sources = string.Join(", ", history.Select(h => h.Source).Distinct());
        var x = history.Select(h => (h.Date - first.Date).TotalDays).ToList();
        var fit = Statistics.LinearRegression(x, history.Select(h => h.FullChargeMWh).ToList());
        var evidence = new List<EvidenceItem> { new(EvidenceKind.Observed, $"{history.Count} capacity readings from {first.Date:d} to {last.Date:d}.", sources) };
        if (fit.N >= 5 && fit.Slope < 0)
            evidence.Add(new(EvidenceKind.Inferred, $"Trend: about {-fit.Slope * 30 / 1000:F2} Wh lost per month (R² {fit.RSquared:F2}).", "Linear trend"));
        evidence.Add(new(EvidenceKind.Possible, "Capacities are firmware estimates; recalibration after a full charge cycle can shift them by a few percent.", null));
        return new Insight("battery-trend", "Battery capacity", text, change < -15 ? Severity.Notice : Severity.Info, Confidence.Moderate, evidence, null, "Battery");
    }

    private Insight? DriveTrend()
    {
        foreach (var d in providers.Storage.Disks)
        {
            var hist = store.QueryDiskHealth(d.Id).Where(h => h.PercentUsed is not null).ToList();
            if (hist.Count < 2) continue;
            var first = hist[0];
            var last = hist[^1];
            var days = (last.Day - first.Day).TotalDays;
            if (days < 14) continue;
            var text = last.PercentUsed == first.PercentUsed
                ? $"{d.Model} health remains stable. Percentage Used has stayed at {last.PercentUsed:F0}% over the last {days:F0} days."
                : $"{d.Model} health remains stable. Percentage Used increased from {first.PercentUsed:F0}% to {last.PercentUsed:F0}% over the last {days:F0} days.";
            var critical = last.Health is "Unhealthy" or "Warning";
            if (critical) text = $"{d.Model} reports '{last.Health}'. Percentage Used is {last.PercentUsed:F0}%.";
            return new Insight("drive-trend:" + d.Id, "Drive health", text, critical ? Severity.Warning : Severity.Info, Confidence.High,
                [new(EvidenceKind.Observed, $"Daily NVMe health readings from {first.Day:d} to {last.Day:d}.", "NVMe health log")], null, "Storage");
        }
        return null;
    }

    private Insight? BootTrend(DateTimeOffset now)
    {
        var boots = store.QueryEvents(now.AddDays(-60), now, [EventCategory.Boot], 500)
            .Where(e => e.Code?.StartsWith("boottime:", StringComparison.Ordinal) == true)
            .Select(e => (e.Timestamp, Seconds: double.Parse(e.Code!["boottime:".Length..], CultureInfo.InvariantCulture) / 1000))
            .OrderBy(b => b.Timestamp).ToList();
        if (boots.Count < 6) return null;
        var cp = Statistics.DetectChangePoint(boots.Select(b => b.Seconds).ToList(), minSegment: 3, minScore: 3);
        if (cp is not { } c || c.MeanAfter - c.MeanBefore < 8) return null;
        var since = boots[c.Index].Timestamp;
        var startupAdds = store.QueryChanges(since.AddDays(-3), now).Count(ch => ch.Kind == "Startup" && ch.Before is null);
        var text = $"Boot time has increased by approximately {c.MeanAfter - c.MeanBefore:F0} seconds since {since:MMM d}.";
        if (startupAdds > 0) text += $" {startupAdds} new startup item{(startupAdds == 1 ? "" : "s")} appeared during the same period.";
        return new Insight("boot-trend", "Boot time", text, Severity.Notice, boots.Count >= 10 ? Confidence.Moderate : Confidence.Low,
            [
                new(EvidenceKind.Observed, $"Average boot {c.MeanBefore:F0} s before, {c.MeanAfter:F0} s after ({boots.Count} measured boots).", "Windows boot diagnostics"),
                startupAdds > 0
                    ? new(EvidenceKind.Possible, "New startup apps are a common cause of slower boots.", null)
                    : new(EvidenceKind.Unknown, "No new startup items were recorded; the cause is not evident from the available data.", null),
            ], new RecommendedAction("Review apps that start with Windows.", "ms-settings:startupapps", "Open Startup Apps settings"), "Power");
    }

    private Insight? LastWorkload(DateTimeOffset now)
    {
        var sessions = store.QueryWorkloadSessions(now.AddDays(-30), now, 50);
        if (sessions.Count == 0) return null;
        var last = sessions[0];
        if (now - last.End > TimeSpan.FromDays(2) || last.GpuTempPeak is not { } peak || last.GpuAvg is not { } gpuAvg) return null;
        var similar = sessions.Skip(1).Where(s => s.GpuAvg is { } g && Math.Abs(g - gpuAvg) < 15 && s.GpuTempPeak is not null).ToList();
        var text = $"Your GPU peaked at {units.Temperature(peak)} during a sustained {gpuAvg:F0}% load" + (last.AppName is { } app ? $" ({app})." : ".");
        var evidence = new List<EvidenceItem> { new(EvidenceKind.Observed, $"Session {last.Start:g}, {UnitFormatter.Duration(last.Duration)}.", "Sentinel sessions") };
        if (similar.Count >= 2)
        {
            var typical = Statistics.Median(similar.Select(s => s.GpuTempPeak!.Value).ToList());
            var delta = peak - typical;
            text += Math.Abs(delta) < 2
                ? $" This is in line with {similar.Count} similar sessions over the previous 30 days."
                : $" This is {units.TemperatureDelta(Math.Abs(delta)).TrimStart('+')} {(delta > 0 ? "warmer" : "cooler")} than {similar.Count} similar sessions over the previous 30 days.";
            evidence.Add(new(EvidenceKind.Observed, $"Median peak in similar sessions: {units.Temperature(typical)}.", "Sentinel sessions"));
        }
        text += last.CrashesDuring == 0 ? " No stability events were recorded." : $" {last.CrashesDuring} stability event(s) occurred during the session.";
        return new Insight("workload:" + last.Id, "Last high-performance session", text, last.CrashesDuring > 0 ? Severity.Notice : Severity.Info,
            similar.Count >= 2 ? Confidence.High : Confidence.Moderate, evidence, null, "GPU", last.Start);
    }

    private Insight? WifiDrops(DateTimeOffset now)
    {
        var dayEvents = store.QueryEvents(TimeRange.StartOfDay(now), now, [EventCategory.NetworkDisconnected, EventCategory.Sleep], 400);
        var drops = NetworkEvents.GenuineDisconnects(dayEvents, dayEvents).Where(e => e.Subject == "Wi-Fi").ToList();
        if (drops.Count < 3) return null;
        var baseline = baselines.Get(MetricKeys.WifiSignal, BaselineContext.All);
        var weakBefore = 0;
        foreach (var d in drops)
        {
            var v = store.ValueAt(MetricKeys.WifiSignal, d.Timestamp.AddMinutes(-1), TimeSpan.FromMinutes(2));
            if (v is { } p && baseline is { } b && p.Avg < b.P05) weakBefore++;
        }
        var text = $"Wi-Fi disconnected {drops.Count} times today.";
        if (baseline is not null && weakBefore == drops.Count) text += $" All {drops.Count} were preceded by signal strength falling below this laptop's normal range.";
        else if (weakBefore > 0) text += $" {weakBefore} of them followed a drop in signal strength below the normal range.";
        return new Insight("wifi-drops", "Wi-Fi stability", text, drops.Count >= 6 ? Severity.Warning : Severity.Notice, weakBefore > 0 ? Confidence.Moderate : Confidence.Low,
            [new(EvidenceKind.Observed, $"{drops.Count} disconnect events today.", "WLAN AutoConfig event log")], null, "Network");
    }

    private Insight? LowDisk()
    {
        var v = providers.Storage.Volumes.FirstOrDefault(v => v.IsSystem && v.TotalBytes > 0);
        if (v is null) return null;
        var pct = v.FreeBytes * 100.0 / v.TotalBytes;
        if (pct >= 10) return null;
        return new Insight("low-disk", "Low free space", $"The system drive has {units.Bytes(v.FreeBytes)} free ({pct:F0}%). Windows needs free space for updates and the page file.",
            pct < 5 ? Severity.Warning : Severity.Notice, Confidence.High,
            [new(EvidenceKind.Observed, $"{v.Name} {units.Bytes(v.FreeBytes)} free of {units.Bytes(v.TotalBytes)}.", "Volume information")],
            new RecommendedAction("Review large files and storage suggestions.", "ms-settings:storagesense", "Open Storage settings"), "Storage");
    }
}
