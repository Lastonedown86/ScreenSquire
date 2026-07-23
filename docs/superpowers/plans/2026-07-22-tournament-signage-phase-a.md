# Tournament Signage — Phase A (Tracer Bullet) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run a single-round Pokémon tournament on the WPF laptop — add players, pair round 1, enter results, compute standings with OWP/OOWP tiebreakers, start a round timer — and display live pairings, standings, and the countdown on one Pi TV, alongside existing signage.

**Architecture:** The tournament engine is a pure `net8.0` class library (`tournament-core`, no WPF) so the correctness-critical math is testable in isolation. The WPF app references it for the UI and broadcasts a JSON payload over HTTP to the Pi agent. The agent stores the payload, caches it to disk, and serves a lightweight `/dashboard` HTML page that polls for data and ticks the timer locally. A dashboard view is just a `url` playlist item, so it reuses all existing signage plumbing.

**Tech Stack:** Python 3.13 / FastAPI (agent), C# / .NET 8 class library + WPF (control app), vanilla JS/HTML (dashboard), pytest + httpx (agent tests), xUnit (engine tests).

## Global Constraints

- Agent runtime uses **stdlib only** for the new endpoints (`json`, `time`) — no new packages in `agent/requirements.txt`.
- Agent dev/test deps (`pytest`, `httpx`) go in a separate `agent/requirements-dev.txt`.
- Engine lives in `tournament-core` (**net8.0, no WPF reference**). WPF app and test project both reference it.
- Dashboard page: **vanilla JS only**, no framework, update DOM in place, no per-second network calls. Must run on a 2GB Pi 4.
- LAN only; **no API auth** (consistent with existing system).
- Timer `endsAt` is **epoch milliseconds**, stamped by the agent against its own clock; the browser computes `remaining = (endsAt - Date.now())/1000`.
- Pokémon tiebreakers: match points (W=3, T=1, L=0, bye=3); rank by points → OWP → OOWP; per-opponent Win% floored at **0.25**; **byes excluded** from opponent lists and from Win% denominators.
- No git repo exists in this tree yet. Task 0 initializes it; commit steps assume git is present.

---

## File Structure

**Agent (Python):**
- Modify `agent/main.py` — add dashboard state + 3 endpoints.
- Create `agent/static/dashboard.html` — pairings / standings / timer / idle views.
- Create `agent/requirements-dev.txt` — pytest, httpx.
- Create `agent/tests/test_dashboard.py` — endpoint tests.

