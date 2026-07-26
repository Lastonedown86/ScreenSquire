# Remote Desktop — Phase 1 (LAN) Design

**Date:** 2026-07-26
**Status:** Approved

## Goal

Operators and customers can see and control a Pi's screen (mouse + keyboard)
from the Windows app, over the same LAN. Motivating case: administering a Pi
that has no keyboard attached (e.g. resetting its login password from the
app instead of pulling the SD card).

## Decision context

This supersedes the "VNC disabled, Quick Assist only" decision baked into
`pi-setup/provision-usb.sh`. That decision assumed remote support is always
attended by the operator on a store laptop. Customers now need self-service
control of their own Pis on their own LAN, so the platform grows a
first-party, paired-auth remote desktop instead of an always-on third-party
VNC service. RealVNC stays disabled; wayvnc runs only on demand.

Phase 2 (internet reach: relay/broker, NAT traversal, remote consent) is
explicitly out of scope and gets its own design.

## Architecture

```
Windows app                          Pi
┌────────────────┐   signed HTTP   ┌───────────────────────────┐
│ Remote control │ ──────────────> │ agent                     │
│ button         │  /api/remote-   │  ├─ writes wayvnc creds   │
│                │   desktop       │  ├─ starts/stops wayvnc   │
│ launches       │                 │  └─ returns session creds │
│ vncviewer.exe  │ <═════════════> │ wayvnc :5900 (RSA-AES+TLS)│
│ (bundled       │   VNC session   │ mirrors labwc compositor  │
│  TigerVNC)     │                 └───────────────────────────┘
└────────────────┘
```

### Pi side

- `wayvnc` already ships with Raspberry Pi OS Bookworm as the built-in VNC
  backend (it is the `wayvnc.service` that `provision-usb.sh` disables), so
  the binary is present and works with the labwc/wlroots compositor the
  kiosk uses. The agent spawns it on demand as a child process; it is never
  left enabled as a service.
- **Auth: wayvnc's only real authentication is RSA-AES-256 over TLS with a
  username/password** — there is no plain VncAuth password mode. The agent
  generates per-session credentials and configures wayvnc for RSA-AES; a
  matching viewer must support that security type (TigerVNC does; mainstream
  .NET RFB libraries do not — this is why the viewer is external, below).
- New agent endpoint: `POST /api/remote-desktop` with body
  `{"running": true|false}`, guarded by `require_control_mutation`
  (paired-controller signature — same trust as playlist mutations).
  Legacy/unpaired agents never expose the feature. `GET /api/remote-desktop`
  reports `{"running": bool}` (unsigned, like `/api/kiosk`).
- Start: agent generates a random per-session username+password, writes a
  0600 wayvnc config enabling RSA-AES auth, launches `wayvnc` on `:5900`,
  and returns `{"ok": true, "port": 5900, "username": "...",
  "password": "..."}` in the signed response.
- Stop: explicit `{"running": false}`, idle timeout (15 min), or agent
  shutdown all terminate wayvnc and delete the creds file. No listening
  port exists while the feature is off.
- The session mirrors whatever the compositor shows: the kiosk if running,
  the desktop if the operator toggled "TV display on/off" first.

### App side

- "Remote control" button beside "TV display on/off"; enabled only while
  connected to a paired Pi (a device with a verified DeviceId and vault
  credential — never for `ControlContext.LegacyUnsigned()`).
- Click → signed start request → app launches a **bundled TigerVNC
  `vncviewer.exe`** pointed at `<pi>:5900` with the returned per-session
  credentials. TigerVNC natively speaks wayvnc's RSA-AES security type, so
  no custom RFB client or crypto handshake is written. The viewer runs in
  its own window (not embedded in the WPF app).
- Credentials are passed to the viewer without landing on disk in plaintext
  where avoidable (passwd file in a temp dir, deleted on viewer exit; or
  stdin where the viewer supports it).
- When the viewer process exits, the app sends the signed stop. Viewer
  crash or app crash leaves the agent idle timeout as the backstop.
- `vncviewer.exe` (~2 MB, TigerVNC, GPLv2) is bundled in the app output.

## Error handling

- Start fails (wayvnc missing, spawn error) → agent returns 500 with
  detail; app shows a toast with the reason.
- Bundled `vncviewer.exe` missing → app shows a toast, sends the signed
  stop, does not launch.
- Agent restart or Pi reboot kills wayvnc (child process, not a service).

## Security

- Start/stop requires a paired-controller signature; the feature does not
  exist for legacy/unsigned devices.
- Session credentials are random, scoped to one session (reconnects within
  the session allowed), returned only inside the signed HTTP response, and
  die with the session; the agent deletes the wayvnc creds file on stop.
- wayvnc uses RSA-AES-256 over TLS — the session is encrypted on the wire,
  not plain RFB.
- wayvnc listens only while a session is active; idle timeout guarantees
  it cannot be left running indefinitely.
- No change to RealVNC (stays disabled) or to the USB pairing model.

## Testing

- Agent: endpoint requires signature; start returns a fresh password each
  session; stop and idle timeout terminate the process; no port listening
  after stop. (pytest, existing control-auth fixtures.)
- App: control-context gating (button disabled for legacy devices);
  start/stop request shapes; viewer-launch argument construction (creds →
  vncviewer command line, unit-tested without spawning). (xUnit, existing
  RecordingHandler pattern.)
- Manual end-to-end on a migrated, paired Pi: view + control kiosk and
  desktop, password reset walkthrough.

## Dependencies / sequencing

- Requires the target Pi on an agent ≥ this feature's version, provisioned
  and USB-paired. (TV1 must complete its pending migration first.)
- Builds on the paired-signature control path shipped in PR #5.
