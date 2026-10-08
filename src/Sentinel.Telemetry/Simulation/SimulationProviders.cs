using Sentinel.Core.Providers;
using Sentinel.Domain;

namespace Sentinel.Telemetry.Simulation;

public abstract class SimulatedProvider(SimulationWorld world, string id, string name, string category) : ITelemetryProvider
{
    protected const string Source = "Developer Simulation";
    protected SimulationWorld World { get; } = world;

    public ProviderDescriptor Descriptor { get; } = new("sim." + id, name + " (simulated)", category, [Source], AccessRequirement.None,
        SamplingCost.Negligible, "Synthetic", SafetyAttestation.ReadOnlyObserver, IsSimulation: true);

    public virtual IReadOnlyList<Capability> Capabilities { get; } = [new("Simulated telemetry", true, Source, Confidence: Confidence.Low)];

    public virtual Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public virtual TimeSpan GetInterval(SamplingMode mode) => mode switch
    {
        SamplingMode.Paused => Intervals.Never,
        SamplingMode.Background => TimeSpan.FromSeconds(5),
        _ => TimeSpan.FromSeconds(1),
    };

    public abstract Task SampleAsync(SampleContext context, CancellationToken ct);

    public virtual void OnSystemResumed()
    {
    }

    public virtual void Dispose() => GC.SuppressFinalize(this);

    protected static Reading Sim(double v, DateTimeOffset ts) => new(v, Quality.Good, Source, ts, Confidence.Low);
}

public sealed class SimulatedSystemProvider(SimulationWorld w) : SimulatedProvider(w, "system", "System inventory", "System"), ISystemInventoryProvider
{
    public SystemInventory Inventory { get; } = SystemInventory.Empty with
    {
        Manufacturer = "Contoso (simulated)",
        Model = "Simulated Laptop 14",
        ProductFamily = "Developer Simulation",
        BiosVendor = "Simulated firmware",
        BiosVersion = "1.0.0-sim",
        FirmwareType = "UEFI",
        SecureBoot = true,
        ChassisType = "Notebook",
        SystemArchitecture = "x64",
        OsArchitecture = "64-bit",
        OsName = "Windows 11 (simulated)",
        OsVersion = "10.0.26100",
        BootTime = DateTimeOffset.Now.AddHours(-5),
        IsLaptop = true,
    };

    public SecurityStatus Security { get; } = new(true, true, true, true, DateTimeOffset.Now.AddHours(-3), Source);

    public override TimeSpan GetInterval(SamplingMode mode) => Intervals.Never;

    public override Task SampleAsync(SampleContext context, CancellationToken ct) => Task.CompletedTask;
}

public sealed class SimulatedCpuProvider(SimulationWorld w) : SimulatedProvider(w, "cpu", "CPU", "CPU"), ICpuTelemetryProvider
{
    public CpuInventory Inventory { get; } = new("Simulated 12-core processor", "Simulated", "x64", 12, 16, 1, 1, 4, 8, true, 2400, 5000,
        [new(1, "Data", 48 * 1024, 1), new(2, "Unified", 2 * 1024 * 1024, 2), new(3, "Unified", 24 * 1024 * 1024, 16)], true,
        [1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0]);

