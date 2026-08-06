"""A shipped Pi must survive trust-store damage in a serviceable state.

trust.json is the one state file whose loss used to take down everything:
a corrupt file crashed the agent at import, a missing one turned /api/status
and /api/pair into 500s — black TV, no USB onboarding, no remote diagnosis.
These tests pin the degraded-but-alive contract instead.
"""

import json

import pytest
from fastapi.testclient import TestClient

from trust import TrustStore


@pytest.fixture
def initialized(tmp_path):
    store = TrustStore(tmp_path / "trust.json")
    pin = store.initialize()
    return tmp_path, store, pin


def test_initialize_writes_an_identity_backup(initialized):
    tmp_path, store, _pin = initialized
    backup = json.loads((tmp_path / "trust.json.bak").read_text())
    assert backup["device_id"] == store.device_id
    assert backup["controller_id"] is None
    assert backup["controller_secret"] is None
    assert backup["last_counter"] == 0
    assert backup["pin_salt"] and backup["pin_hash"]


def test_corrupt_store_restores_identity_from_backup_with_trust_cleared(
    initialized,
):
    tmp_path, store, pin = initialized
    original_device_id = store.device_id
    store.pair(pin, "builder-laptop-0123")
    (tmp_path / "trust.json").write_text("{ truncated by power los")

    recovered = TrustStore(tmp_path / "trust.json")

    # identity and PIN survive; controller trust does not (a restored counter
    # could be stale, so re-pairing over USB is the only safe posture)
    assert recovered.device_id == original_device_id
    assert recovered.controller_id is None
    assert recovered.pair(pin, "store-laptop-4567")


def test_corrupt_store_without_backup_degrades_instead_of_raising_on_init(
    tmp_path,
):
    (tmp_path / "trust.json").write_text("not json at all")

    store = TrustStore(tmp_path / "trust.json")  # must not raise

    assert store.error is not None
    with pytest.raises(RuntimeError):
        store.device_id


def test_missing_store_reports_uninitialized_error(tmp_path):
    store = TrustStore(tmp_path / "trust.json")
    assert store.error is not None
    with pytest.raises(RuntimeError):
        store.device_id


def test_healthy_store_reports_no_error_and_backfills_backup(initialized):
    tmp_path, _store, _pin = initialized
    (tmp_path / "trust.json.bak").unlink()

    reopened = TrustStore(tmp_path / "trust.json")

    assert reopened.error is None
    # already-provisioned Pis (initialized before backups existed) gain one
    # the next time the agent starts
    assert (tmp_path / "trust.json.bak").exists()


# ---- API degradation: the TV keeps rendering and USB stays diagnosable ----


@pytest.fixture
def broken_trust(agent_module, tmp_path, monkeypatch):
    (tmp_path / "trust.json").write_text("not json at all")
    monkeypatch.setattr(
        agent_module, "trust_store", TrustStore(tmp_path / "trust.json")
    )
    return agent_module


def test_status_stays_200_when_trust_is_unavailable(broken_trust, client):
    response = client.get("/api/status")
    assert response.status_code == 200
    body = response.json()
    assert body["device_id"] is None
    assert body["paired"] is False
    assert body["trust_error"]


def test_status_reports_no_trust_error_when_healthy(agent_module, client):
    body = client.get("/api/status").json()
    assert body["trust_error"] is None
    assert body["device_id"]


def test_pair_returns_503_when_trust_is_unavailable(broken_trust):
    with TestClient(
        broken_trust.app, client=("10.55.0.10", 50000)
    ) as usb_client:
        response = usb_client.post(
            "/api/pair",
            json={
                "recovery_pin": "12345678",
                "controller_id": "store-laptop-0123",
            },
        )
    assert response.status_code == 503


def test_pair_status_degrades_instead_of_500(broken_trust):
    with TestClient(
        broken_trust.app, client=("10.55.0.10", 50000)
    ) as usb_client:
        response = usb_client.get("/api/pair/status")
    assert response.status_code == 200
    body = response.json()
    assert body["device_id"] is None
    assert body["paired"] is False
    assert body["trust_error"]
