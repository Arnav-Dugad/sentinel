namespace Sentinel.Domain;

/// <summary>A labelled property for inventory views. <see cref="Sensitive"/> values are masked by default.</summary>
public sealed record InfoItem(string Label, string? Value, string? Source = null, bool Sensitive = false, string? Tooltip = null)
{
    public static InfoItem NotExposed(string label, string? source = null) => new(label, null, source);
}

// ---------------- System ----------------

public sealed record SystemInventory(
    string Manufacturer,
    string Model,
    string? ProductFamily,
    string? Sku,
    string? Baseboard,
    string? BaseboardVersion,
    string? BiosVendor,
    string? BiosVersion,
    DateTimeOffset? BiosDate,
    string FirmwareType,
    bool? SecureBoot,
    string? ChassisType,
    string SystemArchitecture,
    string OsArchitecture,
    string OsName,
    string OsVersion,
    string OsBuild,
    bool? VirtualizationFirmwareEnabled,
    bool? HypervisorPresent,
    bool? VbsRunning,
    bool? HvciRunning,
    DateTimeOffset BootTime,
    string? SerialNumber,
    string? SystemUuid,
    string? ComputerName,
    bool IsLaptop)
{
    public static SystemInventory Empty { get; } = new("Unknown", "Unknown", null, null, null, null, null, null, null,
        "Unknown", null, null, "Unknown", "Unknown", "Windows", "", "", null, null, null, null, DateTimeOffset.MinValue, null, null, null, false);
}

public sealed record SecurityStatus(
    bool? SecureBoot,
    bool? VbsRunning,
    bool? HvciRunning,
    bool? DefenderRealtime,
    DateTimeOffset? DefenderSignatureUpdated,
    string? Source);

// ---------------- CPU ----------------

public sealed record CacheInfo(int Level, string Type, long SizeBytes, int SharedByLogical);

public sealed record CpuInventory(
    string Name,
    string Manufacturer,
    string Architecture,
    int PhysicalCores,
    int LogicalProcessors,
    int ProcessorGroups,
    int NumaNodes,
    int PerformanceCores,
    int EfficiencyCores,
    bool IsHybrid,
    double? BaseMhz,
    double? MaxMhz,
    IReadOnlyList<CacheInfo> Caches,
    bool? VirtualizationSupported,
    IReadOnlyList<int> CoreEfficiencyClassByLogical)
{
    public static CpuInventory Empty { get; } = new("Unknown processor", "Unknown", "Unknown", 0, Environment.ProcessorCount, 1, 1, 0, 0, false, null, null, [], null, []);
}

public sealed record CpuSnapshot(
    DateTimeOffset Timestamp,
    Reading Utilization,
    IReadOnlyList<double> PerLogicalUtilization,
    Reading EffectiveMhz,
    Reading QueueLength,
    Reading ContextSwitchesPerSec,
    Reading InterruptsPerSec,
    Reading DpcPercent,
    Reading PackagePowerW,
    Reading Temperature,
    Reading ThrottleIndicator,
    int Processes,
    int Threads,
    int Handles,
    TimeSpan Uptime)
{
    public static CpuSnapshot Empty { get; } = new(DateTimeOffset.MinValue, Reading.None, [], Reading.None, Reading.None, Reading.None,
        Reading.None, Reading.None, Reading.Unsupported("No safe package power source is enabled"),
        Reading.Unsupported("No safe CPU temperature source is enabled"), Reading.None, 0, 0, 0, TimeSpan.Zero);
}

// ---------------- Memory ----------------

public sealed record MemoryModule(
    string? Manufacturer,
    long CapacityBytes,
    int? SpeedMts,
    int? ConfiguredSpeedMts,
    string? FormFactor,
    string? Slot,
    string? PartNumber,
    int? Rank,
    string? MemoryType,
    string? SerialNumber);

public sealed record MemoryInventory(long InstalledBytes, long UsableBytes, IReadOnlyList<MemoryModule> Modules, int? TotalSlots)
{
    public static MemoryInventory Empty { get; } = new(0, 0, [], null);
}

public sealed record MemorySnapshot(
    DateTimeOffset Timestamp,
    long TotalBytes,
    long AvailableBytes,
    long CommittedBytes,
    long CommitLimitBytes,
    long CachedBytes,
    Reading StandbyBytes,
    long PagedPoolBytes,
    long NonPagedPoolBytes,
    Reading CompressedBytes,
    Reading PageFaultsPerSec,
    Reading HardFaultsPerSec,
    Reading PageFileUsagePercent)
{
    public long UsedBytes => TotalBytes - AvailableBytes;
    public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0;
    public double CommitPercent => CommitLimitBytes > 0 ? CommittedBytes * 100.0 / CommitLimitBytes : 0;

    public static MemorySnapshot Empty { get; } = new(DateTimeOffset.MinValue, 0, 0, 0, 0, 0, Reading.None, 0, 0, Reading.None, Reading.None, Reading.None, Reading.None);
}

