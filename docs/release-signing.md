# Release signing and provenance

LessonCue releases use two independent trust systems:

1. An offline Ed25519 release key authenticates `SHA256SUMS`. The manifest contains every downloadable release file, so changing an archive, APK, AAB, SBOM, notice file, or update document invalidates verification.
2. GitHub artifact attestations bind the same checksums to the exact tagged workflow and attach the release SPDX SBOM as a signed predicate.

Linux installation and automatic updates trust the Ed25519 key embedded in `installers/linux/install-latest.sh` and installed root-owned at `/etc/lessoncue/release-signing-public.pem`. They verify `SHA256SUMS.sig` before reading a checksum, extracting an archive, stopping the service, or executing downloaded code as root. The expected public-key fingerprint is:

```text
SHA-256 (DER): dda6fcd087b5b37576aa8d0d84594478103a62aa2e072297b43270fa1908b3e5
```

## One-time repository configuration

The private key must never be committed. The project `.gitignore` excludes `.secrets/`; the maintainer working copy uses:

```text
.secrets/lessoncue-release-signing-private.pem
```

Keep at least one encrypted offline copy on a different device or password manager. File permissions should be `0600`. Before the next release, configure the private key as an encrypted Actions secret without printing it:

```bash
base64 < .secrets/lessoncue-release-signing-private.pem |
  gh secret set LESSONCUE_RELEASE_SIGNING_KEY_BASE64
```

Create a GitHub environment named `production-release`, restrict it to protected `v*` tags, and require a maintainer review. The release job names that environment and has no access to the signing secret until its protection rules pass.

Do not store the key in a workflow, issue, build artifact, release asset, container, server package, or LessonCue backup. The public key in `installers/release-signing-public.pem` is intentionally committed and distributed.

## Release checks

The tagged workflow:

- validates the exact commit before packaging;
- builds deterministic .NET outputs and Linux archives with sorted paths, fixed ownership, and the tagged commit time;
- refuses a signing key whose derived public-key fingerprint differs from the committed trust anchor;
- creates an SPDX JSON SBOM and a third-party inventory from the completed artifacts;
- creates `SHA256SUMS`, signs it with Ed25519, immediately verifies the signature with the committed public key, and re-hashes every file;
- produces GitHub/Sigstore provenance and SBOM attestations; and
- publishes only after the protected environment approves the job.

Verify a downloaded release manually:

```bash
openssl pkeyutl -verify -pubin \
  -inkey installers/release-signing-public.pem \
  -rawin -in SHA256SUMS \
  -sigfile SHA256SUMS.sig
sha256sum -c SHA256SUMS
```

Verify GitHub provenance:

```bash
gh attestation verify LessonCue-Server-linux-x64.tar.gz \
  --repo ProbablyModded/lessoncue
```

## Rotation and compromise recovery

Routine rotation and compromise response are different:

1. Stop tagged releases and disable the `production-release` environment.
2. Remove or replace `LESSONCUE_RELEASE_SIGNING_KEY_BASE64`.
3. Determine the last known-good signed release and publish an incident note. Do not delete evidence.
4. Generate a new Ed25519 key offline and create two offline backups.
5. Update the committed public key, embedded Linux bootstrap key, expected fingerprint, updater tests, and documentation together.
6. Ship the new trust anchor through an authenticated transition release signed by the old uncompromised key. If the old key may be compromised, require the SSH/manual installer and independently communicate the new fingerprint; automatic updates must not silently trust it.
7. Rotate any other release credentials exposed in the same event, including Android signing material, GitHub tokens, and deployment credentials.
8. Run the invalid-signature, interrupted-update, rollback, clean-install, and prior-release upgrade checks before re-enabling the protected environment.

A public-key change is a security migration, not an ordinary patch. Never weaken verification or accept both keys indefinitely merely to make an update succeed.
