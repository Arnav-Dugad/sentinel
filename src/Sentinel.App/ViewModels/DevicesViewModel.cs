using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.App.Services;
using Sentinel.Core.Units;
using Sentinel.Data;
using Sentinel.Domain;

namespace Sentinel.App.ViewModels;

public sealed record DeviceRow(string Name, string Category, string Status, string Battery, string Glyph, string Seen, bool Connected);

public sealed record DisplayCard(DisplayInfo Info, string Title, string Subtitle, IReadOnlyList<InfoItem> Details);

public sealed record AudioRow(string Name, string Kind, string Detail, string Role);

public sealed record SessionMeter(string Name, double Level, string Device);

public sealed partial class DevicesViewModel : PageViewModel
{
    private int _tick;

    public ObservableCollection<DeviceRow> Devices { get; } = [];
    public ObservableCollection<EventRow> Timeline { get; } = [];
    public ObservableCollection<DisplayCard> Displays { get; } = [];
    public ObservableCollection<AudioRow> AudioDevices { get; } = [];
    public ObservableCollection<SessionMeter> Sessions { get; } = [];
    public SensitiveInfoState Sensitive { get; } = App.Services.GetRequiredService<SensitiveInfoState>();

    [ObservableProperty] public partial int Section { get; set; }
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Computer { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Security { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<InfoItem> Oem { get; set; } = [];
    [ObservableProperty] public partial string OemStatement { get; set; } = "";
    [ObservableProperty] public partial string Problems { get; set; } = "";

    public event EventHandler? DisplaysChanged;

    partial void OnSectionChanged(int value) => Refresh();

    protected override void OnActivated(object? parameter)
    {
        BuildComputer();
        BuildDisplays();
    }

    protected override void Refresh()
    {
        _tick++;
        switch (Section)
        {
            case 0 when _tick % 2 == 0 || Devices.Count == 0:
                BuildDevices();
                break;
            case 1 when _tick % 10 == 0:
                BuildDisplays();
                break;
            case 2:
                BuildAudio();
                break;
            case 3 when _tick % 30 == 0:
                BuildComputer();
                break;
        }
    }

    private void BuildDevices()
    {
        var rows = P.Devices.Devices.Select(d => new DeviceRow(d.Name, d.Category, d.Connected ? "Connected" : "Not connected",
            d.BatteryPercent is { } b ? $"Battery {b}%" : "", Glyph(d.Category), d.Connected
                ? d.LastConnected is { } lc ? $"Connected {lc:t}" : "Connected before Sentinel started"
                : d.LastDisconnected is { } ld ? "Disconnected " + UnitFormatter.When(ld) : "Paired / previously seen", d.Connected)).ToList();
        if (!Devices.SequenceEqual(rows))
        {
            Devices.Clear();
            foreach (var r in rows) Devices.Add(r);
        }
        var now = DateTimeOffset.Now;
        var events = Services.GetRequiredService<HistoryStore>().QueryEvents(now.AddDays(-7), now,
            [EventCategory.DeviceConnected, EventCategory.DeviceDisconnected, EventCategory.DeviceProblem], 80).Select(e => new EventRow(e)).ToList();
        if (!Timeline.SequenceEqual(events))
        {
            Timeline.Clear();
            foreach (var e in events) Timeline.Add(e);
        }
        var problems = events.Where(e => e.Event.Category == EventCategory.DeviceProblem).Select(e => e.Event.Subject).Distinct().Count();
        Problems = problems == 0 ? "" : $"{problems} device(s) reported a problem to Device Manager this week. Sentinel lists them here; repairs and driver changes are done in Device Manager or Windows Update.";
    }

    private static string Glyph(string category)
    {
        var c = category.ToLowerInvariant();
        if (c.Contains("mouse", StringComparison.Ordinal)) return "";
        if (c.Contains("keyboard", StringComparison.Ordinal)) return "";
        if (c.Contains("audio", StringComparison.Ordinal) || c.Contains("head", StringComparison.Ordinal) || c.Contains("speaker", StringComparison.Ordinal)) return "";
        if (c.Contains("display", StringComparison.Ordinal) || c.Contains("monitor", StringComparison.Ordinal)) return "";
        if (c.Contains("storage", StringComparison.Ordinal) || c.Contains("drive", StringComparison.Ordinal)) return "";
        if (c.Contains("camera", StringComparison.Ordinal) || c.Contains("image", StringComparison.Ordinal)) return "";
        if (c.Contains("phone", StringComparison.Ordinal)) return "";
        if (c.Contains("game", StringComparison.Ordinal) || c.Contains("controller", StringComparison.Ordinal)) return "";
        if (c.Contains("receiver", StringComparison.Ordinal)) return "";
        if (c.Contains("hub", StringComparison.Ordinal) || c.Contains("dock", StringComparison.Ordinal)) return "";
        if (c.Contains("print", StringComparison.Ordinal)) return "";
        return "";
    }

    private void BuildDisplays()
    {
        Displays.Clear();
        foreach (var d in P.Display.Displays)
        {
            Displays.Add(new DisplayCard(d, d.FriendlyName, $"{(d.IsInternal ? "Built-in" : "External")} · {d.Connection}" + (d.IsPrimary ? " · Main display" : ""),
            [
                new("Resolution", $"{d.Width} × {d.Height}", "QueryDisplayConfig"),
                new("Refresh rate", $"{d.RefreshHz:0.##} Hz", "QueryDisplayConfig"),
                new("Scale", $"{d.ScalePercent:F0}%", "GetDpiForMonitor"),
                new("Orientation", d.Rotation == 0 ? "Landscape" : $"Rotated {d.Rotation}°", "QueryDisplayConfig"),
                new("HDR", d.HdrSupported switch { true => d.HdrEnabled == true ? "Supported, on" : "Supported, off", false => "Not supported", _ => null }, "Advanced color info"),
                new("Color depth", d.BitsPerColor is { } b ? $"{b} bits per channel" : null, "Advanced color info"),
                new("Graphics adapter", d.GpuName, "DXGI"),
                new("Manufacturer code", d.ManufacturerCode, "EDID"),
                new("Variable refresh rate", null, "Not exposed through a documented read API"),
            ]));
        }
        DisplaysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BuildAudio()
    {
        var rows = P.Audio.Devices.Where(d => d.State is "Active" or "Unplugged")
            .OrderBy(d => d.IsCapture).ThenByDescending(d => d.IsDefault).ThenBy(d => d.Name)
            .Select(d => new AudioRow(d.Name, d.IsCapture ? "Recording" : "Playback",
                $"{d.State}" + (d.SampleRate is { } sr ? $" · {sr / 1000.0:0.#} kHz" : "") + (d.Channels is { } ch ? $" · {ch} channel{(ch == 1 ? "" : "s")}" : ""),
                string.Join(" · ", new[] { d.IsDefault ? "Default" : null, d.IsDefaultCommunications ? "Communications" : null }.Where(s => s is not null)))).ToList();
        if (!AudioDevices.SequenceEqual(rows))
        {
            AudioDevices.Clear();
            foreach (var r in rows) AudioDevices.Add(r);
        }
        var sessions = P.Audio.Sessions.Where(s => s.IsActive).Select(s => new SessionMeter(s.DisplayName, Math.Round(s.PeakLevel * 100), s.DeviceName)).ToList();
        Sessions.Clear();
        foreach (var s in sessions) Sessions.Add(s);
    }

    private void BuildComputer()
    {
        var i = P.System.Inventory;
        Computer =
        [
            new("Manufacturer", i.Manufacturer, "SMBIOS"),
            new("Model", i.Model, "SMBIOS"),
            new("Product family", i.ProductFamily, "SMBIOS"),
            new("SKU", i.Sku, "SMBIOS"),
            new("Motherboard", i.Baseboard, "SMBIOS"),
            new("Motherboard revision", i.BaseboardVersion, "SMBIOS"),
            new("BIOS / UEFI", $"{i.BiosVendor} {i.BiosVersion}".Trim(), "SMBIOS"),
            new("BIOS date", i.BiosDate?.ToString("d", CultureInfo.CurrentCulture), "SMBIOS"),
            new("Firmware type", i.FirmwareType, "GetFirmwareType"),
            new("Chassis", i.ChassisType, "SMBIOS"),
            new("Architecture", i.SystemArchitecture, "Runtime"),
            new("Windows", $"{i.OsName} {i.OsVersion}", "Registry / WMI"),
            new("Build", $"{i.OsBuild} ({i.OsArchitecture})", "Registry"),
            new("Last startup", UnitFormatter.When(i.BootTime), "Operating system"),
            new("Uptime", Core.Units.UnitFormatter.Duration(DateTimeOffset.Now - i.BootTime), "Operating system"),
            new("Computer name", i.ComputerName, "WMI", Sensitive: true),
            new("Serial number", i.SerialNumber, "SMBIOS", Sensitive: true),
            new("System UUID", i.SystemUuid, "SMBIOS", Sensitive: true),
        ];
        var s = P.System.Security;
        Security =
        [
            new("Secure Boot", s.SecureBoot switch { true => "On", false => "Off", _ => null }, "Registry (read-only)"),
            new("Virtualization-based security", s.VbsRunning switch { true => "Running", false => "Not running", _ => null }, "Device Guard WMI"),
            new("Memory integrity (HVCI)", s.HvciRunning switch { true => "On", false => "Off", _ => null }, "Device Guard WMI"),
            new("Microsoft Defender real-time protection", s.DefenderRealtime switch { true => "On", false => "Off", _ => null }, "Defender WMI (status only)"),
            new("Defender definitions updated", s.DefenderSignatureUpdated is { } du ? UnitFormatter.When(du) : null, "Defender WMI"),
            new("Virtualization in firmware", i.VirtualizationFirmwareEnabled switch { true => "Enabled", false => "Disabled", _ => null }, "WMI"),
            new("Hypervisor present", i.HypervisorPresent switch { true => "Yes", false => "No", _ => null }, "WMI"),
        ];
        Oem = P.Oem.Info?.Properties ?? [];
        OemStatement = P.Oem.Info?.Statement ?? "";
    }
}
