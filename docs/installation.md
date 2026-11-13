# Installing LessonCue

LessonCue is a local-network product. Install the server on a computer that remains at your school, church, training site, or other learning facility; connect the television devices to the same trusted network; and use a modern browser on another computer for administration.

## Recommended: headless Ubuntu or Debian server over SSH

These instructions are for a 64-bit Ubuntu or Debian server with no desktop or GUI installed. The server needs internet access during installation, a non-root SSH account with `sudo`, and a static or DHCP-reserved local IP address.

SSH into the server from your computer:

```bash
ssh YOUR_USERNAME@SERVER_IP
```

Paste these two commands into the SSH session. The first installs the one prerequisite needed to download the installer; the installer then installs the complete host toolchain (including Git, Docker Engine, Docker Compose, FFmpeg, document converters, Avahi, and runtime libraries), detects Intel/AMD versus ARM64, downloads the signed release, registers the systemd service, waits for it to become healthy, and prints the browser address:

```bash
sudo apt-get update
sudo apt-get install -y curl ca-certificates
curl -fsSL https://github.com/ProbablyModded/lessoncue/releases/latest/download/install-lessoncue.sh | bash
```

The final message says `LessonCue is ready` and prints `http://lessoncue.local` plus a numeric fallback such as `http://192.168.4.75`. The SSH connection can then be closed; systemd keeps LessonCue running and starts it again after reboot.

### First browser setup

On a computer connected to the same local network, open the address printed by the installer. LessonCue will ask you to create the organization name, administrator username, and password. This account, the complete web interface, database, schedules, and media all remain on your local server.

LessonCue creates a private local pairing secret and displays a six-digit PIN that rotates every ten minutes to accounts with screen or app-settings authority. A Service Admin or App Admin can instead set a persistent six-digit local PIN under **Settings → Connections & pairing**. The same control switches back to automatic rotation at any time; the setting stays entirely on the local server.

### Give staff only the access they need

Open **Users** as a Service Admin or an account with **User administration** permission. Choose **Send setup link** to set an email address and permissions while letting the recipient choose their own name, username, and password, or choose **Create with password** for a fully local account whose temporary password must be replaced at first sign-in. The built-in defaults are straightforward: Service Admins receive every capability; App Admins receive every ordinary app capability plus updates and operational app settings; Editors receive lesson planning, media uploads, and live playback; Viewers are read-only. App Admin settings include registration mode/codes, approved media folders/tags, screen pairing PIN/mode, universal-controller PIN, recycling, and recent activity. Account email, storage allocation, adaptive TV playback, network/remote access, privacy/backups, and server commands remain Service Admin-only.

Permission enforcement happens on the server even if someone constructs an API request manually. A permission change signs that account out of its earlier browser sessions. Only a Service Admin can grant that role or change another Service Admin; other user administrators cannot grant capabilities they do not hold or change their own access; and the last active Service Admin cannot be paused, demoted, or deleted. Accounts without screen or app-settings authority cannot see the local pairing PIN.

Native Linux installation configures `http://lessoncue.local` on standard HTTP port 80 automatically. A user with **Server settings** permission can change either `lessoncue` or the browser port under **Settings → Connection & pairing**. LessonCue applies the change and restarts itself; if a chosen port cannot be opened, it returns to the previous working port. Changing the name does not rename the Linux computer or SSH hostname. Keep the numeric address as a fallback for networks that block multicast DNS.

Under **Settings → Server connection**, **Allow IPv6 for the local .local address** controls whether Avahi advertises the local name over IPv6. It is on by default to preserve existing installations. If a TV chooses an IPv6 address it cannot reach, turn it off and save the local address; this changes Avahi discovery for the whole server (including other Avahi services), not Linux networking or the Cloudflare Tunnel. Test the server's numeric IPv4 address on a failing TV first, and allow a few seconds for mDNS caches to refresh after changing the setting.

### Optional: use your own internet hostname with Cloudflare Tunnel

LessonCue does not require remote access. If staff must reach it away from the local network, native Linux installations can configure an outbound-only, remotely managed Cloudflare Tunnel from **Settings → Optional remote access**. You need a domain managed in your own Cloudflare account. Protect the hostname with Cloudflare Access before inviting users; without Access, anyone on the internet can reach the LessonCue sign-in page.

