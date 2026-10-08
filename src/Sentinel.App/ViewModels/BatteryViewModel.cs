using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.App.Controls;
using Sentinel.Core.Metrics;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Diagnostics;
using Sentinel.Domain;
using Sentinel.Intelligence;
using Sentinel.Platform.Windows.Services;

namespace Sentinel.App.ViewModels;

public sealed record SessionRow(string Kind, string Glyph, string When, string Duration, string Change, string Power, string Note);

public sealed partial class BatteryViewModel : PageViewModel
{
    private readonly HistoryStore _store = App.Services.GetRequiredService<HistoryStore>();
    private int _historyTick;

    public ObservableCollection<SessionRow> Sessions { get; } = [];

    [ObservableProperty] public partial bool HasBattery { get; set; } = true;
    [ObservableProperty] public partial string RangeKey { get; set; } = "6h";
    [ObservableProperty] public partial ChartModel? Chart { get; set; }
    [ObservableProperty] public partial ChartModel? CapacityChart { get; set; }
    [ObservableProperty] public partial string Percent { get; set; } = "—";
    [ObservableProperty] public partial string State { get; set; } = "";
    [ObservableProperty] public partial string Rate { get; set; } = "—";
    [ObservableProperty] public partial string RateCaption { get; set; } = "";
    [ObservableProperty] public partial string Remaining { get; set; } = "—";
    [ObservableProperty] public partial string RemainingCaption { get; set; } = "";
    [ObservableProperty] public partial string Health { get; set; } = "—";
    [ObservableProperty] public partial string HealthCaption { get; set; } = "";
    [ObservableProperty] public partial double ChargeValue { get; set; }
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Details { get; set; } = [];
    [ObservableProperty] public partial string CapacityNote { get; set; } = "";
    [ObservableProperty] public partial string ImportStatus { get; set; } = "";
    [ObservableProperty] public partial bool Importing { get; set; }
    [ObservableProperty] public partial string DrainSummary { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<EvidenceRow> DrainFindings { get; set; } = [];

    partial void OnRangeKeyChanged(string value) => RefreshChart();

    protected override void OnActivated(object? parameter)
    {
        LoadHistory();
        RunDrainDetective();
    }

    protected override void Refresh()
    {
        var b = P.Battery.Latest;
        HasBattery = b.Present || b.Timestamp == DateTimeOffset.MinValue;
        if (!b.Present) return;
        var info = b.Batteries.FirstOrDefault();
        Percent = UnitFormatter.Percent(b.Percent.Value);
        ChargeValue = b.Percent.Value ?? 0;
        State = b.State switch
        {
            ChargeState.Charging => "Charging",
            ChargeState.Discharging => "On battery",
            ChargeState.Full => "Fully charged",
            _ => "Plugged in, not charging",
        } + (b.BatterySaverOn ? " · Battery Saver on" : "");
        Rate = b.RateMilliwatts.Value is { } mw ? $"{Math.Abs(mw) / 1000:F1} W" : "—";
        RateCaption = b.RateMilliwatts.Value is { } r ? (r < -100 ? "Discharging" : r > 100 ? "Charging" : "Idle") : b.RateMilliwatts.Reason ?? "";
        Remaining = b.RemainingMWh.Value is { } rem ? $"{rem / 1000:F1} Wh" : "—";
        RemainingCaption = b.TimeRemaining.HasValue ? $"About {UnitFormatter.Duration(TimeSpan.FromSeconds(b.TimeRemaining.Value!.Value))} left (Windows estimate)" : b.TimeRemaining.Reason ?? "";
        Health = info?.EstimatedHealthPercent is { } hp ? $"{hp:F0}%" : "—";
        HealthCaption = info?.EstimatedHealthPercent is not null ? "Estimated: full-charge ÷ design capacity" : "Design capacity not reported";
        Details =
        [
            new("Battery", info?.Name, "Battery firmware"),
            new("Manufacturer", info?.Manufacturer, "Battery firmware"),
            new("Chemistry", info?.Chemistry, "Battery firmware"),
            new("Design capacity", info?.DesignCapacityMWh is { } d ? $"{d / 1000.0:F1} Wh" : null, "Battery firmware", Tooltip: "The energy the battery was designed to hold when new."),
            new("Full-charge capacity", info?.FullChargeCapacityMWh is { } f ? $"{f / 1000.0:F1} Wh" : null, "Battery firmware", Tooltip: "The energy the battery can hold now. It decreases with age and use."),
            new("Estimated health", info?.EstimatedHealthPercent is { } h ? $"{h:F1}%" : null, "Full-charge ÷ design × 100", Tooltip: "Battery Health = Full Charge Capacity / Design Capacity × 100. An estimate based on what the battery reports; recalibration can move it a few percent."),
            new("Cycle count", info?.CycleCount?.ToString(CultureInfo.CurrentCulture), "Battery firmware"),
            new("Voltage", b.VoltageMv.HasValue ? UnitFormatter.Volts(b.VoltageMv.Value) : null, b.VoltageMv.Source),
            new("Temperature", info?.Temperature.HasValue == true ? U.Temperature(info.Temperature.Value) : null, info?.Temperature.Reason),
            new("Manufactured", info?.ManufactureDate?.ToString("d", CultureInfo.CurrentCulture), "Battery firmware"),
            new("Serial number", info?.SerialNumber, "Battery firmware", Sensitive: true),
        ];
        if (++_historyTick % 30 == 0) RefreshChart();
    }

    private void LoadHistory()
    {
        RefreshChart();
        var now = DateTimeOffset.Now;
        var points = _store.QueryCapacity();
        var model = new ChartModel { From = points.Count > 0 ? points[0].Date.AddDays(-2) : now.AddDays(-30), To = now, YMin = 0 };
        model.Series.Add(new ChartSeries { Name = "Full-charge capacity", ColorIndex = 3, Fill = true, Points = points.Select(p => new ChartPoint(p.Date, p.FullChargeMWh / 1000)).ToList(), Format = v => $"{v:F1} Wh" });
        var design = points.Where(p => p.DesignMWh is not null).Select(p => new ChartPoint(p.Date, p.DesignMWh!.Value / 1000)).ToList();
        if (design.Count > 0) model.Series.Add(new ChartSeries { Name = "Design capacity", ColorIndex = 6, Dashed = true, Points = design, Format = v => $"{v:F1} Wh" });
        CapacityChart = model;
        CapacityNote = points.Count switch
        {
            0 => "No capacity history yet. Sentinel records it daily, or you can import the history Windows already keeps.",
            < 3 => "Capacity history is just starting. Import Windows' battery history to see months of data now.",
            _ => $"{points.Count} readings from {points[0].Date:d} to {points[^1].Date:d}. Sources: {string.Join(", ", points.Select(p => p.Source).Distinct())}.",
        };

        Sessions.Clear();
        foreach (var s in _store.QueryPowerSessions(now.AddDays(-14), now).Take(30))
        {
            var perHour = s.PercentDelta is { } d && s.Duration.TotalHours > 0.2 ? $" · {-d / s.Duration.TotalHours:F1}%/h" : "";
            Sessions.Add(new SessionRow(
                s.Kind switch { PowerSessionKind.Charge => "Plugged in", PowerSessionKind.Sleep => "Asleep", _ => "On battery" },
                s.Kind switch { PowerSessionKind.Charge => "", PowerSessionKind.Sleep => "", _ => "" },
                UnitFormatter.When(s.Start),
                UnitFormatter.Duration(s.Duration),
                s.StartPercent is { } a && s.EndPercent is { } e ? $"{a:F0}% → {e:F0}%" : "—",
                s.AverageWatts is { } w ? $"{Math.Abs(w):F1} W avg" : "",
                (s.Kind == PowerSessionKind.Sleep ? perHour.TrimStart(' ', '·', ' ') : "") + (s.Notes is null ? "" : (s.Kind == PowerSessionKind.Sleep && perHour.Length > 0 ? " · " : "") + s.Notes)));
        }
    }

    private void RunDrainDetective()
    {
        var engine = Services.GetRequiredService<DiagnosticsEngine>();
        var q = QueryParser.Parse("why did my battery drain today", DateTimeOffset.Now);
        var r = engine.Run(q);
        DrainSummary = r.Summary;
        DrainFindings = r.Findings.Select(f => new EvidenceRow(f.Kind, f.Text, f.Source)).ToList();
    }

    private void RefreshChart()
    {
        var now = DateTimeOffset.Now;
        var live = RangeKey == ChartData.Live;
        var (from, to) = ChartData.Range(RangeKey, now);
        var model = new ChartModel { From = from, To = to, YMin = 0, YMax = 100 };
        model.Series.Add(new ChartSeries { Name = "Charge", ColorIndex = 3, Fill = true, Points = ChartData.Points(MetricKeys.BatPercent, from, to, live), Format = v => $"{v:F0}%" });
        model.Series.Add(new ChartSeries { Name = "Power (− discharging, + charging)", ColorIndex = 1, OwnScale = true, Points = ChartData.Points(MetricKeys.BatRate, from, to, live), Format = v => $"{v:+0.0;-0.0} W" });
        if (!live) ChartData.Annotate(model, [EventCategory.PowerSource, EventCategory.Sleep, EventCategory.Wake], k => k == MetricKeys.BatRate);
        Chart = model;
    }

    [RelayCommand]
    private async Task ImportReportAsync()
    {
        Importing = true;
        ImportStatus = "Asking Windows for its battery report (powercfg /batteryreport)…";
        try
        {
            var data = await Services.GetRequiredService<BatteryReportService>().GenerateBatteryReportAsync(CancellationToken.None);
            if (data is null)
            {
                ImportStatus = "Windows did not produce a battery report.";
                return;
            }
            await _store.UpsertCapacityAsync(data.CapacityHistory, CancellationToken.None);
            ImportStatus = data.CapacityHistory.Count > 0
                ? $"Imported {data.CapacityHistory.Count} capacity readings from Windows' battery history. The temporary report file was deleted."
                : "Windows' report contained no capacity history for this battery.";
            LoadHistory();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Xml.XmlException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ImportStatus = "The battery report could not be read: " + ex.Message;
        }
        finally
        {
            Importing = false;
        }
    }
}
