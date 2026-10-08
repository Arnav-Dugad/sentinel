using System.Globalization;
using Sentinel.Core.Settings;
using Sentinel.Domain;

namespace Sentinel.Core.Units;

/// <summary>Formats values consistently and with technically correct units.</summary>
public sealed class UnitFormatter(ISettingsStore settings)
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;
    private static readonly string[] BinaryUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
    private static readonly string[] DecimalUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    private SentinelSettings S => settings.Current;

    public static UnitFormatter Default { get; } = new(new FixedSettings());

    public string Temperature(double? celsius, int decimals = 0)
    {
        if (celsius is not { } c || double.IsNaN(c)) return "—";
        return S.Temperature == TemperatureUnit.Fahrenheit
            ? (c * 9 / 5 + 32).ToString("F" + decimals, Culture) + " °F"
            : c.ToString("F" + decimals, Culture) + " °C";
    }

    /// <summary>Temperature difference (no offset for °F).</summary>
    public string TemperatureDelta(double celsiusDelta)
    {
        var v = S.Temperature == TemperatureUnit.Fahrenheit ? celsiusDelta * 9 / 5 : celsiusDelta;
        var unit = S.Temperature == TemperatureUnit.Fahrenheit ? "°F" : "°C";
        return v.ToString("+0;-0;0", Culture) + " " + unit;
    }

    public string TemperatureUnitSymbol => S.Temperature == TemperatureUnit.Fahrenheit ? "°F" : "°C";

    public double ToDisplayTemperature(double celsius) => S.Temperature == TemperatureUnit.Fahrenheit ? celsius * 9 / 5 + 32 : celsius;

    public string Bytes(double? bytes, int decimals = 1)
    {
        if (bytes is not { } b || double.IsNaN(b)) return "—";
        var binary = S.Bytes == ByteUnitSystem.Binary;
        var step = binary ? 1024.0 : 1000.0;
        var units = binary ? BinaryUnits : DecimalUnits;
        var i = 0;
        var v = Math.Abs(b);
        while (v >= step && i < units.Length - 1) { v /= step; i++; }
        var sign = b < 0 ? "-" : "";
        var d = i == 0 ? 0 : decimals;
        return sign + v.ToString("F" + d, Culture) + " " + units[i];
    }

    /// <summary>Capacity of a drive is always marketed in decimal units; show both when they differ materially.</summary>
    public string Capacity(long bytes) => Bytes(bytes, bytes >= 1L << 40 ? 2 : 0);

    public string Throughput(double? bytesPerSec)
    {
        if (bytesPerSec is not { } b || double.IsNaN(b)) return "—";
        if (S.Throughput == ThroughputUnit.BitsPerSecond)
        {
            var bits = b * 8;
            string[] u = ["bps", "Kbps", "Mbps", "Gbps"];
            var i = 0;
            while (bits >= 1000 && i < u.Length - 1) { bits /= 1000; i++; }
            return bits.ToString(i == 0 ? "F0" : "F1", Culture) + " " + u[i];
        }
        return Bytes(b) + "/s";
    }

    /// <summary>Rate for a metric key: disk metrics in bytes per second, network metrics per the user's setting.</summary>
    public string Rate(string metricKey, double? bytesPerSec) =>
        metricKey.StartsWith("disk.", StringComparison.Ordinal) ? DiskRate(bytesPerSec) : Throughput(bytesPerSec);

    /// <summary>Disk and file I/O rate. Always bytes per second; the bits setting applies to network speeds only.</summary>
    public string DiskRate(double? bytesPerSec) => bytesPerSec is not { } b || double.IsNaN(b) ? "—" : Bytes(b) + "/s";

    /// <summary>
    /// A compact, unambiguous timestamp for lists: "9:52 PM" today, "Yesterday 9:52 PM", "Mon 9:52 PM" this week,
    /// "7 Oct 9:52 PM" this year, otherwise a full short date. Uses the culture's own time pattern.
    /// </summary>
    public static string When(DateTimeOffset t, DateTimeOffset? now = null)
    {
        var local = t.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        var time = local.ToString("t", Culture);
        var day = local.Date;
        if (day == today) return time;
        if (day == today.AddDays(-1)) return "Yesterday " + time;
        if (day > today.AddDays(-7) && day < today) return local.ToString("ddd", Culture) + " " + time;
        if (day.Year == today.Year) return local.ToString(Culture.DateTimeFormat.MonthDayPattern.Contains('M', StringComparison.Ordinal) && Culture.DateTimeFormat.MonthDayPattern.StartsWith('d') ? "d MMM" : "MMM d", Culture) + " " + time;
        return local.ToString("d", Culture) + " " + time;
    }

    /// <summary>A date for lists: "Today", "Yesterday", "Mon", "7 Oct" or a short date.</summary>
    public static string Day(DateTimeOffset t, DateTimeOffset? now = null)
    {
        var day = t.ToLocalTime().Date;
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        if (day > today.AddDays(-7) && day < today) return day.ToString("dddd", Culture);
        return day.ToString(day.Year == today.Year ? "MMMM d" : "D", Culture);
    }

    /// <summary>
    /// An absolute, readable timestamp for sentences that may be stored or read later ("October 7, 11:01 PM";
    /// the year is added when it is not the current year). Never relative, so it cannot go stale.
    /// </summary>
    public static string Absolute(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        var date = local.ToString(Culture.DateTimeFormat.MonthDayPattern, Culture);
        if (local.Year != DateTimeOffset.Now.Year) date += ", " + local.Year.ToString(Culture);
        return date + ", " + local.ToString("t", Culture);
    }

    /// <summary>Full, explicit timestamp for tooltips and detail panes.</summary>
    public static string Full(DateTimeOffset t) => t.ToLocalTime().ToString("f", Culture);

    public string LinkSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "—";
        return bitsPerSecond >= 1_000_000_000
            ? (bitsPerSecond / 1e9).ToString("0.#", Culture) + " Gbps"
            : (bitsPerSecond / 1e6).ToString("0", Culture) + " Mbps";
    }

    public static string Percent(double? v, int decimals = 0) =>
        v is { } x && !double.IsNaN(x) ? x.ToString("F" + decimals, Culture) + "%" : "—";

    public static string Watts(double? w, int decimals = 1) =>
        w is { } x && !double.IsNaN(x) ? x.ToString("F" + decimals, Culture) + " W" : "—";

    public static string Frequency(double? mhz) =>
        mhz is not { } m || double.IsNaN(m) ? "—" : m >= 1000 ? (m / 1000).ToString("F2", Culture) + " GHz" : m.ToString("F0", Culture) + " MHz";

    public static string Rpm(double? rpm) => rpm is { } r ? r.ToString("F0", Culture) + " RPM" : "—";

    public static string Volts(double? millivolts) => millivolts is { } mv ? (mv / 1000).ToString("F2", Culture) + " V" : "—";

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m";
        if (t.TotalHours < 24) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }

    public static string Count(double? v) => v is { } x ? x.ToString("N0", Culture) : "—";

    public string Format(double? value, MetricUnit unit) => unit switch
    {
        MetricUnit.Percent => Percent(value),
        MetricUnit.Celsius => Temperature(value),
        MetricUnit.Watts => Watts(value),
        MetricUnit.Milliwatts => Watts(value / 1000),
        MetricUnit.MilliwattHours => value is { } mwh ? (mwh / 1000).ToString("F1", Culture) + " Wh" : "—",
        MetricUnit.Megahertz => Frequency(value),
        MetricUnit.Bytes => Bytes(value),
        MetricUnit.BytesPerSecond => Throughput(value),
        MetricUnit.PerSecond => value is { } ps ? ps.ToString("N0", Culture) + "/s" : "—",
        MetricUnit.Milliseconds => value is { } ms ? ms.ToString("F1", Culture) + " ms" : "—",
        MetricUnit.Rpm => Rpm(value),
        MetricUnit.Volts => Volts(value),
        MetricUnit.Count => Count(value),
        _ => value is { } v ? v.ToString("F1", Culture) : "—",
    };

    private sealed class FixedSettings : ISettingsStore
    {
        public SentinelSettings Current { get; } = new();
        public event EventHandler<SentinelSettings>? Changed { add { } remove { } }
        public void Update(Action<SentinelSettings> mutate) => mutate(Current);
    }
}