    public CpuSnapshot Latest { get; private set; } = CpuSnapshot.Empty;

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var util = Math.Clamp(World.CpuUtil, 0, 100);
        var cores = Enumerable.Range(0, 16).Select(i => Math.Clamp(util + World.Noise(util * 0.6 + 2), 0, 100)).ToArray();
        var temp = World.CpuTemp;
        var freq = 2400 + util * 22 + World.Noise(80);
        Latest = new CpuSnapshot(ctx.Now, Sim(util, ctx.Now), cores, Sim(freq, ctx.Now), Sim(util > 80 ? 3 : 0, ctx.Now), Sim(12000 + util * 400, ctx.Now),
            Sim(8000 + util * 120, ctx.Now), Sim(0.3 + util / 100, ctx.Now), Sim(8 + util * 0.5, ctx.Now), Sim(temp, ctx.Now),
            Sim(World.Scenario == SimulationScenario.Overheating ? 1 : 0, ctx.Now), 286, 3940, 132_000, TimeSpan.FromHours(5) + TimeSpan.FromSeconds(World.Seconds));
        ctx.Metrics.Record(MetricKeys.CpuUtil, util, ctx.Now);
        ctx.Metrics.Record(MetricKeys.CpuFreq, freq, ctx.Now);
        ctx.Metrics.Record(MetricKeys.Thermal("cpu"), temp, ctx.Now);
        for (var i = 0; i < cores.Length; i++) ctx.Metrics.Record(MetricKeys.CpuCore(i), cores[i], ctx.Now);
        return Task.CompletedTask;
    }

    public override Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class SimulatedMemoryProvider(SimulationWorld w) : SimulatedProvider(w, "memory", "Memory", "Memory"), IMemoryTelemetryProvider
{
    private const long Total = 16L * 1024 * 1024 * 1024;

    public MemoryInventory Inventory { get; } = new(Total, Total - 300L * 1024 * 1024,
        [new("Simulated", 8L << 30, 5600, 5600, "SODIMM", "DIMM A", "SIM-8G-5600", 1, "DDR5", null),
         new("Simulated", 8L << 30, 5600, 5600, "SODIMM", "DIMM B", "SIM-8G-5600", 1, "DDR5", null)], 2);

    public MemorySnapshot Latest { get; private set; } = MemorySnapshot.Empty;

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var used = World.MemoryUsedPercent / 100 * Total;
        var pressure = World.Scenario == SimulationScenario.MemoryPressure;
        Latest = new MemorySnapshot(ctx.Now, Total, (long)(Total - used), (long)(used * 1.3), Total + 8L * 1024 * 1024 * 1024,
            (long)(Total * 0.18), Sim(Total * 0.15, ctx.Now), 600L << 20, 400L << 20, Sim(300L << 20, ctx.Now),
            Sim(2000, ctx.Now), Sim(pressure ? 800 + World.Noise(200) : 3, ctx.Now), Sim(pressure ? 71 : 4, ctx.Now));
        ctx.Metrics.Record(MetricKeys.MemUsedPct, World.MemoryUsedPercent, ctx.Now);
        ctx.Metrics.Record(MetricKeys.MemUsed, used, ctx.Now);
        ctx.Metrics.Record(MetricKeys.MemCommit, used * 1.3, ctx.Now);
        ctx.Metrics.Record(MetricKeys.MemHardFaults, Latest.HardFaultsPerSec.Value ?? 0, ctx.Now);
        return Task.CompletedTask;
    }
}

public sealed class SimulatedGpuProvider(SimulationWorld w) : SimulatedProvider(w, "gpu", "GPU", "GPU"), IGpuTelemetryProvider
{
    public IReadOnlyList<GpuAdapter> Adapters { get; } =
    [
        new("sim-igpu", "Simulated Integrated Graphics", "Simulated", 0x8086, 0x1, GpuKind.Integrated, 128L << 20, 8L << 30, "31.0.101.5000", DateTimeOffset.Now.AddMonths(-3), "sim0", null, false, null),
        new("sim-dgpu", "Simulated Discrete GPU 8GB", "Simulated", 0x10DE, 0x2, GpuKind.Discrete, 8L << 30, 8L << 30, "560.94", DateTimeOffset.Now.AddDays(-12), "sim1", null, true, Source),
    ];

    public IReadOnlyList<GpuSnapshot> Latest { get; private set; } = [];
    public IReadOnlyDictionary<int, double> ProcessUtilization { get; private set; } = new Dictionary<int, double>();

