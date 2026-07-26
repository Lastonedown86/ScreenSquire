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
import ipaddress
import io
import json
import logging
import mimetypes
import os
import py_compile
import re
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
AGENT_VERSION = "2026.07.25.8"  # bump on every agent change; the app compares this


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


@app.websocket("/ws")
async def ws_endpoint(ws: WebSocket):
    await hub.register(ws)
    try:
        while True:
            await ws.receive_text()  # kiosk pings; content unused for now
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
    state.override = req
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
    state.bump()
    return {"ok": True}


@app.post("/api/next", dependencies=[Depends(require_control_mutation)])
async def skip_next():
    state.index = (state.index + 1) % max(len(state.playlist.items), 1)
    state.bump()
    return {"ok": True, "index": state.index}


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=PORT)
