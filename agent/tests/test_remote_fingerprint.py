import asyncio
import hashlib

from test_remote_desktop import FakeProc, _install_fake_spawn


def _fake_openssl_output(n: bytes, e: int) -> str:
    return (
        "Private-Key: (2048 bit, 2 primes)\n"
        f"publicExponent: {e} (0x{e:x})\n"
        f"Modulus={n.hex().upper()}\n"
    )


def test_fingerprint_matches_tigervnc_format(agent_module):
    # 2048-bit-style modulus with high bit set; expected value computed the way
    # TigerVNC's verifyServer() does: sha1(u32 bits || N || E padded to N size)
    n = bytes([0x80] + list(range(1, 256)))
    e = 65537
    expected = "-".join(
        f"{b:02x}"
        for b in hashlib.sha1(
            (len(n) * 8).to_bytes(4, "big") + n + e.to_bytes(len(n), "big")
        ).digest()[:8]
    )
    got = agent_module._tigervnc_fingerprint(_fake_openssl_output(n, e))
    assert got == expected
    assert len(got) == 23 and got.count("-") == 7  # xx-xx-xx-xx-xx-xx-xx-xx


def test_fingerprint_handles_garbage(agent_module):
    assert agent_module._tigervnc_fingerprint("") is None
    assert agent_module._tigervnc_fingerprint("Modulus=nothex") is None
    assert agent_module._tigervnc_fingerprint("publicExponent: x (0x3)") is None


def test_start_response_includes_fingerprint(agent_module, signed, monkeypatch):
    agent_module._remote_proc = None
    agent_module._remote_creds = None
    agent_module._remote_idle_task = None
    _install_fake_spawn(agent_module, monkeypatch, FakeProc(returncode=None))

    async def fake_fp():
        return "aa-bb-cc-dd-ee-ff-00-11"

    monkeypatch.setattr(agent_module, "_wayvnc_fingerprint", fake_fp)
    r = signed("POST", "/api/remote-desktop", json={"running": True}).json()
    assert r["fingerprint"] == "aa-bb-cc-dd-ee-ff-00-11"
    # cleanup so later tests see no live session
    agent_module._remote_proc = None
    agent_module._remote_creds = None


def test_missing_openssl_degrades_to_null(agent_module, monkeypatch):
    async def failing_run(cmd, timeout=15.0):
        raise FileNotFoundError("openssl")

    monkeypatch.setattr(agent_module, "_run", failing_run)
    assert asyncio.run(agent_module._wayvnc_fingerprint()) is None
