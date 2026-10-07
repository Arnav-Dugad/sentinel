using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.App.Controls;
using Sentinel.App.Services;
using Sentinel.Core.Metrics;
using Sentinel.Data;
using Sentinel.Domain;

namespace Sentinel.App.ViewModels;

public sealed partial class AdapterCard : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial HealthStatus Status { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string Down { get; set; } = "—";
    [ObservableProperty] public partial string Up { get; set; } = "—";
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Details { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Wifi { get; set; } = [];
    [ObservableProperty] public partial bool HasWifi { get; set; }
}

public sealed partial class NetworkViewModel : PageViewModel
{
    private int _historyTick;
    private int _slow;

    public ObservableCollection<AdapterCard> Adapters { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];
    public SensitiveInfoState Sensitive { get; } = App.Services.GetRequiredService<SensitiveInfoState>();

    [ObservableProperty] public partial string RangeKey { get; set; } = ChartData.Live;
    [ObservableProperty] public partial ChartModel? Chart { get; set; }
    [ObservableProperty] public partial bool ShowInactive { get; set; }
    [ObservableProperty] public partial string LocationHint { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";

    partial void OnRangeKeyChanged(string value) => RefreshChart();
    partial void OnShowInactiveChanged(bool value) => Refresh();

    protected override void Refresh()
    {
        var snap = P.Network.Latest;
        var visible = snap.Adapters.Where(a => ShowInactive || a.IsUp).ToList();
        if (Adapters.Count != visible.Count || !Adapters.Select(a => a.Id).SequenceEqual(visible.Select(a => a.Id)))
        {
            Adapters.Clear();
            foreach (var a in visible) Adapters.Add(new AdapterCard { Id = a.Id });
        }
        foreach (var card in Adapters)
        {
            var a = visible.First(x => x.Id == card.Id);
            card.Name = a.Name;
            card.Subtitle = $"{a.Kind} · {a.Description}";
            card.Status = a.IsUp ? HealthStatus.Good : HealthStatus.Unknown;
            card.StatusText = a.IsUp ? "Connected" : "Not connected";
            card.Down = "↓ " + U.Throughput(a.RxBytesPerSec);
            card.Up = "↑ " + U.Throughput(a.TxBytesPerSec);
            card.Details =
            [
                new("Link speed", a.LinkSpeedBps > 0 ? U.LinkSpeed(a.LinkSpeedBps) : null, "IP Helper API", Tooltip: "Negotiated link rate, not actual internet speed."),
                new("IPv4", a.IPv4.Count > 0 ? string.Join(", ", a.IPv4) : null, "IP Helper API", Sensitive: true),
                new("IPv6", a.IPv6.Count > 0 ? string.Join(", ", a.IPv6.Take(2)) : null, "IP Helper API", Sensitive: true),
                new("Gateway", a.Gateways.Count > 0 ? string.Join(", ", a.Gateways) : null, "IP Helper API", Sensitive: true),
                new("DNS servers", a.DnsServers.Count > 0 ? string.Join(", ", a.DnsServers.Take(3)) : null, "IP Helper API", Sensitive: true),
                new("Address assignment", a.DhcpEnabled switch { true => "Automatic (DHCP)", false => "Manual", _ => null }, "IP Helper API"),
                new("MAC address", a.MacAddress, "IP Helper API", Sensitive: true),
                new("Errors (in / out)", $"{a.InErrors:N0} / {a.OutErrors:N0}", "IP Helper API", Tooltip: "Since the adapter started. Non-zero values that keep growing can indicate a cable, signal or driver problem."),
                new("Discarded (in / out)", $"{a.InDiscards:N0} / {a.OutDiscards:N0}", "IP Helper API"),
                new("Received / sent since start", $"{U.Bytes(a.BytesReceived)} / {U.Bytes(a.BytesSent)}", "IP Helper API"),
            ];
            card.HasWifi = a.Wifi is not null;
            if (a.Wifi is { } w)
            {
                card.Wifi =
                [
                    new("Network name (SSID)", w.Ssid, "Native Wi-Fi API", Sensitive: true),
                    new("Signal quality", w.SignalQuality.HasValue ? $"{w.SignalQuality.Value:F0}%" : null, w.SignalQuality.Source),
                    new("Signal strength", w.RssiDbm.HasValue ? $"{w.RssiDbm.Value:F0} dBm" : null, w.RssiDbm.Source, Tooltip: Live.GetDefinition(MetricKeys.WifiRssi)?.Description),
                    new("Standard", w.PhyType, "Native Wi-Fi API"),
                    new("Band / channel", $"{w.Band ?? "—"} · channel {w.Channel.Value:F0}", "Native Wi-Fi API"),
                    new("Link rate (receive / transmit)", w.RxRateMbps.HasValue ? $"{w.RxRateMbps.Value:F0} / {w.TxRateMbps.Value:F0} Mbps" : null, "Native Wi-Fi API"),
                    new("Security", w.Authentication, "Native Wi-Fi API"),
                ];
            }
        }

        var wifiCap = P.Network.Capabilities.FirstOrDefault(c => c.Name == "Wi-Fi link details");
        LocationHint = wifiCap is { Available: false } && wifiCap.Reason?.Contains("location", StringComparison.OrdinalIgnoreCase) == true ? wifiCap.Reason : "";
        Summary = $"Passive monitoring only: Sentinel reads adapter counters and generates no traffic. Now ↓ {U.Throughput(snap.TotalRxBytesPerSec)} · ↑ {U.Throughput(snap.TotalTxBytesPerSec)}.";

        if (_slow++ % 15 == 0)
        {
            var now = DateTimeOffset.Now;
            var events = Services.GetRequiredService<HistoryStore>().QueryEvents(now.AddDays(-2), now, [EventCategory.NetworkConnected, EventCategory.NetworkDisconnected], 40)
                .Select(e => new EventRow(e)).ToList();
            if (!Events.SequenceEqual(events))
            {
                Events.Clear();
                foreach (var e in events) Events.Add(e);
            }
        }
        if (RangeKey == ChartData.Live || ++_historyTick % 30 == 0) RefreshChart();
    }

    private void RefreshChart()
    {
        var now = DateTimeOffset.Now;
        var live = RangeKey == ChartData.Live;
        var (from, to) = ChartData.Range(RangeKey, now);
        var model = new ChartModel { From = from, To = to, YMin = 0 };
        model.Series.Add(new ChartSeries { Name = "Download", ColorIndex = 5, Fill = true, Points = ChartData.Points(MetricKeys.NetRx, from, to, live), Format = v => U.Throughput(v) });
        model.Series.Add(new ChartSeries { Name = "Upload", ColorIndex = 1, Points = ChartData.Points(MetricKeys.NetTx, from, to, live), Format = v => U.Throughput(v) });
        var signal = ChartData.Points(MetricKeys.WifiSignal, from, to, live);
        if (signal.Count > 0) model.Series.Add(new ChartSeries { Name = "Wi-Fi signal", ColorIndex = 2, OwnScale = true, Min = 0, Max = 100, Dashed = true, Points = signal, Format = v => $"{v:F0}%" });
        if (!live) ChartData.Annotate(model, [EventCategory.NetworkDisconnected, EventCategory.Wake], k => k.StartsWith("net", StringComparison.Ordinal));
        Chart = model;
    }
}
