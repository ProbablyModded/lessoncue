package org.lessoncue.tv

import android.content.Context
import android.content.Intent
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import java.io.File
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.coroutines.Continuation
import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

@RunWith(AndroidJUnit4::class)
class UpdateManagerIntegrationTest {
    private lateinit var context: Context
    private lateinit var scope: CoroutineScope
    private var manager: UpdateManager? = null

    @Before
    fun setUp() {
        context = InstrumentationRegistry.getInstrumentation().targetContext
        scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        UpdateInstallEvents.clearPersisted(context)
    }

    @After
    fun tearDown() {
        manager?.close()
        UpdateInstallEvents.clearPersisted(context)
    }

    @Test
    fun manualCheckReportsCurrentThenAvailable() = runBlocking {
        val source = FakeSource(manifest(versionCode = BuildConfig.VERSION_CODE.toLong()))
        val subject = manager(source = source)

        subject.checkManually()
        subject.awaitState<UpdateUiState.Current>()

        source.manifest = manifest()
        subject.checkManually()
        val available = subject.awaitState<UpdateUiState.Available>()

        assertTrue(available.manualPresentation)
        assertEquals(BuildConfig.VERSION_CODE + 1L, available.manifest.versionCode)
        assertEquals(2, source.checkCount)
    }

    @Test
    fun canceledOlderCheckCannotReplaceNewerAvailableUpdate() = runBlocking {
        val firstRequest = CompletableDeferred<Continuation<UpdateManifest>>()
        val source = FakeSource(manifest())
        source.fetchOverride = {
            if (source.checkCount == 1) suspendCoroutine { firstRequest.complete(it) }
            else source.manifest
        }
        val subject = manager(source = source)
        subject.checkManually()
        val delayed = withTimeout(5_000) { firstRequest.await() }
        try {
            subject.checkManually()
            subject.awaitState<UpdateUiState.Available>()
        } finally {
            // Simulate an uncancellable network operation returning after its
            // caller was canceled. It must not erase the newer result.
            delayed.resume(manifest(versionCode = BuildConfig.VERSION_CODE.toLong()))
        }
        delay(100)
        assertTrue(subject.state.value is UpdateUiState.Available)
    }

    @Test
    fun automaticCheckRunsOnceOnEveryAppLaunch() = runBlocking {
        val source = FakeSource(manifest())
        val store = MemoryUpdateStore()
        val firstLaunch = manager(source = source, store = store)
        firstLaunch.startAutomaticCheck()
        firstLaunch.awaitState<UpdateUiState.Available>()
        firstLaunch.startAutomaticCheck()
        delay(50)
        assertEquals(1, source.checkCount)
        firstLaunch.close()

        val secondLaunch = manager(source = source, store = store)
        secondLaunch.startAutomaticCheck()
        secondLaunch.awaitState<UpdateUiState.Available>()

        assertEquals(2, source.checkCount)
    }

    @Test
    fun cancelDownloadReturnsToRetryableAvailableState() = runBlocking {
        val source = FakeSource(manifest(), suspendDownload = true)
        val subject = manager(source = source)
        subject.checkManually()
        subject.awaitState<UpdateUiState.Available>()

        subject.downloadAndInstall()
        subject.awaitState<UpdateUiState.Downloading>()
        subject.cancelDownload()

        subject.awaitState<UpdateUiState.Available>()
        assertTrue(source.downloadCanceled)
    }

    @Test
    fun failedManualCheckRetriesTheNetworkCheck() = runBlocking {
        val source = FakeSource(manifest(), fetchFailure = "Network unavailable")
        val subject = manager(source = source)

        subject.checkManually()
        val error = subject.awaitState<UpdateUiState.Error>()
        assertEquals("Network unavailable", error.message)

        source.fetchFailure = null
        subject.retry()
        subject.awaitState<UpdateUiState.Available>()
        assertEquals(2, source.checkCount)
    }

    @Test
    fun permissionReturnResumesVerifiedInstallation() = runBlocking {
        val installer = FakeInstaller(allowed = false)
        val subject = manager(installer = installer)
        subject.checkManually()
        subject.awaitState<UpdateUiState.Available>()
        subject.downloadAndInstall()
        subject.awaitState<UpdateUiState.PermissionRequired>()

        installer.allowed = true
        subject.onPermissionSettingsReturned()

        withTimeout(5_000) {
            while (installer.installCount != 1) delay(10)
        }
        assertTrue(subject.state.value is UpdateUiState.Installing)
    }

