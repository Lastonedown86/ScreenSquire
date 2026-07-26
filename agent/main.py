"""
Pi Signage Agent
================
Runs on the Raspberry Pi (or any Linux box / WSL for development).

- Serves the kiosk page that Chromium displays fullscreen
- REST API for the Windows control app (playlist, media upload, show-now)
- WebSocket push so the kiosk updates instantly
- mDNS advertisement (_pisign._tcp.local) so the Windows app can discover it
- Scheduler that decides what is on screen at any moment

Run for development:
    uvicorn main:app --host 0.0.0.0 --port 8080
"""

import asyncio
import base64
import hashlib
import ipaddress
import io
import json
import logging
import mimetypes
import os
import py_compile
import re
import secrets
import shutil
import socket
import stat
import tempfile
import threading
import time
import uuid
import zipfile
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Literal, Optional
from urllib.parse import parse_qs, urlparse

from fastapi import Depends, FastAPI, HTTPException, Request, UploadFile, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field

from control_auth import (
    VerifiedControl,
    accept_uploaded_counter,
    require_control,
    verify_uploaded_entity,
)
from delivery_reset import prepare_for_delivery
from trust import PairingBlocked, TrustStore

log = logging.getLogger("signage")
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

# ---------------------------------------------------------------- paths / config
APP_DIR = Path(__file__).resolve().parent
DATA_DIR = Path(os.environ.get("SIGNAGE_DATA", APP_DIR / "data"))
MEDIA_DIR = DATA_DIR / "media"
PLAYLIST_FILE = DATA_DIR / "playlist.json"
PORT = int(os.environ.get("SIGNAGE_PORT", "8080"))
NAME_FILE = DATA_DIR / "name.txt"
TRUST_FILE = DATA_DIR / "trust.json"
AGENT_VERSION = "2026.07.26.6"  # bump on every agent change; the app compares this


def _load_name() -> str:
    # a rename from the control app (name.txt) wins over env/hostname
    try:
        if NAME_FILE.exists():
            n = NAME_FILE.read_text().strip()
            if n:
                return n
    except Exception:
        log.exception("Couldn't read name.txt, falling back")
    return os.environ.get("SIGNAGE_NAME", socket.gethostname())


DEVICE_NAME = _load_name()

MEDIA_DIR.mkdir(parents=True, exist_ok=True)

ALLOWED_UPLOAD_EXT = {
    ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp",   # images
    ".mp4", ".webm", ".mov", ".mkv",                     # video
}


def _plain_media_name(name: str) -> str:
    """Return one media-directory filename, rejecting paths and special names."""
    if (
        not name
        or name in (".", "..")
        or len(name) > 255
        or "/" in name
        or "\\" in name
        or "\0" in name
        or Path(name).name != name
    ):
        raise HTTPException(400, "Invalid media name")
    return name


def _media_path(name: str) -> Path:
    safe_name = _plain_media_name(name)
    root = os.path.realpath(MEDIA_DIR)
    candidate = os.path.realpath(os.path.join(root, safe_name))
    if not candidate.startswith(root + os.sep) or os.path.dirname(candidate) != root:
        raise HTTPException(400, "Invalid media name")
    return Path(candidate)


# ---------------------------------------------------------------- models
# "youtube": source is a bare 11-char video id, rendered by the kiosk with the
# YouTube IFrame Player API (sound on, controllable) instead of a muted embed.
# "spotify": source is a spotify:{type}:{id} URI, rendered with the Spotify
# Embed iFrame API (play/pause/seek; the Embed API has no volume control).
ItemType = Literal["image", "video", "url", "youtube", "spotify"]


class PlaylistItem(BaseModel):
    id: str = Field(default_factory=lambda: uuid.uuid4().hex[:8])
    type: ItemType
    # for image/video: filename inside media dir; for url: the full URL
    source: str
    duration: int = Field(default=10, ge=1, le=86400)  # seconds on screen
    name: Optional[str] = None  # friendly label shown in the control app


class Playlist(BaseModel):
    items: list[PlaylistItem] = []
    enabled: bool = True


class ShowNowRequest(BaseModel):
    type: ItemType
    source: str
    duration: Optional[int] = Field(default=None, ge=1, le=86400)  # None = until cleared
    volume: Optional[int] = Field(default=None, ge=0, le=100)  # youtube only


# ---------------------------------------------------------------- state
class State:
    def __init__(self) -> None:
        self.playlist: Playlist = self._load_playlist()
        self.index: int = 0                      # current position in playlist
        self.override: Optional[ShowNowRequest] = None
        self.override_until: Optional[float] = None
        # stable id for the current override, so re-broadcasts after unrelated
        # bumps don't look like a new item to the kiosk (which would reload it)
        self.override_id: Optional[str] = None
        self.version: int = 0                    # bumped on any change -> wakes scheduler
        self.wake = asyncio.Event()

    def _load_playlist(self) -> Playlist:
        if PLAYLIST_FILE.exists():
            try:
                return Playlist.model_validate_json(PLAYLIST_FILE.read_text())
            except Exception:
                log.exception("Corrupt playlist.json, starting empty")
        return Playlist()

    def save_playlist(self) -> None:
        tmp = PLAYLIST_FILE.with_suffix(".tmp")
        tmp.write_text(self.playlist.model_dump_json(indent=2))
        tmp.replace(PLAYLIST_FILE)  # atomic — protects against power loss mid-write

    def bump(self) -> None:
        self.version += 1
        self.wake.set()


state = State()
trust_store = TrustStore(TRUST_FILE)

# ---------------------------------------------------------------- websocket hub
class Hub:
    """Tracks connected kiosk pages and pushes the current display item to them."""

    def __init__(self) -> None:
        self.clients: set[WebSocket] = set()
        self.current: dict = {"type": "idle"}

    async def register(self, ws: WebSocket) -> None:
        await ws.accept()
        self.clients.add(ws)
        await ws.send_json(self.current)  # catch new screens up immediately

    def unregister(self, ws: WebSocket) -> None:
        self.clients.discard(ws)

    async def show(self, payload: dict) -> None:
        self.current = payload
        await self.broadcast(payload)

    async def broadcast(self, payload: dict) -> None:
        """Send without storing — control frames must never become `current`,
        or a reconnecting kiosk would be caught up with one instead of content."""
        dead = []
        for ws in list(self.clients):  # copy: a client may register mid-broadcast
            try:
                await ws.send_json(payload)
            except Exception:
                dead.append(ws)
        for ws in dead:
            self.unregister(ws)


