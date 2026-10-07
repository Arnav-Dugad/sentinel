using Sentinel.Domain;

namespace Sentinel.Intelligence;

public sealed record SentinelNotification(string Key, string Title, string Body, Severity Severity, string? NavigateTo);

public interface INotificationSink
{
    void Show(SentinelNotification notification);
}

/// <summary>
/// Notifications must be rare and meaningful. Ordinary load, brief spikes and normal gaming never notify.
/// Each topic notifies at most once per 24 hours and no more than three notifications are shown per day.
/// </summary>
public sealed class NotificationPolicy(INotificationSink sink, TimeProvider time)
{
    private const int DailyLimit = 3;
    private static readonly TimeSpan TopicCooldown = TimeSpan.FromHours(24);

    private readonly Dictionary<string, DateTimeOffset> _lastByTopic = [];
    private readonly List<DateTimeOffset> _sent = [];
    private readonly Lock _gate = new();

    public bool Enabled { get; set; } = true;

    public void OnAnomaly(Anomaly a)
    {
        var topic = a.MetricKey switch
        {
            var k when k.StartsWith("events.", StringComparison.Ordinal) => k,
            var k when k.StartsWith("device.", StringComparison.Ordinal) => "device-flap",
            "cpu.util" => "idle-load",
            var k when k.Contains("temp", StringComparison.Ordinal) || k.StartsWith("thermal.", StringComparison.Ordinal) => "thermal",
            "bat.rate" => "battery-drain",
            _ => null,
        };
        if (topic is null) return;
        // Thermal and drain anomalies notify only when severe; event clusters always qualify (they are already rare).
        if (topic is "thermal" or "battery-drain" or "idle-load" && a.Severity < Severity.Warning) return;
        if (topic.StartsWith("events.", StringComparison.Ordinal) && topic is not ("events.Bugcheck" or "events.UnexpectedShutdown" or "events.DisplayDriverReset"
                or "events.NetworkDisconnected" or "events.StorageError" or "events.HardwareError"))
            return;
        Send(new SentinelNotification(topic, a.Title, a.Description, a.Severity, "Anomalies"));
    }

    public void OnHealth(HealthReport report)
    {
        foreach (var c in report.Categories.Where(c => c.Status == HealthStatus.Critical))
            Send(new SentinelNotification("health-critical-" + c.Key, $"{c.Name} needs attention", c.Summary, Severity.Critical, "Health"));
    }

    public void OnSleepDrain(PowerSession s)
    {
        if (s.PercentDelta is not { } d || s.Duration.TotalHours < 2) return;
        var perHour = -d / s.Duration.TotalHours;
        if (perHour < 3) return;
        Send(new SentinelNotification("sleep-drain", "Significant battery drain during sleep",
            $"The battery lost {-d:F0}% over {Core.Units.UnitFormatter.Duration(s.Duration)} of sleep ({perHour:F1}% per hour).", Severity.Notice, "Sleep"));
    }

    private void Send(SentinelNotification n)
    {
        if (!Enabled) return;
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_lastByTopic.TryGetValue(n.Key, out var last) && now - last < TopicCooldown) return;
            _sent.RemoveAll(t => now - t > TimeSpan.FromDays(1));
            if (_sent.Count >= DailyLimit) return;
            _lastByTopic[n.Key] = now;
            _sent.Add(now);
        }
        sink.Show(n);
    }
}

/// <summary>Used when the host cannot show notifications (tests, simulation).</summary>
public sealed class NullNotificationSink : INotificationSink
{
    public void Show(SentinelNotification notification)
    {
    }
}