// ---------------- GPU ----------------

public enum GpuKind { Integrated, Discrete, External, Software, Virtual, Unknown }

public sealed record GpuAdapter(
    string Id,
    string Name,
    string Vendor,
    uint VendorId,
    uint DeviceId,
    GpuKind Kind,
    long DedicatedVideoMemory,
    long SharedSystemMemory,
    string? DriverVersion,
    DateTimeOffset? DriverDate,
    string LuidKey,
    string? PnpDeviceId,
    bool VendorTelemetryAvailable,
    string? VendorTelemetrySource);

public sealed record GpuEngineUsage(string EngineType, double Percent);

public sealed record GpuSnapshot(
    string AdapterId,
    DateTimeOffset Timestamp,
    Reading Utilization,
    IReadOnlyList<GpuEngineUsage> Engines,
    Reading DedicatedUsedBytes,
    Reading SharedUsedBytes,
    Reading Temperature,
    Reading HotspotTemperature,
    Reading PowerW,
    Reading PowerLimitW,
    Reading CoreClockMhz,
    Reading MemoryClockMhz,
    Reading FanPercent,
    Reading PerformanceState,
    Reading EncoderPercent,
    Reading DecoderPercent,
    string? ThrottleReasons,
    bool InLowPowerState);

// ---------------- Storage ----------------

public sealed record VolumeInfo(
    string Name,
    string? Label,
    string? FileSystem,
    long TotalBytes,
    long FreeBytes,
    string DriveType,
    bool IsSystem,
    bool IsBoot,
    int? DiskNumber,
    string? Encryption);

public sealed record DiskHealth(
    DateTimeOffset Timestamp,
    string Source,
    string? HealthStatus,
    Reading Temperature,
    Reading WarningTemperature,
    Reading CriticalTemperature,
    Reading PercentageUsed,
    Reading AvailableSpare,
    Reading AvailableSpareThreshold,
    int? CriticalWarning,
    Reading DataReadBytes,
    Reading DataWrittenBytes,
    Reading PowerOnHours,
    Reading PowerCycles,
    Reading UnsafeShutdowns,
    Reading MediaErrors,
    Reading ErrorLogEntries)
{
    public static DiskHealth Unknown(string reason) => new(DateTimeOffset.Now, "None", null,
        Reading.Unavailable(reason), Reading.None, Reading.None, Reading.Unavailable(reason), Reading.Unavailable(reason),
        Reading.None, null, Reading.Unavailable(reason), Reading.Unavailable(reason), Reading.Unavailable(reason),
        Reading.Unavailable(reason), Reading.Unavailable(reason), Reading.Unavailable(reason), Reading.Unavailable(reason));
}

public sealed record PhysicalDiskInfo(
    string Id,
    int Number,
    string Model,
    string? Manufacturer,
    string BusType,
    string MediaType,
    long SizeBytes,
    string? FirmwareVersion,
    bool IsSystemDisk,
    bool IsRemovable,
    string? SerialNumber,
    IReadOnlyList<string> VolumeNames);

public sealed record DiskIoSnapshot(
    string DiskId,
    DateTimeOffset Timestamp,
    Reading ReadBytesPerSec,
    Reading WriteBytesPerSec,
    Reading Iops,
    Reading LatencyMs,
    Reading ActivePercent,
    Reading QueueLength);

// ---------------- Battery ----------------

public enum ChargeState { Unknown, Charging, Discharging, Full, Idle, NoBattery }

public sealed record BatteryInfo(
    string Id,
    string? Name,
    string? Manufacturer,
    string? Chemistry,
    long? DesignCapacityMWh,
    long? FullChargeCapacityMWh,
    int? CycleCount,
    DateTimeOffset? ManufactureDate,
    string? SerialNumber,
    Reading Temperature)
{
    /// <summary>Estimated health = full-charge / design capacity, as reported by the battery firmware.</summary>
    public double? EstimatedHealthPercent =>
        DesignCapacityMWh is > 0 && FullChargeCapacityMWh is > 0 ? FullChargeCapacityMWh.Value * 100.0 / DesignCapacityMWh.Value : null;
}

public sealed record BatterySnapshot(
    DateTimeOffset Timestamp,
    bool Present,
    bool AcOnline,
    ChargeState State,
    Reading Percent,
    Reading RemainingMWh,
    Reading FullChargeMWh,
    Reading DesignMWh,
    Reading RateMilliwatts,
    Reading VoltageMv,
    Reading TimeRemaining,
    bool BatterySaverOn,
    IReadOnlyList<BatteryInfo> Batteries)
{
    public static BatterySnapshot Empty { get; } = new(DateTimeOffset.MinValue, false, true, ChargeState.Unknown, Reading.None, Reading.None, Reading.None,
        Reading.None, Reading.None, Reading.None, Reading.None, false, []);
}

