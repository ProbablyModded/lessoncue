# Bug audit checkpoint

Updated: 2026-09-14. Status: twenty-five findings have local fixes; comprehensive audit remains incomplete. BUG-025 Android checks passed.

Coverage estimate requested by user: 31 of 1,167 baseline files inspected at least partially (2.7%, approximately 3%); 7 fully reviewed/verified (0.6%), 24 partial, 1,136 unreviewed. New regression/helper files are excluded from that denominator. This is file coverage, not a reliable estimate of effort remaining; large high-risk paths have received disproportionate attention. Do not count 25 fixes as completion of the comprehensive audit.

- Repository: `<local LessonCue checkout>`.
- Baseline HEAD: `204d8ca4762e01dcd88764b0668659737b0fc424` on `codex/release-v0.46.3`.
- Published v0.46.3/main at last release verification: `a9dff6c8ffacaf2030aa880aac31558e7df2cab9`.
- Local changes: Android URL/discovery/API/cache/updater/reconnect/navigation fixes and web host snapshot/lifetime/polling fixes, regression tests and audit documents; not committed or released.
- Coverage: 1,167 baseline files with provisional kind classifications; the TSV records partial/full inspections and new local files. Most rows remain unreviewed. New audit documents are outside the baseline.
- Prior evidence: `docs/reliability-review-2026-09-12.md`. Do not repeat its completed fixes without new evidence or relevant changes.

## Next action

USER SCOPE CHANGE: Continue `docs/classroom-reliability-pass.md`, not the comprehensive sweep below. Stabilize the existing 25 fixes, validate six classroom workflows within explicit caps, and document unresolved risks. Older next-action paragraphs are backlog only. Do not resume NSD expansion merely because it appears below.

BOUNDED PASS RESULT: Local stabilization completed on 2026-09-14: 554 server tests, 23 web units, 39 selected browser tests passed; typecheck/build/lint and protocol/network/shortener checks passed (15 existing lint warnings). Latest Android 81 tests per variant/lint and emulator evidence reused without rerunning unchanged code. No new product edits, commits, release, or live changes. Five workflows pass selected automated checks; TV connection recovery still needs the formerly failing on-site TV. See classroom-reliability-pass.md for the acceptance matrix and short checklist. Stop broad discovery/auditing; next steps are authorized release preparation or physical verification, not automatic backlog expansion.

Continue B01 at Medium reasoning: check scoped IPv6 URLs and first resolved-but-unreachable server fallback in MainActivity.findLessonCueServer/reconnectSavedServer. NSD now serially queues resolution failures, but a syntactically valid first result is still returned without probing reachability. Do not claim this closes the four physical-TV failures. Follow with old-run command/cancellation behavior or B00 CI flake work. Do not repeat completed unit checks unless affected code changes.

Immediate work: B01 discovery reachability: first syntactically valid but unreachable NSD address prevents trying another discovered server. Inspect findLessonCueServer/reconnectSavedServer with LessonCueDiscovery and ServiceResolutionQueue; preserve identity/auth boundaries. Screenshot command-loop blocking is locally fixed by BUG-025. BUG-024 bitmap cleanup passed emulator tests; audit-owned emulator-5570 was stopped. Historical CI flake remains unproven.

Resource follow-up: updater cleanup is fixed (BUG-006). Media download cancellation is checked between reads/checksum chunks, but cancellation does not instantly interrupt an active socket read; 15-second read timeout bounds it. API coroutine cancellation likewise does not actively interrupt blocking IO.

B00 still needs shipped-entry-point mapping, disposition of unclassified/legacy files and the activity-host CI flake. Local and fetched main trees were identical on 2026-09-14; git fetch succeeded. An ordinary rg scan inside .git is not an exhaustive hidden/ignored duplicate audit.

## Open leads (not new confirmed bugs)

