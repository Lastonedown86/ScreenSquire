import itertools

from fastapi.testclient import TestClient

from conftest import signed_test_request


def _fake_run(script):
    """Return an async _run that dispatches on the command verb."""
    async def _run(cmd, timeout=30.0):
        return script(cmd)
    return _run


def _signed_usb_wifi(agent_module, paired_signer, body, *, counter=1):
    entity = (
        '{"ssid":"' + body["ssid"] +
        '","password":"' + body["password"] + '"}'
    ).encode()
    headers = paired_signer("POST", "/api/wifi", entity, counter=counter)
    with TestClient(
        agent_module.app,
        client=("10.55.0.10", 50000),
    ) as usb_client:
        return usb_client.post(
            "/api/wifi",
            content=entity,
            headers={**headers, "Content-Type": "application/json"},
        )


def test_wifi_connect_success(agent_module, paired_signer, monkeypatch):
    def script(cmd):
        if cmd[:4] == ["sudo", "nmcli", "dev", "wifi"]:
            return (0, "Device 'wlan0' successfully activated", "")
        if cmd[:2] == ["nmcli", "-t"] and "IP4.ADDRESS" in cmd:
            return (0, "IP4.ADDRESS[1]:192.168.1.42/24\n", "")
        return (0, "", "")
    monkeypatch.setattr(agent_module, "_run", _fake_run(script))
    r = _signed_usb_wifi(
        agent_module,
        paired_signer,
        {"ssid": "Shop", "password": "secret123"},
    )
    body = r.json()
    assert body["ok"] is True and body["connected"] is True
    assert body["ip"] == "192.168.1.42"
    assert body["error"] is None

def test_wifi_connect_failure_hides_password(
    agent_module,
    paired_signer,
    monkeypatch,
):
    def script(cmd):
        return (4, "", "Error: Secrets were required, but not provided.")
    monkeypatch.setattr(agent_module, "_run", _fake_run(script))
    r = _signed_usb_wifi(
        agent_module,
        paired_signer,
        {"ssid": "Shop", "password": "secret123"},
    )
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


def test_wifi_rejects_a_valid_signed_request_over_lan(
    agent_module,
    paired_signer,
):
    body = b'{"ssid":"Shop","password":"secret123"}'
    headers = paired_signer("POST", "/api/wifi", body, counter=1)
    with TestClient(
        agent_module.app,
        client=("192.168.1.50", 50000),
    ) as lan_client:
        response = lan_client.post(
            "/api/wifi",
            content=body,
            headers={**headers, "Content-Type": "application/json"},
        )

    assert response.status_code == 403
    assert response.json()["detail"] == "USB connection required"


def test_wifi_accepts_a_valid_signed_request_over_usb(
    agent_module,
    monkeypatch,
):
    monkeypatch.setattr(
        agent_module,
        "_run",
        _fake_run(lambda cmd: (0, "", "")),
    )
    secret = agent_module.trust_store.pair(
        agent_module._test_recovery_pin,
        "test-controller",
    ).secret
    with TestClient(
        agent_module.app,
        client=("10.55.0.10", 50000),
    ) as usb_client:
        response = signed_test_request(
            usb_client,
            secret,
            next(itertools.count(1)),
            "POST",
            "/api/wifi",
            json_body={"ssid": "Shop", "password": "secret123"},
        )

    assert response.status_code == 200
