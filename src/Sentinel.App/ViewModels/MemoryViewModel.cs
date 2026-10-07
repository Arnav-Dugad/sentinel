using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Sentinel.Analytics;
using Sentinel.App.Controls;
using Sentinel.App.Services;
using Sentinel.Core.Metrics;
using Sentinel.Core.Settings;
using Sentinel.Core.Units;
using Sentinel.Domain;

namespace Sentinel.App.ViewModels;

public sealed record ModuleRow(string Slot, string Capacity, string Type, string Speed, string Manufacturer, string PartNumber, string Serial);

public sealed partial class MemoryViewModel : PageViewModel
{
    private int _historyTick;

    public ObservableCollection<AppRow> Apps { get; } = [];
    public ObservableCollection<ModuleRow> Modules { get; } = [];
    public ObservableCollection<string> Growth { get; } = [];
    public SensitiveInfoState Sensitive { get; } = App.Services.GetRequiredService<SensitiveInfoState>();

    [ObservableProperty] public partial string RangeKey { get; set; } = ChartData.Live;
    [ObservableProperty] public partial ChartModel? Chart { get; set; }
    [ObservableProperty] public partial string InUse { get; set; } = "—";
    [ObservableProperty] public partial string InUseCaption { get; set; } = "";
    [ObservableProperty] public partial string Available { get; set; } = "—";
    [ObservableProperty] public partial string Committed { get; set; } = "—";
    [ObservableProperty] public partial string CommittedCaption { get; set; } = "";
    [ObservableProperty] public partial string Cached { get; set; } = "—";
    [ObservableProperty] public partial string HardFaults { get; set; } = "—";
    [ObservableProperty] public partial GridLength InUseWidth { get; set; } = new(1, GridUnitType.Star);
    [ObservableProperty] public partial GridLength StandbyWidth { get; set; } = new(0, GridUnitType.Star);
    [ObservableProperty] public partial GridLength FreeWidth { get; set; } = new(1, GridUnitType.Star);
    [ObservableProperty] public partial string CompositionText { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Details { get; set; } = [];
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string AppsNote { get; set; } = "";
    [ObservableProperty] public partial string ModulesNote { get; set; } = "";

    partial void OnRangeKeyChanged(string value) => RefreshChart();

    protected override void OnActivated(object? parameter)
    {
        Sensitive.PropertyChanged += OnSensitiveChanged;
        BuildModules();
    }

    protected override void OnDeactivated() => Sensitive.PropertyChanged -= OnSensitiveChanged;

    private void OnSensitiveChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => BuildModules();

    private void BuildModules()
    {
        Modules.Clear();
        foreach (var m in P.Memory.Inventory.Modules)
            Modules.Add(new ModuleRow(string.IsNullOrWhiteSpace(m.Slot) ? "—" : m.Slot, U.Bytes(m.CapacityBytes, 0), m.MemoryType ?? m.FormFactor ?? "—",
                m.ConfiguredSpeedMts is { } c ? $"{c} MT/s" + (m.SpeedMts is { } s && s != c ? $" (rated {s})" : "") : "—", m.Manufacturer ?? "—", m.PartNumber ?? "—", Sensitive.Mask(m.SerialNumber)));
        var inv = P.Memory.Inventory;
        ModulesNote = inv.Modules.Count == 0 ? "This system's firmware does not report memory modules."
            : inv.TotalSlots is { } slots ? $"{inv.Modules.Count} of {slots} slots populated." : $"{inv.Modules.Count} module(s).";
    }

    protected override void Refresh()
    {
        var m = P.Memory.Latest;
        if (m.TotalBytes == 0) return;
        InUse = U.Bytes(m.UsedBytes);
        InUseCaption = $"{m.UsedPercent:F0}% of {U.Bytes(m.TotalBytes)}";
        Available = U.Bytes(m.AvailableBytes);
        Committed = U.Bytes(m.CommittedBytes);
        CommittedCaption = $"of {U.Bytes(m.CommitLimitBytes)} limit ({m.CommitPercent:F0}%)";
        Cached = U.Bytes(m.CachedBytes);
        HardFaults = m.HardFaultsPerSec.HasValue ? $"{m.HardFaultsPerSec.Value:N0}/s" : "—";

        var standby = Math.Min(m.StandbyBytes.Value ?? 0, m.AvailableBytes);
        var free = Math.Max(0, m.AvailableBytes - standby);
        InUseWidth = new GridLength(Math.Max(0.001, m.UsedBytes), GridUnitType.Star);
        StandbyWidth = new GridLength(Math.Max(0.001, standby), GridUnitType.Star);
        FreeWidth = new GridLength(Math.Max(0.001, free), GridUnitType.Star);
        CompositionText = $"In use {U.Bytes(m.UsedBytes)} · Standby (reclaimable cache) {U.Bytes(standby)} · Free {U.Bytes(free)}";

        Details =
        [
            new("Installed", U.Bytes(P.Memory.Inventory.InstalledBytes), "GetPhysicallyInstalledSystemMemory"),
            new("Usable by Windows", U.Bytes(m.TotalBytes), "GlobalMemoryStatusEx", Tooltip: "Installed memory minus what firmware and integrated graphics reserve."),
            new("Committed", $"{U.Bytes(m.CommittedBytes)} / {U.Bytes(m.CommitLimitBytes)}", "GetPerformanceInfo", Tooltip: Live.GetDefinition(MetricKeys.MemCommit)?.Description),
            new("Cached", U.Bytes(m.CachedBytes), "GetPerformanceInfo", Tooltip: Live.GetDefinition(MetricKeys.MemCached)?.Description),
            new("Paged pool", U.Bytes(m.PagedPoolBytes), "GetPerformanceInfo", Tooltip: "Kernel memory that can be written to the page file."),
            new("Non-paged pool", U.Bytes(m.NonPagedPoolBytes), "GetPerformanceInfo", Tooltip: "Kernel memory that must stay in RAM. Steady growth over days can indicate a driver leak."),
            new("Page faults", m.PageFaultsPerSec.HasValue ? $"{m.PageFaultsPerSec.Value:N0}/s" : null, m.PageFaultsPerSec.Source, Tooltip: "All page faults, most of which are resolved from RAM."),
            new("Hard faults", HardFaults, m.HardFaultsPerSec.Source, Tooltip: Live.GetDefinition(MetricKeys.MemHardFaults)?.Description),
            new("Page file in use", m.PageFileUsagePercent.HasValue ? $"{m.PageFileUsagePercent.Value:F0}%" : null, m.PageFileUsagePercent.Source),
            new("Compressed memory", null, m.CompressedBytes.Reason),
        ];

        var press = m.UsedPercent > 90 && (m.HardFaultsPerSec.Value ?? 0) > 300;
        var baseline = Services.GetRequiredService<BaselineEngine>().Get(MetricKeys.MemUsedPct, BaselineContext.All);
        Summary = press
            ? "Memory is under pressure: most RAM is in use and Windows is reading pages back from disk, which slows apps."
            : $"{m.UsedPercent:F0}% of memory is in use" + (baseline is { IsMature: true } b ? $", compared with a typical {b.Median:F0}% on this PC." : ".")
              + " Windows keeps spare RAM filled with cache and releases it instantly when needed, so high 'in use' alone is not a problem.";

        if (Services.GetRequiredService<ISettingsStore>().Current.PrivacyMode)
        {
            Apps.Clear();
            AppsNote = "Privacy mode is on: application details are hidden.";
        }
        else
        {
            AppsNote = "";
            var apps = P.Processes.Latest.Apps.OrderByDescending(a => a.PrivateBytes).Take(8)
                .Select(a => new AppRow(a.DisplayName, a.Publisher ?? "", "", U.Bytes(a.PrivateBytes), "", $"{a.MemoryShareOfApps * 100:F0}% of app memory · {a.ProcessCount} process(es)", a.PrivateBytes)).ToList();
            if (!Apps.SequenceEqual(apps))
            {
                Apps.Clear();
                foreach (var a in apps) Apps.Add(a);
            }
        }

        var growth = Services.GetRequiredService<AnomalyEngine>().Active.Where(a => a.MetricKey.EndsWith(".mem", StringComparison.Ordinal)).Select(a => a.Description).ToList();
        if (!Growth.SequenceEqual(growth))
        {
            Growth.Clear();
            foreach (var g in growth) Growth.Add(g);
        }

        if (RangeKey == ChartData.Live || ++_historyTick % 30 == 0) RefreshChart();
    }

    private void RefreshChart()
    {
        var now = DateTimeOffset.Now;
        var live = RangeKey == ChartData.Live;
        var (from, to) = ChartData.Range(RangeKey, now);
        var model = new ChartModel { From = from, To = to, YMin = 0, YMax = 100 };
        model.Series.Add(new ChartSeries { Name = "In use", ColorIndex = 2, Fill = true, Points = ChartData.Points(MetricKeys.MemUsedPct, from, to, live), Format = v => $"{v:F0}%" });
        model.Series.Add(new ChartSeries { Name = "Committed", ColorIndex = 4, OwnScale = true, Min = 0, Points = ChartData.Points(MetricKeys.MemCommit, from, to, live), Format = v => U.Bytes(v) });
        model.Series.Add(new ChartSeries { Name = "Hard faults", ColorIndex = 3, OwnScale = true, Min = 0, Points = ChartData.Points(MetricKeys.MemHardFaults, from, to, live), Format = v => $"{v.ToString("N0", CultureInfo.CurrentCulture)}/s" });
        if (!live) ChartData.Annotate(model, [EventCategory.WorkloadSession, EventCategory.Anomaly], k => k.StartsWith("mem", StringComparison.Ordinal) || k.EndsWith(".mem", StringComparison.Ordinal));
        Chart = model;
    }
}
