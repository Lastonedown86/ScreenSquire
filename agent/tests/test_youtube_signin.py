import asyncio
import pytest

from test_spotify_signin import FakeProc, SystemctlSpy


# Same run-order hazard as test_spotify_signin.py: module globals persist
# across tests in the session-scoped agent_module. Reset before every test.
@pytest.fixture(autouse=True)
def _reset_signin_state(agent_module):
    agent_module._signin_proc = None
    agent_module._signin_idle_task = None
    agent_module._signin_watch_task = None
    agent_module._signin_site = None


def _install(agent_module, monkeypatch, *procs, rc=0):
    """Spawn seam that records the URL of each launch and hands out procs
    in order (so a cross-site switch can get a fresh proc)."""
    spy = SystemctlSpy(rc=rc)
    monkeypatch.setattr(agent_module, "_systemctl_user", spy)
    queue = list(procs)
    urls = []

    async def _spawn(url):
        urls.append(url)
        return queue.pop(0)
    monkeypatch.setattr(agent_module, "_spawn_signin_browser", _spawn)
    return spy, urls


def test_status_reports_stopped_by_default(agent_module, client):
    assert client.get("/api/youtube/signin").json() == {"running": False}


def test_start_requires_signature(agent_module, client, monkeypatch):
    _install(agent_module, monkeypatch, FakeProc())
    assert client.post("/api/youtube/signin", json={"running": True}).status_code == 401


def test_start_stops_kiosk_and_opens_youtube(agent_module, client, signed, monkeypatch):
    proc = FakeProc()
    spy, urls = _install(agent_module, monkeypatch, proc)
    r = signed("POST", "/api/youtube/signin", json={"running": True}).json()
    assert r == {"ok": True, "running": True, "error": None}
    assert urls == ["https://www.youtube.com/"]
    assert spy.verbs(agent_module.KIOSK_UNIT) == ["stop"]
    assert agent_module._signin_proc is proc
    assert client.get("/api/youtube/signin").json() == {"running": True}


def test_start_is_idempotent_while_running(agent_module, signed, monkeypatch):
    proc = FakeProc()
    spy, urls = _install(agent_module, monkeypatch, proc)
    signed("POST", "/api/youtube/signin", json={"running": True})
    r = signed("POST", "/api/youtube/signin", json={"running": True}).json()
    assert r == {"ok": True, "running": True, "error": None}
    assert urls == ["https://www.youtube.com/"]
    assert spy.verbs(agent_module.KIOSK_UNIT) == ["stop"]
    assert agent_module._signin_proc is proc


def test_cross_site_start_switches_browser(agent_module, signed, monkeypatch):
    """YouTube start while a Spotify sign-in runs must land on the YouTube
    page, not report 'already running' with the browser on Spotify."""
    spotify_proc, youtube_proc = FakeProc(), FakeProc()
    spy, urls = _install(agent_module, monkeypatch, spotify_proc, youtube_proc)
    signed("POST", "/api/spotify/signin", json={"running": True})
    r = signed("POST", "/api/youtube/signin", json={"running": True}).json()
    assert r == {"ok": True, "running": True, "error": None}
    assert urls == ["https://open.spotify.com/", "https://www.youtube.com/"]
    assert spotify_proc.terminated is True
    assert agent_module._signin_proc is youtube_proc


def test_stop_kills_browser_and_restarts_kiosk(agent_module, client, signed, monkeypatch):
    proc = FakeProc()
    spy, urls = _install(agent_module, monkeypatch, proc)
    signed("POST", "/api/youtube/signin", json={"running": True})
    r = signed("POST", "/api/youtube/signin", json={"running": False}).json()
    assert r == {"ok": True, "running": False, "error": None}
    assert proc.terminated is True
    assert agent_module._signin_proc is None
    assert spy.verbs(agent_module.KIOSK_UNIT) == ["stop", "start"]
    assert client.get("/api/youtube/signin").json() == {"running": False}


def test_stop_via_other_site_endpoint_stops_the_session(agent_module, signed, monkeypatch):
    """One shared session: stopping through either endpoint heals the kiosk."""
    proc = FakeProc()
    spy, urls = _install(agent_module, monkeypatch, proc)
    signed("POST", "/api/youtube/signin", json={"running": True})
    r = signed("POST", "/api/spotify/signin", json={"running": False}).json()
    assert r == {"ok": True, "running": False, "error": None}
    assert proc.terminated is True
    assert spy.verbs(agent_module.KIOSK_UNIT) == ["stop", "start"]
