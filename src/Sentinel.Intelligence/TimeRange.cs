namespace Sentinel.Intelligence;

public readonly record struct TimeRange(DateTimeOffset From, DateTimeOffset To, string Label)
{
    public TimeSpan Span => To - From;

    public bool Contains(DateTimeOffset t) => t >= From && t <= To;

    public static TimeRange Last(TimeSpan span, DateTimeOffset now, string label) => new(now - span, now, label);

    public static TimeRange Today(DateTimeOffset now) => new(StartOfDay(now), now, "today");

    public static TimeRange Yesterday(DateTimeOffset now) => new(StartOfDay(now).AddDays(-1), StartOfDay(now), "yesterday");

    public static DateTimeOffset StartOfDay(DateTimeOffset t) => new(t.Year, t.Month, t.Day, 0, 0, 0, t.Offset);

    /// <summary>The equally long window immediately before this one (for comparisons).</summary>
    public TimeRange Previous(string label) => new(From - Span, From, label);

    public static readonly (string Key, TimeSpan Span)[] Presets =
    [
        ("Live", TimeSpan.FromMinutes(10)),
        ("1h", TimeSpan.FromHours(1)),
        ("6h", TimeSpan.FromHours(6)),
        ("24h", TimeSpan.FromDays(1)),
        ("7d", TimeSpan.FromDays(7)),
        ("30d", TimeSpan.FromDays(30)),
    ];
}
