using System.Globalization;
using System.Management;
using Sentinel.Core.Privacy;

namespace Sentinel.Platform.Windows.Interop;

/// <summary>
/// Read-only WMI/CIM queries for static or slow-changing information. Never used as a real-time sensor bus.
/// All strings are treated as untrusted and sanitised.
/// </summary>
internal static class Wmi
{
    public sealed class Row(Dictionary<string, object?> values)
    {
        public string? Str(string name) =>
            values.TryGetValue(name, out var v) && v is not null ? NullIfEmpty(Redactor.SanitizeUntrusted(Convert.ToString(v, CultureInfo.InvariantCulture))) : null;

        public long? Long(string name)
        {
            if (!values.TryGetValue(name, out var v) || v is null) return null;
            try
            {
                return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return null;
            }
        }

        public bool? Bool(string name) => values.TryGetValue(name, out var v) && v is bool b ? b : null;

        public object? Raw(string name) => values.TryGetValue(name, out var v) ? v : null;

        public DateTimeOffset? Date(string name)
        {
            var s = Str(name);
            if (s is null) return null;
            try
            {
                return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(s));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static List<Row> Query(string scope, string wql, int maxRows = 5000, TimeSpan? timeout = null)
    {
        var rows = new List<Row>();
        var options = new System.Management.EnumerationOptions { Timeout = timeout ?? TimeSpan.FromSeconds(20), ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
        using var results = searcher.Get();
        foreach (var obj in results)
        {
            using (obj)
            {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in obj.Properties) dict[p.Name] = p.Value;
                rows.Add(new Row(dict));
            }
            if (rows.Count >= maxRows) break;
        }
        return rows;
    }

    public static List<Row> TryQuery(string scope, string wql, int maxRows = 5000)
    {
        try
        {
            return Query(scope, wql, maxRows);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            return [];
        }
    }

    public const string Cimv2 = @"root\cimv2";
    public const string StorageNs = @"root\Microsoft\Windows\Storage";
    public const string DeviceGuardNs = @"root\Microsoft\Windows\DeviceGuard";
    public const string DefenderNs = @"root\Microsoft\Windows\Defender";
}