1. In the [Cloudflare dashboard](https://one.dash.cloudflare.com/), create a remotely managed Cloudflare Tunnel.
2. Add a published application route using the hostname you want, such as `lesson.example.org`.
3. Set its service to the exact **Local origin route** shown by LessonCue, normally `http://127.0.0.1:80`.
4. In the tunnel's **Overview → Add a replica** instructions, copy the `eyJ…` tunnel token or the complete `cloudflared service install …` command. Do not run it over SSH.
5. In LessonCue, open **Settings → Optional remote access**, enable the tunnel, enter the same public hostname, paste the token or command, acknowledge the exposure warning, and select **Install and enable tunnel**.
6. Wait for LessonCue to report active edge connections, then open the HTTPS public address. The first connection can take more than a minute on networks where `cloudflared` must fall back between transports; LessonCue leaves the service enabled so it can keep retrying.

The native installer pre-downloads and verifies the supported `cloudflared` connector even while remote access is off. LessonCue checks that pinned connector daily, refreshes it when an application update approves a newer version, and reports the installed version and last verification in Settings. Downloads are cached under `/var/cache/lessoncue`, verified against Cloudflare's published SHA-256 digest, checked for successful execution, and installed atomically. A working connector is restored if an active tunnel cannot restart with a replacement.

The browser sends the tunnel secret once. LessonCue passes it through the protected root operation channel, stores it outside the web server's readable files, and never returns it through the API or writes it to the audit log. The connector runs as the separate `lessoncue-tunnel` user. Disabling the feature stops the connector and deletes its stored credential while leaving the verified connector ready for later use, and leaving `lessoncue.local` and the numeric local address unchanged.

If you later change LessonCue's HTTP port, update the Cloudflare published application service to the new **Local origin route**. Paste a replacement token and select **Update tunnel** to rotate the connector credential. For diagnostics over SSH:

```bash
sudo systemctl status lessoncue-cloudflared --no-pager
sudo journalctl -u lessoncue-cloudflared -n 100 --no-pager
sudo systemctl enable --now lessoncue-cloudflared
```

### Optional: remote SSH with Cloudflare Access for Infrastructure

The LessonCue web tunnel and SSH access are separate routes. The web tunnel does not expose port 22 and does not create an SSH identity. If remote maintenance is required, keep its Cloudflare Access configuration and credentials in private operator documentation; do not store them in this repository. It is not automatically provisioned by the web Settings toggle.

The connector needs outbound access to Cloudflare on port `7844` (UDP for QUIC or TCP for HTTP/2). If the service is active but no edge connection appears, verify the tunnel token in Cloudflare, allow outbound TCP or UDP `7844`, and select **Retry tunnel connection** in LessonCue. The published hostname route controls where requests go after the connector reaches Cloudflare; it does not establish the edge connection itself.

#### Docker hosts such as Unraid

The native Settings toggle above requires systemd and is not the Docker-host
path. On Unraid, run the `lessoncue` and `cloudflared` services on the same
Compose network instead. Create the remotely managed Cloudflare Tunnel route
for `demo.lessoncue.net` with the local service `http://lessoncue:8080`, place
the replica token in the Unraid secret store and point
`CLOUDFLARE_TUNNEL_TOKEN_FILE` at it, then start only the optional profile:

```bash
docker compose --profile cloudflare-tunnel up -d --build
```

The token file is mounted read-only and is not placed in the process command
line, written to the repository, or passed through the LessonCue web
administration API. Keep this instance disposable for store review; do not put
Cloudflare Access's interactive login in front of the TV pairing and manifest
endpoints, because the native TV client cannot complete that browser flow.

### Set up reusable lessons and schedules

No additional service or cloud account is required. Build one complete lesson under **Classes**, then open **Templates → New template** and select it as the source. LessonCue keeps media used by a reusable template permanently. Choose **Create lesson** for a one-time dated copy, or **New schedule** for weekly, multi-week, monthly, term-based, or explicit custom dates.

Choose how far LessonCue should generate ahead. The server checks enabled schedules daily, and **Generate now** safely fills only missing dates. Under each schedule, add school breaks, holidays, closures, or other skipped dates. LessonCue removes the lesson generated for that occurrence; click the date chip to restore and regenerate it. Pausing or deleting a schedule leaves lessons already created in place.

### Let LessonCue prepare videos for televisions

No converter setup is required after using the recommended installer; FFmpeg and FFprobe are included as server dependencies. Every new upload is inspected automatically. When a video is not in the broadly supported TV profile, LessonCue keeps the original and creates an H.264/AAC MP4 playback copy locally. Existing videos are inspected in the background after an update, so no re-upload is necessary. In **Media Library**, the original is playable as soon as it is accepted; wait for **TV copy ready** or **TV ready** before relying on offline playback. A compatibility or runtime error remains visible there and can be retried with **Manage versions & impact → Retry processing**. LessonCue verifies the original size, checksum, and content before requeueing it, so a damaged or missing source is not retried as though it were a worker failure.

Compatibility copies count toward the storage allocation. For reliable initial conversion, leave enough available capacity for the original plus a second video file. Neither the original nor its playback copy leaves the local server.

### Verify from SSH

```bash
sudo systemctl status lessoncue --no-pager
curl -fsS http://127.0.0.1/health && echo
curl -fsS http://127.0.0.1/.well-known/lessoncue && echo
sudo journalctl -u lessoncue -n 50 --no-pager
```

To follow the logs continuously, run `sudo journalctl -u lessoncue -f` and press `Ctrl+C` when finished.

### Enable registration and browser password recovery

The initial owner and accounts created under **Users** do not require email. To permit self-registration, account invitations, or browser password recovery, sign in with **Server settings** permission and open **Settings → Organization & accounts → Registration & email**. Configure Resend or Brevo, enter the public HTTPS address recipients can reach, save, and send a real test message from the same page before choosing approval-required, code-required, or open registration. Approval-required registration lets anyone request an account but blocks access until a user administrator approves the request under **Users**.

The provider key is encrypted on this server and is never returned to the browser. Preserve the complete `/var/lib/lessoncue/config` directory in disaster-recovery backups because it contains both the encrypted provider credential and the local encryption keys needed to use it. See [Accounts, registration, and email](account-self-service.md) for code management, expiry behavior, provider setup, and troubleshooting.

### Reset a forgotten administrator password

Password recovery stays local and requires SSH access to the server. First list the local administrator usernames:

```bash
sudo -u lessoncue env LESSONCUE_DATA_PATH=/var/lib/lessoncue \
  /opt/lessoncue/LessonCue.Server --list-admins
```

Reset the password for the required active username, replacing `YOUR_USERNAME`:

```bash
sudo -u lessoncue env LESSONCUE_DATA_PATH=/var/lib/lessoncue \
  /opt/lessoncue/LessonCue.Server --reset-password YOUR_USERNAME
```

Enter the new password twice when prompted. Nothing is shown while typing. The password must contain at least ten characters with uppercase, lowercase, and numeric characters. The command writes the normal ASP.NET password hash, disables authenticator MFA for that account, turns off the all-user MFA requirement, records an audit event, and signs out that account's existing browser sessions. It does not display or recover the old password.

The web server can remain running during this operation. If the selected account is marked `disabled`, the password is still reset but another active Service Admin must enable the account before it can sign in.

### Open LessonCue from another computer

Find the server's local address over SSH:

```bash
hostname -I | awk '{print "http://" $1}'
```

First try `http://lessoncue.local`, then use the printed numeric address if `.local` discovery is unavailable on that network. The complete LessonCue administration interface is served from the local server. It does not load or depend on the hosted prototype.

Do not forward LessonCue's HTTP port directly from the internet. Use the protected Cloudflare Tunnel option above or an administrator-managed VPN. For a shared local network, use the supported [local HTTPS and Caddy workflow](local-network-security.md).

### Use the cellphone controller

Connect the phone to the same trusted Wi-Fi as the LessonCue server and TV. Open one of these addresses in Safari or Chrome, replacing `SERVER-IP` when needed:

```text
http://lessoncue.local/universalremote
http://SERVER-IP/universalremote
```

Sign in with a local LessonCue account. Choose the paired screen, choose a lesson, and use **Play lesson**, an individual media row, pause/resume, previous/next, stop, or seek. The television app must be open and paired; its status should say **Screen online** in the controller.

The native Android TV, Google TV, and Fire TV interface also lets an operator choose a lesson and scroll through every pre-roll, countdown, and lesson cue with the television remote's directional pad. Press the center/select button to play the focused item and Back/Menu to return to the lesson list. This browsing does not require the phone controller.

On iPhone or iPad, tap **Share**, then **Add to Home Screen**. On Android, open the browser menu and tap **Add to Home screen** or **Install app** when offered. This saves the local browser controller as an app-like icon; it does not install a separate LessonCue phone binary or connect to a hosted service.

### Use a computer or projector as a playback screen

Open **Screens** and select **Open browser player**, or browse directly to:

```text
http://lessoncue.local/player
http://SERVER-IP/player
```

Name the display, start pairing, and enter the six-digit PIN from **Screens**. The paired browser receives the same assigned manifests, phone-controller commands, acknowledgements, heartbeats, pre-roll, countdown, trims, fades, signage, and online media as a native TV client. Use `/player?kiosk=1` for the clean kiosk startup view. Select **Enter full screen**, then approve **Start browser playback** when the browser requests the first user gesture for audible media.

See the [browser playback client guide](browser-player.md) for keyboard and presentation-remote controls, kiosk startup, diagnostics, autoplay behavior, and recovery.

## Before you begin

Choose a server with:

- Windows 11 or a current 64-bit Linux distribution.
- 4 GB RAM minimum; 8 GB recommended for transcoding.
- Enough disk space for the original and processed media library.
- Ethernet when possible and a reserved DHCP address.
- TCP port 80, or the administrator-selected port, reachable by the trusted television network.

Install FFmpeg/FFprobe for media inspection and transcoding, LibreOffice plus Poppler for local document conversion, and the optional codec libraries used by your distribution's FFmpeg build. The recommended installer and Docker image install the broad media toolchain automatically, including WebP, Ogg/Theora, HEIF/AVIF, JPEG XL, spreadsheet, and legacy Office support when the host repository provides those packages. Do not expose LessonCue directly to the public internet.

The packaged Windows installer runs LessonCue as the built-in restricted `LocalService` identity, gives that identity read/execute access to the application and modify access only to `%ProgramData%\LessonCue`, and limits its firewall rule to Domain and Private networks. It does not process uploaded media as LocalSystem.

The Linux installer installs both the current Intel media driver and the legacy `i965` driver when the distribution provides them, grants the restricted service account `render` and `video` access, and creates a writable local shader cache. On a server with a supported Intel integrated GPU, keep `/dev/dri` accessible to the `lessoncue` service and open **Settings → Adaptive TV playback → Check hardware**. Version 0.30.5 and newer tries each Intel render node using direct QSV, VAAPI-derived QSV, direct VAAPI, and direct VAAPI forced through `i965`. The successful initializer and driver environment are reused for real conversions. “Hardware ready” means a real H.264 test encode passed. If the driver, GPU, permissions, or FFmpeg support is absent, LessonCue reports the failed paths and continues with software conversion automatically.

## Alternative: Docker

Docker is the quickest evaluation and technical-user installation.

If you are starting from a fresh Ubuntu or Debian VM, the repository installer can install the complete host prerequisite set without installing the native systemd release:

```bash
sudo bash installers/linux/install-latest.sh --prerequisites-only
```

This installs Git, Docker Engine, Docker Compose, FFmpeg/FFprobe, LibreOffice (Impress, Writer, Calc, and Draw), Poppler, the media sandbox, and available optional codec libraries. Continue with the Docker commands below to build the checked-out source tree.

```bash
git clone https://github.com/ProbablyModded/lessoncue.git
cd lessoncue
cp .env.example .env
mkdir -p lessoncue-data
sudo chown -R 10001:10001 lessoncue-data
docker compose up -d --build
docker compose logs -f lessoncue
```

To opt an Intel Docker host into Quick Sync, add the render devices to the service before starting it:

```yaml
services:
  lessoncue:
    devices:
      - /dev/dri:/dev/dri
```

Do not add this mapping on a host without `/dev/dri`; software conversion remains the portable default.

Open `http://SERVER-IP`. Data is stored in `./lessoncue-data` unless `LESSONCUE_DATA_PATH` is changed in `.env`. The directory must be writable by container UID/GID `10001`; apply the same `chown` command to a custom bind-mount path. The application runs as that non-root account with all Linux capabilities dropped, a read-only container filesystem, `no-new-privileges`, and only `/data` plus a bounded temporary filesystem writable. Docker uses the `LESSONCUE_HTTP_PORT` value in `.env` for its host port; recreate the container after changing it.

For a small VM, set `LESSONCUE_CPUS` in `.env` to the number of available virtual CPUs (the default is `2.0`). Docker bridge networking does not reliably publish mDNS; use the numeric address or install the supplied `docker/avahi-service.xml` on the host. With Avahi installed, set the host name and the persisted `lessoncue-data/config/local-hostname` to the same value (for example, `lessoncue`) to use `http://lessoncue.local`. Native installation is friendlier for ordinary deployments.

The container runs the same bounded media worker without requiring nested user or mount namespaces. Docker supplies the read-only root, dropped capabilities, `no-new-privileges`, memory/CPU/PID limits, and writable-data boundary; the worker adds timeout, address-space, output-file, process-count, open-file, and captured-output limits. Native Linux installations receive the equivalent systemd service boundary.

## Manual Linux service installation

Download `LessonCue-Server-linux-x64.tar.gz` or `LessonCue-Server-linux-arm64.tar.gz` from the GitHub release, unpack it, and run:

```bash
sudo ./install.sh
```

The installer creates a restricted `lessoncue` account, installs the application at `/opt/lessoncue`, keeps data at `/var/lib/lessoncue`, registers the systemd service, opens port 80 when UFW is installed, and publishes the Avahi service when available. Running it again upgrades the application while preserving accounts, configuration, media, screen credentials, and backups. Upgrading an older installation preserves its current port; an administrator can switch it to port 80 afterward in Settings.

The release includes architecture-matched `yt-dlp` and Deno helpers used only when an operator explicitly chooses **Download YouTube locally**. LessonCue passes the bundled Deno runtime to yt-dlp for current YouTube extraction, and FFmpeg inspects and thumbnails the resulting MP4. No separate Python, Node, Deno, or downloader installation is required.

Useful commands:

```bash
sudo systemctl status lessoncue
sudo journalctl -u lessoncue -f
sudo systemctl restart lessoncue
```

Run `sudo ./uninstall.sh` to remove the service. It deliberately preserves `/var/lib/lessoncue`.

## Windows service installation

Install a current static Windows build of FFmpeg/FFprobe and put it on the system `PATH`. LessonCue copies those binaries into its restricted media-worker directory during installation, applies LocalService-only ACLs, and blocks outbound network access for that private copy. Then download and unpack `LessonCue-Server-Windows-x64.zip`, open PowerShell as Administrator, and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install-LessonCue.ps1
```

The script installs an automatically starting Windows service, adds the firewall rule, and stores data in `C:\ProgramData\LessonCue`. `Uninstall-LessonCue.ps1` removes the service and application but preserves that data directory.

For production releases, download `SHA256SUMS` and `SHA256SUMS.sig` beside the ZIP and verify them from a trusted checkout before opening an elevated PowerShell window. The public-key fingerprint and exact commands are in [release-signing.md](release-signing.md). Linux's copy/paste installer performs this verification automatically before it executes the downloaded installer as root.

The browser port can be saved under **Settings → Connection & pairing**. On Windows, apply a changed port by opening PowerShell as Administrator, updating the matching Windows Firewall rule if necessary, and running `Restart-Service LessonCue`. Native Linux performs those steps automatically.

For password recovery, open PowerShell as Administrator and run:

```powershell
$env:LESSONCUE_DATA_PATH = "$env:ProgramData\LessonCue"
& "$env:ProgramFiles\LessonCue\LessonCue.Server.exe" --list-admins
& "$env:ProgramFiles\LessonCue\LessonCue.Server.exe" --reset-password YOUR_USERNAME
```

## First server check from another computer

Open these URLs from another computer on the television network, replacing `SERVER-IP` with the numeric address printed by the installer:

```text
http://SERVER-IP/health
http://SERVER-IP/.well-known/lessoncue
```

The first response should say `healthy`; the second should report the server identity and API version. The complete browser interface is at `http://SERVER-IP`.

## Android TV and Fire TV

For ordinary sideloading, download the production-signed APK from the same stable latest-release address:

```text
https://github.com/ProbablyModded/lessoncue/releases/latest/download/lessoncue-tv.apk
```

The matching test build remains available alongside it:

```text
https://github.com/ProbablyModded/lessoncue/releases/latest/download/LessonCue-AndroidTV-debug.apk
```

Use the debug build only for testing. Enable installation from unknown sources only for the file-manager or deployment tool you use, install the APK, then disable that permission again.

Updater-enabled production builds add **Check for updates** to the TV lesson library and perform one quiet background check every time the app launches. The first updater-enabled release must still be installed manually over the existing production app. Later releases can be downloaded and verified inside LessonCue; Android will ask once for **Allow from this source** and will always display its own final installation confirmation. Version 0.30.5 corrects false incompatible-certificate errors on Android 9–12 devices, including NVIDIA Shield TV, without relaxing production signature verification. LessonCue cannot and does not silently install updates.

On first launch:

1. Enter `http://lessoncue.local` or the numeric server address. Include `:PORT` only if an administrator selected a non-default port.
2. Enter the temporary six-digit pairing PIN.
3. Name and assign the screen in the server.
4. Leave the app open until its lesson reports ready for offline use.
5. Test with Wi-Fi disabled before the first live use.

Fire TV requires device-specific testing because background scheduling and storage behavior vary by Fire OS version.

## Apple TV and tvOS

Apple TV is explicitly unsupported and deferred for the current LessonCue product cycle. The archived `tvos/` prototype is not built in CI, distributed in releases, covered by current installation support, or included in feature-parity promises. Use the paired browser player on a presentation computer or an Android TV, Google TV, or Fire TV device. Reintroducing tvOS requires a separate future project and current-device acceptance testing.

## Network checklist

- Put TVs and the server on the same VLAN, or explicitly route LessonCue's selected TCP port and mDNS between them.
- Disable wireless client isolation for the trusted device network.
- Avoid guest Wi-Fi.
- Reserve the server IP in DHCP and record its numeric URL.
- Allow `_lessoncue._tcp` multicast DNS where supported.
- Keep a local keyboard/mouse and the numeric server address available as a recovery path.

## Backups and updates

Service Admins can create and download a consistent configuration backup or a full backup from **Settings → Privacy & backups** while the server is running. Every browser-created export requires a password of at least 12 characters and uses LessonCue's `.lcbak` format: a chunked AES-256-GCM envelope authenticates the complete ZIP digest, while the encrypted ZIP contains a SHA-256 manifest for every database, configuration, and media file. LessonCue never stores the export password. Keep it in a password manager separate from the backup; losing it makes the export unrecoverable.

By default, an export omits the account-email credential, Signage source credentials, pairing secret/PIN preference, and local ASP.NET data-protection keys. This is the safest choice for ordinary off-server storage and migration. Select **Include this server's encrypted provider credentials…** only when the receiving installation must inherit those protected values; LessonCue permits this only inside a password-encrypted export. The password is not placed in the archive. Cloudflare's root-owned tunnel token remains outside browser backups.

To restore a LessonCue `.lcbak` or legacy ZIP from the browser, open **Settings → Privacy & backups → Restore a LessonCue backup**, choose the file, and enter its password when required. LessonCue authenticates and decrypts the envelope, verifies every manifest entry and the SQLite database without changing current data, shows the organization and record counts, identifies whether server secrets were included, and warns whether media is included. Type `RESTORE` only after reviewing that preview. LessonCue creates a full local safety backup automatically, restores the database, restores media only when the uploaded archive is a full backup, and preserves the receiving server's identity, encryption keys, hostname, port, and pairing secrets. Authenticator MFA is disabled for restored accounts because the receiving server deliberately keeps its own encryption keys; users can enroll again after signing in, and the all-user MFA requirement is disabled. The decrypted staged upload expires after 24 hours and the password is discarded immediately after validation.

Use a backup produced by the same or an older LessonCue release. A newer server automatically applies required database upgrades after restoration. A configuration backup preserves media already on the receiving server; use a full backup when moving media to another computer. Legacy unencrypted ZIP backups remain readable but are identified clearly because they do not provide the authenticated envelope and per-file manifest used by current exports.

### Schedule and test recovery copies

Under **Settings → Privacy & backups → Scheduled and off-server backups**, a Service Admin can select daily or weekly execution, the local run hour, and (for weekly schedules) the weekday in the organization's configured time zone. Choose **2 AM** for an off-hours run. The same schedule controls the encrypted backup and any configured media sync. You can also choose configuration-only, full-media, or **Sync media to each remote destination**, plus the number and maximum age of local scheduled copies and the same server-secret exclusion policy used by manual exports. Enter the backup password once. LessonCue protects the scheduler's copy with its local data-protection key ring, never returns it through the API, and deliberately excludes `backup-policy.json` from every backup so the wrapped password is never packaged with those keys.

Optional off-site destinations are configured independently under **Off-site backup destinations**. Add Google Drive, a Nextcloud folder, an ownCloud folder, or another HTTPS WebDAV folder. For Google Drive, create a Google Cloud project, enable the Google Drive API, and create an OAuth client of type **Web application**. Add the exact callback URI shown in LessonCue to the client's authorized redirect URIs, then enter its client ID and secret and save the policy. Select **Connect Google Drive** and authorize the Google account with the storage plan. Use the OAuth consent screen's **Production** publishing status for persistent scheduled access: external apps left in Testing can have refresh tokens expire after seven days. LessonCue requests the `drive.file` scope and creates its own named folder under My Drive; it does not access arbitrary Drive files or Google Cloud Storage buckets. Access LessonCue through its public HTTPS address for the OAuth callback.

For WebDAV, use the provider's app password (or a bearer token), not the primary account password. An optional single folder name is created in the Google Drive My Drive root or below the WebDAV root; both the encrypted archive and the media mirror stay inside it. Each destination is encrypted locally before archive upload and has its own **Keep newest remote copies** count and **Delete remote copies older than** limit. In media-sync mode, LessonCue hashes the local media library, adds missing or changed files under `media/`, and removes only files listed in the previous LessonCue sync manifest that are no longer local. The first sync does not delete unrecognized remote media, so existing provider content is protected. Media files are synchronized as files rather than placed in the encrypted archive; use the provider's HTTPS and at-rest protection for them. Google Drive uploads use resumable upload sessions; cleanup is limited to LessonCue-managed files. WebDAV archive pruning lists the folder with `PROPFIND` and deletes only direct-child files whose names match `lessoncue-*.lcbak`; unrelated files are never touched. OAuth and WebDAV credentials are protected locally and omitted from backups and API responses. **Create and verify now** tests local encryption, manifest/database verification, every configured destination, and remote retention before you rely on it.

LessonCue raises a Service Admin banner when a scheduled backup fails or the latest successful verified copy is overdue. Local pruning applies both the “keep newest copies” and maximum-age limits, without deleting manually created or pre-restore safety backups. The **Run restore drill** action decrypts a selected copy, authenticates its envelope and every manifest entry, runs SQLite integrity and required-table checks, and compares the media inventory without changing production data. Complete the drill by downloading that exact file to another device, confirming the separately stored password, and periodically restoring it to a spare LessonCue server.

The operational recovery objectives are:

- With a healthy daily policy, the target recovery point is no more than 24 hours before a failure; with a weekly policy it is no more than seven days. A visible overdue or failed state means that objective is not currently met.
- A configuration-only restore should be rehearsed to complete within 30 minutes after a replacement server is available. A full-media restore time depends on archive size and disk/network throughput; measure and record it during the spare-server drill.
- Keep at least one verified copy on another physical device, Google Drive, or WebDAV service, retain the password in a separate password manager, and ensure two authorized people know the recovery procedure.

### Move directly to another LessonCue server

The migration workflow transfers the same encrypted `.lcbak` artifact and then uses the normal review-and-restore path:

1. On the source server, create and verify a current encrypted configuration or full backup.
2. Select **Transfer** beside that backup. Copy the displayed source address and 64-character one-time token. The grant expires after 30 minutes and is consumed by its first download.
3. On the destination server, install the same or a newer LessonCue release. Open **Privacy & backups → Move from another LessonCue server**.
4. Paste the source address and token, then enter the backup password separately. The password is used only on the destination and is never sent to the source.
5. Select **Transfer and preview**. LessonCue pulls the encrypted bytes, blocks HTTP redirects, authenticates/decrypts the envelope, verifies the manifest and database, and shows the same restore review used for an uploaded file.
6. Confirm the organization, record counts, media inclusion, source version, and secret-handling policy. Type `RESTORE` only on the intended destination.

Plain HTTP is accepted only for localhost, private/link-local numeric addresses, or `.local` hostnames. Use HTTPS for a Cloudflare or other remotely reachable source. The token is sent in the Authorization header rather than the URL so it is not written into ordinary request-path logs. If the transfer or password is wrong, create a new one-time token on the source.

For a manual whole-server disaster-recovery copy, stop the service first and archive the entire data directory:

```bash
sudo systemctl stop lessoncue
sudo tar -C /var/lib -czf "lessoncue-manual-$(date +%Y%m%d).tar.gz" lessoncue
sudo systemctl start lessoncue
```

To restore that manual whole-directory archive, install the same or newer LessonCue version, stop the service, replace `/var/lib/lessoncue` with the saved directory contents, restore ownership with `sudo chown -R lessoncue:lessoncue /var/lib/lessoncue`, and start the service. Test restoration on a separate machine before relying on a backup policy.

For Docker, pull/build the new image and run `docker compose up -d`. Native Linux installations check for releases daily and alert signed-in users. A user with **Software updates** permission can use **Settings → Software updates → Install**; LessonCue verifies the release checksum, stops database writers, creates and verifies a protected pre-update database/configuration snapshot, snapshots the updater and systemd units, and requires the new server's database and persistent storage to report ready. If migration or readiness fails, the application, database, configuration, updater, and service units are restored together. A root-owned transaction marker and boot recovery unit finish that restoration after an interrupted update or power loss. A **Service Admin** can also select **Restore last-known-good snapshot** on the update page; LessonCue takes and verifies a separate snapshot of the current installation before changing it, and restores that newer state if the requested rollback does not become ready. Media files are not rolled back. Run the two headless installation commands once on a server installed before version 0.4.0 to enable these protected services. Successful updates preserve `/var/lib/lessoncue`, including accounts, media, settings, pairing credentials, and backups.

In **Settings → Media & storage**, a Service Admin can choose a maximum amount of disk space or leave automatic allocation enabled. The page shows current LessonCue usage, free computer disk space, active-upload reservations, and the maximum safe allocation. LessonCue keeps a 512 MB safety reserve and reserves the complete file size before accepting chunks. The adjacent **Upload limits** panel optionally caps file size, per-account daily use, simultaneous sessions, user/role/class daily use, and verified codecs. Everyone with **Media uploads** permission can see remaining capacity. Uploaders can pause, resume, or cancel; after a browser/network interruption, selecting the same file and destination within 24 hours continues its missing chunks. Uploads marked **For a lesson** are automatically removed four weeks after the latest lesson that uses them; uploads marked **Keep permanently** are not automatically removed. See [resumable uploads and storage limits](uploads.md).

Media can be assigned to hierarchical folders and comma-separated tags during upload or later in the Media Library. **Manage versions & impact** shows every lesson cue and sign that uses an item before replacement. Replacing a local file preserves its stable media ID, archives the current original, refreshes affected screen manifests, and queues fresh metadata and preview processing. Previous originals can be downloaded or restored as a new current version. Archived versions count against the storage allocation and are removed with their parent media when its retention period ends.

LessonCue accepts broad video, audio, raster-image, and document families. In addition to common MP4/MOV/MKV/WebM/AVI/MPEG/transport-stream media, it accepts FLAC/Ogg/Opus/WMA/AIFF/AMR/AC-3 audio, GIF/BMP/TIFF/WebP/AVIF/HEIC/JPEG-XL/JPEG-2000 images, and PDF, legacy and macro-enabled Office, OpenDocument, Keynote/Pages/Numbers, RTF, text, CSV, and TSV documents. LessonCue validates each signature or package structure before processing; the extension and browser MIME type are never trusted by themselves.

Document files can be uploaded in the Media Library or directly on a lesson. Lesson uploads queue conversion and append the generated slides automatically at the chosen seconds per slide; library uploads also expose **Convert to slides** under **Manage versions & impact**. Shared Google Slides decks can be imported by URL from either location when **Anyone with the link can view** is enabled.

Local document conversion runs through headless LibreOffice and Poppler and creates PNG media with a maximum 1920-pixel dimension. Google Slides is downloaded once through Google's PDF export endpoint and then follows the same local conversion path; no LessonCue-hosted cloud service receives the deck. Static conversion intentionally loses transitions, builds, animations, embedded video, and presenter timing. Apple office packages depend on the LibreOffice importer available on the server and may need to be exported to PDF if that importer cannot open them. LessonCue shows the detected converter state in the Media upload dialog; missing optional WebP or Theora encoders remain visible as a processing diagnostic instead of causing a silent failure.

The recommended Linux installer and Docker image include both converters and the bounded media worker. For a manual Debian/Ubuntu install, run `sudo apt-get install -y ffmpeg libreoffice-impress libreoffice-writer libreoffice-calc libreoffice-draw poppler-utils util-linux coreutils`, then add the optional codec runtime packages your release names (for example `libavcodec-extra`, `libwebp7`, `libtheora0`, `libvpx9`/`libvpx7`, `libheif1`, `libavif16`/`libavif15`, `libjxl0.7`/`libjxl0`, and `libopenjp2-7`). `apt-cache search '^libvpx[0-9]+$'` and the equivalent searches for AVIF/JXL are useful when a versioned package name differs; the LessonCue installer selects an available variant automatically. On Windows, install a current FFmpeg build with `libwebp` and `libtheora` encoders, install LibreOffice system-wide, install a Poppler build, set the machine environment variable `LESSONCUE_PDFTOPPM_PATH` to `pdftoppm.exe`, and rerun the LessonCue installer so its outbound-deny rules include those converter binaries.

For a deck whose native animations must be retained, export it to H.264/AAC MP4 in PowerPoint, Keynote, or the originating application and upload that video. The [animation-preservation investigation](presentation-animation-preservation.md) explains why LessonCue does not offer a misleading headless “preserve animations” switch.

To remove the headless Linux service while preserving its database and media:

```bash
sudo systemctl disable --now lessoncue
sudo systemctl disable --now lessoncue-update.path 2>/dev/null || true
sudo systemctl stop lessoncue-update 2>/dev/null || true
sudo rm -f /etc/systemd/system/lessoncue.service /etc/systemd/system/lessoncue-update.service /etc/systemd/system/lessoncue-update.path
sudo rm -f /usr/local/sbin/lessoncue-update
sudo rm -f /etc/avahi/services/lessoncue.service
sudo systemctl daemon-reload
sudo rm -rf /opt/lessoncue
echo "LessonCue data remains in /var/lib/lessoncue"
```
