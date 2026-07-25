def _fake_run(script):
    """Return an async _run that dispatches on the command verb."""
    async def _run(cmd, timeout=30.0):
        return script(cmd)
    return _run

def test_wifi_connect_success(agent_module, signed, monkeypatch):
    def script(cmd):
        if cmd[:4] == ["sudo", "nmcli", "dev", "wifi"]:
            return (0, "Device 'wlan0' successfully activated", "")
        if cmd[:2] == ["nmcli", "-t"] and "IP4.ADDRESS" in cmd:
            return (0, "IP4.ADDRESS[1]:192.168.1.42/24\n", "")
        return (0, "", "")
    monkeypatch.setattr(agent_module, "_run", _fake_run(script))
    r = signed("POST", "/api/wifi", json={"ssid": "Shop", "password": "secret123"})
    body = r.json()
    assert body["ok"] is True and body["connected"] is True
    assert body["ip"] == "192.168.1.42"
    assert body["error"] is None

def test_wifi_connect_failure_hides_password(agent_module, signed, monkeypatch):
    def script(cmd):
        return (4, "", "Error: Secrets were required, but not provided.")
    monkeypatch.setattr(agent_module, "_run", _fake_run(script))
    r = signed("POST", "/api/wifi", json={"ssid": "Shop", "password": "secret123"})
    body = r.json()
    assert body["ok"] is False and body["connected"] is False
    assert "Secrets were required" in body["error"]
    assert "secret123" not in body["error"]   # password never leaks

def test_wifi_status_parses(agent_module, client, monkeypatch):
    def script(cmd):
        if "GENERAL.CONNECTION" in cmd:
            return (0, "GENERAL.CONNECTION:ShopWiFi\n", "")
        if "IP4.ADDRESS" in cmd:
            return (0, "IP4.ADDRESS[1]:192.168.1.42/24\n", "")
        return (0, "", "")
    monkeypatch.setattr(agent_module, "_run", _fake_run(script))
    s = client.get("/api/wifi/status").json()
    assert s["connected"] is True and s["ssid"] == "ShopWiFi" and s["ip"] == "192.168.1.42"