    public double? PowerCeilingW(string adapterId) => 175;

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var d = Math.Clamp(World.GpuUtil, 0, 100);
        var temp = World.GpuTemp;
        var heavy = World.IsHeavy;
        var none = Reading.Unsupported("Simulated integrated GPU exposes no vendor telemetry");
        Latest =
        [
            new("sim-igpu", ctx.Now, Sim(1 + Math.Abs(World.Noise(1)), ctx.Now), [new("3D", 1)], Sim(90L << 20, ctx.Now), Sim(400L << 20, ctx.Now),
                none, none, none, none, none, none, none, none, none, none, null, false),
            new("sim-dgpu", ctx.Now, Sim(d, ctx.Now), [new("3D", d), new("Copy", heavy ? 8 : 0), new("VideoDecode", 0)], Sim(heavy ? 6.8e9 : 3e8, ctx.Now),
                Sim(1e8, ctx.Now), Sim(temp, ctx.Now), Sim(temp + 11, ctx.Now), Sim(World.GpuPower, ctx.Now), Sim(115, ctx.Now),
                Sim(heavy ? 2100 : 210, ctx.Now), Sim(heavy ? 8000 : 405, ctx.Now), Sim(heavy ? 64 : 0, ctx.Now), Sim(heavy ? 0 : 8, ctx.Now),
                Sim(0, ctx.Now), Sim(0, ctx.Now), heavy && temp > 80 ? "Software thermal slowdown" : null, !heavy),
        ];
        ProcessUtilization = heavy ? new Dictionary<int, double> { [4242] = d - 2 } : new Dictionary<int, double>();
        ctx.Metrics.Record(MetricKeys.GpuUtilAny, d, ctx.Now);
        ctx.Metrics.Record(MetricKeys.Gpu("sim-dgpu", "util"), d, ctx.Now);
        ctx.Metrics.Record(MetricKeys.Gpu("sim-dgpu", "temp"), temp, ctx.Now);
        ctx.Metrics.Record(MetricKeys.Gpu("sim-dgpu", "power"), World.GpuPower, ctx.Now);
        ctx.Metrics.Record(MetricKeys.GpuTempAny, temp, ctx.Now);
        return Task.CompletedTask;
    }

    public override Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class SimulatedStorageProvider(SimulationWorld w) : SimulatedProvider(w, "storage", "Storage", "Storage"), IStorageTelemetryProvider
{
    public IReadOnlyList<PhysicalDiskInfo> Disks { get; } =
        [new("simdisk0", 0, "Simulated NVMe SSD 1TB", "Simulated", "NVMe", "SSD", 1_000_204_886_016, "SIM1.0", true, false, null, ["C:"])];

    public IReadOnlyList<VolumeInfo> Volumes { get; } =
        [new("C:", "Windows", "NTFS", 999_000_000_000, 412_000_000_000, "Fixed", true, true, 0, "BitLocker (simulated)")];

    public IReadOnlyList<DiskIoSnapshot> Io { get; private set; } = [];
    public IReadOnlyDictionary<string, DiskHealth> Health { get; private set; } = new Dictionary<string, DiskHealth>();

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var warn = World.Scenario == SimulationScenario.StorageWarning;
        var read = Math.Abs(World.Noise(4e6));
        var write = Math.Abs(World.Noise(2e6));
        Io = [new("simdisk0", ctx.Now, Sim(read, ctx.Now), Sim(write, ctx.Now), Sim(40 + World.Noise(30), ctx.Now), Sim(0.4, ctx.Now), Sim(3, ctx.Now), Sim(0.01, ctx.Now))];
        Health = new Dictionary<string, DiskHealth>
        {
            ["simdisk0"] = new(ctx.Now, Source, warn ? "Warning" : "Healthy", Sim(World.DiskTemp, ctx.Now), Sim(75, ctx.Now), Sim(80, ctx.Now),
                Sim(warn ? 97 : 4, ctx.Now), Sim(warn ? 8 : 100, ctx.Now), Sim(10, ctx.Now), warn ? 0x01 : 0,
                Sim(42e12, ctx.Now), Sim(38e12, ctx.Now), Sim(3120, ctx.Now), Sim(1450, ctx.Now), Sim(37, ctx.Now), Sim(warn ? 12 : 0, ctx.Now), Sim(0, ctx.Now)),
        };
        ctx.Metrics.Record(MetricKeys.DiskRead, read, ctx.Now);
        ctx.Metrics.Record(MetricKeys.DiskWrite, write, ctx.Now);
        ctx.Metrics.Record(MetricKeys.DiskActive, 3, ctx.Now);
        ctx.Metrics.Record(MetricKeys.Disk("simdisk0", "temp"), World.DiskTemp, ctx.Now);
        return Task.CompletedTask;
    }

    public override Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class SimulatedBatteryProvider(SimulationWorld w) : SimulatedProvider(w, "battery", "Battery", "Battery"), IBatteryTelemetryProvider
{
    public BatterySnapshot Latest { get; private set; } = BatterySnapshot.Empty;

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var frac = World.ChargeFraction;
        var full = World.FullChargeCapacity;
        var rate = World.BatteryRateWatts * 1000;
        var state = World.AcOnline ? (frac >= 0.999 ? ChargeState.Full : ChargeState.Charging) : ChargeState.Discharging;
        var info = new BatteryInfo("sim-bat", "Simulated Li-ion", "Simulated", "Lithium-ion", World.DesignCapacity, full,
            World.Scenario == SimulationScenario.BatteryDegradation ? 612 : 143, null, null, Reading.Unsupported("Not exposed"));
        var remaining = state == ChargeState.Discharging && rate < 0 ? TimeSpan.FromHours(frac * full / -rate) : (TimeSpan?)null;
        Latest = new BatterySnapshot(ctx.Now, true, World.AcOnline, state, Sim(frac * 100, ctx.Now), Sim(frac * full, ctx.Now), Sim(full, ctx.Now),
            Sim(World.DesignCapacity, ctx.Now), Sim(rate, ctx.Now), Sim(15800, ctx.Now),
            remaining is { } r ? Sim(r.TotalSeconds, ctx.Now) : Reading.Unavailable("Not discharging"), false, [info]);
        ctx.Metrics.Record(MetricKeys.BatPercent, frac * 100, ctx.Now);
        ctx.Metrics.Record(MetricKeys.BatRate, rate / 1000, ctx.Now);
        ctx.Metrics.Record(MetricKeys.BatCapacity, frac * full, ctx.Now);
        ctx.Metrics.Record(MetricKeys.BatFullCharge, full, ctx.Now);
        ctx.Metrics.Record(MetricKeys.AcOnline, World.AcOnline ? 1 : 0, ctx.Now);
        return Task.CompletedTask;
    }
}

