using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Sentinel.App.Controls;

/// <summary>Resolves Sentinel's theme-dependent token colours from code, honouring the element's actual theme and High Contrast.</summary>
public static class ThemeColors
{
    private static readonly AccessibilitySettings Accessibility = new();

    public static Color Get(FrameworkElement element, string key)
    {
        var themeKey = Accessibility.HighContrast ? "HighContrast" : element.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
        {
            if (dict.ThemeDictionaries.TryGetValue(themeKey, out var t) && t is ResourceDictionary rd && rd.TryGetValue(key, out var v))
            {
                if (v is Color c) return c;
                if (v is SolidColorBrush b) return b.Color;
            }
        }
        return Application.Current.Resources.TryGetValue(key, out var fallback) && fallback is Color fc ? fc : Color.FromArgb(255, 128, 128, 128);
    }

    public static SolidColorBrush Brush(FrameworkElement element, string key) => new(Get(element, key));

    /// <summary>The theme dictionary's own brush (keeping its opacity) for the element's actual theme.</summary>
    public static Brush ThemeBrush(FrameworkElement element, string key)
    {
        var themeKey = Accessibility.HighContrast ? "HighContrast" : element.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
        {
            if (dict.ThemeDictionaries.TryGetValue(themeKey, out var t) && t is ResourceDictionary rd && rd.TryGetValue(key, out var v))
            {
                if (v is Brush b) return b;
                if (v is Color c) return new SolidColorBrush(c);
            }
        }
        return Application.Current.Resources.TryGetValue(key, out var fallback) && fallback is Brush fb ? fb : new SolidColorBrush(Get(element, key));
    }

    public static SolidColorBrush Series(FrameworkElement element, int index) => Brush(element, $"ChartSeries{Math.Clamp(index, 1, 6)}Color");

    public static SolidColorBrush Status(FrameworkElement element, Domain.HealthStatus status) => Brush(element, status switch
    {
        Domain.HealthStatus.Critical => "StatusCriticalColor",
        Domain.HealthStatus.Attention => "StatusAttentionColor",
        Domain.HealthStatus.Unknown => "StatusUnknownColor",
        Domain.HealthStatus.Normal => "StatusInfoColor",
        _ => "StatusHealthyColor",
    });

    public static SolidColorBrush Severity(FrameworkElement element, Domain.Severity severity) => Brush(element, severity switch
    {
        Domain.Severity.Critical => "StatusCriticalColor",
        Domain.Severity.Warning => "StatusAttentionColor",
        Domain.Severity.Notice => "StatusInfoColor",
        _ => "StatusUnknownColor",
    });
}
