package org.lessoncue.tv

import android.view.KeyEvent
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Button
import androidx.tv.material3.ButtonDefaults
import androidx.tv.material3.Text
import coil.compose.AsyncImage
import kotlinx.coroutines.delay
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import androidx.compose.runtime.rememberCoroutineScope
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter

internal enum class ConnectionMode(val label: String) {
    Online("ONLINE"),
    Cached("USING CACHED SCHEDULE"),
    Offline("OFFLINE")
}

@Composable
internal fun LessonCueWordmark(modifier: Modifier = Modifier, compact: Boolean = false) {
    Row(modifier, verticalAlignment = Alignment.CenterVertically) {
        Image(
            painter = painterResource(R.drawable.ic_launcher_foreground),
            contentDescription = "LessonCue",
            modifier = Modifier.size(if (compact) 36.dp else 44.dp)
        )
        Spacer(Modifier.width(if (compact) 10.dp else 14.dp))
        Text(
            "LessonCue",
            color = LessonCueTvColors.FocusOrange,
            fontSize = if (compact) 22.sp else 29.sp,
            fontWeight = FontWeight.Bold
        )
    }
}

@Composable
internal fun TvHeader(
    screenName: String,
    connectionMode: ConnectionMode,
    onCheckForUpdates: (() -> Unit)? = null,
    modifier: Modifier = Modifier
) {
    var now by remember { mutableStateOf(ZonedDateTime.now()) }
    LaunchedEffect(Unit) {
        while (true) {
            now = ZonedDateTime.now()
            delay(30_000)
        }
    }
    Column(modifier.fillMaxWidth()) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                LessonCueWordmark()
                Text(
                    screenName.uppercase(),
                    color = LessonCueTvColors.Muted,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.SemiBold,
                    letterSpacing = 1.sp
                )
            }
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(20.dp)
            ) {
                Box(Modifier.size(14.dp).clip(RoundedCornerShape(50)).background(
                    if (connectionMode == ConnectionMode.Online) LessonCueTvColors.Success
                    else LessonCueTvColors.Coral
                ))
                Spacer(Modifier.width(10.dp))
                Text(
                    connectionMode.label,
                    color = if (connectionMode == ConnectionMode.Online) LessonCueTvColors.Success
                    else LessonCueTvColors.Coral,
                    fontSize = 16.sp,
                    fontWeight = FontWeight.Bold,
                    letterSpacing = 1.sp
                )
                Column(horizontalAlignment = Alignment.End) {
                    Text(
                        now.format(DateTimeFormatter.ofPattern("h:mm a")),
                        color = LessonCueTvColors.Cream,
                        fontSize = 28.sp,
                        fontWeight = FontWeight.Bold
                    )
                    Text(
                        now.format(DateTimeFormatter.ofPattern("EEEE, MMMM d")).uppercase(),
                        color = LessonCueTvColors.Muted,
                        fontSize = 14.sp,
                        fontWeight = FontWeight.SemiBold
                    )
                }
                onCheckForUpdates?.let { checkForUpdates ->
                    LessonCueButton(
                        onClick = checkForUpdates,
                        modifier = Modifier.height(54.dp)
                    ) {
                        Text("⇩  Updates", fontSize = 17.sp, fontWeight = FontWeight.Bold)
                    }
                }
            }
        }
        Spacer(Modifier.height(6.dp))
        Box(Modifier.fillMaxWidth().height(1.dp).background(LessonCueTvColors.Border))
    }
}

@Composable
internal fun TvPanel(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    val shape = RoundedCornerShape(LessonCueTvDimens.PanelRadius)
    Box(
        modifier.clip(shape)
            .background(LessonCueTvColors.Panel.copy(alpha = .96f))
            .border(LessonCueTvDimens.PanelBorder, LessonCueTvColors.Border, shape)
    ) { content() }
}

