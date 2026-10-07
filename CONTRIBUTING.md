# Contributing

Thanks for helping. Sentinel's value depends on being **trustworthy**: every number is real, every claim is backed by evidence, and the app never changes your system. Contributions are judged against that first.

## Ground rules

1. **Read-only, always.** Read [SAFETY.md](SAFETY.md) before you write a provider. Pull requests that write to hardware, firmware, the registry (outside Sentinel's own settings), services or power settings, or that load a kernel driver, will be closed.
2. **No fabricated data.** If a value is not exposed, return `Reading.Unavailable("reason")` and mark the capability unavailable. Do not substitute a typical value or guess from model names. Derived values must be labelled as calculated or estimated.
3. **Least privilege.** Sentinel runs as a standard user. A feature that only works elevated must degrade cleanly and explain why ("Requires administrator"). Do not add elevation prompts.
4. **Documented interfaces first.** Prefer documented Win32, WinRT, WMI and PDH APIs. Undocumented structures (such as `SYSTEM_PROCESS_INFORMATION` offsets) are acceptable only when stable across supported Windows versions, and must be commented with what they rely on.
5. **No telemetry, accounts or network calls.** The only network features are user-started (connectivity test) or loopback-only (Ollama).
6. **Privacy by default.** Mark identifiers as sensitive in `InfoList`, never log them, and pass any free text from Windows through `Redactor.SanitizeUntrusted` (display) or `Redactor.Redact` (reports and AI).

## Adding a provider

1. Define or extend the typed interface in `Sentinel.Core/Providers/TypedProviders.cs` and any snapshot records in `Sentinel.Domain`.
2. Implement it in `Sentinel.Platform.Windows/Providers`, deriving from `WindowsProvider`:
   - Use `Describe(...)` to declare every data source and a realistic precision. The attestation defaults to `ReadOnlyObserver`.
   - Report each capability with `SetCapability(name, available, source, reasonIfNot)`.
   - Choose intervals per mode. Background should be slow or `Never`, and Detail is only for the page that needs it.
   - Keep native handles in fields, rebuild them in `OnSystemResumed`, and release them in `Dispose`.
   - Open device handles with zero access rights unless a query requires more, and if it does, reconsider.
3. Add a matching simulation provider in `Sentinel.Telemetry/Simulation`, so the UI and tests work without the hardware.
4. Register both in `App.xaml.cs` and add the provider to `tools/Sentinel.Probe`.
5. Add the metric rows to [TELEMETRY_SOURCES.md](TELEMETRY_SOURCES.md).
6. Add unit tests for any parsing logic (byte layouts, event payloads, XML). Keep parsing in pure static methods so it is testable.

## Adding analysis or insights

- Detectors live in `Sentinel.Analytics/Detectors.cs`. They must use a mature baseline or a statistical test, a sustain period and hysteresis. Explain the evidence in `EvidenceItem`s with the correct `EvidenceKind`.
- Correlations must not upgrade a time coincidence to a cause. Use `CorrelationStrength.Possible` unless Windows records the link or the pattern repeats.
- Notifications go through `NotificationPolicy` (at most one per topic per 24 h and three per day). Only notify about things a user can act on.

## UI guidelines

- Use the tokens in `Styles/Tokens.xaml`. Do not hard-code colours, spacing or font sizes. Check Light, Dark and High Contrast.
- Every page needs a real empty state (`EmptyState`) that says why there is no data and what will make it appear. Do not add placeholder pages.
- Keep work off the UI thread. Page view models refresh on `UiClock` only while visible, and must stop in `Deactivate`.
- Accessibility: set `AutomationProperties.Name` on icon-only buttons and charts. All features must work by keyboard, and motion must respect *Reduce motion*.
- Use `x:Bind` on public members. Avoid nested function bindings and `DataTemplate`s whose root is a `TextBlock` containing only bound `Run`s, which the XAML compiler cannot handle.

## Code style

- C# 14 / .NET 10, nullable enabled, `latest-recommended` analyzers (see `Directory.Build.props`). Keep the build at zero warnings. File-scoped namespaces and primary constructors where they help.
- Match the surrounding code. Comments explain *why*, not *what*.
- Package versions are managed centrally in `Directory.Packages.props`. Adding a dependency needs a reason, and it must not phone home.
- Edit source files as UTF-8. Some files contain Segoe Fluent Icons private-use glyphs, and tools that re-encode files (for example Windows PowerShell 5 `Set-Content`) corrupt them.

## Before opening a pull request

```powershell
dotnet build Sentinel.slnx
dotnet test --project tests\Sentinel.Tests
dotnet run --project tools\Sentinel.Probe     # if you touched a provider
```

Describe what you tested on real hardware (CPU and GPU vendor, desktop or laptop), and include probe output for provider changes.
