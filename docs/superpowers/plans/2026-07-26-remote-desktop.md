# Remote Desktop (Phase 1, LAN) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** See and control a paired Pi's screen from the Windows app over the LAN, by having the agent run wayvnc on demand and the app launch a bundled TigerVNC viewer.

**Architecture:** The agent gains `POST/GET /api/remote-desktop`. POST (signed) starts/stops a wayvnc child process configured for RSA-AES auth with per-session credentials, auto-stopping after 15 min idle. The app adds a "Remote control" button that sends the signed start, then launches a bundled `vncviewer.exe` with the returned credentials in its environment; on viewer exit it sends the signed stop.

**Tech Stack:** Python 3.11 / FastAPI (agent), wayvnc (already on Raspberry Pi OS Bookworm), C#/.NET 8 WPF (app), TigerVNC `vncviewer.exe` (bundled), pytest + xUnit.

## Global Constraints

- Agent endpoint auth: signed mutations via `require_control_mutation` (paired-controller signature). Reads (`GET`) are unsigned, like `/api/kiosk`.
- Bump `AGENT_VERSION` in `agent/main.py` on any agent change (currently `2026.07.25.7`).
- wayvnc RSA-AES config keys: `enable_auth=true`, `username=`, `password=`, `rsa_private_key_file=` (no TLS certs). Invocation: `wayvnc --config <file> 0.0.0.0 5900`.
- TigerVNC viewer connects non-interactively with `SecurityTypes=RA2ne,RA2` and reads `VNC_USERNAME` / `VNC_PASSWORD` from its environment. Host arg form: `<ip>::5900`.
- App feature is gated to paired devices only — never offered for `ControlContext.LegacyUnsigned()`.
- Do not add new NuGet dependencies; the viewer is an external process, not a library.

---

## Part A — Agent side (independently shippable)

Mirrors the existing `/api/kiosk` pattern (`agent/main.py`, tests in `agent/tests/test_kiosk.py`). The wayvnc process launch is factored behind an injectable seam so tests never spawn a real process.

### Task A1: Remote-desktop lifecycle + endpoints in the agent

**Files:**
- Modify: `agent/main.py` (add near the `/api/kiosk` block, ~line 596; bump `AGENT_VERSION`)
- Test: `agent/tests/test_remote_desktop.py` (create)

**Interfaces:**
- Consumes: `require_control_mutation` dependency; `_run(cmd, timeout)`; `DATA_DIR`; `secrets` (already imported by trust flow — confirm import at top of `main.py`, add `import secrets` if absent).
- Produces:
  - `GET /api/remote-desktop` → `{"running": bool}`
  - `POST /api/remote-desktop` body `{"running": bool}` → `{"ok": bool, "running": bool, "error": str|None, "port"?: int, "username"?: str, "password"?: str}`
  - Module seam `async def _spawn_wayvnc(config_path: str) -> _WayvncProc` where `_WayvncProc` is any object exposing `returncode: int|None`, `terminate()`, `kill()`, `async wait()`, and `stderr` — tests monkeypatch this.

- [ ] **Step 1: Write the failing tests**

