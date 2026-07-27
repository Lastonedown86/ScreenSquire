# Third-party components

ScreenSquire itself is MIT licensed (see `LICENSE`). It redistributes the
following component under a different licence.

## TigerVNC viewer — `vncviewer.exe`

- **Licence:** GNU General Public License, version 2
- **Where:** committed at `windows-app/tools/vncviewer.exe`, copied next to
  `PiSignageControl.exe` at build and publish, and attached as a separate asset
  to every GitHub Release.
- **Modified:** no. The binary is redistributed exactly as published upstream.
- **Upstream project:** https://github.com/TigerVNC/tigervnc
- **Binary obtained from:** https://sourceforge.net/projects/tigervnc/files/stable/
- **Corresponding source:** the matching `tigervnc-<version>.tar.gz` in the same
  SourceForge `stable` directory, and the matching tag at
  https://github.com/TigerVNC/tigervnc/tags

The viewer is invoked as a **separate process** — ScreenSquire neither links
against it nor derives from it, so this is mere aggregation under GPLv2 §2.
ScreenSquire's own MIT licence is unaffected.

Anyone who receives `vncviewer.exe` from a ScreenSquire release is entitled to
the corresponding source for that binary under GPLv2 §3; the links above satisfy
that offer. If an upstream link ever goes dead, open an issue and a copy of the
source tarball will be attached to the release.

The app passes per-session credentials to the viewer via the `VNC_USERNAME` and
`VNC_PASSWORD` environment variables and connects with `SecurityTypes=RA2ne,RA2`
(wayvnc's RSA-AES security type). See `windows-app/tools/README.md` for how to
upgrade the bundled copy.
