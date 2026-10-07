using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>Common plumbing for Windows providers: descriptor, capability list and interval table.</summary>
public abstract class WindowsProvider : ITelemetryProvider
{
    private readonly List<Capability> _capabilities = [];

    protected WindowsProvider(ILogger logger)
    {
        Log = logger;
    }

    protected ILogger Log { get; }

    public abstract ProviderDescriptor Descriptor { get; }

    public IReadOnlyList<Capability> Capabilities
    {
        get
        {
            lock (_capabilities) return [.. _capabilities];
        }
    }

    protected void SetCapability(string name, bool available, string source, string? reason = null, Confidence confidence = Confidence.High)
    {
        lock (_capabilities)
        {
            _capabilities.RemoveAll(c => c.Name == name);
            _capabilities.Add(new Capability(name, available, source, reason, confidence));
        }
    }

    public abstract Task InitializeAsync(CancellationToken ct);

    /// <summary>Interval table: (foreground, detail, background). Null background means "do not sample in background".</summary>
    protected abstract (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals { get; }

    public virtual TimeSpan GetInterval(SamplingMode mode) => mode switch
    {
        SamplingMode.Paused => Timeout.InfiniteTimeSpan,
        SamplingMode.Detail => Intervals.Detail,
        SamplingMode.Background => Intervals.Background ?? Timeout.InfiniteTimeSpan,
        _ => Intervals.Foreground,
    };

    public abstract Task SampleAsync(SampleContext context, CancellationToken ct);

    public virtual void OnSystemResumed()
    {
    }

    public virtual void Dispose() => GC.SuppressFinalize(this);

    protected static ProviderDescriptor Describe(string id, string name, string category, SamplingCost cost, string precision, params string[] sources) =>
        new(id, name, category, sources, AccessRequirement.None, cost, precision, SafetyAttestation.ReadOnlyObserver);

    protected static Reading Good(double v, string source, DateTimeOffset ts) => Reading.Good(v, source, ts);

    protected static Reading Maybe(double? v, string source, DateTimeOffset ts, string reasonIfMissing) =>
        v is { } x && !double.IsNaN(x) ? Reading.Good(x, source, ts) : Reading.Unavailable(reasonIfMissing, source);
}
