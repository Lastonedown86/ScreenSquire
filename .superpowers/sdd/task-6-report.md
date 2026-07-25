# Task 6: Sign Every Windows Control Path

## RED

Added deterministic request-capture tests before the production changes.
`PushClientTests` covered the dashboard JSON entity and media source bytes plus
the required signed, escaped `?name=` destination. `AgentUpdaterTests` covered
the ZIP source bytes. `WifiProvisionerTests` held the first HTTP response open
and proved that the second request allocated its counter and entered transport
too early.

The required focused RED command produced `4 failed, 10 passed`. The failures
were the missing PushClient and AgentUpdater authentication headers, the
unsigned `/api/media` destination, and absent per-device serialization.

`ApiClientTests` were then added under `signage-core.Tests`, with an injectable
HTTP-handler seam and coverage for playlist, upload, delete, detach, rename,
show-now, clear-show-now, next, name, and kiosk mutations. Its first build was
an integration RED: the already-changed core signatures had not yet been
threaded through `WifiSetupWindow`, `MainWindow`, and `SignageWindow`, so WPF
compilation failed before the ApiClient assertions could run.

## Implementation

- Added the specified `ControlContext` record.
- Added one shared `SemaphoreSlim` per immutable DeviceId. The lock begins
  before `TakeNextCounter` and is released only after `HttpClient.SendAsync`
  receives the response.
- Routed `ApiClient`, `PushClient`, `WifiProvisioner`, and `AgentUpdater`
  mutations through that shared signed-request dispatcher.
- Serialized each JSON entity once to UTF-8 bytes, hashed those bytes, and sent
  the same byte array.
- Signed multipart media and update requests with the SHA-256 of the uploaded
  file/PNG/ZIP bytes rather than the MIME envelope.
- Added the signed `?name=<escaped filename>` media destination required by the
  agent contract.
- Signed empty-body delete, detach, clear-show-now, and next operations with the
  SHA-256 of zero bytes.
- Threaded immutable device identity and credential snapshots through
  `MainWindow`, `SignageWindow`, tournament `PushTarget`s, and USB Wi-Fi setup.
- Kept status, playlist, media, dashboard, Wi-Fi-status, and kiosk reads
  unsigned. Tests explicitly guard the ApiClient and PushClient read paths.

`WifiSetupWindow` and `MultiPush` were touched beyond the brief's primary file
list because the new required `ControlContext` signatures otherwise leave
production call sites uncompilable and tournament fan-out without a stable
per-device credential.

## Focused GREEN

The combined focused command covering `ApiClientTests`, `PushClientTests`,
`WifiProvisionerTests`, `AgentUpdaterTests`, and `MultiPushTests` produced
`19 passed, 0 failed` in 691 ms. The core library, WPF app, and test project all
compiled.

## Self-review

- No unsigned mutation overload or convenience `PostAsJsonAsync`,
  `PutAsJsonAsync`, `PostAsync`, or `DeleteAsync` path remains in the four
  clients.
- The shared lock is keyed by DeviceId, so different client instances and
  mutation families serialize against one another while different Pis remain
  independent.
- Counter allocation occurs inside the lock and immediately before signing.
  Cancellation, signing errors, transport errors, and non-success responses
  all release the semaphore.
- Absolute request URIs are finalized before signing, including escaped query
  destinations and escaped media path segments.
- Multipart tests compare the authentication entity hash with the source bytes
  and separately confirm those same source bytes are present in the multipart
  part.
- `git diff --check` passed; Git emitted only the repository's existing
  LF-to-CRLF working-copy notices.

## Full verification

Root-agent execution completed successfully:

- `dotnet test PiSignage.slnx`: `101 passed, 0 failed` in 1 second.
- `dotnet build PiSignage.slnx`: `0 warnings, 0 errors` in 2.68 seconds.
- `agent\.venv\Scripts\python.exe -m pytest agent -q`: `91 passed`,
  `1 accepted warning` in 12.95 seconds.
- The runtime `agent/data/dashboard.json` hash and timestamp were unchanged:
  `59C0FCED820275B9C25E3F99938588745EB24B6D0BCE1AB9496B83247D842E32|639205422347712357`.

## Review fixes

Review identified that tournament targets without credentials were filtered out
of `Targets`, so a remembered multi-TV selection could silently become a
partial push. It also requested direct proof that the static lock coordinates
different client instances and mutation families, and found that AgentUpdater
normalized only its upload URL, producing `//api/status` while polling a base
URL with a trailing slash.

The review RED run produced `3 failed, 7 passed`:

- three AgentUpdater polls used `GET //api/status`;
- both TvChoice tests failed because the explicit requested/pairing/control
  state did not exist;
- both new concurrency tests already passed, confirming that a PushClient
  dashboard mutation blocks a WifiProvisioner mutation for the same DeviceId
  across separate HttpClient instances, while different DeviceIds proceed
  concurrently.

The fixes:

- keep every saved TV visible in the tournament selector;
- force credential-missing targets unchecked and disabled with a visible
  `Pair this Pi` label and tooltip;
- preserve a remembered unavailable DeviceId so it cannot be silently dropped,
  show an explicit warning, and restore its checked state after pairing;
- gate capture, timer, preset, pin, and playlist-return actions on at least one
  checked controllable target;
- refresh every open SignageWindow after AddPi/re-pair returns;
- normalize AgentUpdater's base URL once for both signed upload and unsigned
  status polling, with exact URI and unsigned-read assertions.

Focused review GREEN verification produced `10 passed, 0 failed` in 317 ms,
including WPF compilation.

Final full review-fix verification:

- `dotnet test PiSignage.slnx`: `105 passed, 0 failed` in 1 second.
- `dotnet build PiSignage.slnx`: `0 warnings, 0 errors` in 6.70 seconds.
- `agent\.venv\Scripts\python.exe -m pytest agent -q`: `91 passed`,
  `1 accepted warning` in 14.01 seconds.
- Runtime dashboard metadata was identical before and after:
  `59C0FCED820275B9C25E3F99938588745EB24B6D0BCE1AB9496B83247D842E32|639205422347712357`.
