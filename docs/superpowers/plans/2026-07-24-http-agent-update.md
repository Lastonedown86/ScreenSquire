# HTTP Agent Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Push agent updates (main.py + static) to Pis over HTTP — no SSH — via a new `/api/update` endpoint, a rewritten `deploy-agent.ps1`, and an "Update Pi software" button in the Windows app.

**Architecture:** The agent accepts a validated zip upload, backs up current files, swaps new ones in, then restarts the kiosk and exits — systemd (`Restart=always`) relaunches it with the new code. The Windows app embeds the agent files as resources and pushes them with shared logic in `signage-core` (`AgentUpdater`). The dev script does the same push from PowerShell.

**Tech Stack:** FastAPI/Python (agent, pytest), PowerShell 7 (deploy script), .NET 8 / WPF + xunit (app + signage-core).

**Spec:** `docs/superpowers/specs/2026-07-24-http-agent-update-design.md`

## Global Constraints

- No new dependencies anywhere (Python stdlib `zipfile`/`py_compile`; .NET `System.IO.Compression`; PS `Compress-Archive`/`Invoke-RestMethod`).
- No auth on `/api/update` (phase-1 trusted-LAN posture; phase 2 token must cover it).
- All user-facing app copy in plain language — the client is non-technical. No jargon like "endpoint", "HTTP", "zip".
- Version format: date-based string `"YYYY.MM.DD.N"`, single source of truth: `AGENT_VERSION` in `agent/main.py`.
- Update payload may contain ONLY `main.py` and `static/*`. Never the venv, requirements, or setup scripts.
- Python tests run from `agent/`: `cd agent` then `.venv/Scripts/python -m pytest tests -v` (if `.venv/Scripts` doesn't exist it's a WSL venv — use `python -m pytest tests -v` with fastapi installed, same as existing tests were run).
- .NET tests: `dotnet test signage-core.Tests` from repo root.

---

### Task 1: Agent — `AGENT_VERSION` in `/api/status`

**Files:**
- Modify: `agent/main.py` (top constants ~line 44, `status()` ~line 490)
- Test: `agent/tests/test_update.py` (new)

**Interfaces:**
- Produces: module constant `AGENT_VERSION: str = "2026.07.24.1"`; `/api/status` JSON gains `"agent_version": AGENT_VERSION`. Tasks 2–5 rely on the exact key name `agent_version` and the constant name `AGENT_VERSION`.

- [ ] **Step 1: Write the failing test**

Create `agent/tests/test_update.py`:

```python
import main
from fastapi.testclient import TestClient

client = TestClient(main.app)


def test_status_reports_agent_version():
    body = client.get("/api/status").json()
    assert body["agent_version"] == main.AGENT_VERSION
    assert main.AGENT_VERSION  # non-empty
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd agent` then `.venv/Scripts/python -m pytest tests/test_update.py -v`
Expected: FAIL with `AttributeError: module 'main' has no attribute 'AGENT_VERSION'`

- [ ] **Step 3: Implement**

In `agent/main.py`, below `PORT = ...` (~line 43) add:

```python
AGENT_VERSION = "2026.07.24.1"  # bump on every agent change; the app compares this
```

In `status()` (~line 491) add the key after `"version"`:

```python
        "agent_version": AGENT_VERSION,
```

- [ ] **Step 4: Run test to verify it passes**

Run: `.venv/Scripts/python -m pytest tests/test_update.py -v` — PASS. Also run the full suite: `.venv/Scripts/python -m pytest tests -v` — all pass.

- [ ] **Step 5: Commit**

```bash
git add agent/main.py agent/tests/test_update.py
git commit -m "feat(agent): report AGENT_VERSION in /api/status"
```

---

### Task 2: Agent — `POST /api/update`

**Files:**
- Modify: `agent/main.py` (new section after the kiosk-control section, ~line 454)
- Test: `agent/tests/test_update.py` (extend)

**Interfaces:**
- Consumes: `AGENT_VERSION` (Task 1), existing `_systemctl_user()` helper and `KIOSK_UNIT`.
- Produces: `POST /api/update` — multipart field name `file`, zip containing `main.py` (required) + `static/*`. Success: `200 {"ok": true, "version": "<new AGENT_VERSION parsed from uploaded main.py>"}`. Failures: `400` with `detail` string; nothing on disk changes. Side effects on success: previous `main.py`+`static/` copied to `agent/update-backup/`, new files written, then (background) kiosk restart + `os._exit(0)`. Also produces `_restart_after_update()` coroutine (monkeypatch target for tests) and `_UPDATE_MAX_BYTES` constant.

