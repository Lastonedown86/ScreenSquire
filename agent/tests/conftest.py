import main
import pytest


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