```python
# agent/tests/test_remote_desktop.py
import asyncio
import pytest


class FakeProc:
    def __init__(self, returncode=None, stderr=b""):
        self.returncode = returncode
        self._stderr = stderr
        self.terminated = False
        self.killed = False

    def terminate(self):
        self.terminated = True
        self.returncode = 0

    def kill(self):
        self.killed = True
        self.returncode = -9

    async def wait(self):
        return self.returncode

    class _Reader:
        def __init__(self, data): self._data = data
        async def read(self): return self._data

    @property
    def stderr(self):
        return FakeProc._Reader(self._stderr)


def _install_fake_spawn(agent_module, monkeypatch, proc):
    async def _spawn(config_path):
        return proc
    monkeypatch.setattr(agent_module, "_ensure_rsa_key", lambda: asyncio.sleep(0))
    monkeypatch.setattr(agent_module, "_spawn_wayvnc", _spawn)


def test_status_reports_stopped_by_default(agent_module, client, monkeypatch):
    monkeypatch.setattr(agent_module, "_remote_proc", None)
    assert client.get("/api/remote-desktop").json() == {"running": False}


def test_start_returns_credentials_and_runs(agent_module, signed, monkeypatch):
    _install_fake_spawn(agent_module, monkeypatch, FakeProc(returncode=None))
    r = signed("POST", "/api/remote-desktop", json={"running": True}).json()
    assert r["ok"] is True and r["running"] is True
    assert r["port"] == 5900
    assert r["username"] and r["password"]
    assert client_running(agent_module)


def client_running(agent_module):
    return agent_module._remote_proc is not None and agent_module._remote_proc.returncode is None


def test_start_requires_signature(agent_module, client, monkeypatch):
    _install_fake_spawn(agent_module, monkeypatch, FakeProc(returncode=None))
    assert client.post("/api/remote-desktop", json={"running": True}).status_code == 401


def test_stop_terminates_process(agent_module, signed, monkeypatch):
    proc = FakeProc(returncode=None)
    _install_fake_spawn(agent_module, monkeypatch, proc)
    signed("POST", "/api/remote-desktop", json={"running": True})
    r = signed("POST", "/api/remote-desktop", json={"running": False}).json()
    assert r == {"ok": True, "running": False, "error": None}
    assert proc.terminated is True
    assert agent_module._remote_proc is None


def test_start_failure_reports_stderr(agent_module, signed, monkeypatch):
    _install_fake_spawn(agent_module, monkeypatch, FakeProc(returncode=1, stderr=b"no wayland display"))
    r = signed("POST", "/api/remote-desktop", json={"running": True})
    assert r.status_code == 500
    assert "no wayland display" in r.json()["detail"]
    assert agent_module._remote_proc is None
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd agent && python -m pytest tests/test_remote_desktop.py -v`
Expected: FAIL — `/api/remote-desktop` not found (404) / attributes missing.

- [ ] **Step 3: Implement the endpoints and lifecycle**

Add `import secrets` at the top of `agent/main.py` if not present. Insert after the `/api/kiosk` block:

