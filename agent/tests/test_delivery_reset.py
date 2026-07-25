import asyncio
import json
import socket
import threading
from concurrent.futures import ThreadPoolExecutor

import pytest
from fastapi.testclient import TestClient

from conftest import signed_test_request
import delivery_reset


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


def test_prepare_delivery_serializes_signed_mutation_until_trust_is_cleared(
    agent_module,
    tmp_path,
    monkeypatch,
):
    _, _, _, name_file = _seed_customer_data(agent_module, tmp_path, monkeypatch)
    entered_wifi_cleanup = threading.Event()
    release_wifi_cleanup = threading.Event()
    mutation_started = threading.Event()
    mutation_finished = threading.Event()

    async def run(cmd, timeout=30.0):
        if cmd == ["nmcli", "-t", "-f", "UUID,TYPE", "connection", "show"]:
            entered_wifi_cleanup.set()
            released = await asyncio.to_thread(release_wifi_cleanup.wait, 5)
            assert released
            return 0, "", ""
        raise AssertionError(f"Unexpected command: {cmd!r}")

    monkeypatch.setattr(agent_module, "_run", run)
    secret = agent_module.trust_store.pair(
        agent_module._test_recovery_pin,
        "test-controller",
    ).secret

    with TestClient(
        agent_module.app,
        client=("10.55.0.10", 50000),
    ) as client:
        def reset():
            return signed_test_request(
                client,
                secret,
                1,
                "POST",
                "/api/prepare-delivery",
            )

        def rename():
            mutation_started.set()
            try:
                return signed_test_request(
                    client,
                    secret,
                    2,
                    "POST",
                    "/api/name",
                    json_body={"name": "Must Not Survive"},
                )
            finally:
                mutation_finished.set()

        with ThreadPoolExecutor(max_workers=2) as executor:
            reset_future = executor.submit(reset)
            assert entered_wifi_cleanup.wait(2)
            mutation_future = executor.submit(rename)
            assert mutation_started.wait(2)
            assert not mutation_finished.wait(0.25)

            release_wifi_cleanup.set()
            reset_response = reset_future.result(timeout=5)
            mutation_response = mutation_future.result(timeout=5)

    assert reset_response.status_code == 200
    assert mutation_response.status_code == 401
    assert not name_file.exists()
    assert agent_module.DEVICE_NAME == socket.gethostname()


def test_prepare_delivery_serializes_pairing_until_reset_finishes(
    agent_module,
    tmp_path,
    monkeypatch,
):
    _seed_customer_data(agent_module, tmp_path, monkeypatch)
    entered_wifi_cleanup = threading.Event()
    release_wifi_cleanup = threading.Event()
    pair_started = threading.Event()
    pair_finished = threading.Event()

    async def run(cmd, timeout=30.0):
        if cmd == ["nmcli", "-t", "-f", "UUID,TYPE", "connection", "show"]:
            entered_wifi_cleanup.set()
            released = await asyncio.to_thread(release_wifi_cleanup.wait, 5)
            assert released
            return 0, "", ""
        raise AssertionError(f"Unexpected command: {cmd!r}")

    monkeypatch.setattr(agent_module, "_run", run)
    secret = agent_module.trust_store.pair(
        agent_module._test_recovery_pin,
        "test-controller",
    ).secret

    with TestClient(
        agent_module.app,
        client=("10.55.0.10", 50000),
    ) as client:
        def reset():
            return signed_test_request(
                client,
                secret,
                1,
                "POST",
                "/api/prepare-delivery",
            )

        def pair():
            pair_started.set()
            try:
                return client.post(
                    "/api/pair",
                    json={
                        "recovery_pin": agent_module._test_recovery_pin,
                        "controller_id": "replacement-controller",
                    },
                )
            finally:
                pair_finished.set()

        with ThreadPoolExecutor(max_workers=2) as executor:
            reset_future = executor.submit(reset)
            assert entered_wifi_cleanup.wait(2)
            pair_future = executor.submit(pair)
            assert pair_started.wait(2)
            if pair_finished.wait(0.25):
                try:
                    early = pair_future.result(timeout=1)
                except BaseException as exc:
                    pytest.fail(f"pair request raised before reset finished: {exc!r}")
                pytest.fail(
                    "pair request completed before reset finished: "
                    f"{early.status_code} {early.text}"
                )

            release_wifi_cleanup.set()
            assert reset_future.result(timeout=5).status_code == 200
            assert pair_future.result(timeout=5).status_code == 200

    assert agent_module.trust_store.controller_id == "replacement-controller"


