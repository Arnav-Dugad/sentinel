using System.Runtime.InteropServices;
using System.Text;

namespace Sentinel.Platform.Windows.Interop;

/// <summary>
/// Minimal binding to NVIDIA's documented NVML library, restricted to read-only "Get" queries.
/// No NVML setter (clocks, power limits, fan, persistence, ECC, etc.) is bound — intentionally.
/// The library is only ever loaded by full path from locations the NVIDIA driver installs to,
/// preventing DLL search-order hijacking.
/// </summary>
internal sealed unsafe class Nvml : IDisposable
{
    public const int NVML_SUCCESS = 0;
    public const int NVML_ERROR_NOT_SUPPORTED = 3;

    private readonly IntPtr _lib;
    private readonly delegate* unmanaged[Cdecl]<int> _init;
    private readonly delegate* unmanaged[Cdecl]<int> _shutdown;
    private readonly delegate* unmanaged[Cdecl]<uint*, int> _getCount;
    private readonly delegate* unmanaged[Cdecl]<uint, IntPtr*, int> _getHandle;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int> _getName;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, PciInfo*, int> _getPci;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> _getTemp;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> _getTempThreshold;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, int> _getPower;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, int> _getPowerLimit;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> _getClock;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, int> _getFan;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int*, int> _getPState;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int> _getEnc;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int> _getDec;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int> _getUtil;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, ulong*, int> _getThrottle;

