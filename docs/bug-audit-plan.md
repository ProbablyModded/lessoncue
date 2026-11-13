# LessonCue comprehensive bug review

Prepared 2026-09-14. This is the execution plan, not a completed audit.

## Start here

- Repository: `<local LessonCue checkout>`. The old Documents path is stale and exposed the repository to iCloud duplication.
- Resume state: `docs/bug-audit-checkpoint.md`. File coverage: `docs/bug-audit-coverage.tsv`.
- Inventory baseline: local commit `204d8ca4762e01dcd88764b0668659737b0fc424`, version 0.46.3. Published main/tag commit was `a9dff6c8ffacaf2030aa880aac31558e7df2cab9`. Reconcile these trees and current main before implementing fixes.
- Previous targeted evidence: `docs/reliability-review-2026-09-12.md`, including its September 13 continuation. Reuse valid evidence; do not mark the whole application reviewed on its basis.
- The completed v0.46.3 release workflow passed its gates, including 168 browser tests. That establishes a historical baseline, not correctness of every path.
- This planning pass changed only audit documents. No new product bug has been reproduced during this pass.

## Working within limited five-hour windows

Use **Medium reasoning by default**. Use **High** for narrowly scoped concurrency, authorization, migration/rollback, or native lifecycle investigations. Use Low for clerical follow-up only. Do not use maximum effort for the entire repository sweep. These are engineering recommendations, not promises about a particular account's quota or token savings.

Official reference: https://developers.openai.com/api/docs/guides/latest-model . Higher effort permits more reasoning; actual work and quota consumption vary with model, task, and context. Select the effort in the app; a resume prompt alone does not change it.

One session should finish one small investigation or coherent fix, even if that is only part of a batch. A five-hour allowance is not a commitment to spend five hours continuously. Work to available usage, leaving room for validation and a checkpoint; do not infer a numeric token budget from the reset interval.

1. Read the checkpoint and relevant batch only. Inspect git status and the diff since the checkpoint. Preserve concurrent Claude/user changes; use a separate worktree when needed.
2. Choose a concrete behavior and inspect its caller, state mutation, persistence, and consumer together. Start with approximately 3–8 related files; split large classes by symbols and record that boundary.
3. Form a falsifiable failure hypothesis. Trace the input and lifecycle; reproduce with the smallest fixture that exercises the actual contract.
4. Fix confirmed bugs locally, with focused regression coverage for consequential behavior. Keep speculative risks separate from demonstrated defects. Avoid broad rewrites and cosmetic cleanup.
5. Run the smallest relevant checks, inspect failures, and update coverage/finding status immediately. Record command, exit status, commit/tree state, and evidence location. Redact private data.
6. Save the checkpoint after each finding or completed slice, before any long test, and at session end. Leave one exact next action with required files and command. Summarize only changed findings and blockers to the user.

Avoid reprinting whole files, rediscovering architecture, loading full logs, or sending multiple agents across overlapping code. Batch independent searches. Save test logs locally and inspect the failure region. Do not repeatedly poll unchanged CI or keep rerunning an entire suite for one defect. Existing flaky tests need investigation, not retries until green or arbitrarily increased timeouts.

## Coverage and evidence rules

The initial ledger lists all 1,167 tracked files with their git blob IDs. Initial batch labels are routing hints and must be corrected when ownership is inspected. Tests belong to B14 for final coverage accounting but should be read alongside their feature in every batch. B03 spans the session, progression, and drawing sub-batches below.

At B00, classify files as production source, tests, configuration, scripts, documentation, assets, generated, vendor, or legacy. Every tracked file needs a disposition. Generated/vendor content can be excluded from line-by-line review with a reason and a check of its source, version, provenance, and integration. Never silently omit `app/`, `worker/`, `db/`, `drizzle/`, `tvos/`, Vega, or copied companion sources because they appear old or optional.

Allowed coverage statuses: `unreviewed`, `partial`, `reviewed`, `verified`, `blocked`, `excluded-with-reason`. Record symbol ranges for partial reviews. `reviewed` means inspected, not tested; `verified` requires recorded behavior checks. If the blob changes, mark the affected coverage stale and reopen it; also revisit unchanged callers whose contracts changed.

