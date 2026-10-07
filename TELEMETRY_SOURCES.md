# Telemetry sources

Every value Sentinel shows comes from one of the sources below. The **Admin** column says whether that value needs administrator rights. Sentinel never runs elevated, so a value marked "Yes" is shown as unavailable with that reason. The **Fallback** column says what Sentinel uses when the primary source is missing. "None" means the value is reported as *Not exposed by this system*.

Performance counters are opened with `PdhAddEnglishCounterW`, so they work on every display language.

## Processor

| Metric | Primary source | Fallback | Admin |
|---|---|---|---|
| Total and per-core load | `\Processor Information(*)\% Processor Utility` | `\Processor(*)\% Processor Time` | No |
| Effective clock | Base clock (`\Processor Information(_Total)\Processor Frequency`) × `% Processor Performance` | None | No |
| Performance limited (%) | 100 − `\Processor Information(_Total)\% Performance Limit` | None | No |
| Queue length, context switches, interrupts, DPC time, processes, threads | `\System\…`, `\Processor Information(_Total)\…` counters | None | No |
| Hybrid P/E-core layout | `GetLogicalProcessorInformationEx` (efficiency class) | Treated as uniform cores | No |
| Name, base/max clock, virtualisation | WMI `Win32_Processor` (SELECT only) | None | No |
| Package power | `\Energy Meter(*)\Power` (RAPL-backed, where Windows exposes it) | None | No |
| Package temperature | **Not available**: no documented user-mode API | ACPI thermal zone labelled as CPU, if the firmware provides one | n/a |

## Memory

| Metric | Primary source | Fallback | Admin |
|---|---|---|---|
| Used, available, commit | `GlobalMemoryStatusEx`, `GetPerformanceInfo` | None | No |
| Standby cache | `\Memory\Standby Cache * Bytes` | None | No |
| Hard faults, page reads, page-file use | `\Memory\Page Faults/sec`, `\Memory\Page Reads/sec`, `\Paging File(_Total)\% Usage` | None | No |
| Modules (size, speed, slot, part number) | WMI `Win32_PhysicalMemory` (SMBIOS) | Total only | No |
| Compressed memory size | **Not available**: no documented API | None | n/a |

## Graphics

| Metric | Primary source | Fallback | Admin |
|---|---|---|---|
| Adapters, dedicated/shared memory size, LUID | DXGI `IDXGIFactory1::EnumAdapters1` / `GetDesc1` | WMI `Win32_VideoController` | No |
| Per-engine load (3D, copy, video, compute) | `\GPU Engine(*)\Utilization Percentage` | None | No |
| Per-process GPU load | `\GPU Engine(pid_*)\…` instances | None | No |
| Memory in use | `\GPU Adapter Memory(*)\Dedicated Usage`, `Shared Usage` | None | No |
| Driver version and date | WMI `Win32_VideoController` | Driver inventory | No |
| Temperature, power, clocks, fan %, throttle reasons | NVIDIA NVML (`nvml.dll` from System32, getters only) | None (AMD/Intel: not integrated) | No |

On systems with a battery, a discrete NVIDIA GPU that is asleep is **not** polled through NVML, because polling would wake it. Its load still comes from the counters.

## Storage

| Metric | Primary source | Fallback | Admin |
|---|---|---|---|
| Read/write throughput, IOPS, latency, active time, queue | `\PhysicalDisk(*)\…` counters | None | No |
| Disk model, bus, media type, size | `IOCTL_STORAGE_QUERY_PROPERTY` (device descriptor), Storage Management API | WMI | No |
| NVMe health: wear, spare, media errors, unsafe shutdowns, power-on hours, data written | `IOCTL_STORAGE_QUERY_PROPERTY` → NVMe SMART/health log (0x02), zero-access handle | None | No |
| Drive temperature | Storage temperature property (`StorageDeviceTemperatureProperty`) | NVMe composite temperature | No |
| SATA SMART attributes | **Not available** without admin | None | Yes |
| Volumes and free space | `System.IO.DriveInfo` | None | No |

## Battery and power

| Metric | Primary source | Fallback | Admin |
|---|---|---|---|
| Charge %, rate (mW), voltage, remaining capacity | Battery device IOCTLs (`IOCTL_BATTERY_QUERY_STATUS`) via SetupAPI | `GetSystemPowerStatus` (% only) | No |
| Time remaining | Windows' own estimate (`GetSystemPowerStatus.BatteryLifeTime`), labelled as an estimate | None | No |
| Design / full-charge capacity, cycle count, chemistry | `IOCTL_BATTERY_QUERY_INFORMATION` | None | No |
| Capacity history (before Sentinel was installed) | `powercfg /batteryreport /xml` (on demand, allow-listed) | Sentinel's own history | No |
| AC online | `GetSystemPowerStatus` | None | No |
| Power mode and plan | `PowerGetEffectiveOverlaySchemeId` (resolved dynamically), `PowerGetActiveScheme` | Plan only | No |
| Sleep, resume, display on/off | `PowerRegisterSuspendResumeNotification`, `PowerSettingRegisterNotification` | Event log | No |
| User idle time | `GetLastInputInfo` | None | No |

## Thermals

