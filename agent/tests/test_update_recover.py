"""pi-setup/update-recover.sh runs as ExecStartPre= on every agent start.

Contract with main.py's /api/update: the marker `update-pending` (holding the
count of starts attempted since the update) is written before the swap and
deleted by a healthy startup. While it exists this script counts starts, and
once MAX_ATTEMPTS starts have failed to clear it, restores `update-backup`.
"""

import shutil
import subprocess
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parents[2] / "pi-setup" / "update-recover.sh"

ROOT_FILES = ["main.py", "trust.py", "control_auth.py", "delivery_reset.py"]

pytestmark = pytest.mark.skipif(
    shutil.which("bash") is None, reason="bash not available"
)


def _run(agent_dir: Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["bash", SCRIPT.as_posix(), agent_dir.as_posix()],
        capture_output=True,
        text=True,
        timeout=30,
    )


@pytest.fixture
def agent_dir(tmp_path):
    for name in ROOT_FILES:
        (tmp_path / name).write_text("NEW")
    (tmp_path / "static").mkdir()
    (tmp_path / "static" / "kiosk.html").write_text("NEW")
    (tmp_path / "static" / "added-by-update.html").write_text("NEW")
    backup = tmp_path / "update-backup"
    backup.mkdir()
    for name in ROOT_FILES:
        (backup / name).write_text("OLD")
    (backup / "static").mkdir()
    (backup / "static" / "kiosk.html").write_text("OLD")
    return tmp_path


def test_no_marker_changes_nothing(agent_dir):
    result = _run(agent_dir)
    assert result.returncode == 0
    assert (agent_dir / "main.py").read_text() == "NEW"
    assert (agent_dir / "update-backup").is_dir()


def test_counts_the_start_attempt_below_the_limit(agent_dir):
    (agent_dir / "update-pending").write_text("0\n")
    result = _run(agent_dir)
    assert result.returncode == 0
    assert (agent_dir / "update-pending").read_text().strip() == "1"
    assert (agent_dir / "main.py").read_text() == "NEW"


def test_restores_the_backup_after_three_failed_starts(agent_dir):
    (agent_dir / "update-pending").write_text("3\n")
    result = _run(agent_dir)
    assert result.returncode == 0
    for name in ROOT_FILES:
        assert (agent_dir / name).read_text() == "OLD"
    assert (agent_dir / "static" / "kiosk.html").read_text() == "OLD"
    # the update's static tree is replaced wholesale, not merged
    assert not (agent_dir / "static" / "added-by-update.html").exists()
    assert not (agent_dir / "update-pending").exists()


def test_three_runs_then_recovery(agent_dir):
    """The full crash-loop: marker starts at 0, three failed starts, the
    fourth start restores."""
    (agent_dir / "update-pending").write_text("0\n")
    for expected in ("1", "2", "3"):
        assert _run(agent_dir).returncode == 0
        assert (agent_dir / "update-pending").read_text().strip() == expected
        assert (agent_dir / "main.py").read_text() == "NEW"
    assert _run(agent_dir).returncode == 0
    assert (agent_dir / "main.py").read_text() == "OLD"
    assert not (agent_dir / "update-pending").exists()


def test_unreadable_marker_recovers_immediately(agent_dir):
    (agent_dir / "update-pending").write_text("not a number")
    result = _run(agent_dir)
    assert result.returncode == 0
    assert (agent_dir / "main.py").read_text() == "OLD"
    assert not (agent_dir / "update-pending").exists()


def test_marker_without_backup_is_cleared_without_touching_files(agent_dir):
    shutil.rmtree(agent_dir / "update-backup")
    (agent_dir / "update-pending").write_text("3\n")
    result = _run(agent_dir)
    assert result.returncode == 0
    assert (agent_dir / "main.py").read_text() == "NEW"
    assert not (agent_dir / "update-pending").exists()


def test_missing_agent_dir_exits_cleanly(tmp_path):
    result = _run(tmp_path / "does-not-exist")
    assert result.returncode == 0
