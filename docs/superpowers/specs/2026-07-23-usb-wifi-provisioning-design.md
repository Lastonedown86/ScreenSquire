# USB WiFi Provisioning Wizard — Design

**Date:** 2026-07-23
**Status:** Approved design, pre-plan

## Context

Pi Signage units (Raspberry Pi 4) are **pre-imaged by the builder** and shipped
to customers (shops). A non-technical customer must get a brand-new unit onto
**their own WiFi** with no SD-card handling and no command line.

The blocker: WiFi cannot be configured over the network, because the unit isn't
on any network yet. The out-of-band channel is **USB**: the Pi 4's USB-C port,
put in **USB gadget mode** (baked into the pre-image), presents the Pi to the
customer's Windows PC as a **USB network adapter**. The control app then talks
HTTP to the agent over that cable and hands it the customer's WiFi credentials.

Outcome: customer plugs the Pi into their PC with a USB-C data cable, opens the
app's **Add-a-Pi wizard**, types their WiFi **SSID + password**, clicks **Connect
to WiFi**, and the wizard **confirms the Pi joined** (and shows its new WiFi
address). They unplug it, and it's on the network.

## Decisions (settled in brainstorming)

- **USB link:** NCM ethernet gadget (`g_ncm` / configfs NCM). Windows 11 has a
  native NCM class driver — no driver install. The Pi gets a fixed USB IP; the
  app talks plain HTTP to the existing agent over USB. (Fallbacks, not built now:
  RNDIS — deprecated/flaky on Win11; USB serial — bulletproof driver but needs a
  custom protocol.)
- **Credential entry:** manual SSID + password (no nearby-network scan in v1).
- **WiFi backend:** `nmcli` (Raspberry Pi OS Bookworm uses NetworkManager).
- **No hardware to buy:** gadget mode is a software feature of the USB-C port;
  only a USB-C **data** cable is needed. On a PC port the Pi runs on limited
  power — fine for the brief setup step (no TV attached yet).
- **Security:** the WiFi password travels only over the local USB link (POST
  body) and is written to a root-owned NetworkManager profile on the Pi. Nothing
  leaves the machine. Agent stays unauthenticated (USB/LAN only), consistent with
  the rest of the system.

## Non-goals (v1)

- No nearby-SSID scan/pick (manual entry only).
- No RNDIS or serial fallback implementation (documented, not built).
- No device naming/persistent saved-device list (separate follow-on).
- No captive portal / hotspot method.

## Architecture

```
BUILDER (pre-image, once per unit):
  bake into the SD image:
    • USB NCM gadget on boot  (dwc2 + configfs NCM, fixed usb0 IP 10.55.0.1/24,
      tiny dnsmasq handing the PC 10.55.0.10-20)
    • agent already listens on 0.0.0.0:8080 -> reachable over usb0
    • agent WiFi endpoints (/api/wifi, /api/wifi/status)
    • sudoers: pi may run nmcli without password

CUSTOMER:
  Pi 4 --USB-C data cable--> Windows 11 PC   (PC powers Pi, carries data)
        Pi enumerates as a USB network adapter (NCM, native driver)

  App "Add a Pi" wizard:
    1. detect Pi over USB   ->  GET http://10.55.0.1:8080/api/status
    2. enter SSID + password
    3. "Connect to WiFi"    ->  POST http://10.55.0.1:8080/api/wifi {ssid,password}
    4. confirm              ->  poll GET /api/wifi/status until connected/failed
    5. show success + the Pi's new WiFi IP  (or failure reason)
```

The USB link and WiFi coexist (`usb0` + `wlan0`), so the wizard keeps talking to
the Pi over USB to confirm `wlan0` obtained an IP.

## Components

### 1. Pre-image provisioning (builder, baked into the SD image)

Added to `pi-setup/` as a provisioning step (script + config files applied when
preparing an image). Establishes:

- **USB gadget (NCM):** `dtoverlay=dwc2` in `config.txt`; a `systemd` unit that,
  at boot, sets up a configfs USB NCM gadget and assigns `usb0` a static
  `10.55.0.1/24`.
- **DHCP for the PC:** a minimal `dnsmasq` (or `NetworkManager` shared mode) on
  `usb0` handing the PC an address in `10.55.0.10-20` so the PC's USB adapter
  auto-configures.
- **nmcli permission:** a sudoers drop-in so the agent (running as the `pi`
  user) may run `sudo nmcli ...` non-interactively.
- The agent service already binds `0.0.0.0:8080`, so it is reachable at
  `10.55.0.1:8080` over USB with no rebind.

### 2. Agent — WiFi endpoints (`agent/main.py`)

