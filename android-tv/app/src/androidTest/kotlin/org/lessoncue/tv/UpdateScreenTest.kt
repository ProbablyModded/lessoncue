package org.lessoncue.tv

import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsFocused
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithText
import org.junit.Rule
import org.junit.Test

class UpdateScreenTest {
    @get:Rule
    val compose = createComposeRule()

    private val manifest = UpdateManifest(
        schemaVersion = 1,
        channel = "stable",
        versionCode = BuildConfig.VERSION_CODE + 1L,
        versionName = "next",
        apkUrl = "https://github.com/ProbablyModded/lessoncue/releases/latest/download/lessoncue-tv.apk",
        sha256 = "a".repeat(64),
        fileSize = 20L * 1024L * 1024L,
        mandatory = false,
        minimumSupportedVersionCode = 1,
        releaseNotes = "Adds secure television self-updates."
    )

    @Test
    fun optionalUpdateDefaultsFocusToDownloadAndOffersLater() {
        compose.setContent {
            UpdateScreen(
                UpdateUiState.Available("0.25.0", manifest, blocking = false, manualPresentation = true),
                {}, {}, {}, {}, {}, {}
            )
        }
        compose.onNodeWithText("Download and update").assertIsDisplayed().assertIsFocused()
        compose.onNodeWithText("Later").assertIsDisplayed()
    }

    @Test
    fun mandatoryUpdateDoesNotOfferLater() {
        compose.setContent {
            UpdateScreen(
                UpdateUiState.Available(
                    "0.25.0",
                    manifest.copy(
                        mandatory = true,
                        minimumSupportedVersionCode = BuildConfig.VERSION_CODE + 1L
                    ),
                    blocking = true,
                    manualPresentation = false
                ),
                {}, {}, {}, {}, {}, {}
            )
        }
        compose.onNodeWithText("Download and update").assertIsFocused()
        compose.onAllNodesWithText("Later").assertCountEquals(0)
        compose.onNodeWithText("REQUIRED UPDATE").assertIsDisplayed()
    }
}
