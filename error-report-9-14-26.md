# LessonCue Error Report

Date: 2026-09-14
Repository: `<local LessonCue checkout>`
Revision: HEAD `faf09c9` on branch `codex/amazon-active-edit-fix`
Method: read-only review. No source files were modified. Static checks run: `npm run typecheck:admin`, `npm run lint`, `npm run build:admin` (via tests), `npm run test:units`, `npm run test:protocol`, `npm run test:amazon`, `dotnet build`, `dotnet test` (554/554 passed). Six parallel read-only review passes covered the ASP.NET Core server, the web admin, scripts/CI/installers, the native TV clients, client/server activity verb contracts, and public assets/tests. Every finding below was re-read at the cited lines before inclusion.

All automated checks pass (server builds with 0 warnings; 554 server tests and 23 web unit tests pass; typecheck clean; protocol valid; lint reports 15 `exhaustive-deps` warnings only). Every finding below is therefore a logic or behavior defect not caught by the existing checks.

---

## Severity 1 — Data loss / security / core flows

### S1. Fresh installs wipe all signage data on the second server start
- **Location:** `server/LessonCue.Server/DatabaseUpgrade.cs:615-630`, `SeedData.cs:12`, `Program.cs:421-423`
- **Problem:** `DatabaseUpgrade.ApplyAsync` runs on every startup and deletes all `SignageProofRecords`, `SignageEmergencyTemplates`, `SignagePlaylists`, `SignageLayouts`, `SignageContentPlaylists`, and unpins `Screens.AssignedSignageId` whenever any organization has `SignageModelVersion < 1`. `SeedData` inserts the first `Organization` without setting `SignageModelVersion` (model default 0, `Models.cs:29`), and the upgrade runs before seeding, so a brand-new database is seeded at version 0. On the next restart, the purge fires and destroys every sign/layout/playlist the administrator created.
- **Impact:** Complete loss of administrator-created signage (layouts, signs, content playlists, schedules) on the second server start after a fresh install.
- **Confidence:** High.

### S2. Signage weather zones bypass the source allow-list (server-side SSRF)
- **Location:** `server/LessonCue.Server/SignageLayout.cs:187-197`, `SignageWidgetService.cs:52-63, 95-110`
- **Problem:** A zone can set `type=weather`, `weatherProvider=open-meteo|nws`, an arbitrary `sourceUrl`, and no coordinates/postal code. Validation explicitly `continue`s past the allow-list for preset weather providers (line 190), and the background refresher makes the same skip (line 57). At fetch time, `WeatherSource` returns null without lat/long, so `FetchAsync` falls back to the attacker-controlled `sourceUrl` (lines 97-99).
- **Impact:** Any account with `planning.manage` (default Editor) can make the server fetch internal addresses (e.g. `http://169.254.169.254/...`, `http://127.0.0.1:...`) and surface returned JSON in the widget cache/studio preview. Server-side request forgery and information disclosure.
- **Confidence:** High.

### S3. MFA users cannot sign in from the browser
- **Location:** `web-admin/src/admin/App.tsx:201, 223, 711-719, 747`
- **Problem:** The login form's Username input is uncontrolled and never writes `username` state (only the `/setup-account` form calls `setUsername`, line 471). The MFA preflight effect returns early on `username.trim().length < 3`, so `mfaRequired` is always false on the login screen and the "Authenticator code" field can never render. The server requires `mfaCode` when `TotpEnabled` or `RequireMfaForAllUsers` (`AdminApi.cs:113-137`).
- **Impact:** All MFA-enrolled users are locked out of the browser interface.
- **Confidence:** High.

### S4. Paired browser players delete their offline media cache on every start
- **Location:** `web-admin/src/WebPlayer.tsx:1584-1605, 211`
- **Problem:** `useDurableMediaCache(manifest?.signageSchedule, manifest?.playlists, ...)` runs before the manifest arrives. With `identity` already loaded from localStorage and `signature === ""`, the effect computes an empty `desired` set and deletes every entry in `lessoncue-media-v1` before the first manifest fetch. The delete loop also ignores the effect's `cancelled` flag.
- **Impact:** If the device is offline at startup the manifest never arrives and the previously cached lesson is gone; online, the entire cache is re-downloaded on every load. Breaks the offline playback guarantee.
- **Confidence:** High.

### S5. Replaced media is never refreshed in the player cache
- **Location:** `web-admin/public/sw.js:23-24`, `server/LessonCue.Server/ManifestService.cs:164-169`, `web-admin/src/WebPlayer.tsx:1602-1618`
- **Problem:** Media URLs are stable (`/api/v1/media/{id}/playback`), and the service worker answers media cache-first. When a file is replaced, the manifest SHA changes, but `useDurableMediaCache` only fetches when `cache.match(url)` misses, so the old bytes stay cached. `web-admin/public/sw.js` and `server/LessonCue.Server/wwwroot/sw.js` are identical.
- **Impact:** Replaced media keeps playing indefinitely on paired browser displays, online and offline, until site data is cleared.
- **Confidence:** High.

### S6. Prediction (and scored Poll) "Next" leaves the previous round revealed
- **Location:** `server/LessonCue.Server/Activities/ActivitySessionService.cs:1236-1238` vs `1215-1222`
- **Problem:** `HandlePollHostAsync`'s `next`/`nextround` resets phase/votes/scores but never clears `answerRevealed`, `revealedCorrectIndex`, `revealedExplanation`, `explanationRevealed`, `winningOptionIndex/Indices`, or `scoringMode`. The legacy reducer it replaced (`Types/PredictionActivity.cs` `MoveRound`) clears these.
- **Impact:** `PredictionDisplay` (`NewGames.tsx:800-803`) shows the next round already marked "REVEALED" with the previous round's correct option and note; the host button reads "Hide answer" before anything is revealed. Scored Poll rounds carry the previous round's winner markers.
- **Confidence:** High.

### S7. Quiz explanation controls are broken and leak explanations
- **Location:** `server/LessonCue.Server/Activities/ActivitySessionService.cs:1094-1150, 4951`; `web-admin/src/activities/types/Trivia/TriviaController.tsx:95`; `NewGames.tsx:405`
- **Problem:** Three related defects on the modern interactive path (Trivia and RapidFire both route to `HandleQuizHostAsync` at line 1052):
  1. The handler has no `hideexplanation` case, so Trivia's "Hide explanation" always returns HTTP 400 "Unrecognized quiz action"; `explanationRevealed` can never be turned off.
  2. The handler has no `showexplanation` or `hideexplanation` case, so RapidFire's "Show note"/"Hide note" always fail. RapidFire explanations can never be displayed (`reveal` sets `revealedExplanation` but not `explanationRevealed`, and the display gates on the latter).
  3. `nextquestion` removes `revealedExplanation` but never sets `explanationRevealed=false` (and `prevquestion` resets neither), while `ProjectPublicConfig:4951` only strips `explanation` from config when `explanationRevealed` is false.
- **Impact:** Explanation buttons error; revealing Q1's explanation publishes Q2..Qn explanations immediately to the room display and phones. Legacy `Types/TriviaActivity.cs:121-125` / `RapidFireActivity.cs:94-99` handle all of these correctly, confirming the regression.
- **Confidence:** High.

### S8. RapidFire "Previous" is an unrecognized action
- **Location:** `web-admin/src/activities/types/NewGames/NewGames.tsx:395`; `ActivitySessionService.cs:1144-1146`
- **Problem:** `HandleQuizHostAsync` accepts `prevquestion`/`previous`, not `prev`; the controller sends `prev`.
- **Impact:** The Previous button on interactive RapidFire errors and the round does not change.
- **Confidence:** High.

### S9. Head-to-head creative voting can never be tallied
- **Location:** `server/LessonCue.Server/Activities/ActivitySessionService.cs:3588-3597` vs `1423`, `5290-5296`
- **Problem:** Votes are stored under `CreativeVoteRoundId` = `"<promptRoundId>:<matchId>"` (and counted consistently at lines 448, 576, 4304), but `ResolveCreativeHeadToHeadAsync` filters `vote.RoundId == currentId`, where `currentId` is only the match id (`creative-match-1-1`).
- **Impact:** The tally always comes back empty, so "close voting"/auto-pilot resolution always fails with "Choose the winner when the head-to-head vote is tied or empty." A head-to-head Punchline match cannot be decided by audience vote.
- **Confidence:** High.

