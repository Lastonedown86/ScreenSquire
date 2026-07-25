import json
import os
import re
import stat
import subprocess
import sys
import threading
from concurrent.futures import ThreadPoolExecutor
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
    assert len(chmod_calls) == 1
    assert chmod_calls[0][0].parent == path.parent
    assert chmod_calls[0][0] != path
    assert chmod_calls[0][1] == 0o600
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


def test_counter_acceptance_rejects_secret_from_previous_pairing_epoch(tmp_path):
    store = TrustStore(tmp_path / "trust.json")
    pin = store.initialize()
    store.pair(pin, "store")
    verified_secret = store.controller_secret("store")
    assert verified_secret is not None

    # A request verifies with this secret, then the same controller ID re-pairs
    # before the request reaches its atomic counter-acceptance step.
    current = store.pair(pin, "store")

    assert not store.accept_counter(
        "store",
        1,
        expected_secret=verified_secret,
    )
    assert store.accept_counter(
        "store",
        1,
        expected_secret=current.secret,
    )


def test_two_instances_cannot_resurrect_revoked_controller_or_stale_counter(tmp_path):
    path = tmp_path / "trust.json"
    first = TrustStore(path)
    pin = first.initialize()
    first.pair(pin, "old-controller")
    stale = TrustStore(path)

    first.clear_controller()

    assert stale.controller_secret("old-controller") is None
    assert not stale.accept_counter("old-controller", 1)
    assert TrustStore(path).controller_id is None

    first.pair(pin, "store")
    counter_four = TrustStore(path)
    counter_five = TrustStore(path)
    assert counter_four.accept_counter("store", 4)
    assert not counter_five.accept_counter("store", 3)
    assert counter_five.accept_counter("store", 5)
    assert not counter_four.accept_counter("store", 4)


def test_two_instances_serialize_concurrent_counter_writes(tmp_path):
    path = tmp_path / "trust.json"
    store = TrustStore(path)
    pin = store.initialize()
    store.pair(pin, "store")
    low = TrustStore(path)
    high = TrustStore(path)
    low_entered_save = threading.Event()
    high_finished_save = threading.Event()
    real_low_save = low._save
    real_high_save = high._save

    def delayed_low_save(data):
        low_entered_save.set()
        high_finished_save.wait(timeout=0.25)
        real_low_save(data)

    def tracked_high_save(data):
        real_high_save(data)
        high_finished_save.set()

    low._save = delayed_low_save
    high._save = tracked_high_save
    with ThreadPoolExecutor(max_workers=2) as executor:
        low_result = executor.submit(low.accept_counter, "store", 1)
        assert low_entered_save.wait(timeout=1)
        high_result = executor.submit(high.accept_counter, "store", 2)
        assert low_result.result(timeout=2)
        assert high_result.result(timeout=2)

    assert not TrustStore(path).accept_counter("store", 2)


def test_failed_precommit_chmod_keeps_disk_and_memory_in_sync(
    tmp_path, monkeypatch
):
    path = tmp_path / "trust.json"
    store = TrustStore(path)
    pin = store.initialize()
    store.pair(pin, "store")
    assert store.accept_counter("store", 3)

    monkeypatch.setattr(
        trust.os,
        "chmod",
        lambda *_args: (_ for _ in ()).throw(PermissionError("denied")),
    )
    with pytest.raises(PermissionError, match="denied"):
        store.accept_counter("store", 4)
    monkeypatch.undo()

    assert TrustStore(path).accept_counter("store", 4)
    assert not store.accept_counter("store", 4)


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


def test_cli_prints_pin_once_and_repeat_does_not_reveal_it(tmp_path):
    script = Path(trust.__file__).resolve()
    command = [
        sys.executable,
        str(script),
        "init",
        "--data-dir",
        str(tmp_path),
    ]

    first = subprocess.run(command, capture_output=True, text=True, check=True)

    assert re.fullmatch(r"RECOVERY_PIN=\d{8}\n", first.stdout)
    assert first.stderr == ""

    repeat = subprocess.run(command, capture_output=True, text=True)

    assert repeat.returncode != 0
    assert not re.search(r"RECOVERY_PIN=\d{8}", repeat.stdout + repeat.stderr)
