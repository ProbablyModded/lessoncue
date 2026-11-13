package org.lessoncue.tv

import android.content.pm.PackageManager
import org.junit.Assert.assertThrows
import org.junit.Assert.assertEquals
import org.junit.Test

class ApkIdentityPolicyTest {
    private val certificate = "E875F8F9F4E80494DF1658D5E59662BE1048D7CD5D53DB2131103051352F64AE"
    private val manifest = UpdateManifest(
        schemaVersion = 1,
        channel = "stable",
        versionCode = 30,
        versionName = "0.26.0",
        apkUrl = "https://github.com/ProbablyModded/lessoncue/releases/latest/download/lessoncue-tv.apk",
        sha256 = "a".repeat(64),
        fileSize = 100,
        mandatory = false,
        minimumSupportedVersionCode = 1,
        releaseNotes = "Update"
    )
    private val installed = ApkIdentity("org.lessoncue.tv", 29, setOf(certificate))

    @Test
    fun acceptsSamePackageHigherVersionAndCompatibleCertificate() {
        ApkIdentityPolicy.validate(
            manifest,
            installed,
            ApkIdentity("org.lessoncue.tv", 30, setOf(certificate)),
            certificate.lowercase()
        )
    }

    @Test
    fun acceptsCertificateHistoryContainingInstalledIdentity() {
        ApkIdentityPolicy.validate(
            manifest,
            installed,
            ApkIdentity("org.lessoncue.tv", 30, setOf("B".repeat(64), certificate)),
            certificate
        )
    }

    @Test
    fun rejectsWrongPackage() {
        assertRejected(ApkIdentity("example.other", 30, setOf(certificate)))
    }

    @Test
    fun rejectsLowerOrEqualVersionCode() {
        assertRejected(ApkIdentity("org.lessoncue.tv", 29, setOf(certificate)))
    }

    @Test
    fun rejectsManifestAndApkVersionMismatch() {
        assertRejected(ApkIdentity("org.lessoncue.tv", 31, setOf(certificate)))
    }

    @Test
    fun rejectsWrongSigningIdentity() {
        assertRejected(ApkIdentity("org.lessoncue.tv", 30, setOf("B".repeat(64))))
    }

    @Test
    @Suppress("DEPRECATION")
    fun androidNineThroughTwelveRequestSigningInfoInsteadOfLegacySignatures() {
        for (apiLevel in 28..32) {
            assertEquals(
                PackageManager.GET_SIGNING_CERTIFICATES,
                ApkVerifier.signingCertificateFlags(apiLevel)
            )
        }
        assertEquals(PackageManager.GET_SIGNATURES, ApkVerifier.signingCertificateFlags(27))
    }

    private fun assertRejected(downloaded: ApkIdentity) {
        assertThrows(UpdateValidationException::class.java) {
            ApkIdentityPolicy.validate(manifest, installed, downloaded, certificate)
        }
    }
}
