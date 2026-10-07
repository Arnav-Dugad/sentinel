namespace Sentinel.Diagnostics.Reports;

public enum ReportKind
{
    SystemHealth,
    HardwareInventory,
    Battery,
    Reliability,
    Crash,
    Drivers,
    PerformanceInvestigation,
    SupportBundle,
}

public enum ReportFormat { Html, Json, Csv }

public sealed class ReportTable
{
    public required string Title { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public List<IReadOnlyList<string>> Rows { get; } = [];
}

public sealed class ReportSection
{
    public required string Heading { get; init; }
    public List<string> Paragraphs { get; } = [];
    public List<(string Label, string Value)> Facts { get; } = [];
    public List<ReportTable> Tables { get; } = [];
}

public sealed class ReportDocument
{
    public required string Title { get; init; }
    public required ReportKind Kind { get; init; }
    public DateTimeOffset Generated { get; init; } = DateTimeOffset.Now;
    public bool PrivacySafe { get; init; } = true;
    public List<ReportSection> Sections { get; } = [];

    public ReportSection Add(string heading)
    {
        var s = new ReportSection { Heading = heading };
        Sections.Add(s);
        return s;
    }
}
