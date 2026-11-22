package org.lessoncue.tv

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Handler
import android.os.Looper
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import java.nio.charset.StandardCharsets
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.coroutines.resume

internal class LessonCueDiscovery(context: Context) {
    private val appContext = context.applicationContext
    private val nsdManager = appContext.getSystemService(Context.NSD_SERVICE) as NsdManager

    suspend fun findServers(timeoutMillis: Long = 6_000): List<String> =
        withTimeoutOrNull(timeoutMillis) { browse() }.orEmpty()

    suspend fun findServer(timeoutMillis: Long = 6_000): String? = findServers(timeoutMillis).firstOrNull()

    private suspend fun browse(): List<String> = suspendCancellableCoroutine { continuation ->
        val completed = AtomicBoolean(false)
        val found = linkedSetOf<String>()
        val handler = Handler(Looper.getMainLooper())
        lateinit var discoveryListener: NsdManager.DiscoveryListener
        lateinit var timeout: Runnable
        lateinit var resolutions: ServiceResolutionQueue<NsdServiceInfo>

        fun releaseResources() {
            resolutions.close()
            // Start and stop share the main queue, including cancellation before start.
            handler.post {
                handler.removeCallbacks(timeout)
                runCatching { nsdManager.stopServiceDiscovery(discoveryListener) }
            }
        }

        fun complete() {
            if (!completed.compareAndSet(false, true)) return
            releaseResources()
            if (continuation.isActive) continuation.resume(found.toList())
        }

        resolutions = ServiceResolutionQueue(
            key = { "${it.serviceName}|${it.serviceType}" },
            resolve = { service, resolved ->
                nsdManager.resolveService(service, object : NsdManager.ResolveListener {
                    override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) = resolved(null)

                    override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                        val address = runCatching {
                            val secure = serviceInfo.attributes["secure"]
                                ?.toString(StandardCharsets.UTF_8)
                                ?.equals("true", ignoreCase = true) == true
                            lessonCueServiceUrl(serviceHostAddress(serviceInfo.host), serviceInfo.port, secure)
                        }.getOrNull()
                        resolved(address)
                    }
                })
            },
            found = { url -> found += url },
        )

        discoveryListener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) = Unit
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onServiceLost(serviceInfo: NsdServiceInfo) = Unit
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) = complete()
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                if (serviceInfo.serviceType.trimEnd('.').equals(SERVICE_TYPE.trimEnd('.'), ignoreCase = true)) {
                    resolutions.offer(serviceInfo)
                }
            }
        }

        timeout = Runnable { complete() }
        continuation.invokeOnCancellation {
            if (completed.compareAndSet(false, true)) releaseResources()
        }
        handler.post {
            if (!completed.get()) {
                handler.postDelayed(timeout, 5_500)
                runCatching {
                    nsdManager.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discoveryListener)
                }.onFailure { complete() }
            }
        }
    }

    private companion object {
        const val SERVICE_TYPE = "_lessoncue._tcp."
    }
}

private fun serviceHostAddress(host: java.net.InetAddress?): String? {
    if (host !is java.net.Inet6Address) return host?.hostAddress
    val hostAddress = host.hostAddress ?: return null
    var value = hostAddress.substringBefore('%')
    if (!host.isLinkLocalAddress) return value
    val scope = hostAddress.substringAfter('%', "").takeIf { it.isNotBlank() }
        ?: runCatching { host.scopedInterface?.name }.getOrNull()?.takeIf { it.isNotBlank() }
        ?: host.scopeId.takeIf { it > 0 }?.toString()
        ?: return null
    value += "%$scope"
    return value
}

internal fun lessonCueServiceUrl(hostAddress: String?, port: Int, secure: Boolean): String? {
    val address = hostAddress?.trim()?.takeIf(String::isNotEmpty) ?: return null
    if (port !in 1..65_535) return null
    if (':' in address && isBareLinkLocalHost(address)) return null
    val scheme = if (secure) "https" else "http"
    val host = if (':' in address) "[${address.replace("%", "%25")}]" else address
    val defaultPort = if (secure) 443 else 80
    return "$scheme://$host${if (port == defaultPort) "" else ":$port"}"
}

private fun isBareLinkLocalHost(value: String): Boolean {
    if ('%' in value) return false
    val address = runCatching { java.net.InetAddress.getByName(value) }
        .getOrNull() as? java.net.Inet6Address ?: return false
    return address.isLinkLocalAddress
}
