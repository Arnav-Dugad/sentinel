using Microsoft.UI.Xaml;

namespace Sentinel.App.Views;

/// <summary>Small pure functions for x:Bind.</summary>
public static class Bool
{
    public static Visibility Vis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility HasText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility NonZero(int value) => value > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Zero(int value) => value == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Invert(bool value) => !value;

    public static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

    public static Visibility Many(int count) => count > 1 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Visible only when a series actually changes, so a flat or single-point history is not drawn as a chart.</summary>
    public static Visibility Trend(IReadOnlyList<Controls.ChartPoint>? points) =>
        points is { Count: >= 2 } && points.Max(p => p.V) - points.Min(p => p.V) > 1e-9 ? Visibility.Visible : Visibility.Collapsed;
}

public static class Text
{
    /// <summary>Column header text with a sort arrow on the active column (names sort A–Z, numbers high to low).</summary>
    public static string SortHeader(string column, string current) =>
        column == current ? column + (column == "Name" ? "  ↑" : "  ↓") : column;

    public static string WhatsNew(string? version) => string.IsNullOrWhiteSpace(version) ? "What's new" : $"What's new in {version}";
}
