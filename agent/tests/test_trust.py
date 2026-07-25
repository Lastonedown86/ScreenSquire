import json
import os
import stat
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import trust
from trust import PairingBlocked, TrustStore


def test_initialize_returns_eight_digits_and_persists_device_id(tmp_path):
    store = TrustStore(tmp_path / "trust.json")

    pin = store.initialize()

    assert pin.isdigit() and len(pin) == 8
    assert TrustStore(tmp_path / "trust.json").device_id == store.device_id


def test_initialize_refuses_to_replace_existing_trust(tmp_path):
    path = tmp_path / "trust.json"
    store = TrustStore(path)
    store.initialize()
    original = path.read_bytes()

    with pytest.raises(RuntimeError, match="already initialized"):
        store.initialize()

    assert path.read_bytes() == original


def test_initialize_writes_private_file_with_expected_shape(tmp_path, monkeypatch):
    path = tmp_path / "nested" / "trust.json"
    chmod_calls = []
    real_chmod = trust.os.chmod

    def record_chmod(chmod_path, mode):
        chmod_calls.append((Path(chmod_path), mode))
        real_chmod(chmod_path, mode)

    monkeypatch.setattr(trust.os, "chmod", record_chmod)

    TrustStore(path).initialize()

    data = json.loads(path.read_text(encoding="utf-8"))
    assert set(data) == {
        "device_id",
        "pin_salt",
        "pin_hash",
        "controller_id",
        "controller_secret",
        "last_counter",
    }
    assert data["controller_id"] is None
    assert data["controller_secret"] is None
    assert data["last_counter"] == 0
    assert chmod_calls[-1] == (path, 0o600)
    if os.name != "nt":
        assert stat.S_IMODE(path.stat().st_mode) == 0o600
    assert list(path.parent.iterdir()) == [path]


def test_pair_returns_unique_secret_and_replaces_previous_controller(tmp_path):
    store = TrustStore(tmp_path / "trust.json")
    pin = store.initialize()

    first = store.pair(pin, "builder")
    second = store.pair(pin, "store")

    assert len(first.secret) == len(second.secret) == 32
    assert first.secret != second.secret
    assert store.controller_id == "store"
    assert store.controller_secret("builder") is None
    assert store.controller_secret("store") == second.secret


def test_pair_rejects_incorrect_pin_without_changing_controller(tmp_path):
    path = tmp_path / "trust.json"
    store = TrustStore(path)
    pin = store.initialize()
    paired = store.pair(pin, "store")

    with pytest.raises(ValueError, match="Invalid recovery PIN"):
        store.pair("00000000" if pin != "00000000" else "00000001", "attacker")

    reloaded = TrustStore(path)
    assert reloaded.controller_id == "store"
    assert reloaded.controller_secret("store") == paired.secret


def test_clear_controller_removes_secret_and_resets_counter(tmp_path):
    path = tmp_path / "trust.json"
    store = TrustStore(path)
    pin = store.initialize()
    store.pair(pin, "store")
    assert store.accept_counter("store", 7)

    store.clear_controller()

    reloaded = TrustStore(path)
    assert reloaded.controller_id is None
    assert reloaded.controller_secret("store") is None
    assert not reloaded.accept_counter("store", 8)


def test_counter_must_increase_and_survives_reload(tmp_path):
    store = TrustStore(tmp_path / "trust.json")
    pin = store.initialize()
    store.pair(pin, "store")

    assert store.accept_counter("store", 4)
    reloaded = TrustStore(tmp_path / "trust.json")
    assert not reloaded.accept_counter("store", 4)
    assert not reloaded.accept_counter("builder", 5)
    assert reloaded.accept_counter("store", 5)


def test_sixth_pairing_attempt_is_blocked_until_sixty_seconds(monkeypatch, tmp_path):
    now = [100.0]
    monkeypatch.setattr(trust.time, "monotonic", lambda: now[0])
    store = TrustStore(tmp_path / "trust.json")
    pin = store.initialize()
    wrong_pin = "00000000" if pin != "00000000" else "00000001"

    for _ in range(5):
        with pytest.raises(ValueError, match="Invalid recovery PIN"):
            store.pair(wrong_pin, "attacker")

    with pytest.raises(PairingBlocked) as blocked:
        store.pair(pin, "store")
    assert blocked.value.retry_after == 60

    now[0] += 60
    result = store.pair(pin, "store")
    assert len(result.secret) == 32
