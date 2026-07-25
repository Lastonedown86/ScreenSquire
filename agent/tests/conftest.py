import importlib
import hashlib
import itertools
import json
import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

AGENT_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(AGENT_DIR))

from control_auth import canonical_message, sign


def signed_test_request(
    client,
    secret,
    counter,
    method,
    path,
    *,
    json_body=None,
    files=None,
):
    if json_body is not None:
        entity = json.dumps(json_body, separators=(",", ":")).encode()
        send = {
            "content": entity,
            "headers": {"Content-Type": "application/json"},
        }
    elif files is not None:
        file_value = files["file"]
        entity = file_value[1]
        send = {"files": files, "headers": {}}
    else:
        entity = b""
        send = {"headers": {}}
    entity_hash = hashlib.sha256(entity).hexdigest()
    canonical = canonical_message(
        "test-controller",
        counter,
        method.upper(),
        path,
        entity_hash,
    )
    send["headers"].update({
        "X-PiSignage-Controller": "test-controller",
        "X-PiSignage-Counter": str(counter),
        "X-PiSignage-Entity-SHA256": entity_hash,
        "X-PiSignage-Signature": sign(secret, canonical),
    })
    return client.request(method, path, **send)


@pytest.fixture(scope="session")
def agent_module(tmp_path_factory):
    patch = pytest.MonkeyPatch()
    data_dir = tmp_path_factory.mktemp("signage-data")
    agent_dir = str(Path(__file__).resolve().parents[1])
    patch.setenv("SIGNAGE_DATA", str(data_dir))
    patch.syspath_prepend(agent_dir)
    sys.modules.pop("main", None)
    module = importlib.import_module("main")
    module._test_recovery_pin = module.trust_store.initialize()
    try:
        yield module
    finally:
        sys.modules.pop("main", None)
        patch.undo()


@pytest.fixture(scope="session")
def client(agent_module):
    return TestClient(agent_module.app)


@pytest.fixture
def paired_signer(agent_module):
    secret = agent_module.trust_store.pair(
        agent_module._test_recovery_pin,
        "test-controller",
    ).secret

    def headers(method, path, entity=b"", *, counter):
        entity_hash = hashlib.sha256(entity).hexdigest()
        canonical = canonical_message(
            "test-controller",
            counter,
            method.upper(),
            path,
            entity_hash,
        )
        return {
            "X-PiSignage-Controller": "test-controller",
            "X-PiSignage-Counter": str(counter),
            "X-PiSignage-Entity-SHA256": entity_hash,
            "X-PiSignage-Signature": sign(secret, canonical),
        }

    return headers


@pytest.fixture
def signed(client, agent_module):
    counter = itertools.count(1)
    secret = agent_module.trust_store.pair(
        agent_module._test_recovery_pin,
        "test-controller",
    ).secret

    def request(method, path, *, json=None, files=None):
        return signed_test_request(
            client,
            secret,
            next(counter),
            method,
            path,
            json_body=json,
            files=files,
        )

    return request


@pytest.fixture
def media(agent_module, tmp_path, monkeypatch):
    """Isolated media dir + empty playlist/dashboard, seeded with old.jpg."""
    monkeypatch.setattr(agent_module, "MEDIA_DIR", tmp_path / "media")
    monkeypatch.setattr(agent_module, "PLAYLIST_FILE", tmp_path / "playlist.json")
    monkeypatch.setattr(agent_module, "DASHBOARD_FILE", tmp_path / "dashboard.json")
    agent_module.MEDIA_DIR.mkdir()
    monkeypatch.setattr(agent_module.state, "playlist", agent_module.Playlist())
    monkeypatch.setattr(agent_module, "_dashboard", {})
    (agent_module.MEDIA_DIR / "old.jpg").write_bytes(b"x")
    return agent_module.MEDIA_DIR
