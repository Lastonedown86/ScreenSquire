import time
from fastapi.testclient import TestClient
import main

client = TestClient(main.app)

def test_running_timer_gets_endsat_epoch_ms():
    before = int(time.time() * 1000)
    r = client.post("/api/dashboard", json={
        "view_data": {"boards": {}},
        "timer": {"state": "running", "remaining": 1500, "label": "Round 1"},
    })
    assert r.status_code == 200 and r.json()["ok"] is True
    ends = client.get("/api/dashboard").json()["timer"]["endsAt"]
    assert before + 1500 * 1000 <= ends <= before + 1500 * 1000 + 5000

def test_boards_roundtrip():
    client.post("/api/dashboard", json={
        "view_data": {"boards": {"pairings": "/media/pairings-3.png"}},
        "timer": {"state": "stopped"}})
    got = client.get("/api/dashboard").json()
    assert got["view_data"]["boards"]["pairings"] == "/media/pairings-3.png"

def test_dashboard_page_served():
    assert client.get("/dashboard").status_code == 200

def test_dashboard_page_has_views_and_poll():
    html = client.get("/dashboard").text
    assert "view-board" in html and "view-timer" in html
    assert "/api/dashboard" in html            # it polls
    assert "No pairings posted" in html or "Nothing posted" in html  # idle state
