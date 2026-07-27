# Bundled tools

## vncviewer.exe (TigerVNC) — required for Remote Control

The app's **Remote control** feature launches a bundled TigerVNC viewer to
show and control a paired Pi's screen. The binary **is committed to this repo**
(`vncviewer.exe`, ~23 MB, GPLv2), so a clean checkout builds and releases
without any manual download step. See `THIRD-PARTY.md` at the repository root
for the licence and the corresponding upstream source.

`PiSignageControl.csproj` copies it unconditionally to both the build output and
the publish output, with `TargetPath` flattening away the `tools\` folder —
`RemoteViewerLauncher.BundledViewerPath()` looks for the viewer next to the exe.
**The shipped app is two files side by side, not one exe.** If the viewer is
missing at runtime, the Remote control button shows "Remote viewer is missing
from this install."

To upgrade it:

1. Download the **64-bit Windows** TigerVNC viewer. Binaries are published on
   SourceForge (the GitHub releases page has tags only, no assets):
   https://sourceforge.net/projects/tigervnc/files/stable/
   — pick the version's `vncviewer64-<version>.exe`. Verify the Authenticode
   signature after download (`Get-AuthenticodeSignature`).
2. Rename it to `vncviewer.exe`, replace the copy in this directory, and update
   the recorded version and source link in `THIRD-PARTY.md`.

The viewer must support wayvnc's RSA-AES security type (TigerVNC does). The
app passes per-session credentials via the `VNC_USERNAME` / `VNC_PASSWORD`
environment variables and connects with `SecurityTypes=RA2ne,RA2`.
