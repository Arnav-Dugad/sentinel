using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Sentinel.Platform.Windows.Interop;

internal sealed record WlanConnection(
    Guid InterfaceGuid,
    string InterfaceDescription,
    bool Connected,
    string? Ssid,
    int PhyType,
    uint SignalQuality,
    uint RxRateKbps,
    uint TxRateKbps,
    int AuthAlgorithm,
    int? RssiDbm,
    uint? Channel,
    uint? CenterFrequencyKhz);

/// <summary>
/// Native Wi-Fi (wlanapi) read-only queries for the current connection. No scans are triggered and no
/// profiles or settings are touched. On recent Windows builds some fields require location permission.
/// </summary>
internal static class Wlan
{
    private const int wlan_intf_opcode_current_connection = 7;
    private const int wlan_intf_opcode_channel_number = 8;
    private const int wlan_intf_opcode_rssi = 0x10000102;

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved, out uint dataSize,
        out IntPtr data, IntPtr opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetNetworkBssList(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr ssid, int bssType,
        [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, IntPtr reserved, out IntPtr bssList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    public static uint LastError { get; private set; }

    public static List<WlanConnection>? Query()
    {
        uint rc;
        try
        {
            rc = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
            if (rc != 0)
            {
                LastError = rc;
                return null;
            }
            try
            {
                return QueryInterfaces(handle);
            }
            finally
            {
                WlanCloseHandle(handle, IntPtr.Zero);
            }
        }
        catch (DllNotFoundException)
        {
            // WLAN AutoConfig is not installed (e.g. Windows Server without the Wireless LAN feature).
            LastError = 1062;
            return null;
        }
    }

    private static List<WlanConnection> QueryInterfaces(IntPtr handle)
    {
        var result = new List<WlanConnection>();
        if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return result;
        try
        {
            var count = Marshal.ReadInt32(list);
            // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems, dwIndex, then WLAN_INTERFACE_INFO[] (16 + 512 + 4 = 532 bytes each).
            for (var i = 0; i < count && i < 8; i++)
            {
                var item = list + 8 + i * 532;
                var guid = Marshal.PtrToStructure<Guid>(item);
                var desc = Marshal.PtrToStringUni(item + 16, 256).TrimEnd('\0');
                var state = Marshal.ReadInt32(item + 528);
                result.Add(QueryConnection(handle, guid, Core.Privacy.Redactor.SanitizeUntrusted(desc, 128), state));
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }
        return result;
    }

    private static WlanConnection QueryConnection(IntPtr handle, Guid guid, string description, int interfaceState)
    {
        const int wlan_interface_state_connected = 1;
        var connected = interfaceState == wlan_interface_state_connected;
        if (!connected) return new WlanConnection(guid, description, false, null, 0, 0, 0, 0, 0, null, null, null);

        string? ssid = null;
        int phy = 0, auth = 0;
        uint quality = 0, rx = 0, tx = 0;
        byte[]? ssidRaw = null;
        var rc = WlanQueryInterface(handle, ref guid, wlan_intf_opcode_current_connection, IntPtr.Zero, out var size, out var data, IntPtr.Zero);
        LastError = rc;
        if (rc == 0 && data != IntPtr.Zero)
        {
            try
            {
                if (size >= 604)
                {
                    var buf = new byte[604];
                    Marshal.Copy(data, buf, 0, 604);
                    var ssidLen = (int)Math.Min(32, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(520)));
                    ssidRaw = buf.AsSpan(520, 36).ToArray();
                    ssid = ssidLen > 0 ? Core.Privacy.Redactor.SanitizeUntrusted(Encoding.UTF8.GetString(buf, 524, ssidLen), 32) : null;
                    phy = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(568));
                    quality = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(576));
                    rx = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(580));
                    tx = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(584));
                    auth = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(596));
                }
            }
            finally
            {
                WlanFreeMemory(data);
            }
        }

        int? rssi = null;
        if (WlanQueryInterface(handle, ref guid, wlan_intf_opcode_rssi, IntPtr.Zero, out var rsz, out var rdata, IntPtr.Zero) == 0 && rdata != IntPtr.Zero)
        {
            if (rsz >= 4) rssi = Marshal.ReadInt32(rdata);
            WlanFreeMemory(rdata);
        }
        uint? channel = null;
        if (WlanQueryInterface(handle, ref guid, wlan_intf_opcode_channel_number, IntPtr.Zero, out var csz, out var cdata, IntPtr.Zero) == 0 && cdata != IntPtr.Zero)
        {
            if (csz >= 4) channel = (uint)Marshal.ReadInt32(cdata);
            WlanFreeMemory(cdata);
        }
        var freq = ssidRaw is null ? null : CenterFrequency(handle, guid, ssidRaw, rssi);
        return new WlanConnection(guid, description, true, ssid, phy, quality, rx, tx, auth, rssi is < 0 and > -120 ? rssi : null, channel, freq);
    }

    /// <summary>Reads the cached BSS list (no scan) to find the connected network's centre frequency, which identifies the band.</summary>
    private static uint? CenterFrequency(IntPtr handle, Guid guid, byte[] dot11Ssid, int? rssi)
    {
        var ssidPtr = Marshal.AllocHGlobal(36);
        try
        {
            Marshal.Copy(dot11Ssid, 0, ssidPtr, 36);
            if (WlanGetNetworkBssList(handle, ref guid, ssidPtr, 1, true, IntPtr.Zero, out var list) != 0 || list == IntPtr.Zero) return null;
            try
            {
                var count = Marshal.ReadInt32(list, 4);
                uint? best = null;
                var bestDelta = int.MaxValue;
                for (var i = 0; i < count && i < 64; i++)
                {
                    var entry = list + 8 + i * 360;
                    var entryRssi = Marshal.ReadInt32(entry, 56);
                    var freq = (uint)Marshal.ReadInt32(entry, 92);
                    var delta = rssi is { } r ? Math.Abs(entryRssi - r) : 0;
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        best = freq;
                    }
                }
                return best;
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ssidPtr);
        }
    }

    public static string PhyName(int phy) => phy switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "802.11n (Wi-Fi 4)",
        8 => "802.11ac (Wi-Fi 5)",
        9 => "802.11ad (WiGig)",
        10 => "802.11ax (Wi-Fi 6/6E)",
        11 => "802.11be (Wi-Fi 7)",
        _ => "Unknown",
    };

    public static string? Band(uint? centerKhz, uint? channel)
    {
        if (centerKhz is { } f and > 0)
        {
            var mhz = f / 1000;
            return mhz switch { < 3000 => "2.4 GHz", < 5925 => "5 GHz", < 7200 => "6 GHz", _ => "60 GHz" };
        }
        return channel switch { >= 1 and <= 14 => "2.4 GHz", >= 32 and <= 177 => "5 GHz (or 6 GHz)", _ => null };
    }

    public static string AuthName(int auth) => auth switch
    {
        1 => "Open",
        2 => "Shared key",
        3 => "WPA-Enterprise",
        4 => "WPA-Personal",
        5 => "WPA-None",
        6 => "WPA2-Enterprise",
        7 => "WPA2-Personal",
        8 => "WPA3-Enterprise (192-bit)",
        9 => "WPA3-Personal",
        10 => "WPA3-Enterprise",
        11 => "OWE",
        _ => "Unknown",
    };
}
