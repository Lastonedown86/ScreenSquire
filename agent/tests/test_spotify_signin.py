import asyncio
import pytest


# Same run-order hazard as test_remote_desktop.py: module globals persist
# across tests in the session-scoped agent_module. Reset before every test.
@pytest.fixture(autouse=True)
def _reset_signin_state(agent_module):
    agent_module._signin_proc = None
    agent_module._signin_idle_task = None
    agent_module._signin_watch_task = None


class FakeProc:
    def __init__(self, returncode=None, stderr=b""):
        self.returncode = returncode
        self._stderr = stderr
        self.terminated = False
        self._exited = asyncio.Event()

    def terminate(self):
        self.terminated = True
        self.returncode = 0
        self._exited.set()

    def kill(self):
        self.returncode = -9
        self._exited.set()

    async def wait(self):
        await self._exited.wait()
        return self.returncode

    class _Reader:
        def __init__(self, data): self._data = data
        async def read(self): return self._data

    @property
    def stderr(self):
        return FakeProc._Reader(self._stderr)


class SystemctlSpy:
    """Records systemctl --user verbs so tests can assert kiosk stop/start."""
    def __init__(self, rc=0):
        self.calls = []
        self.rc = rc

    async def __call__(self, *args, timeout=15.0):
        self.calls.append(args)
        return self.rc, "", ""

    def verbs(self, unit):
        return [a[0] for a in self.calls if unit in a]


def _install(agent_module, monkeypatch, proc, rc=0):
    spy = SystemctlSpy(rc=rc)
    monkeypatch.setattr(agent_module, "_systemctl_user", spy)

    async def _spawn():
        return proc
    monkeypatch.setattr(agent_module, "_spawn_signin_browser", _spawn)
    return spy


def test_status_reports_stopped_by_default(agent_module, client):
    assert client.get("/api/spotify/signin").json() == {"running": False}


def test_start_requires_signature(agent_module, client, monkeypatch):
    _install(agent_module, monkeypatch, FakeProc())
    assert client.post("/api/spotify/signin", json={"running": True}).status_code == 401


def test_start_stops_kiosk_and_spawns_browser(agent_module, client, signed, monkeypatch):
    proc = FakeProc()
    spy = _install(agent_module, monkeypatch, proc)
    r = signed("POST", "/api/spotify/signin", json={"running": True}).json()
    assert r == {"ok": True, "running": True, "error": None}
    assert spy.verbs(agent_module.KIOSK_UNIT) == ["stop"]
    assert agent_module._signin_proc is proc
    assert client.get("/api/spotify/signin").json() == {"running": True}


def test_start_is_idempotent_while_running(agent_module, signed, monkeypatch):
    proc = FakeProc()
    spy = _install(agent_module, monkeypatch, proc)
    signed("POST", "/api/spotify/signin", json={"running": True})
    r = signed("POST", "/api/spotify/signin", json={"running": True}).json()
    assert r == {"ok": True, "running": True, "error": None}
    # kiosk stopped once, browser spawned once
    assert spy.verbs(agent_module.KIOSK_UNIT) == ["stop"]
    assert agent_module._signin_proc is proc


def test_stop_kills_browser_and_restarts_kiosk(agent_module, client, signed, monkeypatch):
    proc = FakeProc()
    spy = _install(agent_module, monkeypatch, proc)
    signed("POST", "/api/spotify/signin", json={"running": True})
    r = signed("POST", "/api/spotify/signin", json={"running": False}).json()
    assert r == {"ok": True, "running": False, "error": None}
    assert proc.terminated is True
    assert agent_module._signin_proc is None
    assert spy.verbs(agent_module.KIOSK_UNIT) == ["stop", "start"]
    assert client.get("/api/spotify/signin").json() == {"running": False}


def test_stop_when_not_running_is_noop_ok(agent_module, signed, monkeypatch):
    spy = _install(agent_module, monkeypatch, FakeProc())
    r = signed("POST", "/api/spotify/signin", json={"running": False}).json()
    assert r == {"ok": True, "running": False, "error": None}
    # never touched the kiosk: nothing was stopped, nothing to heal
    assert spy.verbs(agent_module.KIOSK_UNIT) == []


def test_spawn_failure_restarts_kiosk_and_500s(agent_module, signed, monkeypatch):
    proc = FakeProc(returncode=1, stderr=b"no display")
    spy = _install(agent_module, monkeypatch, proc)
    r = signed("POST", "/api/spotify/signin", json={"running": True})
    assert r.status_code == 500
    assert "no display" in r.json()["detail"]
    assert agent_module._signin_proc is None
    # kiosk must not stay down when the browser never came up
    assert spy.verbs(agent_module.KIOSK_UNIT) == ["stop", "start"]


def test_browser_exit_restarts_kiosk(agent_module, monkeypatch):
    """Operator closes the Chromium window on the Pi instead of using the app.
    The watcher coroutine is driven directly — TestClient's per-request event
    loop can't carry a background task across requests."""
    proc = FakeProc()
    spy = SystemctlSpy()
    monkeypatch.setattr(agent_module, "_systemctl_user", spy)
    agent_module._signin_proc = proc

    async def _scenario():
        watcher = asyncio.create_task(agent_module._watch_signin_exit(proc))
        await asyncio.sleep(0)   # let the watcher start waiting
        proc.terminate()         # browser window closed on the Pi
        await asyncio.wait_for(watcher, timeout=2)

    asyncio.run(_scenario())
    assert agent_module._signin_proc is None
    assert spy.verbs(agent_module.KIOSK_UNIT) == ["start"]


def test_browser_exit_watcher_ignores_stale_proc(agent_module, monkeypatch):
    """A watcher for an already-replaced session must not restart the kiosk
    under the new session's feet."""
    stale, current = FakeProc(), FakeProc()
    spy = SystemctlSpy()
    monkeypatch.setattr(agent_module, "_systemctl_user", spy)
    agent_module._signin_proc = current

    async def _scenario():
        watcher = asyncio.create_task(agent_module._watch_signin_exit(stale))
        await asyncio.sleep(0)
        stale.terminate()
        await asyncio.wait_for(watcher, timeout=2)

    asyncio.run(_scenario())
    assert agent_module._signin_proc is current
    assert spy.verbs(agent_module.KIOSK_UNIT) == []
