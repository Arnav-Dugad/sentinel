using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.App.Controls;
using Sentinel.Core.Metrics;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Intelligence;

namespace Sentinel.App.ViewModels;

public sealed partial class DriveCard : ObservableObject
{
    public required string Id { get; init; }
    public required string Model { get; init; }
    public required string Subtitle { get; init; }

    [ObservableProperty] public partial HealthStatus Status { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string Temperature { get; set; } = "—";
    [ObservableProperty] public partial string Endurance { get; set; } = "—";
    [ObservableProperty] public partial string Read { get; set; } = "—";
    [ObservableProperty] public partial string Write { get; set; } = "—";
    [ObservableProperty] public partial string Active { get; set; } = "—";
    [ObservableProperty] public partial string Latency { get; set; } = "—";
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Health { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<ChartPoint> UsedHistory { get; set; } = [];
    [ObservableProperty] public partial string HistoryNote { get; set; } = "";
}

public sealed record VolumeRow(string Name, string Label, string FileSystem, double UsedPercent, string Space, string Encryption, string Role);

public sealed partial class StorageViewModel : PageViewModel
{
    private int _historyTick;
    private int _slow;

    public ObservableCollection<DriveCard> Drives { get; } = [];
    public ObservableCollection<VolumeRow> Volumes { get; } = [];

    [ObservableProperty] public partial string RangeKey { get; set; } = ChartData.Live;
    [ObservableProperty] public partial ChartModel? Chart { get; set; }

    partial void OnRangeKeyChanged(string value) => RefreshChart();

    protected override void OnActivated(object? parameter) => Build();

    private void Build()
    {
        Drives.Clear();
        foreach (var d in P.Storage.Disks)
            Drives.Add(new DriveCard
            {
                Id = d.Id,
                Model = d.Model,
                Subtitle = $"{d.BusType} {d.MediaType} · {U.Capacity(d.SizeBytes)}" + (d.IsSystemDisk ? " · System drive" : "") + (d.VolumeNames.Count > 0 ? " · " + string.Join(", ", d.VolumeNames) : ""),
            });
        Volumes.Clear();
        foreach (var v in P.Storage.Volumes)
            Volumes.Add(new VolumeRow(v.Name, string.IsNullOrWhiteSpace(v.Label) ? "Local disk" : v.Label!, v.FileSystem ?? "", v.TotalBytes > 0 ? (v.TotalBytes - v.FreeBytes) * 100.0 / v.TotalBytes : 0,
                $"{U.Bytes(v.FreeBytes)} free of {U.Bytes(v.TotalBytes)}", v.Encryption ?? "Encryption state not reported", v.IsSystem ? "Windows" : v.DriveType));
        LoadHistory();
    }

    private void LoadHistory()
    {
        var store = Services.GetRequiredService<HistoryStore>();
        foreach (var card in Drives)
        {
            var hist = store.QueryDiskHealth(card.Id).Where(h => h.PercentUsed is not null).ToList();
            card.UsedHistory = hist.Select(h => new ChartPoint(h.Day, h.PercentUsed!.Value)).ToList();
            card.HistoryNote = hist.Count >= 2
                ? $"Percentage Used went from {hist[0].PercentUsed:F0}% on {hist[0].Day:d} to {hist[^1].PercentUsed:F0}% on {hist[^1].Day:d}."
                : "Sentinel records drive health daily; trends appear after a few days.";
        }
    }

    protected override void Refresh()
    {
        if (Drives.Count != P.Storage.Disks.Count) Build();
        foreach (var card in Drives)
        {
            P.Storage.Health.TryGetValue(card.Id, out var h);
            var io = P.Storage.Io.FirstOrDefault(i => i.DiskId == card.Id);
            card.Read = U.Throughput(io?.ReadBytesPerSec.Value);
            card.Write = U.Throughput(io?.WriteBytesPerSec.Value);
            card.Active = UnitFormatter.Percent(io?.ActivePercent.Value);
            card.Latency = io?.LatencyMs.Value is { } l ? $"{l:F2} ms" : "—";
            if (h is null) continue;
            card.Temperature = h.Temperature.HasValue ? U.Temperature(h.Temperature.Value) : "Not reported";
            card.Endurance = h.PercentageUsed.HasValue ? $"{h.PercentageUsed.Value:F0}% used" : "Not reported";
            var critical = h.CriticalWarning is > 0 || h.HealthStatus == "Unhealthy" || (h.AvailableSpare.Value is { } sp && h.AvailableSpareThreshold.Value is { } th && sp <= th);
            card.Status = critical ? HealthStatus.Critical : h.HealthStatus == "Warning" ? HealthStatus.Attention : h.HealthStatus == "Healthy" ? HealthStatus.Excellent : HealthStatus.Unknown;
            card.StatusText = critical ? "Critical" : h.HealthStatus ?? "Not reported";
            card.Summary = critical
                ? "The drive reports a critical condition. Back up important data and contact the manufacturer."
                : h.PercentageUsed.Value is { } pu
                    ? $"Healthy. About {pu:F0}% of the drive's rated write endurance has been used" + (h.AvailableSpare.Value is { } spare ? $"; spare capacity is {spare:F0}%." : ".")
                    : h.HealthStatus is "Healthy" ? "Windows reports this drive as healthy. Detailed endurance data is not exposed to standard queries." : "Detailed health is not exposed for this drive.";
            card.Health =
            [
                new("Health (Windows)", h.HealthStatus, "Storage Management API"),
                new("Temperature", h.Temperature.HasValue ? U.Temperature(h.Temperature.Value) : null, h.Temperature.Source,
                    Tooltip: h.WarningTemperature.HasValue ? $"Drive-reported warning temperature {U.Temperature(h.WarningTemperature.Value)}, critical {U.Temperature(h.CriticalTemperature.Value)}" : null),
                new("Percentage used", h.PercentageUsed.HasValue ? $"{h.PercentageUsed.Value:F0}%" : null, h.PercentageUsed.Source,
                    Tooltip: "An NVMe health indicator estimating how much of the drive's rated endurance has been consumed. It can exceed 100% on drives that keep working."),
                new("Available spare", h.AvailableSpare.HasValue ? $"{h.AvailableSpare.Value:F0}% (threshold {h.AvailableSpareThreshold.Value:F0}%)" : null, h.AvailableSpare.Source,
                    Tooltip: "Reserve capacity used to replace worn blocks. Falling below the threshold is a critical condition."),
                new("Critical warning", h.CriticalWarning is { } cw ? Platform.Windows.Interop.NvmeHealthLog.DescribeCriticalWarning((byte)cw) : null, h.Source),
                new("Data written", h.DataWrittenBytes.HasValue ? U.Bytes(h.DataWrittenBytes.Value) : null, h.DataWrittenBytes.Source),
                new("Data read", h.DataReadBytes.HasValue ? U.Bytes(h.DataReadBytes.Value) : null, h.DataReadBytes.Source),
                new("Power-on hours", h.PowerOnHours.HasValue ? $"{h.PowerOnHours.Value:N0} h" : null, h.PowerOnHours.Source),
                new("Power cycles", h.PowerCycles.HasValue ? $"{h.PowerCycles.Value:N0}" : null, h.PowerCycles.Source),
                new("Unsafe shutdowns", h.UnsafeShutdowns.HasValue ? $"{h.UnsafeShutdowns.Value:N0}" : null, h.UnsafeShutdowns.Source,
                    Tooltip: "Times the drive lost power without being told to flush first, such as during a forced power-off."),
                new("Media errors", h.MediaErrors.HasValue ? $"{h.MediaErrors.Value:N0}" : null, h.MediaErrors.Source,
                    Tooltip: "Unrecovered data-integrity errors over the drive's life. Any non-zero value is worth watching."),
            ];
        }
        if (_slow++ % 60 == 0) LoadHistory();
        if (RangeKey == ChartData.Live || ++_historyTick % 30 == 0) RefreshChart();
    }

    private void RefreshChart()
    {
        var now = DateTimeOffset.Now;
        var live = RangeKey == ChartData.Live;
        var (from, to) = ChartData.Range(RangeKey, now);
        var model = new ChartModel { From = from, To = to, YMin = 0 };
        model.Series.Add(new ChartSeries { Name = "Read", ColorIndex = 2, Fill = true, Points = ChartData.Points(MetricKeys.DiskRead, from, to, live), Format = v => U.Throughput(v) });
        model.Series.Add(new ChartSeries { Name = "Write", ColorIndex = 3, Points = ChartData.Points(MetricKeys.DiskWrite, from, to, live), Format = v => U.Throughput(v) });
        model.Series.Add(new ChartSeries { Name = "Active time", ColorIndex = 6, OwnScale = true, Min = 0, Max = 100, Dashed = true, Points = ChartData.Points(MetricKeys.DiskActive, from, to, live), Format = v => $"{v:F0}%" });
        if (!live) ChartData.Annotate(model, [EventCategory.StorageError, EventCategory.UpdateInstalled], k => k.StartsWith("disk", StringComparison.Ordinal));
        Chart = model;
    }
}