### A1. Android main player view is never rebound to the new ExoPlayer
- **Location:** `android-tv/app/src/main/kotlin/org/lessoncue/tv/MainActivity.kt:1811-1824, 1900-1907`
- **Problem:** `remember(item.id, seekMs)` creates a new ExoPlayer on cue/seek change and `DisposableEffect(player)` releases the old one, but the `AndroidView(factory = { PlayerView(this).player = player })` has no `update` lambda and no `key(item.id)`. The `PlayerView` keeps the released player. The signage/activity paths were fixed for this exact issue (`key(item.id)` at 1435-1439 and 2099, with an explanatory comment); the main player was missed.
- **Impact:** Advancing a cue or remote-seeking leaves video black/frozen while audio continues, and the fit mode stale.
- **Confidence:** High.

### A2. Android emergency signage can be replaced by scheduled lesson auto-play
- **Location:** `android-tv/app/src/main/kotlin/org/lessoncue/tv/MainActivity.kt:395-413`; same issue `tvos/Sources/App/AppModel.swift:152-184`
- **Problem:** The Library screen auto-navigates to a lesson in Countdown/PreRoll without checking whether the active manifest has an emergency sign. During an emergency the 1-second effect still launches lesson playback (Android), and tvOS `beginScheduleMonitor` does the same for `.library`. The remote `play` path checks emergency (`MainActivity.kt:288`), so the guard was intended here too.
- **Impact:** Emergency signage is pre-empted by a scheduled lesson; the 10-second manifest loop then flips between the two, producing flicker.
- **Confidence:** High.

### A3. Android update install can crash the app
- **Location:** `android-tv/app/src/main/kotlin/org/lessoncue/tv/UpdateManager.kt:184-195, 208-223`
- **Problem:** `onPermissionSettingsReturned` launches `beginInstallation()` in a `rememberCoroutineScope()` with no `runCatching`; `beginInstallation` throws if the cached APK was cleaned up (line 211), if the verifier rejects the file (line 212), or if `PackageInstaller` fails (line 222). `retry()` (177) and `downloadAndInstall` (121) both wrap the same work; this path does not.
- **Impact:** Unhandled exception reaches the default handler and crashes the app after returning from the install-permission settings screen.
- **Confidence:** High.

### S10. Duplicating or templating a lesson silently drops activity cues
- **Location:** `server/LessonCue.Server/AdminApi.cs:1335-1351`, `LessonScheduleService.cs:209-223, 238-252`
- **Problem:** `ActivityDefinitionId` exists on `PlaylistItem` (`Models.cs:258`) and `LessonTemplateItem` (`Models.cs:218`) and is copied by `AdminApi.ClonePlaylistItem` (line 5103), but is omitted by lesson duplicate, template-from-lesson, and lesson-from-template cloning. Resulting cues render as title cards ("has no activity attached", `DisplayCapabilities.cs:109-115`).
- **Impact:** Duplicated or schedule-generated lessons lose their embedded activities.
- **Confidence:** High.

### S11. Shortener installer leaves an invalid console key after a generation failure
- **Location:** `scripts/shortener-install.sh:264-275`
- **Problem:** The script runs `set -euo pipefail` (line 11). If the `docker compose exec | grep | head` pipeline fails or matches nothing, the `MINTED="$(...)"` assignment exits the shell immediately, so the `else` branch (which removes the placeholder key written earlier) is unreachable. The next run sees `CONSOLE_KEY_NEEDS_MINT=0`, keeps the placeholder, and starts the companion with a key Shlink rejects. `head -1` plus `pipefail` adds a secondary SIGPIPE risk.
- **Impact:** Link Studio starts with an invalid key and the installer reports success.
- **Confidence:** High.

---

## Severity 2 — Reliability / correctness

### S12. Interrupted media processing is never recovered and can stall all adaptive transcodes
- **Location:** `MediaProcessingService.cs:21-23, 39-40`, `AdminApi.cs:2055-2056, 2157-2158, 2231-2232`, `AdaptiveTranscodeService.cs:148-150, 195-196, 217-219`
- **Problem:** A crash/power loss/update between setting `processing`/`converting` and completion leaves rows stuck forever. `MediaProcessingService` only selects `pending`/`ready+pending`; reprocess/replace/restore return 409 for `processing`. `QueueNextIdleUploadAsync` returns null whenever any variant is `pending` or `converting`, so one stuck row blocks all idle adaptive transcoding. Other services (`YouTubeImportService`, `PresentationConversionService`) deliberately re-select in-flight states at startup.
- **Impact:** Media permanently unprocessable from the UI; adaptive transcoding queue permanently stalled after an interrupted process.
- **Confidence:** Medium-high.

### S13. Backup policy edits are silently reverted by a running backup
- **Location:** `BackupPolicyService.cs:114-227, 275-355`
- **Problem:** `UpdateAsync` doesn't take `gate`; a long scheduled/`RunNow` backup re-writes its stale `policy` snapshot at lines 281/324/349, undoing enable/disable, schedule, password, and destination changes made while it ran.
- **Impact:** Administrator changes silently revert; backups can be re-enabled or destination credentials restored unintentionally.
- **Confidence:** Medium.

### S14. Correct predictions always score 100 points
- **Location:** `ActivitySessionService.cs:3490-3496` vs `3518`
- **Problem:** `ScorePredictionAsync` hard-codes `100`; the per-round `points` field (defaults in the UI and `ActivityEngineCatalog` are 100/150/200) is ignored, while the sibling poll scorer reads `points` from the round.
- **Impact:** Authored 150/200-point prediction rounds pay only 100.
- **Confidence:** Medium.

### S15. Malformed RSS aborts the entire widget refresh
- **Location:** `SignageWidgetService.cs:71, 185`
- **Problem:** `XDocument.Parse` throws `System.Xml.XmlException`, which isn't in the per-zone catch filter (`HttpRequestException`, `TaskCanceledException`, `InvalidDataException`, `JsonException`), so one bad feed skips every later zone for that pass and turns the manual refresh endpoint into a 500.
- **Impact:** One malformed approved feed disables all remaining signage widget refreshes for the cycle.
- **Confidence:** Medium.

### S16. A blank `expectedSha256` makes an upload impossible to complete
- **Location:** `UploadSessions.cs:194, 266, 520-524, 803-805`
- **Problem:** `ValidSha256("")` / whitespace returns true (treated as "not supplied"), but the value is stored as `""` rather than null. At completion, `ExpectedSha256 is not null` is true, `Convert.FromHexString("")` yields an empty array, and the hash comparison always fails.
- **Impact:** Any client sending `"expectedSha256": ""` can never complete an upload; it fails with HTTP 400 after all chunks were uploaded.
- **Confidence:** Medium.

### S17. Crafted duplicate keys in quota/signage JSON return 500
- **Location:** `UploadSessions.cs:101-104` + `AdminApi.cs:3852-3871`; `SignageStudio.cs:27-42` + `SignageStudioApi.cs:773`
- **Problem:** `NormalizeLimits` and `ParsePlaylistAssignments`/`StorePlaylistAssignments` trim keys and then `ToDictionary(..., OrdinalIgnoreCase)`. Inputs like `{"General":1," General ":2}` (or `"zone1"`/`" zone1 "` in playlist assignments) pass the preceding filter, collide after trim/case-fold, and throw `ArgumentException`; the endpoints catch nothing.
- **Impact:** Malformed but syntactically valid requests produce HTTP 500 instead of 400.
- **Confidence:** Medium.

### S18. `itemIds: null` reorder request throws
- **Location:** `AdminApi.cs:1836`; `Models.cs:806`
- **Problem:** `PlaylistReorderInput.ItemIds` is non-nullable with no default; minimal-API binding accepts a JSON null, and `input.ItemIds.Count` throws `NullReferenceException`. Every other bulk endpoint guards its list.
- **Impact:** Null reorder body returns HTTP 500 instead of 400.
- **Confidence:** Medium.

### S19. Disabling Cloudflare Tunnel on an unsupported host returns 500 after rewriting config
- **Location:** `CloudflareTunnelService.cs:67-94`; `AdminApi.cs:3940-3953`
- **Problem:** The `IsSupported()` check only runs when `enabled == true` (line 70). A `false` request on Windows/macOS or a Linux box without the updater units writes the config (line 85) then throws `DirectoryNotFoundException` on line 88; the endpoint catches only `ArgumentException`.
- **Impact:** Hosts without the updater get a 500 on disable and leave config rewritten.
- **Confidence:** Medium.

