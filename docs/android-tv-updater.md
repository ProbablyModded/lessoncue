# Android TV self-update system

LessonCue's **Sideload** Android TV build is distributed directly from public GitHub Releases. It does not use the Google Play In-App Updates API and cannot silently update an ordinary consumer Android TV or Google TV device. LessonCue downloads and independently verifies the APK, then hands it to Android's official `PackageInstaller`; Android always owns the final user-confirmation screen.

The **store** build is produced from the same playback code and application ID but removes `REQUEST_INSTALL_PACKAGES`, disables all GitHub update checks and controls, and relies exclusively on Google Play or the Amazon Appstore for updates.

The Android client’s supported renderers, browser-only signage behavior, and safe fallback
rules are listed in the [display compatibility contract](display-compatibility.md). LessonCue
warns before an operator deliberately assigns unsupported content to an Android display.

## Architecture

The updater follows the existing application conventions:

- Kotlin and Compose for TV on API 26–36.
- `HttpURLConnection` for HTTPS requests, with explicit timeouts and redirect validation.
- DataStore for the last successful automatic check record and optional-update dismissal.
- An app-private `cacheDir/updates` directory for temporary and verified APK files.
- `PackageManager` for archive metadata and signing-certificate inspection.
- `PackageInstaller` full-install sessions for the system installation flow.
- Lifecycle-bound coroutines for checks and downloads.

No repository credentials, GitHub tokens, signing keys, or private server credentials are stored in the APK.

## Build configuration

The following generated `BuildConfig` values isolate distribution policy from updater code:

| Value | Production setting |
| --- | --- |
| `UPDATE_ENABLED` | `true` for the sideload flavor and `false` for the store flavor |
| `UPDATE_MANIFEST_URL` | `https://github.com/ProbablyModded/lessoncue/releases/latest/download/update.json` |
| `UPDATE_CHANNEL` | `stable` |
| `UPDATE_ALLOWED_HOSTS` | `github.com`, `objects.githubusercontent.com`, and `release-assets.githubusercontent.com` |
| `UPDATE_SIGNING_CERT_SHA256` | LessonCue's permanent production certificate fingerprint |

The sideload flavor includes `REQUEST_INSTALL_PACKAGES`, external update checks, and the installer flow. The store manifest explicitly removes the install-packages permission and installer receiver, while its generated `UPDATE_ENABLED` value removes update checks and controls from the interface. Playback, discovery, pairing, and caching remain shared.

Debug builds keep the updater interface disabled because Android cannot install the production-signed APK over a debug-signed application. To move a test device from debug to production, uninstall the debug build, install the production APK, and pair the device again. Production-to-production updates install in place and retain application data.

## Manifest schema

Every tagged release publishes an `update.json` asset:

```json
{
  "schemaVersion": 1,
  "channel": "stable",
  "versionCode": 30,
  "versionName": "0.26.0",
  "apkUrl": "https://github.com/ProbablyModded/lessoncue/releases/download/v0.26.0/lessoncue-tv.apk",
  "sha256": "lowercase 64-character SHA-256",
  "fileSize": 11000000,
  "mandatory": false,
  "minimumSupportedVersionCode": 1,
  "releaseNotes": "Release summary"
}
```

The client rejects unsupported schemas, missing or incorrectly typed fields, nonpositive or fractional version codes, another channel, non-HTTPS URLs, user information in URLs, untrusted hosts, malformed or uppercase hashes, invalid sizes, and responses larger than 1 MiB. Update availability is determined only by `versionCode`.

## Check behavior

- LessonCue waits briefly after normal startup and checks once on every app launch.
- Automatic failures are silent and never interrupt playback or pairing.
- **Check for updates** on the lesson library always contacts the release endpoint and reports current, available, or unable to check.
- Choosing **Later** records that optional `versionCode`. Automatic checks do not show it again, while a manual check always does.
- A release blocks normal use only when `mandatory` is true and the installed version is below `minimumSupportedVersionCode`. Required screens retain download, error, permission, and retry paths.

## Download and verification

Downloads use connection and read timeouts, reject untrusted redirects, and stream into `cacheDir/updates/*.part`. LessonCue:

1. prevents concurrent update downloads;
2. removes obsolete update APKs;
3. enforces a 500 MiB absolute maximum;
4. checks the received byte count when `fileSize` is present;
5. checks the manifest SHA-256;
6. atomically renames only a verified file;
7. removes partial or invalid files after cancellation, network loss, HTTP failure, storage errors, or process interruption;
8. asks Android to parse the APK;
9. verifies `org.lessoncue.tv`;
10. requires a greater APK `versionCode` that exactly matches the manifest;
11. compares installed and downloaded signing certificates, including available signing history; and
12. requires the permanent LessonCue production certificate in production builds.

Android 9 through Android 12 require `GET_SIGNING_CERTIFICATES` through the legacy integer-flags `PackageManager` overload. LessonCue uses that combination and falls back to the legacy signature array for OEMs that omit `signingInfo`. This avoids a false incompatible-certificate result on Shield TV and similar devices while retaining the same package, version, hash, installed-certificate, and permanent-production-certificate checks.

The original and every redirected URL must remain HTTPS and on the explicit host allowlist.

## Android installation permission and confirmation

If Android has not granted **Allow from this source**:

