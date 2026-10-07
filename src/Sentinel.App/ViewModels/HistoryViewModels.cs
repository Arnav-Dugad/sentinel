using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.App.Controls;
using Sentinel.Core.Metrics;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Intelligence;

namespace Sentinel.App.ViewModels;

public sealed record DeltaRow(string Name, string Before, string After, string Delta);

public sealed record MetricChoice(string Name, string Key, MetricUnit Unit);

public sealed partial class CompareViewModel : PageViewModel
{
    private readonly HistoryStore _store = App.Services.GetRequiredService<HistoryStore>();
    private TimeRange _a;
    private TimeRange _b;

    protected override int RefreshEveryTicks => 3600;

    public IReadOnlyList<string> Presets { get; } =
    [
        "Today vs yesterday",
        "This week vs last week",
        "Last 24 hours vs the 24 hours before",
        "Latest high-performance session vs the previous one",
        "After vs before the latest driver change",
    ];

    public ObservableCollection<DeltaRow> Deltas { get; } = [];
    public ObservableCollection<MetricChoice> Metrics { get; } = [];

    [ObservableProperty] public partial int PresetIndex { get; set; } = -1;
    [ObservableProperty] public partial string LabelA { get; set; } = "";
    [ObservableProperty] public partial string LabelB { get; set; } = "";
    [ObservableProperty] public partial string Note { get; set; } = "Choose what to compare.";
    [ObservableProperty] public partial MetricChoice? SelectedMetric { get; set; }
    [ObservableProperty] public partial ChartModel? Chart { get; set; }

    partial void OnPresetIndexChanged(int value) => Run(value);
    partial void OnSelectedMetricChanged(MetricChoice? value) => BuildChart();

    protected override void OnActivated(object? parameter)
    {
        Metrics.Clear();
        foreach (var d in Live.Definitions.Where(d => d.Persist == PersistPolicy.Full && d.Unit is MetricUnit.Percent or MetricUnit.Celsius or MetricUnit.Watts or MetricUnit.BytesPerSecond or MetricUnit.Megahertz).OrderBy(d => d.Category).ThenBy(d => d.Name))
            Metrics.Add(new MetricChoice(d.Name, d.Key, d.Unit));
        SelectedMetric = Metrics.FirstOrDefault(m => m.Key == MetricKeys.CpuUtil) ?? Metrics.FirstOrDefault();
        if (PresetIndex < 0) PresetIndex = 0;
    }

    protected override void Refresh()
    {
    }

    private void Run(int preset)
    {
        var now = DateTimeOffset.Now;
        Deltas.Clear();
        switch (preset)
        {
            case 0:
                _a = TimeRange.Yesterday(now);
                _b = TimeRange.Today(now);
                break;
            case 1:
                _a = new TimeRange(now.AddDays(-14), now.AddDays(-7), "last week");
                _b = TimeRange.Last(TimeSpan.FromDays(7), now, "this week");
                break;
            case 2:
                _b = TimeRange.Last(TimeSpan.FromDays(1), now, "last 24 hours");
                _a = _b.Previous("previous 24 hours");
                break;
            case 3:
            {
                var sessions = _store.QueryWorkloadSessions(now.AddDays(-60), now, 2);
                if (sessions.Count < 2)
                {
                    Note = "Two high-performance sessions are needed. Sessions are recorded automatically during sustained heavy load.";
                    LabelA = LabelB = "";
                    Chart = null;
                    return;
                }
                CompareSessions(sessions[1], sessions[0]);
                _a = new TimeRange(sessions[1].Start, sessions[1].End, "previous session");
                _b = new TimeRange(sessions[0].Start, sessions[0].End, "latest session");
                LabelA = $"Previous: {sessions[1].AppName ?? "session"} · {sessions[1].Start:g}";
                LabelB = $"Latest: {sessions[0].AppName ?? "session"} · {sessions[0].Start:g}";
                BuildChart();
                return;
            }
            case 4:
            {
                var change = _store.QueryEvents(now.AddDays(-60), now, [EventCategory.DriverChanged], 1).FirstOrDefault();
                if (change is null)
                {
                    Note = "No driver change was recorded in the last 60 days.";
                    LabelA = LabelB = "";
                    Chart = null;
                    return;
                }
                _a = new TimeRange(change.Timestamp.AddDays(-7), change.Timestamp, "7 days before");
                _b = new TimeRange(change.Timestamp, change.Timestamp.AddDays(7) < now ? change.Timestamp.AddDays(7) : now, "after");
                Note = $"Change: {change.Title} on {change.Timestamp:g}. Workloads differ between periods, so treat differences as indicative.";
                break;
            }
        }
        LabelA = $"A: {_a.Label} ({_a.From:g} – {_a.To:g})";
        LabelB = $"B: {_b.Label} ({_b.From:g} – {_b.To:g})";
        var deltas = Services.GetRequiredService<TimelineService>().Compare(_a, _b);
        foreach (var d in deltas) Deltas.Add(new DeltaRow(d.Name, d.BeforeText, d.AfterText, d.DeltaText));
        if (preset != 4) Note = deltas.Count == 0 ? "There is no recorded data for these periods yet." : "Averages over each period. Only metrics Sentinel actually recorded are compared.";
        BuildChart();
    }

