using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sentinel.Core.Metrics;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Events;
using Sentinel.Platform.Windows.Providers;

// Sentinel provider probe: initialises every real (read-only) provider once, samples twice and prints what this
// machine exposes. Intended for the hardware test matrix. Prints no serial numbers or other identifiers.

var live = new LiveMetricStore();
var sink = new Sink(live);
ILogger<T> L<T>() => NullLogger<T>.Instance;
var gpu = new GpuProvider(L<GpuProvider>());
var system = new SystemInventoryProvider(L<SystemInventoryProvider>());
ITelemetryProvider[] providers =
[
    system, new CpuProvider(L<CpuProvider>()), new MemoryProvider(L<MemoryProvider>()), gpu, new StorageProvider(L<StorageProvider>()),
    new BatteryProvider(L<BatteryProvider>()), new NetworkProvider(L<NetworkProvider>()), new ThermalProvider(L<ThermalProvider>()),
    new PowerProvider(L<PowerProvider>()), new ProcessProvider(L<ProcessProvider>(), gpu), new DisplayProvider(L<DisplayProvider>()),
    new AudioProvider(L<AudioProvider>()), new DeviceProvider(L<DeviceProvider>()), new DriverProvider(L<DriverProvider>()),
    new SoftwareInventoryProvider(L<SoftwareInventoryProvider>()), new StartupProvider(L<StartupProvider>()),
    new UpdateHistoryProvider(L<UpdateHistoryProvider>()), new EventLogProvider(L<EventLogProvider>()), new OemProvider(L<OemProvider>(), system),
];

foreach (var p in providers)
{
    SafetyContract.Validate(p.Descriptor);
    var sw = Stopwatch.StartNew();
    try
    {
        await p.InitializeAsync(CancellationToken.None);
        Console.WriteLine($"[init] {p.Descriptor.Id,-10} {sw.ElapsedMilliseconds,5} ms");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[init] {p.Descriptor.Id,-10} FAILED: {ex.GetType().Name}: {ex.Message}");
    }
}

for (var round = 0; round < 2; round++)
{
    await Task.Delay(1100);
    foreach (var p in providers)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await p.SampleAsync(new SampleContext(DateTimeOffset.Now, SamplingMode.Detail, sink, sink), CancellationToken.None);
            if (round == 1) Console.WriteLine($"[sample] {p.Descriptor.Id,-10} {sw.Elapsed.TotalMilliseconds,7:F1} ms");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sample] {p.Descriptor.Id,-10} FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

Console.WriteLine();
Console.WriteLine("== Capabilities ==");
foreach (var p in providers)
    foreach (var c in p.Capabilities)
        Console.WriteLine($"  {p.Descriptor.Id,-10} {(c.Available ? "yes" : "no "),-4} {c.Name}{(c.Reason is null ? "" : " — " + c.Reason)}");

