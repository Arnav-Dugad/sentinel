using System.Globalization;
using System.Text.RegularExpressions;

namespace Sentinel.Intelligence;

public enum QueryIntent
{
    Unknown,
    Overview,
    Heat,
    BatteryDrain,
    BatteryHealth,
    Storage,
    Slowdown,
    Restart,
    Crashes,
    Memory,
    DriverChange,
    TopApps,
    Network,
    Gaming,
    Sleep,
    WhatChanged,
    Navigate,
}

public sealed record ParsedQuery(string Text, QueryIntent Intent, TimeRange Range, bool RangeExplicit, DateTimeOffset? PointInTime, string? NavigateTarget, string? Subject);

/// <summary>
/// Deterministic natural-language parsing: intent from keyword groups, time range from common phrases.
/// Works without any language model; the optional local AI only rephrases the resulting evidence.
/// </summary>
public static partial class QueryParser
{
    private static readonly (QueryIntent Intent, string[] Keywords)[] Rules =
    [
        (QueryIntent.WhatChanged, ["what changed", "what's changed", "what has changed", "changes since", "changed after", "changed since"]),
        (QueryIntent.DriverChange, ["driver update", "driver change", "after my driver", "nvidia driver", "amd driver", "intel driver", "drivers", "driver"]),
        (QueryIntent.Restart, ["restart", "reboot", "blue screen", "bsod", "stop error", "bugcheck", "shut down", "shutdown", "turned off", "power off"]),
        (QueryIntent.Crashes, ["crash", "crashed", "crashing", "not responding", "freeze", "froze", "hung", "hangs", "hanging"]),
        (QueryIntent.BatteryHealth, ["battery health", "battery capacity", "battery deteriorat", "battery degrad", "battery wear", "battery aging", "battery ageing", "cycle count"]),
        (QueryIntent.BatteryDrain, ["battery drain", "drain", "battery life", "battery die", "losing battery", "battery quickly", "battery fast", "battery"]),
        (QueryIntent.Sleep, ["sleep", "standby", "hibernat", "woke up", "wake"]),
        (QueryIntent.Heat, ["hot", "heat", "temperature", "temp", "thermal", "overheat", "fan", "warm"]),
        (QueryIntent.Storage, ["ssd", "nvme", "hard drive", "disk", "drive health", "storage", "smart"]),
        (QueryIntent.Gaming, ["gaming", "game", "fps", "frame"]),
        (QueryIntent.Memory, ["memory", "ram", "commit", "page file", "pagefile", "leak"]),
        (QueryIntent.TopApps, ["which app", "which application", "what app", "consume", "most resources", "resource hog", "using the most", "top app"]),
        (QueryIntent.Network, ["wifi", "wi-fi", "internet", "network", "disconnect", "ethernet", "latency", "ping"]),
        (QueryIntent.Slowdown, ["slow", "lag", "sluggish", "stutter", "performance", "unresponsive"]),
        (QueryIntent.Overview, ["normal", "healthy", "health", "okay", "ok?", "how is my", "how's my", "status", "anything wrong", "behaving"]),
    ];

    private static readonly (string Phrase, string Target)[] NavigateTargets =
    [
        ("cpu", "CPU"), ("processor", "CPU"), ("gpu", "GPU"), ("graphics", "GPU"), ("memory", "Memory"), ("ram", "Memory"),
        ("storage", "Storage"), ("ssd", "Storage"), ("disk", "Storage"), ("drive", "Storage"), ("battery", "Battery"),
        ("network", "Network"), ("wifi", "Network"), ("wi-fi", "Network"), ("thermal", "Thermals"), ("temperature", "Thermals"),
        ("device", "Devices"), ("display", "Devices"), ("monitor", "Devices"), ("audio", "Devices"), ("usb", "Devices"), ("bluetooth", "Devices"),
        ("health", "Health"), ("timeline", "Timeline"), ("anomal", "Anomalies"), ("reliability", "Reliability"), ("crash", "Reliability"),
        ("process", "Processes"), ("task manager", "Processes"), ("startup", "Startup"), ("driver", "Drivers"), ("update", "Updates"),
        ("sleep", "Sleep"), ("power", "Power"), ("report", "Reports"), ("setting", "Settings"), ("compare", "Compare"), ("time machine", "TimeMachine"),
    ];

