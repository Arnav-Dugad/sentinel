namespace Sentinel.Core.Knowledge;

/// <summary>
/// Curated, authoritative names and plain-language categories for common Windows stop codes
/// (from Microsoft's Bug Check Code Reference). Explanations describe categories, never verdicts.
/// </summary>
public static class BugcheckCatalog
{
    public sealed record Entry(string Name, string Category, string Explanation);

    private static readonly Dictionary<uint, Entry> Entries = new()
    {
        [0x0000000A] = new("IRQL_NOT_LESS_OR_EQUAL", "Driver or memory", "A kernel-mode driver or the kernel accessed memory at an invalid interrupt level. Most often caused by a faulty driver; less often by faulty memory."),
        [0x0000001A] = new("MEMORY_MANAGEMENT", "Memory", "Windows detected a severe memory management error. Can be caused by faulty RAM, a driver, or disk/page file corruption."),
        [0x0000001E] = new("KMODE_EXCEPTION_NOT_HANDLED", "Driver", "A kernel-mode program generated an exception the error handler did not catch. Usually a driver issue."),
        [0x00000024] = new("NTFS_FILE_SYSTEM", "Storage", "A problem occurred in the NTFS file system driver. Can indicate disk corruption or a failing drive."),
        [0x0000003B] = new("SYSTEM_SERVICE_EXCEPTION", "Driver", "An exception happened while executing a system routine, often in a graphics or third-party driver."),
        [0x0000004E] = new("PFN_LIST_CORRUPT", "Memory", "The memory manager's page list is corrupt. Commonly caused by a driver passing a bad memory descriptor, or by faulty RAM."),
        [0x00000050] = new("PAGE_FAULT_IN_NONPAGED_AREA", "Driver or memory", "Invalid system memory was referenced. Causes include faulty drivers, faulty RAM and antivirus filter drivers."),
        [0x0000007A] = new("KERNEL_DATA_INPAGE_ERROR", "Storage", "Kernel data could not be read from the page file. Often indicates a storage, cable or controller problem."),
        [0x0000007E] = new("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "Driver", "A system thread generated an exception that was not handled. Usually a driver issue."),
        [0x0000007F] = new("UNEXPECTED_KERNEL_MODE_TRAP", "Hardware or driver", "The CPU generated a trap the kernel did not catch. Can be caused by hardware faults, overheating, or kernel stack overflow."),
        [0x0000009F] = new("DRIVER_POWER_STATE_FAILURE", "Driver (power)", "A driver did not complete a power transition (for example during sleep or resume) in time."),
        [0x000000A0] = new("INTERNAL_POWER_ERROR", "Power management", "The power policy manager experienced a fatal error."),
        [0x000000BE] = new("ATTEMPTED_WRITE_TO_READONLY_MEMORY", "Driver", "A driver attempted to write to read-only memory."),
        [0x000000C2] = new("BAD_POOL_CALLER", "Driver", "A kernel-mode thread made a bad pool request. Usually a driver issue."),
        [0x000000C5] = new("DRIVER_CORRUPTED_EXPOOL", "Driver", "The system tried to access invalid pool memory, typically because a driver corrupted the pool."),
        [0x000000D1] = new("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Driver", "A driver accessed pageable or invalid memory at too high an interrupt level. Almost always a driver bug."),
        [0x000000EF] = new("CRITICAL_PROCESS_DIED", "System process", "A critical system process terminated unexpectedly. Causes range from driver and storage issues to corrupted system files."),
        [0x000000F4] = new("CRITICAL_OBJECT_TERMINATION", "Storage or system process", "A process or thread crucial to system operation exited unexpectedly, often due to storage I/O failures."),
        [0x00000101] = new("CLOCK_WATCHDOG_TIMEOUT", "CPU or firmware", "A processor did not respond to a clock interrupt in time. Can be firmware, driver or CPU-stability related."),
        [0x00000109] = new("CRITICAL_STRUCTURE_CORRUPTION", "Driver or memory", "Kernel code or critical data was modified, either by a driver or by faulty memory."),
        [0x00000116] = new("VIDEO_TDR_FAILURE", "Graphics", "The graphics driver did not recover from a timeout (TDR). Often driver, overheating or GPU-stability related."),
        [0x00000117] = new("VIDEO_TDR_TIMEOUT_DETECTED", "Graphics", "The display driver failed to respond in a timely fashion."),
        [0x00000119] = new("VIDEO_SCHEDULER_INTERNAL_ERROR", "Graphics", "The video scheduler detected a fatal violation, usually in a graphics driver."),
        [0x00000124] = new("WHEA_UNCORRECTABLE_ERROR", "Hardware", "A fatal hardware error occurred (reported via WHEA). Can involve CPU, memory, buses, or instability from overclocking or overheating."),
        [0x00000133] = new("DPC_WATCHDOG_VIOLATION", "Driver or storage", "A DPC ran too long. Commonly a storage, network or graphics driver problem, or outdated storage firmware."),
        [0x00000139] = new("KERNEL_SECURITY_CHECK_FAILURE", "Driver or memory", "The kernel detected corruption of a critical data structure."),
        [0x0000013A] = new("KERNEL_MODE_HEAP_CORRUPTION", "Driver", "The kernel-mode heap manager detected corruption."),
        [0x00000154] = new("UNEXPECTED_STORE_EXCEPTION", "Storage or memory", "The kernel memory store component caught an unexpected exception; often storage-related."),
        [0x0000015F] = new("CONNECTED_STANDBY_WATCHDOG_TIMEOUT_LIVEDUMP", "Power", "A component took too long during a Modern Standby transition."),
        [0x000001CA] = new("SYNTHETIC_WATCHDOG_TIMEOUT", "Hypervisor/system", "A system-wide watchdog expired."),
        [0x000001D5] = new("DRIVER_PNP_WATCHDOG", "Driver", "A driver took too long during a Plug and Play operation."),
    };

    public static Entry? Lookup(string code) =>
        uint.TryParse(code.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? code[2..] : code, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && Entries.TryGetValue(v, out var e) ? e : null;

    public static string? Name(string code) => Lookup(code)?.Name;
}
