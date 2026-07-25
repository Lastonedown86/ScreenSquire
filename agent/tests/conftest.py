import os
import shutil
import tempfile

import pytest
from fastapi.testclient import TestClient


_TEST_DATA_DIR = tempfile.mkdtemp(prefix="pi-signage-test-")
os.environ["SIGNAGE_DATA"] = _TEST_DATA_DIR

import main


@pytest.fixture(scope="session")
def agent_module():
    return main


@pytest.fixture(scope="session")
def client(agent_module):
    return TestClient(agent_module.app)


def pytest_sessionfinish(session, exitstatus):
    shutil.rmtree(_TEST_DATA_DIR, ignore_errors=True)


@pytest.fixture
def media(tmp_path, monkeypatch):
    """Isolated media dir + empty playlist/dashboard, seeded with old.jpg."""
    monkeypatch.setattr(main, "MEDIA_DIR", tmp_path / "media")
    monkeypatch.setattr(main, "PLAYLIST_FILE", tmp_path / "playlist.json")
    monkeypatch.setattr(main, "DASHBOARD_FILE", tmp_path / "dashboard.json")
    main.MEDIA_DIR.mkdir()
    monkeypatch.setattr(main.state, "playlist", main.Playlist())
    monkeypatch.setattr(main, "_dashboard", {})
    (main.MEDIA_DIR / "old.jpg").write_bytes(b"x")
    return main.MEDIA_DIR