1. LessonCue explains why the permission is required.
2. The operator explicitly selects **Open Android settings**.
3. LessonCue opens `ACTION_MANAGE_UNKNOWN_APP_SOURCES` for its own package.
4. The operator enables the setting and returns.
5. LessonCue checks the permission again; it never reopens Settings automatically.

If that settings activity is unavailable, the screen gives the manual path: **Settings → Apps → Special app access → Install unknown apps → LessonCue**.

After verification and permission, LessonCue creates a full-install `PackageInstaller` session, declares the package and expected size, streams the APK, calls `fsync`, and commits through an explicit mutable `PendingIntent`. It handles pending confirmation, success, cancellation, blocked installs, package conflicts, incompatible or invalid APKs, insufficient storage, and generic failure. Only the system-provided confirmation intent is opened.

## Publishing a release

The production application ID and signing identity must never change. Before tagging:

1. Increase Android `versionCode`.
2. Update `versionName` and the other LessonCue package/client versions.
3. Run unit tests, lint, debug build, instrumentation APK compilation, and a production-signed release build.
4. Confirm no keystore, recovery document, password, token, or signing material is tracked by Git.
5. Push the reviewed commit and create a `v*` tag.

The release workflow:

- loads the existing keystore only from GitHub Actions secrets;
- builds the signed GitHub sideload APK, signed store APK, and signed Google Play Android App Bundle;
- verifies the production signing-certificate fingerprint;
- extracts the actual package version from the signed APK;
- creates `lessoncue-tv.apk.sha256` and `update.json`;
- publishes `lessoncue-tv.apk`, `LessonCue-TV-store.apk`, `LessonCue-TV-Google-Play.aab`, both server architectures, Windows, an SPDX SBOM, third-party notices, and an Ed25519-signed `SHA256SUMS`; and
- fails before publishing if any production signing or metadata check fails.

Required repository secrets remain:

- `ANDROID_SIGNING_KEY_BASE64`
- `ANDROID_SIGNING_KEYSTORE_PASSWORD`
- `ANDROID_SIGNING_KEY_ALIAS`
- `ANDROID_SIGNING_KEY_PASSWORD`
- `ANDROID_SIGNING_CERT_SHA256`

Keep the keystore and both passwords in at least two encrypted, access-controlled locations, including one offline copy. Losing the signing identity prevents installed devices from accepting future in-place updates.

## First updater release and safe testing

Versions before the updater exists cannot discover it. Install the first updater-enabled production APK manually over the existing production-signed application. Do not uninstall first; an in-place install retains pairing, settings, cached media, and the application identity.

For a safe end-to-end test:

1. Keep one nonproduction television on the previous signed release.
2. Publish a higher `versionCode` with `mandatory: false`.
3. Open **Check for updates** using only the D-pad.
4. Verify the version, notes, and size.
5. Choose **Later**, then confirm a manual check still shows the release.
6. Download, deny unknown-app permission, return, and verify the app remains usable.
7. grant permission, return, and select the update again;
8. cancel Android's confirmation once and verify retry;
9. install, relaunch LessonCue, and confirm pairing, settings, assignments, and cached media remain.

## Acceptance matrix

- [ ] Current version reports **LessonCue is current**.
- [ ] Optional update can be postponed.
- [ ] Manual check shows a previously postponed version.
- [ ] Permission denial returns to a usable permission screen without a settings loop.
- [ ] Permission approval resumes the verified installation.
- [ ] Canceling a download removes the partial file and allows retry.
- [ ] Interrupting network access reports an error and allows retry.
- [ ] Process termination during download leaves no accepted partial APK.
- [ ] Wrong SHA-256 is rejected before package parsing.
- [ ] Wrong application ID is rejected.
- [ ] Wrong signing key is rejected.
- [ ] Lower or equal APK version code is rejected.
- [ ] Manifest/APK version disagreement is rejected.
- [ ] System confirmation cancellation reports a retryable failure.
- [ ] Successful update retains pairing, settings, assignments, and cached media.
- [ ] App restarts normally after installation.
- [ ] Every updater action is reachable and understandable with D-pad only.
- [ ] Mandatory update blocks normal use only below `minimumSupportedVersionCode`.
- [ ] Mandatory download, permission, error, and retry paths remain usable.

For v0.26.0, 20 JVM tests passed (including 17 updater policy, parser, hash, size, package, version, and signing tests), and all 7 updater instrumentation tests passed on the Android 16 Google TV 1080p emulator. The instrumentation suite exercises default focus, optional and mandatory controls, manual current/available/error-and-retry results, cancellation, permission return, and persisted installer callbacks. Unit tests, lint, debug APK, and instrumentation-APK compilation also run in CI.

The physical-device rows above must still be exercised on representative Google TV, Android TV, and Fire TV hardware before declaring a model supported.

## Rollback and recovery

Android normally rejects installing a lower `versionCode`. The safe production rollback is a corrective APK with a new, higher version code signed by the same production identity. Uninstalling and installing an older version discards application data and should be reserved for a documented disaster-recovery test. A bad or compromised manifest can be removed from the latest GitHub release to stop new downloads while a corrective release is prepared.

Silent installation is unavailable on ordinary consumer devices by Android design. Device-owner or enterprise-management installation is outside LessonCue's current scope.
