using Sentinel.Core.Metrics;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;

namespace Sentinel.Intelligence;

public sealed record ContextReading(string Label, string Value, DateTimeOffset Timestamp);

/// <summary>A reconstructed chain of evidence around one event, newest first.</summary>
public sealed record EventContext(SystemEvent Subject, IReadOnlyList<Correlation> Related, IReadOnlyList<ContextReading> Telemetry, string Summary);

/// <summary>
/// Temporal correlation between events, hardware telemetry and changes. Strength labels are conservative:
/// timing alone is never more than "Possible"; "Strong" requires a repeated pattern against a baseline;
/// "Confirmed" requires a direct record linking the two (for example a restart record carrying the stop code).
/// </summary>
public sealed class CorrelationEngine(HistoryStore store, UnitFormatter units)
{
    private static readonly TimeSpan NearWindow = TimeSpan.FromMinutes(30);

    public EventContext Explain(SystemEvent subject)
    {
        var related = new List<Correlation>();
        var nearby = store.QueryEvents(subject.Timestamp - NearWindow, subject.Timestamp + TimeSpan.FromMinutes(10), null, 200)
            .Where(e => e.Id != subject.Id && e.DedupeKey != subject.DedupeKey).ToList();

        foreach (var e in nearby)
        {
            var strength = Classify(subject, e, out var why);
            if (strength is { } s) related.Add(new Correlation(subject, e, s, why));
        }

        // Driver changes in the preceding 14 days are relevant to display/driver failures and stop errors.
        if (subject.Category is EventCategory.DisplayDriverReset or EventCategory.Bugcheck or EventCategory.DriverFailure or EventCategory.UnexpectedShutdown or EventCategory.DeviceProblem)
        {
            var changes = store.QueryEvents(subject.Timestamp.AddDays(-14), subject.Timestamp, [EventCategory.DriverChanged, EventCategory.UpdateInstalled], 100);
            foreach (var c in changes.Take(10))
            {
                var (strength, why) = ChangeImpact(subject, c);
                related.Add(new Correlation(subject, c, strength, why));
            }
        }

        var telemetry = TelemetryAround(subject.Timestamp);
        var strongest = related.OrderByDescending(r => r.Strength).FirstOrDefault();
        var summary = strongest is null
            ? "No related events were found near this time. There is insufficient evidence to determine a cause."
            : strongest.Strength switch
            {
                CorrelationStrength.Confirmed => $"Confirmed link: {strongest.Rationale}",
                CorrelationStrength.Strong => $"Strong correlation: {strongest.Rationale}",
                _ => "Only timing links the nearby events to this one; that alone does not establish a cause.",
            };
        return new EventContext(subject, related.OrderByDescending(r => r.Strength).ThenByDescending(r => r.Related.Timestamp).ToList(), telemetry, summary);
    }

    private static CorrelationStrength? Classify(SystemEvent subject, SystemEvent other, out string why)
    {
        why = "";
        var gap = subject.Timestamp - other.Timestamp;
        // A Kernel-Power restart record that carries the same stop code directly confirms the link.
        if (subject.Category == EventCategory.UnexpectedShutdown && other.Category == EventCategory.Bugcheck && Math.Abs(gap.TotalMinutes) <= 15)
        {
            why = $"Windows recorded a stop error ({other.Code}) around the same restart, so the unexpected shutdown was caused by that stop error.";
            return subject.Detail?.Contains("stop error", StringComparison.OrdinalIgnoreCase) == true ? CorrelationStrength.Confirmed : CorrelationStrength.Strong;
        }
        if (subject.Category == EventCategory.Bugcheck && other.Category == EventCategory.UnexpectedShutdown && Math.Abs(gap.TotalMinutes) <= 15)
        {
            why = "The system restarted because of this stop error.";
            return CorrelationStrength.Confirmed;
        }
        if (subject.Category == EventCategory.AppCrash && other.Category == EventCategory.AppHang && other.Subject == subject.Subject && gap >= TimeSpan.Zero && gap.TotalMinutes <= 5)
        {
            why = $"{subject.Subject} stopped responding shortly before it closed.";
            return CorrelationStrength.Strong;
        }
        if (other.Category is EventCategory.Anomaly or EventCategory.WorkloadSession or EventCategory.DisplayDriverReset or EventCategory.UpdateInstalled
            or EventCategory.DriverChanged or EventCategory.Wake or EventCategory.Sleep or EventCategory.DeviceDisconnected or EventCategory.DeviceConnected
            or EventCategory.PowerSource or EventCategory.AppCrash or EventCategory.ServiceFailure)
        {
            why = $"{other.Title} happened {Describe(gap)}.";
            return CorrelationStrength.Possible;
        }
        return null;
    }

    /// <summary>Compares the subject's category rate after a change with the 30 days before it.</summary>
    private (CorrelationStrength, string) ChangeImpact(SystemEvent subject, SystemEvent change)
    {
        var after = store.QueryEvents(change.Timestamp, subject.Timestamp.AddMinutes(1), [subject.Category], 500).Count;
        var before = store.QueryEvents(change.Timestamp.AddDays(-30), change.Timestamp, [subject.Category], 500).Count;
        var daysAfter = Math.Max((subject.Timestamp - change.Timestamp).TotalDays, 0.5);
        var rateAfter = after / daysAfter;
        var rateBefore = before / 30.0;
        if (after >= 3 && before == 0)
            return (CorrelationStrength.Strong, $"{after} × {SystemEvent.CategoryLabel(subject.Category).ToLowerInvariant()} occurred after \"{change.Title}\" on {change.Timestamp:MMM d}; there were none in the preceding 30 days.");
        if (after >= 3 && rateAfter > rateBefore * 3)
            return (CorrelationStrength.Possible, $"The rate of {SystemEvent.CategoryLabel(subject.Category).ToLowerInvariant()} rose from {rateBefore:F2}/day to {rateAfter:F2}/day after \"{change.Title}\".");
        return (CorrelationStrength.InsufficientEvidence, $"\"{change.Title}\" happened {Describe(subject.Timestamp - change.Timestamp)}, but there is no pattern linking it to this event.");
    }

    private List<ContextReading> TelemetryAround(DateTimeOffset at)
    {
        var list = new List<ContextReading>();
        void Add(string key, string label, Func<double, string> fmt)
        {
            if (store.ValueAt(key, at, TimeSpan.FromMinutes(3)) is { } p) list.Add(new ContextReading(label, fmt(p.Avg), p.Timestamp));
        }
        Add(MetricKeys.CpuUtil, "CPU utilization", v => $"{v:F0}%");
        Add(MetricKeys.GpuUtilAny, "GPU utilization", v => $"{v:F0}%");
        Add(MetricKeys.GpuTempAny, "GPU temperature", v => units.Temperature(v));
        Add(MetricKeys.MemUsedPct, "Memory in use", v => $"{v:F0}%");
        Add(MetricKeys.DiskActive, "Disk active time", v => $"{v:F0}%");
        Add(MetricKeys.BatRate, "Battery rate", v => $"{v:F1} W");
        return list;
    }

    private static string Describe(TimeSpan gap)
    {
        var abs = gap.Duration();
        var text = abs.TotalMinutes < 1 ? "at the same time" : abs.TotalHours < 1 ? $"{abs.TotalMinutes:F0} min" : abs.TotalDays < 1 ? $"{abs.TotalHours:F1} h" : $"{abs.TotalDays:F0} days";
        return abs.TotalMinutes < 1 ? text : gap > TimeSpan.Zero ? text + " earlier" : text + " later";
    }
}
