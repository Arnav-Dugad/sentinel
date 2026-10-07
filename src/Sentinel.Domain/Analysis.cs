namespace Sentinel.Domain;

public sealed record EvidenceItem(EvidenceKind Kind, string Statement, string? Source = null, DateTimeOffset? Timestamp = null);

/// <summary>A learned description of what is normal for this computer for a metric in a context.</summary>
public sealed record Baseline(
    string MetricKey,
    string Context,
    double Median,
    double Mad,
    double P05,
    double P95,
    double Mean,
    int SampleCount,
    int DaysCovered,
    DateTimeOffset ComputedAt)
{
    /// <summary>Robust sigma estimate (MAD scaled for a normal distribution).</summary>
    public double RobustSigma => Math.Max(Mad * 1.4826, 1e-9);

    public bool IsMature => DaysCovered >= 3 && SampleCount >= 120;
}

public sealed record Anomaly(
    string Id,
    string MetricKey,
    string Title,
    string Description,
    double BaselineValue,
    double ObservedValue,
    double Deviation,
    string DeviationText,
    DateTimeOffset Start,
    DateTimeOffset LastSeen,
    Severity Severity,
    Confidence Confidence,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<string> CorrelatedEvents,
    bool Active)
{
    public TimeSpan Duration => LastSeen - Start;
}

public sealed record RecommendedAction(string Text, string? SettingsUri = null, string? SettingsLabel = null);

public sealed record Insight(
    string Id,
    string Title,
    string Summary,
    Severity Severity,
    Confidence Confidence,
    IReadOnlyList<EvidenceItem> Evidence,
    RecommendedAction? Action = null,
    string? Category = null,
    DateTimeOffset? Timestamp = null);

public sealed record HealthFactor(string Text, EvidenceKind Kind, HealthStatus Impact);

public sealed record HealthCategoryResult(
    string Key,
    string Name,
    HealthStatus Status,
    Confidence Confidence,
    string Summary,
    IReadOnlyList<HealthFactor> Factors);

public sealed record HealthReport(DateTimeOffset Timestamp, HealthStatus Overall, string Headline, string Detail, IReadOnlyList<HealthCategoryResult> Categories);

public sealed record Correlation(SystemEvent Subject, SystemEvent Related, CorrelationStrength Strength, string Rationale);

// ---------------- Sessions ----------------

public sealed record WorkloadSession(
    long Id,
    DateTimeOffset Start,
    DateTimeOffset End,
    string? AppName,
    double CpuAvg,
    double CpuPeak,
    double? GpuAvg,
    double? GpuPeak,
    double? GpuTempAvg,
    double? GpuTempPeak,
    double? CpuTempAvg,
    double? CpuTempPeak,
    double? GpuPowerAvg,
    long MemoryPeakBytes,
    int CrashesDuring,
    bool OnBattery)
{
    public TimeSpan Duration => End - Start;
}

public enum PowerSessionKind { Discharge, Charge, Sleep }

public sealed record PowerSession(
    long Id,
    PowerSessionKind Kind,
    DateTimeOffset Start,
    DateTimeOffset End,
    double? StartPercent,
    double? EndPercent,
    double? StartMWh,
    double? EndMWh,
    double? AverageWatts,
    string? Notes)
{
    public TimeSpan Duration => End - Start;
    public double? PercentDelta => StartPercent is { } s && EndPercent is { } e ? e - s : null;
}

public sealed record CapacityPoint(DateTimeOffset Date, double FullChargeMWh, double? DesignMWh, string Source);
