import asyncio
import pytest


# ponytail: agent_module is session-scoped and _remote_proc/_remote_creds/
# _remote_idle_task are set via plain module globals (not monkeypatch), so a
# real state change in one test survives into the next. Reset before every
# test in this file so tests don't depend on run order.
@pytest.fixture(autouse=True)
def _reset_remote_desktop_state(agent_module):
    agent_module._remote_proc = None
    agent_module._remote_creds = None
    agent_module._remote_idle_task = None


class FakeProc:
    def __init__(self, returncode=None, stderr=b""):
        self.returncode = returncode
        self._stderr = stderr
        self.terminated = False
        self.killed = False

    def terminate(self):
        self.terminated = True
        self.returncode = 0

    def kill(self):
        self.killed = True
        self.returncode = -9

    async def wait(self):
        return self.returncode

    class _Reader:
        def __init__(self, data): self._data = data
        async def read(self): return self._data

    @property
    def stderr(self):
        return FakeProc._Reader(self._stderr)


def _install_fake_spawn(agent_module, monkeypatch, proc):
    async def _spawn(config_path):
        return proc
    monkeypatch.setattr(agent_module, "_ensure_rsa_key", lambda: asyncio.sleep(0))
    monkeypatch.setattr(agent_module, "_spawn_wayvnc", _spawn)


def test_status_reports_stopped_by_default(agent_module, client, monkeypatch):
    monkeypatch.setattr(agent_module, "_remote_proc", None)
    assert client.get("/api/remote-desktop").json() == {"running": False}


def test_start_returns_credentials_and_runs(agent_module, signed, monkeypatch):
    _install_fake_spawn(agent_module, monkeypatch, FakeProc(returncode=None))
    r = signed("POST", "/api/remote-desktop", json={"running": True}).json()
    assert r["ok"] is True and r["running"] is True
    assert r["port"] == 5900
    assert r["username"] and r["password"]
    assert client_running(agent_module)


def client_running(agent_module):
    return agent_module._remote_proc is not None and agent_module._remote_proc.returncode is None


def test_start_requires_signature(agent_module, client, monkeypatch):
    _install_fake_spawn(agent_module, monkeypatch, FakeProc(returncode=None))
    assert client.post("/api/remote-desktop", json={"running": True}).status_code == 401


def test_stop_terminates_process(agent_module, signed, monkeypatch):
    proc = FakeProc(returncode=None)
    _install_fake_spawn(agent_module, monkeypatch, proc)
    signed("POST", "/api/remote-desktop", json={"running": True})
    r = signed("POST", "/api/remote-desktop", json={"running": False}).json()
    assert r == {"ok": True, "running": False, "error": None}
    assert proc.terminated is True
    assert agent_module._remote_proc is None


def test_start_failure_reports_stderr(agent_module, signed, monkeypatch):
    _install_fake_spawn(agent_module, monkeypatch, FakeProc(returncode=1, stderr=b"no wayland display"))
    r = signed("POST", "/api/remote-desktop", json={"running": True})
    assert r.status_code == 500
    assert "no wayland display" in r.json()["detail"]
    assert agent_module._remote_proc is None
