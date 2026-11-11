# LessonCue change log

This is the release history for LessonCue. Each release publishes both user and
developer notes on GitHub; the app shows only the user changes before an
administrator installs an update.

## v0.46.44 — Troubleshooting report accuracy

### User changes

- Failure reports now use the same seven-day retention window for audit events
  as they do for runtime failures, so resolved historical incidents do not
  drown out current diagnostics.
- Successful report-delivery events are no longer reported as failures merely
  because their summary says “Failures-only.”
- An expired Google Drive authorization callback is recorded as a reconnect
  prompt instead of a server outage. Existing offline media and YouTube
  streaming safeguards remain unchanged.

### Developer changes

- Expired OAuth callback handling no longer attaches an exception to the
  warning log, preventing expected user-action outcomes from entering the
  failure store.
- Added regression coverage for audit retention and successful-audit false
  positives.
- Release server/web version 0.46.44. This is a server-only release; Android
  TV/Fire TV remains at version 0.46.17 (version code 157).

## v0.46.43 — Release security hardening

### User changes

- The server image now refreshes its APT security packages during image creation, preventing a newly published base-image advisory from blocking release validation.

### Developer changes

- Release server/web version 0.46.43 and Android TV/Fire TV version 0.46.17 (version code 157).

## v0.46.42 — Media recovery and presentation conversion hardening

### User changes

- YouTube imports now keep a valid source playable through streaming when an
  offline download cannot be completed, instead of leaving an unusable failed
  media item.
- Large presentation conversions retry LibreOffice with a fresh profile and
  the explicit Impress PDF exporter, improving recovery from transient office
  process failures.
- Google Drive reconnects now reuse the stored offline grant when it is still
  valid, instead of needlessly creating a new consent grant each time.

### Developer changes

- Existing failed YouTube imports with valid source links are repaired on server
  startup, and the importer includes a bounded no-JavaScript fallback for Deno
  memory failures.
- Conversion diagnostics retain the LibreOffice output from both attempts;
  image assets remain excluded from adaptive video-transcode queues.
- Release server/web version 0.46.42 and Android TV/Fire TV version 0.46.16 (version code 156).

## v0.46.41 — Faster Android TV lesson previews

### User changes

- Android TV now puts the newest lessons first, highlights the next lesson in a larger focused preview, and shows two additional lesson options before loading more on demand.
- Lesson previews load faster by limiting initial media work to the visible cards.
- Video cues now use their generated media thumbnails instead of the generic video placeholder.

### Developer changes

- Lesson manifests publish versioned thumbnail URLs, with an Android fallback for older manifests that only contain a media ID.
- Added protocol and server regression coverage for thumbnail publication and deterministic lesson ordering.
- Release server/web version 0.46.41 and Android TV/Fire TV version 0.46.15 (version code 155).

## v0.46.40 — Release security refresh

- Refresh the Vega TV dependency lockfile to patched high-severity and critical versions so the release security gate passes.
- Release server/web version 0.46.40 and Android TV/Fire TV version 0.46.14 (version code 154).

## v0.46.39 — Release validation fix

- Keep the seeded demo media taxonomy values approved during the browser release workflow so the full validation suite passes with the bundled review sequence.

## v0.46.38 — Fire TV review navigation and media recovery fixes

### User changes

- The Fire TV local review path now opens a navigable sample content library,
  lesson detail screen, and bundled looping video. Review controls receive
  explicit initial focus and remain reachable with the D-pad.
- Interrupted TV media downloads are revalidated by size and checksum before
  playback, and recoverable stale transcode records are retried after the
  media worker is repaired.

### Developer changes

- YouTube imports now fall back to safer download profiles after bounded Deno
  memory failures instead of treating an out-of-memory extractor exit as a
  permanent media failure.
- Expired Google Drive authorization is surfaced as a reauthorization action
  instead of generating repeated scheduled-backup failures.

## v0.46.28 — Review-ready TV pairing and Docker-host tunnels

### User changes

- Android TV pairing now explains that the display uses a server address and
  six-digit pairing PIN rather than a personal username and password.
- Docker hosts such as Unraid can run an optional, pinned Cloudflare Tunnel
  connector alongside the LessonCue server.

### Developer changes

- Added copy-ready Amazon Appstore testing instructions for the server-pairing
  flow and documented the public HTTPS review-server requirement.
- Raised the Android TV store build to version 0.46.8 after the 0.46.7 review
  rejection caused by undocumented test access.

## v0.46.27 — Google Drive backups and organized activities

### User changes

- Off-site backups can now connect to Google Drive through OAuth using the
  limited `drive.file` permission. LessonCue creates and manages its own named
  folder, uploads encrypted archives with resumable transfers, and can mirror
  media without accessing unrelated Drive files.
- Settings show the exact Google OAuth callback URL, connection state, and
  setup guidance while keeping client secrets and refresh tokens protected.
- Activities Studio now groups games into quizzes, puzzles, game shows,
  creative activities, audience activities, movement, media, and classroom
  tools, with category filters and optional grouped library sections.
- Activity creation now separates ready-made formats from blank builders and
  includes search, participation filters, practical descriptions, and a detail
  panel before creation.
- The activity editor now guides teachers through content and rules, timing
  and flow, and TV presentation in that order.

### Developer changes

- Added a scoped Google Drive client with OAuth state and replay protection,
  refresh-token handling, resumable upload recovery, managed-file metadata,
  retention, media manifests, and regression coverage.
- Added one shared teacher-facing activity taxonomy used by the library and
  chooser, plus unit and browser coverage for category mapping, descriptions,
  creation, filtering, arranging, and editing.
- This is a server/web release only; no Android/Google TV, Vega TV, or Apple
  TV artifact is produced.

## v0.46.26 — Resilient backups and high-quality media imports

### User changes

- Large ownCloud media files now use the server's advertised chunked-upload
  support, with bounded retries for transient upload failures.
- Backup status and safe destination details are included in troubleshooting
  reports. A successful configuration-backup upload remains visible when a
  later media-sync step fails.
- YouTube imports now prefer the highest available source quality up to 4K,
  while keeping the original encoded streams intact; only separate TV playback
  copies may be transcoded for compatibility.
- Android/Google TV playback now starts with the highest codec-compatible
  available version and automatically falls back to lower versions after a
  playback error or sustained buffering, resuming at the same position.

### Developer changes

- WebDAV media-upload errors now include the relative media path, byte counts,
  chunk progress, elapsed time, response status, and proxy trace identifiers
  when provided. Interrupted ownCloud chunk uploads are cleaned up.
- Troubleshooting snapshots include schedule, last-run/verification state,
  per-destination media-sync counts, and errors without including credentials
  or full WebDAV URLs.
- YouTube format selection now considers non-MP4 high-resolution codecs,
  prefers formats up to 2160p, and uses a lossless MKV stream-copy merge when
  separate video and audio streams must be combined. Android and progressive
  fallback profiles remain available when the preferred download path fails.
- TV manifests now include a versioned, codec-filtered playback ladder. The
  Media3 player records quality fallback diagnostics and retries after 15
  seconds of continuous buffering without changing the stored original.
- This release includes Android/Google TV artifacts because playback behavior
  changed; no Apple TV/tvOS artifact is produced.

## v0.46.25 — Reliable protected updates and existing WebDAV folders

### User changes

- Server updates now verify that the installed LessonCue binary reports the
  requested release version before marking the update successful.
- Scheduled ownCloud/WebDAV backups now recognize an existing destination
  folder even when the provider returns `400` to an idempotent folder-create
  request. Invalid paths and credentials still report the create and verify
  statuses together.

### Developer changes

- Added a staged-binary version check to the protected Linux updater so a
  release built with stale assembly metadata cannot be reported as installed.
- Added WebDAV collection verification and regression coverage for providers
  that reject `MKCOL` on an existing collection with `400`.
- This is a server/web release only; no Android/Google TV or Vega TV artifact
  is produced.

## v0.46.24 — Independent YouTube downloader updates

### User changes

- LessonCue now checks and maintains its YouTube downloader independently from
  full LessonCue releases.
- Settings and the Dashboard show when a verified yt-dlp update is available,
  with manual check and update controls. Routine downloader maintenance does
  not use the normal LessonCue update announcement.
- A stronger warning appears when YouTube imports are failing and an update is
  available.

### Developer changes

- Added a root-owned Linux updater with daily checks, official GitHub asset and
  SHA-256 verification, atomic replacement, rollback, and preservation of a
  newer independently managed binary during full LessonCue upgrades.
- Added runtime state to support and troubleshooting diagnostics, plus
  regression coverage for silent notices and transactional updater behavior.
- This is a server/web release only; no Android/Google TV or Vega TV artifact
  is produced.

## v0.46.22 — Provider-safe troubleshooting reports

### User changes

- Daily troubleshooting reports still contain the complete JSON diagnostic
  payload, but are delivered with a `.txt` filename accepted by providers that
  reject `.gz` and `.json` attachments.

