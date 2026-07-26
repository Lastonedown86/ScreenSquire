# Pi Signage

Digital signage for Raspberry Pi with a Windows control app. Each Display Pi is
the source of truth for its media, playlist, tournament board, and timer, so the
TV keeps running when the Controller laptop is off.

```
agent/            FastAPI service and kiosk pages that run on each Display Pi
windows-app/      .NET 8 WPF control app for the shared store Windows login
signage-core/     Testable controller, pairing, signing, and discovery logic
pi-setup/         Installer and USB provisioning scripts for a real Pi
```

## Security and ownership model

Each Display Pi has a stable device ID and a unique 8-digit Recovery PIN. The
PIN is printed once during USB provisioning and belongs on the bottom of that
Pi's case. A Display Pi trusts exactly one Controller laptop at a time.

- Pairing requires the correct Recovery PIN and a USB connection at
  `10.55.0.1`; five bad PIN attempts block pairing for 60 seconds.
- Pairing or Ownership recovery creates a new per-Pi controller secret and
  invalidates the previous laptop immediately.
- The Windows app stores controller secrets encrypted with Windows DPAPI for
  the current Windows user. Daily use on the shared store login is
  passwordless after Store onboarding.
- Every state-changing request is HMAC-SHA256 signed over the controller ID,
  durable monotonic counter, HTTP method, exact path/query, and entity hash.
  Replayed counters are rejected.
- Pairing is the only unsigned state change. It is USB-only and PIN-gated.
  Prepare for delivery is both USB-only and signed.
- Status, playlists, dashboard state, kiosk pages, media display, and other
  read-only traffic remain public so the TV can render without a login. HTTP
  traffic is not encrypted; use only the intended USB/store LAN.
- There is no legacy insecure control mode.

## Windows control app

Requires the .NET 8 SDK (or Visual Studio 2022) for development.

```powershell
cd windows-app
dotnet run

# Ship one self-contained executable; the client does not need .NET installed.
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# bin\Release\net8.0-windows\win-x64\publish\PiSignageControl.exe
```

The executable embeds the complete approved agent bundle:
`main.py`, `trust.py`, `control_auth.py`, `delivery_reset.py`, and `static/**`.
When a paired Pi is out of date, **Update Pi software** sends that authenticated
bundle and confirms the new version after the agent restarts.

For VM/WSL development, enter its address manually (for example,
`localhost:8080`) because mDNS discovery generally does not cross WSL2 NAT.
Mutation controls remain unavailable until the development agent has been
paired; production pairing is intentionally restricted to the USB subnet.

## Run the agent for development

Use a disposable data directory so development cannot alter real Pi state:

```bash
cd agent
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
export SIGNAGE_DATA="$(mktemp -d)"
python trust.py init --data-dir "$SIGNAGE_DATA"
python main.py
```

Record the printed `RECOVERY_PIN` if the pairing flow will be tested. Open
`http://localhost:8080/` for the TV view. Public GET endpoints can be inspected
directly; use the Windows app for signed mutations.

## Builder setup

These steps are performed on every new Pi before shipment:

1. Copy this repository to `~/pi-signage` and run
   `cd ~/pi-signage/pi-setup && bash install.sh`.
2. Run `bash provision-usb.sh`. Record the one-time `RECOVERY_PIN` output and
   put that exact 8-digit PIN on a durable bottom-case sticker. Re-running the
   script does not reveal or replace the existing PIN.
3. Reboot, connect the Pi to the builder laptop with a USB-C **data** cable,
   open **Set up a new Pi**, enter the PIN, and pair it.
4. Join the builder's Wi-Fi through the wizard. Confirm the saved Pi has the
   same stable device identity over USB and Wi-Fi.
5. Over Wi-Fi, test media upload/playback, playlists, tournament boards,
   timer start/pause/extend, kiosk control, and an agent update.
6. Reconnect USB, select the saved Pi, and run **Prepare for delivery**. Read
   the warning, confirm the correct physical Pi, and type `PREPARE`.
7. Confirm the Pi disappears from the builder device list and that the old
   builder credential can no longer send Wi-Fi or other mutations.

Prepare for delivery removes media, playlist contents, dashboard/tournament
state, timer state, temporary name, every NetworkManager Wi-Fi profile, and
controller trust. It preserves installed software, USB provisioning, the stable
device ID, and the Recovery PIN verifier. If Pi-side reset fails, builder
credentials are retained so the reset can be repaired and retried. If the Pi
reset succeeds but local cleanup reports residue, the Pi is still delivery
ready; remove the reported local residue before reusing the builder laptop.

## Store onboarding

Use the store's shared Windows login:

1. Install or open `PiSignageControl.exe`.
2. Connect the Display Pi with a USB-C **data** cable and click
   **Set up a new Pi**.
3. Enter the 8-digit PIN from the bottom label.
4. Select or type the store Wi-Fi name, enter its password, and click
   **Connect to WiFi**.
5. Confirm the success message shows a Wi-Fi address, unplug USB, and connect
   to the saved Pi. Verify it is the same stable device identity.

After this one-time flow, normal store operations are passwordless on that
Windows login. Do not pair the Pi from a second laptop merely to provide
convenient access: pairing transfers ownership and revokes the first laptop.

## Ownership recovery

If the Controller laptop or its Windows profile is replaced:

1. Install/open the app on the replacement laptop or profile.
2. Connect one Display Pi at a time by USB data cable.
3. Open **Set up a new Pi**, enter that Pi's bottom-label PIN and the store
   Wi-Fi details.
4. Confirm the warning that pairing the replacement will remove the previous
   laptop's access.
5. Confirm the old laptop receives HTTP 401 for a fresh signed mutation and the
   replacement laptop succeeds.
