# Handoff to Codex

Standing notes for whoever picks this repository up next. It is written for
Codex specifically, because most of the recent history alternates between Codex
and Claude and the expensive part has been each one rediscovering what the other
already knew.

**Keep this file current.** Update it in the same commit as the work it
describes, the way `CHANGELOG.md` is updated. If a section here is wrong, that
is a bug in the file — fix it rather than working around it.

Last reviewed: 2026-11-05, against the `v0.46.42` release candidate.

---

## 0. `main` is not the release line — read this first

`github/main` is at **v0.46.4**. The branch `codex/amazon-release-notes-fix` is
at **v0.46.27**. Twenty-three releases, including everything shipped since
2026-09-14, are tagged on that branch and have never been merged back.

What happened: PR #126 merged the branch into `main` on 2026-09-14, and work
then continued on the same branch instead of a new one. `main` has exactly one
commit the branch does not have — that merge commit.

**This makes every open pull request ineffective.** They all target `main`, so
#130's eighteen bug fixes, if merged today, land on a branch that is not
shipping.

The fix is a clean merge: `git merge-tree` reports **zero conflicts** merging
the release branch into `main`. Someone with a view on repository conventions
should decide between merging it back, making it the default branch, or
retargeting the open PRs — but the present state should not be left alone,
because it silently discards work.

## 1. Where things stand

| Component | Version | Notes |
| --- | --- | --- |
| Server (`csproj`) | 0.46.42 | release candidate; source of truth for the tag |
| `package.json` | 0.46.42 | release candidate |
| `WebPlayer.tsx` `APP_VERSION` | 0.46.42 | release candidate |
| Android TV `versionName` | 0.46.16 | release candidate; version code 156 |
| Vega `manifest.toml` | 0.46.6 | parked, see §4 |

The version drift was a recurring defect: `APP_VERSION` was found twelve
releases stale at v0.46.0 and corrected, then drifted eleven releases again by
v0.46.27 — and that constant is what classroom screens report to the server.
Nothing checked it.

`npm run test:version` now does, and runs in the `web` CI job.
`scripts/check-version-consistency.mjs` treats the server `csproj` as the source
of truth and fails the build on a mismatch. It deliberately does **not** compare
against git tags: a pull request legitimately precedes its own tag, and a CI
checkout may have no tags at all.

## 2. Open pull requests

| PR | Branch | State |
| --- | --- | --- |
| #130 | `claude/games-activities-audit-z8fikj` | Games audit: 18 fixes with regression tests, 25 more documented. **Highest value open work.** |
| #109 | `vega-tv` | The Vega app **plus the offline lesson-media caching fix**. Green, unmerged. |
| #87 | `remove-stray-test-output` | Removes `run*.txt` committed by accident. |
| #117, #116, #114, #113, #112, #111, #127, #128, #129 | dependabot | routine |

Two things to know before touching these:

- **#109 is mis-bundled.** The offline media caching fix is wanted regardless of
  whether Vega ever ships, and it is currently gated behind a parked port. Split
  it out or merge the PR; do not close it as "Vega work".
- **#130 is clean against its base and conflicts with the release line.**
  Measured on 2026-09-28, not guessed: `git merge-tree` against `main` reports no
  conflicts, and against `codex/amazon-release-notes-fix` reports exactly two —

  ```
  server/LessonCue.Server.Tests/ActivitySessionServiceTests.cs
  web-admin/src/activities/ActivityDisplay.tsx
  ```

  `ActivitySessionService.cs` itself auto-merges, despite carrying most of the
  fixes. Two files is a small resolution, but it has to happen whichever
  direction §0 is settled in.

  Separately, its author notes the brief it was written from describes code on
  no branch (`OpeningRunId`, an offensive-name filter, a device-wide token
  fallback) with line numbers ~140 ahead — most likely a Codex working tree that
  was never pushed. **If you are holding that tree, push it**, or those fixes
  will be reapplied on top of work nobody can see.

## 3. Sound packs (changed 2026-09-28)

