using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Analytics;
using Sentinel.App.Controls;
using Sentinel.Core.Knowledge;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Intelligence;

namespace Sentinel.App.ViewModels;

// ---------------------------------------------------------------- Health

public sealed record HealthCategoryCard(string Name, string Summary, HealthStatus Status, string Confidence, IReadOnlyList<EvidenceRow> Factors);

public sealed partial class HealthViewModel : PageViewModel
{
    private readonly IntelligenceService _intelligence = App.Services.GetRequiredService<IntelligenceService>();

    protected override int RefreshEveryTicks => 5;

    public ObservableCollection<HealthCategoryCard> Categories { get; } = [];

    [ObservableProperty] public partial string Headline { get; set; } = "Evaluating…";
    [ObservableProperty] public partial string Detail { get; set; } = "";
    [ObservableProperty] public partial HealthStatus Overall { get; set; }
    [ObservableProperty] public partial string Updated { get; set; } = "";

    protected override void Refresh()
    {
        if (_intelligence.Health is not { } h) return;
        Headline = h.Headline;
        Detail = h.Detail;
        Overall = h.Overall;
        Updated = $"Evaluated {h.Timestamp:T}. No single score: each area shows its status, confidence and the exact evidence behind it.";
        var cards = h.Categories.Select(c => new HealthCategoryCard(c.Name, c.Summary, c.Status, c.Confidence.Label(),
            c.Factors.Select(f => new EvidenceRow(f.Kind, f.Text, null)).ToList())).ToList();
        if (Categories.Count == cards.Count && Categories.Zip(cards).All(p => p.First.Summary == p.Second.Summary && p.First.Status == p.Second.Status)) return;
        Categories.Clear();
        foreach (var c in cards) Categories.Add(c);
    }
}

// ---------------------------------------------------------------- Timeline

public sealed record TimelineDay(string Header, IReadOnlyList<EventRow> Events);

public sealed record CorrelationRow(string Strength, string Title, string Time, string Rationale, CorrelationStrength Level);

public sealed record ChangeGroupRow(string Kind, string Glyph, IReadOnlyList<ChangeItemRow> Items);

public sealed record ChangeItemRow(string Time, string Title, string Delta, string Source);

public sealed partial class TimelineViewModel : PageViewModel
{
    private static readonly (string Name, EventCategory[] Categories)[] Filters =
    [
        ("Reliability", [EventCategory.AppCrash, EventCategory.AppHang, EventCategory.Bugcheck, EventCategory.UnexpectedShutdown, EventCategory.DisplayDriverReset,
            EventCategory.DriverFailure, EventCategory.HardwareError, EventCategory.StorageError, EventCategory.ServiceFailure, EventCategory.DeviceProblem]),
        ("Power", [EventCategory.Boot, EventCategory.Shutdown, EventCategory.Sleep, EventCategory.Wake, EventCategory.PowerSource]),
        ("Network", [EventCategory.NetworkConnected, EventCategory.NetworkDisconnected]),
        ("Devices", [EventCategory.DeviceConnected, EventCategory.DeviceDisconnected]),
        ("Software", [EventCategory.UpdateInstalled, EventCategory.UpdateFailed, EventCategory.SoftwareInstalled, EventCategory.SoftwareRemoved,
            EventCategory.StartupItemAdded, EventCategory.DriverChanged]),
        ("Sentinel", [EventCategory.Anomaly, EventCategory.WorkloadSession, EventCategory.Other]),
    ];

    private readonly HistoryStore _store = App.Services.GetRequiredService<HistoryStore>();

    protected override int RefreshEveryTicks => 30;

    public ObservableCollection<TimelineDay> Days { get; } = [];
    public ObservableCollection<CorrelationRow> Related { get; } = [];
    public ObservableCollection<ChangeGroupRow> Changes { get; } = [];
    public IReadOnlyList<string> FilterNames { get; } = Filters.Select(f => f.Name).ToList();
    public HashSet<string> ActiveFilters { get; } = ["Reliability", "Power", "Network", "Devices", "Software", "Sentinel"];

