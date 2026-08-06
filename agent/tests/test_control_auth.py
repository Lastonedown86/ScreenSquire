import base64
import asyncio
import hashlib
import io
import json

import pytest
from fastapi import HTTPException, UploadFile
from fastapi.testclient import TestClient

from control_auth import canonical_message, sign, verify_uploaded_entity


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
            "/api/media?name=x.jpg",
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
def test_every_control_mutation_rejects_unsigned_requests(
    agent_module,
    client,
    method,
    path,
    kwargs,
):
    if path == "/api/wifi":
        # Wi-Fi has an additional physical-link boundary. Exercise it from
        # USB here so this test specifically proves missing HMAC is rejected;
        # test_wifi separately proves even a valid signature gets 403 on LAN.
        with TestClient(
            agent_module.app,
            client=("10.55.0.10", 50000),
        ) as usb_client:
            response = usb_client.request(method, path, **kwargs)
    else:
        response = client.request(method, path, **kwargs)
    assert response.status_code == 401


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
    path = "/api/media?name=signed.jpg"
    headers = paired_signer("POST", path, entity, counter=1)

    response = client.post(
        path,
        files={"file": ("untrusted-name.jpg", entity, "image/jpeg")},
        headers=headers,
    )

    assert response.status_code == 200
    assert (media / "signed.jpg").read_bytes() == entity


def test_upload_verifier_stops_at_limit_plus_one_and_can_return_verified_bytes():
    oversized = UploadFile(file=io.BytesIO(b"x" * 100))
    bytes_returned = [0]
    original_read = oversized.read

    async def tracking_read(size=-1):
        chunk = await original_read(size)
        bytes_returned[0] += len(chunk)
        return chunk

    oversized.read = tracking_read
    with pytest.raises(HTTPException) as error:
        asyncio.run(
            verify_uploaded_entity(
                oversized,
                hashlib.sha256(b"x" * 100).hexdigest(),
                max_bytes=10,
                capture=True,
            )
        )
    assert error.value.status_code == 400
    assert bytes_returned[0] == 11

    exact = b"verified"
    upload = UploadFile(file=io.BytesIO(exact))
    captured = asyncio.run(
        verify_uploaded_entity(
            upload,
            hashlib.sha256(exact).hexdigest(),
            max_bytes=len(exact),
            capture=True,
        )
    )
    assert captured == exact
    assert asyncio.run(upload.read()) == exact


def test_multipart_entity_mismatch_returns_400(client, media, paired_signer):
    path = "/api/media?name=signed.jpg"
    headers = paired_signer("POST", path, b"different", counter=1)

    response = client.post(
        path,
        files={"file": ("signed.jpg", b"actual", "image/jpeg")},
        headers=headers,
    )

    assert response.status_code == 400
    assert not (media / "signed.jpg").exists()


def test_multipart_hash_failure_does_not_consume_counter(
    client,
    media,
    paired_signer,
):
    path = "/api/media?name=signed.jpg"
    entity = b"correct bytes"
    headers = paired_signer("POST", path, entity, counter=1)

    mismatch = client.post(
        path,
        files={"file": ("ignored.jpg", b"wrong bytes", "image/jpeg")},
        headers=headers,
    )
    retry = client.post(
        path,
        files={"file": ("ignored-again.jpg", entity, "image/jpeg")},
        headers=headers,
    )

    assert mismatch.status_code == 400
    assert retry.status_code == 200
    assert (media / "signed.jpg").read_bytes() == entity
    assert not (media / "ignored.jpg").exists()
    assert not (media / "ignored-again.jpg").exists()


def test_changed_multipart_filename_cannot_redirect_upload(
    client,
    media,
    paired_signer,
):
    path = "/api/media?name=signed.jpg"
    entity = b"same signed bytes"
    headers = paired_signer("POST", path, entity, counter=1)

    response = client.post(
        path,
        files={"file": ("attacker.jpg", entity, "image/jpeg")},
        headers=headers,
    )

    assert response.status_code == 200
    assert response.json()["name"] == "signed.jpg"
    assert (media / "signed.jpg").read_bytes() == entity
    assert not (media / "attacker.jpg").exists()


def test_changed_signed_upload_query_invalidates_signature(
    client,
    media,
    paired_signer,
):
    entity = b"signed bytes"
    headers = paired_signer(
        "POST",
        "/api/media?name=signed.jpg",
        entity,
        counter=1,
    )

    response = client.post(
        "/api/media?name=redirected.jpg",
        files={"file": ("ignored.jpg", entity, "image/jpeg")},
        headers=headers,
    )

    assert response.status_code == 401
    assert not (media / "signed.jpg").exists()
    assert not (media / "redirected.jpg").exists()


def test_signed_method_substitution_is_rejected(client, paired_signer):
    headers = paired_signer("POST", "/api/show-now", counter=1)

    assert client.delete("/api/show-now", headers=headers).status_code == 401


def test_signed_raw_path_substitution_is_rejected(client, paired_signer):
    headers = paired_signer(
        "POST",
        "/api/media/signed.jpg/detach",
        counter=1,
    )

    response = client.post("/api/media/other.jpg/detach", headers=headers)

    assert response.status_code == 401


def test_signed_query_order_is_exact(client, media, paired_signer):
    entity = b"signed bytes"
    headers = paired_signer(
        "POST",
        "/api/media?name=signed.jpg&tag=one",
        entity,
        counter=1,
    )

    response = client.post(
        "/api/media?tag=one&name=signed.jpg",
        files={"file": ("ignored.jpg", entity, "image/jpeg")},
        headers=headers,
    )

    assert response.status_code == 401
    assert not (media / "signed.jpg").exists()


def test_signed_query_encoding_is_exact(client, media, paired_signer):
    entity = b"signed bytes"
    headers = paired_signer(
        "POST",
        "/api/media?name=signed.jpg&tag=a%2Fb",
        entity,
        counter=1,
    )

    response = client.post(
        "/api/media?name=signed.jpg&tag=a/b",
        files={"file": ("ignored.jpg", entity, "image/jpeg")},
        headers=headers,
    )

    assert response.status_code == 401
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
        "trust_error": None,
    }
    assert "secret" not in json.dumps(status.json()).lower()
    assert "pin" not in json.dumps(status.json()).lower()


def test_pair_post_off_usb_and_failures_never_echo_credentials(
    agent_module,
    monkeypatch,
):
    import trust

    now = [100.0]
    monkeypatch.setattr(trust.time, "monotonic", lambda: now[0])
    non_usb = TestClient(agent_module.app)
    usb = TestClient(agent_module.app, client=("10.55.0.2", 50000))
    recovery_pin = agent_module._test_recovery_pin
    wrong_pin = "00000000" if recovery_pin != "00000000" else "00000001"
    request = {
        "recovery_pin": recovery_pin,
        "controller_id": "windows-controller",
    }

    off_usb = non_usb.post("/api/pair", json=request)
    assert off_usb.status_code == 403
    assert recovery_pin not in off_usb.text
    assert "secret" not in off_usb.text.lower()

    wrong_request = {**request, "recovery_pin": wrong_pin}
    for _ in range(5):
        wrong = usb.post("/api/pair", json=wrong_request)
        assert wrong.status_code == 401
        assert wrong_pin not in wrong.text
        assert "secret" not in wrong.text.lower()

    blocked = usb.post("/api/pair", json=request)
    assert blocked.status_code == 429
    assert recovery_pin not in blocked.text
    assert "secret" not in blocked.text.lower()

    now[0] += 60
    assert usb.post("/api/pair", json=request).status_code == 200


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
