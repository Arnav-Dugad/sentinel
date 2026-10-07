using System.Runtime.InteropServices;

namespace Sentinel.Platform.Windows.Interop;

internal sealed record DisplayPathInfo(
    long AdapterLuid,
    uint SourceId,
    uint TargetId,
    int OutputTechnology,
    int Rotation,
    double RefreshHz,
    int Width,
    int Height,
    int PositionX,
    int PositionY,
    string? GdiDeviceName,
    string? FriendlyName,
    ushort EdidManufacturerId,
    ushort EdidProductCode,
    bool? HdrSupported,
    bool? HdrEnabled,
    int? BitsPerColor);

/// <summary>
/// Read-only display topology via QueryDisplayConfig / DisplayConfigGetDeviceInfo.
/// SetDisplayConfig is deliberately not declared anywhere in Sentinel.
/// </summary>
internal static unsafe class DisplayConfig
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    private const int DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public uint SourceLuidLow;
        public int SourceLuidHigh;
        public uint SourceId;
        public uint SourceModeIdx;
        public uint SourceStatus;
        public uint TargetLuidLow;
        public int TargetLuidHigh;
        public uint TargetId;
        public uint TargetModeIdx;
        public int OutputTechnology;
        public int Rotation;
        public int Scaling;
        public uint RefreshNum;
        public uint RefreshDen;
        public int ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatus;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ModeInfo
    {
        [FieldOffset(0)] public int InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public uint LuidLow;
        [FieldOffset(12)] public int LuidHigh;
        // Source mode
        [FieldOffset(16)] public uint Width;
        [FieldOffset(20)] public uint Height;
        [FieldOffset(24)] public int PixelFormat;
        [FieldOffset(28)] public int PositionX;
        [FieldOffset(32)] public int PositionY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public int Type;
        public uint Size;
        public uint LuidLow;
        public int LuidHigh;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceName
    {
        public Header Header;
        public fixed char ViewGdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetName
    {
        public Header Header;
        public uint Flags;
        public int OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo
    {
        public Header Header;
        public uint Value;
        public int ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(void* request);

    public static List<DisplayPathInfo> Query()
    {
        var result = new List<DisplayPathInfo>();
        PathInfo[] paths;
        ModeInfo[] modes;
        uint numPaths, numModes;
        var attempts = 0;
        while (true)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes) != 0) return result;
            paths = new PathInfo[numPaths];
            modes = new ModeInfo[numModes];
            var rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (rc == 0) break;
            // ERROR_INSUFFICIENT_BUFFER: topology changed between the two calls; retry a few times.
            if (rc != 122 || ++attempts > 3) return result;
        }

        for (var i = 0; i < numPaths; i++)
        {
            var p = paths[i];
            int width = 0, height = 0, x = 0, y = 0;
            if (p.SourceModeIdx < numModes && modes[p.SourceModeIdx].InfoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            {
                var m = modes[p.SourceModeIdx];
                width = (int)m.Width;
                height = (int)m.Height;
                x = m.PositionX;
                y = m.PositionY;
            }
            var refresh = p.RefreshDen > 0 ? (double)p.RefreshNum / p.RefreshDen : 0;

            string? gdi = null;
            var sn = new SourceName { Header = MakeHeader(DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, sizeof(SourceName), p.SourceLuidLow, p.SourceLuidHigh, p.SourceId) };
            if (DisplayConfigGetDeviceInfo(&sn) == 0) gdi = new string(sn.ViewGdiDeviceName);

            string? friendly = null;
            ushort mfg = 0, product = 0;
            var tn = new TargetName { Header = MakeHeader(DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, sizeof(TargetName), p.TargetLuidLow, p.TargetLuidHigh, p.TargetId) };
            if (DisplayConfigGetDeviceInfo(&tn) == 0)
            {
                friendly = new string(tn.MonitorFriendlyDeviceName);
                mfg = tn.EdidManufactureId;
                product = tn.EdidProductCodeId;
            }

            bool? hdrSupported = null, hdrEnabled = null;
            int? bits = null;
            var ac = new AdvancedColorInfo { Header = MakeHeader(DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO, sizeof(AdvancedColorInfo), p.TargetLuidLow, p.TargetLuidHigh, p.TargetId) };
            if (DisplayConfigGetDeviceInfo(&ac) == 0)
            {
                hdrSupported = (ac.Value & 0x1) != 0;
                hdrEnabled = (ac.Value & 0x2) != 0;
                bits = ac.BitsPerColorChannel is > 0 and <= 16 ? (int)ac.BitsPerColorChannel : null;
            }

            result.Add(new DisplayPathInfo(((long)p.TargetLuidHigh << 32) | p.TargetLuidLow, p.SourceId, p.TargetId, p.OutputTechnology, p.Rotation, refresh,
                width, height, x, y, Clean(gdi), Clean(friendly), mfg, product, hdrSupported, hdrEnabled, bits));
        }
        return result;
    }

    private static Header MakeHeader(int type, int size, uint luidLow, int luidHigh, uint id) =>
        new() { Type = type, Size = (uint)size, LuidLow = luidLow, LuidHigh = luidHigh, Id = id };

    private static string? Clean(string? s)
    {
        if (s is null) return null;
        var i = s.IndexOf('\0');
        var t = (i >= 0 ? s[..i] : s).Trim();
        return t.Length == 0 ? null : Core.Privacy.Redactor.SanitizeUntrusted(t, 96);
    }

    /// <summary>Decodes the EDID three-letter PnP manufacturer ID (as returned by DisplayConfig, byte-swapped).</summary>
    public static string? EdidManufacturer(ushort raw)
    {
        if (raw == 0) return null;
        var v = (ushort)((raw >> 8) | (raw << 8));
        var a = (char)('A' + ((v >> 10) & 0x1F) - 1);
        var b = (char)('A' + ((v >> 5) & 0x1F) - 1);
        var c = (char)('A' + (v & 0x1F) - 1);
        return char.IsAsciiLetterUpper(a) && char.IsAsciiLetterUpper(b) && char.IsAsciiLetterUpper(c) ? $"{a}{b}{c}" : null;
    }

    public static string Connection(int tech) => tech switch
    {
        0 => "VGA",
        1 => "S-Video",
        2 => "Composite",
        3 => "Component",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS (internal)",
        8 => "D-Jpn",
        9 => "SDI",
        10 => "DisplayPort",
        11 => "Embedded DisplayPort (internal)",
        12 => "UDI",
        13 => "Embedded UDI (internal)",
        14 => "SDTV dongle",
        15 => "Miracast (wireless)",
        16 => "Indirect (wired)",
        17 => "Indirect (virtual)",
        18 => "DisplayPort over USB-C",
        unchecked((int)0x80000000) => "Internal",
        _ => "Other",
    };

    public static bool IsInternal(int tech) => tech is 6 or 11 or 13 or unchecked((int)0x80000000);
}