// ---------------- Network ----------------

public sealed record WifiInfo(
    string? Ssid,
    Reading SignalQuality,
    Reading RssiDbm,
    string? PhyType,
    Reading Channel,
    string? Band,
    Reading RxRateMbps,
    Reading TxRateMbps,
    string? Authentication);

public sealed record NetworkAdapterSnapshot(
    string Id,
    string Name,
    string Description,
    string Kind,
    bool IsUp,
    long LinkSpeedBps,
    long BytesReceived,
    long BytesSent,
    double RxBytesPerSec,
    double TxBytesPerSec,
    long UnicastPacketsReceived,
    long UnicastPacketsSent,
    long InErrors,
    long OutErrors,
    long InDiscards,
    long OutDiscards,
    IReadOnlyList<string> IPv4,
    IReadOnlyList<string> IPv6,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    bool? DhcpEnabled,
    string? MacAddress,
    WifiInfo? Wifi);

public sealed record NetworkSnapshot(DateTimeOffset Timestamp, IReadOnlyList<NetworkAdapterSnapshot> Adapters, double TotalRxBytesPerSec, double TotalTxBytesPerSec, bool InternetConnectivityKnown)
{
    public static NetworkSnapshot Empty { get; } = new(DateTimeOffset.MinValue, [], 0, 0, false);
}

// ---------------- Thermal ----------------

public sealed record ThermalSensor(string Id, string Name, string Component, Reading Temperature, double? LimitCelsius, string? LimitSource);

// ---------------- Display / Audio / Devices ----------------

public sealed record DisplayInfo(
    string Id,
    string FriendlyName,
    string? ManufacturerCode,
    string? GpuName,
    int Width,
    int Height,
    double RefreshHz,
    int Rotation,
    double ScalePercent,
    string Connection,
    bool IsInternal,
    bool IsPrimary,
    bool? HdrSupported,
    bool? HdrEnabled,
    int? BitsPerColor,
    int PositionX,
    int PositionY);

public sealed record AudioDevice(string Id, string Name, bool IsCapture, bool IsDefault, bool IsDefaultCommunications, string State, int? SampleRate, int? Channels);

public sealed record AudioSession(int ProcessId, string DisplayName, float PeakLevel, bool IsActive, string DeviceName);

public sealed record DeviceRecord(
    string ContainerId,
    string Name,
    string Category,
    string? Manufacturer,
    bool Connected,
    DateTimeOffset FirstSeen,
    DateTimeOffset? LastConnected,
    DateTimeOffset? LastDisconnected,
    int? BatteryPercent,
    int ProblemCode,
    string? DriverProvider,
    string? DriverVersion);

// ---------------- Drivers ----------------

public sealed record DriverInfo(
    string DeviceName,
    string DeviceClass,
    string Group,
    string? Manufacturer,
    string? Provider,
    string? Version,
    DateTimeOffset? Date,
    string? Signer,
    bool? IsSigned,
    string? InfName,
    string DeviceInstanceId);

// ---------------- Processes ----------------

public sealed record ProcessSample(
    int Pid,
    int ParentPid,
    string Name,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    double DiskBytesPerSec,
    double GpuPercent,
    int Threads,
    int Handles,
    DateTimeOffset StartTime,
    int SessionId,
    string? ImagePath);

/// <summary>Processes grouped by application (executable).</summary>
public sealed record AppUsage(
    string AppKey,
    string DisplayName,
    string? Publisher,
    int ProcessCount,
    double CpuPercent,
    long PrivateBytes,
    long WorkingSetBytes,
    double DiskBytesPerSec,
    double GpuPercent,
    double CpuShareOfActive,
    double MemoryShareOfApps);

public sealed record ProcessSnapshot(DateTimeOffset Timestamp, IReadOnlyList<ProcessSample> Processes, IReadOnlyList<AppUsage> Apps, double TotalCpuPercent)
{
    public static ProcessSnapshot Empty { get; } = new(DateTimeOffset.MinValue, [], [], 0);
}

// ---------------- Software / startup / updates ----------------

public enum StartupImpact { Unknown, Low, Moderate, High }

public sealed record StartupItem(string Name, string? Publisher, string Location, string? Target, bool Enabled, StartupImpact Impact, string? ImpactEvidence);

public sealed record SoftwareItem(string Name, string? Version, string? Publisher, DateTimeOffset? InstallDate, string Scope);

public sealed record ServiceItem(string Name, string DisplayName, string StartType, string Status);

public enum UpdateKind { Cumulative, Feature, Driver, Definition, DotNet, StoreApp, Other }

public sealed record UpdateRecord(DateTimeOffset Date, string Title, UpdateKind Kind, string Result, string? KbArticle, string? Description);

// ---------------- OEM ----------------

public sealed record OemInfo(string Manufacturer, string? UtilityDetected, IReadOnlyList<InfoItem> Properties, string Statement);
