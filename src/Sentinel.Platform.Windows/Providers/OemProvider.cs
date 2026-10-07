using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// Identifies the OEM from SMBIOS and notes which OEM control utility is installed, so Sentinel can point users to it.
/// No OEM-specific interface is called: none of the supported manufacturers publishes a documented, supportable,
/// read-only API that Sentinel can use without risking conflicts with the OEM's own software.
/// </summary>
public sealed class OemProvider(ILogger<OemProvider> log, ISystemInventoryProvider system) : WindowsProvider(log), IOemTelemetryProvider
{
    private static readonly (string Vendor, string Utility, string[] Services)[] Known =
    [
        ("ASUS", "Armoury Crate / MyASUS", ["ArmouryCrateService", "AsusAppService", "ASUSOptimization", "ASUSSystemAnalysis"]),
        ("Lenovo", "Lenovo Vantage", ["ImControllerService", "LenovoVantageService"]),
        ("Dell", "Dell Command / SupportAssist", ["DellClientManagementService", "SupportAssistAgent", "Dell SupportAssist Remediation"]),
        ("HP", "HP Support Assistant / Omen Gaming Hub", ["HPAppHelperCap", "HPSysInfoCap", "HpTouchpointAnalyticsService", "HPOmenCap"]),
        ("Micro-Star", "MSI Center", ["MSI_Center_Service", "MSI Central Service"]),
        ("Acer", "Acer Care Center / NitroSense", ["AcerQAAgent", "ACCSvc"]),
        ("Microsoft", "Surface app", ["SurfaceService"]),
        ("Samsung", "Samsung Settings", ["SamsungSystemSupportService"]),
        ("Razer", "Razer Synapse", ["Razer Synapse Service"]),
    ];

    public override ProviderDescriptor Descriptor { get; } = Describe("oem", "Manufacturer", "System", SamplingCost.Low, "Static",
        "SMBIOS (via system inventory)", "Service Control Manager (query only)");

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromHours(1), TimeSpan.FromHours(1), null);

    public OemInfo? Info { get; private set; }

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
        var inv = system.Inventory;
        var manufacturer = inv.Manufacturer;
        string? utility = null;
        try
        {
            var names = ServiceController.GetServices().Select(s =>
            {
                using (s) return s.ServiceName;
            }).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var k in Known)
            {
                if (!manufacturer.Contains(k.Vendor, StringComparison.OrdinalIgnoreCase) && !(k.Vendor == "Micro-Star" && manufacturer.Contains("MSI", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (k.Services.Any(names.Contains)) utility = k.Utility;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.LogDebug(ex, "Service enumeration failed");
        }

        var props = new List<InfoItem>
        {
            new("Manufacturer", manufacturer, "SMBIOS"),
            new("Model", inv.Model, "SMBIOS"),
            new("Product family", inv.ProductFamily, "SMBIOS"),
            new("SKU", inv.Sku, "SMBIOS"),
            new("OEM utility detected", utility ?? "None detected", "Installed services"),
        };
        var statement = utility is null
            ? "Sentinel observes; your manufacturer's tools control. No OEM control utility was detected."
            : $"Sentinel observes; {utility} controls. Performance profiles, fan modes and battery charge limits are managed there. " +
              "Sentinel does not read them because no documented, read-only interface is available — reverse-engineered methods could conflict with the OEM software.";
        Info = new OemInfo(manufacturer, utility, props, statement);
        SetCapability("Manufacturer identity", true, "SMBIOS");
        SetCapability("OEM performance profile / charge limit", false, "None",
            "Not exposed through a documented read-only interface. Use your manufacturer's app.");
    }
}
