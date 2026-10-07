using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Interop;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// Installed device drivers (Win32_PnPSignedDriver). Inventory only; Sentinel never installs, updates, rolls back
/// or removes drivers. Version history is derived by Sentinel from successive snapshots and Kernel-PnP events.
/// </summary>
public sealed class DriverProvider(ILogger<DriverProvider> log) : WindowsProvider(log), IDriverInventoryProvider
{
    private const string Source = "WMI Win32_PnPSignedDriver";
    private static readonly TimeSpan Refresh = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public override ProviderDescriptor Descriptor { get; } = Describe("drivers", "Drivers", "Drivers", SamplingCost.High, "Every 6 hours", Source);

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1));

    public IReadOnlyList<DriverInfo> Drivers { get; private set; } = [];
    public DateTimeOffset? LastRefreshed { get; private set; }

    public override Task InitializeAsync(CancellationToken ct) => RefreshAsync(ct);

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct) =>
        LastRefreshed is { } t && ctx.Now - t < Refresh ? Task.CompletedTask : RefreshAsync(ct);

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = await Task.Run(() => Wmi.TryQuery(Wmi.Cimv2,
                "SELECT DeviceName, DeviceClass, Manufacturer, DriverProviderName, DriverVersion, DriverDate, Signer, IsSigned, InfName, DeviceID FROM Win32_PnPSignedDriver", 4000), ct)
                .ConfigureAwait(false);
            var list = new List<DriverInfo>(rows.Count);
            foreach (var r in rows)
            {
                var name = r.Str("DeviceName");
                var id = r.Str("DeviceID");
                if (name is null || id is null || r.Str("DriverVersion") is null) continue;
                var cls = r.Str("DeviceClass") ?? "UNKNOWN";
                list.Add(new DriverInfo(name, cls, Group(cls, name), r.Str("Manufacturer"), r.Str("DriverProviderName"), r.Str("DriverVersion"),
                    r.Date("DriverDate"), r.Str("Signer"), r.Bool("IsSigned"), r.Str("InfName"), id));
            }
            Drivers = list.OrderBy(d => d.Group).ThenBy(d => d.DeviceName).ToList();
            LastRefreshed = DateTimeOffset.Now;
            SetCapability("Driver inventory", list.Count > 0, Source, list.Count == 0 ? "WMI did not return driver information" : null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string Group(string deviceClass, string name)
    {
        var c = deviceClass.ToUpperInvariant();
        if (c is "DISPLAY") return "Graphics";
        if (c is "MEDIA" or "AUDIOENDPOINT" or "AUDIOPROCESSINGOBJECT") return "Audio";
        if (c is "SOFTWARECOMPONENT" && name.Contains("Audio", StringComparison.OrdinalIgnoreCase)) return "Audio";
        if (c is "NET" or "NETSERVICE" or "NETTRANS" or "NETCLIENT") return "Networking";
        if (c is "DISKDRIVE" or "SCSIADAPTER" or "HDC" or "VOLUME" or "STORAGEVOLUME" or "VOLUMESNAPSHOT" or "CDROM") return "Storage";
        if (c is "BLUETOOTH") return "Bluetooth";
        if (c is "HIDCLASS" or "KEYBOARD" or "MOUSE" or "IMAGE" or "CAMERA" or "PRINTER" or "PRINTQUEUE" or "WPD" or "BIOMETRIC" or "SENSOR" or "XNACOMPOSITE") return "Peripherals";
        if (c is "USB") return "USB";
        if (c is "SYSTEM" && (name.Contains("Chipset", StringComparison.OrdinalIgnoreCase) || name.Contains("PCI", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SMBus", StringComparison.OrdinalIgnoreCase) || name.Contains("Management Engine", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Serial IO", StringComparison.OrdinalIgnoreCase) || name.Contains("LPC", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Thermal", StringComparison.OrdinalIgnoreCase) || name.Contains("Dynamic Tuning", StringComparison.OrdinalIgnoreCase)))
            return "Chipset";
        if (c is "FIRMWARE") return "Firmware";
        return "System";
    }
}
