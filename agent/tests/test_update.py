def test_status_reports_agent_version(agent_module, client):
    body = client.get("/api/status").json()
    assert body["agent_version"] == agent_module.AGENT_VERSION
    assert agent_module.AGENT_VERSION  # non-empty


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


def _post(signed, data: bytes):
    return signed(
        "POST",
        "/api/update",
        files={"file": ("update.zip", data, "application/zip")},
    )


def _fake_app_dir(agent_module, tmp_path, monkeypatch):
    (tmp_path / "static").mkdir()
    (tmp_path / "main.py").write_text("OLD = 1\n")
    (tmp_path / "static" / "kiosk.html").write_text("<old>")
    monkeypatch.setattr(agent_module, "APP_DIR", tmp_path)
    async def no_restart():
        pass
    monkeypatch.setattr(agent_module, "_restart_after_update", no_restart)
    return tmp_path


def test_update_rejects_non_zip(agent_module, signed, tmp_path, monkeypatch):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    r = _post(signed, b"this is not a zip")
    assert r.status_code == 400


def test_update_rejects_traversal_and_stray_files(agent_module, signed, tmp_path, monkeypatch):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    for bad in ("../evil.py", "requirements.txt", "static/../../evil.py"):
        r = _post(signed, _zip_bytes({"main.py": GOOD_MAIN, bad: "x"}))
        assert r.status_code == 400, bad
    assert (tmp_path / "main.py").read_text() == "OLD = 1\n"  # untouched


def test_update_rejects_traversal_directory_entry(agent_module, signed, tmp_path, monkeypatch):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        zf.writestr("main.py", GOOD_MAIN)
        zf.writestr(zipfile.ZipInfo("static/../../evil/"), "")  # pure directory entry
    r = _post(signed, buf.getvalue())
    assert r.status_code == 400
    assert (tmp_path / "main.py").read_text() == "OLD = 1\n"  # untouched


def test_update_requires_main_py(agent_module, signed, tmp_path, monkeypatch):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    assert _post(signed, _zip_bytes({"static/kiosk.html": "<new>"})).status_code == 400


def test_update_rejects_oversize(agent_module, signed, tmp_path, monkeypatch):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    monkeypatch.setattr(agent_module, "_UPDATE_MAX_BYTES", 10)
    assert _post(signed, _zip_bytes({"main.py": GOOD_MAIN * 100})).status_code == 400


def test_update_rejects_syntax_error(agent_module, signed, tmp_path, monkeypatch):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    r = _post(signed, _zip_bytes({"main.py": "def broken(:\n"}))
    assert r.status_code == 400
    assert (tmp_path / "main.py").read_text() == "OLD = 1\n"  # untouched


def test_update_happy_path_swaps_files_and_backs_up(agent_module, signed, tmp_path, monkeypatch):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    r = _post(signed, _zip_bytes({"main.py": GOOD_MAIN, "static/kiosk.html": "<new>"}))
    assert r.status_code == 200
    assert r.json() == {"ok": True, "version": "9999.01.01.1"}
    assert (tmp_path / "main.py").read_text() == GOOD_MAIN
    assert (tmp_path / "static" / "kiosk.html").read_text() == "<new>"
    assert (tmp_path / "update-backup" / "main.py").read_text() == "OLD = 1\n"
    assert (tmp_path / "update-backup" / "static" / "kiosk.html").read_text() == "<old>"
    # temp extraction dirs cleaned up
    assert not [p for p in tmp_path.iterdir() if p.name.startswith("update-") and p.name != "update-backup"]