@Composable
internal fun FocusedTvCard(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    onFocused: (() -> Unit)? = null,
    initialFocusRequester: FocusRequester? = null,
    onLongClick: (() -> Unit)? = null,
    selected: Boolean = false,
    content: @Composable RowScope.() -> Unit
) {
    var focused by remember { mutableStateOf(false) }
    var longPressTriggered by remember { mutableStateOf(false) }
    var longPressJob by remember { mutableStateOf<Job?>(null) }
    val longPressScope = rememberCoroutineScope()
    val scale by animateFloatAsState(if (focused) 1.025f else 1f, label = "tv-card-focus")
    val shape = RoundedCornerShape(LessonCueTvDimens.CardRadius)
    Button(
        onClick = onClick,
        colors = ButtonDefaults.colors(
            containerColor = LessonCueTvColors.ElevatedPanel,
            contentColor = LessonCueTvColors.Cream,
            focusedContainerColor = LessonCueTvColors.ElevatedPanel,
            focusedContentColor = LessonCueTvColors.Cream,
            pressedContainerColor = LessonCueTvColors.FocusOrange,
            pressedContentColor = LessonCueTvColors.Background
        ),
        shape = ButtonDefaults.shape(shape, shape, shape, shape, shape),
        scale = ButtonDefaults.scale(1f, 1f, 1f, 1f, 1f),
        contentPadding = PaddingValues(0.dp),
        modifier = modifier
            .graphicsLayer { scaleX = scale; scaleY = scale }
            .clip(shape)
            .border(
                if (focused) LessonCueTvDimens.FocusBorder else if (selected) 2.dp else LessonCueTvDimens.PanelBorder,
                if (focused || selected) LessonCueTvColors.FocusOrange else LessonCueTvColors.Border,
                shape
            )
            .onFocusChanged {
                focused = it.isFocused
                if (it.isFocused) onFocused?.invoke()
            }
            .then(if (onLongClick == null) Modifier else Modifier.onPreviewKeyEvent { event ->
                val native = event.nativeKeyEvent
                if (native.keyCode !in setOf(
                        KeyEvent.KEYCODE_DPAD_CENTER,
                        KeyEvent.KEYCODE_ENTER,
                        KeyEvent.KEYCODE_NUMPAD_ENTER
                    )) return@onPreviewKeyEvent false
                when (native.action) {
                    KeyEvent.ACTION_DOWN -> {
                        if (native.repeatCount > 0) {
                            if (!longPressTriggered) {
                                longPressTriggered = true
                                longPressJob?.cancel()
                                onLongClick()
                            }
                            true
                        } else {
                            longPressTriggered = false
                            longPressJob?.cancel()
                            longPressJob = longPressScope.launch {
                                delay(650)
                                if (!longPressTriggered) {
                                    longPressTriggered = true
                                    onLongClick()
                                }
                            }
                            false
                        }
                    }
                    KeyEvent.ACTION_UP -> {
                        longPressJob?.cancel()
                        val consumed = longPressTriggered
                        longPressTriggered = false
                        consumed
                    }
                    else -> false
                }
            })
            .then(initialFocusRequester?.let { Modifier.focusRequester(it) } ?: Modifier)
    ) { content() }
}

@Composable
internal fun StatusBadge(
    label: String,
    color: Color = LessonCueTvColors.Success,
    modifier: Modifier = Modifier
) {
    Text(
        label,
        color = color,
        fontSize = 13.sp,
        fontWeight = FontWeight.Bold,
        letterSpacing = .8.sp,
        maxLines = 1,
        modifier = modifier
            .clip(RoundedCornerShape(50))
            .background(color.copy(alpha = .13f))
            .padding(horizontal = 14.dp, vertical = 7.dp)
    )
}

@Composable
internal fun CuePreview(
    item: CueItem?,
    modifier: Modifier = Modifier,
    selectedLabel: String? = null,
    showPlayGlyph: Boolean = true
) {
    val shape = RoundedCornerShape(18.dp)
    Box(
        modifier.clip(shape).background(LessonCueTvColors.ElevatedPanel)
            .semantics { contentDescription = item?.let { "Preview for ${it.title}" } ?: "LessonCue media preview" },
        contentAlignment = Alignment.Center
    ) {
        val source = item?.thumbnailUrl ?: item?.takeIf {
            it.type.equals("image", true) || it.contentType?.startsWith("image/") == true
        }?.url
        if (source != null) {
            AsyncImage(
                model = source,
                contentDescription = item?.title,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize()
            )
        } else {
            BrandedMediaPlaceholder(Modifier.fillMaxSize())
        }
        selectedLabel?.let {
            Text(
                it,
                color = LessonCueTvColors.Cream,
                fontSize = 13.sp,
                fontWeight = FontWeight.Bold,
                modifier = Modifier.align(Alignment.TopStart).padding(16.dp)
                    .clip(RoundedCornerShape(50)).background(LessonCueTvColors.Background.copy(alpha = .94f))
                    .padding(horizontal = 13.dp, vertical = 7.dp)
            )
        }
        if (showPlayGlyph) {
            Box(
                Modifier.size(66.dp).clip(RoundedCornerShape(50))
                    .background(Color.Black.copy(alpha = .9f)),
                contentAlignment = Alignment.Center
            ) {
                Text("▶", color = LessonCueTvColors.Cream, fontSize = 30.sp)
            }
        }
    }
}

