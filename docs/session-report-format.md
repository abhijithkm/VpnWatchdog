# VPN Session Summary — report format

When the CLI shuts down (normal exit, `Ctrl+C`, or process termination), it writes a plain-text **"VPN Session Summary"** report summarizing everything observed and everything the watchdog itself did during that run. The report is derived from the correlator's `DisconnectCorrelation` records (what FortiClient did, as seen in its own logs) and from the process's own reconnect-attempt bookkeeping (what this watchdog did) — writing it performs no new observation and no VPN action.

The report is written to `vpn-watchdog-summary-{yyyyMMdd-HHmmss}.txt` next to the running executable, and also echoed to the console; it never overwrites a previous session's report. A summary is guaranteed to be written exactly once per run — both `Ctrl+C` and `AppDomain.ProcessExit` are handled so this happens even on an unexpected shutdown.

## Two independent halves

The report has two clearly separated blocks, and they are **not** meant to be added together:

1. **What FortiClient did on its own**, as seen in its trace logs (unexpected disconnects, automatic recoveries, recovery timing) — this is true regardless of whether auto-reconnect is enabled.
2. **What this watchdog itself did** (`Watchdog-initiated reconnects`) — only ever non-zero if `AutoReconnectEnabled` was on for the run, and counts only this process's own `Connect` calls through `IReconnectController`.

## Fields

| Field | Meaning |
|---|---|
| Profile | The FortiClient profile name that was monitored. |
| Monitoring started / ended | Timestamps bracketing the session. |
| Total time connected / disconnected | Cumulative duration the correlator's fused state was `Connected` vs. not, across the whole session. |
| Unexpected disconnects | Count of correlations classified `"Unexpected"` — FortiClient's own log showed the tunnel going down without a normal user-initiated disconnect. |
| Automatic recoveries | Count of correlations where `RecoverySucceeded == true` — the tunnel came back up (FortiClient's own recovery, *or* this watchdog's reconnect — the trace log doesn't distinguish which). |
| Failed recoveries | Count of correlations where `RecoverySucceeded == false` (the correlator's `OpenCorrelationTimeoutMinutes` elapsed with no reconnect observed). |
| Still open at shutdown | 1 if a disconnect was unresolved when the tool exited, else 0. |
| Average / longest recovery time | Mean / max of `RecoveryDuration` across successful recoveries only. `n/a` if none occurred. |
| Disconnects w/ internet UP / DOWN | Split of disconnects by `InternetStateAtDisconnect` — did the VPN drop while the underlying network was fine, or alongside a general outage? |
| Reconnect attempt observed / not observed | Count of correlations with/without a `ReconnectAttemptDetectedAt` marker. **Known limitation** (unchanged since Phase 1): FortiClient's trace log has no distinct "attempting to reconnect" line, only disconnect and eventual connect-success lines — so this field is expected to always read "not observed" for every disconnect, and recovery timing is measured disconnect→reconnect-success rather than from a true attempt-started marker. |
| Watchdog-initiated reconnects | `Auto-reconnect: ENABLED / DISABLED (observe only) / DISABLED (requested, but FortiClient COM was unavailable)`, plus this process's own attempt/success/failure counts, plus an "unresolved" count if an attempt was still in flight at shutdown. |
| Recovery outcome — succeeded / failed / still open | Restates automatic-recoveries / failed-recoveries / still-open-count as a final tally. |

## Example rendering

```
============================================================================
VPN Session Summary
============================================================================
Profile:                        MLA-DEV-VPN-2-LocalAuth
Monitoring started:             2026-09-05 12:23:31 -04:00
Monitoring ended:               2026-09-05 12:26:42 -04:00

Total time connected:           00h 01m 33s
Total time disconnected:        00h 01m 37s

Unexpected disconnects:         1
Automatic recoveries:           1
Failed recoveries:              0
Still open at shutdown:         0

Average recovery time:          00h 00m 04s
Longest recovery time:          00h 00m 04s

Disconnects w/ internet UP:     1
Disconnects w/ internet DOWN:   0

Reconnect attempt observed:     0
Reconnect attempt not observed: 1
  KNOWN LIMITATION: FortiClient's trace log has no distinct "attempting to
  reconnect" line - only disconnect and eventual connect-success lines exist.
  ReconnectAttemptDetectedAt is therefore expected to always be null in this
  build, so every disconnect currently shows as "not observed" above regardless
  of what FortiClient's built-in auto-reconnect actually did behind the scenes.

Watchdog-initiated reconnects (this process's own actions):
  Auto-reconnect:               ENABLED
  Reconnect attempts made:      1
  Attempts succeeded:           1
  Attempts failed:              0

Recovery outcome - succeeded:   1
Recovery outcome - failed:      0
Recovery outcome - still open:  0
============================================================================
Mode: AUTO-RECONNECT -- this session may ask FortiClient to CONNECT the profile
above. It never disconnects the VPN and never handles credentials.
============================================================================
```

With auto-reconnect off, the closing banner instead reads:

```
Mode: OBSERVE ONLY -- this session never connected, disconnected, or modified the VPN.
```
