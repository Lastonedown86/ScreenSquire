def _fake_run(script):
    async def _run(cmd, timeout=15.0):
        return script(cmd)
    return _run


def test_kiosk_status_running(agent_module, client, monkeypatch):
    monkeypatch.setattr(agent_module, "_run", _fake_run(lambda cmd: (0, "active\n", "")))
    assert client.get("/api/kiosk").json() == {"running": True}


def test_kiosk_status_stopped(agent_module, client, monkeypatch):
    monkeypatch.setattr(agent_module, "_run", _fake_run(lambda cmd: (3, "inactive\n", "")))
    assert client.get("/api/kiosk").json() == {"running": False}


def test_kiosk_start(agent_module, signed, monkeypatch):
    seen = {}
    def script(cmd):
        seen["cmd"] = cmd
        return (0, "", "")
    monkeypatch.setattr(agent_module, "_run", _fake_run(script))
    r = signed("POST", "/api/kiosk", json={"running": True}).json()
    assert r == {"ok": True, "running": True, "error": None}
    assert seen["cmd"] == ["systemctl", "--user", "start", "pisignage-kiosk.service"]


def test_kiosk_stop_maps_to_stop(agent_module, signed, monkeypatch):
    seen = {}
    monkeypatch.setattr(agent_module, "_run", _fake_run(lambda cmd: (seen.__setitem__("cmd", cmd) or (0, "", ""))))
    signed("POST", "/api/kiosk", json={"running": False})
    assert seen["cmd"] == ["systemctl", "--user", "stop", "pisignage-kiosk.service"]


def test_kiosk_failure_returns_error(agent_module, signed, monkeypatch):
    monkeypatch.setattr(agent_module, "_run", _fake_run(lambda cmd: (1, "", "Failed to start unit")))
    r = signed("POST", "/api/kiosk", json={"running": True}).json()
    assert r["ok"] is False and "Failed to start" in r["error"]