Give findings stable IDs (BUG-001, etc.), severity, evidence strength (reproduced / source-confirmed / hypothesis), affected version and path/symbol, trigger, impact, fix, regression check, and remaining limitations. Use P0 for catastrophic/security emergencies, P1 for broken core flows, data loss or authorization defects, P2 for material incorrect behavior, and P3 for minor defects. Keep hardware/environment blockers separate from application defects.

## Ordered review batches

### B00 — Baseline, inventory, and existing failures [Medium]

Reconcile local/main/release trees; map shipped entry points from builds and installers; classify the ledger and identify missing checks. Inspect repository instructions and check for duplicate suffix files without deleting ambiguous user data. Establish which prior fixes are actually present. Preserve the prior browser timeout/roster flake as an unresolved lead: v0.46.3 still needed a release-validation rerun after the wait increased to 30 seconds. Obtain failing traces if available, then distinguish test coordination, server behavior, and runner load. Do not label it solved merely because a rerun passed.

### B01 — On-site discovery, pairing, and reconnect [High for failure analysis]

Inspect LocalAddressService, HttpPortService, PairingCodeService, CloudflareTunnelService, Android discovery/address selection, Avahi configuration and network tests. Cases: A and AAAA answers; IPv6 unreachable while IPv4 works; link-local scope IDs; DHCP/IP change; stale pairing; DNS delay; Wi-Fi isolation; mDNS across subnets; captive portals; HTTPS redirects and Cloudflare Access responses. Verify bounded attempts, useful failure messages, and recovery after server restart. Verify the IPv6 toggle changes local Avahi discovery only, preserves tunnel service, and rolls back failed updates.

Reinspect the supplied troubleshooting log if accessible. Compare the working television with each failing television; collect TV model/app version and observed endpoint errors if missing. Keep physical-site diagnosis open until evidence exists. Remote Cloudflare login previously lacked authentication: record that as a blocker, never as a verified connection. Continue local work without changing live network settings.

### B02 — Game joining and shared session lifetime [High; coverage B03]

Trace ActivitySessionService, ActivityApi/Hub, code pools, join address service and phone identity through the database. Exercise lost accepted replies, duplicate/concurrent joins, shared-device switching, late joins, server restart, cross-lesson isolation, old-run reads, reset/revocation, lock/unlock, exhausted short codes and unavailable shortener. Verify current expiry behavior at the two-hour boundary and whether it is inactivity-based; flag any mismatch with intended behavior. Check code release and database uniqueness across old and new lobbies. Ensure public player joins and authorized remotes do not require administrator login.

### B03 — Progression, engine rules, scores, and drawing [High for state transitions]

Enumerate current engines and presets from the catalog rather than relying on historic counts. Build a matrix of supported phases/actions and host/participant/stage projections. Cover manual and automatic progression, timed transitions, hold/resume, all-answered rules, reconnects, duplicate actions, simultaneous final answer/timer expiry, cancellation, restart, empty lobbies, absent/locked players, team scoring, ties, streaks, repeated question IDs and hidden-answer leakage. Establish exactly-once scoring and deterministic transition ownership under locks.

Give drawing/telephone a separate session: touch coordinates after resize/orientation, dots and long strokes, bounded payloads, malformed input, preserve-on-failed-send, duplicate retry, moderation, no self-voting, late join, missing players, attribution and draw/describe/redraw progression. Test meaningful user results; catalog rendering alone is insufficient.

### B04 — Phone remotes and push/fallback control [High]

Trace controller grants, controller sessions, SyncHub, command persistence/acknowledgment and remote UI. Check per-room permissions, universal PIN/grant expiry, stale or out-of-order commands, double taps, reconnecting SignalR, fallback overlap, lost ACKs, sleep/foreground recovery and room changes. Ensure disabled/locked controls cannot submit. Verify one poll/subscription lifetime, bounded retry, and command receipt-to-display latency with request counts at idle and under a class-sized burst.