**Engine (C# class library `tournament-core/`, net8.0, no WPF):**
- `Models.cs` — `Player`, `MatchResult` enum, `Match`, `Round`, `Tournament`.
- `GamePreset.cs` — preset + built-in Pokémon preset.
- `StandingsCalculator.cs` — match points, OWP, OOWP, ranking.
- `SwissPairer.cs` — sequential pairing + bye assignment.
- `RoundTimer.cs` — start/pause/resume/extend/stop model.
- `TournamentStore.cs` — save/load JSON.
- `DashboardPayload.cs` — the wire DTO + builder from a `Tournament`.
- `DashboardClient.cs` — HTTP POST to an agent.

**Engine tests (`tournament-core.Tests/`, xUnit net8.0):**
- `StandingsCalculatorTests.cs`, `SwissPairerTests.cs`, `RoundTimerTests.cs`, `TournamentStoreTests.cs`, `DashboardPayloadTests.cs`.

**WPF:**
- Create `windows-app/TournamentWindow.xaml` + `.cs` — the TO console.
- Modify `windows-app/MainWindow.xaml` — one button to open it.
- Modify `windows-app/PiSignageControl.csproj` — reference `tournament-core`.

---

## Task 0: Project scaffolding

**Files:**
- Create: git repo, `PiSignage.sln`, `tournament-core/tournament-core.csproj`, `tournament-core.Tests/tournament-core.Tests.csproj`, `agent/requirements-dev.txt`, `.gitignore`

- [ ] **Step 1: Init git and gitignore**

Run from `C:/Users/Bill/Downloads/pi-signage/pi-signage`:
```bash
git init
printf '%s\n' 'bin/' 'obj/' '.venv/' '__pycache__/' 'agent/data/' '*.user' > .gitignore
```

- [ ] **Step 2: Create the class library and test project**

```bash
dotnet new classlib -n tournament-core -f net8.0 -o tournament-core
dotnet new xunit -n tournament-core.Tests -f net8.0 -o tournament-core.Tests
rm tournament-core/Class1.cs tournament-core.Tests/UnitTest1.cs
dotnet add tournament-core.Tests reference tournament-core
dotnet new sln -n PiSignage
dotnet sln add tournament-core tournament-core.Tests windows-app
```

- [ ] **Step 3: Create agent dev requirements**

Create `agent/requirements-dev.txt`:
```
pytest>=8
httpx>=0.27
```

- [ ] **Step 4: Verify build + test tooling**

```bash
dotnet build tournament-core.Tests
```
Expected: build succeeds, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "chore: scaffold tournament-core lib, tests, solution"
```

---

## Task 1: Agent dashboard endpoints

**Files:**
- Modify: `agent/main.py`
- Test: `agent/tests/test_dashboard.py`

**Interfaces:**
- Produces (HTTP): `POST /api/dashboard` accepts `{view_data: object, timer: {state, remaining?, round?, label?}}`; `GET /api/dashboard` returns the stored payload with `timer.endsAt` (epoch ms) set when running; `GET /dashboard` serves the HTML page.

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
        "view_data": {"round": 1},
        "timer": {"state": "running", "remaining": 1500, "label": "Round 1"},
    })
    assert r.status_code == 200 and r.json()["ok"] is True
    got = client.get("/api/dashboard").json()
    ends = got["timer"]["endsAt"]
    # endsAt is now + 1500s, in ms, anchored to the agent clock
    assert before + 1500 * 1000 <= ends <= before + 1500 * 1000 + 5000

def test_view_data_roundtrips():
    payload = {"view_data": {"pairings": [{"table": 1, "p1": "Ash", "p2": "Gary"}]},
               "timer": {"state": "stopped"}}
    client.post("/api/dashboard", json=payload)
    got = client.get("/api/dashboard").json()
    assert got["view_data"]["pairings"][0]["p1"] == "Ash"

def test_dashboard_page_served():
    assert client.get("/dashboard").status_code == 200
```

- [ ] **Step 3: Run test to verify it fails**

```bash
cd agent && ./.venv/Scripts/python.exe -m pytest tests/test_dashboard.py -v
```
Expected: FAIL (404 on `/api/dashboard`, and `/dashboard` file missing).

- [ ] **Step 4: Add endpoints to `main.py`**

Add `import time` to the imports block. After the `app.mount("/media", ...)` line, add:
```python
# ---- tournament dashboard (Phase A) ----
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
    return {"view_data": {}, "timer": {"state": "stopped"}}


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

- [ ] **Step 5: Create a placeholder page so the page test passes**

Create `agent/static/dashboard.html` with a single line (full page comes in Task 2):
```html
<!DOCTYPE html><html><head><meta charset="utf-8"><title>Dashboard</title></head><body></body></html>
```

- [ ] **Step 6: Run tests to verify they pass**

```bash
cd agent && ./.venv/Scripts/python.exe -m pytest tests/test_dashboard.py -v
```
Expected: 3 passed.

- [ ] **Step 7: Commit**

```bash
git add agent/main.py agent/tests/test_dashboard.py agent/static/dashboard.html agent/requirements-dev.txt
git commit -m "feat(agent): dashboard payload endpoints with agent-clock timer stamping"
```

---

## Task 2: Dashboard page (pairings / standings / timer / idle)

**Files:**
- Modify: `agent/static/dashboard.html`
- Test: `agent/tests/test_dashboard.py` (add a content assertion)

**Interfaces:**
- Consumes (HTTP): `GET /api/dashboard`. URL param `?view=pairings|standings|timer`.

- [ ] **Step 1: Write the failing content test**

Add to `agent/tests/test_dashboard.py`:
```python
def test_dashboard_page_has_views_and_poll():
    html = client.get("/dashboard").text
    assert "view-standings" in html and "view-pairings" in html and "view-timer" in html
    assert "/api/dashboard" in html            # it polls
    assert "No active tournament" in html      # idle state
```

- [ ] **Step 2: Run to verify it fails**

```bash
cd agent && ./.venv/Scripts/python.exe -m pytest tests/test_dashboard.py::test_dashboard_page_has_views_and_poll -v
```
Expected: FAIL (placeholder page has none of these).

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
  html,body { margin:0; height:100%; background:#0b0d12; color:#e8eaf0;
    font:500 2.4vmin/1.3 system-ui,sans-serif; overflow:hidden; cursor:none; }
  #root { height:100%; contain:strict; display:flex; flex-direction:column; }
  header { padding:2vmin 3vmin; display:flex; justify-content:space-between;
    align-items:baseline; border-bottom:1px solid #232838; }
  header .game { font-weight:700; letter-spacing:.02em; }
  header .round { color:#8b93a7; }
  main { flex:1; overflow:hidden; padding:1vmin 3vmin; }
  table { width:100%; border-collapse:collapse; }
  th,td { text-align:left; padding:1.2vmin 1.6vmin; }
  thead th { color:#8b93a7; font-weight:600; font-size:2vmin;
    border-bottom:1px solid #232838; }
  tbody tr:nth-child(even){ background:#12151d; }
  .rank { color:#8b93a7; width:6vmin; }
  .rec { color:#9fb0d0; }
  .clock { display:flex; align-items:center; justify-content:center; height:100%;
    flex-direction:column; }
  .clock .time { font-variant-numeric:tabular-nums; font-weight:800;
    font-size:26vmin; line-height:1; }
  .clock .time.over { color:#ff5d5d; }
  .clock .label { color:#8b93a7; font-size:4vmin; margin-top:2vmin; }
  #idle { display:flex; align-items:center; justify-content:center; height:100%;
    color:#5a627a; font-size:3.2vmin; }
  .hidden { display:none !important; }
</style>
</head>
<body>
  <div id="root">
    <header><span class="game" id="game"></span><span class="round" id="round"></span></header>
    <main>
      <div id="idle">No active tournament</div>
      <table id="view-standings" class="hidden">
        <thead><tr><th class="rank">#</th><th>Player</th><th>Record</th><th>Pts</th><th>OWP</th><th>OOWP</th></tr></thead>
        <tbody id="standings-body"></tbody>
      </table>
      <table id="view-pairings" class="hidden">
        <thead><tr><th class="rank">Table</th><th>Player 1</th><th>Player 2</th></tr></thead>
        <tbody id="pairings-body"></tbody>
      </table>
      <div id="view-timer" class="clock hidden">
        <div class="time" id="clock-time">--:--</div>
        <div class="label" id="clock-label"></div>
      </div>
    </main>
  </div>
<script>
(() => {
  const view = new URLSearchParams(location.search).get('view') || 'standings';
  const $ = id => document.getElementById(id);
  let data = null;      // last payload
  let endsAt = null;    // epoch ms for a running timer
  let pausedRemaining = null;

  function fmt(sec){ sec=Math.max(0,Math.round(sec));
    return String(Math.floor(sec/60)).padStart(2,'0')+':'+String(sec%60).padStart(2,'0'); }

  function showIdle(on){ $('idle').classList.toggle('hidden', !on);
    ['view-standings','view-pairings','view-timer'].forEach(v=>$(v).classList.add('hidden'));
    if(!on) $('view-'+view).classList.remove('hidden'); }

  function renderStatic(){
    const vd = (data && data.view_data) || {};
    const hasContent = view==='timer'
      ? (data && data.timer && data.timer.state!=='stopped')
      : Array.isArray(vd[view]) && vd[view].length>0;
    if(!hasContent){ showIdle(true); return; }
    showIdle(false);
    $('game').textContent = vd.game || '';
    $('round').textContent = vd.round ? ('Round '+vd.round) : '';
    if(view==='standings'){
      $('standings-body').innerHTML = vd.standings.map(s=>
        `<tr><td class="rank">${s.rank}</td><td>${esc(s.name)}</td>`+
        `<td class="rec">${esc(s.record)}</td><td>${s.points}</td>`+
        `<td>${pct(s.owp)}</td><td>${pct(s.oowp)}</td></tr>`).join('');
    } else if(view==='pairings'){
      $('pairings-body').innerHTML = vd.pairings.map(p=>
        `<tr><td class="rank">${p.table}</td><td>${esc(p.p1)}</td>`+
        `<td>${esc(p.p2||'BYE')}</td></tr>`).join('');
    } else if(view==='timer'){
      $('clock-label').textContent = (data.timer.label)||'';
    }
  }
  function esc(s){ return String(s).replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c])); }
  function pct(x){ return x==null?'':(Math.round(x*1000)/10).toFixed(1)+'%'; }

  function tick(){
    if(view!=='timer' || !data || !data.timer) return;
    const t = data.timer;
    let rem;
    if(t.state==='running' && endsAt!=null) rem = (endsAt - Date.now())/1000;
    else if(t.state==='paused') rem = pausedRemaining;
    else return;
    const el = $('clock-time'); el.textContent = fmt(rem);
    el.classList.toggle('over', rem<=0);
    if(rem<=0) el.textContent = 'TIME';
  }

  async function poll(){
    try{
      const r = await fetch('/api/dashboard',{cache:'no-store'});
      data = await r.json();
      const t = data.timer||{};
      endsAt = (t.state==='running') ? t.endsAt : null;
      pausedRemaining = (t.state==='paused') ? t.remaining : null;
      renderStatic();
    }catch(e){ /* keep last render; TV never goes blank on a blip */ }
  }
  poll(); setInterval(poll, 15000);   // state changes are infrequent
  setInterval(tick, 250);             // smooth local countdown
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

- [ ] **Step 5: Manual smoke (real browser, not the automation tab)**

Start the agent (`./.venv/Scripts/python.exe main.py`), then:
```bash
curl -s -X POST http://localhost:8080/api/dashboard -H "Content-Type: application/json" -d '{
  "view_data":{"game":"Pokémon","round":1,
    "pairings":[{"table":1,"p1":"Ash","p2":"Gary"},{"table":2,"p1":"Misty","p2":"BYE"}],
    "standings":[{"rank":1,"name":"Ash","record":"1-0-0","points":3,"owp":0.5,"oowp":0.5}]},
  "timer":{"state":"running","remaining":1500,"label":"Round 1"}}'
