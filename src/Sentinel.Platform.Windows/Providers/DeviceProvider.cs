using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Windows.Devices.Enumeration;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// External devices as people think of them (one entry per physical device container), with a live
/// connect/disconnect timeline from Windows' device watcher, reported battery levels (Bluetooth) and
/// device problem codes. Internal components of the PC itself are excluded.
/// </summary>
public sealed class DeviceProvider(ILogger<DeviceProvider> log) : WindowsProvider(log), IDeviceInventoryProvider
{
    private const string Source = "Windows.Devices.Enumeration";
    private const string PropConnected = "System.Devices.Connected";
    private const string PropLocalMachine = "System.Devices.LocalMachine";
    private const string PropCategory = "System.Devices.Category";
    private const string PropManufacturer = "System.Devices.Manufacturer";
    private const string PropBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string PropProblemCode = "{4340a6c5-93fa-4706-972c-7b648008a5a7} 3";
    private static readonly TimeSpan SlowRefresh = TimeSpan.FromMinutes(5);
    private static readonly string[] VirtualPrinters = ["Print to PDF", "OneNote", "XPS Document Writer", "Fax", "Send To OneNote", "Microsoft Print"];

    private DeviceWatcher? _watcher;
    private readonly ConcurrentDictionary<string, DeviceRecord> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<SystemEvent> _pending = new();
    private volatile bool _enumerated;
    private DateTimeOffset _lastSlow = DateTimeOffset.MinValue;
    private readonly HashSet<string> _reportedProblems = [];

    public override ProviderDescriptor Descriptor { get; } = Describe("devices", "Devices", "Devices", SamplingCost.Low, "Event-driven", Source);

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

    public IReadOnlyList<DeviceRecord> Devices => [.. _devices.Values.OrderByDescending(d => d.Connected).ThenBy(d => d.Name)];

