using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.App.Controls;
using Sentinel.Core.Metrics;
using Sentinel.Diagnostics;
using Sentinel.Domain;
using Sentinel.Intelligence;
using Sentinel.Platform.Windows.Providers;

namespace Sentinel.App.ViewModels;

public sealed record SensorRow(string Name, string Component, string Value, string Source, string Quality, string Limit, string Glyph);

public sealed partial class ThermalsViewModel : PageViewModel
{
    private int _historyTick;

    public ObservableCollection<SensorRow> Sensors { get; } = [];
    public ObservableCollection<string> Unavailable { get; } = [];

    [ObservableProperty] public partial string RangeKey { get; set; } = "1h";
    [ObservableProperty] public partial ChartModel? Chart { get; set; }
    [ObservableProperty] public partial string DiagnosisTitle { get; set; } = "";
    [ObservableProperty] public partial string DiagnosisSummary { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<EvidenceRow> DiagnosisFindings { get; set; } = [];
    [ObservableProperty] public partial bool HasDiagnosis { get; set; }

    partial void OnRangeKeyChanged(string value) => RefreshChart();

    protected override void OnActivated(object? parameter)
    {
        Unavailable.Clear();
        Unavailable.Add("CPU package temperature — Windows does not expose it through a documented interface, and Sentinel does not install drivers to read processor registers.");
        if (!P.Thermal.Sensors.Any())
            Unavailable.Add("ACPI thermal zones — this PC's firmware does not publish any to Windows.");
        foreach (var a in P.Gpu.Adapters.Where(a => a.Kind is GpuKind.Discrete or GpuKind.Integrated && !a.VendorTelemetryAvailable))
            Unavailable.Add($"{a.Name} temperature — no documented read-only interface for this vendor is integrated.");
        Unavailable.Add("Fan speeds — not exposed through documented interfaces on most PCs. Fan control stays with your manufacturer's app.");
    }

    protected override void Refresh()
    {
        var rows = new List<SensorRow>();
        foreach (var s in P.Thermal.Sensors)
            rows.Add(new SensorRow(s.Name, s.Component, s.Temperature.HasValue ? U.Temperature(s.Temperature.Value) : "—", s.Temperature.Source,
                s.Temperature.Quality == Quality.Good ? "Live" : s.Temperature.Reason ?? s.Temperature.Quality.Label(),
                s.LimitCelsius is { } l ? $"Limit {U.Temperature(l)} ({s.LimitSource})" : "No documented limit", ""));
        foreach (var g in P.Gpu.Latest)
        {
            var a = P.Gpu.Adapters.FirstOrDefault(x => x.Id == g.AdapterId);
            if (a is null || !a.VendorTelemetryAvailable) continue;
            var limits = P.Gpu is GpuProvider gp ? gp.TemperatureLimits(a.Id) : (null, null);
            rows.Add(new SensorRow(a.Name, "GPU core", g.Temperature.HasValue ? U.Temperature(g.Temperature.Value) : g.InLowPowerState ? "Resting" : "—", g.Temperature.Source,
                g.Temperature.HasValue ? "Live" : g.Temperature.Reason ?? "", limits.Slowdown is { } sl ? $"Driver slowdown at {U.Temperature(sl)}" : "No documented limit", ""));
        }
        foreach (var d in P.Storage.Disks)
        {
            if (!P.Storage.Health.TryGetValue(d.Id, out var h) || !h.Temperature.HasValue) continue;
            rows.Add(new SensorRow(d.Model, "Drive", U.Temperature(h.Temperature.Value), h.Temperature.Source, "Updated every minute",
                h.CriticalTemperature.HasValue ? $"Drive critical at {U.Temperature(h.CriticalTemperature.Value)}" : h.WarningTemperature.HasValue ? $"Drive warning at {U.Temperature(h.WarningTemperature.Value)}" : "No documented limit", ""));
        }
        foreach (var b in P.Battery.Latest.Batteries.Where(b => b.Temperature.HasValue))
            rows.Add(new SensorRow(b.Name ?? "Battery", "Battery", U.Temperature(b.Temperature.Value), b.Temperature.Source, "Live", "No documented limit", ""));
        if (!Sensors.SequenceEqual(rows))
        {
            Sensors.Clear();
            foreach (var r in rows) Sensors.Add(r);
        }
        if (++_historyTick % 10 == 0 || Chart is null) RefreshChart();
    }

    private void RefreshChart()
    {
        var now = DateTimeOffset.Now;
        var live = RangeKey == ChartData.Live;
        var (from, to) = ChartData.Range(RangeKey, now);
        var model = new ChartModel { From = from, To = to };
        var color = 5;
        var temps = Live.Definitions.Where(d => d.Unit == MetricUnit.Celsius && d.Persist == PersistPolicy.Full).ToList();
        foreach (var d in temps.Take(4))
        {
            var pts = ChartData.Points(d.Key, from, to, live);
            if (pts.Count == 0) continue;
            model.Series.Add(new ChartSeries { Name = d.Name, ColorIndex = color, Points = pts, Format = v => U.Temperature(v) });
            color = color == 5 ? 3 : color == 3 ? 4 : 6;
        }
        model.Series.Add(new ChartSeries { Name = "CPU load", ColorIndex = 1, OwnScale = true, Min = 0, Max = 100, Fill = true, Points = ChartData.Points(MetricKeys.CpuUtil, from, to, live), Format = v => $"{v:F0}%" });
        model.Series.Add(new ChartSeries { Name = "GPU load", ColorIndex = 2, OwnScale = true, Min = 0, Max = 100, Dashed = true, Points = ChartData.Points(MetricKeys.GpuUtilAny, from, to, live), Format = v => $"{v:F0}%" });
        if (P.Cpu.Latest.PackagePowerW.HasValue)
            model.Series.Add(new ChartSeries { Name = "CPU power", ColorIndex = 6, OwnScale = true, Min = 0, Points = ChartData.Points(MetricKeys.CpuPower, from, to, live), Format = v => $"{v:F1} W" });
        if (!live) ChartData.Annotate(model, [EventCategory.WorkloadSession, EventCategory.Anomaly, EventCategory.PowerSource], k => k.StartsWith("thermal.", StringComparison.Ordinal) || k.EndsWith(".temp", StringComparison.Ordinal));
        Chart = model;
    }

    [RelayCommand]
    private void Diagnose()
    {
        var (from, to) = ChartData.Range(RangeKey == ChartData.Live ? "1h" : RangeKey, DateTimeOffset.Now);
        var q = new ParsedQuery("Why is my PC hot?", QueryIntent.Heat, new TimeRange(from, to, "the selected period"), true, null, null, null);
        var r = Services.GetRequiredService<DiagnosticsEngine>().Run(q);
        DiagnosisTitle = $"{r.Title} · {r.Confidence.Label()}";
        DiagnosisSummary = r.Summary;
        DiagnosisFindings = r.Findings.Select(f => new EvidenceRow(f.Kind, f.Text, f.Source)).ToList();
        HasDiagnosis = true;
    }
}
