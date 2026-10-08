using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sentinel.App.Controls;
using Sentinel.Core.Metrics;
using Sentinel.Core.Units;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Providers;

namespace Sentinel.App.ViewModels;

public sealed record AdapterChoice(string Id, string Name);

public sealed record EngineRow(string Name, double Percent, string Text);

public sealed partial class GpuViewModel : PageViewModel
{
    private int _historyTick;

    public ObservableCollection<AdapterChoice> AdapterChoices { get; } = [];
    public ObservableCollection<EngineRow> Engines { get; } = [];
    public ObservableCollection<AppRow> Apps { get; } = [];

    [ObservableProperty] public partial string? SelectedId { get; set; }
    [ObservableProperty] public partial string RangeKey { get; set; } = ChartData.Live;
    [ObservableProperty] public partial ChartModel? Chart { get; set; }
    [ObservableProperty] public partial string Utilization { get; set; } = "—";
    [ObservableProperty] public partial string Temperature { get; set; } = "—";
    [ObservableProperty] public partial string TemperatureCaption { get; set; } = "";
    [ObservableProperty] public partial string Power { get; set; } = "—";
    [ObservableProperty] public partial string PowerCaption { get; set; } = "";
    [ObservableProperty] public partial string Clock { get; set; } = "—";
    [ObservableProperty] public partial string ClockCaption { get; set; } = "";
    [ObservableProperty] public partial string Vram { get; set; } = "—";
    [ObservableProperty] public partial string VramCaption { get; set; } = "";
    [ObservableProperty] public partial string LowPowerNote { get; set; } = "";
    [ObservableProperty] public partial string Throttle { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Details { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Vendor { get; set; } = [];
    [ObservableProperty] public partial string Title { get; set; } = "";

    partial void OnSelectedIdChanged(string? value)
    {
        Refresh();
        RefreshChart();
    }

    partial void OnRangeKeyChanged(string value) => RefreshChart();

    protected override void OnActivated(object? parameter)
    {
        AdapterChoices.Clear();
        foreach (var a in P.Gpu.Adapters.Where(a => a.Kind != GpuKind.Software).OrderByDescending(a => a.Kind == GpuKind.Discrete))
            AdapterChoices.Add(new AdapterChoice(a.Id, GpuProvider.ShortName(a)));
        SelectedId ??= AdapterChoices.FirstOrDefault()?.Id;
    }

    protected override void Refresh()
    {
        var a = P.Gpu.Adapters.FirstOrDefault(x => x.Id == SelectedId);
        var s = P.Gpu.Latest.FirstOrDefault(x => x.AdapterId == SelectedId);
        if (a is null || s is null) return;
        Title = a.Name;
        Utilization = UnitFormatter.Percent(s.Utilization.Value);
        Temperature = s.Temperature.HasValue ? U.Temperature(s.Temperature.Value) : s.InLowPowerState ? "Resting" : "Not exposed";
        var limits = P.Gpu is GpuProvider gp ? gp.TemperatureLimits(a.Id) : (null, null);
        TemperatureCaption = s.Temperature.HasValue ? (limits.Slowdown is { } sl ? $"Driver slows down at {U.Temperature(sl)}" : s.Temperature.Source)
            : s.InLowPowerState ? "Not read while resting" : s.Temperature.Reason ?? "";
        Power = s.PowerW.HasValue ? UnitFormatter.Watts(s.PowerW.Value) : s.InLowPowerState ? "Resting" : "—";
        PowerCaption = s.PowerLimitW.HasValue ? $"Limit {UnitFormatter.Watts(s.PowerLimitW.Value, 0)}" : s.InLowPowerState ? "Not read while resting" : s.PowerW.Reason ?? "";
        Clock = s.CoreClockMhz.HasValue ? UnitFormatter.Frequency(s.CoreClockMhz.Value) : s.InLowPowerState ? "Resting" : "—";
        ClockCaption = s.MemoryClockMhz.HasValue ? $"Memory {UnitFormatter.Frequency(s.MemoryClockMhz.Value)}" : s.InLowPowerState ? "Not read while resting" : s.CoreClockMhz.Reason ?? "";
        Vram = s.DedicatedUsedBytes.HasValue ? U.Bytes(s.DedicatedUsedBytes.Value) : "—";
        VramCaption = a.DedicatedVideoMemory > 0 ? $"of {U.Bytes(a.DedicatedVideoMemory)} dedicated" + (s.SharedUsedBytes.HasValue ? $" · {U.Bytes(s.SharedUsedBytes.Value)} shared" : "") : "Uses shared system memory";
        LowPowerNote = s.InLowPowerState
            ? "This GPU is resting in a low-power state. To preserve battery life, Sentinel does not wake it just to read its sensors; readings resume as soon as something uses it."
            : "";
        Throttle = s.ThrottleReasons is { } t ? "The driver reports clock reductions: " + t + "." : "";

        var engines = s.Engines.Select(e => new EngineRow(e.EngineType, e.Percent, $"{e.Percent:F0}%")).ToList();
        if (!Engines.SequenceEqual(engines))
        {
            Engines.Clear();
            foreach (var e in engines) Engines.Add(e);
        }

        Details =
        [
            new("Adapter", a.Name, "DXGI"),
            new("Vendor", a.Vendor, "DXGI vendor ID"),
            new("Type", a.Kind switch { GpuKind.Integrated => "Integrated", GpuKind.Discrete => "Discrete", GpuKind.Virtual => "Virtual / basic display", _ => a.Kind.ToString() }, "Sentinel classification"),
            new("Dedicated memory", a.DedicatedVideoMemory > 0 ? U.Bytes(a.DedicatedVideoMemory) : "None", "DXGI"),
            new("Shared memory", U.Bytes(a.SharedSystemMemory), "DXGI"),
            new("Driver version", a.DriverVersion, "WMI Win32_VideoController"),
            new("Driver date", a.DriverDate?.ToString("d", CultureInfo.CurrentCulture), "WMI Win32_VideoController"),
            new("Device ID", $"VEN_{a.VendorId:X4} DEV_{a.DeviceId:X4}", "DXGI"),
        ];
        Vendor =
        [
            new("Vendor telemetry", a.VendorTelemetryAvailable ? a.VendorTelemetrySource : "Not available", "Capability check"),
            new("Performance state", s.PerformanceState.HasValue ? $"P{s.PerformanceState.Value:F0}" : null, s.PerformanceState.Source, Tooltip: "P0 is maximum performance; higher numbers are lower-power states."),
            new("Fan", s.FanPercent.HasValue ? $"{s.FanPercent.Value:F0}% of maximum" : null, s.FanPercent.Source),
            new("Video encoder", s.EncoderPercent.HasValue ? $"{s.EncoderPercent.Value:F0}%" : null, s.EncoderPercent.Source),
            new("Video decoder", s.DecoderPercent.HasValue ? $"{s.DecoderPercent.Value:F0}%" : null, s.DecoderPercent.Source),
            new("Hotspot temperature", s.HotspotTemperature.HasValue ? U.Temperature(s.HotspotTemperature.Value) : null, s.HotspotTemperature.Reason),
            new("Slowdown threshold", limits.Slowdown is { } sd ? U.Temperature(sd) : null, "NVML (read-only)"),
            new("Shutdown threshold", limits.Shutdown is { } sh ? U.Temperature(sh) : null, "NVML (read-only)"),
        ];

        var apps = P.Processes.Latest.Apps.Where(x => x.GpuPercent >= 0.5).OrderByDescending(x => x.GpuPercent).Take(6)
            .Select(x => new AppRow(x.DisplayName, x.Publisher ?? "", UnitFormatter.Percent(x.CpuPercent, 1), U.Bytes(x.PrivateBytes), UnitFormatter.Percent(x.GpuPercent), "", x.GpuPercent)).ToList();
        if (!Apps.SequenceEqual(apps))
        {
            Apps.Clear();
            foreach (var x in apps) Apps.Add(x);
        }

        if (RangeKey == ChartData.Live || ++_historyTick % 30 == 0) RefreshChart();
    }

    private void RefreshChart()
    {
        if (SelectedId is not { } id) return;
        var now = DateTimeOffset.Now;
        var live = RangeKey == ChartData.Live;
        var (from, to) = ChartData.Range(RangeKey, now);
        var model = new ChartModel { From = from, To = to, YMin = 0, YMax = 100 };
        model.Series.Add(new ChartSeries { Name = "Utilization", ColorIndex = 4, Fill = true, Points = ChartData.Points(MetricKeys.Gpu(id, "util"), from, to, live), Format = v => $"{v:F0}%" });
        var temp = ChartData.Points(MetricKeys.Gpu(id, "temp"), from, to, live);
        if (temp.Count > 0) model.Series.Add(new ChartSeries { Name = "Temperature", ColorIndex = 5, OwnScale = true, Points = temp, Format = v => U.Temperature(v) });
        var power = ChartData.Points(MetricKeys.Gpu(id, "power"), from, to, live);
        if (power.Count > 0) model.Series.Add(new ChartSeries { Name = "Power", ColorIndex = 3, OwnScale = true, Min = 0, Points = power, Format = v => $"{v:F0} W" });
        if (!live) ChartData.Annotate(model, [EventCategory.DisplayDriverReset, EventCategory.WorkloadSession, EventCategory.DriverChanged], k => k.StartsWith("gpu.", StringComparison.Ordinal));
        Chart = model;
    }
}
