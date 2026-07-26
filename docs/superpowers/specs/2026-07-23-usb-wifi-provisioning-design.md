# USB Wi-Fi Provisioning and Ownership — Implemented Design

**Date:** 2026-07-23
**Security lifecycle revision:** 2026-07-25
**Status:** Implemented; real-Pi acceptance remains a release gate

## Context and outcome

Display Pis are prepared and fully tested by the builder, then shipped to a
store where a non-technical employee joins them to store Wi-Fi from the shared
Controller laptop. No SD-card handling or command line is required at the
store.

The out-of-band channel is USB. A pre-provisioned Raspberry Pi 4 presents an
NCM network adapter at `10.55.0.1:8080` over a USB-C data cable. The setup wizard
uses that link to verify physical proximity, pair with the Pi's 8-digit Recovery
PIN, and send Wi-Fi credentials in a signed request. USB and Wi-Fi remain up
together long enough to confirm the joined network and stable device identity.

## Settled security and lifecycle decisions

- Each Display Pi has a random stable device ID and one 8-digit Recovery PIN.
  `provision-usb.sh` initializes them once. Only the PIN verifier is stored; the
  printed PIN cannot be recovered from the Pi.
- The PIN must be printed on a durable bottom-case label. The label is the
  client's recovery credential.
- A Pi trusts exactly one Controller laptop. A successful USB-plus-PIN pairing
  generates a fresh 32-byte secret and invalidates any previous controller.
- Five failed PIN attempts cause a 60-second in-memory pairing block.
- Windows stores each per-Pi secret in a DPAPI CurrentUser vault. The shared
  store login therefore gets passwordless daily operation without making the
  secret portable to another Windows profile.
- Every post-pairing mutation, including Wi-Fi, media, kiosk, tournament,
  playlist, rename, update, and delivery reset, is HMAC signed and uses a
  durable monotonic counter. Pairing is the sole unsigned state change.
- `POST /api/pair`, `GET /api/pair/status`, and
  `POST /api/prepare-delivery` reject callers outside `10.55.0.0/24`.
  Prepare for delivery additionally requires the current controller signature.
- Read/display endpoints remain public. Plain HTTP is confined to the physical
  USB link or the intended store LAN; authentication does not add encryption.
- No insecure or legacy mutation mode exists.

## Architecture

```
BUILDER
  install.sh
  provision-usb.sh -> stable DeviceId + one-time RECOVERY_PIN
  label case
  USB pair builder laptop
  join builder Wi-Fi
  perform full Wi-Fi acceptance
  USB Prepare for delivery

STORE
  Pi --USB-C data cable--> shared Windows login
  detect 10.55.0.1
  GET /api/pair/status
  enter bottom-label PIN
  POST /api/pair
  save secret in DPAPI vault
  USB-only signed POST /api/wifi
  poll public GET /api/wifi/status
  verify /api/status DeviceId
  unplug USB; continue passwordlessly over store Wi-Fi
```

The NCM gadget has fixed Pi address `10.55.0.1/24`; dnsmasq leases the Windows
adapter an address in `10.55.0.10-20`. The agent listens on `0.0.0.0:8080`.
NetworkManager owns `wlan0`; the agent user may execute only
`sudo /usr/bin/nmcli` without a password.

## Builder setup

1. Run `install.sh`, then `provision-usb.sh`.
2. Capture the one-time `RECOVERY_PIN` output and attach the exact PIN to the
   bottom label.
3. Reboot and pair the builder laptop over USB.
4. Join builder Wi-Fi through the wizard.
5. Confirm the stable device ID matches over USB and Wi-Fi.
6. Run the media, tournament, kiosk, timer, and update acceptance suite over
   Wi-Fi.
7. Reconnect USB and choose **Prepare for delivery**.
8. Confirm the Pi is gone from the builder list and its former credential now
   receives 401.

Builder pairing is temporary trust for testing, not permanent client ownership.

## Store onboarding and Ownership recovery

The same wizard handles both flows:

1. Detect the Pi on USB and read its stable identity/current pairing state.
2. Require the bottom-label PIN unless this same Windows controller already has
   the matching credential from a previous Wi-Fi attempt.