1. Four on-site televisions failed while one connected. Supplied troubleshooting log is available at `/tmp/codex-remote-attachments/01a01738-ad43-70a2-9331-f837e5c1a188/95DF7520-C2E9-4A7E-8864-255EADCEB11D/1-lessoncue-troubleshooting-2026-09-13T17-49-01.005Z.json`. Its 15 runtime entries show local port 8081 refusal, a canceled Quick Sync probe, and unreachable/resource-unavailable calendar/weather requests. Request/pipeline entries can describe the same failure twice. These entries do not establish IPv6 as the cause or identify each failing TV. Audit entries have not yet been analyzed.
2. Native Android signage rotation/source switching and WebView memory use remain unverified on physical hardware in the prior review.
3. Activity-host CI test failed even with the 30-second response wait; the release rerun passed. Investigate flakiness using traces and actual request/state sequencing.
4. Current CHANGELOG.md lists v0.46.2 before v0.46.3. Check extraction/update behavior in B12; ordering alone does not establish an updater bug.

## Session record template

Append compact entries: batch/symbols reviewed; tree/commit; finding IDs and evidence strength; local fixes; tests with outcomes and evidence path; coverage rows changed; unresolved issue; exact next command/files; recommended next effort. Replace the Next action above as work progresses. Keep credentials and private log payloads out of this file.

## Findings

### BUG-001 — P2 — Public DNS name accepted as a private HTTP address

- Evidence: reproduced on the pre-fix source with JUnit. `isTrustedLocalHttpHost("10.0.0.1.example.org")` returned true because mapNotNull discarded DNS labels, leaving four private-address octets. The caller consequently accepted this ordinary DNS hostname over HTTP, contrary to its HTTPS policy. Possible exposure depends on the user/discovery selecting such a host; no exploit or on-site connection cause is claimed.
- Location: `android-tv/app/src/main/kotlin/org/lessoncue/tv/ServerUrlPolicy.kt`, isTrustedLocalHttpHost.
- Fix: require the original hostname to have exactly four labels as well as four valid octets. Valid private IPv4, IPv6 and .local behavior is preserved.
- Regression: three ordinary DNS names embedding private/loopback octets must be rejected over HTTP and accepted over HTTPS. Original test run reproduced one assertion failure. One fixture initially had an invalid numeric DNS suffix and was corrected to a valid .org hostname before final verification.
- Validation: `gradle -p android-tv :app:testSideloadDebugUnitTest :app:testStoreDebugUnitTest --console=plain` passed both complete unit suites; log `/tmp/lessoncue-audit-url-after.log`. Pre-fix log `/tmp/lessoncue-audit-url-before.log`. `npm run test:network-config`: 3 passed. No physical-TV test performed. Vega's equivalent IPv4 check retains every label and does not share this defect.
- State: fixed locally, unreleased; larger address-policy and discovery review continues.

### BUG-002 — P2 — Android API connections skip cleanup on IO failures

- Evidence: reproduced with injected HttpURLConnection failures through actual requestPairing and uploadDiagnosticScreenshot entry points. Before the fix both failed with `Disconnect after connect failure expected:1 but was:0`. Source also skipped disconnect after upload/read exceptions because it occurred only after successful body reading.
- Location: LessonCueApi.kt request and uploadDiagnosticScreenshot. Repeated polling/reconnect/upload failures could retain connection resources until other cleanup/GC; no physical-device memory growth has been measured.
- Fix: wrap connection configuration and IO in try/finally, always disconnect. Add a defaulted connection factory to allow deterministic fault injection without global networking hooks. Existing callers retain the normal URL.openConnection behavior.
- Regression: ApiConnectionCleanupTest covers connect, upload and read failures for JSON and screenshot requests, successful discovery and HTTP 503, checking cleanup and preserved exception messages. Coroutine stack recovery can copy exceptions, so tests compare type/message rather than object identity.
- Validation: full sideload and store debug unit suites each passed 48 tests (96 executions); `/tmp/lessoncue-audit-cleanup-after.log`. Reproducing failures logged in `/tmp/lessoncue-audit-cleanup-before.log`. `git diff --check` passed. Test outputs under android-tv/app/build/test-results/test{Sideload,Store}DebugUnitTest are overwritten by later runs.
- State: fixed locally, unreleased. MediaCacheWorker and active cancellation behavior remain separate follow-up work.

