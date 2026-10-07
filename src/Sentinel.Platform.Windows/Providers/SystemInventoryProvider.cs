using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Interop;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>Computer, firmware and OS identity (SMBIOS via WMI, read once) and security posture (read-only).</summary>
public sealed class SystemInventoryProvider(ILogger<SystemInventoryProvider> log) : WindowsProvider(log), ISystemInventoryProvider
{
    private const string Smbios = "SMBIOS via WMI";
    private DateTimeOffset _lastSecurity = DateTimeOffset.MinValue;

    public override ProviderDescriptor Descriptor { get; } = Describe("system", "System inventory", "System", SamplingCost.Moderate, "Static",
        Smbios, "Registry (read-only)", "Device Guard WMI", "Microsoft Defender WMI (status only)");

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));

    public SystemInventory Inventory { get; private set; } = SystemInventory.Empty;
    public SecurityStatus Security { get; private set; } = new(null, null, null, null, null, null);

    public override Task InitializeAsync(CancellationToken ct)
    {
        var cs = Wmi.TryQuery(Wmi.Cimv2, "SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber, PCSystemType, HypervisorPresent, Name FROM Win32_ComputerSystem", 1).FirstOrDefault();
        var bb = Wmi.TryQuery(Wmi.Cimv2, "SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard", 1).FirstOrDefault();
        var bios = Wmi.TryQuery(Wmi.Cimv2, "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS", 1).FirstOrDefault();
        var enc = Wmi.TryQuery(Wmi.Cimv2, "SELECT ChassisTypes FROM Win32_SystemEnclosure", 1).FirstOrDefault();
        var prod = Wmi.TryQuery(Wmi.Cimv2, "SELECT UUID FROM Win32_ComputerSystemProduct", 1).FirstOrDefault();
        var os = Wmi.TryQuery(Wmi.Cimv2, "SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime FROM Win32_OperatingSystem", 1).FirstOrDefault();
        var cpu = Wmi.TryQuery(Wmi.Cimv2, "SELECT VirtualizationFirmwareEnabled FROM Win32_Processor", 1).FirstOrDefault();

        string? displayVersion = null;
        int? ubr = null;
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
        {
            displayVersion = k?.GetValue("DisplayVersion") as string;
            ubr = k?.GetValue("UBR") as int?;
        }

        var chassis = ChassisName(enc?.Raw("ChassisTypes") is ushort[] types && types.Length > 0 ? types[0] : null);
        var pcType = cs?.Long("PCSystemType");
        var hasBattery = Native.GetSystemPowerStatus(out var ps) && ps.BatteryFlag is not 128 and not 255;
        var isLaptop = pcType == 2 || chassis is "Laptop" or "Notebook" or "Portable" or "Convertible" or "Detachable" or "Tablet" || hasBattery;
        var build = os?.Str("BuildNumber") ?? Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var firmware = Native.GetFirmwareType(out var ft) ? ft switch { 1 => "Legacy BIOS", 2 => "UEFI", _ => "Unknown" } : "Unknown";
        var boot = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

        Inventory = new SystemInventory(
            cs?.Str("Manufacturer") ?? "Unknown",
            cs?.Str("Model") ?? "Unknown",
            cs?.Str("SystemFamily"),
            cs?.Str("SystemSKUNumber"),
            Join(bb?.Str("Manufacturer"), bb?.Str("Product")),
            bb?.Str("Version"),
            bios?.Str("Manufacturer"),
            bios?.Str("SMBIOSBIOSVersion"),
            bios?.Date("ReleaseDate"),
            firmware,
            ReadSecureBoot(),
            chassis,
            RuntimeInformation.OSArchitecture.ToString(),
            os?.Str("OSArchitecture") ?? (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit"),
            os?.Str("Caption") ?? "Windows",
            displayVersion ?? os?.Str("Version") ?? "",
            ubr is { } u ? $"{build}.{u}" : build,
            cpu?.Bool("VirtualizationFirmwareEnabled"),
            cs?.Bool("HypervisorPresent"),
            null,
            null,
            os?.Date("LastBootUpTime") ?? boot,
            bios?.Str("SerialNumber"),
            prod?.Str("UUID"),
            cs?.Str("Name"),
            isLaptop);

        RefreshSecurity();
        SetCapability("SMBIOS inventory", cs is not null, Smbios, cs is null ? "WMI is unavailable" : null);
        return Task.CompletedTask;
    }

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        if (ctx.Now - _lastSecurity > TimeSpan.FromMinutes(30)) RefreshSecurity();
        return Task.CompletedTask;
    }

    private void RefreshSecurity()
    {
        bool? vbs = null, hvci = null, defender = null;
        DateTimeOffset? sigs = null;
        var dg = Wmi.TryQuery(Wmi.DeviceGuardNs, "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard", 1).FirstOrDefault();
        if (dg is not null)
        {
            vbs = dg.Long("VirtualizationBasedSecurityStatus") == 2;
            hvci = dg.Raw("SecurityServicesRunning") is uint[] running ? running.Contains(2u) : null;
        }
        var mp = Wmi.TryQuery(Wmi.DefenderNs, "SELECT RealTimeProtectionEnabled, AntivirusSignatureLastUpdated FROM MSFT_MpComputerStatus", 1).FirstOrDefault();
        if (mp is not null)
        {
            defender = mp.Bool("RealTimeProtectionEnabled");
            sigs = mp.Date("AntivirusSignatureLastUpdated");
        }
        var secureBoot = ReadSecureBoot();
        Security = new SecurityStatus(secureBoot, vbs, hvci, defender, sigs, "Registry, Device Guard WMI, Defender WMI");
        Inventory = Inventory with { SecureBoot = secureBoot, VbsRunning = vbs, HvciRunning = hvci };
        _lastSecurity = DateTimeOffset.Now;
    }

    private static bool? ReadSecureBoot()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return k?.GetValue("UEFISecureBootEnabled") is int v ? v == 1 : null;
    }

    private static string? Join(string? a, string? b) =>
        string.Join(" ", new[] { a, b }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } s ? s : null;

    private static string? ChassisName(ushort? t) => t switch
    {
        3 => "Desktop",
        4 => "Low-profile desktop",
        6 => "Mini tower",
        7 => "Tower",
        8 => "Portable",
        9 => "Laptop",
        10 => "Notebook",
        13 => "All-in-one",
        14 => "Sub-notebook",
        30 => "Tablet",
        31 => "Convertible",
        32 => "Detachable",
        35 => "Mini PC",
        36 => "Stick PC",
        null => null,
        _ => "Other",
    };
}
