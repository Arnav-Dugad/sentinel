using System.Runtime.InteropServices;

namespace Sentinel.Platform.Windows.Interop;

/// <summary>
/// Read-only Win32 declarations. Nothing in this file writes system state; every handle opened here
/// requests the minimum access needed for a query (often zero access).
/// </summary>
internal static partial class Native
{
    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;
    public const uint ERROR_ACCESS_DENIED = 5;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // ---------------------------------------------------------------- kernel32

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    [DllImport("kernel32.dll")]
    public static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    public static extern uint GetActiveProcessorCount(ushort groupNumber);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFirmwareType(out int firmwareType);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize,
        void* outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    // ---------------------------------------------------------------- advapi32 (token query only)

    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenIntegrityLevel = 25;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, uint length, out uint returnLength);

    [DllImport("advapi32.dll")]
    public static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    public static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    // ---------------------------------------------------------------- ntdll (documented in winternl.h)

    public const int SystemProcessInformation = 5;
    public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    [DllImport("ntdll.dll")]
    public static extern uint NtQuerySystemInformation(int infoClass, IntPtr info, uint length, out uint returnLength);

    // ---------------------------------------------------------------- user32

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("kernel32.dll")]
    public static extern uint GetTickCount();

    // ---------------------------------------------------------------- powrprof (read-only queries + notifications)

    public const uint DEVICE_NOTIFY_CALLBACK = 2;
    public const uint PBT_APMSUSPEND = 0x4;
    public const uint PBT_APMRESUMESUSPEND = 0x7;
    public const uint PBT_APMRESUMEAUTOMATIC = 0x12;
    public const uint PBT_POWERSETTINGCHANGE = 0x8013;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint DeviceNotifyCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        public DeviceNotifyCallback Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    public static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll", SetLastError = true)]
    public static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);

    [DllImport("powrprof.dll", SetLastError = true)]
    public static extern uint PowerSettingRegisterNotification(ref Guid settingGuid, uint flags, ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll", SetLastError = true)]
    public static extern uint PowerSettingUnregisterNotification(IntPtr registrationHandle);

    [DllImport("powrprof.dll")]
    public static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    public static extern unsafe uint PowerReadFriendlyName(IntPtr rootPowerKey, Guid* schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid, byte* buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr mem);

    [StructLayout(LayoutKind.Sequential)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data;
    }

    public static readonly Guid GUID_ACDC_POWER_SOURCE = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");
    public static readonly Guid GUID_POWER_SAVING_STATUS = new("e00958c0-c213-4ace-ac77-fecced2eeea5");
    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    // ---------------------------------------------------------------- shcore / monitors

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize;
        public int rcMonitorLeft, rcMonitorTop, rcMonitorRight, rcMonitorBottom;
        public int rcWorkLeft, rcWorkTop, rcWorkRight, rcWorkBottom;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEX info);

    // ---------------------------------------------------------------- setupapi (device interface enumeration)

    public const uint DIGCF_PRESENT = 0x2;
    public const uint DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr detailData,
        uint detailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    /// <summary>Enumerates device interface paths for a device interface class (e.g. batteries).</summary>
    public static List<string> EnumerateInterfacePaths(Guid interfaceClass)
    {
        var result = new List<string>();
        var set = SetupDiGetClassDevsW(ref interfaceClass, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE || set == IntPtr.Zero) return result;
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceClass, i, ref data)) break;
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0 || required > 4096) continue;
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is 8 on 64-bit, 6 on 32-bit.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, required, out _, IntPtr.Zero))
                    {
                        var path = Marshal.PtrToStringUni(buffer + 4);
                        if (!string.IsNullOrEmpty(path)) result.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }
}
