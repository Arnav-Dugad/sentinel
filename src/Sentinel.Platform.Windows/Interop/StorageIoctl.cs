using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sentinel.Platform.Windows.Interop;

internal sealed record StorageDeviceDescriptor(string? Vendor, string? Product, string? Revision, string? Serial, int BusType, bool Removable);

internal sealed record StorageTemperature(double CurrentC, double? WarningC, double? CriticalC);

/// <summary>Parsed NVMe SMART / Health Information log page (log identifier 02h), per the NVMe base specification.</summary>
public sealed record NvmeHealthLog(
    byte CriticalWarning,
    double CompositeTemperatureC,
    byte AvailableSpare,
    byte AvailableSpareThreshold,
    byte PercentageUsed,
    double DataUnitsReadBytes,
    double DataUnitsWrittenBytes,
    double PowerCycles,
    double PowerOnHours,
    double UnsafeShutdowns,
    double MediaErrors,
    double ErrorLogEntries)
{
    public static NvmeHealthLog? Parse(ReadOnlySpan<byte> log)
    {
        if (log.Length < 192) return null;
        var tempK = BinaryPrimitives.ReadUInt16LittleEndian(log[1..3]);
        // A data unit is 1000 × 512 bytes.
        return new NvmeHealthLog(
            log[0],
            tempK > 0 ? tempK - 273.15 : double.NaN,
            log[3],
            log[4],
            log[5],
            U128(log[32..48]) * 512_000d,
            U128(log[48..64]) * 512_000d,
            U128(log[112..128]),
            U128(log[128..144]),
            U128(log[144..160]),
            U128(log[160..176]),
            U128(log[176..192]));
    }

    private static double U128(ReadOnlySpan<byte> b) =>
        BinaryPrimitives.ReadUInt64LittleEndian(b[..8]) + BinaryPrimitives.ReadUInt64LittleEndian(b[8..16]) * 18446744073709551616d;

    public static string DescribeCriticalWarning(byte w)
    {
        if (w == 0) return "None";
        var parts = new List<string>();
        if ((w & 0x01) != 0) parts.Add("available spare below threshold");
        if ((w & 0x02) != 0) parts.Add("temperature threshold exceeded");
        if ((w & 0x04) != 0) parts.Add("reliability degraded");
        if ((w & 0x08) != 0) parts.Add("media placed in read-only mode");
        if ((w & 0x10) != 0) parts.Add("volatile memory backup failed");
        if ((w & 0x20) != 0) parts.Add("persistent memory region read-only");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Storage property queries. Devices are opened with zero access rights (query-only), which is sufficient for
/// IOCTL_STORAGE_QUERY_PROPERTY. No pass-through commands, no writes, no vendor commands.
/// </summary>
internal static unsafe class StorageIoctl
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int StorageDeviceTemperatureProperty = 52;
    private const int ProtocolTypeNvme = 3;
    private const int NVMeDataTypeLogPage = 2;
    private const int NvmeLogPageHealthInfo = 2;
    private const int ProtocolSpecificDataSize = 40;
    private const int NvmeHealthLogSize = 512;

    public static SafeFileHandle OpenQueryOnly(string path) =>
        Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);

    public static SafeFileHandle OpenDisk(int number) => OpenQueryOnly($@"\\.\PhysicalDrive{number}");

    public static StorageDeviceDescriptor? QueryDevice(SafeFileHandle h)
    {
        var buf = Query(h, StorageDeviceProperty, 1024);
        if (buf is null || buf.Length < 36) return null;
        string? Str(int offsetField)
        {
            var off = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(offsetField));
            if (off <= 0 || off >= buf.Length) return null;
            var end = Array.IndexOf(buf, (byte)0, off);
            if (end < 0) end = buf.Length;
            var s = Encoding.ASCII.GetString(buf, off, end - off).Trim();
            return s.Length == 0 ? null : Core.Privacy.Redactor.SanitizeUntrusted(s, 96);
        }
        return new StorageDeviceDescriptor(Str(12), Str(16), Str(20), Str(24), BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(28)), buf[10] != 0);
    }

    public static bool? IncursSeekPenalty(SafeFileHandle h)
    {
        var buf = Query(h, StorageDeviceSeekPenaltyProperty, 16);
        return buf is { Length: >= 9 } ? buf[8] != 0 : null;
    }

    public static StorageTemperature? QueryTemperature(SafeFileHandle h)
    {
        var buf = Query(h, StorageDeviceTemperatureProperty, 512);
        if (buf is null || buf.Length < 24) return null;
        var critical = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(8));
        var warning = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(10));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(12));
        if (count == 0 || buf.Length < 24 + 16) return null;
        // STORAGE_TEMPERATURE_INFO[0].Temperature is at offset 24 + 2 (after the Index field).
        var current = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(26));
        if (current is <= -60 or >= 200) return null;
        return new StorageTemperature(current, warning is > 0 and < 200 ? warning : null, critical is > 0 and < 200 ? critical : null);
    }

    public static NvmeHealthLog? QueryNvmeHealth(SafeFileHandle h)
    {
        const int headerSize = 8;
        var size = headerSize + ProtocolSpecificDataSize + NvmeHealthLogSize;
        var buffer = new byte[size];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 0); // PropertyStandardQuery
        var p = span[headerSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, ProtocolTypeNvme);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], NVMeDataTypeLogPage);
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], NvmeLogPageHealthInfo);
        BinaryPrimitives.WriteInt32LittleEndian(p[12..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(p[16..], ProtocolSpecificDataSize);
        BinaryPrimitives.WriteInt32LittleEndian(p[20..], NvmeHealthLogSize);

        fixed (byte* ptr = buffer)
        {
            if (!Native.DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, ptr, (uint)size, ptr, (uint)size, out var returned, IntPtr.Zero)) return null;
            if (returned < headerSize + ProtocolSpecificDataSize) return null;
        }
        // Output is STORAGE_PROTOCOL_DATA_DESCRIPTOR { Version, Size, STORAGE_PROTOCOL_SPECIFIC_DATA }.
        var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(span[(headerSize + 16)..]);
        var dataLength = BinaryPrimitives.ReadInt32LittleEndian(span[(headerSize + 20)..]);
        var start = headerSize + dataOffset;
        if (dataOffset <= 0 || dataLength < 192 || start + Math.Min(dataLength, NvmeHealthLogSize) > buffer.Length) return null;
        return NvmeHealthLog.Parse(span.Slice(start, Math.Min(dataLength, NvmeHealthLogSize)));
    }

    /// <summary>Physical disk numbers backing a volume such as "C:".</summary>
    public static List<int> VolumeDisks(string driveLetter)
    {
        var result = new List<int>();
        using var h = OpenQueryOnly($@"\\.\{driveLetter.TrimEnd('\\')}");
        if (h.IsInvalid) return result;
        var buffer = new byte[8 + 24 * 8];
        fixed (byte* ptr = buffer)
        {
            if (!Native.DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, ptr, (uint)buffer.Length, out _, IntPtr.Zero)) return result;
        }
        var n = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        for (var i = 0; i < n && i < 8; i++) result.Add(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8 + i * 24)));
        return result;
    }

    private static byte[]? Query(SafeFileHandle h, int propertyId, int outSize)
    {
        Span<byte> query = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(query, propertyId);
        BinaryPrimitives.WriteInt32LittleEndian(query[4..], 0);
        var output = new byte[outSize];
        fixed (byte* q = query)
        fixed (byte* o = output)
        {
            if (!Native.DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, q, 12, o, (uint)outSize, out var returned, IntPtr.Zero) || returned == 0) return null;
            return returned < outSize ? output[..(int)returned] : output;
        }
    }

    public static string BusTypeName(int bus) => bus switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "Unknown",
    };
}