    [ObservableProperty] public partial int Section { get; set; }
    [ObservableProperty] public partial int DaysBack { get; set; } = 7;
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial EventRow? Selected { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial string SelectedSummary { get; set; } = "";

    public bool HasSelection => !string.IsNullOrEmpty(SelectedSummary);
    [ObservableProperty] public partial IReadOnlyList<InfoItem> SelectedTelemetry { get; set; } = [];
    [ObservableProperty] public partial string ChangeRangeLabel { get; set; } = "";
    [ObservableProperty] public partial int ChangeCount { get; set; }

    partial void OnDaysBackChanged(int value) => Refresh();
    partial void OnSearchChanged(string value) => Refresh();
    partial void OnSelectedChanged(EventRow? value) => Explain(value);

    protected override void OnActivated(object? parameter)
    {
        if (parameter is TimeRange r) LoadChanges(r);
        else LoadChanges(TimeRange.Last(TimeSpan.FromDays(7), DateTimeOffset.Now, "in the last week"));
    }

    public void ToggleFilter(string name, bool on)
    {
        if (on) ActiveFilters.Add(name);
        else ActiveFilters.Remove(name);
        Refresh();
    }

    protected override void Refresh()
    {
        var now = DateTimeOffset.Now;
        var cats = Filters.Where(f => ActiveFilters.Contains(f.Name)).SelectMany(f => f.Categories).ToArray();
        var events = cats.Length == 0 ? [] : _store.QueryEvents(now.AddDays(-DaysBack), now, cats, 3000);
        if (!string.IsNullOrWhiteSpace(Search))
            events = events.Where(e => e.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) || (e.Detail?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        Days.Clear();
        foreach (var g in events.GroupBy(e => e.Timestamp.Date).OrderByDescending(g => g.Key))
        {
            var header = g.Key == DateTime.Today ? "Today" : g.Key == DateTime.Today.AddDays(-1) ? "Yesterday" : g.Key.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
            // Identical events in the same minute (for example one Store package per architecture) are shown once with a count.
            var rows = g.OrderByDescending(e => e.Timestamp)
                .GroupBy(e => (e.Title, e.Timestamp.ToUnixTimeSeconds() / 60))
                .Select(r => r.Count() == 1 ? new EventRow(r.First()) : new EventRow(r.First() with { Title = $"{r.First().Title} (×{r.Count()})" }))
                .ToList();
            Days.Add(new TimelineDay($"{header} · {rows.Count} event{(rows.Count == 1 ? "" : "s")}", rows));
        }
    }

    private void Explain(EventRow? row)
    {
        Related.Clear();
        if (row is null)
        {
            SelectedSummary = "";
            SelectedTelemetry = [];
            return;
        }
        var ctx = Services.GetRequiredService<CorrelationEngine>().Explain(row.Event);
        SelectedSummary = ctx.Summary;
        foreach (var c in ctx.Related.Take(12))
            Related.Add(new CorrelationRow(c.Strength.Label(), c.Related.Title, c.Related.Timestamp.ToString("g", CultureInfo.CurrentCulture), c.Rationale, c.Strength));
        var extra = new List<InfoItem>();
        if (row.Event.Code is { } code && row.Event.Category == EventCategory.Bugcheck && BugcheckCatalog.Lookup(code) is { } bc)
        {
            extra.Add(new InfoItem("Stop code", $"{bc.Name} ({code})", "Microsoft bug check reference"));
            extra.Add(new InfoItem("Category", bc.Category, "Microsoft bug check reference"));
            extra.Add(new InfoItem("What it means", bc.Explanation, "Microsoft bug check reference"));
        }
        extra.AddRange(ctx.Telemetry.Select(t => new InfoItem(t.Label, $"{t.Value} (at {t.Timestamp:t})", "Sentinel history")));
        extra.Add(new InfoItem("Source", row.Event.Source, null));
        SelectedTelemetry = extra;
    }

    [RelayCommand]
    private void ChangesSince(string key)
    {
        var now = DateTimeOffset.Now;
        var boot = P.System.Inventory.BootTime;
        LoadChanges(key switch
        {
            "yesterday" => new TimeRange(TimeRange.StartOfDay(now).AddDays(-1), now, "since yesterday"),
            "restart" => new TimeRange(boot, now, "since the last restart"),
            "month" => TimeRange.Last(TimeSpan.FromDays(30), now, "in the last 30 days"),
            _ => TimeRange.Last(TimeSpan.FromDays(7), now, "in the last week"),
        });
    }

    public void LoadChanges(TimeRange range)
    {
        var groups = Services.GetRequiredService<TimelineService>().WhatChanged(range);
        Changes.Clear();
        foreach (var g in groups)
            Changes.Add(new ChangeGroupRow(g.Kind, g.Glyph, g.Items.Take(40).Select(i => new ChangeItemRow(i.Timestamp.ToString("MMM d, t", CultureInfo.CurrentCulture), i.Title,
                i.Before is null && i.After is null ? "" : $"{i.Before ?? "—"} → {i.After ?? "—"}", i.Source)).ToList()));
        ChangeCount = groups.Sum(g => g.Items.Count);
        ChangeRangeLabel = ChangeCount == 0 ? $"No changes recorded {range.Label}." : $"{ChangeCount} change{(ChangeCount == 1 ? "" : "s")} {range.Label}.";
    }
}

// ---------------------------------------------------------------- Anomalies

public sealed record AnomalyCard(string Title, string Description, Severity Severity, HealthStatus Status, string SeverityText, string When, string Deviation,
    string Confidence, IReadOnlyList<EvidenceRow> Evidence, string Correlated, bool Active);

public sealed record BaselineRow(string Metric, string Context, string Typical, string Median, string Basis);

public sealed partial class AnomaliesViewModel : PageViewModel
{
    protected override int RefreshEveryTicks => 15;

    public ObservableCollection<AnomalyCard> Active { get; } = [];
    public ObservableCollection<AnomalyCard> History { get; } = [];
    public ObservableCollection<BaselineRow> Baselines { get; } = [];

    [ObservableProperty] public partial string Summary { get; set; } = "";

    protected override void Refresh()
    {
        var now = DateTimeOffset.Now;
        var all = Services.GetRequiredService<HistoryStore>().QueryAnomalies(now.AddDays(-30), now);
        AnomalyCard Card(Anomaly a) => new(a.Title, a.Description, a.Severity, a.Severity switch { Severity.Critical => HealthStatus.Critical, Severity.Warning => HealthStatus.Attention, _ => HealthStatus.Normal },
            a.Severity.Label(), $"{a.Start:g} · {UnitFormatter.Duration(a.Duration)}" + (a.Active ? " · ongoing" : ""), a.DeviationText, a.Confidence.Label(),
            a.Evidence.Select(e => new EvidenceRow(e.Kind, e.Statement, e.Source)).ToList(),
            a.CorrelatedEvents.Count == 0 ? "" : "Around the same time: " + string.Join(" · ", a.CorrelatedEvents), a.Active);
        Sync(Active, all.Where(a => a.Active).Select(Card).ToList());
        Sync(History, all.Where(a => !a.Active).Take(40).Select(Card).ToList());
        var baselines = Services.GetRequiredService<BaselineEngine>().All.OrderBy(b => b.MetricKey).Select(b =>
        {
            var def = Live.GetDefinition(b.MetricKey);
            string F(double v) => def?.Unit switch
            {
                MetricUnit.Celsius => U.Temperature(v),
                MetricUnit.Percent => $"{v:F0}%",
                MetricUnit.Watts => $"{v:F1} W",
                MetricUnit.BytesPerSecond => U.Throughput(v),
                _ => v.ToString("F1", CultureInfo.CurrentCulture),
            };
            return new BaselineRow(def?.Name ?? b.MetricKey, BaselineContexts.Describe(Enum.TryParse<BaselineContext>(b.Context, true, out var c) ? c : BaselineContext.All),
                $"{F(b.P05)} – {F(b.P95)}", F(b.Median), $"{b.SampleCount:N0} minutes over {b.DaysCovered} day(s){(b.IsMature ? "" : " · still learning")}");
        }).ToList();
        Sync(Baselines, baselines);
        Summary = Active.Count == 0
            ? "Nothing unusual right now. Sentinel compares live behaviour with what is normal for this PC, in the same context (idle, under load, on battery)."
            : $"{Active.Count} unusual pattern{(Active.Count == 1 ? "" : "s")} right now.";
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        if (target.SequenceEqual(source)) return;
        target.Clear();
        foreach (var s in source) target.Add(s);
    }
}

// ---------------------------------------------------------------- Reliability

public sealed record StopErrorRow(string Time, string Code, string Name, string Category, string Explanation);

public sealed record AppCrashRow(string App, string Counts, string Last, string Module);

public sealed partial class ReliabilityViewModel : PageViewModel
{
    private readonly HistoryStore _store = App.Services.GetRequiredService<HistoryStore>();

    protected override int RefreshEveryTicks => 30;

    public ObservableCollection<StopErrorRow> StopErrors { get; } = [];
    public ObservableCollection<AppCrashRow> Apps { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];

    [ObservableProperty] public partial ChartModel? Chart { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Counts { get; set; } = [];

    protected override void Refresh()
    {
        var now = DateTimeOffset.Now;
        var from = TimeRange.StartOfDay(now).AddDays(-29);
        var events = _store.QueryEvents(from, now, null, 10000).Where(e => e.IsReliabilityRelevant).ToList();

        var model = new ChartModel { From = from, To = now, YMin = 0 };
        IReadOnlyList<ChartPoint> Daily(Func<SystemEvent, bool> pred) => Enumerable.Range(0, 30)
            .Select(i => from.AddDays(i)).Select(d => new ChartPoint(d.AddHours(12), events.Count(e => e.Timestamp >= d && e.Timestamp < d.AddDays(1) && pred(e)))).ToList();
        model.Series.Add(new ChartSeries { Name = "App crashes and hangs", ColorIndex = 3, Fill = true, Points = Daily(e => e.Category is EventCategory.AppCrash or EventCategory.AppHang), Format = v => $"{v:F0}" });
        model.Series.Add(new ChartSeries { Name = "System failures", ColorIndex = 5, Fill = true, Points = Daily(e => e.Category is EventCategory.Bugcheck or EventCategory.UnexpectedShutdown or EventCategory.HardwareError), Format = v => $"{v:F0}" });
        model.Series.Add(new ChartSeries { Name = "Driver and service failures", ColorIndex = 4, Points = Daily(e => e.Category is EventCategory.DisplayDriverReset or EventCategory.DriverFailure or EventCategory.ServiceFailure or EventCategory.StorageError), Format = v => $"{v:F0}" });
        foreach (var b in events.Where(e => e.Category == EventCategory.Bugcheck)) model.Markers.Add(new ChartMarker(b.Timestamp, $"{b.Timestamp:g} {b.Title}"));
        Chart = model;

        var stops = events.Where(e => e.Category == EventCategory.Bugcheck).Select(e =>
        {
            var entry = e.Code is null ? null : BugcheckCatalog.Lookup(e.Code);
            return new StopErrorRow(e.Timestamp.ToString("g", CultureInfo.CurrentCulture), e.Code ?? "—", entry?.Name ?? "Unrecognised stop code", entry?.Category ?? "", entry?.Explanation ?? "");
        }).ToList();
        StopErrors.Clear();
        foreach (var s in stops) StopErrors.Add(s);

        Apps.Clear();
        foreach (var g in events.Where(e => e.Category is EventCategory.AppCrash or EventCategory.AppHang).GroupBy(e => e.Subject ?? "Unknown app").OrderByDescending(g => g.Count()).Take(10))
        {
            var crashes = g.Count(e => e.Category == EventCategory.AppCrash);
            var hangs = g.Count(e => e.Category == EventCategory.AppHang);
            var module = g.Select(e => e.Detail).Where(d => d is not null).GroupBy(d => d).OrderByDescending(m => m.Count()).FirstOrDefault()?.Key ?? "";
            Apps.Add(new AppCrashRow(g.Key, $"{crashes} crash{(crashes == 1 ? "" : "es")}, {hangs} hang{(hangs == 1 ? "" : "s")}", g.Max(e => e.Timestamp).ToString("g", CultureInfo.CurrentCulture), module));
        }

        Events.Clear();
        foreach (var e in events.OrderByDescending(e => e.Timestamp).Take(60)) Events.Add(new EventRow(e));

        int C(params EventCategory[] cats) => events.Count(e => cats.Contains(e.Category));
        Counts =
        [
            new("Stop errors", C(EventCategory.Bugcheck).ToString(CultureInfo.CurrentCulture), "WER system error reports"),
            new("Unexpected shutdowns", C(EventCategory.UnexpectedShutdown).ToString(CultureInfo.CurrentCulture), "Kernel-Power 41 / EventLog 6008"),
            new("App crashes", C(EventCategory.AppCrash).ToString(CultureInfo.CurrentCulture), "Application Error 1000"),
            new("App hangs", C(EventCategory.AppHang).ToString(CultureInfo.CurrentCulture), "Application Hang 1002"),
            new("Display driver resets", C(EventCategory.DisplayDriverReset).ToString(CultureInfo.CurrentCulture), "Display 4101"),
            new("Hardware error records", C(EventCategory.HardwareError).ToString(CultureInfo.CurrentCulture), "WHEA-Logger"),
            new("Storage errors", C(EventCategory.StorageError).ToString(CultureInfo.CurrentCulture), "disk / Ntfs"),
            new("Service failures", C(EventCategory.ServiceFailure).ToString(CultureInfo.CurrentCulture), "Service Control Manager"),
            new("Update failures", C(EventCategory.UpdateFailed).ToString(CultureInfo.CurrentCulture), "WindowsUpdateClient 20"),
        ];
        var serious = C(EventCategory.Bugcheck, EventCategory.UnexpectedShutdown);
        Summary = serious == 0
            ? "No system crashes in the last 30 days." + (C(EventCategory.AppCrash, EventCategory.AppHang) > 0 ? " Some applications crashed or stopped responding; see below." : "")
            : $"{serious} system failure{(serious == 1 ? "" : "s")} (stop errors or unexpected shutdowns) in the last 30 days.";
    }
}
