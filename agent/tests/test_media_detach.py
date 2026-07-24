import json

import main
import pytest
from fastapi.testclient import TestClient

client = TestClient(main.app)


def test_detach_removes_playlist_items(media):
    main.state.playlist.items = [
        main.PlaylistItem(type="image", source="old.jpg"),
        main.PlaylistItem(type="url", source="https://example.com"),
        main.PlaylistItem(type="image", source="old.jpg"),
    ]
    before = main.state.version
    r = client.post("/api/media/old.jpg/detach").json()
    assert r == {"ok": True, "playlist_removed": 2, "boards_cleared": 0}
    assert [i.source for i in main.state.playlist.items] == ["https://example.com"]
    assert main.state.version == before + 1
    saved = json.loads(main.PLAYLIST_FILE.read_text())
    assert len(saved["items"]) == 1


def test_detach_clears_dashboard_boards(media, monkeypatch):
    monkeypatch.setattr(main, "_dashboard", {"view_data": {"boards": {
        "left": "/media/old.jpg", "right": "/media/other.jpg"}}})
    r = client.post("/api/media/old.jpg/detach").json()
    assert r["boards_cleared"] == 1
    assert main._dashboard["view_data"]["boards"] == {"right": "/media/other.jpg"}
    saved = json.loads(main.DASHBOARD_FILE.read_text())
    assert saved["view_data"]["boards"] == {"right": "/media/other.jpg"}


def test_detach_then_delete_succeeds(media):
    main.state.playlist.items = [main.PlaylistItem(type="image", source="old.jpg")]
    assert client.delete("/api/media/old.jpg").status_code == 409
    client.post("/api/media/old.jpg/detach")
    assert client.delete("/api/media/old.jpg").status_code == 200
    assert not (media / "old.jpg").exists()


def test_detach_unused_file_noop(media):
    r = client.post("/api/media/old.jpg/detach").json()
    assert r == {"ok": True, "playlist_removed": 0, "boards_cleared": 0}
