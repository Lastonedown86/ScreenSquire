# HTTP Agent Update (no SSH) — Design

Date: 2026-07-24
Status: Approved

## Problem

Updating the Pi agent today requires `deploy-agent.ps1`, which uses ssh/scp.
The SSH password prompt freezes the Claude Code chat terminal, and the client
has no way to update Pis at all. We want agent updates pushed over the same
HTTP channel the control app already uses.

## Goals

- Push agent updates (main.py + static pages) to Pis over HTTP — no SSH, no
  password prompt.
- Dev path: a rewritten `deploy-agent.ps1` that works from any terminal,
  including Claude Code chat.
- Client path: an "Update Pi software" feature in the Windows control app, with
  the agent files bundled inside the exe, so shipping a new exe is all the
  client needs.

## Non-goals

- Updating the Python venv / dependencies (requirements.txt changes still go
  over SSH; they are rare).
- Updating pi-setup scripts, systemd units, or the OS.
- Auth on the endpoint (see Security).
- Automatic rollback / canary machinery (see Failure handling).

## Design

### 1. Agent: `POST /api/update`

Multipart upload of a single zip containing only `main.py` and `static/*`.

Validation, in order, before any file is touched:

1. Zip entries must be `main.py` or paths under `static/`. Reject `..`,
   absolute paths, drive letters, symlinks — anything else.
2. Total uncompressed size cap: 20 MB.
3. Extract to a temp dir inside the agent directory (same filesystem, so the
   final move is atomic-ish).
4. `py_compile` the new `main.py`. A syntax error must never brick a Pi that
   is now hard to SSH into — reject with the compile error in the response.

Apply:

1. Copy current `main.py` + `static/` to `agent/update-backup/` (overwrite
   previous backup — one level of history is enough for manual recovery).
2. Move new files into place.
3. Respond `{"ok": true, "version": "<new AGENT_VERSION>"}`.
4. Background task after ~1 s: restart the kiosk user service (existing
   `systemctl --user` helper — makes the TV pick up new static pages), then
   `os._exit(0)`. systemd (`Restart=always`, `RestartSec=3`) relaunches the
   agent with the new code. No sudo required anywhere.

Versioning: new module-level constant `AGENT_VERSION` (date-based string, e.g.
`"2026.07.24.1"`), returned by `/api/status` as `agent_version`. Callers use it
to detect "out of date" and to confirm an update took.

### 2. Dev deploy: rewrite `deploy-agent.ps1`

Same interface as today (`-Hosts`, or the saved devices list from
`%APPDATA%\PiSignage\devices.json`). New body per host:

1. Zip `agent/main.py` + `agent/static/` (built once, in temp).
2. `POST /api/update` via `Invoke-RestMethod`.
3. Poll `/api/status` (timeout ~60 s) until the agent is back and reports the
   new `agent_version`.
4. Report per-host success/failure; nonzero exit if any host failed.

No ssh/scp anywhere in the script.

Bootstrap: the deploy that first ships `/api/update` must itself go over SSH
once per Pi (run from a normal terminal, not Claude Code chat). After that,
SSH is no longer part of the update path.

### 3. Windows app: admin update UI

- Build: the csproj embeds `../agent/main.py` and `../agent/static/**` as
  embedded resources. The app reads its bundled `AGENT_VERSION` by regex from
  the embedded main.py at runtime — main.py is the single source of truth and
  there is no build-time version step.
- The app zips the embedded files at runtime when pushing (keeps the build
  simple; no build-time zip step).
- Device list: when a Pi's `/api/status.agent_version` is older than the
  bundled version, show a small "software update available" indicator on that
  device.
- "Update Pi software" action per device, plus "Update all Pis". Pushes the
  zip, waits for the Pi to come back (same poll as the script), reports in
  plain language ("TV will blink once while it updates"). Errors are shown in
  plain language too — the client is non-technical.
- Old agents (no `agent_version` in status): treat as out of date; the update
  push will 404 until the Pi is bootstrapped over SSH once — show "this Pi
  needs a one-time manual update" rather than a raw error.

### 4. Security

`/api/update` accepts code from anyone on the LAN. This matches the existing
phase-1 posture: the API is deliberately unauthenticated on a trusted LAN and
already exposes sudo-backed WiFi changes and kiosk stop. Decision: no auth
now; phase 2's planned token auth must cover this endpoint along with the
rest. This endpoint is the most powerful one — it is called out in the README
known-limits list.

## Failure handling

- Bad zip / oversized / syntax error → 400, nothing changed on the Pi.
- Runtime (non-syntax) crash after update → systemd restart-loops the agent;
  recovery is manual: SSH in, restore from `agent/update-backup/`. Accepted
  for phase 1; py_compile catches the common case.
- Pi unreachable / times out during poll → reported per host; other hosts
  continue.

## Testing

- Agent: pytest coverage for the validation matrix (traversal names, absolute
  paths, oversize, syntax-error main.py, happy path writes files + schedules
  restart). Restart/exit is mocked.
- Script + app push: exercised against the WSL/VM agent (existing dev loop).
- App: version-compare logic unit-testable; UI flow tested manually against VM.