### Developer changes

- Added regression coverage for the report filename and kept the server-only
  release scope; no Android/Google TV or Vega TV artifact is produced.

## v0.46.23 — Resilient YouTube download fallback

### User changes

- YouTube imports still try the fast Android-compatible MP4 path first.
- If YouTube rejects that path with a 403 or exposes only adaptive streams,
  LessonCue makes one bounded fallback attempt and merges validated MP4/audio
  streams locally.

### Developer changes

- Added regression coverage for the fallback format and MP4 merge arguments.
- Fallback failures remain visible with the selected profile and original
  downloader error; checksum and media validation are unchanged.
- This is a server/web release only; no Android/Google TV or Vega TV artifact
  is produced.

## v0.46.21 — YouTube import and service reliability

### User changes

- Updated the bundled YouTube downloader to yt-dlp 2026.08.19 and retained the
  bundled Deno runtime plus the Android-player compatibility path for current
  YouTube downloads.
- Fixed daily troubleshooting report delivery through providers that reject
  compressed `.gz` attachments.
- Fixed account setup, verification, and password-reset links on SQLite-backed
  installations.

### Developer changes

- Verified the yt-dlp Linux and Windows artifacts against the official release
  SHA-256 values.
- Moved account-token expiry validation out of the SQLite query and added a
  regression test for live and expired tokens.
- This is a server/web release only; Android/Google TV and Vega TV artifacts
  remain at their previous release because their sources did not change.

## v0.46.20 — scheduled media synchronization

### User changes

- Scheduled backups can run at a selected local hour, including 2 AM for
  off-hours operation.
- Off-site WebDAV destinations can keep media synchronized separately from
  encrypted database/configuration backups.
- A named folder can be created below each WebDAV root for the archive and
  media mirror.

### Developer changes

- Added hash-based WebDAV media synchronization with a managed manifest,
  safe stale-file cleanup, and sync diagnostics.
- Added regression coverage for scheduled timing, folder creation, media
  add/update/delete behavior, and preservation of unrelated remote files.
- This is a server/web release only; Android/Google TV and Vega TV artifacts
  remain at their previous release because their sources did not change.

## v0.46.19 — universal remote initialization

### User changes

- The universal remote now selects its initial classroom reliably while the
  classroom and screen library is loading.

### Developer changes

- Added regression coverage for asynchronous public controller bootstrap data.
- This is a server/web release only; Android/Google TV and Vega TV artifacts
  remain at their previous release because their sources did not change.

## v0.46.18 — universal remote and import reliability

### User changes

- The universal remote now begins with a classroom selector and adopts the
  selected classroom's theme color.
- LessonCue can retry a failed YouTube download without re-uploading or
  changing the existing media identity.
- YouTube downloads avoid a current default-player HTTP 403 path and retry
  transient transfer failures.
- Daily troubleshooting and AI review emails now attach provider-compatible
  plain JSON reports instead of rejected gzip attachments.

### Developer changes

- Added Android-player yt-dlp selection and bounded download retries for local
  YouTube imports, with regression coverage for the observed SABR/403 failure.
- Added a safe requeue path for failed YouTube imports and a matching Media
  Library action.
- Kept compressed troubleshooting artifacts server-side while sending
  uncompressed JSON through Resend or Brevo.
- This is a server/web release only; Android/Google TV and Vega TV artifacts
  remain at their previous release because their sources did not change.

## v0.46.17 — live troubleshooting reports

### User changes

- Service Admins can schedule a redacted troubleshooting review daily, weekly,
  monthly, or on a custom hour/day/week/month interval.
- Codex creates a subscription-ready report and prompt package without putting
  a ChatGPT credential on the server. DeepSeek can complete the review on the
  server when `LESSONCUE_DEEPSEEK_API_KEY` is configured.
- Review artifacts can be downloaded from Service Admin settings or delivered
  through the existing troubleshooting-report recipient.
- A protected live report endpoint at /report.log can be pulled by Codex at
  any time, independently of the email schedule, with ETag change detection.
- Added a repository pull helper that persists the ETag and only hands Codex a
  changed report for evaluation.

### Developer changes

- Added a provider-neutral scheduled review worker, persisted schedule/status,
  redacted artifact storage, DeepSeek adapter, Codex prompt package, and
  migration-safe database fields.
- Added time-zone-aware schedule coverage for daily, weekly, monthly, custom,
  and invalid-setting cases.
- Added stable issue codes/descriptions and detailed media/TV evidence to the
  live and scheduled reports.
- This is a server/web-only change; Android/Google TV and Vega TV artifacts do
  not need to be rebuilt.

## v0.46.16 — reliable YouTube local imports

### User changes

- Download YouTube locally now includes the JavaScript runtime required by
  current YouTube extraction, so imports no longer depend on a separate Node or
  Deno installation.
- A missing or misconfigured runtime now produces a direct repair message in
  the Media Library instead of an opaque 403 download failure.

### Developer changes

- Server packages and the container bundle Deno 2.9.7 for Linux x64/arm64 and
  Windows x64, verify its SHA-256, and pass its explicit path to yt-dlp.
- Troubleshooting diagnostics report the yt-dlp and Deno paths and whether each
  executable is available.
- This is a server/web release only; Android/Google TV and Vega TV artifacts
  remain at their previous release because their sources did not change.

## v0.46.15 — reliable daily troubleshooting delivery

### User changes

- Sending a daily troubleshooting report now queues the work immediately
  instead of waiting for the full diagnostic bundle inside the browser request.
- Delivery failures remain visible in service settings with the email
  provider's response detail and attachment size when available.

### Developer changes

- Manual troubleshooting delivery runs in the background and records a
  durable failure/audit entry if report generation or email delivery fails.
- Shortener diagnostics are bounded and reserved-code checks use bounded
  parallelism so an unavailable optional integration cannot block a report.
- This is a server/web release only; Android/Google TV and Vega TV artifacts
  remain at their previous release because their sources did not change.

## v0.46.14 — Shortener diagnostics correction

### User changes

- Reserved game-code status now distinguishes missing codes, confirmed links
  owned by another account, and shortener/API failures.
- The administrator is no longer told to delete links when LessonCue only
  received an HTTP 500, authentication failure, or other unverifiable result.
- Shortener checks and support exports include the response detail and request
  ID needed to diagnose public redirect failures.

### Developer changes

- Reconciliation audit entries now include all result counts and are marked
  degraded when repair is incomplete.
- Daily troubleshooting and support bundles include categorized shortener pool
  state and public probe results.
- This is a server/web release only; Android/Google TV and Vega TV artifacts
  remain at their previous release because their sources did not change.

## v0.46.13 — Phone controller layout hotfix

### User changes

- The master controller now fills the full phone viewport instead of exposing
  a white area beneath a short control surface.
- Selecting a lesson cue now opens that cue's game or media controls at the top
  of the scrollable area. **All lesson cues** returns to the lesson and cue list.

### Developer changes

- Made the public controller own its dynamic viewport height instead of relying
  on the authenticated app shell that does not wrap public controller routes.
- Added phone-sized browser regression coverage for full-height rendering,
  selected-cue expansion, and returning to the cue list.
- Updated the full local workflow test to enforce the expanded selected-cue
  interaction instead of the superseded always-visible cue list.
- Made the layout regression independent of lesson ordering when the complete
  browser suite has multiple paired screens.
- Replaced a timestamp-sensitive drawing-privacy assertion with a structural
  state check so release validation tests the intended privacy boundary.
- This is a server/web release only; Android/Google TV and Vega TV artifacts
  remain at their previous release because their sources did not change.

## v0.46.9 — Lesson games carry identity and score forward

### User changes

- The lesson join code appears in the opening lobby only; later activities no
  longer repeat the invite code or QR panel.
- Player and team scores carry across games in the same lesson, with team
  totals derived from the shared score ledger.
- Quiz speed bonuses now award a fixed, configurable bonus to the first correct
  answer by default, with an explicit opt-out.
- Activity creation choices are grouped into categories such as Quizzes,
  Puzzles & word games, and Game show games.
- Phones and tablets retain their player identity through refreshes and name
  changes. The existing player handoff action still creates a fresh identity.
- Player and team names are checked against a broad normalized offensive-name
  filter, including punctuation and common leetspeak variations.

### Developer changes

- Added lesson-opening join-state projections, persistent device-token reuse,
  score-ledger team projections, and migration coverage for existing session
  groups.
- Added server and browser regression coverage for cumulative scoring, fastest
  correct answers, opening-lobby invite visibility, player identity, reset
  lobbies, grouped activity choices, and name filtering.
- Removed redundant host-state polling during connection fallback so a stalled
  refresh cannot accumulate duplicate requests.
- This is a server/web release only; Android/Google TV and Vega TV artifacts
  remain at their previous release because their sources did not change.

## v0.46.7 — Reliable server processing and diagnostics

### User changes

