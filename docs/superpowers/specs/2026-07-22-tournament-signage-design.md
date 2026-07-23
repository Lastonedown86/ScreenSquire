# Tournament Signage — Design

**Date:** 2026-07-22
**Status:** Approved design, pre-implementation
**Repo:** pi-signage (agent = Python/FastAPI on Pi; windows-app = WPF/.NET 8 control app)

## Context

The Otaku Hangout (Hickory, NC) runs 7+ different TCGs in weekly in-store
tournaments (Gundam, Pokémon, Shadowverse Evolve, Cardfight Vanguard, Union
Arena, Grand Archive, Riftbound, and more sold). They already have the
pi-signage system: Raspberry Pi TVs showing image/video/URL playlists, driven
by a WPF control app that discovers and controls every Pi on the LAN.

This project turns the WPF app into a **tournament backend + broadcast hub**:
run Swiss tournaments on the shop laptop, compute pairings and standings, and
push live tournament views (pairings, standings, round timer) to the signage
TVs — each Pi able to show a different view — **without removing any existing
signage capability**. Tournament content is additive: a TV can loop regular
promos/videos and live tournament views in the same rotation.

Outcome: staff run an event from the laptop; players watch live pairings,
standings, and the round clock on the shop's TVs.

## Non-goals (this project)

- No player self-service (results entered by the TO at the laptop only).
- No cloud/hosted component. LAN only, no API auth (matches existing system).
- Not a replacement for BCP/RK9 as a data source — the WPF app *is* the source
  of truth for events run on it.

## Scope facts (from brainstorming)

- **Backend scope:** full tournament engine (players, Swiss pairings, results,
  standings + tiebreakers), configurable per game.
- **Games:** many → one **configurable engine with per-game presets**, not
  hardcoded per game.
- **Event size:** medium (16–40 players) → standings must auto-page/scroll on
  the TV; pairing algorithm must stay fast but needs no exotic optimization.
- **Screens:** a few Pis, **different views per Pi** (e.g. one pairings, one
  standings, one timer) — via existing per-Pi playlist control.
- **Result entry:** TO at the laptop. Single source.
- **Tracer game:** Pokémon.

## Architecture

Engine lives in the **C# WPF app** (source of truth during an event). Each Pi
agent is a **display sink**: it stores whatever payload it is given, serves a
dashboard page, and keeps showing the last data if the laptop drops.

```
WPF app (laptop)                     Pi agent (per TV)
─────────────────                    ─────────────────
tournament model      ── POST ──►    store payload + cache to disk
Swiss pairing engine                 serve GET /dashboard (HTML)
standings + tiebreakers              page polls GET /api/dashboard (~15s)
round timer (endsAt)                 page ticks the clock locally
per-Pi view assignment ─────────►    TV shows pairings | standings | timer
save/load to disk                    survives laptop drop (cached payload)
```

**Rejected alternatives:** engine on the Pi in Python (WPF is the natural TO
console, and the user asked for the backend in the WPF app); a separate backend
service (overkill for an LGS).

### Key reuse decision

A dashboard view is just a **locally-served URL** playlist item:
`http://localhost:8080/dashboard?view=standings`. This means the *existing*
`url` item type, per-Pi playlist control, rotation, and `show-now` override all
work unchanged. Different Pis get different views by being given different
dashboard URLs. The only genuinely new agent code is storing/serving the
tournament payload and one HTML page.

Regular signage is untouched: images, videos, and other URLs work exactly as
today. A single TV can loop mixed content, e.g.
`promo.jpg 8s → dashboard?view=standings 20s → hype.mp4 15s → dashboard?view=pairings 20s`.

## Components

### WPF app (new)

- **Tournament model:** `Tournament { id, game(preset), name, players[], rounds[], currentRound, status }`; `Player { id, name, dropped }`; `Match { round, table, p1, p2|BYE, result: p1|p2|draw|pending, games?: (for Bo3 game-win tiebreakers) }`.
- **Configurable Swiss engine:**
  - `GamePreset { name, pointsWin=3, pointsDraw=1, pointsLoss=0, tiebreakers: ordered[], floorPct, bestOf }`.
  - Pairing: score-group pairing, rematch avoidance, bye assignment (lowest
    standing without a prior bye), repeat-bye avoidance.
  - Standings: match points, then tiebreakers per preset.
- **Persistence:** save tournament to disk (JSON) on every change → crash/close
  recovery. The laptop is the source of truth, so this is required, not
  optional.
- **Broadcast:** assign each discovered Pi a view; POST the payload to each
  agent. Reuses existing mDNS discovery + per-Pi addressing in the WPF app.
- **Timer controls:** start (duration from preset, editable), pause, resume,
  extend, stop.

### Agent (new — ~40 lines in `agent/main.py` + one HTML file)

