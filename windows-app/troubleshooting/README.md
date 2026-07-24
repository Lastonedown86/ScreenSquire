# Troubleshooting

## Bluetooth mouse/keyboard drops out

**Symptom:** a Bluetooth mouse or keyboard on the control PC disconnects
intermittently — often while the control app is busy talking to a Pi over Wi-Fi.

**Cause:** many laptops/mini-PCs use a **combo Wi-Fi + Bluetooth card** (e.g.
Realtek 8852BE) where both radios share one chip. When Windows power-saves the
Wi-Fi adapter, it can hiccup the Bluetooth radio on the same chip → the mouse/
keyboard drops. It is **not** caused by the Pi Signage app; the app just makes
Wi-Fi busy enough to expose it. Most PCs never hit this.

**Fix:** run [`fix-bt-drops.ps1`](fix-bt-drops.ps1) — it disables the relevant
power-saving:

1. Right-click `fix-bt-drops.ps1` → **Run with PowerShell** (it self-elevates —
   approve the UAC prompt), **or** in a terminal:
   ```
   powershell -ExecutionPolicy Bypass -File fix-bt-drops.ps1
   ```
2. It disables: Wi-Fi adapter power-off, USB selective suspend, and the
   Bluetooth radio's power-save.
3. Toggle the mouse/keyboard off/on once if it's still asleep.

**If drops persist:** update the Wi-Fi/Bluetooth driver (the combo-card BT
firmware/driver is usually the real fix). Vendor driver > Windows-default.

> This changes local Windows power settings only. It ships nothing to the Pis
> and does not modify the app. Only run it on a PC that actually shows the
> problem.