- Linux media processing no longer depends on Bubblewrap, so uploads and Intel
  Quick Sync are not blocked by host namespace restrictions. The service still
  runs the bounded unprivileged worker with resource limits and local-input
  validation.
- System diagnostics now returns the usable parts of a support bundle when one
  check fails, identifies the unavailable component, and remains downloadable
  instead of becoming a generic 502 error.
- Service Admins can opt in to a daily failures-only troubleshooting report at
  a chosen address and local time, with a **Send now** check.
- The phone remote now uses a recognizable open/closed lock icon, while its
  existing lock behavior and pinned top controls remain unchanged.

### Developer changes

- Added direct-worker regression coverage, partial support-bundle diagnostics,
  persisted daily email failure state, and a release-scope guard that prevents
  server-only changes from publishing TV artifacts.
- This is a server/web release only; Android/Google TV and Vega TV artifacts
  remain at their previous release because their sources did not change.

## v0.46.6 — Media recovery and dual-stack discovery

### User changes

- New JPG and MP4 uploads remain playable from the intact original while optional TV compatibility processing runs or needs a runtime retry.
- Media Library troubleshooting now shows processing state, original/derived file checks, worker dependencies, and a safe **Retry processing** action.
- Android TV verifies discovered LessonCue endpoints and automatically falls back from an unusable IPv6 address to a working IPv4 address, including `.local` link-local scope handling.

### Developer changes

- Added end-to-end media manifest, HTTP Range, ETag, checksum, and troubleshooting regression coverage.
- Added dual-stack endpoint candidate diagnostics and persisted the endpoint that actually verified.
- Default native and recovery-mode server bindings now match Avahi's dual-stack advertisement.
- Reduced routine HttpClient troubleshooting noise and canonicalized Shlink lookups.

## v0.46.5 — Immediate media playback

### User changes

- Uploaded lessons become playable as soon as the original file is inspected and its thumbnail is created; the broader TV compatibility copy is made immediately afterward without blocking playback.
- The lesson builder shows each cue's preview while it is being dragged, and cues can be reordered by mouse, touch, or trackpad drag and drop.
- The phone remote keeps stop, play/pause, previous, and next controls pinned above its scrollable workflow and no longer opens zoomed on iPhone.

### Developer changes

- Separated source readiness from optional compatibility conversion and kept original-file manifest delivery available while conversion is pending or fails.
- Added manifest and display-capability regression coverage for immediate source playback.

## v0.46.4 — Classroom reliability

### User changes

- TV remote commands acknowledge promptly, including while the TV is idle.
- Stop commands work from the local cached library when the server connection is temporarily unavailable.
- Diagnostic screenshot capture no longer blocks remote controls or retains canceled image buffers.
- Phone joining, activity progression, drawing games, host controls, and shared-player recovery received additional reliability fixes and regression coverage.

### Developer changes

- Added bounded classroom reliability validation across server, web, Android TV, joining, games, drawing, remote control, updates, and backups.
- Hardened Android TV request cancellation, manifest caching, media downloads, local discovery, update checks, and command navigation.

## v0.46.2 — Security maintenance

### User changes

- Updated the Vega TV package dependencies to include the latest compatible
  security fixes.

### Developer changes

- Updated release metadata consistently across the server, browser player,
  Android TV, and Vega TV packages.

## v0.46.3 — Release validation reliability

### User changes

- No functional changes. This patch release improves the reliability of the
  release validation around activity host controls.

### Developer changes

- Allow the activity host browser check enough time for a busy CI runner to
  receive its command response.

## v0.46.1 — More reliable games, displays, and local discovery

### User changes

- Games and phone remotes recover more reliably from lost replies and brief network interruptions. Joining again after a lost response does not create a duplicate player; drawing games retain work after a failed send.
- Automatic game progression, shared-lobby scoring, and phone-host controls work consistently across more activity types.
- Server connection settings now include an optional **Allow IPv6 for the local .local address** switch. It stays on by default. Turning it off suppresses Avahi's IPv6 discovery on the server without disabling Linux IPv6 or the Cloudflare Tunnel.
- Browser and Android TV displays avoid several resource-lifecycle problems that could cause duplicate status traffic, frozen signage video, or stale signage WebViews.

### Developer changes

- Reworked activity join, push subscription, refresh, autopilot, drawing, and remote-control paths with server and browser regression coverage.
- Added a guarded Avahi configuration update with rollback on restart failure and a test for both IPv6 modes.
- Fixed browser display heartbeat and control-poll cleanup races, and Android signage player/WebView rebinding and cleanup.
- Added an initial Amazon Vega client and CI type/address-policy checks. Vega packaging and device acceptance remain separate work; this release does not distribute a Vega package.

## v0.46.0 — Games that run themselves, and a remote that flows

### User changes

- The remote is one downward flow instead of three tabs: choose the lesson,
  choose the cue, control that cue. The cue list stays on screen throughout, and
  a step whose turn has not come is dimmed rather than hidden, so the shape of
  the job is visible at a glance.
- The short domain is now a choice for the game join address rather than
  something that quietly took over once the shortener was running. Games whose
  code the shortener does not hold still get an ordinary address, so a wall
  never carries a dead link.
- The join code stays on screen for the whole game. Only five of the sixteen
  stages showed it during play, so for most games a latecomer had nothing to
  join with once the first question went up.
- Games where the room votes on each other's answers now put it to the vote
  without the host pressing for it.
- Music plays during a game. There are four new optional sounds — a bed under
  play, a bed while a clock runs, an announcement as it starts, and one for the
  last five seconds, so a recorded count can be dropped in.
- Round pacing is yours to set: how long to read the question, answer it, look
  at the answer, and look at the scores. Empty means the shipped pace, which is
  shown in the box.
- The stage no longer prints the game's name twice, and a custom title is
  honoured everywhere, including Mystery Image where it was ignored outright.
- Image Reveal games can be created again. Their shipped settings were rejected
  by the server, so the six games on that engine could not be made at all
  without hand-writing a configuration.
- A staged reveal keeps going when the host closes the remote or their phone
  sleeps.
- The television app answers the remote faster, launches in about a third of the
  time, and no longer looks frozen when the server is off.

### Developer changes

- Reserved codes are covered end to end: a game takes a four-character code,
  gives it back when the lobby is done, keeps it across a lesson, and never
  hands one out when the shortener is not holding them. The session service now
  takes a one-property interface, because the whole path was untested while it
  depended on the concrete service.
- Every engine is played on all three surfaces by `zz-activity-sweep`. That
  found Image Reveal's default style was one validation rejects, and that
  reading a JSON null through TryGetInt32 throws — a 500 with no message, at
  thirty call sites, two of which had already been patched where they landed.
- Beds and stings play on separate channels, so a sting no longer stops the
  music; asking for the bed already playing does nothing.
- The Android status loop no longer counts files and reads free space on the
  main thread every two seconds, and signage remembers its file lookups instead
  of repeating them each recomposition. R8 is on for release: 11.5 MB to 3.5 MB,
  cold launch 485 ms to about 280 ms, UI-thread stalls 20 frames to 3.
- Activity tests wait for the save to reach the server rather than for a banner
  that was already showing those words from the previous save — the race that
  failed two release builds and looked like the editor saving the wrong preset.
- The web player reported version 0.45.4, twelve releases behind, which is what
  screens displayed.

## v0.45.17 — Reliable activity sessions and public documentation

### User changes

- Activity join codes now reject legacy five-digit values and expire after two
  hours without activity, with a fresh code available when a lobby is reset.
- Hosts can lock or unlock individual players and reset the entire player lobby
  so everyone can join again with a new code.
- Room and universal controllers open without an administrator sign-in page;
  server-side controller authorization remains in force.
- The remote pause/resume control now has the same compact footprint as the
  other playback controls on desktop and mobile.

### Developer changes

- The public GitHub Pages site now builds a styled privacy-policy page directly
  from the canonical root `privacy.md`, and also publishes the source Markdown.
- Added regression coverage for session expiration, code rotation, player
  locking, player reset, public controller access, and compact remote controls.

## v0.45.16 — Production release correction

### User changes

- Republishes the v0.45.15 activity reliability and Link Studio fixes through
  the corrected Google Play production release workflow.

### Developer changes

- Google Play production uploads now omit the review flag when the Play account
  automatically sends completed changes for review.

## v0.45.15 — Activity reliability and Link Studio polish

### User changes

- Game join address choices now save reliably and confirm the selected address.
- Existing activities launch and display correctly on the room screen without the
  display-recovery 500 error.
- Link Studio favicon changes now reach `/favicon.ico` immediately, including
  after browser caching, and the signed-out logo matches the signed-in size.
- Added the prepared Fire TV store artwork and native 1920 × 1080 screenshots.

### Developer changes

- Activity run collections now map to the existing database foreign keys instead
  of generating a nonexistent shadow `ActivityRunId1` relationship.
