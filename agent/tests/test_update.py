import main
from fastapi.testclient import TestClient

client = TestClient(main.app)


def test_status_reports_agent_version():
    body = client.get("/api/status").json()
    assert body["agent_version"] == main.AGENT_VERSION
    assert main.AGENT_VERSION  # non-empty


import io
import zipfile
from pathlib import Path

GOOD_MAIN = 'AGENT_VERSION = "9999.01.01.1"\n'


def _zip_bytes(entries: dict[str, str]) -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        for name, text in entries.items():
            zf.writestr(name, text)
    return buf.getvalue()


def _post(data: bytes):
    return client.post("/api/update", files={"file": ("update.zip", data, "application/zip")})


def _fake_app_dir(tmp_path, monkeypatch):
    (tmp_path / "static").mkdir()
    (tmp_path / "main.py").write_text("OLD = 1\n")
    (tmp_path / "static" / "kiosk.html").write_text("<old>")
    monkeypatch.setattr(main, "APP_DIR", tmp_path)
    async def no_restart():
        pass
    monkeypatch.setattr(main, "_restart_after_update", no_restart)
    return tmp_path


def test_update_rejects_non_zip(tmp_path, monkeypatch):
    _fake_app_dir(tmp_path, monkeypatch)
    r = _post(b"this is not a zip")
    assert r.status_code == 400


def test_update_rejects_traversal_and_stray_files(tmp_path, monkeypatch):
    _fake_app_dir(tmp_path, monkeypatch)
    for bad in ("../evil.py", "requirements.txt", "static/../../evil.py"):
        r = _post(_zip_bytes({"main.py": GOOD_MAIN, bad: "x"}))
        assert r.status_code == 400, bad
    assert (tmp_path / "main.py").read_text() == "OLD = 1\n"  # untouched


def test_update_requires_main_py(tmp_path, monkeypatch):
    _fake_app_dir(tmp_path, monkeypatch)
    assert _post(_zip_bytes({"static/kiosk.html": "<new>"})).status_code == 400


def test_update_rejects_oversize(tmp_path, monkeypatch):
    _fake_app_dir(tmp_path, monkeypatch)
    monkeypatch.setattr(main, "_UPDATE_MAX_BYTES", 10)
    assert _post(_zip_bytes({"main.py": GOOD_MAIN * 100})).status_code == 400


def test_update_rejects_syntax_error(tmp_path, monkeypatch):
    _fake_app_dir(tmp_path, monkeypatch)
    r = _post(_zip_bytes({"main.py": "def broken(:\n"}))
    assert r.status_code == 400
    assert (tmp_path / "main.py").read_text() == "OLD = 1\n"  # untouched


def test_update_happy_path_swaps_files_and_backs_up(tmp_path, monkeypatch):
    _fake_app_dir(tmp_path, monkeypatch)
    r = _post(_zip_bytes({"main.py": GOOD_MAIN, "static/kiosk.html": "<new>"}))
    assert r.status_code == 200
    assert r.json() == {"ok": True, "version": "9999.01.01.1"}
    assert (tmp_path / "main.py").read_text() == GOOD_MAIN
    assert (tmp_path / "static" / "kiosk.html").read_text() == "<new>"
    assert (tmp_path / "update-backup" / "main.py").read_text() == "OLD = 1\n"
    assert (tmp_path / "update-backup" / "static" / "kiosk.html").read_text() == "<old>"
    # temp extraction dirs cleaned up
    assert not [p for p in tmp_path.iterdir() if p.name.startswith("update-") and p.name != "update-backup"]
