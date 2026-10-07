using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Interop;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>Connected displays, the GPU driving each one, modes, scale and HDR state. Never alters display settings.</summary>
public sealed class DisplayProvider(ILogger<DisplayProvider> log) : WindowsProvider(log), IDisplayTelemetryProvider
{
    private const string Source = "QueryDisplayConfig / DisplayConfigGetDeviceInfo";

    public override ProviderDescriptor Descriptor { get; } = Describe("display", "Displays", "Devices", SamplingCost.Low, "On change (polled)", Source, "GetDpiForMonitor", "DXGI");

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));

    public IReadOnlyList<DisplayInfo> Displays { get; private set; } = [];

    public override Task InitializeAsync(CancellationToken ct)
    {
        Read();
        SetCapability("Display topology", Displays.Count > 0, Source, Displays.Count == 0 ? "No active display paths (remote or headless session)" : null);
        SetCapability("Variable refresh rate", false, "None", "Windows does not expose per-display VRR capability through a documented read API.");
        return Task.CompletedTask;
    }

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        Read();
        return Task.CompletedTask;
    }

    private void Read()
    {
        var gpus = Dxgi.EnumerateAdapters().ToDictionary(a => a.Luid, a => a.Description);
        var dpi = MonitorDpi();
        var list = new List<DisplayInfo>();
        foreach (var p in DisplayConfig.Query())
        {
            var name = p.FriendlyName ?? (DisplayConfig.IsInternal(p.OutputTechnology) ? "Built-in display" : "Display");
            var scale = p.GdiDeviceName is not null && dpi.TryGetValue(p.GdiDeviceName, out var d) ? d / 96.0 * 100 : 100;
            var rotation = p.Rotation switch { 2 => 90, 3 => 180, 4 => 270, _ => 0 };
            list.Add(new DisplayInfo($"{p.AdapterLuid:x}-{p.TargetId}", name, DisplayConfig.EdidManufacturer(p.EdidManufacturerId),
                gpus.TryGetValue(p.AdapterLuid, out var g) ? g : null, p.Width, p.Height, Math.Round(p.RefreshHz, 2), rotation, Math.Round(scale),
                DisplayConfig.Connection(p.OutputTechnology), DisplayConfig.IsInternal(p.OutputTechnology), p.PositionX == 0 && p.PositionY == 0,
                p.HdrSupported, p.HdrEnabled, p.BitsPerColor, p.PositionX, p.PositionY));
        }
        Displays = list;
    }

    private static Dictionary<string, uint> MonitorDpi()
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfoW(monitor, ref info) && Native.GetDpiForMonitor(monitor, 0, out var x, out _) == 0) result[info.szDevice] = x;
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
