using Sentinel.Domain;

namespace Sentinel.Intelligence;

/// <summary>Distinguishes genuine network drops from disconnects that are a normal part of going to sleep.</summary>
public static class NetworkEvents
{
    public static IReadOnlyList<SystemEvent> GenuineDisconnects(IReadOnlyList<SystemEvent> disconnects, IReadOnlyList<SystemEvent> sleeps)
    {
        var sleepTimes = sleeps.Where(s => s.Category == EventCategory.Sleep).Select(s => s.Timestamp).ToList();
        return disconnects.Where(d =>
                d.Category == EventCategory.NetworkDisconnected
                && d.Detail?.Contains("sleep", StringComparison.OrdinalIgnoreCase) != true
                && d.Detail?.Contains("power", StringComparison.OrdinalIgnoreCase) != true
                && !sleepTimes.Any(t => (t - d.Timestamp).Duration() <= TimeSpan.FromMinutes(3)))
            .ToList();
    }
}