- [ ] **Step 1: Write the failing tests**

Append to `agent/tests/test_update.py`:

```python
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `.venv/Scripts/python -m pytest tests/test_update.py -v`
Expected: the new tests FAIL with 404 (`/api/update` doesn't exist).

- [ ] **Step 3: Implement the endpoint**

In `agent/main.py`: add `import io`, `import py_compile`, `import tempfile`, `import zipfile` and `from pathlib import PurePosixPath` to the imports. After the kiosk-control section (~line 454) add:

```python
# ---- self-update (pushed from the control app / deploy script; no SSH) ----
_UPDATE_MAX_BYTES = 20 * 1024 * 1024
_VERSION_RE = re.compile(r'AGENT_VERSION\s*=\s*"([^"]+)"')


async def _restart_after_update() -> None:
    # let the HTTP response flush before we pull the rug out
    await asyncio.sleep(1.0)
    await _systemctl_user("restart", KIOSK_UNIT)  # TV picks up new static pages
    os._exit(0)  # systemd Restart=always relaunches us with the new code


@app.post("/api/update")
async def update_agent(file: UploadFile):
    data = await file.read()
    try:
        zf = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile:
        raise HTTPException(400, "That doesn't look like a valid update file")

    total = 0
    for info in zf.infolist():
        name = info.filename
        if name.endswith("/"):
            if name != "static/" and not name.startswith("static/"):
                raise HTTPException(400, f"Unexpected folder in update: {name}")
            continue
        parts = PurePosixPath(name).parts
        if name.startswith("/") or ".." in parts or ":" in name or "\\" in name:
            raise HTTPException(400, f"Unsafe path in update: {name}")
        if name != "main.py" and not name.startswith("static/"):
            raise HTTPException(400, f"Unexpected file in update: {name}")
        total += info.file_size
    if total > _UPDATE_MAX_BYTES:
        raise HTTPException(400, "Update is too large")
    if "main.py" not in zf.namelist():
        raise HTTPException(400, "Update is missing main.py")

    tmp = Path(tempfile.mkdtemp(prefix="update-tmp-", dir=APP_DIR))
    try:
        zf.extractall(tmp)  # safe: every entry name validated above
        try:
            py_compile.compile(str(tmp / "main.py"), doraise=True)
        except py_compile.PyCompileError as e:
            raise HTTPException(400, f"New main.py won't run (syntax error): {e}")

        m = _VERSION_RE.search((tmp / "main.py").read_text())
        new_version = m.group(1) if m else "unknown"

        # one level of backup for manual recovery over SSH if a bad update lands
        backup = APP_DIR / "update-backup"
        shutil.rmtree(backup, ignore_errors=True)
        backup.mkdir()
        shutil.copy2(APP_DIR / "main.py", backup / "main.py")
        if (APP_DIR / "static").exists():
            shutil.copytree(APP_DIR / "static", backup / "static")

        shutil.copy2(tmp / "main.py", APP_DIR / "main.py")
        if (tmp / "static").exists():
            shutil.copytree(tmp / "static", APP_DIR / "static", dirs_exist_ok=True)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    asyncio.create_task(_restart_after_update())
    log.info("Agent updated to %s — restarting", new_version)
    return {"ok": True, "version": new_version}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `.venv/Scripts/python -m pytest tests -v`
Expected: ALL tests pass (including pre-existing ones).

- [ ] **Step 5: Commit**

```bash
git add agent/main.py agent/tests/test_update.py
git commit -m "feat(agent): POST /api/update — self-update over HTTP, no SSH"
```

---

### Task 3: Rewrite `deploy-agent.ps1` (HTTP push, no SSH)

**Files:**
- Modify: `deploy-agent.ps1` (full rewrite)

**Interfaces:**
- Consumes: `POST /api/update` (Task 2), `agent_version` in `/api/status` (Task 1), saved devices at `%APPDATA%\PiSignage\devices.json` (`Ip` property).
- Produces: same CLI as before: `.\deploy-agent.ps1 [-Hosts a,b] [-Port 8080]`. `-User` is gone (no SSH).

- [ ] **Step 1: Replace the script body**

