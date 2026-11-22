package org.lessoncue.tv

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.ConcurrentHashMap

internal object MediaCacheLocks {
    private val locks = ConcurrentHashMap<String, Mutex>()

    suspend fun <T> withLock(destination: java.io.File, block: () -> T): T {
        val lock = locks.computeIfAbsent(destination.absolutePath) { Mutex() }
        return lock.withLock(action = block)
    }
}

class MediaCacheWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        val url = inputData.getString("url") ?: return@withContext Result.failure()
        val fileName = inputData.getString("fileName") ?: return@withContext Result.failure()
        if (fileName.isBlank() || fileName == "." || fileName == ".." || '/' in fileName || '\\' in fileName)
            return@withContext Result.failure()
        val token = inputData.getString("token")
        val serverHost = inputData.getString("serverHost")
        val expectedSha = inputData.getString("sha256")
            ?.takeIf { it.isNotBlank() }
        val expectedBytes = inputData.getLong("sizeBytes", 0L).takeIf { it > 0L }
        val destination = applicationContext.filesDir.resolve("media").also { it.mkdirs() }.resolve(fileName)
        val coroutineContext = currentCoroutineContext()
        MediaCacheLocks.withLock(destination) {
            val diagnosticError = destination.resolveSibling("$fileName.error")
            try {
                coroutineContext.ensureActive()
                if (isCacheFileUsable(destination, expectedBytes, expectedSha, verifyChecksum = true)) {
                    diagnosticError.delete()
                    Result.success()
                } else {
                    if (destination.exists()) destination.delete()
                    val connection = URL(url).openConnection() as HttpURLConnection
                    connection.instanceFollowRedirects = false
                    if (URL(url).host.equals(serverHost, ignoreCase = true))
                        token?.let { connection.setRequestProperty("Authorization", "Bearer $it") }
                    downloadMedia(connection, destination, expectedSha, expectedBytes) { coroutineContext.ensureActive() }
                    diagnosticError.delete()
                    Result.success()
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                diagnosticError.writeText("${System.currentTimeMillis()}\n${error.message ?: error.javaClass.simpleName}")
                if (error is PermanentMediaDownloadException || runAttemptCount >= 2) Result.failure() else Result.retry()
            }
        }
    }
}
