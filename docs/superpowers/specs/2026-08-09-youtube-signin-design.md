# One-click YouTube sign-in

## Problem

Age-restricted videos refuse to play, and YouTube sometimes blocks the IFrame
player with "sign in to confirm you're not a bot", until someone signs into
youtube.com inside the kiosk's Chromium profile. The Spotify sign-in flow
(2026-08-06) already solved the exact mechanics; YouTube needs the same
button. A signed-in Premium account also removes ads as a side effect.

## Design (approved by user in conversation)

Reuse the Spotify sign-in machinery wholesale — one shared session, one
browser, one kiosk swap — parameterized by site.

## Agent (`agent/main.py`)

- `SIGNIN_URL` becomes `SIGNIN_URLS = {"spotify": ..., "youtube":
  "https://www.youtube.com/"}`; `_spawn_signin_browser(url)` takes the URL.
- Shared handler `_signin_request(site, running)` holds the previous endpoint
  body; `/api/spotify/signin` and `/api/youtube/signin` (GET + signed POST)
  are thin wrappers over it. Old apps keep working — the Spotify endpoint is
  unchanged on the wire.
- `_signin_site` tracks which site the running session is for. Starting a
  sign-in for a **different** site while one runs tears the old session down
  and starts fresh at the new URL (the kiosk bounces briefly — rare path,
  kept simple) instead of reporting "already running" with the browser on the
  wrong page. Same-site start stays idempotent.
- Stop through **either** endpoint stops the shared session — there is only
  one.
- Everything else is inherited: idle backstop (15 min), browser-exit watcher,
  spawn-failure heal, lifespan shutdown stop. `AGENT_VERSION` bumped.

Known edge (accepted): Google can show "This browser or app may not be
secure" on unusual browser setups. A real windowed Chromium normally passes;
no mitigation on our side if it doesn't.

## Windows app

- The 60-line start/confirm/launch/cleanup flow moved from
  `SpotifyWindow.Signin_Click` into shared `TvSigninFlow.RunAsync(owner,
  toaster, api, ctx, dev, signinApiPath, siteName, done)`; both windows call
  it. (`RemoteTrust` was already shared.)
- `ApiClient.StartSpotifySigninAsync`/`Stop...` generalized to
  `StartSigninAsync(apiPath, ctx)` / `StopSigninAsync(apiPath, ctx)`.
- `BookmarksWindow` gains the hint line + **Sign in on TV…** button, enabled
  when connected, no version gate — an old agent 404s and the flow toasts
  "Update Pi software", mirroring Spotify.

## Testing

- `test_youtube_signin.py`: status default, signature required, start stops
  kiosk + opens youtube.com, same-site idempotent, **cross-site switch**
  (Spotify → YouTube lands on YouTube, old browser killed), stop restarts
  kiosk, stop via the other site's endpoint stops the shared session.
- `test_spotify_signin.py` updated only for the seam's new `url` parameter
  and the `_signin_site` reset.
- App side: no UI harness in the repo; `dotnet build` + signage-core tests
  stay green.