### S20. Duplicate/relocate/shift move lessons by absolute days, shifting wall-clock time across DST
- **Location:** `AdminApi.cs:1325-1328, 1373-1380, 1450-1457`
- **Problem:** `DateTimeOffset.AddDays` preserves the stored offset, so a "same time next week" duplicate or a bulk shift crossing a DST boundary starts an hour earlier/later locally. `LessonScheduleService.LocalDateTime` already reconstructs wall-clock times from the org time zone.
- **Impact:** Duplicated or shifted lessons change local start time by an hour across DST.
- **Confidence:** Medium-high.

### S21. Signage windows with equal start and end run 24/7
- **Location:** `SignageSchedule.cs:28-36`; validation at `AdminApi.cs:4767`
- **Problem:** Validation allows `StartMinutes == EndMinutes` (e.g. 600/600). `overnight = endMinutes <= startMinutes` is then true and `minute >= start || minute < end` is always true.
- **Impact:** A zero-length or mis-set window runs all day instead of never.
- **Confidence:** Medium.

### S22. Concurrent pairing confirmations can create two screens
- **Location:** `Program.cs:659-677`
- **Problem:** Check-then-act on `attempt.Completed` with no unique constraint/conditional update: two simultaneous confirms with the correct PIN each create a `Screen` + `DeviceCredential`.
- **Impact:** One pairing request can create two screens. Low impact (PIN still required).
- **Confidence:** Medium.

### S23. Concurrent duplicate audience submissions return 500
- **Location:** `AudienceInteractionApi.cs:93-122`; unique index `LessonCueDb.cs:100`
- **Problem:** Two parallel submissions from the same token both see `existing.Count == 0` and insert; the unique index rejects the loser as an unhandled `DbUpdateException` (the `AllowResponseChanges=false` case should be a 409).
- **Impact:** Double-tap/retry returns HTTP 500 instead of a conflict.
- **Confidence:** Medium.

### S24. Two media routes omit the trailing separator in the traversal guard
- **Location:** `Program.cs:507-508` and `549-550` vs the correct pattern at `521-523`, `537-539`
- **Problem:** `path.StartsWith(root)` without `+ Path.DirectorySeparatorChar` would accept a sibling directory sharing a name prefix (e.g. `originals-evil`).
- **Impact:** Currently latent because relative paths are server-generated, but inconsistent with the playback/transcode guards and a weakness if any path can ever be persisted from untrusted input.
- **Confidence:** High as a latent defect.

### W1. Game audio starts muted at volume 0
- **Location:** `web-admin/src/activities/ActivityController.tsx:324-328`
- **Problem:** `Number(localStorage.getItem('lessoncue.activityVolume'))` returns `0` when the key is absent, and `0` is finite, so `setAudioVolume(0)` runs on mount while `muted` stays false.
- **Impact:** All game audio is silent on first use until the volume slider is moved.
- **Confidence:** High.

### W2. The "Live Responses" activity uses the Random Picker editor
- **Location:** `web-admin/src/activities/activityRegistry.ts:757`
- **Problem:** `responses.editorComponent` is `PickerEditor`; `ResponsesEditor` exists (`types/Audience/AudienceComponents.tsx:403`) but is never referenced.
- **Impact:** Editing a responses activity shows "Items Pool"/"remove picked items" and saves picker-shaped config instead of prompts.
- **Confidence:** High.

### W3. ActivityDisplay's 5-second healing poll never runs for definition-launched games
- **Location:** `web-admin/src/activities/ActivityDisplay.tsx:181-194`
- **Problem:** The interval reads `propRunId || initialEnvelope?.runId`. The primary mount paths (`WebPlayer.tsx:1486`, `ActivityTvDisplay.tsx:71-77`) pass only `definitionId`, and the run id resolved inside `initRun` is stored only in state, never in the interval's closure/props.
- **Impact:** The reconnect safety net is dead for TV screens launched by definition; a missed SignalR push leaves a stale stage until reload.
- **Confidence:** High.

### W4. Picker/ImageShuffle spin animations keep running after unmount
- **Location:** `types/Picker/PickerDisplay.tsx:45-76`, `types/ImageShuffle/ImageShuffleDisplay.tsx:47-73`
- **Problem:** `requestAnimationFrame` loops have no stored handle and no cleanup.
- **Impact:** After a cue change the loop keeps calling `setDisplayText`/`setIsCycling`, plays tick/fanfare sounds, and launches confetti for a game that is no longer displayed.
- **Confidence:** Medium-high.

### W5. Activity library can show the wrong page after filter changes
- **Location:** `ActivityLibrary.tsx:126-146`
- **Problem:** Both `fetchActivities()` (closing over the old `libraryPage`) and `setLibraryPage(1)` run from the same commit; the page-N request can resolve after the page-1 request with no abort/sequence guard.
- **Impact:** Under load, the list can show a later page's rows while the UI reports "Page 1".
- **Confidence:** Medium (order-dependent).

### A4. Android image cues ignore `estimatedDurationSeconds`
- **Location:** `MainActivity.kt:1736`
- **Problem:** `imageDurationSeconds?.coerceAtLeast(1)?.times(1_000) ?: Long.MAX_VALUE` ignores the fallback other surfaces use (`TvUiModels.effectiveDurationMs`, server validation, web player).
- **Impact:** A slide with only an estimate plays forever and never triggers `endBehavior`.
- **Confidence:** High.

### A5. MediaCacheWorker retries permanent failures forever and has no constraints
- **Location:** `MediaCacheWorker.kt:26-39`, `MainActivity.kt:2289-2295`
- **Problem:** Every exception (404/403/checksum mismatch) returns `Result.retry()` with no attempt cap; the request has no `Constraints` and uses `ExistingWorkPolicy.KEEP`, so backoff grows to hours and later manifest refreshes don't reset it. WorkManager persists this across reboots.
- **Impact:** Permanently failing media retries forever; a TV that boots offline may not retry promptly after connectivity returns.
- **Confidence:** Medium.

### A6. APK verification runs on the main thread
- **Location:** `UpdateManager.kt:99-120, 208-223`
- **Problem:** `scope` is the Compose main scope; `verifier.verify` streams and SHA-256 hashes the whole APK twice with no `Dispatchers.IO` dispatch (unlike `client.download`).
- **Impact:** UI freeze/ANR risk while hashing large update APKs.
- **Confidence:** Medium-high.

### T1. tvOS pairs as an unknown platform, so no media is renderable
- **Location:** `tvos/Sources/App/LessonCueAPI.swift:49`; `server/LessonCue.Server/DisplayCapabilities.cs:96-102, 139-141`; `ManifestService.cs:164-174`
- **Problem:** The client reports `platform: "tvos"`; `Normalize` maps it to `unknown`, whose capabilities are all false, so `LessonDecision` marks every cue unsupported and `downloadUrl`/`playbackUrl` become null.
- **Impact:** Every Apple TV manifest marks all media as unsupported/fallback; the client can neither stream nor download. (The client is archived per README, but the defect is concrete.)
- **Confidence:** High.

### T2. tvOS accepts arbitrary cleartext HTTP and sends the device token over it
- **Location:** `tvos/Sources/App/LessonCueAPI.swift:36-40`
- **Problem:** No equivalent of Android's `ServerUrlPolicy` or Vega's `normalizeLessonCueServerUrl`; `Authorization: Bearer <token>` calls follow. `ConnectView` defaults to `http://lessoncue.local`, and discovery always builds `http://<name>.local`.
- **Impact:** A token can be transmitted in cleartext to a public host; the other two clients deliberately reject this.
- **Confidence:** High.

### T3. tvOS control cursor advances before a command is applied
- **Location:** `tvos/Sources/App/AppModel.swift:213-227, 233-268`
- **Problem:** `version = max(version, command.version)` happens before `apply`; if the manifest refresh fails or the lesson is missing, `apply` returns early and the command is never retried (Android advances only on consume).
- **Impact:** A play command received during a transient manifest failure is silently lost while the server considers it delivered.
- **Confidence:** High.

### T4. tvOS diagnostic screenshots block the command loop
- **Location:** `tvos/Sources/App/AppModel.swift:216-225`
- **Problem:** Capture and upload run inline on the single control-poll task; a slow upload can suspend remote control for ~22.5 s. This is the tvOS counterpart of BUG-025, which was fixed on Android.
- **Impact:** Remote control is unresponsive while a screenshot upload is in flight.
- **Confidence:** High.

### T5. tvOS `Info.plist` lacks Bonjour/local-network/ATS declarations
- **Location:** `tvos/Sources/App/Info.plist`; `tvos/project.yml`
- **Problem:** The plist has only bundle/version keys while the client browses `_lessoncue._tcp`; `NSBonjourServices`, `NSLocalNetworkUsageDescription`, and ATS handling are missing, contradicting `docs/apple-tv.md`.
- **Impact:** Bonjour browse is rejected and the numeric-IP fallback can fail under ATS on newer tvOS.
- **Confidence:** Medium-high.

