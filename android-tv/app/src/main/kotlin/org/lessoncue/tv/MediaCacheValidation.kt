package org.lessoncue.tv

import java.io.File
import java.security.MessageDigest

/**
 * A cache entry is usable only when it is a real, non-empty file and its
 * advertised byte count agrees with the manifest. The checksum is reserved for
 * worker/playback repair paths because hashing a large video during Compose
 * recomposition would block the TV UI.
 */
internal fun isCacheFileUsable(
    file: File,
    expectedBytes: Long?,
    expectedSha256: String? = null,
    verifyChecksum: Boolean = false,
): Boolean {
    if (!file.isFile || file.length() <= 0L) return false
    val expectedSize = expectedBytes?.takeIf { it > 0L }
    if (expectedSize != null && file.length() != expectedSize) return false
    if (!verifyChecksum || expectedSha256.isNullOrBlank()) return true
    return runCatching { sha256(file).equals(expectedSha256, ignoreCase = true) }.getOrDefault(false)
}

internal fun findUsableCacheFile(directory: File, item: CueItem): File? =
    listOf(
        directory.resolve(item.cacheFileName()),
        directory.resolve("${item.id}.bin"),
    ).firstOrNull { isCacheFileUsable(it, item.sizeBytes) }

/** Remove obsolete final files and their stale error markers before scheduling a repair. */
internal fun prepareCacheForDownload(directory: File, item: CueItem): File? {
    directory.mkdirs()
    val candidates = listOf(
        directory.resolve(item.cacheFileName()),
        directory.resolve("${item.id}.bin"),
    )
    candidates.filter { it.exists() && !isCacheFileUsable(it, item.sizeBytes) }
        .forEach { runCatching { it.delete() } }
    val expectedSize = item.sizeBytes?.takeIf { it > 0L }
    val partial = directory.resolve("${item.cacheFileName()}.part")
    if (expectedSize != null && partial.isFile && partial.length() > expectedSize) {
        // This is a partial from an older, larger rendition. It cannot be
        // resumed against the current manifest and otherwise makes the cache
        // look like it is downloading more bytes than the item needs.
        runCatching { partial.delete() }
        runCatching { directory.resolve("${partial.name}.etag").delete() }
    }
    val usable = candidates.firstOrNull { isCacheFileUsable(it, item.sizeBytes) }
    // A new WorkManager attempt will write a fresh diagnostic if it fails.
    // Do not carry an old retry marker into that attempt.
    directory.resolve("${item.cacheFileName()}.error").delete()
    return usable
}

/** Remove only published cache copies after playback proves one is unusable.
 * Keep an in-progress .part file so the worker can safely resume it. */
internal fun invalidateCachedMedia(directory: File, item: CueItem) {
    listOf(
        directory.resolve(item.cacheFileName()),
        directory.resolve("${item.id}.bin"),
    ).forEach { it.delete() }
    directory.resolve("${item.cacheFileName()}.error").delete()
}

internal fun sha256(file: File): String {
    val digest = MessageDigest.getInstance("SHA-256")
    file.inputStream().buffered().use { input ->
        val buffer = ByteArray(64 * 1024)
        while (true) {
            val count = input.read(buffer)
            if (count < 0) break
            digest.update(buffer, 0, count)
        }
    }
    return digest.digest().joinToString("") { "%02x".format(it) }
}
