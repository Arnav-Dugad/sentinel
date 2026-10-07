using Sentinel.Domain;

namespace Sentinel.Core.Providers;

/// <summary>How hard Sentinel is currently looking. Providers choose their own interval per mode.</summary>
public enum SamplingMode
{
    /// <summary>Main window visible.</summary>
    Foreground,
    /// <summary>A detailed sensor page for this provider's category is visible.</summary>
    Detail,
    /// <summary>Window hidden / tray only.</summary>
    Background,
    /// <summary>System is suspending or user paused collection.</summary>
    Paused,
}

public enum ProviderHealth
{
    NotStarted,
    Healthy,
    Degraded,
    Unavailable,
    Failed,
    Disabled,
}

/// <summary>
/// Declaration every provider must make about its own behaviour. Validated by <see cref="SafetyContract"/>
/// before a provider can be registered. See SAFETY.md.
/// </summary>
public sealed record SafetyAttestation(
    bool ReadOnly = true,
    bool ModifiesHardwareState = false,
    bool DegradesSecurity = false,
    bool InstallsKernelDriver = false,
    bool InteractsWithFirmware = false,
    bool RunsArbitraryPrivilegedCommands = false,
    bool FailureIsolated = true,
    bool UserCanDisable = true,
    bool ReportsProvenance = true,
    bool FailsGracefullyOnUnsupportedHardware = true)
{
    public static SafetyAttestation ReadOnlyObserver { get; } = new();
}

public sealed record ProviderDescriptor(
    string Id,
    string Name,
    string Category,
    IReadOnlyList<string> DataSources,
    AccessRequirement Access,
    SamplingCost Cost,
    string Precision,
    SafetyAttestation Safety,
    bool IsSimulation = false);

public sealed record Capability(string Name, bool Available, string Source, string? Reason = null, Confidence Confidence = Confidence.High);

public sealed record ProviderStatus(
    ProviderHealth Health,
    string? Message,
    DateTimeOffset? LastSuccess,
    int ConsecutiveFailures,
    TimeSpan LastDuration,
    long SampleCount)
{
    public static ProviderStatus NotStarted { get; } = new(ProviderHealth.NotStarted, null, null, 0, TimeSpan.Zero, 0);
}

/// <summary>Receives scalar metric samples (live buffers + history recorder).</summary>
public interface IMetricSink
{
    void Record(string key, double value, DateTimeOffset timestamp);
    void Define(MetricDefinition definition);
}

/// <summary>Receives normalised events. Implementations deduplicate on <see cref="SystemEvent.DedupeKey"/>.</summary>
public interface IEventSink
{
    void Publish(SystemEvent e);
}

public sealed class SampleContext(DateTimeOffset now, SamplingMode mode, IMetricSink metrics, IEventSink events)
{
    public DateTimeOffset Now { get; } = now;
    public SamplingMode Mode { get; } = mode;
    public IMetricSink Metrics { get; } = metrics;
    public IEventSink Events { get; } = events;
}

/// <summary>Base contract for every data provider. Implementations must be read-only (see SAFETY.md).</summary>
public interface ITelemetryProvider : IDisposable
{
    ProviderDescriptor Descriptor { get; }

    IReadOnlyList<Capability> Capabilities { get; }

    /// <summary>Discover hardware/APIs. Must not throw for unsupported systems; report via <see cref="Capabilities"/>.</summary>
    Task InitializeAsync(CancellationToken ct);

    /// <summary>Interval for the given mode. <see cref="Timeout.InfiniteTimeSpan"/> means "do not sample in this mode".</summary>
    TimeSpan GetInterval(SamplingMode mode);

    Task SampleAsync(SampleContext context, CancellationToken ct);

    /// <summary>Called after resume from sleep/hibernate. Rebuild native handles/queries that may have gone stale.</summary>
    void OnSystemResumed();
}

public static class Intervals
{
    public static readonly TimeSpan Never = Timeout.InfiniteTimeSpan;
    public static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);
}