```powershell
# Push the agent (main.py + static pages) to Pis over HTTP — no SSH, no password.
#   .\deploy-agent.ps1                 # deploys to every Pi saved in the control app
#   .\deploy-agent.ps1 -Hosts 192.168.0.58, pisignage2.local
# Requires PowerShell 7 (Invoke-RestMethod -Form).
param(
    [string[]]$Hosts,
    [int]$Port = 8080
)

$agentDir = Join-Path $PSScriptRoot "agent"

$expected = (Select-String -Path (Join-Path $agentDir "main.py") -Pattern 'AGENT_VERSION\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
if (-not $expected) { Write-Error "Couldn't read AGENT_VERSION from agent\main.py"; exit 1 }

if (-not $Hosts) {
    # same list the control app uses
    $devicesFile = Join-Path $env:APPDATA "PiSignage\devices.json"
    if (-not (Test-Path $devicesFile)) {
        Write-Error "No -Hosts given and no saved devices at $devicesFile"; exit 1
    }
    $devices = Get-Content $devicesFile | ConvertFrom-Json
    $Hosts = $devices | ForEach-Object { $_.Ip }
    Write-Host "Deploying $expected to saved Pis: $($devices | ForEach-Object { "$($_.Name) ($($_.Ip))" } | Join-String -Separator ', ')"
}

# build the zip once: main.py at the root + static/ folder
$staging = Join-Path ([IO.Path]::GetTempPath()) "pisignage-agent-update"
$zip = "$staging.zip"
Remove-Item $staging, $zip -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $staging | Out-Null
Copy-Item (Join-Path $agentDir "main.py") $staging
Copy-Item (Join-Path $agentDir "static") $staging -Recurse
Compress-Archive -Path (Join-Path $staging "main.py"), (Join-Path $staging "static") -DestinationPath $zip

$failed = @()
foreach ($h in $Hosts) {
    Write-Host "`n==> $h"
    $base = "http://${h}:$Port"
    try {
        $resp = Invoke-RestMethod -Method Post -Uri "$base/api/update" -Form @{ file = Get-Item $zip } -TimeoutSec 30
    } catch {
        Write-Warning "$h — push failed: $($_.Exception.Message) (agent too old? bootstrap it over SSH once)"
        $failed += $h; continue
    }
    if (-not $resp.ok) { Write-Warning "$h — Pi rejected the update"; $failed += $h; continue }

    # agent restarts itself now — wait for it to come back with the new version
    $back = $false
    foreach ($i in 1..60) {
        Start-Sleep 1
        try {
            $st = Invoke-RestMethod "$base/api/status" -TimeoutSec 3
            if ($st.agent_version -eq $expected) { $back = $true; break }
        } catch { }  # still restarting
    }
    if ($back) { Write-Host "$h — updated to $expected (TV will blink once)" }
    else { Write-Warning "$h — pushed, but agent didn't come back within 60s"; $failed += $h }
}
Remove-Item $staging, $zip -Recurse -Force -ErrorAction SilentlyContinue

if ($failed) { Write-Warning "Failed: $($failed -join ', ')"; exit 1 }
Write-Host "`nAll Pis updated to $expected."
```

- [ ] **Step 2: Verify against a local agent**

Start the agent locally (separate terminal): `cd agent` then `.venv/Scripts/python main.py` (set `$env:SIGNAGE_DATA` to a temp dir first to avoid touching real data). Then:

Run: `.\deploy-agent.ps1 -Hosts localhost`
Expected: "push" succeeds; the local agent process exits after ~1s (that's `os._exit` — there is no systemd locally, so the poll times out with "didn't come back"). Confirm `agent/update-backup/` was created and `agent/main.py` content unchanged (you pushed the same version onto itself). Restore state: delete `agent/update-backup/` and `git status` must show no unexpected agent changes.

- [ ] **Step 3: Commit**

```bash
git add deploy-agent.ps1
git commit -m "feat(deploy): push agent updates over HTTP instead of ssh/scp"
```

---

### Task 4: `signage-core` — `AgentUpdater` (version parse, zip build, push+poll)

**Files:**
- Create: `signage-core/AgentUpdater.cs`
- Test: `signage-core.Tests/AgentUpdaterTests.cs`

**Interfaces:**
- Consumes: agent HTTP API from Tasks 1–2.
- Produces (used by Task 5):
  - `static string? AgentUpdater.ParseVersion(string mainPySource)`
  - `static byte[] AgentUpdater.BuildZip(IReadOnlyDictionary<string, byte[]> files)` — keys are zip entry paths like `main.py`, `static/kiosk.html`
  - `static Task AgentUpdater.PushAsync(HttpClient http, string baseUrl, byte[] zip, string expectedVersion, TimeSpan? timeout = null, CancellationToken ct = default)` — throws `HttpRequestException` on push failure (404 = old agent), `TimeoutException` if the Pi doesn't come back with `expectedVersion`.

- [ ] **Step 1: Write the failing tests**

Create `signage-core.Tests/AgentUpdaterTests.cs`:

```csharp
using System.IO.Compression;
using System.Net;
using System.Text;
using PiSignage.Signage;