- Join-address preferences live in service-owned configuration, with installer
  and updater ownership repair for upgraded Linux installations.
- Companion password-reset processing and favicon delivery are permission-safe
  and cache-resistant.

## v0.45.14 — Responsive Link Studio

### User changes

- Link Studio now adapts to phone-sized screens without horizontal scrolling.
- Link rows become compact cards on mobile, while destinations, click totals,
  dates, and copy/delete actions remain available.
- Analytics charts, settings, dialogs, navigation, and sign-in layouts now fit
  narrow screens.

### Developer changes

- Added mobile containment rules and responsive breakpoints to the bundled
  companion, with the standalone Link Studio copy kept in sync.

## v0.45.13 — Per-user security and compact settings

### User changes

- Authenticator MFA can now be enabled separately by each user, with an optional
  administrator requirement for every active user.
- Administrators can view and reset another user's enrolled MFA, and the
  controller pairing PIN remains visible after it is saved.
- Settings panels are reorganized into compact two-column layouts, with system
  diagnostics also available from the Settings overview.
- The bundled Link Studio companion includes its custom favicon setting under
  **Access & brand**.

## v0.45.12 — Improve Media & Storage layout

### User changes

- Upload limits now share a row with Storage allocation on desktop, making
  both settings easier to compare.

## v0.45.11 — Fix Activity availability toggle layout

### User changes

- The Activity availability switch in Settings now displays its label and
  explanation without overlapping or wrapping into the switch control.

## v0.45.10 — Custom Link Studio favicon

### User changes

- Administrators can choose a custom favicon for the Link Studio web client
  from **Access & brand**.

### Developer changes

- The companion validates and persists favicon data alongside the existing
  branding settings, applies it to the browser tab, and keeps the default
  favicon as the fallback.

## v0.45.9 — Complete Link Studio analytics charts

### User changes

- Platform and device analytics now show a complete ring when all recent
  visits belong to one category.

### Developer changes

- Full-circle SVG segments avoid browser-specific dash rendering gaps while
  mixed breakdowns retain their proportional arcs.

## v0.45.8 — Reliable Link Studio rebuilding

### User changes

- Link Studio now rebuilds correctly when the server performs a protected
  LessonCue update.

### Developer changes

- Shortener installers use a writable Docker client configuration directory
  instead of the unavailable root home directory under systemd hardening.

## v0.45.7 — Preserve short-domain forwarding

### User changes

- Installing or refreshing the URL shortener now preserves the configured
  destination for the bare short domain.

### Developer changes

- The protected Linux shortener-install path and the standalone installer both
  pass through LessonCue's saved root-redirect handoff instead of allowing an
  empty value to replace it.

## v0.45.6 — Reliable activity editor saves

### User changes

- Applying an activity preset or entering a media URL immediately before saving
  now saves the new value reliably.

### Developer changes

- Removed stale effect-based draft-ref synchronization that could replace a
  just-entered editor value with the previous draft during a fast save.

## v0.45.5 — LessonCue Shortener installation

### User changes

- The next shortener installation starts the LessonCue Link Shortener
  Companion directly in place of the retired Shlink Web client.
- Short Link game addresses now refresh shortener health before they are
  offered, so a newly installed shortener is available immediately when it is
  healthy.
- Settings is organized behind an expandable left navigation with an overview
  and grouped pages for organization, content and devices, infrastructure, and
  administration.

### Developer changes

- The release bundle includes a one-time `remove-shlink-web-client.sh` utility
  for retiring the old management containers and image without touching Shlink
  or PostgreSQL data.
- The normal installer and updater no longer contain legacy migration cleanup;
  they install the Companion directly as the supported management UI.

## v0.45.4 — Complete Google Play TV release

### User changes

- Google Play production publishing now leaves the release in the Play Console
  for the required review workflow instead of failing during automatic
  submission.

### Developer changes

- Bumped the application and Android TV package versions for the corrected
  production release.

## v0.45.3 — Complete Link Studio migration

### User changes

- Installing or updating the URL shortener now removes the legacy Shlink Web
  container and starts Link Studio under its own service and container name.
- The installer always uses the release's compose file, so a stale compose
  override cannot silently restore the old web client.

### Developer changes

- The Companion migration covers both the old nginx gate and the known Shlink
  Web container naming layouts without touching Shlink or PostgreSQL data.

## v0.45.2 — Link Shortener Companion

### User changes

- The shortener management workspace now uses LessonCue's Link Shortener
  Companion, with separate Administrator and Link Studio accounts.
- Set the initial shared password in LessonCue Settings, then change either
  account independently in the Companion's Access & brand page.

### Developer changes

- The Companion source is bundled and built locally; its scoped Shlink API key
  stays server-side in a Docker secret.
- Install and update paths remove the obsolete v0.45.1 nginx gate before
  publishing the Companion management port, and wait for both services.

## v0.45.1 — Short links that stay set up

### User changes

- The URL shortener no longer reports its own hundred reserved game codes as
  belonging to somebody else. They were LessonCue's all along; it was looking
  them up in the wrong case and never finding them.
- The shortener's web console now asks for a password, which you set in
  Settings. It arrives locked, so publishing its address does not hand it to
  whoever finds it.
- The short domain is now one of the choices for the game join address, rather
  than something that quietly took over once the shortener was running. Games
  whose code the shortener does not hold still get an ordinary address, so a
  wall never carries a dead link.
- Pressing Install for the URL shortener no longer claims the install will
  happen at the next update. It starts immediately, and now says so.
- The shortener panel keeps up with an install while it runs, so the result —
  whether it worked or why it did not — appears without reloading the page.
- Installing the shortener works on a real server: it finds its own files,
  starts only its own containers, and the parts that need to read each other's
  credentials can.
- Queueing an adaptive video profile no longer fails when the same profile is
  being queued elsewhere at that moment.
- Saving an activity right after choosing a preset stores the preset you chose.
  It could still store the previous one.

### Developer changes

- The join address service offers the short domain as a mode and owns the
  decision, so the session no longer overrides whatever the room was set to. It
  takes an interface rather than the shortener itself, which is what lets the
  awkward case -- short domain chosen, code not reserved -- be tested.
- Short URL lookup falls back to the lower-cased slug. Loose mode lower-cases a
  custom slug as it stores it while lookup stays exact, so all hundred codes
  were created and then never found again.
- An nginx gate fronts the console; the console itself is no longer published.
  The gate takes the port the console had, so an existing tunnel route is
  unaffected. The password file is read per request, so a change needs no
  restart.
- The shortener scripts locate their compose file rather than assuming a fixed
  depth, record SHORT_DOMAIN in .env, name the services they start, and leave
  both secrets readable by the unprivileged users that consume them.
- Secretless backups exclude the installer's shortener keys, which live one
  directory below the names the exclusion list held.
- The activity editor writes its draft ref in the same handler as the state
  rather than in an effect. The effect ran after commit, so a save pressed
  before React flushed it read the draft from before the change.
- The browser tests' activity helper waits for the editor to finish handling a
  save rather than for the response alone. It was returning while the client
  still had work to do, so the next step interleaved with it -- which is what
  made a whole spec file fail differently on each loaded run.
- The shortener's console image is swappable through SHORTENER_UI_IMAGE, as the
  API image already was, for anyone running their own build of either.
- The compose guard runs the installer against a stub docker in both layouts,
  and the provisioner's stand-in shortener lower-cases slugs the way the real
  one does.

## v0.45.0 — One button sets up short links

### User changes

- Setting up the URL shortener is now one button. Enter the short domain and
  where the bare domain should forward to, press Install, and everything else
  is worked out: the management address, how LessonCue reaches it, the reserved
  game codes, and Docker itself if this server does not have it yet.
- Changing where the bare short domain forwards to now takes effect. The
  shortener reads that when it starts, so the setting was previously stored and
  quietly ignored.
- The install controls stay on screen until the shortener is actually running,
  and report progress and any error. They used to disappear the moment a domain
  was saved — exactly when they were needed.
- The settings panel shows one thing at a time: two fields and a button before
  it is running, the full configuration after.

### Developer changes

- The updater installs Docker and the Compose plugin when they are missing,
  using the distribution package where it carries Compose v2 and Docker's
  signed repository where it does not. It runs as root; refusing for the want
  of a prerequisite it can satisfy was the wrong division of labour.
- `POST /shortener/install` settles the derived configuration — management
  host, upstream, enabled — and records the root destination where the
  installer reads it at container start.
- `ShortenerService.ReapplyAsync` re-runs the install so a changed root
  destination reaches the running containers.
- `install-latest.sh` and the updater both pass `SHORT_DOMAIN_ROOT_REDIRECT`
  through, so the bare domain forwards from the first start.

## v0.44.0 — Setting up short links in one pass

### User changes

- Fixed: the television could stay on the lobby after the host pressed Start.
  It listened for live updates but never checked again, so anything that
  happened while it was still connecting was missed and nothing recovered it
  until the browser was reloaded.