### BUG-003 — P2 — Discovery drops other services during an outstanding resolve

- Source-confirmed: the old AtomicBoolean rejected all announcements while a resolve was in flight; failure only cleared the flag and never recovered discarded announcements. An unusable resolved address also left the flag set. No physical-site reproduction is claimed.
- Fix: ServiceResolutionQueue retains/deduplicates announcements, resolves one at a time, moves on after failure/invalid address, and ignores duplicate/late callbacks after completion or cancellation. Android's adapter handles a null host. Start/stop are posted to the main Handler to prevent discovery starting after cancellation cleanup.
- Tests: four pure coordinator tests cover queued success after failure, duplicate/stale callbacks, cancellation, and synchronous resolver exceptions. Both Android unit variants passed in `/tmp/lessoncue-audit-discovery.log`. Android NSD itself still needs emulator/device validation.

### BUG-004 — P2 — Media worker can hang or skip connection cleanup

- Source-confirmed: MediaCacheWorker set no socket timeouts and disconnected only after a fully successful download/checksum/rename. runCatching also converted cancellation into retry/error reporting.
- Fix: extracted downloadMedia with 8-second connect and 15-second read timeouts, finally-disconnect, cancellation checks during copying/checksumming, and explicit CancellationException propagation in worker. Reject path separators/dot paths in worker filenames before accessing cache storage. Keep auth-header setup in worker; follow-up review should cover URL/redirect credential handling and cancellation at connection establishment.
- Tests: fake-connection download tests verify timeout configuration, read failures, mid-copy cancellation and cleanup. This proves control flow, not native memory trends.

### BUG-005 — P2 — Incomplete or inconsistent media can be published as cached

- Source-confirmed: previous worker accepted EOF without checking Content-Length or Content-Range; absent a checksum, it could rename an incomplete or wrongly resumed response into the playable cache. It also opened/truncated a partial file before opening a failing response stream, and repeated an unavailable range on every retry.
- Fix: accept only 200/206; validate range start/end/total and declared response length; force identity encoding for byte accounting; retain incomplete partials without publishing; preserve known-good destination; restart after 416; require checksum success when supplied. A server ignoring Range (200) replaces the partial from zero. Invalid responses cannot replace the existing playable file.
- Tests: seven MediaDownloadTest scenarios cover complete response, resume/range ignored, truncated body preserving known-good cache, wrong offset/503 preserving partial, partial range/416/full retry, checksum mismatch/match, read failure/cancellation. Final full-variant validation is recorded below.

## Latest validation

Final validation after adding Accept-Encoding: identity succeeded: 59 sideload and 59 store unit tests, zero failures; both Android debug lint tasks passed. Command: `gradle -p android-tv :app:testSideloadDebugUnitTest :app:testStoreDebugUnitTest :app:lintSideloadDebug :app:lintStoreDebug --console=plain`. Log: `/tmp/lessoncue-audit-download-final.log`. `git diff --check` passed. No release or live-server changes made. Five-hour usage reached 100% at the end of this slice; no reset credit was consumed. Next work remains the discovery/reconnect and updater cleanup follow-up above.

## Resumed window — updater and remote checks

Allowance reset naturally; no reset credits used. New local fixes below, not yet released. Final Android validation after BUG-009: 72 sideload and 72 store tests, zero failures, both debug lint tasks passed (`/tmp/lessoncue-audit-cache-after.log`).

### BUG-006 — P2 — Updater failure cleanup and unbounded error reads

UpdateClient.openFollowingRedirects skipped disconnect on connection/error-body/invalid redirect exceptions. It read the entire error body before taking 300 characters. Injected-connection tests reproduced three failures. Finally cleanup now covers every non-handoff path; the final successful reader retains ownership. Error reading is bounded before building the message. Five tests cover transport failure, malformed/untrusted/missing redirect, successful redirect and large error body. Full Android variants passed in `/tmp/lessoncue-audit-update-after.log`.