    private void CompareSessions(WorkloadSession a, WorkloadSession b)
    {
        string T(double? v) => v is { } x ? U.Temperature(x) : "—";
        string P(double? v) => v is { } x ? $"{x:F0}%" : "—";
        string D(double? x, double? y, Func<double, string> f) => x is { } p && y is { } q ? f(q - p) : "—";
        Deltas.Add(new DeltaRow("Duration", UnitFormatter.Duration(a.Duration), UnitFormatter.Duration(b.Duration), ""));
        Deltas.Add(new DeltaRow("GPU utilization (avg)", P(a.GpuAvg), P(b.GpuAvg), D(a.GpuAvg, b.GpuAvg, v => $"{v:+0;-0} pts")));
        Deltas.Add(new DeltaRow("GPU temperature (avg)", T(a.GpuTempAvg), T(b.GpuTempAvg), D(a.GpuTempAvg, b.GpuTempAvg, U.TemperatureDelta)));
        Deltas.Add(new DeltaRow("GPU temperature (peak)", T(a.GpuTempPeak), T(b.GpuTempPeak), D(a.GpuTempPeak, b.GpuTempPeak, U.TemperatureDelta)));
        Deltas.Add(new DeltaRow("GPU power (avg)", a.GpuPowerAvg is { } pa ? $"{pa:F0} W" : "—", b.GpuPowerAvg is { } pb ? $"{pb:F0} W" : "—", D(a.GpuPowerAvg, b.GpuPowerAvg, v => $"{v:+0;-0} W")));
        Deltas.Add(new DeltaRow("CPU utilization (avg)", P(a.CpuAvg), P(b.CpuAvg), D(a.CpuAvg, b.CpuAvg, v => $"{v:+0;-0} pts")));
        Deltas.Add(new DeltaRow("Memory (peak)", U.Bytes(a.MemoryPeakBytes), U.Bytes(b.MemoryPeakBytes), ""));
        Deltas.Add(new DeltaRow("Stability events", a.CrashesDuring.ToString(CultureInfo.CurrentCulture), b.CrashesDuring.ToString(CultureInfo.CurrentCulture), ""));
        Note = "Frame rate is not compared: there is no safe, non-invasive source for it.";
    }

    private void BuildChart()
    {
        if (SelectedMetric is not { } m || _b.Span <= TimeSpan.Zero) return;
        var pointsA = ChartData.Points(m.Key, _a.From, _a.To, live: false);
        var pointsB = ChartData.Points(m.Key, _b.From, _b.To, live: false);
        var shift = _b.From - _a.From;
        Func<double, string> f = m.Unit switch
        {
            MetricUnit.Celsius => v => U.Temperature(v),
            MetricUnit.Percent => v => $"{v:F0}%",
            MetricUnit.Watts => v => $"{v:F1} W",
            MetricUnit.Megahertz => v => UnitFormatter.Frequency(v),
            _ => v => U.Throughput(v),
        };
        var model = new ChartModel { From = _b.From, To = _b.From + TimeSpan.FromTicks(Math.Max(_a.Span.Ticks, _b.Span.Ticks)), YMin = m.Unit == MetricUnit.Percent ? 0 : null, YMax = m.Unit == MetricUnit.Percent ? 100 : null };
        model.Series.Add(new ChartSeries { Name = "A", ColorIndex = 6, Dashed = true, Points = pointsA.Select(p => new ChartPoint(p.T + shift, p.V)).ToList(), Format = f });
        model.Series.Add(new ChartSeries { Name = "B", ColorIndex = 1, Fill = true, Points = pointsB, Format = f });
        Chart = model;
    }
}