- Installing the URL shortener is now a switch in Settings. Enter the short
  domain, turn it on, and it installs — no command line, and no waiting for an
  update.
- The hundred reserved game codes provision themselves once the shortener
  answers, instead of waiting for someone to find a Repair button.
- The installer takes where the bare short domain should forward to, so
  `chroc.cc` sends people to your website from the first start.
- The management console arrives pointed at your shortener, with only the API
  key to supply — one click from **Show API key** in Settings.
- Settings names the exact Cloudflare Tunnel routes for this server, and
  recommends putting Cloudflare Access in front of the console's hostname.

### Developer changes

- `shortener:install` joins `update:` and `tunnel:enable` on the protected
  request path, so the console can ask the privileged helper to install now and
  read the outcome back. Where no helper exists the marker still stands and the
  next update honours it, and the response says which happened.
- `ShortenerHealthService` reconciles reserved codes when it finds the
  shortener healthy but incomplete.
- `SHORTENER_BIND` and `SHORTENER_UI_BIND` make the published ports an
  operator's choice, defaulting to loopback since the tunnel is the way in.
- The console is given `SHLINK_SERVER_URL` and `SHLINK_SERVER_NAME` but never
  an API key: it is served to any browser reaching its hostname.
  `SHORTENER_UI_API_KEY` opts in, for hostnames behind Cloudflare Access.

## v0.43.0 — The shortener installs with the server

### User changes

- The URL shortener now installs alongside LessonCue. The Linux installer
  offers it, and an update installs or refreshes it on a server that already
  said yes — so it no longer has to be set up by hand.
- Settings shows the exact Cloudflare Tunnel routes for this server: its own
  address, the ports actually published, and a note that neither needs opening
  on the firewall because the tunnel is the way in.
- The API key LessonCue holds can be shown on request, with the address to
  paste it into, for connecting the shortener's own console.
- Fixed: games stopped using short links on a server left running, because the
  check behind them was only refreshed when somebody opened Settings.

### Developer changes

- The release carries `LessonCue-URL-Shortener.tar.gz` — the compose file and
  its scripts — verified against the same signed checksums as the server.
- `install-latest.sh --with-shortener DOMAIN` installs it and records the
  answer in `/var/lib/lessoncue/config/shortener-domain`; the updater acts on
  that marker. The stack lives in `/opt/lessoncue-shortener`, beside the
  application rather than inside it, since `/opt/lessoncue` is replaced on
  every update.
- `ShortenerHealthService` keeps the short-link gate's finding fresh in the
  background rather than depending on somebody loading the settings page.
- Tunnel routes take the server's own host, and the reveal endpoint is audited
  and excluded from the polled status.

## v0.42.1 — Shortener settings that fill themselves in

### User changes

- The URL shortener's reachable address arrives filled in rather than blank.
  Which address is right depends on how LessonCue itself runs, so it is worked
  out for you instead of left as a guess.
- The management address follows the short domain as you type it, and stops
  following once you override it.
- The Cloudflare Tunnel instructions are always shown, not only after a domain
  has been saved — including the warning that a redirect rule on the whole
  hostname would swallow every short link. Once a domain is set, the exact
  hostnames and ports appear as a table to copy.
- Where this server has a local cloudflared configuration, the merged version
  is shown with the new routes added and every existing one untouched.

### Developer changes

- `ShortenerConfiguration.SuggestUpstream` derives the address from whether
  LessonCue is containerised: the shortener's service name and internal port
  inside the compose network, the published loopback port outside it. Saving
  the field blank stores the suggestion rather than nothing.
- `ForManagedTunnel` returns generic guidance when no domain is configured, so
  the section is useful before setup rather than after it.

## v0.42.0 — Optional URL shortener

### User changes

- LessonCue can now run a self-hosted URL shortener on a short domain of your
  own, giving the organization ordinary short links and giving games short,
  readable join codes on the same domain.
- Games joined at `{your-short-domain}/{code}`. Codes are four characters —
  a letter, a digit, a letter, a digit — with `0`, `1`, `I`, `L` and `O` left
  out so a room can read one off a television. A code typed in lower case works.
- The QR on the lobby screen and the address beside it both use the short
  domain when the shortener is on.
- Entirely optional. An installation that never turns it on is unchanged, and
  one whose shortener stops answering keeps running games on its own addresses.
- Settings → Integrations · URL shortener shows what state it is in, how many
  reserved codes the shortener holds, how many games are using one, and the
  exact Cloudflare Tunnel routes to add. It can test each hostname separately
  and repair codes that have drifted.
- The bare short domain can be sent to the organization's website, to
  LessonCue, or left showing the shortener's own page.

### Developer changes

- Four containers behind a `shortener` compose profile: Shlink, PostgreSQL, the
  web client, and nothing at all when the profile is not requested. The
  database is never published, both published ports bind to loopback, and the
  browser client is deployed with no credentials.
- 100 reserved codes ship as a fixed, version-controlled asset with a digest
  test, because they become slugs inside the shortener and a silent edit would
  strand links.
- Reserved links are permanent. Games take and release codes in LessonCue
  alone, so starting or ending one never touches the shortener.
- Provisioning, reconciliation and repair go through the REST API with a
  credential of LessonCue's own. A slug an administrator created by hand is
  reported as a conflict rather than taken over.
- Replaces the 0.41.0 root-redirect proxy, which put LessonCue in front of the
  short domain. The shortener answers its own root, so the tunnel now points
  there directly.
- `scripts/shortener-install.sh` and `scripts/shortener-update.sh` handle
  secrets, ordering, and update safety; `npm run test:shortener` guards the
  compose file's security properties.

## v0.41.0 — Activities that survive a real lesson

### User changes

- One join code per lesson. A lesson with four games used to hand out four
  codes and four rosters; players now scan once and keep their name, character,
  and score across every game in it, with a control to wipe scores and start
  over.
- Games run themselves. The host presses Start and moderates anything a person
  sent; everything else is on a clock — thirty seconds to answer a multiple
  choice, sixty to draw or write, closing early once everyone is in. Standings
  follow every round.
- Answer clocks now appear on the TV and on every phone. Games timed by
  autonomy previously counted down with nothing on screen showing it.
- The console says what it wants at each phase, and says so plainly when a game
  needs a person before it can continue instead of appearing frozen.
- Hosts can remove a player mid-game. Their phone cannot rejoin, and anything of
  theirs still waiting to be reviewed goes with them.
- A full class fits. The lobby wall used to clip names past roughly two dozen
  players, which looked to a student like a failed join; names now shrink as the
  room fills. Standings say how many players are not on screen, and every player
  sees their own place on their phone.
- Sorting, matching, and grouping answers no longer lose the player's work when
  the screen updates.
- The TV opens straight to its library instead of waiting on the network. On a
  slow or unreachable server this was over twenty seconds of nothing; it is now
  about four, marked as a cached schedule.
- A short domain can send its bare root somewhere useful — an organization's
  website, for example — while every short link and game code underneath it
  keeps working.
- Service Admins can hide Activities while testing, and choose which address the
  room is shown for joining.

### Developer changes

- `ActivitySessionGroup` owns the join code, roster, teams, and scores for a
  lesson; runs join a group rather than each minting their own code.
- `ActivityAutoPilot` decides the next transition from phase alone and returns
  nothing for the lobby and for outstanding moderation, so the two places a
  person is required are structural rather than conventional.
- A refused autonomous action parks the run and records why, instead of leaving
  a due timestamp in the past for the background service to retry every second.
- `CommitAsync` serializes state after stamping rather than before; the previous
  order discarded every write that function made, including the countdown the
  console was meant to show.
- The short domain is routed by one pure function, asserted for every root
  configuration to never claim a non-root path. Requests reach the shortener
  with their original Host so per-domain code resolution still works, and Shlink
  ships behind a compose profile.
- Sign-in, controller unlock, and screen pairing are each performed once per
  test worker; all three share a per-IP budget that the suite had begun to
  exhaust.

## v0.40.58 — Google TV production release

### User changes

- Submitted the LessonCue TV-only bundle to the dedicated Google TV production
  track.

### Developer changes

- Routed the Play release workflow to the Android TV `tv:production` track instead of
  the generic mobile internal track, which rejects the required Leanback TV
  feature.

## v0.40.57 — Google Play internal distribution

### User changes

- Published the signed LessonCue TV bundle to Google Play internal testing from
  tagged GitHub releases.

### Developer changes

- Connected the existing production release workflow to the Google Play
  Developer API for the `org.lessoncue.tv` internal track.

## v0.40.56 — Activity game typography

### User changes

- Added Fredoka for activity questions, answers, instructions, controls, and
  player names.
- Added Luckiest Guy for game names, round/status callouts, scores, reveals,
  and the “TIME'S UP!” moment.

### Developer changes

- Bundled the supplied font files locally with their license notices and kept
  the display face scoped to Activity/game surfaces.
