# Sentinel

Sentinel is a native Windows 11 app that watches your PC's health, performance and stability. It records history and explains what changed in plain language.

It works offline, costs nothing, sends no telemetry and needs no account. The only automatic connection is a periodic check for new versions on GitHub, which you can turn off. It is **strictly read-only**: it never overclocks, never controls fans, never writes to firmware or hardware registers, never installs a kernel driver and never changes system settings on your behalf. See [SAFETY.md](SAFETY.md).

> Every value Sentinel shows comes from a documented Windows interface or a vendor's read-only API, and is labelled with that source. If a machine does not expose something, Sentinel says **"Not exposed by this system"**. It never estimates or invents a reading.

## What it does

| Area | Highlights |
|---|---|
| **Home** | Live tiles with sparklines, the most important current insight, and recent notable events |
| **Processor** | Per-core and hybrid P/E-core load, effective clock, performance-limit reasons, package power (when Windows exposes an energy meter) |
| **Graphics** | Per-adapter engine load (3D, copy, video encode/decode, compute) and dedicated/shared memory. On NVIDIA GPUs with drivers installed, temperature, power and clocks via NVML (read-only) |
| **Memory** | Use, commit, cache, paging, module inventory (from SMBIOS), growth detection for individual apps |
| **Storage** | Throughput, latency and queue depth per disk. NVMe health log: wear, spare, media errors, unsafe shutdowns and temperature, read without admin rights |
| **Battery Lab** | Charge rate, health versus design capacity, capacity history (merged with the Windows battery report), charge and discharge sessions |
| **Thermals** | ACPI thermal zones and NVML GPU temperature, each labelled with what it actually measures |
| **Network** | Per-adapter throughput, Wi-Fi signal, band and channel, a disconnect history that ignores sleep, and an optional connectivity test you run yourself |
| **Devices** | Displays, audio endpoints, USB/Bluetooth device timeline, driver inventory with Device Manager problem codes |
| **Reliability** | Stop errors (named, e.g. `DRIVER_POWER_STATE_FAILURE`), unexpected shutdowns, app crashes and hangs, display-driver resets, WHEA errors |
| **Software** | Processes, startup apps (with Windows' own startup impact), Windows Update history, installed apps |
| **Intelligence** | Health overview with transparent factors (no arbitrary single score), learned baselines, anomaly detection, a "What changed?" timeline, Time Machine, period comparison |
| **Ask Sentinel** | Plain-language questions ("Why did my PC restart today?"). Answers are deterministic and evidence-based. An optional *local* Ollama model can rephrase them; see below |
| **Reports** | HTML, JSON and CSV reports and a support bundle, privacy-safe by default |
| **Updates** | Signed, verified automatic updates from GitHub Releases, with status in Settings, the title bar and the tray |

Pages appear only when they have something real to show. If a capability is missing on a machine, the page explains why rather than leaving an empty chart.

### Ask Sentinel and local AI

Answers are built from recorded evidence by a deterministic engine. Each finding is labelled **Observed**, **Inferred**, **Possible** or **Unknown** and carries a confidence level. If you have [Ollama](https://ollama.com) running locally, you can let a local model add a short explanation. Sentinel only connects to loopback addresses (`localhost`, `127.0.0.1`, `::1`). Evidence is redacted before it reaches the model, and the model cannot call tools or run anything. Code blocks and shell commands are stripped from its replies.

## Install

1. Download `Sentinel-<version>-win-x64.zip` from the [latest release](https://github.com/Arnav-Dugad/sentinel/releases/latest). Use `win-arm64` on ARM PCs such as Snapdragon laptops.
2. Extract it to a folder you own, for example `%LOCALAPPDATA%\Programs\Sentinel`.
3. Run `Sentinel.exe`. Windows SmartScreen may warn you the first time, because the app is not code-signed with a paid certificate.

Sentinel needs no installer and no administrator rights. Your history and settings live in `%LOCALAPPDATA%\Sentinel`, separate from the app folder.

### Updates

Sentinel checks [GitHub Releases](https://github.com/Arnav-Dugad/sentinel/releases) shortly after it starts and then every 6 hours. A new version downloads in the background. Sentinel then verifies its **ECDSA P-256 signature** against the release key built into the app. Only after that check passes is the update installed, the next time Sentinel starts, or straight away from **Restart and update**. A package that fails verification is deleted and never run. A failed installation restores the previous version automatically.

Update status, release notes and both switches (automatic checks, automatic install) are in **Settings → Updates**. When an update is ready, a pill also appears in the title bar and an entry appears in the tray menu.

## Build from source

Requirements: Windows 11 on x64 or ARM64 and the .NET 10 SDK. The minimum supported build is Windows 10 version 2004 (19041), but Sentinel is developed and tested on Windows 11.

```powershell
dotnet build src\Sentinel.App -c Release
.\src\Sentinel.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\Sentinel.exe
```

See [BUILDING.md](BUILDING.md) for details, publishing and the hardware probe tool.

Command-line switches:

| Switch | Effect |
|---|---|
| `--background` | Start in the notification area with no window (used by *Start with Windows*) |
| `--simulate` | Run on synthetic **Developer Simulation** telemetry, stored in a separate database |

## Resource use

Sentinel aims to be invisible when you are not looking at it. In the notification area it samples slowly on a single coalesced timer. Measured on a 14-core laptop, that costs about **0.03 % CPU** with a working set of about **100 MB**. With a page open, it samples faster, but only for the page you are viewing. Expensive sources such as driver inventory, update history and Wi-Fi scans refresh on long intervals or on demand. A sleeping discrete GPU is never woken just to read it.

## Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md): projects, data flow, the telemetry engine, storage and analysis
- [SAFETY.md](SAFETY.md): the read-only contract and how it is enforced
- [PRIVACY.md](PRIVACY.md): what is stored, where, for how long, and how to delete it
- [TELEMETRY_SOURCES.md](TELEMETRY_SOURCES.md): every metric and the Windows interface it comes from
- [BUILDING.md](BUILDING.md): building, testing and publishing
- [CONTRIBUTING.md](CONTRIBUTING.md): rules for new providers and features

## Known limitations

These are deliberate. Each would need elevation, a kernel driver or an undocumented interface:

- **CPU package temperature** is not shown on most systems, because Windows has no documented user-mode API for it. ACPI thermal zones are shown when the firmware provides them, labelled with what they actually measure.
- **AMD and Intel GPU temperature and power** need vendor SDKs that are not integrated yet. Load and memory are shown for every GPU.
- **Per-process network usage** and **deep ETW tracing** need administrator rights and are not implemented.
- **Variable refresh rate** state and **memory compression** size are not exposed through documented read APIs.
- Boot and shutdown durations from the *Diagnostics-Performance* log can only be read by administrators. Sentinel says so rather than asking to run elevated.

## License

Free for personal and commercial use. [MIT](LICENSE) © 2026 Arnav Dugad.