```
Open `http://localhost:8080/dashboard?view=timer` in Chrome/Edge — clock counts down. Try `?view=pairings` and `?view=standings`. Clear with `{"view_data":{},"timer":{"state":"stopped"}}` → shows "No active tournament".

- [ ] **Step 6: Commit**

```bash
git add agent/static/dashboard.html agent/tests/test_dashboard.py
git commit -m "feat(agent): tournament dashboard page (pairings/standings/timer/idle)"
```

---

## Task 3: Engine domain model + Pokémon preset

**Files:**
- Create: `tournament-core/Models.cs`, `tournament-core/GamePreset.cs`

**Interfaces:**
- Produces: `Player{ string Id, string Name, bool Dropped }`; enum `MatchResult{ Pending, P1Win, P2Win, Draw, P1Bye }`; `Match{ int Table, Player P1, Player? P2, MatchResult Result }`; `Round{ int Number, List<Match> Matches }`; `Tournament{ string Id, string Name, GamePreset Preset, List<Player> Players, List<Round> Rounds, int CurrentRound, string Status }`; `GamePreset{ string Name, int PointsWin, int PointsDraw, int PointsLoss, double WinPctFloor, int DefaultRoundMinutes, string[] Tiebreakers }` with static `GamePreset.Pokemon`.

- [ ] **Step 1: Write `Models.cs`**

```csharp
namespace PiSignage.Tournament;

public sealed class Player
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public bool Dropped { get; set; }
}

public enum MatchResult { Pending, P1Win, P2Win, Draw, P1Bye }

public sealed class Match
{
    public int Table { get; set; }
    public Player P1 { get; set; } = null!;
    public Player? P2 { get; set; }               // null == bye
    public MatchResult Result { get; set; } = MatchResult.Pending;
    public bool IsBye => P2 is null;
}

public sealed class Round
{
    public int Number { get; set; }
    public List<Match> Matches { get; set; } = new();
}

public sealed class Tournament
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public GamePreset Preset { get; set; } = GamePreset.Pokemon;
    public List<Player> Players { get; set; } = new();
    public List<Round> Rounds { get; set; } = new();
    public int CurrentRound { get; set; }
    public string Status { get; set; } = "setup";  // setup | running | done
}
```

- [ ] **Step 2: Write `GamePreset.cs`**

```csharp
namespace PiSignage.Tournament;

public sealed class GamePreset
{
    public string Name { get; init; } = "";
    public int PointsWin { get; init; } = 3;
    public int PointsDraw { get; init; } = 1;
    public int PointsLoss { get; init; } = 0;
    public double WinPctFloor { get; init; } = 0.25;
    public int DefaultRoundMinutes { get; init; } = 25;
    public string[] Tiebreakers { get; init; } = { "OWP", "OOWP" };

    public static readonly GamePreset Pokemon = new()
    {
        Name = "Pokémon",
        PointsWin = 3, PointsDraw = 1, PointsLoss = 0,
        WinPctFloor = 0.25, DefaultRoundMinutes = 25,
        Tiebreakers = new[] { "OWP", "OOWP" },
    };
}
```

- [ ] **Step 3: Build**

```bash
dotnet build tournament-core
```
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add tournament-core/Models.cs tournament-core/GamePreset.cs
git commit -m "feat(engine): tournament domain model + Pokémon preset"
```

---

## Task 4: Standings calculator (OWP / OOWP) — correctness-critical

**Files:**
- Create: `tournament-core/StandingsCalculator.cs`
- Test: `tournament-core.Tests/StandingsCalculatorTests.cs`

**Interfaces:**
- Consumes: `Tournament`, `Player`, `Match`, `MatchResult`, `GamePreset`.
- Produces: `record Standing(int Rank, Player Player, string Record, int Points, double Owp, double Oowp)`; `StandingsCalculator.Compute(Tournament t) -> List<Standing>` sorted by Points desc, Owp desc, Oowp desc.

- [ ] **Step 1: Write the failing tests (hand-worked bracket)**

Create `tournament-core.Tests/StandingsCalculatorTests.cs`:
```csharp
using PiSignage.Tournament;
using Xunit;