### BUG-007 — P1 — Reconnected TV background loops keep old address/token

Source-confirmed: heartbeat/control/manifest effects were keyed only by screen ID while each captured serverUrl and token. Cached startup launches old-address loops; successful rediscovery with the same screen ID never restarts them. All three effects now key on full DeviceIdentity. CancellableResult preserves cancellation and prevents stale success/error handlers after cancellation in startup, connection and those loops. Three coroutine tests passed; full Android variants passed in `/tmp/lessoncue-audit-reconnect.log`. Physical Compose/device reconnect validation remains outstanding.

### BUG-008 — P1 — Navigation no-op blocks following remote commands

Source-confirmed: server returns oldest command newer than cursor; Android only advanced cursor when next/previous/seek changed a Player. Next at the last item or navigation outside Player therefore repeated forever, hiding later commands. applyRemoteNavigation explicitly consumes recognized no-ops; valid movement/seek still updates state, negative seek clamps to zero. Three regression tests cover next-at-end then previous, no-player navigation and valid/clamped movement. Both Android unit suites passed in `/tmp/lessoncue-audit-control.log`. Separate lead: a play targeting a deleted/unavailable lesson can still stall the browser and native queues; investigate desired error/ack semantics.

### BUG-009 — P2 — Bad manifest response overwrites usable offline cache

Reproduced: both manifest fetch methods wrote raw responses before parsing. A 200 HTML sign-in response or incompatible manifest destroyed the prior valid cache. Parse fully before touching the cache, then write a temporary sibling and replace atomically (ordinary replace fallback only if atomic moves are unsupported). Two regression tests cover both fetch methods, malformed HTML/incomplete JSON preserving version 7, and valid version 8 replacing it without temporary leftovers. Pre-fix ComparisonFailure is in `/tmp/lessoncue-audit-cache-before.log`; all final Android tests/lint passed in `/tmp/lessoncue-audit-cache-after.log`. Local/unreleased.

### BUG-010 — P2 — Delayed host snapshots restore obsolete rosters and controls

Reproduced in the browser: replaying a pre-join host snapshot after Carmen joined removed Carmen from the displayed roster. All four host-state fetch paths used unconditional setters, unlike the ordered public-state path. They now preserve the latest revision/serverTime using latestActivityHostView. Initial connection also checks cancellation after its awaited host request. Browser regression failed before the fix (`/tmp/lessoncue-audit-host-order-before.log`) and passed after; the signed-out-phone regression also passed (`/tmp/lessoncue-audit-host-order-after.log`). Typecheck and all 22 web unit tests passed. This does not claim the historical CI timeout is resolved. Cross-game component lifetime remains a separate test in progress.

### BUG-011 — P1 — Previous game response temporarily restores wrong game controls

Reproduced with two already-created lessons: delay the old host poll, report TV playing the second game, wait for its controls, release the old response. A DOM observer caught the previous join code returning; an ordinary eventual assertion initially missed the transient regression. ActivityController now keys its internal session by run/definition/lesson/item identity, isolating initial-envelope refs, state, subscriptions and pending setters across cues. All three ordering/signed-out browser cases passed (`/tmp/lessoncue-audit-host-switch-after.log`). Pre-fix failure `/tmp/lessoncue-audit-host-switch-before.log`. The initial fixture created the second lesson after the remote library loaded and was corrected to avoid testing missing library refresh instead.

### BUG-012 — P2 — Host polling accumulates stalled requests

Reproduced: blocking the first host request accumulated three in under five seconds because setInterval ignored pending fetches. Periodic host polling now uses the existing single-flight createRefreshLoop and passes its AbortSignal through getHostState to fetch; 10-second timeout and cleanup abort are preserved. Browser regression `/tmp/lessoncue-audit-host-poll-before.log` failed with expected 1/received 3. Post-fix full spec is `/tmp/lessoncue-audit-host-final.log`. Existing loop unit test covers coalescing and cleanup abort. Separate disconnected full-state polling remains to review; no claim all controller requests are serialized.

