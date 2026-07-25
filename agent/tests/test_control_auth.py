import base64
import hashlib
import json

import pytest
from fastapi.testclient import TestClient

from control_auth import canonical_message, sign


def test_signature_matches_known_vector():
    canonical = canonical_message(
        "store",
        7,
        "POST",
        "/api/name",
        hashlib.sha256(b'{"name":"Front"}').hexdigest(),
    )

    got = sign(bytes.fromhex("11" * 32), canonical)

    assert got == "5a2a17c6dacd1fbf9584c45e4b8348ee875d6ce4e9e15aa01d60942eb2e04ef5"


def test_unsigned_mutation_is_rejected(client):
    assert client.post("/api/name", json={"name": "Front"}).status_code == 401


def test_signed_mutation_succeeds_once_and_replay_fails(client, paired_signer):
    entity = b'{"name":"Front"}'
    headers = paired_signer("POST", "/api/name", entity, counter=1)
    headers["Content-Type"] = "application/json"

    assert client.post("/api/name", content=entity, headers=headers).status_code == 200
    assert client.post("/api/name", content=entity, headers=headers).status_code == 409


@pytest.mark.parametrize(
    ("method", "path", "kwargs"),
    [
        ("POST", "/api/dashboard", {"json": {}}),
        ("POST", "/api/wifi", {"json": {"ssid": "x", "password": "y"}}),
        ("POST", "/api/kiosk", {"json": {"running": True}}),
        (
            "POST",
            "/api/update",
            {"files": {"file": ("update.zip", b"x", "application/zip")}},
        ),
        ("POST", "/api/name", {"json": {"name": "x"}}),
        ("PUT", "/api/playlist", {"json": {"items": [], "enabled": True}}),
        (
            "POST",
            "/api/media",
            {"files": {"file": ("x.jpg", b"x", "image/jpeg")}},
        ),
        ("DELETE", "/api/media/x.jpg", {}),
        ("POST", "/api/media/x.jpg/detach", {}),
        ("POST", "/api/media/x.jpg/rename", {"json": {"new_name": "y"}}),
        (
            "POST",
            "/api/show-now",
            {"json": {"type": "url", "source": "https://example.com"}},
        ),
        ("DELETE", "/api/show-now", {}),
        ("POST", "/api/next", {}),
    ],
)
def test_every_control_mutation_rejects_unsigned_requests(client, method, path, kwargs):
    assert client.request(method, path, **kwargs).status_code == 401


@pytest.mark.parametrize(
    "headers",
    [
        {},
        {
            "X-PiSignage-Controller": "test-controller",
            "X-PiSignage-Counter": "not-an-integer",
            "X-PiSignage-Entity-SHA256": "0" * 64,
            "X-PiSignage-Signature": "0" * 64,
        },
        {
            "X-PiSignage-Controller": "test-controller",
            "X-PiSignage-Counter": "1",
            "X-PiSignage-Entity-SHA256": "not-a-sha256",
            "X-PiSignage-Signature": "0" * 64,
        },
        {
            "X-PiSignage-Controller": "test-controller",
            "X-PiSignage-Counter": "1",
            "X-PiSignage-Entity-SHA256": "0" * 64,
            "X-PiSignage-Signature": "not-a-signature",
        },
    ],
)
def test_missing_or_malformed_headers_return_401(client, headers):
    assert client.post("/api/next", headers=headers).status_code == 401


def test_wrong_signature_returns_401(client, paired_signer):
    headers = paired_signer("POST", "/api/next", counter=1)
    headers["X-PiSignage-Signature"] = "0" * 64

    assert client.post("/api/next", headers=headers).status_code == 401


def test_exact_json_entity_mismatch_returns_400(client, paired_signer):
    signed_entity = b'{"name":"Front"}'
    sent_entity = b'{ "name": "Front" }'
    headers = paired_signer("POST", "/api/name", signed_entity, counter=1)
    headers["Content-Type"] = "application/json"

    response = client.post("/api/name", content=sent_entity, headers=headers)

    assert response.status_code == 400


def test_multipart_hashes_uploaded_bytes_and_resets_stream(
    client,
    media,
    paired_signer,
):
    entity = b"exact uploaded bytes"
    headers = paired_signer("POST", "/api/media", entity, counter=1)

    response = client.post(
        "/api/media",
        files={"file": ("signed.jpg", entity, "image/jpeg")},
        headers=headers,
    )

    assert response.status_code == 200
    assert (media / "signed.jpg").read_bytes() == entity


def test_multipart_entity_mismatch_returns_400(client, media, paired_signer):
    headers = paired_signer("POST", "/api/media", b"different", counter=1)

    response = client.post(
        "/api/media",
        files={"file": ("signed.jpg", b"actual", "image/jpeg")},
        headers=headers,
    )

    assert response.status_code == 400
    assert not (media / "signed.jpg").exists()


def test_pair_and_pair_status_are_usb_only(agent_module):
    non_usb = TestClient(agent_module.app)
    usb = TestClient(agent_module.app, client=("10.55.0.2", 50000))

    assert non_usb.get("/api/pair/status").status_code == 403
    response = usb.post(
        "/api/pair",
        json={
            "recovery_pin": agent_module._test_recovery_pin,
            "controller_id": "windows-controller",
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["device_id"] == agent_module.trust_store.device_id
    assert body["controller_id"] == "windows-controller"
    assert len(base64.b64decode(body["controller_secret"], validate=True)) == 32
    status = usb.get("/api/pair/status")
    assert status.status_code == 200
    assert status.json() == {
        "device_id": agent_module.trust_store.device_id,
        "paired": True,
        "controller_id": "windows-controller",
    }
    assert "secret" not in json.dumps(status.json()).lower()
    assert "pin" not in json.dumps(status.json()).lower()


def test_public_status_has_stable_device_identity_and_pairing_state(
    agent_module,
    client,
):
    response = client.get("/api/status")

    assert response.status_code == 200
    assert response.json()["device_id"] == agent_module.trust_store.device_id
    assert response.json()["paired"] is (agent_module.trust_store.controller_id is not None)


@pytest.mark.parametrize(
    "path",
    [
        "/api/status",
        "/api/dashboard",
        "/api/playlist",
        "/api/media",
        "/dashboard",
    ],
)
def test_read_and_display_routes_remain_public(client, path):
    assert client.get(path).status_code == 200


def test_wifi_status_route_remains_public(agent_module, client, monkeypatch):
    async def fake_run(_cmd, timeout=30.0):
        return 0, "", ""

    monkeypatch.setattr(agent_module, "_run", fake_run)

    assert client.get("/api/wifi/status").status_code == 200