6. Repeat for every Display Pi; each has its own PIN and controller secret.

The PIN does not grant remote ownership by itself. Physical USB access is also
required.

## Remote support

Use attended Windows Quick Assist for normal remote help. The store employee
starts Quick Assist, enters the builder's support code, and explicitly approves
screen sharing/control for that session. End Quick Assist when work is done.
Do not install unattended remote-access software, copy controller secrets, or
pair the builder laptop to a production Pi.

## API summary

| Access | Method | Path | Purpose |
|---|---|---|---|
| Public read/display | GET | `/`, `/dashboard`, `/media/*` | TV content |
| Public read | GET | `/api/status`, `/api/playlist`, `/api/media`, `/api/dashboard`, `/api/wifi/status`, `/api/kiosk` | Current state |
| USB read | GET | `/api/pair/status` | Stable identity and current pairing state |
| USB + PIN bootstrap | POST | `/api/pair` | Establish or replace the Controller laptop |
| Signed control | POST/PUT/DELETE | `/api/dashboard`, `/api/kiosk`, `/api/name`, `/api/playlist`, `/api/media*`, `/api/show-now`, `/api/next`, `/api/update` | Change Display Pi state |
| USB + signed control | POST | `/api/wifi`, `/api/prepare-delivery` | Set store Wi-Fi or remove ownership only over the physical USB link |

The agent advertises `_pisign._tcp.local` over mDNS with its name and port. The
app verifies the stable device ID returned by `/api/status`; names, IP addresses,
DHCP changes, and non-default ports do not define ownership.

## Agent updates

For developers, `deploy-agent.ps1` sends the same complete signed bundle used by
the app. It reads saved device identities and DPAPI-protected credentials from
the current Windows user's Pi Signage data.

```powershell
.\deploy-agent.ps1
.\deploy-agent.ps1 -Hosts 192.168.0.58,pisignage2.local
.\deploy-agent.ps1 -WhatIf
```

Targets without a saved pairing credential are rejected before bundling or
network activity. Updates are limited to 20 MB compressed and uncompressed,
bind the uploaded bytes to the signature, reject unexpected/archive-traversal
paths, compile every Python module before installation, and replace the managed
bundle transactionally with rollback on installation failure. Changes to
`requirements.txt`, Pi setup scripts, systemd units, or the OS still require a
builder-managed deployment.

## Automated verification

```powershell
agent\.venv\Scripts\python.exe -m pytest agent -q
dotnet test PiSignage.slnx
dotnet build PiSignage.slnx -c Release
dotnet list PiSignage.slnx package --vulnerable --include-transitive
```

The Python suite is configured to use test data outside `agent/data`. Treat
real-Pi testing as a release gate in addition to these automated checks.

Automated release evidence recorded on 2026-07-25:

- Python: 127 passed; the runtime `agent/data/dashboard.json` hash and
  timestamp were unchanged. The only warning was the accepted deprecation from
  the pinned Starlette test-client stack.
- .NET: 123 passed.
- Release build: succeeded with zero warnings and zero errors.
- NuGet advisory scan: no known vulnerable direct or transitive packages in
  `signage-core`, `PiSignageControl`, or `signage-core.Tests` from the configured
  package sources. The test project uses xUnit 2.9.3 to avoid the vulnerable
  legacy `NETStandard.Library` dependency chain.
- Real-Pi builder acceptance and store-laptop simulation: **pending hardware
  execution**.

## Real-Pi release acceptance

Do not mark these items passed without a reachable test Pi and observed
results. Record Pi device ID, agent version, test date, operator, laptop/profile,
and evidence links with the release record.

Builder acceptance:

- [ ] Initialize the Pi; photograph the durable label and verify its PIN matches
  the one-time provisioning output.
- [ ] Pair the builder laptop over USB and record the stable device ID.
- [ ] Join builder Wi-Fi and verify `/api/status` reports that same device ID
  over the reported Wi-Fi address.
- [ ] Over Wi-Fi: upload one image and one video, save/play a playlist, capture
  a tournament board, start/pause/extend the timer, toggle kiosk off/on, and
  install an authenticated agent update.
- [ ] Send an unsigned mutation (for example `POST /api/kiosk`) and record HTTP
  401.
- [ ] Replay the exact headers and bytes of one accepted signed request and
  record HTTP 409.
- [ ] On USB, submit five wrong 8-digit PINs and record HTTP 429 with a
  `Retry-After` value no greater than 60 seconds; verify the correct PIN is
  blocked until that interval expires.
- [ ] Run Prepare for delivery over USB and verify media, playlist, dashboard,
  timer, temporary name, all Wi-Fi profiles, and builder control are gone.
- [ ] Verify installed software, USB provisioning, stable device ID, and the
  correct Recovery PIN still work.

Store-laptop simulation (second Windows profile or second laptop):

- [ ] Pair the delivery-ready Pi over USB using its label PIN.
- [ ] Join a different Wi-Fi network and verify the stable device ID is
  unchanged.
- [ ] Close and reopen the app; verify all daily operations work without a
  password prompt.
- [ ] Verify a fresh builder-laptop mutation receives HTTP 401.
- [ ] Pair once more from a third/replacement profile after accepting the
  replacement warning; verify the previous store profile receives HTTP 401 and
  the replacement succeeds.

## Known limits

- URL items use an iframe. Sites that deny framing will not display.
- Videos are muted to satisfy browser autoplay rules.
- USB NCM requires Windows 11's native NCM driver and a data-capable cable.
  RNDIS and serial fallbacks are not implemented.
- Controller authentication provides integrity, ownership, and replay
  protection, not transport confidentiality.