3. If another controller is paired, warn that the previous laptop will lose
   access and require explicit confirmation.
4. Pair, validate the returned identities, and persist the secret before
   sending Wi-Fi.
5. Send the signed Wi-Fi request; poll status over USB.
6. Save the Pi by stable device ID with its reported Wi-Fi address.

If Wi-Fi fails after pairing, the wizard retains the new credential and lets
the user correct the Wi-Fi details without replacing ownership again. A
replacement laptop repeats the USB-plus-PIN flow for each Pi. The old laptop's
next mutation fails with 401 because its controller ID/secret is no longer
trusted.

## Wire contracts

```
GET /api/pair/status                    USB only
  { "device_id": "...", "paired": true|false,
    "controller_id": "..."|null }

POST /api/pair                          USB + PIN bootstrap
  { "recovery_pin": "12345678", "controller_id": "..." }
  -> { "device_id": "...", "controller_id": "...",
       "controller_secret": "<base64 32 bytes>" }

POST /api/wifi                          USB-only signed control request
  { "ssid": "ShopWiFi", "password": "..." }
  -> { "ok": true, "connected": true,
       "ip": "192.168.1.42", "error": null }

GET /api/wifi/status                    public read
  { "connected": true, "ssid": "ShopWiFi",
    "ip": "192.168.1.42" }
```

Signed requests include `X-PiSignage-Controller`, `X-PiSignage-Counter`,
`X-PiSignage-Entity-SHA256`, and `X-PiSignage-Signature`. The canonical value
is the controller ID, decimal counter, uppercase method, exact path/query, and
lowercase entity SHA-256 separated by newlines. The Pi durably accepts only a
counter greater than the last accepted counter. Multipart requests defer
counter acceptance until the uploaded bytes match the signed entity hash.

## Prepare for delivery

The Windows action is deliberately destructive and requires:

1. a selected saved Pi with stable identity and local credential;
2. that exact Pi connected over USB;
3. matching USB-reported device/controller identities;
4. the current signed controller credential;
5. warning confirmation plus the literal text `PREPARE`.

The Pi clears media, persists an empty playlist, persists an empty
dashboard/timer, removes the temporary name, deletes every NetworkManager
wireless profile, and clears controller trust strictly last. Installed
software, USB gadget provisioning, device ID, and PIN verifier survive.
Mutations are serialized so pairing or another controller request cannot race
the reset.

If Pi-side reset fails, controller trust remains so the builder can repair and
retry. Only after confirmed Pi success does Windows attempt all local cleanup:
credential, saved device, remembered identity, and thumbnail cache. Local
cleanup errors are aggregated; they do not misreport the already-reset Pi as
unsafe for delivery.

## Failure handling

- USB not found: keep polling, then suggest a data-capable cable and enough boot
  time.
- Wrong PIN: return 401 without revealing which comparison failed.
- Five wrong PINs: return 429 with `Retry-After` during the 60-second block.
- Replacement declined: leave the existing controller untouched.
- Wrong SSID/password or connect timeout: retain the successful pairing and
  offer Wi-Fi retry.
- Identity changes during setup: fail closed without saving the device.
- Malformed NetworkManager enumeration or any profile deletion failure: fail
  Prepare for delivery before clearing controller trust.
- NCM does not enumerate: Windows 11 NCM is the supported v1 path; RNDIS/serial
  remain future fallbacks.

## Verification and pending hardware acceptance

Automated tests cover USB source enforcement, PIN format/throttling, ownership
replacement, stable identity, DPAPI persistence, signed Wi-Fi, replay
protection, cancellation, reset ordering, Wi-Fi profile deletion, and local
cleanup residue.

Release acceptance still requires a real Pi 4 and two Windows profiles/laptops.
Use the checklists in `README.md`; record observed HTTP 401/409/429 results and
the before/after stable device ID. Lack of reachable hardware is a pending
release gate, not an automated-test failure.

## Non-goals

- Multiple simultaneous Controller laptops
- Unattended remote access
- RNDIS or serial fallback
- Captive-portal/hotspot provisioning
- Remote PIN-only ownership transfer
