package org.lessoncue.tv

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant
import java.time.LocalDate

class TvUiModelsTest {
    private fun item(id: String, durationMs: Long? = 30_000) =
        CueItem(id, id.replaceFirstChar { it.uppercase() }, "video", "http://server/$id", durationMs = durationMs)

    private fun lesson(
        id: String = "lesson",
        start: Instant? = null,
        regular: List<CueItem> = listOf(item("lesson-item"))
    ) = LessonPlaylist(
        id = id,
        title = id,
        designatedStartAt = start,
        preRollStartsAt = null,
        countdown = CountdownCue("countdown", 60_000, null, item("countdown", 60_000)),
        preRoll = PreRollCue(listOf(item("pre-roll"))),
        postLesson = PreRollCue(listOf(item("post-lesson"))),
        items = regular
    )

    @Test
    fun timelineUsesActualPlaybackOrder() {
        val timeline = lesson().timeline()

        assertEquals(
            listOf(CueRole.PreRoll, CueRole.Countdown, CueRole.Lesson, CueRole.PostLesson),
            timeline.map { it.role }
        )
        assertEquals(listOf("pre-roll", "countdown", "lesson-item", "post-lesson"), timeline.map { it.item.id })
    }

    @Test
    fun manualLessonDownloadIncludesEveryPlaybackCue() {
        val playlist = lesson().let { source ->
            source.copy(
                preRoll = source.preRoll?.let { it.copy(items = it.items.map { item -> item.copy(offlineEligible = true) }) },
                countdown = source.countdown?.let { it.copy(item = it.item.copy(offlineEligible = true)) },
                items = source.items.map { it.copy(offlineEligible = true) },
                postLesson = source.postLesson?.let { it.copy(items = it.items.map { item -> item.copy(offlineEligible = true) }) }
            )
        }

        assertEquals(
            listOf("pre-roll", "countdown", "lesson-item", "post-lesson"),
            cacheableLessonItems(playlist).map { it.id }
        )
    }

    @Test
    fun initialCueFocusPrefersFirstRegularLessonCue() {
        assertEquals(2, lesson().initialCueIndex())
        assertEquals(1, lesson(regular = emptyList()).initialCueIndex())
    }

    @Test
    fun libraryFocusPrefersNearestFutureLessonAndSurvivesRefresh() {
        val now = Instant.parse("2026-08-04T14:00:00Z")
        val later = lesson("later", now.plusSeconds(7_200))
        val next = lesson("next", now.plusSeconds(900))
        val playlists = listOf(later, next, lesson("unscheduled"))

        assertEquals(1, preferredLessonIndex(playlists, now))
        assertEquals(0, retainedLessonIndex(playlists, "later", now))
    }

    @Test
    fun librarySortsNewestDatedLessonsFirstAndKeepsUndatedLessonsStable() {
        val undated = lesson("undated")
        val older = lesson("older").copy(lessonDate = LocalDate.of(2026, 7, 1))
        val newest = lesson("newest").copy(lessonDate = LocalDate.of(2026, 8, 1))

        assertEquals(
            listOf("newest", "older", "undated"),
            lessonsForLibrary(listOf(undated, older, newest)).map { it.id }
        )
    }

    @Test
    fun emergencyAndSignageOnlyModesDisableLessonInteraction() {
        val emergency = SignageCue(
            id = "alert", name = "Alert", mode = "emergency", priority = 10, message = "Evacuate",
            backgroundColor = "#000000", textColor = "#ffffff", mediaUrl = null
        )
        val playlist = lesson()

        assertEquals(
            LibraryInteractionMode.Emergency,
            ScreenManifest(1, "TV", listOf(emergency), listOf(playlist)).libraryInteractionMode()
        )
        assertEquals(
            LibraryInteractionMode.SignageOnly,
            ScreenManifest(1, "TV", emptyList(), listOf(playlist), signageOnly = true).libraryInteractionMode()
        )
    }

    @Test
    fun activePlaybackAndSignageKeepTheDisplayAwake() {
        val signage = SignageCue(
            id = "sign", name = "Sign", mode = "always", priority = 1, message = "Welcome",
            backgroundColor = "#000000", textColor = "#ffffff", mediaUrl = null
        )
        val off = signage.copy(displayPower = "off")

        assertTrue(shouldKeepScreenAwake(isPlayer = true, manifest = null))
        assertTrue(shouldKeepScreenAwake(isPlayer = false,
            manifest = ScreenManifest(1, "TV", listOf(signage), emptyList())))
        assertFalse(shouldKeepScreenAwake(isPlayer = false,
            manifest = ScreenManifest(1, "TV", listOf(off), emptyList())))
        assertFalse(shouldKeepScreenAwake(isPlayer = false, manifest = null))
    }

    @Test
    fun playbackOverlayTimesOutOnlyWhilePlaying() {
        assertTrue(shouldShowPlaybackOverlay(1_000, 4_999, playing = true))
        assertFalse(shouldShowPlaybackOverlay(1_000, 5_000, playing = true))
        assertTrue(shouldShowPlaybackOverlay(1_000, 20_000, playing = false))
        assertTrue(shouldShowPlaybackOverlay(1_000, 20_000, playing = true, hasError = true))
    }
}
