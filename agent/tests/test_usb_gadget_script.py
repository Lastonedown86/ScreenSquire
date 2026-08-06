"""pi-setup/usb-gadget-ncm.sh brings up the USB onboarding network at boot.

It is the front door of Store onboarding: if the gadget does not bind, the
customer can never reach 10.55.0.1. The UDC can appear after the service
starts, so the script must wait for one — and rebind on a re-run, because a
run that lost that race leaves the gadget directory created but unbound.

The script honors SYS_ROOT (fake /sys for tests) and UDC_WAIT_TRIES, and the
tests stub modprobe/ip/udevadm/sleep through PATH.
"""

import os
import shutil
import stat
import subprocess
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parents[2] / "pi-setup" / "usb-gadget-ncm.sh"

GADGET = "sys/kernel/config/usb_gadget/pisignage"
UDC_CLASS = "sys/class/udc"

pytestmark = pytest.mark.skipif(
    shutil.which("bash") is None, reason="bash not available"
)


def _stub(bin_dir: Path, name: str, body: str = "exit 0") -> None:
    path = bin_dir / name
    path.write_text(f"#!/usr/bin/env bash\n{body}\n")
    path.chmod(path.stat().st_mode | stat.S_IEXEC)


@pytest.fixture
def fake_sys(tmp_path):
    (tmp_path / UDC_CLASS).mkdir(parents=True)
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    # `ln` is stubbed too: creating configfs-style symlinks needs privileges
    # Windows lacks, and no test asserts on the link itself
    for tool in ("modprobe", "ip", "udevadm", "ln"):
        _stub(bin_dir, tool)
    return tmp_path


def _run(fake_sys: Path, tries: str = "1") -> subprocess.CompletedProcess:
    env = {
        **os.environ,
        "PATH": f"{fake_sys / 'bin'}{os.pathsep}{os.environ['PATH']}",
        "SYS_ROOT": fake_sys.as_posix(),
        "UDC_WAIT_TRIES": tries,
    }
    return subprocess.run(
        ["bash", SCRIPT.as_posix()],
        capture_output=True,
        text=True,
        timeout=60,
        env=env,
    )


def test_fresh_boot_with_a_ready_udc_creates_and_binds_the_gadget(fake_sys):
    (fake_sys / UDC_CLASS / "fe980000.usb").mkdir()

    result = _run(fake_sys)

    assert result.returncode == 0, result.stderr
    gadget = fake_sys / GADGET
    assert (gadget / "idVendor").read_text().strip() == "0x1d6b"
    assert (gadget / "UDC").read_text().strip() == "fe980000.usb"


def test_no_udc_fails_loudly_instead_of_silently_not_binding(fake_sys):
    result = _run(fake_sys)

    assert result.returncode != 0
    assert "dtoverlay" in result.stderr


def test_rerun_rebinds_a_gadget_left_unbound_by_a_lost_boot_race(fake_sys):
    """The historical strand: gadget directory exists, UDC write was a no-op.
    A later run (manual restart, next boot) must bind rather than short-circuit
    on the existing directory."""
    gadget = fake_sys / GADGET
    (gadget / "functions" / "ncm.usb0").mkdir(parents=True)
    (gadget / "UDC").write_text("\n")
    (fake_sys / UDC_CLASS / "fe980000.usb").mkdir()

    result = _run(fake_sys)

    assert result.returncode == 0, result.stderr
    assert (gadget / "UDC").read_text().strip() == "fe980000.usb"


def test_rerun_leaves_an_already_bound_gadget_alone(fake_sys):
    gadget = fake_sys / GADGET
    (gadget / "functions" / "ncm.usb0").mkdir(parents=True)
    (gadget / "UDC").write_text("fe980000.usb\n")

    # no entries under sys/class/udc: a rebind attempt would fail, proving the
    # script did not touch the existing binding
    result = _run(fake_sys)

    assert result.returncode == 0, result.stderr
    assert (gadget / "UDC").read_text().strip() == "fe980000.usb"


def test_waits_for_a_udc_that_appears_late(fake_sys):
    # the stubbed `sleep` stands in for time passing: the UDC registers while
    # the script is inside its wait loop
    _stub(
        fake_sys / "bin",
        "sleep",
        f"mkdir -p {(fake_sys / UDC_CLASS / 'fe980000.usb').as_posix()}",
    )

    result = _run(fake_sys, tries="3")

    assert result.returncode == 0, result.stderr
    assert (fake_sys / GADGET / "UDC").read_text().strip() == "fe980000.usb"
