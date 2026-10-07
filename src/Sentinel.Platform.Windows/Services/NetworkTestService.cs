using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace Sentinel.Platform.Windows.Services;

public sealed record LatencyResult(string Target, string Label, int Sent, int Received, double? MinMs, double? AvgMs, double? MaxMs, double? JitterMs)
{
    public double LossPercent => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;
}

public sealed record NetworkTestResult(DateTimeOffset Timestamp, IReadOnlyList<LatencyResult> Latency, double? DnsMs, string? DnsError);

/// <summary>
/// Active network test. Only runs when the user presses the button: it pings the default gateway and
/// Microsoft's connectivity-check host, and times one DNS lookup. Nothing is sent continuously.
/// </summary>
public sealed class NetworkTestService
{
    public const string InternetHost = "www.msftconnecttest.com";

    public async Task<NetworkTestResult> RunAsync(int pings, CancellationToken ct)
    {
        var results = new List<LatencyResult>();
        var gateway = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        if (gateway is not null) results.Add(await PingAsync(gateway.ToString(), "Router (default gateway)", pings, ct).ConfigureAwait(false));

        double? dnsMs = null;
        string? dnsError = null;
        IPAddress? internet = null;
        var sw = Stopwatch.StartNew();
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(InternetHost, ct).ConfigureAwait(false);
            dnsMs = sw.Elapsed.TotalMilliseconds;
            internet = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            dnsError = ex.SocketErrorCode.ToString();
        }
        if (internet is not null) results.Add(await PingAsync(internet.ToString(), "Internet (Microsoft connectivity check)", pings, ct).ConfigureAwait(false));
        return new NetworkTestResult(DateTimeOffset.Now, results, dnsMs, dnsError);
    }

    private static async Task<LatencyResult> PingAsync(string target, string label, int count, CancellationToken ct)
    {
        using var ping = new Ping();
        var times = new List<double>();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(target, 1000).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch (PingException)
            {
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        double? jitter = times.Count > 1 ? times.Zip(times.Skip(1), (a, b) => Math.Abs(b - a)).Average() : null;
        return new LatencyResult(target, label, count, times.Count, times.Count > 0 ? times.Min() : null, times.Count > 0 ? times.Average() : null,
            times.Count > 0 ? times.Max() : null, jitter);
    }
}
