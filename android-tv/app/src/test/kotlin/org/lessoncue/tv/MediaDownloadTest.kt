package org.lessoncue.tv

import kotlinx.coroutines.CancellationException
import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.nio.file.Files
import java.security.MessageDigest

class MediaDownloadTest {
    private class Connection(
        private val body: String = "abc",
        private val status: Int = 200,
        private val length: Long = body.length.toLong(),
        private val range: String? = null,
        private val etag: String? = null,
        private val failRead: Boolean = false,
    ) : HttpURLConnection(URL("http://lessoncue.local/media/test")) {
        var disconnects = 0
        override fun connect() = Unit
        override fun disconnect() { disconnects++ }
        override fun usingProxy() = false
        override fun getResponseCode() = status
        override fun getContentLengthLong() = length
        override fun getHeaderField(name: String?): String? = when (name) {
            "Content-Range" -> range
            "ETag" -> etag
            else -> null
        }
        override fun getInputStream(): InputStream = if (failRead) object : InputStream() {
            override fun read(): Int = throw IOException("connection lost")
        } else ByteArrayInputStream(body.toByteArray())
    }

    private fun withDestination(test: (File, File) -> Unit) {
        val directory = Files.createTempDirectory("lessoncue-download-test").toFile()
        try { test(File(directory, "video.bin"), File(directory, "video.bin.part")) }
        finally { directory.deleteRecursively() }
    }

    @Test fun publishesOnlyAfterCompleteResponseAndConfiguresTimeouts() = withDestination { destination, partial ->
        val connection = Connection()
        downloadMedia(connection, destination, null)
        assertEquals("abc", destination.readText())
        assertFalse(partial.exists())
        assertEquals(15_000, connection.connectTimeout)
        assertEquals(60_000, connection.readTimeout)
        assertEquals(1, connection.disconnects)
    }

    @Test fun resumesAtValidatedOffsetAndRestartsIfRangeIsIgnored() = withDestination { destination, partial ->
        partial.writeText("abc")
        val resumed = Connection("def", 206, range = "bytes 3-5/6", etag = "\"v1\"")
        downloadMedia(resumed, destination, null)
        assertEquals("bytes=3-", resumed.getRequestProperty("Range"))
        assertEquals("abcdef", destination.readText())
        partial.writeText("old")
        downloadMedia(Connection("replacement"), destination, null)
        assertEquals("replacement", destination.readText())
    }

    @Test fun resumesOnlyWhenTheServerObjectStillMatches() = withDestination { destination, partial ->
        partial.writeText("abc")
        partial.resolveSibling("${partial.name}.etag").writeText("\"v1\"")
        val resumed = Connection("def", 206, range = "bytes 3-5/6", etag = "\"v1\"")

        downloadMedia(resumed, destination, null)

        assertEquals("\"v1\"", resumed.getRequestProperty("If-Range"))
        assertEquals("abcdef", destination.readText())
        assertFalse(partial.resolveSibling("${partial.name}.etag").exists())
    }

    @Test fun discardsPartialWhenResumeValidatorChanges() = withDestination { destination, partial ->
        partial.writeText("abc")
        partial.resolveSibling("${partial.name}.etag").writeText("\"v1\"")

        assertThrows(IOException::class.java) {
            downloadMedia(Connection("def", 206, range = "bytes 3-5/6", etag = "\"v2\""), destination, null)
        }

        assertFalse(partial.exists())
        assertFalse(partial.resolveSibling("${partial.name}.etag").exists())
        assertFalse(destination.exists())
    }

    @Test fun refusesTruncatedBodyWithoutReplacingKnownGoodCache() = withDestination { destination, partial ->
        destination.writeText("known-good")
        val connection = Connection("abc", length = 6)
        assertThrows(IOException::class.java) { downloadMedia(connection, destination, null) }
        assertEquals("known-good", destination.readText())
        assertEquals("abc", partial.readText())
        assertEquals(1, connection.disconnects)
    }

    @Test fun refusesWrongRangeAndHttpErrorsWithoutDestroyingPartial() = withDestination { destination, partial ->
        for (connection in listOf(Connection("xyz", 206, range = "bytes 0-2/6"), Connection(status = 503))) {
            partial.writeText("abc")
            assertThrows(IOException::class.java) { downloadMedia(connection, destination, null) }
            assertEquals("abc", partial.readText())
            assertFalse(destination.exists())
            assertEquals(1, connection.disconnects)
        }
    }

    @Test fun keepsNotReadyMediaRetryableForLaterTranscodeAttempts() {
        assertTrue(isRetryableMediaHttpStatus(404))
        assertTrue(isRetryableMediaHttpStatus(409))
        assertTrue(isRetryableMediaHttpStatus(425))
        assertTrue(isRetryableMediaHttpStatus(503))
        assertFalse(isRetryableMediaHttpStatus(401))
        assertFalse(isRetryableMediaHttpStatus(403))
    }

    @Test fun incompleteRangeRemainsPartialAnd416AllowsFreshRetry() = withDestination { destination, partial ->
        partial.writeText("abc")
        assertThrows(IOException::class.java) {
            downloadMedia(Connection("de", 206, range = "bytes 3-4/6"), destination, null)
        }
        assertEquals("abcde", partial.readText())
        assertFalse(destination.exists())
        val unavailable = Connection(status = 416)
        assertThrows(IOException::class.java) { downloadMedia(unavailable, destination, null) }
        assertFalse(partial.exists())
        assertEquals(1, unavailable.disconnects)
        downloadMedia(Connection("fresh"), destination, null)
        assertEquals("fresh", destination.readText())
    }

    @Test fun checksumMismatchNeverPublishesAndMatchingChecksumSucceeds() = withDestination { destination, partial ->
        val expected = MessageDigest.getInstance("SHA-256").digest("abc".toByteArray())
            .joinToString("") { "%02x".format(it) }
        val invalid = Connection("bad")
        assertThrows(IOException::class.java) { downloadMedia(invalid, destination, expected) }
        assertFalse(destination.exists())
        assertFalse(partial.exists())
        assertEquals(1, invalid.disconnects)
        downloadMedia(Connection(), destination, expected)
        assertEquals("abc", destination.readText())
    }

    @Test fun readFailureAndCancellationAlwaysDisconnect() = withDestination { destination, _ ->
        val broken = Connection(failRead = true)
        assertThrows(IOException::class.java) { downloadMedia(broken, destination, null) }
        assertEquals(1, broken.disconnects)
        val cancelled = Connection()
        var checks = 0
        assertThrows(CancellationException::class.java) {
            downloadMedia(cancelled, destination, null) {
                if (++checks > 1) throw CancellationException("stopped")
            }
        }
        assertFalse(destination.exists())
        assertEquals(1, cancelled.disconnects)
    }
}