hub = Hub()

# ---------------------------------------------------------------- scheduler
# YouTube watch/short links refuse to load inside an iframe (X-Frame-Options),
# so the kiosk shows black. Rewrite them to the embed player, which allows it.
_YOUTUBE_HOSTS = {"youtube.com", "www.youtube.com", "m.youtube.com"}
_YOUTUBE_ID_CHARS = frozenset(
    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-"
)
_MAX_NORMALIZED_URL_LENGTH = 4096


def _youtube_id(value: str) -> Optional[str]:
    if len(value) == 11 and all(char in _YOUTUBE_ID_CHARS for char in value):
        return value
    return None


_SPOTIFY_TYPES = {"track", "album", "playlist", "episode", "show", "artist"}


def _spotify_uri(value: str) -> Optional[str]:
    """Validate a canonical spotify:{type}:{id} URI (22 base62 chars)."""
    parts = value.split(":")
    if (
        len(parts) == 3
        and parts[0] == "spotify"
        and parts[1] in _SPOTIFY_TYPES
        and len(parts[2]) == 22
        and parts[2].isascii()
        and parts[2].isalnum()
    ):
        return value
    return None


def _normalize_url(src: str) -> str:
    if len(src) > _MAX_NORMALIZED_URL_LENGTH:
        return src
    try:
        parsed = urlparse(src)
    except ValueError:
        return src
    if parsed.scheme not in ("http", "https"):
        return src

    host = (parsed.hostname or "").lower()
    vid = None
    if host == "youtu.be":
        vid = _youtube_id(parsed.path.removeprefix("/").split("/", 1)[0])
    elif host in _YOUTUBE_HOSTS:
        parts = parsed.path.strip("/").split("/")
        if parsed.path == "/watch":
            candidates = parse_qs(parsed.query).get("v", [])
            if len(candidates) == 1:
                vid = _youtube_id(candidates[0])
        elif len(parts) == 2 and parts[0] in ("shorts", "live"):
            vid = _youtube_id(parts[1])
    if vid is None:
        return src
    # mute: Chromium only autoplays muted video without a user gesture
    return (f"https://www.youtube.com/embed/{vid}"
            f"?autoplay=1&mute=1&controls=0&loop=1&playlist={vid}")


def _item_payload(item: PlaylistItem) -> dict:
    if item.type == "youtube":
        return {"type": "youtube", "videoId": item.source, "id": item.id}
    if item.type == "spotify":
        return {"type": "spotify", "uri": item.source, "id": item.id}
    if item.type == "url":
        return {"type": "url", "src": _normalize_url(item.source), "id": item.id}
    return {"type": item.type, "src": f"/media/{item.source}", "id": item.id}


async def scheduler() -> None:
    """Decides what's on screen. Re-evaluates when woken (playlist change,
    show-now) or when the current item's duration elapses."""
    loop = asyncio.get_event_loop()
    while True:
        state.wake.clear()
        now = loop.time()

        # 1) active override?
        if state.override is not None:
            if state.override_until is not None and now >= state.override_until:
                state.override = None
                state.override_until = None
            else:
                ov = state.override
                item = PlaylistItem(type=ov.type, source=ov.source)
                if state.override_id:
                    item.id = state.override_id
                payload = _item_payload(item)
                if ov.type == "youtube" and ov.volume is not None:
                    payload["volume"] = ov.volume
                await hub.show(payload)
                timeout = (state.override_until - now) if state.override_until else None
                await _sleep_or_wake(timeout)
                continue

        # 2) normal playlist rotation
        items = state.playlist.items if state.playlist.enabled else []
        if not items:
            await hub.show({"type": "idle", "name": DEVICE_NAME})
            await _sleep_or_wake(None)
            continue

        state.index %= len(items)
        item = items[state.index]
        await hub.show(_item_payload(item))
        woke_early = await _sleep_or_wake(item.duration)
        if not woke_early:
            state.index = (state.index + 1) % max(len(state.playlist.items), 1)


async def _sleep_or_wake(timeout: Optional[float]) -> bool:
    """Sleep up to `timeout` seconds (None = forever). True if woken early."""
    try:
        await asyncio.wait_for(state.wake.wait(), timeout=timeout)
        return True
    except asyncio.TimeoutError:
        return False


# ---------------------------------------------------------------- mDNS
def register_mdns():
    try:
        from zeroconf import ServiceInfo, Zeroconf
    except ImportError:
        log.warning("zeroconf not installed — discovery disabled")
        return None
    try:
        ip = _primary_ip()
        info = ServiceInfo(
            "_pisign._tcp.local.",
            f"{DEVICE_NAME}._pisign._tcp.local.",
            addresses=[socket.inet_aton(ip)],
            port=PORT,
            properties={"name": DEVICE_NAME, "api": "1"},
        )
        zc = Zeroconf()
        zc.register_service(info)
        log.info("mDNS registered as %s at %s:%s", DEVICE_NAME, ip, PORT)
        return zc
    except Exception:
        log.exception("mDNS registration failed (fine in some VMs)")
        return None


def _primary_ip() -> str:
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("8.8.8.8", 80))  # no packets sent; just picks the route
        return s.getsockname()[0]
    except Exception:
        return "127.0.0.1"
    finally:
        s.close()


# ---------------------------------------------------------------- app
@asynccontextmanager
async def lifespan(app: FastAPI):
    # heal the kiosk launcher before anything else: after a pushed update this
    # is what applies new Chromium flags to already-provisioned Pis
    await _sync_kiosk_script()
    task = asyncio.create_task(scheduler())
    # mDNS can block on odd networks (containers, some VMs) — never let it
    # delay startup; run it in a worker thread and keep the handle for shutdown
    zc_holder: list = []

    async def _mdns_bg():
        zc = await asyncio.to_thread(register_mdns)
        if zc:
            zc_holder.append(zc)

    mdns_task = asyncio.create_task(_mdns_bg())
    yield
    task.cancel()
    mdns_task.cancel()
    for zc in zc_holder:
        try:
            zc.close()
        except Exception:
            pass
    await _stop_wayvnc()


app = FastAPI(title="Pi Signage Agent", version="0.1.0", lifespan=lifespan)
app.state.trust_store = trust_store


