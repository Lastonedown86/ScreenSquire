import main
from fastapi.testclient import TestClient

client = TestClient(main.app)


def _fake_run(script):
    async def _run(cmd, timeout=15.0):
        return script(cmd)
    return _run


def test_kiosk_status_running(monkeypatch):
    monkeypatch.setattr(main, "_run", _fake_run(lambda cmd: (0, "active\n", "")))
    assert client.get("/api/kiosk").json() == {"running": True}


def test_kiosk_status_stopped(monkeypatch):
    monkeypatch.setattr(main, "_run", _fake_run(lambda cmd: (3, "inactive\n", "")))
    assert client.get("/api/kiosk").json() == {"running": False}


def test_kiosk_start(monkeypatch):
    seen = {}
    def script(cmd):
        seen["cmd"] = cmd
        return (0, "", "")
    monkeypatch.setattr(main, "_run", _fake_run(script))
    r = client.post("/api/kiosk", json={"running": True}).json()
    assert r == {"ok": True, "running": True, "error": None}
    assert seen["cmd"] == ["systemctl", "--user", "start", "pisignage-kiosk.service"]


def test_kiosk_stop_maps_to_stop(monkeypatch):
    seen = {}
    monkeypatch.setattr(main, "_run", _fake_run(lambda cmd: (seen.__setitem__("cmd", cmd) or (0, "", ""))))
    client.post("/api/kiosk", json={"running": False})
    assert seen["cmd"] == ["systemctl", "--user", "stop", "pisignage-kiosk.service"]


def test_kiosk_failure_returns_error(monkeypatch):
    monkeypatch.setattr(main, "_run", _fake_run(lambda cmd: (1, "", "Failed to start unit")))
    r = client.post("/api/kiosk", json={"running": True}).json()
    assert r["ok"] is False and "Failed to start" in r["error"]
