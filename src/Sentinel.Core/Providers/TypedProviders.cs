using Sentinel.Domain;

namespace Sentinel.Core.Providers;

public interface ICpuTelemetryProvider : ITelemetryProvider
{
    CpuInventory Inventory { get; }
    CpuSnapshot Latest { get; }
}

public interface IMemoryTelemetryProvider : ITelemetryProvider
{
    MemoryInventory Inventory { get; }
    MemorySnapshot Latest { get; }
}

public interface IGpuTelemetryProvider : ITelemetryProvider
{
    IReadOnlyList<GpuAdapter> Adapters { get; }
    IReadOnlyList<GpuSnapshot> Latest { get; }
    /// <summary>Per-process GPU utilisation from Windows GPU engine counters (pid → percent).</summary>
    IReadOnlyDictionary<int, double> ProcessUtilization { get; }

    /// <summary>
    /// The highest board power this adapter can physically draw, in watts: 1.25 × the enforced limit when the vendor
    /// API reports it, otherwise a conservative class ceiling. Readings above it are driver glitches. Null when the
    /// adapter has no vendor power telemetry at all.
    /// </summary>
    double? PowerCeilingW(string adapterId);
}

public interface IStorageTelemetryProvider : ITelemetryProvider
{
    IReadOnlyList<PhysicalDiskInfo> Disks { get; }
    IReadOnlyList<VolumeInfo> Volumes { get; }
    IReadOnlyList<DiskIoSnapshot> Io { get; }
    IReadOnlyDictionary<string, DiskHealth> Health { get; }
}

public interface IBatteryTelemetryProvider : ITelemetryProvider
{
    BatterySnapshot Latest { get; }
}

public interface INetworkTelemetryProvider : ITelemetryProvider
{
    NetworkSnapshot Latest { get; }
}

public interface IThermalTelemetryProvider : ITelemetryProvider
{
    IReadOnlyList<ThermalSensor> Sensors { get; }
}

public interface IDisplayTelemetryProvider : ITelemetryProvider
{
    IReadOnlyList<DisplayInfo> Displays { get; }
}

public interface IAudioTelemetryProvider : ITelemetryProvider
{
    IReadOnlyList<AudioDevice> Devices { get; }
    IReadOnlyList<AudioSession> Sessions { get; }
}

public interface IDeviceInventoryProvider : ITelemetryProvider
{
    IReadOnlyList<DeviceRecord> Devices { get; }
}

public interface IDriverInventoryProvider : ITelemetryProvider
{
    IReadOnlyList<DriverInfo> Drivers { get; }
    DateTimeOffset? LastRefreshed { get; }
    Task RefreshAsync(CancellationToken ct);
}

public interface IWindowsEventProvider : ITelemetryProvider
{
    /// <summary>Most recent normalised events read from Windows logs (bounded).</summary>
    IReadOnlyList<SystemEvent> Recent { get; }
    /// <summary>Reads a historical window once (e.g. first run back-fill). Bounded by <paramref name="maxEvents"/>.</summary>
    Task<IReadOnlyList<SystemEvent>> ReadHistoryAsync(DateTimeOffset since, int maxEvents, CancellationToken ct);
}

public interface ISystemInventoryProvider : ITelemetryProvider
{
    SystemInventory Inventory { get; }
    SecurityStatus Security { get; }
}

public interface IPowerTelemetryProvider : ITelemetryProvider
{
    bool AcOnline { get; }
    bool BatterySaverOn { get; }
    bool DisplayOn { get; }
    TimeSpan UserIdle { get; }
    event EventHandler? Suspending;
    event EventHandler? Resumed;
    event EventHandler? PowerSourceChanged;
}

public interface IProcessTelemetryProvider : ITelemetryProvider
{
    ProcessSnapshot Latest { get; }
    ProcessDetails? GetDetails(int pid);
}

public sealed record ProcessDetails(int Pid, string Name, string? ImagePath, string? Publisher, string? Description, string? FileVersion,
    string? Architecture, string? IntegrityLevel, DateTimeOffset? StartTime, int ParentPid, string? ParentName);

public interface ISoftwareInventoryProvider : ITelemetryProvider
{
    IReadOnlyList<SoftwareItem> Software { get; }
    IReadOnlyList<ServiceItem> Services { get; }
}

public interface IStartupProvider : ITelemetryProvider
{
    IReadOnlyList<StartupItem> Items { get; }
}

public interface IUpdateHistoryProvider : ITelemetryProvider
{
    IReadOnlyList<UpdateRecord> Updates { get; }
    bool? RebootRequired { get; }
}

public interface IOemTelemetryProvider : ITelemetryProvider
{
    OemInfo? Info { get; }
}

/// <summary>Extension point for an optional third-party sensor library. None is bundled by default (see SAFETY.md).</summary>
public interface IHardwareTelemetryProvider : ITelemetryProvider
{
    IReadOnlyList<ThermalSensor> AdditionalSensors { get; }
}

/// <summary>User-triggered Windows diagnostic reports (powercfg). Never run automatically.</summary>
public interface IWindowsReportService
{
    Task<BatteryReportData?> GenerateBatteryReportAsync(CancellationToken ct);
}

public sealed record BatteryReportData(
    string? Manufacturer,
    string? Chemistry,
    long? DesignCapacityMWh,
    long? FullChargeCapacityMWh,
    int? CycleCount,
    IReadOnlyList<CapacityPoint> CapacityHistory,
    IReadOnlyList<BatteryUsageEntry> RecentUsage,
    string? RawPath);

public sealed record BatteryUsageEntry(DateTimeOffset Start, TimeSpan Duration, string State, bool AcPowered, double? ChargePercent, double? DischargeMWh);