    public override Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            StartWatcher();
            SetCapability("Device timeline", true, Source);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            SetCapability("Device timeline", false, Source, ex.Message);
        }
        return Task.CompletedTask;
    }

    private void StartWatcher()
    {
        _watcher = DeviceInformation.CreateWatcher("", [PropConnected, PropLocalMachine, PropCategory, PropManufacturer], DeviceInformationKind.DeviceContainer);
        _watcher.Added += (_, info) => Upsert(info.Id, info.Name, info.Properties, added: true);
        _watcher.Updated += (_, update) => Upsert(update.Id, null, update.Properties, added: false);
        _watcher.Removed += (_, update) =>
        {
            if (_devices.TryRemove(update.Id, out var d) && d.Connected) Publish(d, connected: false);
        };
        _watcher.EnumerationCompleted += (_, _) => _enumerated = true;
        _watcher.Start();
    }

    private void Upsert(string id, string? name, IReadOnlyDictionary<string, object> props, bool added)
    {
        if (props.TryGetValue(PropLocalMachine, out var lm) && lm is true) return;
        if (id.Contains("00000000-0000-0000-ffff-ffffffffffff", StringComparison.OrdinalIgnoreCase)) return;
        var now = DateTimeOffset.Now;
        var connected = props.TryGetValue(PropConnected, out var c) && c is true;
        var hasConnected = props.ContainsKey(PropConnected);
        if (_devices.TryGetValue(id, out var existing))
        {
            var nowConnected = hasConnected ? connected : existing.Connected;
            var updated = existing with
            {
                Connected = nowConnected,
                LastConnected = !existing.Connected && nowConnected ? now : existing.LastConnected,
                LastDisconnected = existing.Connected && !nowConnected ? now : existing.LastDisconnected,
            };
            _devices[id] = updated;
            if (existing.Connected != nowConnected && _enumerated) Publish(updated, nowConnected);
            return;
        }
        if (!added) return;
        // Software print queues and USB/PCI plumbing are containers too, but not devices people plug in.
        if (name is not null && (VirtualPrinters.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase)) || IsInfrastructure(name))) return;
        var category = props.TryGetValue(PropCategory, out var cat) ? cat switch
        {
            string[] arr when arr.Length > 0 => arr[0],
            string s => s,
            _ => null,
        } : null;
        var record = new DeviceRecord(id, Core.Privacy.Redactor.SanitizeUntrusted(name ?? "Unknown device", 96),
            Core.Privacy.Redactor.SanitizeUntrusted(category ?? InferCategory(name), 48),
            props.TryGetValue(PropManufacturer, out var m) ? Core.Privacy.Redactor.SanitizeUntrusted(m as string, 64) : null,
            // A device already present during the initial enumeration has no observed connection time.
            connected, now, connected && _enumerated ? now : null, null, null, 0, null, null);
        _devices[id] = record;
        if (_enumerated && connected) Publish(record, connected: true);
    }

    internal static bool IsInfrastructure(string name) =>
        name.Contains("Root Hub", StringComparison.OrdinalIgnoreCase) || name.Contains("Generic USB Hub", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PCI to PCI Bridge", StringComparison.OrdinalIgnoreCase) || name.Contains("Host Controller", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Universal Serial Bus (USB) Controller", StringComparison.OrdinalIgnoreCase)
        || System.Text.RegularExpressions.Regex.IsMatch(name, @"^USB\d{3,5}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Best-effort category when Windows provides none. Purely descriptive; used for the list icon and label.</summary>
    internal static string InferCategory(string? name)
    {
        var n = name?.ToLowerInvariant() ?? "";
        if (n.Contains("keyboard", StringComparison.Ordinal)) return "Keyboard";
        if (n.Contains("mouse", StringComparison.Ordinal) || n.Contains("deathadder", StringComparison.Ordinal) || n.Contains("trackpad", StringComparison.Ordinal)) return "Mouse";
        if (n.Contains("monitor", StringComparison.Ordinal) || n.Contains("display", StringComparison.Ordinal)) return "Display";
        if (n.Contains("headphone", StringComparison.Ordinal) || n.Contains("headset", StringComparison.Ordinal) || n.Contains("audio", StringComparison.Ordinal)
            || n.Contains("speaker", StringComparison.Ordinal) || n.Contains("buds", StringComparison.Ordinal) || n.Contains("tas2", StringComparison.Ordinal)) return "Audio";
        if (n.Contains("receiver", StringComparison.Ordinal) || n.Contains("dongle", StringComparison.Ordinal)) return "Wireless receiver";
        if (n.Contains("controller", StringComparison.Ordinal) || n.Contains("gamepad", StringComparison.Ordinal)) return "Game controller";
        if (n.Contains("camera", StringComparison.Ordinal) || n.Contains("webcam", StringComparison.Ordinal)) return "Camera";
        if (n.Contains("drive", StringComparison.Ordinal) || n.Contains("disk", StringComparison.Ordinal) || n.Contains("flash", StringComparison.Ordinal)) return "Storage";
        if (n.Contains("phone", StringComparison.Ordinal) || n.Contains("iphone", StringComparison.Ordinal) || n.Contains("galaxy", StringComparison.Ordinal)) return "Phone";
        if (n.Contains("printer", StringComparison.Ordinal)) return "Printer";
        if (n.Contains("hub", StringComparison.Ordinal) || n.Contains("dock", StringComparison.Ordinal)) return "Hub or dock";
        return "Device";
    }

    private void Publish(DeviceRecord d, bool connected)
    {
        var now = DateTimeOffset.Now;
        _pending.Enqueue(new SystemEvent(now, connected ? EventCategory.DeviceConnected : EventCategory.DeviceDisconnected, Severity.Info,
            $"{d.Name} {(connected ? "connected" : "disconnected")}", d.Category, Source, d.Name, $"dev-{d.ContainerId}-{now.ToUnixTimeMilliseconds()}"));
        while (_pending.Count > 500) _pending.TryDequeue(out _);
    }

    public override async Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        while (_pending.TryDequeue(out var e)) ctx.Events.Publish(e);
        if (ctx.Now - _lastSlow < SlowRefresh) return;
        _lastSlow = ctx.Now;
        await RefreshBatteriesAsync().ConfigureAwait(false);
        await RefreshProblemsAsync(ctx).ConfigureAwait(false);
    }

    private async Task RefreshBatteriesAsync()
    {
        foreach (var d in _devices.Values.Where(d => d.Connected).Take(24).ToList())
        {
            try
            {
                var guid = d.ContainerId.Trim('{', '}');
                var members = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{guid}}}\"", [PropBattery], DeviceInformationKind.Device);
                int? level = null;
                foreach (var m in members)
                    if (m.Properties.TryGetValue(PropBattery, out var b) && b is byte v && v <= 100) level = v;
                if (level != d.BatteryPercent && _devices.TryGetValue(d.ContainerId, out var cur)) _devices[d.ContainerId] = cur with { BatteryPercent = level };
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
            {
                Log.LogDebug(ex, "Battery query failed for a device container");
            }
        }
    }

    private async Task RefreshProblemsAsync(SampleContext ctx)
    {
        try
        {
            var problems = await DeviceInformation.FindAllAsync("System.Devices.DeviceHasProblem:=System.StructuredQueryType.Boolean#True", [PropProblemCode],
                DeviceInformationKind.Device);
            foreach (var p in problems.Take(50))
            {
                var code = p.Properties.TryGetValue(PropProblemCode, out var pc) && pc is uint u ? (int)u : 0;
                // Code 22 = disabled by the user, 45 = not currently connected: not problems worth surfacing.
                if (code is 0 or 22 or 45) continue;
                var key = $"{p.Id}|{code}|{DateOnly.FromDateTime(ctx.Now.Date)}";
                if (!_reportedProblems.Add(key)) continue;
                var name = Core.Privacy.Redactor.SanitizeUntrusted(string.IsNullOrWhiteSpace(p.Name) ? "A device" : p.Name, 96);
                ctx.Events.Publish(new SystemEvent(ctx.Now, EventCategory.DeviceProblem, Severity.Notice, $"{name} reports a problem (code {code})",
                    Knowledge.DeviceProblemCodes.Describe(code), Source, name, "devprob-" + key, Code: code.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            if (_reportedProblems.Count > 2000) _reportedProblems.Clear();
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Log.LogDebug(ex, "Device problem query is not supported on this system");
        }
    }

    public override void OnSystemResumed()
    {
        // The watcher survives sleep; nothing to rebuild.
    }

    public override void Dispose()
    {
        try
        {
            if (_watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted }) _watcher.Stop();
        }
        catch (InvalidOperationException)
        {
        }
        base.Dispose();
    }
}
