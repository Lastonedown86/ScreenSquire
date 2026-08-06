# One-click Spotify sign-in

## Problem

Spotify items play 30-second previews until someone signs into open.spotify.com
inside the kiosk's Chromium profile. Today that takes: Remote control, Kiosk
off, a terminal on the Pi desktop, and a hand-typed
`chromium --user-data-dir=...` command. Store operators won't do that.

## Design (approved by user in conversation)

One button in the app's Spotify window: **Sign in on TV…**

Flow:

1. App starts a remote-desktop session (existing `/api/remote-desktop`,
   existing fingerprint trust-on-first-use dialog).
2. App calls new signed endpoint `POST /api/spotify/signin {"running": true}`.
   The agent stops the kiosk unit and launches a **windowed** Chromium on the
   kiosk profile (`~/.config/pisignage-kiosk`) at https://open.spotify.com/.
3. App launches the bundled TigerVNC viewer. The operator sees the Spotify
   page on the TV, signs in with their own credentials, and closes the viewer.
4. On viewer exit the app calls `{"running": false}` and stops remote desktop.
   The agent kills the browser and restarts the kiosk. The login persists in
   the profile (verified earlier in this session).

## Agent (`agent/main.py`)

Mirrors the remote-desktop section's shape:

- `GET /api/spotify/signin` → `{"running": bool}` (public read, like
  `/api/remote-desktop`).
- `POST /api/spotify/signin {"running": bool}` — signed
  (`require_control_mutation`).
  - start: idempotent if already running. Stops `KIOSK_UNIT` (tolerates a
    dev box with no systemd user manager), spawns the browser through a
    monkeypatchable seam `_spawn_signin_browser()`, verifies it didn't die
    within 0.5 s (on failure restarts the kiosk and returns 500 with stderr).
  - stop: kills the browser, restarts the kiosk.
- Browser spawn: `chromium` or `chromium-browser` (whichever exists),
  `--user-data-dir=$KIOSK_PROFILE_DIR --no-first-run --password-store=basic
  --start-maximized https://open.spotify.com/`. The agent's service env has
  `XDG_RUNTIME_DIR` but not `WAYLAND_DISPLAY`; detect the `wayland-*` socket
  in the runtime dir and pass it, plus `--ozone-platform=wayland`.
- Watcher task: if the operator closes the browser window on the Pi, the
  agent notices the process exit and restarts the kiosk on its own.
- Idle backstop: 15 minutes, same constant style as wayvnc — a TV can never
  be stranded on the login page.
- Lifespan shutdown also stops any live sign-in session (like `_stop_wayvnc`).
- `AGENT_VERSION` bumped.

Known edge (accepted): if the agent process dies mid-sign-in, the kiosk stays
stopped until the daily kiosk-restart timer or a reboot heals it. Same
exposure class as an agent death mid-update; not worth extra machinery.

## Windows app

- `ApiClient`: `StartSpotifySigninAsync` / `StopSpotifySigninAsync` (same
  SendJsonAsync pattern as remote desktop).
- `RemoteTrust.ConfirmServerFingerprint(...)` extracted from `MainWindow` so
  `SpotifyWindow` can reuse the TOFU dialog unchanged.
- `SpotifyWindow`: the static "use Remote control once to sign in" hint gains
  the **Sign in on TV…** button. Click: start remote desktop → confirm
  fingerprint → start sign-in mode → launch viewer → toast telling the
  operator to sign in and close the window. Viewer exit: stop sign-in mode,
  stop remote desktop, toast reminding that previews need Widevine if they
  persist.
- Old agent (no endpoint → 404): toast "Update Pi software", stop the
  remote-desktop session, no other changes.

## Testing

- Agent: `test_spotify_signin.py` in the style of `test_remote_desktop.py`
  (fake proc via the seam, fake `_systemctl_user`): signature required,
  start stops kiosk + spawns browser, idempotent start, stop restarts kiosk,
  spawn failure restarts kiosk + 500, browser-exit watcher restarts kiosk,
  status endpoint.
- App side: flow code follows the already-shipped `BtnRemote_Click` pattern;
  no UI test harness exists in the repo, `dotnet build` + existing
  signage-core tests must stay green.
