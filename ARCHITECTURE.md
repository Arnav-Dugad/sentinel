# Architecture

Sentinel is a .NET 10 / WinUI 3 (Windows App SDK) desktop app. Its layers are split into projects, so the core logic has no dependency on WinUI or on Windows-specific APIs and can be unit-tested.

## Projects

```
Sentinel.Domain              records and enums only: readings, snapshots, events, anomalies, sessions
   └─ Sentinel.Core          provider contracts, safety contract, settings, units, redaction, live metric store, logging
       ├─ Sentinel.Platform.Windows   all Win32/COM/WinRT/WMI/PDH access: providers, event-log mapping, trusted tools
       └─ Sentinel.Data              SQLite history store and migrations
           ├─ Sentinel.Telemetry     scheduler engine, history recorder, developer simulation providers
           └─ Sentinel.Analytics     statistics, baselines, anomaly detectors
               └─ Sentinel.Intelligence   health model, insights, correlations, change tracking, sessions, timeline, query parser, notifications
                   └─ Sentinel.Diagnostics   guided diagnostics, performance investigation, reports
                       └─ Sentinel.AI        optional local Ollama client and Ask Sentinel orchestration
Sentinel.App                 WinUI 3 shell: DI composition, pages, view models, controls, tray, windows
tests/Sentinel.Tests         xUnit v3
tools/Sentinel.Probe         console provider probe for the hardware test matrix
```

Only `Sentinel.Platform.Windows` and `Sentinel.App` call into Windows directly. Everything above `Core` sees providers only through interfaces (`ICpuTelemetryProvider`, `IWindowsEventProvider`, …), which is how the simulation providers replace real hardware.

## Data flow

```
 Providers ──SampleContext──▶ HistoryRecorder ──▶ LiveMetricStore (ring buffers, UI)
 (Platform.Windows                │
  or Simulation)                  ├─ 10 s / 1 min buckets ──flush every 60 s──▶ HistoryStore (SQLite)
                                  └─ SystemEvent queue ───────────────────────▶ event table (deduplicated)
                                                                                   │
 IntelligenceService (15 s tick)     ◀──────────────────────────────────────────────┘
   ├─ BaselineEngine / AnomalyEngine (detectors with sustain + hysteresis)
   ├─ ChangeTracker (inventory snapshot diffs → change records)
   ├─ SessionTracker (workload, charge/discharge, sleep sessions)
   ├─ HealthModel + InsightEngine → Home, Health
   └─ NotificationPolicy → tray balloons / app notifications
 UI pages ── query HistoryStore + LiveMetricStore ── TimelineService, CorrelationEngine, DiagnosticsEngine
```

## Providers

A provider implements `ITelemetryProvider`:

- `Descriptor`: id, name, category, **data sources**, access requirement, sampling cost, precision and the `SafetyAttestation` (see [SAFETY.md](SAFETY.md))
- `Capabilities`: what this machine actually exposes, each with availability, source, confidence and a reason when unavailable
- `GetInterval(SamplingMode)`: Foreground, Detail, Background or Paused
- `InitializeAsync`, `SampleAsync(SampleContext)`, `OnSystemResumed`

Readings are `Reading` values: a nullable value plus `Quality` (Good, Estimated, Stale, Unavailable, Unsupported), a source, a timestamp and a reason. A provider that cannot read something returns `Reading.Unavailable("why")` and never a default.

Windows providers derive from `WindowsProvider`, which handles capability bookkeeping, deferred metric definitions and per-mode intervals. Native interop is in `Interop/`, written by hand as P/Invoke declarations and raw COM vtable calls. Only the calls Sentinel needs are declared.

## Telemetry engine

`TelemetryEngine` runs **one** loop for all providers:

- Each provider has a `NextDue` time aligned to its interval boundary, so wake-ups coalesce. The loop sleeps until the earliest due time (at most 5 s) or until something calls `Reschedule`.
- **Sampling modes.** When the window is hidden, providers use their *Background* interval (slow or off). When a window is visible, they use *Foreground*. The provider whose category matches the open page uses *Detail*. A user-started Performance Investigation switches every provider to *Detail* for 30 s.
- **Isolation.** Each sample runs on the thread pool with a 15 s timeout and an `InFlight` guard. A failure backs off exponentially (5 s × 2ⁿ, at most 10 min) and marks the provider Degraded, then Failed. Other providers are unaffected.
- **Sleep and resume.** Power notifications call `NotifySuspending` and `NotifyResumed`. Gap markers are added to every live series. Providers rebuild native state and are sampled immediately after resume.

## History storage

`HistoryStore` is SQLite (WAL mode, a single writer connection, short-lived readers):

| Table | Resolution | Retention |
|---|---|---|
| `sample_10s` | 10 s avg/min/max/n | 48 h |
| `sample_1m` | 1 min | min(retention, 21 days) |
| `sample_1h` | 1 h avg/min/max/median/p95 | retention setting |
| `event`, `change`, `anomaly`, `workload_session`, `power_session` | per item | retention setting |
| `inventory_snapshot`, `baseline`, `capacity_history`, `disk_health_history`, `app_usage_1m`, `kv` | n/a | retention setting |

