# VPN Watchdog

VPN Watchdog monitors a FortiClient IPsec VPN tunnel (profile configurable, default `MLA-DEV-VPN-2-LocalAuth`) on Windows, records evidence of every disconnect and reconnect, and — only when you explicitly turn it on — asks FortiClient to reconnect the tunnel after an outage. It ships as both a console app (`VpnWatchdog.Cli`) and a compact WinForms desktop app (`VpnWatchdog.Gui`), sharing one core library and one activity-log database so either can be used interchangeably.

Developed by Abhijith for the Nix family.

## What it does

- **Monitors** the VPN adapter, general internet reachability, the FortiClient processes, and FortiClient's own trace logs — independently of each other, every 2 seconds by default.
- **Correlates** that evidence into disconnect/reconnect episodes, so you can see how often the tunnel drops, how long it stays down, and whether FortiClient's own built-in recovery brought it back.
- **Can reconnect automatically** (opt-in, off by default) by driving FortiClient's own COM automation interface — the same mechanism the FortiClient UI itself uses. It waits out a grace period first so it never races FortiClient's own recovery, then retries with exponential backoff up to a configurable attempt limit.
- **Supports manual Connect/Disconnect** from a button (GUI) or a one-shot flag (CLI) — reachable only from an explicit user action, never from the automatic monitoring loop.
- **Never handles credentials.** FortiClient's own saved sign-in is what connects the tunnel; this app has no field, parameter, or code path that could hold a password, token, or cookie.
- **Keeps a shared activity log** (GUI and CLI both write to and read from the same file) so you get one continuous history regardless of which one you run.

## What it deliberately does not do