public sealed record SnapshotRow(string Label, string Value, string Source);

public sealed partial class TimeMachineViewModel : PageViewModel
{
    protected override int RefreshEveryTicks => 3600;

    public ObservableCollection<SnapshotRow> Metrics { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];
    public ObservableCollection<AppRow> Apps { get; } = [];

    [ObservableProperty] public partial DateTimeOffset? Date { get; set; } = DateTimeOffset.Now;
    [ObservableProperty] public partial double Minute { get; set; } = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
    [ObservableProperty] public partial string MomentLabel { get; set; } = "";
    [ObservableProperty] public partial string Note { get; set; } = "";
    [ObservableProperty] public partial ChartModel? Chart { get; set; }

    partial void OnDateChanged(DateTimeOffset? value)
    {
        BuildDayChart();
        Reconstruct();
    }

    partial void OnMinuteChanged(double value) => Reconstruct();

    private DateTimeOffset Day => TimeRange.StartOfDay((Date ?? DateTimeOffset.Now).ToLocalTime());

    private DateTimeOffset Moment => Day.AddMinutes(Math.Clamp(Minute, 0, 1439));

    protected override void OnActivated(object? parameter)
    {
        if (parameter is TimeRange r)
        {
            Date = r.From;
            Minute = r.From.Hour * 60 + r.From.Minute;
        }
        BuildDayChart();
        Reconstruct();
    }

    protected override void Refresh()
    {
    }

    [RelayCommand]
    private void Step(string minutes)
    {
        if (double.TryParse(minutes, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) Minute = Math.Clamp(Minute + m, 0, 1439);
    }

    private void BuildDayChart()
    {
        var day = Day;
        var model = new ChartModel { From = day, To = day.AddDays(1), YMin = 0, YMax = 100 };
        model.Series.Add(new ChartSeries { Name = "CPU", ColorIndex = 1, Fill = true, Points = ChartData.Points(MetricKeys.CpuUtil, day, day.AddDays(1), false), Format = v => $"{v:F0}%" });
        model.Series.Add(new ChartSeries { Name = "GPU", ColorIndex = 4, Points = ChartData.Points(MetricKeys.GpuUtilAny, day, day.AddDays(1), false), Format = v => $"{v:F0}%" });
        model.Series.Add(new ChartSeries { Name = "Memory", ColorIndex = 2, Points = ChartData.Points(MetricKeys.MemUsedPct, day, day.AddDays(1), false), Format = v => $"{v:F0}%" });
        ChartData.Annotate(model, [EventCategory.Bugcheck, EventCategory.UnexpectedShutdown, EventCategory.AppCrash, EventCategory.Boot, EventCategory.Sleep, EventCategory.Wake, EventCategory.UpdateInstalled, EventCategory.DisplayDriverReset]);
        Chart = model;
    }

    private void Reconstruct()
    {
        var at = Moment;
        MomentLabel = at.ToString("dddd d MMMM yyyy, t", CultureInfo.CurrentCulture);
        var snap = Services.GetRequiredService<TimelineService>().Reconstruct(at);
        Metrics.Clear();
        foreach (var m in snap.Metrics) Metrics.Add(new SnapshotRow(m.Label, m.Value, m.Source));
        Events.Clear();
        foreach (var e in snap.Events) Events.Add(new EventRow(e));
        Apps.Clear();
        foreach (var a in snap.Apps)
            Apps.Add(new AppRow(AppNames.Display(a.App, P), "", UnitFormatter.Percent(a.Cpu, 1), U.Bytes(a.MemoryBytes), UnitFormatter.Percent(a.Gpu), "", a.Cpu));
        Note = snap.HasData
            ? "Reconstructed from Sentinel's stored history at the nearest available resolution (10 seconds for the last two days, then minutes and hours)."
            : "Sentinel has no recorded data for this moment. History exists only for times Sentinel was running.";
    }
}