namespace signage_core.Tests;

public class AgentUpdaterTests
{
    [Fact]
    public void ParseVersion_reads_the_constant()
    {
        var src = "PORT = 8080\nAGENT_VERSION = \"2026.07.24.1\"\napp = None\n";
        Assert.Equal("2026.07.24.1", AgentUpdater.ParseVersion(src));
    }

    [Fact]
    public void ParseVersion_returns_null_when_missing()
    {
        Assert.Null(AgentUpdater.ParseVersion("PORT = 8080\n"));
    }

    [Fact]
    public void BuildZip_roundtrips_entries()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["main.py"] = Encoding.UTF8.GetBytes("AGENT_VERSION = \"1\"\n"),
            ["static/kiosk.html"] = Encoding.UTF8.GetBytes("<html>"),
        };
        using var zip = new ZipArchive(new MemoryStream(AgentUpdater.BuildZip(files)));
        Assert.Equal(2, zip.Entries.Count);
        using var r = new StreamReader(zip.GetEntry("static/kiosk.html")!.Open());
        Assert.Equal("<html>", r.ReadToEnd());
    }

    // fake HTTP handler: scripted responses per URL
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add($"{req.Method} {req.RequestUri!.PathAndQuery}");
            return Task.FromResult(Respond(req));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task PushAsync_posts_then_polls_until_version_matches()
    {
        var handler = new FakeHandler();
        int statusCalls = 0;
        handler.Respond = req =>
            req.RequestUri!.AbsolutePath == "/api/update"
                ? Json("{\"ok\": true, \"version\": \"2\"}")
                : (++statusCalls < 3
                    ? Json("{\"agent_version\": \"1\"}")     // still old / restarting
                    : Json("{\"agent_version\": \"2\"}"));
        using var http = new HttpClient(handler);
        await AgentUpdater.PushAsync(http, "http://pi:8080", new byte[] { 1 }, "2",
            timeout: TimeSpan.FromSeconds(30), pollDelay: TimeSpan.Zero);
        Assert.Equal("POST /api/update", handler.Requests[0]);
        Assert.True(statusCalls >= 3);
    }

    [Fact]
    public async Task PushAsync_throws_on_404_old_agent()
    {
        var handler = new FakeHandler { Respond = _ => new(HttpStatusCode.NotFound) };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            AgentUpdater.PushAsync(http, "http://pi:8080", new byte[] { 1 }, "2"));
    }

    [Fact]
    public async Task PushAsync_times_out_when_pi_never_comes_back()
    {
        var handler = new FakeHandler();
        handler.Respond = req =>
            req.RequestUri!.AbsolutePath == "/api/update"
                ? Json("{\"ok\": true, \"version\": \"2\"}")
                : Json("{\"agent_version\": \"1\"}");  // never updates
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            AgentUpdater.PushAsync(http, "http://pi:8080", new byte[] { 1 }, "2",
                timeout: TimeSpan.FromMilliseconds(50), pollDelay: TimeSpan.Zero));
    }
}
```

Note the extra `pollDelay` parameter used by the tests — it exists so tests don't sleep; production callers omit it (defaults to 1 s).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test signage-core.Tests`
Expected: FAIL — `AgentUpdater` does not exist (compile error).

- [ ] **Step 3: Implement `signage-core/AgentUpdater.cs`**

