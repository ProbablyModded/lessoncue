# Reliability review — 2026-09-12

Reviewed `main` at `d843be5` in `<local LessonCue checkout>`, plus the local fixes described below. No release, push, live-server modification, or native Android change was made. This is a targeted reliability review, not a guarantee that the entire application is defect-free.

## Activity and phone fixes in this working tree

- Reading an old run no longer changes the lesson's current game. Existing phones follow the current run without rescanning; responses to a previous game are refused. Resetting players revokes older runs' lobby access.
- Concurrent joins are serialized. A phone retains a cryptographically random join token across retries, including when the server accepts a join but its reply is lost. Join and answer requests have bounded waits.
- SignalR subscriptions are isolated by run, cleaned up synchronously, retried after failed initial connection/reconnection, and reconciled against a snapshot. Older snapshots cannot undo newer pushed revisions.
- Participant polling has one request in flight, coalesces bursts, aborts on cleanup, retries temporary failures, and refreshes on return to the foreground/network.
- Automatic progression decides and executes under the same run lock. Answers no longer extend countdowns; submitted text is not counted as a later vote; hold/resume works; one failed run cannot stop other classrooms.
- Game hosting on a phone accepts its existing room/session/universal-controller grant. Editing the library and controlling another lesson remain protected.
- Remote playback commands are ordered in the browser and assigned unique versions under a bounded server lock. Acknowledgments refresh automatically; frequent refreshes do not re-download the lesson library.
- Shared-lobby scoring distinguishes runs even when question IDs repeat. Team assignment works across games in the same lobby without crossing lesson boundaries.
- Drawing phones now have a moderated voting surface, exclude self-votes, display telephone inputs passed from another player, and advance through drawing/description/redrawing. Long strokes are sampled to the configured server limits; payload sizes are bounded; dots render; malformed payloads are rejected without a server exception. The canvas redraw and resize observer do not restart for every pointer move.
- Failed sends retain drawings and typed answers. Long titles/player names wrap within narrow phone viewports instead of expanding the layout viewport and breaking tap coordinates.

## Additional findings — identified on 2026-09-12

These were left unchanged during the original review. The 2026-09-13 continuation below records the subsequent fixes and validation.

### P1 — Browser display can accumulate heartbeat loops

Location: `web-admin/src/WebPlayer.tsx:459–509`; the control poll at `:412–452` has the same successful-response cleanup race.

The heartbeat effect restarts whenever `acknowledgedVersion` changes. Its cleanup sets `stopped` and clears the last timer, but does not abort the pending request. A successful response checks neither `stopped` nor a cancellation signal before scheduling the next heartbeat. An old request finishing after cleanup therefore starts a second persistent loop, carrying its old acknowledgment value. Further overlapping commands can add more loops and resource use. The control poll likewise can restart after identity change/unmount when a successful response arrives late.

**Reproduced locally:** intercept the first `/api/v1/tv/status` request and hold its successful response; deliver one `pause` command; wait for the new heartbeat request caused by that command's acknowledgment; release both responses. The browser then has two active ten-second heartbeat timers, not one. Probe output: `{"heartbeats":2,"controls":2,"activeTenSecondHeartbeatTimers":2}`. All endpoints in this probe used local mocked fixtures; no real screen was controlled.

Suggested fix: keep acknowledgment in a ref rather than restarting the heartbeat, abort requests on cleanup, check cancellation before applying responses or scheduling any timer, and add the delayed-response regression test. Also apply the cleanup guard to the command poll.

### P1 — Android signage video changes can leave the view attached to a released player

Locations: `android-tv/app/src/main/kotlin/org/lessoncue/tv/MainActivity.kt:1398–1419` (`SignageVideo`) and `:1423–1444` (`SignageBackdrop`).

Changing the video ID/source creates a new remembered ExoPlayer and releases the old player. However, `PlayerView.player` is assigned only inside the `AndroidView` factory: neither renderer has an `update` callback or a keyed view host. A presentation rotating from video A to video B in the same composition slot can therefore retain the view attached to A's released player while B runs without a rendering surface. Switching from a streamed URL to a cached URL has the same risk.

