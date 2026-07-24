import json

import main
from fastapi.testclient import TestClient

client = TestClient(main.app)


def test_rename_happy_path(media):
    r = client.post("/api/media/old.jpg/rename", json={"new_name": "new"})
    assert r.json() == {"ok": True, "name": "new.jpg"}
    assert not (media / "old.jpg").exists()
    assert (media / "new.jpg").exists()


def test_rename_rewrites_playlist_and_bumps(media):
    main.state.playlist.items.append(
        main.PlaylistItem(type="image", source="old.jpg"))
    before = main.state.version
    r = client.post("/api/media/old.jpg/rename", json={"new_name": "new"})
    assert r.status_code == 200
    assert main.state.playlist.items[0].source == "new.jpg"
    assert main.state.version == before + 1
    saved = json.loads(main.PLAYLIST_FILE.read_text())
    assert saved["items"][0]["source"] == "new.jpg"


def test_rename_rewrites_dashboard_boards(media, monkeypatch):
    monkeypatch.setattr(main, "_dashboard",
                        {"view_data": {"boards": {"left": "/media/old.jpg"}}})
    r = client.post("/api/media/old.jpg/rename", json={"new_name": "new"})
    assert r.status_code == 200
    assert main._dashboard["view_data"]["boards"]["left"] == "/media/new.jpg"
    saved = json.loads(main.DASHBOARD_FILE.read_text())
    assert saved["view_data"]["boards"]["left"] == "/media/new.jpg"


def test_rename_duplicate_target_409(media):
    (media / "new.jpg").write_bytes(b"y")
    r = client.post("/api/media/old.jpg/rename", json={"new_name": "new"})
    assert r.status_code == 409
    assert (media / "old.jpg").exists()


def test_rename_bad_names_400(media):
    for bad in ["", "  ", "a/b", "a\\b", "..", "x" * 101]:
        r = client.post("/api/media/old.jpg/rename", json={"new_name": bad})
        assert r.status_code == 400, bad


def test_rename_missing_file_404(media):
    r = client.post("/api/media/nope.jpg/rename", json={"new_name": "new"})
    assert r.status_code == 404


def test_rename_same_name_noop(media):
    r = client.post("/api/media/old.jpg/rename", json={"new_name": "old"})
    assert r.json() == {"ok": True, "name": "old.jpg"}
    assert (media / "old.jpg").exists()