`web-admin/public/assets/games/` holds 29 packs — 28 engine folders plus
`shared` — each with 14 `.txt` placeholders, one per cue. The tree ships **no
audio**, and that is the supported default: every effect has a synthesized
fallback, and a missing theme means silence.

Resolution cascades **preset → engine → shared**, per cue, in
`resolveGameAudioChain`. `shared` is the pack to author first: it is the only one
consulted for every activity. An engine folder is an override, and only for the
cues it supplies — **a `.txt` is not an override, only a real `.mp3` is.**

Each placeholder now states which games reach it and which do not. Those
sections are generated, because they depend on the preset catalogue and on which
packs already ship an `.mp3`:

```
npm run audio:placeholders   # after adding an engine, a preset, or any .mp3
npm run audio:manifest       # after adding or removing any .mp3
```

`scripts/build-audio-placeholders.mjs` rewrites only the generated sections and
leaves the hand-written role/timing/character/spec notes alone. It is
idempotent. It parses `activityPresetRegistry.ts` with a regex rather than
importing it, because importing would pull the browser build into a build
script; it throws rather than writing "0 named games" if the catalogue's shape
changes, so a parse break fails loudly.

Known cosmetic issue left alone deliberately: the hand-written
`SUGGESTED CHARACTER` lines in some placeholders run to 135 characters. The
generator does not touch hand-written prose.

## 4. Parked: Amazon Vega

The Vega port builds for all three architectures, installs, launches in ~1.1 s,
runs in the foreground, mounts its React Native views, and executes its own
`probeServer` logic. **What has never been verified is what it draws** — no
screenshot was ever obtained.

Do not repeat the dead ends. `screencap` is absent (Android binary), the
emulator console has no screenshot command, `qemu monitor` is disabled, both
in-guest Wayland tools die on `creating a buffer file for N B failed: Permission
denied` (a sandbox refusal, not a size problem), and `vda root` is refused on
production builds.

**The route that works** is the QMP socket the device is launched with:
`-qemu -qmp unix:/tmp/qmp-socket-5554.sock,server,nowait`. QEMU 2.12, `screendump`
present, writes a valid `P6 1280 720 255` PPM host-side, bypassing the guest
allocator entirely. The remaining catch is `hw.gpu.mode = host`, so qemu's own
framebuffer is empty and every dump is pure black. Two untried fixes: the
emulator's `-grpc <port>` plus `EmulatorController.getScreenshot` (purpose-built,
works with the host renderer), or software rendering (`-gpu swiftshader_indirect`
/ `guest`) to force pixels into the framebuffer.

Operational traps: the VM dies with its launching shell unless started with
`start_new_session=True`; `virtualdevice` wants `-p <sdk>/vvd/instances` with
**no** `-n` flag; a stale Android `adb` on port 5037 makes the device invisible
to every Vega tool.

## 4a. Amazon Appstore review access

The Android TV store APK uses the local Fire TV review path for Amazon
certification. On a clean install, enter `http://lsnq.demo`, select **Find
server**, enter `123456`, and select **Open demo**. The app does not resolve the
marker or contact a device; it opens the normal lesson library, lesson detail
screen, cue timeline, and player with bundled sample media. Amazon's **Testing Instructions** field
must contain these exact steps. The copy-ready template is
`docs/amazon-appstore-testing-instructions.md`.

The Amazon publisher script uploads the APK and listing release notes; it does
not populate the Console's Testing Instructions field, so that field remains a
manual submission step.

## 5. Conventions and commands

```
npm run typecheck:admin        npm run lint
npm run build:admin            npm run test:units
npm run test:e2e               npm run test:protocol
npm run test:amazon            npm run test:shortener
npm run audio:manifest         npm run audio:placeholders
```

Server tests are xUnit v3; the activities suite is the one that matters most and
is where #130 adds 21 regression tests. Browser tests are Playwright.

Two traps that have each cost a release:

- **Do not assert on a toast that a previous action already put on screen.**
  Two v0.45.1 builds failed this way: tests waited for "Activity saved." that was
  already showing from an earlier save. Wait for the request, not the banner.