```csharp
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiSignage.Signage;

/// <summary>Pushes an agent software bundle (main.py + static) to a Pi over
/// HTTP and waits for the agent to come back with the new version.</summary>
public static class AgentUpdater
{
    public static string? ParseVersion(string mainPySource)
    {
        var m = Regex.Match(mainPySource, "AGENT_VERSION\\s*=\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static byte[] BuildZip(IReadOnlyDictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, bytes) in files)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(bytes);
            }
        return ms.ToArray();
    }

    public static async Task PushAsync(HttpClient http, string baseUrl, byte[] zip,
        string expectedVersion, TimeSpan? timeout = null, TimeSpan? pollDelay = null,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(zip), "file", "agent-update.zip" } };
        var resp = await http.PostAsync($"{baseUrl}/api/update", form, ct);
        resp.EnsureSuccessStatusCode();

        // the agent restarts itself now; poll until it's back on the new version
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        var delay = pollDelay ?? TimeSpan.FromSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(delay, ct);
            try
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync($"{baseUrl}/api/status", ct));
                if (doc.RootElement.TryGetProperty("agent_version", out var v) &&
                    v.GetString() == expectedVersion)
                    return;
            }
            catch (HttpRequestException) { /* agent still restarting */ }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* request timeout mid-restart */ }
        }
        throw new TimeoutException("The Pi didn't come back with the new software version");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test signage-core.Tests`
Expected: ALL pass (new + existing).

- [ ] **Step 5: Commit**

```bash
git add signage-core/AgentUpdater.cs signage-core.Tests/AgentUpdaterTests.cs
git commit -m "feat(core): AgentUpdater — build/push agent bundle over HTTP"
```

---

### Task 5: Windows app — embed agent files + "Update Pi software" button

**Files:**
- Modify: `windows-app/PiSignageControl.csproj` (embed resources)
- Create: `windows-app/AgentBundle.cs` (read embedded files)
- Modify: `windows-app/Models.cs` (StatusInfo.AgentVersion)
- Modify: `windows-app/MainWindow.xaml` (button, after `BtnRemote` ~line 63)
- Modify: `windows-app/MainWindow.xaml.cs` (handler + out-of-date check in `RefreshStatusAsync` ~line 404)

**Interfaces:**
- Consumes: `AgentUpdater.ParseVersion/BuildZip/PushAsync` (Task 4), `agent_version` from `/api/status` (Task 1), `_devices` list (`PiSignage.Signage.SavedDevice.Ip`), `Toaster.Show(...)` toast helper, `_api` (connected `ApiClient`, has `BaseUrl`).
- Produces: `AgentBundle.Files()` → `Dictionary<string, byte[]>` with keys `main.py`, `static/...`; `AgentBundle.Version()` → bundled version string. UI: `BtnUpdatePi` button, visible only when the connected Pi's version differs from the bundled one.

- [ ] **Step 1: Embed the agent files**

In `PiSignageControl.csproj` add:

```xml
  <ItemGroup>
    <EmbeddedResource Include="..\agent\main.py" LogicalName="agent/main.py" />
    <EmbeddedResource Include="..\agent\static\**\*" LogicalName="agent/static/%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>
```

- [ ] **Step 2: Create `windows-app/AgentBundle.cs`**

```csharp
using System.IO;
using System.Reflection;
using System.Text;
using PiSignage.Signage;

namespace PiSignage.Control;

/// <summary>The agent software that shipped inside this exe (embedded at build
/// time from ..\agent). Shipping a new exe is how the client gets Pi updates.</summary>
public static class AgentBundle
{
    public static Dictionary<string, byte[]> Files()
    {
        var asm = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, byte[]>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("agent/")) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            // zip entry path: strip "agent/", normalise any backslashes from RecursiveDir
            files[name.Substring("agent/".Length).Replace('\\', '/')] = ms.ToArray();
        }
        return files;
    }

    public static string? Version()
    {
        var files = Files();
        return files.TryGetValue("main.py", out var main)
            ? AgentUpdater.ParseVersion(Encoding.UTF8.GetString(main))
            : null;
    }
}
```

- [ ] **Step 3: Verify the embed works (fast, before UI)**

Run: `cd windows-app` then `dotnet build`, then check resources landed:

```powershell
dotnet build -v:q && [System.Reflection.Assembly]::LoadFrom("bin\Debug\net8.0-windows\PiSignageControl.dll").GetManifestResourceNames() | Select-String '^agent/'
```

Expected: `agent/main.py` plus one line per file under `agent/static/` (forward or backslashes after `static/` are both fine — `Files()` normalises).

- [ ] **Step 4: Model + UI**

`Models.cs` — add to `StatusInfo`:

```csharp
    [JsonPropertyName("agent_version")] public string? AgentVersion { get; set; }
```

`MainWindow.xaml` — after the `BtnRemote` button (~line 65) add:

```xml
                <Button x:Name="BtnUpdatePi" Content="Update _Pi software" Margin="0,0,6,4"
                        Click="BtnUpdatePi_Click" Visibility="Collapsed"
                        ToolTip="This app came with newer Pi software — click to update your Pis. The TV will blink once."/>
```

