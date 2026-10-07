using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.App.Controls;
using Sentinel.App.Services;
using Sentinel.Core.Metrics;
using Sentinel.Core.Settings;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;
using Sentinel.Intelligence;

namespace Sentinel.App.ViewModels;

public sealed partial class HomeViewModel : PageViewModel
{
    private readonly IntelligenceService _intelligence = App.Services.GetRequiredService<IntelligenceService>();
    private readonly HistoryStore _store = App.Services.GetRequiredService<HistoryStore>();
    private readonly ISettingsStore _settings = App.Services.GetRequiredService<ISettingsStore>();
    private int _slowTick;

    public HomeViewModel()
    {
        Tiles =
        [
            new HomeTile { Label = "CPU", Glyph = "", Target = "CPU", ColorIndex = 1, SparkMax = 100 },
            new HomeTile { Label = "GPU", Glyph = "", Target = "GPU", ColorIndex = 4, SparkMax = 100 },
            new HomeTile { Label = "Memory", Glyph = "", Target = "Memory", ColorIndex = 2, SparkMax = 100 },
            new HomeTile { Label = "Battery", Glyph = "", Target = "Battery", ColorIndex = 3, SparkMax = 100 },
            new HomeTile { Label = "Storage", Glyph = "", Target = "Storage", ColorIndex = 6 },
            new HomeTile { Label = "Network", Glyph = "", Target = "Network", ColorIndex = 5 },
        ];
    }

    public ObservableCollection<HomeTile> Tiles { get; }
    public ObservableCollection<EventRow> RecentEvents { get; } = [];
    public ObservableCollection<HealthChip> HealthChips { get; } = [];
    public ObservableCollection<AppRow> TopApps { get; } = [];
    public ObservableCollection<InsightRow> Insights { get; } = [];

    [ObservableProperty] public partial string Greeting { get; set; } = "";
    [ObservableProperty] public partial string Headline { get; set; } = "Getting to know your PC…";
    [ObservableProperty] public partial string Detail { get; set; } = "Sentinel is collecting its first readings.";
    [ObservableProperty] public partial HealthStatus Overall { get; set; } = HealthStatus.Unknown;
    [ObservableProperty] public partial string InsightText { get; set; } = "Sentinel is gathering readings. Insights appear as history builds up.";
    [ObservableProperty] public partial string InsightConfidence { get; set; } = "";
    [ObservableProperty] public partial string AppsNote { get; set; } = "";
    [ObservableProperty] public partial bool HasEvents { get; set; }