public class StandingsCalculatorTests
{
    static Player P(string n) => new() { Name = n };

    // 4 players, 2 rounds, no bye/tie. Hand-computed expected values.
    // R1: A>B, C>D.  R2: A>C, B>D.
    // Win%: A=1.0 B=.5 C=.5 D=0->floor .25
    // OWP:  A=.5  B=.625 C=.625 D=.5
    // OOWP: A=.625 B=.5 C=.5 D=.625
    [Fact]
    public void ComputesPointsOwpOowpAndRanksThem()
    {
        var a = P("A"); var b = P("B"); var c = P("C"); var d = P("D");
        var t = new Tournament { Players = { a, b, c, d } };
        t.Rounds.Add(new Round { Number = 1, Matches = {
            new Match { Table = 1, P1 = a, P2 = b, Result = MatchResult.P1Win },
            new Match { Table = 2, P1 = c, P2 = d, Result = MatchResult.P1Win } } });
        t.Rounds.Add(new Round { Number = 2, Matches = {
            new Match { Table = 1, P1 = a, P2 = c, Result = MatchResult.P1Win },
            new Match { Table = 2, P1 = b, P2 = d, Result = MatchResult.P1Win } } });

        var s = StandingsCalculator.Compute(t);

        Assert.Equal("A", s[0].Player.Name);
        Assert.Equal(6, s[0].Points);
        Assert.Equal(0.5, s[0].Owp, 3);
        Assert.Equal(0.625, s[0].Oowp, 3);

        var dS = s.Single(x => x.Player.Name == "D");
        Assert.Equal(0, dS.Points);
        Assert.Equal(0.5, dS.Owp, 3);
        Assert.Equal(0.625, dS.Oowp, 3);

        var bS = s.Single(x => x.Player.Name == "B");
        Assert.Equal(3, bS.Points);
        Assert.Equal(0.625, bS.Owp, 3);
        Assert.Equal("1-1-0", bS.Record);
    }

