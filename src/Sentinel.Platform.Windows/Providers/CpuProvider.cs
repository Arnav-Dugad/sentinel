using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Interop;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// CPU telemetry from Windows performance counters (Processor Information set, which is what Task Manager uses)
/// and topology from GetLogicalProcessorInformationEx. No MSR access; no driver.
/// </summary>
public sealed class CpuProvider(ILogger<CpuProvider> log) : WindowsProvider(log), ICpuTelemetryProvider
{
    private const string PdhSource = "Windows performance counters";
    private const string NoTemp = "Windows does not expose CPU package temperature through a documented API. ACPI thermal zones (if any) are shown on the Thermals page.";

    private PdhQuery? _pdh;
    private PdhCounter? _utilTotal, _utilPerCore, _perf, _freq, _queue, _ctxsw, _interrupts, _dpc, _processes, _threads, _perfLimit, _energy;
    private double? _baseMhz;
    private int[] _groupOffsets = [0];

    public override ProviderDescriptor Descriptor { get; } = Describe("cpu", "Processor", "CPU", SamplingCost.Low, "1 s counter resolution",
        PdhSource, "GetLogicalProcessorInformationEx", "WMI Win32_Processor (inventory)", "Windows Energy Meter counters (if present)");

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

    public CpuInventory Inventory { get; private set; } = CpuInventory.Empty;

    public CpuSnapshot Latest { get; private set; } = CpuSnapshot.Empty;

    public override Task InitializeAsync(CancellationToken ct)
    {
        var topo = CpuTopology.Read();
        string name = "Unknown processor", vendor = "Unknown";
        double? regMhz = null;
        using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
        {
            if (key is not null)
            {
                name = Sanitize(key.GetValue("ProcessorNameString") as string) ?? name;
                vendor = (key.GetValue("VendorIdentifier") as string) switch
                {
                    "GenuineIntel" => "Intel",
                    "AuthenticAMD" => "AMD",
                    { } v => Sanitize(v) ?? vendor,
                    _ => vendor,
                };
                if (key.GetValue("~MHz") is int mhz) regMhz = mhz;
            }
        }

        double? maxMhz = null;
        bool? virt = null;
        var wmi = Wmi.TryQuery(Wmi.Cimv2, "SELECT MaxClockSpeed, VirtualizationFirmwareEnabled, Manufacturer FROM Win32_Processor", 4);
        if (wmi.Count > 0)
        {
            maxMhz = wmi[0].Long("MaxClockSpeed");
            virt = wmi[0].Bool("VirtualizationFirmwareEnabled");
        }

        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
        _groupOffsets = Enumerable.Range(0, topo.Groups).Select(g => Enumerable.Range(0, g).Sum(x => (int)Native.GetActiveProcessorCount((ushort)x))).ToArray();

        _pdh = new PdhQuery();
        _utilTotal = _pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
        _utilPerCore = _pdh.Add(@"\Processor Information(*)\% Processor Utility");
        if (_utilTotal is null)
        {
            // Older systems: fall back to the classic Processor set.
            _utilTotal = _pdh.Add(@"\Processor(_Total)\% Processor Time");
            _utilPerCore = _pdh.Add(@"\Processor(*)\% Processor Time");
        }
        _perf = _pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
        _freq = _pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
        _queue = _pdh.Add(@"\System\Processor Queue Length");
        _ctxsw = _pdh.Add(@"\System\Context Switches/sec");
        _interrupts = _pdh.Add(@"\Processor Information(_Total)\Interrupts/sec");
        _dpc = _pdh.Add(@"\Processor Information(_Total)\% DPC Time");
        _processes = _pdh.Add(@"\System\Processes");
        _threads = _pdh.Add(@"\System\Threads");
        _perfLimit = _pdh.Add(@"\Processor Information(_Total)\% Performance Limit");
        _energy = _pdh.Add(@"\Energy Meter(*)\Power");
        _pdh.Collect();
        _baseMhz = _freq?.Value() ?? maxMhz ?? regMhz;

        Inventory = new CpuInventory(name, vendor, arch, topo.PhysicalCores, topo.LogicalProcessors, topo.Groups, topo.NumaNodes,
            topo.PerformanceCores, topo.EfficiencyCores, topo.IsHybrid, _baseMhz, maxMhz, topo.Caches, virt, topo.EfficiencyClassByLogical);

        SetCapability("Utilization", _utilTotal is not null, PdhSource);
        SetCapability("Per-core utilization", _utilPerCore is not null, PdhSource);
        SetCapability("Effective clock", _perf is not null && _baseMhz is not null, PdhSource, _perf is null ? "Processor performance counter not present" : null);
        SetCapability("Performance limit (throttling evidence)", _perfLimit is not null, PdhSource);
        SetCapability("Hybrid core classes", topo.IsHybrid, "GetLogicalProcessorInformationEx", topo.IsHybrid ? null : "Processor reports a single efficiency class");
        SetCapability("Package temperature", false, "None", NoTemp);
        SetCapability("Package power", _energy is not null, "Windows Energy Meter counters",
            _energy is null ? "No Energy Meter counter set is exposed by this system's firmware" : null, Confidence.Moderate);

        DefineMetrics();
        return Task.CompletedTask;
    }