public sealed class SimulatedNetworkProvider(SimulationWorld w) : SimulatedProvider(w, "network", "Network", "Network"), INetworkTelemetryProvider
{
    private bool _wasConnected = true;

    public NetworkSnapshot Latest { get; private set; } = NetworkSnapshot.Empty;

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var connected = World.WifiConnected;
        var rx = connected ? Math.Abs(World.Noise(World.IsHeavy ? 2e6 : 3e5)) : 0;
        var tx = connected ? Math.Abs(World.Noise(1e5)) : 0;
        var signal = World.WifiSignal;
        var wifi = new WifiInfo("Simulated-WiFi", Sim(signal, ctx.Now), Sim(-100 + signal / 2, ctx.Now), "802.11ax (Wi-Fi 6)", Sim(149, ctx.Now), "5 GHz",
            Sim(1201, ctx.Now), Sim(1201, ctx.Now), "WPA3-Personal");
        var adapter = new NetworkAdapterSnapshot("sim-wlan", "Wi-Fi", "Simulated Wi-Fi 6 adapter", "Wi-Fi", connected, 1_201_000_000, 0, 0, rx, tx,
            0, 0, 0, 0, 0, 0, ["192.0.2.10"], [], ["192.0.2.1"], ["192.0.2.1"], true, "00-00-5E-00-53-01", wifi);
        Latest = new NetworkSnapshot(ctx.Now, [adapter], rx, tx, true);
        ctx.Metrics.Record(MetricKeys.NetRx, rx, ctx.Now);
        ctx.Metrics.Record(MetricKeys.NetTx, tx, ctx.Now);
        ctx.Metrics.Record(MetricKeys.WifiSignal, signal, ctx.Now);
        if (connected != _wasConnected)
        {
            ctx.Events.Publish(new SystemEvent(ctx.Now, connected ? EventCategory.NetworkConnected : EventCategory.NetworkDisconnected,
                connected ? Severity.Info : Severity.Notice, connected ? "Wi-Fi reconnected" : "Wi-Fi disconnected",
                $"Signal quality {signal:F0}% at the time.", Source, "Wi-Fi", $"sim-net-{ctx.Now.ToUnixTimeMilliseconds()}"));
            _wasConnected = connected;
        }
        return Task.CompletedTask;
    }
}

