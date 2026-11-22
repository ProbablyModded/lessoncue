package org.lessoncue.tv

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.file.Files
import java.time.LocalDate
import java.time.ZoneOffset

class MediaCachePolicyTest {
    private fun item(id: String, sizeBytes: Long = 10L) = CueItem(
        id = id,
        title = id,
        type = "video",
        url = "http://lessoncue.local/media/$id.mp4",
        fileExtension = "mp4",
        sizeBytes = sizeBytes,
        offlineEligible = true,
    )

    private fun lesson(id: String, date: LocalDate, item: CueItem) = LessonPlaylist(
        id = id,
        title = id,
        designatedStartAt = null,
        preRollStartsAt = null,
        countdown = null,
        preRoll = null,
        items = listOf(item),
        lessonDate = date,
    )

    @Test
    fun currentDayIsSeparatedFromOlderLessonsAndOlderItemsAreOldestFirst() {
        val today = LocalDate.of(2026, 11, 22)
        val priority = mediaCachePriority(
            listOf(
                lesson("new", today, item("new-item")),
                lesson("oldest", today.minusDays(2), item("oldest-item")),
                lesson("old", today.minusDays(1), item("old-item")),
            ),
            today,
            ZoneOffset.UTC,
        )

        assertEquals(listOf("new"), priority.currentDay.map { it.playlist.id })
        assertEquals(listOf("oldest", "old"), priority.older.map { it.playlist.id })
        assertEquals(
            listOf("oldest-item", "old-item"),
            olderMediaCacheItems(priority).map { it.item.id },
        )
    }

    @Test
    fun designatedStartDateCanIdentifyTodaysLessonWhenLessonDateIsMissing() {
        val today = LocalDate.of(2026, 11, 22)
        val scheduled = LessonPlaylist(
            id = "scheduled",
            title = "scheduled",
            designatedStartAt = today.atStartOfDay(ZoneOffset.UTC).toInstant(),
            preRollStartsAt = null,
            countdown = null,
            preRoll = null,
            items = listOf(item("scheduled-item")),
        )

        assertEquals(
            listOf("scheduled"),
            mediaCachePriority(listOf(scheduled), today, ZoneOffset.UTC).currentDay.map { it.playlist.id },
        )
    }

    @Test
    fun evictionSelectsOldestCopiesUntilPrioritySpaceIsAvailable() {
        val directory = Files.createTempDirectory("lessoncue-cache-policy").toFile()
        try {
            val oldest = item("oldest", 40)
            val old = item("old", 30)
            directory.resolve(oldest.cacheFileName()).writeBytes(ByteArray(40))
            directory.resolve(old.cacheFileName()).writeBytes(ByteArray(30))
            val today = LocalDate.of(2026, 11, 22)
            val priority = mediaCachePriority(
                listOf(
                    lesson("oldest-lesson", today.minusDays(2), oldest),
                    lesson("old-lesson", today.minusDays(1), old),
                ),
                today,
                ZoneOffset.UTC,
            )

            val selected = selectOlderMediaCacheItemsToEvict(
                directory = directory,
                candidates = olderMediaCacheItems(priority),
                freeBytes = 50,
                requiredBytes = 60,
                reserveBytes = 0,
            )

            assertEquals(listOf("oldest"), selected.map { it.item.id })
        } finally {
            directory.deleteRecursively()
        }
    }

    @Test
    fun missingBytesSubtractAnExistingPartialDownload() {
        val directory = Files.createTempDirectory("lessoncue-cache-policy").toFile()
        try {
            val media = item("media", 100)
            directory.resolve("${media.cacheFileName()}.part").writeBytes(ByteArray(35))

            assertEquals(65L, estimateMissingMediaBytes(directory, listOf(media)))
            assertTrue(mediaCacheCopyFiles(directory, media).any { it.name.endsWith(".part") })
        } finally {
            directory.deleteRecursively()
        }
    }

    @Test
    fun deletionRemovesFinalPartialValidatorAndErrorCopies() {
        val directory = Files.createTempDirectory("lessoncue-cache-policy").toFile()
        try {
            val media = item("media", 10)
            mediaCacheCopyFiles(directory, media).forEach { it.writeBytes(ByteArray(2)) }

            assertEquals(10L, deleteMediaCacheCopies(directory, media))
            assertTrue(mediaCacheCopyFiles(directory, media).none { it.exists() })
        } finally {
            directory.deleteRecursively()
        }
    }
}
