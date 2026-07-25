import json
import socket

from fastapi.testclient import TestClient

from conftest import signed_test_request


def _paired_request(agent_module, source_host, *, raise_server_exceptions=True):
    secret = agent_module.trust_store.pair(
        agent_module._test_recovery_pin,
        "test-controller",
    ).secret
    client = TestClient(
        agent_module.app,
        client=(source_host, 50000),
        raise_server_exceptions=raise_server_exceptions,
    )

    def request(counter=1):
        return signed_test_request(
            client,
            secret,
            counter,
            "POST",
            "/api/prepare-delivery",
        )

    return request


def _seed_customer_data(agent_module, tmp_path, monkeypatch):
    media_dir = tmp_path / "media"
    media_dir.mkdir()
    (media_dir / "customer.jpg").write_bytes(b"photo")
    (media_dir / "unfinished.mp4.part").write_bytes(b"partial")
    playlist_file = tmp_path / "playlist.json"
    dashboard_file = tmp_path / "dashboard.json"
    name_file = tmp_path / "name.txt"
    name_file.write_text("Tournament Pi")

    monkeypatch.setattr(agent_module, "MEDIA_DIR", media_dir)
    monkeypatch.setattr(agent_module, "PLAYLIST_FILE", playlist_file)
    monkeypatch.setattr(agent_module, "DASHBOARD_FILE", dashboard_file)
    monkeypatch.setattr(agent_module, "NAME_FILE", name_file)
    monkeypatch.setattr(agent_module, "DEVICE_NAME", "Tournament Pi")
    monkeypatch.setattr(
        agent_module.state,
        "playlist",
        agent_module.Playlist(
            items=[
                agent_module.PlaylistItem(
                    type="image",
                    source="customer.jpg",
                )
            ],
            enabled=False,
        ),
    )
    monkeypatch.setattr(agent_module.state, "index", 4)
    monkeypatch.setattr(
        agent_module.state,
        "override",
        agent_module.ShowNowRequest(
            type="image",
            source="customer.jpg",
            duration=30,
        ),
    )
    monkeypatch.setattr(agent_module.state, "override_until", 123.0)
    monkeypatch.setattr(
        agent_module,
        "_dashboard",
        {
            "view_data": {"boards": {"pairings": "/media/customer.jpg"}},
            "timer": {
                "state": "running",
                "endsAt": 9999999999999,
                "remaining": 600,
            },
        },
    )
    agent_module.state.wake.clear()
    return media_dir, playlist_file, dashboard_file, name_file


def test_prepare_delivery_erases_customer_data_but_preserves_identity(
    agent_module,
    tmp_path,
    monkeypatch,
):
    media_dir, playlist_file, dashboard_file, name_file = _seed_customer_data(
        agent_module,
        tmp_path,
        monkeypatch,
    )
    before_trust = json.loads(agent_module.TRUST_FILE.read_text())
    commands = []

    async def run(cmd, timeout=30.0):
        commands.append(cmd)
        if cmd == ["nmcli", "-t", "-f", "UUID,TYPE", "connection", "show"]:
            return (
                0,
                "ethernet-uuid:802-3-ethernet\n"
                "customer-wifi-uuid:802-11-wireless\n",
                "",
            )
        if cmd == [
            "sudo",
            "nmcli",
            "connection",
            "delete",
            "uuid",
            "customer-wifi-uuid",
        ]:
            return 0, "", ""
        raise AssertionError(f"Unexpected command: {cmd!r}")

    monkeypatch.setattr(agent_module, "_run", run)
    response = _paired_request(agent_module, "10.55.0.10")()

    assert response.status_code == 200
    assert response.json() == {
        "ok": True,
        "device_id": before_trust["device_id"],
    }
    assert list(media_dir.iterdir()) == []
    assert agent_module.state.playlist.items == []
    assert agent_module.state.playlist.enabled is True
    assert agent_module.state.index == 0
    assert agent_module.state.override is None
    assert agent_module.state.override_until is None
    assert agent_module._dashboard == {
        "view_data": {"boards": {}},
        "timer": {"state": "stopped"},
    }
    assert not name_file.exists()
    assert agent_module.DEVICE_NAME == socket.gethostname()
    assert json.loads(playlist_file.read_text()) == {
        "items": [],
        "enabled": True,
    }
    assert json.loads(dashboard_file.read_text()) == {
        "view_data": {"boards": {}},
        "timer": {"state": "stopped"},
    }
    assert agent_module.state.wake.is_set()

    after_trust = json.loads(agent_module.TRUST_FILE.read_text())
    assert after_trust["device_id"] == before_trust["device_id"]
    assert after_trust["pin_salt"] == before_trust["pin_salt"]
    assert after_trust["pin_hash"] == before_trust["pin_hash"]
    assert agent_module.trust_store.has_recovery_pin
    assert after_trust["controller_id"] is None
    assert after_trust["controller_secret"] is None
    assert after_trust["last_counter"] == 0
    assert commands == [
        ["nmcli", "-t", "-f", "UUID,TYPE", "connection", "show"],
        [
            "sudo",
            "nmcli",
            "connection",
            "delete",
            "uuid",
            "customer-wifi-uuid",
        ],
    ]


def test_prepare_delivery_checks_usb_before_consuming_signature(
    agent_module,
    tmp_path,
    monkeypatch,
):
    _seed_customer_data(agent_module, tmp_path, monkeypatch)

    async def run(cmd, timeout=30.0):
        if cmd == ["nmcli", "-t", "-f", "UUID,TYPE", "connection", "show"]:
            return 0, "", ""
        raise AssertionError(f"Unexpected command: {cmd!r}")

    monkeypatch.setattr(agent_module, "_run", run)
    secret = agent_module.trust_store.pair(
        agent_module._test_recovery_pin,
        "test-controller",
    ).secret
    lan = TestClient(agent_module.app, client=("192.168.50.10", 50000))
    usb = TestClient(agent_module.app, client=("10.55.0.10", 50000))

    assert usb.post("/api/prepare-delivery").status_code == 401

    rejected = signed_test_request(
        lan,
        secret,
        1,
        "POST",
        "/api/prepare-delivery",
    )
    accepted = signed_test_request(
        usb,
        secret,
        1,
        "POST",
        "/api/prepare-delivery",
    )

    assert rejected.status_code == 403
    assert accepted.status_code == 200


def test_prepare_delivery_keeps_controller_trust_when_wifi_cleanup_fails(
    agent_module,
    tmp_path,
    monkeypatch,
):
    _seed_customer_data(agent_module, tmp_path, monkeypatch)
    clear_calls = []
    original_clear = agent_module.trust_store.clear_controller

    def clear_controller():
        clear_calls.append("clear")
        original_clear()

    async def run(cmd, timeout=30.0):
        if cmd == ["nmcli", "-t", "-f", "UUID,TYPE", "connection", "show"]:
            return 0, "customer-wifi-uuid:802-11-wireless\n", ""
        if cmd[-1] == "customer-wifi-uuid":
            return 10, "", "NetworkManager refused deletion"
        raise AssertionError(f"Unexpected command: {cmd!r}")

    monkeypatch.setattr(agent_module.trust_store, "clear_controller", clear_controller)
    monkeypatch.setattr(agent_module, "_run", run)
    response = _paired_request(
        agent_module,
        "10.55.0.10",
        raise_server_exceptions=False,
    )()

    assert response.status_code == 500
    assert clear_calls == []
    assert agent_module.trust_store.controller_id == "test-controller"
    assert agent_module.trust_store.controller_secret("test-controller") is not None