Console.WriteLine();
Console.WriteLine("== Selected readings ==");
var inv = system.Inventory;
Console.WriteLine($"System: {inv.Manufacturer} {inv.Model}, {inv.OsName} {inv.OsVersion} build {inv.OsBuild}, firmware {inv.FirmwareType}, laptop={inv.IsLaptop}");
var cpu = (ICpuTelemetryProvider)providers[1];
Console.WriteLine($"CPU: {cpu.Inventory.Name} | {cpu.Inventory.PhysicalCores}C/{cpu.Inventory.LogicalProcessors}T hybrid={cpu.Inventory.IsHybrid} P={cpu.Inventory.PerformanceCores} E={cpu.Inventory.EfficiencyCores} base={cpu.Inventory.BaseMhz} MHz");
Console.WriteLine($"     util={cpu.Latest.Utilization.Value:F1}% eff={cpu.Latest.EffectiveMhz.Value:F0} MHz perfLimit={cpu.Latest.ThrottleIndicator.Value} power={cpu.Latest.PackagePowerW.Value} cores={cpu.Latest.PerLogicalUtilization.Count}");
Console.WriteLine("     caches: " + string.Join(", ", cpu.Inventory.Caches.Select(c => $"L{c.Level} {c.Type} {c.SizeBytes / 1024} KiB x{c.SharedByLogical}")));
var mem = (IMemoryTelemetryProvider)providers[2];
Console.WriteLine($"Memory: {mem.Latest.UsedBytes / 1e9:F1}/{mem.Latest.TotalBytes / 1e9:F1} GB, commit {mem.Latest.CommittedBytes / 1e9:F1}/{mem.Latest.CommitLimitBytes / 1e9:F1} GB, modules={mem.Inventory.Modules.Count} ({string.Join(", ", mem.Inventory.Modules.Select(m => $"{m.CapacityBytes >> 30}G {m.MemoryType} {m.ConfiguredSpeedMts}"))})");
foreach (var a in gpu.Adapters)
{
    var s = gpu.Latest.FirstOrDefault(x => x.AdapterId == a.Id);
    Console.WriteLine($"GPU: {a.Name} [{a.Kind}] vram={a.DedicatedVideoMemory >> 20} MiB driver={a.DriverVersion} vendorTelemetry={a.VendorTelemetryAvailable} util={s?.Utilization.Value:F1} temp={s?.Temperature.Value} lowPower={s?.InLowPowerState} engines={string.Join("/", s?.Engines.Select(e => e.EngineType) ?? [])}");
}
var storage = (IStorageTelemetryProvider)providers[4];
foreach (var d in storage.Disks)
{
    storage.Health.TryGetValue(d.Id, out var h);
    Console.WriteLine($"Disk {d.Number}: {d.Model} {d.BusType}/{d.MediaType} {d.SizeBytes / 1e9:F0} GB health={h?.HealthStatus} temp={h?.Temperature.Value} used%={h?.PercentageUsed.Value} spare={h?.AvailableSpare.Value} poh={h?.PowerOnHours.Value} written={h?.DataWrittenBytes.Value / 1e12:F1} TB");
}
foreach (var v in storage.Volumes) Console.WriteLine($"Volume {v.Name} {v.FileSystem} {v.FreeBytes / 1e9:F0}/{v.TotalBytes / 1e9:F0} GB disk={v.DiskNumber} enc={v.Encryption}");
var bat = (IBatteryTelemetryProvider)providers[5];
Console.WriteLine($"Battery: present={bat.Latest.Present} ac={bat.Latest.AcOnline} state={bat.Latest.State} pct={bat.Latest.Percent.Value} rate={bat.Latest.RateMilliwatts.Value} mW full={bat.Latest.FullChargeMWh.Value} design={bat.Latest.DesignMWh.Value} V={bat.Latest.VoltageMv.Value}");
foreach (var b in bat.Latest.Batteries) Console.WriteLine($"   {b.Name} {b.Manufacturer} {b.Chemistry} cycles={b.CycleCount} health={b.EstimatedHealthPercent:F1}% made={b.ManufactureDate:d}");
var net = (INetworkTelemetryProvider)providers[6];
foreach (var a in net.Latest.Adapters) Console.WriteLine($"Net: {a.Name} [{a.Kind}] up={a.IsUp} speed={a.LinkSpeedBps / 1e6:F0} Mbps rx={a.RxBytesPerSec:F0} B/s wifi={(a.Wifi is { } w ? $"{w.SignalQuality.Value}% {w.RssiDbm.Value} dBm {w.PhyType} ch{w.Channel.Value} {w.Band}" : "-")}");
var thermal = (IThermalTelemetryProvider)providers[7];
foreach (var t in thermal.Sensors) Console.WriteLine($"Thermal: {t.Name} [{t.Component}] {t.Temperature.Value:F1} °C q={t.Temperature.Quality}");
var power = (PowerProvider)providers[8];
Console.WriteLine($"Power: ac={power.AcOnline} saver={power.BatterySaverOn} idle={power.UserIdle.TotalSeconds:F0}s mode={power.PowerMode} plan={power.PowerPlan}");
var procs = (IProcessTelemetryProvider)providers[9];
Console.WriteLine($"Processes: {procs.Latest.Processes.Count}, top: " + string.Join(", ", procs.Latest.Apps.Take(5).Select(a => $"{a.DisplayName} {a.CpuPercent:F1}% {a.PrivateBytes >> 20} MiB")));
foreach (var d in ((IDisplayTelemetryProvider)providers[10]).Displays)
    Console.WriteLine($"Display: {d.FriendlyName} {d.Width}x{d.Height}@{d.RefreshHz}Hz scale={d.ScalePercent}% {d.Connection} internal={d.IsInternal} hdr={d.HdrSupported}/{d.HdrEnabled} bpc={d.BitsPerColor} gpu={d.GpuName} mfg={d.ManufacturerCode}");