| Metric | Primary source | Fallback | Admin |
|---|---|---|---|
| ACPI thermal zones | `\Thermal Zone Information(*)\High Precision Temperature` | `…\Temperature` | No |
| Passive cooling limit | `\Thermal Zone Information(*)\% Passive Limit` | None | No |
| GPU temperature | NVML | None | No |
| Drive temperature | See Storage | None | No |
| Fan speed, CPU core temperatures, voltages | **Not available**: needs a kernel driver (see [SAFETY.md](SAFETY.md)) | None | n/a |

A thermal zone whose value does not change for 10 minutes is flagged as possibly frozen or synthetic.

## Network

| Metric | Primary source | Fallback | Admin |
|---|---|---|---|
| Throughput per adapter | IP Helper (`System.Net.NetworkInformation` statistics); QoS/WFP filter pseudo-interfaces and virtual adapters excluded from totals | None | No |
| Adapter type, link speed, addresses, gateway, DNS | IP Helper | None | No |
| Wi-Fi SSID, signal, RSSI, band, channel, PHY | Native Wi-Fi API (`WlanQueryInterface`, `WlanGetNetworkBssList`) | None | No |
| Disconnects | Event log: WLAN-AutoConfig 8003 (sleep-related disconnects excluded) | None | No |
| Latency and DNS test | `Ping`, `Dns` (user-started only) | None | No |
| Per-process network usage | **Not available** without ETW kernel tracing | None | Yes |

## Devices, displays and audio

| Metric | Primary source | Fallback | Admin |
|---|---|---|---|
| Displays: resolution, refresh rate, HDR, connection type, scaling | `QueryDisplayConfig`, `DisplayConfigGetDeviceInfo`, `GetDpiForMonitor` | DXGI outputs | No |
| Variable refresh rate | **Not available**: no documented read API | None | n/a |
| Audio endpoints, default device, format, peak level | Core Audio (`IMMDeviceEnumerator`, `IAudioMeterInformation`). Read-only | None | No |
| USB/Bluetooth connect/disconnect timeline | WinRT `DeviceWatcher` (DeviceContainer) | Kernel-PnP event log | No |
| Driver inventory, signer, date | WMI `Win32_PnPSignedDriver` (every 6 hours) | None | No |
| Device problem codes | `Windows.Devices.Enumeration` (`DeviceHasProblem`, problem-code property), explained from the Device Manager code list | None | No |

## Reliability and software

| Data | Primary source | Fallback | Admin |
|---|---|---|---|
| Stop errors (bugchecks) | System log: WER-SystemErrorReporting 1001, Kernel-Power 41 `BugcheckCode` | None | No |
| Unexpected shutdowns | Kernel-Power 41, EventLog 6008 (deduplicated) | None | No |
| App crashes and hangs | Application log: Application Error 1000, Application Hang 1002 | None | No |
| Display driver resets (TDR) | System log: Display 4101 | None | No |
| Hardware errors (WHEA) | System log: WHEA-Logger 1, 17, 18, 19, 47 | None | No |
| Disk and file-system errors | System log: disk 7, 11, 51, 153; Ntfs 55 | None | No |
| Service failures | System log: Service Control Manager 7000, 7009, 7023, 7031, 7034 | None | No |
| Boots, shutdowns, sleep and wake | Kernel-General 12/13, User32 1074, Kernel-Power 42/105/506/507, Power-Troubleshooter 1 | Live power notifications | No |
| Software installs | MsiInstaller 11707/11724, WindowsUpdateClient 19/20 (incl. Microsoft Store apps) | Uninstall-key diff | No |
| Driver installs/updates | Kernel-PnP/Configuration 400 (`DeviceUpdated`) | Driver inventory diff | No |
| Boot durations | Diagnostics-Performance/Operational 100 | None | **Yes** on most systems (shown as unavailable) |
| Windows Update history | Windows Update Agent COM API (`IUpdateSearcher.QueryHistory`) | WindowsUpdateClient event log | No |
| Processes | `NtQuerySystemInformation(SystemProcessInformation)`, `QueryFullProcessImageName`, version resources | None | No (protected processes show limited detail) |
| Startup apps and impact | Run keys (HKCU, HKLM, WOW6432Node), Startup folders, `StartupApproved` state; impact from Diagnostics-Performance 101 when readable | None | No (impact: often Yes) |
| Installed software | Uninstall registry keys (HKLM, HKLM WOW6432Node, HKCU) | None | No |
| Security status | Device Guard WMI, Microsoft Defender WMI (status only), Secure Boot registry state | None | No |
| System model, firmware, board | WMI `Win32_ComputerSystem`, `Win32_BIOS`, `Win32_BaseBoard` (SMBIOS) | Registry | No |

## Derived values

These are calculated by Sentinel, never read from hardware, and are labelled as calculated in the UI:

| Value | Calculation |
|---|---|
| Battery health % | Full-charge capacity ÷ design capacity |
| Data written per day | NVMe data-units-written history |
| Baselines | Robust median / MAD per context (low load, away, high load, on battery) over learned history |
| Anomalies | Robust z-score with sustain and hysteresis, change-point and Poisson event-rate tests |
| Health categories | Transparent factor lists. There is no hidden weighted score |