    private void DefineMetrics()
    {
        // Per-core utilization is kept in the short detail tier only.
        for (var i = 0; i < Inventory.LogicalProcessors; i++)
            Pending.Add(new MetricDefinition(MetricKeys.CpuCore(i), $"CPU {i} utilization", MetricUnit.Percent, "CPU",
                "Utilization of one logical processor.", PersistPolicy.Detail));
        Pending.Add(new MetricDefinition("cpu.perflimit", "CPU performance limitation", MetricUnit.Percent, "CPU",
            "How far below its nominal performance Windows reports the processor is currently limited (by power policy, power budget or temperature). 0% means not limited; sustained non-zero values under load are evidence of throttling."));
        Pending.Add(new MetricDefinition(MetricKeys.CpuPower, "CPU package power", MetricUnit.Watts, "CPU",
            "Package power reported by the Windows Energy Meter counter set (RAPL), when the firmware exposes it."));
    }

    private List<MetricDefinition> Pending { get; } = [];

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        if (Pending.Count > 0)
        {
            foreach (var d in Pending) ctx.Metrics.Define(d);
            Pending.Clear();
        }
        if (_pdh is null || !_pdh.Collect()) throw new InvalidOperationException("Performance counter query failed");
        var now = ctx.Now;

        var util = _utilTotal?.Value(noCap: true) is { } u ? Math.Clamp(u, 0, 100) : (double?)null;
        var perCore = new double[Inventory.LogicalProcessors];
        if (_utilPerCore is not null)
        {
            foreach (var (instance, value) in _utilPerCore.Instances(noCap: true))
            {
                var idx = LogicalIndex(instance);
                if (idx >= 0 && idx < perCore.Length) perCore[idx] = Math.Clamp(value, 0, 100);
            }
        }

        double? mhz = _perf?.Value(noCap: true) is { } perf && _baseMhz is { } b ? b * perf / 100 : null;
        // "% Performance Limit" is the performance the processor can currently guarantee relative to nominal (100 = not limited).
        // Sentinel stores the shortfall, so 0 means "not limited" and larger values mean stronger limiting.
        var perfLimit = _perfLimit?.Value(noCap: true) is { } guaranteed ? Math.Clamp(100 - guaranteed, 0, 100) : (double?)null;
        var power = ReadPackagePower();

        Latest = new CpuSnapshot(now,
            Maybe(util, PdhSource, now, "Utilization counter unavailable"),
            perCore,
            mhz is { } m ? Reading.Estimated(m, "Processor Information: base frequency × % Processor Performance", now, Confidence.High) : Reading.Unavailable("Not exposed by this system"),
            Maybe(_queue?.Value(), PdhSource, now, "Not exposed"),
            Maybe(_ctxsw?.Value(), PdhSource, now, "Not exposed"),
            Maybe(_interrupts?.Value(), PdhSource, now, "Not exposed"),
            Maybe(_dpc?.Value(), PdhSource, now, "Not exposed"),
            power is { } w ? Reading.Estimated(w, "Windows Energy Meter (RAPL package)", now, Confidence.Moderate) : Reading.Unsupported("No safe package power source is exposed by this system"),
            Reading.Unsupported(NoTemp),
            Maybe(perfLimit, PdhSource + " (100 − % Performance Limit)", now, "Not exposed"),
            (int)(_processes?.Value() ?? 0),
            (int)(_threads?.Value() ?? 0),
            0,
            TimeSpan.FromMilliseconds(Environment.TickCount64));

        if (util is { } uu) ctx.Metrics.Record(MetricKeys.CpuUtil, uu, now);
        if (mhz is { } mm) ctx.Metrics.Record(MetricKeys.CpuFreq, mm, now);
        if (Latest.QueueLength.Value is { } q) ctx.Metrics.Record(MetricKeys.CpuQueue, q, now);
        if (Latest.ContextSwitchesPerSec.Value is { } cs) ctx.Metrics.Record(MetricKeys.CpuContextSwitches, cs, now);
        if (Latest.InterruptsPerSec.Value is { } ir) ctx.Metrics.Record(MetricKeys.CpuInterrupts, ir, now);
        if (Latest.DpcPercent.Value is { } dpc) ctx.Metrics.Record(MetricKeys.CpuDpc, dpc, now);
        if (perfLimit is { } pl) ctx.Metrics.Record("cpu.perflimit", pl, now);
        if (power is { } pw) ctx.Metrics.Record(MetricKeys.CpuPower, pw, now);
        if (ctx.Mode != SamplingMode.Background)
            for (var i = 0; i < perCore.Length; i++) ctx.Metrics.Record(MetricKeys.CpuCore(i), perCore[i], now);
        return Task.CompletedTask;
    }

    private double? ReadPackagePower()
    {
        if (_energy is null) return null;
        foreach (var (instance, value) in _energy.Instances())
        {
            if (!instance.Contains("PKG", StringComparison.OrdinalIgnoreCase)) continue;
            // The counter reports milliwatts. Values outside a plausible package range are rejected rather than shown.
            var watts = value / 1000.0;
            return watts is >= 0.2 and <= 500 ? watts : null;
        }
        return null;
    }

    private int LogicalIndex(string instance)
    {
        // Processor Information instances are "group,number"; "_Total" and "g,_Total" are aggregates.
        if (instance.Contains("_Total", StringComparison.OrdinalIgnoreCase)) return -1;
        var parts = instance.Split(',');
        if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return (g < _groupOffsets.Length ? _groupOffsets[g] : 0) + n;
        return int.TryParse(instance, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1;
    }

    public override void OnSystemResumed() => _pdh?.Rebuild();

    public override void Dispose()
    {
        _pdh?.Dispose();
        base.Dispose();
    }

    private static string? Sanitize(string? s) => string.IsNullOrWhiteSpace(s) ? null : Core.Privacy.Redactor.SanitizeUntrusted(s.Trim(), 128);
}