`MainWindow.xaml.cs` — in `RefreshStatusAsync()` after the status is fetched successfully (`var s = await _api.GetStatusAsync();` ~line 409, inside the success path) add:

```csharp
            BtnUpdatePi.Visibility =
                AgentBundle.Version() is string bundled && s != null && s.AgentVersion != bundled
                    ? Visibility.Visible : Visibility.Collapsed;
```

And add the handler (near `BtnKiosk_Click`):

```csharp
    private async void BtnUpdatePi_Click(object sender, RoutedEventArgs e)
    {
        var bundled = AgentBundle.Version();
        if (bundled == null || _api == null) return;
        BtnUpdatePi.IsEnabled = false;
        try
        {
            Toaster.Show("Updating your Pi — the TV will blink once. This takes about half a minute…");
            var zip = AgentUpdater.BuildZip(AgentBundle.Files());
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            // update every saved Pi that's reachable and out of date; connected Pi included
            var targets = _devices.Where(d => !string.IsNullOrEmpty(d.Ip)).ToList();
            int ok = 0, skipped = 0; var failedNames = new List<string>();
            foreach (var dev in targets)
            {
                var baseUrl = $"http://{dev.Ip}:8080";
                try
                {
                    string? current = null;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(
                            await http.GetStringAsync($"{baseUrl}/api/status"));
                        if (doc.RootElement.TryGetProperty("agent_version", out var v))
                            current = v.GetString();
                    }
                    catch (Exception) { skipped++; continue; }   // off / unreachable — leave it alone
                    if (current == bundled) { skipped++; continue; }  // already up to date

                    await AgentUpdater.PushAsync(http, baseUrl, zip, bundled);
                    ok++;
                }
                catch (HttpRequestException) { failedNames.Add($"{dev.Name} (needs a one-time manual update)"); }
                catch (Exception) { failedNames.Add(dev.Name); }
            }

            if (failedNames.Count == 0)
                Toaster.Show(ok > 0 ? $"Done — {ok} Pi{(ok == 1 ? "" : "s")} updated." : "Everything was already up to date.",
                             ToastKind.Success);
            else
                Toaster.Show($"Updated {ok}, but these didn't finish: {string.Join(", ", failedNames)}. " +
                             "Check they're powered on and try again.", ToastKind.Warning);
            await RefreshStatusAsync();
        }
        finally { BtnUpdatePi.IsEnabled = true; }
    }
```

- [ ] **Step 5: Build + manual verification against the local agent**

Run: `dotnet build` in `windows-app` — clean build. Manual smoke (same setup as Task 3 Step 2): start the local agent with an artificially old `AGENT_VERSION` (edit a scratch copy via `$env:SIGNAGE_DATA` temp dir, or temporarily lower the constant, run, restore), connect the app to `localhost`, confirm the button appears, click it, confirm the push succeeds and the local agent process exits. Restore any temporary edits (`git status` clean except intended changes).

- [ ] **Step 6: Commit**

```bash
git add windows-app/PiSignageControl.csproj windows-app/AgentBundle.cs windows-app/Models.cs windows-app/MainWindow.xaml windows-app/MainWindow.xaml.cs
git commit -m "feat(app): Update Pi software button — pushes bundled agent to all Pis"
```

---

### Task 6: Docs

**Files:**
- Modify: `README.md` (deploy section + known limits)

**Interfaces:** none.

- [ ] **Step 1: Update README**

- Replace any mention of ssh/scp-based agent deployment with: `deploy-agent.ps1` pushes over HTTP to `/api/update`; SSH is only needed once per Pi to bootstrap a pre-`/api/update` agent (and for venv/requirements changes, which stay manual).
- In "Notes & known limits", extend the no-auth bullet: `/api/update` accepts agent code from anyone on the LAN — the phase-2 API token must cover it.
- Document the app behavior in the Windows app section: "The app carries the Pi software inside it. When a connected Pi is out of date, an **Update Pi software** button appears and updates every reachable Pi."

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: HTTP agent update flow (no SSH)"
```

---

## Bootstrap note (manual, once per existing Pi)

The first deploy that ships `/api/update` must go over SSH from a **regular terminal** (not Claude Code chat — the password prompt is exactly the bug this feature removes). Use the pre-rewrite script: `git show HEAD~n:deploy-agent.ps1` or just `scp` main.py + static and restart the service as before. Every deploy after that is HTTP.
