# Screenshot-Push Tournament Signage — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A worker captures a region of the official pairings/standings page from their own browser with one click in the WPF app, pushes it to selected Pi TVs, and it rotates on the TVs alongside a live round timer.

**Architecture:** Pure, testable helpers (timer, payload, push client, capture geometry) live in a `net8.0` class library `signage-core`, referenced by both the WPF app and an xUnit test project. The screenshot is uploaded via the *existing* `POST /api/media`; the agent gains `/api/dashboard` endpoints + a `/dashboard` page with a `board` view (shows the latest pushed image) and a `timer` view (ticks locally). Display views are `url` playlist items, so rotation/override reuse existing pi-signage plumbing.

**Tech Stack:** Python 3.13 / FastAPI (agent), C# / .NET 8 class library + WPF (control app), `System.Drawing.Common` (screen capture), vanilla JS/HTML (dashboard), pytest + httpx (agent tests), xUnit (helper tests).

## Global Constraints

- Agent runtime uses **stdlib only** for new endpoints (`json`, `time`) — no new packages in `agent/requirements.txt`. Dev deps (`pytest`, `httpx`) go in `agent/requirements-dev.txt`.
- Pure helpers live in `signage-core` (**net8.0, no WPF**). Screen-grab code (`System.Drawing`) lives in the WPF app (`net8.0-windows`), not in the library.
- Dashboard page: **vanilla JS only**, one image at a time, no per-second network. Must run on a 2GB Pi 4.
- **Unique filename per capture** (`<slot>-<counter>.png`) — this is the cache-busting mechanism; do not reuse a fixed filename.
- Timer `endsAt` = **epoch milliseconds**, stamped by the agent against its own clock; browser computes `remaining = (endsAt - Date.now())/1000`.
- Wire payload shape (exact keys): `{ "view_data": { "boards": { "<slot>": "/media/<file>" } }, "timer": { "state": "running|paused|stopped", "remaining": <int|null>, "round": <int|null>, "label": <str|null> } }`.
- LAN only; **no API auth**.
- No git repo yet — Task 0 initializes it.

---

## File Structure

**Agent (Python):**
- Modify `agent/main.py` — dashboard state + 3 endpoints.
- Create `agent/static/dashboard.html` — `board` + `timer` views.
- Create `agent/requirements-dev.txt`, `agent/tests/test_dashboard.py`.

**Library (`signage-core/`, net8.0):**
- `RoundTimer.cs`, `DashboardState.cs`, `DashboardPayload.cs`, `PushClient.cs`, `CaptureGeometry.cs`.

**Library tests (`signage-core.Tests/`, xUnit net8.0):**
- `RoundTimerTests.cs`, `DashboardPayloadTests.cs`, `PushClientTests.cs`, `CaptureGeometryTests.cs`.

**WPF (`windows-app/`, net8.0-windows):**
- Create `ScreenCapture.cs`, `RegionSelectorWindow.xaml` + `.cs`, `SignageWindow.xaml` + `.cs`.
- Modify `PiSignageControl.csproj` (references + `System.Drawing.Common`), `MainWindow.xaml` + `.cs` (launch button).

---

## Task 0: Scaffolding

**Files:** git repo, `PiSignage.sln`, `signage-core/`, `signage-core.Tests/`, `agent/requirements-dev.txt`, `.gitignore`

- [ ] **Step 1: Init git + gitignore**

Run from `C:/Users/Bill/Downloads/pi-signage/pi-signage`:
```bash
git init
printf '%s\n' 'bin/' 'obj/' '.venv/' '__pycache__/' 'agent/data/' '*.user' > .gitignore
```

- [ ] **Step 2: Create library + test project + solution**

```bash
dotnet new classlib -n signage-core -f net8.0 -o signage-core
dotnet new xunit -n signage-core.Tests -f net8.0 -o signage-core.Tests
rm signage-core/Class1.cs signage-core.Tests/UnitTest1.cs
dotnet add signage-core.Tests reference signage-core
dotnet new sln -n PiSignage
dotnet sln add signage-core signage-core.Tests windows-app
```

- [ ] **Step 3: Agent dev requirements**

Create `agent/requirements-dev.txt`:
```
pytest>=8
httpx>=0.27
```

- [ ] **Step 4: Verify tooling**

