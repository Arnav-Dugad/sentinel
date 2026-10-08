using System.Globalization;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Interop;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// GPU discovery via DXGI, engine/memory utilisation via Windows GPU performance counters (works for every vendor),
/// and — for NVIDIA — temperatures, power and clocks via NVML's read-only queries.
/// On battery-powered systems a sleeping discrete GPU is never polled through NVML, because that would wake it.
/// </summary>
public sealed class GpuProvider(ILogger<GpuProvider> log) : WindowsProvider(log), IGpuTelemetryProvider
{
    private const string PdhSource = "Windows GPU performance counters";
    private const string NvmlSource = "NVIDIA NVML";
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromSeconds(15);

    private PdhQuery? _pdh;
    private PdhCounter? _engine, _dedicated, _shared;
    private Nvml? _nvml;
    private readonly Dictionary<string, IntPtr> _nvmlHandles = [];
    private readonly Dictionary<string, DateTimeOffset> _lastActive = [];
    private readonly Dictionary<string, (double Slowdown, double Shutdown)> _thresholds = [];
    private readonly Dictionary<string, double> _powerCeilings = [];
    private bool _hasBattery;
    private List<MetricDefinition> _pendingDefinitions = [];

    public override ProviderDescriptor Descriptor { get; } = Describe("gpu", "Graphics", "GPU", SamplingCost.Low, "1 s",
        "DXGI adapter enumeration", PdhSource, "WMI Win32_VideoController (driver)", NvmlSource + " (read-only, when installed)");

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

    public IReadOnlyList<GpuAdapter> Adapters { get; private set; } = [];
    public IReadOnlyList<GpuSnapshot> Latest { get; private set; } = [];
    public IReadOnlyDictionary<int, double> ProcessUtilization { get; private set; } = new Dictionary<int, double>();

    public override Task InitializeAsync(CancellationToken ct)
    {
        var dxgi = Dxgi.EnumerateAdapters();
        var controllers = Wmi.TryQuery(Wmi.Cimv2, "SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController", 16);
        _hasBattery = Native.GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 128 && ps.BatteryFlag != 255;

        var adapters = new List<GpuAdapter>();
        var seenIds = new Dictionary<string, int>();
        foreach (var d in dxgi)
        {
            var vendor = VendorName(d.VendorId);
            var baseId = $"{vendor.ToLowerInvariant()}-{d.DeviceId:x4}";
            seenIds[baseId] = seenIds.GetValueOrDefault(baseId) + 1;
            var id = $"{baseId}-{seenIds[baseId] - 1}";
            var token = $"VEN_{d.VendorId:X4}&DEV_{d.DeviceId:X4}";
            var wmi = controllers.FirstOrDefault(c => c.Str("PNPDeviceID")?.Contains(token, StringComparison.OrdinalIgnoreCase) == true);
            var kind = Classify(d);
            adapters.Add(new GpuAdapter(id, d.Description, vendor, d.VendorId, d.DeviceId, kind, (long)d.DedicatedVideoMemory, (long)d.SharedSystemMemory,
                wmi?.Str("DriverVersion"), wmi?.Date("DriverDate"), d.LuidKey, wmi?.Str("PNPDeviceID"), false, null));
        }

        if (adapters.Any(a => a.VendorId == 0x10DE))
        {
            try
            {
                _nvml = Nvml.TryLoad();
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                Log.LogInformation(ex, "NVML not available");
            }
            if (_nvml is not null)
            {
                var used = new HashSet<int>();
                for (var i = 0; i < adapters.Count; i++)
                {
                    var a = adapters[i];
                    if (a.VendorId != 0x10DE) continue;
                    for (var n = 0; n < _nvml.DeviceCount(); n++)
                    {
                        if (used.Contains(n)) continue;
                        var h = _nvml.Handle(n);
                        if (h == IntPtr.Zero || _nvml.Pci(h) is not { } pci || pci.Device != a.DeviceId) continue;
                        used.Add(n);
                        _nvmlHandles[a.Id] = h;
                        _thresholds[a.Id] = (_nvml.TemperatureThreshold(h, 1) ?? double.NaN, _nvml.TemperatureThreshold(h, 0) ?? double.NaN);
                        // Read alongside the thresholds in this one-time start-up query; used to reject impossible power readings.
                        _powerCeilings[a.Id] = PowerCeiling(_nvml.PowerLimitMilliwatts(h), a.Name);
                        adapters[i] = a with { VendorTelemetryAvailable = true, VendorTelemetrySource = NvmlSource };
                        break;
                    }
                }
            }
        }
        Adapters = adapters;

        _pdh = new PdhQuery();
        _engine = _pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
        _dedicated = _pdh.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _shared = _pdh.Add(@"\GPU Adapter Memory(*)\Shared Usage");
        _pdh.Collect();

        SetCapability("Adapters", adapters.Count > 0, "DXGI");
        SetCapability("Engine utilization", _engine is not null, PdhSource, _engine is null ? "GPU performance counters require a WDDM 2.x driver" : null);
        SetCapability("Temperature / power / clocks", _nvmlHandles.Count > 0, _nvmlHandles.Count > 0 ? NvmlSource : "None",
            _nvmlHandles.Count > 0 ? null : "No documented vendor telemetry interface is available for these adapters.");

        foreach (var a in adapters.Where(a => a.Kind != GpuKind.Software))
        {
            _pendingDefinitions.Add(new(MetricKeys.Gpu(a.Id, "util"), $"{ShortName(a)} utilization", MetricUnit.Percent, "GPU", "Busiest engine utilization for this adapter."));
            _pendingDefinitions.Add(new(MetricKeys.Gpu(a.Id, "mem"), $"{ShortName(a)} dedicated memory", MetricUnit.Bytes, "GPU", "Dedicated video memory in use.", PersistPolicy.Detail));
            if (_nvmlHandles.ContainsKey(a.Id))
            {
                _pendingDefinitions.Add(new(MetricKeys.Gpu(a.Id, "temp"), $"{ShortName(a)} temperature", MetricUnit.Celsius, "GPU", "GPU core temperature reported by NVML."));
                _pendingDefinitions.Add(new(MetricKeys.Gpu(a.Id, "power"), $"{ShortName(a)} power", MetricUnit.Watts, "GPU", "Board power draw reported by NVML."));
                _pendingDefinitions.Add(new(MetricKeys.Gpu(a.Id, "clock"), $"{ShortName(a)} core clock", MetricUnit.Megahertz, "GPU", "Graphics clock reported by NVML.", PersistPolicy.Detail));
            }
        }
        return Task.CompletedTask;
    }

