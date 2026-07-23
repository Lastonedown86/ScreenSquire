# Screenshot-Push Tournament Signage — Design

**Date:** 2026-07-22
**Status:** Approved design, pre-plan
**Supersedes as PRIMARY:** the tournament-engine design (`2026-07-22-tournament-signage-design.md`), which is now the **backup** path (casual nights, games with no usable official page). This spec is the primary tournament-signage path.

## Context

The Otaku Hangout must run every sanctioned event on the **official TCG-company
website** (RK9 for Pokémon, Bandai TCG+, etc.) — the companies track games and
player counts there and use that data to send stores promos and support. So the
shop cannot use a homemade engine as the system of record.

The signage job is therefore to **mirror the official pairings/standings onto
the shop TVs**. Iframing the official page is fragile (framing blocks, login
walls, tiny non-TV text) and scraping is brittle (breaks on HTML changes, ToS
gray area). A **screenshot** sidesteps all of it: it is just an image, so
framing/login/HTML-structure are irrelevant, it works identically for every
game, and the worker is already logged in and running the event on the official
site — so the companies still get their data.

Outcome: a worker captures the official pairings/standings from their own
browser with one action in the existing WPF control app, and it appears on the
selected shop TVs, rotating alongside a **live round timer** and regular
signage.

## Non-goals

- No scraping, no iframing of official sites in the primary path.
- No window-capture (screen-region capture only).
- No auto-recapture loop (that is a later "effort level 3" upgrade).
- The homemade tournament engine is out of scope here (separate backup spec).
- LAN only, no API auth (matches existing system).

## Approach chosen (from brainstorming)