```bash
dotnet build signage-core.Tests
```
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "chore: scaffold signage-core lib, tests, solution"
```

---

## Task 1: Agent dashboard endpoints

**Files:**
- Modify: `agent/main.py`
- Test: `agent/tests/test_dashboard.py`

**Interfaces:**
- Produces (HTTP): `POST /api/dashboard` accepts `{view_data:{boards:{}}, timer:{state,remaining?,round?,label?}}`; stamps `timer.endsAt` (epoch ms) when running. `GET /api/dashboard` returns the payload. `GET /dashboard` serves the page.

- [ ] **Step 1: Install dev deps**

```bash
cd agent && ./.venv/Scripts/python.exe -m pip install -r requirements-dev.txt
```

- [ ] **Step 2: Write the failing test**

Create `agent/tests/test_dashboard.py`:
```python
import time
from fastapi.testclient import TestClient
import main

client = TestClient(main.app)

def test_running_timer_gets_endsat_epoch_ms():
    before = int(time.time() * 1000)
    r = client.post("/api/dashboard", json={
        "view_data": {"boards": {}},
        "timer": {"state": "running", "remaining": 1500, "label": "Round 1"},
    })
    assert r.status_code == 200 and r.json()["ok"] is True
    ends = client.get("/api/dashboard").json()["timer"]["endsAt"]
    assert before + 1500 * 1000 <= ends <= before + 1500 * 1000 + 5000

def test_boards_roundtrip():
    client.post("/api/dashboard", json={
        "view_data": {"boards": {"pairings": "/media/pairings-3.png"}},
        "timer": {"state": "stopped"}})
    got = client.get("/api/dashboard").json()
    assert got["view_data"]["boards"]["pairings"] == "/media/pairings-3.png"

def test_dashboard_page_served():
    assert client.get("/dashboard").status_code == 200
```

- [ ] **Step 3: Run to verify failure**

```bash
cd agent && ./.venv/Scripts/python.exe -m pytest tests/test_dashboard.py -v
```
Expected: FAIL (404 + missing page).

- [ ] **Step 4: Add endpoints to `main.py`**

Add `import time` to the imports. After the `app.mount("/media", ...)` line, add:
```python
# ---- screenshot dashboard (boards + live timer) ----
DASHBOARD_FILE = DATA_DIR / "dashboard.json"


class TimerState(BaseModel):
    state: Literal["running", "paused", "stopped"] = "stopped"
    endsAt: Optional[int] = None       # epoch ms, stamped by the agent when running
    remaining: Optional[int] = None    # seconds
    round: Optional[int] = None
    label: Optional[str] = None


class DashboardPayload(BaseModel):
    view_data: dict = {}
    timer: TimerState = TimerState()


def _load_dashboard() -> dict:
    if DASHBOARD_FILE.exists():
        try:
            return json.loads(DASHBOARD_FILE.read_text())
        except Exception:
            log.exception("Corrupt dashboard.json, starting empty")
    return {"view_data": {"boards": {}}, "timer": {"state": "stopped"}}


_dashboard: dict = _load_dashboard()


@app.post("/api/dashboard")
async def set_dashboard(payload: DashboardPayload):
    global _dashboard
    d = payload.model_dump()
    t = d.get("timer") or {}
    if t.get("state") == "running" and t.get("remaining") is not None:
        t["endsAt"] = int(time.time() * 1000) + int(t["remaining"]) * 1000
    d["timer"] = t
    _dashboard = d
    tmp = DASHBOARD_FILE.with_suffix(".tmp")
    tmp.write_text(json.dumps(d, indent=2))
    tmp.replace(DASHBOARD_FILE)  # atomic
    return {"ok": True}


@app.get("/api/dashboard")
async def get_dashboard():
    return _dashboard


@app.get("/dashboard")
async def dashboard_page():
    return FileResponse(APP_DIR / "static" / "dashboard.html")
```

- [ ] **Step 5: Placeholder page (full page in Task 2)**

Create `agent/static/dashboard.html`:
```html
<!DOCTYPE html><html><head><meta charset="utf-8"><title>Dashboard</title></head><body></body></html>
```

- [ ] **Step 6: Run to verify pass**

```bash
cd agent && ./.venv/Scripts/python.exe -m pytest tests/test_dashboard.py -v
```
Expected: 3 passed.

- [ ] **Step 7: Commit**

```bash
git add agent/main.py agent/tests/test_dashboard.py agent/static/dashboard.html agent/requirements-dev.txt
git commit -m "feat(agent): dashboard endpoints (boards + agent-clock timer)"
```

---

## Task 2: Dashboard page (board + timer views)

**Files:**
- Modify: `agent/static/dashboard.html`
- Test: `agent/tests/test_dashboard.py`

**Interfaces:**
- Consumes (HTTP): `GET /api/dashboard`. URL params `?view=board&name=<slot>` or `?view=timer`.

- [ ] **Step 1: Write the failing content test**

Add to `agent/tests/test_dashboard.py`:
```python
def test_dashboard_page_has_views_and_poll():
    html = client.get("/dashboard").text
    assert "view-board" in html and "view-timer" in html
    assert "/api/dashboard" in html            # it polls
    assert "No pairings posted" in html or "Nothing posted" in html  # idle state
