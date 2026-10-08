using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sentinel.App.Controls;
using Sentinel.Core.Units;
using Sentinel.Domain;

namespace Sentinel.App.ViewModels;

public static class EventGlyphs
{
    public static string For(EventCategory c) => c switch
    {
        EventCategory.AppCrash or EventCategory.AppHang => "",
        EventCategory.Bugcheck => "",
        EventCategory.UnexpectedShutdown => "",
        EventCategory.DisplayDriverReset => "",
        EventCategory.DriverFailure or EventCategory.DriverChanged => "",
        EventCategory.HardwareError => "",
        EventCategory.StorageError => "",
        EventCategory.ServiceFailure => "",
        EventCategory.UpdateInstalled or EventCategory.UpdateFailed => "",
        EventCategory.Boot => "",
        EventCategory.Shutdown => "",
        EventCategory.Sleep => "",
        EventCategory.Wake => "",
        EventCategory.NetworkConnected or EventCategory.NetworkDisconnected => "",
        EventCategory.DeviceConnected or EventCategory.DeviceDisconnected or EventCategory.DeviceProblem => "",
        EventCategory.SoftwareInstalled or EventCategory.SoftwareRemoved => "",
        EventCategory.StartupItemAdded => "",
        EventCategory.Anomaly => "",
        EventCategory.WorkloadSession => "",
        EventCategory.PowerSource => "",
        _ => "",
    };
}

/// <summary>One row in an event list. Severity is conveyed by icon and text, not colour alone.</summary>
public sealed record EventRow(SystemEvent Event)
{
    public string Time => UnitFormatter.When(Event.Timestamp);

    public string FullTime => UnitFormatter.Full(Event.Timestamp);
    public string ShortTime => Event.Timestamp.ToString("t", CultureInfo.CurrentCulture);
    public string Title => Event.Title;
    public string Detail => Event.Detail ?? SystemEvent.CategoryLabel(Event.Category);
    public string Glyph => EventGlyphs.For(Event.Category);
    public string SeverityLabel => Event.Severity >= Severity.Warning ? Event.Severity.Label() : "";
    public string Category => SystemEvent.CategoryLabel(Event.Category);
}

public sealed partial class HomeTile : ObservableObject
{
    public required string Label { get; init; }
    public required string Glyph { get; init; }
    public required string Target { get; init; }
    public int ColorIndex { get; init; } = 1;
    public double? SparkMax { get; init; }
    public double SparkMaxOrNaN => SparkMax ?? double.NaN;

    [ObservableProperty]
    public partial string Value { get; set; } = "—";

    [ObservableProperty]
    public partial string Caption { get; set; } = "";

    [ObservableProperty]
    public partial string Provenance { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<ChartPoint> Spark { get; set; } = [];

    [ObservableProperty]
    public partial bool Visible { get; set; } = true;
}

public sealed record AppRow(string Name, string Publisher, string Cpu, string Memory, string Gpu, string Impact, double CpuValue);

public sealed record HealthChip(string Name, string Summary, HealthStatus Status);

public sealed record InsightRow(Insight Insight)
{
    public string Title => Insight.Title;
    public string Summary => Insight.Summary;
    public string ConfidenceLabel => Insight.Confidence.Label();
    public HealthStatus Status => Insight.Severity switch
    {
        Severity.Critical => HealthStatus.Critical,
        Severity.Warning => HealthStatus.Attention,
        Severity.Notice => HealthStatus.Normal,
        _ => HealthStatus.Good,
    };
}
