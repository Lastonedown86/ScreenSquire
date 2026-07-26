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
Windows app                        Pi
┌───────────────┐   signed HTTP   ┌───────────────────────────┐
│ Remote control │ ──────────────>│ agent                     │
│ button         │  /api/remote-  │  ├─ starts/stops wayvnc   │
│               │   desktop       │  └─ one-time password     │
│ Viewer window  │ <═════════════>│ wayvnc :5900 (RFB)        │
│ (RFB client)   │   VNC session  │ mirrors labwc compositor  │
└───────────────┘                 └───────────────────────────┘
```

### Pi side

- `wayvnc` (Raspberry Pi OS repo package; works with the labwc/wlroots
  compositor the kiosk already uses). Installed by `pi-setup/install.sh`.
  Never enabled as a service; the agent spawns it as a child process.
- New agent endpoint: `POST /api/remote-desktop` with body
  `{"running": true|false}`, guarded by `require_control_mutation`
  (paired-controller signature — same trust as playlist mutations).
  Legacy/unpaired agents never expose the feature.
- Start: agent generates a random per-session password, launches
  `wayvnc` on `:5900` with that password, and returns
  `{"ok": true, "port": 5900, "password": "..."}` in the signed response.
- Stop: explicit `{"running": false}`, idle timeout (15 min with no
  connected viewer), or agent shutdown all terminate wayvnc. No listening
  port exists while the feature is off.
- The session mirrors whatever the compositor shows: the kiosk if running,
  the desktop if the operator toggled "TV display on/off" first.

### App side

- "Remote control" button beside "TV display on/off"; enabled only while
  connected to a paired Pi (a device with a verified DeviceId and vault
  credential — never for `ControlContext.LegacyUnsigned()`).
- Click → signed start request → viewer window opens inside the app and
  auto-connects to `<pi>:5900` with the returned password. Mouse and
  keyboard flow through the RFB session.
- Viewer is WPF-rendered via a .NET RFB client library; the exact library
  is selected during implementation planning (must support RFB 3.8 +
  VncAuth minimum, render to a WPF surface, and be actively maintained).
- Closing the viewer window sends the signed stop. Viewer disconnect or
  app crash leaves the idle timeout as the backstop.

## Error handling

- Start fails (wayvnc missing, spawn error) → agent returns 500 with
  detail; app shows a toast with the reason.
- Viewer cannot connect within 10 s → app sends stop, shows a toast.
- Agent restart or Pi reboot kills wayvnc (child process, not a service).

## Security

- Start/stop requires a paired-controller signature; the feature does not
  exist for legacy/unsigned devices.
- Session password is random, scoped to one session (reconnects within the
  session allowed), returned only inside the signed HTTP response, and dies
  with the session.
- wayvnc listens only while a session is active; idle timeout guarantees
  it cannot be left running indefinitely.
- No change to RealVNC (stays disabled) or to the USB pairing model.

## Testing

- Agent: endpoint requires signature; start returns a fresh password each
  session; stop and idle timeout terminate the process; no port listening
  after stop. (pytest, existing control-auth fixtures.)
- App: control-context gating (button disabled for legacy devices);
  start/stop request shapes. (xUnit, existing RecordingHandler pattern.)
- Manual end-to-end on a migrated, paired Pi: view + control kiosk and
  desktop, password reset walkthrough.

## Dependencies / sequencing

- Requires the target Pi on an agent ≥ this feature's version, provisioned
  and USB-paired. (TV1 must complete its pending migration first.)
- Builds on the paired-signature control path shipped in PR #5.
