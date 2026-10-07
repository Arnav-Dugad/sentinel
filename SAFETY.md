# Safety

Sentinel observes. It does not act. This document is the contract every part of the codebase follows. Changes that weaken it will not be accepted.

## The contract

Sentinel **never**:

1. Writes to hardware: no MSR, EC, SMBus, PCI configuration space, GPU registers, fan or voltage control, overclocking or power limits.
2. Interacts with firmware: no UEFI variable writes, BIOS/EC/SSD firmware updates or SMM calls.
3. Installs or loads a kernel driver, including WinRing0, InpOut or any driver bundled by a third-party monitoring library.
4. Degrades security: it does not turn off Defender, SmartScreen, VBS/HVCI, Secure Boot, UAC or firewall rules, and it does not ask you to.
5. Changes system configuration on its own: no registry writes outside its own settings, no service or startup changes, no power-plan switching and no driver changes. The one exception is the *Start with Windows* option, which you turn on yourself. It adds a single per-user `Run` entry for Sentinel.
6. Runs arbitrary or privileged commands. External tools are limited to a fixed allow-list (below) and only run when you ask.
7. Fabricates data. A missing sensor is reported as missing, never estimated and shown as measured.

## How it is enforced

### Structural: the safety attestation

Every provider declares a `ProviderDescriptor` containing a `SafetyAttestation`:

```csharp
public sealed record SafetyAttestation(
    bool ReadOnly, bool ModifiesHardwareState, bool DegradesSecurity,
    bool InstallsKernelDriver, bool InteractsWithFirmware, bool RunsArbitraryPrivilegedCommands);
```

`SafetyContract.Validate` runs on every provider when the telemetry engine is built. A provider that is not `ReadOnlyObserver`, or that does not name its data sources, throws `SafetyContractViolationException` and is never scheduled. The tests in `SafetyContractTests` cover each clause.

### Least privilege

- The app manifest requests `asInvoker`. Sentinel runs as a standard user and never prompts for elevation.
- Device handles are opened with **zero access rights** (`dwDesiredAccess = 0`). This is enough for `IOCTL_STORAGE_QUERY_PROPERTY` (NVMe health, temperature) and battery queries, and it cannot be used to read or write data.
- WMI is used only for `SELECT` queries. No methods are invoked.
- The Service Control Manager is opened with query rights only.
- The registry is opened read-only, apart from Sentinel's own `Run` value described above.
- NVML is loaded from its full system path, and only getter functions are bound. No `nvmlDeviceSet*` function is ever resolved.
- COM interfaces (Core Audio, WLAN, Windows Update Agent) are used for queries only. Audio reads peak meters only and never sets volume.

### External tools

`TrustedToolRunner` runs only allow-listed executables from `%SystemRoot%\System32`, with fixed arguments, no shell, a timeout, and output confined to Sentinel's own temp folder (path traversal is rejected). The current allow-list:

| Tool | Purpose | When |
|---|---|---|
| `powercfg.exe /batteryreport /xml` | Battery capacity history | When you open Battery Lab history or request a report |

### Untrusted input

Event log text, device names, process names, file version strings and model output all come from outside Sentinel. They are:

- Stripped of control and bidirectional-override characters and length-limited (`Redactor.SanitizeUntrusted`).
- HTML-encoded in reports, and guarded against formula injection in CSV (cells starting with `= + - @` are prefixed).
- Parsed as XML with DTD processing **prohibited** (the battery report).

Ollama responses are treated as untrusted text. Code blocks and command-like lines are removed before display, and the model is never given tools.

## Self-update

Updating replaces Sentinel's **own** program folder and nothing else. It never needs elevation, never touches the system, and is built so that a compromised download cannot run:

1. Release packages are signed with an **ECDSA P-256** key whose private half never leaves the maintainer's machine. The public key is compiled into the app; its fingerprint is shown in Settings → Updates.
2. Packages are fetched over HTTPS from GitHub hosts only (`api.github.com`, `github.com`, `*.githubusercontent.com`), with a size limit.
3. The signature is verified **before** the archive is opened. A package that fails is deleted, and the failure is reported.
4. Extraction rejects paths outside the staging folder (zip-slip), and the package must contain `Sentinel.exe`.
5. Installation runs the *new, verified* build with `--install-update`. It waits for the old instance to exit, moves the old folder aside, copies the new files in and relaunches. If anything fails, the previous folder is restored.
6. Development builds (run from `bin\`) and read-only install locations never replace themselves.

Release notes from GitHub are untrusted text. They are sanitised and shown as plain text only.

## Third-party monitoring libraries

**LibreHardwareMonitor** was evaluated. It reads CPU temperatures, voltages and fan speeds through a kernel driver (historically WinRing0, which has a published vulnerability, CVE-2020-14979, and is flagged by Microsoft Defender). Loading that driver breaks clauses 1 and 3, so **LibreHardwareMonitor is not bundled**. The provider architecture could host a future adapter that uses only its driver-free sensors. Such an adapter would have to pass the same attestation and never trigger driver installation. Until that exists, Sentinel shows "Not exposed by this system" for CPU package temperature.

## Behaviour on failure

- Every provider runs isolated. A slow or failing provider times out after 15 seconds and backs off exponentially (up to 10 minutes). It cannot stall or crash the others.
- After sleep or hibernation, providers rebuild their native handles, and charts show a gap rather than interpolating across it.
- If a value cannot be read, the UI says so and why ("Not exposed by this system", "Requires administrator", "Driver not installed").

## Reporting a safety problem

If you find any code path that could write to hardware, change system state or run unexpected commands, treat it as a critical bug. Please report it privately to the maintainers rather than in a public issue.
