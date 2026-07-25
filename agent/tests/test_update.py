def test_status_reports_agent_version(agent_module, client):
    body = client.get("/api/status").json()
    assert body["agent_version"] == agent_module.AGENT_VERSION
    assert agent_module.AGENT_VERSION  # non-empty


import io
import zipfile
from pathlib import Path

GOOD_MAIN = 'AGENT_VERSION = "9999.01.01.1"\n'
GOOD_MODULES = {
    "main.py": GOOD_MAIN,
    "trust.py": "TRUST = True\n",
    "control_auth.py": "AUTH = True\n",
    "delivery_reset.py": "RESET = True\n",
}


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


def test_update_requires_a_signed_controller(agent_module, client, tmp_path, monkeypatch):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    data = _zip_bytes(GOOD_MODULES)
    r = client.post(
        "/api/update",
        files={"file": ("update.zip", data, "application/zip")},
    )
    assert r.status_code == 401


def test_update_rejects_mismatched_zip_hash(
    agent_module,
    client,
    paired_signer,
    tmp_path,
    monkeypatch,
):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    data = _zip_bytes(GOOD_MODULES)
    headers = paired_signer("POST", "/api/update", b"different zip", counter=1)
    r = client.post(
        "/api/update",
        files={"file": ("update.zip", data, "application/zip")},
        headers=headers,
    )
    assert r.status_code == 400
    assert "hash" in r.json()["detail"].lower()


def test_update_rejects_traversal_and_stray_files(agent_module, signed, tmp_path, monkeypatch):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    for bad in ("../evil.py", "requirements.txt", "static/../../evil.py"):
        r = _post(signed, _zip_bytes({"main.py": GOOD_MAIN, bad: "x"}))
        assert r.status_code == 400, bad
    assert (tmp_path / "main.py").read_text() == "OLD = 1\n"  # untouched


def test_update_accepts_only_expected_python_modules(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
):
    app_dir = _fake_app_dir(agent_module, tmp_path, monkeypatch)
    r = _post(signed, _zip_bytes(GOOD_MODULES))
    assert r.status_code == 200
    for name, source in GOOD_MODULES.items():
        assert (app_dir / name).read_text() == source


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


def test_update_rejects_compressed_archive_over_limit(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    data = _zip_bytes(GOOD_MODULES)
    monkeypatch.setattr(
        agent_module,
        "_UPDATE_MAX_COMPRESSED_BYTES",
        len(data) - 1,
        raising=False,
    )
    r = _post(signed, data)
    assert r.status_code == 400
    assert "compressed" in r.json()["detail"].lower()


def test_update_rejects_syntax_error(agent_module, signed, tmp_path, monkeypatch):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    r = _post(signed, _zip_bytes({"main.py": "def broken(:\n"}))
    assert r.status_code == 400
    assert (tmp_path / "main.py").read_text() == "OLD = 1\n"  # untouched


def test_update_compiles_every_uploaded_python_module_before_install(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
):
    app_dir = _fake_app_dir(agent_module, tmp_path, monkeypatch)
    entries = {**GOOD_MODULES, "trust.py": "def broken(:\n"}
    r = _post(signed, _zip_bytes(entries))
    assert r.status_code == 400
    assert "trust.py" in r.json()["detail"]
    assert (app_dir / "main.py").read_text() == "OLD = 1\n"
    assert not (app_dir / "trust.py").exists()


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