- Corrected participant-preview typography so global admin heading styles cannot
  leak into phone questions and prompts.

## v0.40.55 — Classroom remote preset reliability

### User changes

- Fixed preset selection so choosing a template and applying it immediately
  always saves the selected format, including its engine and preset identity.

### Developer changes

- Kept preset selection local until Apply commits the template, and made the
  server trust the config preset when the top-level identity is stale.

## v0.40.53 — Classroom remote preset reliability

### User changes

- Fixed Activity preset templates so the selected template is always the one
  applied, even when it is chosen and applied immediately.

### Developer changes

- Made the shared preset picker read the live select value when applying a
  template and keep it stable across parent re-renders, removing the
  render-timing race exposed by the release browser suite.

### Reliability fix

- Kept the event-selected preset in a dedicated live ref so Apply cannot fall
  back to the previous template while React reconciles the editor.

## v0.40.50 — Classroom remote navigation

### User changes

- Reworked classroom and universal remotes around three clear tabs: Lesson,
  Playlist, and Activity.
- Made Lesson the weekly lesson picker and Playlist the place for individual
  cue selection, setup, notes, and timing details.
- Reduced the transport controls to compact icon buttons, moved locking to a
  small inline lock icon, and removed the remote header and save-as-app panel.

### Developer changes

- Removed the unused Quick tools launcher and kept screen selection in the
  Lesson surface so universal remote control remains available without a top
  bar.
- Added regression coverage for both remote tab semantics, compact controls,
  header removal, and activity-console continuity.

## v0.40.49 — Jackbox-style Activity rooms and host control polish

### User changes

- Added a full-screen classroom lobby with a persistent room code, scannable QR, absolute phone join address, and one-by-one player arrivals.
- Let players choose a fixed emoji and colour identity, switch players on shared phones, and see their own outcome, points, rank, and total after each reveal.
- Added TV response clocks, between-round standings races, first-in and streak callouts, optional automatic response-window advance, and a live host roster with answer counts.
- Made the host remote retain run timing, estimated finish, overrun guidance, playback errors, and save-as-app instructions alongside the compact controls.

### Developer changes

- Added server-resolved join addresses with reachable-mode fallback, per-participant projections, atomic auto-advance locking, server-derived callouts, and shared presentation components.
- Added lobby, join, identity, participant-result, host-console, standings, stage-clock, palette, sound fallback, reduced-motion, and full catalog smoke coverage.
- Added a manifest-driven optional sound-pack lookup with synthesized fallback cues and restored compact-remote diagnostics and run-of-show information.

## v0.40.48 — Activity game audio and visual polish

### User changes

- Gave Activity stages and participant phones distinct, coordinated colour
  identities for each game family and named preset.
- Added tactile phone feedback, gently animated waiting states, stronger
  countdown urgency, and large touch targets while preserving reduced-motion
  accessibility behavior.
- Added shared Activity sound effects with synthesized fallbacks, optional
  licensed sound-pack support, and display-only lobby/round/start/end themes.
  Phones never create a room-wide music chorus.
- Preserved teacher-customized themes instead of replacing them with generated
  game palettes.

### Developer changes

- Added shared palette resolution, contrast-aware accent text, Activity juice,
  audio preloading, preset-to-engine-to-shared sound lookup, and TV-only theme
  playback without changing server authority or participant projections.
- Added browser regression coverage for palettes, touch feedback, timer panic
  state, audio fallback/cascade, display ownership, theme transitions, and
  reduced motion.
- Added the documented optional sound-pack asset tree without bundling
  third-party audio.

## v0.40.47 — Google TV Activity display polish and reliability

### User changes

- Fixed the Google TV Activity display so every game in a lesson continues
  rendering when moving from one cue to the next.
- Made the wheel, scoreboards, countdowns, survey boards, ordering games, and
  other content-heavy Activities fit short TV WebView screens without
  clipping.
- Replaced the oversized native playback controls with a thin, translucent
  fading transport and clear web-controller guidance for Activities.
- Added visible loading, reconnect, and recovery states when a TV display
  cannot reach the Activity server.

### Developer changes

- Added a dedicated `/activity-display` projection route for native TV clients
  and keyed native WebViews to cue identity so destroyed views are never
  reused for later Activities.
- Added constrained-viewport browser coverage and a live 27-cue Google TV
  walk covering every seeded Activity type.

## v0.40.46 — Google TV Activity playback fix

### User changes

- Fixed Activity lesson cues on Google TV so they open the shared LessonCue
  web player instead of incorrectly showing “Media unavailable” or its pairing
  screen.
- Fixed Activity cues to open the requested round immediately, pass the paired
  TV identity securely to the shared player, and avoid the unnecessary first-
  play media prompt over the game.

### Developer changes

- Added TV playback-routing regression coverage for Activity cues that use a
  web-player URL without a downloadable media asset, including paired identity
  and web-player cache-busting coverage.

## v0.40.45 — Animal activity test pack and game-show polish

### User changes

- Added a ready-to-play animal-themed test catalog covering every current
  teacher-facing Activity Studio type, all collected in one lesson.
- Added a player-focused fun plan and richer game-show presentation guidance,
  including join prompts, reveal moments, scoring feedback, and celebration.
- Improved the phone-controller path so lesson activities expose their live
  definition, join code, QR link, and participant state to the host.

### Developer changes

- Added an idempotent `--seed-animal-activity-pack` command and regression
  coverage for the 27 current Studio activity types.
- Added live TV trivia join/reveal verification coverage and documented the
  player-facing engagement plan.

## v0.40.44 — Activities and game-show platform

### User changes

- Added Activities Studio with reusable teacher-created games, direct launch,
  lesson integration, grid/list library views, filtering, manual arranging,
  favorites, archiving, and bulk management.
- Added game-show experiences including Trivia, Read the Room, Punchline,
  Fake Out, Survey Board, Order Up, Match Minds, Doodle & Guess, Beat the
  Clock, Bracket Battle, Four Corners, and shared game-show utilities.
- Added phone participation with QR join codes, moderated responses, teams,
  scoring, host controls, reconnect-safe sessions, and flexible content counts.
- Signage is now always live; the obsolete organization feature checkbox was
  removed.

### Developer changes

- Added shared Activity definitions, live sessions, engine registry, role-safe
  projections, server-authoritative actions, scoring, timers, SignalR updates,
  and migration-safe persistence.
- Added Activity lesson-cue protocol, OpenAPI coverage, server validation,
  browser display/participant/host surfaces, and end-to-end coverage.

## v0.40.43 — installation and network guidance

### User changes

- Improved installation and LAN recovery guidance for administrators.

### Developer changes

- Updated the Docker/Avahi installation example to use the permanent
  `lessoncue.local` hostname.

## v0.40.42 — independent off-site backup destinations

### User changes

- Scheduled backups can now upload independently to Nextcloud, ownCloud, and
  another HTTPS WebDAV folder.
- Each destination has its own app-password/token setting, remote copy count,
  maximum age, upload status, retained-copy count, and actionable failure.
- Remote cleanup touches only LessonCue's encrypted `.lcbak` files; unrelated
  files in a shared WebDAV folder are preserved.
- Documentation now explains the separate Cloudflare Access for Infrastructure
  SSH plan and the credentials still required before it can be provisioned.

### Developer changes

- WebDAV listing is depth-limited, redirect-disabled, HTTPS-only, credential-free
  in URLs, and parsed with DTD/external-resolution protections.
- Legacy single-WebDAV policies migrate to the generic destination without
  losing their protected credential or retention settings.
- Added OpenAPI schemas and server tests for dual-provider upload, pruning,
  migration compatibility, retention, and validation failures.

## v0.40.41 — accurate local installer address

### User changes

- Linux installer completion messages now use the saved `.local` hostname, so
  isolated installations point operators to the address they will actually use.

### Developer changes

- Both the packaged installer and the prerequisite/download wrapper validate
  and reuse the persisted local hostname instead of printing a hard-coded name.

## v0.40.40 — reliable root-shell installer invocation

### User changes

- Linux installs now work from `sudo`, a root shell, or `su -c` even when the
  caller's `PATH` omits administrative `sbin` directories.

### Developer changes

- The packaged installer establishes a standard root execution path before
  checking and invoking `runuser`, `setpriv`, and service tooling.

## v0.40.39 — reliable Debian installer prerequisites

### User changes

- Linux installs now include the Debian package that provides `runuser` when
  the distribution splits it from `util-linux`, preventing a false prerequisite
  failure during media-sandbox validation.

### Developer changes

- The installer reports the exact missing capability and keeps the prior
  service recoverable when a host package layout differs.

## v0.40.38 — clearer live workflows and service diagnostics

### User changes

