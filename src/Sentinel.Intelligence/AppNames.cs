using Sentinel.Telemetry;

namespace Sentinel.Intelligence;

/// <summary>
/// App history is keyed by executable name (stable across versions); this turns a key into the friendly name
/// the app currently reports, falling back to the executable name without its extension.
/// </summary>
public static class AppNames
{
    public static string Display(string key, ProviderSet providers)
    {
        var current = providers.Processes.Latest.Apps.FirstOrDefault(a => a.AppKey.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (current is not null) return current.DisplayName;
        return key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? key[..^4] : key;
    }
}
