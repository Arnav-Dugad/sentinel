using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Interop;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// Battery telemetry from GetSystemPowerStatus (Windows' own percentage and AC state) and the battery device
/// IOCTLs (capacities, cycle count, chemistry, charge/discharge rate). Supports multiple batteries.
/// </summary>
public sealed class BatteryProvider(ILogger<BatteryProvider> log) : WindowsProvider(log), IBatteryTelemetryProvider
{
    private const string IoctlSource = "Windows battery device interface";
    private const string PowerSource = "GetSystemPowerStatus";
    private static readonly TimeSpan StaticRefresh = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, BatteryStaticInfo> _static = [];
    private DateTimeOffset _lastStatic = DateTimeOffset.MinValue;
    private List<string> _paths = [];

    public override ProviderDescriptor Descriptor { get; } = Describe("battery", "Battery", "Battery", SamplingCost.Negligible, "Firmware-reported (typically 1–60 s update rate)",
        IoctlSource, PowerSource);

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    public BatterySnapshot Latest { get; private set; } = BatterySnapshot.Empty;

    public override Task InitializeAsync(CancellationToken ct)
    {
        _paths = BatteryIoctl.EnumeratePaths();
        RefreshStatic();
        var hasSystemBattery = Native.GetSystemPowerStatus(out var ps) && ps.BatteryFlag is not 128 and not 255;
        SetCapability("Battery", _paths.Count > 0 || hasSystemBattery, IoctlSource, _paths.Count == 0 && !hasSystemBattery ? "No system battery present" : null);
        SetCapability("Capacities and cycle count", _static.Values.Any(s => !s.CapacityIsRelative && s.DesignedCapacity > 0), IoctlSource,
            _static.Count == 0 ? "No battery device interface" : null);
        return Task.CompletedTask;
    }

    private void RefreshStatic()
    {
        _static.Clear();
        foreach (var path in _paths)
        {
            using var h = BatteryIoctl.Open(path);
            if (h.IsInvalid) continue;
            var tag = BatteryIoctl.QueryTag(h);
            if (tag == 0) continue;
            if (BatteryIoctl.QueryStatic(h, tag) is { } info) _static[path] = info;
        }
        _lastStatic = DateTimeOffset.Now;
    }

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var now = ctx.Now;
        if (now - _lastStatic > StaticRefresh) RefreshStatic();
        if (!Native.GetSystemPowerStatus(out var ps)) throw new InvalidOperationException("GetSystemPowerStatus failed");
        var ac = ps.ACLineStatus == 1;
        var saver = ps.SystemStatusFlag == 1;
        var hasBattery = ps.BatteryFlag is not 128 and not 255 && (_static.Count > 0 || ps.BatteryLifePercent != 255);
        if (!hasBattery)
        {
            Latest = BatterySnapshot.Empty with { Timestamp = now, Present = false, AcOnline = true, State = ChargeState.NoBattery };
            ctx.Metrics.Record(MetricKeys.AcOnline, 1, now);
            return Task.CompletedTask;
        }

        long remaining = 0, full = 0, design = 0;
        long rate = 0;
        double voltage = 0;
        var anyRate = false;
        var absolute = true;
        var charging = false;
        var discharging = false;
        var batteries = new List<BatteryInfo>();
        foreach (var (path, info) in _static)
        {
            using var h = BatteryIoctl.Open(path);
            if (h.IsInvalid) continue;
            var st = BatteryIoctl.QueryStatus(h, info.Tag);
            var temp = BatteryIoctl.QueryTemperature(h, info.Tag);
            absolute &= !info.CapacityIsRelative;
            if (st is not null)
            {
                if (st.Capacity != BatteryStatusInfo.UNKNOWN) remaining += st.Capacity;
                if (st.Rate != BatteryStatusInfo.UNKNOWN_RATE)
                {
                    rate += st.Rate;
                    anyRate = true;
                }
                if (st.Voltage != BatteryStatusInfo.UNKNOWN) voltage = Math.Max(voltage, st.Voltage);
                charging |= (st.PowerState & BatteryStatusInfo.BATTERY_CHARGING) != 0;
                discharging |= (st.PowerState & BatteryStatusInfo.BATTERY_DISCHARGING) != 0;
            }
            full += info.FullChargedCapacity;
            design += info.DesignedCapacity;
            batteries.Add(new BatteryInfo(BatteryId(info), info.DeviceName, info.Manufacturer, info.Chemistry,
                info.CapacityIsRelative ? null : info.DesignedCapacity, info.CapacityIsRelative ? null : info.FullChargedCapacity,
                info.CycleCount > 0 ? (int)info.CycleCount : null, info.ManufactureDate, info.SerialNumber,
                temp is { } t ? Good(t, IoctlSource, now) : Reading.Unsupported("The battery firmware does not report temperature")));
        }

        var percent = ps.BatteryLifePercent <= 100 ? ps.BatteryLifePercent : (full > 0 ? remaining * 100.0 / full : (double?)null);
        var state = charging ? ChargeState.Charging
            : discharging ? ChargeState.Discharging
            : ac && percent >= 95 ? ChargeState.Full
            : ac ? ChargeState.Idle
            : ChargeState.Discharging;
        var timeLeft = ps.BatteryLifeTime != uint.MaxValue && !ac ? Reading.Estimated(ps.BatteryLifeTime, "Windows estimate", now) : Reading.Unavailable(ac ? "On AC power" : "Windows has no estimate yet");
        var capSource = absolute ? IoctlSource : "Relative capacity only";

        Latest = new BatterySnapshot(now, true, ac, state,
            Maybe(percent, PowerSource, now, "Unknown"),
            absolute && remaining > 0 ? Good(remaining, IoctlSource, now) : Reading.Unavailable("Battery reports relative capacity only", capSource),
            absolute && full > 0 ? Good(full, IoctlSource, now) : Reading.Unavailable("Not reported", capSource),
            absolute && design > 0 ? Good(design, IoctlSource, now) : Reading.Unavailable("Not reported", capSource),
            anyRate && absolute ? Good(rate, IoctlSource, now) : Reading.Unavailable("Battery does not report its charge/discharge rate"),
            voltage > 0 ? Good(voltage, IoctlSource, now) : Reading.Unavailable("Not reported"),
            timeLeft, saver, batteries);

        if (percent is { } p) ctx.Metrics.Record(MetricKeys.BatPercent, p, now);
        if (anyRate && absolute) ctx.Metrics.Record(MetricKeys.BatRate, rate / 1000.0, now);
        if (absolute && remaining > 0) ctx.Metrics.Record(MetricKeys.BatCapacity, remaining, now);
        if (absolute && full > 0) ctx.Metrics.Record(MetricKeys.BatFullCharge, full, now);
        if (voltage > 0) ctx.Metrics.Record(MetricKeys.BatVoltage, voltage, now);
        ctx.Metrics.Record(MetricKeys.AcOnline, ac ? 1 : 0, now);
        return Task.CompletedTask;
    }

    private static string BatteryId(BatteryStaticInfo info) =>
        "bat-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            (info.DeviceName ?? "") + (info.Manufacturer ?? "") + (info.SerialNumber ?? "") + info.DesignedCapacity)), 0, 4).ToLowerInvariant();

    public override void OnSystemResumed() => _lastStatic = DateTimeOffset.MinValue;
}
