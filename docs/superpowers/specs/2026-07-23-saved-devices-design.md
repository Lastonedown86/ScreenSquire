# Saved Devices List — Design

**Date:** 2026-07-23
**Status:** Approved design, pre-plan

## Context

The control app connects to a Pi by typing its IP each time, and after the USB
WiFi wizard connects a new Pi there's no record of it — the operator has to find
its IP and retype it (the gap hit during the first real deployment: a Pi joined
WiFi at `192.168.0.58` but nothing in the app showed it).

This adds a **persistent saved-devices dropdown**: each Pi the operator connects
to (or provisions via the wizard) is remembered with an **editable friendly
name**, and selecting it reconnects — self-healing across DHCP IP changes.

## Decisions (from brainstorming)

- **Self-healing on IP change:** store hostname + last IP. Selecting a device
  tries the last IP; on failure it re-resolves the Pi by hostname over mDNS
  (scan → match the Pi whose `/api/status` `name` equals the saved hostname),
  updates the stored IP, and connects.
- **Editable friendly names:** default to the Pi's hostname; the operator can
  rename each ("Front Counter TV"). The dropdown shows the friendly name.
- **Auto-add:** the wizard adds the Pi on success; connecting to any new address
  also saves it.
- **Persistence:** per-PC JSON at `%APPDATA%\PiSignage\devices.json`.

## Non-goals (v1)

- No cloud sync / cross-PC sharing.
- No grouping/folders.
- No health polling of all saved devices (only the connected one, as today).

## Architecture

```
signage-core (net8.0, testable):
  SavedDevice { Name, Hostname, Ip }
  DeviceStore  — load / save / upsert / remove  (JSON at %APPDATA%\PiSignage\devices.json)

WPF (PiSignage.Control):
  MainWindow connect bar:
     [ dropdown: friendly names ]  [Connect] [Rename] [Forget] [Scan] [Add a Pi]
     (raw address box kept for a brand-new/unknown Pi)
  select device -> ConnectToDeviceAsync(dev):
       try dev.Ip
       fail -> mDNS scan -> GET /api/status on each -> match name == dev.Hostname
             -> update dev.Ip -> connect -> DeviceStore.Save
  successful connect to a NEW address -> upsert {name=status.Name, hostname=status.Name, ip}
  WifiSetupWindow success -> upsert the provisioned Pi (its WiFi IP + name) -> Save
     -> MainWindow reloads the store on the wizard's close -> dropdown updates
```

`StatusInfo.Name` (from `/api/status`) is the Pi's hostname (`DEVICE_NAME`), used
both as the default friendly name and as the stable match key for re-resolution.

## Components

### signage-core (new, unit-tested)

- `SavedDevice { string Name; string Hostname; string Ip; }` (mutable; `Name`
  editable).
- `DeviceStore`:
  - `List<SavedDevice> Load()` — read the JSON (empty list if missing/corrupt).
  - `void Save(IEnumerable<SavedDevice> devices)` — atomic write (temp + move).
  - `List<SavedDevice> Upsert(List<SavedDevice> list, SavedDevice dev)` — match by
    `Hostname` (case-insensitive); update Ip/keep Name if present, else add.
    Returns the updated list (pure, testable).
  - Path resolved from `%APPDATA%\PiSignage\devices.json`; the path is injectable
    for tests (pass a file path).

### WPF (MainWindow)

- Replace the plain address `ComboBox` behavior with a devices dropdown bound to
  the loaded `List<SavedDevice>`, `DisplayMemberPath = Name`. Keep it editable so
  a raw `host:port` can still be typed for a new Pi.
- **Connect**: if a saved device is selected → `ConnectToDeviceAsync(dev)`
  (self-heal); if raw text typed → connect as today, then upsert on success.
- **Rename**: prompt for a new friendly name for the selected device → update
  `Name` → `Save` → refresh dropdown.
- **Forget**: remove the selected device → `Save` → refresh.
- **Scan**: merge discovered Pis into the store (upsert by hostname), refresh.
- `ConnectToDeviceAsync(dev)`: reuse the existing `ApiClient` connect path; on
  the initial IP failing, run `MdnsDiscovery.ScanAsync`, `GetStatusAsync` each
  candidate, match `Name == dev.Hostname`, update `dev.Ip`, retry, and `Save`.

### WPF (WifiSetupWindow)

- On successful WiFi connect (already has the Pi's WiFi `Ip` from `WifiStatus`),
  `GET /api/status` over the USB link for the Pi's `Name`, then
  `DeviceStore.Upsert` + `Save` a `SavedDevice { Name=status.Name,
  Hostname=status.Name, Ip=wifiIp }`. The success message already shown stays.
- `MainWindow` reloads the store when the wizard window closes, so the new Pi
  appears in the dropdown immediately.

## Data / persistence

`%APPDATA%\PiSignage\devices.json`:
```json
[
  { "Name": "Front Counter TV", "Hostname": "pisignage1", "Ip": "192.168.0.58" },
  { "Name": "pisignage2",       "Hostname": "pisignage2", "Ip": "192.168.0.71" }
]
```
Atomic write (temp file + move). Corrupt/missing file → empty list (never crash).

## Testing / verification

- **signage-core (xUnit):** `DeviceStore.Save` then `Load` round-trips; `Upsert`
  adds a new device, and on a matching hostname updates the Ip while keeping the
  edited Name; corrupt/missing file yields an empty list.
- **WPF (build + manual):** no unit test for the UI; `dotnet build` is the gate.
  Manual: connect to a Pi → it appears in the dropdown; rename it → persists
  across app restart; wizard-provisioned Pi auto-appears; select a device after
  its IP changed → it re-resolves by hostname and connects.
- **End-to-end (real Pi):** the `pisignage1` unit already on the LAN — connect,
  rename to "Front Counter TV", restart the app, confirm it's remembered and
  reconnects; force a DHCP change (or edit the stored IP wrong) → confirm
  self-heal re-resolves it.
- **Regression:** existing connect/scan/playlist/media flows unchanged.

## Notes

- Self-heal depends on mDNS working on the control PC (same dependency as the
  existing Scan). If mDNS is blocked, the operator re-scans or edits the address
  manually — same fallback as today.
- LAN only; no auth (consistent with the system).