- **Effort level #2:** one-click region capture in the WPF app → push to
  selected Pis. (Rejected: #1 manual phone upload — too much per-round friction;
  #3 auto-recapture — needs an always-on logged-in browser, deferred.)
- **Live timer view** included: a screenshot is frozen, so the round clock is a
  separate agent-served view that ticks locally.

## Architecture

```
Worker's browser (official pairings, logged in)
        │  region capture (Graphics.CopyFromScreen)
        ▼
WPF control app ──upload PNG (POST /api/media)──►  Pi agent (per selected TV)
        │                                          stores image in media dir
        └──POST /api/dashboard {boards, timer}──►  stores payload + caches to disk
                                                   serves /dashboard page
                                                          │
                     TV rotation (existing url items):    ▼
   /dashboard?view=board&name=pairings   →  latest screenshot (swaps on new push)
   /dashboard?view=timer                 →  live countdown (ticks locally)
   promo.jpg / hype.mp4 / ...            →  existing signage, unchanged
```

The screenshot **is an image** and the display views are **`url` playlist
items**, so the entire rotation, per-Pi control, and `show-now` override reuse
existing pi-signage plumbing. New code is small: image upload reuses
`POST /api/media`; the agent gains the dashboard endpoints + a page with a
`board` view and a `timer` view; the WPF app gains region capture + push + timer
controls.

## Components

### WPF app (new)

- **Region capture:** a transparent full-screen overlay window; the worker drags
  a rectangle over the official pairings table; capture that rect via
  `System.Drawing.Graphics.CopyFromScreen` into a PNG. **Remember the last
  region** so "Re-capture" repeats it in one click each round. Also allow
  full-screen / specific-monitor capture.
- **Target selection:** which discovered Pis receive the push (reuses existing
  mDNS discovery + Pi list in the app). One capture can go to several TVs.
- **Push:** for each selected Pi — `POST /api/media` the PNG with a **unique
  filename per capture** (e.g. `pairings-<counter>.png`) → then `POST
  /api/dashboard` with `view_data.boards[name] = "/media/<file>"`. Unique names
  are automatic cache-busting (no header tricks; the TV always shows the newest
  shot).
- **Slots:** at least `pairings` and `standings` named boards; the worker picks
  which slot a capture fills.
- **Timer controls:** start (minutes, default from a per-game value), pause,
  resume, extend, stop → pushed in the `/api/dashboard` payload `timer` field.

### Agent (new — mirrors the engine spec's agent work)

- `POST /api/dashboard` — store `{view_data:{boards:{name:url,...}}, timer:{...}}`;
  for a running timer, stamp `endsAt = agent_now + remaining` (epoch ms) so the
  countdown is anchored to the Pi's own clock. Cache to disk (atomic write).
- `GET /api/dashboard` — return the stored payload (also reloads cache on boot).
- `GET /dashboard` — serve `dashboard.html`.
- Image upload reuses existing `POST /api/media` (already accepts PNG).

### Dashboard page (`agent/static/dashboard.html`)

Vanilla JS, Pi-tuned. URL param `?view=`:
- **`board&name=pairings`** — fullscreen `<img>` (`object-fit: contain`, black
  bg) of `view_data.boards.pairings`; **swaps when that URL changes** (new
  filename each push); clean idle card ("No pairings posted") when absent.
- **`timer`** — big center clock; ticks locally every 250ms from `endsAt`; shows
  "TIME" at 0; idle when the timer is stopped.

Polls `GET /api/dashboard` every ~15s for state/URL changes; never goes blank on
a network blip (keeps last render).

### Wire payload (WPF → agent)

```json
{
  "view_data": {
    "boards": { "pairings": "/media/pairings-7.png",
                "standings": "/media/standings-3.png" }
  },
  "timer": { "state": "running", "endsAt": null, "remaining": 1500,
             "round": 3, "label": "Round 3" }
}
```
`endsAt` is null on the wire; the agent stamps it when `state == running`.

## Worker flow (per round)

1. Official pairings on screen (their browser, logged in on the official site).
2. WPF → **Capture pairings** → drag over the table (or **Re-capture** = last
   region), pushes to the selected TVs.
3. TVs show the shot on the next rotation. Between rounds, capture standings the
   same way into the `standings` slot.
4. **Start timer** when the round begins → TVs show the live clock in rotation.

## Reuse summary

- Image upload → existing `POST /api/media`.
- Display + rotation + override → existing `url` playlist items + kiosk.
- Pi discovery + per-Pi control → existing WPF app.
- New → agent dashboard endpoints + page (board + timer views); WPF capture +
  push + timer UI.

## Resilience

- Agent caches the last payload to disk → survives Pi reboot / laptop drop; board
  images persist in the media dir.
- Timer keeps counting on the Pi if the laptop drops (`endsAt` anchored to the
  Pi clock; requires NTP, which WiFi provides).

## Testing / verification

- **Agent (pytest + TestClient):** `POST /api/dashboard` stores boards + stamps
  running-timer `endsAt`; `GET` round-trips; `GET /dashboard` serves a page
  containing the `board` and `timer` views, the poll URL, and idle text.
- **WPF (xUnit on a pure helper):** the payload builder emits the exact
  `boards`/`timer` shape; the push client `POST`s to `/api/media` then
  `/api/dashboard` against a running agent (skips if agent down). Region-grab
  math (rectangle → bitmap size) unit-tested where feasible; live
  `CopyFromScreen` verified manually.
- **End-to-end (real Chrome, not the automation tab):** capture a region on the
  dev box → push to the local agent → open `/dashboard?view=board&name=pairings`
  → see the shot; re-capture → it swaps within a rotation; `?view=timer` ticks
  and shows "TIME" at 0; idle states show when nothing is posted.
- **Regression:** existing playlist / media / show-now / kiosk `/ws` unchanged.

## Notes / constraints

- 2GB Pi 4 target → dashboard stays vanilla JS, one image at a time, no
  per-second network. A screenshot is far lighter than iframing a live SPA.
- Readability: capture just the pairings *region* (not the whole browser) so text
  is large on the TV.
- No git repo in this tree yet; the plan's Task 0 initializes it (shared with the
  engine plan).
- Old board images accumulate in the media dir; a "keep last N" cleanup is a
  minor later addition (YAGNI now).
```