```python
# ---- remote desktop (wayvnc on demand, paired-signature gated) ----
WAYVNC_PORT = 5900
WAYVNC_IDLE_SECONDS = 15 * 60
_RSA_KEY_FILE = DATA_DIR / "wayvnc_rsa_key.pem"
_WAYVNC_CONFIG = DATA_DIR / "wayvnc.config"
_remote_proc = None          # asyncio subprocess (or FakeProc in tests)
_remote_creds = None         # dict returned to the app while a session is live
_remote_idle_task = None     # asyncio.Task that stops an idle session


class RemoteDesktopRequest(BaseModel):
    running: bool


async def _ensure_rsa_key() -> None:
    if not _RSA_KEY_FILE.exists():
        rc, _, err = await _run(
            ["ssh-keygen", "-t", "rsa", "-b", "2048", "-m", "PEM",
             "-f", str(_RSA_KEY_FILE), "-N", "", "-q"])
        if rc != 0:
            raise RuntimeError(f"could not generate wayvnc key: {err.strip()}")


def _write_wayvnc_config(username: str, password: str) -> None:
    _WAYVNC_CONFIG.write_text(
        "enable_auth=true\n"
        f"username={username}\n"
        f"password={password}\n"
        f"rsa_private_key_file={_RSA_KEY_FILE}\n")
    os.chmod(_WAYVNC_CONFIG, 0o600)


async def _spawn_wayvnc(config_path: str):
    # Seam: tests monkeypatch this so no real process is launched.
    return await asyncio.create_subprocess_exec(
        "wayvnc", "--config", config_path, "0.0.0.0", str(WAYVNC_PORT),
        stdout=asyncio.subprocess.DEVNULL,
        stderr=asyncio.subprocess.PIPE)


async def _stop_wayvnc() -> None:
    global _remote_proc, _remote_creds, _remote_idle_task
    if _remote_idle_task is not None:
        _remote_idle_task.cancel()
        _remote_idle_task = None
    if _remote_proc is not None:
        try:
            _remote_proc.terminate()
            await asyncio.wait_for(_remote_proc.wait(), timeout=5)
        except (ProcessLookupError, asyncio.TimeoutError):
            try:
                _remote_proc.kill()
            except ProcessLookupError:
                pass
        _remote_proc = None
    _remote_creds = None
    try:
        _WAYVNC_CONFIG.unlink()
    except FileNotFoundError:
        pass


async def _idle_stop_after(seconds: int) -> None:
    try:
        await asyncio.sleep(seconds)
        await _stop_wayvnc()
    except asyncio.CancelledError:
        pass


@app.get("/api/remote-desktop")
async def remote_desktop_status():
    running = _remote_proc is not None and _remote_proc.returncode is None
    return {"running": running}


@app.post("/api/remote-desktop", dependencies=[Depends(require_control_mutation)])
async def set_remote_desktop(req: RemoteDesktopRequest):
    global _remote_proc, _remote_creds, _remote_idle_task
    if not req.running:
        await _stop_wayvnc()
        return {"ok": True, "running": False, "error": None}
    if _remote_proc is not None and _remote_proc.returncode is None:
        return {"ok": True, "running": True, "error": None, **_remote_creds}
    try:
        await _ensure_rsa_key()
    except Exception as exc:
        raise HTTPException(500, f"Could not prepare remote desktop: {exc}")
    username = secrets.token_hex(4)
    password = secrets.token_urlsafe(12)
    _write_wayvnc_config(username, password)
    _remote_proc = await _spawn_wayvnc(str(_WAYVNC_CONFIG))
    await asyncio.sleep(0.5)   # give wayvnc a moment to bind or fail
    if _remote_proc.returncode is not None:
        err = (await _remote_proc.stderr.read()).decode("utf-8", "replace")[:200]
        await _stop_wayvnc()
        raise HTTPException(500, f"wayvnc failed to start: {err.strip()}")
    _remote_creds = {"port": WAYVNC_PORT, "username": username, "password": password}
    _remote_idle_task = asyncio.create_task(_idle_stop_after(WAYVNC_IDLE_SECONDS))
    return {"ok": True, "running": True, "error": None, **_remote_creds}
```

Bump `AGENT_VERSION` to the next date-stamped value.

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd agent && python -m pytest tests/test_remote_desktop.py -v`
Expected: PASS (5 tests). Note the 0.5s sleep runs in the failure test — acceptable.

- [ ] **Step 5: Run the full agent suite**

Run: `cd agent && python -m pytest -q`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add agent/main.py agent/tests/test_remote_desktop.py
git commit -m "feat(agent): remote desktop endpoint — wayvnc on demand"
```

### Task A2: Install-time note for wayvnc availability

**Files:**
- Modify: `pi-setup/install.sh` (packages line, ~line 18)

**Interfaces:**
- Consumes: nothing. Produces: guarantees `wayvnc` + `ssh-keygen` (openssh-client) present.

- [ ] **Step 1: Add wayvnc to the install packages**

wayvnc ships with the Bookworm desktop image but is not guaranteed on Lite; make it explicit. Edit the `apt-get install` line to include `wayvnc openssh-client`:

```bash
sudo apt-get install -y python3-venv chromium unzip wayvnc openssh-client || \
  sudo apt-get install -y python3-venv chromium-browser unzip wayvnc openssh-client
```

- [ ] **Step 2: Commit**

```bash
git add pi-setup/install.sh
git commit -m "build(pi): ensure wayvnc + ssh-keygen are installed"
```

---

## Part B — App side (independently shippable, depends on Part A's response shape)

### Task B1: ApiClient remote-desktop calls

