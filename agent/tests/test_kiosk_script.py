import asyncio
import os


def _capture_run(calls, is_active_out="active\n"):
    async def _run(cmd, timeout=15.0):
        calls.append(cmd)
        if "is-active" in cmd:
            return (0, is_active_out, "")
        return (0, "", "")
    return _run


def _sync(agent_module):
    asyncio.run(agent_module._sync_kiosk_script())


def test_no_script_means_no_op(agent_module, monkeypatch, tmp_path):
    calls = []
    monkeypatch.setattr(agent_module, "KIOSK_SH_PATH", tmp_path / "kiosk.sh")
    monkeypatch.setattr(agent_module, "_run", _capture_run(calls))
    _sync(agent_module)
    assert not (tmp_path / "kiosk.sh").exists()
    assert calls == []


def test_stale_script_rewritten_and_kiosk_restarted(agent_module, monkeypatch, tmp_path):
    path = tmp_path / "kiosk.sh"
    path.write_text("#!/usr/bin/env bash\n# old script without the keyring flag\n")
    calls = []
    monkeypatch.setattr(agent_module, "KIOSK_SH_PATH", path)
    monkeypatch.setattr(agent_module, "_run", _capture_run(calls))
    _sync(agent_module)
    assert path.read_text() == agent_module.KIOSK_SH
    assert "--password-store=basic" in path.read_text()
    assert ["systemctl", "--user", "restart", agent_module.KIOSK_UNIT] in calls
    if os.name == "posix":
        assert os.stat(path).st_mode & 0o111


def test_stale_script_rewritten_but_stopped_kiosk_left_alone(agent_module, monkeypatch, tmp_path):
    path = tmp_path / "kiosk.sh"
    path.write_text("old\n")
    calls = []
    monkeypatch.setattr(agent_module, "KIOSK_SH_PATH", path)
    monkeypatch.setattr(agent_module, "_run", _capture_run(calls, is_active_out="inactive\n"))
    _sync(agent_module)
    assert path.read_text() == agent_module.KIOSK_SH
    assert not any("restart" in c for c in calls)


def test_current_script_untouched(agent_module, monkeypatch, tmp_path):
    path = tmp_path / "kiosk.sh"
    path.write_text(agent_module.KIOSK_SH)
    calls = []
    monkeypatch.setattr(agent_module, "KIOSK_SH_PATH", path)
    monkeypatch.setattr(agent_module, "_run", _capture_run(calls))
    _sync(agent_module)
    assert calls == []


def test_template_matches_install_sh():
    # install.sh writes the first copy; the agent heals it afterwards.
    # If the two drift, fresh Pis get a pointless kiosk restart on first boot.
    from pathlib import Path
    install = Path(__file__).resolve().parents[2] / "pi-setup" / "install.sh"
    if not install.exists():
        return  # agent deployed standalone (no repo checkout) — nothing to compare
    text = install.read_text(encoding="utf-8")
    import main
    start = text.index("#!/usr/bin/env bash\n# Wait for the agent")
    end = text.index("\nEOF\n", start)
    assert text[start:end + 1] == main.KIOSK_SH
