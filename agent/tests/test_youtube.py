import asyncio
import json
import time


def _reset_override(agent_module):
    agent_module.state.override = None
    agent_module.state.override_until = None
    agent_module.state.override_id = None


def test_item_payload_youtube(agent_module):
    item = agent_module.PlaylistItem(type="youtube", source="2B_L3WsMqTc", id="deadbeef")
    assert agent_module._item_payload(item) == {
        "type": "youtube",
        "videoId": "2B_L3WsMqTc",
        "id": "deadbeef",
    }


def test_show_now_youtube_sets_override(agent_module, signed):
    r = signed("POST", "/api/show-now",
               json={"type": "youtube", "source": "2B_L3WsMqTc", "volume": 60})
    assert r.status_code == 200
    ov = agent_module.state.override
    assert ov is not None and ov.type == "youtube" and ov.volume == 60
    assert agent_module.state.override_id  # stable id assigned
    _reset_override(agent_module)


def test_show_now_youtube_clears_stale_player_state(agent_module, signed):
    agent_module._youtube_state = {"state": "ended", "videoId": "2B_L3WsMqTc"}
    r = signed("POST", "/api/show-now",
               json={"type": "youtube", "source": "2B_L3WsMqTc"})
    assert r.status_code == 200
    assert agent_module._youtube_state is None
    _reset_override(agent_module)


def test_show_now_youtube_rejects_bad_id(agent_module, signed):
    r = signed("POST", "/api/show-now",
               json={"type": "youtube", "source": "not-a-valid-id!"})
    assert r.status_code == 400
    assert agent_module.state.override is None


def test_playlist_rejects_bad_youtube_id(agent_module, signed, media):
    r = signed("PUT", "/api/playlist", json={
        "items": [{"type": "youtube", "source": "tooShort", "duration": 10}],
        "enabled": True,
    })
    assert r.status_code == 400


def test_playlist_accepts_youtube_item(agent_module, signed, media):
    r = signed("PUT", "/api/playlist", json={
        "items": [{"type": "youtube", "source": "2B_L3WsMqTc", "duration": 10}],
        "enabled": True,
    })
    assert r.status_code == 200


def test_control_requires_signature(client):
    r = client.post("/api/youtube/control", json={"action": "pause"})
    assert r.status_code in (401, 403)


def test_control_broadcasts_without_touching_current(agent_module, signed, monkeypatch):
    sent = []

    async def fake_broadcast(payload):
        sent.append(payload)

    monkeypatch.setattr(agent_module.hub, "broadcast", fake_broadcast)
    before = agent_module.hub.current
    r = signed("POST", "/api/youtube/control", json={"action": "pause"})
    assert r.status_code == 200
    assert sent == [{"type": "yt-control", "action": "pause", "value": None}]
    assert agent_module.hub.current is before


def test_control_volume_requires_valid_value(signed):
    assert signed("POST", "/api/youtube/control",
                  json={"action": "volume"}).status_code == 400
    assert signed("POST", "/api/youtube/control",
                  json={"action": "volume", "value": 150}).status_code == 400
    assert signed("POST", "/api/youtube/control",
                  json={"action": "volume", "value": 60}).status_code == 200


def test_control_seek_requires_value(signed):
    assert signed("POST", "/api/youtube/control",
                  json={"action": "seek"}).status_code == 400
    assert signed("POST", "/api/youtube/control",
                  json={"action": "seek", "value": -10}).status_code == 200


def test_ws_reports_state_into_status(agent_module, client):
    with client.websocket_connect("/ws") as ws:
        ws.receive_json()  # catch-up frame
        ws.send_text("ping")  # keepalive must be ignored, not crash
        ws.send_text(json.dumps(
            {"type": "yt-state", "state": "paused", "videoId": "2B_L3WsMqTc"}))
        # the handler runs on the app loop; poll briefly for it to land
        for _ in range(50):
            got = client.get("/api/status").json()["youtube_state"]
            if got == {"state": "paused", "videoId": "2B_L3WsMqTc"}:
                break
            time.sleep(0.05)
        else:
            raise AssertionError(f"yt-state never surfaced: {got}")


def test_backstop_clears_only_the_same_override(agent_module, monkeypatch):
    monkeypatch.setattr(agent_module, "_YT_BACKSTOP_SECONDS", 0)
    ended = agent_module.ShowNowRequest(type="youtube", source="2B_L3WsMqTc")
    agent_module.state.override = ended
    agent_module.state.override_id = "aaaaaaaa"
    asyncio.run(agent_module._yt_backstop(ended))
    assert agent_module.state.override is None
    assert agent_module.state.override_id is None

    # a stale "ended" from the previous video must not clear the next one
    nxt = agent_module.ShowNowRequest(type="youtube", source="Ks-_Mh1QhMc")
    agent_module.state.override = nxt
    agent_module.state.override_id = "bbbbbbbb"
    asyncio.run(agent_module._yt_backstop(ended))
    assert agent_module.state.override is nxt
    _reset_override(agent_module)
