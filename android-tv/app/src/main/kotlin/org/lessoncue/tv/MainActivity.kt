package org.lessoncue.tv

import android.annotation.SuppressLint
import android.content.ActivityNotFoundException
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.graphics.Bitmap
import android.graphics.Color as AndroidColor
import android.util.Log
import android.view.PixelCopy
import android.view.View
import android.view.WindowManager
import android.webkit.ConsoleMessage
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebChromeClient
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.basicMarquee
import androidx.compose.foundation.border
import androidx.compose.foundation.focusable
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.relocation.BringIntoViewRequester
import androidx.compose.foundation.relocation.bringIntoViewRequester
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.focusProperties
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.zIndex
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.InputMode
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalInputModeManager
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.viewinterop.AndroidView
import androidx.media3.common.MediaItem
import androidx.media3.common.C
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.tv.material3.Button
import androidx.tv.material3.ButtonDefaults
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import androidx.work.ExistingWorkPolicy
import androidx.work.Constraints
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.WorkManager
import androidx.work.workDataOf
import coil.compose.AsyncImage
import coil.imageLoader
import coil.request.ImageRequest
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.MultiFormatWriter
import kotlinx.coroutines.launch
import org.json.JSONArray
import java.time.Instant
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {
    internal fun setKeepScreenAwake(enabled: Boolean) {
        if (enabled) {
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        } else {
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { LessonCueApp() }
    }
}

private sealed interface AppScreen {
    data object Loading : AppScreen
    data class Connect(val message: String? = null) : AppScreen
    data class DemoPassword(val message: String? = null) : AppScreen
    data class EnterPin(val api: LessonCueApi, val requestId: String, val serverName: String) : AppScreen
    data class Library(val identity: DeviceIdentity, val manifest: ScreenManifest) : AppScreen
    data class LessonDetail(val identity: DeviceIdentity, val manifest: ScreenManifest, val playlist: LessonPlaylist) : AppScreen
    data class Player(
        val playlist: LessonPlaylist,
        val items: List<CueItem> = playlist.items,
        val itemIndex: Int = 0,
        val seekMs: Long = 0
    ) : AppScreen
}

private data class ManualDownloadState(
    val identity: DeviceIdentity,
    val playlist: LessonPlaylist,
    val items: List<CueItem>,
    val cachedCount: Int,
    val started: Boolean = false,
    val completed: Boolean = false,
    val error: String? = null,
)

@Composable
fun LessonCueApp() {
    val context = LocalContext.current
    val store = remember { IdentityStore(context) }
    val scope = rememberCoroutineScope()
    val updateManager = remember(context, scope) { UpdateManager(context, scope) }
    val updateState by updateManager.state.collectAsState()
    val updatePermissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) {
        updateManager.onPermissionSettingsReturned()
    }
    var screen by remember { mutableStateOf<AppScreen>(AppScreen.Loading) }
    var activeIdentity by remember { mutableStateOf<DeviceIdentity?>(null) }
    val heartbeatWake = remember(activeIdentity) { HeartbeatWake() }
    var activeManifestVersion by remember { mutableStateOf(0) }
    var playbackControl by remember { mutableStateOf<ControlCommand?>(null) }
    var acknowledgedControlVersion by remember { mutableStateOf(0) }
    var playbackTelemetry by remember { mutableStateOf(PlaybackTelemetry()) }
    var playbackCommandError by remember { mutableStateOf<String?>(null) }
    var totalManifestItems by remember { mutableStateOf(0) }
    var diagnosticCaptureVisible by remember { mutableStateOf(false) }
    var interruptedPlayer by remember { mutableStateOf<AppScreen.Player?>(null) }
    var connectionMode by remember { mutableStateOf(ConnectionMode.Offline) }
    var manualDownload by remember { mutableStateOf<ManualDownloadState?>(null) }

    val activity = context as? MainActivity
    // Keep Android TV/Fire TV from dimming or launching its screensaver while
    // the window is presenting lesson media or an active signage manifest.
    // FLAG_KEEP_SCREEN_ON applies only while this foreground window is visible.
    val keepScreenAwake = when (val current = screen) {
        is AppScreen.Player -> shouldKeepScreenAwake(isPlayer = true, manifest = null)
        is AppScreen.Library -> shouldKeepScreenAwake(isPlayer = false, manifest = current.manifest)
        else -> false
    }
    DisposableEffect(activity, keepScreenAwake) {
        activity?.setKeepScreenAwake(keepScreenAwake)
        onDispose { activity?.setKeepScreenAwake(false) }
    }

    DisposableEffect(updateManager) {
        onDispose { updateManager.close() }
    }

    LaunchedEffect(Unit) {
        if (BuildConfig.UPDATE_ENABLED) updateManager.startAutomaticCheck()
    }

    // Launch must not wait on the network.
    //
    // This used to call reconnectSavedServer first and only fall back to the
    // cache when that failed — up to 8s connect plus 15s read, then mDNS
    // discovery, all on a "Loading" screen, while a perfectly good manifest sat
    // on disk. On a slow or absent server the TV showed nothing for over twenty
    // seconds. Paint from the cache, then reconnect behind it.
    LaunchedEffect(Unit) {
        val identity = store.load()
        if (identity == null) {
            screen = AppScreen.Connect()
            return@LaunchedEffect
        }

        val manifestCache = context.filesDir.resolve("manifest.json")
        val cached = withContext(Dispatchers.IO) {
            LessonCueApi(identity.serverUrl, manifestCache).cachedManifest()
        }
        if (cached != null) {
            connectionMode = ConnectionMode.Cached
            activeIdentity = identity
            activeManifestVersion = cached.version
            totalManifestItems = cached.itemCount()
            screen = AppScreen.Library(identity, cached)
        }

        cancellableResult { reconnectSavedServer(context, identity, manifestCache) }
            .onSuccess { (resolvedIdentity, manifest) ->
                if (resolvedIdentity != identity) store.save(resolvedIdentity)
                connectionMode = ConnectionMode.Online
                activeIdentity = resolvedIdentity
                activeManifestVersion = manifest.version
                totalManifestItems = manifest.itemCount()
                // Refresh the library underneath them, but never yank someone
                // out of a lesson they opened while the reconnect was in flight.
                if (screen is AppScreen.Library || screen is AppScreen.Loading) {
                    screen = AppScreen.Library(resolvedIdentity, manifest)
                }
            }
            .onFailure {
                // Only strand the user when there was nothing to show anyway.
                if (cached == null) {
                    screen = AppScreen.Connect(
                        "Saved server unavailable. Automatic discovery could not reconnect. Enter the server's numeric IP address."
                    )
                }
            }
    }

    LaunchedEffect(activeIdentity) {
        val identity = activeIdentity ?: return@LaunchedEffect
        val api = LessonCueApi(identity.serverUrl, context.filesDir.resolve("manifest.json"),
            connectionDiagnostics = identity.connectionDiagnostics)
        while (true) {
            // Counting cached files and asking for free space are both disk
            // work, and this loop runs every two seconds while a lesson plays.
            // On a TV's flash that was enough to make the remote feel late,
            // because it happened on the thread that draws and handles keys.
            val (cachedItems, freeBytes) = withContext(Dispatchers.IO) {
                val expected = api.cachedManifest()?.allItems().orEmpty().filter { it.offlineEligible }
                val mediaDirectory = context.filesDir.resolve("media")
                val cached = expected.count { item -> findUsableCacheFile(mediaDirectory, item) != null }
                cached to context.filesDir.usableSpace
            }
            cancellableResult { api.reportStatus(identity, activeManifestVersion, freeBytes,
                acknowledgedControlVersion = acknowledgedControlVersion,
                playback = playbackCommandError?.let { playbackTelemetry.copy(state = "error", error = it) } ?: playbackTelemetry,
                cachedItems = cachedItems, totalItems = totalManifestItems) }
            heartbeatWake.awaitNext(if (playbackTelemetry.state in setOf("playing", "loading", "buffering")) 2_000 else 30_000)
        }
    }

    LaunchedEffect(manualDownload?.playlist?.id, manualDownload?.started) {
        val initial = manualDownload?.takeIf { it.started } ?: return@LaunchedEffect
        if (initial.items.isEmpty()) {
            manualDownload = manualDownload?.takeIf { it.playlist.id == initial.playlist.id }
                ?.copy(cachedCount = 0, completed = true)
            return@LaunchedEffect
        }
        while (true) {
            val cached = withContext(Dispatchers.IO) {
                val directory = context.filesDir.resolve("media")
                initial.items.count { findUsableCacheFile(directory, it) != null }
            }
            val completed = cached >= initial.items.size
            manualDownload = manualDownload
                ?.takeIf { it.playlist.id == initial.playlist.id && it.started }
                ?.copy(cachedCount = cached, completed = completed)
            if (completed) break
            kotlinx.coroutines.delay(750)
        }
    }

    LaunchedEffect(activeIdentity) {
        val identity = activeIdentity ?: return@LaunchedEffect
        val api = LessonCueApi(identity.serverUrl, context.filesDir.resolve("manifest.json"),
            connectionDiagnostics = identity.connectionDiagnostics)
        var controlVersion = cancellableResult { api.control(identity).version }.getOrDefault(0)
        val diagnosticCapture = DiagnosticCaptureTask(this) { diagnosticCaptureVisible = it }
        playbackCommandError = null
        while (true) {
            cancellableResult { api.control(identity, controlVersion) }.getOrNull()?.let { command ->
                if (command.screenshotRequestId != null &&
                    (command.screenshotExpiresAt == null || command.screenshotExpiresAt.isAfter(Instant.now()))) {
                    diagnosticCapture.submit(command.screenshotRequestId) {
                        kotlinx.coroutines.delay(2_500)
                        if (command.screenshotExpiresAt == null || command.screenshotExpiresAt.isAfter(Instant.now())) {
                            val activity = context as? ComponentActivity
                            val jpeg = activity?.let { captureDiagnosticScreenshot(it) }
                            if (jpeg != null) api.uploadDiagnosticScreenshot(identity, command.screenshotRequestId, jpeg)
                        }
                    }
                }
                if (command.changed) {
                    playbackControl = command
                    var applied = false
                    var commandError: String? = null
                    when (command.action) {
                        "play" -> cancellableResult { api.manifest(identity) }.getOrNull()?.let { manifest ->
                            activeManifestVersion = manifest.version
                            totalManifestItems = manifest.itemCount()
                            val resolution = resolveRemotePlay(manifest, command.lessonId)
                            val playlist = resolution.playlist
                            applied = resolution.consume
                            commandError = resolution.error
                            if (playlist != null) {
                            val allItems = playlist.preRoll?.items.orEmpty() + listOfNotNull(playlist.countdown?.item) + playlist.items + playlist.postLesson?.items.orEmpty()
                                val selected = command.itemId?.let { id -> allItems.indexOfFirst { it.id == id } }?.takeIf { it >= 0 }
                                val requested = if (selected != null) AppScreen.Player(playlist, allItems, selected)
                                    else AppScreen.Player(playlist)
                                if (manifest.signage.any { it.mode == "emergency" }) {
                                    interruptedPlayer = requested
                                    screen = AppScreen.Library(identity, manifest)
                                } else screen = requested
                                applied = true
                            }
                        }
                        "stop" -> {
                            // Stop must not depend on another successful network request.
                            // The background manifest loop refreshes this library when online.
                            val manifest = resolveRemoteStop(withContext(Dispatchers.IO) { api.cachedManifest() })
                            interruptedPlayer = null
                            screen = AppScreen.Library(identity, manifest)
                            applied = true
                        }
                        "pause", "resume" -> { applied = true }
                        else -> {
                            val current = screen as? AppScreen.Player
                            applied = applyRemoteNavigation(command.action, current?.itemIndex,
                                current?.items?.size ?: 0, command.positionMs) { index, seek ->
                                if (current != null) screen = current.copy(itemIndex = index, seekMs = seek)
                            }
                        }
                    }
                    if (applied) {
                        playbackCommandError = commandError
                        controlVersion = command.version
                        acknowledgedControlVersion = command.version
                        heartbeatWake.request()
                    }
                } else controlVersion = maxOf(controlVersion, command.version)
            }
            kotlinx.coroutines.delay(750)
        }
    }

    LaunchedEffect(activeIdentity) {
        val identity = activeIdentity ?: return@LaunchedEffect
        val api = LessonCueApi(identity.serverUrl, context.filesDir.resolve("manifest.json"),
            connectionDiagnostics = identity.connectionDiagnostics)
        while (true) {
            val refresh = cancellableResult { api.manifest(identity) }
            refresh.onFailure { connectionMode = ConnectionMode.Cached }
            refresh.getOrNull()?.let { latest ->
                connectionMode = ConnectionMode.Online
                activeManifestVersion = latest.version
                totalManifestItems = latest.itemCount()
                scheduleMediaCaches(context, identity, latest)
                val emergency = latest.signage.any { it.mode == "emergency" }
                val current = screen
                when {
                    emergency && current is AppScreen.Player -> {
                        interruptedPlayer = current.copy(seekMs = maxOf(current.seekMs, playbackTelemetry.positionMs))
                        screen = AppScreen.Library(identity, latest)
                    }
                    emergency && current is AppScreen.Library -> screen = AppScreen.Library(identity, latest)
                    !emergency && interruptedPlayer != null -> {
                        screen = interruptedPlayer!!
                        interruptedPlayer = null
                    }
                    current is AppScreen.Library -> screen = AppScreen.Library(identity, latest)
                    current is AppScreen.LessonDetail -> {
                        val playlist = latest.playlists.firstOrNull { it.id == current.playlist.id }
                        screen = if (playlist == null) AppScreen.Library(identity, latest)
                            else AppScreen.LessonDetail(identity, latest, playlist)
                    }
                }
            }
            kotlinx.coroutines.delay(10_000)
        }
    }

    MaterialTheme {
        Surface(modifier = Modifier.fillMaxSize(), colors = androidx.tv.material3.SurfaceDefaults.colors(containerColor = LessonCueTvColors.Background)) {
          Box(Modifier.fillMaxSize()) {
            when (val current = screen) {
                AppScreen.Loading -> LoadingScreen(onEnterAddress = { screen = AppScreen.Connect() })
                is AppScreen.Connect -> ConnectScreen(current.message) { address, deviceName ->
                    if (isDemoServerUrl(address)) {
                        screen = AppScreen.DemoPassword()
                    } else {
                        scope.launch {
                            cancellableResult {
                                val (api, name) = findLessonCueServer(
                                    context, address, context.filesDir.resolve("manifest.json")
                                )
                                val request = api.requestPairing(deviceName)
                                screen = AppScreen.EnterPin(api, request, name)
                            }.onFailure { screen = AppScreen.Connect(it.message) }
                        }
                    }
                }
                is AppScreen.DemoPassword -> DemoPasswordScreen(
                    message = current.message,
                    onBack = { screen = AppScreen.Connect() },
                    onSubmit = { password ->
                        if (isDemoPassword(password)) {
                            // Render the demo through the same shared manifest,
                            // library, lesson-detail, and player path used by
                            // a paired TV. Keep the identity local to the UI;
                            // activeIdentity stays null so this marker can
                            // never start server polling or telemetry.
                            activeIdentity = null
                            connectionMode = ConnectionMode.Cached
                            screen = AppScreen.Library(DEMO_IDENTITY, demoManifest(context.packageName))
                        } else {
                            screen = AppScreen.DemoPassword("That demo password is not correct.")
                        }
                    }
                )
                is AppScreen.EnterPin -> PinScreen(
                    serverName = current.serverName,
                    onBack = { screen = AppScreen.Connect() }
                ) { pin ->
                    scope.launch {
                        cancellableResult {
                            val identity = current.api.confirmPairing(current.requestId, pin)
                            store.save(identity)
                            val manifest = current.api.manifest(identity)
                            connectionMode = ConnectionMode.Online
                            activeIdentity = identity
                            activeManifestVersion = manifest.version
                            totalManifestItems = manifest.itemCount()
                            screen = AppScreen.Library(identity, manifest)
                        }.onFailure { screen = AppScreen.Connect(it.message) }
                    }
                }
                is AppScreen.Library -> {
                    LaunchedEffect(current.manifest.version) { playbackTelemetry = PlaybackTelemetry() }
                    LaunchedEffect(current.manifest.version) { scheduleMediaCaches(context, current.identity, current.manifest) }
                    LaunchedEffect(current.manifest.version, current.manifest.playlists.size) {
                        if (current.manifest.signage.any { it.mode == "emergency" }) return@LaunchedEffect
                        while (true) {
                            val scheduled = current.manifest.playlists.map { it to ScheduleCoordinator.phase(it, Instant.now()) }
                                .firstOrNull { (_, phase) -> phase is PlaybackPhase.Countdown || phase is PlaybackPhase.PreRoll }
                            if (scheduled != null) {
                                val (playlist, phase) = scheduled
                                screen = when (phase) {
                                    is PlaybackPhase.Countdown -> playlist.countdown?.item?.let { AppScreen.Player(playlist, listOf(it), seekMs = phase.seekMs) } ?: AppScreen.Player(playlist)
                                    is PlaybackPhase.PreRoll -> {
                                        val preRollItems = playlist.preRoll?.items.orEmpty()
                                        AppScreen.Player(playlist, loopingPreRoll(preRollItems))
                                    }
                                    else -> AppScreen.Player(playlist)
                                }
                                break
                            }
                            kotlinx.coroutines.delay(1_000)
                        }
                    }
                    LibraryScreen(
                        current.manifest,
                        current.identity,
                        connectionMode,
                        onStart = { playlist -> screen = AppScreen.LessonDetail(current.identity, current.manifest, playlist) },
                        onManualDownload = { playlist -> manualDownload = manualDownloadState(context, current.identity, playlist) },
                        onCheckForUpdates = (updateManager::checkManually).takeIf { BuildConfig.UPDATE_ENABLED }
                    )
                }
                is AppScreen.LessonDetail -> {
                    LaunchedEffect(current.playlist.id) {
                        while (true) {
                            when (val phase = ScheduleCoordinator.phase(current.playlist, Instant.now())) {
                                is PlaybackPhase.Countdown -> current.playlist.countdown?.item?.let {
                                    screen = AppScreen.Player(current.playlist, listOf(it), seekMs = phase.seekMs)
                                    return@LaunchedEffect
                                }
                                is PlaybackPhase.PreRoll -> {
                                    screen = AppScreen.Player(current.playlist, loopingPreRoll(current.playlist.preRoll?.items.orEmpty()))
                                    return@LaunchedEffect
                                }
                                else -> Unit
                            }
                            kotlinx.coroutines.delay(1_000)
                        }
                    }
                    LessonDetailScreen(current.playlist, current.manifest.screenName, connectionMode,
                        onBack = { screen = AppScreen.Library(current.identity, current.manifest) },
                        onManualDownload = { playlist -> manualDownload = manualDownloadState(context, current.identity, playlist) },
                        onPlay = { items, index -> screen = AppScreen.Player(current.playlist, items, index) })
                }
                is AppScreen.Player -> {
                    LaunchedEffect(current.playlist.id, current.items.map { it.id }) {
                        val preRollIds = current.playlist.preRoll?.items.orEmpty().map { it.id }
                        val countdownId = current.playlist.countdown?.item?.id
                        while (true) {
                            val phase = ScheduleCoordinator.phase(current.playlist, Instant.now())
                            val playingIds = current.items.map { it.id }
                            when {
                                playingIds == preRollIds && phase is PlaybackPhase.Countdown -> {
                                    current.playlist.countdown?.item?.let {
                                        screen = AppScreen.Player(current.playlist, listOf(it), seekMs = phase.seekMs)
                                    }
                                    break
                                }
                                (playingIds == preRollIds || (playingIds.size == 1 && playingIds.firstOrNull() == countdownId)) && phase is PlaybackPhase.Ready -> {
                                    screen = AppScreen.Player(current.playlist)
                                    break
                                }
                            }
                            kotlinx.coroutines.delay(250)
                        }
                    }
                    PlayerScreen(current.playlist, current.items, current.itemIndex, current.seekMs, playbackControl, activeIdentity,
                    onTelemetry = { playbackTelemetry = it },
                    onExit = {
                        if (current.playlist.id == DEMO_PLAYLIST_ID) {
                            val manifest = demoManifest(context.packageName)
                            screen = AppScreen.LessonDetail(DEMO_IDENTITY, manifest,
                                manifest.playlists.firstOrNull { it.id == current.playlist.id } ?: current.playlist)
                        } else {
                            scope.launch { store.load()?.let { identity ->
                                val api = LessonCueApi(identity.serverUrl, context.filesDir.resolve("manifest.json"),
                                    connectionDiagnostics = identity.connectionDiagnostics)
                                val manifest = cancellableResult { api.manifest(identity) }.getOrElse {
                                    api.cachedManifest() ?: ScreenManifest(1, "LessonCue", emptyList(), listOf(current.playlist))
                                }
                                screen = AppScreen.LessonDetail(identity, manifest,
                                    manifest.playlists.firstOrNull { it.id == current.playlist.id } ?: current.playlist)
                            } }
                        }
                    },
                    onFinished = { scope.launch {
                        val identity = activeIdentity ?: return@launch
                        val api = LessonCueApi(identity.serverUrl, context.filesDir.resolve("manifest.json"),
                            connectionDiagnostics = identity.connectionDiagnostics)
                        val manifest = cancellableResult { api.manifest(identity) }.getOrElse {
                            api.cachedManifest() ?: ScreenManifest(1, "LessonCue", emptyList(), listOf(current.playlist))
                        }
                        playbackTelemetry = PlaybackTelemetry()
                        val postLesson = loopingPreRoll(current.playlist.postLesson?.items.orEmpty())
                        screen = if (postLesson.isNotEmpty() && current.items == current.playlist.items) {
                            AppScreen.Player(current.playlist, postLesson)
                        } else if (postLesson.isNotEmpty() && current.items == postLesson) {
                            AppScreen.Player(current.playlist, postLesson)
                        } else {
                            AppScreen.Library(identity, manifest)
                        }
                    } },
                    onNext = { next -> screen = current.copy(itemIndex = next, seekMs = 0) })
                }
            }
            if (diagnosticCaptureVisible) {
                Column(
                    Modifier.align(Alignment.TopEnd).padding(28.dp)
                        .background(LessonCueTvColors.Background.copy(alpha = .96f), RoundedCornerShape(14.dp))
                        .border(2.dp, Coral, RoundedCornerShape(14.dp)).padding(horizontal = 20.dp, vertical = 14.dp)
                ) {
                    Text("ADMIN DIAGNOSTIC CAPTURE", color = Coral, fontSize = 16.sp,
                        fontWeight = FontWeight.Bold, letterSpacing = 1.sp)
                    Text("A temporary screenshot was requested", color = Cream, fontSize = 14.sp)
                }
            }
            val passiveUpdate = (updateState as? UpdateUiState.Available)
                ?.takeIf { !it.blocking && !it.manualPresentation }
            if (passiveUpdate != null) {
                Box(Modifier.align(Alignment.TopCenter)) {
                    UpdateAvailableBanner(
                        passiveUpdate,
                        onReview = updateManager::reviewAvailableUpdate,
                        onLater = updateManager::dismiss
                    )
                }
            } else if (updateState !is UpdateUiState.Idle) {
                UpdateScreen(
                    state = updateState,
                    onDownload = updateManager::downloadAndInstall,
                    onLater = updateManager::dismiss,
                    onCancelDownload = updateManager::cancelDownload,
                    onRetry = updateManager::retry,
                    onClose = updateManager::closeMessage,
                    onOpenPermissionSettings = {
                        try {
                            updatePermissionLauncher.launch(updateManager.permissionIntent())
                        } catch (_: ActivityNotFoundException) {
                            updateManager.onPermissionSettingsUnavailable()
                        } catch (_: SecurityException) {
                            updateManager.onPermissionSettingsUnavailable()
                        }
                    }
                )
            }
            manualDownload?.let { state ->
                ManualDownloadOverlay(
                    state = state,
                    onDownload = {
                        if (state.items.isEmpty() || state.cachedCount >= state.items.size) {
                            manualDownload = state.copy(started = true, completed = true, error = null)
                        } else {
                            runCatching {
                                enqueuePriorityLessonDownload(context, state.identity, state.playlist)
                            }.onSuccess {
                                manualDownload = state.copy(started = true, error = null)
                            }.onFailure { error ->
                                manualDownload = state.copy(started = true, error = error.message ?: "Could not start the download.")
                            }
                        }
                    },
                    onDismiss = { manualDownload = null }
                )
            }
          }
        }
    }
}

