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
}

public static class Text
{
    public static string WhatsNew(string? version) => string.IsNullOrWhiteSpace(version) ? "What's new" : $"What's new in {version}";
}
