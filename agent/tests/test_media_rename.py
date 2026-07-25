import json

def test_rename_happy_path(client, media):
    r = client.post("/api/media/old.jpg/rename", json={"new_name": "new"})
    assert r.json() == {"ok": True, "name": "new.jpg"}
    assert not (media / "old.jpg").exists()
    assert (media / "new.jpg").exists()


def test_rename_rewrites_playlist_and_bumps(agent_module, client, media):
    agent_module.state.playlist.items.append(
        agent_module.PlaylistItem(type="image", source="old.jpg"))
    before = agent_module.state.version
    r = client.post("/api/media/old.jpg/rename", json={"new_name": "new"})
    assert r.status_code == 200
    assert agent_module.state.playlist.items[0].source == "new.jpg"
    assert agent_module.state.version == before + 1
    saved = json.loads(agent_module.PLAYLIST_FILE.read_text())
    assert saved["items"][0]["source"] == "new.jpg"


def test_rename_rewrites_dashboard_boards(agent_module, client, media, monkeypatch):
    monkeypatch.setattr(agent_module, "_dashboard",
                        {"view_data": {"boards": {"left": "/media/old.jpg"}}})
    r = client.post("/api/media/old.jpg/rename", json={"new_name": "new"})
    assert r.status_code == 200
    assert agent_module._dashboard["view_data"]["boards"]["left"] == "/media/new.jpg"
    saved = json.loads(agent_module.DASHBOARD_FILE.read_text())
    assert saved["view_data"]["boards"]["left"] == "/media/new.jpg"


def test_rename_duplicate_target_409(client, media):
    (media / "new.jpg").write_bytes(b"y")
    r = client.post("/api/media/old.jpg/rename", json={"new_name": "new"})
    assert r.status_code == 409
    assert (media / "old.jpg").exists()


def test_rename_bad_names_400(client, media):
    for bad in ["", "  ", "a/b", "a\\b", "..", "x" * 101]:
        r = client.post("/api/media/old.jpg/rename", json={"new_name": bad})
        assert r.status_code == 400, bad


def test_rename_missing_file_404(client, media):
    r = client.post("/api/media/nope.jpg/rename", json={"new_name": "new"})
    assert r.status_code == 404


def test_rename_same_name_noop(client, media):
    r = client.post("/api/media/old.jpg/rename", json={"new_name": "old"})
    assert r.json() == {"ok": True, "name": "old.jpg"}
    assert (media / "old.jpg").exists()
