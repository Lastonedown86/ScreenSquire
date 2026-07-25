import json

def test_detach_removes_playlist_items(agent_module, client, media):
    agent_module.state.playlist.items = [
        agent_module.PlaylistItem(type="image", source="old.jpg"),
        agent_module.PlaylistItem(type="url", source="https://example.com"),
        agent_module.PlaylistItem(type="image", source="old.jpg"),
    ]
    before = agent_module.state.version
    r = client.post("/api/media/old.jpg/detach").json()
    assert r == {"ok": True, "playlist_removed": 2, "boards_cleared": 0}
    assert [i.source for i in agent_module.state.playlist.items] == ["https://example.com"]
    assert agent_module.state.version == before + 1
    saved = json.loads(agent_module.PLAYLIST_FILE.read_text())
    assert len(saved["items"]) == 1


def test_detach_clears_dashboard_boards(agent_module, client, media, monkeypatch):
    monkeypatch.setattr(agent_module, "_dashboard", {"view_data": {"boards": {
        "left": "/media/old.jpg", "right": "/media/other.jpg"}}})
    r = client.post("/api/media/old.jpg/detach").json()
    assert r["boards_cleared"] == 1
    assert agent_module._dashboard["view_data"]["boards"] == {"right": "/media/other.jpg"}
    saved = json.loads(agent_module.DASHBOARD_FILE.read_text())
    assert saved["view_data"]["boards"] == {"right": "/media/other.jpg"}


def test_detach_then_delete_succeeds(agent_module, client, media):
    agent_module.state.playlist.items = [agent_module.PlaylistItem(type="image", source="old.jpg")]
    assert client.delete("/api/media/old.jpg").status_code == 409
    client.post("/api/media/old.jpg/detach")
    assert client.delete("/api/media/old.jpg").status_code == 200
    assert not (media / "old.jpg").exists()


def test_detach_unused_file_noop(client, media):
    r = client.post("/api/media/old.jpg/detach").json()
    assert r == {"ok": True, "playlist_removed": 0, "boards_cleared": 0}
