namespace Sentinel.Domain;

/// <summary>
/// A single scalar reading with its provenance. A reading without a value must carry a
/// non-Good quality and, ideally, a human readable reason.
/// </summary>
public readonly record struct Reading(
    double? Value,
    Quality Quality,
    string Source,
    DateTimeOffset Timestamp,
    Confidence Confidence = Confidence.High,
    string? Reason = null)
{
    public bool HasValue => Value.HasValue && Quality is Quality.Good or Quality.Estimated;

    public TimeSpan Age(DateTimeOffset now) => now - Timestamp;

    public static Reading Good(double value, string source, DateTimeOffset ts) => new(value, Quality.Good, source, ts);

    public static Reading Estimated(double value, string source, DateTimeOffset ts, Confidence confidence = Confidence.Moderate) =>
        new(value, Quality.Estimated, source, ts, confidence);

    public static Reading Unavailable(string reason, string source = "None") =>
        new(null, Quality.Unavailable, source, DateTimeOffset.MinValue, Confidence.Unknown, reason);

    public static Reading Unsupported(string reason) =>
        new(null, Quality.Unsupported, "None", DateTimeOffset.MinValue, Confidence.Unknown, reason);

    /// <summary>Downgrades the reading to Stale once older than <paramref name="maxAge"/>.</summary>
    public Reading WithStaleness(DateTimeOffset now, TimeSpan maxAge) =>
        HasValue && now - Timestamp > maxAge ? this with { Quality = Quality.Stale } : this;

    public static readonly Reading None = Unavailable("Not sampled yet");
}