## End-of-slice validation — 2026-09-14

- Android: 72 tests per variant (144 executions), zero failures; sideload/store debug lint passed. `/tmp/lessoncue-audit-cache-after.log`.
- Web: typecheck, production build and 22 unit tests passed; all 9 host-console browser tests passed in 57.8 seconds. `/tmp/lessoncue-audit-host-final.log` and `/tmp/lessoncue-audit-host-units.log`.
- Web lint: zero errors, 15 warnings; no ActivityController/activityConnection warning reported. `git diff --check` passed.
- Protocol validation (134 paths), all 3 network configuration tests, and shortener compose security checks passed.
- No commits, release, live-server mutation or reset-credit redemption during this audit. Native physical-device behavior, the on-site four-TV failure and comprehensive coverage remain unresolved.
- Resume at Medium on the immediate disconnected-polling reproduction above. Do not repeat completed suites without affected changes; escalate reasoning for a demonstrated concurrency ambiguity.

## Next naturally reset window

No reset credit consumed. Git fsck completed with only dangling entries (`/tmp/lessoncue-audit-git-fsck.log`); hidden/ignored .git file listing found no trailing space-number duplicates. Three more fixes below. A read-only, no-snapshot Google TV emulator is running on emulator-5570 for instrumentation tests; stop this audit-owned instance with `adb -s emulator-5570 emu kill` when done. Do not stop unrelated devices. Its saved image is unchanged. The first install had a signature mismatch; Gradle cleanup left no package, a manual uninstall found nothing, and the next install/test worked.

### BUG-013 — P2 — Disconnected fallback polling also accumulates requests

Reproduced by holding SignalR negotiation and the first getRun response (`/tmp/lessoncue-audit-fallback-before.log`). Fallback now uses createRefreshLoop, passes its AbortSignal through getRun and host-state, and skips canceled state/error updates. Passive refreshes do not toggle the manual refresh spinner. Both this regression and signed-out-phone test passed (`/tmp/lessoncue-audit-fallback-after.log`). Typecheck/build passed. Manual refresh and initial connection remain separate bounded callers, not one globally serialized stream.

### BUG-014 — P2 — Canceled update check overwrites newer update result

Reproduced on Google TV Android 16 emulator with a suspended noncancellable first request: manually recheck, get Available, then complete the canceled old request with Current; old code replaced Available. performCheck now uses cancellableResult, preserving cancellation and checking after late returns. All 7 UpdateManagerIntegrationTest cases passed on the emulator plus both 72-test unit variants and lint (`/tmp/lessoncue-audit-updater-race-after.log`). Before-fix AssertionError `/tmp/lessoncue-audit-updater-race-before.log`. This verifies the updater, not physical TV discovery/resource stability.

### BUG-015 — P2 — Drawing eraser misses the visible middle of sparse strokes

Reproduced with a real touch stroke reduced to two points: erasing its center left it intact, because only stored points were checked. strokeTouchesPoint now measures distance to finite line segments, with dot/degenerate support. Unit checks cover sparse/diagonal lines, nearby unrelated lines, out-of-segment projections, dots and empty strokes. Browser regression failed before (`/tmp/lessoncue-audit-eraser-before.log`) and passed after; full 10-test drawing spec `/tmp/lessoncue-audit-eraser-after.log`. Typecheck and 23 web unit tests passed.

### BUG-016 — P2 — Rejected drawing favorite prevents valid winner scoring

Reproduced with two drawings and three voters: reject the drawing with two votes, reveal, and no points were awarded to the valid drawing with one vote. ScoreDrawingAsync now filters to approved/nonhidden current-round drawings before counting; canonicalizes eligible GUID spelling while grouping. Regression also retries reveal to verify no duplicate award. Before fix: `/tmp/lessoncue-audit-drawing-score-before.log` (empty score list). Full server suite after: 550 passed, `/tmp/lessoncue-audit-server-after.log`. Existing AwardScoreAsync prevents duplicate same-recipient/reason/run/round/team awards; team-change semantics remain a separate review lead.