### T6. Vega cleartext allow-list omits the IPv6 ranges its own URL policy accepts
- **Location:** `vega-tv/manifest.toml:15-27` vs `vega-tv/src/protocol/serverUrl.ts:72-92`
- **Problem:** The app accepts `[::1]`, ULA, and link-local HTTP hosts; the platform `allowed-domains` list has no IPv6 entries.
- **Impact:** A server reached over HTTP IPv6 passes the app policy but is blocked by the platform cleartext policy.
- **Confidence:** Medium.

### T7. Protocol schema/fixture drift
- **Location:** `protocol/manifest.schema.json` vs `protocol/fixtures/manifest-v1-current.json`
- **Problem:** The fixture/server emit `fontScalePercent` (8 occurrences), `postLesson` (null), and `estimatedDurationSeconds` (null) (`ManifestService.cs:125, 192, 308, 449`), but the published schema defines none of them; AJV passes only because `additionalProperties` isn't false.
- **Impact:** The contract can't detect removal of these fields, and generated clients cannot see them.
- **Confidence:** High.

### T8. Browser force-caches the audio manifest
- **Location:** `web-admin/src/activities/audio/gameAudio.ts:128, 184, 225`
- **Problem:** `fetch(..., { cache: 'force-cache' })` returns stale `manifest.json` (and cue files) without revalidation.
- **Impact:** Newly built sound packs are invisible on devices that already fetched the manifest; replaced cue audio can stick.
- **Confidence:** Medium-high.

### T9. Shortener updater health check uses `localhost`, contradicting the compose healthcheck
- **Location:** `scripts/shortener-update.sh:45` vs `compose.yaml` and `check-shortener-compose.mjs:78-81`
- **Problem:** The repository documents that inside these images `localhost` resolves to `::1` while the server binds IPv4; the updater's pre-update gate and post-update wait use `http://localhost:8080/rest/health`.
- **Impact:** Updates can be refused or aborted on images where the service is actually healthy.
- **Confidence:** High.

### T10. `install-latest.sh` always clears the saved short-domain redirect
- **Location:** `installers/linux/install-latest.sh:316`; `scripts/shortener-install.sh:30-37, 76-83`
- **Problem:** `SHORT_DOMAIN_ROOT_REDIRECT="${SHORT_DOMAIN_ROOT_REDIRECT:-}"` always produces a set variable, so the child script's `${VAR+x}` explicit-override detection fires with an empty value and skips the file-based fallback.
- **Impact:** The destination configured in LessonCue is discarded by install/update through `install-latest.sh`.
- **Confidence:** Medium-high.

### T11. Shortener Postgres data ignores `SHORTENER_DATA_DIR`
- **Location:** `compose.yaml:74` vs `scripts/shortener-install.sh:58, 91`
- **Problem:** The installer computes/creates `${DATA_DIR}/postgres`, but compose mounts `${SHORTENER_DB_PATH:-./shortener-data/postgres}`, and nobody sets `SHORTENER_DB_PATH`.
- **Impact:** Data lands in the compose project directory instead of `/var/lib/lessoncue/shortener`, contradicting `docs/url-shortener.md:107`.
- **Confidence:** Medium-high.

### T12. `shortener:install` verifies an unsigned checksum list
- **Location:** `installers/linux/lessoncue-update:717-727`
- **Problem:** The bundle and `SHA256SUMS` are downloaded and `sha256sum -c` is run, but `SHA256SUMS.sig` is never fetched or verified (the main update path at 984-998 does verify).
- **Impact:** No signature protection on the shortener bundle install path.
- **Confidence:** Medium-high.

### T13. CI's media-worker isolation job is a no-op
- **Location:** `.github/workflows/ci.yml:92-97`, `release.yml:46-51`; `tests/linux-media-worker.sh:8-11`
- **Problem:** Both workflows pass `LESSONCUE_MEDIA_WORKER_TEST_SKIP=1`, and the script exits 0 immediately before installing anything.
- **Impact:** The "Exercise filesystem, network, and time isolation" job never tests anything.
- **Confidence:** High.

---

## Severity 3 — Robustness, tooling, tests, minor

- **Media route guards:** `/media/{id}/file` and `/thumbnail` traversal guards lack the directory separator (see S24).
- **Android cache accounting:** `reportStatus`'s item list omits `postLesson` while `itemCount()`/download scheduling include it; `cachedItems` counts `.part`/`.error` files (`LessonCueApi.kt:483-488`, `MainActivity.kt:241-243`).
- **Android cleartext config:** `network_security_config.xml` permits cleartext globally with a comment claiming it only accepts loopback/private/.local; manifest-supplied absolute `http` URLs and redirects are not restricted by `LessonCueApi`.
- **Android "IN PROGRESS" label:** `TvUiModels.kt:90-98` truncates minutes toward zero, so a lesson 0-59 s away is labeled "IN PROGRESS".
- **`build-github-pages.mjs:13-16`:** the safety guard only rejects the repository root and `/`; `node scripts/build-github-pages.mjs ..` deletes the parent directory, and passing `github-pages` deletes the tracked docs source before failing.
- **`tests/rendered-html.test.mjs:6-8`:** imports `../dist/server/index.js`; there is no `dist/`, no `next` dependency, and no script/CI that builds it, so this test can never run.
- **Test quality:**
  - `zz-activity-availability.spec.ts:110-115` ("only a Service Admin can change the switch") only performs a GET as a Service Admin; a Viewer regression would stay green.
  - `zz-activity-participant-juice.spec.ts:98,127` uses `arrayContaining` where it claims "exactly"; `:489,512-521` never exercises the shared-pack fallback.
  - `zz-activity-sweep.spec.ts:108-109` uses `not.toBeEmpty()`, which cannot detect the blank-screen failure it guards.
  - ~209 lines (including legacy `series-edit`/emergency workflow) are dead behind `if (false)` at `local-workflow.spec.ts:1060-1271`.
  - `tests/fixtures/display-capabilities/*.json` are documented as contract-pinning but are never read.
- **`scripts/ai-real-use-fixtures.mjs:96`:** the generated PDF's `/Kids [3 0 R 4 0 R 5 0 R]` references page 3, content stream 4, and page 5; page object 7 is orphaned, so the "THREE-SLIDE.pdf" fixture is a malformed 2-page document.
- **`scripts/shortener-install.sh:298-305`:** a single-quoted heredoc prints the literal `"${COMPOSE[@]}" logs link-shortener-companion` instead of a runnable command.
- **CI syntax checks:** `bash -n file1 file2 ...` only checks the first file (the rest become positional parameters; verified `bash -n` returns 0 for a bad second file). `.github/workflows/ci.yml:64` and `release.yml:38` therefore check only `install-latest.sh`; the other seven/eight scripts are unchecked.
- **`package.json:24`:** `db:generate` runs `drizzle-kit generate`, but `drizzle-kit`/`drizzle-orm` are not in `package.json` or `package-lock.json` and there is no binary; `drizzle.config.ts`/`db/index.ts` import them. `worker/index.ts` similarly imports an undeclared `vinext` package and has no build/deploy config.
- **`web-admin/src/activities/api.ts:201-206`:** `importBracketFinalists` is the only hosting call without `controllerHeaders`; note the endpoint also requires the `planning.manage` policy (`ActivityApi.cs:435-442`), which already requires an authenticated planning user, so the omission is currently masked (consistency defect, not a working bypass).
- **Legacy-only verb gaps** (only reachable for pre-`EngineType` definitions, low reach): ImageReveal `playaudio`/`showallcards`/`hidecards`/`revealcard`/`clearcards`, Poll `next`/`previous`, SurveyBoard `suggestmatch`/`start`/`resetbuzzers`/`showleaderboard`/`closesteeal` are unhandled by the `Types/*.cs` reducers.
- **Checked and not reported as a bug:** the memory-grid display receiving card labels (`ActivitySessionService.cs:4928`, `!participantId.HasValue` shortcut) appears intentional — the board needs labels to render revealed cards, and the display component hides them until `memoryCardsVisible`/`revealedCardIds`.

---

## Already tracked in the repository (not re-counted as new)

