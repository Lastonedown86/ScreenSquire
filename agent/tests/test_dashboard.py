import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
import main

client = TestClient(main.app)


def test_agent_import_uses_test_data_dir(agent_module):
    repository_data = Path(__file__).resolve().parents[1] / "data"
    assert agent_module.DATA_DIR != repository_data
    assert agent_module.DASHBOARD_FILE.parent == agent_module.DATA_DIR

@pytest.fixture(autouse=True)
def _reset_dashboard_state():
    main._dashboard = {"view_data": {"boards": {}}, "timer": {"state": "stopped"}}
    yield

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

def test_board_push_preserves_running_timer_endsat():
    client.post("/api/dashboard", json={"view_data": {"boards": {}},
        "timer": {"state": "running", "remaining": 1500, "round": 1, "label": "Round 1"}})
    e1 = client.get("/api/dashboard").json()["timer"]["endsAt"]
    # a board push mid-round re-posts the same running timer:
    client.post("/api/dashboard", json={"view_data": {"boards": {"pairings": "/media/p-1.png"}},
        "timer": {"state": "running", "remaining": 1500, "round": 1, "label": "Round 1"}})
    e2 = client.get("/api/dashboard").json()["timer"]["endsAt"]
    assert e2 == e1  # countdown NOT reset

def test_expired_timer_reanchors_on_repost():
    # a stored running timer whose endsAt already passed must not "resume" as TIME
    main._dashboard = {"view_data": {"boards": {}},
        "timer": {"state": "running", "endsAt": int(time.time() * 1000) - 60000,
                  "remaining": 1500, "round": 1, "label": "Round 1"}}
    client.post("/api/dashboard", json={"view_data": {"boards": {}},
        "timer": {"state": "running", "remaining": 1500, "round": 1, "label": "Round 1"}})
    ends = client.get("/api/dashboard").json()["timer"]["endsAt"]
    assert ends > int(time.time() * 1000)   # re-anchored into the future, not kept expired


def test_new_round_reanchors_timer():
    client.post("/api/dashboard", json={"view_data": {"boards": {}},
        "timer": {"state": "running", "remaining": 1500, "round": 1, "label": "Round 1"}})
    e1 = client.get("/api/dashboard").json()["timer"]["endsAt"]
    client.post("/api/dashboard", json={"view_data": {"boards": {}},
        "timer": {"state": "running", "remaining": 1500, "round": 2, "label": "Round 2"}})
    e2 = client.get("/api/dashboard").json()["timer"]["endsAt"]
    assert e2 >= e1  # re-anchored from now because the round changed

def test_partial_board_push_merges():
    client.post("/api/dashboard", json={"view_data": {"boards": {"standings": "/media/s-1.png"}},
        "timer": {"state": "stopped"}})
    client.post("/api/dashboard", json={"view_data": {"boards": {"pairings": "/media/p-9.png"}},
        "timer": {"state": "stopped"}})
    boards = client.get("/api/dashboard").json()["view_data"]["boards"]
    assert boards["standings"] == "/media/s-1.png"
    assert boards["pairings"] == "/media/p-9.png"

def test_dashboard_page_pulses_time_up():
    html = client.get("/dashboard").text
    assert "timepulse" in html   # TIME pulses so it reads across the room