**Files:**
- Modify: `windows-app/ApiClient.cs`; `windows-app/Models.cs`
- Test: `signage-core.Tests/ApiClientTests.cs`

**Interfaces:**
- Consumes: existing `SendJsonAsync`, `GetFromJsonAsync`, `ControlContext`.
- Produces:
  - `record RemoteDesktopSession(int Port, string Username, string Password)` in `Models.cs`
  - `Task<RemoteDesktopSession?> StartRemoteDesktopAsync(ControlContext ctx)` — POST `{"running":true}`, returns creds
  - `Task StopRemoteDesktopAsync(ControlContext ctx)` — POST `{"running":false}`

- [ ] **Step 1: Write the failing test**

```csharp
// in ApiClientTests.cs
[Fact]
public async Task StartRemoteDesktop_signs_request_and_returns_session()
{
    var handler = new RecordingHandler();
    using var client = new ApiClient("pi", 8080, handler);
    var session = await client.StartRemoteDesktopAsync(Context());

    Assert.NotNull(session);
    Assert.Equal(5900, session!.Port);
    Assert.Equal("u1", session.Username);
    Assert.Equal("p1", session.Password);
    var req = handler.Requests.Single();
    Assert.Equal(HttpMethod.Post, req.Method);
    Assert.Equal("/api/remote-desktop", req.RequestUri!.PathAndQuery);
    AssertSigned(req, System.Text.Encoding.UTF8.GetBytes("{\"running\":true}"));
}
```

Add a branch to `RecordingHandler.SendAsync`'s `switch` for `"/api/remote-desktop"` returning
`"""{"ok":true,"running":true,"error":null,"port":5900,"username":"u1","password":"p1"}"""`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test signage-core.Tests --filter StartRemoteDesktop_signs_request_and_returns_session`
Expected: FAIL — method not defined.

- [ ] **Step 3: Implement the model and client methods**

```csharp
// Models.cs
public record RemoteDesktopSession(
    [property: System.Text.Json.Serialization.JsonPropertyName("port")] int Port,
    [property: System.Text.Json.Serialization.JsonPropertyName("username")] string Username,
    [property: System.Text.Json.Serialization.JsonPropertyName("password")] string Password);
```

```csharp
// ApiClient.cs
public async Task<RemoteDesktopSession?> StartRemoteDesktopAsync(ControlContext context)
{
    var body = JsonSerializer.SerializeToUtf8Bytes(new { running = true }, JsonOpts);
    using var resp = await SendJsonAsync(HttpMethod.Post, "/api/remote-desktop", body, context);
    await ThrowIfError(resp);
    return await resp.Content.ReadFromJsonAsync<RemoteDesktopSession>();
}