### B05 — Browser display lifecycle and playback [High for cleanup races]

Inspect WebPlayer, manifest/media handling, timers, HLS, audio, overlays and subscriptions. Extend existing heartbeat tests only where gaps exist. Cover late success after cleanup, cue/identity changes, video end/error, pause/resume/seek, missing media, failed preload, stream disconnect and server outage. Measure live timers, requests, media resources and memory trend through repeated transitions. Verify no overlapping stale loops or background work after unmount.

### B06 — Android TV reliability and resource use [High]

Inspect coroutines/scopes, Compose effects, main-thread IO, OkHttp calls, discovery, caching/downloads, ExoPlayer ownership, AndroidView rebinding, WebView disposal, update handling and activity lifecycle. Test TV boot/resume, offline/cached playback, source replacement, background/foreground and long cue/signage rotations. Compare never-assigned-signage, currently-active-signage, and previously-active-signage cases. Measure idle CPU/network and heap/player/WebView/thread counts over repeated cycles; distinguish normal cache warm-up from retained growth. Use emulator tests where useful and physical-TV profiling when available. Compilation/unit tests alone cannot close the four failing TVs or establish memory stability.

### B07 — Accounts, authentication, and access boundaries [High]

Trace roles across AdminApi, Program routing/middleware, MFA, credentials/recovery, cookies, SignalR and room/participant grants. Verify per-user optional MFA and global policy/enrollment, rate limiting, session revocation, password/PIN handling, object ownership, CSRF where relevant and unsafe HTML/URL handling. Exercise denied requests alongside valid requests; changing UI visibility is not an authorization test. Keep PIN/token values and account data out of evidence files.

### B08 — Uploads, storage, processing, and live media [High for races/isolation]

Inspect upload reservations, quota accounting, resumable chunks, interrupted/duplicate finalization, simultaneous uploads, cancellation, disk full, malformed/traversing paths, content inspection, external converter sandboxing, worker timeouts, hardware/software fallback, cache eviction, streaming/range requests and cleanup. Verify accounting/persistence on success and every failure path. Use temporary storage and fixtures; exercise large-file boundaries without filling real disks.

### B09 — Signage, schedules, audience, and lessons [Medium; High for timing]

Review SignageStudio/SimpleSignage, widgets, manifest selection, lesson scheduling, audience interactions and their APIs. Cover DST/time zones, overnight/repeating schedules, overlaps, no schedule, missing assets, lesson/signage precedence, room switching, stale widgets and protected credentials. Verify off-screen/disabled features stop unnecessary work. Test real transitions, layout overflow and focus behavior on narrow/mobile and TV surfaces.

### B10 — Database, backup, restore, retention [High]

Trace EF schema upgrades, transactions/concurrency, retention and recycle-bin references, encryption, scheduled backups, WebDAV transfer, restore and migration. Use disposable copies to test interrupted migration/restore, corrupt archives, wrong passwords, disk full, partial uploads, path traversal, retention limits and secret handling. Verify restoration is usable and failure preserves the previous valid state. Never test destructive recovery against classroom data.

### B11 — Shortener and Link Studio [Medium; High for credentials]

Review Shortener services, reserved codes, health gating, install/update scripts, compose and the shipped shortener-companion copy. Compare upstream `<upstream Link-Shortener checkout>` only if present and relevant to the shipped source. Check real Shlink API contracts in an isolated stack where Docker is available. Cover unhealthy startup, restart/persistence, key validation, password-reset file ownership/atomicity, both account roles, favicon cache/ICO endpoint, charts and responsive layout. Verify direct short-domain routing and safe fallback; do not resurrect the deleted reverse proxy, hard-code domains, or edit the live reserved-code list.

### B12 — Installation, update, release, and dependencies [High for rollback]

Review Linux/Windows installer and updater transactions, permissions/services, port/network changes, package contents, shortener replacement, signed manifests and verification-before-execution, rollback after partial failure, and supported update paths. Verify release version metadata, Android versionCode, TV-only track, documentation/Pages publishing and ordering of changelog extraction. Review dependency/configuration scans and environment approval behavior. Use sandbox/CI update fixtures; never bypass failing gates or silently replace published artifacts.

