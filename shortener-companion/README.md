# LessonCue Link Shortener Companion

This directory vendors the `companion/` application from
[ProbablyModded/Link-Shortener-Companion](https://github.com/ProbablyModded/Link-Shortener-Companion)
at commit `8f4f841` (2026-08-25), with LessonCue's password-reset integration
applied on top. It is built into the optional shortener
Compose service; it does not replace or proxy the Shlink API.

The companion calls Shlink server-side with the scoped console key. The key is
mounted as a Docker secret and is never sent to a browser. Its own admin and
Link Studio passwords are stored as password hashes in `data/ui-config.json`.

LessonCue can set both passwords through the one-shot
`COMPANION_PASSWORD_RESET_FILE` control file. The companion consumes that file
on its next request and then behaves like the upstream application: an
administrator can change either password later in **Access & brand**. Password
environment variables are deliberately not used because the upstream
application gives them precedence over its stored settings, which would make
web-client password changes appear to succeed and then stop working.

The upstream application is distributed under the MIT license; see
[LICENSE](./LICENSE).
