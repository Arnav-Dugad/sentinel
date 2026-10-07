using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Interop;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// Passive network monitoring: adapter statistics from the IP Helper API (via NetworkInterface) and Wi-Fi link
/// details from the WLAN API. Generates no network traffic.
/// </summary>
public sealed class NetworkProvider(ILogger<NetworkProvider> log) : WindowsProvider(log), INetworkTelemetryProvider
{
    private const string IpHelper = "IP Helper API";
    private const string WlanSource = "Native Wi-Fi API";
    private static readonly TimeSpan PropertiesRefresh = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, (long Rx, long Tx, DateTimeOffset At)> _previous = [];
    private readonly Dictionary<string, (IReadOnlyList<string> V4, IReadOnlyList<string> V6, IReadOnlyList<string> Gw, IReadOnlyList<string> Dns, bool? Dhcp, DateTimeOffset At)> _props = [];
    private readonly Dictionary<string, bool> _lastUp = [];
    private static readonly TimeSpan WifiRefresh = TimeSpan.FromSeconds(5);
    private bool _wlanAvailable = true;
    private bool _definedWifi;
    private List<WlanConnection>? _wifi;
    private DateTimeOffset _wifiAt = DateTimeOffset.MinValue;

    public override ProviderDescriptor Descriptor { get; } = Describe("network", "Network", "Network", SamplingCost.Low, "1 s", IpHelper, WlanSource);

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

    public NetworkSnapshot Latest { get; private set; } = NetworkSnapshot.Empty;

    public override Task InitializeAsync(CancellationToken ct)
    {
        SetCapability("Adapter statistics", true, IpHelper);
        var wlan = Wlan.Query();
        _wlanAvailable = wlan is not null;
        SetCapability("Wi-Fi link details", wlan is { Count: > 0 }, WlanSource,
            wlan is null ? "No wireless LAN service" : wlan.Count == 0 ? "No Wi-Fi adapter" : null);
        return Task.CompletedTask;
    }

    // Lightweight filter drivers (QoS, WFP, Npcap…) appear as extra "interfaces" that mirror a real adapter's traffic.
    private static readonly string[] PseudoTokens =
    [
        "WAN Miniport", "Kernel Debug", "Filter", "Teredo", "6to4", "ISATAP", "QoS Packet Scheduler", "WFP ", "Npcap", "Packet Scheduler", "-0000",
    ];