    protected override void Refresh()
    {
        var now = DateTimeOffset.Now;
        Greeting = now.Hour switch { < 5 => "Good evening", < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" };
        UpdateTiles(now);
        if (_slowTick++ % 5 == 0) UpdateIntelligence(now);
    }

    private void UpdateTiles(DateTimeOffset now)
    {
        var p = P;
        var u = U;
        IReadOnlyList<ChartPoint> Spark(string key) =>
            Live.Get(key)?.Since(now.AddMinutes(-2)).Select(x => new ChartPoint(x.Timestamp, x.Value)).ToList() ?? [];

        var cpu = Tiles[0];
        cpu.Value = UnitFormatter.Percent(p.Cpu.Latest.Utilization.Value);
        var temp = LiveSummary.CpuAreaTemperature(p);
        cpu.Caption = string.Join(" • ", new[] { temp is { } t ? u.Temperature(t.Temp) : null, UnitFormatter.Frequency(p.Cpu.Latest.EffectiveMhz.Value) }.Where(s => s is not null && s != "—"));
        cpu.Provenance = "Utilization: " + p.Cpu.Latest.Utilization.Source + (temp is { } tt ? "\nTemperature: " + tt.Source : "\nCPU package temperature: not exposed by Windows");
        cpu.Spark = Spark(MetricKeys.CpuUtil);

        var gpu = Tiles[1];
        var gpus = p.Gpu.Latest.Where(g => p.Gpu.Adapters.FirstOrDefault(a => a.Id == g.AdapterId)?.Kind is GpuKind.Discrete or GpuKind.Integrated or GpuKind.External).ToList();
        gpu.Visible = gpus.Count > 0;
        if (gpus.Count > 0)
        {
            var busiest = gpus.OrderByDescending(g => g.Utilization.Value ?? 0).First();
            gpu.Value = UnitFormatter.Percent(busiest.Utilization.Value);
            var gt = gpus.Where(g => g.Temperature.HasValue).Select(g => g.Temperature.Value).Max();
            gpu.Caption = gt is { } g2 ? u.Temperature(g2) : gpus.Any(g => g.InLowPowerState) ? "Discrete GPU resting" : p.Gpu.Adapters.FirstOrDefault(a => a.Id == busiest.AdapterId)?.Name ?? "";
            gpu.Provenance = "Utilization: Windows GPU counters" + (gt is not null ? "\nTemperature: NVIDIA NVML" : "");
            gpu.Spark = Spark(MetricKeys.GpuUtilAny);
        }

        var mem = Tiles[2];
        var m = p.Memory.Latest;
        mem.Value = m.TotalBytes > 0 ? $"{u.Bytes(m.UsedBytes)} / {u.Bytes(m.TotalBytes, 0)}" : "—";
        mem.Caption = m.TotalBytes > 0 ? $"{m.UsedPercent:F0}% in use • {m.CommitPercent:F0}% committed" : "";
        mem.Provenance = "GlobalMemoryStatusEx / GetPerformanceInfo";
        mem.Spark = Spark(MetricKeys.MemUsedPct);

        var bat = Tiles[3];
        var b = p.Battery.Latest;
        bat.Visible = b.Present;
        if (b.Present)
        {
            bat.Value = UnitFormatter.Percent(b.Percent.Value);
            var rate = b.RateMilliwatts.Value is { } mw && Math.Abs(mw) > 100 ? $"{Math.Abs(mw) / 1000:F1} W" : null;
            var state = b.State switch { ChargeState.Charging => "Charging", ChargeState.Discharging => "Discharging", ChargeState.Full => "Fully charged", _ => "Plugged in" };
            bat.Caption = rate is null ? state : $"{state} • {rate}";
            bat.Provenance = "Battery firmware via Windows battery IOCTLs";
            bat.Spark = Spark(MetricKeys.BatPercent);
        }

        var st = Tiles[4];
        var sys = p.Storage.Volumes.FirstOrDefault(v => v.IsSystem);
        var disk = sys?.DiskNumber is { } dn ? p.Storage.Disks.FirstOrDefault(d => d.Number == dn) : p.Storage.Disks.FirstOrDefault();
        var health = disk is not null && p.Storage.Health.TryGetValue(disk.Id, out var h) ? h : null;
        st.Value = health?.Temperature.Value is { } dt ? u.Temperature(dt) : sys is not null ? u.Bytes(sys.FreeBytes, 0) + " free" : "—";
        st.Caption = string.Join(" • ", new[] { health?.HealthStatus, sys is not null && health?.Temperature.Value is not null ? u.Bytes(sys.FreeBytes, 0) + " free" : null }.Where(s => s is not null));
        st.Provenance = disk is null ? "" : $"{disk.Model}\nHealth: Storage Management API\nTemperature: {health?.Temperature.Source}";
        st.Spark = Spark(MetricKeys.DiskActive);

        var net = Tiles[5];
        net.Value = "↓ " + u.Throughput(p.Network.Latest.TotalRxBytesPerSec);
        net.Caption = "↑ " + u.Throughput(p.Network.Latest.TotalTxBytesPerSec) + (p.Network.Latest.Adapters.FirstOrDefault(a => a.IsUp && a.Wifi is not null)?.Wifi is { } w ? $" • Wi-Fi {w.SignalQuality.Value:F0}%" : "");
        net.Provenance = "IP Helper API (passive; Sentinel sends no traffic)";
        net.Spark = Spark(MetricKeys.NetRx);
    }

    private void UpdateIntelligence(DateTimeOffset now)
    {
        if (_intelligence.Health is { } h)
        {
            Headline = h.Headline;
            Detail = h.Detail;
            Overall = h.Overall;
            Sync(HealthChips, h.Categories.Select(c => new HealthChip(c.Name, c.Summary, c.Status)).ToList());
        }
        if (_intelligence.Headline is { } ins)
        {
            InsightText = ins.Summary;
            InsightConfidence = ins.Confidence.Label();
        }
        Sync(Insights, _intelligence.Insights.Take(4).Select(i => new InsightRow(i)).ToList());

        // Home shows notable events only; routine ones (plug-in, reconnects, Store app updates, known device problems) live on their own pages.
        var events = _store.QueryEvents(now.AddDays(-3), now, null, 200)
            .Where(e => e.Category is not (EventCategory.PowerSource or EventCategory.NetworkConnected or EventCategory.DeviceProblem or EventCategory.Sleep
                or EventCategory.Wake or EventCategory.DeviceConnected or EventCategory.DeviceDisconnected))
            .Where(e => !(e.Category == EventCategory.SoftwareInstalled && e.Title.EndsWith("(Microsoft Store)", StringComparison.Ordinal)))
            .Take(7).Select(e => new EventRow(e)).ToList();
        Sync(RecentEvents, events);
        HasEvents = events.Count > 0;

        if (_settings.Current.PrivacyMode)
        {
            TopApps.Clear();
            AppsNote = "Privacy mode is on: application details are hidden.";
        }
        else
        {
            var apps = P.Processes.Latest.Apps.Take(5).Select(a => new AppRow(a.DisplayName, a.Publisher ?? "", UnitFormatter.Percent(a.CpuPercent, 1), U.Bytes(a.PrivateBytes),
                UnitFormatter.Percent(a.GpuPercent), $"{a.CpuShareOfActive * 100:F0}% of active CPU • {a.MemoryShareOfApps * 100:F0}% of app memory", a.CpuPercent)).ToList();
            Sync(TopApps, apps);
            AppsNote = apps.Count == 0 ? "Collecting process information…" : "";
        }
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        if (target.SequenceEqual(source)) return;
        target.Clear();
        foreach (var s in source) target.Add(s);
    }
}
