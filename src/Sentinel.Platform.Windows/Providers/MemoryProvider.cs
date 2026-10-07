using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Interop;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>Memory from GlobalMemoryStatusEx/GetPerformanceInfo (cheap) plus memory performance counters; DIMMs via SMBIOS-backed WMI.</summary>
public sealed class MemoryProvider(ILogger<MemoryProvider> log) : WindowsProvider(log), IMemoryTelemetryProvider
{
    private const string Win32 = "GlobalMemoryStatusEx / GetPerformanceInfo";
    private const string Pdh = "Windows memory performance counters";

    private PdhQuery? _pdh;
    private PdhCounter? _standbyNormal, _standbyReserve, _standbyCore, _faults, _pageReads, _pageFile;

    public override ProviderDescriptor Descriptor { get; } = Describe("memory", "Memory", "Memory", SamplingCost.Negligible, "1 s",
        Win32, Pdh, "WMI Win32_PhysicalMemory (SMBIOS)");

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

    public MemoryInventory Inventory { get; private set; } = MemoryInventory.Empty;
    public MemorySnapshot Latest { get; private set; } = MemorySnapshot.Empty;

    public override Task InitializeAsync(CancellationToken ct)
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref status);
        var installed = Native.GetPhysicallyInstalledSystemMemory(out var kb) ? (long)kb * 1024 : (long)status.ullTotalPhys;

        var modules = new List<MemoryModule>();
        foreach (var r in Wmi.TryQuery(Wmi.Cimv2,
                     "SELECT Manufacturer, Capacity, Speed, ConfiguredClockSpeed, FormFactor, DeviceLocator, BankLabel, PartNumber, SerialNumber, Attributes, SMBIOSMemoryType FROM Win32_PhysicalMemory", 64))
        {
            modules.Add(new MemoryModule(
                Clean(r.Str("Manufacturer")),
                r.Long("Capacity") ?? 0,
                (int?)r.Long("Speed"),
                (int?)r.Long("ConfiguredClockSpeed"),
                FormFactor(r.Long("FormFactor")),
                string.Join(" ", new[] { r.Str("BankLabel"), r.Str("DeviceLocator") }.Where(s => !string.IsNullOrWhiteSpace(s))),
                Clean(r.Str("PartNumber")),
                r.Long("Attributes") is > 0 and < 16 ? (int?)r.Long("Attributes") : null,
                MemoryType(r.Long("SMBIOSMemoryType")),
                Clean(r.Str("SerialNumber"))));
        }
        int? slots = null;
        var arr = Wmi.TryQuery(Wmi.Cimv2, "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray", 4);
        if (arr.Count > 0) slots = (int?)arr.Sum(a => a.Long("MemoryDevices") ?? 0);
        Inventory = new MemoryInventory(installed, (long)status.ullTotalPhys, modules, slots is > 0 ? slots : null);

        _pdh = new PdhQuery();
        _standbyNormal = _pdh.Add(@"\Memory\Standby Cache Normal Priority Bytes");
        _standbyReserve = _pdh.Add(@"\Memory\Standby Cache Reserve Bytes");
        _standbyCore = _pdh.Add(@"\Memory\Standby Cache Core Bytes");
        _faults = _pdh.Add(@"\Memory\Page Faults/sec");
        _pageReads = _pdh.Add(@"\Memory\Page Reads/sec");
        _pageFile = _pdh.Add(@"\Paging File(_Total)\% Usage");
        _pdh.Collect();

        SetCapability("Physical memory", true, Win32);
        SetCapability("Memory modules", modules.Count > 0, "SMBIOS via WMI", modules.Count == 0 ? "Firmware does not report memory modules" : null);
        SetCapability("Hard faults", _pageReads is not null, Pdh);
        SetCapability("Compressed memory", false, "None", "Windows does not expose the compressed store size through a documented counter.");
        return Task.CompletedTask;
    }

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var now = ctx.Now;
        var status = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("GlobalMemoryStatusEx failed");
        Native.GetPerformanceInfo(out var perf, (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>());
        var page = (long)perf.PageSize;
        _pdh?.Collect();

        double? standby = _standbyNormal?.Value() is { } a ? a + (_standbyReserve?.Value() ?? 0) + (_standbyCore?.Value() ?? 0) : null;
        var total = (long)status.ullTotalPhys;
        var avail = (long)status.ullAvailPhys;
        Latest = new MemorySnapshot(now, total, avail, (long)perf.CommitTotal * page, (long)perf.CommitLimit * page, (long)perf.SystemCache * page,
            Maybe(standby, Pdh, now, "Standby counters unavailable"), (long)perf.KernelPaged * page, (long)perf.KernelNonpaged * page,
            Reading.Unsupported("Not exposed through a documented counter"),
            Maybe(_faults?.Value(), Pdh, now, "Not exposed"),
            Maybe(_pageReads?.Value(), Pdh + " (Page Reads/sec)", now, "Not exposed"),
            Maybe(_pageFile?.Value(), Pdh, now, "No page file"));

        ctx.Metrics.Record(MetricKeys.MemUsedPct, Latest.UsedPercent, now);
        ctx.Metrics.Record(MetricKeys.MemUsed, Latest.UsedBytes, now);
        ctx.Metrics.Record(MetricKeys.MemCommit, Latest.CommittedBytes, now);
        ctx.Metrics.Record(MetricKeys.MemCached, Latest.CachedBytes, now);
        if (Latest.HardFaultsPerSec.Value is { } hf) ctx.Metrics.Record(MetricKeys.MemHardFaults, hf, now);
        if (Latest.PageFileUsagePercent.Value is { } pf) ctx.Metrics.Record(MetricKeys.MemPageFile, pf, now);
        return Task.CompletedTask;
    }

    public override void OnSystemResumed() => _pdh?.Rebuild();

    public override void Dispose()
    {
        _pdh?.Dispose();
        base.Dispose();
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s is "Unknown" or "Undefined" or "0000" or "00000000" ? null : s;
    }

    private static string? FormFactor(long? v) => v switch
    {
        8 => "DIMM",
        12 => "SODIMM",
        13 => "SRIMM",
        null or 0 => null,
        _ => "Other",
    };

    private static string? MemoryType(long? v) => v switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null,
    };
}