var audio = (IAudioTelemetryProvider)providers[11];
Console.WriteLine($"Audio: {audio.Devices.Count} endpoints; defaults: " + string.Join(", ", audio.Devices.Where(d => d.IsDefault).Select(d => $"{d.Name} {d.SampleRate}Hz {d.Channels}ch")) + $"; sessions={audio.Sessions.Count}");
await Task.Delay(1500);
var devices = (IDeviceInventoryProvider)providers[12];
Console.WriteLine($"Devices: {devices.Devices.Count} external containers; connected: " + string.Join(", ", devices.Devices.Where(d => d.Connected).Take(8).Select(d => d.Name)));
var drivers = (IDriverInventoryProvider)providers[13];
Console.WriteLine($"Drivers: {drivers.Drivers.Count}; groups: " + string.Join(", ", drivers.Drivers.GroupBy(d => d.Group).Select(g => $"{g.Key}={g.Count()}")));
var sw2 = (ISoftwareInventoryProvider)providers[14];
Console.WriteLine($"Software: {sw2.Software.Count} apps, {sw2.Services.Count} services");
var startup = (IStartupProvider)providers[15];
Console.WriteLine($"Startup: {startup.Items.Count} items: " + string.Join(", ", startup.Items.Take(6).Select(i => $"{i.Name}({(i.Enabled ? "on" : "off")},{i.Impact})")));
var updates = (IUpdateHistoryProvider)providers[16];
Console.WriteLine($"Updates: {updates.Updates.Count}, reboot required={updates.RebootRequired}; latest: " + string.Join(" | ", updates.Updates.Take(3).Select(u => $"{u.Date:d} {u.Kind} {u.Result}")));
var events = (EventLogProvider)providers[17];
var hist = await events.ReadHistoryAsync(DateTimeOffset.Now.AddDays(30 * -1), 5000, CancellationToken.None);
Console.WriteLine($"Events (30 d): {hist.Count}; by category: " + string.Join(", ", hist.GroupBy(e => e.Category).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}")));
foreach (var e in hist.Where(e => e.IsReliabilityRelevant).TakeLast(5)) Console.WriteLine($"   {e.Timestamp:g} {e.Category}: {e.Title}");
var oem = (IOemTelemetryProvider)providers[18];
Console.WriteLine($"OEM: {oem.Info?.Manufacturer} utility={oem.Info?.UtilityDetected}");
Console.WriteLine($"Metrics defined: {live.Definitions.Count}, series: {live.Keys.Count()}");
foreach (var p in providers) p.Dispose();

sealed class Sink(LiveMetricStore live) : IMetricSink, IEventSink
{
    public void Record(string key, double value, DateTimeOffset timestamp) => live.Record(key, value, timestamp);
    public void Define(MetricDefinition definition) => live.Define(definition);
    public void Publish(SystemEvent e) { }
}
