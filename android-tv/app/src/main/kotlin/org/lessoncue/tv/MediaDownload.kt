package org.lessoncue.tv

import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest

internal class PermanentMediaDownloadException(message: String) : IOException(message)

/** A manifest can briefly point at a rendition while the server is finishing it. */
internal fun isRetryableMediaHttpStatus(status: Int): Boolean =
    status == 404 || status == 408 || status == 409 || status == 425 || status == 429 || status >= 500

/** Publish a cache file only after the response, byte count and checksum agree. */
internal fun downloadMedia(
    connection: HttpURLConnection,
    destination: File,
    expectedSha: String?,
    expectedBytes: Long? = null,
    checkActive: () -> Unit = {},
) {
    val partial = destination.resolveSibling("${destination.name}.part")
    val partialTag = destination.resolveSibling("${destination.name}.part.etag")
    try {
        checkActive()
        // Large media responses can pause briefly while a server or proxy
        // resumes a range request. Keep the timeout bounded, but do not make
        // a normal buffering pause look like a failed download to the server.
        connection.connectTimeout = 15_000
        connection.readTimeout = 60_000
        connection.setRequestProperty("Accept-Encoding", "identity")
        val existing = if (partial.exists()) partial.length() else 0L
        val resumeTag = if (existing > 0L && partialTag.isFile)
            partialTag.readText().trim().takeIf { it.isNotEmpty() }
        else null
        if (existing > 0) {
            connection.setRequestProperty("Range", "bytes=$existing-")
            resumeTag?.let { connection.setRequestProperty("If-Range", it) }
        } else if (partialTag.exists()) {
            partialTag.delete()
        }
        val status = connection.responseCode
        if (status == 416) {
            // The remote object changed, or an interrupted transfer already reached EOF.
            discardPartial(partial, partialTag)
            throw IOException("Cached media range is unavailable; retrying a full download")
        }
        if (status in 400..499 && !isRetryableMediaHttpStatus(status) && status != 416)
            throw PermanentMediaDownloadException("Media download returned HTTP $status")
        if (status != 200 && status != 206) throw IOException("Media download returned HTTP $status")
        val responseTag = connection.getHeaderField("ETag")?.trim()?.takeIf { it.isNotEmpty() }
        if (status == 206 && existing > 0L && resumeTag != null &&
            (responseTag == null || !sameEntityTag(resumeTag, responseTag))) {
            discardPartial(partial, partialTag)
            throw IOException("Cached media resume validator did not match the server object")
        }
        if (responseTag != null) partialTag.writeText(responseTag)
        else if (status == 200) partialTag.delete()
        val range = if (status == 206) {
            val parts = Regex("bytes (\\d+)-(\\d+)/(\\d+)")
                .matchEntire(connection.getHeaderField("Content-Range").orEmpty())
                ?: throw IOException("Media download returned an invalid Content-Range")
            val start = parts.groupValues[1].toLongOrNull()
            val end = parts.groupValues[2].toLongOrNull()
            val total = parts.groupValues[3].toLongOrNull()
            if (start != existing || end == null || total == null || end < existing || end >= total)
                throw IOException("Media download returned an unexpected Content-Range")
            (end - existing + 1) to total
        } else null
        val declaredLength = connection.contentLengthLong
        val expectedLength = range?.first ?: declaredLength
        if (range != null && declaredLength >= 0 && declaredLength != range.first)
            throw IOException("Media download returned inconsistent response lengths")
        var received = 0L
        // Obtain the response stream before truncating a previous partial file.
        connection.inputStream.use { input ->
            FileOutputStream(partial, status == 206 && existing > 0).buffered().use { output ->
                val buffer = ByteArray(64 * 1024)
                while (true) {
                    checkActive()
                    val count = input.read(buffer)
                    if (count < 0) break
                    output.write(buffer, 0, count)
                    received += count
                }
            }
        }
        if (expectedLength >= 0 && received != expectedLength) {
            if (received > expectedLength) partial.delete()
            throw IOException("Media download was incomplete or exceeded its declared length")
        }
        if (range != null && partial.length() != range.second)
            throw IOException("Media download requires another range to complete")
        val expectedTotal = expectedBytes?.takeIf { it > 0L }
        if (expectedTotal != null && partial.length() != expectedTotal) {
            if (partial.length() > expectedTotal) partial.delete()
            throw IOException("Media download did not match the manifest byte count")
        }
        if (expectedSha != null) {
            val digest = MessageDigest.getInstance("SHA-256")
            partial.inputStream().use { input ->
                val buffer = ByteArray(64 * 1024)
                while (true) {
                    checkActive()
                    val count = input.read(buffer)
                    if (count < 0) break
                    digest.update(buffer, 0, count)
                }
            }
            val actual = digest.digest().joinToString("") { "%02x".format(it) }
            if (!actual.equals(expectedSha, ignoreCase = true)) {
                discardPartial(partial, partialTag)
                throw IOException("Cached media checksum did not match")
            }
        }
        checkActive()
        publishCacheFile(partial, destination)
        partialTag.delete()
    } finally {
        connection.disconnect()
    }
}

private fun discardPartial(partial: File, partialTag: File) {
    partial.delete()
    partialTag.delete()
}

private fun sameEntityTag(first: String, second: String): Boolean =
    first == second

private fun publishCacheFile(partial: File, destination: File) {
    try {
        Files.move(partial.toPath(), destination.toPath(),
            StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE)
    } catch (_: AtomicMoveNotSupportedException) {
        Files.move(partial.toPath(), destination.toPath(), StandardCopyOption.REPLACE_EXISTING)
    }
}