### BUG-017 — P2 — Host command failures escape as unhandled promise rejections

Reproduced by returning HTTP 503 to Start: the expected visible command error appeared, but a browser pageerror also fired. Live host actions now share send(), which consumes the API rejection after ActivityApi publishes the visible lifecycle failure, clears busy state, and leaves retry available. Applied to start/hold/standings, reset/lock, moderation and auto-advance checkbox. Pre-fix `/tmp/lessoncue-audit-host-error-before.log`; full 11-test host suite after passed (`/tmp/lessoncue-audit-host-errors-after.log`).

### BUG-018 — P2 — Host voting count includes earlier drawing/text submissions

Reproduced: two drawings submitted, switch to voting with no votes, and host still reported 2 of 2 with answered ticks. During a voting phase only votes now count; non-voting engines that save responses as votes retain their previous behavior. Label becomes VOTES IN. Browser test expects 2 of 2 before voting, 0 of 2 at voting start, then 1 of 2 after a real vote. Before-fix `/tmp/lessoncue-audit-host-votes-before.log`; after `/tmp/lessoncue-audit-host-votes-after.log` (first launch hit the still-shutting-down old test port, rerun started after cleanup).

Emulator note: the audit-owned emulator-5570 was stopped successfully after updater tests. No physical TV or live-server changes made.

### BUG-019 — P2 — Accepted noncanonical vote targets can lose points

Fake Out explicitly accepted TRUTH case-insensitively but scored only lowercase truth; regression accepted the vote yet found no score. SaveVoteAsync now stores canonical truth/GUID targets; ScoreBluff also accepts historical truth casing. Lowercase and uppercase truth regressions both pass. Evidence: `/tmp/lessoncue-audit-bluff-before.log`; final full server validation below.

### BUG-020 — P2 — Rejected creative/bluff submissions still receive points

Reproduced: rejected bluff earned 100 points from two votes; rejected Punchline favorite received points instead of the valid runner-up. Creative counting now filters current-round approved/nonhidden submissions before choosing a winner, and bluff scoring applies the same eligibility checks. Before logs: `/tmp/lessoncue-audit-bluff-before.log` and `/tmp/lessoncue-audit-creative-before.log`. Valid multiple bluff votes already awarded the correct total (two 50-point events); an initial test incorrectly required one event and was corrected to assert totals and repeated-reveal stability. That behavior was preserved, not reported as a bug.

## Latest checkpoint validation

554 server tests passed (`/tmp/lessoncue-audit-server-votes-after.log`); all 10 drawing browser tests passed; full 11-test host suite passed before vote-count addition, then vote-count and error tests both passed (`/tmp/lessoncue-audit-host-votes-after.log`). Web typecheck/build and 23 units passed; latest lint zero errors/15 warnings. Seven updater instrumentation tests passed on Google TV emulator, both Android 72-test variants and lint passed. Git diff whitespace check passed. Usage reached 97% before checkpoint writing; no reset credits used. No commits/releases/server changes. Resume at Medium on poisoned-command reproduction, not completed fixes.

### BUG-021 — P1 — Missing lesson poisons the remote command queue

Reproduced in browser with an unavailable play followed by pause: no failure acknowledgment arrived and later command never executed. After a successful manifest fetch, a missing lesson is now a terminal rejected command: report an explicit playback error and consume its version, preserving current media. Fetch failures still retry. Browser error ref survives normal media telemetry and clears on successful command/identity reset. Native resolveRemotePlay encodes retry vs consume; separate error state is merged into heartbeat telemetry so Library resets cannot erase it. This does not yet speed native idle acknowledgments (up to 30 seconds).