    @Test
    fun installationCallbacksArePersistedAndPresented() = runBlocking {
        val subject = manager()
        subject.checkManually()
        subject.awaitState<UpdateUiState.Available>()

        UpdateInstallEvents.publish(context, InstallEvent.Failure("Test installation failure"))
        val error = subject.awaitState<UpdateUiState.Error>()
        assertEquals("Test installation failure", error.message)

        UpdateInstallEvents.publish(context, InstallEvent.Success)
        subject.awaitState<UpdateUiState.Current>()
        Unit
    }

    private fun manager(
        source: FakeSource = FakeSource(manifest()),
        installer: FakeInstaller = FakeInstaller(allowed = true),
        store: MemoryUpdateStore = MemoryUpdateStore()
    ): UpdateManager {
        return UpdateManager(
            context = context,
            scope = scope,
            store = store,
            client = source,
            verifier = FakeVerifier(),
            installer = installer,
            automaticStartDelayMillis = 0
        ).also { manager = it }
    }

    private fun manifest(versionCode: Long = BuildConfig.VERSION_CODE + 1L) = UpdateManifest(
        schemaVersion = 1,
        channel = "stable",
        versionCode = versionCode,
        versionName = "test-$versionCode",
        apkUrl = "https://github.com/ProbablyModded/lessoncue/releases/download/test/lessoncue-tv.apk",
        sha256 = "a".repeat(64),
        fileSize = null,
        mandatory = false,
        minimumSupportedVersionCode = 1,
        releaseNotes = "Integration test update"
    )

    private suspend inline fun <reified T : UpdateUiState> UpdateManager.awaitState(): T =
        withTimeout(5_000) { state.first { it is T } as T }

    private class MemoryUpdateStore : UpdatePreferencesStore {
        private var preferences = UpdatePreferences()

        override suspend fun load() = preferences

        override suspend fun recordSuccessfulAutomaticCheck(atMillis: Long) {
            preferences = preferences.copy(lastSuccessfulAutomaticCheckMillis = atMillis)
        }

        override suspend fun dismiss(versionCode: Long) {
            preferences = preferences.copy(dismissedVersionCode = versionCode)
        }

        override suspend fun clearDismissal() {
            preferences = preferences.copy(dismissedVersionCode = null)
        }
    }

    private class FakeSource(
        var manifest: UpdateManifest,
        private val suspendDownload: Boolean = false,
        var fetchFailure: String? = null
    ) : UpdateSource {
        var checkCount = 0
        var downloadCanceled = false
        var fetchOverride: (suspend () -> UpdateManifest)? = null

        override suspend fun cleanInterruptedDownloads(directory: File) = Unit

        override suspend fun fetchManifest(): UpdateManifest {
            checkCount += 1
            fetchOverride?.let { return it() }
            fetchFailure?.let { throw UpdateValidationException(it) }
            return manifest
        }

        override suspend fun download(
            manifest: UpdateManifest,
            directory: File,
            onProgress: (downloadedBytes: Long, totalBytes: Long?) -> Unit
        ): DownloadedApk {
            if (suspendDownload) {
                try {
                    delay(Long.MAX_VALUE)
                } finally {
                    downloadCanceled = true
                }
            }
            directory.mkdirs()
            val file = directory.resolve("integration-${manifest.versionCode}.apk")
            file.writeText("integration update")
            onProgress(file.length(), file.length())
            return DownloadedApk(file, manifest.apkUrl)
        }
    }

    private class FakeVerifier : UpdateApkVerifier {
        override fun verify(file: File, manifest: UpdateManifest) = ApkIdentity(
            packageName = "org.lessoncue.tv",
            versionCode = manifest.versionCode,
            certificateSha256 = setOf("A".repeat(64))
        )
    }

    private class FakeInstaller(var allowed: Boolean) : UpdatePackageInstaller {
        var installCount = 0

        override fun canRequestPackageInstalls() = allowed

        override fun permissionIntent() = Intent()

        override suspend fun install(file: File) {
            installCount += 1
        }
    }
}