    public static string ShortName(GpuAdapter a) => a.Name.Replace("NVIDIA ", "", StringComparison.Ordinal).Replace("GeForce ", "", StringComparison.Ordinal)
        .Replace("(R)", "", StringComparison.Ordinal).Replace("(TM)", "", StringComparison.Ordinal).Trim();

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        if (_pendingDefinitions.Count > 0)
        {
            foreach (var d in _pendingDefinitions) ctx.Metrics.Define(d);
            _pendingDefinitions = [];
        }
        var now = ctx.Now;
        _pdh?.Collect();

        // engine key (luid|phys|eng) -> (type, summed utilization across processes)
        var engines = new Dictionary<string, (string Luid, string Type, double Value)>();
        var perPid = new Dictionary<int, double>();
        if (_engine is not null)
        {
            foreach (var (instance, value) in _engine.Instances(noCap: true))
            {
                if (!TryParseEngine(instance, out var pid, out var luid, out var engineKey, out var type)) continue;
                var key = luid + "|" + engineKey;
                engines[key] = engines.TryGetValue(key, out var e) ? (e.Luid, e.Type, e.Value + value) : (luid, type, value);
                if (pid > 0) perPid[pid] = Math.Max(perPid.GetValueOrDefault(pid), value);
            }
        }
        ProcessUtilization = perPid;
        var dedicated = SumByLuid(_dedicated);
        var shared = SumByLuid(_shared);

