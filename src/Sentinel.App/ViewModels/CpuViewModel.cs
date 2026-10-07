using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sentinel.App.Controls;
using Sentinel.App.Services;
using Sentinel.Core.Metrics;
using Sentinel.Core.Units;
using Sentinel.Domain;

namespace Sentinel.App.ViewModels;

public sealed partial class CoreTile : ObservableObject
{
    public required int Index { get; init; }
    public required string Kind { get; init; }

    [ObservableProperty] public partial double Utilization { get; set; }
    [ObservableProperty] public partial string Text { get; set; } = "0%";
}

public sealed partial class CpuViewModel : PageViewModel
{
    private int _historyTick;

    public ObservableCollection<CoreTile> Cores { get; } = [];

    [ObservableProperty] public partial string RangeKey { get; set; } = ChartData.Live;
    [ObservableProperty] public partial ChartModel? Chart { get; set; }
    [ObservableProperty] public partial string Utilization { get; set; } = "—";
    [ObservableProperty] public partial string Clock { get; set; } = "—";
    [ObservableProperty] public partial string ClockCaption { get; set; } = "";
    [ObservableProperty] public partial string Temperature { get; set; } = "—";
    [ObservableProperty] public partial string TemperatureCaption { get; set; } = "";
    [ObservableProperty] public partial string Power { get; set; } = "—";
    [ObservableProperty] public partial string PowerCaption { get; set; } = "";
    [ObservableProperty] public partial string Limitation { get; set; } = "—";
    [ObservableProperty] public partial string Processes { get; set; } = "—";
    [ObservableProperty] public partial string Uptime { get; set; } = "—";
    [ObservableProperty] public partial string Analysis { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Activity { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Inventory { get; set; } = [];
    [ObservableProperty] public partial bool ShowClock { get; set; } = true;
    [ObservableProperty] public partial bool ShowPower { get; set; } = true;
    [ObservableProperty] public partial bool ShowTemperature { get; set; } = true;
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";

    partial void OnRangeKeyChanged(string value) => RefreshChart();
    partial void OnShowClockChanged(bool value) => RefreshChart();
    partial void OnShowPowerChanged(bool value) => RefreshChart();
    partial void OnShowTemperatureChanged(bool value) => RefreshChart();

    protected override void OnActivated(object? parameter)
    {
        if (parameter is Intelligence.TimeRange r) RangeKey = r.Span > TimeSpan.FromDays(8) ? "30d" : r.Span > TimeSpan.FromDays(1.5) ? "7d" : r.Span > TimeSpan.FromHours(7) ? "24h" : "6h";
        var inv = P.Cpu.Inventory;
        Name = inv.Name;
        Subtitle = $"{inv.PhysicalCores} cores, {inv.LogicalProcessors} logical processors" + (inv.IsHybrid ? $" • {inv.PerformanceCores} performance + {inv.EfficiencyCores} efficiency cores" : "");
        Cores.Clear();
        for (var i = 0; i < inv.LogicalProcessors; i++)
        {
            var cls = i < inv.CoreEfficiencyClassByLogical.Count ? inv.CoreEfficiencyClassByLogical[i] : 0;
            var maxCls = inv.CoreEfficiencyClassByLogical.Count > 0 ? inv.CoreEfficiencyClassByLogical.Max() : 0;
            Cores.Add(new CoreTile { Index = i, Kind = inv.IsHybrid ? (cls == maxCls ? "P" : "E") : "" });
        }
        Inventory = BuildInventory();
    }

    private IReadOnlyList<InfoItem> BuildInventory()
    {
        var inv = P.Cpu.Inventory;
        var list = new List<InfoItem>
        {
            new("Processor", inv.Name, "Registry (CPUID string)"),
            new("Manufacturer", inv.Manufacturer, "Registry"),
            new("Architecture", inv.Architecture, ".NET runtime"),
            new("Physical cores", inv.PhysicalCores.ToString(CultureInfo.CurrentCulture), "GetLogicalProcessorInformationEx"),
            new("Logical processors", inv.LogicalProcessors.ToString(CultureInfo.CurrentCulture), "GetLogicalProcessorInformationEx"),
            new("Hybrid design", inv.IsHybrid ? $"Yes — {inv.PerformanceCores} performance cores, {inv.EfficiencyCores} efficiency cores" : "No", "Core efficiency classes"),
            new("Processor groups", inv.ProcessorGroups.ToString(CultureInfo.CurrentCulture), "GetActiveProcessorGroupCount"),
            new("NUMA nodes", inv.NumaNodes.ToString(CultureInfo.CurrentCulture), "GetLogicalProcessorInformationEx"),
            new("Base frequency", inv.BaseMhz is { } b ? UnitFormatter.Frequency(b) : null, "Processor Frequency counter"),
            new("Virtualization", inv.VirtualizationSupported switch { true => "Enabled in firmware", false => "Disabled in firmware", _ => null }, "WMI Win32_Processor"),
        };
        foreach (var c in inv.Caches)
            list.Add(new InfoItem($"L{c.Level} {c.Type.ToLowerInvariant()} cache", $"{U.Bytes(c.SizeBytes)} total" + (c.SharedByLogical > 1 ? $" ({c.SharedByLogical} instances)" : ""), "GetLogicalProcessorInformationEx"));
        return list;
    }

    protected override void Refresh()
    {
        var s = P.Cpu.Latest;
        Utilization = UnitFormatter.Percent(s.Utilization.Value);
        Clock = UnitFormatter.Frequency(s.EffectiveMhz.Value);
        ClockCaption = P.Cpu.Inventory.BaseMhz is { } b ? $"Base {UnitFormatter.Frequency(b)}" : "";
        var t = LiveSummary.CpuAreaTemperature(P);
        Temperature = t is { } tt ? U.Temperature(tt.Temp) : "Not exposed";
        TemperatureCaption = t is { } t2 ? t2.Source : "No safe CPU temperature source on this PC";
        Power = s.PackagePowerW.HasValue ? UnitFormatter.Watts(s.PackagePowerW.Value) : "Not exposed";
        PowerCaption = s.PackagePowerW.HasValue ? "Package (Windows Energy Meter)" : "No safe package power source";
        Limitation = s.ThrottleIndicator.HasValue ? (s.ThrottleIndicator.Value < 0.5 ? "Not limited" : $"{s.ThrottleIndicator.Value:F0}% below nominal") : "Not exposed";
        Processes = $"{s.Processes:N0} / {s.Threads:N0}";
        Uptime = UnitFormatter.Duration(s.Uptime);
        Activity =
        [
            new("Processor queue", s.QueueLength.Value?.ToString("F0", CultureInfo.CurrentCulture), s.QueueLength.Source, Tooltip: Live.GetDefinition(MetricKeys.CpuQueue)?.Description),
            new("Context switches", s.ContextSwitchesPerSec.Value is { } cs ? $"{cs:N0}/s" : null, s.ContextSwitchesPerSec.Source, Tooltip: Live.GetDefinition(MetricKeys.CpuContextSwitches)?.Description),
            new("Interrupts", s.InterruptsPerSec.Value is { } ir ? $"{ir:N0}/s" : null, s.InterruptsPerSec.Source, Tooltip: Live.GetDefinition(MetricKeys.CpuInterrupts)?.Description),
            new("DPC time", s.DpcPercent.Value is { } d ? $"{d:F2}%" : null, s.DpcPercent.Source, Tooltip: Live.GetDefinition(MetricKeys.CpuDpc)?.Description),
            new("Processes / threads", Processes, "System counters"),
            new("Uptime", Uptime, "GetTickCount64"),
        ];

        for (var i = 0; i < Cores.Count && i < s.PerLogicalUtilization.Count; i++)
        {
            Cores[i].Utilization = s.PerLogicalUtilization[i];
            Cores[i].Text = $"{s.PerLogicalUtilization[i]:F0}%";
        }

        Analysis = Analyse(s, t?.Temp);
        if (RangeKey == ChartData.Live || ++_historyTick % 30 == 0) RefreshChart();
    }

    private string Analyse(CpuSnapshot s, double? temp)
    {
        if (s.Utilization.Value is not { } u) return "";
        var minute = Live.Get(MetricKeys.CpuUtil)?.Stats(DateTimeOffset.Now.AddMinutes(-1));
        var limit = Live.Get("cpu.perflimit")?.Stats(DateTimeOffset.Now.AddMinutes(-1));
        var text = minute is { N: > 10 } m ? $"Over the last minute the CPU averaged {m.Avg:F0}% (peak {m.Max:F0}%)" : $"The CPU is at {u:F0}%";
        text += s.EffectiveMhz.Value is { } mhz ? $" at an effective {UnitFormatter.Frequency(mhz)}." : ".";
        if (limit is { N: > 10, Avg: > 5 } l)
            text += $" Windows reports performance limited about {l.Avg:F0}% below nominal" + (minute is { Avg: > 50 } ? " while under load — evidence of power or thermal limiting." : " at light load, which usually reflects power-saving policy.");
        else if (limit is { N: > 10 })
            text += " No performance limitation is reported.";
        if (temp is null) text += " CPU package temperature isn't exposed through a documented interface on this PC.";
        return text;
    }

    private void RefreshChart()
    {
        var now = DateTimeOffset.Now;
        var live = RangeKey == ChartData.Live;
        var (from, to) = ChartData.Range(RangeKey, now);
        var model = new ChartModel { From = from, To = to, YMin = 0, YMax = 100 };
        model.Series.Add(new ChartSeries { Name = "Utilization", ColorIndex = 1, Fill = true, Points = ChartData.Points(MetricKeys.CpuUtil, from, to, live), Format = v => $"{v:F0}%" });
        if (ShowClock)
            model.Series.Add(new ChartSeries { Name = "Effective clock", ColorIndex = 2, OwnScale = true, Min = 0, Points = ChartData.Points(MetricKeys.CpuFreq, from, to, live), Format = v => UnitFormatter.Frequency(v) });
        if (ShowPower && P.Cpu.Latest.PackagePowerW.HasValue)
            model.Series.Add(new ChartSeries { Name = "Package power", ColorIndex = 3, OwnScale = true, Min = 0, Points = ChartData.Points(MetricKeys.CpuPower, from, to, live), Format = v => $"{v:F1} W" });
        if (ShowTemperature && Live.Keys.FirstOrDefault(k => k.StartsWith("thermal.", StringComparison.Ordinal) && !k.EndsWith(".passive", StringComparison.Ordinal)) is { } tk)
            model.Series.Add(new ChartSeries { Name = Live.GetDefinition(tk)?.Name ?? "Thermal zone", ColorIndex = 5, OwnScale = true, Points = ChartData.Points(tk, from, to, live), Format = v => U.Temperature(v) });
        model.Series.Add(new ChartSeries { Name = "Performance limitation", ColorIndex = 6, Dashed = true, Points = ChartData.Points("cpu.perflimit", from, to, live), Format = v => $"{v:F0}%" });
        if (!live)
            ChartData.Annotate(model, [EventCategory.WorkloadSession, EventCategory.Anomaly, EventCategory.Bugcheck, EventCategory.UnexpectedShutdown, EventCategory.Wake],
                k => k.StartsWith("cpu", StringComparison.Ordinal) || k.StartsWith("thermal.", StringComparison.Ordinal));
        Chart = model;
    }
}
