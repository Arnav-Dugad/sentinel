namespace Sentinel.Domain;

public enum EventCategory
{
    AppCrash,
    AppHang,
    Bugcheck,
    UnexpectedShutdown,
    DisplayDriverReset,
    DriverFailure,
    HardwareError,
    StorageError,
    ServiceFailure,
    UpdateInstalled,
    UpdateFailed,
    Boot,
    Shutdown,
    Sleep,
    Wake,
    NetworkConnected,
    NetworkDisconnected,
    DeviceConnected,
    DeviceDisconnected,
    DeviceProblem,
    DriverChanged,
    SoftwareInstalled,
    SoftwareRemoved,
    StartupItemAdded,
    Anomaly,
    WorkloadSession,
    PowerSource,
    Other,
}

/// <summary>A normalised event from any source (event log, telemetry, inventory diff, Sentinel itself).</summary>
public sealed record SystemEvent(
    DateTimeOffset Timestamp,
    EventCategory Category,
    Severity Severity,
    string Title,
    string? Detail,
    string Source,
    string? Subject = null,
    string? DedupeKey = null,
    long Id = 0,
    string? Code = null)
{
    public bool IsReliabilityRelevant => Category is EventCategory.AppCrash or EventCategory.AppHang or EventCategory.Bugcheck
        or EventCategory.UnexpectedShutdown or EventCategory.DisplayDriverReset or EventCategory.DriverFailure
        or EventCategory.HardwareError or EventCategory.StorageError or EventCategory.ServiceFailure or EventCategory.UpdateFailed;

    public static string CategoryLabel(EventCategory c) => c switch
    {
        EventCategory.AppCrash => "App crash",
        EventCategory.AppHang => "App stopped responding",
        EventCategory.Bugcheck => "Stop error (bugcheck)",
        EventCategory.UnexpectedShutdown => "Unexpected shutdown",
        EventCategory.DisplayDriverReset => "Display driver recovered",
        EventCategory.DriverFailure => "Driver failure",
        EventCategory.HardwareError => "Hardware error record",
        EventCategory.StorageError => "Storage error",
        EventCategory.ServiceFailure => "Service failure",
        EventCategory.UpdateInstalled => "Update installed",
        EventCategory.UpdateFailed => "Update failed",
        EventCategory.Boot => "Startup",
        EventCategory.Shutdown => "Shutdown",
        EventCategory.Sleep => "Entered sleep",
        EventCategory.Wake => "Woke",
        EventCategory.NetworkConnected => "Network connected",
        EventCategory.NetworkDisconnected => "Network disconnected",
        EventCategory.DeviceConnected => "Device connected",
        EventCategory.DeviceDisconnected => "Device disconnected",
        EventCategory.DeviceProblem => "Device problem",
        EventCategory.DriverChanged => "Driver changed",
        EventCategory.SoftwareInstalled => "App installed",
        EventCategory.SoftwareRemoved => "App removed",
        EventCategory.StartupItemAdded => "Startup item added",
        EventCategory.Anomaly => "Anomaly",
        EventCategory.WorkloadSession => "High-performance session",
        EventCategory.PowerSource => "Power source changed",
        _ => "Event",
    };
}

public sealed record ChangeRecord(
    DateTimeOffset Timestamp,
    string Kind,
    string Title,
    string? Before,
    string? After,
    string Source,
    long Id = 0);
