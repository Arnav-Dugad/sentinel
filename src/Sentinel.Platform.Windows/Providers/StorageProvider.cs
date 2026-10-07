using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Providers;
using Sentinel.Domain;
using Sentinel.Platform.Windows.Interop;

namespace Sentinel.Platform.Windows.Providers;

/// <summary>
/// Disks and volumes (Storage Management API via WMI, refreshed slowly), live I/O from PhysicalDisk counters,
/// and health/temperature from documented storage property queries (NVMe health log, temperature descriptor).
/// </summary>
public sealed class StorageProvider(ILogger<StorageProvider> log) : WindowsProvider(log), IStorageTelemetryProvider
{
    private const string PdhSource = "Windows PhysicalDisk counters";
    private const string HealthSource = "Storage property query (NVMe health log)";
    private const string TempSource = "Storage temperature property";
    private const string WmiSource = "Windows Storage Management API";
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InventoryInterval = TimeSpan.FromMinutes(5);

    private PdhQuery? _pdh;
    private PdhCounter? _read, _write, _xfers, _latency, _idle, _queue;
    private DateTimeOffset _lastHealth = DateTimeOffset.MinValue;
    private DateTimeOffset _lastInventory = DateTimeOffset.MinValue;
    private readonly HashSet<string> _definedTemps = [];
    private Dictionary<int, string?> _wmiHealth = [];

    public override ProviderDescriptor Descriptor { get; } = Describe("storage", "Storage", "Storage", SamplingCost.Low, "1 s I/O, 60 s health",
        PdhSource, WmiSource, HealthSource, TempSource);

