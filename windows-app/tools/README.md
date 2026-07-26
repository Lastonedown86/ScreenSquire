# Bundled tools

## vncviewer.exe (TigerVNC) — required for Remote Control

The app's **Remote control** feature launches a bundled TigerVNC viewer to
show and control a paired Pi's screen. The binary is **not** committed to the
repo (it is GPLv2 and ~2 MB); supply it here before building a release.

1. Download the **64-bit Windows** TigerVNC viewer from the official releases:
   https://github.com/TigerVNC/tigervnc/releases (asset `vncviewer64-*.exe`).
2. Rename it to `vncviewer.exe` and place it in this directory
   (`windows-app/tools/vncviewer.exe`).
3. Build normally. `PiSignageControl.csproj` copies it to the app output only
   when present (`Condition="Exists('tools\vncviewer.exe')"`), so the project
   still builds without it — but the Remote control button will show
   "Remote viewer is missing from this install." until it is added.

The viewer must support wayvnc's RSA-AES security type (TigerVNC does). The
app passes per-session credentials via the `VNC_USERNAME` / `VNC_PASSWORD`
environment variables and connects with `SecurityTypes=RA2ne,RA2`.
