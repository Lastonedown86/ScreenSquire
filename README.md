# Pi Signage — Phase 1

Digital signage agent for Raspberry Pi with LAN control. The Pi is the source of
truth: playlist and media live on it, so the TV keeps running even when the
control PC is off.

```
agent/            The service (FastAPI) + kiosk page — runs on the Pi
windows-app/      WPF control app (.NET 8) — runs on Windows
pi-setup/         One-shot installer for the real Pi
```

## Windows control app

Requires the .NET 8 SDK (or Visual Studio 2022).

```powershell
cd windows-app
dotnet run                      # develop / test

# ship a single self-contained .exe (no .NET install needed on the client PC):
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# -> bin\Release\net8.0-windows\win-x64\publish\PiSignageControl.exe
```

Testing against the VM agent: start the agent in WSL/VM, then in the app type
the VM's address (e.g. `localhost:8080` for WSL) and click **Connect**. Note:
**Scan network** (mDNS) won't see through WSL2's NAT — that button gets its
real test when the Pi is on your actual WiFi.

The app carries the Pi software inside it. When a connected Pi is out of date,
an **Update Pi software** button appears and updates every reachable Pi.


## Try it today (WSL / Linux VM / any Linux box)

```bash
cd agent
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
python main.py
```

Open **http://localhost:8080/** in a browser — that's the "TV". Fullscreen it (F11).

Then drive it from a second terminal:

```bash
# upload media
curl -F "file=@photo.jpg" http://localhost:8080/api/media
curl -F "file=@clip.mp4"  http://localhost:8080/api/media

# set a playlist (image 8s -> video 20s -> web page 15s, looping)
curl -X PUT http://localhost:8080/api/playlist -H "Content-Type: application/json" -d '{
  "items": [
    {"type": "image", "source": "photo.jpg", "duration": 8},
    {"type": "video", "source": "clip.mp4", "duration": 20},
    {"type": "url", "source": "https://example.com", "duration": 15}
  ]
}'

# interrupt with something urgent for 30s, then resume the playlist
curl -X POST http://localhost:8080/api/show-now -H "Content-Type: application/json" \
  -d '{"type": "url", "source": "https://example.com", "duration": 30}'
```

## API (what the Windows app will call)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/status` | Health, screens connected, what's on now |
| GET/PUT | `/api/playlist` | Read / replace the rotation |
| GET/POST | `/api/media` | List / upload media files |
| DELETE | `/api/media/{name}` | Delete (refuses if in playlist) |
| POST/DELETE | `/api/show-now` | Interrupt override / clear it |
| POST | `/api/next` | Skip to next item |
| POST | `/api/update` | Push a software update (zip of main.py + static) |
| WS | `/ws` | Kiosk page live channel |

Discovery: the agent advertises `_pisign._tcp.local` over mDNS with its name and
port — the Windows app browses for that service type to find Pis on the LAN.

## Install on the real Pi (Raspberry Pi OS **Bookworm, Desktop** image)

**Initial setup** (once per Pi):

```bash
# from your PC:
scp -r pi-signage pi@<pi-address>:~
# on the Pi:
cd ~/pi-signage/pi-setup && bash install.sh && sudo reboot
```

The installer sets up: the agent as a systemd service (auto-restart), Chromium
kiosk on boot (auto-relaunch if it crashes), screen blanking off, desktop
autologin.

**Updates after initial deploy:**

Use `deploy-agent.ps1` (from your Windows PC) to push updates over HTTP to `/api/update`:

```powershell
.\deploy-agent.ps1 -piAddress <pi-address>
```

This updates the agent without SSH. **Note:** Changes to `venv/requirements.txt`
and pre-`/api/update` agents still require manual SSH deployment.

## Pre-imaging a unit for USB WiFi setup (builder)

After running `install.sh` on a fresh image, also run:

    cd ~/pi-signage/pi-setup && bash provision-usb.sh && sudo reboot

The unit now presents a USB network link at `10.55.0.1:8080` when plugged into a
PC. The customer uses the app's **Add a Pi** wizard: plug in the Pi with a USB-C
**data** cable, enter WiFi SSID + password, click **Connect to WiFi**.

## Notes & known limits (phase 1)

- **URL items render in an iframe.** Sites that send `X-Frame-Options: DENY`
  (many login pages, some dashboards) won't display. Phase 2 fix: drive Chromium
  directly via the DevTools protocol so any site works.
- Videos are muted (browser autoplay rules) and loop within their duration.
- No auth on the API yet — fine on a home/office LAN, add a token in phase 2.
  `/api/update` accepts agent code from anyone on the LAN; phase-2 API token
  authentication must cover this endpoint.
- The kiosk page reloads itself nightly at 03:30 to keep Chromium's memory flat.