USB_NET = ipaddress.ip_network("10.55.0.0/24")


class MutationGate:
    """Async context gate that is safe across request event loops."""

    def __init__(self) -> None:
        self._lock = threading.Lock()

    async def __aenter__(self):
        while not self._lock.acquire(blocking=False):
            # Never block the event-loop thread. Cancellation while waiting
            # exits here without owning (and therefore without leaking) the lock.
            await asyncio.sleep(0.01)
        return self

    async def __aexit__(self, exc_type, exc, traceback) -> None:
        self._lock.release()


mutation_gate = MutationGate()


def require_usb(request: Request) -> None:
    try:
        if request.client is None:
            raise ValueError
        address = ipaddress.ip_address(request.client.host)
    except ValueError:
        raise HTTPException(403, "USB connection required") from None
    if address not in USB_NET:
        raise HTTPException(403, "USB connection required")


async def require_control_mutation(request: Request):
    """Hold the shared mutation gate from authentication through the response."""
    async with mutation_gate:
        yield await require_control(request)


async def require_usb_mutation(request: Request):
    """Hold the mutation gate for an unsigned USB-only mutation."""
    require_usb(request)
    async with mutation_gate:
        yield


async def require_usb_control_mutation(request: Request):
    """Check USB first, then hold the gate through signed reset completion."""
    require_usb(request)
    async with mutation_gate:
        yield await require_control(request)


class PairRequest(BaseModel):
    recovery_pin: str = Field(pattern=r"^\d{8}$")
    controller_id: str = Field(min_length=16, max_length=64)


@app.post("/api/pair")
async def pair_controller(
    req: PairRequest,
    _mutation: None = Depends(require_usb_mutation),
):
    try:
        result = trust_store.pair(req.recovery_pin, req.controller_id)
    except ValueError:
        raise HTTPException(401, "Invalid recovery PIN") from None
    except PairingBlocked as exc:
        raise HTTPException(
            429,
            "Pairing temporarily blocked",
            headers={"Retry-After": str(exc.retry_after)},
        ) from None
    return {
        "device_id": trust_store.device_id,
        "controller_id": req.controller_id,
        "controller_secret": base64.b64encode(result.secret).decode("ascii"),
    }


@app.get("/api/pair/status")
async def pair_status(request: Request):
    require_usb(request)
    controller_id = trust_store.controller_id
    return {
        "device_id": trust_store.device_id,
        "paired": controller_id is not None,
        "controller_id": controller_id,
    }


def _set_delivery_dashboard(value: dict) -> None:
    global _dashboard
    _dashboard = value


def _set_delivery_device_name(value: str) -> None:
    global DEVICE_NAME
    DEVICE_NAME = value


@app.post("/api/prepare-delivery")
async def prepare_delivery(
    _control: VerifiedControl = Depends(require_usb_control_mutation),
):
    await prepare_for_delivery(
        media_dir=MEDIA_DIR,
        playlist_file=PLAYLIST_FILE,
        name_file=NAME_FILE,
        dashboard_file=DASHBOARD_FILE,
        state=state,
        empty_playlist=Playlist(items=[], enabled=True),
        set_dashboard=_set_delivery_dashboard,
        set_device_name=_set_delivery_device_name,
        run=_run,
        clear_controller=trust_store.clear_controller,
    )
    return {"ok": True, "device_id": trust_store.device_id}


# ---- kiosk page + media ----
@app.get("/")
async def kiosk_page():
    # no-store: the kiosk must always pick up page updates on reload
    return FileResponse(APP_DIR / "static" / "kiosk.html",
                        headers={"Cache-Control": "no-store"})


app.mount("/media", StaticFiles(directory=MEDIA_DIR), name="media")

# ---- screenshot dashboard (boards + live timer) ----
DASHBOARD_FILE = DATA_DIR / "dashboard.json"


class TimerState(BaseModel):
    state: Literal["running", "paused", "stopped"] = "stopped"
    endsAt: Optional[int] = None       # epoch ms, stamped by the agent when running
    remaining: Optional[int] = None    # seconds
    round: Optional[int] = None
    label: Optional[str] = None


class DashboardPayload(BaseModel):
    view_data: dict = {}
    timer: TimerState = TimerState()


def _load_dashboard() -> dict:
    if DASHBOARD_FILE.exists():
        try:
            return json.loads(DASHBOARD_FILE.read_text())
        except Exception:
            log.exception("Corrupt dashboard.json, starting empty")
    return {"view_data": {"boards": {}}, "timer": {"state": "stopped"}}


_dashboard: dict = _load_dashboard()


@app.post("/api/dashboard", dependencies=[Depends(require_control_mutation)])
async def set_dashboard(payload: DashboardPayload):
    global _dashboard
    prev = _dashboard or {}
    prev_timer = prev.get("timer") or {}
    d = payload.model_dump()

    # Merge boards: a partial push (e.g. only "pairings") must not wipe others.
    prev_boards = (prev.get("view_data") or {}).get("boards") or {}
    new_boards = (d.get("view_data") or {}).get("boards") or {}
    d.setdefault("view_data", {})["boards"] = {**prev_boards, **new_boards}

    t = d.get("timer") or {}
    if t.get("state") == "running" and t.get("remaining") is not None:
        now_ms = int(time.time() * 1000)
        # Preserve the countdown across re-posts of the SAME still-running timer
        # (e.g. a board push mid-round). Re-anchor on a genuine start/restart —
        # a changed round/remaining, OR a stored endsAt that has already expired
        # (a stale clock must not "resume" as instant TIME).
        same_timer = (
            prev_timer.get("state") == "running"
            and prev_timer.get("endsAt") is not None
            and prev_timer["endsAt"] > now_ms            # only keep a clock still in the future
            and prev_timer.get("round") == t.get("round")
            and prev_timer.get("remaining") == t.get("remaining")
        )
        t["endsAt"] = prev_timer["endsAt"] if same_timer else now_ms + int(t["remaining"]) * 1000
    d["timer"] = t

    _dashboard = d
    tmp = DASHBOARD_FILE.with_suffix(".tmp")
    tmp.write_text(json.dumps(d, indent=2))
    tmp.replace(DASHBOARD_FILE)  # atomic
    return {"ok": True}


@app.get("/api/dashboard")
async def get_dashboard():
    return _dashboard


