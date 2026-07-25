"""Durable device identity, controller pairing, and replay protection."""

from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import os
import secrets
import tempfile
import threading
import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any


def _b64(value: bytes) -> str:
    return base64.b64encode(value).decode("ascii")


def _unb64(value: str) -> bytes:
    return base64.b64decode(value.encode("ascii"), validate=True)


def _pin_hash(pin: str, salt: bytes) -> bytes:
    return hashlib.pbkdf2_hmac("sha256", pin.encode("ascii"), salt, 200_000)


@dataclass(frozen=True)
class PairResult:
    secret: bytes


class PairingBlocked(RuntimeError):
    def __init__(self, retry_after: int):
        self.retry_after = retry_after
        super().__init__(f"Pairing blocked for {retry_after} seconds")


class PairingGuard:
    def __init__(self):
        self.failures = 0
        self.blocked_until = 0.0

    def check(self, now: float) -> None:
        if now < self.blocked_until:
            raise PairingBlocked(round(self.blocked_until - now))

    def failed(self, now: float) -> None:
        self.failures += 1
        if self.failures >= 5:
            self.blocked_until = now + 60
            self.failures = 0

    def succeeded(self) -> None:
        self.failures = 0
        self.blocked_until = 0.0


class TrustStore:
    def __init__(self, path: Path):
        self.path = Path(path)
        self._lock = threading.RLock()
        self._guard = PairingGuard()
        self._data = self._read() if self.path.exists() else None

    @property
    def device_id(self) -> str:
        return self._require_data()["device_id"]

    @property
    def controller_id(self) -> str | None:
        return self._require_data()["controller_id"]

    def initialize(self) -> str:
        with self._lock:
            if self.path.exists():
                raise RuntimeError("Trust is already initialized")
            pin = f"{secrets.randbelow(100_000_000):08d}"
            salt = secrets.token_bytes(16)
            data = {
                "device_id": uuid.uuid4().hex,
                "pin_salt": _b64(salt),
                "pin_hash": _b64(_pin_hash(pin, salt)),
                "controller_id": None,
                "controller_secret": None,
                "last_counter": 0,
            }
            self._save(data)
            self._data = data
            return pin

    def pair(self, pin: str, controller_id: str) -> PairResult:
        with self._lock:
            now = time.monotonic()
            self._guard.check(now)
            data = self._require_data()
            salt = _unb64(data["pin_salt"])
            valid_format = (
                isinstance(pin, str)
                and len(pin) == 8
                and pin.isascii()
                and pin.isdigit()
            )
            candidate_pin = pin if valid_format else "00000000"
            supplied_hash = _pin_hash(candidate_pin, salt)
            expected_hash = _unb64(data["pin_hash"])
            if not valid_format or not hmac.compare_digest(supplied_hash, expected_hash):
                self._guard.failed(now)
                raise ValueError("Invalid recovery PIN")

            secret = secrets.token_bytes(32)
            updated = {
                **data,
                "controller_id": controller_id,
                "controller_secret": _b64(secret),
                "last_counter": 0,
            }
            self._save(updated)
            self._data = updated
            self._guard.succeeded()
            return PairResult(secret=secret)

    def controller_secret(self, controller_id: str) -> bytes | None:
        with self._lock:
            data = self._require_data()
            if data["controller_id"] != controller_id:
                return None
            encoded = data["controller_secret"]
            return _unb64(encoded) if encoded is not None else None

    def accept_counter(self, controller_id: str, counter: int) -> bool:
        with self._lock:
            data = self._require_data()
            if (
                data["controller_id"] != controller_id
                or type(counter) is not int
                or counter <= data["last_counter"]
            ):
                return False
            updated = {**data, "last_counter": counter}
            self._save(updated)
            self._data = updated
            return True

    def clear_controller(self) -> None:
        with self._lock:
            data = self._require_data()
            updated = {
                **data,
                "controller_id": None,
                "controller_secret": None,
                "last_counter": 0,
            }
            self._save(updated)
            self._data = updated

    def _require_data(self) -> dict[str, Any]:
        if self._data is None:
            raise RuntimeError("Trust is not initialized")
        return self._data

    def _read(self) -> dict[str, Any]:
        with self.path.open("r", encoding="utf-8") as handle:
            return json.load(handle)

    def _save(self, data: dict[str, Any]) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        fd, temporary_name = tempfile.mkstemp(
            dir=self.path.parent,
            prefix=f".{self.path.name}.",
        )
        temporary_path = Path(temporary_name)
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as handle:
                json.dump(data, handle, separators=(",", ":"), sort_keys=True)
                handle.write("\n")
                handle.flush()
                os.fsync(handle.fileno())
            os.chmod(temporary_path, 0o600)
            os.replace(temporary_path, self.path)
            os.chmod(self.path, 0o600)
            self._sync_parent_directory()
        except BaseException:
            temporary_path.unlink(missing_ok=True)
            raise

    def _sync_parent_directory(self) -> None:
        flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0)
        try:
            directory_fd = os.open(self.path.parent, flags)
        except OSError:
            return
        try:
            os.fsync(directory_fd)
        except OSError:
            pass
        finally:
            os.close(directory_fd)


def _main() -> None:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    init_parser = subparsers.add_parser("init")
    init_parser.add_argument("--data-dir", type=Path, required=True)
    args = parser.parse_args()

    if args.command == "init":
        pin = TrustStore(args.data_dir / "trust.json").initialize()
        print(f"RECOVERY_PIN={pin}")


if __name__ == "__main__":
    _main()
