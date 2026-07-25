import importlib
import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient


@pytest.fixture(scope="session")
def agent_module(tmp_path_factory):
    patch = pytest.MonkeyPatch()
    data_dir = tmp_path_factory.mktemp("signage-data")
    agent_dir = str(Path(__file__).resolve().parents[1])
    patch.setenv("SIGNAGE_DATA", str(data_dir))
    patch.syspath_prepend(agent_dir)
    sys.modules.pop("main", None)
    module = importlib.import_module("main")
    try:
        yield module
    finally:
        sys.modules.pop("main", None)
        patch.undo()


@pytest.fixture(scope="session")
def client(agent_module):
    return TestClient(agent_module.app)


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
