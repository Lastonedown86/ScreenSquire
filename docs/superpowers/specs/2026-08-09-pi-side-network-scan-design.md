# Pi-Side Network Scan for the SSID Picker — Implemented Design

**Date:** 2026-08-09
**Status:** Implemented
**Extends:** [USB Wi-Fi Provisioning and Ownership](2026-07-23-usb-wifi-provisioning-design.md)

## Context and outcome

During Store onboarding the setup wizard pre-fills the SSID picker. Previously
the list came from the Controller laptop's own radio (`netsh wlan show
networks`). That is a proxy: the laptop sits at the register while the Display
Pi is mounted behind a TV, and a laptop without a Wi-Fi adapter produced an
empty list. The picker now lists what the **Pi's radio** sees — the radio that
actually has to connect.

## Settled decisions

- **Scan source is the Pi.** New agent endpoint `GET /api/wifi/networks` runs
  `nmcli -t -f SSID,SIGNAL,SECURITY dev wifi list ifname wlan0 --rescan yes`
  (15 s timeout). A forced rescan is used because the Pi is freshly booted
  during onboarding and NetworkManager's cache is often empty; the 2–5 s cost
  hides behind the "Pi found over USB" moment.
- **USB-only exposure.** The endpoint depends on `require_usb`
  (`10.55.0.0/24`). It is read-only and unsigned, matching the trust level of
  the other USB-gated setup calls; keeping it off the store LAN avoids leaking
  neighboring SSIDs and rules out production rescans disturbing `wlan0`.
- **The agent does the cleanup.** Rows are deduped by SSID keeping the max
  signal (bands/APs collapse to one row), hidden networks (empty SSID) are
  dropped, and the result is sorted by signal descending, so the store's own
  router is normally the first row. Payload:
  `{"networks": [{"ssid", "signal", "security"}]}` — signal and security ride
  along now so future UI (weak-signal graying, open-network warning) needs no
  agent redeploy. `nmcli -t` escapes `:` inside SSIDs as `\:`; the parser
  splits on unescaped colons and unescapes.
- **Scan failure is an error, not an empty list.** A failed `nmcli` returns
  502 so the app can distinguish "nothing heard" from "scan broke" and fall
  back.
- **App falls back to the laptop scan.** `WifiProvisioner.ListNetworksAsync`
  (signage-core, testable) is tried first; any failure — including 404 from an
  agent older than 2026.08.09.1 — falls back to the existing `netsh` path in
  `WifiSetupWindow`. Behavior never degrades below the previous release, so
  agent and app ship in one PR with no rollout ordering.
- **No rescan button in v1.** The combo box stays editable (hidden SSIDs are
  typed by hand) and the Retry detection flow re-runs the scan.
