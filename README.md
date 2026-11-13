![LessonCue](branding/source/lessoncue-banner.svg)

# LessonCue

LessonCue is a self-hosted lesson scheduling and television playback system for schools, churches, training programs, and other learning organizations. Administrators build dated media playlists in a browser; paired Android TV, Google TV, and Fire TV clients cache their assignments and keep playing when the network is unavailable. Computers and projectors use the paired full-screen browser player.

[Installation guide](docs/installation.md) · [Accounts and registration](docs/account-self-service.md) · [Local network security](docs/local-network-security.md) · [Accessibility acceptance](docs/accessibility.md) · [Run-of-show planning](docs/run-of-show.md) · [Audience interaction](docs/audience-interaction.md) · [Signage scheduling](docs/signage.md) · [URL shortener](docs/url-shortener.md) · [Browser player](docs/browser-player.md) · [Display compatibility](docs/display-compatibility.md) · [Implementation guide](docs/implementation.md) · [Protocol contract](docs/protocol-contract.md) · [Feature roadmap](docs/feature-roadmap.md) · [AI UI evaluation brief](docs/ai-ui-evaluation-brief.md) · [Brand assets](branding/README.md)

## What is included

- A complete, responsive React/TypeScript administration interface served directly by the local server, visually matched to the LessonCue prototype.
- An ASP.NET Core 10 API with SQLite, pairing, manifests, health reporting, SignalR invalidation, and range-enabled media hosting.
- A native Android TV/Fire TV application using Kotlin, Compose for TV, Media3, DataStore, and WorkManager.
- Separate Android distributions: **LessonCue Sideload** with verified GitHub updates, and policy-clean Google Play/Amazon store packages whose updates are managed only by the installed store.
- A paired full-screen browser client for Windows, macOS, Linux, ChromeOS, computers, and projectors, using the same local manifests, controller commands, acknowledgements, heartbeats, scheduling, and diagnostics as Android displays.
- A versioned OpenAPI contract and JSON Schema shared by every client.
- Docker, Windows, and Linux installation assets.
- Calendar, local role-based users, approval-required or verified self-registration, administrator email invitations, first-login temporary passwords, and optional self-hosted signage with persistent layouts, independent looping playlists, one active sign per screen, and browser kiosk playback. Signage remains an administrator-enabled preview feature while its production-hardening roadmap is completed.
- Direct lesson uploads, online webpages, embedded YouTube playback, queued local YouTube imports, reusable or four-week lesson retention, automatic cleanup, durable pause/resume/cancel uploads of every size, streaming SHA-256 and signature validation, SHA-256 deduplication, FFprobe metadata, FFmpeg thumbnails, and range-enabled delivery. Uploaded video is audited automatically and, when necessary, converted locally to a TV-safe H.264/AAC MP4 while preserving the original.
- Multi-file uploads from lessons and the Media Library; bulk lesson archive, restore, class move, date/time shift, rename, and deletion; bulk playlist role, volume, ending, skip, rename, and removal; and Media Library bulk rename, folder/tag organization, retention, and safe deletion. Every retention date can also be edited directly from its table row.
- Searchable media folders and tags, upload-time and bulk organization, lesson/signage impact previews, local reprocessing, and safe file replacement behind a stable media ID with downloadable and restorable original-version history.
- A visual signage canvas with zoom/pan, snapping and alignment guides, groups, rich text, QR/Wi-Fi sharing, widgets, background audio, portrait/ultrawide/custom formats, approved server-side data credentials, HLS/HTTP/RTMP/RTMPS/RTSP relays, per-screen delivery progress, and exact manifest preview.
- PDF, current and legacy PowerPoint, OpenDocument, Keynote, Word, and shared Google Slides ingestion from the Media Library or lesson page, producing ordered screen-ready PNG slide sequences with configurable timing and automatic lesson insertion.
- Reusable lesson templates that preserve complete playlist and timing structure, one-click dated instantiation, and automatic weekly, biweekly, monthly, term, or custom-date schedules with idempotent generation and reversible holiday exceptions.
- Teacher and substitute notes, flexible-time cues, printable run sheets, trim/speed/repeat-aware duration estimates, live remaining-time and overrun guidance, same-room conflict warnings, five calendar views, full copy/move between rooms and dates, and private pre-roll livestream monitoring on the phone controller.
- Local audience polls and QR response collection with single choice, multiple choice, and moderated written answers; anonymous per-session device tokens; optional approved live results; explicit open/close controls; rate limits; audit history; and automatic 1–30 day deletion.
- Daily release checks, administrator alerts, protected one-click Linux updates with health-check rollback, and administrator-controlled storage allocation with uploader-visible capacity.
- Password-encrypted `.lcbak` exports with an authenticated whole-archive digest, per-file SHA-256 manifest, default exclusion of server secrets, and verified browser restore preview.
- A locally configurable six-digit pairing PIN, with a choice between a persistent administrator-set PIN and automatic ten-minute rotation.
- Automatic `lessoncue.local` setup on native Linux, with an administrator-configurable `.local` browser name and numeric-IP fallback.
- Optional Cloudflare Tunnel remote access through an administrator-owned hostname, with a write-only token, checksum-verified connector installation, a restricted local service account, active edge-connection status, safe disable/credential removal, and strong Cloudflare Access guidance. Local-only operation remains the default.
- Local administrator password recovery over SSH, including username listing, audited resets, and existing-session invalidation.
- Administrator user management with editable names, usernames, email addresses, roles, and passwords, plus pause/reactivate and protected account deletion.
- Granular per-user permissions for lesson planning, media uploads, live playback, screen administration, user administration, server settings, backups, and software updates. Built-in roles supply safe defaults, while owners can grant an exact custom combination.
- Local interface branding with independent navigation background, navigation text, selected-tab, and accent colors.
- Browser previews for every ready media item, including playlist trim points, synchronized audio-and-picture fades to/from black, fit/fill/letterbox, crop, rotation, background, speed, repeat, volume/mute, ending, and operator notes.
- Simple and Advanced cue-editing modes plus a prominent **Visually trim both ends & edit fades** action on every lesson cue. Locally generated video filmstrips and audio waveforms have draggable IN/OUT and independently colored fade handles, edge-following preview, 0.04-second keyboard nudging, and synchronized numeric controls.
- A remote-friendly media browser in the Android TV app: choose a lesson, scroll through pre-roll, countdown, and lesson cues with the directional pad, and start any item without returning to the local browser.
- A full-screen local browser/projector player at `/player`, with secure TV-style pairing, phone control, scheduled pre-roll and countdown transitions, autoplay guidance, reconnection, next-item prefetching, keyboard and presentation-remote controls, and a kiosk-friendly startup URL.
- Actionable screen diagnostics with per-file cache and queue detail, decoder capabilities, download and playback errors, local-network latency, clock drift, and freshness. Optional screenshots are disabled per screen by default, visibly announced on the TV, valid for one request and 60 seconds, restricted to screen administrators, and deleted automatically after 24 hours.
- A phone-first local controller for selecting screens, lessons, and individual media, with play, pause, resume, previous, next, stop, and seek controls; actual playback state, progress, errors, and command acknowledgement arrive live from the TV and the controller can be saved to an iPhone, iPad, or Android home screen.
- Dedicated classroom controller paths with unique colors and optional public hostnames; locally generated room or lesson QR codes; expiring restricted substitute/event links; and a separately PIN-protected universal remote.
- Editable and removable classrooms plus an administrator-only recycling bin: deleted classes, lessons, and media remain restorable for 30 days, keep their relationships and files intact, and can be purged immediately when recovery is not needed.
- GitHub Actions that build the web app, server, Android packages, container, release packages, and GitHub Pages documentation.