Evidence: `/tmp/lessoncue-audit-queue-before.log`; browser full heartbeat spec `/tmp/lessoncue-audit-queue-after.log`; Android both unit/lint variants `/tmp/lessoncue-audit-queue-android.log`; expected 73 tests per variant. Native decision regression covers failed fetch, missing lesson, and next valid playlist, but no physical-device command test. Final identity-reset clearing lines were added after those builds; final typecheck/lint `/tmp/lessoncue-audit-last-lint.log`, rerun affected builds if needed on resume. Usage is now near weekly limit (98% at last check); no reset credits redeemed.

Final BUG-021 revalidation includes the identity-reset lines: production build and all 3 heartbeat browser regressions passed (`/tmp/lessoncue-audit-queue-final.log`, 23.7 seconds); both Android unit variants and debug lint passed (`/tmp/lessoncue-audit-queue-android-final.log`, 30 seconds). Typecheck passed; lint zero errors/15 existing warnings. No affected builds remain pending for this fix. Next work is native heartbeat wake/ack latency and offline stop behavior; keep no-release scope.

### BUG-022 — P2 — Idle command acknowledgment waits up to 30 seconds

Native control consumption now wakes the existing heartbeat loop through a per-identity conflated channel. It does not create parallel heartbeat requests; bursts during a slow request leave one follow-up. Three coroutine regressions verify prompt wake, coalescing, periodic timeout and cancellation. Both Android unit variants (76 each) and lint passed (`/tmp/lessoncue-audit-heartbeat-wake.log`). This removes the idle sleep after a command, not network-request latency; no physical remote latency measurement made.

### BUG-023 — P1 — Received Stop requires another successful network request

MainActivity previously fetched a fresh manifest before exiting playback or acknowledging Stop. A failing request left media playing and the command pending. Stop now uses cachedManifest on Dispatchers.IO, or an empty library when no valid cache exists; normal background refresh repopulates it. Interrupted playback is cleared and the command consumed. Two decision tests cover cache preservation and cache absence; both Android variants (78 each) and lint passed (`/tmp/lessoncue-audit-stop.log`). Failure dependency verified by source inspection, not a physical offline-TV test.

### BUG-024 — P2 — Canceled diagnostic capture skips bitmap cleanup

PixelCopy callback returned before recycling its bitmap when the coroutine was canceled. Extracted captureDiagnosticBitmap now recycles in callback finally, including canceled/failed copies; synchronous request failure also recycles and returns null. It deliberately does not recycle while PixelCopy may still be writing. Three instrumentation tests passed on Google TV Android 16, covering cancellation, successful JPEG, copy failure and synchronous failure (`/tmp/lessoncue-audit-screenshot-device-retry.log`). First install failed because the preloaded emulator app had a different signing key; test-runner cleanup removed it and retry passed. Read-only/no-snapshot emulator-5570 was stopped after validation; no saved AVD or physical TV changes. Both Android variants' 78 unit tests and lint passed (`/tmp/lessoncue-audit-screenshot-build.log`). Git diff whitespace check passed. No commit, push, release, or live-server mutation this pass.

### BUG-025 — P2 — Diagnostic screenshots suspend remote command processing

Source-confirmed: the control loop awaited the 2.5-second notice, PixelCopy, and an upload with 8-second connect/20-second read timeouts before applying even a command in the same response. Its notice also lacked cancellation cleanup. DiagnosticCaptureTask launches one identity-scoped child task without suspending polling, deduplicates handled requests, leaves a different busy request eligible next poll, consumes ordinary capture failures, and hides the notice in finally. Expiry is rechecked after the notice delay. Three coroutine tests exercise blocked capture, dedup/single-flight, failure recovery and cancellation cleanup. Both Android unit variants (81 tests each) and lint passed (`/tmp/lessoncue-audit-capture-task.log`); diff whitespace check passed. No physical latency measurement. Blocking network IO still observes cancellation only after IO returns/times out; JPEG compression remains on the callback thread, a separate performance lead. No commit/release/live-server change.