    internal static bool IsRelevant(NetworkInterface n)
    {
        if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
        return !PseudoTokens.Any(t => n.Description.Contains(t, StringComparison.OrdinalIgnoreCase) || n.Name.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVirtual(NetworkInterface n) =>
        n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
        || n.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
        || n.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
        || n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) || n.Name.Contains("NordLynx", StringComparison.OrdinalIgnoreCase)
        || n.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var now = ctx.Now;
        if (_wlanAvailable && now - _wifiAt >= WifiRefresh)
        {
            _wifi = Wlan.Query();
            _wifiAt = now;
        }
        var wifi = _wifi;
        var adapters = new List<NetworkAdapterSnapshot>();
        double totalRx = 0, totalTx = 0;

        foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(IsRelevant))
        {
            IPInterfaceStatistics stats;
            try
            {
                stats = n.GetIPStatistics();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            var id = n.Id;
            var up = n.OperationalStatus == OperationalStatus.Up;
            double rxRate = 0, txRate = 0;
            if (_previous.TryGetValue(id, out var prev))
            {
                var dt = (now - prev.At).TotalSeconds;
                if (dt > 0.2 && stats.BytesReceived >= prev.Rx && stats.BytesSent >= prev.Tx)
                {
                    rxRate = (stats.BytesReceived - prev.Rx) / dt;
                    txRate = (stats.BytesSent - prev.Tx) / dt;
                }
            }
            _previous[id] = (stats.BytesReceived, stats.BytesSent, now);
            if (up && !IsVirtual(n))
            {
                totalRx += rxRate;
                totalTx += txRate;
            }

            if (!_props.TryGetValue(id, out var p) || now - p.At > PropertiesRefresh) p = _props[id] = ReadProperties(n, now);

            var kind = n.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "Ethernet",
                NetworkInterfaceType.Ppp => "PPP",
                NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Cellular",
                _ => "Other",
            };
            if (IsVirtual(n)) kind = n.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ? "Bluetooth PAN" : "Virtual";

            WifiInfo? wi = null;
            if (kind == "Wi-Fi" && wifi is not null && Guid.TryParse(id, out var g) && wifi.FirstOrDefault(w => w.InterfaceGuid == g) is { } w && w.Connected)
            {
                wi = new WifiInfo(w.Ssid,
                    Good(w.SignalQuality, WlanSource, now),
                    w.RssiDbm is { } r ? Good(r, WlanSource, now) : Reading.Unavailable("Not reported"),
                    Wlan.PhyName(w.PhyType),
                    w.Channel is { } c ? Good(c, WlanSource, now) : Reading.Unavailable("Not reported"),
                    Wlan.Band(w.CenterFrequencyKhz, w.Channel),
                    Good(w.RxRateKbps / 1000.0, WlanSource, now),
                    Good(w.TxRateKbps / 1000.0, WlanSource, now),
                    Wlan.AuthName(w.AuthAlgorithm));
                if (!_definedWifi)
                {
                    _definedWifi = true;
                    SetCapability("Wi-Fi link details", true, WlanSource);
                }
                ctx.Metrics.Record(MetricKeys.WifiSignal, w.SignalQuality, now);
                if (w.RssiDbm is { } rssi) ctx.Metrics.Record(MetricKeys.WifiRssi, rssi, now);
            }
            else if (kind == "Wi-Fi" && up && Wlan.LastError == Native.ERROR_ACCESS_DENIED)
            {
                SetCapability("Wi-Fi link details", false, WlanSource, "Windows requires location permission for apps to read Wi-Fi connection details.");
            }

            // Ethernet link changes are recorded here; Wi-Fi connects/disconnects come from the WLAN event log with reasons.
            if (kind == "Ethernet" && _lastUp.TryGetValue(id, out var wasUp) && wasUp != up)
            {
                ctx.Events.Publish(new SystemEvent(now, up ? EventCategory.NetworkConnected : EventCategory.NetworkDisconnected, up ? Severity.Info : Severity.Notice,
                    up ? $"{n.Name} connected" : $"{n.Name} disconnected", n.Description, IpHelper, n.Name, $"eth-{id}-{now.ToUnixTimeSeconds()}"));
            }
            _lastUp[id] = up;

            string? mac = null;
            try
            {
                var bytes = n.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length == 6) mac = string.Join("-", bytes.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
            }
            catch (NetworkInformationException)
            {
            }

            adapters.Add(new NetworkAdapterSnapshot(id, Core.Privacy.Redactor.SanitizeUntrusted(n.Name, 64), Core.Privacy.Redactor.SanitizeUntrusted(n.Description, 128),
                kind, up, Math.Max(0, n.Speed), stats.BytesReceived, stats.BytesSent, rxRate, txRate, stats.UnicastPacketsReceived, stats.UnicastPacketsSent,
                stats.IncomingPacketsWithErrors, stats.OutgoingPacketsWithErrors, stats.IncomingPacketsDiscarded, stats.OutgoingPacketsDiscarded,
                p.V4, p.V6, p.Gw, p.Dns, p.Dhcp, mac, wi));
        }

        Latest = new NetworkSnapshot(now, adapters.OrderByDescending(a => a.IsUp).ThenBy(a => a.Kind == "Virtual").ToList(), totalRx, totalTx,
            NetworkInterface.GetIsNetworkAvailable());
        ctx.Metrics.Record(MetricKeys.NetRx, totalRx, now);
        ctx.Metrics.Record(MetricKeys.NetTx, totalTx, now);
        return Task.CompletedTask;
    }

    private static (IReadOnlyList<string>, IReadOnlyList<string>, IReadOnlyList<string>, IReadOnlyList<string>, bool?, DateTimeOffset) ReadProperties(NetworkInterface n, DateTimeOffset now)
    {
        try
        {
            var p = n.GetIPProperties();
            var v4 = p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList();
            var v6 = p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6).Select(a => a.Address.ToString()).ToList();
            var gw = p.GatewayAddresses.Select(a => a.Address.ToString()).Where(s => s != "0.0.0.0").ToList();
            var dns = p.DnsAddresses.Select(a => a.ToString()).ToList();
            bool? dhcp = null;
            try
            {
                dhcp = p.GetIPv4Properties()?.IsDhcpEnabled;
            }
            catch (NetworkInformationException)
            {
            }
            return (v4, v6, gw, dns, dhcp, now);
        }
        catch (NetworkInformationException)
        {
            return ([], [], [], [], null, now);
        }
    }

    public override void OnSystemResumed()
    {
        _previous.Clear();
        _props.Clear();
    }
}
