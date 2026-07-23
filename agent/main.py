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
import json
import logging
import mimetypes
import os
import socket
import time
import uuid
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Literal, Optional

from fastapi import FastAPI, HTTPException, UploadFile, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field

log = logging.getLogger("signage")
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

# ---------------------------------------------------------------- paths / config
APP_DIR = Path(__file__).resolve().parent
DATA_DIR = Path(os.environ.get("SIGNAGE_DATA", APP_DIR / "data"))
MEDIA_DIR = DATA_DIR / "media"
PLAYLIST_FILE = DATA_DIR / "playlist.json"
PORT = int(os.environ.get("SIGNAGE_PORT", "8080"))
DEVICE_NAME = os.environ.get("SIGNAGE_NAME", socket.gethostname())

MEDIA_DIR.mkdir(parents=True, exist_ok=True)

ALLOWED_UPLOAD_EXT = {
    ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp",   # images
    ".mp4", ".webm", ".mov", ".mkv",                     # video
}

# ---------------------------------------------------------------- models
ItemType = Literal["image", "video", "url"]


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


# ---------------------------------------------------------------- state
class State:
    def __init__(self) -> None:
        self.playlist: Playlist = self._load_playlist()
        self.index: int = 0                      # current position in playlist
        self.override: Optional[ShowNowRequest] = None
        self.override_until: Optional[float] = None
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
        dead = []
        for ws in self.clients:
            try:
                await ws.send_json(payload)
            except Exception:
                dead.append(ws)
        for ws in dead:
            self.unregister(ws)


hub = Hub()

# ---------------------------------------------------------------- scheduler
def _item_payload(item: PlaylistItem) -> dict:
    if item.type == "url":
        return {"type": "url", "src": item.source, "id": item.id}
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
                await hub.show(_item_payload(PlaylistItem(type=ov.type, source=ov.source)))
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


app = FastAPI(title="Pi Signage Agent", version="0.1.0", lifespan=lifespan)


# ---- kiosk page + media ----
@app.get("/")
async def kiosk_page():
    return FileResponse(APP_DIR / "static" / "kiosk.html")


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


@app.post("/api/dashboard")
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
    return FileResponse(APP_DIR / "static" / "dashboard.html")


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


@app.post("/api/wifi")
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


@app.websocket("/ws")
async def ws_endpoint(ws: WebSocket):
    await hub.register(ws)
    try:
        while True:
            await ws.receive_text()  # kiosk pings; content unused for now
    except WebSocketDisconnect:
        hub.unregister(ws)


# ---- control API (used by the Windows app) ----
@app.get("/api/status")
async def status():
    return {
        "name": DEVICE_NAME,
        "version": app.version,
        "screens_connected": len(hub.clients),
        "playlist_items": len(state.playlist.items),
        "playlist_enabled": state.playlist.enabled,
        "override_active": state.override is not None,
        "now_showing": hub.current,
    }


@app.get("/api/playlist")
async def get_playlist():
    return state.playlist


@app.put("/api/playlist")
async def put_playlist(playlist: Playlist):
    # validate media references exist before accepting
    for item in playlist.items:
        if item.type in ("image", "video"):
            if "/" in item.source or "\\" in item.source or ".." in item.source:
                raise HTTPException(400, f"Invalid media name: {item.source}")
            if not (MEDIA_DIR / item.source).exists():
                raise HTTPException(400, f"Media file not found: {item.source}")
    state.playlist = playlist
    state.index = 0
    state.save_playlist()
    state.bump()
    return {"ok": True, "items": len(playlist.items)}


@app.post("/api/media")
async def upload_media(file: UploadFile):
    name = Path(file.filename or "upload").name  # strip any path components
    ext = Path(name).suffix.lower()
    if ext not in ALLOWED_UPLOAD_EXT:
        raise HTTPException(400, f"Unsupported file type: {ext}")
    dest = MEDIA_DIR / name
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


@app.delete("/api/media/{name}")
async def delete_media(name: str):
    safe = Path(name).name
    target = MEDIA_DIR / safe
    if not target.exists():
        raise HTTPException(404, "Not found")
    in_use = any(i.source == safe for i in state.playlist.items)
    if in_use:
        raise HTTPException(409, "File is used by the current playlist")
    target.unlink()
    return {"ok": True}


@app.post("/api/show-now")
async def show_now(req: ShowNowRequest):
    if req.type in ("image", "video") and not (MEDIA_DIR / Path(req.source).name).exists():
        raise HTTPException(400, f"Media file not found: {req.source}")
    state.override = req
    if req.duration:
        state.override_until = asyncio.get_event_loop().time() + req.duration
    else:
        state.override_until = None
    state.bump()
    return {"ok": True}


@app.delete("/api/show-now")
async def clear_show_now():
    state.override = None
    state.override_until = None
    state.bump()
    return {"ok": True}


@app.post("/api/next")
async def skip_next():
    state.index = (state.index + 1) % max(len(state.playlist.items), 1)
    state.bump()
    return {"ok": True, "index": state.index}


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=PORT)
