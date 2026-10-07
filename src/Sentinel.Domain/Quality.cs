namespace Sentinel.Domain;

/// <summary>Quality of a single reading. Never present a non-Good reading as current fact.</summary>
public enum Quality
{
    Good,
    Estimated,
    Stale,
    Unavailable,
    Unsupported,
}

public enum Confidence
{
    Unknown = 0,
    Low = 1,
    Moderate = 2,
    High = 3,
}

/// <summary>Severity is always rendered with an icon and text label, never colour alone.</summary>
public enum Severity
{
    Info = 0,
    Notice = 1,
    Warning = 2,
    Critical = 3,
}

public enum AccessRequirement
{
    None,
    /// <summary>Only available through the user-triggered, short-lived elevated helper.</summary>
    ElevatedHelper,
    /// <summary>Requires a vendor component (e.g. NVIDIA driver) that is already installed.</summary>
    VendorComponent,
}

public enum SamplingCost
{
    Negligible,
    Low,
    Moderate,
    High,
}

public enum HealthStatus
{
    Unknown,
    Excellent,
    Good,
    Normal,
    Attention,
    Critical,
}

/// <summary>Epistemic class of a statement. Keeps telemetry facts separate from interpretation.</summary>
public enum EvidenceKind
{
    Observed,
    Inferred,
    Possible,
    Unknown,
}

public enum CorrelationStrength
{
    InsufficientEvidence,
    Possible,
    Strong,
    Confirmed,
}

public static class EnumText
{
    public static string Label(this Confidence c) => c switch
    {
        Confidence.High => "High confidence",
        Confidence.Moderate => "Moderate confidence",
        Confidence.Low => "Low confidence",
        _ => "Confidence unknown",
    };

    public static string Label(this Severity s) => s switch
    {
        Severity.Critical => "Critical",
        Severity.Warning => "Warning",
        Severity.Notice => "Notice",
        _ => "Info",
    };

    public static string Label(this HealthStatus h) => h switch
    {
        HealthStatus.Excellent => "Excellent",
        HealthStatus.Good => "Good",
        HealthStatus.Normal => "Normal",
        HealthStatus.Attention => "Needs attention",
        HealthStatus.Critical => "Critical",
        _ => "Not enough data",
    };

    public static string Label(this CorrelationStrength c) => c switch
    {
        CorrelationStrength.Confirmed => "Confirmed",
        CorrelationStrength.Strong => "Strong correlation",
        CorrelationStrength.Possible => "Possible correlation",
        _ => "Insufficient evidence",
    };

    public static string Label(this EvidenceKind k) => k switch
    {
        EvidenceKind.Observed => "Observed",
        EvidenceKind.Inferred => "Inferred",
        EvidenceKind.Possible => "Possible",
        _ => "Unknown",
    };

    public static string Label(this Quality q) => q switch
    {
        Quality.Good => "Good",
        Quality.Estimated => "Estimated",
        Quality.Stale => "Stale",
        Quality.Unavailable => "Unavailable",
        _ => "Unsupported",
    };
}