This is a source-confirmed lifecycle mismatch; it has **not** been reproduced on physical Android TV hardware. Android documents that the factory runs once and changed properties belong in `update`: [AndroidView reference](https://developer.android.com/reference/kotlin/androidx/compose/ui/viewinterop/AndroidView.composable).

Suggested fix: bind the current player and resize mode in `AndroidView.update`, detach the view on release, and keep player disposal scoped to the player lifetime, not a changing callback. Test a two-video signage rotation and a network-to-cache source switch on a TV/emulator.

### P2 — Android signage WebViews have no release cleanup and can reload redirected pages

Location: `android-tv/app/src/main/kotlin/org/lessoncue/tv/MainActivity.kt:1323–1332` (`SignageWebZone`).

Unlike the normal cue WebView, the signage WebView has no `stopLoading`/`destroy` disposal path. Pages that have been shown can retain browser resources after leaving the signage slot. The update callback also compares the current navigated URL against the configured source; redirects or intentional in-page navigation make these differ, so a later recomposition can reload the starting page even though the configured source never changed.

Suggested fix: add release cleanup, and track the last requested source separately from the browser's current URL. Validate repeated entry/exit and a redirecting signage URL. Physical-device resource retention remains unmeasured. A TV that has **never rendered** a signage web zone does not create these WebViews; this is not evidence that every signage renderer runs unconditionally.

## Validation

- Server: 548 tests passed.
- Frontend unit tests: 21 passed.
- Typecheck: passed.
- Lint: zero errors, 15 existing warnings outside the new code.
- Protocol and shortener contract checks: passed.
- Production build/bundle budget: passed; initial JavaScript approximately 283 KiB.
- General workflow, display conformance, settings layout, per-user MFA, and shortener browser checks: 32 passed.
- Broad activity browser rerun: 130 passed (9.3 minutes), after correcting the narrow-screen title overflow exposed by the initial 129/130 run. This includes the full catalog preset smoke test and 28-engine, three-surface sweep.
- Final rebuilt drawing/identity/join rerun: 15 passed (1.3 minutes), including a join accepted by the server whose reply is deliberately lost, retry without duplicate players, player switching, all seven narrow-screen drawing presets, and a failed telephone-description send followed by retry. These are a focused rerun of tests already included in the 130-test activity suite, not 15 additional distinct tests.

Browser checks include real Chromium touch events, seven drawing presets, moderation/voting/telephone transitions, offline recovery, signed-out remote hosting and scope rejection, concurrent duplicate joins, and a 30-player answer burst. They do not establish real classroom Wi-Fi performance or native-TV memory usage. The extra program-wide findings above were investigated but intentionally left separate from the activity fixes.

## Continuation — 2026-09-13

- Browser display control, manifest, and heartbeat requests now abort on cleanup and cannot schedule new timers after unmount or identity change. The heartbeat keeps one loop across command acknowledgments; a command wakes it promptly, or queues one send if a request is already in flight. Browser regression tests hold the first heartbeat response until a command is acknowledged, verify a single follow-up send carrying the new version, and verify that a late control response cannot restart polling after unpairing.
- Android signage video and backdrop views now rebind their `PlayerView` when the remembered player changes. A video availability callback changing across recompositions no longer releases its player. The views detach on release.
- Signage WebViews now track the last requested source separately from the navigated URL and stop/destroy themselves when their view is released. Redirects no longer cause repeated reloads on unrelated recompositions.
- Validation at review time: browser typecheck and lint passed (zero errors, 15 pre-existing warnings); production web build passed; 11 distinct focused browser tests passed; both sideload and store Android debug variants compiled and passed unit tests; `git diff --check` passed. No Android device was connected, so video rotation, source switching, and WebView resource use still need physical-TV confirmation. Release status is tracked separately in the version history.
