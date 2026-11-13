package org.lessoncue.tv

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class UpdateManifestTest {
    @get:Rule
    val temporaryFolder = TemporaryFolder()

    private val hosts = setOf("github.com", "release-assets.githubusercontent.com")

    @Test
    fun parsesCompleteStableManifest() {
        val parsed = UpdateManifestParser.parse(validManifest(), "stable", hosts)
        assertEquals(30L, parsed.versionCode)
        assertEquals("0.26.0", parsed.versionName)
        assertEquals(28_493_752L, parsed.fileSize)
        assertFalse(parsed.mandatory)
    }

    @Test
    fun rejectsUnsupportedSchema() {
        assertValidation(validManifest().replace("\"schemaVersion\":1", "\"schemaVersion\":2"))
    }

    @Test
    fun rejectsMalformedAndIncompleteResponses() {
        assertValidation("{")
        assertValidation(validManifest().replace("\"sha256\":\"${"a".repeat(64)}\",", ""))
        assertValidation(validManifest().replace("\"versionCode\":30", "\"versionCode\":30.5"))
        assertValidation(validManifest().replace("\"mandatory\":false", "\"mandatory\":\"false\""))
    }

    @Test
    fun rejectsWrongChannelAndUntrustedOrInsecureUrls() {
        assertValidation(validManifest().replace("\"channel\":\"stable\"", "\"channel\":\"beta\""))
        assertValidation(validManifest().replace("https://github.com", "http://github.com"))
        assertValidation(validManifest().replace("github.com", "example.com"))
    }

    @Test
    fun rejectsNonLowercaseOrMalformedSha256() {
        assertValidation(validManifest().replace("a".repeat(64), "A".repeat(64)))
        assertValidation(validManifest().replace("a".repeat(64), "abc"))
    }

    @Test
    fun rejectsOversizedApkMetadata() {
        assertValidation(
            validManifest().replace(
                "28493752",
                (UpdateManifestParser.MAX_APK_BYTES + 1).toString()
            )
        )
    }

    @Test
    fun comparesVersionCodeRatherThanVersionName() {
        val manifest = parsed().copy(versionCode = 30, versionName = "0.1")
        assertTrue(UpdatePolicy.isUpdateAvailable(manifest, 29))
        assertFalse(UpdatePolicy.isUpdateAvailable(manifest, 30))
    }

    @Test
    fun optionalDismissalIsIgnoredForManualChecks() {
        val manifest = parsed()
        assertFalse(UpdatePolicy.shouldPresent(manifest, 29, 30, manual = false))
        assertTrue(UpdatePolicy.shouldPresent(manifest, 29, 30, manual = true))
    }

    @Test
    fun mandatoryMinimumVersionCannotBeDismissed() {
        val manifest = parsed().copy(mandatory = true, minimumSupportedVersionCode = 30)
        assertTrue(UpdatePolicy.isBlocking(manifest, 29))
        assertTrue(UpdatePolicy.shouldPresent(manifest, 29, 30, manual = false))
        assertFalse(UpdatePolicy.isBlocking(manifest, 30))
    }

    @Test
    fun validatesDownloadedSizeAndSha256() {
        val file = temporaryFolder.newFile("update.apk").apply { writeText("LessonCue update") }
        val manifest = parsed().copy(fileSize = file.length(), sha256 = UpdateFileValidation.sha256(file))
        UpdateFileValidation.validate(file, manifest)
        assertThrows(UpdateValidationException::class.java) {
            UpdateFileValidation.validate(file, manifest.copy(fileSize = file.length() + 1))
        }
        assertThrows(UpdateValidationException::class.java) {
            UpdateFileValidation.validate(file, manifest.copy(sha256 = "0".repeat(64)))
        }
    }

    private fun parsed() = UpdateManifestParser.parse(validManifest(), "stable", hosts)

    private fun assertValidation(raw: String) {
        assertThrows(UpdateValidationException::class.java) {
            UpdateManifestParser.parse(raw, "stable", hosts)
        }
    }

    private fun validManifest() = """
        {
          "schemaVersion":1,
          "channel":"stable",
          "versionCode":30,
          "versionName":"0.26.0",
          "apkUrl":"https://github.com/ProbablyModded/lessoncue/releases/latest/download/lessoncue-tv.apk",
          "sha256":"${"a".repeat(64)}",
          "fileSize":28493752,
          "mandatory":false,
          "minimumSupportedVersionCode":1,
          "releaseNotes":"Secure self-updates."
        }
    """.trimIndent()
}