- Audience sessions now guide operators from poll creation to opening and QR sharing, with copy/download actions, live participant and moderation counts, and closed-session recovery.
- Media uploads run a server preflight that names the detected format, converter readiness, expected output, storage limits, and queue position before any bytes are sent.
- Controller focus mode provides large targets, visible current/next context, keyboard/remote shortcuts, selected-room and connection state, and command receipt feedback.
- Lesson editing separates Present now, Schedule, Save draft, Duplicate, Archive, and Delete, with local autosave recovery and a compact timeline.
- Service Admins can inspect storage, converters, queues, displays, backups, and updates and download a redacted support bundle.
- Large administration workspaces load on demand, with a first-interaction performance mark and a checked JavaScript bundle budget.

### Developer changes

- Added `/api/v1/media/preflight` and `/api/v1/support/bundle` to the OpenAPI contract.
- Expanded media preflight diagnostics across the broad catalog, including optional WebP/Theora and LibreOffice/Poppler readiness.
- Added browser accessibility, end-to-end, protocol, server, real-use, and bundle-budget verification to the release checklist.

## v0.40.37 — streamline signage playlist editing

### User changes

- Playlist cards now keep the timeline compact with a single duration indicator; production notes and item settings stay in the left inspector.
- The Playlist inspector now combines playlist and selected-item editing without a duplicate Selected item tab.
- Video items offer a Full video duration timing option, while custom timing remains available.
- Calendar elements keep one larger editable Title textbox so users can control title line breaks.

### Developer changes

- Removed the card-level notes/settings popovers and their duplicate controls while preserving inspector editing and playlist persistence.
- Full video timing derives from the selected media asset duration and updates the playlist item duration used for signage playback.
- Added browser coverage for full-video timing, consolidated inspector editing, and the compact card controls.
- Release metadata advances to 0.40.37 and Android version code 96.

## v0.40.36 — polished compact weather card

### User changes

- Weather elements now match the reference design with a bold location heading, illustrated condition icon, prominent temperature, and a clean precipitation, high/low, and wind row.
- Compact weather panels keep every text and icon region separate, so the temperature, conditions, and detail values no longer overlap or become partially obscured.
- Weather styling now adds a subtle dimensional background, purpose-built sun, droplet, and wind artwork, and consistent typography in both the editor and the live display.

### Developer changes

- Rebuilt the weather card around bounded responsive title, reading, and metrics rows shared by editor previews and live signage.
- Added browser geometry regression checks for clipping and overlap at narrow signage-panel dimensions.
- Release metadata advances to 0.40.36 and Android version code 95.

## v0.40.35 — assign signage to screens

### User changes

- The Signage editor's Screens tab now works and lets you select the paired screens that should display a Sign.
- The Screens page now switches from Assigned class to Assigned Screen for signage-only displays.
- Signs can be assigned or cleared directly from the Screens page, and the assignment updates the display immediately.

### Developer changes

- Added screen-level Sign assignment support to the admin PATCH API with validation for published Sign records.
- End-to-end coverage now exercises both Signage editor assignment and Screens-page assignment.

## v0.40.34 — rich signage typography and reference weather card

### User changes

- Every signage element now exposes a font choice, with rich formatting for editable titles and messages.
- Calendar event names wrap inside their panel instead of being clipped to one line.
- Weather elements now use the requested title, icon, temperature, conditions, precipitation, `H##/L##`, and wind layout; forecast, humidity, sunrise, and sunset controls are removed.

### Developer changes

- Unified responsive weather markup and styling between the layout editor and live display, including scalable SVG detail icons and wind direction handling.
- Weather field normalization now strips unsupported legacy fields from saved layouts and cache generation.
- Release metadata advances to 0.40.34 and Android version code 93.

## v0.40.33 — organized signage widget layouts and responsive text spacing

### User changes

- Weather elements now keep their title, reading, conditions, and detail values organized in a compact information card.
- Calendar elements keep the upcoming-events heading, event title, date, time, and optional description readable inside narrow panels.
- QR and Wi-Fi labels resize with their panels, and line-spacing settings now apply to widget text as well as ordinary signage text.

### Developer changes

- Added panel-relative sizing and line-height coverage for live weather, calendar, QR, and Wi-Fi rendering.
- Matched the signage editor previews to the live display structure, including separate calendar date/time rows and grouped weather details.
- Release metadata advances to 0.40.33 and Android version code 92.

## v0.40.32 — responsive signage elements and calendar descriptions

### User changes

- Signage elements now scale typography and widget content from the panel dimensions, so resizing a panel preserves its proportions.
- Weather elements use a responsive information-card layout with icon placement, icon color, and detail controls.
- Calendar elements use a compact upcoming-events layout with optional date, time, location, and description fields. Descriptions are hidden by default and can be enabled in element settings.

### Developer changes

- Added panel-relative container sizing to both signage editors and the live display renderer.
- Preserved calendar descriptions from iCalendar feeds and added browser coverage for responsive rendering and opt-in descriptions.
- Release metadata advances to 0.40.32 and Android version code 91.

## v0.40.31 — reliable signage drag-and-drop and release packaging

### User changes

- Make mouse dragging of ready media use the same reliable pointer path as touch dragging, avoiding Chromium native-drag inconsistencies while retaining native drag support.
- Preserve duplicate protection, click-to-add, timeline insertion, duration controls, notes, and item settings.

### Developer changes

- Make user-facing release-note extraction tolerate release entries that use top-level bullets instead of a dedicated `User changes` subsection.
- Release metadata advances to 0.40.31 and Android version code 90.

## v0.40.30 — pointer-based signage drag-and-drop fallback

- Make mouse dragging of ready media use the same reliable pointer path as touch dragging, avoiding Chromium native-drag inconsistencies while retaining native drag support.
- Preserve duplicate protection, click-to-add, timeline insertion, duration controls, notes, and item settings.

## v0.40.29 — native signage drag-and-drop bridge

- Handle ready-media drops through the browser's native capture-phase drag events so the first item can be dropped into an empty signage playlist reliably across Chromium/Linux environments.
- Prevent duplicate inserts while preserving click-to-add, touch dragging, timeline insertion, duration controls, notes, and item settings.

## v0.40.28 — hardened signage drag-and-drop

- Make ready-media drag-and-drop reliable in hosted Chromium/Linux browsers, including the first item dropped into an empty signage playlist.
- Keep click-to-add, touch dragging, timeline insertion, duration controls, notes, and item settings unchanged.

## v0.40.27 — signage drag-and-drop release fix

- Preserve the active media identifier across the browser drag lifecycle so dragging ready media into an empty signage playlist reliably creates the first timeline card.
- Commit the drop from the drag-end event as a browser compatibility fallback when a hosted browser omits the expected drop callback.
- Keep the existing signage horizontal timeline, duration controls, notes, and item settings behavior unchanged.

## v0.40.26 — signage drag-and-drop release fix

- Preserve the active media identifier across the browser drag lifecycle so dragging ready media into an empty signage playlist reliably creates the first timeline card.
- Keep the existing signage horizontal timeline, duration controls, notes, and item settings behavior unchanged.

## v0.40.25 — Android TV redesign and media workflow reliability

### User changes

- Android TV now has a calm, remote-friendly LessonCue interface with branded connection, pairing, lesson library, cue timeline, and playback screens.
- Lesson detail shows the next lesson clearly, preserves cue order, supports starting from any cue, and keeps playback controls available through a temporary overlay.
- Signage playback preserves emergency, offline, and display-power behavior while sharing the improved playback presentation.
- Linux media uploads avoid the capability configuration that caused Bubblewrap and `setpriv` processing failures on restricted installations.
- Signage playlist items now retain operator notes.

### Developer changes

- Added centralized Android TV colors, spacing, focus treatment, cue models, playback overlay behavior, and screen-level tests.
- Added 720p and 1080p visual verification for the primary Android TV screens.
- Hardened media-worker capability isolation by clearing ambient and inheritable capabilities without attempting an unprivileged bounding-set mutation.
- Release metadata advances to 0.40.25 and Android version code 84.

## v0.40.24 — grouped playback sections

### User changes

- Total playback now always presents Pre-Roll, Countdown, Main Lesson, and Post Lesson in playback order.
- Each playback section has a distinct shaded background, including visible empty drop zones for new lessons.
- Ready media can be dragged directly into an empty section or inserted within an existing section.

### Developer changes

- Total-view rendering groups cues by role instead of relying on interleaved global positions.
- Drag-and-drop insertion preserves the selected section role and calculates positions within that section.
- Release metadata advances to 0.40.24 and Android version code 83.

## v0.40.23 — clearer lesson playback builder

### User changes

- Removed the redundant “Preview with Trims & Fades” strip from the lesson playback builder.
- Playback sections now use high-contrast blue Pre-Roll, purple Countdown, red Main Lesson, and teal Post Lesson colors.
- Each cue’s left category rail matches its playback section, including in the Total view.
- Lesson cards stay compact with consistent previews, dedicated Notes/duration/Advanced Options footer controls, and clearer still-slide duration behavior.

### Developer changes

