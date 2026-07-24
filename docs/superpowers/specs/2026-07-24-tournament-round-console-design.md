# Tournament Round Console — Design

**Date:** 2026-07-24
**Status:** Approved design, pre-plan
**Builds on:** `2026-07-22-screenshot-signage-design.md` (screenshot-push signage, shipped)

## Context

The screenshot-push Tournament Signage works end to end: the client captures
official pairings/standings from their browser and pushes them to one Pi, with
a round timer. Field-driven gaps remain, all in the per-round experience:

- Only **one Pi** can be targeted; the shop has ~4 TVs.
- Timer has **Start/Stop only** — no pause, resume, or extend.
- **No alert** when the round clock hits zero (app is silent; TV just shows a
  static red "TIME").
- Round turnover is manual: bump the round number, re-capture, start — three
  separate actions each round.
- Capture slots are hardcoded to `pairings`/`standings`; the client sometimes
  needs other boards (Top 8 bracket, announcements).

Operator is the non-tech client running the whole event alone — every change
must stay plain-language, one-glance, hard to misuse.

## Scope

WPF `SignageWindow` (+ signage-core payload/timer helpers) and a few lines of
CSS/JS in `agent/static/dashboard.html`. **No agent endpoint changes** — the
agent already stores arbitrary board names and handles running/paused/stopped
timers. Kiosk, playlist, capture mechanics unchanged.

## Design

### 1. Multi-TV push

- Replace the single "Choose your Pi" combo with a **checkbox row** of saved
  Pis (`[✓] Front  [✓] Back  [ ] Counter …`). Checked set persists in settings
  (`SignageTargets`, replacing single `SignageTarget`).
- Every action fans out over the checked Pis sequentially: PNG upload +
  dashboard POST, timer start/pause/resume/extend/stop, Pin to TV, Back to
  playlist.
- **Per-Pi failure isolation:** one unreachable Pi never blocks the others.
  Toast names successes and failures: "Front TV updated. Back TV unreachable —
  that TV was not updated."
- Zero TVs checked → per-round action buttons disabled; status text says
  "Tick at least one TV above."
- Each agent stamps its own `endsAt` from `remaining` (existing behavior), so
  countdowns stay accurate per device.
- Hydrate-on-open reads from the **first checked** Pi (they all receive the
  same pushes, so any one reflects shared state).

### 2. Timer: pause / resume / extend / alert

- **Pause** button; while paused it reads **Resume**. Wire shape already
  supported end to end: `state=paused` + `remaining`, no `endsAt`
  (dashboard.html already renders paused; agent passes it through).
- **+5 min** extends a running or paused round: running → recompute
  `remaining` from the app-side `endsAt` + 300 s and re-push; paused →
  `remaining += 300`.
- **Time's-up alert:**
  - App: system sound + toast when the app-side clock crosses zero.
  - TV: slow CSS pulse animation on the red "TIME" — both the big timer view
    and the corner chip on board views (~6 lines in dashboard.html).
  - No TV audio (Pis may not have speakers).

### 3. Next Round button

- One click does the whole round turnover:
  1. Round number +1.
  2. Capture the **pairings** board using its saved region. If no region is
     saved yet, open the region selector first, then continue.
  3. Push capture to all checked TVs.
  4. Start the timer at the current minutes value.
- Tooltip: "Put the new pairings on your screen first, then click."
- Toast summary: "Round 4 started — pairings sent to 2 TVs, 25:00 on the
  clock."

### 4. Custom boards (slots)

- Board dropdown gains **"+ Add board…"** (TextPrompt: "What's on this board?
  e.g. Top 8 bracket") and a **Remove board** action for non-default boards.
- Board list persists in settings; `pairings` and `standings` are permanent.
- Board names are slugged for filenames/URLs (lowercase, spaces → dashes);
  the display name is what the client typed.
- Each board keeps its own capture region (existing per-slot region dict).
- Agent and TV page already accept arbitrary board names (`?name=<slug>`,
  `boards[<slug>]`) — zero changes there.

### 5. Window layout: round console

Rework `SignageWindow` from the 1-2-3 setup list into a console whose center
is the per-round loop:

```
┌─ Tournament Signage ─────────────────────┐
│ TVs: [✓]Front [✓]Back [ ]Counter [ ]Side │
│ Board: [pairings ▾][+ add]  Min:[25][30] │
├──────────────────────────────────────────┤
│              ⏱  18:42                    │
│              Round 3                     │
│  ┌────────────────────────────────────┐  │
│  │  ▶  NEXT ROUND (captures + starts) │  │
│  └────────────────────────────────────┘  │
│  [Pause] [+5 min] [Stop]                 │
│                                          │
│  Capture: [Region…][Re-capture][Pin][⏏]  │
├──────────────────────────────────────────┤
│  preview of last capture                 │
└──────────────────────────────────────────┘
```

- Top strip (setup, touched rarely): TV checkboxes; board picker + add/remove;
  minutes box + one-click presets.
- Center (per-round, touched constantly): big clock + round label; **Next
  round** as the large primary button; Pause / +5 min / Stop row; capture row
  (Capture region… / Re-capture / Pin to TV / Back to playlist).
- Bottom: existing preview pane, unchanged.
- All wording follows the plain-language client rules (say "TV", name the
  next step in tooltips/toasts, buttons named exactly as referenced).

## Wire payload

Unchanged shape. Multi-TV means the same payload POSTs to N agents. Paused:
`{"timer": {"state": "paused", "remaining": 843, "round": 3, "label": "Round 3"}}`.

## Testing / verification

- **signage-core (xUnit):** payload builder emits correct running / paused /
  extended shapes; multi-target push helper isolates per-Pi failures (one
  throwing target doesn't stop the rest, failures collected by name).
- **Agent (pytest):** paused-payload round-trip (add only if not already
  covered).
- **Manual:** two local agents → fan-out + per-Pi error toast; Next Round with
  and without a saved region; TIME pulse on both TV views; pause survives a
  page reload on the TV.

## Out of scope

- Per-TV different views (all checked TVs show the same pushes; different
  views per TV remain possible via each Pi's playlist, as today).
- TV audio alerts.
- Auto-recapture loop (still the later "effort level 3" upgrade).
- Board image cleanup on the Pi (keep-last-N remains a minor later addition).