- `POST /api/dashboard` — receive payload, store in memory, cache to disk
  (atomic write, same pattern as `save_playlist`). For a **running** timer,
  stamp `endsAt = agent_now + remaining` so timing is anchored to the Pi's own
  clock (no cross-device skew).
- `GET /api/dashboard` — return current payload (also used to reload cache on
  boot).
- `GET /dashboard` — serve `static/dashboard.html`.
- `static/dashboard.html` — reads `?view=`, **polls** `/api/dashboard` every
  ~15s for state changes, renders the requested view, and ticks the timer
  locally. Auto-pages standings for 16–40 rows. Pi-tuned CSS (`contain`, no
  heavy blur/shadow). Clean **idle state** ("No active tournament") when there
  is no live round, so a dashboard item left in a playlist never looks broken.

Polling (not WebSocket) for the dashboard: round data changes rarely, polling
is simpler and more resilient, and it avoids the WebSocket quirk observed in
one browser environment during setup. The existing kiosk `/ws` is unchanged.

### Dashboard payload (WPF → agent)

```json
{
  "view_data": {
    "game": "Pokémon",
    "name": "Wednesday Pokémon",
    "round": 3,
    "pairings": [ {"table": 1, "p1": "Ash", "p2": "Gary", "result": "pending"} ],
    "standings": [ {"rank": 1, "name": "Ash", "record": "3-0-0", "points": 9,
                    "owp": 0.62, "oowp": 0.55} ]
  },
  "timer": { "state": "running", "endsAt": null, "remaining": 1500,
             "round": 3, "label": "Round 3" }
}
```

`endsAt` is null on the wire; the agent computes it from `remaining` against its
own clock when `state == running`. Paused timers carry `remaining` and no
`endsAt`.

## Pokémon ruleset (tracer — correctness-critical, gets unit tests)

- **Match points:** Win 3, Tie 1, Loss 0. A **bye = win** (3 pts) for standings.
- **Rank order:** match points desc → **OWP** desc → **OOWP** desc.
- **Win% (of a player, as used in opponents' calc):**
  `max(floorPct, matchWins / matchesPlayed)` with `floorPct = 0.25`. **Byes are
  excluded** from both the opponent list and from `matchesPlayed`.
- **OWP:** mean of each opponent's Win%.
- **OOWP:** mean of each opponent's OWP.
- **Rounds by attendance:** per the official Play! Pokémon table.

> **Must-verify during implementation:** exact treatment of ties and byes in the
> Win% denominator against the *current* Play! Pokémon tournament rulebook.
> Pin it down with unit tests computing OWP/OOWP for a small hand-worked bracket
> (including a bye and a tie) and asserting known values. This is the single
> highest-risk correctness area — a wrong standing is the one thing a tournament
> cannot tolerate.

## Phasing (tracer bullet first)

- **Phase A — tracer bullet (Pokémon, end-to-end thin):** WPF adds players →
  generates round 1 → enter results → standings with OWP/OOWP → save/load.
  Agent `/api/dashboard` + `/dashboard` page (pairings, standings, basic
  countdown via `endsAt` + local tick). Push to one Pi. Proves the whole pipe on
  one screen.
- **Phase B — real engine:** multi-round Swiss (rematch avoidance, byes),
  full tiebreaker correctness + tests, more game presets, generic/editable
  preset editor, timer pause/resume/extend + per-preset defaults.
- **Phase C — shop reality:** multiple Pis / different views, standings
  auto-paging for medium events, per-Pi view-assignment UI, corner-clock overlay
  on pairings/standings, polish.

## Testing / verification

- **Unit (WPF):** tiebreaker math against a hand-worked Pokémon bracket (bye +
  tie included); pairing produces no rematches and valid byes across several
  rounds.
- **Integration:** WPF POSTs a payload → `GET /api/dashboard` returns it →
  `/dashboard?view=standings` renders it. Kill the laptop mid-event → Pi still
  serves the cached view and the timer keeps counting.
- **End-to-end (real Pi Chromium):** load `/dashboard?view=timer` fullscreen,
  confirm the clock ticks locally and updates on the next poll after a
  pause/extend; confirm idle state when no round is live.
- **Regression:** existing playlist / media / show-now / kiosk `/ws` behavior
  unchanged.

## Notes / constraints

- LAN only; no API auth (consistent with the current system).
- Pi needs NTP (WiFi provides it) for wall-clock sanity; timer skew is avoided
  by anchoring `endsAt` to the agent's own clock, so absolute clock correctness
  only matters for human-readable time, not countdown accuracy.
- Target hardware includes a 2GB Pi 4 → dashboard must stay lightweight
  (vanilla JS, update DOM in place, no framework, no per-second network).
- No git repo present in this working tree, so this spec is not committed;
  re-run the commit step if the project is later put under version control.