    [GeneratedRegex(@"\b(?:last|past)\s+(\d{1,3})\s*(minute|min|hour|hr|day|week|month)s?\b", RegexOptions.IgnoreCase)]
    private static partial Regex LastN();

    [GeneratedRegex(@"\bat\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm|a\.m\.|p\.m\.)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex AtTime();

    [GeneratedRegex(@"\b(?:on|since)\s+(\d{4}-\d{2}-\d{2}|\w+\s+\d{1,2}(?:st|nd|rd|th)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OnDate();

    public static ParsedQuery Parse(string text, DateTimeOffset now, DateTimeOffset? lastBoot = null)
    {
        var t = (text ?? "").Trim();
        var lower = t.ToLowerInvariant();

        var intent = QueryIntent.Unknown;
        foreach (var (i, keys) in Rules)
        {
            if (keys.Any(k => lower.Contains(k, StringComparison.Ordinal)))
            {
                intent = i;
                break;
            }
        }

        string? navigate = null;
        if (lower.StartsWith("show ", StringComparison.Ordinal) || lower.StartsWith("open ", StringComparison.Ordinal) || lower.StartsWith("go to ", StringComparison.Ordinal)
            || lower.StartsWith("compare ", StringComparison.Ordinal))
        {
            navigate = lower.StartsWith("compare ", StringComparison.Ordinal) ? "Compare" : NavigateTargets.FirstOrDefault(n => lower.Contains(n.Phrase, StringComparison.Ordinal)).Target;
            if (navigate is not null) intent = QueryIntent.Navigate;
        }

        var (range, explicitRange, point) = ParseRange(lower, now, lastBoot);
        if (!explicitRange)
        {
            range = intent switch
            {
                QueryIntent.BatteryHealth or QueryIntent.Storage => TimeRange.Last(TimeSpan.FromDays(180), now, "the last 6 months"),
                QueryIntent.DriverChange or QueryIntent.WhatChanged => TimeRange.Last(TimeSpan.FromDays(14), now, "the last 2 weeks"),
                QueryIntent.TopApps or QueryIntent.Gaming => TimeRange.Last(TimeSpan.FromDays(7), now, "the last 7 days"),
                QueryIntent.Restart or QueryIntent.Crashes => TimeRange.Last(TimeSpan.FromDays(7), now, "the last 7 days"),
                QueryIntent.Overview => TimeRange.Last(TimeSpan.FromHours(24), now, "the last 24 hours"),
                _ => TimeRange.Today(now),
            };
        }

        string? subject = null;
        foreach (var vendor in new[] { "nvidia", "amd", "intel", "realtek", "qualcomm", "mediatek" })
            if (lower.Contains(vendor, StringComparison.Ordinal)) subject = vendor;
        return new ParsedQuery(t, intent, range, explicitRange, point, navigate, subject);
    }

    internal static (TimeRange Range, bool Explicit, DateTimeOffset? Point) ParseRange(string lower, DateTimeOffset now, DateTimeOffset? lastBoot)
    {
        var today = TimeRange.StartOfDay(now);
        var dayBase = today;
        var dayLabel = "today";
        var explicitDay = false;
        if (lower.Contains("yesterday", StringComparison.Ordinal))
        {
            dayBase = today.AddDays(-1);
            dayLabel = "yesterday";
            explicitDay = true;
        }
        else if (lower.Contains("today", StringComparison.Ordinal) || lower.Contains("this morning", StringComparison.Ordinal)
                 || lower.Contains("this afternoon", StringComparison.Ordinal) || lower.Contains("tonight", StringComparison.Ordinal)
                 || lower.Contains("this evening", StringComparison.Ordinal))
        {
            explicitDay = true;
        }

        var at = AtTime().Match(lower);
        if (at.Success)
        {
            var hour = int.Parse(at.Groups[1].Value, CultureInfo.InvariantCulture);
            var minute = at.Groups[2].Success ? int.Parse(at.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            var ampm = at.Groups[3].Value.Replace(".", "", StringComparison.Ordinal);
            if (ampm == "pm" && hour < 12) hour += 12;
            if (ampm == "am" && hour == 12) hour = 0;
            if (hour is >= 0 and < 24 && minute is >= 0 and < 60)
            {
                var point = dayBase.AddHours(hour).AddMinutes(minute);
                if (!explicitDay && point > now) point = point.AddDays(-1);
                return (new TimeRange(point.AddMinutes(-45), point.AddMinutes(30), $"around {point:t}{(dayLabel == "yesterday" ? " yesterday" : "")}"), true, point);
            }
        }

        if (lower.Contains("this morning", StringComparison.Ordinal)) return (new TimeRange(dayBase.AddHours(5), Min(dayBase.AddHours(12), now), "this morning"), true, null);
        if (lower.Contains("this afternoon", StringComparison.Ordinal)) return (new TimeRange(dayBase.AddHours(12), Min(dayBase.AddHours(18), now), "this afternoon"), true, null);
        if (lower.Contains("tonight", StringComparison.Ordinal) || lower.Contains("this evening", StringComparison.Ordinal) || lower.Contains("last night", StringComparison.Ordinal))
        {
            var start = lower.Contains("last night", StringComparison.Ordinal) ? today.AddDays(-1).AddHours(18) : dayBase.AddHours(18);
            return (new TimeRange(start, Min(start.AddHours(14), now), lower.Contains("last night", StringComparison.Ordinal) ? "last night" : "this evening"), true, null);
        }

        var lastN = LastN().Match(lower);
        if (lastN.Success)
        {
            var n = Math.Clamp(int.Parse(lastN.Groups[1].Value, CultureInfo.InvariantCulture), 1, 400);
            var unit = lastN.Groups[2].Value;
            var span = unit switch
            {
                "minute" or "min" => TimeSpan.FromMinutes(n),
                "hour" or "hr" => TimeSpan.FromHours(n),
                "week" => TimeSpan.FromDays(7 * n),
                "month" => TimeSpan.FromDays(30 * n),
                _ => TimeSpan.FromDays(n),
            };
            return (TimeRange.Last(span, now, $"the last {n} {unit}{(n == 1 ? "" : "s")}"), true, null);
        }

        if (lower.Contains("since last restart", StringComparison.Ordinal) || lower.Contains("since restart", StringComparison.Ordinal) || lower.Contains("since boot", StringComparison.Ordinal))
            return (new TimeRange(lastBoot ?? now.AddHours(-24), now, "since the last restart"), true, null);
        if (lower.Contains("this week", StringComparison.Ordinal)) return (TimeRange.Last(TimeSpan.FromDays(7), now, "this week"), true, null);
        if (lower.Contains("last week", StringComparison.Ordinal)) return (new TimeRange(now.AddDays(-14), now.AddDays(-7), "last week"), true, null);
        if (lower.Contains("this month", StringComparison.Ordinal)) return (TimeRange.Last(TimeSpan.FromDays(30), now, "this month"), true, null);
        if (lower.Contains("last month", StringComparison.Ordinal)) return (new TimeRange(now.AddDays(-60), now.AddDays(-30), "last month"), true, null);
        if (lower.Contains("yesterday", StringComparison.Ordinal)) return (TimeRange.Yesterday(now), true, null);
        if (lower.Contains("today", StringComparison.Ordinal)) return (TimeRange.Today(now), true, null);

        var on = OnDate().Match(lower);
        if (on.Success && DateTime.TryParse(Regex.Replace(on.Groups[1].Value, @"(st|nd|rd|th)$", ""), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var date))
        {
            if (date > now.DateTime) date = date.AddYears(-1);
            var start = new DateTimeOffset(date.Date, now.Offset);
            return lower.Contains("since", StringComparison.Ordinal)
                ? (new TimeRange(start, now, $"since {start:d}"), true, null)
                : (new TimeRange(start, start.AddDays(1), $"on {start:d}"), true, null);
        }

        return (TimeRange.Today(now), false, null);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
