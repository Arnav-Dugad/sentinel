using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Core.Settings;
using Sentinel.Domain;

namespace Sentinel.Platform.Windows.Services;

/// <summary>
/// Generates Windows' own battery report (powercfg /batteryreport /xml) when the user asks, and parses its
/// capacity history and recent usage. The report file is kept in Sentinel's temp folder and deleted after parsing.
/// </summary>
public sealed class BatteryReportService(TrustedToolRunner runner, SentinelPaths paths, ILogger<BatteryReportService> log) : IWindowsReportService
{
    public async Task<BatteryReportData?> GenerateBatteryReportAsync(CancellationToken ct)
    {
        var file = TrustedToolRunner.ConfinePath(paths.Temp, $"battery-report-{Guid.NewGuid():N}.xml");
        try
        {
            var result = await runner.RunAsync("powercfg.exe", ["/batteryreport", "/xml", "/output", file], TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (result.TimedOut || !File.Exists(file))
            {
                log.LogWarning("Battery report was not produced (exit {Code})", result.ExitCode);
                return null;
            }
            var info = new FileInfo(file);
            if (info.Length > 32 * 1024 * 1024) return null;
            await using var stream = File.OpenRead(file);
            return Parse(stream);
        }
        finally
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Parses the battery report XML defensively (DTDs prohibited, unknown elements ignored).</summary>
    public static BatteryReportData? Parse(Stream xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64_000_000 };
        XDocument doc;
        using (var reader = XmlReader.Create(xml, settings)) doc = XDocument.Load(reader);
        var root = doc.Root;
        if (root is null) return null;

        static string? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
        static string? Attr(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
        static long? L(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        static DateTimeOffset? D(string? s) => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var v) ? v : null;

        var battery = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Battery");
        var history = new List<CapacityPoint>();
        foreach (var h in root.Descendants().Where(e => e.Name.LocalName == "HistoryEntry"))
        {
            var full = L(Attr(h, "FullChargeCapacity"));
            var design = L(Attr(h, "DesignCapacity"));
            var date = D(Attr(h, "LocalEndDate")) ?? D(Attr(h, "EndDate")) ?? D(Attr(h, "LocalStartDate"));
            if (full is > 0 && date is { } d) history.Add(new CapacityPoint(d, full.Value, design, "Windows battery report"));
        }

        var usage = new List<BatteryUsageEntry>();
        foreach (var u in root.Descendants().Where(e => e.Name.LocalName == "UsageEntry"))
        {
            var ts = D(Attr(u, "LocalTimestamp")) ?? D(Attr(u, "Timestamp"));
            if (ts is null) continue;
            TimeSpan duration = TimeSpan.Zero;
            if (Attr(u, "Duration") is { } dur)
            {
                try
                {
                    duration = XmlConvert.ToTimeSpan(dur);
                }
                catch (FormatException)
                {
                }
            }
            var charge = L(Attr(u, "ChargeCapacity"));
            var full = L(Attr(u, "FullChargeCapacity"));
            usage.Add(new BatteryUsageEntry(ts.Value, duration, Attr(u, "EntryType") ?? "Unknown", Attr(u, "Ac") == "1",
                charge is > 0 && full is > 0 ? charge * 100.0 / full : null, L(Attr(u, "Discharge"))));
        }

        return new BatteryReportData(Child(battery, "Manufacturer"), Child(battery, "Chemistry"), L(Child(battery, "DesignCapacity")),
            L(Child(battery, "FullChargeCapacity")), (int?)L(Child(battery, "CycleCount")), history, usage, null);
    }
}
