using System.Runtime.InteropServices;

namespace Sentinel.Platform.Windows.Interop;

internal sealed record DxgiAdapterDesc(string Description, uint VendorId, uint DeviceId, uint SubSysId, uint Revision,
    ulong DedicatedVideoMemory, ulong DedicatedSystemMemory, ulong SharedSystemMemory, uint LuidLow, int LuidHigh, bool IsSoftware)
{
    /// <summary>Matches the "luid_0xHHHHHHHH_0xLLLLLLLL" prefix used by GPU performance counter instances.</summary>
    public string LuidKey => $"luid_0x{LuidHigh:X8}_0x{LuidLow:X8}";

    public long Luid => ((long)LuidHigh << 32) | LuidLow;
}

/// <summary>
/// Enumerates graphics adapters with DXGI (EnumAdapters1 + GetDesc1). This only reads adapter descriptions;
/// no Direct3D device is created, so idle discrete GPUs are not woken.
/// </summary>
internal static unsafe class Dxgi
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    public static List<DxgiAdapterDesc> EnumerateAdapters()
    {
        var list = new List<DxgiAdapterDesc>();
        var iid = IID_IDXGIFactory1;
        if (CreateDXGIFactory1(ref iid, out var factory) < 0 || factory == IntPtr.Zero) return list;
        try
        {
            var vtbl = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtbl[12];
            for (uint i = 0; i < 16; i++)
            {
                IntPtr adapter;
                var hr = enumAdapters1(factory, i, &adapter);
                if (hr == DXGI_ERROR_NOT_FOUND || hr < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    var avtbl = *(IntPtr**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC1_RAW*, int>)avtbl[10];
                    DXGI_ADAPTER_DESC1_RAW raw;
                    if (getDesc1(adapter, &raw) < 0) continue;
                    var desc = new string(raw.Description, 0, 128).TrimEnd('\0');
                    var d = new DxgiAdapterDesc(Core.Privacy.Redactor.SanitizeUntrusted(desc, 128), raw.VendorId, raw.DeviceId, raw.SubSysId, raw.Revision,
                        raw.DedicatedVideoMemory, raw.DedicatedSystemMemory, raw.SharedSystemMemory, raw.LuidLow, raw.LuidHigh,
                        (raw.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0);
                    if (list.All(x => x.Luid != d.Luid)) list.Add(d);
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }
        return list;
    }

    private static void Release(IntPtr unknown)
    {
        var vtbl = *(IntPtr**)unknown;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2])(unknown);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_ADAPTER_DESC1_RAW
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}
