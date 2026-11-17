package org.lessoncue.tv

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.ExperimentalTestApi
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsFocused
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performKeyInput
import androidx.compose.ui.test.performSemanticsAction
import androidx.compose.ui.test.pressKey
import java.time.Instant
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

@OptIn(ExperimentalTestApi::class)
class TvScreenTest {
    @get:Rule
    val compose = createComposeRule()

    private val identity = DeviceIdentity("screen", "token", "http://lessoncue.local")

    private fun cue(id: String, type: String = "video") = CueItem(
        id = id,
        title = id.replace('-', ' ').replaceFirstChar { it.uppercase() },
        type = type,
        url = "http://lessoncue.local/media/$id",
        durationMs = 90_000
    )

    private fun lesson(id: String, start: Instant? = null) = LessonPlaylist(
        id = id,
        title = id.replace('-', ' ').replaceFirstChar { it.uppercase() },
        designatedStartAt = start,
        preRollStartsAt = null,
        countdown = CountdownCue("countdown-$id", 60_000, null, cue("countdown-$id")),
        preRoll = PreRollCue(listOf(cue("pre-$id"))),
        postLesson = null,
        items = listOf(cue("main-$id"), cue("second-$id", "image"))
    )

    @Test
    fun preferredUpcomingLessonIsVisibleAndFocusable() {
        val now = Instant.now()
        val manifest = ScreenManifest(
            1,
            "Room 204",
            emptyList(),
            listOf(lesson("later", now.plusSeconds(7_200)), lesson("next", now.plusSeconds(900)))
        )

        compose.setContent {
            LibraryScreen(manifest, identity, ConnectionMode.Online, onStart = {}, onCheckForUpdates = null)
        }

        compose.onNodeWithTag("lesson-card-next").assertIsDisplayed()
            .performSemanticsAction(SemanticsActions.RequestFocus)
            .assertIsFocused()
    }

    @Test
    fun featuredLessonMovesDownIntoTheLessonList() {
        val manifest = ScreenManifest(
            1,
            "Room 204",
            emptyList(),
            listOf(lesson("next"), lesson("later"))
        )

        compose.setContent {
            LibraryScreen(manifest, identity, ConnectionMode.Online, onStart = {}, onCheckForUpdates = null)
        }

        compose.onNodeWithTag("lesson-card-next")
            .performSemanticsAction(SemanticsActions.RequestFocus)
            .performKeyInput { pressKey(Key.DirectionDown) }
        compose.onNodeWithTag("lesson-card-later").assertIsFocused()
    }

    @Test
    fun landingPageUsesTheNewHomeSectionsAndFourCuePreview() {
        val manifest = ScreenManifest(
            1,
            "Room 204",
            emptyList(),
            listOf(lesson("animals"))
        )

        compose.setContent {
            LibraryScreen(
                manifest,
                identity,
                ConnectionMode.Online,
                onStart = {},
                onCheckForUpdates = {}
            )
        }

        compose.onNodeWithText("Up Next").assertIsDisplayed()
        compose.onNodeWithText("Available Lessons").assertIsDisplayed()
        compose.onNodeWithText("Today's lessons").assertDoesNotExist()
        compose.onNodeWithText("⇩  Updates").assertIsDisplayed()
        compose.onNodeWithText("MEDIA CACHED").assertDoesNotExist()
        compose.onNodeWithText("v${BuildConfig.VERSION_NAME}").assertIsDisplayed()
        compose.onNodeWithTag("up-next-cue-pre-animals").assertIsDisplayed()
        compose.onNodeWithTag("up-next-cue-countdown-animals").assertIsDisplayed()
        compose.onNodeWithTag("up-next-cue-main-animals").assertIsDisplayed()
        compose.onNodeWithTag("up-next-cue-second-animals").assertIsDisplayed()
    }

    @Test
    fun lessonDetailFocusesFirstRegularCueAndBackReturnsToLibrary() {
        var backed = false
        val playlist = lesson("lesson")
        compose.setContent {
            LessonDetailScreen(
                playlist,
                "Room 204",
                ConnectionMode.Cached,
                onBack = { backed = true },
                onPlay = { _, _ -> }
            )
        }

        compose.onNodeWithTag("cue-card-main-lesson")
            .performSemanticsAction(SemanticsActions.RequestFocus)
            .assertIsFocused()
            .assertIsDisplayed()
        compose.onNodeWithTag("back-to-lessons")
            .performSemanticsAction(SemanticsActions.RequestFocus)
            .assertIsFocused()
            .performKeyInput { pressKey(Key.DirectionCenter) }
        compose.runOnIdle { assertTrue(backed) }
    }

    @Test
    fun emergencySignageDisablesLessonCards() {
        val emergency = SignageCue(
            id = "emergency",
            name = "Emergency",
            mode = "emergency",
            priority = 10,
            message = "Please leave the building",
            backgroundColor = "#101815",
            textColor = "#F7F2E8",
            mediaUrl = null
        )
        val manifest = ScreenManifest(1, "Room 204", listOf(emergency), listOf(lesson("lesson")))

        compose.setContent {
            LibraryScreen(manifest, identity, ConnectionMode.Online, onStart = {}, onCheckForUpdates = null)
        }

        compose.onNodeWithTag("lesson-card-lesson").assertDoesNotExist()
        compose.onNodeWithText("EMERGENCY OVERRIDE").assertIsDisplayed()
        compose.onNodeWithText("Lesson controls are temporarily unavailable. Interrupted playback will resume automatically.")
            .assertIsDisplayed()
    }

    @Test
    fun activityPlaybackOverlayKeepsOnlyCompactNavigationControls() {
        val activity = cue("animal-game", "activity").copy(
            playbackUrl = "http://lessoncue.local/activity-display?definitionId=animal-game"
        )

        compose.setContent {
            Box(Modifier.fillMaxSize()) {
                PlaybackOverlay(
                    visible = true,
                    lessonTitle = "Animal Adventure",
                    item = activity,
                    itemIndex = 2,
                    itemCount = 27,
                    positionMs = 0,
                    durationMs = null,
                    playing = true,
                    availabilityLabel = "ACTIVITY LIVE",
                    actions = PlaybackOverlayActions({}, {}, {}, {}, {}, {}),
                    modifier = Modifier.fillMaxSize()
                )
            }
        }

        compose.onNodeWithText("USE THE WEB CONTROLLER TO PLAY").assertIsDisplayed()
        compose.onNodeWithText("‹  Back").assertIsDisplayed()
        compose.onNodeWithText("↶  Previous").assertIsDisplayed()
        compose.onNodeWithText("↷  Next").assertIsDisplayed()
        compose.onNodeWithText("−5  Rewind").assertDoesNotExist()
        compose.onNodeWithText("Ⅱ  Pause").assertDoesNotExist()
    }
}
