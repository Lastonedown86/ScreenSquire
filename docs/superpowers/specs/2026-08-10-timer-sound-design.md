# Time-up sound on the TV

Date: 2026-08-10
Status: approved

## Problem

When the tournament round timer hits zero, the TV flips the overlay red but
makes no sound. The TO wants an audible cue in the venue, with a few built-in
sounds and the option to upload their own.

## Design

The TV plays the sound; the app chooses it.

- **Selection**: a dropdown beside the round-timer controls in Tournament
  Signage — No sound / Buzzer / Bell / Gong / Chime / any uploaded audio
  file / Upload sound…. Persisted in app settings. The choice travels in
  `dashboard.timer.sound`: a built-in slug (`"gong"`) or a media path
  (`"/media/name.mp3"`) for uploads.
- **Playback**: the kiosk page already tracks the countdown locally
  (`endsAt`); when a running timer crosses zero it plays the sound once.
  One-shot latch per round (same pattern as the app's `_timeUpFired`), so a
  page reload after zero does not replay. Chromium's existing
  `--autoplay-policy=no-user-gesture-required` flag permits playback.
- **Built-in assets**: four synthesized, license-free WAVs (~1–3 s) at
  `agent/static/sounds/*.wav`, served by a new read-only `/static` mount
  (public, same posture as `/media`). `agent/static/**` is already embedded
  in the app bundle, so sounds ship over the existing signed update channel.
- **Custom sounds**: uploaded through the existing media pipeline. Audio
  extensions (`.mp3`, `.wav`, `.ogg`) join the upload allowlist, typed
  `"audio"`. Upload sound… pushes the file to every ticked TV (same
  MultiPush path as board captures). Audio files appear in the main media
  list (🔊 glyph, renamable, deletable) but **Add to playlist** and
  **Show now** skip them with a toast — sounds are timer-only; audio stays
  out of the playlist engine.

## Agent changes

`TimerState.sound: Optional[str]`, audio upload extensions, `/static`
mount. AGENT_VERSION bump.

## Edges

- A custom sound exists only on TVs ticked at upload time; a TV added later
  stays silent for that sound. Accepted.
- Missing/unloadable sound file: silent, never an error on the TV.
- Prepare for delivery already wipes `/media` (custom sounds) and preserves
  built-ins under `static/`.

## Testing

Agent: TDD (timer.sound roundtrip through /api/dashboard, audio upload
accepted and typed, /static/sounds served). signage-core: DashboardPayload
carries the sound (testable .NET). Kiosk JS and WPF UI have no harness —
approved exception; verified on hardware by running the timer to zero and
hearing the sound on the TV, plus the checklist's timer and upload items on
the release build.