        var snapshots = new List<GpuSnapshot>();
        double maxUtil = 0;
        double? maxTemp = null;
        foreach (var a in Adapters)
        {
            var mine = engines.Values.Where(e => e.Luid.Equals(a.LuidKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var byType = mine.GroupBy(e => NormalizeEngine(e.Type)).Select(g => new GpuEngineUsage(g.Key, Math.Clamp(g.Max(x => x.Value), 0, 100)))
                .OrderByDescending(x => x.Percent).ToList();
            var util = byType.Count > 0 ? byType.Max(x => x.Percent) : (double?)null;
            if (util > 0.5) _lastActive[a.Id] = now;
            if (a.Kind != GpuKind.Software && util is { } u) maxUtil = Math.Max(maxUtil, u);

            var snap = new GpuSnapshot(a.Id, now,
                Maybe(util, PdhSource, now, "GPU engine counters unavailable"), byType,
                dedicated.TryGetValue(a.LuidKey, out var ded) ? Good(ded, PdhSource, now) : Reading.Unavailable("Not exposed"),
                shared.TryGetValue(a.LuidKey, out var sh) ? Good(sh, PdhSource, now) : Reading.Unavailable("Not exposed"),
                NoVendor(a), NoVendor(a), NoVendor(a), NoVendor(a), NoVendor(a), NoVendor(a), NoVendor(a), NoVendor(a), NoVendor(a), NoVendor(a), null, false);

            if (_nvml is not null && _nvmlHandles.TryGetValue(a.Id, out var h))
            {
                var active = _lastActive.TryGetValue(a.Id, out var last) && now - last <= ActivityWindow;
                if (!_hasBattery || active)
                {
                    snap = ReadNvml(snap, h, a.Id, now);
                    if (snap.Temperature.Value is { } t) maxTemp = Math.Max(maxTemp ?? t, t);
                }
                else
                {
                    var sleeping = Reading.Unavailable("The GPU is idle in a low-power state. Sentinel does not wake it just to read sensors.", NvmlSource);
                    snap = snap with
                    {
                        Temperature = sleeping, HotspotTemperature = sleeping, PowerW = sleeping, CoreClockMhz = sleeping, MemoryClockMhz = sleeping,
                        FanPercent = sleeping, PerformanceState = sleeping, EncoderPercent = sleeping, DecoderPercent = sleeping, PowerLimitW = sleeping,
                        InLowPowerState = true,
                    };
                }
            }
            snapshots.Add(snap);

            if (a.Kind == GpuKind.Software) continue;
            if (util is { } uu) ctx.Metrics.Record(MetricKeys.Gpu(a.Id, "util"), uu, now);
            if (snap.DedicatedUsedBytes.Value is { } m) ctx.Metrics.Record(MetricKeys.Gpu(a.Id, "mem"), m, now);
            if (snap.Temperature.HasValue) ctx.Metrics.Record(MetricKeys.Gpu(a.Id, "temp"), snap.Temperature.Value!.Value, now);
            if (snap.PowerW.HasValue) ctx.Metrics.Record(MetricKeys.Gpu(a.Id, "power"), snap.PowerW.Value!.Value, now);
            if (snap.CoreClockMhz.HasValue) ctx.Metrics.Record(MetricKeys.Gpu(a.Id, "clock"), snap.CoreClockMhz.Value!.Value, now);
        }
        Latest = snapshots;
        if (_engine is not null) ctx.Metrics.Record(MetricKeys.GpuUtilAny, maxUtil, now);
        if (maxTemp is { } mt) ctx.Metrics.Record(MetricKeys.GpuTempAny, mt, now);
        return Task.CompletedTask;
    }

    public double? PowerCeilingW(string adapterId) => _powerCeilings.TryGetValue(adapterId, out var w) ? w : null;

    /// <summary>
    /// 1.25 × the enforced limit when NVML reports one. Many laptop GPUs do not, so laptop parts fall back to 200 W
    /// (no NVIDIA laptop GPU is rated above 175 W) and anything else to 1,000 W.
    /// </summary>
    public static double PowerCeiling(uint? enforcedLimitMilliwatts, string? name) =>
        enforcedLimitMilliwatts is > 1000 and < 2_000_000 ? enforcedLimitMilliwatts.Value / 1000.0 * 1.25
        : name?.Contains("Laptop", StringComparison.OrdinalIgnoreCase) == true ? 200
        : 1000;

    public (double? Slowdown, double? Shutdown) TemperatureLimits(string adapterId) =>
        _thresholds.TryGetValue(adapterId, out var t)
            ? (double.IsNaN(t.Slowdown) || t.Slowdown <= 0 ? null : t.Slowdown, double.IsNaN(t.Shutdown) || t.Shutdown <= 0 ? null : t.Shutdown)
            : (null, null);

    private GpuSnapshot ReadNvml(GpuSnapshot s, IntPtr h, string id, DateTimeOffset now)
    {
        var n = _nvml!;
        Reading R(double? v, string what) => v is { } x ? Good(x, NvmlSource, now) : Reading.Unavailable($"{what} is not reported by this GPU's driver", NvmlSource);
        // Drivers occasionally return garbage while a GPU is entering or leaving a low-power state (e.g. 590 W on a
        // 140 W laptop GPU). Physically impossible values are discarded rather than recorded.
        Reading Checked(double? v, string what, double min, double max) =>
            v is { } x && (x < min || x > max) ? Reading.Unavailable($"{what} reading was implausible and was discarded", NvmlSource) : R(v, what);
        var throttle = n.ThrottleReasons(h);
        var util = n.Util(h);
        var limitW = n.PowerLimitMilliwatts(h) / 1000.0;
        var maxPowerW = _powerCeilings.TryGetValue(id, out var ceiling) ? ceiling : 1000;
        return s with
        {
            Temperature = Checked(n.Temperature(h), "Temperature", 1, 130),
            HotspotTemperature = Reading.Unsupported("Hotspot temperature is not exposed through NVML's public API"),
            PowerW = Checked(n.PowerMilliwatts(h) / 1000.0, "Power draw", 0, maxPowerW),
            PowerLimitW = Checked(limitW, "Power limit", 1, 2000),
            CoreClockMhz = Checked(n.ClockMhz(h, 0), "Graphics clock", 0, 5000),
            MemoryClockMhz = Checked(n.ClockMhz(h, 2), "Memory clock", 0, 30000),
            FanPercent = Checked(n.FanPercent(h), "Fan speed", 0, 100),
            PerformanceState = R(n.PerformanceState(h), "Performance state"),
            EncoderPercent = R(n.EncoderPercent(h), "Encoder utilization"),
            DecoderPercent = R(n.DecoderPercent(h), "Decoder utilization"),
            Utilization = s.Utilization.HasValue ? s.Utilization : R(util?.Gpu, "Utilization"),
            ThrottleReasons = throttle is { } t ? Nvml.DescribeThrottle(t) : null,
            InLowPowerState = false,
        };
    }

    private static Reading NoVendor(GpuAdapter a) => a.VendorTelemetryAvailable
        ? Reading.None
        : Reading.Unsupported(a.VendorId switch
        {
            0x1002 or 0x1022 => "AMD sensors are not read: no documented read-only AMD interface is integrated yet.",
            0x8086 => "Intel GPU sensors are not read: no documented read-only Intel interface is integrated yet.",
            0x10DE => "NVIDIA NVML is not available (it is installed with the NVIDIA driver).",
            _ => "No documented vendor telemetry interface is available for this adapter.",
        });

    private Dictionary<string, double> SumByLuid(PdhCounter? counter)
    {
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (counter is null) return d;
        foreach (var (instance, value) in counter.Instances(noCap: true))
        {
            var i = instance.IndexOf("_phys", StringComparison.OrdinalIgnoreCase);
            var luid = i > 0 ? instance[..i] : instance;
            d[luid] = d.GetValueOrDefault(luid) + value;
        }
        return d;
    }

    /// <summary>Parses "pid_1234_luid_0x00000000_0x0000E5F6_phys_0_eng_3_engtype_3D".</summary>
    internal static bool TryParseEngine(string instance, out int pid, out string luid, out string engineKey, out string type)
    {
        pid = 0;
        luid = engineKey = type = "";
        var li = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        var pi = instance.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
        var ti = instance.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        if (li < 0 || pi < li || ti < pi) return false;
        luid = instance[li..pi];
        engineKey = instance[(pi + 1)..ti].TrimEnd('_');
        type = instance[(ti + 8)..];
        if (type.Length == 0) return false;
        if (instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
        {
            var end = instance.IndexOf('_', 4);
            if (end > 4) int.TryParse(instance.AsSpan(4, end - 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
        }
        return true;
    }

    internal static string NormalizeEngine(string type)
    {
        var t = type.Trim();
        var underscore = t.LastIndexOf('_');
        if (underscore > 0 && int.TryParse(t.AsSpan(underscore + 1), out _)) t = t[..underscore];
        return t switch
        {
            "VideoDecode" => "Video decode",
            "VideoEncode" => "Video encode",
            "VideoProcessing" => "Video processing",
            "LegacyOverlay" => "Overlay",
            _ => t,
        };
    }

    private static string VendorName(uint vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        0x5143 or 0x4D4F4351 => "Qualcomm",
        0x106B => "Apple",
        _ => $"Vendor 0x{vendorId:X4}",
    };

    private static GpuKind Classify(DxgiAdapterDesc d)
    {
        if (d.IsSoftware) return GpuKind.Software;
        if (d.VendorId == 0x1414) return GpuKind.Virtual;
        if (d.VendorId == 0x10DE) return GpuKind.Discrete;
        if (d.VendorId == 0x8086) return d.Description.Contains("Arc", StringComparison.OrdinalIgnoreCase) && d.DedicatedVideoMemory > 2UL << 30 ? GpuKind.Discrete : GpuKind.Integrated;
        if (d.VendorId is 0x1002 or 0x1022) return d.DedicatedVideoMemory >= 1UL << 30 ? GpuKind.Discrete : GpuKind.Integrated;
        return d.DedicatedVideoMemory >= 1UL << 30 ? GpuKind.Discrete : GpuKind.Integrated;
    }

    public override void OnSystemResumed()
    {
        _pdh?.Rebuild();
        _lastActive.Clear();
    }

    public override void Dispose()
    {
        _pdh?.Dispose();
        _nvml?.Dispose();
        base.Dispose();
    }
}