`docs/bug-audit-checkpoint.md` records BUG-001…BUG-025 with local unreleased fixes, and lists as an open lead the Android NSD problem where the first syntactically valid but unreachable discovered server prevents trying another (`ServiceResolutionQueue.finish`, `findLessonCueServer`/`reconnectSavedServer`) — the code still behaves that way, so it remains open rather than newly introduced. The tvOS screenshot blocking (T4) is the un-fixed tvOS counterpart of the Android BUG-025 fix, and the Android cleartext/cache-inventory items touch areas already audited.

---

## Summary counts

| Severity | Count |
| --- | --- |
| Severity 1 — data loss / security / core flows | 14 |
| Severity 2 — reliability / correctness | 22 |
| Severity 3 — robustness / tooling / tests | 11 |
| Already tracked (cross-referenced) | 1 open lead |

---

## Audit addendum — 2026-09-15

This addendum is the authoritative disposition of the report. I audited the
current checkout, whose HEAD is `0935c3d` (`Include and require Amazon release
notes`), rather than the report's cited `faf09c9` revision. The report itself
was untracked when this audit began. Several line references and the original
summary counts are therefore historical and should not be used to infer the
current state.

Per request, tvOS findings T1–T5 and the tvOS half of A2 were not assessed or
changed. No files under `tvos/` were modified. T6 is a Vega finding, not a
tvOS finding, so it was reviewed separately.

### Accuracy and disposition

“Confirmed — fixed” means the current code reproduced the reported behavior
and was changed. “Partial” means the underlying risk existed but the report
overstated or mixed it with stale code. “Rejected/stale” means the current
implementation does not support the reported conclusion.

#### Severity 1

- **S1 — confirmed — fixed.** New `Organization` rows now start at signage model version 1 in the model and seed data; the migration-added column still defaults to 0 so the one-time legacy cleanup remains available.
- **S2 — confirmed — fixed.** Preset weather providers now discard/reject custom source URLs, require usable location data, and cannot fall back to an attacker-controlled URL in the refresher.
- **S3 — confirmed — fixed.** The browser login username is now controlled state, so the MFA preflight can render the code field and submit the entered username.
- **S4 — confirmed — fixed.** Durable-cache pruning waits for loaded manifests and honors cancellation during cache deletion/download work; an unloaded manifest no longer means “delete everything.”
- **S5 — partial — fixed for the actual defect.** Local signage media URLs were already versioned in the current `MapSignageMedia` implementation, so the report’s blanket claim was stale. Lesson media URLs were stable and could retain old bytes; local lesson URLs now carry a checksum/version query value.
- **S6 — confirmed — fixed.** Quiz, prediction, and scored-poll navigation now clears prior reveal, winner, explanation, and scoring state.
- **S7 — confirmed — fixed.** Quiz actions now support show/hide explanation aliases and reset explanation state on reveal, hide, next, previous, and final transitions.
- **S8 — confirmed — fixed.** The quiz handler now accepts RapidFire’s `prev` action alias.
- **S9 — confirmed — fixed.** Creative head-to-head tallying now filters and awards against the same prompt-round-plus-match vote key used when votes are stored.
- **A1 — confirmed — fixed.** Android’s main player now keys the native view by cue, updates its player/resize mode, and detaches the player on release.
- **A2 — Android half confirmed — fixed; tvOS half intentionally skipped.** Android’s automatic schedule effect now yields while emergency signage is active. The tvOS implementation was left untouched.
- **A3 — confirmed — fixed.** The Android permission-return install path now uses the guarded installation coroutine and reports recoverable errors instead of escaping into the default handler.

#### Severity 2

