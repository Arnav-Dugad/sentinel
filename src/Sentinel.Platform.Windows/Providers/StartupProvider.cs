using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sentinel.Core.Providers;
using Sentinel.Domain;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// Startup apps from the documented Run keys and Startup folders, with the enabled state Windows shows in
/// Settings (StartupApproved). Boot impact uses Windows' own boot diagnostics when they are readable.
/// Sentinel never disables or removes startup items — users are sent to Settings › Apps › Startup.
/// </summary>
public sealed class StartupProvider(ILogger<StartupProvider> log) : WindowsProvider(log), IStartupProvider
{
    private const string Source = "Registry Run keys and Startup folders (read-only)";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public override ProviderDescriptor Descriptor { get; } = Describe("startup", "Startup apps", "Startup", SamplingCost.Low, "Every 10 minutes",
        Source, "Diagnostics-Performance event log (boot impact)");

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));

    public IReadOnlyList<StartupItem> Items { get; private set; } = [];

    public override Task InitializeAsync(CancellationToken ct)
    {
        Read();
        return Task.CompletedTask;
    }

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        Read();
        return Task.CompletedTask;
    }

    private void Read()
    {
        var impact = ReadBootDegradation();
        var items = new List<StartupItem>();
        void FromKey(RegistryKey hive, string path, string location, string approvedSub)
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) return;
            using var approved = hive.OpenSubKey(ApprovedRoot + "\\" + approvedSub);
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var command = key.GetValue(name) as string;
                items.Add(Make(name, command, location, IsEnabled(approved, name), impact));
            }
        }

        FromKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Registry (current user)", "Run");
        FromKey(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "Registry (all users)", "Run");
        FromKey(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Registry (all users, 32-bit)", "Run32");

        foreach (var (folder, hive, label) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "Startup folder (current user)"),
                     (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "Startup folder (all users)"),
                 })
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            using var approved = hive.OpenSubKey(ApprovedRoot + "\\StartupFolder");
            foreach (var file in Directory.EnumerateFiles(folder).Take(200))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(Make(Path.GetFileNameWithoutExtension(file), file, label, IsEnabled(approved, fileName), impact));
            }
        }
        Items = items.OrderByDescending(i => i.Enabled).ThenByDescending(i => i.Impact).ThenBy(i => i.Name).ToList();
        SetCapability("Startup apps", true, Source);
        SetCapability("Boot impact", impact.Count > 0, "Diagnostics-Performance event log",
            impact.Count > 0 ? null : "Windows has not recorded startup slowdowns that Sentinel can read without elevation.");
    }

    private static StartupItem Make(string name, string? command, string location, bool enabled, Dictionary<string, double> impact)
    {
        var target = ExtractPath(command);
        string? publisher = null;
        if (target is not null && File.Exists(target))
        {
            try
            {
                publisher = FileVersionInfo.GetVersionInfo(target).CompanyName?.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        var exe = target is null ? null : Path.GetFileName(target);
        StartupImpact level = StartupImpact.Unknown;
        string? evidence = null;
        if (exe is not null && impact.TryGetValue(exe, out var ms))
        {
            level = ms >= 3000 ? StartupImpact.High : ms >= 1000 ? StartupImpact.Moderate : StartupImpact.Low;
            evidence = $"Windows recorded this app delaying startup by up to {ms / 1000:F1} s.";
        }
        return new StartupItem(Core.Privacy.Redactor.SanitizeUntrusted(name, 96), string.IsNullOrWhiteSpace(publisher) ? null : Core.Privacy.Redactor.SanitizeUntrusted(publisher, 96),
            location, target is null ? null : Core.Privacy.Redactor.Redact(target), enabled, level, evidence);
    }

    // StartupApproved values: first byte 0x02/0x06 = enabled, 0x03/0x07 = disabled. Absent = enabled.
    private static bool IsEnabled(RegistryKey? approved, string name) =>
        approved?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 0x1) == 0;

    internal static string? ExtractPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
    }

    /// <summary>Event 101 records apps that slowed startup, with their degradation time.</summary>
    private Dictionary<string, double> ReadBootDegradation()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName,
                "*[System[(EventID=101) and TimeCreated[timediff(@SystemTime) <= 7776000000]]]"));
            for (var i = 0; i < 500; i++)
            {
                using var r = reader.ReadEvent();
                if (r is null) break;
                var doc = XElement.Parse(r.ToXml());
                XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
                var data = doc.Element(ns + "EventData")?.Elements(ns + "Data").ToDictionary(d => d.Attribute("Name")?.Value ?? "", d => d.Value) ?? [];
                if (!data.TryGetValue("FileName", out var file) || !data.TryGetValue("DegradationTime", out var deg)) continue;
                if (!double.TryParse(deg, System.Globalization.CultureInfo.InvariantCulture, out var ms)) continue;
                var key = Path.GetFileName(file);
                result[key] = Math.Max(result.GetValueOrDefault(key), ms);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or EventLogException or System.Xml.XmlException or ArgumentException)
        {
            Log.LogDebug(ex, "Boot diagnostics log not readable");
        }
        return result;
    }
}