public async Task StopRemoteDesktopAsync(ControlContext context)
{
    var body = JsonSerializer.SerializeToUtf8Bytes(new { running = false }, JsonOpts);
    using var resp = await SendJsonAsync(HttpMethod.Post, "/api/remote-desktop", body, context);
    await ThrowIfError(resp);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test signage-core.Tests --filter StartRemoteDesktop_signs_request_and_returns_session`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows-app/ApiClient.cs windows-app/Models.cs signage-core.Tests/ApiClientTests.cs
git commit -m "feat(app): ApiClient start/stop remote desktop"
```

### Task B2: Viewer launcher (pure, unit-tested command construction)

**Files:**
- Create: `windows-app/RemoteViewerLauncher.cs`
- Test: `signage-core.Tests/RemoteViewerLauncherTests.cs`

**Interfaces:**
- Produces: `static (string exePath, string args, IDictionary<string,string> env) BuildLaunch(string viewerExe, string host, RemoteDesktopSession s)` — returns the exact process start recipe; credentials go in `env` (`VNC_USERNAME`/`VNC_PASSWORD`), never in `args`.

- [ ] **Step 1: Write the failing test**

```csharp
using PiSignage.Control;
using Xunit;

public class RemoteViewerLauncherTests
{
    [Fact]
    public void BuildLaunch_puts_credentials_in_env_not_args()
    {
        var s = new RemoteDesktopSession(5900, "user1", "pass1");
        var (exe, args, env) = RemoteViewerLauncher.BuildLaunch(@"C:\vnc\vncviewer.exe", "192.168.0.58", s);

        Assert.Equal(@"C:\vnc\vncviewer.exe", exe);
        Assert.Contains("SecurityTypes=RA2ne,RA2", args);
        Assert.Contains("192.168.0.58::5900", args);
        Assert.DoesNotContain("pass1", args);
        Assert.Equal("user1", env["VNC_USERNAME"]);
        Assert.Equal("pass1", env["VNC_PASSWORD"]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test signage-core.Tests --filter BuildLaunch_puts_credentials_in_env_not_args`
Expected: FAIL — type not defined.

- [ ] **Step 3: Implement the launcher**

```csharp
// windows-app/RemoteViewerLauncher.cs
using System.Collections.Generic;
using System.Diagnostics;

namespace PiSignage.Control;

public static class RemoteViewerLauncher
{
    public static (string exePath, string args, IDictionary<string, string> env) BuildLaunch(
        string viewerExe, string host, RemoteDesktopSession session)
    {
        var args = $"SecurityTypes=RA2ne,RA2 {host}::{session.Port}";
        var env = new Dictionary<string, string>
        {
            ["VNC_USERNAME"] = session.Username,
            ["VNC_PASSWORD"] = session.Password,
        };
        return (viewerExe, args, env);
    }

    public static Process Launch(string viewerExe, string host, RemoteDesktopSession session)
    {
        var (exe, args, env) = BuildLaunch(viewerExe, host, session);
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false };
        foreach (var (k, v) in env) psi.Environment[k] = v;
        return Process.Start(psi)!;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test signage-core.Tests --filter BuildLaunch_puts_credentials_in_env_not_args`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows-app/RemoteViewerLauncher.cs signage-core.Tests/RemoteViewerLauncherTests.cs
git commit -m "feat(app): build TigerVNC viewer launch recipe"
```

### Task B3: Bundle vncviewer.exe

**Files:**
- Modify: `windows-app/PiSignageControl.csproj`
- Add: `windows-app/tools/vncviewer.exe` (TigerVNC standalone Windows viewer, GPLv2 — downloaded, committed via Git LFS or as a build asset)

**Interfaces:**
- Produces: `vncviewer.exe` copied to the app output dir; a helper `AgentBundle`-style path resolver `RemoteViewerLauncher.BundledViewerPath()` returning `Path.Combine(AppContext.BaseDirectory, "vncviewer.exe")`.

- [ ] **Step 1: Add the bundled file to the project**

```xml
<ItemGroup>
  <None Include="tools\vncviewer.exe" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 2: Add the path resolver + a presence test**

```csharp
// append to RemoteViewerLauncher.cs
public static string BundledViewerPath() =>
    System.IO.Path.Combine(System.AppContext.BaseDirectory, "vncviewer.exe");
```

```csharp
// RemoteViewerLauncherTests.cs
[Fact]
public void BundledViewerPath_is_next_to_the_app()
{
    Assert.EndsWith("vncviewer.exe", RemoteViewerLauncher.BundledViewerPath());
}
```

- [ ] **Step 3: Build to confirm the asset copies**

Run: `dotnet build windows-app/PiSignageControl.csproj` then confirm `bin/Debug/net8.0-windows/vncviewer.exe` exists.
Expected: file present.

- [ ] **Step 4: Commit**

```bash
git add windows-app/PiSignageControl.csproj windows-app/tools/vncviewer.exe
git commit -m "build(app): bundle TigerVNC vncviewer"
```

### Task B4: "Remote control" button wiring

**Files:**
- Modify: `windows-app/MainWindow.xaml` (button beside `BtnKiosk`, ~line 60)
- Modify: `windows-app/MainWindow.xaml.cs` (handler + enable/disable in connect path)

**Interfaces:**
- Consumes: `_api`, `_connectedControlContext`, `_connectedHost`, `ConnectedControlContext()`, `RemoteViewerLauncher`, `Toaster`.
- Produces: `BtnRemote_Click` handler.

- [ ] **Step 1: Add the button**

```xml
<Button x:Name="BtnRemote" Content="_Remote control" Margin="0,0,6,4" Click="BtnRemote_Click" IsEnabled="False"
        ToolTip="See and control this Pi's screen (connect and pair first)"
        ToolTipService.ShowOnDisabled="True"/>
```

- [ ] **Step 2: Enable it only for a paired connection**

In `ConnectHostAsync` success path (after `BtnKiosk.IsEnabled = true;`, ~line 176) add:

```csharp
BtnRemote.IsEnabled = _connectedControlContext is not null &&
                      !_connectedControlContext.IsLegacyUnsigned;
```

In the `catch` block (~line 196, alongside `BtnKiosk.IsEnabled = false;`) add `BtnRemote.IsEnabled = false;`.

- [ ] **Step 3: Implement the handler**

```csharp
private async void BtnRemote_Click(object sender, RoutedEventArgs e)
{
    if (_api == null || _connectedHost == null) return;
    var viewer = RemoteViewerLauncher.BundledViewerPath();
    if (!System.IO.File.Exists(viewer))
    {
        Toaster.Show("Remote viewer is missing from this install.", ToastKind.Error);
        return;
    }
    BtnRemote.IsEnabled = false;
    var ctx = ConnectedControlContext();
    try
    {
        var session = await _api.StartRemoteDesktopAsync(ctx);
        if (session == null) { Toaster.Show("The Pi did not start remote control.", ToastKind.Error); return; }
        Toaster.Show("Opening remote control — the viewer window will appear.", ToastKind.Success);
        var proc = RemoteViewerLauncher.Launch(viewer, _connectedHost, session);
        _ = Task.Run(async () =>
        {
            await proc.WaitForExitAsync();
            try { await _api.StopRemoteDesktopAsync(ctx); } catch { /* idle timeout is the backstop */ }
        });
    }
    catch (Exception ex)
    {
        Toaster.Show("Couldn't start remote control: " + ex.Message, ToastKind.Error);
        try { await _api.StopRemoteDesktopAsync(ctx); } catch { }
    }
    finally { BtnRemote.IsEnabled = true; }
}
```

- [ ] **Step 4: Build and run the app**

Run: `dotnet build windows-app/PiSignageControl.csproj`
Expected: builds clean. Manual: connect to the paired Pi, confirm the button enables.

- [ ] **Step 5: Commit**

```bash
git add windows-app/MainWindow.xaml windows-app/MainWindow.xaml.cs
git commit -m "feat(app): remote control button launches the viewer"
```

### Task B5: Manual end-to-end verification

- [ ] Connect to the paired Pi (TV1) in the app.
- [ ] Click "Remote control"; confirm `vncviewer.exe` opens and shows the Pi's screen.
- [ ] Move the mouse / type; confirm input reaches the Pi.
- [ ] Toggle "TV display on/off" first, reconnect remote, confirm you see the desktop (for the password/admin use case).
- [ ] Close the viewer; confirm `GET /api/remote-desktop` on the Pi returns `{"running": false}` shortly after.
- [ ] Leave a session idle 15 min (or temporarily lower `WAYVNC_IDLE_SECONDS`); confirm it auto-stops.

---

## Self-Review Notes

- Spec coverage: agent start/stop/idle/auth (A1), wayvnc availability (A2), client calls (B1), viewer launch + creds-in-env (B2/B3), gating to paired devices (B4), e2e incl. desktop-mode and idle-stop (B5). All spec sections mapped.
- The 15-min idle timer uses a real `asyncio.sleep`; B5 lowers it for the manual test rather than adding a fake clock (YAGNI).
- `_spawn_wayvnc` seam keeps A1 tests process-free; the real spawn path is only exercised in B5.