```

- [ ] **Step 2: Run to verify failure**

```bash
cd agent && ./.venv/Scripts/python.exe -m pytest tests/test_dashboard.py::test_dashboard_page_has_views_and_poll -v
```
Expected: FAIL.

- [ ] **Step 3: Write the full page**

Replace `agent/static/dashboard.html` with:
```html
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Tournament</title>
<style>
  :root { color-scheme: dark; }
  html,body { margin:0; height:100%; background:#000; color:#e8eaf0;
    font:600 3vmin/1.3 system-ui,sans-serif; overflow:hidden; cursor:none; }
  #root { height:100%; contain:strict; }
  .fill { position:fixed; inset:0; display:flex; align-items:center; justify-content:center; }
  #board img { max-width:100%; max-height:100%; object-fit:contain; }
  .clock { flex-direction:column; }
  .clock .time { font-variant-numeric:tabular-nums; font-weight:800;
    font-size:26vmin; line-height:1; }
  .clock .time.over { color:#ff5d5d; }
  .clock .label { color:#8b93a7; font-size:5vmin; margin-top:2vmin; }
  #idle { color:#5a627a; font-size:3.2vmin; }
  .hidden { display:none !important; }
</style>
</head>
<body>
  <div id="root">
    <div id="idle" class="fill">Nothing posted yet</div>
    <div id="view-board" class="fill hidden"><img id="board-img" alt=""></div>
    <div id="view-timer" class="fill clock hidden">
      <div class="time" id="clock-time">--:--</div>
      <div class="label" id="clock-label"></div>
    </div>
  </div>
<script>
(() => {
  const params = new URLSearchParams(location.search);
  const view = params.get('view') || 'board';
  const slot = params.get('name') || 'pairings';
  const $ = id => document.getElementById(id);
  let data = null, endsAt = null, pausedRemaining = null, curSrc = null;

  const idleText = view === 'board' ? 'No ' + slot + ' posted' : 'No round running';
  $('idle').textContent = idleText;

  function fmt(s){ s=Math.max(0,Math.round(s));
    return String(Math.floor(s/60)).padStart(2,'0')+':'+String(s%60).padStart(2,'0'); }

  function showIdle(on){
    $('idle').classList.toggle('hidden', !on);
    ['view-board','view-timer'].forEach(v=>$(v).classList.add('hidden'));
    if(!on) $('view-'+view).classList.remove('hidden');
  }

  function render(){
    if(view === 'board'){
      const src = data && data.view_data && data.view_data.boards
                 ? data.view_data.boards[slot] : null;
      if(!src){ showIdle(true); return; }
      showIdle(false);
      if(src !== curSrc){ curSrc = src; $('board-img').src = src; }  // swap on new push
    } else { // timer
      const running = data && data.timer && data.timer.state !== 'stopped';
      showIdle(!running);
      if(running) $('clock-label').textContent = data.timer.label || '';
    }
  }

  function tick(){
    if(view !== 'timer' || !data || !data.timer) return;
    const t = data.timer;
    let rem;
    if(t.state==='running' && endsAt!=null) rem = (endsAt - Date.now())/1000;
    else if(t.state==='paused') rem = pausedRemaining;
    else return;
    const el = $('clock-time');
    el.classList.toggle('over', rem<=0);
    el.textContent = rem<=0 ? 'TIME' : fmt(rem);
  }

  async function poll(){
    try{
      const r = await fetch('/api/dashboard',{cache:'no-store'});
      data = await r.json();
      const t = data.timer||{};
      endsAt = t.state==='running' ? t.endsAt : null;
      pausedRemaining = t.state==='paused' ? t.remaining : null;
      render();
    }catch(e){ /* keep last render; never blank the TV on a blip */ }
  }
  poll(); setInterval(poll, 15000);
  setInterval(tick, 250);
})();
</script>
</body>
</html>
```

- [ ] **Step 4: Run all agent tests**

```bash
cd agent && ./.venv/Scripts/python.exe -m pytest tests/ -v
```
Expected: all passed.

- [ ] **Step 5: Manual smoke (real browser)**

Start the agent, then upload an image and point a board at it:
```bash
cd agent && ./.venv/Scripts/python.exe main.py   # in one terminal
# other terminal — upload any png as the pairings board:
curl -F "file=@static/../data/media/demo.png" http://localhost:8080/api/media   # or any image
curl -s -X POST http://localhost:8080/api/dashboard -H "Content-Type: application/json" \
  -d '{"view_data":{"boards":{"pairings":"/media/demo.png"}},"timer":{"state":"running","remaining":1500,"label":"Round 1"}}'
```
Open `http://localhost:8080/dashboard?view=board&name=pairings` (shows image) and `?view=timer` (counts down) in Chrome. Clear boards → shows idle.

- [ ] **Step 6: Commit**

```bash
git add agent/static/dashboard.html agent/tests/test_dashboard.py
git commit -m "feat(agent): dashboard page with board + live timer views"
```

---

## Task 3: RoundTimer model

**Files:**
- Create: `signage-core/RoundTimer.cs`
- Test: `signage-core.Tests/RoundTimerTests.cs`

**Interfaces:**
- Produces: `enum TimerRunState { Stopped, Running, Paused }`; `RoundTimer` with `State`, `int? RemainingSeconds`, `string? Label`, `int? Round`, methods `Start(int minutes, string label, int round)`, `Pause(int remainingSeconds)`, `Resume(int remainingSeconds)`, `Stop()`.

- [ ] **Step 1: Write the failing tests**

Create `signage-core.Tests/RoundTimerTests.cs`:
```csharp
using PiSignage.Signage;
using Xunit;

public class RoundTimerTests
{
    [Fact]
    public void StartSetsRunningWithSecondsLabelRound()
    {
        var t = new RoundTimer();
        t.Start(25, "Round 1", 1);
        Assert.Equal(TimerRunState.Running, t.State);
        Assert.Equal(1500, t.RemainingSeconds);
        Assert.Equal("Round 1", t.Label);
        Assert.Equal(1, t.Round);
    }

    [Fact]
    public void PauseResumeKeepsRemaining()
    {
        var t = new RoundTimer(); t.Start(25, "R1", 1);
        t.Pause(600);
        Assert.Equal(TimerRunState.Paused, t.State);
        Assert.Equal(600, t.RemainingSeconds);
        t.Resume(600);
        Assert.Equal(TimerRunState.Running, t.State);
    }

    [Fact]
    public void StopClears()
    {
        var t = new RoundTimer(); t.Start(25, "R1", 1);
        t.Stop();
        Assert.Equal(TimerRunState.Stopped, t.State);
        Assert.Null(t.RemainingSeconds);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test signage-core.Tests --filter RoundTimerTests
```
Expected: FAIL (type not found).

- [ ] **Step 3: Implement `RoundTimer.cs`**

```csharp
namespace PiSignage.Signage;

public enum TimerRunState { Stopped, Running, Paused }

public sealed class RoundTimer
{
    public TimerRunState State { get; private set; } = TimerRunState.Stopped;
    public int? RemainingSeconds { get; private set; }
    public string? Label { get; private set; }
    public int? Round { get; private set; }

    public void Start(int minutes, string label, int round)
    { State = TimerRunState.Running; RemainingSeconds = minutes * 60; Label = label; Round = round; }

    public void Pause(int remainingSeconds)
    { State = TimerRunState.Paused; RemainingSeconds = remainingSeconds; }

    public void Resume(int remainingSeconds)
    { State = TimerRunState.Running; RemainingSeconds = remainingSeconds; }

    public void Stop()
    { State = TimerRunState.Stopped; RemainingSeconds = null; }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test signage-core.Tests --filter RoundTimerTests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add signage-core/RoundTimer.cs signage-core.Tests/RoundTimerTests.cs
git commit -m "feat(core): round timer model + tests"
```

---

## Task 4: Dashboard state + payload builder

**Files:**
- Create: `signage-core/DashboardState.cs`, `signage-core/DashboardPayload.cs`
- Test: `signage-core.Tests/DashboardPayloadTests.cs`

**Interfaces:**
- Produces: `DashboardState { Dictionary<string,string> Boards }` (slot → media path); `DashboardPayload.Build(DashboardState state, RoundTimer timer) -> object` matching the wire shape in Global Constraints.

- [ ] **Step 1: Write the failing test**

Create `signage-core.Tests/DashboardPayloadTests.cs`:
```csharp
using System.Text.Json;
using PiSignage.Signage;
using Xunit;

public class DashboardPayloadTests
{
    [Fact]
    public void BuildMatchesWireShape()
    {
        var state = new DashboardState();
        state.Boards["pairings"] = "/media/pairings-2.png";
        var timer = new RoundTimer(); timer.Start(25, "Round 3", 3);

        var json = JsonSerializer.Serialize(DashboardPayload.Build(state, timer));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("/media/pairings-2.png",
            root.GetProperty("view_data").GetProperty("boards").GetProperty("pairings").GetString());
        Assert.Equal("running", root.GetProperty("timer").GetProperty("state").GetString());
        Assert.Equal(1500, root.GetProperty("timer").GetProperty("remaining").GetInt32());
        Assert.Equal(3, root.GetProperty("timer").GetProperty("round").GetInt32());
        Assert.Equal("Round 3", root.GetProperty("timer").GetProperty("label").GetString());
    }

    [Fact]
    public void StoppedTimerSerializesNullRemaining()
    {
        var json = JsonSerializer.Serialize(DashboardPayload.Build(new DashboardState(), new RoundTimer()));
        using var doc = JsonDocument.Parse(json);
        var timer = doc.RootElement.GetProperty("timer");
        Assert.Equal("stopped", timer.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, timer.GetProperty("remaining").ValueKind);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test signage-core.Tests --filter DashboardPayloadTests
```
Expected: FAIL.

- [ ] **Step 3: Implement `DashboardState.cs`**

```csharp
namespace PiSignage.Signage;

public sealed class DashboardState
{
    // slot name (e.g. "pairings", "standings") -> media path (e.g. "/media/pairings-2.png")
    public Dictionary<string, string> Boards { get; } = new();
}
```

- [ ] **Step 4: Implement `DashboardPayload.cs`**

```csharp
namespace PiSignage.Signage;

public static class DashboardPayload
{
    public static object Build(DashboardState state, RoundTimer timer) => new
    {
        view_data = new { boards = state.Boards },
        timer = new
        {
            state = timer.State.ToString().ToLowerInvariant(),
            remaining = timer.RemainingSeconds,
            round = timer.Round,
            label = timer.Label,
        },
    };
}
```

- [ ] **Step 5: Run to verify pass**

```bash
dotnet test signage-core.Tests --filter DashboardPayloadTests
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add signage-core/DashboardState.cs signage-core/DashboardPayload.cs signage-core.Tests/DashboardPayloadTests.cs
git commit -m "feat(core): dashboard state + payload builder + tests"
```

---

## Task 5: Push client (upload image + post dashboard)

**Files:**
- Create: `signage-core/PushClient.cs`
- Test: `signage-core.Tests/PushClientTests.cs`

**Interfaces:**
- Produces: `PushClient(HttpClient http)` with
  `Task<string> UploadMediaAsync(string agentBaseUrl, string filename, byte[] png)` → returns the media path `"/media/<name>"`;
  `Task PostDashboardAsync(string agentBaseUrl, object payload)` → POSTs JSON to `/api/dashboard`.

- [ ] **Step 1: Write the failing test (against a running agent, skips if down)**

Create `signage-core.Tests/PushClientTests.cs`:
```csharp
using System.Net.Http;
using PiSignage.Signage;
using Xunit;

public class PushClientTests
{
    // 1x1 PNG
    static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public async Task UploadThenPostReachesAgent()
    {
        using var http = new HttpClient();
        try { await http.GetAsync("http://localhost:8080/api/status"); }
        catch { return; }  // agent down -> skip

        var client = new PushClient(http);
        var path = await client.UploadMediaAsync("http://localhost:8080", "pairings-test.png", Png);
        Assert.StartsWith("/media/", path);

        var state = new DashboardState(); state.Boards["pairings"] = path;
        await client.PostDashboardAsync("http://localhost:8080",
            DashboardPayload.Build(state, new RoundTimer()));

        var back = await http.GetStringAsync("http://localhost:8080/api/dashboard");
        Assert.Contains("pairings-test.png", back);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test signage-core.Tests --filter PushClientTests
```
Expected: FAIL (type not found) — compile error even with the agent down.

- [ ] **Step 3: Implement `PushClient.cs`**

```csharp
using System.Net.Http;
using System.Net.Http.Json;

namespace PiSignage.Signage;

public sealed class PushClient(HttpClient http)
{
    public async Task<string> UploadMediaAsync(string agentBaseUrl, string filename, byte[] png)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", filename);
        var resp = await http.PostAsync(agentBaseUrl.TrimEnd('/') + "/api/media", form);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<UploadResult>()
                   ?? throw new InvalidOperationException("Empty upload response");
        return "/media/" + body.name;
    }

    public async Task PostDashboardAsync(string agentBaseUrl, object payload)
    {
        var resp = await http.PostAsJsonAsync(agentBaseUrl.TrimEnd('/') + "/api/dashboard", payload);
        resp.EnsureSuccessStatusCode();
    }

    private sealed record UploadResult(bool ok, string name, string type, long bytes);
}
```

- [ ] **Step 4: Run to verify pass**

Start the agent, then:
```bash
dotnet test signage-core.Tests --filter PushClientTests
```
Expected: PASS (or silent skip if agent down — start it and re-run to confirm).

- [ ] **Step 5: Commit**

```bash
git add signage-core/PushClient.cs signage-core.Tests/PushClientTests.cs
git commit -m "feat(core): push client (media upload + dashboard post) + integration test"
```

---

## Task 6: Capture geometry + screen grab

**Files:**
- Create: `signage-core/CaptureGeometry.cs`, `windows-app/ScreenCapture.cs`
- Test: `signage-core.Tests/CaptureGeometryTests.cs`
- Modify: `windows-app/PiSignageControl.csproj` (add `System.Drawing.Common`)

**Interfaces:**
- Produces: `CaptureGeometry.Normalize((int x,int y) a, (int x,int y) b) -> (int x,int y,int w,int h)` — turns two drag corners into a positive-size rectangle. `ScreenCapture.CaptureRegion(int x,int y,int w,int h) -> byte[]` (PNG) [WPF app, manual-tested].

- [ ] **Step 1: Write the failing geometry test**

Create `signage-core.Tests/CaptureGeometryTests.cs`:
```csharp
using PiSignage.Signage;
using Xunit;

public class CaptureGeometryTests
{
    [Fact]
    public void NormalizeHandlesReverseDrag()
    {
        var (x, y, w, h) = CaptureGeometry.Normalize((300, 200), (100, 50));
        Assert.Equal(100, x); Assert.Equal(50, y);
        Assert.Equal(200, w); Assert.Equal(150, h);
    }

    [Fact]
    public void NormalizeForwardDrag()
    {
        var (x, y, w, h) = CaptureGeometry.Normalize((10, 20), (110, 220));
        Assert.Equal(10, x); Assert.Equal(20, y);
        Assert.Equal(100, w); Assert.Equal(200, h);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test signage-core.Tests --filter CaptureGeometryTests
```
Expected: FAIL.

- [ ] **Step 3: Implement `CaptureGeometry.cs`**

```csharp
namespace PiSignage.Signage;

public static class CaptureGeometry
{
    public static (int x, int y, int w, int h) Normalize((int x, int y) a, (int x, int y) b)
    {
        int x = Math.Min(a.x, b.x), y = Math.Min(a.y, b.y);
        int w = Math.Abs(a.x - b.x), h = Math.Abs(a.y - b.y);
        return (x, y, w, h);
    }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test signage-core.Tests --filter CaptureGeometryTests
```
Expected: PASS.

- [ ] **Step 5: Add references + capture package to the WPF csproj**

Edit `windows-app/PiSignageControl.csproj` — add:
```xml
  <ItemGroup>
    <ProjectReference Include="..\signage-core\signage-core.csproj" />
    <PackageReference Include="System.Drawing.Common" Version="8.0.0" />
  </ItemGroup>
```

- [ ] **Step 6: Implement `ScreenCapture.cs` (WPF app, Windows-only)**

Create `windows-app/ScreenCapture.cs`:
```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace PiSignage.Control;

public static class ScreenCapture
{
    // Captures a screen rectangle (device pixels) and returns PNG bytes.
    public static byte[] CaptureRegion(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) throw new ArgumentException("Empty capture region");
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
```

- [ ] **Step 7: Build the WPF project**

```bash
dotnet build windows-app
```
Expected: 0 errors.

- [ ] **Step 8: Commit**

```bash
git add signage-core/CaptureGeometry.cs signage-core.Tests/CaptureGeometryTests.cs windows-app/ScreenCapture.cs windows-app/PiSignageControl.csproj
git commit -m "feat: capture geometry (tested) + Windows screen grab"
```

---

## Task 7: WPF signage window (region selector + push + timer)

**Files:**
- Create: `windows-app/RegionSelectorWindow.xaml` + `.cs`, `windows-app/SignageWindow.xaml` + `.cs`
- Modify: `windows-app/MainWindow.xaml` + `.cs` (launch button)

**Interfaces:**
- Consumes: `ScreenCapture`, `CaptureGeometry`, `PushClient`, `DashboardState`, `DashboardPayload`, `RoundTimer`.

- [ ] **Step 1: Create `RegionSelectorWindow.xaml`**

A fullscreen, semi-transparent overlay the worker drags a rectangle on.
```xml
<Window x:Class="PiSignage.Control.RegionSelectorWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="#33000000"
        WindowState="Maximized" Topmost="True" Cursor="Cross"
        Title="Select region">
    <Canvas x:Name="Canvas">
        <Rectangle x:Name="Sel" Stroke="#FF4DA3FF" StrokeThickness="2"
                   Fill="#224DA3FF" Visibility="Collapsed"/>
        <TextBlock Canvas.Left="20" Canvas.Top="20" Foreground="White"
                   Text="Drag over the pairings table — Esc to cancel"/>
    </Canvas>
</Window>
```

- [ ] **Step 2: Create `RegionSelectorWindow.xaml.cs`**

Returns the chosen rectangle in **device pixels** (accounts for DPI scaling).
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PiSignage.Control;

public partial class RegionSelectorWindow : Window
{
    Point _start;
    bool _dragging;
    public (int x, int y, int w, int h)? Result { get; private set; }

    public RegionSelectorWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Result = null; DialogResult = false; } };
    }

    void OnDown(object s, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Canvas); _dragging = true;
        Canvas.SetLeft(Sel, _start.X); Canvas.SetTop(Sel, _start.Y);
        Sel.Width = 0; Sel.Height = 0; Sel.Visibility = Visibility.Visible;
    }

    void OnMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(Canvas);
        var (x, y, w, h) = PiSignage.Signage.CaptureGeometry.Normalize(
            ((int)_start.X, (int)_start.Y), ((int)p.X, (int)p.Y));
        Canvas.SetLeft(Sel, x); Canvas.SetTop(Sel, y); Sel.Width = w; Sel.Height = h;
    }

    void OnUp(object s, MouseButtonEventArgs e)
    {
        _dragging = false;
        var p = e.GetPosition(Canvas);
        var (x, y, w, h) = PiSignage.Signage.CaptureGeometry.Normalize(
            ((int)_start.X, (int)_start.Y), ((int)p.X, (int)p.Y));
        // WPF units -> device pixels (DPI scale)
        var m = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformToDevice;
        Result = ((int)(x * m.M11), (int)(y * m.M22), (int)(w * m.M11), (int)(h * m.M22));
        DialogResult = w > 0 && h > 0;
    }
}
```

- [ ] **Step 3: Create `SignageWindow.xaml`**

```xml
<Window x:Class="PiSignage.Control.SignageWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Tournament Signage" Height="360" Width="440">
    <StackPanel Margin="14">
        <TextBlock Text="Target Pi (host:port)"/>
        <TextBox x:Name="TxtAgent" Text="localhost:8080" Margin="0,2,0,10"/>

        <TextBlock Text="Capture slot"/>
        <ComboBox x:Name="CmbSlot" SelectedIndex="0" Margin="0,2,0,10">
            <ComboBoxItem>pairings</ComboBoxItem>
            <ComboBoxItem>standings</ComboBoxItem>
        </ComboBox>

        <StackPanel Orientation="Horizontal" Margin="0,0,0,12">
            <Button Content="Capture region…" Click="Capture_Click" Padding="10,4"/>
            <Button x:Name="BtnRecapture" Content="Re-capture" Click="Recapture_Click"
                    Padding="10,4" Margin="8,0,0,0" IsEnabled="False"/>
        </StackPanel>

        <TextBlock Text="Round timer"/>
        <StackPanel Orientation="Horizontal" Margin="0,2,0,0">
            <TextBox x:Name="TxtMinutes" Text="25" Width="46"/>
            <TextBox x:Name="TxtRound" Text="1" Width="46" Margin="6,0,0,0"/>
            <Button Content="Start" Click="StartTimer_Click" Margin="6,0,0,0" Padding="8,2"/>
            <Button Content="Stop" Click="StopTimer_Click" Margin="6,0,0,0" Padding="8,2"/>
        </StackPanel>

        <TextBlock x:Name="Status" Margin="0,12,0,0" Foreground="#666" TextWrapping="Wrap"/>
    </StackPanel>