private suspend fun captureDiagnosticScreenshot(activity: ComponentActivity): ByteArray? {
    val width = activity.window.decorView.width.coerceAtLeast(1)
    val height = activity.window.decorView.height.coerceAtLeast(1)
    val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
    return captureDiagnosticBitmap(bitmap) { target, finished ->
        PixelCopy.request(activity.window, target, finished, Handler(Looper.getMainLooper()))
    }
}

@Composable
private fun ConnectScreen(message: String?, onConnect: (String, String) -> Unit) {
    var address by remember { mutableStateOf("http://lessoncue.local") }
    var deviceName by remember { mutableStateOf(defaultDeviceName()) }
    val addressFocus = remember { FocusRequester() }
    val findServerFocus = remember { FocusRequester() }
    LaunchedEffect(Unit) {
        kotlinx.coroutines.delay(100)
        runCatching { addressFocus.requestFocus() }
    }
    FormLayout(
        "Connect this TV",
        "LessonCue TV uses server pairing, not a username and password. Enter a reachable LessonCue server address, then use its six-digit pairing PIN."
    ) {
        PrivacyPolicyNotice()
        Spacer(Modifier.height(20.dp))
        BoxWithConstraints(Modifier.fillMaxWidth()) {
            val compact = maxWidth < 760.dp
            val fieldModifier = Modifier.fillMaxWidth()
            if (compact) {
                Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
                    Column {
                        Text("DEVICE NAME", color = Muted, fontSize = 13.sp, fontWeight = FontWeight.Bold, letterSpacing = 1.sp)
                        Spacer(Modifier.height(6.dp))
                        TvTextField(
                            deviceName,
                            { deviceName = it.take(MAX_DEVICE_NAME_LENGTH) },
                            modifier = fieldModifier,
                            placeholder = "Room or display name"
                        )
                    }
                    Column {
                        Text("SERVER ADDRESS", color = Muted, fontSize = 13.sp, fontWeight = FontWeight.Bold, letterSpacing = 1.sp)
                        Spacer(Modifier.height(6.dp))
                        TvTextField(
                            address,
                            { address = it },
                            modifier = fieldModifier.focusRequester(addressFocus),
                            placeholder = "http://192.168.1.25"
                        )
                    }
                }
            } else {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(30.dp)) {
                    Column(Modifier.weight(1f)) {
                        Text("DEVICE NAME", color = Muted, fontSize = 15.sp, fontWeight = FontWeight.Bold, letterSpacing = 1.sp)
                        Spacer(Modifier.height(8.dp))
                        TvTextField(
                            deviceName,
                            { deviceName = it.take(MAX_DEVICE_NAME_LENGTH) },
                            modifier = fieldModifier,
                            placeholder = "Room or display name"
                        )
                    }
                    Column(Modifier.weight(1f)) {
                        Text("SERVER ADDRESS", color = Muted, fontSize = 15.sp, fontWeight = FontWeight.Bold, letterSpacing = 1.sp)
                        Spacer(Modifier.height(8.dp))
                        TvTextField(
                            address,
                            { address = it },
                            modifier = fieldModifier.focusRequester(addressFocus),
                            placeholder = "http://192.168.1.25"
                        )
                    }
                }
            }
        }
        message?.let {
            Spacer(Modifier.height(18.dp))
            Box(
                Modifier.fillMaxWidth().background(Coral.copy(alpha = .12f), RoundedCornerShape(14.dp))
                    .border(1.dp, Coral.copy(alpha = .55f), RoundedCornerShape(14.dp)).padding(18.dp)
            ) {
                Text(it, color = Cream, fontSize = 17.sp, maxLines = 4,
                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
            }
        }
        Spacer(Modifier.height(26.dp))
        BoxWithConstraints(Modifier.fillMaxWidth()) {
            val compact = maxWidth < 900.dp
            if (compact) {
                Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
                    Text(
                        "This display is paired to a LessonCue server; it does not use a personal account login. LessonCue will also search the local network automatically.",
                        color = Muted,
                        fontSize = 14.sp,
                        maxLines = 4,
                        overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                    )
                    LessonCueButton(
                        onClick = { onConnect(address, deviceName.trim().ifBlank { defaultDeviceName() }) },
                        enabled = deviceName.isNotBlank(),
                        modifier = Modifier.fillMaxWidth().height(56.dp).focusRequester(findServerFocus)
                    ) { Text("Find server", fontSize = 17.sp, fontWeight = FontWeight.Bold) }
                }
            } else {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        "This display is paired to a LessonCue server; it does not use a personal account login. LessonCue will also search the local network automatically.",
                        color = Muted,
                        fontSize = 16.sp,
                        modifier = Modifier.weight(1f),
                        maxLines = 3,
                        overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                    )
                    LessonCueButton(
                        onClick = { onConnect(address, deviceName.trim().ifBlank { defaultDeviceName() }) },
                        enabled = deviceName.isNotBlank(),
                        modifier = Modifier.width(220.dp).height(62.dp).focusRequester(findServerFocus)
                    ) { Text("Find server", fontSize = 18.sp, fontWeight = FontWeight.Bold) }
                }
            }
        }
    }
}

private suspend fun findLessonCueServer(context: android.content.Context, address: String, manifestCache: java.io.File):
    Pair<LessonCueApi, String> {
    val resolution = withContext(Dispatchers.IO) { ServerEndpointSelection.resolve(address) }
    val attempts = resolution.rejected.toMutableList()
    val tried = mutableSetOf<String>()

    suspend fun tryCandidates(candidates: List<ServerEndpointCandidate>): Pair<LessonCueApi, String>? {
      val verified = firstVerifiedServerEndpoint(candidates, tried, attempts) { candidate ->
        val api = LessonCueApi(candidate.endpoint, manifestCache)
        api to (api.discoverQuickly())
      } ?: return null
      val (candidate, result) = verified
      val diagnostics = ConnectionDiagnostics(address, candidate.endpoint, attempts.toList())
      return result.first.withConnectionDiagnostics(diagnostics) to result.second
    }

    tryCandidates(resolution.candidates)?.let { return it }
    val discovered = LessonCueDiscovery(context).findServers()
    val discoveredResolution = withContext(Dispatchers.IO) { ServerEndpointSelection.resolve(address, discovered) }
    attempts += discoveredResolution.rejected.filter { rejected -> attempts.none { it.endpoint == rejected.endpoint && it.reason == rejected.reason } }
    tryCandidates(discoveredResolution.candidates)?.let { return it }
    error("Could not verify LessonCue at $address or any discovered endpoint. ${attemptSummary(attempts)}")
}

private suspend fun reconnectSavedServer(context: android.content.Context, identity: DeviceIdentity, manifestCache: java.io.File):
    Pair<DeviceIdentity, ScreenManifest> {
    // Expand the saved hostname and every NSD result into concrete candidates.
    // Each candidate is verified with the authenticated manifest endpoint, so
    // a dead AAAA result cannot prevent a working A result from being used.
    val attempts = withContext(Dispatchers.IO) {
        ServerEndpointSelection.resolve(identity.serverUrl).rejected.toMutableList()
    }
    val tried = mutableSetOf<String>()

    suspend fun tryCandidates(candidates: List<ServerEndpointCandidate>): Pair<DeviceIdentity, ScreenManifest>? {
      val verified = firstVerifiedServerEndpoint(candidates, tried, attempts) { candidate ->
        val candidateIdentity = identity.copy(serverUrl = candidate.endpoint)
        val api = LessonCueApi(candidate.endpoint, manifestCache,
            connectionDiagnostics = identity.connectionDiagnostics)
        candidateIdentity to api.manifestQuickly(candidateIdentity)
      } ?: return null
      val (candidate, result) = verified
      val diagnostics = ConnectionDiagnostics(identity.serverUrl, candidate.endpoint, attempts.toList())
      return result.first.copy(connectionDiagnostics = diagnostics) to result.second
    }

    tryCandidates(withContext(Dispatchers.IO) { ServerEndpointSelection.resolve(identity.serverUrl).candidates })?.let { return it }
    val discovered = LessonCueDiscovery(context).findServers()
    val resolution = withContext(Dispatchers.IO) { ServerEndpointSelection.resolve(identity.serverUrl, discovered) }
    attempts += resolution.rejected.filter { rejected -> attempts.none { it.endpoint == rejected.endpoint && it.reason == rejected.reason } }
    tryCandidates(resolution.candidates)?.let { return it }
    error("Automatic LessonCue discovery did not find a verified server. ${attemptSummary(attempts)}")
}

private fun attemptSummary(attempts: List<EndpointAttempt>): String = attempts.takeLast(8).joinToString("; ") { attempt ->
    "${attempt.endpoint} -> ${attempt.outcome}${attempt.reason?.let { " ($it)" }.orEmpty()}"
}

