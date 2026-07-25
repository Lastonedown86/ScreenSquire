# Authenticated HTTP Agent Update — Implemented Design

**Date:** 2026-07-24
**Security lifecycle revision:** 2026-07-25
**Status:** Implemented; real-Pi update acceptance remains a release gate

## Problem and outcome

The builder and client need to update paired Display Pis without interactive
SSH. The developer script and Windows app now send the same complete,
authenticated agent bundle over HTTP. The endpoint is a normal signed control
request: an unpaired laptop or unsigned LAN caller cannot install code.

Dependency, Pi setup, systemd, and OS changes remain outside this mechanism and
require builder-managed deployment.

## Approved bundle

Every accepted archive must contain exactly the managed Python modules plus
static content:

```
main.py
trust.py
control_auth.py
delivery_reset.py
static/**
```

The WPF project file embeds `agent/*.py` plus `agent/static/**` as resources.
`AgentBundle` filters those resources and emits only the approved paths above
when it creates an update; `deploy-agent.ps1` stages the same allowlist.
`main.py` contains the single `AGENT_VERSION` value used by the app/script to
confirm the update after restart.

## Authentication and transport

`POST /api/update` requires the current per-device controller secret. The
caller reserves and durably saves a monotonic counter before network I/O, then
signs:

```
controller_id
counter
POST
/api/update
sha256(zip bytes)
```

The entity hash is sent separately from multipart framing, so both C# and
PowerShell bind the signature to the exact ZIP bytes. The agent verifies the
HMAC first, streams and hashes at most 20 MB of compressed content, compares
the uploaded bytes with the signed entity hash, and only then consumes the
counter. Reusing the accepted request/counter returns 409; a stale controller
returns 401.

HTTP provides no confidentiality. Updates are intended for the physical USB
link or controlled store LAN, but authenticity and integrity do not depend on
LAN trust.

## Agent validation and transactional installation

Before changing installed code, the agent:

1. rejects compressed uploads over 20 MB;
2. opens the ZIP and rejects absolute paths, drive letters, backslashes,
   traversal, empty/dot segments, duplicate names, file/directory collisions,
   and unapproved files/folders;
3. requires all four root modules and static content;
4. rejects total uncompressed content over 20 MB;
5. extracts to a temporary directory on the same filesystem;
6. compiles every managed Python module;
7. reads the proposed `AGENT_VERSION`;
8. builds `update-backup/` from the complete currently installed bundle.

Installation moves each managed path into a rollback directory and replaces it
from staging. Any move failure restores every touched path in reverse order.
The endpoint reports whether rollback itself was incomplete rather than
claiming the old version was restored. On success it returns the new version,
restarts the kiosk after the response can flush, and exits so systemd relaunches
the agent.

`update-backup/` remains a one-level recovery snapshot for manual support. The
transactional rollback handles install-time failures; a syntactically valid
but runtime-broken release can still require attended repair.

## Developer deployment

`deploy-agent.ps1` uses the current Windows user's saved device list and
DPAPI-protected credential vault.

1. Resolve each target to exactly one saved Pi by address, hostname, or name.
2. Display name, stable device ID, bundled version, and pairing state.
3. Refuse the entire run if any target lacks a saved paired credential.
4. In `-WhatIf`, stop before bundle creation, counter reservation, credential
   writes, or network activity.
5. Build one approved ZIP.
6. For each Pi, reserve its counter, sign the ZIP, and POST `/api/update`.
7. Poll public `/api/status` for up to 60 seconds until the expected
   `agent_version` appears.
8. Continue across per-Pi failures and exit nonzero if any target failed.

There is no SSH/SCP path in this script.

## Windows client update

The self-contained controller executable embeds the approved bundle. When a
saved, reachable, paired Pi reports a different version, the app can update it
using the credential for that stable device ID. Targets with no credential are
shown as requiring pairing instead of receiving an attempted mutation.

The app uses the same canonical HMAC fields and hashes the ZIP bytes, not the
multipart envelope. It waits for the agent to return at the embedded version
and explains that the TV can blink once while the kiosk/agent restart.

## Ownership lifecycle

- During Builder setup, the builder pairs by USB, joins builder Wi-Fi, and
  exercises the update path over Wi-Fi.
- Prepare for delivery removes the builder credential from the Pi and then
  attempts to remove it from the builder laptop. A delivery-ready Pi therefore
  rejects subsequent builder-signed updates.
- Store onboarding pairs the shared Controller laptop, which can then update
  the Pi without a password prompt.
- Ownership recovery rotates the controller secret. The retired laptop's
  update request receives 401 even if it still holds its old secret.
- Remote support uses attended Quick Assist on the Controller laptop. It does
  not add a second controller or copy credentials.

## Failure handling

- Missing/extra/unsafe archive content, size limit, hash mismatch, or syntax
  error: 400 with installed files unchanged.
- Missing/invalid controller signature: 401.
- Replayed counter: 409.
- Install move failure with successful rollback: 500 and previous managed
  bundle restored.
- Rollback failure: 500 with an explicit incomplete-rollback message and
  critical log.
- Pi does not return at the expected version: report per target; do not hide
  failures behind success for other Pis.
- Dependency/setup/OS change: not accepted in the bundle; use a
  builder-managed maintenance path.

## Verification and pending hardware acceptance

Automated tests cover archive allowlisting, traversal/collision cases, both
size limits, signed byte integrity, replay, missing/stale credentials,
PowerShell `-WhatIf`, embedded bundle contents, complete backup, move rollback,
and post-update version polling behavior.

Release acceptance still requires a reachable real Pi: install an update over
Wi-Fi, observe the temporary restart, verify the expected version returns, send
an unsigned update and record 401, and replay an accepted signed request to
record 409. Use the complete builder/store checklists in `README.md`. Hardware
unavailability leaves this acceptance gate pending; it is not evidence that the
automated update regression failed.