public sealed class SimulatedProcessProvider(SimulationWorld w) : SimulatedProvider(w, "processes", "Processes", "Processes"), IProcessTelemetryProvider
{
    public ProcessSnapshot Latest { get; private set; } = ProcessSnapshot.Empty;

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        var heavy = World.IsHeavy;
        var leak = World.Scenario == SimulationScenario.MemoryPressure ? 2e9 + World.Seconds * 4e6 : 1.2e9;
        var procs = new List<ProcessSample>
        {
            new(4242, 1000, heavy ? "SimGame.exe" : "SimBrowser.exe", heavy ? World.CpuUtil * 0.7 : 1.5, (long)(heavy ? 5e9 : leak), (long)(heavy ? 5.5e9 : leak), 2e5, heavy ? World.GpuUtil - 2 : 0, 80, 900, DateTimeOffset.Now.AddMinutes(-40), 1, null),
            new(1100, 1000, "SimEditor.exe", 0.6, (long)4e8, (long)4.5e8, 0, 0, 30, 400, DateTimeOffset.Now.AddHours(-2), 1, null),
            new(900, 4, "System", 0.4, (long)2e7, (long)2e7, 1e5, 0, 200, 4000, DateTimeOffset.Now.AddHours(-5), 0, null),
        };
        var total = procs.Sum(p => p.CpuPercent);
        var apps = procs.Select(p => new AppUsage(p.Name.ToLowerInvariant(), p.Name.Replace(".exe", "", StringComparison.Ordinal), "Simulated", 1, p.CpuPercent,
            p.PrivateBytes, p.WorkingSetBytes, p.DiskBytesPerSec, p.GpuPercent, total > 0 ? p.CpuPercent / total : 0, p.PrivateBytes / procs.Sum(x => (double)x.PrivateBytes))).ToList();
        Latest = new ProcessSnapshot(ctx.Now, procs, apps, total);
        return Task.CompletedTask;
    }

    public ProcessDetails? GetDetails(int pid) =>
        Latest.Processes.FirstOrDefault(p => p.Pid == pid) is { } p
            ? new ProcessDetails(pid, p.Name, null, "Simulated", "Simulated process", "1.0", "x64", "Medium", p.StartTime, p.ParentPid, null)
            : null;
}

public sealed class SimulatedThermalProvider(SimulationWorld w) : SimulatedProvider(w, "thermal", "Thermal zones", "Thermals"), IThermalTelemetryProvider
{
    public IReadOnlyList<ThermalSensor> Sensors { get; private set; } = [];

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        Sensors =
        [
            new("cpu", "CPU package (simulated)", "CPU", Sim(World.CpuTemp, ctx.Now), 100, "Simulated limit"),
            new("ssd", "NVMe SSD (simulated)", "Storage", Sim(World.DiskTemp, ctx.Now), 80, "Simulated drive-reported critical temperature"),
        ];
        return Task.CompletedTask;
    }
}

public sealed class SimulatedPowerProvider(SimulationWorld w) : SimulatedProvider(w, "power", "Power state", "Power"), IPowerTelemetryProvider
{
    public bool AcOnline => World.AcOnline;
    public bool BatterySaverOn => false;
    public bool DisplayOn => true;
    public TimeSpan UserIdle => World.IsHeavy ? TimeSpan.Zero : TimeSpan.FromMinutes(12);