def test_prepare_delivery_does_not_publish_playlist_before_durable_write(
    agent_module,
    tmp_path,
    monkeypatch,
):
    _, playlist_file, _, _ = _seed_customer_data(
        agent_module,
        tmp_path,
        monkeypatch,
    )
    original_playlist = agent_module.state.playlist
    original_persist = delivery_reset._persist_json

    def persist(path, value):
        if path == playlist_file:
            raise OSError("playlist disk failure")
        original_persist(path, value)

    async def run(cmd, timeout=30.0):
        raise AssertionError("Wi-Fi cleanup must not run after persistence failure")

    monkeypatch.setattr(delivery_reset, "_persist_json", persist)
    monkeypatch.setattr(agent_module, "_run", run)
    response = _paired_request(
        agent_module,
        "10.55.0.10",
        raise_server_exceptions=False,
    )()

    assert response.status_code == 500
    assert agent_module.state.playlist is original_playlist
    assert agent_module.trust_store.controller_id == "test-controller"


def test_prepare_delivery_does_not_publish_dashboard_before_durable_write(
    agent_module,
    tmp_path,
    monkeypatch,
):
    _, _, dashboard_file, _ = _seed_customer_data(
        agent_module,
        tmp_path,
        monkeypatch,
    )
    original_dashboard = agent_module._dashboard
    original_persist = delivery_reset._persist_json

    def persist(path, value):
        if path == dashboard_file:
            raise OSError("dashboard disk failure")
        original_persist(path, value)

    async def run(cmd, timeout=30.0):
        raise AssertionError("Wi-Fi cleanup must not run after persistence failure")

    monkeypatch.setattr(delivery_reset, "_persist_json", persist)
    monkeypatch.setattr(agent_module, "_run", run)
    response = _paired_request(
        agent_module,
        "10.55.0.10",
        raise_server_exceptions=False,
    )()

    assert response.status_code == 500
    assert agent_module._dashboard is original_dashboard
    assert agent_module.trust_store.controller_id == "test-controller"


def test_prepare_delivery_keeps_controller_trust_when_wifi_enumeration_fails(
    agent_module,
    tmp_path,
    monkeypatch,
):
    _seed_customer_data(agent_module, tmp_path, monkeypatch)

    async def run(cmd, timeout=30.0):
        assert cmd == ["nmcli", "-t", "-f", "UUID,TYPE", "connection", "show"]
        return 10, "", "NetworkManager unavailable"

    monkeypatch.setattr(agent_module, "_run", run)
    response = _paired_request(
        agent_module,
        "10.55.0.10",
        raise_server_exceptions=False,
    )()

    assert response.status_code == 500
    assert agent_module.trust_store.controller_id == "test-controller"


@pytest.mark.parametrize(
    "malformed_row",
    [
        "missing-separator",
        ":802-11-wireless",
        "wifi-uuid:",
        "wifi-uuid:802-11-wireless:unexpected",
    ],
)
def test_prepare_delivery_fails_closed_on_malformed_nmcli_row(
    agent_module,
    tmp_path,
    monkeypatch,
    malformed_row,
):
    _seed_customer_data(agent_module, tmp_path, monkeypatch)

    async def run(cmd, timeout=30.0):
        assert cmd == ["nmcli", "-t", "-f", "UUID,TYPE", "connection", "show"]
        return 0, malformed_row + "\n", ""

    monkeypatch.setattr(agent_module, "_run", run)
    response = _paired_request(
        agent_module,
        "10.55.0.10",
        raise_server_exceptions=False,
    )()

    assert response.status_code == 500
    assert agent_module.trust_store.controller_id == "test-controller"


def test_prepare_delivery_retries_after_partial_wifi_deletion_failure(
    agent_module,
    tmp_path,
    monkeypatch,
):
    _seed_customer_data(agent_module, tmp_path, monkeypatch)
    remaining = ["first-wifi", "second-wifi"]
    second_attempts = 0

    async def run(cmd, timeout=30.0):
        nonlocal second_attempts
        if cmd == ["nmcli", "-t", "-f", "UUID,TYPE", "connection", "show"]:
            return (
                0,
                "".join(
                    f"{connection_uuid}:802-11-wireless\n"
                    for connection_uuid in remaining
                ),
                "",
            )
        connection_uuid = cmd[-1]
        if connection_uuid == "second-wifi":
            second_attempts += 1
            if second_attempts == 1:
                return 10, "", "NetworkManager refused deletion"
        remaining.remove(connection_uuid)
        return 0, "", ""

    monkeypatch.setattr(agent_module, "_run", run)
    request = _paired_request(
        agent_module,
        "10.55.0.10",
        raise_server_exceptions=False,
    )

    first = request(1)
    assert first.status_code == 500
    assert remaining == ["second-wifi"]
    assert agent_module.trust_store.controller_id == "test-controller"

    second = request(2)

    assert second.status_code == 200
    assert remaining == []
    assert agent_module.trust_store.controller_id is None