</Window>
```

- [ ] **Step 4: Create `SignageWindow.xaml.cs`**

```csharp
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using PiSignage.Signage;

namespace PiSignage.Control;

public partial class SignageWindow : Window
{
    readonly DashboardState _state = new();
    readonly RoundTimer _timer = new();
    readonly PushClient _client = new(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
    (int x, int y, int w, int h)? _lastRegion;
    int _counter;

    public SignageWindow() => InitializeComponent();

    string Slot => ((ComboBoxItem)CmbSlot.SelectedItem).Content!.ToString()!;
    string Base => "http://" + TxtAgent.Text.Trim();

    async void Capture_Click(object s, RoutedEventArgs e)
    {
        var sel = new RegionSelectorWindow { Owner = this };
        Hide();                       // get our own window out of the shot
        var ok = sel.ShowDialog();
        Show();
        if (ok != true || sel.Result is null) return;
        _lastRegion = sel.Result;
        BtnRecapture.IsEnabled = true;
        await CaptureAndPush(_lastRegion.Value);
    }

    async void Recapture_Click(object s, RoutedEventArgs e)
    {
        if (_lastRegion is { } r) await CaptureAndPush(r);
    }

    async Task CaptureAndPush((int x, int y, int w, int h) r)
    {
        try
        {
            var png = ScreenCapture.CaptureRegion(r.x, r.y, r.w, r.h);
            var name = $"{Slot}-{++_counter}.png";                 // unique -> cache-bust
            var path = await _client.UploadMediaAsync(Base, name, png);
            _state.Boards[Slot] = path;
            await _client.PostDashboardAsync(Base, DashboardPayload.Build(_state, _timer));
            Status.Text = $"Pushed {Slot} → {TxtAgent.Text}";
        }
        catch (Exception ex) { Status.Text = "Push failed: " + ex.Message; }
    }

    async void StartTimer_Click(object s, RoutedEventArgs e)
    {
        int min = int.TryParse(TxtMinutes.Text, out var m) ? m : 25;
        int round = int.TryParse(TxtRound.Text, out var rd) ? rd : 1;
        _timer.Start(min, $"Round {round}", round);
        await Post("Timer started");
    }

    async void StopTimer_Click(object s, RoutedEventArgs e)
    {
        _timer.Stop();
        await Post("Timer stopped");
    }

    async Task Post(string msg)
    {
        try { await _client.PostDashboardAsync(Base, DashboardPayload.Build(_state, _timer)); Status.Text = msg; }
        catch (Exception ex) { Status.Text = "Push failed: " + ex.Message; }
    }
}
```

- [ ] **Step 5: Add the launch button to `MainWindow.xaml`**

In the header area of `MainWindow.xaml`, add:
```xml
<Button Content="Tournament Signage" Click="OpenSignage_Click" Margin="8,0,0,0"/>
```

- [ ] **Step 6: Handle it in `MainWindow.xaml.cs`**

Add to the `MainWindow` class:
```csharp
void OpenSignage_Click(object sender, System.Windows.RoutedEventArgs e)
    => new SignageWindow { Owner = this }.Show();
```

- [ ] **Step 7: Build the whole solution**

```bash
dotnet build PiSignage.sln
```
Expected: 0 errors.

- [ ] **Step 8: Manual end-to-end (the payoff)**

1. Start the agent: `cd agent && ./.venv/Scripts/python.exe main.py`.
2. Run the app: `cd windows-app && dotnet run`. Click **Tournament Signage**.
3. Open any web page with a table (stand-in for RK9 pairings). Slot = `pairings`.
4. **Capture region…** → drag over the table. Status shows "Pushed pairings".
5. Open `http://localhost:8080/dashboard?view=board&name=pairings` in Chrome → the shot shows.
6. Change the page, **Re-capture** → the TV image swaps within a rotation.
7. **Start** timer (25 / round 1) → open `?view=timer` → live countdown; **Stop** → idle.
8. Add `url` items `localhost:8080/dashboard?view=board&name=pairings` and `?view=timer` to a Pi playlist alongside an image → confirm they rotate with existing signage, and `show-now` still overrides.

- [ ] **Step 9: Commit**

```bash
git add windows-app/ PiSignage.sln
git commit -m "feat(wpf): tournament signage window — region capture, push, timer"
```

---

## Self-Review

- **Spec coverage:** region capture + remember-last-region ✓ (Task 6/7); push to selected Pi via existing `/api/media` ✓ (Task 5/7); unique-filename cache-bust ✓ (Task 7 `_counter`); named board slots ✓ (Task 4/7 `Slot`); agent board + timer views + idle ✓ (Task 2); polling + local timer tick + agent-clock `endsAt` ✓ (Tasks 1–2); resilience via disk cache ✓ (Task 1); reuse of rotation/override ✓ (Task 7 step 8). Multi-Pi (push to several) → the window targets one Pi at a time in this plan; **repeating the push per Pi covers it** (noted below).
- **Deferred (by design, YAGNI):** window-capture, auto-recapture loop, multi-Pi one-shot fan-out (loop over Pis is a small later add), old-image cleanup.
- **Placeholder scan:** none — every code step is complete, including a real 1×1 PNG in the push test.
- **Type consistency:** namespace `PiSignage.Signage` for the library, `PiSignage.Control` for WPF, used consistently. Payload keys (`view_data.boards`, `timer.state/remaining/round/label`) identical across agent (Task 1/2), builder (Task 4), and page (Task 2). `PushClient.UploadMediaAsync/PostDashboardAsync`, `RoundTimer`, `DashboardState.Boards`, `CaptureGeometry.Normalize`, `ScreenCapture.CaptureRegion` names match every call site.

**Verify during implementation:** DPI scaling in `RegionSelectorWindow` (`TransformToDevice`) on a multi-monitor / non-100%-scale setup — the shop laptop should be checked, since a wrong scale crops the wrong pixels. The capture-geometry math is unit-tested; the DPI conversion is the manual check.
