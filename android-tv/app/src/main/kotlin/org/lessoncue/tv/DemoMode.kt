package org.lessoncue.tv

/**
 * The Fire TV review demo is deliberately local. These values are handled
 * before endpoint discovery so the review path cannot generate DNS, HTTP,
 * pairing, or telemetry traffic.
 */
const val DEMO_SERVER_URL = "http://lsnq.demo"
const val DEMO_PASSWORD = "123456"
const val DEMO_PLAYLIST_ID = "lessoncue-review-demo"
const val LESSONCUE_PRIVACY_POLICY_URL = "https://probablymodded.github.io/lessoncue/privacy.html"

/**
 * A demo identity is only used by the shared TV screens to render the same
 * library, lesson-detail, and player UI as a paired display. MainActivity
 * never assigns it to activeIdentity, so no polling, pairing, telemetry, or
 * media-cache request can be made for the demo marker.
 */
val DEMO_IDENTITY = DeviceIdentity(
    screenId = "lessoncue-review-demo-screen",
    token = "local-demo",
    serverUrl = DEMO_SERVER_URL
)

/**
 * Build the review lesson from the APK resource rather than a network URL.
 * The resulting manifest intentionally uses the production TV data model so
 * the Amazon review path exercises the same composables and remote navigation
 * as normal mode.
 */
fun demoManifest(packageName: String): ScreenManifest {
    val bundledVideo = "android.resource://$packageName/${R.raw.lessoncue_demo}"
    fun item(id: String, title: String, endBehavior: String) = CueItem(
        id = id,
        title = title,
        type = "video",
        url = bundledVideo,
        contentType = "video/mp4",
        fileExtension = "mp4",
        endBehavior = endBehavior,
        offlineEligible = false,
        renderSupport = "supported"
    )
    val preRoll = item("lessoncue-review-demo-pre-roll", "Sample pre-roll", "advance")
    val countdown = item("lessoncue-review-demo-countdown", "Sample countdown", "advance")
    val lesson = item("lessoncue-review-demo-video", "Sample lesson video", "advance")
    val postRoll = item("lessoncue-review-demo-post-roll", "Sample post-roll", "loop")
    val playlist = LessonPlaylist(
        id = DEMO_PLAYLIST_ID,
        title = "Sample lesson",
        designatedStartAt = null,
        preRollStartsAt = null,
        countdown = CountdownCue(countdown.id, durationMs = 5_000L, startAt = null, item = countdown),
        preRoll = PreRollCue(listOf(preRoll)),
        postLesson = PreRollCue(listOf(postRoll)),
        items = listOf(lesson)
    )
    return ScreenManifest(
        version = 1,
        screenName = "LessonCue demo",
        signage = emptyList(),
        playlists = listOf(playlist)
    )
}

fun isBundledDemoMedia(url: String?): Boolean = url?.startsWith("android.resource://") == true

fun isDemoServerUrl(value: String): Boolean =
    value.trim().trimEnd('/').equals(DEMO_SERVER_URL, ignoreCase = true)

fun isDemoPassword(value: String): Boolean = value == DEMO_PASSWORD
