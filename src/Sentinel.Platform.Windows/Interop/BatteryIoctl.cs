using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sentinel.Platform.Windows.Interop;

internal sealed record BatteryStaticInfo(
    uint Tag,
    uint Capabilities,
    string? Chemistry,
    uint DesignedCapacity,
    uint FullChargedCapacity,
    uint CycleCount,
    string? DeviceName,
    string? Manufacturer,
    string? SerialNumber,
    DateTimeOffset? ManufactureDate)
{
    public const uint BATTERY_CAPACITY_RELATIVE = 0x40000000;
    public bool CapacityIsRelative => (Capabilities & BATTERY_CAPACITY_RELATIVE) != 0;
}

internal sealed record BatteryStatusInfo(uint PowerState, uint Capacity, uint Voltage, int Rate)
{
    public const uint BATTERY_POWER_ON_LINE = 0x1;
    public const uint BATTERY_DISCHARGING = 0x2;
    public const uint BATTERY_CHARGING = 0x4;
    public const uint UNKNOWN = 0xFFFFFFFF;
    public const int UNKNOWN_RATE = unchecked((int)0x80000000);
}

/// <summary>
/// Battery IOCTLs as documented in "Enumerating Battery Devices". Read-only queries on the battery
/// device interface; this is how Windows' own battery UI obtains the same values.
/// </summary>
internal static unsafe class BatteryIoctl
{
    public static readonly Guid GUID_DEVICE_BATTERY = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
    private const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;
    private const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;

    private const int BatteryInformation = 0;
    private const int BatteryTemperature = 2;
    private const int BatteryDeviceName = 4;
    private const int BatteryManufactureDate = 5;
    private const int BatteryManufactureName = 6;
    private const int BatterySerialNumber = 8;

    public static List<string> EnumeratePaths() => Native.EnumerateInterfacePaths(GUID_DEVICE_BATTERY);

    public static SafeFileHandle Open(string path) =>
        Native.CreateFileW(path, Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);

    public static uint QueryTag(SafeFileHandle h)
    {
        uint wait = 0, tag = 0;
        return Native.DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, &wait, 4, &tag, 4, out _, IntPtr.Zero) ? tag : 0;
    }

    private static byte[]? QueryInfo(SafeFileHandle h, uint tag, int level, int size)
    {
        Span<byte> q = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(q, tag);
        BinaryPrimitives.WriteInt32LittleEndian(q[4..], level);
        BinaryPrimitives.WriteUInt32LittleEndian(q[8..], 0);
        var output = new byte[size];
        fixed (byte* qp = q)
        fixed (byte* op = output)
        {
            if (!Native.DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, qp, 12, op, (uint)size, out var returned, IntPtr.Zero) || returned == 0) return null;
            return output[..(int)returned];
        }
    }

    private static string? QueryString(SafeFileHandle h, uint tag, int level)
    {
        var b = QueryInfo(h, tag, level, 512);
        if (b is null) return null;
        var s = Encoding.Unicode.GetString(b).TrimEnd('\0').Trim();
        return s.Length == 0 ? null : Core.Privacy.Redactor.SanitizeUntrusted(s, 64);
    }

    public static BatteryStaticInfo? QueryStatic(SafeFileHandle h, uint tag)
    {
        var info = QueryInfo(h, tag, BatteryInformation, 64);
        if (info is null || info.Length < 36) return null;
        var chem = Encoding.ASCII.GetString(info, 8, 4).TrimEnd('\0', ' ');
        DateTimeOffset? made = null;
        var md = QueryInfo(h, tag, BatteryManufactureDate, 8);
        if (md is { Length: >= 4 })
        {
            var day = md[0];
            var month = md[1];
            var year = BinaryPrimitives.ReadUInt16LittleEndian(md.AsSpan(2));
            if (year is > 1990 and < 2100 && month is >= 1 and <= 12 && day is >= 1 and <= 31)
            {
                try
                {
                    made = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }
        }
        return new BatteryStaticInfo(tag,
            BinaryPrimitives.ReadUInt32LittleEndian(info),
            ChemistryName(chem),
            BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(12)),
            BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(16)),
            BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(32)),
            QueryString(h, tag, BatteryDeviceName),
            QueryString(h, tag, BatteryManufactureName),
            QueryString(h, tag, BatterySerialNumber),
            made);
    }

    /// <summary>Battery temperature in °C, when the firmware reports it (often not).</summary>
    public static double? QueryTemperature(SafeFileHandle h, uint tag)
    {
        var b = QueryInfo(h, tag, BatteryTemperature, 4);
        if (b is not { Length: 4 }) return null;
        var tenthsKelvin = BinaryPrimitives.ReadUInt32LittleEndian(b);
        var c = tenthsKelvin / 10.0 - 273.15;
        return c is > -40 and < 100 ? c : null;
    }

    public static BatteryStatusInfo? QueryStatus(SafeFileHandle h, uint tag)
    {
        Span<byte> wait = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(wait, tag);
        Span<byte> status = stackalloc byte[16];
        fixed (byte* wp = wait)
        fixed (byte* sp = status)
        {
            if (!Native.DeviceIoControl(h, IOCTL_BATTERY_QUERY_STATUS, wp, 20, sp, 16, out var returned, IntPtr.Zero) || returned < 16) return null;
        }
        return new BatteryStatusInfo(
            BinaryPrimitives.ReadUInt32LittleEndian(status),
            BinaryPrimitives.ReadUInt32LittleEndian(status[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(status[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(status[12..]));
    }

    private static string? ChemistryName(string code) => code.ToUpperInvariant() switch
    {
        "LION" or "LI-I" or "LI I" => "Lithium-ion",
        "LIP" or "LIPO" => "Lithium polymer",
        "PBAC" => "Lead acid",
        "NICD" => "Nickel cadmium",
        "NIMH" => "Nickel metal hydride",
        "NIZN" => "Nickel zinc",
        "RAM" => "Rechargeable alkaline-manganese",
        "" => null,
        _ => $"Unrecognised code \"{code}\"",
    };
}