### B13 — Vega, tvOS, and other client compatibility [Medium]

Establish what is shipped versus experimental. Review address policy, pairing/authentication, payload versions, persistence, reconnect, playback/resource cleanup, navigation/focus and release/package assumptions. Run supported SDK checks when available. Record unsupported/missing hardware explicitly; do not infer native parity from Chromium tests. Review shared protocol compatibility against older supported Android/browser clients too.

### B14 — Tests, contracts, fault injection, and integrated validation [Medium/High]

Review fixtures, cached test authentication, mocks, clock dependencies, assertion strength and test data isolation alongside feature batches. Close untested error/race paths instead of duplicating happy-path assertions. Integrate a teacher-to-remote-to-TV-to-phones scenario including drawing/voting, network loss, restore/reconnect and a full lesson. Exercise concurrent classrooms independently. Record latency distributions and resource trends with environment/load details rather than claiming classroom performance from local timings.

### B15 — Remaining UI, APIs, and unassigned production code [Medium]

Reconcile the ledger against actual entry points; review every remaining server route, admin view, form/save path, data model, utility, error boundary and legacy root. Include settings (save feedback and secret placeholders), lessons/playlists/templates, media editing, accessibility, mobile layouts, retention exports and account self-service. Inspect stale closures, unsaved-state loss, async response ordering, null/JSON handling, pagination and timezone conversions. No file is covered just because its enclosing directory was visited.

### B16 — Documentation/assets/configuration and closure [Medium]

Review remaining top-level configs, build entry points, protocol schemas, fixtures/examples, fonts/assets/licenses, privacy policy and public docs for correspondence to shipped behavior. Reconcile all ledger rows, remaining findings, changed blobs, unsupported environments and security reports. Summarize what is verified and what still needs on-site/hardware evidence.

## Validation policy

- Fast frontend checks: `npm run typecheck:admin`, `npm run lint`, `npm run test:units`; use relevant contract/network/shortener checks when touched.
- Focused server check: `dotnet test server/LessonCue.Server.Tests/LessonCue.Server.Tests.csproj -c Release --filter <relevant-filter>`; full suite at integration checkpoints.
- Browser: run `npm run build:admin` after web changes BEFORE Playwright because it serves prebuilt wwwroot. Use `npx playwright test tests/browser/<relevant>.spec.ts`. Leave `PLAYWRIGHT_BASE_URL` unset for local fixtures; never aim mutating tests at the live site. Use a separate dedicated test data directory when runs could overlap.
- Android: follow checked-in CI Gradle tasks for sideload/store unit tests, lint and builds; add actual emulator/device scenarios for lifecycle claims. Vega: its `typecheck`, `lint`, `test:units` where supported. Determine tvOS checks from its project/docs.
- Installer/media-worker/shortener integration: reuse checked-in scripts and required isolated Linux/Docker environments; report missing runtime explicitly.
- Final integration: full server and browser suites, frontend checks, protocol/network/shortener contracts, bundle budget, supported native builds and dependency/container scans. Run once per coherent final revision; repeat affected checks after any subsequent fix.
- A batch closes only when coverage is explicit and its findings are verified or documented as unresolved. The audit cannot promise zero defects or label untested hardware working.

## Completion and publishing

Deliver a coverage report, prioritized findings with fixes and evidence, reproducible remaining failures, hardware/site checks and a concise release candidate summary. Keep changes local/reviewable during this audit. The preceding v0.46.3 release is complete; this plan does not initiate another release or alter the live server. Publish a future audit release when requested.

## Resume prompt

> Resume the LessonCue bug audit in <local LessonCue checkout>. Read docs/bug-audit-checkpoint.md and the next relevant section of docs/bug-audit-plan.md. Check changed files against docs/bug-audit-coverage.tsv. Continue the next incomplete slice, reproduce and fix confirmed bugs, run focused checks, and update the checkpoint and coverage before stopping. Conserve tokens; do not restart the entire review or release changes. Tell me if the next specific investigation needs High reasoning.
