import io
import stat
import warnings
import zipfile
from pathlib import Path

import pytest


GOOD_MAIN = 'AGENT_VERSION = "9999.01.01.1"\n'
GOOD_MODULES = {
    "main.py": GOOD_MAIN,
    "trust.py": "TRUST = True\n",
    "control_auth.py": "AUTH = True\n",
    "delivery_reset.py": "RESET = True\n",
}
GOOD_BUNDLE = {
    **GOOD_MODULES,
    "static/kiosk.html": "<new>",
    "static/dashboard.html": "<dashboard>",
}
OLD_MODULES = {
    name: f"OLD_{name.replace('.', '_')} = True\n"
    for name in GOOD_MODULES
}


def test_status_reports_agent_version(agent_module, client):
    body = client.get("/api/status").json()
    assert body["agent_version"] == agent_module.AGENT_VERSION
    assert agent_module.AGENT_VERSION


def _zip_entry_bytes(entries: list[tuple[str, str]]) -> bytes:
    buf = io.BytesIO()
    with warnings.catch_warnings():
        warnings.filterwarnings(
            "ignore",
            message="Duplicate name: .*",
            category=UserWarning,
            module="zipfile",
        )
        with zipfile.ZipFile(buf, "w") as zf:
            for name, text in entries:
                zf.writestr(name, text)
    return buf.getvalue()


def _zip_bytes(entries: dict[str, str]) -> bytes:
    return _zip_entry_bytes(list(entries.items()))


def _post(signed, data: bytes):
    return signed(
        "POST",
        "/api/update",
        files={"file": ("update.zip", data, "application/zip")},
    )


def _fake_app_dir(agent_module, tmp_path, monkeypatch):
    (tmp_path / "static").mkdir()
    for name, source in OLD_MODULES.items():
        (tmp_path / name).write_text(source)
    (tmp_path / "static" / "kiosk.html").write_text("<old>")
    (tmp_path / "static" / "dashboard.html").write_text("<old-dashboard>")
    (tmp_path / "static" / "removed.html").write_text("<stale>")
    monkeypatch.setattr(agent_module, "APP_DIR", tmp_path)

    async def no_restart():
        pass

    monkeypatch.setattr(agent_module, "_restart_after_update", no_restart)
    return tmp_path


def test_update_rejects_non_zip(agent_module, signed, tmp_path, monkeypatch):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    assert _post(signed, b"this is not a zip").status_code == 400


def test_update_requires_a_signed_controller(agent_module, client, tmp_path, monkeypatch):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    data = _zip_bytes(GOOD_BUNDLE)
    response = client.post(
        "/api/update",
        files={"file": ("update.zip", data, "application/zip")},
    )
    assert response.status_code == 401


def test_update_rejects_mismatched_zip_hash(
    agent_module,
    client,
    paired_signer,
    tmp_path,
    monkeypatch,
):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    data = _zip_bytes(GOOD_BUNDLE)
    headers = paired_signer("POST", "/api/update", b"different zip", counter=1)
    response = client.post(
        "/api/update",
        files={"file": ("update.zip", data, "application/zip")},
        headers=headers,
    )
    assert response.status_code == 400
    assert "hash" in response.json()["detail"].lower()