## Quick start

For a headless Ubuntu or Debian server accessed over SSH, use the complete copy-and-paste block in the [installation guide](docs/installation.md#recommended-headless-ubuntu-or-debian-server-over-ssh).

For a quick Docker evaluation:

```bash
cp .env.example .env
mkdir -p lessoncue-data
sudo chown -R 10001:10001 lessoncue-data
docker compose up -d --build
```

Then open `http://localhost`. Native Linux installations automatically configure the preferred local-network address `http://lessoncue.local`; administrators can change both the name and HTTP port in Settings.

For browser-interface development:

```bash
npm ci
npm run build:admin
LESSONCUE_HTTP_PORT=8080 dotnet run --project server/LessonCue.Server
```

Open `http://localhost:8080`. For live front-end development, run `npm run dev:admin` in a second terminal and open `http://localhost:5173`.

For native development and production installation, see [docs/installation.md](docs/installation.md). The server API is independently runnable with `dotnet run --project server/LessonCue.Server`.

## Scheduled playback modes

LessonCue publishes two coordinated pre-class modes in every screen manifest:

1. **Pre-roll** — a sequence of videos or images loops until the countdown window begins.
2. **Duration-aware countdown** — the chosen countdown video starts exactly one video-duration before the lesson's designated start time, so its final frame lands on the start time.

If the countdown duration is five minutes and class begins at 09:00, the TV transitions from pre-roll to countdown at 08:55. Clients calculate this locally from the manifest, so an already-synchronized screen can make the transition while offline.

## Templates and recurring schedules

Open **Templates** to capture any existing lesson as a reusable structure. LessonCue copies playlist order, media links, roles, trims, fades, cue markers, notes, pre-roll and countdown settings, start-time offsets, availability, and offline defaults. Media referenced by a template is changed to permanent retention so a future generated lesson cannot lose its source. A template can create one dated lesson immediately, and its structure can later be refreshed from a newer lesson without changing its schedules or previously generated lessons.

Recurring schedules support weekly or multi-week intervals, monthly dates, bounded terms, and explicit custom dates. Each schedule generates ahead by an administrator-selected window and the local server checks enabled schedules once per day. Generation is idempotent: a schedule/date pair is created at most once even after restarts or manual reruns. Adding a holiday or skipped date removes only that schedule's generated lesson; restoring the date safely regenerates it. Pausing or deleting a schedule preserves lessons already created.

## Media retention

Every file upload asks how it should be stored. **For a lesson** is the default and automatically deletes the file four weeks after the latest lesson that uses it. **Keep permanently** places reusable material in the media library until an administrator removes it. Playlist history remains intact when an expired file is cleaned up.

Lesson pages and the Media Library also accept webpages and YouTube URLs. Android TV, Google TV, Fire TV, and the browser player render supported online content while connected. A YouTube URL can instead be queued as a local MP4 import and then uses the same four-week or permanent retention policy as an upload; only import video you are authorized to copy.

LessonCue inspects every uploaded video in the background. MP4/H.264/AAC files that meet the common TV profile are used directly. Other containers, codecs, pixel formats, oversized frames, or unsupported H.264 levels receive a local H.264 High 4.1, 8-bit 4:2:0, AAC, 1080p-or-smaller playback copy. The original remains available for future reprocessing, and the Media Library shows whether the item is already TV-ready, is making its TV copy, or needs attention. Existing videos are audited automatically after upgrading.

Adaptive playback can additionally cache reusable 720p and 480p H.264/AAC copies. Each paired screen receives the best ready profile for its reported decoder support, measured network quality, and free device storage; the universal 1080p copy remains the fallback until the smaller copy is ready. **Settings → Adaptive TV playback** controls automatic pre-generation one to thirty days before assigned lessons, while **Media Library → Manage versions & impact** shows size, progress, failures, retry controls, and manual generation for both profiles. All conversion stays on the self-hosted server and counts toward its configured storage allocation.

On Intel Linux and Windows servers, LessonCue checks FFmpeg and the installed Intel GPU driver with a real local H.264 hardware encode at startup and once per day. It prefers modern Intel Quick Sync and, on Linux, falls back to direct VAAPI—including Haswell systems that require the legacy `i965` driver. The verified pipeline accelerates both the universal TV-safe copy and adaptive 720p/480p queues. Service Admins can disable it or recheck the hardware under **Settings → Adaptive TV playback**. Every generated file is probed as H.264 MP4 before it is installed; a hardware error automatically retries the same job with software and is reported in Settings.

## Updates and storage

Native Linux installations check for a new LessonCue release once per day. Service Admins and App Admins can check immediately and install an available update from **Settings → Software updates**. The protected updater verifies the offline Ed25519 signature on the complete release manifest and then the selected archive checksum before it extracts or executes downloaded code. It stops writers, creates and verifies a pre-update database/configuration snapshot, snapshots its protected executable and systemd units, starts the new server, and requires database plus persistent-storage readiness. If migration or readiness fails, it restores the application, database, configuration, updater, and service units together. A persistent transaction marker also triggers boot-time recovery after a power loss. When a verified snapshot exists, a Service Admin can deliberately restore it from the same page; LessonCue first verifies and protects the currently running installation so a rejected rollback can itself be reversed. Existing servers must run the current SSH installer once to add these protected services; later releases can be installed or rolled back from the browser.

Native Linux also advertises `http://lessoncue.local` automatically. Service Admins can choose a different single-label `.local` name or HTTP port under **Settings → Connections & pairing** without changing the computer's Linux or SSH hostname. Port 80 is the default, so it does not need to appear in the address.

Service Admins can choose an explicit LessonCue storage allocation or let it follow safely available disk space. LessonCue preserves a 512 MB operating-system reserve, atomically reserves the full size of each persisted upload session, and shows remaining plus in-flight reserved capacity. Uploaders can pause, resume, cancel, or retry from the same file for 24 hours after an interruption. Optional limits cover file size, active sessions, daily use by account/role/class, and verified codecs. See [resumable uploads and storage limits](docs/uploads.md).

## Roles and granular permissions

Open **Users** to send a setup invitation, create an account with a first-login temporary password, approve a verified access request, edit or pause an account, reset its password, or delete it. Service Admins have unrestricted access, including account-email delivery, storage allocation, adaptive playback, network/remote access, privacy/backups, and server operations. App Admins have every ordinary app capability plus updates, registration mode/codes, approved media folders/tags, pairing PINs, recycling, and recent activity. Editors default to lesson planning, uploads, and live playback; Viewers default to read-only access. Service-only capabilities cannot be granted to a lower role, and only a Service Admin can create, edit, or delete another Service Admin.

Permission checks run on the local server, not only in the browser. Restricted navigation and controls are hidden, direct API attempts return HTTP 403, pairing PINs are withheld from accounts without screen or settings authority, and changing identity, role, permissions, status, or password invalidates older sessions.

## Preview and cellphone control

Select **Preview** on any ready item in the Media Library, or use the preview row on a lesson playlist. Video previews reproduce the saved start/end trims and fade both picture and audio from/to black; audio previews apply the same volume envelope. Loop behavior and notes are also preserved. Images, PDFs, online webpages, and YouTube embeds preview in the same local interface; presentation files provide a local open action when the browser cannot render the format directly.

On a lesson page, choose **Visually trim both ends & edit fades** beneath a cue. Drag IN or OUT directly on the filmstrip/waveform and the preview seeks to that edge; drag either blue fade handle and the preview moves to that fade's midpoint. Range and numeric controls stay synchronized for exact entry. Simple mode keeps role, ending, volume, picture fit, and still duration close at hand; Advanced adds speed, finite repeats, rotation, asymmetric crop, background, transition, exact timing, notes, and audio options. All values are saved into the same manifest used by browser and Android displays.

On a phone connected to the same trusted network, open a class's **Controller link** from the Classes page, scan its locally generated QR code, and sign in with a LessonCue account that has live-playback permission. The address uses `/room/class-name`; administrators can set its path, color, and optional Cloudflare hostname, point its QR code at a particular lesson, or create a class/lesson-restricted link that expires in 15 minutes to seven days. Temporary links are deliberately cleared by a server restart. Room scope is validated again by the server for every playback command.

The controller for all classrooms is `http://lessoncue.local/universalremote` (or the server's numeric IP). An administrator must set its separate six-digit PIN in **Settings → Universal controller**. Select a paired screen and lesson, then start the complete sequence or a particular media item. Commands are versioned and stored on the local server, so a short Wi-Fi interruption does not reorder them; the controller distinguishes a command that was sent from one the TV has acknowledged. Actual title, player state, elapsed time, duration, remaining run time, estimated finish, overrun warning, current cue notes, substitute instructions, and optional private pre-roll monitor update through the local SignalR connection with automatic polling fallback.

The **Screens** page provides the same live operational evidence for administrators: device and operating-system version, last network address, free space, manifest version, command acknowledgement, cached/total item count, and recent download or playback errors. No screen diagnostics are sent outside the self-hosted server.

To save it like an app, use **Share → Add to Home Screen** in Safari on iPhone/iPad, or **Add to Home screen** / **Install app** from the Android browser menu. The controller remains a local web interface—there is no separate phone app or hosted dependency.

## Repository map

```text
web-admin/           Local React administration interface
server/              ASP.NET Core API and tests
android-tv/          Android TV and Fire TV client
tvos/                Archived unsupported Apple TV prototype (not built or released)
protocol/            OpenAPI, manifest schema, and behavioral rules
installers/          Linux and Windows service installers
docker/              Container support files
docs/                Operator and developer documentation
github-pages/        Public project documentation site
```

## Project status

The complete self-hosted workflow runs on the local server: setup, local or verified self-service accounts, roles and permissions, registration codes, password recovery, classes, calendar, lesson playlists, resumable media ingestion, optional preview signage, pre-roll, duration-aware countdown, rotating PIN pairing, screen assignment/health, branding, audit history, and backups. Android/Google TV/Fire TV clients build from this repository and retain offline manifests and media. Hardware signing, managed-store submission, and final device certification require the deploying organization's accounts and target devices.

Apple TV/tvOS is explicitly unsupported and deferred for the current product cycle. The archived source is not part of CI, releases, installation support, or current feature-parity promises. See the [feature roadmap](docs/feature-roadmap.md) for the requirements that would apply before it can be reconsidered.

## License

LessonCue is free to use, modify, and self-host for non-commercial purposes under the [PolyForm Noncommercial License 1.0.0](LICENSE). Commercial use requires a separately purchased commercial license from the LessonCue maintainers. For commercial licensing, contact the maintainers through the [LessonCue repository](https://github.com/ProbablyModded/lessoncue).

This is not an OSI-approved open-source license. The full license—not this summary—controls.

Tagged downloads also include an exact SPDX software bill of materials and generated third-party notices. Release archives are authenticated by a pinned Ed25519 key and GitHub provenance attestations; verification and signing-key recovery procedures are documented in [docs/release-signing.md](docs/release-signing.md).

Release scope is detected automatically: server-only changes do not rebuild or publish the TV app. See [docs/release-scope.md](docs/release-scope.md).