- `POST /api/wifi` — body `{ "ssid": str, "password": str }`. Runs
  `sudo nmcli dev wifi connect "<ssid>" password "<pwd>" ifname wlan0` via an
  async subprocess with a server-side timeout (~30s). Returns
  `{ "ok": bool, "connected": bool, "ip": str|null, "error": str|null }`.
  Never logs the password.
- `GET /api/wifi/status` — returns the current WiFi state, parsed from
  `nmcli -t -f GENERAL.STATE,IP4.ADDRESS dev show wlan0` (and the active SSID):
  `{ "connected": bool, "ssid": str|null, "ip": str|null }`. Used by the wizard
  to poll for confirmation and to read the Pi's LAN address after joining.

Both shell out through a single helper that runs a command with a timeout and
returns (rc, stdout, stderr); the password is passed as an argument, never
written to disk by the agent (NetworkManager owns the profile, root-only).

### 3. Windows app — Add-a-Pi wizard (`windows-app/WifiSetupWindow.xaml` + `.cs`)

A step-through window launched from MainWindow ("Add a Pi"):

1. **Detect** — poll `GET http://10.55.0.1:8080/api/status` until the Pi answers
   (spinner + "Plug the Pi into this PC with a USB-C cable…"). Timeout → guidance
   (check cable is a *data* cable, wait for boot).
2. **Enter WiFi** — `SSID` textbox + `Password` box (show/hide toggle) + a
   **Connect to WiFi** button (disabled until both filled).
3. **Connect** — on click: spinner + "Connecting the Pi to <ssid>…"; `POST
   /api/wifi`; then poll `GET /api/wifi/status` up to ~40s.
4. **Result** — success: green "Connected — this Pi is on <ssid> at <ip>. You can
   unplug the USB cable." Failure: red reason (e.g. wrong password / network not
   found) + **Try again** (back to step 2, credentials retained).

Reuses the existing `HttpClient` pattern. WiFi calls live in a small
`WifiProvisioner` helper in `signage-core` (net8.0, testable): `DetectAsync`,
`ConnectAsync(ssid, password)`, `GetStatusAsync`, each against a base URL, so the
logic is unit-testable and the window stays thin.

## Wire contracts

```
POST /api/wifi
  req:  { "ssid": "ShopWiFi", "password": "hunter2" }
  resp: { "ok": true, "connected": true, "ip": "192.168.1.42", "error": null }
     or { "ok": false, "connected": false, "ip": null, "error": "Secrets were required, but not provided" }

GET /api/wifi/status
  resp: { "connected": true, "ssid": "ShopWiFi", "ip": "192.168.1.42" }
     or { "connected": false, "ssid": null, "ip": null }
```

## Failure handling

- **USB not detected:** wizard keeps polling with clear guidance; the top
  suspect is a charge-only cable — say so explicitly.
- **Wrong password / SSID not found:** surfaced from `nmcli` stderr as a plain
  message; **Try again** keeps the entered SSID.
- **Connect timeout:** report "couldn't confirm within 40s" and offer retry;
  `nmcli` call is bounded server-side so the agent never hangs.
- **NCM driver doesn't enumerate (older Windows):** documented limitation;
  fallback options (serial/RNDIS) noted for a future iteration.

## Security

- Password handled only over the local USB link; never logged by the agent;
  stored solely in NetworkManager's root-owned system-connection (0600).
- No external transmission. Agent remains unauthenticated by design (physical
  USB / LAN only), consistent with the existing system.

## Testing / verification

- **Agent (pytest):** `/api/wifi` and `/api/wifi/status` with the `nmcli` call
  mocked (monkeypatch the subprocess helper) — asserts success maps to
  `connected/ip`, a non-zero `nmcli` maps to `ok:false` + `error`, and the
  password never appears in logs. A guarded live test (skipped unless a real
  wlan is present) is out of scope for CI.
- **signage-core (xUnit):** `WifiProvisioner` against a stub HTTP server —
  `ConnectAsync` posts the right body, `GetStatusAsync` parses the response,
  `DetectAsync` returns true only on a 200.
- **End-to-end (manual, real Pi 4):** pre-image a unit, plug USB-C into a Win11
  PC, run the wizard, enter real WiFi, confirm the Pi joins and reports its LAN
  IP; then unplug and reach it over LAN via the normal Scan/Connect.
- **Regression:** existing agent endpoints, dashboard, kiosk unchanged.

## Follow-ons (not in this spec)

- Nearby-SSID scan (`nmcli dev wifi list`) → pick from a dropdown.
- Persistent named saved-device list (reconnect by name; wraps this wizard).
- Serial-gadget fallback for PCs where NCM won't enumerate.