    // Bye is excluded from opponents' Win% math; bye player still gets 3 pts.
    [Fact]
    public void ByeCountsAsPointsButIsExcludedFromOpponentMath()
    {
        var a = P("A"); var b = P("B"); var c = P("C");
        var t = new Tournament { Players = { a, b, c } };
        t.Rounds.Add(new Round { Number = 1, Matches = {
            new Match { Table = 1, P1 = a, P2 = b, Result = MatchResult.P1Win },
            new Match { Table = 2, P1 = c, P2 = null, Result = MatchResult.P1Bye } } });

        var s = StandingsCalculator.Compute(t);
        Assert.Equal(3, s.Single(x => x.Player.Name == "C").Points);   // bye = win
        // A's only opponent is B (win% floored to .25) -> A.OWP == .25
        Assert.Equal(0.25, s.Single(x => x.Player.Name == "A").Owp, 3);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test tournament-core.Tests
```
Expected: FAIL (StandingsCalculator not found).

- [ ] **Step 3: Implement `StandingsCalculator.cs`**

```csharp
namespace PiSignage.Tournament;

public record Standing(int Rank, Player Player, string Record, int Points, double Owp, double Oowp);

public static class StandingsCalculator
{
    public static List<Standing> Compute(Tournament t)
    {
        var preset = t.Preset;
        // Gather, per player: wins/losses/ties/byes/points and real opponents.
        var wins = new Dictionary<string, int>();
        var losses = new Dictionary<string, int>();
        var ties = new Dictionary<string, int>();
        var pts = new Dictionary<string, int>();
        var played = new Dictionary<string, int>();          // real matches, byes excluded
        var opps = new Dictionary<string, List<string>>();   // opponent ids, byes excluded
        foreach (var p in t.Players)
        { wins[p.Id]=0; losses[p.Id]=0; ties[p.Id]=0; pts[p.Id]=0; played[p.Id]=0; opps[p.Id]=new(); }

        foreach (var r in t.Rounds)
            foreach (var m in r.Matches)
            {
                if (m.Result == MatchResult.Pending) continue;
                if (m.IsBye)
                { wins[m.P1.Id]++; pts[m.P1.Id]+=preset.PointsWin; continue; }  // bye = win, excluded elsewhere
                var p1=m.P1.Id; var p2=m.P2!.Id;
                played[p1]++; played[p2]++;
                opps[p1].Add(p2); opps[p2].Add(p1);
                switch (m.Result)
                {
                    case MatchResult.P1Win: wins[p1]++; losses[p2]++; pts[p1]+=preset.PointsWin; pts[p2]+=preset.PointsLoss; break;
                    case MatchResult.P2Win: wins[p2]++; losses[p1]++; pts[p2]+=preset.PointsWin; pts[p1]+=preset.PointsLoss; break;
                    case MatchResult.Draw:  ties[p1]++; ties[p2]++; pts[p1]+=preset.PointsDraw; pts[p2]+=preset.PointsDraw; break;
                }
            }

        // Win% for opponents' math: wins / real matches played, floored. Ties count in the
        // denominator, not as wins. (Verify against the current Play! Pokémon rulebook.)
        double WinPct(string id) =>
            played[id] == 0 ? preset.WinPctFloor
                            : Math.Max(preset.WinPctFloor, (double)wins[id] / played[id]);

        double Owp(string id) =>
            opps[id].Count == 0 ? 0.0 : opps[id].Average(WinPct);

        double Oowp(string id) =>
            opps[id].Count == 0 ? 0.0 : opps[id].Average(o => Owp(o));

        var rows = t.Players.Select(p => new {
            p,
            Rec = $"{wins[p.Id]}-{losses[p.Id]}-{ties[p.Id]}",
            Pts = pts[p.Id],
            OwpV = Owp(p.Id),
            OowpV = Oowp(p.Id),
        })
        .OrderByDescending(x => x.Pts)
        .ThenByDescending(x => x.OwpV)
        .ThenByDescending(x => x.OowpV)
        .ToList();

        return rows.Select((x, i) => new Standing(i + 1, x.p, x.Rec, x.Pts, x.OwpV, x.OowpV)).ToList();
    }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test tournament-core.Tests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tournament-core/StandingsCalculator.cs tournament-core.Tests/StandingsCalculatorTests.cs
git commit -m "feat(engine): Pokémon standings with OWP/OOWP tiebreakers + tests"
```

---

## Task 5: Swiss pairer (round 1 + bye)

**Files:**
- Create: `tournament-core/SwissPairer.cs`
- Test: `tournament-core.Tests/SwissPairerTests.cs`

**Interfaces:**
- Produces: `SwissPairer.PairNextRound(IReadOnlyList<Player> ordered) -> Round`. Pairs sequentially (0v1, 2v3, …); odd count → last active player gets a bye (`P1Bye`, table = last). Round `Number` is 1 (Phase A pairs round 1 only; caller shuffles `ordered` for randomness). Dropped players are excluded.

- [ ] **Step 1: Write failing tests**

Create `tournament-core.Tests/SwissPairerTests.cs`:
```csharp
using PiSignage.Tournament;
using Xunit;

public class SwissPairerTests
{
    static Player P(string n) => new() { Name = n };

    [Fact]
    public void EvenCountPairsSequentiallyNoBye()
    {
        var ps = new[] { P("A"), P("B"), P("C"), P("D") };
        var r = SwissPairer.PairNextRound(ps);
        Assert.Equal(2, r.Matches.Count);
        Assert.Equal("A", r.Matches[0].P1.Name);
        Assert.Equal("B", r.Matches[0].P2!.Name);
        Assert.DoesNotContain(r.Matches, m => m.IsBye);
    }

    [Fact]
    public void OddCountGivesLastPlayerABye()
    {
        var ps = new[] { P("A"), P("B"), P("C") };
        var r = SwissPairer.PairNextRound(ps);
        var bye = Assert.Single(r.Matches, m => m.IsBye);
        Assert.Equal("C", bye.P1.Name);
        Assert.Equal(MatchResult.P1Bye, bye.Result);
    }

    [Fact]
    public void DroppedPlayersAreExcluded()
    {
        var ps = new[] { P("A"), new Player { Name = "B", Dropped = true }, P("C") };
        var r = SwissPairer.PairNextRound(ps);
        Assert.DoesNotContain(r.Matches.SelectMany(m => new[] { m.P1, m.P2 }),
                              p => p is { Name: "B" });
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test tournament-core.Tests --filter SwissPairerTests
```
Expected: FAIL (SwissPairer not found).

- [ ] **Step 3: Implement `SwissPairer.cs`**

```csharp
namespace PiSignage.Tournament;

public static class SwissPairer
{
    public static Round PairNextRound(IReadOnlyList<Player> ordered)
    {
        var active = ordered.Where(p => !p.Dropped).ToList();
        var round = new Round { Number = 1 };
        int table = 1;
        int i = 0;
        for (; i + 1 < active.Count; i += 2)
            round.Matches.Add(new Match { Table = table++, P1 = active[i], P2 = active[i + 1] });
        if (i < active.Count)  // odd leftover
            round.Matches.Add(new Match { Table = table, P1 = active[i], P2 = null, Result = MatchResult.P1Bye });
        return round;
    }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test tournament-core.Tests --filter SwissPairerTests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tournament-core/SwissPairer.cs tournament-core.Tests/SwissPairerTests.cs
git commit -m "feat(engine): round-1 Swiss pairing with bye assignment + tests"
```

---

## Task 6: Round timer model

**Files:**
- Create: `tournament-core/RoundTimer.cs`
- Test: `tournament-core.Tests/RoundTimerTests.cs`

**Interfaces:**
- Produces: `RoundTimer` with `State (Stopped|Running|Paused)`, `int? RemainingSeconds`, `string? Label`, methods `Start(int minutes, string label)`, `Pause(int remainingSeconds)`, `Resume(int remainingSeconds)`, `Stop()`. Pure model — the countdown itself runs in the browser; this only tracks intent + the `remaining` value sent to the agent.

- [ ] **Step 1: Write failing tests**

Create `tournament-core.Tests/RoundTimerTests.cs`:
```csharp
using PiSignage.Tournament;
using Xunit;

public class RoundTimerTests
{
    [Fact]
    public void StartSetsRunningWithRemainingSeconds()
    {
        var t = new RoundTimer();
        t.Start(25, "Round 1");
        Assert.Equal(TimerRunState.Running, t.State);
        Assert.Equal(1500, t.RemainingSeconds);
        Assert.Equal("Round 1", t.Label);
    }

    [Fact]
    public void PauseThenResumeKeepsRemaining()
    {
        var t = new RoundTimer();
        t.Start(25, "R1");
        t.Pause(600);
        Assert.Equal(TimerRunState.Paused, t.State);
        Assert.Equal(600, t.RemainingSeconds);
        t.Resume(600);
        Assert.Equal(TimerRunState.Running, t.State);
    }

    [Fact]
    public void StopClearsState()
    {
        var t = new RoundTimer();
        t.Start(25, "R1");
        t.Stop();
        Assert.Equal(TimerRunState.Stopped, t.State);
        Assert.Null(t.RemainingSeconds);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test tournament-core.Tests --filter RoundTimerTests
```
Expected: FAIL.

- [ ] **Step 3: Implement `RoundTimer.cs`**

```csharp
namespace PiSignage.Tournament;

public enum TimerRunState { Stopped, Running, Paused }

public sealed class RoundTimer
{
    public TimerRunState State { get; private set; } = TimerRunState.Stopped;
    public int? RemainingSeconds { get; private set; }
    public string? Label { get; private set; }

    public void Start(int minutes, string label)
    { State = TimerRunState.Running; RemainingSeconds = minutes * 60; Label = label; }

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
dotnet test tournament-core.Tests --filter RoundTimerTests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tournament-core/RoundTimer.cs tournament-core.Tests/RoundTimerTests.cs
git commit -m "feat(engine): round timer model + tests"
```

---

## Task 7: Dashboard payload builder + persistence

**Files:**
- Create: `tournament-core/DashboardPayload.cs`, `tournament-core/TournamentStore.cs`
- Test: `tournament-core.Tests/DashboardPayloadTests.cs`, `tournament-core.Tests/TournamentStoreTests.cs`

**Interfaces:**
- Produces: `DashboardPayload.Build(Tournament t, RoundTimer timer) -> object` — a JSON-serializable object matching the agent contract `{view_data:{game,round,pairings[],standings[]}, timer:{state,remaining,round,label}}`. `TournamentStore.Save(Tournament t, string path)` / `Load(string path) -> Tournament` (round-trip via System.Text.Json).

- [ ] **Step 1: Write failing tests**

Create `tournament-core.Tests/DashboardPayloadTests.cs`:
```csharp
using System.Text.Json;
using PiSignage.Tournament;
using Xunit;

public class DashboardPayloadTests
{
    [Fact]
    public void BuildProducesAgentContractShape()
    {
        var a = new Player { Name = "Ash" }; var b = new Player { Name = "Gary" };
        var t = new Tournament { Name = "Wed Pokémon", CurrentRound = 1, Players = { a, b } };
        t.Rounds.Add(new Round { Number = 1, Matches = {
            new Match { Table = 1, P1 = a, P2 = b, Result = MatchResult.P1Win } } });
        var timer = new RoundTimer(); timer.Start(25, "Round 1");

        var json = JsonSerializer.Serialize(DashboardPayload.Build(t, timer));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("Pokémon", root.GetProperty("view_data").GetProperty("game").GetString());
        Assert.Equal(1, root.GetProperty("view_data").GetProperty("round").GetInt32());
        Assert.True(root.GetProperty("view_data").GetProperty("pairings").GetArrayLength() >= 1);
        Assert.Equal("running", root.GetProperty("timer").GetProperty("state").GetString());
        Assert.Equal(1500, root.GetProperty("timer").GetProperty("remaining").GetInt32());
    }
}
```

Create `tournament-core.Tests/TournamentStoreTests.cs`:
```csharp
using PiSignage.Tournament;
using Xunit;

public class TournamentStoreTests
{
    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var a = new Player { Name = "Ash" };
        var t = new Tournament { Name = "Wed", Players = { a } };
        var path = Path.Combine(Path.GetTempPath(), $"tour-{Guid.NewGuid():N}.json");
        try
        {
            TournamentStore.Save(t, path);
            var loaded = TournamentStore.Load(path);
            Assert.Equal("Wed", loaded.Name);
            Assert.Equal("Ash", loaded.Players[0].Name);
            Assert.Equal("Pokémon", loaded.Preset.Name);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test tournament-core.Tests --filter "DashboardPayloadTests|TournamentStoreTests"
```
Expected: FAIL.

- [ ] **Step 3: Implement `DashboardPayload.cs`**

```csharp
namespace PiSignage.Tournament;

public static class DashboardPayload
{
    public static object Build(Tournament t, RoundTimer timer)
    {
        var round = t.Rounds.LastOrDefault();
        var pairings = (round?.Matches ?? new List<Match>()).Select(m => new {
            table = m.Table,
            p1 = m.P1.Name,
            p2 = m.P2?.Name,   // null -> serialized as null -> page shows "BYE"
        });
        var standings = StandingsCalculator.Compute(t).Select(s => new {
            rank = s.Rank, name = s.Player.Name, record = s.Record,
            points = s.Points, owp = s.Owp, oowp = s.Oowp,
        });
        return new
        {
            view_data = new { game = t.Preset.Name, round = t.CurrentRound, pairings, standings },
            timer = new
            {
                state = timer.State.ToString().ToLowerInvariant(),
                remaining = timer.RemainingSeconds,
                round = t.CurrentRound,
                label = timer.Label,
            },
        };
    }
}
```

- [ ] **Step 4: Implement `TournamentStore.cs`**

```csharp
using System.Text.Json;

namespace PiSignage.Tournament;

public static class TournamentStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static void Save(Tournament t, string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(t, Opts));
        File.Move(tmp, path, overwrite: true);   // atomic-ish; protects against mid-write crash
    }

    public static Tournament Load(string path) =>
        JsonSerializer.Deserialize<Tournament>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty tournament file");
}
```

- [ ] **Step 5: Run to verify pass**

```bash
dotnet test tournament-core.Tests
```
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add tournament-core/DashboardPayload.cs tournament-core/TournamentStore.cs tournament-core.Tests/DashboardPayloadTests.cs tournament-core.Tests/TournamentStoreTests.cs
git commit -m "feat(engine): dashboard payload builder + tournament persistence + tests"
```

---

## Task 8: Dashboard HTTP client

**Files:**
- Create: `tournament-core/DashboardClient.cs`
- Test: `tournament-core.Tests/DashboardClientTests.cs`

**Interfaces:**
- Produces: `DashboardClient(HttpClient http)` with `Task PostAsync(string agentBaseUrl, object payload)` → POSTs JSON to `{agentBaseUrl}/api/dashboard`.

- [ ] **Step 1: Write the failing test (against the running agent)**

This test talks to a live agent on `localhost:8080`; it is skipped if the agent is not up. Create `tournament-core.Tests/DashboardClientTests.cs`:
```csharp
using System.Net.Http;
using PiSignage.Tournament;
using Xunit;

public class DashboardClientTests
{
    [Fact]
    public async Task PostReachesAgentWhenRunning()
    {
        using var http = new HttpClient();
        try { await http.GetAsync("http://localhost:8080/api/status"); }
        catch { return; }  // agent not running -> skip silently

        var client = new DashboardClient(http);
        var payload = new { view_data = new { game = "Pokémon", round = 1,
            pairings = new[] { new { table = 1, p1 = "Ash", p2 = (string?)"Gary" } },
            standings = Array.Empty<object>() },
            timer = new { state = "stopped", remaining = (int?)null, round = 1, label = (string?)null } };

        await client.PostAsync("http://localhost:8080", payload);   // throws on failure

        var back = await http.GetStringAsync("http://localhost:8080/api/dashboard");
        Assert.Contains("Ash", back);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test tournament-core.Tests --filter DashboardClientTests
```
Expected: FAIL (DashboardClient not found) — compile error even if the agent is down.

- [ ] **Step 3: Implement `DashboardClient.cs`**

```csharp
using System.Net.Http.Json;

namespace PiSignage.Tournament;

public sealed class DashboardClient(HttpClient http)
{
    public async Task PostAsync(string agentBaseUrl, object payload)
    {
        var url = agentBaseUrl.TrimEnd('/') + "/api/dashboard";
        var resp = await http.PostAsJsonAsync(url, payload);
        resp.EnsureSuccessStatusCode();
    }
}
```

- [ ] **Step 4: Run to verify pass**

Start the agent first (`cd agent && ./.venv/Scripts/python.exe main.py`), then:
```bash
dotnet test tournament-core.Tests --filter DashboardClientTests
```
Expected: PASS (or a silent skip if the agent is down — then start it and re-run to confirm).

- [ ] **Step 5: Commit**

```bash
git add tournament-core/DashboardClient.cs tournament-core.Tests/DashboardClientTests.cs
git commit -m "feat(engine): dashboard HTTP client + integration test"
```

---

## Task 9: WPF tournament console + broadcast

**Files:**
- Modify: `windows-app/PiSignageControl.csproj` (reference the library)
- Create: `windows-app/TournamentWindow.xaml`, `windows-app/TournamentWindow.xaml.cs`
- Modify: `windows-app/MainWindow.xaml` (add a button), `windows-app/MainWindow.xaml.cs` (open the window)

**Interfaces:**
- Consumes: everything from `tournament-core` (`Tournament`, `SwissPairer`, `StandingsCalculator`, `RoundTimer`, `DashboardPayload`, `DashboardClient`, `TournamentStore`).

- [ ] **Step 1: Reference the library**

Edit `windows-app/PiSignageControl.csproj` — add inside a new `<ItemGroup>`:
```xml
  <ItemGroup>
    <ProjectReference Include="..\tournament-core\tournament-core.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Create `TournamentWindow.xaml`**

```xml
<Window x:Class="PiSignage.Control.TournamentWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Tournament" Height="620" Width="900">
    <Grid Margin="12">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="1*"/>
            <ColumnDefinition Width="1.4*"/>
        </Grid.ColumnDefinitions>

        <!-- ==== players + setup ==== -->
        <GroupBox Grid.Column="0" Header="Players">
            <DockPanel>
                <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,6">
                    <TextBox x:Name="TxtPlayer" Width="180"/>
                    <Button Content="Add" Margin="6,0,0,0" Click="AddPlayer_Click"/>
                </StackPanel>
                <StackPanel DockPanel.Dock="Bottom" Margin="0,6,0,0">
                    <Button Content="Generate Round 1 pairings" Click="Pair_Click"/>
                    <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                        <TextBox x:Name="TxtAgent" Width="180" Text="localhost:8080"/>
                        <Button Content="Broadcast" Margin="6,0,0,0" Click="Broadcast_Click"/>
                    </StackPanel>
                    <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                        <TextBox x:Name="TxtMinutes" Width="50" Text="25"/>
                        <Button Content="Start timer" Margin="6,0,0,0" Click="StartTimer_Click"/>
                        <Button Content="Stop" Margin="6,0,0,0" Click="StopTimer_Click"/>
                    </StackPanel>
                </StackPanel>
                <ListBox x:Name="LstPlayers"/>
            </DockPanel>
        </GroupBox>

        <!-- ==== pairings + standings ==== -->
        <GroupBox Grid.Column="1" Header="Round / Standings">
            <DockPanel>
                <DataGrid x:Name="GridPairings" DockPanel.Dock="Top" Height="220"
                          AutoGenerateColumns="False" IsReadOnly="False" CanUserAddRows="False">
                    <DataGrid.Columns>
                        <DataGridTextColumn Header="Table" Binding="{Binding Table}" IsReadOnly="True"/>
                        <DataGridTextColumn Header="P1" Binding="{Binding P1.Name}" IsReadOnly="True"/>
                        <DataGridTextColumn Header="P2" Binding="{Binding P2Name}" IsReadOnly="True"/>
                        <DataGridComboBoxColumn Header="Result" x:Name="ColResult" Width="120"/>
                    </DataGrid.Columns>
                </DataGrid>
                <Button DockPanel.Dock="Top" Content="Save results + recompute standings"
                        Margin="0,6" Click="Save_Click"/>
                <DataGrid x:Name="GridStandings" AutoGenerateColumns="False" IsReadOnly="True">
                    <DataGrid.Columns>
                        <DataGridTextColumn Header="#" Binding="{Binding Rank}"/>
                        <DataGridTextColumn Header="Player" Binding="{Binding Player.Name}"/>
                        <DataGridTextColumn Header="Rec" Binding="{Binding Record}"/>
                        <DataGridTextColumn Header="Pts" Binding="{Binding Points}"/>
                        <DataGridTextColumn Header="OWP" Binding="{Binding Owp, StringFormat=P1}"/>
                        <DataGridTextColumn Header="OOWP" Binding="{Binding Oowp, StringFormat=P1}"/>
                    </DataGrid.Columns>
                </DataGrid>
            </DockPanel>
        </GroupBox>
    </Grid>
</Window>
```

- [ ] **Step 3: Create `TournamentWindow.xaml.cs`**

```csharp
using System.Net.Http;
using System.Windows;
using PiSignage.Tournament;

namespace PiSignage.Control;

public partial class TournamentWindow : Window
{
    readonly Tournament _t = new() { Name = "Event" };
    readonly RoundTimer _timer = new();
    readonly DashboardClient _client = new(new HttpClient { Timeout = TimeSpan.FromSeconds(5) });

    public TournamentWindow()
    {
        InitializeComponent();
        ColResult.ItemsSource = new[] { "Pending", "P1Win", "P2Win", "Draw" };
        ColResult.SelectedValueBinding = new System.Windows.Data.Binding("ResultText");
    }

    void AddPlayer_Click(object s, RoutedEventArgs e)
    {
        var name = TxtPlayer.Text.Trim();
        if (name.Length == 0) return;
        _t.Players.Add(new Player { Name = name });
        TxtPlayer.Clear();
        LstPlayers.ItemsSource = null;
        LstPlayers.ItemsSource = _t.Players.Select(p => p.Name).ToList();
    }

    void Pair_Click(object s, RoutedEventArgs e)
    {
        var shuffled = _t.Players.OrderBy(_ => Guid.NewGuid()).ToList();  // R1 random
        var round = SwissPairer.PairNextRound(shuffled);
        _t.Rounds.Clear(); _t.Rounds.Add(round); _t.CurrentRound = 1; _t.Status = "running";
        GridPairings.ItemsSource = round.Matches.Select(m => new PairingRow(m)).ToList();
        RefreshStandings();
    }

    void Save_Click(object s, RoutedEventArgs e)
    {
        foreach (var row in (IEnumerable<PairingRow>)GridPairings.ItemsSource) row.Apply();
        TournamentStore.Save(_t, System.IO.Path.Combine(
            AppContext.BaseDirectory, $"tournament-{_t.Id}.json"));
        RefreshStandings();
    }

    void RefreshStandings() => GridStandings.ItemsSource = StandingsCalculator.Compute(_t);

    void StartTimer_Click(object s, RoutedEventArgs e)
    {
        _timer.Start(int.TryParse(TxtMinutes.Text, out var m) ? m : 25, $"Round {_t.CurrentRound}");
        _ = BroadcastAsync();
    }

    void StopTimer_Click(object s, RoutedEventArgs e) { _timer.Stop(); _ = BroadcastAsync(); }

    void Broadcast_Click(object s, RoutedEventArgs e) => _ = BroadcastAsync();

    async Task BroadcastAsync()
    {
        try
        {
            var payload = DashboardPayload.Build(_t, _timer);
            await _client.PostAsync($"http://{TxtAgent.Text.Trim()}", payload);
        }
        catch (Exception ex) { MessageBox.Show($"Broadcast failed: {ex.Message}"); }
    }
}

// View-model row so the DataGrid can edit a result without mutating the Match until "Save".
public sealed class PairingRow(Match m)
{
    public int Table => m.Table;
    public Player P1 => m.P1;
    public string P2Name => m.P2?.Name ?? "BYE";
    public string ResultText { get; set; } = m.Result.ToString();
    public void Apply() =>
        m.Result = m.IsBye ? MatchResult.P1Bye : Enum.Parse<MatchResult>(ResultText);
}
```

- [ ] **Step 4: Add the launch button to `MainWindow.xaml`**

In `MainWindow.xaml`, inside the top `<Border>`/header area (near the existing header controls), add:
```xml
<Button Content="Tournament" Click="OpenTournament_Click" Margin="8,0,0,0"/>
```

- [ ] **Step 5: Handle the click in `MainWindow.xaml.cs`**

Add the method to the `MainWindow` class:
```csharp
void OpenTournament_Click(object sender, System.Windows.RoutedEventArgs e)
    => new TournamentWindow { Owner = this }.Show();
```

- [ ] **Step 6: Build the whole solution**

```bash
dotnet build PiSignage.sln
```
Expected: 0 errors.

- [ ] **Step 7: Manual end-to-end (the tracer bullet payoff)**

1. Start the agent: `cd agent && ./.venv/Scripts/python.exe main.py`.
2. Run the app: `cd windows-app && dotnet run`. Click **Tournament**.
3. Add 5 players → **Generate Round 1 pairings** (one gets a BYE).
4. **Broadcast** with agent `localhost:8080`.
5. Open `http://localhost:8080/dashboard?view=pairings` in Chrome → see the pairings; `?view=standings` → standings.
6. Enter results in the grid → **Save results + recompute standings** → **Broadcast** → standings view updates within 15s.
7. **Start timer** (25) → open `?view=timer` → clock counts down. **Stop** → next poll shows idle.
8. Add a dashboard URL as a normal playlist item on a Pi and confirm it rotates alongside an image (existing signage still works).

- [ ] **Step 8: Commit**

```bash
git add windows-app/ PiSignage.sln
git commit -m "feat(wpf): tournament console with pairing, results, standings, timer broadcast"
```

---

## Self-Review

- **Spec coverage:** engine in WPF-referenced lib ✓ (Tasks 3–8); agent sink + endpoints ✓ (Task 1); dashboard `url` item + idle state ✓ (Task 2); polling not WS ✓ (Task 2); timer anchored to `endsAt` ✓ (Tasks 1, 2, 6); Pokémon OWP/OOWP + floor + byes excluded ✓ (Task 4, tested); per-Pi different views = give each Pi a different `?view=` URL ✓ (existing plumbing, exercised in Task 9 step 7); medium-event auto-paging → **deferred to Phase C** (Phase A shows all rows; noted here, not a gap for the tracer); save/load recovery ✓ (Task 7); regular signage untouched ✓ (Task 9 step 7.8).
- **Deferred to Phase B/C (by design):** multi-round Swiss + rematch avoidance, timer pause/resume/extend UI, more presets + editor, standings auto-paging, corner-clock overlay, per-Pi assignment UI. The engine model already accommodates them.
- **Placeholder scan:** none — every code step is complete.
- **Type consistency:** payload keys (`view_data`, `pairings`, `standings`, `timer.state/remaining/round/label`, pairing `p1/p2/table`, standing `rank/name/record/points/owp/oowp`) match across agent (Task 1/2), payload builder (Task 7), and page (Task 2). `MatchResult`, `RoundTimer`/`TimerRunState`, `Standing` names consistent across Tasks 3–9.

**Correctness caveat carried from the spec:** the Win% tie/bye treatment in Task 4 must be validated against the current Play! Pokémon rulebook before real events; the test encodes the assumed rule and is the place to lock the verified one.
