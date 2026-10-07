# Building Sentinel

## Requirements

- Windows 11, x64 or ARM64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.100 or later)
- Optional: Visual Studio 2026 or VS Code with C# Dev Kit. A plain `dotnet` CLI is enough.

The Windows App SDK and Windows SDK build tools come from NuGet. No Visual Studio workload or MSIX tooling is needed. The app is **unpackaged** and **self-contained** (the Windows App SDK runtime is copied next to `Sentinel.exe`).

### Using a per-user .NET install

If the SDK is installed per-user (for example with `dotnet-install.ps1` into `%LOCALAPPDATA%\Microsoft\dotnet`) and is not on `PATH`, set it for the session:

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
```

## Build and run

```powershell
dotnet build src\Sentinel.App                 # Debug, win-x64
.\src\Sentinel.App\bin\Debug\net10.0-windows10.0.22621.0\win-x64\Sentinel.exe
```

Other configurations:

```powershell
dotnet build src\Sentinel.App -c Release
dotnet build src\Sentinel.App -c Release -r win-arm64
```

Run with synthetic data (useful on a VM or for UI work):

```powershell
Sentinel.exe --simulate
```

Simulation uses its own database (`sentinel-simulation.db`), and every page shows a **Developer Simulation** banner. Scenarios (Idle, Gaming, Overheating, StorageWarning, BatteryDegradation, WifiDropout, DriverCrash, BsodTimeline, MemoryPressure) are chosen in **Settings → Developer**.

Sentinel is single-instance. Starting it a second time brings the running window forward. Close it completely with **Exit** in the tray menu before rebuilding, or the build cannot overwrite `Sentinel.exe`.

## Publish

```powershell
dotnet publish src\Sentinel.App -c Release -r win-x64 -o publish\win-x64
dotnet publish src\Sentinel.App -c Release -r win-arm64 -o publish\win-arm64
```

The output folder is portable. Copy it anywhere and run `Sentinel.exe`. Nothing is installed, and the only state written is under `%LOCALAPPDATA%\Sentinel` (see [PRIVACY.md](PRIVACY.md)).

## Releasing

Releases are GitHub Releases containing a portable, signed zip per architecture. The in-app updater installs only packages whose signature matches the public key in `src/Sentinel.Core/Updates/UpdateSignature.cs`.

One-time setup: create the signing key. It is kept **outside** the repository, so back it up somewhere safe. If it is lost, existing installs cannot update automatically.

```powershell
dotnet run --project tools\Sentinel.ReleaseTool -- keygen "$env:USERPROFILE\.sentinel\release-signing-key.pem"
# paste the printed public key into UpdateSignature.OfficialPublicKeyPem
```

For each release:

1. Bump `<Version>` in `Directory.Build.props`.
2. Commit and push.
3. Run:

```powershell
./tools/release.ps1 -Notes "What changed in this version."
```

The script publishes win-x64 and win-arm64, packs them (`Sentinel-<version>-<rid>.zip`), signs each package (`.zip.sig`), checks every signature against the key built into the app, and runs `gh release create v<version>`. Use `-NoPublish` for a dry run.

`EnableMsixTooling` in `Sentinel.App.csproj` is required. Without it, `dotnet publish` omits `Sentinel.pri` (the compiled XAML) and the published app fails at startup.

## Tests

```powershell
dotnet test --project tests\Sentinel.Tests
```

The suite uses xUnit v3 on Microsoft.Testing.Platform. `global.json` opts `dotnet test` into that runner, which the .NET 10 SDK requires. It covers:

| Area | Examples |
|---|---|
| Analytics | Median/MAD, percentiles, regression, change points, Poisson tail, EWMA, baselines and contexts |
| Storage | SQLite round trips across 10 s/1 min/1 h tiers, retention, event de-duplication, capacity history, app usage |
| Recording | Down-sampling, persist policies, pause behaviour, bounded live buffers |
| Platform parsing | Event-log mapping (bugchecks, Kernel-Power 41, Store updates, PnP 400), NVMe health log layout, battery report XML (with DTDs rejected), GPU engine instance names, update classification, startup command paths |
| Intelligence | Query intents and time ranges, correlation strength, sleep-aware Wi-Fi disconnects |
| Privacy and safety | Redaction, masking, safety-contract violations, allow-listed tools and path confinement, loopback-only AI endpoints, removal of commands from model output, CSV/HTML injection in reports |
| Updates | Version ordering, release feed parsing, signature verification (tampering, wrong key, malformed), host allow-list, download → verify → stage with a fake GitHub, discarding tampered packages |
| Soak | Six simulated hours of 1 s sampling per scenario with heap growth, buffer and series bounds checked; the real scheduler loop including suspend/resume |

You can also run the test executable directly for faster iteration:

```powershell
dotnet build tests\Sentinel.Tests
.\tests\Sentinel.Tests\bin\Debug\net10.0-windows10.0.22621.0\win-x64\Sentinel.Tests.exe -class Sentinel.Tests.SoakTests
```

## Hardware probe

`tools/Sentinel.Probe` initialises every real provider once, samples twice, and prints which capabilities this machine exposes and from which source. It prints no serial numbers or other identifiers, so its output is safe to attach to an issue when reporting a hardware-support problem.

```powershell
dotnet run --project tools\Sentinel.Probe
```

## Icon

`tools/make-icon.ps1` regenerates `src/Sentinel.App/Assets/Sentinel.ico` and `Sentinel.png` from vector drawing code.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `error MSB3027: could not copy Sentinel.exe` | Sentinel is still running (possibly in the tray). Choose **Exit** from the tray menu, or `Get-Process Sentinel \| Stop-Process` |
| XAML compiler error `WMC…` with no detail | Run `dotnet build -v n` and look for the `XamlCompiler` output. Most often a binding to a member the compiler cannot see (bindings require public members on public types) |
| Logs | `%LOCALAPPDATA%\Sentinel\logs\sentinel.log` |
