using Sentinel.Core.Metrics;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;

namespace Sentinel.Intelligence;

public sealed record ChangeGroup(string Kind, string Glyph, IReadOnlyList<ChangeItem> Items);

public sealed record ChangeItem(DateTimeOffset Timestamp, string Title, string? Before, string? After, string Source);

public sealed record TimeMachineSnapshot(
    DateTimeOffset At,
    IReadOnlyList<(string Label, string Value, string Source)> Metrics,
    IReadOnlyList<SystemEvent> Events,
    IReadOnlyList<AppUsageRow> Apps,
    bool HasData);

public sealed record MetricDelta(string Name, string Unit, double? Before, double? After, string BeforeText, string AfterText, string DeltaText, bool HigherIsWorse);

/// <summary>"What changed?", "time machine" reconstruction and comparisons — all from stored history.</summary>
public sealed class TimelineService(HistoryStore store, LiveMetricStore live, UnitFormatter units)
{
    private static readonly EventCategory[] ChangeCategories =
    [
        EventCategory.UpdateInstalled, EventCategory.UpdateFailed, EventCategory.DriverChanged, EventCategory.SoftwareInstalled,
        EventCategory.SoftwareRemoved, EventCategory.StartupItemAdded,
    ];

    public IReadOnlyList<ChangeGroup> WhatChanged(TimeRange range)
    {
        var items = new List<(string Kind, ChangeItem Item)>();
        foreach (var c in store.QueryChanges(range.From, range.To))
            items.Add((c.Kind, new ChangeItem(c.Timestamp, c.Title, c.Before, c.After, c.Source)));
        foreach (var e in store.QueryEvents(range.From, range.To, ChangeCategories, 1000))
        {
            var kind = e.Category switch
            {
                EventCategory.UpdateInstalled or EventCategory.UpdateFailed => "Windows Update",
                EventCategory.DriverChanged => "Driver",
                EventCategory.StartupItemAdded => "Startup",
                _ => "App",
            };
            items.Add((kind, new ChangeItem(e.Timestamp, e.Title, null, e.Detail, e.Source)));
        }

        // The same change can be seen both in an inventory diff and an event log entry; keep one.
        var deduped = items
            .GroupBy(i => (i.Kind, Key: Normalize(i.Item.Title), Day: i.Item.Timestamp.Date))
            .Select(g => g.OrderBy(i => i.Item.Source.Contains("event log", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First())
            .ToList();

        string Glyph(string kind) => kind switch
        {
            "Windows Update" or "Windows" => "",
            "Driver" => "",
            "App" => "",
            "Startup" => "",
            "Service" => "",
            "Device" => "",
            "Firmware" => "",
            "Battery" => "",
            "Storage" => "",
            _ => "",
        };
        return deduped.GroupBy(i => i.Kind)
            .OrderBy(g => g.Key switch { "Windows Update" => 0, "Windows" => 1, "Firmware" => 2, "Driver" => 3, "App" => 4, "Startup" => 5, "Service" => 6, "Device" => 7, _ => 8 })
            .Select(g => new ChangeGroup(g.Key, Glyph(g.Key), g.Select(x => x.Item).OrderByDescending(x => x.Timestamp).ToList()))
            .ToList();
    }

    private static string Normalize(string title) => title.ToLowerInvariant().Replace(" installed", "", StringComparison.Ordinal).Trim();

    /// <summary>Reconstructs what the PC looked like at a past moment.</summary>
    public TimeMachineSnapshot Reconstruct(DateTimeOffset at)
    {
        var metrics = new List<(string, string, string)>();
        var tolerance = TimeSpan.FromMinutes(at > DateTimeOffset.Now.AddDays(-2) ? 2 : 35);
        void Add(string key, Func<double, string> fmt)
        {
            // Stored history first; for the last few minutes (not yet written to disk) fall back to the live buffer.
            if ((store.ValueAt(key, at, tolerance) ?? LiveAt(key, at)) is not { } p) return;
            var name = live.GetDefinition(key)?.Name ?? key;
            metrics.Add((name, fmt(p.Avg) + (Math.Abs(p.Max - p.Min) > 0.01 ? $"  (range {fmt(p.Min)}–{fmt(p.Max)})" : ""), $"Sentinel history, {p.Timestamp:t}"));
        }
        Add(MetricKeys.CpuUtil, v => $"{v:F0}%");
        Add(MetricKeys.CpuFreq, v => UnitFormatter.Frequency(v));
        Add(MetricKeys.GpuUtilAny, v => $"{v:F0}%");
        Add(MetricKeys.GpuTempAny, v => units.Temperature(v));
        foreach (var k in live.Definitions.Where(d => d.Key.StartsWith("thermal.", StringComparison.Ordinal) && d.Persist == PersistPolicy.Full).Take(3))
            Add(k.Key, v => units.Temperature(v));
        Add(MetricKeys.MemUsedPct, v => $"{v:F0}%");
        Add(MetricKeys.MemCommit, v => units.Bytes(v));
        Add(MetricKeys.DiskRead, v => units.DiskRate(v));
        Add(MetricKeys.DiskWrite, v => units.DiskRate(v));
        Add(MetricKeys.NetRx, v => units.Throughput(v));
        Add(MetricKeys.NetTx, v => units.Throughput(v));
        Add(MetricKeys.BatPercent, v => $"{v:F0}%");
        Add(MetricKeys.BatRate, v => $"{v:F1} W");
        Add(MetricKeys.WifiSignal, v => $"{v:F0}%");
        var events = store.QueryEvents(at.AddMinutes(-20), at.AddMinutes(20), null, 50).OrderBy(e => e.Timestamp).ToList();
        var apps = store.AppUsageAt(at);
        return new TimeMachineSnapshot(at, metrics, events, apps, metrics.Count > 0 || events.Count > 0);
    }

    private SeriesPoint? LiveAt(string key, DateTimeOffset at)
    {
        if (DateTimeOffset.Now - at > TimeSpan.FromMinutes(15)) return null;
        var near = live.Get(key)?.Since(at.AddSeconds(-30)).Where(p => p.Timestamp <= at.AddSeconds(30) && !double.IsNaN(p.Value)).ToList();
        if (near is not { Count: > 0 }) return null;
        return new SeriesPoint(at, near.Average(p => p.Value), near.Min(p => p.Value), near.Max(p => p.Value));
    }

    private static readonly (string Key, string Name, string Kind, bool HigherIsWorse)[] CompareMetrics =
    [
        (MetricKeys.CpuUtil, "Average CPU utilization", "pct", false),
        (MetricKeys.GpuUtilAny, "Average GPU utilization", "pct", false),
        (MetricKeys.GpuTempAny, "Average GPU temperature", "temp", true),
        (MetricKeys.MemUsedPct, "Average memory in use", "pct", true),
        (MetricKeys.BatRate, "Average battery rate", "watts", false),
        (MetricKeys.DiskWrite, "Average disk writes", "tput", false),
        (MetricKeys.NetRx, "Average network download", "tput", false),
        (MetricKeys.WifiSignal, "Average Wi-Fi signal", "pct", false),
    ];

    public IReadOnlyList<MetricDelta> Compare(TimeRange a, TimeRange b)
    {
        var list = new List<MetricDelta>();
        var thermal = live.Definitions.Where(d => d.Key.StartsWith("thermal.", StringComparison.Ordinal) && d.Persist == PersistPolicy.Full)
            .Select(d => (d.Key, "Average " + d.Name.ToLowerInvariant(), "temp", true));
        var gpuPower = live.Definitions.Where(d => d.Key.StartsWith("gpu.", StringComparison.Ordinal) && d.Key.EndsWith(".power", StringComparison.Ordinal))
            .Select(d => (d.Key, "Average " + d.Name, "watts", false));
        foreach (var (key, name, kind, worse) in CompareMetrics.Concat(thermal).Concat(gpuPower))
        {
            var sa = store.Stats(key, a.From, a.To);
            var sb = store.Stats(key, b.From, b.To);
            if (!sa.HasData && !sb.HasData) continue;
            double? va = sa.HasData ? sa.Avg : null, vb = sb.HasData ? sb.Avg : null;
            string F(double? v) => v is not { } x ? "No data" : kind switch
            {
                "temp" => units.Temperature(x),
                "watts" => $"{x:F1} W",
                "tput" => units.Rate(key, x),
                _ => $"{x:F0}%",
            };
            var delta = va is { } x && vb is { } y
                ? kind switch
                {
                    "temp" => units.TemperatureDelta(y - x),
                    "watts" => $"{y - x:+0.0;-0.0} W",
                    "tput" => (y >= x ? "+" : "−") + units.Rate(key, Math.Abs(y - x)),
                    _ => $"{y - x:+0;-0} pts",
                }
                : "—";
            list.Add(new MetricDelta(name, kind, va, vb, F(va), F(vb), delta, worse));
        }
        return list;
    }
}