@Composable
internal fun PinScreen(serverName: String, onBack: () -> Unit, onConfirm: (String) -> Unit) {
    var pin by remember { mutableStateOf("") }
    val pinFocus = remember { FocusRequester() }
    LaunchedEffect(Unit) {
        kotlinx.coroutines.delay(100)
        runCatching { pinFocus.requestFocus() }
    }
    BackHandler(onBack = onBack)
    FormLayout(
        "Pair this TV",
        "Connected to $serverName. Enter the six-digit pairing PIN shown in the LessonCue administrator screen. This pairs the TV; it is not a user account password."
    ) {
        TvTextField(
            value = pin,
            onValueChange = { pin = it.filter(Char::isDigit).take(6) },
            modifier = Modifier.fillMaxWidth(.7f).focusRequester(pinFocus),
            numeric = true,
            placeholder = "•  •  •  •  •  •"
        )
        Spacer(Modifier.height(18.dp))
        Text(
            if (pin.length == 6) "PIN complete" else "${6 - pin.length} digits remaining",
            color = if (pin.length == 6) Mint else Muted,
            fontSize = 16.sp
        )
        Spacer(Modifier.height(28.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            LessonCueButton(onClick = { if (pin.length == 6) onConfirm(pin) }, enabled = pin.length == 6,
                modifier = Modifier.width(210.dp).height(62.dp)) {
                Text("Pair TV", fontSize = 18.sp, fontWeight = FontWeight.Bold)
            }
            LessonCueButton(onClick = onBack, modifier = Modifier.width(160.dp).height(62.dp)) { Text("Back") }
        }
    }
}

@Composable
internal fun DemoPasswordScreen(
    message: String?,
    onBack: () -> Unit,
    onSubmit: (String) -> Unit
) {
    var password by remember { mutableStateOf("") }
    val passwordFocus = remember { FocusRequester() }
    val submitFocus = remember { FocusRequester() }
    BackHandler(onBack = onBack)
    LaunchedEffect(Unit) {
        kotlinx.coroutines.delay(100)
        runCatching { passwordFocus.requestFocus() }
    }
    LaunchedEffect(password.length) {
        if (password.length == DEMO_PASSWORD.length) {
            kotlinx.coroutines.delay(80)
            runCatching { submitFocus.requestFocus() }
        }
    }
    FormLayout(
        "LessonCue demo",
        "Enter the review password to open the bundled sample content. This path does not connect to a server."
    ) {
        PrivacyPolicyNotice()
        Spacer(Modifier.height(20.dp))
        TvTextField(
            value = password,
            onValueChange = { password = it.filter(Char::isDigit).take(DEMO_PASSWORD.length) },
            modifier = Modifier.fillMaxWidth(.7f).focusRequester(passwordFocus),
            numeric = true,
            placeholder = "•  •  •  •  •  •"
        )
        message?.let {
            Spacer(Modifier.height(18.dp))
            Box(
                Modifier.fillMaxWidth().background(Coral.copy(alpha = .12f), RoundedCornerShape(14.dp))
                    .border(1.dp, Coral.copy(alpha = .55f), RoundedCornerShape(14.dp)).padding(18.dp)
            ) {
                Text(it, color = Cream, fontSize = 17.sp)
            }
        }
        Spacer(Modifier.height(18.dp))
        Text(
            if (password.length == DEMO_PASSWORD.length) "Password complete — press Select to open the demo"
            else "${DEMO_PASSWORD.length - password.length} digits remaining",
            color = if (password.length == DEMO_PASSWORD.length) Mint else Muted,
            fontSize = 16.sp
        )
        Spacer(Modifier.height(28.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            LessonCueButton(
                onClick = { if (password.length == DEMO_PASSWORD.length) onSubmit(password) },
                enabled = password.length == DEMO_PASSWORD.length,
                modifier = Modifier.width(250.dp).height(62.dp).focusRequester(submitFocus)
            ) { Text("Open demo", fontSize = 18.sp, fontWeight = FontWeight.Bold) }
            LessonCueButton(onClick = onBack, modifier = Modifier.width(160.dp).height(62.dp)) {
                Text("Back")
            }
        }
    }
}

@Composable
private fun PrivacyPolicyNotice() {
    Column(
        Modifier.fillMaxWidth().testTag("privacy-policy-notice"),
        verticalArrangement = Arrangement.spacedBy(3.dp)
    ) {
        Text("Privacy policy", color = Muted, fontSize = 13.sp, fontWeight = FontWeight.Bold)
        Text(
            LESSONCUE_PRIVACY_POLICY_URL,
            color = Mint,
            fontSize = 13.sp,
            maxLines = 3,
            overflow = androidx.compose.ui.text.style.TextOverflow.Clip
        )
    }
}

@Composable
internal fun LibraryScreen(
    manifest: ScreenManifest,
    identity: DeviceIdentity,
    connectionMode: ConnectionMode,
    onStart: (LessonPlaylist) -> Unit,
    onManualDownload: (LessonPlaylist) -> Unit = {},
    onCheckForUpdates: (() -> Unit)?
) {
    val signage = manifest.signage.firstOrNull { it.mode == "emergency" } ?: manifest.signage.firstOrNull()
    val interactionMode = manifest.libraryInteractionMode()
    if (signage?.displayPower == "off") { Box(Modifier.fillMaxSize().background(Color.Black)); return }
    val signageEntries = signage?.contentPlaylist?.items.orEmpty().filter(::isRenderableSignageEntry)
    var signageEntryIndex by remember(signage?.contentPlaylist?.id, signage?.contentPlaylist?.version) {
        mutableIntStateOf(if (signage?.contentPlaylist?.synchronization == "screen") 0 else synchronizedSignageIndex(signageEntries))
    }
    val signageEntrySchedule = signageEntries.map { "${it.id}:${it.durationSeconds}" }
    LaunchedEffect(signage?.contentPlaylist?.id, signage?.contentPlaylist?.version, signageEntrySchedule) {
        if (signageEntries.isNotEmpty()) {
            var nextIndex = signageEntryIndex % signageEntries.size
            while (true) {
                kotlinx.coroutines.delay(signageEntries[nextIndex].durationSeconds.coerceAtLeast(1) * 1_000L)
                nextIndex = (nextIndex + 1) % signageEntries.size
                signageEntryIndex = nextIndex
            }
        }
    }
    val signageEntry = signageEntries.getOrNull(signageEntryIndex % maxOf(1, signageEntries.size))
    val displaySignage = signageEntry?.layout?.let { layout ->
        signage?.copy(name = layout.name, backgroundColor = layout.backgroundColor, zones = layout.zones,
            backgroundAudio = layout.backgroundAudio)
    } ?: signage
    val context = LocalContext.current
    val inputModeManager = LocalInputModeManager.current
    val firstFocus = remember { FocusRequester() }
    val libraryPlaylists = remember(manifest.playlists) { lessonsForLibrary(manifest.playlists) }
    val lessonIds = libraryPlaylists.map { it.id }
    val defaultPreferredIndex = preferredLessonIndex(libraryPlaylists, Instant.now())
    var focusedLessonId by remember(lessonIds) {
        mutableStateOf(libraryPlaylists.getOrNull(defaultPreferredIndex)?.id)
    }
    val featured = libraryPlaylists.getOrNull(defaultPreferredIndex)
    val additionalLessons = libraryPlaylists.filterNot { it.id == featured?.id }
    var visibleAdditionalCount by remember(lessonIds) { mutableIntStateOf(2) }
    val visibleAdditionalLessons = additionalLessons.take(visibleAdditionalCount)
    val lessonListState = rememberLazyListState()
    val firstAdditionalFocus = remember(lessonIds) { FocusRequester() }
    LaunchedEffect(lessonIds, featured?.id, interactionMode) {
        if (featured != null && interactionMode == LibraryInteractionMode.Lessons) {
            if (focusedLessonId !in lessonIds) {
                focusedLessonId = featured.id
            }
            inputModeManager.requestInputMode(InputMode.Keyboard)
            kotlinx.coroutines.delay(80)
            firstFocus.requestFocus()
        }
    }
    Box(Modifier.fillMaxSize().background(displaySignage?.backgroundColor?.let(::parseDisplayColor) ?: Navy)) {
      SignageImagePreload(signageEntries)
      displaySignage?.backgroundAudio?.let { SignageBackgroundAudio(it, displaySignage.volumePercent) }
      if (displaySignage?.zones?.isNotEmpty() == true) SignageZoneLayout(displaySignage)
      else signageEntry?.media?.let { SignageBackdrop(it) }
          ?: signageEntry?.sourceUrl?.let { SignageWebZone(it) }
          ?: displaySignage?.media?.let { SignageBackdrop(it) }
      if (interactionMode == LibraryInteractionMode.SignageOnly) {
          // Signage-only: full-screen signage with no app chrome.
          Box(Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.BottomEnd) {
              if (displaySignage?.zones?.isEmpty() != false) {
                  // When there are no structured zones, show a subtle screen-name label only.
                  Text(manifest.screenName, color = Muted, fontSize = 14.sp)
              }
          }
      } else {
          Column(
              Modifier.fillMaxSize().background(LessonCueTvColors.BlackScrim)
                  .padding(horizontal = LessonCueTvDimens.ScreenHorizontal, vertical = 30.dp)
          ) {
              TvHeader(
                  screenName = manifest.screenName,
                  connectionMode = connectionMode,
                  onCheckForUpdates = onCheckForUpdates
              )
              Spacer(Modifier.height(26.dp))
              if (interactionMode == LibraryInteractionMode.Emergency) {
                  TvPanel(Modifier.fillMaxWidth().weight(1f)) {
                      Column(
                          Modifier.fillMaxSize().padding(52.dp),
                          verticalArrangement = Arrangement.Center,
                          horizontalAlignment = Alignment.CenterHorizontally
                      ) {
                          StatusBadge("EMERGENCY OVERRIDE", Coral)
                          Spacer(Modifier.height(20.dp))
                          Text(signage?.message?.ifBlank { signage.name }.orEmpty(), color = Cream, fontSize = 38.sp,
                              fontWeight = FontWeight.Bold, textAlign = TextAlign.Center)
                          Spacer(Modifier.height(18.dp))
                          Text("Lesson controls are temporarily unavailable. Interrupted playback will resume automatically.",
                              color = Muted, fontSize = 21.sp, textAlign = TextAlign.Center)
                      }
                  }
              } else {
                  Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(28.dp)) {
                      featured?.let { playlist ->
                          FeaturedLessonPanel(
                              playlist = playlist,
                              readiness = mediaReadiness(context, playlist),
                              modifier = Modifier.weight(1.1f).fillMaxSize(),
                              initialFocusRequester = firstFocus,
                              downFocusRequester = firstAdditionalFocus.takeIf { visibleAdditionalLessons.isNotEmpty() },
                              selected = playlist.id == focusedLessonId,
                              onFocused = { focusedLessonId = playlist.id },
                              onOpen = { onStart(playlist) },
                              onLongClick = { onManualDownload(playlist) }
                          )
                      }
                      TvPanel(Modifier.weight(1f).fillMaxSize()) {
                          Column(Modifier.fillMaxSize().padding(24.dp)) {
                              Text("Available Lessons", color = Cream, fontSize = 28.sp, fontWeight = FontWeight.Bold,
                                  maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                              Text("Choose a lesson to preview its cues.", color = Muted, fontSize = 16.sp)
                              Spacer(Modifier.height(18.dp))
                              LazyColumn(Modifier.weight(1f), state = lessonListState,
                                  verticalArrangement = Arrangement.spacedBy(12.dp)) {
                                  itemsIndexed(visibleAdditionalLessons, key = { _, playlist -> playlist.id }) { index, playlist ->
                                      CompactLessonCard(
                                          playlist = playlist,
                                          onOpen = { onStart(playlist) },
                                          onLongClick = { onManualDownload(playlist) },
                                          onFocused = { focusedLessonId = playlist.id },
                                          selected = playlist.id == focusedLessonId,
                                          modifier = remoteListItemModifier(),
                                          initialFocusRequester = firstAdditionalFocus.takeIf { index == 0 },
                                          upFocusRequester = firstFocus.takeIf { index == 0 }
                                      )
                                  }
                                  if (visibleAdditionalLessons.size < additionalLessons.size) {
                                      item {
                                          LessonCueButton(
                                              onClick = {
                                                  visibleAdditionalCount =
                                                      (visibleAdditionalCount + 4).coerceAtMost(additionalLessons.size)
                                              },
                                              modifier = Modifier.fillMaxWidth().height(54.dp)
                                                  .testTag("see-more-lessons")
                                          ) {
                                              Text("See more", fontSize = 17.sp, fontWeight = FontWeight.Bold)
                                          }
                                      }
                                  }
                              }
                          }
                      }
                  }
              }
          }
          Text(
              "v${BuildConfig.VERSION_NAME}",
              color = Muted,
              fontSize = 12.sp,
              modifier = Modifier.align(Alignment.BottomStart)
                  .padding(start = LessonCueTvDimens.ScreenHorizontal, bottom = 8.dp)
          )
      }
    }
}

private data class MediaReadiness(val label: String, val shortLabel: String, val color: Color)

@Composable
private fun FeaturedLessonPanel(
    playlist: LessonPlaylist,
    readiness: MediaReadiness,
    modifier: Modifier,
    initialFocusRequester: FocusRequester,
    downFocusRequester: FocusRequester?,
    selected: Boolean,
    onFocused: () -> Unit,
    onOpen: () -> Unit,
    onLongClick: () -> Unit
) {
    val previewCues = playlist.timeline().take(4)
    FocusedTvCard(
        onClick = onOpen,
        onLongClick = onLongClick,
        modifier = modifier
            .focusProperties { downFocusRequester?.let { down = it } }
            .testTag("lesson-card-${playlist.id}"),
        onFocused = onFocused,
        initialFocusRequester = initialFocusRequester,
        selected = selected
    ) {
        Column(Modifier.fillMaxSize().padding(20.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text("Up Next", color = Cream, fontSize = 27.sp, fontWeight = FontWeight.Bold)
                Spacer(Modifier.width(12.dp))
                StatusBadge(beginsInLabel(playlist, Instant.now()), Gold)
            }
            Spacer(Modifier.height(4.dp))
            Text(playlist.title, color = Cream, fontSize = 25.sp, fontWeight = FontWeight.Bold,
                maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
            val metadata = buildList {
                playlist.designatedStartAt?.let { add(formatLessonTime(it)) }
                playlist.estimatedDurationMs().takeIf { it > 0 }?.let { add(formatDuration(it)) }
                add("${playlist.timeline().size} cues")
            }.joinToString("  •  ")
            Text(metadata, color = Muted, fontSize = 14.sp)
            Spacer(Modifier.height(4.dp))
            Text(readiness.label, color = readiness.color, fontSize = 13.sp, maxLines = 1,
                overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
            Spacer(Modifier.height(8.dp))
            CueTimelinePreview(previewCues, Modifier.weight(1f).fillMaxWidth())
            Spacer(Modifier.height(8.dp))
            Text("Select to open  •  Hold Select to download", color = Muted, fontSize = 14.sp, fontWeight = FontWeight.Bold,
                modifier = Modifier.align(Alignment.CenterHorizontally))
        }
    }
}

@Composable
private fun CompactLessonCard(
    playlist: LessonPlaylist,
    modifier: Modifier,
    selected: Boolean,
    onFocused: () -> Unit,
    onOpen: () -> Unit,
    onLongClick: () -> Unit,
    initialFocusRequester: FocusRequester?,
    upFocusRequester: FocusRequester?
) {
    val previewItem = playlist.items.firstOrNull() ?: playlist.timeline().firstOrNull()?.item
    FocusedTvCard(
        onClick = onOpen,
        onLongClick = onLongClick,
        modifier = modifier
            .height(116.dp)
            .focusProperties { upFocusRequester?.let { up = it } }
            .testTag("lesson-card-${playlist.id}"),
        onFocused = onFocused,
        initialFocusRequester = initialFocusRequester,
        selected = selected
    ) {
        Row(
            Modifier.fillMaxSize().padding(12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            previewItem?.let {
                CueThumbnail(it, Modifier.width(142.dp).height(88.dp))
                Spacer(Modifier.width(14.dp))
            }
            Column(Modifier.weight(1f)) {
                playlist.designatedStartAt?.let {
                    Text(formatLessonTime(it), color = Gold, fontSize = 13.sp, fontWeight = FontWeight.Bold)
                }
                Text(playlist.title, color = Cream, fontSize = 19.sp, fontWeight = FontWeight.Bold,
                    maxLines = 2, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                Text("${playlist.timeline().size} cues", color = Muted, fontSize = 13.sp)
            }
        }
    }
}

@Composable
private fun CueTimelinePreview(cues: List<TimelineCue>, modifier: Modifier = Modifier) {
    if (cues.isEmpty()) {
        Box(modifier, contentAlignment = Alignment.Center) {
            Text("This lesson does not have any cues yet.", color = Muted, fontSize = 17.sp)
        }
        return
    }
    Column(modifier, verticalArrangement = Arrangement.spacedBy(4.dp)) {
        cues.forEachIndexed { index, cue ->
            Row(
                Modifier.fillMaxWidth().height(56.dp).clip(RoundedCornerShape(10.dp))
                    .background(LessonCueTvColors.ElevatedPanel.copy(alpha = .72f))
                    .testTag("up-next-cue-${cue.item.id}"),
                verticalAlignment = Alignment.CenterVertically
            ) {
                CueThumbnail(cue.item, Modifier.width(148.dp).fillMaxSize())
                Column(Modifier.weight(1f).padding(horizontal = 12.dp, vertical = 2.dp)) {
                    Text(
                        "${index + 1}. ${cue.item.title}",
                        color = Cream,
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Bold,
                        maxLines = 1,
                        overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                    )
                    val duration = cue.item.effectiveDurationMs()?.let(::formatDuration) ?: "—"
                    Text(
                        "${cue.role.label}  •  ${cue.item.type.replaceFirstChar { it.uppercase() }}  •  $duration",
                        color = Muted,
                        fontSize = 10.sp,
                        maxLines = 1,
                        overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                    )
                }
            }
        }
    }
}

private fun formatLessonTime(value: Instant): String = DateTimeFormatter.ofPattern("h:mm a")
    .withZone(java.time.ZoneId.systemDefault()).format(value)

private fun cachedMediaFile(context: android.content.Context, item: CueItem): java.io.File? =
    findUsableCacheFile(context.filesDir.resolve("media"), item)

private data class ResilientCacheSource(
    val cached: java.io.File?,
    val source: String?,
    val usingCache: Boolean,
    val onCacheFailure: () -> Unit,
)

/**
 * Prefer the local copy, but make a failed cache disposable. The server URL
 * remains available for the same item so a bad local file cannot strand a
 * display that is online.
 */
@Composable
private fun rememberResilientCacheSource(item: CueItem): ResilientCacheSource {
    val context = LocalContext.current
    val cached = rememberCachedMediaFile(item)
    var serverFallback by remember(item.id, item.cacheFileName()) { mutableStateOf(cached == null) }
    val usingCache = cached != null && !serverFallback
    val source = if (usingCache) cached.toURI().toString() else item.url
    val onCacheFailure = {
        if (usingCache && !item.url.isNullOrBlank()) {
            invalidateCachedMedia(context.filesDir.resolve("media"), item)
            serverFallback = true
        }
    }
    return ResilientCacheSource(cached, source, usingCache, onCacheFailure)
}

@Composable
private fun ResilientCachedImage(item: CueItem, contentDescription: String?, contentScale: ContentScale,
    modifier: Modifier = Modifier) {
    val source = rememberResilientCacheSource(item)
    AsyncImage(model = source.source, contentDescription = contentDescription, contentScale = contentScale,
        onError = { source.onCacheFailure() }, modifier = modifier)
}

/**
 * The cached file for an item, looked up once per item rather than on every
 * recomposition.
 *
 * Asking the filesystem whether a file exists is cheap once and expensive in a
 * loop: signage redraws its zones on a timer, and each redraw was stat-ing
 * every item again on the thread that also handles the remote.
 */
@Composable
private fun rememberCachedMediaFile(item: CueItem): java.io.File? {
    val context = LocalContext.current
    return remember(context, item.id, item.cacheFileName()) { cachedMediaFile(context, item) }
}

private fun mediaReadiness(context: android.content.Context, playlist: LessonPlaylist): MediaReadiness {
    val items = playlist.timeline().map(TimelineCue::item)
    if (items.isEmpty()) return MediaReadiness("This lesson does not contain media yet.", "NO MEDIA", Muted)
    val bundledItems = items.filter { isBundledDemoMedia(it.url) }
    val offlineItems = items.filter { it.offlineEligible && !isBundledDemoMedia(it.url) }
    val cached = offlineItems.count { cachedMediaFile(context, it) != null }
    val onlineOnly = items.count { !isBundledDemoMedia(it.url) && !it.offlineEligible &&
        (it.playbackUrl != null || it.streamingFallbackUrl != null || it.url != null) }
    return when {
        bundledItems.size + cached == items.size ->
            MediaReadiness("All media is ready on this device.", "READY", Mint)
        offlineItems.isNotEmpty() && cached < offlineItems.size ->
            MediaReadiness("$cached/${offlineItems.size} media items cached. Downloading the remaining media.",
                "$cached/${offlineItems.size} CACHED", Gold)
        onlineOnly > 0 ->
            MediaReadiness("$onlineOnly ${if (onlineOnly == 1) "item requires" else "items require"} an internet connection.",
                "$onlineOnly ONLINE", Coral)
        else -> MediaReadiness("Media is available from the LessonCue server.", "READY", Mint)
    }
}

private fun synchronizedSignageIndex(entries: List<SignagePlaylistEntry>): Int {
    if (entries.isEmpty()) return 0
    val cycle = entries.sumOf { it.durationSeconds.coerceAtLeast(1) }.coerceAtLeast(1)
    var offset = (System.currentTimeMillis() / 1_000 % cycle).toInt()
    entries.forEachIndexed { index, entry ->
        offset -= entry.durationSeconds.coerceAtLeast(1)
        if (offset < 0) return index
    }
    return 0
}

private fun isRenderableSignageEntry(entry: SignagePlaylistEntry): Boolean =
    !entry.hidden && (entry.layout != null || entry.media?.url != null || !entry.sourceUrl.isNullOrBlank())

@Composable
private fun SignageImagePreload(entries: List<SignagePlaylistEntry>) {
    val context = LocalContext.current
    val imageSources = remember(entries) {
        entries.flatMap { it.mediaItems() }
            .filter { it.type == "image" || it.contentType?.startsWith("image/") == true }
            .mapNotNull { it.url }.distinct()
    }
    DisposableEffect(imageSources) {
        val requests = imageSources.map { source ->
            context.imageLoader.enqueue(ImageRequest.Builder(context).data(source).build())
        }
        onDispose { requests.forEach { it.dispose() } }
    }
}

@Composable
private fun SignageZoneLayout(signage: SignageCue) {
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val availableWidth = maxWidth
        val designScale = (availableWidth.value / 1920f).coerceIn(.25f, 1f)
        signage.zones.filterNot { it.hidden }.sortedBy { it.zIndex }.forEach { zone ->
            val modifier = Modifier.offset(maxWidth * (zone.x / 100f), maxHeight * (zone.y / 100f))
                .size(maxWidth * (zone.width / 100f), maxHeight * (zone.height / 100f))
                .zIndex(zone.zIndex.toFloat())
                .graphicsLayer {
                    rotationZ = zone.rotation.toFloat()
                    alpha = (zone.opacity.coerceIn(0, 100) / 100f)
                    scaleX = if (zone.flipX) -1f else 1f
                    scaleY = if (zone.flipY) -1f else 1f
                }
                .background(parseDisplayColor(zone.backgroundColor))
                .border(2.dp, parseDisplayColor(zone.accentColor))
                .clipToBounds()
                .let { modifier ->
                    val cornerRadiusPercent = zone.cornerRadius.coerceIn(0, 50)
                    if (cornerRadiusPercent > 0) {
                        val cornerDp = (maxWidth * (zone.width / 100f) * (cornerRadiusPercent / 100f)).coerceAtMost(200.dp)
                        modifier.clip(RoundedCornerShape(cornerDp))
                    } else modifier
                }
            Box(modifier) {
                zone.media?.let { SignageZoneMedia(it, zone.fit) }
                zone.streamUrl?.takeIf { zone.type == "stream" }?.let { SignageStreamMedia(zone.id, it, zone.fit) }
                (if (zone.type == "customHtml") zone.htmlUrl else zone.sourceUrl)
                    ?.takeIf { zone.type in signageWebZoneTypes }?.let { SignageWebZone(it) }
                if (zone.type == "presentation") SignagePresentationZone(zone)
                val arrangement = when (zone.verticalAlign) {
                    "top" -> Arrangement.Top
                    "bottom" -> Arrangement.Bottom
                    else -> Arrangement.Center
                }
                val contentPadding = availableWidth * (zone.width / 100f) *
                    (zone.contentPadding.coerceIn(0, 30) / 100f)
                val horizontalAlignment = signageHorizontalAlignment(zone.textAlign)
                Column(Modifier.fillMaxSize()
                    .graphicsLayer {
                        scaleX = zone.contentScale.coerceIn(25, 100) / 100f
                        scaleY = zone.contentScale.coerceIn(25, 100) / 100f
                    }
                    .padding(contentPadding),
                    horizontalAlignment = horizontalAlignment, verticalArrangement = arrangement) {
                    zone.title?.takeIf { zone.type !in signageNonTextZoneTypes }?.let { Text(it.uppercase(), color = parseDisplayColor(zone.accentColor), fontSize = signageSp(14, designScale), letterSpacing = (2f * designScale).sp, modifier = Modifier.fillMaxWidth(), textAlign = signageTextAlignment(zone.textAlign)) }
                    if (zone.type == "clock") {
                        SignageClock(zone, designScale)
                    } else if (zone.type == "weather") {
                        SignageWeather(zone, designScale)
                    } else if (zone.type == "calendar") {
                        SignageCalendar(zone, designScale)
                    } else if (zone.type == "qr" || zone.type == "wifi") {
                        zone.qrValue?.let { SignageQrCode(it, zone, designScale) }
                    } else if (zone.type == "counter") {
                        SignageCounter(zone, designScale)
                    } else if (zone.type !in signageNonTextZoneTypes) {
                        val body = zone.cached?.text?.ifBlank { null } ?: zone.content
                        body?.let { Text(signageText(zone, it), color = parseDisplayColor(zone.textColor),
                            fontSize = signageSp(zone.fontSize.coerceIn(8, 180), designScale),
                            lineHeight = signageSp(zone.fontSize.coerceIn(8, 180) * zone.lineHeightPercent.coerceIn(80, 300) / 100f, designScale),
                            fontFamily = signageFontFamily(zone.fontFamily),
                            fontWeight = FontWeight(zone.fontWeight.coerceIn(100, 900)),
                            fontStyle = if (zone.italic) FontStyle.Italic else FontStyle.Normal,
                            textDecoration = if (zone.underline) TextDecoration.Underline else TextDecoration.None,
                            textAlign = signageTextAlignment(zone.textAlign),
                            modifier = Modifier.fillMaxWidth().let { base -> if (zone.type == "ticker") base.basicMarquee(
                                iterations = Int.MAX_VALUE,
                                velocity = (zone.tickerSpeed.coerceIn(10, 300) / 2f).dp
                            ) else base }) }
                        zone.cached?.items?.take(8)?.forEach { Text("• $it", color = parseDisplayColor(zone.textColor), fontSize = signageSp(18, designScale), modifier = Modifier.fillMaxWidth().padding(top = 7.dp), textAlign = signageTextAlignment(zone.textAlign)) }
                    }
                }
                if (zone.renderSupport == "fallback") {
                    Column(
                        Modifier.fillMaxSize().zIndex(10_000f).background(parseDisplayColor(zone.backgroundColor))
                            .padding(24.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.Center
                    ) {
                        Text("CONTENT UNAVAILABLE", color = parseDisplayColor(zone.accentColor),
                            fontSize = 14.sp, fontWeight = FontWeight.Bold, letterSpacing = 2.sp)
                        Text(zone.title ?: "Signage element", color = parseDisplayColor(zone.textColor),
                            fontSize = 30.sp, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center)
                        Text(zone.fallbackMessage ?: "This element is not supported by this display.",
                            color = parseDisplayColor(zone.textColor), fontSize = 18.sp, textAlign = TextAlign.Center)
                    }
                }
            }
        }
    }
}

private fun signageHorizontalAlignment(value: String): androidx.compose.ui.Alignment.Horizontal = when (value) {
    "center" -> Alignment.CenterHorizontally
    "right" -> Alignment.End
    else -> Alignment.Start
}

private fun signageTextAlignment(value: String): TextAlign = when (value) {
    "center" -> TextAlign.Center
    "right" -> TextAlign.Right
    "justify" -> TextAlign.Justify
    else -> TextAlign.Left
}

private fun signageSp(value: Number, scale: Float): TextUnit =
    maxOf(8f, value.toFloat() * scale).sp

private val signageWebZoneTypes = setOf("webpage", "customHtml")
private val signageNonTextZoneTypes = signageWebZoneTypes + setOf("qr", "wifi", "presentation", "stream", "weather")

private fun signageFontFamily(value: String?) = when (value?.lowercase()) {
    "georgia", "serif" -> FontFamily.Serif
    "monospace" -> FontFamily.Monospace
    "arial", "sans-serif" -> FontFamily.SansSerif
    else -> FontFamily.Default
}

private fun signageText(zone: SignageZone, fallback: String) = runCatching {
    val runs = JSONArray(zone.richTextJson ?: return@runCatching buildAnnotatedString { append(fallback) })
    buildAnnotatedString {
        for (index in 0 until minOf(runs.length(), 50)) {
            val run = runs.optJSONObject(index) ?: continue
            val color = run.optString("color").takeIf { it.matches(Regex("^#[0-9a-fA-F]{6}$")) }
            withStyle(SpanStyle(
                color = color?.let(::parseDisplayColor) ?: Color.Unspecified,
                fontWeight = if (run.optBoolean("bold")) FontWeight.Bold else FontWeight.Normal,
                fontStyle = if (run.optBoolean("italic")) FontStyle.Italic else FontStyle.Normal,
                textDecoration = if (run.optBoolean("underline")) TextDecoration.Underline else TextDecoration.None,
                fontFamily = run.optString("fontFamily").takeIf(String::isNotBlank)
                    ?.let(::signageFontFamily),
                fontSize = run.optInt("fontSize").takeIf { it in 8..300 }?.sp ?: TextUnit.Unspecified
            )) { append(run.optString("text")) }
        }
    }.takeIf { it.isNotEmpty() } ?: buildAnnotatedString { append(fallback) }
}.getOrElse { buildAnnotatedString { append(fallback) } }

@Composable
private fun SignageCounter(zone: SignageZone, designScale: Float) {
    var now by remember(zone.id) { mutableStateOf(Instant.now()) }
    LaunchedEffect(zone.id, zone.counterTargetAt) {
        while (true) {
            kotlinx.coroutines.delay(1_000)
            now = Instant.now()
        }
    }
    val target = zone.counterTargetAt?.let {
        if (!zone.counterRepeatWeekly || it.isAfter(now)) it
        else it.plusSeconds((((now.epochSecond - it.epochSecond) / 604_800L) + 1L) * 604_800L)
    }
    val remaining = target?.epochSecond?.minus(now.epochSecond)?.coerceAtLeast(0)
    val countdown = remaining?.let {
        val days = it / 86_400
        val hours = (it % 86_400) / 3_600
        val minutes = (it % 3_600) / 60
        val seconds = it % 60
        if (days > 0) "$days days  ${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}"
        else "${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}"
    }
    val text = countdown?.let { value -> zone.content?.replace("[countdown]", value) ?: value }
        ?: (zone.content ?: "Countdown")
    Text(text, color = parseDisplayColor(zone.textColor), fontSize = signageSp(zone.fontSize.coerceIn(8, 180), designScale),
        fontFamily = signageFontFamily(zone.fontFamily), fontWeight = FontWeight(zone.fontWeight.coerceIn(100, 900)))
}

@Composable
private fun SignageQrCode(value: String, zone: SignageZone, designScale: Float) {
    val bitmap = remember(value) {
        runCatching {
            val matrix = MultiFormatWriter().encode(value, BarcodeFormat.QR_CODE, 480, 480,
                mapOf(EncodeHintType.MARGIN to 1))
            Bitmap.createBitmap(matrix.width, matrix.height, Bitmap.Config.ARGB_8888).apply {
                for (y in 0 until matrix.height) for (x in 0 until matrix.width) {
                    setPixel(x, y, if (matrix[x, y]) AndroidColor.BLACK else AndroidColor.WHITE)
                }
            }.asImageBitmap()
        }.getOrNull()
    }
    bitmap?.let { image ->
        BoxWithConstraints(Modifier.fillMaxSize()) {
            val placement = zone.qrPlacement
            val hasOuterLabels = !zone.qrLabelTop.isNullOrBlank() || !zone.qrLabelBottom.isNullOrBlank()
            val qrFraction = zone.qrSizePercent.coerceIn(20, 90) / 100f
            val qrSize = minOf(maxWidth * qrFraction, maxHeight * if (hasOuterLabels) .72f else .98f)
            val labelStyle = signageSp(zone.fontSize.coerceIn(12, 96), designScale)
            val label: @Composable (String?, TextAlign, Modifier) -> Unit = { value, align, modifier ->
                value?.takeIf(String::isNotBlank)?.let { Text(it, color = parseDisplayColor(zone.textColor), fontSize = labelStyle,
                    lineHeight = (labelStyle.value * 1.1f).sp, textAlign = align, modifier = modifier) }
            }
            Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally) {
                label(zone.qrLabelTop, TextAlign.Center, Modifier.fillMaxWidth())
                Row(Modifier.weight(1f).fillMaxWidth(), verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.Center) {
                    when (placement) {
                        "left" -> {
                            Image(bitmap = image, contentDescription = "QR code", contentScale = ContentScale.Fit, modifier = Modifier.size(qrSize))
                            label(zone.qrLabelRight, TextAlign.Left, Modifier.weight(1f).padding(start = 8.dp))
                        }
                        "right" -> {
                            label(zone.qrLabelLeft, TextAlign.Right, Modifier.weight(1f).padding(end = 8.dp))
                            Image(bitmap = image, contentDescription = "QR code", contentScale = ContentScale.Fit, modifier = Modifier.size(qrSize))
                        }
                        else -> {
                            label(zone.qrLabelLeft, TextAlign.Right, Modifier.weight(1f).padding(end = 8.dp))
                            Image(bitmap = image, contentDescription = "QR code", contentScale = ContentScale.Fit, modifier = Modifier.size(qrSize))
                            label(zone.qrLabelRight, TextAlign.Left, Modifier.weight(1f).padding(start = 8.dp))
                        }
                    }
                }
                label(zone.qrLabelBottom, TextAlign.Center, Modifier.fillMaxWidth())
            }
        }
    }
}

@Composable
private fun SignageWeather(zone: SignageZone, designScale: Float) {
    val snapshot = zone.cached?.weather
    val fields = zone.weatherFields.split(',').map(String::trim).filter(String::isNotBlank).toSet()
    val unit = snapshot?.temperatureUnit ?: if (zone.weatherUnits == "celsius") "°C" else "°F"
    val condition = snapshot?.conditions ?: zone.cached?.text.orEmpty()
    val icon = zone.cached?.icon?.takeIf(String::isNotBlank) ?: when (condition.lowercase()) {
        in listOf("clear", "sunny", "sun") -> "☀"
        in listOf("partly-cloudy", "partly cloudy") -> "⛅"
        in listOf("rain", "rainy", "showers") -> "🌧"
        in listOf("snow", "snowy") -> "❄"
        in listOf("storm", "thunderstorm") -> "⛈"
        else -> "☁"
    }
    val details = buildList {
        if ("feelsLike" in fields && snapshot?.feelsLike != null) add("Feels like ${snapshot.feelsLike.toInt()}$unit")
        if ("high" in fields && snapshot?.high != null) add("High ${snapshot.high.toInt()}$unit")
        if ("low" in fields && snapshot?.low != null) add("Low ${snapshot.low.toInt()}$unit")
        if ("precipitation" in fields && snapshot?.precipitation != null) add("Precipitation ${snapshot.precipitation.toInt()}%")
        if ("humidity" in fields && snapshot?.humidity != null) add("Humidity ${snapshot.humidity.toInt()}%")
        if ("wind" in fields && snapshot?.wind != null) add("Wind ${snapshot.wind.toInt()} ${snapshot.windUnit.orEmpty()}".trim())
        if ("forecast" in fields) snapshot?.forecast?.takeIf(String::isNotBlank)?.let(::add)
        if ("sunrise" in fields) snapshot?.sunrise?.takeIf(String::isNotBlank)?.let { add("Sunrise $it") }
        if ("sunset" in fields) snapshot?.sunset?.takeIf(String::isNotBlank)?.let { add("Sunset $it") }
    }
    val iconContent: @Composable () -> Unit = {
        if ("icon" in fields) Text(icon,
            color = if (zone.weatherIconStyle == "white") Color.White else parseDisplayColor(zone.accentColor),
            fontSize = signageSp(zone.weatherIconSize.coerceIn(20, 180), designScale), lineHeight = signageSp(zone.weatherIconSize, designScale))
    }
    val title: @Composable () -> Unit = {
        Text(zone.title ?: zone.cached?.title ?: zone.weatherLocation.orEmpty(),
            color = parseDisplayColor(zone.textColor), fontSize = signageSp(zone.weatherTitleSize.coerceIn(8, 100), designScale),
            fontWeight = FontWeight.Bold, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
    }
    val detailsContent: @Composable () -> Unit = {
        if (details.isNotEmpty()) Text(details.joinToString("  •  "), color = parseDisplayColor(zone.textColor),
            fontSize = signageSp(zone.weatherDetailsSize.coerceIn(8, 100), designScale),
            textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
    }
    val values: @Composable () -> Unit = {
        Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.wrapContentWidth()) {
            if ("temperature" in fields && snapshot?.temperature != null)
                Text("${snapshot.temperature.toInt()}$unit", color = parseDisplayColor(zone.textColor),
                    fontSize = signageSp(zone.weatherTemperatureSize.coerceIn(16, 180), designScale), fontWeight = FontWeight.Bold,
                    textAlign = TextAlign.Center)
            if ("conditions" in fields && condition.isNotBlank())
                Text(condition, color = parseDisplayColor(zone.textColor),
                    fontSize = signageSp(zone.weatherDetailsSize.coerceIn(8, 100), designScale), textAlign = TextAlign.Center)
            detailsContent()
        }
    }
    when (zone.weatherLayout) {
        "icon-top" -> Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center) { title(); iconContent(); values() }
        "icon-right" -> Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center) { title(); Row(horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) { values(); Spacer(Modifier.width(18.dp)); iconContent() } }
        "compact" -> Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center) { Row(horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) { iconContent(); Spacer(Modifier.width(12.dp)); title(); Spacer(Modifier.width(12.dp)); values() }; detailsContent() }
        else -> Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center) { title(); Row(horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) { iconContent(); Spacer(Modifier.width(18.dp)); values() } }
    }
}

@Composable
private fun SignageCalendar(zone: SignageZone, designScale: Float) {
    val fields = zone.calendarFields.split(',').map(String::trim).filter(String::isNotBlank).toSet()
    val available = zone.cached?.events.orEmpty()
    val events = if (zone.calendarMaxItems > 0) available.take(zone.calendarMaxItems) else available
    if (events.isEmpty()) {
        Text(zone.cached?.text?.ifBlank { null } ?: zone.content ?: "No upcoming events",
            color = parseDisplayColor(zone.textColor), fontSize = signageSp(zone.fontSize.coerceIn(8, 180), designScale),
            textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
        return
    }
    Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(5.dp)) {
        events.forEach { event ->
            val starts = event.startsAt?.atZone(java.time.ZoneId.systemDefault())
            val ends = event.endsAt?.atZone(java.time.ZoneId.systemDefault())
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.Top) {
                Box(Modifier.width(3.dp).height(35.dp).background(parseDisplayColor(zone.accentColor)))
                Column(Modifier.weight(1f).padding(start = 7.dp)) {
                    if ("title" in fields) Text(event.title, color = parseDisplayColor(zone.accentColor),
                        fontSize = signageSp(zone.fontSize.coerceIn(8, 180), designScale), fontWeight = FontWeight.Bold,
                        maxLines = 2, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                    if (starts != null && ("date" in fields || "time" in fields)) {
                        if ("date" in fields) Text(starts.format(DateTimeFormatter.ofPattern("MMMM d")), color = parseDisplayColor(zone.textColor),
                            fontSize = signageSp(zone.fontSize * .62f, designScale), maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                        if ("time" in fields && !event.allDay) {
                            val timeText = starts.format(DateTimeFormatter.ofPattern("h:mm a")) +
                                if (ends != null && ends.toLocalDate() == starts.toLocalDate()) " - ${ends.format(DateTimeFormatter.ofPattern("h:mm a"))}" else ""
                            Text(timeText, color = parseDisplayColor(zone.textColor), fontSize = signageSp(zone.fontSize * .62f, designScale),
                                maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                        }
                    }
                    if ("description" in fields && !event.description.isNullOrBlank())
                        Text(event.description, color = parseDisplayColor(zone.textColor), fontSize = signageSp(zone.fontSize * .45f, designScale), maxLines = 2,
                            overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                    if ("location" in fields && !event.location.isNullOrBlank())
                        Text(event.location, color = parseDisplayColor(zone.accentColor), fontSize = signageSp(zone.fontSize * .42f, designScale), maxLines = 1,
                            overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                }
            }
        }
    }
}

@Composable
private fun SignageClock(zone: SignageZone, designScale: Float) {
    var now by remember(zone.id) { mutableStateOf(ZonedDateTime.now()) }
    LaunchedEffect(zone.id) { while (true) { kotlinx.coroutines.delay(1_000); now = ZonedDateTime.now() } }
    var timePattern = when (zone.clockTimeFormat) {
        "24h" -> "HH:mm"
        "24h-seconds" -> "HH:mm:ss"
        "12h-seconds" -> "h:mm:ss a"
        else -> "h:mm a"
    }
    if (!zone.clockShowPeriod) timePattern = timePattern.replace(" a", "")
    var datePattern = when (zone.clockDateFormat) {
        "numeric" -> "MM/dd/yyyy"
        "short" -> "MMM d"
        "medium" -> "EEE, MMM d"
        else -> "EEEE, MMMM d, yyyy"
    }
    if (!zone.clockShowWeekday) datePattern = datePattern.replace("EEEE, ", "").replace("EEE, ", "")
    if (!zone.clockShowYear) datePattern = datePattern.replace(", yyyy", "").replace("/yyyy", "")
    val textAlignment = signageTextAlignment(zone.textAlign)
    val horizontalAlignment = signageHorizontalAlignment(zone.textAlign)
    val time: @Composable () -> Unit = { Text(now.format(DateTimeFormatter.ofPattern(timePattern)),
        color = parseDisplayColor(zone.textColor), fontSize = signageSp(zone.clockTimeFontSize.coerceIn(8, 180), designScale),
        modifier = Modifier.fillMaxWidth(), textAlign = textAlignment) }
    val date: @Composable () -> Unit = { Text(now.format(DateTimeFormatter.ofPattern(datePattern)),
        color = parseDisplayColor(zone.textColor), fontSize = signageSp(zone.clockDateFontSize.coerceIn(8, 180), designScale),
        modifier = Modifier.fillMaxWidth(), textAlign = textAlignment) }
    Column(Modifier.fillMaxWidth(), horizontalAlignment = horizontalAlignment) {
        when (zone.clockDisplay) {
            "time" -> time()
            "date" -> date()
            else -> if (zone.clockOrder == "inline") {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = when (zone.textAlign) { "center" -> Arrangement.Center; "right" -> Arrangement.End; else -> Arrangement.Start }, verticalAlignment = Alignment.CenterVertically) { time(); date() }
            } else if (zone.clockOrder == "date-time") { date(); time() } else { time(); date() }
        }
    }
}

@SuppressLint("SetJavaScriptEnabled")
@Composable
private fun SignageWebZone(source: String) {
    // Compare against the requested source, not WebView.url: a redirect or in-page
    // navigation must not trigger a reload on every unrelated recomposition.
    var lastRequestedSource by remember { mutableStateOf<String?>(null) }
    AndroidView(factory = { context ->
        WebView(context).apply {
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            webViewClient = WebViewClient()
            webChromeClient = WebChromeClient()
        }
    }, update = { view ->
        if (lastRequestedSource != source) {
            lastRequestedSource = source
            view.loadUrl(source)
        }
    }, onRelease = { view ->
        view.stopLoading()
        view.loadUrl("about:blank")
        view.webChromeClient = null
        view.webViewClient = WebViewClient()
        view.destroy()
    }, modifier = Modifier.fillMaxSize())
}

@Composable
private fun SignagePresentationZone(zone: SignageZone) {
    val entries = zone.contentPlaylist?.items.orEmpty().filter(::isRenderableSignageEntry)
    var index by remember(zone.contentPlaylist?.id, zone.contentPlaylist?.version) { mutableIntStateOf(0) }
    var streamLive by remember(zone.id, zone.streamUrl) { mutableStateOf(false) }
    val entrySchedule = entries.map { "${it.id}:${it.durationSeconds}" }
    LaunchedEffect(zone.contentPlaylist?.id, zone.contentPlaylist?.version, entrySchedule, streamLive) {
        if (entries.isNotEmpty() && !streamLive) {
            var nextIndex = index % entries.size
            while (true) {
                kotlinx.coroutines.delay(entries[nextIndex].durationSeconds.coerceAtLeast(1) * 1_000L)
                nextIndex = (nextIndex + 1) % entries.size
                index = nextIndex
            }
        }
    }
    val entry = entries.getOrNull(index % maxOf(1, entries.size))
    Box(Modifier.fillMaxSize()) {
        entry?.layout?.let { layout ->
            SignageZoneLayout(SignageCue(zone.id, layout.name, "always", 0, "", layout.backgroundColor,
                zone.textColor, null, zones = layout.zones, backgroundAudio = layout.backgroundAudio))
        } ?: entry?.media?.let { SignageZoneMedia(it, zone.fit) }
            ?: entry?.sourceUrl?.let { SignageWebZone(it) }
        if (zone.streamOverrideWhenLive && zone.streamUrl != null) {
            Box(Modifier.fillMaxSize().graphicsLayer(alpha = if (streamLive) 1f else 0f)) {
                SignageVideo("presentation-stream-${zone.id}", zone.streamUrl, zone.fit,
                    onAvailabilityChange = { streamLive = it })
            }
        }
    }
}

@Composable
private fun SignageZoneMedia(item: CueItem, fit: String = "cover") {
    val cacheSource = rememberResilientCacheSource(item)
    val source = cacheSource.source
    if (source == null) {
        item.streamingFallbackUrl?.let { SignageWebZone(it) }
        return
    }
    if (item.type == "image" || item.contentType?.startsWith("image/") == true) {
        val scale = when (fit) { "contain" -> ContentScale.Fit; "fill" -> ContentScale.FillBounds; else -> ContentScale.Crop }
        ResilientCachedImage(item, null, scale, Modifier.fillMaxSize())
        return
    }
    if (item.type != "video" && item.contentType?.startsWith("video/") != true) return
    SignageVideo(item.id, source, fit,
        fallbackSource = item.url?.takeIf { cacheSource.usingCache },
        onCacheFailure = cacheSource.onCacheFailure)
}

@Composable
private fun SignageStreamMedia(id: String, source: String, fit: String) {
    SignageVideo("stream-$id", source, fit)
}

@Composable
private fun SignageBackgroundAudio(item: CueItem, volumePercent: Int) {
    val context = LocalContext.current
    val cacheSource = rememberResilientCacheSource(item)
    val source = cacheSource.source ?: return
    val player = remember(item.id, source) { ExoPlayer.Builder(context).build().apply {
        setMediaItem(MediaItem.fromUri(source)); repeatMode = Player.REPEAT_MODE_ONE
        volume = (volumePercent.coerceIn(0, 100) / 100f); prepare(); playWhenReady = true
    } }
    DisposableEffect(player) {
        val listener = object : Player.Listener {
            override fun onPlayerError(error: PlaybackException) {
                if (cacheSource.usingCache) cacheSource.onCacheFailure()
            }
        }
        player.addListener(listener)
        onDispose { player.removeListener(listener); player.release() }
    }
}

@Composable
@SuppressLint("UnsafeOptInUsageError")
private fun SignageVideo(id: String, source: String, fit: String,
    onAvailabilityChange: ((Boolean) -> Unit)? = null,
    fallbackSource: String? = null,
    onCacheFailure: (() -> Unit)? = null) {
    val context = LocalContext.current
    val currentAvailabilityChange by rememberUpdatedState(onAvailabilityChange)
    val player = remember(id, source) { ExoPlayer.Builder(context).build().apply {
        setMediaItem(MediaItem.fromUri(source)); repeatMode = Player.REPEAT_MODE_ONE; volume = 0f; prepare(); playWhenReady = true
    } }
    DisposableEffect(player) {
        val listener = object : Player.Listener {
            override fun onPlaybackStateChanged(playbackState: Int) {
                if (playbackState == Player.STATE_READY) currentAvailabilityChange?.invoke(true)
                else if (playbackState == Player.STATE_IDLE) currentAvailabilityChange?.invoke(false)
            }
            override fun onPlayerError(error: PlaybackException) {
                if (fallbackSource != null && source != fallbackSource && onCacheFailure != null)
                    onCacheFailure()
                else currentAvailabilityChange?.invoke(false)
            }
        }
        player.addListener(listener)
        onDispose {
            currentAvailabilityChange?.invoke(false)
            player.removeListener(listener)
            player.release()
        }
    }
    val resizeMode = when (fit) { "contain" -> AspectRatioFrameLayout.RESIZE_MODE_FIT; "fill" -> AspectRatioFrameLayout.RESIZE_MODE_FILL; else -> AspectRatioFrameLayout.RESIZE_MODE_ZOOM }
    AndroidView(factory = { PlayerView(it).apply { useController = false } },
        update = { view ->
            if (view.player !== player) view.player = player
            if (view.resizeMode != resizeMode) view.resizeMode = resizeMode
        }, onRelease = { it.player = null }, modifier = Modifier.fillMaxSize())
}

@Composable
private fun SignageBackdrop(item: CueItem) {
    val cacheSource = rememberResilientCacheSource(item)
    val source = cacheSource.source
    if (source == null) {
        item.streamingFallbackUrl?.let { SignageWebZone(it) }
        return
    }
    if (item.type == "image" || item.contentType?.startsWith("image/") == true) {
        ResilientCachedImage(item, null, ContentScale.Crop,
            Modifier.fillMaxSize().graphicsLayer(alpha = .38f))
        return
    }
    if (item.type != "video" && item.contentType?.startsWith("video/") != true) return
    SignageVideo(item.id, source, "cover",
        fallbackSource = item.url?.takeIf { cacheSource.usingCache },
        onCacheFailure = cacheSource.onCacheFailure)
}

private fun parseDisplayColor(value: String): Color = runCatching {
    Color(android.graphics.Color.parseColor(value))
}.getOrDefault(Navy)

@Composable
internal fun LessonDetailScreen(
    playlist: LessonPlaylist,
    screenName: String,
    connectionMode: ConnectionMode,
    onBack: () -> Unit,
    onManualDownload: (LessonPlaylist) -> Unit = {},
    onPlay: (List<CueItem>, Int) -> Unit
) {
    val context = LocalContext.current
    val inputModeManager = LocalInputModeManager.current
    val timeline = playlist.timeline()
    val allItems = timeline.map(TimelineCue::item)
    val initialIndex = playlist.initialCueIndex()
    var selectedCueId by remember(playlist.id) {
        mutableStateOf(timeline.getOrNull(initialIndex)?.item?.id)
    }
    val selectedIndex = timeline.indexOfFirst { it.item.id == selectedCueId }
        .takeIf { it >= 0 } ?: initialIndex
    val selected = timeline.getOrNull(selectedIndex)
    val firstFocus = remember(playlist.id) { FocusRequester() }
    val cueListState = rememberLazyListState(initialFirstVisibleItemIndex = initialIndex.coerceAtLeast(0))
    BackHandler(onBack = onBack)
    LaunchedEffect(playlist.id, initialIndex) {
        if (initialIndex >= 0) {
            inputModeManager.requestInputMode(InputMode.Keyboard)
            cueListState.scrollToItem(initialIndex)
            kotlinx.coroutines.delay(80)
            firstFocus.requestFocus()
        }
    }
    Column(
        Modifier.fillMaxSize().background(LessonCueTvColors.Background)
            .padding(horizontal = LessonCueTvDimens.ScreenHorizontal, vertical = 22.dp)
    ) {
        TvHeader(screenName, connectionMode)
        Spacer(Modifier.height(14.dp))
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            LessonCueButton(
                onClick = onBack,
                modifier = Modifier.width(220.dp).height(44.dp).testTag("back-to-lessons")
            ) {
                Text("‹  Back to lessons", fontWeight = FontWeight.Bold)
            }
            Spacer(Modifier.width(20.dp))
            Column(Modifier.weight(1f)) {
                Text(playlist.title, color = Cream, fontSize = 30.sp, fontWeight = FontWeight.Bold,
                    maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                Text("Select a cue to start from that point.", color = Muted, fontSize = 15.sp)
            }
            LessonCueButton(
                onClick = { onManualDownload(playlist) },
                modifier = Modifier.width(210.dp).height(48.dp)
            ) { Text("⇩  Download lesson", fontSize = 15.sp, fontWeight = FontWeight.Bold) }
        }
        Spacer(Modifier.height(14.dp))
        Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(28.dp)) {
            TvPanel(Modifier.weight(1.45f).fillMaxSize()) {
                if (selected == null) {
                    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Text("No media has been added to this lesson.", color = Muted, fontSize = 23.sp)
                    }
                } else {
                    Row(Modifier.fillMaxSize().padding(12.dp), horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                        CuePreview(
                            selected.item,
                            Modifier.weight(1.08f).fillMaxSize(),
                            selectedLabel = "SELECTED CUE"
                        )
                        Column(Modifier.weight(.92f).fillMaxSize(), verticalArrangement = Arrangement.Center) {
                            StatusBadge(selected.role.label, cueRoleColor(selected.role))
                            Spacer(Modifier.height(4.dp))
                            Text(selected.item.title, color = Cream, fontSize = 20.sp, fontWeight = FontWeight.Bold,
                                maxLines = 2, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                            Spacer(Modifier.height(2.dp))
                            Text(
                                "${selected.item.type.replaceFirstChar { it.uppercase() }}  •  ${formatDuration(selected.item.effectiveDurationMs())}",
                                color = Muted,
                                fontSize = 16.sp,
                            )
                            Spacer(Modifier.height(3.dp))
                            Text(cueAvailabilityLabel(context, selected.item), color = cueAvailabilityColor(context, selected.item),
                                fontSize = 14.sp, fontWeight = FontWeight.Bold, maxLines = 1,
                                overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                            Spacer(Modifier.height(5.dp))
                            LessonCueButton(
                                onClick = { onPlay(allItems, selectedIndex) },
                                modifier = Modifier.fillMaxWidth().height(40.dp)
                            ) {
                                Text("▶  Play from here", fontSize = 15.sp, fontWeight = FontWeight.Bold)
                            }
                            Spacer(Modifier.height(4.dp))
                            LessonCueButton(
                                onClick = { onPlay(allItems, 0) },
                                modifier = Modifier.fillMaxWidth().height(34.dp)
                            ) { Text("Start from beginning", fontSize = 14.sp, fontWeight = FontWeight.Bold) }
                        }
                    }
                }
            }
            Column(Modifier.weight(.95f).fillMaxSize()) {
                Text("Cue timeline", color = Cream, fontSize = 28.sp, fontWeight = FontWeight.Bold)
                Text("Pre-roll, countdown, lesson, and post-lesson media", color = Muted, fontSize = 15.sp)
                Spacer(Modifier.height(14.dp))
                LazyColumn(Modifier.weight(1f), state = cueListState, verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    itemsIndexed(timeline, key = { _, cue -> cue.item.id }) { index, cue ->
                        FocusedTvCard(
                            onClick = { onPlay(allItems, index) },
                            onFocused = { selectedCueId = cue.item.id },
                            initialFocusRequester = firstFocus.takeIf { index == initialIndex },
                            selected = cue.item.id == selectedCueId,
                            modifier = remoteListItemModifier().testTag("cue-card-${cue.item.id}")
                        ) {
                            Row(
                                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 13.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Box(
                                    Modifier.size(52.dp).background(
                                        if (cue.item.id == selectedCueId) cueRoleColor(cue.role) else LessonCueTvColors.Border,
                                        RoundedCornerShape(12.dp)
                                    ),
                                    contentAlignment = Alignment.Center
                                ) {
                                    Text("${index + 1}".padStart(2, '0'), color = if (cue.item.id == selectedCueId) Navy else Cream,
                                        fontSize = 19.sp, fontWeight = FontWeight.Bold)
                                }
                                Spacer(Modifier.width(14.dp))
                                Column(Modifier.weight(1f)) {
                                    Text(cue.item.title, color = Cream, fontSize = 19.sp, fontWeight = FontWeight.Bold,
                                        maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                                    Text("${cue.role.label}  •  ${cue.item.type.uppercase()}", color = cueRoleColor(cue.role),
                                        fontSize = 12.sp, fontWeight = FontWeight.Bold)
                                }
                                Text(formatDuration(cue.item.effectiveDurationMs()), color = Cream, fontSize = 15.sp,
                                    fontWeight = FontWeight.Bold)
                            }
                        }
                    }
                }
            }
        }
        Spacer(Modifier.height(12.dp))
        RemoteHintStrip(
            statusContent = {
                StatusBadge(
                    if (timeline.all { cachedMediaFile(context, it.item) != null || !it.item.offlineEligible }) "ALL MEDIA READY"
                    else "MEDIA CACHING",
                    if (timeline.all { cachedMediaFile(context, it.item) != null || !it.item.offlineEligible }) Mint else Gold
                )
            },
            hints = listOf("↑ ↓" to "Move through cues", "●" to "Play selected cue", "◀" to "Return to lessons"),
            modifier = Modifier.fillMaxWidth()
        )
    }
}

private fun cueRoleColor(role: CueRole): Color = when (role) {
    CueRole.PreRoll -> Color(0xFF48A8FF)
    CueRole.Countdown -> Color(0xFFA879FF)
    CueRole.Lesson -> LessonCueTvColors.FocusOrange
    CueRole.PostLesson -> Color(0xFF3DD6C5)
}

private fun cueAvailabilityLabel(context: android.content.Context, item: CueItem): String = when {
    isBundledDemoMedia(item.url) -> "Bundled with this app"
    cachedMediaFile(context, item) != null -> "Downloaded and ready"
    item.offlineEligible -> "Downloading for offline playback"
    item.playbackUrl != null || item.streamingFallbackUrl != null -> "Internet connection required"
    item.url != null -> "Available from LessonCue server"
    else -> "Media unavailable"
}

private fun cueAvailabilityColor(context: android.content.Context, item: CueItem): Color = when {
    isBundledDemoMedia(item.url) -> Mint
    cachedMediaFile(context, item) != null -> Mint
    item.offlineEligible -> Gold
    item.url == null && item.playbackUrl == null && item.streamingFallbackUrl == null -> Coral
    else -> Muted
}

@Composable
private fun remoteListItemModifier(): Modifier {
    val requester = remember { BringIntoViewRequester() }
    val scope = rememberCoroutineScope()
    return Modifier.fillMaxWidth().bringIntoViewRequester(requester).onFocusChanged { state ->
        if (state.isFocused) scope.launch { requester.bringIntoView() }
    }
}

@Composable
private fun playbackRemoteModifier(
    itemId: String,
    onAction: (PlaybackRemoteAction) -> Unit
): Modifier {
    val focusRequester = remember(itemId) { FocusRequester() }
    val remoteKeys = remember(itemId) { PlaybackRemoteKeyState() }
    LaunchedEffect(itemId) {
        runCatching { focusRequester.requestFocus() }
    }
    return Modifier
        .focusRequester(focusRequester)
        .onPreviewKeyEvent { event ->
            val native = event.nativeKeyEvent
            val decision = remoteKeys.handle(native.keyCode, native.action, native.repeatCount)
            decision.action?.let(onAction)
            decision.consumed
        }
        .focusable()
}

@Composable
@SuppressLint("UnsafeOptInUsageError")
private fun PlayerScreen(playlist: LessonPlaylist, items: List<CueItem>, index: Int, seekMs: Long,
    control: ControlCommand?, identity: DeviceIdentity?, onTelemetry: (PlaybackTelemetry) -> Unit, onExit: () -> Unit,
    onFinished: () -> Unit, onNext: (Int) -> Unit) {
    val item = items.getOrNull(index)
    BackHandler(onBack = onExit)
    if (item?.renderSupport == "fallback") {
        LaunchedEffect(item.id) {
            onTelemetry(PlaybackTelemetry("unavailable", playlist.id, item.id,
                error = item.fallbackMessage ?: "This item is not supported by this display."))
        }
        UnavailableMediaScreen(
            title = item.title,
            message = item.fallbackMessage ?: "This item cannot be displayed on this TV.",
            hasPrevious = index > 0,
            hasNext = index + 1 < items.size,
            onPrevious = { onNext(index - 1) },
            onNext = { onNext(index + 1) },
            onExit = onExit
        )
        return
    }
    if (item != null && shouldUseOnlinePlayback(item)) {
        OnlineMediaScreen(playlist, items, index, item, control, identity, onTelemetry, onExit, onNext)
        return
    }
    var useYouTubeStreamingFallback by remember(item?.id, item?.streamingFallbackUrl) { mutableStateOf(false) }
    if (useYouTubeStreamingFallback && item?.streamingFallbackUrl != null) {
        OnlineMediaScreen(playlist, items, index, item, control, identity, onTelemetry, onExit, onNext)
        return
    }
    if (item?.url == null) {
        LaunchedEffect(item?.id) { onTelemetry(PlaybackTelemetry("error", playlist.id, item?.id,
            error = "This item is not available on the server.")) }
        UnavailableMediaScreen(
            title = item?.title ?: "Nothing to play",
            message = "This item is not available on the LessonCue server.",
            hasPrevious = index > 0,
            hasNext = index + 1 < items.size,
            onPrevious = { onNext(index - 1) },
            onNext = { onNext(index + 1) },
            onExit = onExit
        )
        return
    }
    val context = LocalContext.current
    val cacheSource = rememberResilientCacheSource(item)
    val cached = cacheSource.cached
    var visualOpacity by remember(item.id) { mutableStateOf(if (item.fadeInMs > 0) 0f else 1f) }
    var visualSize by remember(item.id) { mutableStateOf(IntSize.Zero) }
    var repeatCompleted by remember(item.id) { mutableIntStateOf(0) }
    var lastOverlayInteraction by remember(item.id) { mutableLongStateOf(System.currentTimeMillis()) }
    var overlayClock by remember(item.id) { mutableLongStateOf(System.currentTimeMillis()) }
    fun revealOverlay() {
        val now = System.currentTimeMillis()
        lastOverlayInteraction = now
        overlayClock = now
    }
    LaunchedEffect(item.id) {
        while (true) {
            overlayClock = System.currentTimeMillis()
            kotlinx.coroutines.delay(250)
        }
    }
    if (item.type == "image") {
        val duration = item.effectiveDurationMs()?.coerceAtLeast(1_000L) ?: Long.MAX_VALUE
        var position by remember(item.id, seekMs) { mutableLongStateOf(seekMs.coerceIn(0, duration)) }
        var playing by remember(item.id) { mutableStateOf(true) }
        LaunchedEffect(control?.version) {
            when (control?.action) {
                "pause" -> { playing = false; revealOverlay() }
                "resume" -> { playing = true; revealOverlay() }
            }
        }
        val remoteModifier = playbackRemoteModifier(item.id) { action ->
            revealOverlay()
            when (action) {
                PlaybackRemoteAction.Previous -> onNext((index - 1).coerceAtLeast(0))
                PlaybackRemoteAction.Next -> if (index + 1 < items.size) onNext(index + 1)
                PlaybackRemoteAction.Rewind ->
                    position = (position - REMOTE_SEEK_STEP_MS).coerceAtLeast(0)
                PlaybackRemoteAction.FastForward ->
                    position = (position + REMOTE_SEEK_STEP_MS).coerceAtMost(duration)
                PlaybackRemoteAction.TogglePlayPause -> playing = !playing
                PlaybackRemoteAction.Play -> playing = true
                PlaybackRemoteAction.Pause -> playing = false
            }
        }
        LaunchedEffect(item.id) {
            var telemetryElapsed = 0L
            while (true) {
                while (position < duration) {
                    visualOpacity = cueOpacity(item, position, duration)
                    if (telemetryElapsed == 0L) {
                        onTelemetry(PlaybackTelemetry(if (playing) "playing" else "paused",
                            playlist.id, item.id, position, duration.takeUnless { it == Long.MAX_VALUE }, item.volumePercent))
                    }
                    kotlinx.coroutines.delay(50)
                    if (playing) position = (position + (50 * (item.playbackRatePercent.coerceIn(25, 400) / 100f)).toLong()).coerceAtMost(duration)
                    telemetryElapsed = (telemetryElapsed + 50) % 1_000
                }
                repeatCompleted += 1
                if (item.endBehavior == "loop" || repeatCompleted < item.repeatCount.coerceIn(1, 99)) {
                    position = 0
                    continue
                }
                break
            }
            if (item.endBehavior == "advance" && index + 1 < items.size) onNext(index + 1)
            else if (item.endBehavior == "playlistLoop") onNext(0)
            else if (item.endBehavior != "pause" ||
                (index == items.lastIndex && items == playlist.items && playlist.postLesson?.items?.isNotEmpty() == true)) onFinished()
        }
        Box(Modifier.fillMaxSize().background(cueBackground(item)).then(remoteModifier)) {
            AsyncImage(model = cacheSource.source, contentDescription = item.title,
                contentScale = if (item.fitMode == "fill") ContentScale.Crop else ContentScale.Fit,
                onError = { cacheSource.onCacheFailure() },
                modifier = Modifier.fillMaxSize().onSizeChanged { visualSize = it }.cueVisual(item, visualOpacity, visualSize))
            PlaybackOverlay(
                visible = shouldShowPlaybackOverlay(lastOverlayInteraction, overlayClock, playing),
                lessonTitle = playlist.title,
                item = item,
                itemIndex = index,
                itemCount = items.size,
                positionMs = position,
                durationMs = duration.takeUnless { it == Long.MAX_VALUE },
                playing = playing,
            availabilityLabel = when {
                isBundledDemoMedia(item.url) -> "BUNDLED SAMPLE"
                cached != null -> "OFFLINE COPY"
                else -> "SERVER MEDIA"
            },
                actions = PlaybackOverlayActions(
                    previous = { revealOverlay(); if (index > 0) onNext(index - 1) },
                    rewind = { revealOverlay(); position = (position - REMOTE_SEEK_STEP_MS).coerceAtLeast(0) },
                    togglePlayPause = { revealOverlay(); playing = !playing },
                    fastForward = { revealOverlay(); position = (position + REMOTE_SEEK_STEP_MS).coerceAtMost(duration) },
                    next = { revealOverlay(); if (index + 1 < items.size) onNext(index + 1) },
                    exit = onExit
                ),
                modifier = Modifier.fillMaxSize()
            )
        }
        return
    }
    val streamingSources = remember(item.id, item.streamingSources, item.url) {
        item.streamingSources.filter { it.url.isNotBlank() }.ifEmpty {
            listOf(PlaybackSource("manifest", item.url, item.contentType))
        }
    }
    val initialStreamingSource = streamingSources.firstOrNull()
    var activeSourceIndex by remember(item.id, seekMs, cached?.absolutePath) {
        mutableIntStateOf(if (!cacheSource.usingCache) 0 else -1)
    }
    var fallbackDiagnostic by remember(item.id) { mutableStateOf<String?>(null) }
    fun clippedMediaItem(uri: String, contentType: String?): MediaItem {
        val clipping = MediaItem.ClippingConfiguration.Builder().setStartPositionMs(item.startMs).apply {
            item.endMs?.let { setEndPositionMs(it) }
        }.build()
        return MediaItem.Builder().setUri(uri).setMimeType(contentType)
            .setClippingConfiguration(clipping).build()
    }
    val player = remember(item.id, seekMs) {
        ExoPlayer.Builder(context).build().apply {
            val cachedUri = cacheSource.source?.takeIf { cacheSource.usingCache }
            val source = initialStreamingSource
            setMediaItem(clippedMediaItem(cachedUri ?: source?.url ?: item.url,
                if (cachedUri != null) item.contentType else source?.contentType ?: item.contentType))
            prepare()
            seekTo(seekMs.coerceAtLeast(0))
            volume = if (item.muted) 0f else (item.volumePercent / 100f).coerceIn(0f, 1.5f)
            setPlaybackSpeed(item.playbackRatePercent.coerceIn(25, 400) / 100f)
            playWhenReady = true
        }
    }
    var playerState by remember(item.id) { mutableStateOf("loading") }
    var playerPosition by remember(item.id) { mutableLongStateOf(seekMs.coerceAtLeast(0)) }
    var playerDuration by remember(item.id) { mutableStateOf<Long?>(item.effectiveDurationMs()) }
    fun tryNextPlaybackSource(reason: String): Boolean {
        if (activeSourceIndex < 0) cacheSource.onCacheFailure()
        val nextIndex = PlaybackFallbackPolicy.nextSourceIndex(activeSourceIndex, streamingSources.size)
        if (nextIndex == null) {
            val fallback = item.streamingFallbackUrl
            if (!fallback.isNullOrBlank() && !useYouTubeStreamingFallback) {
                useYouTubeStreamingFallback = true
                playerState = "buffering"
                fallbackDiagnostic = "Local YouTube copy failed; using the original streaming source."
                onTelemetry(PlaybackTelemetry("buffering", playlist.id, item.id,
                    player.currentPosition.coerceAtLeast(0), player.duration.takeUnless { it == C.TIME_UNSET },
                    item.volumePercent, fallbackDiagnostic))
                return true
            }
            return false
        }
        val nextSource = streamingSources[nextIndex]
        val previous = if (activeSourceIndex < 0) "offline cache"
            else streamingSources.getOrNull(activeSourceIndex)?.displayQuality() ?: "current source"
        val resumeAt = player.currentPosition.coerceAtLeast(0)
        activeSourceIndex = nextIndex
        fallbackDiagnostic = "Quality fallback: $previous → ${nextSource.displayQuality()} after $reason"
        Log.w("LessonCuePlayback", "${fallbackDiagnostic}; item=${item.id}; resumePositionMs=$resumeAt")
        playerState = "buffering"
        player.setMediaItem(clippedMediaItem(nextSource.url, nextSource.contentType ?: item.contentType))
        player.prepare()
        player.seekTo(resumeAt)
        player.playWhenReady = true
        onTelemetry(PlaybackTelemetry("buffering", playlist.id, item.id, resumeAt,
            player.duration.takeUnless { it == C.TIME_UNSET }, item.volumePercent, fallbackDiagnostic))
        return true
    }
    LaunchedEffect(fallbackDiagnostic) {
        val diagnostic = fallbackDiagnostic ?: return@LaunchedEffect
        kotlinx.coroutines.delay(8_000)
        if (fallbackDiagnostic == diagnostic) fallbackDiagnostic = null
    }
    val remoteModifier = playbackRemoteModifier(item.id) { action ->
        revealOverlay()
        when (action) {
            PlaybackRemoteAction.Previous -> onNext((index - 1).coerceAtLeast(0))
            PlaybackRemoteAction.Next -> if (index + 1 < items.size) onNext(index + 1)
            PlaybackRemoteAction.Rewind ->
                player.seekTo((player.currentPosition - REMOTE_SEEK_STEP_MS).coerceAtLeast(0))
            PlaybackRemoteAction.FastForward -> {
                val maximum = player.duration.takeIf { it != C.TIME_UNSET && it >= 0 } ?: Long.MAX_VALUE
                player.seekTo((player.currentPosition + REMOTE_SEEK_STEP_MS).coerceAtMost(maximum))
            }
            PlaybackRemoteAction.TogglePlayPause ->
                if (player.playWhenReady) player.pause() else player.play()
            PlaybackRemoteAction.Play -> player.play()
            PlaybackRemoteAction.Pause -> player.pause()
        }
    }
    LaunchedEffect(player, item.id) {
        val targetVolume = if (item.muted) 0f else (item.volumePercent / 100f).coerceIn(0f, 1.5f)
        while (true) {
            val position = player.currentPosition.coerceAtLeast(0)
            val duration = player.duration
            val fadeIn = if (item.fadeInMs > 0) (position.toFloat() / item.fadeInMs).coerceIn(0f, 1f) else 1f
            val fadeOut = if (item.fadeOutMs > 0 && duration != C.TIME_UNSET)
                ((duration - position).toFloat() / item.fadeOutMs).coerceIn(0f, 1f) else 1f
            val fade = minOf(fadeIn, fadeOut)
            player.volume = targetVolume * fade
            visualOpacity = minOf(fade, cueTransitionOpacity(item, position, duration.takeUnless { it == C.TIME_UNSET } ?: item.durationMs ?: 0))
            val state = when {
                player.playerError != null -> "error"
                player.playbackState == Player.STATE_BUFFERING -> "buffering"
                player.playbackState == Player.STATE_ENDED -> "completed"
                player.isPlaying -> "playing"
                player.playbackState == Player.STATE_READY -> "paused"
                else -> "loading"
            }
            playerState = state
            playerPosition = position
            playerDuration = duration.takeUnless { it == C.TIME_UNSET }
            onTelemetry(PlaybackTelemetry(state, playlist.id, item.id, position,
                duration.takeUnless { it == C.TIME_UNSET }, item.volumePercent,
                player.playerError?.message ?: fallbackDiagnostic))
            kotlinx.coroutines.delay(500)
        }
    }
    LaunchedEffect(player, item.id) {
        var bufferingSince: Long? = null
        while (true) {
            if (player.playWhenReady && player.playbackState == Player.STATE_BUFFERING) {
                val now = android.os.SystemClock.elapsedRealtime()
                val startedAt = bufferingSince
                if (startedAt == null) bufferingSince = now
                else if (PlaybackFallbackPolicy.bufferingTimedOut(startedAt, now)) {
                    if (tryNextPlaybackSource("buffering for ${now - startedAt}ms"))
                        bufferingSince = android.os.SystemClock.elapsedRealtime()
                    else bufferingSince = null
                }
            } else bufferingSince = null
            kotlinx.coroutines.delay(1_000)
        }
    }
    LaunchedEffect(control?.version) {
        when (control?.action) {
            "pause" -> { player.pause(); revealOverlay() }
            "resume" -> { player.play(); revealOverlay() }
        }
    }
    DisposableEffect(player) {
        val listener = object : Player.Listener {
            override fun onPlayerError(error: PlaybackException) {
                if (!tryNextPlaybackSource(error.message ?: "playback error")) {
                    playerState = "error"
                    onTelemetry(PlaybackTelemetry("error", playlist.id, item.id,
                        player.currentPosition.coerceAtLeast(0),
                        player.duration.takeUnless { it == C.TIME_UNSET }, item.volumePercent,
                        error.message ?: "Playback failed for all available quality levels."))
                }
            }

            override fun onPlaybackStateChanged(state: Int) {
                if (state == Player.STATE_ENDED) {
                    repeatCompleted += 1
                    when (item.endBehavior) {
                        "loop" -> { player.seekTo(0); player.play() }
                        else -> if (repeatCompleted < item.repeatCount.coerceIn(1, 99)) { player.seekTo(0); player.play() }
                        else when (item.endBehavior) {
                        "advance" -> if (index + 1 < items.size) onNext(index + 1) else onFinished()
                        "playlistLoop" -> onNext(0)
                        "pause" -> if (index == items.lastIndex && items == playlist.items && playlist.postLesson?.items?.isNotEmpty() == true) onFinished() else player.pause()
                        else -> onFinished()
                        }
                    }
                }
            }
        }
        player.addListener(listener)
        onDispose { player.removeListener(listener); player.release() }
    }
    Box(Modifier.fillMaxSize().background(cueBackground(item)).then(remoteModifier)) {
        key(item.id) {
            AndroidView(factory = { PlayerView(it).apply {
                    this.player = player
                    useController = false
                    isFocusable = false
                    isFocusableInTouchMode = false
                    resizeMode = if (item.fitMode == "fill") AspectRatioFrameLayout.RESIZE_MODE_ZOOM else AspectRatioFrameLayout.RESIZE_MODE_FIT
                } },
                update = { view ->
                    view.player = player
                    view.resizeMode = if (item.fitMode == "fill") AspectRatioFrameLayout.RESIZE_MODE_ZOOM else AspectRatioFrameLayout.RESIZE_MODE_FIT
                },
                onRelease = { it.player = null },
                modifier = Modifier.fillMaxSize().onSizeChanged { visualSize = it }.cueVisual(item, visualOpacity, visualSize))
        }
        PlaybackOverlay(
            visible = shouldShowPlaybackOverlay(
                lastOverlayInteraction,
                overlayClock,
                playing = playerState == "playing",
                hasError = playerState == "error"
            ),
            lessonTitle = playlist.title,
            item = item,
            itemIndex = index,
            itemCount = items.size,
            positionMs = playerPosition,
            durationMs = playerDuration,
            playing = playerState == "playing",
            availabilityLabel = if (cacheSource.usingCache && activeSourceIndex < 0) "OFFLINE COPY"
                else streamingSources.getOrNull(activeSourceIndex)?.let { "STREAMING ${it.displayQuality()}" }
                    ?: "SERVER MEDIA",
            actions = PlaybackOverlayActions(
                previous = { revealOverlay(); if (index > 0) onNext(index - 1) },
                rewind = { revealOverlay(); player.seekTo((player.currentPosition - REMOTE_SEEK_STEP_MS).coerceAtLeast(0)) },
                togglePlayPause = { revealOverlay(); if (player.playWhenReady) player.pause() else player.play() },
                fastForward = {
                    revealOverlay()
                    val maximum = player.duration.takeIf { it != C.TIME_UNSET && it >= 0 } ?: Long.MAX_VALUE
                    player.seekTo((player.currentPosition + REMOTE_SEEK_STEP_MS).coerceAtMost(maximum))
                },
                next = { revealOverlay(); if (index + 1 < items.size) onNext(index + 1) },
                exit = onExit
            ),
            modifier = Modifier.fillMaxSize()
        )
    }
}

@Composable
private fun UnavailableMediaScreen(
    title: String,
    message: String,
    hasPrevious: Boolean,
    hasNext: Boolean,
    onPrevious: () -> Unit,
    onNext: () -> Unit,
    onExit: () -> Unit
) {
    val initialFocus = remember(title, hasNext) { FocusRequester() }
    LaunchedEffect(title, hasNext) { runCatching { initialFocus.requestFocus() } }
    FormLayout(title, message) {
        StatusBadge("MEDIA UNAVAILABLE", Coral)
        Spacer(Modifier.height(26.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
            if (hasPrevious) LessonCueButton(onClick = onPrevious) { Text("Previous") }
            if (hasNext) LessonCueButton(onClick = onNext, modifier = Modifier.focusRequester(initialFocus)) { Text("Next") }
            LessonCueButton(
                onClick = onExit,
                modifier = if (!hasNext) Modifier.focusRequester(initialFocus) else Modifier
            ) { Text("Back to lesson") }
        }
    }
}

@SuppressLint("SetJavaScriptEnabled")
@Composable
private fun OnlineMediaScreen(playlist: LessonPlaylist, items: List<CueItem>, index: Int, item: CueItem,
    control: ControlCommand?, identity: DeviceIdentity?, onTelemetry: (PlaybackTelemetry) -> Unit, onExit: () -> Unit,
    onNext: (Int) -> Unit) {
    val context = LocalContext.current
    var locallyPaused by remember(item.id) { mutableStateOf(false) }
    var visualSize by remember(item.id) { mutableStateOf(IntSize.Zero) }
    var pageLoaded by remember(item.id) { mutableStateOf(false) }
    var webError by remember(item.id) { mutableStateOf<String?>(null) }
    var lastOverlayInteraction by remember(item.id) { mutableLongStateOf(System.currentTimeMillis()) }
    var overlayClock by remember(item.id) { mutableLongStateOf(System.currentTimeMillis()) }
    fun revealOverlay() {
        val now = System.currentTimeMillis()
        lastOverlayInteraction = now
        overlayClock = now
    }
    LaunchedEffect(item.id) {
        while (true) {
            overlayClock = System.currentTimeMillis()
            kotlinx.coroutines.delay(250)
        }
    }
    val webView = remember(item.id) {
        WebView(context).apply {
            settings.javaScriptEnabled = item.linkKind != "webpage"
            settings.domStorageEnabled = true
            settings.mediaPlaybackRequiresUserGesture = false
            settings.setSupportZoom(false)
            settings.builtInZoomControls = false
            settings.displayZoomControls = false
            settings.useWideViewPort = true
            settings.loadWithOverviewMode = false
            settings.textZoom = 100
            settings.cacheMode = WebSettings.LOAD_DEFAULT
            settings.allowFileAccess = false
            settings.allowContentAccess = false
            settings.javaScriptCanOpenWindowsAutomatically = false
            setLayerType(View.LAYER_TYPE_HARDWARE, null)
            webViewClient = object : WebViewClient() {
                override fun onPageStarted(view: WebView?, url: String?, favicon: Bitmap?) {
                    pageLoaded = false
                    webError = null
                }

                override fun onPageFinished(view: WebView?, url: String?) {
                    pageLoaded = true
                }

                override fun onReceivedError(view: WebView?, request: WebResourceRequest?, error: WebResourceError?) {
                    if (request?.isForMainFrame == true) {
                        webError = error?.description?.toString()?.takeIf(String::isNotBlank)
                            ?: "The LessonCue Activity display could not be loaded."
                    }
                }

                override fun onReceivedHttpError(view: WebView?, request: WebResourceRequest?, response: WebResourceResponse?) {
                    if (request?.isForMainFrame == true && (response?.statusCode ?: 0) >= 400) {
                        webError = "LessonCue returned display error ${response?.statusCode}."
                    }
                }
            }
            webChromeClient = object : WebChromeClient() {
                override fun onConsoleMessage(message: ConsoleMessage): Boolean {
                    val priority = if (message.messageLevel() == ConsoleMessage.MessageLevel.ERROR) Log.ERROR else Log.DEBUG
                    Log.println(priority, "LessonCueActivity", "${message.message()} (${message.sourceId()}:${message.lineNumber()})")
                    return true
                }
            }
            setBackgroundColor(AndroidColor.TRANSPARENT)
            loadUrl(activityPlaybackUrl(item, identity, BuildConfig.VERSION_NAME)
                ?: item.playbackUrl
                ?: item.streamingFallbackUrl!!)
        }
    }
    val remoteModifier = playbackRemoteModifier(item.id) { action ->
        revealOverlay()
        when (action) {
            PlaybackRemoteAction.Previous -> onNext((index - 1).coerceAtLeast(0))
            PlaybackRemoteAction.Next -> if (index + 1 < items.size) onNext(index + 1)
            PlaybackRemoteAction.Rewind -> webView.evaluateMediaScript(
                "if(v){v.currentTime=Math.max(0,v.currentTime-${REMOTE_SEEK_STEP_MS / 1_000.0});}"
            )
            PlaybackRemoteAction.FastForward -> webView.evaluateMediaScript(
                "if(v){v.currentTime=Math.min(v.duration||Infinity,v.currentTime+${REMOTE_SEEK_STEP_MS / 1_000.0});}"
            )
            PlaybackRemoteAction.TogglePlayPause -> {
                if (item.type == "activity") {
                    webError = null
                    pageLoaded = false
                    webView.reload()
                } else {
                    locallyPaused = !locallyPaused
                    webView.evaluateMediaScript("if(v){if(v.paused){v.play();}else{v.pause();}}")
                }
            }
            PlaybackRemoteAction.Play -> {
                locallyPaused = false
                webView.onResume()
                webView.evaluateMediaScript("if(v){v.play();}")
            }
            PlaybackRemoteAction.Pause -> {
                locallyPaused = true
                webView.evaluateMediaScript("if(v){v.pause();}")
            }
        }
    }
    LaunchedEffect(control?.version) {
        if (item.type == "activity") return@LaunchedEffect
        when (control?.action) {
            "pause" -> {
                locallyPaused = true
                revealOverlay()
                webView.evaluateMediaScript("if(v){v.pause();}")
            }
            "resume" -> {
                locallyPaused = false
                revealOverlay()
                webView.onResume()
                webView.evaluateMediaScript("if(v){v.play();}")
            }
        }
    }
    LaunchedEffect(item.id, control?.version, locallyPaused) {
        webView.evaluateMediaScript("if(v){v.playbackRate=${item.playbackRatePercent.coerceIn(25, 400) / 100.0};v.muted=${item.muted};v.volume=${(item.volumePercent / 100.0).coerceIn(0.0, 1.0)};}")
        onTelemetry(PlaybackTelemetry(if (locallyPaused) "paused" else "playing",
            playlist.id, item.id, volumePercent = item.volumePercent))
    }
    DisposableEffect(webView) { onDispose { webView.stopLoading(); webView.destroy() } }
    Box(Modifier.fillMaxSize().background(cueBackground(item)).then(remoteModifier)) {
        // AndroidView reuses its native child across recompositions unless its
        // composition identity changes. A lesson cue change also creates a new
        // WebView above, so key this host to the cue; otherwise Compose keeps
        // the destroyed previous WebView attached and every later Activity is
        // rendered as a black frame.
        key(item.id) {
            AndroidView(factory = {
                webView.apply {
                    isFocusable = false
                    isFocusableInTouchMode = false
                }
            }, modifier = Modifier.fillMaxSize().onSizeChanged { visualSize = it }.cueVisual(item, 1f, visualSize))
        }
        if (!pageLoaded && webError == null) {
            Box(
                Modifier.align(Alignment.Center).background(LessonCueTvColors.Background.copy(alpha = .82f), RoundedCornerShape(16.dp))
                    .padding(horizontal = 28.dp, vertical = 18.dp)
            ) {
                Text("Loading Activity stage…", color = Cream, fontSize = 20.sp, fontWeight = FontWeight.Bold)
            }
        }
        webError?.let { message ->
            Column(
                Modifier.align(Alignment.Center).width(620.dp)
                    .background(LessonCueTvColors.Background.copy(alpha = .94f), RoundedCornerShape(20.dp))
                    .border(1.dp, Coral.copy(alpha = .75f), RoundedCornerShape(20.dp))
                    .padding(horizontal = 34.dp, vertical = 28.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Text("ACTIVITY DISPLAY DISCONNECTED", color = Coral, fontSize = 15.sp, fontWeight = FontWeight.Bold)
                Spacer(Modifier.height(10.dp))
                Text(message, color = Cream, fontSize = 25.sp, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center)
                Spacer(Modifier.height(8.dp))
                Text("Press Play to retry, or use Previous / Next to continue.", color = Muted, fontSize = 16.sp,
                    textAlign = TextAlign.Center)
            }
        }
        val activityPlaying = item.type == "activity" || !locallyPaused
        PlaybackOverlay(
            visible = shouldShowPlaybackOverlay(lastOverlayInteraction, overlayClock, playing = activityPlaying, hasError = webError != null),
            lessonTitle = playlist.title,
            item = item,
            itemIndex = index,
            itemCount = items.size,
            positionMs = 0,
            durationMs = item.effectiveDurationMs(),
            playing = activityPlaying,
            availabilityLabel = when {
                webError != null -> "DISPLAY ERROR"
                item.type == "activity" -> "ACTIVITY LIVE"
                else -> "ONLINE MEDIA"
            },
            actions = PlaybackOverlayActions(
                previous = { revealOverlay(); if (index > 0) onNext(index - 1) },
                rewind = { revealOverlay(); webView.evaluateMediaScript("if(v){v.currentTime=Math.max(0,v.currentTime-${REMOTE_SEEK_STEP_MS / 1_000.0});}") },
                togglePlayPause = {
                    revealOverlay()
                    if (item.type == "activity") {
                        webError = null
                        pageLoaded = false
                        webView.reload()
                    } else {
                        locallyPaused = !locallyPaused
                        webView.evaluateMediaScript("if(v){if(v.paused){v.play();}else{v.pause();}}")
                    }
                },
                fastForward = { revealOverlay(); webView.evaluateMediaScript("if(v){v.currentTime=Math.min(v.duration||Infinity,v.currentTime+${REMOTE_SEEK_STEP_MS / 1_000.0});}") },
                next = { revealOverlay(); if (index + 1 < items.size) onNext(index + 1) },
                exit = onExit
            ),
            hasError = webError != null,
            modifier = Modifier.fillMaxSize()
        )
    }
}

private fun WebView.evaluateMediaScript(body: String) {
    evaluateJavascript(
        "(function(){var v=document.querySelector('video');$body})()",
        null
    )
}

private fun loopingPreRoll(items: List<CueItem>) = items.mapIndexed { index, item ->
    item.copy(endBehavior = if (index == items.lastIndex) "playlistLoop" else "advance")
}

private const val REMOTE_SEEK_STEP_MS = 5_000L

private fun ScreenManifest.itemCount() = allItems().size

@Composable
private fun FormLayout(title: String, subtitle: String, content: @Composable ColumnScope.() -> Unit) {
    BoxWithConstraints(Modifier.fillMaxSize().background(LessonCueTvColors.Background)) {
        val compact = maxWidth < 900.dp || maxHeight < 600.dp
        val outerHorizontal = if (compact) 20.dp else 82.dp
        val outerVertical = if (compact) 16.dp else 54.dp
        val panelHorizontal = if (compact) 22.dp else 48.dp
        val panelVertical = if (compact) 20.dp else 38.dp
        Column(
            Modifier.fillMaxSize().verticalScroll(rememberScrollState())
                .padding(horizontal = outerHorizontal, vertical = outerVertical)
        ) {
            LessonCueWordmark(compact = compact)
            Spacer(Modifier.height(if (compact) 14.dp else 30.dp))
            val panelModifier = if (compact) Modifier.fillMaxWidth() else Modifier.fillMaxWidth().weight(1f)
            TvPanel(panelModifier) {
                Column(
                    (if (compact) Modifier.fillMaxWidth() else Modifier.fillMaxSize())
                        .padding(horizontal = panelHorizontal, vertical = panelVertical)
                ) {
                    Text(title, fontSize = if (compact) 28.sp else 40.sp, color = Cream,
                        fontWeight = FontWeight.Bold, maxLines = 2,
                        overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                    Text(subtitle, fontSize = if (compact) 15.sp else 19.sp, color = Muted,
                        maxLines = if (compact) 4 else 2,
                        overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
                        modifier = Modifier.padding(top = 9.dp, bottom = if (compact) 20.dp else 30.dp))
                    content()
                }
            }
        }
    }
}

@Composable
internal fun LessonCueButton(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    content: @Composable RowScope.() -> Unit
) {
    Button(
        onClick = onClick,
        modifier = modifier,
        enabled = enabled,
        colors = ButtonDefaults.colors(
            containerColor = ButtonSurface,
            contentColor = Cream,
            focusedContainerColor = SelectedButton,
            focusedContentColor = Navy,
            pressedContainerColor = SelectedButton,
            pressedContentColor = Navy,
            disabledContainerColor = DisabledButtonSurface,
            disabledContentColor = DisabledButtonText
        ),
        content = content
    )
}

@Composable
private fun ManualDownloadOverlay(
    state: ManualDownloadState,
    onDownload: () -> Unit,
    onDismiss: () -> Unit,
) {
    val actionFocus = remember(state.playlist.id, state.started, state.completed) { FocusRequester() }
    LaunchedEffect(state.playlist.id, state.started, state.completed) {
        kotlinx.coroutines.delay(80)
        runCatching { actionFocus.requestFocus() }
    }
    val total = state.items.size
    val cached = state.cachedCount.coerceIn(0, total)
    val remaining = (total - cached).coerceAtLeast(0)
    Box(
        Modifier.fillMaxSize()
            .background(Color.Black.copy(alpha = .78f))
            .zIndex(10_000f),
        contentAlignment = Alignment.Center
    ) {
        BoxWithConstraints(Modifier.fillMaxWidth(.9f)) {
            val compact = maxWidth < 760.dp
            TvPanel(Modifier.fillMaxWidth()) {
                Column(
                    Modifier.fillMaxWidth().padding(
                        horizontal = if (compact) 22.dp else 38.dp,
                        vertical = if (compact) 20.dp else 30.dp
                    ),
                    verticalArrangement = Arrangement.spacedBy(if (compact) 10.dp else 14.dp)
                ) {
                    Text("Manually Download Lesson", color = Cream,
                        fontSize = if (compact) 25.sp else 34.sp, fontWeight = FontWeight.Bold,
                        maxLines = 2, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                    Text(state.playlist.title, color = Gold,
                        fontSize = if (compact) 18.sp else 22.sp, fontWeight = FontWeight.Bold,
                        maxLines = 2, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                    when {
                        state.error != null -> Text(state.error, color = Coral,
                            fontSize = if (compact) 15.sp else 18.sp)
                        state.completed -> Text("This lesson is downloaded and ready for local playback.", color = Mint,
                            fontSize = if (compact) 15.sp else 18.sp)
                        state.started -> Text("Downloading $cached of $total media files ($remaining remaining). Downloads are prioritized and continue in the background.",
                            color = Cream, fontSize = if (compact) 15.sp else 18.sp)
                        total == 0 -> Text("This lesson has no downloadable media. Streaming-only content will use the server when played.",
                            color = Muted, fontSize = if (compact) 15.sp else 18.sp)
                        else -> Text("Download all $total media files, including pre-roll, countdown, lesson, and post-roll, for local playback.",
                            color = Cream, fontSize = if (compact) 15.sp else 18.sp)
                    }
                    if (state.started && !state.completed && total > 0) {
                        Box(Modifier.fillMaxWidth().height(8.dp)
                            .background(LessonCueTvColors.Border, RoundedCornerShape(50))) {
                            Box(Modifier.fillMaxWidth(cached.toFloat() / total.toFloat()).height(8.dp)
                                .background(Mint, RoundedCornerShape(50)))
                        }
                    }
                    Row(
                        Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(14.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        if (!state.started && !state.completed && total > 0) {
                            LessonCueButton(
                                onClick = onDownload,
                                modifier = Modifier.weight(1f).height(if (compact) 54.dp else 62.dp)
                                    .focusRequester(actionFocus)
                            ) { Text("Download lesson", fontSize = if (compact) 16.sp else 18.sp, fontWeight = FontWeight.Bold) }
                        } else {
                            LessonCueButton(
                                onClick = onDismiss,
                                modifier = Modifier.weight(1f).height(if (compact) 54.dp else 62.dp)
                                    .focusRequester(actionFocus)
                            ) { Text(if (state.completed) "Done" else "Close", fontSize = if (compact) 16.sp else 18.sp, fontWeight = FontWeight.Bold) }
                        }
                        if (!state.completed) {
                            LessonCueButton(
                                onClick = onDismiss,
                                modifier = Modifier.weight(1f).height(if (compact) 54.dp else 62.dp)
                            ) { Text("Cancel", fontSize = if (compact) 16.sp else 18.sp) }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun LoadingScreen(onEnterAddress: (() -> Unit)? = null) {
    var dots by remember { mutableIntStateOf(1) }
    var waitedSeconds by remember { mutableIntStateOf(0) }
    LaunchedEffect(Unit) {
        while (true) {
            kotlinx.coroutines.delay(450)
            dots = dots % 3 + 1
        }
    }
    // A first launch with nothing cached can spend the better part of a minute
    // here: eight seconds to connect, twenty to read, then discovery, then the
    // same again on whatever that finds. A still screen for that long is
    // indistinguishable from a frozen one, so say how long it has been and
    // offer the way out rather than making somebody guess.
    LaunchedEffect(Unit) {
        while (true) {
            kotlinx.coroutines.delay(1_000)
            waitedSeconds += 1
        }
    }
    Column(
        Modifier.fillMaxSize().background(LessonCueTvColors.Background),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        LessonCueWordmark()
        Spacer(Modifier.height(34.dp))
        Text("Connecting to LessonCue", fontSize = 32.sp, color = Cream, fontWeight = FontWeight.Bold)
        Text(
            if (waitedSeconds >= 6) "Still searching for the local server (${waitedSeconds}s)${".".repeat(dots)}"
            else "Searching for the local server${".".repeat(dots)}",
            fontSize = 18.sp, color = Muted, modifier = Modifier.padding(top = 10.dp)
        )
        Spacer(Modifier.height(26.dp))
        Box(Modifier.width(240.dp).height(5.dp).background(Slate, RoundedCornerShape(50))) {
            Box(Modifier.fillMaxWidth(dots / 3f).height(5.dp).background(Gold, RoundedCornerShape(50)))
        }
        if (onEnterAddress != null && waitedSeconds >= 6) {
            Spacer(Modifier.height(26.dp))
            LessonCueButton(onClick = onEnterAddress, modifier = Modifier.width(300.dp).height(56.dp)) {
                Text("Enter the server address")
            }
        }
    }
}

internal fun lessonMediaItems(playlist: LessonPlaylist): List<CueItem> =
    playlist.timeline().map(TimelineCue::item).distinctBy { it.id }

internal fun cacheableLessonItems(playlist: LessonPlaylist): List<CueItem> =
    lessonMediaItems(playlist).filter {
        it.offlineEligible && !isBundledDemoMedia(it.url) && !it.url.isNullOrBlank()
    }

private fun mediaCacheWorkName(item: CueItem): String =
    "lessoncue-media-${item.id}-${item.sha256?.take(12) ?: "current"}"

private fun mediaCacheInputData(identity: DeviceIdentity, item: CueItem, priority: String) = workDataOf(
    "url" to item.url,
    "fileName" to item.cacheFileName(),
    "token" to identity.token,
    "serverHost" to java.net.URL(identity.serverUrl).host,
    "sha256" to item.sha256,
    "sizeBytes" to (item.sizeBytes ?: 0L),
    "priority" to priority,
)

private fun manualDownloadState(
    context: android.content.Context,
    identity: DeviceIdentity,
    playlist: LessonPlaylist,
): ManualDownloadState {
    val items = cacheableLessonItems(playlist)
    val directory = context.filesDir.resolve("media")
    return ManualDownloadState(
        identity = identity,
        playlist = playlist,
        items = items,
        cachedCount = items.count { findUsableCacheFile(directory, it) != null },
        completed = items.isEmpty(),
    )
}

/** Enqueue the selected lesson ahead of background cache work. */
private fun enqueuePriorityLessonDownload(
    context: android.content.Context,
    identity: DeviceIdentity,
    playlist: LessonPlaylist,
): List<CueItem> {
    val manager = WorkManager.getInstance(context)
    val constraints = Constraints.Builder()
        .setRequiredNetworkType(NetworkType.CONNECTED)
        .build()
    val items = cacheableLessonItems(playlist)
    items.forEach { item ->
        prepareCacheForDownload(context.filesDir.resolve("media"), item)
        val workName = mediaCacheWorkName(item)
        // Replace the normal background request with expedited work so this
        // explicit user action gets the next available worker slot. The
        // download itself is resumable, so replacing an existing request does
        // not throw away an interrupted .part file.
        manager.cancelUniqueWork(workName)
        val request = OneTimeWorkRequestBuilder<MediaCacheWorker>()
            .setInputData(mediaCacheInputData(identity, item, priority = "manual"))
            .setConstraints(constraints)
            .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
            .build()
        manager.enqueueUniqueWork(workName, ExistingWorkPolicy.REPLACE, request)
    }
    return items
}

private fun scheduleMediaCaches(context: android.content.Context, identity: DeviceIdentity, manifest: ScreenManifest) {
    val manager = WorkManager.getInstance(context)
    val lessonMedia = manifest.playlists.flatMap { playlist ->
        playlist.items + playlist.preRoll?.items.orEmpty() + listOfNotNull(playlist.countdown?.item) + playlist.postLesson?.items.orEmpty()
    }
    val signageMedia = manifest.signageSchedule.flatMap { sign ->
        listOfNotNull(sign.media, sign.backgroundAudio) +
            sign.zones.flatMap { it.mediaItems() } +
            sign.contentPlaylist?.items.orEmpty().flatMap { it.mediaItems() }
    }
    val items = (lessonMedia + signageMedia)
        .distinctBy { it.id }.filter { it.offlineEligible && it.url != null }
    val constraints = Constraints.Builder()
        .setRequiredNetworkType(NetworkType.CONNECTED)
        .build()
    items.forEach { item ->
        prepareCacheForDownload(context.filesDir.resolve("media"), item)
        val request = OneTimeWorkRequestBuilder<MediaCacheWorker>()
            .setInputData(mediaCacheInputData(identity, item, priority = "background"))
            .setConstraints(constraints)
            .build()
        manager.enqueueUniqueWork(mediaCacheWorkName(item),
            ExistingWorkPolicy.KEEP, request)
    }
}

private fun SignagePlaylistEntry.mediaItems(depth: Int = 0): List<CueItem> {
    if (depth > 4) return listOfNotNull(media, layout?.backgroundAudio)
    return buildList {
        media?.let(::add)
        layout?.backgroundAudio?.let(::add)
        layout?.zones.orEmpty().forEach { addAll(it.mediaItems(depth + 1)) }
    }
}

private fun SignageZone.mediaItems(depth: Int = 0): List<CueItem> {
    if (depth > 4) return listOfNotNull(media)
    return buildList {
        media?.let(::add)
        contentPlaylist?.items.orEmpty().forEach { addAll(it.mediaItems(depth + 1)) }
    }
}

private fun cueOpacity(item: CueItem, positionMs: Long, durationMs: Long): Float {
    val fadeIn = if (item.fadeInMs > 0) (positionMs.toFloat() / item.fadeInMs).coerceIn(0f, 1f) else 1f
    val fadeOut = if (item.fadeOutMs > 0) ((durationMs - positionMs).toFloat() / item.fadeOutMs).coerceIn(0f, 1f) else 1f
    return minOf(fadeIn, fadeOut, cueTransitionOpacity(item, positionMs, durationMs))
}

private fun cueTransitionOpacity(item: CueItem, positionMs: Long, durationMs: Long): Float {
    if (item.transitionStyle != "fade-black" || item.transitionDurationMs <= 0 || durationMs <= 0) return 1f
    val fadeIn = positionMs.toFloat() / item.transitionDurationMs
    val fadeOut = (durationMs - positionMs).toFloat() / item.transitionDurationMs
    return minOf(fadeIn, fadeOut, 1f).coerceIn(0f, 1f)
}

private fun cueBackground(item: CueItem): Color {
    if (item.fitMode == "letterbox") return Color.Black
    return runCatching { Color(android.graphics.Color.parseColor(item.backgroundColor)) }.getOrDefault(Color.Black)
}

private fun Modifier.cueVisual(item: CueItem, opacity: Float, size: IntSize): Modifier {
    val horizontal = (item.cropLeftPercent + item.cropRightPercent).coerceIn(0, 89)
    val vertical = (item.cropTopPercent + item.cropBottomPercent).coerceIn(0, 89)
    val scaleX = 100f / (100 - horizontal)
    val scaleY = 100f / (100 - vertical)
    return clipToBounds().graphicsLayer(
        alpha = opacity,
        rotationZ = item.rotationDegrees.toFloat(),
        scaleX = scaleX,
        scaleY = scaleY,
        translationX = (item.cropRightPercent - item.cropLeftPercent) / 200f * size.width * scaleX,
        translationY = (item.cropBottomPercent - item.cropTopPercent) / 200f * size.height * scaleY
    )
}

private const val MAX_DEVICE_NAME_LENGTH = 100

private fun defaultDeviceName(): String = (Build.MODEL.takeIf { it.isNotBlank() } ?: "LessonCue TV")
    .take(MAX_DEVICE_NAME_LENGTH)