@Composable
internal fun CueThumbnail(
    item: CueItem,
    modifier: Modifier = Modifier
) {
    val shape = RoundedCornerShape(12.dp)
    Box(
        modifier
            .clip(shape)
            .background(LessonCueTvColors.ElevatedPanel)
            .semantics { contentDescription = "Thumbnail for ${item.title}" },
        contentAlignment = Alignment.Center
    ) {
        val source = item.thumbnailUrl ?: item.takeIf {
            it.type.equals("image", true) || it.contentType?.startsWith("image/") == true
        }?.url
        if (source != null) {
            AsyncImage(
                model = source,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize()
            )
        } else {
            Box(
                Modifier.fillMaxSize().background(
                    if (item.type.equals("activity", true)) LessonCueTvColors.Orange.copy(alpha = .82f)
                    else LessonCueTvColors.Border.copy(alpha = .72f)
                )
            )
            Text(
                when {
                    item.type.equals("activity", true) -> "GAME"
                    item.type.equals("video", true) -> "VIDEO"
                    else -> item.type.uppercase().take(8)
                },
                color = LessonCueTvColors.Cream,
                fontSize = 13.sp,
                fontWeight = FontWeight.Bold,
                letterSpacing = 1.sp
            )
        }
        Box(
            Modifier.fillMaxWidth().align(Alignment.BottomCenter)
                .background(Color.Black.copy(alpha = .62f))
                .padding(horizontal = 8.dp, vertical = 5.dp)
        ) {
            Text(
                item.type.replaceFirstChar { it.uppercase() },
                color = LessonCueTvColors.Cream,
                fontSize = 11.sp,
                fontWeight = FontWeight.Bold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
    }
}

@Composable
private fun BrandedMediaPlaceholder(modifier: Modifier = Modifier) {
    Box(modifier.background(LessonCueTvColors.ElevatedPanel)) {
        Box(
            Modifier.size(160.dp).align(Alignment.Center).clip(RoundedCornerShape(50))
                .background(LessonCueTvColors.Orange.copy(alpha = .9f))
        )
        Box(
            Modifier.size(190.dp).align(Alignment.BottomStart)
                .graphicsLayer { rotationZ = 45f }
                .background(Color(0xFF55776A))
        )
        Image(
            painter = painterResource(R.drawable.ic_launcher_foreground),
            contentDescription = null,
            modifier = Modifier.size(86.dp).align(Alignment.Center)
        )
    }
}

@Composable
internal fun TvTextField(
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    singleLine: Boolean = true,
    numeric: Boolean = false,
    placeholder: String = ""
) {
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(12.dp)
    BasicTextField(
        value = value,
        onValueChange = onValueChange,
        singleLine = singleLine,
        keyboardOptions = KeyboardOptions(keyboardType = if (numeric) KeyboardType.Number else KeyboardType.Text),
        textStyle = TextStyle(
            color = LessonCueTvColors.Cream,
            fontSize = 24.sp,
            fontWeight = FontWeight.Medium
        ),
        modifier = modifier
            .clip(shape)
            .background(LessonCueTvColors.ElevatedPanel)
            .border(
                if (focused) 3.dp else 1.dp,
                if (focused) LessonCueTvColors.FocusOrange else LessonCueTvColors.Border,
                shape
            )
            .onFocusChanged { focused = it.isFocused }
            .padding(horizontal = 20.dp, vertical = 18.dp),
        decorationBox = { inner ->
            Box {
                if (value.isEmpty() && placeholder.isNotEmpty()) {
                    Text(placeholder, color = LessonCueTvColors.Muted, fontSize = 22.sp)
                }
                inner()
            }
        }
    )
}

@Composable
internal fun SectionTitle(eyebrow: String, title: String, subtitle: String? = null) {
    Text(
        eyebrow.uppercase(),
        color = LessonCueTvColors.Orange,
        fontSize = 13.sp,
        fontWeight = FontWeight.Bold,
        letterSpacing = 2.sp
    )
    Text(
        title,
        color = LessonCueTvColors.Cream,
        fontSize = 32.sp,
        fontWeight = FontWeight.Bold,
        maxLines = 2,
        overflow = TextOverflow.Ellipsis
    )
    subtitle?.let {
        Text(it, color = LessonCueTvColors.Muted, fontSize = 17.sp, maxLines = 2)
    }
}

@Composable
internal fun RemoteHintStrip(
    statusContent: @Composable RowScope.() -> Unit,
    hints: List<Pair<String, String>>,
    modifier: Modifier = Modifier
) {
    TvPanel(modifier) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 9.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(22.dp), content = statusContent)
            Box(Modifier.width(1.dp).height(34.dp).background(LessonCueTvColors.Border))
            Row(
                Modifier.weight(1.3f).padding(start = 22.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                hints.forEach { (glyph, label) ->
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(glyph, color = LessonCueTvColors.Cream, fontSize = 21.sp, fontWeight = FontWeight.Bold)
                        Spacer(Modifier.width(9.dp))
                        Text(label, color = LessonCueTvColors.Muted, fontSize = 13.sp, maxLines = 1)
                    }
                }
            }
        }
    }
}