`ChooseTier` picks the finest tier that covers a query range with a reasonable point count. Charts decimate further to the pixel width. Each metric's `PersistPolicy` (LiveOnly, Detail, Full) controls how far it is stored. Per-core loads, for example, stay in the 10 s tier only.

## Analysis

- **Statistics**: median, MAD, robust z, interpolated percentiles, least-squares regression, a binary-segmentation change point, a Poisson upper tail and EWMA.
- **Baselines**: per metric and per context (All, LowLoad, Away, HighLoad, OnBattery), from minute history. A baseline is *mature* only after enough samples over several days. Detectors stay silent until then and say so.
- **Detectors**: BaselineDeviation (temperature, power, idle load), EventCluster (Poisson rate against history), MemoryGrowth (per-app regression), DeviceFlapping and DiskWriteVolume. An anomaly opens only after its condition holds for a sustain period, and closes with hysteresis.
- **Correlation**: events near a subject are graded *Confirmed* (a causal link recorded by Windows, e.g. a bugcheck before an unexpected restart), *Strong* (a repeated pattern after a change), *Possible* (coincidence in time) or *Insufficient*. Sentinel never presents a time coincidence as a cause.

## Intelligence and Ask Sentinel

`QueryParser` maps a question to an intent and a time range ("at 2 PM", "yesterday", "last 7 days"). `DiagnosticsEngine` runs the matching deterministic diagnostic. Each finding is labelled **Observed / Inferred / Possible / Unknown**, and the result has an overall confidence and suggested next steps (always informational, never automatic). If local AI is on, `AskSentinelService` sends the redacted evidence to Ollama, asks for a short explanation that keeps those labels, and sanitises the reply.

## UI

- **Shell**: `MainWindow` with a custom title bar, a `NavigationView` built from `PageRegistry`, a Ctrl+K command palette and a recording indicator. Closing hides to the tray. The frame navigates to a blank page and the working set is trimmed.
- **MVVM**: CommunityToolkit.Mvvm source generators. `PageViewModel` provides Activate/Deactivate/Refresh, and `UiClock` ticks only while a window is visible. `SentinelPage` tells the engine which category is on screen.
- **Design system**: `Styles/Tokens.xaml` (colour, spacing, type and radius tokens with Light, Dark and HighContrast dictionaries) and `Styles/Controls.xaml`.
- **Controls**: `SentinelChart` (drawn with XAML shapes, no third-party chart library; decimation, gaps, event markers, baseline bands, hover, zoom, legend), `Sparkline`, `MetricTile`, `InfoList` (sensitive-value masking), `EvidenceList`, `EmptyState`, `StatusPill`, `RangeSelector` and the `AdaptiveGrid` panel.
- **Secondary windows**: the tray quick panel and the always-on-top mini monitor.

## Composition

`App.xaml.cs` builds the DI container. The real or simulated provider set is chosen once at startup (`--simulate` or Settings → Developer). The background services are the telemetry engine, the history flush timer, the intelligence loop and the notification policy. Shutdown is ordered: stop the engine, flush the recorder, dispose providers, close the database.

## Updates

- `Sentinel.Core/Updates`: `AppVersion` (SemVer ordering), `UpdateFeed` (GitHub release JSON → `ReleaseInfo`, untrusted text sanitised), `UpdateSignature` (ECDSA P-256 verify and sign, built-in public key) and `UpdateClient` (HTTPS fetch with a host allow-list, size limits, verify-then-extract, staging under `%LOCALAPPDATA%\Sentinel\updates\<version>\app`).
- `Sentinel.App/Updates/UpdateService`: the schedule (2 min after start, then every 6 h), the state machine (Idle, Checking, UpToDate, Available, Downloading, Ready, Failed, Off) raised on the UI thread for Settings, the title-bar pill and the tray menu, and one notification per version.
- `Sentinel.App/Updates/UpdateInstaller`: runs from the staged build (`Sentinel.exe --install-update --target <dir> --pid <n>`) before any WinUI or single-instance code. It waits, swaps folders with rollback, records `last-install.json` and relaunches with `--updated`. `Program.Main` hands off to a staged update at startup when automatic install is on.
- `tools/Sentinel.ReleaseTool` (keygen, pack, sign, verify) and `tools/release.ps1` build, sign and publish a release.

## Performance budget

| State | Target | Measured (14-core laptop) |
|---|---|---|
| Tray only | < 0.2 % CPU | ~0.03 % CPU, ~100 MB working set |
| Home visible | < 1 % CPU | ~0.4 % CPU |
| Memory | ideally < 150–200 MB | ~100 MB in tray. A visible WinUI window is higher, mostly framework and composition memory |

Techniques used to meet the budget: coalesced timers, sampling only what the visible page needs, cached Wi-Fi and process metadata, long intervals for WMI-heavy sources, never waking an idle dGPU, non-concurrent workstation GC with adaptive heap sizing (`GarbageCollectionAdaptationMode=1`), and trimming the working set when parked in the tray.
