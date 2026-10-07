using System.Globalization;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sentinel.Core.Providers;
using Sentinel.Domain;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>Installed applications (Uninstall registry keys) and services (Service Control Manager), read-only.</summary>
public sealed class SoftwareInventoryProvider(ILogger<SoftwareInventoryProvider> log) : WindowsProvider(log), ISoftwareInventoryProvider
{
    public override ProviderDescriptor Descriptor { get; } = Describe("software", "Installed software", "Software", SamplingCost.Low, "Every 10 minutes",
        "Uninstall registry keys (read-only)", "Service Control Manager (query only)");

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30));

    public IReadOnlyList<SoftwareItem> Software { get; private set; } = [];
    public IReadOnlyList<ServiceItem> Services { get; private set; } = [];

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
        var apps = new Dictionary<string, SoftwareItem>(StringComparer.OrdinalIgnoreCase);
        void FromKey(RegistryKey hive, string path, string scope)
        {
            using var root = hive.OpenSubKey(path);
            if (root is null) return;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k is null) continue;
                if (k.GetValue("SystemComponent") is 1 || k.GetValue("ParentKeyName") is not null) continue;
                var name = k.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                DateTimeOffset? date = null;
                if (k.GetValue("InstallDate") is string d && DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                    date = parsed;
                var item = new SoftwareItem(Core.Privacy.Redactor.SanitizeUntrusted(name, 128), Core.Privacy.Redactor.SanitizeUntrusted(k.GetValue("DisplayVersion") as string, 48),
                    Core.Privacy.Redactor.SanitizeUntrusted(k.GetValue("Publisher") as string, 96), date, scope);
                apps.TryAdd(item.Name + "|" + item.Version, item);
            }
        }
        FromKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "All users");
        FromKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "All users (32-bit)");
        FromKey(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "Current user");
        Software = apps.Values.OrderBy(a => a.Name).ToList();

        var services = new List<ServiceItem>();
        try
        {
            foreach (var s in ServiceController.GetServices())
            {
                using (s)
                {
                    try
                    {
                        services.Add(new ServiceItem(s.ServiceName, Core.Privacy.Redactor.SanitizeUntrusted(s.DisplayName, 128), s.StartType.ToString(), s.Status.ToString()));
                    }
                    catch (InvalidOperationException)
                    {
                        // Service disappeared while enumerating.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.LogDebug(ex, "Service enumeration failed");
        }
        Services = services.OrderBy(s => s.DisplayName).ToList();
        SetCapability("Installed applications", Software.Count > 0, "Uninstall registry keys");
        SetCapability("Services", Services.Count > 0, "Service Control Manager");
    }
}