- Shared section color tokens now drive both section tabs and per-role cue rails, preventing Total view from flattening all cue colors.
- Still-image cues omit video-only trim/fade controls while videos retain the visual editor.
- Release metadata advances to 0.40.23 and Android version code 82.

## v0.40.22 — media-processing validation repair

### User changes

- Corrective release for the compact lesson builder and media workflow update.

### Developer changes

- The explicit `LESSONCUE_MEDIA_WORKER_SKIP_SANDBOX=1` validation override now
  bypasses the outer Linux `setpriv` wrapper as well as the media-worker
  Bubblewrap wrapper. Production remains sandboxed by default.
- Release metadata and Android version code advance to 0.40.22 and 81.

## v0.40.21 — compact lesson builder and reliable media workflows

### User changes

- Lessons now use compact horizontal timeline cards with visual previews, notes, durations, and Advanced Options menus.
- Playback is organized into Pre-Roll, Countdown, Main Lesson, Post Lesson, and Total sections, with looping pre-roll/post-lesson media and drag-and-drop Ready media.
- Still images and slides can remain untimed while carrying an optional expected lesson-duration estimate; new cues pause at the last frame by default.
- Media Library renaming preserves file extensions, and media-type filtering makes large libraries easier to browse.
- Linux media uploads now process correctly on installations affected by Bubblewrap capability restrictions.

### Developer changes

- Added section-aware manifest playback for post-lesson loops across the web player and Android TV client.
- Repaired visual trim/fade pointer interaction and added a pointer fallback for reliable timeline drag insertion.
- Added extension-preserving rename validation, untimed presentation conversion, and protected media-worker capability isolation.
- Release metadata and Android version code advance to 0.40.21 and 80.

## v0.40.20 — compact lesson timeline controls

### User changes

- Lesson timeline cards now stay compact with dedicated Notes and Advanced Options buttons for each cue.
- Ready media can be dragged to any position in the lesson timeline, with click-to-add still available.

### Developer changes

- Added accessible cue popovers for notes, playback settings, precision timing, and visual trim/fade controls.
- Added browser coverage for drag insertion, cue popovers, notes persistence, and existing advanced playback workflows.
- Release metadata and Android version code advance to 0.40.20 and 79.

## v0.40.19 — reliable SQLite snapshot verification

### User changes

- Linux updates no longer reject a valid protected snapshot when SQLite creates temporary WAL bookkeeping files during its integrity check.

### Developer changes

- Database integrity checks run against a disposable verification copy, preserving the byte-for-byte rollback snapshot for comparison and restoration.
- Linux transaction coverage reproduces SQLite read-only WAL/SHM side effects and confirms they never enter protected snapshots.
- Release metadata and Android version code advance to 0.40.19 and 78.

## v0.40.18 — self-repairing protected updates

### User changes

- Native Linux updates now run the updater shipped in the verified release before creating the protected snapshot, so updater fixes can repair older installations instead of failing behind the old updater.
- Update requests pin the release version selected by LessonCue, preventing a later release from being installed accidentally while an operation is queued.
- The Linux installer can repair only the signed updater and systemd units without replacing the application or user data.

### Developer changes

- The updater securely hands off to a checksum- and signature-verified release candidate while the original process retains the operation lock.
- Linux transaction tests cover release-updater handoff, pinned request versions, updater replacement, rollback, and snapshot failure diagnostics.
- Release metadata and Android version code advance to 0.40.18 and 77.

## v0.40.16 — reliable protected update diagnostics

### User changes

- Failed native updates now report the exact protected snapshot phase that needs attention while leaving the existing installation unchanged.

### Developer changes

- Linux snapshot creation now validates and reports every copy, comparison, rename, and metadata-write step.
- Release metadata and Android version code advance to 0.40.16 and 75.

## v0.40.15 — horizontal lesson and signage builders

### User changes

- Lesson editing now uses one clean surface with lesson details above the playback sequence.
- Transition Options and Playback Options stay collapsed until they are needed.
- Lesson and signage playlists now use horizontal cards with a media library shelf.

### Developer changes

- Browser coverage follows the consolidated lesson editor and horizontal signage playlist.
- Release metadata and Android version code advance to 0.40.15 and 74.

## v0.40.14 — reliable uploads on port 80 servers

### User changes

- PNG and other media uploads now process correctly when the Linux server listens on port 80.

### Developer changes

- Media workers clear inherited service capabilities before starting Bubblewrap.
- The Linux installer verifies that the required `setpriv` capability-isolation utility is installed.
- Release metadata and Android version code advance to 0.40.14 and 73.

## v0.40.13 — reliable Linux installer validation

### User changes

- Re-running the Linux installer now validates media conversion under the same service account used in production.
- Existing servers with private media temporary directories can complete the repair or upgrade without a false sandbox permission failure.

### Developer changes

- The installer Bubblewrap probe runs as `lessoncue`, matching the systemd service identity and user-namespace mapping.
- Linux media-worker regression coverage exercises a service-owned 0700 probe directory.
- Browser validation now targets the unique playback heading when the same title is also shown in media metadata.
- Release metadata and Android version code advance to 0.40.13 and 72.

## v0.40.11 — recoverable protected updates

### User changes

- Concurrent protected update requests now record a clear durable result instead of leaving the Software updates screen stuck in progress.
- A request that loses the updater lock is safely discarded while the active operation continues.

### Developer changes

- The Linux updater writes a failure result and consumes duplicate requests when another updater process owns the lock.
- The disposable transaction regression covers lock contention on both x64 and arm64 runners.
- Release metadata and Android version code advance to 0.40.11 and 70.

## v0.40.10 — a clearer lesson workflow

### User changes

- The administration sidebar is organized into clearer sections, with faster dashboard actions for common tasks.
- Lesson editing now separates lesson settings from the playback sequence, making run-of-show changes easier to review.
- The Media Library supports both grid and list views for visual browsing or detailed management.
- Administration dialogs and navigation labels have improved contrast and accessibility behavior.

### Developer changes

- Browser coverage follows the tabbed lesson editor and explicit Media Library view modes.
- The Linux updater transaction fixture selects the correct release asset on both x64 and arm64 runners.
- Release metadata and Android version code advance to 0.40.10 and 69.

## v0.40.9 — clearer update recovery

### User changes

- If a server update is already running, LessonCue now reports that clearly instead of showing a generic server error.
- Failed update attempts retain a useful explanation so administrators know what to fix before trying again.
- A failed update no longer leaves the update screen stuck in an endless installing state.

### Developer changes

- The Linux updater records failures that happen before transaction setup, including signature and package validation failures.
- Update and rollback requests are serialized in the web process, and expected operation contention returns HTTP 409 instead of HTTP 500.
- Regression coverage verifies that an invalid release signature produces a durable failure result for the server UI.

## v0.40.8 — more reliable server updates and media playback

### User changes

- Linux server updates now install and verify the components needed for media conversion before restarting the server.
- Older Intel processors can use supported H.264 hardware encoding, with automatic software conversion when hardware is unavailable.
- Media names can be replaced completely, with a clear warning when the new name is already in use.
- Account security settings now show the authenticator option in one place.
- Troubleshooting failures retain useful details for seven days.

### Developer changes

- The Linux package now installs the protected media worker and an Intel render-node udev rule, validates the sandbox before service restart, and removes both during uninstall.
- Bubblewrap preserves legacy VAAPI driver selection, exposes the required read-only DRM topology, and maps namespaced root to the invoking service account for device access.
- Transactional updates snapshot and restore the media worker and render-node rule alongside the application and service units.
- Haswell validation confirmed H.264 VAAPI through Debian's `i965` driver; QSV/oneVPL remains a newer-platform path.

## v0.40.7 — safer server upgrades

### User changes

- Re-running the Linux installer on an existing server now completes safely, so repairs and upgrades do not fail while the server is running.

### Developer changes

- The Linux installer stops the active LessonCue service before replacing its executable and restores service availability if installation exits early.

## v0.40.6 — more dependable updates and clearer diagnostics

### User changes

- Server updates now explain why an update could not start and provide a failure reference for support.
- Service administrators can filter troubleshooting logs to failures and expand their technical details.
- Failures remain available for seven days so intermittent update problems are easier to diagnose.

### Developer changes

- Update requests now record trace IDs, protected-operation decisions, HTTP failure statuses, and exception details.
- The Linux installer repairs updater-owned configuration paths left with older permissions.
- Failures use a separate seven-day JSONL store alongside the normal 2,000-entry runtime log.

## v0.40.5 — clearer updates and a more dependable release

### User changes

- The update screen now explains the purpose of an available update before you install it.
- Media uploads now follow the current two-step media workflow.
- Administration screens have clearer contrast and better labels for keyboard and assistive-technology users.

### Developer changes

- Browser validation now follows the current two-step media workflow and tolerates cross-platform font rasterization.
- Release validation no longer depends on the Bubblewrap gate that cannot run reliably on GitHub-hosted runners.
- GitHub releases publish both sections, while Android TV update metadata carries only the user changes.