- It never disconnects the VPN on its own. The automatic monitoring/reconnect path is typed against an interface (`IReconnectController`) that has no Disconnect method at all — this is a compiler-enforced guarantee, not a convention. See [Safety model](#safety-model) below.
- It never stores, logs, or transmits a VPN credential.
- It never modifies the registry, restarts FortiClient, or patches/reverse-engineers any FortiClient binary.
- It never spawns FortiClient processes directly (no `ipsec.exe`, no `scheduler.exe`) — all VPN control goes through FortiClient's own registered COM automation interface.

---

## Quick start

```bash
# from the repository root
dotnet build
dotnet test

# run the console app (observe-only by default)
dotnet run --project src\VpnWatchdog.Cli

# or run the desktop app
dotnet run --project src\VpnWatchdog.Gui
```

Both are safe to run with no configuration — they default to **observe-only**: nothing connects, disconnects, or reconnects the VPN unless you explicitly opt in (`--auto-reconnect` for the CLI, the Auto-reconnect checkbox for the GUI).

---

## Project structure

```
vpn-watchdog/
├── VpnWatchdog.slnx
├── README.md                        (this file)
├── docs/
│   ├── example-output.txt           # sample CLI console output
│   └── session-report-format.md     # shutdown summary report field reference
├── src/
│   ├── VpnWatchdog.Core/            # shared domain model + all logic (net8.0, no UI dependency)
│   │   ├── Contracts.cs             #   every enum/record/interface + WatchdogConfig
│   │   ├── Providers/               #   adapter / internet / process observation
│   │   ├── Logging/                 #   FortiClient trace-log tailer + the shared activity log
│   │   ├── Correlation/             #   VpnEventCorrelator - the disconnect/reconnect state machine
│   │   ├── Storage/                 #   SqliteVpnEventStore - raw evidence (snapshots/correlations)
│   │   └── Reconnect/               #   the COM controller, the reconnect policy, the safe fallback
│   ├── VpnWatchdog.Cli/             # console app (net8.0)
│   │   ├── Program.cs               #   entry point, flag parsing, poll loop, session summary
│   │   ├── AppConfigLoader.cs       #   loads/merges WatchdogConfig from JSON
│   │   ├── ConsoleDashboard.cs      #   live status rendering
│   │   └── appsettings.example.json #   copy to appsettings.json to override defaults
│   └── VpnWatchdog.Gui/             # WinForms desktop app (net8.0-windows)
│       ├── MainForm.cs/.Designer.cs #   the compact main window
│       ├── SettingsForm.cs          #   modal settings editor
│       ├── AboutForm.cs             #   About dialog
│       ├── ActivityLogForm.cs       #   "View Logs" window
│       ├── GuiSettings.cs           #   GUI's own persisted preferences
│       └── Assets/                  #   icon.ico, wordmark.png (embedded into the exe)
└── tests/
    └── VpnWatchdog.Tests/           # xUnit — providers, correlator, policy, activity log, etc.
```

---

## How it works

### The monitoring loop

Every poll tick (default 2s), the watchdog independently checks:

- **Adapter state** — the VPN adapter is found dynamically each time by matching `NetworkInterface.InterfaceDescription` against a configurable pattern (default `"Fortinet Virtual Ethernet Adapter"`), never a hardcoded adapter name or index, because Windows can renumber/rename adapters across reboots.
- **Internet reachability** — an independent check (gateway ping, DNS resolution, an HTTPS probe) that is completely decoupled from VPN state, so a general internet outage is never mistaken for a VPN problem or vice versa.
- **FortiClient processes** — whether `FortiVPN`/`FortiSSLVPNdaemon`/etc. are running, purely observational (never started, stopped, or signaled).
- **FortiClient's own trace log** — tailed incrementally by tracked byte offset (never rewritten, never backfilled on first run), classified into typed log events, with automatic recovery from log rotation/truncation.

`VpnEventCorrelator` (pure logic, no I/O) fuses these four signals each tick into one `VpnState` (`Connected` / `Disconnected` / `NetworkUnavailable` / `Recovering` / `Unknown`) and opens/closes `DisconnectCorrelation` evidence records — the primary artifact behind both the live dashboard and the shutdown summary report.

### The reconnect mechanism

Reconnect is driven by `FortiClientComReconnectController`, which talks to FortiClient's own registered COM automation interface — the same interface FortiClient's own UI uses internally, not a workaround.

- **ProgID `FCCOMInt.XVPN`** (out-of-process server `fccomint.exe`), falling back to `FortiClient.VPN` if that's not registered. Loaded late-bound (`Type.GetTypeFromProgID` + `InvokeMember`), so the app builds and runs fine even on a machine without FortiClient installed.
- Only four COM methods are ever called: `Connect`, `Disconnect`, `IsConnected`, `GetTunnelList`. Never `SendXAuthResponse` — there is no credential path.
- `Connect`/`Disconnect` are fire-and-forget on FortiClient's side, so both are followed by polling `IsConnected` until it actually flips (or a verify timeout expires) — an earlier version trusted the immediate return and that was a real, observed bug.
- All COM calls go through a **bounded** lock (`Monitor.TryEnter`, never a plain `lock`), because a blocking cross-process COM call cannot be cancelled — this is specifically what stops a wedged `fccomint.exe` from permanently freezing the reconnect logic or the UI thread.
- Failures are classified three ways: **unavailable** (FortiClient COM genuinely isn't there → fall back to observe-only, but re-probe periodically and re-arm automatically if it comes back), **stale** (the COM server restarted underneath us → rebind and retry once), **failed** (a normal attempt that didn't work → count it, back off, try again later).

### The reconnect policy — when it actually fires

`ReconnectPolicy` is pure, deterministic decision logic (no COM, no I/O — fully unit-testable), evaluated every poll tick, first-match-wins:

1. VPN is `Connected` → nothing to do, reset everything for the next outage.
2. Auto-reconnect is off → stay disabled, no matter what else is true.
3. Internet isn't up → reconnecting can't help yet; the grace-period clock is cleared (not just paused) so a long ISP outage can't quietly burn through the whole grace window.
4. A reconnect is already in flight → wait (single-flight; two overlapping reconnects can never happen).
5–6. **Inside the grace period** (default 90s) → wait. FortiClient's own built-in recovery was measured healing in 6–70 seconds during testing, and this grace period exists specifically so the watchdog never races it.
7. Attempt budget exhausted (default 5 per outage) → give up until the tunnel changes state.
8. Still inside exponential backoff (30s initial, doubling, capped at 300s by default) after a failed attempt → wait.
9. Otherwise → trigger a reconnect attempt.

The policy also defends against two real-world clock problems: a **laptop suspend/resume** gap longer than a few minutes restarts the grace window (sleeping isn't a chance for FortiClient to self-heal) without resetting the attempt budget, and a **backward wall-clock step** (NTP correction, DST) can never stall the watchdog indefinitely, because elapsed time is cross-checked against a monotonic clock.

### Manual Connect/Disconnect — and why it can never fight the automatic loop

`FortiClientComReconnectController` implements two separate interfaces:

- **`IReconnectController`** (`Connect` / `IsConnected` / `GetTunnelList` — no Disconnect method exists on this interface at all) — this is the *only* type the automatic poll/policy loop is ever given. It is structurally incapable of disconnecting the tunnel; this is enforced by the compiler, not a rule someone has to remember to follow.
- **`IManualVpnControl`** (`Connect` / `Disconnect`) — reachable only from an explicit user action: the GUI's Connect/Disconnect button, or the CLI's `--connect`/`--disconnect` flags. Never from a timer, a poll tick, or the reconnect policy.

Because a manual disconnect while auto-reconnect is armed would otherwise have the watchdog immediately bring the tunnel right back up, both the GUI and CLI **disarm auto-reconnect first**, visibly, before disconnecting — and the GUI confirms with the user (defaulting to *No*) before doing it at all.

### The activity log

A shared, human-readable trail — separate from the raw evidence database — recording things like "VPN Connected", "Auto-reconnect triggered", "Manual disconnect requested", each with a timestamp, message, and optional detail. It lives at **`%LOCALAPPDATA%\VpnWatchdog\activity-log.db`** by default, a fixed path so the GUI and CLI both append to and read from the same one history. It's capped (default 5000 entries, pruned lazily) and its `Record()` call is guaranteed to never throw — a failure to write a log line must never be able to take down monitoring or a reconnect attempt.

---

## Safety model

These are structural properties of the code, not just documented promises:

| Guarantee | How it's enforced |
|---|---|
| Never disconnects automatically | The automatic loop only ever holds an `IReconnectController` reference, which has no Disconnect member — won't compile otherwise. Covered by a dedicated reflection-based test suite (`ManualVpnControlTests.cs`) that fails if this is ever weakened. |
| Never handles credentials | No field, parameter, or config key exists for one anywhere in the codebase; `SendXAuthResponse` (the one COM method that would need a credential) is never called. |
| Auto-reconnect is opt-in | `AutoReconnectEnabled` defaults to `false` everywhere (`WatchdogConfig.Default`, `GuiSettings`) and must be explicitly set — because no FortiClient log signal reliably distinguishes a deliberate manual disconnect from an unexpected one, an explicit switch is the only honest control. |
| Won't race FortiClient's own recovery | The grace period (rule 5–6 above) always runs before any reconnect attempt. |
| Single-flight | An interlocked latch (`BeginAttempt`/`EndAttempt`) guarantees at most one reconnect attempt runs at a time. |
| No registry/process/binary modification | Never written anywhere in the codebase — the only Windows APIs touched are read-only network/process enumeration plus the FortiClient COM interface. |

---

## Running the CLI

```
dotnet run --project src\VpnWatchdog.Cli -- [config-file] [--auto-reconnect | --connect | --disconnect]
```

Or, after `dotnet build`, run the built executable directly:
`src\VpnWatchdog.Cli\bin\Debug\net8.0\VpnWatchdog.exe`

| Flag | Behavior |
|---|---|
| *(none)* | Observe-only monitoring loop. Renders the live dashboard until `Ctrl+C`, then writes a session summary. |
| `--auto-reconnect` | Arms auto-reconnect for this run (in addition to whatever the config file says — either source can turn it *on*, neither can turn it *off* if the other wants it on). |
| `--connect` | One-shot: ask FortiClient to connect the configured profile, confirm, print the outcome, exit. No monitoring, no database, no session summary. |
| `--disconnect` | One-shot: ask FortiClient to disconnect, confirm, print the outcome, exit. Same "no monitoring machinery" behavior. |
| *(first plain argument)* | Optional path to a config JSON file. If omitted, looks for `appsettings.json` next to the executable, then falls back to compiled-in defaults. |

`--connect` + `--disconnect` together, and `--disconnect` + `--auto-reconnect` together, are both refused outright (exit code 2) rather than guessed at.

**Exit codes:** `0` success, `2` bad command line, `3` FortiClient COM unavailable, `4` attempted but not confirmed, `5` cancelled.

See [`docs/example-output.txt`](docs/example-output.txt) for a sample of the live dashboard, and [`docs/session-report-format.md`](docs/session-report-format.md) for the full shutdown-summary field reference.

---

## Running the GUI

```
dotnet run --project src\VpnWatchdog.Gui
```

Or run `src\VpnWatchdog.Gui\bin\Debug\net8.0-windows\VpnWatchdogGui.exe` directly after building.

A single compact window (≈400×450px, resizable-free by design — it's meant to sit beside your other apps, not dominate the screen):

- **Status hero** — one glance answer to "is my VPN connected right now", colored by state.
- **Diagnostics** — Internet / VPN Adapter / FortiClient rows, VPN uptime, and the most recent activity-log entry.
- **Start/Stop** — starts or stops *this app's own monitoring loop only*. Has no effect on the real VPN by itself.
- **Auto-reconnect checkbox** — arms/disarms the automatic reconnect policy; takes effect immediately, independent of whether monitoring is running.
- **Connect/Disconnect** — a single contextual button (never both at once) that reflects the tunnel's actual current state. Disconnect asks for confirmation (default: No) and disarms auto-reconnect first.
- **View Logs** — opens the shared activity log in a filterable, exportable list (All events / VPN only / Reconnects / Warnings & errors), with Copy, Export (.txt/.csv), and a confirmed Clear Logs.
- **Settings (gear icon)** — profile name, poll interval, start-on-launch, minimize-to-tray, and all five reconnect tuning numbers; edits a private copy, only applies on OK. Includes a link to **About**.
- **Tray icon** — minimizing (or closing, if "Minimize to tray on close" is checked) hides to the system tray rather than exiting; double-click or the tray menu's Show restores it.

GUI preferences persist to **`%LOCALAPPDATA%\VpnWatchdog\gui-settings.json`**. The GUI uses its own evidence database (`vpn-watchdog-gui.db`, distinct from the CLI's `vpn-watchdog.db`, so the two can run simultaneously without contention) but shares the same activity-log file as the CLI.

---

## Configuration reference (`WatchdogConfig`)

Copy `src\VpnWatchdog.Cli\appsettings.example.json` to `appsettings.json` next to the built CLI executable to override any of these (every field is optional — only specify what you want to change):

| Field | Purpose | Default |
|---|---|---|
| `ProfileName` | FortiClient VPN profile name to monitor. | `MLA-DEV-VPN-2-LocalAuth` |
| `AdapterDescriptionPattern` | Substring matched against the adapter's `InterfaceDescription` to find it dynamically every poll. | `Fortinet Virtual Ethernet Adapter` |
| `HttpsProbeUrl` | URL used for the internet-reachability probe. | `https://www.microsoft.com/robots.txt` |
| `PollIntervalMs` | How often adapter/internet/process state is checked. | `2000` |
| `HeartbeatIntervalMs` | How often the dashboard/heartbeat redraws even with no change. | `30000` |
| `OpenCorrelationTimeoutMinutes` | How long an unresolved disconnect stays "open" before being declared a non-recovery. | `30` |
| `FortiClientLogDirectory` | Where FortiClient's trace logs live (read-only, never written to). | `C:\Program Files\Fortinet\FortiClient\logs\trace` |
| `DatabasePath` | The tool's own raw-evidence SQLite database. | `vpn-watchdog.db` |
| `AutoReconnectEnabled` | Master switch for automatic reconnect. | `false` |
| `ReconnectGracePeriodSeconds` | How long to wait for FortiClient's own recovery before intervening. | `90` |
| `ReconnectMaxAttempts` | Attempts allowed per outage before giving up. | `5` |
| `ReconnectInitialBackoffSeconds` | Wait after the first failed attempt. | `30` |
| `ReconnectMaxBackoffSeconds` | Cap on the exponential backoff. | `300` |
| `ReconnectVerifyTimeoutSeconds` | How long to wait for FortiClient to confirm a Connect/Disconnect actually took effect. | `30` |
| `ActivityLogPath` | Path to the shared human-readable activity log. | `%LOCALAPPDATA%\VpnWatchdog\activity-log.db` |
| `ActivityLogMaxEntries` | Retention cap for the activity log. | `5000` |

The GUI has its own separate, simpler preferences file (`GuiSettings` → `gui-settings.json`) editable through the Settings dialog rather than by hand.

---

## Data files — where everything lives

| File | What it holds | Written by |
|---|---|---|
| `%LOCALAPPDATA%\VpnWatchdog\activity-log.db` | Shared human-readable event history (see [The activity log](#the-activity-log)). | GUI and CLI both |
| `%LOCALAPPDATA%\VpnWatchdog\gui-settings.json` | GUI preferences. | GUI only |
| `vpn-watchdog.db` (next to the CLI exe, or wherever `DatabasePath` points) | Raw evidence: adapter/internet snapshots, log events, disconnect correlations. | CLI |
| `vpn-watchdog-gui.db` (next to the GUI exe) | Same shape as above, kept separate so GUI and CLI can run at once. | GUI |
| `vpn-watchdog-summary-{timestamp}.txt` (next to the CLI exe) | End-of-session report, one per run, never overwritten. | CLI, on shutdown |

None of these ever contain a credential — there is no field in the data model for one.

---

## Testing

```
dotnet test
```

154 tests across: provider behavior, the correlator state machine (with real log-rotation scenarios), the 9-rule reconnect policy (including clock-anomaly robustness — backward steps, laptop suspend/resume), the activity log (round-tripping, retention/pruning, concurrent writers, never-throws guarantees), and — most importantly — a dedicated suite asserting the automatic/manual safety separation by reflection, so a future change that accidentally lets the automatic loop reach Disconnect fails the build.

---

## Known limitations

- **`ReconnectAttemptDetectedAt` is always null.** FortiClient's own trace log has no distinct "attempting to reconnect" line — only a disconnect line and, eventually, a connect-success line. Recovery timing in the session summary is therefore measured disconnect→reconnect-success, which may include time FortiClient spent before it actually started retrying. This is called out explicitly in every session summary rather than silently omitted.
- **Reason codes in the trace log don't distinguish manual from unexpected disconnects.** Observed live: a deliberate manual disconnect and a genuine unexpected drop both logged reason code 21 ("Cancelled"). This is exactly why auto-reconnect defaults off and must be explicitly armed — no signal exists that could safely make that call for you.

## Uninstall / rollback

VPN Watchdog only ever reads OS-provided network/process APIs and FortiClient's own trace logs, and drives FortiClient through its own registered COM interface — it never writes to FortiClient's files, the registry, or any other application's state. The only things it writes are its own database files, its activity log, its settings file, and its session summaries (all listed in [Data files](#data-files--where-everything-lives) above). To remove it:

1. Stop the running process(es).
2. Delete the `vpn-watchdog` project folder.
3. Delete `%LOCALAPPDATA%\VpnWatchdog\` (activity log + GUI settings).

Nothing else on the system was touched.