    public event EventHandler? Suspending { add { } remove { } }
    public event EventHandler? Resumed { add { } remove { } }
    public event EventHandler? PowerSourceChanged { add { } remove { } }

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        ctx.Metrics.Record(MetricKeys.UserIdleSeconds, UserIdle.TotalSeconds, ctx.Now);
        return Task.CompletedTask;
    }
}

public sealed class SimulatedEventProvider(SimulationWorld w) : SimulatedProvider(w, "events", "Windows event logs", "Reliability"), IWindowsEventProvider
{
    private double _lastTdr;

    public IReadOnlyList<SystemEvent> Recent { get; private set; } = [];

    public override TimeSpan GetInterval(SamplingMode mode) => mode == SamplingMode.Paused ? Intervals.Never : TimeSpan.FromSeconds(15);

    public override Task SampleAsync(SampleContext ctx, CancellationToken ct)
    {
        if (World.Scenario == SimulationScenario.DriverCrash && World.Seconds - _lastTdr > 120)
        {
            _lastTdr = World.Seconds;
            var e = new SystemEvent(ctx.Now, EventCategory.DisplayDriverReset, Severity.Warning, "Display driver stopped responding and recovered",
                "Simulated display driver timeout detection and recovery (TDR).", Source, "Simulated Discrete GPU 8GB", $"sim-tdr-{ctx.Now.ToUnixTimeSeconds()}", Code: "4101");
            ctx.Events.Publish(e);
            Recent = [e, .. Recent.Take(99)];
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SystemEvent>> ReadHistoryAsync(DateTimeOffset since, int maxEvents, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var list = new List<SystemEvent>
        {
            new(now.AddDays(-6).AddHours(2), EventCategory.UpdateInstalled, Severity.Info, "2026-09 Cumulative Update for Windows 11 (simulated)", null, Source, "Windows Update", "sim-upd-1"),
            new(now.AddDays(-3), EventCategory.DriverChanged, Severity.Info, "Simulated Discrete GPU 8GB driver changed", "552.22 → 560.94", Source, "Simulated Discrete GPU 8GB", "sim-drv-1"),
            new(now.AddDays(-2).AddHours(-4), EventCategory.AppCrash, Severity.Notice, "SimEditor.exe crashed", "Faulting module: simcore.dll", Source, "SimEditor.exe", "sim-crash-1", Code: "0xc0000005"),
            new(now.AddHours(-30), EventCategory.Boot, Severity.Info, "Windows started", null, Source, null, "sim-boot-1"),
        };
        if (World.Scenario == SimulationScenario.BsodTimeline)
        {
            list.Add(new(now.AddHours(-20), EventCategory.Bugcheck, Severity.Critical, "Windows stopped unexpectedly (stop code 0x0000009F)",
                "DRIVER_POWER_STATE_FAILURE", Source, null, "sim-bc-1", Code: "0x0000009F"));
            list.Add(new(now.AddHours(-20).AddMinutes(1), EventCategory.UnexpectedShutdown, Severity.Warning, "The system rebooted without cleanly shutting down",
                "Kernel-Power 41", Source, null, "sim-kp41-1", Code: "41"));
            list.Add(new(now.AddHours(-8), EventCategory.Bugcheck, Severity.Critical, "Windows stopped unexpectedly (stop code 0x0000009F)",
                "DRIVER_POWER_STATE_FAILURE", Source, null, "sim-bc-2", Code: "0x0000009F"));
            list.Add(new(now.AddHours(-8).AddMinutes(1), EventCategory.UnexpectedShutdown, Severity.Warning, "The system rebooted without cleanly shutting down",
                "Kernel-Power 41", Source, null, "sim-kp41-2", Code: "41"));
        }
        return Task.FromResult<IReadOnlyList<SystemEvent>>(list.Where(e => e.Timestamp >= since).Take(maxEvents).ToList());
    }
}
