using System.Runtime.InteropServices;

namespace Sentinel.Platform.Windows.Interop;

internal sealed record AudioEndpointInfo(string Id, string Name, bool IsCapture, uint State, int? SampleRate, int? Channels);

internal sealed record AudioSessionInfo(uint ProcessId, string? DisplayName, bool IsSystem, int State, float Peak);

/// <summary>
/// Core Audio (MMDevice + session API) through raw COM vtable calls, limited to enumeration and read-only queries.
/// No audio client or capture stream is ever activated: Sentinel cannot listen to any microphone.
/// Vtable slot numbers follow the interface order in mmdeviceapi.h / audiopolicy.h / endpointvolume.h.
/// </summary>
internal static unsafe class CoreAudio
{
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IID_IAudioSessionControl2 = new("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d");
    private static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    private static readonly Guid PKEY_FriendlyName_Fmt = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid PKEY_DeviceFormat_Fmt = new("f19f064d-082c-4e27-bc73-6882a1bb8e4c");

    public const uint DEVICE_STATE_ACTIVE = 0x1;
    private const uint DEVICE_STATE_DISABLED = 0x2;
    private const uint DEVICE_STATE_UNPLUGGED = 0x8;
    private const int CLSCTX_ALL = 0x17;
    private const ushort VT_LPWSTR = 31;
    private const ushort VT_BLOB = 65;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(8)] public uint BlobSize;
        [FieldOffset(16)] public IntPtr BlobData;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    private static IntPtr* Vtbl(IntPtr obj) => *(IntPtr**)obj;

    private static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(obj)[2])(obj);
    }

    private static IntPtr QueryInterface(IntPtr obj, Guid iid)
    {
        IntPtr result;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(obj)[0])(obj, &iid, &result) >= 0 ? result : IntPtr.Zero;
    }

    private static string? TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(p);
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>Enumerates endpoints and, optionally, active render sessions. Returns null when the audio service is unavailable.</summary>
    public static (List<AudioEndpointInfo> Endpoints, Dictionary<string, string?> Defaults, List<(string Device, AudioSessionInfo Session)> Sessions)? Query(bool includeSessions)
    {
        CoInitializeEx(IntPtr.Zero, 0x0); // COINIT_MULTITHREADED; harmless if already initialised
        var clsid = CLSID_MMDeviceEnumerator;
        var iid = IID_IMMDeviceEnumerator;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out var enumerator) < 0 || enumerator == IntPtr.Zero) return null;
        try
        {
            var defaults = new Dictionary<string, string?>();
            foreach (var (flow, role, key) in new[] { (0, 0, "render"), (0, 2, "render-comms"), (1, 0, "capture"), (1, 2, "capture-comms") })
            {
                IntPtr dev;
                var hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)Vtbl(enumerator)[4])(enumerator, flow, role, &dev);
                defaults[key] = hr >= 0 && dev != IntPtr.Zero ? DeviceId(dev) : null;
                Release(hr >= 0 ? dev : IntPtr.Zero);
            }

            var endpoints = new List<AudioEndpointInfo>();
            var sessions = new List<(string, AudioSessionInfo)>();
            IntPtr collection;
            if (((delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr*, int>)Vtbl(enumerator)[3])(enumerator, 2, DEVICE_STATE_ACTIVE | DEVICE_STATE_DISABLED | DEVICE_STATE_UNPLUGGED, &collection) < 0)
                return (endpoints, defaults, sessions);
            try
            {
                uint count;
                ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Vtbl(collection)[3])(collection, &count);
                for (uint i = 0; i < count && i < 64; i++)
                {
                    IntPtr dev;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(collection)[4])(collection, i, &dev) < 0 || dev == IntPtr.Zero) continue;
                    try
                    {
                        var id = DeviceId(dev) ?? "";
                        uint state;
                        ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Vtbl(dev)[6])(dev, &state);
                        var (name, rate, channels) = ReadProperties(dev);
                        var capture = id.StartsWith("{0.0.1.", StringComparison.Ordinal);
                        endpoints.Add(new AudioEndpointInfo(id, name ?? "Audio device", capture, state, rate, channels));
                        if (includeSessions && !capture && state == DEVICE_STATE_ACTIVE)
                            foreach (var s in ReadSessions(dev)) sessions.Add((name ?? "Audio device", s));
                    }
                    finally
                    {
                        Release(dev);
                    }
                }
            }
            finally
            {
                Release(collection);
            }
            return (endpoints, defaults, sessions);
        }
        finally
        {
            Release(enumerator);
        }
    }

    private static string? DeviceId(IntPtr dev)
    {
        IntPtr p;
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(dev)[5])(dev, &p) >= 0 ? TakeString(p) : null;
    }

    private static (string? Name, int? Rate, int? Channels) ReadProperties(IntPtr dev)
    {
        IntPtr store;
        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(dev)[4])(dev, 0 /* STGM_READ */, &store) < 0 || store == IntPtr.Zero) return (null, null, null);
        try
        {
            string? name = null;
            int? rate = null, channels = null;
            var key = new PropertyKey { FmtId = PKEY_FriendlyName_Fmt, Pid = 14 };
            var pv = default(PropVariant);
            if (((delegate* unmanaged[Stdcall]<IntPtr, PropertyKey*, PropVariant*, int>)Vtbl(store)[5])(store, &key, &pv) >= 0)
            {
                if (pv.Vt == VT_LPWSTR && pv.Pointer != IntPtr.Zero) name = Marshal.PtrToStringUni(pv.Pointer);
                PropVariantClear(ref pv);
            }
            key = new PropertyKey { FmtId = PKEY_DeviceFormat_Fmt, Pid = 0 };
            pv = default;
            if (((delegate* unmanaged[Stdcall]<IntPtr, PropertyKey*, PropVariant*, int>)Vtbl(store)[5])(store, &key, &pv) >= 0)
            {
                // WAVEFORMATEX: wFormatTag (2), nChannels (2), nSamplesPerSec (4)
                if (pv.Vt == VT_BLOB && pv.BlobSize >= 16 && pv.BlobData != IntPtr.Zero)
                {
                    var ch = Marshal.ReadInt16(pv.BlobData, 2);
                    var sr = Marshal.ReadInt32(pv.BlobData, 4);
                    channels = ch is > 0 and < 64 ? ch : null;
                    rate = sr is > 0 and < 1_000_000 ? sr : null;
                }
                PropVariantClear(ref pv);
            }
            return (name is null ? null : Core.Privacy.Redactor.SanitizeUntrusted(name, 96), rate, channels);
        }
        finally
        {
            Release(store);
        }
    }

    private static List<AudioSessionInfo> ReadSessions(IntPtr dev)
    {
        var result = new List<AudioSessionInfo>();
        var iid = IID_IAudioSessionManager2;
        IntPtr manager;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, IntPtr, IntPtr*, int>)Vtbl(dev)[3])(dev, &iid, CLSCTX_ALL, IntPtr.Zero, &manager) < 0 || manager == IntPtr.Zero)
            return result;
        try
        {
            IntPtr en;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(manager)[5])(manager, &en) < 0 || en == IntPtr.Zero) return result;
            try
            {
                int n;
                ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(en)[3])(en, &n);
                for (var i = 0; i < n && i < 64; i++)
                {
                    IntPtr control;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Vtbl(en)[4])(en, i, &control) < 0 || control == IntPtr.Zero) continue;
                    try
                    {
                        int state;
                        ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(control)[3])(control, &state);
                        IntPtr namePtr;
                        var display = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(control)[4])(control, &namePtr) >= 0 ? TakeString(namePtr) : null;
                        uint pid = 0;
                        var isSystem = false;
                        var c2 = QueryInterface(control, IID_IAudioSessionControl2);
                        if (c2 != IntPtr.Zero)
                        {
                            ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Vtbl(c2)[14])(c2, &pid);
                            isSystem = ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(c2)[15])(c2) == 0; // S_OK = system sounds session
                            Release(c2);
                        }
                        var peak = 0f;
                        var meter = QueryInterface(control, IID_IAudioMeterInformation);
                        if (meter != IntPtr.Zero)
                        {
                            ((delegate* unmanaged[Stdcall]<IntPtr, float*, int>)Vtbl(meter)[3])(meter, &peak);
                            Release(meter);
                        }
                        result.Add(new AudioSessionInfo(pid, display, isSystem, state, peak));
                    }
                    finally
                    {
                        Release(control);
                    }
                }
            }
            finally
            {
                Release(en);
            }
        }
        finally
        {
            Release(manager);
        }
        return result;
    }
}