    [StructLayout(LayoutKind.Sequential)]
    public struct PciInfo
    {
        public fixed byte BusIdLegacy[16];
        public uint Domain;
        public uint Bus;
        public uint Device;
        public uint PciDeviceId;
        public uint PciSubSystemId;
        public fixed byte BusId[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    private Nvml(IntPtr lib)
    {
        _lib = lib;
        _init = (delegate* unmanaged[Cdecl]<int>)Export("nvmlInit_v2");
        _shutdown = (delegate* unmanaged[Cdecl]<int>)Export("nvmlShutdown");
        _getCount = (delegate* unmanaged[Cdecl]<uint*, int>)Export("nvmlDeviceGetCount_v2");
        _getHandle = (delegate* unmanaged[Cdecl]<uint, IntPtr*, int>)Export("nvmlDeviceGetHandleByIndex_v2");
        _getName = (delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int>)Export("nvmlDeviceGetName");
        _getPci = (delegate* unmanaged[Cdecl]<IntPtr, PciInfo*, int>)Export("nvmlDeviceGetPciInfo_v3");
        _getTemp = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)Export("nvmlDeviceGetTemperature");
        _getTempThreshold = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)Export("nvmlDeviceGetTemperatureThreshold");
        _getPower = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)Export("nvmlDeviceGetPowerUsage");
        _getPowerLimit = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)Export("nvmlDeviceGetEnforcedPowerLimit");
        _getClock = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)Export("nvmlDeviceGetClockInfo");
        _getFan = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)Export("nvmlDeviceGetFanSpeed");
        _getPState = (delegate* unmanaged[Cdecl]<IntPtr, int*, int>)Export("nvmlDeviceGetPerformanceState");
        _getEnc = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int>)Export("nvmlDeviceGetEncoderUtilization");
        _getDec = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int>)Export("nvmlDeviceGetDecoderUtilization");
        _getUtil = (delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int>)Export("nvmlDeviceGetUtilizationRates");
        _getThrottle = (delegate* unmanaged[Cdecl]<IntPtr, ulong*, int>)(Export("nvmlDeviceGetCurrentClocksEventReasons") is var e && e != IntPtr.Zero
            ? e : Export("nvmlDeviceGetCurrentClocksThrottleReasons"));
    }

    private IntPtr Export(string name) => NativeLibrary.TryGetExport(_lib, name, out var p) ? p : IntPtr.Zero;

    public static Nvml? TryLoad()
    {
        string[] candidates =
        [
            Path.Combine(Environment.SystemDirectory, "nvml.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
        ];
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            if (!NativeLibrary.TryLoad(path, out var lib)) continue;
            var nvml = new Nvml(lib);
            if (nvml._init == null || nvml._getCount == null || nvml._getHandle == null || nvml._init() != NVML_SUCCESS)
            {
                NativeLibrary.Free(lib);
                continue;
            }
            return nvml;
        }
        return null;
    }

    public int DeviceCount()
    {
        uint n = 0;
        return _getCount(&n) == NVML_SUCCESS ? (int)n : 0;
    }

    public IntPtr Handle(int index)
    {
        IntPtr h;
        return _getHandle((uint)index, &h) == NVML_SUCCESS ? h : IntPtr.Zero;
    }

    public string? Name(IntPtr dev)
    {
        if (_getName == null) return null;
        var buf = stackalloc byte[96];
        return _getName(dev, buf, 96) == NVML_SUCCESS ? Encoding.ASCII.GetString(buf, 96).TrimEnd('\0') : null;
    }

    /// <summary>Returns (vendorId, deviceId) parsed from the PCI device id.</summary>
    public (uint Vendor, uint Device)? Pci(IntPtr dev)
    {
        if (_getPci == null) return null;
        PciInfo info;
        if (_getPci(dev, &info) != NVML_SUCCESS) return null;
        return (info.PciDeviceId & 0xFFFF, info.PciDeviceId >> 16);
    }

    public uint? Temperature(IntPtr dev) => Get(_getTemp, dev, 0);

    /// <summary>Threshold 1 = slowdown, 0 = shutdown (NVML_TEMPERATURE_THRESHOLD_*). Read-only.</summary>
    public uint? TemperatureThreshold(IntPtr dev, int threshold) => Get(_getTempThreshold, dev, threshold);

    public uint? PowerMilliwatts(IntPtr dev) => Get(_getPower, dev);

    public uint? PowerLimitMilliwatts(IntPtr dev) => Get(_getPowerLimit, dev);

    /// <summary>Clock type: 0 graphics, 1 SM, 2 memory, 3 video.</summary>
    public uint? ClockMhz(IntPtr dev, int type) => Get(_getClock, dev, type);

    public uint? FanPercent(IntPtr dev) => Get(_getFan, dev);

    public int? PerformanceState(IntPtr dev)
    {
        if (_getPState == null) return null;
        int v;
        return _getPState(dev, &v) == NVML_SUCCESS ? v : null;
    }

    public uint? EncoderPercent(IntPtr dev)
    {
        if (_getEnc == null) return null;
        uint v, period;
        return _getEnc(dev, &v, &period) == NVML_SUCCESS ? v : null;
    }

    public uint? DecoderPercent(IntPtr dev)
    {
        if (_getDec == null) return null;
        uint v, period;
        return _getDec(dev, &v, &period) == NVML_SUCCESS ? v : null;
    }

    public Utilization? Util(IntPtr dev)
    {
        if (_getUtil == null) return null;
        Utilization u;
        return _getUtil(dev, &u) == NVML_SUCCESS ? u : null;
    }

    public ulong? ThrottleReasons(IntPtr dev)
    {
        if (_getThrottle == null) return null;
        ulong v;
        return _getThrottle(dev, &v) == NVML_SUCCESS ? v : null;
    }

    public static string? DescribeThrottle(ulong reasons)
    {
        var parts = new List<string>();
        if ((reasons & 0x4) != 0) parts.Add("Software power cap");
        if ((reasons & 0x8) != 0) parts.Add("Hardware slowdown");
        if ((reasons & 0x20) != 0) parts.Add("Software thermal slowdown");
        if ((reasons & 0x40) != 0) parts.Add("Hardware thermal slowdown");
        if ((reasons & 0x80) != 0) parts.Add("Power brake slowdown");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static uint? Get(delegate* unmanaged[Cdecl]<IntPtr, uint*, int> fn, IntPtr dev)
    {
        if (fn == null) return null;
        uint v;
        return fn(dev, &v) == NVML_SUCCESS ? v : null;
    }

    private static uint? Get(delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> fn, IntPtr dev, int arg)
    {
        if (fn == null) return null;
        uint v;
        return fn(dev, arg, &v) == NVML_SUCCESS ? v : null;
    }

    public void Dispose()
    {
        if (_shutdown != null) _shutdown();
        NativeLibrary.Free(_lib);
    }
}
