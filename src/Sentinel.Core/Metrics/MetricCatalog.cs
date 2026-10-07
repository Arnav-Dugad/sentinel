using Sentinel.Domain;

namespace Sentinel.Core.Metrics;

/// <summary>Definitions and educational descriptions for well-known metrics.</summary>
public static class MetricCatalog
{
    public static readonly IReadOnlyList<MetricDefinition> Core =
    [
        new(MetricKeys.CpuUtil, "CPU utilization", MetricUnit.Percent, "CPU",
            "Share of total processor capacity in use across all logical processors, as reported by Windows' processor utility counter."),
        new(MetricKeys.CpuFreq, "CPU effective clock", MetricUnit.Megahertz, "CPU",
            "Average effective frequency derived from Windows' processor performance counter (base frequency × % performance)."),
        new(MetricKeys.CpuQueue, "Processor queue", MetricUnit.Count, "CPU",
            "Threads that are ready to run but waiting for a processor. Sustained values above the core count suggest CPU saturation.", PersistPolicy.Detail),
        new(MetricKeys.CpuContextSwitches, "Context switches", MetricUnit.PerSecond, "CPU",
            "How often processors switch between threads. High values are normal under load; spikes at idle can indicate a busy driver or app.", PersistPolicy.Detail),
        new(MetricKeys.CpuInterrupts, "Interrupts", MetricUnit.PerSecond, "CPU",
            "Hardware interrupts serviced per second.", PersistPolicy.Detail),
        new(MetricKeys.CpuDpc, "DPC time", MetricUnit.Percent, "CPU",
            "Time spent in deferred procedure calls — driver work scheduled from interrupts. Elevated DPC time can cause audio crackle or stutter.", PersistPolicy.Detail),
        new(MetricKeys.MemUsedPct, "Memory in use", MetricUnit.Percent, "Memory",
            "Physical memory in use as a share of installed usable memory."),
        new(MetricKeys.MemUsed, "Memory in use (bytes)", MetricUnit.Bytes, "Memory",
            "Physical memory currently in use by processes, drivers and the operating system."),
        new(MetricKeys.MemCommit, "Committed memory", MetricUnit.Bytes, "Memory",
            "The amount of virtual memory Windows has promised to processes. It is backed by RAM or the page file."),
        new(MetricKeys.MemCached, "Cached memory", MetricUnit.Bytes, "Memory",
            "Memory holding file data and standby pages that can be reclaimed instantly when an app needs it.", PersistPolicy.Detail),
        new(MetricKeys.MemHardFaults, "Hard faults", MetricUnit.PerSecond, "Memory",
            "Page reads that had to come from disk. Sustained high values indicate memory pressure and cause slowdowns."),
        new(MetricKeys.MemPageFile, "Page file usage", MetricUnit.Percent, "Memory",
            "How much of the page file is currently in use.", PersistPolicy.Detail),
        new(MetricKeys.GpuUtilAny, "GPU utilization (busiest)", MetricUnit.Percent, "GPU",
            "Utilization of the busiest GPU engine on the busiest adapter, matching Task Manager's GPU column."),
        new(MetricKeys.GpuTempAny, "GPU temperature", MetricUnit.Celsius, "GPU",
            "GPU core temperature from the vendor's official read-only telemetry interface, when available."),
        new(MetricKeys.DiskRead, "Disk read", MetricUnit.BytesPerSecond, "Storage", "Bytes read per second across all physical disks."),
        new(MetricKeys.DiskWrite, "Disk write", MetricUnit.BytesPerSecond, "Storage", "Bytes written per second across all physical disks."),
        new(MetricKeys.DiskActive, "Disk active time", MetricUnit.Percent, "Storage",
            "Share of time the busiest disk was processing requests."),
        new(MetricKeys.NetRx, "Network received", MetricUnit.BytesPerSecond, "Network", "Bytes received per second across active adapters."),
        new(MetricKeys.NetTx, "Network sent", MetricUnit.BytesPerSecond, "Network", "Bytes sent per second across active adapters."),
        new(MetricKeys.WifiSignal, "Wi-Fi signal quality", MetricUnit.Percent, "Network",
            "Signal quality reported by the Windows WLAN service (0–100)."),
        new(MetricKeys.WifiRssi, "Wi-Fi RSSI", MetricUnit.None, "Network", "Received signal strength in dBm. Closer to 0 is stronger; below −75 dBm is weak."),
        new(MetricKeys.BatPercent, "Battery charge", MetricUnit.Percent, "Battery", "Remaining charge as a share of the current full-charge capacity."),
        new(MetricKeys.BatRate, "Battery rate", MetricUnit.Watts, "Battery",
            "Power flowing out of (negative) or into (positive) the battery, as reported by the battery firmware."),
        new(MetricKeys.BatCapacity, "Remaining capacity", MetricUnit.MilliwattHours, "Battery", "Remaining energy in the battery."),
        new(MetricKeys.BatFullCharge, "Full-charge capacity", MetricUnit.MilliwattHours, "Battery",
            "Energy the battery can currently hold when full. It decreases as the battery ages."),
        new(MetricKeys.BatVoltage, "Battery voltage", MetricUnit.Volts, "Battery", "Battery terminal voltage.", PersistPolicy.Detail),
        new(MetricKeys.AcOnline, "On AC power", MetricUnit.None, "Power", "1 when external power is connected."),
        new(MetricKeys.UserIdleSeconds, "User idle time", MetricUnit.None, "Power", "Seconds since the last keyboard or mouse input.", PersistPolicy.Detail),
    ];

    public static void RegisterCore(LiveMetricStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        foreach (var d in Core) store.Define(d);
    }
}