    protected override (TimeSpan Foreground, TimeSpan Detail, TimeSpan? Background) Intervals =>
        (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    public IReadOnlyList<PhysicalDiskInfo> Disks { get; private set; } = [];
    public IReadOnlyList<VolumeInfo> Volumes { get; private set; } = [];
    public IReadOnlyList<DiskIoSnapshot> Io { get; private set; } = [];
    public IReadOnlyDictionary<string, DiskHealth> Health { get; private set; } = new Dictionary<string, DiskHealth>();

    public override async Task InitializeAsync(CancellationToken ct)
    {
        await RefreshInventoryAsync(ct).ConfigureAwait(false);
        _pdh = new PdhQuery();
        _read = _pdh.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _write = _pdh.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _xfers = _pdh.Add(@"\PhysicalDisk(*)\Disk Transfers/sec");
        _latency = _pdh.Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
        _idle = _pdh.Add(@"\PhysicalDisk(*)\% Idle Time");
        _queue = _pdh.Add(@"\PhysicalDisk(*)\Current Disk Queue Length");
        _pdh.Collect();
        SetCapability("Disk I/O", _read is not null, PdhSource);
        SetCapability("Disk inventory", Disks.Count > 0, WmiSource);
    }

    private async Task RefreshInventoryAsync(CancellationToken ct)
    {
        var disks = new List<PhysicalDiskInfo>();
        var health = new Dictionary<int, string?>();
        var volumes = await Task.Run(ReadVolumes, ct).ConfigureAwait(false);
        var rows = Wmi.TryQuery(Wmi.StorageNs, "SELECT DeviceId, FriendlyName, Model, Manufacturer, MediaType, BusType, Size, HealthStatus, FirmwareVersion, SerialNumber FROM MSFT_PhysicalDisk", 64);
        if (rows.Count > 0)
        {
            foreach (var r in rows)
            {
                if (!int.TryParse(r.Str("DeviceId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) continue;
                var model = r.Str("FriendlyName") ?? r.Str("Model") ?? $"Disk {number}";
                var serial = r.Str("SerialNumber");
                var bus = (int)(r.Long("BusType") ?? 0);
                var media = r.Long("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" };
                if (media == "Unspecified")
                {
                    using var h = StorageIoctl.OpenDisk(number);
                    if (!h.IsInvalid && StorageIoctl.IncursSeekPenalty(h) is { } penalty) media = penalty ? "HDD" : "SSD";
                }
                health[number] = r.Long("HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => null };
                disks.Add(new PhysicalDiskInfo(DiskId(model, serial, number), number, model, r.Str("Manufacturer"), StorageIoctl.BusTypeName(bus), media,
                    r.Long("Size") ?? 0, r.Str("FirmwareVersion"), volumes.Any(v => v.IsSystem && v.DiskNumber == number), bus is 7 or 12 or 13, serial,
                    volumes.Where(v => v.DiskNumber == number).Select(v => v.Name).ToList()));
            }
            SetCapability("Disk inventory", true, WmiSource);
        }
        else
        {
            // Fallback: probe PhysicalDriveN directly with query-only handles.
            for (var n = 0; n < 16; n++)
            {
                using var h = StorageIoctl.OpenDisk(n);
                if (h.IsInvalid) continue;
                var d = StorageIoctl.QueryDevice(h);
                if (d is null) continue;
                var model = string.Join(" ", new[] { d.Vendor, d.Product }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var media = StorageIoctl.IncursSeekPenalty(h) is { } p ? (p ? "HDD" : "SSD") : "Unspecified";
                disks.Add(new PhysicalDiskInfo(DiskId(model, d.Serial, n), n, model.Length > 0 ? model : $"Disk {n}", d.Vendor, StorageIoctl.BusTypeName(d.BusType), media,
                    0, d.Revision, volumes.Any(v => v.IsSystem && v.DiskNumber == n), d.Removable, d.Serial, volumes.Where(v => v.DiskNumber == n).Select(v => v.Name).ToList()));
            }
            SetCapability("Disk inventory", disks.Count > 0, "Storage property query", disks.Count == 0 ? "Storage Management API unavailable" : null);
        }
        Disks = disks.OrderBy(d => d.Number).ToList();
        Volumes = volumes;
        _wmiHealth = health;
        _lastInventory = DateTimeOffset.Now;
    }

    private static List<VolumeInfo> ReadVolumes()
    {
        var list = new List<VolumeInfo>();
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            try
            {
                if (!d.IsReady) continue;
                var name = d.Name.TrimEnd('\\');
                var disks = StorageIoctl.VolumeDisks(name);
                list.Add(new VolumeInfo(name, Core.Privacy.Redactor.SanitizeUntrusted(d.VolumeLabel, 64), d.DriveFormat, d.TotalSize, d.AvailableFreeSpace,
                    d.DriveType.ToString(), name.Equals(systemDrive, StringComparison.OrdinalIgnoreCase), name.Equals(systemDrive, StringComparison.OrdinalIgnoreCase),
                    disks.Count > 0 ? disks[0] : null, ReadBitLocker(name)));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return list;
    }

    // System.Volume.BitLockerProtection is a documented shell property readable without elevation.
    private static string? ReadBitLocker(string drive)
    {
        try
        {
            var folder = global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(drive + "\\").AsTask().GetAwaiter().GetResult();
            var props = folder.Properties.RetrievePropertiesAsync(["System.Volume.BitLockerProtection"]).AsTask().GetAwaiter().GetResult();
            if (!props.TryGetValue("System.Volume.BitLockerProtection", out var v) || v is null) return "Not reported";
            return Convert.ToInt32(v, CultureInfo.InvariantCulture) switch
            {
                1 => "BitLocker on",
                2 => "BitLocker off",
                3 => "Encrypting",
                4 => "Decrypting",
                5 => "BitLocker suspended",
                6 => "BitLocker on (locked)",
                8 => "Waiting for activation",
                var code => $"Reported state {code}",
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidCastException or FormatException)
        {
            return null;
        }
    }

    internal static string DiskId(string model, string? serial, int number)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(model + "|" + (serial ?? number.ToString(CultureInfo.InvariantCulture))));
        return "d" + Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    public override async Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var now = ctx.Now;
        if (now - _lastInventory > InventoryInterval) await RefreshInventoryAsync(ct).ConfigureAwait(false);
        _pdh?.Collect();

        var read = ByDisk(_read);
        var write = ByDisk(_write);
        var xfers = ByDisk(_xfers);
        var lat = ByDisk(_latency);
        var idle = ByDisk(_idle);
        var queue = ByDisk(_queue);
        var io = new List<DiskIoSnapshot>();
        double totalRead = 0, totalWrite = 0, maxActive = 0;
        foreach (var d in Disks)
        {
            var r = read.GetValueOrDefault(d.Number, double.NaN);
            var w = write.GetValueOrDefault(d.Number, double.NaN);
            double? active = idle.TryGetValue(d.Number, out var idl) ? Math.Clamp(100 - idl, 0, 100) : null;
            if (!double.IsNaN(r)) totalRead += r;
            if (!double.IsNaN(w)) totalWrite += w;
            if (active is { } a) maxActive = Math.Max(maxActive, a);
            io.Add(new DiskIoSnapshot(d.Id, now, Maybe(double.IsNaN(r) ? null : r, PdhSource, now, "Not exposed"), Maybe(double.IsNaN(w) ? null : w, PdhSource, now, "Not exposed"),
                Maybe(xfers.TryGetValue(d.Number, out var x) ? x : null, PdhSource, now, "Not exposed"),
                Maybe(lat.TryGetValue(d.Number, out var l) ? l * 1000 : null, PdhSource, now, "Not exposed"),
                Maybe(active, PdhSource, now, "Not exposed"), Maybe(queue.TryGetValue(d.Number, out var q) ? q : null, PdhSource, now, "Not exposed")));
        }
        Io = io;
        ctx.Metrics.Record(MetricKeys.DiskRead, totalRead, now);
        ctx.Metrics.Record(MetricKeys.DiskWrite, totalWrite, now);
        ctx.Metrics.Record(MetricKeys.DiskActive, maxActive, now);

        if (now - _lastHealth >= HealthInterval)
        {
            _lastHealth = now;
            Health = ReadHealth(ctx, now);
        }
    }

    private Dictionary<string, DiskHealth> ReadHealth(SampleContext ctx, DateTimeOffset now)
    {
        var result = new Dictionary<string, DiskHealth>();
        var anyNvmeLog = false;
        var anyTemp = false;
        foreach (var d in Disks)
        {
            var status = _wmiHealth.GetValueOrDefault(d.Number);
            using var h = StorageIoctl.OpenDisk(d.Number);
            NvmeHealthLog? nvme = null;
            StorageTemperature? temp = null;
            if (!h.IsInvalid)
            {
                if (d.BusType == "NVMe") nvme = StorageIoctl.QueryNvmeHealth(h);
                temp = StorageIoctl.QueryTemperature(h);
            }
            anyNvmeLog |= nvme is not null;
            double? tempC = temp?.CurrentC ?? (nvme is { } n && !double.IsNaN(n.CompositeTemperatureC) ? n.CompositeTemperatureC : null);
            anyTemp |= tempC is not null;
            var why = d.BusType == "NVMe"
                ? "This drive did not return its NVMe health log to a standard (non-administrator) query."
                : "Detailed health attributes are only read for NVMe drives through documented queries.";
            Reading N(double? v, string src) => v is { } x ? Good(x, src, now) : Reading.Unavailable(why);
            var tempSource = temp is not null ? TempSource : HealthSource;
            result[d.Id] = new DiskHealth(now, nvme is not null ? HealthSource : WmiSource, status,
                tempC is { } t ? Good(t, tempSource, now) : Reading.Unavailable("This drive does not report its temperature to a standard query."),
                temp?.WarningC is { } wc ? Good(wc, TempSource, now) : Reading.Unavailable("Not reported"),
                temp?.CriticalC is { } cc ? Good(cc, TempSource, now) : Reading.Unavailable("Not reported"),
                N(nvme?.PercentageUsed, HealthSource), N(nvme?.AvailableSpare, HealthSource), N(nvme?.AvailableSpareThreshold, HealthSource),
                nvme?.CriticalWarning,
                N(nvme?.DataUnitsReadBytes, HealthSource), N(nvme?.DataUnitsWrittenBytes, HealthSource), N(nvme?.PowerOnHours, HealthSource),
                N(nvme?.PowerCycles, HealthSource), N(nvme?.UnsafeShutdowns, HealthSource), N(nvme?.MediaErrors, HealthSource), N(nvme?.ErrorLogEntries, HealthSource));

            if (tempC is { } tc)
            {
                var key = MetricKeys.Disk(d.Id, "temp");
                if (_definedTemps.Add(key))
                    ctx.Metrics.Define(new MetricDefinition(key, $"{d.Model} temperature", MetricUnit.Celsius, "Storage", "Drive temperature reported by the drive."));
                ctx.Metrics.Record(key, tc, now);
            }
        }
        SetCapability("NVMe health log", anyNvmeLog, HealthSource, anyNvmeLog ? null : "No drive returned its health log to a standard query.");
        SetCapability("Drive temperature", anyTemp, TempSource, anyTemp ? null : "No drive reported a temperature.");
        return result;
    }

    private static Dictionary<int, double> ByDisk(PdhCounter? c)
    {
        var d = new Dictionary<int, double>();
        if (c is null) return d;
        foreach (var (instance, value) in c.Instances(noCap: true))
        {
            var space = instance.IndexOf(' ');
            var num = space > 0 ? instance[..space] : instance;
            if (int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) d[n] = value;
        }
        return d;
    }

    public override void OnSystemResumed()
    {
        _pdh?.Rebuild();
        _lastInventory = DateTimeOffset.MinValue;
        _lastHealth = DateTimeOffset.MinValue;
    }

    public override void Dispose()
    {
        _pdh?.Dispose();
        base.Dispose();
    }
}