def test_update_rejects_traversal_and_stray_files(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    for bad in ("../evil.py", "requirements.txt", "static/../../evil.py"):
        response = _post(signed, _zip_bytes({**GOOD_BUNDLE, bad: "x"}))
        assert response.status_code == 400, bad
    assert (tmp_path / "main.py").read_text() == OLD_MODULES["main.py"]


def test_update_accepts_complete_bundle_and_exactly_replaces_static(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
):
    app_dir = _fake_app_dir(agent_module, tmp_path, monkeypatch)
    response = _post(signed, _zip_bytes(GOOD_BUNDLE))
    assert response.status_code == 200
    for name, source in GOOD_MODULES.items():
        assert (app_dir / name).read_text() == source
    assert (app_dir / "static" / "kiosk.html").read_text() == "<new>"
    assert not (app_dir / "static" / "removed.html").exists()


def test_update_rejects_traversal_directory_entry(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
):
    app_dir = _fake_app_dir(agent_module, tmp_path, monkeypatch)
    entries = list(GOOD_BUNDLE.items())
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        for name, source in entries:
            zf.writestr(name, source)
        zf.writestr(zipfile.ZipInfo("static/../../evil/"), "")
    response = _post(signed, buf.getvalue())
    assert response.status_code == 400
    assert (app_dir / "main.py").read_text() == OLD_MODULES["main.py"]


@pytest.mark.parametrize("missing", sorted(GOOD_MODULES))
def test_update_requires_every_root_module(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
    missing,
):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    entries = {name: value for name, value in GOOD_BUNDLE.items() if name != missing}
    response = _post(signed, _zip_bytes(entries))
    assert response.status_code == 400
    assert missing in response.json()["detail"]


@pytest.mark.parametrize(
    "missing",
    ["static/kiosk.html", "static/dashboard.html"],
)
def test_update_requires_each_nonempty_display_page(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
    missing,
):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    entries = {name: value for name, value in GOOD_BUNDLE.items() if name != missing}
    response = _post(signed, _zip_bytes(entries))
    assert response.status_code == 400
    assert missing in response.json()["detail"]


@pytest.mark.parametrize(
    "empty",
    ["static/kiosk.html", "static/dashboard.html"],
)
def test_update_rejects_empty_display_page(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
    empty,
):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    response = _post(
        signed,
        _zip_bytes({**GOOD_BUNDLE, empty: ""}),
    )
    assert response.status_code == 400
    assert empty in response.json()["detail"]


def test_update_rejects_static_directory_without_display_pages(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    entries = list(GOOD_MODULES.items()) + [("static/", "")]
    response = _post(signed, _zip_entry_bytes(entries))
    assert response.status_code == 400
    assert "static/kiosk.html" in response.json()["detail"]


def test_update_rejects_symlink_as_required_display_page(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    entries = [
        (name, source)
        for name, source in GOOD_BUNDLE.items()
        if name != "static/kiosk.html"
    ]
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        for name, source in entries:
            zf.writestr(name, source)
        link = zipfile.ZipInfo("static/kiosk.html")
        link.create_system = 3
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        zf.writestr(link, "dashboard.html")

    response = _post(signed, buf.getvalue())

    assert response.status_code == 400
    assert "regular" in response.json()["detail"].lower()


def test_update_rejects_expanded_archive_over_limit(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    monkeypatch.setattr(agent_module, "_UPDATE_MAX_BYTES", 10)
    entries = {**GOOD_BUNDLE, "main.py": GOOD_MAIN * 100}
    assert _post(signed, _zip_bytes(entries)).status_code == 400


def test_update_rejects_compressed_archive_at_limit_plus_one(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
):
    _fake_app_dir(agent_module, tmp_path, monkeypatch)
    data = _zip_bytes(GOOD_BUNDLE)
    monkeypatch.setattr(agent_module, "_UPDATE_MAX_COMPRESSED_BYTES", len(data) - 1)
    response = _post(signed, data)
    assert response.status_code == 400
    assert "compressed" in response.json()["detail"].lower()


def test_update_rejects_syntax_error(agent_module, signed, tmp_path, monkeypatch):
    app_dir = _fake_app_dir(agent_module, tmp_path, monkeypatch)
    response = _post(
        signed,
        _zip_bytes({**GOOD_BUNDLE, "main.py": "def broken(:\n"}),
    )
    assert response.status_code == 400
    assert (app_dir / "main.py").read_text() == OLD_MODULES["main.py"]


def test_update_compiles_every_uploaded_python_module_before_install(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
):
    app_dir = _fake_app_dir(agent_module, tmp_path, monkeypatch)
    response = _post(
        signed,
        _zip_bytes({**GOOD_BUNDLE, "trust.py": "def broken(:\n"}),
    )
    assert response.status_code == 400
    assert "trust.py" in response.json()["detail"]
    for name, source in OLD_MODULES.items():
        assert (app_dir / name).read_text() == source


@pytest.mark.parametrize(
    "extra_entries",
    [
        [("main.py", GOOD_MAIN)],
        [("./main.py", GOOD_MAIN)],
        [("static//kiosk.html", "<duplicate>")],
        [("static/conflict", "file"), ("static/conflict/child", "child")],
        [("static/conflict/", ""), ("static/conflict", "file")],
    ],
)
def test_update_rejects_duplicate_or_colliding_canonical_paths(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
    extra_entries,
):
    app_dir = _fake_app_dir(agent_module, tmp_path, monkeypatch)
    data = _zip_entry_bytes(list(GOOD_BUNDLE.items()) + extra_entries)

    response = _post(signed, data)

    assert response.status_code == 400
    detail = response.json()["detail"].lower()
    assert "duplicate" in detail or "canonical" in detail or "collid" in detail
    assert (app_dir / "main.py").read_text() == OLD_MODULES["main.py"]


@pytest.mark.parametrize("failed_target", ["delivery_reset.py", "static"])
def test_update_rolls_back_entire_bundle_when_a_swap_fails(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
    failed_target,
):
    app_dir = _fake_app_dir(agent_module, tmp_path, monkeypatch)
    real_replace = agent_module.os.replace
    injected = [False]

    def fail_once(source, destination):
        if Path(destination) == app_dir / failed_target and not injected[0]:
            injected[0] = True
            raise OSError(f"injected {failed_target} swap failure")
        return real_replace(source, destination)

    monkeypatch.setattr(agent_module.os, "replace", fail_once)

    response = _post(signed, _zip_bytes(GOOD_BUNDLE))

    assert injected[0]
    assert response.status_code == 500
    for name, source in OLD_MODULES.items():
        assert (app_dir / name).read_text() == source
    assert (app_dir / "static" / "kiosk.html").read_text() == "<old>"
    assert (app_dir / "static" / "removed.html").read_text() == "<stale>"


def test_update_happy_path_swaps_files_and_backs_up(
    agent_module,
    signed,
    tmp_path,
    monkeypatch,
):
    app_dir = _fake_app_dir(agent_module, tmp_path, monkeypatch)
    response = _post(signed, _zip_bytes(GOOD_BUNDLE))
    assert response.status_code == 200
    assert response.json() == {"ok": True, "version": "9999.01.01.1"}
    assert (app_dir / "main.py").read_text() == GOOD_MAIN
    assert (app_dir / "trust.py").read_text() == GOOD_MODULES["trust.py"]
    assert (app_dir / "static" / "kiosk.html").read_text() == "<new>"
    assert not (app_dir / "static" / "removed.html").exists()
    assert (app_dir / "update-backup" / "main.py").read_text() == OLD_MODULES["main.py"]
    assert (app_dir / "update-backup" / "trust.py").read_text() == OLD_MODULES["trust.py"]
    assert (app_dir / "update-backup" / "static" / "kiosk.html").read_text() == "<old>"
    assert not [
        path
        for path in app_dir.iterdir()
        if path.name.startswith("update-") and path.name != "update-backup"
    ]