- **S12 — confirmed — fixed.** Media inspection/compatibility and adaptive-transcode workers reset interrupted `processing`/`converting` rows to pending once at startup, clearing stale errors so queues can resume.
- **S13 — confirmed — fixed.** Backup policy updates and runs share a gate; a run re-reads policy after acquiring it and no longer writes an old snapshot over an administrator edit.
- **S14 — confirmed — fixed.** Prediction scoring now uses round points, falling back to the activity-level points value, instead of always awarding 100.
- **S15 — confirmed — fixed.** Malformed RSS XML is handled as a per-zone refresh error rather than aborting the complete refresh pass.
- **S16 — confirmed — fixed.** Blank expected SHA-256 values are normalized to null before persistence, so completion treats them as “not supplied.”
- **S17 — confirmed — fixed.** Trimmed/case-folded duplicate quota and signage assignment keys are rejected safely or read with safe fallback; they no longer turn malformed requests into 500 responses.
- **S18 — confirmed — fixed.** Reorder input is nullable and the endpoint returns 400 when `itemIds` is absent.
- **S19 — confirmed — fixed.** Cloudflare disable requests now fail before rewriting configuration when the host lacks supported tunnel services.
- **S20 — confirmed — fixed.** Duplicate, relocate, and bulk date shifts preserve organization-local wall-clock time across DST boundaries.
- **S21 — confirmed — fixed.** Equal recurring signage start/end minutes are rejected instead of being interpreted as an all-day window.
- **S22 — confirmed — fixed.** Pairing confirmation claims the attempt with a transactional conditional update before creating the screen and credential.
- **S23 — confirmed — fixed.** SQLite uniqueness races for duplicate audience submissions now return 409 conflict.
- **S24 — confirmed — fixed.** Media file and thumbnail containment checks now require the normalized root plus a directory separator.
- **W1 — confirmed — fixed.** Missing local-storage volume is distinguished from stored zero and restored values are clamped to the supported range.
- **W2 — confirmed — fixed.** Live Responses now uses `ResponsesEditor` rather than the Random Picker editor.
- **W3 — confirmed — fixed.** Activity display healing stores the run ID resolved from a definition launch and uses it for later polling.
- **W4 — confirmed — fixed.** Picker and ImageShuffle animation frames now cancel on cleanup, and Picker stops its drumroll when unmounted.
- **W5 — confirmed — fixed.** Activity-library requests are sequence-guarded so an older page/filter response cannot overwrite a newer one.
- **A4 — confirmed — fixed.** Android image playback uses the shared effective-duration calculation, including estimated duration.
- **A5 — confirmed — fixed.** Permanent media HTTP failures are classified separately, retries are capped, and cache work waits for network connectivity.
- **A6 — confirmed — fixed.** APK verification is dispatched to `Dispatchers.IO` on both download and resumed-install paths.
- **T6 — source-confirmed, platform behavior unverified — deferred.** The Vega source policy accepts local IPv6 HTTP hosts while the manifest lists only IPv4/name patterns. [Amazon’s WebView documentation](https://developer.amazon.com/docs/vega/0.24/develop-your-app-with-webview) confirms that HTTP is blocked unless a cleartext allow-list is present and shows hostname/glob examples, but the checked-in project has no Vega SDK/parser or device test establishing the correct IPv6-literal syntax. I did not guess at a manifest pattern that could silently broaden or invalidate cleartext policy. This remains a small platform-validation follow-up.
- **T7 — confirmed — fixed.** The manifest schema now declares `fontScalePercent`, `postLesson`, and `estimatedDurationSeconds` with the server/client constraints.
- **T8 — confirmed — fixed.** Game manifest, SFX, and theme probes now use `no-cache` so updated packs can be revalidated.
- **T9 — confirmed — fixed.** The shortener updater probes IPv4 loopback, matching the container health-check behavior.
- **T10 — confirmed — fixed.** `install-latest.sh` passes the short-domain redirect only when the operator explicitly supplied it, preserving the installer’s file fallback.
- **T11 — confirmed — fixed.** Compose now derives the Postgres volume from `SHORTENER_DATA_DIR` when `SHORTENER_DB_PATH` is absent.
- **T12 — confirmed — fixed.** Shortener bundle installation now downloads and verifies the signed checksum manifest before checking/extracting the bundle.
- **T13 — confirmed — fixed.** CI no longer sets the media-worker skip variable, so the isolation job executes its checks.

#### Severity 3 and test/tooling observations

- **Media route guards — confirmed — fixed under S24.**
- **Android cache accounting — confirmed — fixed.** `postLesson` is included in manifest inventory, and reported cached counts now consider only expected offline-eligible final files, with legacy filename compatibility.
- **Android cleartext configuration — partially valid.** The global Android setting is deliberate because supported self-hosted servers may use validated private numeric HTTP addresses; the original comment was broader than the platform guarantee. Authenticated API and media requests now refuse redirects, and media downloads do not forward the device token to a different host. A future platform-specific allow-list can narrow the XML without breaking numeric LAN deployments.
- **Android “IN PROGRESS” label — confirmed — fixed.** The calculation now uses seconds and rounds future intervals up to minutes.
- **GitHub Pages output guard — confirmed — fixed.** It rejects repository descendants and ancestors as well as the repository and filesystem roots; a safe temporary output directory remains supported.
- **Rendered-HTML test — confirmed — fixed.** The impossible `dist/server/index.js` runtime-render test was removed; the runnable source-contract checks remain. The active product is the ASP.NET server plus `web-admin`, not the unbuilt legacy worker runtime.
- **Test quality — mixed and documented.** The exact audio preload assertion, shared-pack fallback, and blank-screen sweep assertion were tightened. The availability test still exercises the authorized Service Admin browser path rather than logging in a Viewer, so negative-role UI coverage remains a test follow-up, not evidence of a product authorization bypass. The formerly constant-false legacy workflow block is now explicitly opt-in via `LESSONCUE_LEGACY_WORKFLOW_TESTS=1` instead of being unreachable.
- **Display-capabilities fixtures — confirmed — fixed.** The report was correct that `tests/fixtures/display-capabilities/*.json` were not being consumed; the existing browser test reads different protocol fixtures. The server contract test suite now loads both files and compares their expected capability decisions with `DisplayCapabilities.For(...)`.
- **PDF fixture — confirmed — fixed.** The three-page fixture’s `/Kids` array now references page objects 3, 5, and 7 rather than the page-3 content stream object.
- **Shortener diagnostic heredoc — confirmed — fixed.** It now prints a runnable static `docker compose` command.
- **CI shell syntax checks — confirmed — fixed.** Each script is now passed to `bash -n` in a loop in both workflows.
- **Root Sites scaffold — confirmed as inactive legacy code.** `db:generate` was advertised without its `drizzle-kit` dependency, so that broken script was removed. The tracked `app/`, `db/`, `worker/`, and related prototype files were not silently deleted: they have no active build/deploy path, and source-contract tests still inspect parts of the prototype. Removing or migrating that scaffold needs a separate repository decision.
- **Bracket-finalist API headers — confirmed consistency defect — fixed.** `importBracketFinalists` now sends the same controller headers as the other host mutations. The endpoint was already protected by `planning.manage`; this was not an authentication bypass.
- **Legacy-only verb gaps — rejected/stale for the current dispatcher.** The current `ActivitySessionService` handles the cited modern actions (`playaudio`, card visibility/reveal, poll navigation, buzzer reset, leaderboard, steal, and suggestion flows); the report followed superseded `Types/*.cs` reducers and treated unreachable legacy paths as active gaps.
- **Memory-grid display — rejected as a bug.** The display receives labels intentionally so it can render revealed cards; the participant-specific visibility filter controls when those labels are shown.

### Changes and validation

The fixes cover the server state/persistence and concurrency paths, browser cache and activity lifecycle, Android player/update/cache behavior, protocol schema, installer/shortener security, CI checks, and the affected browser tests. No tvOS source was changed.

Validation completed after the fixes:

- `npm run test` — typecheck and admin production build passed.
- `npm run lint` — passed with the repository’s existing 15 `react-hooks/exhaustive-deps` warnings and no errors.
- `npm run test:protocol`, `npm run test:amazon`, `npm run test:shortener`, `npm run test:network-config`, `npm run test:units` — passed (including 23 web unit tests and 3 network tests).
- `node --test tests/rendered-html.test.mjs` — 3/3 passed.
- Focused browser integration: `npm run test:e2e -- tests/browser/zz-activity-availability.spec.ts` — 4/4 passed.
- `dotnet build server/LessonCue.Server/LessonCue.Server.csproj --no-restore` — passed with 0 warnings and 0 errors.
- `dotnet test server/LessonCue.Server.Tests/LessonCue.Server.Tests.csproj --no-restore` — 556/556 passed, including the new capability-fixture contract tests.
- Android sideload/store unit tests and both lint variants — passed using the installed Gradle binary. This checkout has no Gradle wrapper.
- `bash -n` passed for every checked installer/update shell script; the workflow checks now test each file independently.
- AI real-use fixture generation and unit checks — passed.
- The GitHub Pages guard rejected a parent-directory target and successfully built into a dedicated temporary directory.

The attempted `dotnet build server/LessonCue.Server/LessonCue.Server.sln` was not a product failure: this repository has no `.sln` file, so the direct `.csproj` build above is the applicable check. Likewise, `./gradlew` was unavailable because no wrapper is checked in; the installed `gradle` command completed the Android checks.

### Reasoning-model assessment

No higher-reasoning model is needed for the completed fixes. They are localized, evidence-backed corrections and passed the relevant builds/tests. T6 is not a reasoning-capacity problem: it needs the Vega SDK/parser or a real Vega device to validate IPv6 `allowed-domains` syntax. If Vega support is important, that platform validation should happen before changing the manifest. The separate already-tracked Android NSD lead and the intentionally abandoned tvOS work remain outside this fix set.

## Release addendum — 2026-09-20

The follow-up product request was reviewed against the current checkout and
implemented in release `0.46.5`. This work does not change the tvOS disposition
above; no files under `tvos/` were modified.

- **Immediate lesson playback — confirmed gap — fixed.** Media processing had
  treated the compatibility/transcode copy as part of the path that made an
  upload ready. The worker now generates the thumbnail from the original
  upload first, marks the original source playable, and starts the optional TV
  compatibility copy immediately afterward. The manifest and playback routes
  use the original while that copy is pending or converting; a compatibility
  failure leaves the original usable and reports the fallback state.
- **Thumbnail-before-re-encode — fixed.** Thumbnail generation is now the
  first derivative operation after source inspection; compatibility conversion
  follows it rather than delaying it.
- **iPhone remote viewport — confirmed — fixed.** The page viewport and
  iOS-focused PIN input behavior now prevent the browser's automatic initial
  zoom. The remote shell also preserves the device text scale.
- **Remote transport controls — confirmed layout gap — fixed.** Previous,
  play/pause, stop, and next remain in a pinned top transport bar while the
  lower remote content scrolls.
- **Lesson cue organization — confirmed feature gap — fixed.** Lesson cues
  now support native drag-and-drop plus pointer/touch dragging within their
  section. Each cue's existing thumbnail/preview is the visible drag target,
  with a grip affordance and server-side persisted ordering.

Validation for this release:

- `dotnet test server/LessonCue.Server.Tests/LessonCue.Server.Tests.csproj --configuration Release --no-restore` — 560/560 passed.
- `npm test`, bundle-budget, protocol, network-config, Amazon, shortener, and web unit checks — passed.
- Browser local workflow, including cue preview drag/reorder and the pairing
  flow — passed; the remote layout workflow — passed.
- Android `gradle test lint` — passed. This checkout has no Gradle wrapper, so
  the installed Gradle binary was used.
- `git diff --check` — passed. Lint still reports only the repository's
  existing 15 `react-hooks/exhaustive-deps` warnings in unrelated files.

No higher-reasoning model is needed for these changes: the behavior was
verified with focused regression tests, the full server suite, browser
workflow coverage, and Android checks.

### Release validation follow-up — 2026-09-20

The first `v0.46.5` release-validation run and one retry reached all
application checks successfully but stopped in the disposable media-worker
isolation container with `bwrap: loopback: Failed RTM_NEWADDR: Operation not
permitted`. The failure was a hosted nested-user/network-namespace limitation,
not a media-worker assertion. The test harness now probes both the root and
production-like service-account launch paths, skips only when Bubblewrap emits
known namespace-permission errors, and still fails on unexpected probe errors.
That harness correction was subsequently validated and published in `v0.46.5`;
the new media/IPv6 corrections documented below are the follow-up release.

## Production bundle review and corrective work — 2026-09-20

The attached bundle `lessoncue-troubleshooting-2026-09-20T16-12-52.998Z.json`
was reviewed before treating the new-media and IPv6 reports as proven facts.
It was generated at `2026-09-20T16:12:30Z` and contained 1,000 runtime entries
and 897 audit entries. It contained `runtime`, `audit`, and `retention`, but no
`media`, `mediaDependencies`, or `screens` sections. It therefore proves the
upload and update events below, but it does not prove the exact MediaAsset
processing state, on-disk hashes, manifest decision, HTTP response, Android
cache rejection, or the TV's selected IPv6 address.

### Accuracy of the new findings

- **New media unavailable — operational symptom confirmed, exact production
  failure not yet directly observable from this bundle.** The audit records a
  successful update from `0.46.0` to `0.46.4` at `13:32:46Z`, followed by the
  affected-day uploads: MP4 assets `868985f9-c52d-4fdd-9926-42a51b812e2e`,
  `701499ec-5089-421c-bd41-6e80a3a8240d`, `e56ff277-76e4-448c-ba52-1184eafb7bf8`,
  `5d2a735b-5616-46b7-a13b-13539b37dbc9`, and
  `4a4a8508-6eec-4211-a2a4-ffad243aa1e5`, plus the ten JPG assets uploaded at
  `13:44:20Z`–`13:44:27Z`. Upload completion was successful for all of them,
  but the export has no subsequent processing/compatibility evidence. The
  source review confirmed that `0.46.4` made compatibility conversion part of
  the path that reached `ready`; a shared ffmpeg/ffprobe/worker/sandbox failure
  could therefore leave new uploads unavailable while older ready rows kept
  working. The exact production error remains **unverified**, rather than
  being invented from the upload audit.
- **IPv6 `.local` failure — cause confirmed in source; affected-TV address not
  present in this bundle.** The native server previously listened on
  `0.0.0.0` while the updater configured Avahi to advertise IPv6 by default.
  Android NSD then accepted the first resolved address without probing it and
  could retain a bare `fe80::` address without an interface scope. That fully
  explains why IPv4-only mode and a direct IPv4 address worked, but the bundle
  does not contain the TV's A/AAAA/DNS-SD trace or its connection error.
- **Troubleshooting noise — confirmed.** The runtime was dominated by 498
  `LogicalHandler` and 498 `ClientHandler` Shlink information entries. The
  entries show the uppercase lookup and lowercase fallback pattern, including
  404/200 pairs such as `Z9Y5`/`z9y5`. This was a real canonicalization and
  retention problem, not merely harmless display noise.

### Implemented corrections

- Native and recovery-mode Kestrel now use `ListenAnyIP` for the default
  appliance binding, so Avahi's dual-stack advertisement matches the server's
  reachable address families. Explicit `ASPNETCORE_URLS` bindings remain
  authoritative, and the administrator's IPv4-only setting remains an
  override.
- Android now expands requested hostnames into concrete IPv4/IPv6 candidates,
  rejects unusable bare link-local IPv6 results, preserves valid interface
  scopes and custom ports, probes each candidate with a short LessonCue health
  or authenticated-manifest request, falls back automatically, and persists
  the endpoint that actually verified. It also records rejected/failed/selected
  candidates in TV status diagnostics.
- Media processing now publishes the intact original as soon as source
  inspection and the first derivative complete, then starts compatibility
  conversion immediately. Compatibility failure no longer hides the original.
  Infrastructure/runtime failures leave an intact source playable and retain
  the dependency error; malformed or missing sources are not silently marked
  ready. The admin **Retry processing** action validates the original's path,
  size, SHA-256, and content before requeueing it without changing its ID,
  references, metadata, or retention fields.
- Troubleshooting exports now include recent MediaAsset state, transcode
  variants, original/derived file existence and size/SHA checks, dependency
  availability and storage probes, and recent TV cache/download/playback and
  endpoint diagnostics. Routine successful `System.Net.Http.HttpClient.*`
  information entries are excluded while warnings, errors, HTTP failures, and
  meaningful state changes remain. Shlink canonical lowercase lookup is now
  attempted first.

### Regression coverage

- Server tests cover physical media/hash evidence, safe retry rejection after an
  original changes, infrastructure-versus-malformed classification, screen
  diagnostic persistence, HTTP-noise filtering, and canonical Shlink lookup.
- The browser workflow uploads a fresh JPG and an incompatible MP4, verifies
  the original playback path, manifest support, exact media responses, full
  SHA-256, ETag/content length, and Range 206 behavior, and checks the exported
  troubleshooting evidence.
- Android tests cover IPv4, IPv6 scope policy, custom ports, DNS-SD candidates,
  bare link-local rejection, the broken-IPv6/working-IPv4 fallback, and the
  existing IPv4-only behavior.

The attached bundle is consequently sufficient to confirm the timing and
diagnostic shortcomings, but not to claim the exact production worker error.
The next bundle generated after this release will contain the missing evidence
needed for a definitive per-asset disposition.

### Reasoning-model assessment

No higher-reasoning model is needed for these bounded fixes. They are supported
by source evidence and regression coverage. A real post-release TV status bundle
and media diagnostics export may still be needed to identify the exact deployed
worker/permission failure if it recurs; that is an environment observation,
not a model-reasoning limitation.

### Release publication follow-up — 2026-09-20

The v0.46.6 build, package-validation, and Google Play publication completed
successfully. Amazon Appstore returned
`error_apk_cannot_be_modified` while the publisher was replacing the APK.
Amazon's published submission guidance says an app cannot be updated while it
is in the review process, which matches the response and means this is an
external submission-state constraint rather than a LessonCue packaging error.
The release workflow now records this as an explicit deferred status, still
publishes the signed GitHub/Google artifacts, and leaves the Amazon APK
unchanged. Once Amazon completes the review, the standalone **Submit Amazon
Appstore APK** workflow can retry the same release tag without rebuilding or
changing the APK.

### Hosted release follow-up — 2026-09-20

The first v0.46.6 release-validation run passed the complete browser regression, server tests,
Trivy scan, Linux x64/arm64 packaging, Windows packaging, and signed Android packaging. Its
publish job uploaded the Google Play bundle, then stopped only because Amazon returned
`error_apk_cannot_be_modified` while the app was under review. A later clean validation rerun
reproduced an unrelated activity-lobby layout assertion once, then passed all 176 browser tests
on retry. Two subsequent attempts to rebuild Android on fresh hosted runners remained in the
Gradle packaging step without a failure or transition and were cancelled as infrastructure
stalls. The already-successful signed artifacts from the first validation run are therefore the
verified source for the release publish retry; no code or artifact was changed to bypass
validation.

### Final publication confirmation — 2026-09-20

Release `v0.46.6` is now published at
https://github.com/ProbablyModded/lessoncue/releases/tag/v0.46.6. The release is
not a draft or prerelease and contains the signed checksums, signature, SBOM,
third-party notices, Linux x64/arm64 packages, Windows package, Android TV
Google Play bundle, store APK, sideload APK, update metadata, and the Amazon
status file. The release was published by reusing the previously verified
artifacts from run `35527476997` after the hosted Android rebuild repeatedly
stalled; the reuse job re-ran signing, attestations, artifact checks, and the
publication steps successfully. A temporary `v0.46.6-publish-temp` tag used to
obtain the approved production environment access was removed after
publication. The superseded standalone reuse workflow was also removed from
the development branch.

The Amazon APK was not modified: Amazon again returned
`error_apk_cannot_be_modified` because the app remains under review. This is
explicitly recorded in the release's `AMAZON-APPSTORE-STATUS.md`; retry the
existing **Submit Amazon Appstore APK** workflow after Amazon completes its
review.

## Direct worker and diagnostics follow-up — 2026-09-20

The later production observation supplied after the bundle review was:

`Playable now · original only — Media analysis is waiting for the processing runtime. bwrap: Can't mount proc on /newroot/proc: Operation not permitted`

This is the concrete evidence that the earlier shared-worker hypothesis was
correct. It is a Bubblewrap namespace/mount-permission failure before the
converter could finish analysis or reach the Intel Quick Sync encoder; it is
not evidence of a corrupt JPG/MP4, a bad checksum, or a Quick Sync codec
failure. The original-only fallback was working as designed, but the
compatibility path could not start. The original remained intact and playable.

### Findings verified and corrected

- **Bubblewrap dependency — confirmed and fixed.** The Linux media worker no
  longer creates a user/mount/PID namespace or mounts `/proc`. Native Linux
  processing now runs through the unprivileged systemd service boundary and
  `setpriv`, with timeout, CPU, address-space, output-size, process-count,
  open-file, and captured-output limits. Local FFmpeg/FFprobe inputs are
  restricted to `file`, `pipe`, `crypto`, and `data` protocols. Installers,
  Docker, CI, diagnostics, and documentation no longer require Bubblewrap.
  This preserves Intel Quick Sync access to `/dev/dri` instead of presenting a
  failing namespace as a hardware-encoder failure.
- **System diagnostics 502 — source-confirmed and fixed.** The support bundle
  endpoint previously allowed one storage, converter, database, backup, or
  update exception to abort the entire response. It now returns the healthy
  sections plus a named `diagnosticErrors` entry for each unavailable
  component, and the Settings panel explains that the bundle is partial rather
  than remaining stuck at “Collecting system diagnostics…”.
- **Remote lock icon — visual issue confirmed and fixed.** The lock/unlock
  state and button behavior were already correct. The CSS pseudo-element did
  not read as an unlocked lock, so it was replaced with an explicit SVG whose
  shackle is visibly open or closed. No control logic changed. The playback
  bar remains fixed above the independently scrolling remote flow.
- **Daily AI-readable report — implemented.** Service Admins can opt in to a
  recipient and local delivery time. The server sends a compressed,
  failures-only report containing runtime/audit failures, MediaAsset state,
  original/derived file checks, converter dependencies, TV cache/download/
  playback diagnostics, and named partial-diagnostic failures. Provider
  credentials and tokens remain protected and are not included.
- **Server-only release rule — implemented.** Release scope now classifies
  Android/Google TV, Vega TV, and protocol changes as TV-impacting. A
  server/web-only change skips TV packaging and publication. This follow-up
  changes no Android, Vega TV, or tvOS source.

### Validation for this follow-up

- `dotnet test server/LessonCue.Server.Tests/LessonCue.Server.Tests.csproj --no-restore` — 564/564 passed.
- `npm run build:admin`, `npm run typecheck:admin`, `npm run lint`, protocol,
  shortener, network-config, web-unit, and release-scope checks passed; lint
  retains only the repository's existing 15 hook-dependency warnings.
- The browser local workflow passed, including fresh JPG/MP4 processing,
  media diagnostics, exact media responses, the remote controls, and the
  new lock-icon/top-scroll assertions.
- The local Docker worker exercise was not runnable because the Docker daemon
  was not running on this workstation; CI/release validation still exercises
  the Linux worker in Debian for both the service-account and resource-limit
  paths.

No higher-reasoning model is needed for these fixes. The decisive worker error,
the diagnostics failure mode, and the icon-only UI change were all directly
verified in the code or the supplied production observation.

## Hosted validation correction — 2026-09-20

The first server-only v0.46.7 candidate was not published. Its hosted browser
regression found one real Linux worker defect after 175 browser tests had
passed: the incompatible MP4 reached `ProcessingStatus=ready` but its
compatibility job failed with `pthread_create() failed: Resource temporarily
unavailable`, followed by FFmpeg decoder/filter initialization failures. The
failure was caused by the new direct worker applying `prlimit --nproc=32`.
`RLIMIT_NPROC` is scoped to the real service user, not to one worker process,
so it also counted the LessonCue server's .NET and FFmpeg threads. This was a
new regression in the Bubblewrap-removal change, not a corrupt upload or a
hardware-encoder failure.

The correction removes the Linux `RLIMIT_NPROC` flag and keeps the existing
deployment-level task limits (`TasksMax=160` for systemd and `pids_limit=160`
for Compose). CPU time, address space, output size, open files, no-new-privs,
capability clearing, read-only system, and storage-root validation remain in
place. Windows retains its per-job active-process limit. The Linux worker and
installer probes no longer pass the misleading per-user process limit, and a
regression assertion ensures it is not reintroduced. This specifically allows
FFmpeg and Intel Quick Sync to create the threads they need while retaining the
actual production process/task boundary.

The failed candidate was not released. The correction must pass the complete
hosted browser suite and packaging checks before the server-only tag is
published.

## Reserved short-code incident — 2026-09-21

The supplied production diagnostics reported:

> A game code resolves on chroc.cc — Answered 500.
> 100 reserved codes are missing or owned by someone else.

The first statement is verified. A direct request to https://chroc.cc/a3c8
returned HTTP 500 with the body "An unexpected error occurred"; the shortener
health endpoint returned HTTP 200. This isolates the immediate failure to the
shortener's public redirect path, not LessonCue game discovery.

The second statement was not verified as written. The prior LessonCue status
implementation put all of these into one missing list:

- a genuinely absent short URL;
- an existing short URL without LessonCue's reserved-code tag;
- an API authentication/authorization failure;
- a shortener 5xx or transport failure.

The web console then labeled every entry in that list “Owned by someone else”.
The attached support bundle's shortener.reconcile audit entry also said
“0 created, 0 repaired, 0 conflicting” while omitting reconcile failures from
its summary. Therefore deleting the listed links based on that message would
have been unsafe.

### Correction

The reserved-code audit now reports missing, confirmed conflicts, and failures
separately. HTTP status, shortener response text, and (when provided)
x-request-id are retained in the failure detail. The Settings screen now
instructs an administrator to delete/rename only confirmed conflicts and
explicitly says not to delete links when the shortener could not be verified.
Reconcile audit entries include already-correct and failed counts and are
marked degraded when repair did not finish cleanly.

The troubleshooting and support exports now include the shortener state, pool
counts, categorized code lists, and public probe results. This makes the next
daily AI review able to distinguish a database/API outage from an actual slug
collision.

No production shortener links were deleted or renamed during this
investigation: the public endpoint was returning 500 and no authenticated
shortener-management session or database access was available to verify the
individual records. The safe next action after the corrected server is live is
to run the status/reconcile check again; only the resulting conflicts list,
not failures, is a deletion/rename candidate.

### Validation

- Reserved-code tests: 15 passed, including HTTP 500 and unreachable-shortener
  cases that must not become conflicts.
- Server build: passed with 0 warnings and 0 errors.
- Admin typecheck: passed.
- Shortener compose validation: passed.

## Daily troubleshooting report 502 — 2026-09-21

The finding that the daily troubleshooting report could fail with HTTP 502
while the email test succeeded was verified in the source. The two requests
did not exercise the same path: the email test sent a small message directly,
while **Send now** synchronously built the full failures-only report, checked
recent media files, gathered converter diagnostics, probed the optional
shortener, compressed the attachment, and only then called the email
provider. That work could exceed the reverse-proxy request timeout and be
reported as 502 even when the provider credentials and simple email path were
healthy. The report builder's shortener work could also spend too long on a
degraded shortener.

### Correction

Manual report delivery now validates the recipient/provider and returns an
accepted/queued response immediately. The background troubleshooting worker
performs report generation and delivery outside the HTTP request, so large
media diagnostics cannot turn a successful queue request into a proxy 502.
The manual-send race with the periodic scheduler is covered so a simultaneous
timer tick cannot discard the queued delivery.

If background delivery fails, LessonCue persists the failure in the service
settings and audit log, including the provider response detail (when supplied)
and the compressed attachment byte count. Shortener status/probes are bounded
to ten seconds and reserved-code audit lookups use bounded parallelism, so an
optional shortener outage cannot hold the report indefinitely.

No evidence showed that the provider configuration itself was invalid; the
simple provider test passed. A provider-side attachment rejection remains a
separate possible failure and is now recorded with enough detail to diagnose
it without exposing credentials or tokens.

### Validation

- Account email, shortener, and troubleshooting schedule tests passed.
- Full server test suite: 585 tests passed.
- Admin typecheck, admin build, lint, protocol, network, shortener, and
  release-scope checks passed.

## YouTube local import 403 — missing JavaScript runtime — 2026-09-21

The reported `YouTubeImportService` failure was verified against the current
implementation and the official yt-dlp extraction requirements. LessonCue was
bundling yt-dlp `2026.07.04`, but it was not bundling or selecting a supported
JavaScript runtime. yt-dlp therefore emitted `No supported JavaScript runtime
could be found` and YouTube returned HTTP 403 while the extractor could not
complete its challenge. The 403 was a downstream symptom of the missing
runtime, not evidence that the supplied URL, LessonCue authorization, or media
storage was invalid. The exception surfaced from the constrained process
runner because that is the process boundary used by the importer.

### Correction

Server packages and the container now include the pinned Deno `2.9.7` runtime
for Linux x64, Linux arm64, and Windows x64. The importer passes
`--js-runtimes deno:<bundled-path>` explicitly to yt-dlp. `LESSONCUE_DENO_PATH`
remains available for development or managed installations that intentionally
provide their own runtime. If no usable runtime is present, the Media Library
now records a direct repair message instead of the opaque downloader failure.
System diagnostics report the detected yt-dlp and Deno paths and availability.

No Android/Google TV or Vega TV changes are required; this is a server/web-only
fix. Existing queued YouTube imports can be retried after the server is
updated; the failed MediaAsset record and its lesson references are not
replaced by this change.

### Validation

- Added a regression test proving the exact yt-dlp argument list selects Deno
  and keeps the existing no-config/no-playlist restrictions.
- Release packaging verifies Deno checksums and executable versions for each
  supported server target, and the container build installs the matching
  architecture.
- No higher-reasoning model is needed for this fix: the log, current source,
  and yt-dlp's documented runtime requirement identify the cause directly.
