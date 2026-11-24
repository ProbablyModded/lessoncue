package org.lessoncue.tv

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Test
import java.nio.file.Files

class MediaCacheValidationTest {
    @Test
    fun truncatedFileIsNotReportedAsCached() {
        val directory = Files.createTempDirectory("lessoncue-cache-validation").toFile()
        try {
            val item = item(sizeBytes = 10)
            val file = directory.resolve(item.cacheFileName()).also { it.writeText("short") }

            assertFalse(isCacheFileUsable(file, item.sizeBytes))
            assertNull(findUsableCacheFile(directory, item))
        } finally {
            directory.deleteRecursively()
        }
    }

    @Test
    fun validFileClearsStaleErrorMarkerAndInvalidFileIsRemoved() {
        val directory = Files.createTempDirectory("lessoncue-cache-validation").toFile()
        try {
            val item = item(sizeBytes = 5)
            val destination = directory.resolve(item.cacheFileName())
            destination.writeText("too-short")
            val error = directory.resolve("${item.cacheFileName()}.error").also { it.writeText("old") }

            assertNull(prepareCacheForDownload(directory, item))
            assertFalse(destination.exists())
            assertFalse(error.exists())

            destination.writeText("valid")
            error.writeText("old")
            assertEquals(destination, prepareCacheForDownload(directory, item))
            assertFalse(error.exists())
        } finally {
            directory.deleteRecursively()
        }
    }

    @Test
    fun checksumValidationRejectsSameSizedCorruption() {
        val directory = Files.createTempDirectory("lessoncue-cache-validation").toFile()
        try {
            val item = item(sizeBytes = 5, sha256 = "0".repeat(64))
            val file = directory.resolve(item.cacheFileName()).also { it.writeText("valid") }

            assertFalse(isCacheFileUsable(file, item.sizeBytes, item.sha256, verifyChecksum = true))
        } finally {
            directory.deleteRecursively()
        }
    }

    @Test
    fun oversizedPartialFromAnOlderManifestIsRemovedBeforeRetry() {
        val directory = Files.createTempDirectory("lessoncue-cache-validation").toFile()
        try {
            val item = item(sizeBytes = 5)
            val partial = directory.resolve("${item.cacheFileName()}.part").also { it.writeBytes(ByteArray(8)) }
            val etag = directory.resolve("${partial.name}.etag").also { it.writeText("\"old\"") }

            prepareCacheForDownload(directory, item)

            assertFalse(partial.exists())
            assertFalse(etag.exists())
        } finally {
            directory.deleteRecursively()
        }
    }

    private fun item(sizeBytes: Long, sha256: String? = null) = CueItem(
        id = "item-1",
        title = "Test video",
        type = "video",
        url = "http://lessoncue.local/media/item-1.mp4",
        sha256 = sha256,
        sizeBytes = sizeBytes,
        offlineEligible = true,
    )
}
