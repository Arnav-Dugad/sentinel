# Privacy

Sentinel runs entirely on your PC. It has no servers, no accounts, no analytics, no crash upload and no automatic update check. It makes no network connections except the ones below. The update check is the only automatic one, and it can be turned off:

| Connection | When | To |
|---|---|---|
| Update check | Two minutes after start, then every 6 hours (Settings → Updates → *Check for updates automatically*) | `api.github.com` (latest release of `Arnav-Dugad/sentinel`). Packages are downloaded from `github.com` / `*.githubusercontent.com`. The request carries only a `Sentinel/<version>` user agent. GitHub sees your IP address, as with any web request |
| Connectivity test | You run it on the Network Test page | Pings your default gateway, resolves and pings `www.msftconnecttest.com` (the host Windows itself uses for connectivity checks) |
| Local AI (Ollama) | You turn it on in Settings and ask a question | A loopback address only (`localhost` / `127.0.0.1` / `::1`). Remote endpoints are rejected |
| Links | You click a documentation link | Opens in your browser |

## What is stored

Everything lives under `%LOCALAPPDATA%\Sentinel\`:

| File / folder | Contents |
|---|---|
| `sentinel.db` | SQLite history: metric samples, events, detected changes, hardware inventory snapshots, anomalies, baselines, sessions, battery capacity, disk health, per-app resource use |
| `sentinel-simulation.db` | Developer Simulation data, kept separate so it is never mixed with real history |
| `settings.json` | Your preferences |
| `logs\` | Rotating diagnostic logs (5 × 2 MB). They contain no metric values |
| `reports\` | Reports you exported |
| `temp\` | Intermediate files such as the Windows battery report XML, deleted after parsing |
| `updates\` | A downloaded, verified update waiting to be installed, plus `install.log` and the result of the last installation. Old packages are removed after updating |

### Retention

| Data | Kept for |
|---|---|
| 10-second samples | 48 hours |
| 1-minute samples | 21 days, or your retention setting if shorter |
| Hourly aggregates, events, changes, anomalies, sessions | Your retention setting (default 90 days; 0 = unlimited) |
| Battery capacity and disk health history | Your retention setting |

Typical database size is a few tens of MB after several months. **Settings → History and storage** shows the current size.

## Sensitive identifiers

Serial numbers, the system UUID, MAC addresses, IP addresses, Wi-Fi network names and the Windows user name are **masked in the UI by default** (`••••••••5678`). The *Show sensitive identifiers* switch reveals them for the current session only; it is never saved. Sentinel does not write them to its logs, and redaction is applied to logs exported in a support bundle.

## Privacy mode

**Privacy mode** (Settings → Privacy, or the tray menu) does the following:

- Stops recording which applications use resources. Per-app history is neither written nor shown.
- Hides process names from the Home page, anomaly explanations and memory-growth detection.
- Keeps system-level metrics (CPU, temperatures, battery and so on) working as normal.

You can also turn off **Record per-application usage history** alone, or **Pause history recording** entirely. While paused, live views keep working but nothing is written to disk.

## Reports and support bundles

Reports are **privacy-safe by default**. `Redactor` removes or masks:

- IPv4/IPv6 and MAC addresses, GUIDs and email addresses
- User profile paths (`C:\Users\<name>\…`)
- Windows account SIDs
- The current user name and computer name
- Serial numbers (omitted from privacy-safe reports)

Driver and firmware version numbers are kept, because they matter for diagnosis. A support bundle is a `.zip` with an HTML and a JSON summary, the redacted log and a README describing what was removed. It never includes the database. Open it and check it before sharing.

## Local AI

When local AI is on, Sentinel sends the model only the evidence summary for your question, with the same redaction applied. Nothing is sent anywhere other than the loopback endpoint. Turning the option off stops all requests.

## Deleting your data

- **Settings → History and storage → Delete all history…** deletes all recorded history and leaves your settings in place.
- To remove everything, quit Sentinel from the tray menu and delete `%LOCALAPPDATA%\Sentinel`. If you enabled *Start with Windows*, turn it off first, or delete the `Sentinel` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
