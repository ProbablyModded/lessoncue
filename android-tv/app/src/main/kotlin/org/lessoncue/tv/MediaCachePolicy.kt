package org.lessoncue.tv

import java.io.File
import java.time.LocalDate
import java.time.ZoneId

/** Keep enough room for the priority lesson plus a little filesystem headroom. */
internal const val MEDIA_CACHE_RESERVE_BYTES = 128L * 1024L * 1024L

internal data class MediaCacheLesson(
    val playlist: LessonPlaylist,
    val date: LocalDate?,
    val items: List<CueItem>,
)

internal data class MediaCachePriority(
    val currentDay: List<MediaCacheLesson>,
    val older: List<MediaCacheLesson>,
)

internal data class EvictableMediaCacheItem(
    val item: CueItem,
    val lessonDate: LocalDate,
)

internal fun lessonCacheDate(playlist: LessonPlaylist, zoneId: ZoneId): LocalDate? =
    playlist.lessonDate ?: playlist.designatedStartAt?.atZone(zoneId)?.toLocalDate()

internal fun mediaCachePriority(
    playlists: List<LessonPlaylist>,
    today: LocalDate,
    zoneId: ZoneId,
): MediaCachePriority {
    val lessons = playlists.map { playlist ->
        MediaCacheLesson(playlist, lessonCacheDate(playlist, zoneId), cacheableLessonItems(playlist))
    }
    return MediaCachePriority(
        currentDay = lessons.filter { it.date == today },
        older = lessons
            .filter { it.date?.isBefore(today) == true }
            .sortedWith(compareBy<MediaCacheLesson>({ it.date }, { it.playlist.id })),
    )
}

internal fun distinctMediaCacheItems(lessons: Iterable<MediaCacheLesson>): List<CueItem> =
    lessons.flatMap { it.items }.distinctBy { it.id }

internal fun olderMediaCacheItems(
    priority: MediaCachePriority,
    protectedItemIds: Set<String> = emptySet(),
): List<EvictableMediaCacheItem> =
    priority.older
        .flatMap { lesson -> lesson.items.map { EvictableMediaCacheItem(it, lesson.date!!) } }
        .filterNot { it.item.id in protectedItemIds }
        .groupBy { it.item.id }
        .values
        .map { copies -> copies.minWithOrNull(compareBy<EvictableMediaCacheItem>({ it.lessonDate }, { it.item.id }))!! }
        .sortedWith(compareBy<EvictableMediaCacheItem>({ it.lessonDate }, { it.item.id }))

internal fun estimateMissingMediaBytes(directory: File, items: Collection<CueItem>): Long =
    items.distinctBy { it.id }.sumOf { item ->
        if (findUsableCacheFile(directory, item) != null) {
            0L
        } else {
            val expected = item.sizeBytes?.takeIf { it > 0L } ?: 0L
            val partial = directory.resolve("${item.cacheFileName()}.part")
            (expected - partial.length()).coerceAtLeast(0L)
        }
    }

internal fun mediaCacheCopyFiles(directory: File, item: CueItem): List<File> = listOf(
    directory.resolve(item.cacheFileName()),
    directory.resolve("${item.id}.bin"),
    directory.resolve("${item.cacheFileName()}.part"),
    directory.resolve("${item.cacheFileName()}.part.etag"),
    directory.resolve("${item.cacheFileName()}.error"),
).distinctBy { it.absolutePath }

internal fun selectOlderMediaCacheItemsToEvict(
    directory: File,
    candidates: Collection<EvictableMediaCacheItem>,
    freeBytes: Long,
    requiredBytes: Long,
    reserveBytes: Long = MEDIA_CACHE_RESERVE_BYTES,
): List<EvictableMediaCacheItem> {
    val targetBytes = requiredBytes.coerceAtLeast(0L) + reserveBytes.coerceAtLeast(0L)
    if (freeBytes >= targetBytes) return emptyList()

    var availableBytes = freeBytes
    val selected = mutableListOf<EvictableMediaCacheItem>()
    for (candidate in candidates) {
        if (availableBytes >= targetBytes) break
        val reclaimable = mediaCacheCopyFiles(directory, candidate.item)
            .filter { it.isFile }
            .sumOf { it.length() }
        if (reclaimable <= 0L) continue
        selected += candidate
        availableBytes += reclaimable
    }
    return selected
}

/** Delete only files owned by this media item; never sweep the whole app directory. */
internal fun deleteMediaCacheCopies(directory: File, item: CueItem): Long =
    mediaCacheCopyFiles(directory, item).sumOf { file ->
        if (!file.isFile) return@sumOf 0L
        val size = file.length()
        if (runCatching { file.delete() }.getOrDefault(false)) size else 0L
    }
