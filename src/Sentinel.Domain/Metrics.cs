namespace Sentinel.Domain;

public enum MetricUnit
{
    None,
    Percent,
    Celsius,
    Watts,
    Milliwatts,
    MilliwattHours,
    Megahertz,
    Bytes,
    BytesPerSecond,
    PerSecond,
    Milliseconds,
    Rpm,
    Count,
    Volts,
}

/// <summary>How a metric is retained in the history database.</summary>
public enum PersistPolicy
{
    /// <summary>Live ring buffer only.</summary>
    LiveOnly,
    /// <summary>Live + 10-second detail tier (short retention).</summary>
    Detail,
    /// <summary>Live + detail + minute and hour aggregates (long retention).</summary>
    Full,
}

public sealed record MetricDefinition(
    string Key,
    string Name,
    MetricUnit Unit,
    string Category,
    string Description,
    PersistPolicy Persist = PersistPolicy.Full);

/// <summary>Well-known metric keys. Per-device metrics use the <c>{prefix}.{deviceId}.{name}</c> pattern.</summary>
public static class MetricKeys
{
    public const string CpuUtil = "cpu.util";
    public const string CpuFreq = "cpu.freq";
    public const string CpuQueue = "cpu.queue";
    public const string CpuContextSwitches = "cpu.ctxsw";
    public const string CpuInterrupts = "cpu.interrupts";
    public const string CpuDpc = "cpu.dpc";
    public const string CpuTemp = "cpu.temp";
    public const string CpuPower = "cpu.power";
    public static string CpuCore(int i) => $"cpu.core.{i}.util";

    public const string MemUsedPct = "mem.used.pct";
    public const string MemUsed = "mem.used";
    public const string MemCommit = "mem.commit";
    public const string MemCached = "mem.cached";
    public const string MemHardFaults = "mem.hardfaults";
    public const string MemPageFile = "mem.pagefile.pct";

    public const string GpuUtilAny = "gpu.util";
    public const string GpuTempAny = "gpu.temp";
    public static string Gpu(string id, string name) => $"gpu.{id}.{name}";

    public const string DiskRead = "disk.read";
    public const string DiskWrite = "disk.write";
    public const string DiskActive = "disk.active";
    public static string Disk(string id, string name) => $"disk.{id}.{name}";

    public const string NetRx = "net.rx";
    public const string NetTx = "net.tx";
    public const string WifiSignal = "net.wifi.signal";
    public const string WifiRssi = "net.wifi.rssi";

    public const string BatPercent = "bat.pct";
    public const string BatRate = "bat.rate";
    public const string BatCapacity = "bat.capacity";
    public const string BatFullCharge = "bat.full";
    public const string BatVoltage = "bat.voltage";
    public const string AcOnline = "power.ac";
    public const string UserIdleSeconds = "user.idle";

    public static string Thermal(string id) => $"thermal.{id}";
}
