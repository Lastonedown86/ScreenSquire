import asyncio
import json
import time

URI = "spotify:track:4uLU6hMCjMI75M1A2tKUQC"


def _reset_override(agent_module):
    agent_module.state.override = None
    agent_module.state.override_until = None
    agent_module.state.override_id = None


def test_item_payload_spotify(agent_module):
    item = agent_module.PlaylistItem(type="spotify", source=URI, id="deadbeef")
    assert agent_module._item_payload(item) == {
        "type": "spotify",
        "uri": URI,
        "id": "deadbeef",
    }


def test_show_now_spotify_sets_override(agent_module, signed):
    r = signed("POST", "/api/show-now", json={"type": "spotify", "source": URI})
    assert r.status_code == 200
    ov = agent_module.state.override
    assert ov is not None and ov.type == "spotify" and ov.source == URI
    assert agent_module.state.override_id
    _reset_override(agent_module)


def test_show_now_spotify_clears_stale_player_state(agent_module, signed):
    agent_module._spotify_state = {"state": "ended", "uri": URI}
    r = signed("POST", "/api/show-now", json={"type": "spotify", "source": URI})
    assert r.status_code == 200
    assert agent_module._spotify_state is None
    _reset_override(agent_module)


def test_show_now_spotify_rejects_bad_uri(agent_module, signed):
    for source in (
        "https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC",  # URL, not URI
        "spotify:track:short",
        "spotify:bogus:4uLU6hMCjMI75M1A2tKUQC",
        "spotify:track:4uLU6hMCjMI75M1A2tKU_C",  # base62 has no underscore
    ):
        r = signed("POST", "/api/show-now", json={"type": "spotify", "source": source})
        assert r.status_code == 400, source
    assert agent_module.state.override is None


def test_playlist_validates_spotify_uri(agent_module, signed, media):
    bad = signed("PUT", "/api/playlist", json={
        "items": [{"type": "spotify", "source": "spotify:track:nope", "duration": 10}],
        "enabled": True,
    })
    assert bad.status_code == 400
    ok = signed("PUT", "/api/playlist", json={
        "items": [{"type": "spotify", "source": URI, "duration": 10}],
        "enabled": True,
    })
    assert ok.status_code == 200


def test_control_requires_signature(client):
    r = client.post("/api/spotify/control", json={"action": "pause"})
    assert r.status_code in (401, 403)


def test_control_broadcasts_without_touching_current(agent_module, signed, monkeypatch):
    sent = []

    async def fake_broadcast(payload):
        sent.append(payload)

    monkeypatch.setattr(agent_module.hub, "broadcast", fake_broadcast)
    before = agent_module.hub.current
    r = signed("POST", "/api/spotify/control", json={"action": "pause"})
    assert r.status_code == 200
    assert sent == [{"type": "sp-control", "action": "pause", "value": None}]
    assert agent_module.hub.current is before


def test_control_seek_requires_value(signed):
    assert signed("POST", "/api/spotify/control",
                  json={"action": "seek"}).status_code == 400
    assert signed("POST", "/api/spotify/control",
                  json={"action": "seek", "value": -10}).status_code == 200


def test_control_rejects_unknown_action(signed):
    # the Embed API has no volume control — the endpoint must not accept it
    assert signed("POST", "/api/spotify/control",
                  json={"action": "volume", "value": 50}).status_code == 422


def test_ws_reports_state_into_status(agent_module, client):
    with client.websocket_connect("/ws") as ws:
        ws.receive_json()  # catch-up frame
        ws.send_text("ping")  # keepalive still ignored
        ws.send_text(json.dumps({"type": "sp-state", "state": "paused", "uri": URI}))
        for _ in range(50):
            got = client.get("/api/status").json()["spotify_state"]
            if got == {"state": "paused", "uri": URI}:
                break
            time.sleep(0.05)
        else:
            raise AssertionError(f"sp-state never surfaced: {got}")


def test_backstop_clears_spotify_override(agent_module, monkeypatch):
    monkeypatch.setattr(agent_module, "_YT_BACKSTOP_SECONDS", 0)
    ended = agent_module.ShowNowRequest(type="spotify", source=URI)
    agent_module.state.override = ended
    agent_module.state.override_id = "cccccccc"
    asyncio.run(agent_module._yt_backstop(ended))
    assert agent_module.state.override is None

    # a stale "ended" must not clear a different override
    nxt = agent_module.ShowNowRequest(type="spotify", source=URI)
    agent_module.state.override = nxt
    asyncio.run(agent_module._yt_backstop(ended))
    assert agent_module.state.override is nxt
    _reset_override(agent_module)