@app.get("/dashboard")
async def dashboard_page():
    return FileResponse(APP_DIR / "static" / "dashboard.html",
                        headers={"Cache-Control": "no-store"})


# ---- WiFi provisioning (USB setup) ----
class WifiRequest(BaseModel):
    ssid: str
    password: str


async def _run(cmd: list[str], timeout: float = 30.0) -> tuple[int, str, str]:
    """Run a command with a timeout. Returns (returncode, stdout, stderr)."""
    proc = await asyncio.create_subprocess_exec(
        *cmd, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    try:
        out, err = await asyncio.wait_for(proc.communicate(), timeout=timeout)
    except asyncio.TimeoutError:
        proc.kill()
        await proc.wait()
        return 124, "", "timed out"
    return proc.returncode or 0, out.decode(errors="replace"), err.decode(errors="replace")


async def _wlan_ip() -> Optional[str]:
    _, out, _ = await _run(["nmcli", "-t", "-f", "IP4.ADDRESS", "dev", "show", "wlan0"], timeout=5)
    for line in out.splitlines():
        if ":" in line:
            val = line.split(":", 1)[1].strip()
            if val:
                return val.split("/")[0]
    return None


async def _wlan_ssid() -> Optional[str]:
    _, out, _ = await _run(["nmcli", "-t", "-f", "GENERAL.CONNECTION", "dev", "show", "wlan0"], timeout=5)
    for line in out.splitlines():
        if line.startswith("GENERAL.CONNECTION:"):
            v = line.split(":", 1)[1].strip()
            return v if v and v != "--" else None
    return None


@app.post("/api/wifi", dependencies=[Depends(require_usb_control_mutation)])
async def set_wifi(req: WifiRequest):
    rc, out, err = await _run(
        ["sudo", "nmcli", "dev", "wifi", "connect", req.ssid,
         "password", req.password, "ifname", "wlan0"],
        timeout=30.0,
    )
    if rc != 0:
        # never echo the password back
        return {"ok": False, "connected": False, "ip": None,
                "error": (err.strip() or out.strip() or "connect failed")}
    ip = await _wlan_ip()
    return {"ok": True, "connected": ip is not None, "ip": ip, "error": None}


@app.get("/api/wifi/status")
async def wifi_status():
    ssid = await _wlan_ssid()
    ip = await _wlan_ip()
    return {"connected": ip is not None, "ssid": ssid, "ip": ip}


# ---- kiosk control (start or stop the local signage display) ----
KIOSK_UNIT = "pisignage-kiosk.service"

# The agent owns kiosk.sh: it rewrites the script at startup whenever this
# template changed, so "Update Pi software" heals Chromium launch flags on
# every Pi without SSH. Keep in sync with the heredoc in pi-setup/install.sh,
# which writes the same script during first-time provisioning (before the
# agent has ever run).
KIOSK_SH = r"""#!/usr/bin/env bash
# Wait for the agent, then run Chromium fullscreen on the signage page forever.
# AUTO-GENERATED by the agent at startup — local edits are overwritten.
CHROME="$(command -v chromium || command -v chromium-browser)"   # Trixie=chromium, Bookworm=chromium-browser
PROFILE="$HOME/.config/pisignage-kiosk"                          # dedicated profile (no stray tabs/logins)
EXTFLAG=""; [ -f "$HOME/h264ify/manifest.json" ] && EXTFLAG="--load-extension=$HOME/h264ify"
until curl -sf http://localhost:8080/api/status >/dev/null; do sleep 1; done
while true; do
  # never let a saved session/tab take over — always open the signage page
  rm -f "$PROFILE"/Default/Current\ Session "$PROFILE"/Default/Current\ Tabs \
        "$PROFILE"/Default/Last\ Session "$PROFILE"/Default/Last\ Tabs 2>/dev/null
  # --password-store=basic: without it Chromium asks GNOME keyring to unlock,
  # which pops "Choose a password for the new keyring" over the desktop the
  # first time someone signs into a site (e.g. Spotify) via remote control
  "$CHROME" --kiosk "http://localhost:8080/" --user-data-dir="$PROFILE" \
    --noerrdialogs --disable-infobars --disable-session-crashed-bubble --no-first-run \
    --password-store=basic \
    --disable-features=Translate --autoplay-policy=no-user-gesture-required \
    --check-for-update-interval=31536000 --overscroll-history-navigation=0 \
    --enable-gpu-rasterization --ignore-gpu-blocklist \
    --disk-cache-size=104857600 $EXTFLAG   # 100MB cache cap: protect the SD card
  sleep 2   # if Chromium ever crashes, relaunch it
done
"""
# install.sh puts kiosk.sh next to the agent dir ($HOME/pi-signage/kiosk.sh)
KIOSK_SH_PATH = APP_DIR.parent / "kiosk.sh"


async def _sync_kiosk_script() -> None:
    """Bring kiosk.sh up to date with the bundled template.

    Only touches a file that already exists — on a dev box (no provisioned
    kiosk.sh) this is a no-op, and on a fresh Pi install.sh owns the first
    write. Restarts the kiosk only when it was already running, so the TV
    blinks once and relaunches Chromium with the fixed flags."""
    try:
        if not KIOSK_SH_PATH.exists():
            return
        if KIOSK_SH_PATH.read_text() == KIOSK_SH:
            return
        tmp = KIOSK_SH_PATH.with_name("kiosk.sh.tmp")
        tmp.write_text(KIOSK_SH)
        os.chmod(tmp, 0o755)
        tmp.replace(KIOSK_SH_PATH)
        log.info("kiosk.sh regenerated from bundled template")
        _, out, _ = await _systemctl_user("is-active", KIOSK_UNIT, timeout=8.0)
        if out.strip() == "active":
            await _systemctl_user("restart", KIOSK_UNIT)  # TV picks up new flags
    except Exception:
        log.exception("kiosk.sh sync failed")


class KioskRequest(BaseModel):
    running: bool


async def _systemctl_user(*args: str, timeout: float = 15.0) -> tuple[int, str, str]:
    # agent runs as the desktop user with XDG_RUNTIME_DIR set (see the service unit),
    # so it can drive the user-session kiosk service.
    return await _run(["systemctl", "--user", *args], timeout=timeout)


@app.get("/api/kiosk")
async def kiosk_status():
    _, out, _ = await _systemctl_user("is-active", KIOSK_UNIT, timeout=8.0)
    return {"running": out.strip() == "active"}


@app.post("/api/kiosk", dependencies=[Depends(require_control_mutation)])
async def set_kiosk(req: KioskRequest):
    rc, out, err = await _systemctl_user("start" if req.running else "stop", KIOSK_UNIT)
    if rc != 0:
        return {"ok": False, "running": None, "error": (err.strip() or out.strip() or "systemctl failed")}
    return {"ok": True, "running": req.running, "error": None}


# ---- remote desktop (wayvnc on demand, paired-signature gated) ----
WAYVNC_PORT = 5900
WAYVNC_IDLE_SECONDS = 15 * 60
_RSA_KEY_FILE = DATA_DIR / "wayvnc_rsa_key.pem"
_WAYVNC_CONFIG = DATA_DIR / "wayvnc.config"
_remote_proc = None          # asyncio subprocess (or FakeProc in tests)
_remote_creds = None         # dict returned to the app while a session is live
_remote_idle_task = None     # asyncio.Task that stops an idle session


class RemoteDesktopRequest(BaseModel):
    running: bool


async def _ensure_rsa_key() -> None:
    if not _RSA_KEY_FILE.exists():
        rc, _, err = await _run(
            ["ssh-keygen", "-t", "rsa", "-b", "2048", "-m", "PEM",
             "-f", str(_RSA_KEY_FILE), "-N", "", "-q"])
        if rc != 0:
            raise RuntimeError(f"could not generate wayvnc key: {err.strip()}")


def _tigervnc_fingerprint(openssl_text: str) -> Optional[str]:
    """Fingerprint of the wayvnc RSA public key, in exactly the format the
    TigerVNC viewer shows in its "verify server" dialog: first 8 bytes of
    SHA-1 over (u32 key-bits BE || modulus || exponent padded to modulus
    size), lowercase hex, dash-separated. Input is the output of
    `openssl rsa -in key -noout -modulus -text`."""
    mod_hex = None
    exponent = None
    for line in openssl_text.splitlines():
        line = line.strip()
        if line.startswith("Modulus="):
            mod_hex = line.split("=", 1)[1].strip()
        elif line.startswith("publicExponent:"):
            try:
                exponent = int(line.split()[1])
            except (IndexError, ValueError):
                return None
    if not mod_hex or exponent is None:
        return None
    try:
        if len(mod_hex) % 2:
            mod_hex = "0" + mod_hex
        n = bytes.fromhex(mod_hex)
        e = exponent.to_bytes(len(n), "big")
    except (ValueError, OverflowError):
        return None
    digest = hashlib.sha1((len(n) * 8).to_bytes(4, "big") + n + e).digest()
    return "-".join(f"{b:02x}" for b in digest[:8])


async def _wayvnc_fingerprint() -> Optional[str]:
    try:
        rc, out, _ = await _run(
            ["openssl", "rsa", "-in", str(_RSA_KEY_FILE), "-noout", "-modulus", "-text"])
    except Exception:
        return None  # fingerprint is best-effort; the session still works
    if rc != 0:
        return None
    return _tigervnc_fingerprint(out)


def _write_wayvnc_config(username: str, password: str) -> None:
    content = (
        "enable_auth=true\n"
        f"username={username}\n"
        f"password={password}\n"
        f"rsa_private_key_file={_RSA_KEY_FILE}\n")
    fd = os.open(_WAYVNC_CONFIG, os.O_CREAT | os.O_WRONLY | os.O_TRUNC, 0o600)
    try:
        os.write(fd, content.encode())
    finally:
        os.close(fd)
    os.chmod(_WAYVNC_CONFIG, 0o600)  # ensure 0600 even if the file pre-existed with looser mode


async def _spawn_wayvnc(config_path: str):
    # Seam: tests monkeypatch this so no real process is launched.
    return await asyncio.create_subprocess_exec(
        "wayvnc", "--config", config_path, "0.0.0.0", str(WAYVNC_PORT),
        stdout=asyncio.subprocess.DEVNULL,
        stderr=asyncio.subprocess.PIPE)


async def _stop_wayvnc() -> None:
    global _remote_proc, _remote_creds, _remote_idle_task
    if _remote_idle_task is not None:
        _remote_idle_task.cancel()
        _remote_idle_task = None
    if _remote_proc is not None:
        try:
            _remote_proc.terminate()
            await asyncio.wait_for(_remote_proc.wait(), timeout=5)
        except (ProcessLookupError, asyncio.TimeoutError):
            try:
                _remote_proc.kill()
            except ProcessLookupError:
                pass
        _remote_proc = None
    _remote_creds = None
    try:
        _WAYVNC_CONFIG.unlink()
    except FileNotFoundError:
        pass


async def _idle_stop_after(seconds: int) -> None:
    try:
        await asyncio.sleep(seconds)
        await _stop_wayvnc()
    except asyncio.CancelledError:
        pass


@app.get("/api/remote-desktop")
async def remote_desktop_status():
    running = _remote_proc is not None and _remote_proc.returncode is None
    return {"running": running}


@app.post("/api/remote-desktop", dependencies=[Depends(require_control_mutation)])
async def set_remote_desktop(req: RemoteDesktopRequest):
    global _remote_proc, _remote_creds, _remote_idle_task
    if not req.running:
        await _stop_wayvnc()
        return {"ok": True, "running": False, "error": None}
    if _remote_proc is not None and _remote_proc.returncode is None:
        return {"ok": True, "running": True, "error": None, **_remote_creds}
    # Respawn path: the previous wayvnc died on its own. Its idle-timeout task
    # is still pending and would later stop() the *new* session early — cancel it.
    if _remote_idle_task is not None:
        _remote_idle_task.cancel()
        _remote_idle_task = None
    try:
        await _ensure_rsa_key()
    except Exception as exc:
        raise HTTPException(500, f"Could not prepare remote desktop: {exc}")
    username = secrets.token_hex(4)
    password = secrets.token_urlsafe(12)
    _write_wayvnc_config(username, password)
    _remote_proc = await _spawn_wayvnc(str(_WAYVNC_CONFIG))
    await asyncio.sleep(0.5)   # give wayvnc a moment to bind or fail
    if _remote_proc.returncode is not None:
        err = (await _remote_proc.stderr.read()).decode("utf-8", "replace")[:200]
        await _stop_wayvnc()
        raise HTTPException(500, f"wayvnc failed to start: {err.strip()}")
    _remote_creds = {
        "port": WAYVNC_PORT,
        "username": username,
        "password": password,
        # arrives over the HMAC-authenticated channel, so the app can show the
        # expected value next to TigerVNC's trust-on-first-use prompt
        "fingerprint": await _wayvnc_fingerprint(),
    }
    _remote_idle_task = asyncio.create_task(_idle_stop_after(WAYVNC_IDLE_SECONDS))
    return {"ok": True, "running": True, "error": None, **_remote_creds}


# ---- self-update (pushed from the control app / deploy script; no SSH) ----
_UPDATE_MAX_COMPRESSED_BYTES = 20 * 1024 * 1024
_UPDATE_MAX_BYTES = 20 * 1024 * 1024
_UPDATE_ROOT_FILES = frozenset(
    {"main.py", "trust.py", "control_auth.py", "delivery_reset.py"}
)
_UPDATE_REQUIRED_STATIC_FILES = frozenset(
    {"static/kiosk.html", "static/dashboard.html"}
)
_VERSION_RE = re.compile(r'AGENT_VERSION\s*=\s*"([^"]+)"')
_restart_task: Optional[asyncio.Task] = None  # strong ref: keep the restart task from being GC'd


async def _restart_after_update() -> None:
    # let the HTTP response flush before we pull the rug out
    try:
        await asyncio.sleep(1.0)
        await _systemctl_user("restart", KIOSK_UNIT)  # TV picks up new static pages
    finally:
        os._exit(0)  # systemd Restart=always relaunches us with the new code


@app.post("/api/update")
async def update_agent(
    file: UploadFile,
    control: VerifiedControl = Depends(require_control_mutation),
):
    data = await verify_uploaded_entity(
        file,
        control.entity_sha256,
        max_bytes=_UPDATE_MAX_COMPRESSED_BYTES,
        capture=True,
    )
    if data is None:
        raise RuntimeError("Verified update capture unexpectedly returned no bytes")
    accept_uploaded_counter(control, trust_store)
    try:
        zf = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile:
        raise HTTPException(400, "That doesn't look like a valid update file")

    total = 0
    paths: dict[str, str] = {}
    root_files: set[str] = set()
    regular_static_files: set[str] = set()
    for info in zf.infolist():
        name = info.filename
        is_directory = name.endswith("/")
        unix_type = stat.S_IFMT(info.external_attr >> 16)
        if (
            (is_directory and unix_type not in {0, stat.S_IFDIR})
            or (
                not is_directory
                and unix_type not in {0, stat.S_IFREG}
            )
        ):
            raise HTTPException(
                400,
                f"Update entries must be regular files or directories: {name}",
            )
        raw_parts = name.split("/")
        path_parts = raw_parts[:-1] if is_directory else raw_parts
        if name.startswith("/") or ":" in name or "\\" in name or ".." in path_parts:
            raise HTTPException(400, f"Unsafe path in update: {name}")
        if not path_parts or any(part in {"", "."} for part in path_parts):
            raise HTTPException(400, f"Non-canonical path in update: {name}")
        canonical = "/".join(path_parts)
        if is_directory:
            if canonical != "static" and not canonical.startswith("static/"):
                raise HTTPException(400, f"Unexpected folder in update: {name}")
            kind = "directory"
        else:
            if canonical not in _UPDATE_ROOT_FILES and not canonical.startswith("static/"):
                raise HTTPException(400, f"Unexpected file in update: {name}")
            kind = "file"
            if canonical in _UPDATE_ROOT_FILES:
                root_files.add(canonical)
            else:
                if info.file_size > 0:
                    regular_static_files.add(canonical)

        parents = [
            "/".join(path_parts[:index])
            for index in range(1, len(path_parts))
        ]
        if (
            canonical in paths
            or any(paths.get(parent) == "file" for parent in parents)
            or (
                kind == "file"
                and any(existing.startswith(canonical + "/") for existing in paths)
            )
        ):
            raise HTTPException(400, f"Duplicate or colliding path in update: {name}")
        paths[canonical] = kind
        total += info.file_size
    if total > _UPDATE_MAX_BYTES:
        raise HTTPException(400, "Update is too large")
    missing = sorted(_UPDATE_ROOT_FILES - root_files)
    if missing:
        raise HTTPException(400, f"Update is missing {', '.join(missing)}")
    missing_static = sorted(
        _UPDATE_REQUIRED_STATIC_FILES - regular_static_files
    )
    if missing_static:
        raise HTTPException(
            400,
            "Update is missing required non-empty static files: "
            + ", ".join(missing_static),
        )

    tmp = Path(tempfile.mkdtemp(prefix="update-tmp-", dir=APP_DIR))
    try:
        staged = tmp / "staged"
        staged.mkdir()
        zf.extractall(staged)  # safe: every entry name validated above
        for module_name in sorted(_UPDATE_ROOT_FILES):
            try:
                py_compile.compile(str(staged / module_name), doraise=True)
            except py_compile.PyCompileError as e:
                raise HTTPException(
                    400,
                    f"New {module_name} won't run (syntax error): {e}",
                )

        m = _VERSION_RE.search((staged / "main.py").read_text())
        new_version = m.group(1) if m else "unknown"

        # Build a complete recovery snapshot before changing any installed path.
        backup = APP_DIR / "update-backup"
        shutil.rmtree(backup, ignore_errors=True)
        backup.mkdir()
        for module_name in sorted(_UPDATE_ROOT_FILES):
            installed_module = APP_DIR / module_name
            if installed_module.exists():
                shutil.copy2(installed_module, backup / module_name)
        if (APP_DIR / "static").exists():
            shutil.copytree(APP_DIR / "static", backup / "static")

        rollback = tmp / "rollback"
        rollback.mkdir()
        managed_paths = [*sorted(_UPDATE_ROOT_FILES), "static"]
        touched: list[str] = []
        try:
            for relative in managed_paths:
                installed = APP_DIR / relative
                original = rollback / relative
                if installed.exists():
                    os.replace(installed, original)
                touched.append(relative)
                os.replace(staged / relative, installed)
        except OSError as install_error:
            rollback_errors = []
            for relative in reversed(touched):
                installed = APP_DIR / relative
                original = rollback / relative
                try:
                    if installed.is_dir():
                        shutil.rmtree(installed)
                    elif installed.exists():
                        installed.unlink()
                    if original.exists():
                        os.replace(original, installed)
                except OSError as rollback_error:
                    rollback_errors.append(f"{relative}: {rollback_error}")
            if rollback_errors:
                log.critical(
                    "Agent update rollback was incomplete: %s",
                    "; ".join(rollback_errors),
                )
                raise HTTPException(
                    500,
                    "Agent update failed and rollback was incomplete",
                ) from install_error
            raise HTTPException(
                500,
                "Agent update failed; previous bundle restored",
            ) from install_error
    finally:
        zf.close()
        shutil.rmtree(tmp, ignore_errors=True)

    global _restart_task
    _restart_task = asyncio.create_task(_restart_after_update())
    log.info("Agent updated to %s — restarting", new_version)
    return {"ok": True, "version": new_version}


# last player state reported by a kiosk ({"state": ..., "videoId": ...});
# surfaced in /api/status so the app can advance its queue on "ended"
_youtube_state: Optional[dict] = None
# same, for the Spotify embed ({"state": ..., "uri": ...})
_spotify_state: Optional[dict] = None

_YT_BACKSTOP_SECONDS = 10.0


async def _yt_backstop(ended_override: ShowNowRequest) -> None:
    """If the app isn't around to clear a finished video, do it ourselves so
    the TV doesn't sit on the YouTube end screen forever. The app's queue poll
    (1 s) replaces the override long before this fires, so no race.
    Serves spotify overrides too — it only compares override identity."""
    await asyncio.sleep(_YT_BACKSTOP_SECONDS)
    if state.override is ended_override:
        state.override = None
        state.override_until = None
        state.override_id = None
        state.bump()


@app.websocket("/ws")
async def ws_endpoint(ws: WebSocket):
    global _youtube_state, _spotify_state
    await hub.register(ws)
    try:
        while True:
            msg = await ws.receive_text()
            try:
                data = json.loads(msg)
            except ValueError:
                continue  # keepalive "ping"
            if not isinstance(data, dict):
                continue
            if data.get("type") == "yt-state":
                _youtube_state = {
                    "state": data.get("state"),
                    "videoId": data.get("videoId"),
                }
                if (
                    data.get("state") == "ended"
                    and state.override is not None
                    and state.override.type == "youtube"
                ):
                    asyncio.create_task(_yt_backstop(state.override))
            elif data.get("type") == "sp-state":
                _spotify_state = {
                    "state": data.get("state"),
                    "uri": data.get("uri"),
                }
                if (
                    data.get("state") == "ended"
                    and state.override is not None
                    and state.override.type == "spotify"
                ):
                    asyncio.create_task(_yt_backstop(state.override))
    except WebSocketDisconnect:
        pass
    finally:
        hub.unregister(ws)  # never leak a client, whatever killed the socket


# ---- control API (used by the Windows app) ----
class RenameRequest(BaseModel):
    name: str = Field(min_length=1, max_length=64)


@app.post("/api/name", dependencies=[Depends(require_control_mutation)])
async def set_name(req: RenameRequest):
    """Rename this Pi. Persists across restarts and refreshes the TV splash."""
    global DEVICE_NAME
    name = req.name.strip()
    if not name:
        raise HTTPException(400, "Name cannot be blank")
    DEVICE_NAME = name
    tmp = NAME_FILE.with_suffix(".tmp")
    tmp.write_text(name)
    tmp.replace(NAME_FILE)  # atomic, like the playlist
    state.bump()  # re-sends the idle splash so the TV shows the new name
    # ponytail: mDNS keeps advertising the old name until the agent restarts;
    # discovery still works because the app probes /api/status for the name
    return {"ok": True, "name": DEVICE_NAME}


@app.get("/api/status")
async def status():
    return {
        "name": DEVICE_NAME,
        "device_id": trust_store.device_id,
        "paired": trust_store.controller_id is not None,
        "version": app.version,
        "agent_version": AGENT_VERSION,
        "screens_connected": len(hub.clients),
        "playlist_items": len(state.playlist.items),
        "playlist_enabled": state.playlist.enabled,
        "override_active": state.override is not None,
        "now_showing": hub.current,
        "youtube_state": _youtube_state,
        "spotify_state": _spotify_state,
    }


@app.get("/api/playlist")
async def get_playlist():
    return state.playlist


@app.put("/api/playlist", dependencies=[Depends(require_control_mutation)])
async def put_playlist(playlist: Playlist):
    # validate media references exist before accepting
    for item in playlist.items:
        if item.type in ("image", "video"):
            if not _media_path(item.source).is_file():
                raise HTTPException(400, f"Media file not found: {item.source}")
        elif item.type == "youtube" and _youtube_id(item.source) is None:
            raise HTTPException(400, f"Not a valid YouTube video id: {item.source}")
        elif item.type == "spotify" and _spotify_uri(item.source) is None:
            raise HTTPException(400, f"Not a valid Spotify URI: {item.source}")
    state.playlist = playlist
    state.index = 0
    state.save_playlist()
    state.bump()
    return {"ok": True, "items": len(playlist.items)}


@app.post("/api/media")
async def upload_media(
    name: str,
    file: UploadFile,
    control: VerifiedControl = Depends(require_control_mutation),
):
    await verify_uploaded_entity(file, control.entity_sha256)
    name = _plain_media_name(name)
    accept_uploaded_counter(control, trust_store)
    ext = Path(name).suffix.lower()
    if ext not in ALLOWED_UPLOAD_EXT:
        raise HTTPException(400, f"Unsupported file type: {ext}")
    # a full SD card kills the whole Pi — refuse uploads before that happens
    if shutil.disk_usage(MEDIA_DIR).free < 500 * 1024 * 1024:
        raise HTTPException(507, "Pi is low on disk space — delete media first")
    dest = _media_path(name)
    tmp = dest.with_suffix(dest.suffix + ".part")
    with tmp.open("wb") as out:
        while chunk := await file.read(1024 * 1024):
            out.write(chunk)
    tmp.replace(dest)
    kind = "video" if (mimetypes.guess_type(name)[0] or "").startswith("video") else "image"
    return {"ok": True, "name": name, "type": kind, "bytes": dest.stat().st_size}


@app.get("/api/media")
async def list_media():
    files = []
    for p in sorted(MEDIA_DIR.iterdir()):
        if p.is_file() and not p.name.endswith(".part"):
            mime = mimetypes.guess_type(p.name)[0] or ""
            files.append({
                "name": p.name,
                "type": "video" if mime.startswith("video") else "image",
                "bytes": p.stat().st_size,
            })
    return {"files": files}


@app.delete("/api/media/{name}", dependencies=[Depends(require_control_mutation)])
async def delete_media(name: str):
    safe = _plain_media_name(name)
    target = _media_path(safe)
    if not target.is_file():
        raise HTTPException(404, "Not found")
    in_use = any(i.source == safe for i in state.playlist.items)
    if in_use:
        raise HTTPException(409, "File is used by the current playlist")
    boards = (_dashboard.get("view_data") or {}).get("boards") or {}
    if any(v == f"/media/{safe}" for v in boards.values()):
        raise HTTPException(409, "File is shown on a dashboard board")
    target.unlink()
    return {"ok": True}


@app.post("/api/media/{name}/detach", dependencies=[Depends(require_control_mutation)])
async def detach_media(name: str):
    """Stop displaying a file: pull it out of the playlist and off any
    dashboard board, so it can then be deleted or replaced."""
    safe = _plain_media_name(name)
    removed = 0
    kept = [i for i in state.playlist.items if i.source != safe]
    removed = len(state.playlist.items) - len(kept)
    if removed:
        state.playlist.items = kept
        state.index = 0
        state.save_playlist()
    boards = (_dashboard.get("view_data") or {}).get("boards") or {}
    cleared = [k for k, v in boards.items() if v == f"/media/{safe}"]
    if cleared:
        for k in cleared:
            del boards[k]
        tmp = DASHBOARD_FILE.with_suffix(".tmp")
        tmp.write_text(json.dumps(_dashboard, indent=2))
        tmp.replace(DASHBOARD_FILE)  # atomic
    state.bump()
    return {"ok": True, "playlist_removed": removed, "boards_cleared": len(cleared)}


class RenameMediaRequest(BaseModel):
    new_name: str  # base name without extension; extension is preserved


@app.post("/api/media/{name}/rename", dependencies=[Depends(require_control_mutation)])
async def rename_media(name: str, req: RenameMediaRequest):
    safe = _plain_media_name(name)
    src = _media_path(safe)
    if not src.is_file():
        raise HTTPException(404, "Not found")
    base = req.new_name.strip()
    if not base or len(base) > 100 or "/" in base or "\\" in base or ".." in base:
        raise HTTPException(400, "Invalid name")
    new_full = base + src.suffix.lower()
    if new_full == safe:
        return {"ok": True, "name": safe}
    dest = _media_path(new_full)
    if dest.exists():
        raise HTTPException(409, "A file with that name already exists")
    src.rename(dest)
    # media is referenced by filename — rewrite playlist + dashboard so nothing breaks
    changed = False
    for i in state.playlist.items:
        if i.source == safe:
            i.source = new_full
            changed = True
    if changed:
        state.save_playlist()
    boards = (_dashboard.get("view_data") or {}).get("boards") or {}
    if any(v == f"/media/{safe}" for v in boards.values()):
        for k, v in boards.items():
            if v == f"/media/{safe}":
                boards[k] = f"/media/{new_full}"
        tmp = DASHBOARD_FILE.with_suffix(".tmp")
        tmp.write_text(json.dumps(_dashboard, indent=2))
        tmp.replace(DASHBOARD_FILE)  # atomic
    state.bump()
    return {"ok": True, "name": new_full}


@app.post("/api/show-now", dependencies=[Depends(require_control_mutation)])
async def show_now(req: ShowNowRequest):
    if req.type in ("image", "video"):
        if not _media_path(req.source).is_file():
            raise HTTPException(400, f"Media file not found: {req.source}")
    if req.type == "youtube":
        if _youtube_id(req.source) is None:
            raise HTTPException(400, "Not a valid YouTube video id")
        # drop any stale player state, or replaying the same video would look
        # instantly "ended" to a queue-polling controller
        global _youtube_state
        _youtube_state = None
    if req.type == "spotify":
        if _spotify_uri(req.source) is None:
            raise HTTPException(400, "Not a valid Spotify URI")
        global _spotify_state
        _spotify_state = None  # same stale-state hazard as youtube
    state.override = req
    state.override_id = uuid.uuid4().hex[:8]
    if req.duration:
        state.override_until = asyncio.get_event_loop().time() + req.duration
    else:
        state.override_until = None
    state.bump()
    return {"ok": True}


@app.delete("/api/show-now", dependencies=[Depends(require_control_mutation)])
async def clear_show_now():
    state.override = None
    state.override_until = None
    state.override_id = None
    state.bump()
    return {"ok": True}


class YouTubeControlRequest(BaseModel):
    action: Literal["play", "pause", "seek", "volume"]
    # seek: signed offset in seconds; volume: 0-100
    value: Optional[int] = Field(default=None, ge=-86400, le=86400)


@app.post("/api/youtube/control", dependencies=[Depends(require_control_mutation)])
async def youtube_control(req: YouTubeControlRequest):
    if req.action == "volume" and (req.value is None or not 0 <= req.value <= 100):
        raise HTTPException(400, "volume needs a value between 0 and 100")
    if req.action == "seek" and req.value is None:
        raise HTTPException(400, "seek needs an offset value")
    await hub.broadcast({"type": "yt-control", "action": req.action, "value": req.value})
    return {"ok": True}


class SpotifyControlRequest(BaseModel):
    action: Literal["play", "pause", "seek"]
    # seek: signed offset in seconds (the Embed API has no volume control)
    value: Optional[int] = Field(default=None, ge=-86400, le=86400)


@app.post("/api/spotify/control", dependencies=[Depends(require_control_mutation)])
async def spotify_control(req: SpotifyControlRequest):
    if req.action == "seek" and req.value is None:
        raise HTTPException(400, "seek needs an offset value")
    await hub.broadcast({"type": "sp-control", "action": req.action, "value": req.value})
    return {"ok": True}


@app.post("/api/next", dependencies=[Depends(require_control_mutation)])
async def skip_next():
    state.index = (state.index + 1) % max(len(state.playlist.items), 1)
    state.bump()
    return {"ok": True, "index": state.index}


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=PORT)