- **Reading a JSON null through `TryGetInt32` throws**, producing a 500 with no
  message. This was fixed at ~30 call sites in v0.46.0 via `NumberOr()` in
  `ActivityValidation.cs`. Use the helpers.

## 6. Recommended next work

Ordered by value, not effort.

1. **Resolve the trunk divergence in §0.** Everything below is worth less until
   merged work actually ships.
2. **Land #130.** Currently `MERGEABLE`, `CLEAN`, and green on all fourteen
   checks. Eighteen fixes with regression tests, several severe: a game reset
   wiping every game's points in the lesson, Fake Out scoreable by posting
   `{"targetId":"truth"}`, Order Up dealing cards in answer order so locking in
   immediately scored 100%.
3. **Merge or split #109** so the offline media caching fix ships. Currently
   `CONFLICTING`.
4. ~~Add a version-consistency test.~~ Done 2026-09-28; see §1.
5. **Ship a default sound pack.** The four audio cues added in v0.46.0 are
   inert — the plumbing exists, the placeholders now explain themselves, and no
   `.mp3` ships. A small set of licensed beds in `shared/` would make the feature
   real. Licensing requirements are in `docs/activities-assets-and-sound.md`.
6. **Separate per-game state from lobby state.** #130 names this its top
   improvement and traces four bugs to it (AUD-01, 05, 10, 23); independent work
   in `ActivitySessionService` reached the same conclusion.
7. **Extend the `zz-activity-sweep` pattern.** Playing all 28 engines on all
   three surfaces found a total outage (six uncreatable games) on its first run.
   Lesson cues and signage have no equivalent.

## 6a. Path containment, and the CodeQL alerts

CodeQL reports seven high-severity `cs/path-injection` alerts against
`AdminApi.cs` and `MediaRetentionService.cs`, first raised 2026-07-31. **They are
false positives.** Every flagged sink is guarded: the guard resolves the full
path and compares it to the root with a trailing separator, and `&&`
short-circuits so the file operation only runs after containment passes. The
uploaded extension reaching those paths is separator-free by construction —
`Path.GetExtension` stops at a separator — and is checked against the format
catalogue's allowlist. CodeQL does not model a `StartsWith` comparison as a
sanitizer; this is a documented limitation of that query, not a gap in the code.

They were still worth acting on, because what they pointed at was real. The same
rule had been written out by hand in **fourteen places across ten files**, in
three spellings, and two had drifted — `BackupService.Resolve` compared against a
root with no trailing separator (unreachable only because `Path.GetFileName`
strips the directory part first), and `AdaptiveTranscodeService` combines a
stored path with no check at all.

There is now one `ContainedPath` (`Resolve`, `ResolveExistingFile`,
`DeleteIfContained`, `IsInside`) with 15 tests covering traversal, a rooted path
that would make `Path.Combine` discard the root, the sibling directory that
defeats a prefix comparison, and the root itself. It asks
`Path.GetRelativePath` the containment question instead of comparing string
prefixes, so it cannot forget the separator and it uses the platform's own casing
rules. All 18 call sites go through it, including the backup extractor's
zip-slip guard and the check on yt-dlp's stdout, neither of which CodeQL flagged.

**The alerts will probably not clear on their own**, because the helper is still
a `GetRelativePath` check that the query does not recognise either. Dismissing
them as false positives is reasonable and is the repository owner's call; the
justification is this section and `ContainedPathTests`.

## 7. Security constraints that still apply

These were set by the repository owner and are not negotiable without asking:

- Never embed an administrator Shlink API key in `servers.json`, JavaScript,
  HTML, or Docker environment variables reaching browser-visible config. Never
  log it.
- Reserved codes are created through Shlink's REST API, never by direct database
  insert, and the list is not regenerated on each install.
- Do not expose PostgreSQL publicly. Keep container ports bound to the host
  where practical. HTTPS externally via Cloudflare.
- Cloudflare integration is additive: never replace the tunnel configuration
  wholesale, and preserve the catch-all rule.
- Validate every domain and redirect destination. Never impose a blanket rule
  such as "all four-character URLs are reserved".
- Destructive uninstall/reset stays behind explicit confirmation, and shortener
  configuration behind existing administrator permissions.
