package org.gamecult.cultmesh

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import java.net.URI
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets
import java.security.SecureRandom
import java.security.MessageDigest
import java.time.Instant
import java.util.ArrayDeque
import java.util.Base64
import java.util.TreeMap
import java.util.concurrent.atomic.AtomicBoolean

interface CultDocumentCodec<T> {
    val documentType: String
    val schemaVersion: String
    fun encode(value: T): ByteArray
    fun decode(payload: ByteArray): T
}

data class CultDocumentDefinition<T : Any>(
    val codec: CultDocumentCodec<T>,
    val global: Boolean = false,
) {
    val documentType: String get() = codec.documentType
    val schemaVersion: String get() = codec.schemaVersion
}

data class CultCacheRecord<T : Any>(
    val key: String,
    val value: T,
)

data class CultCacheEnvelope(
    val documentType: String,
    val schemaId: String,
    val key: String,
    val storedAt: String,
    val payload: ByteArray,
)

class CultCache {
    companion object {
        const val GLOBAL_KEY = "__global__"
    }

    private val codecs = linkedMapOf<String, CultDocumentCodec<*>>()
    private val codecsBySchema = linkedMapOf<String, CultDocumentCodec<*>>()
    private val values = linkedMapOf<String, LinkedHashMap<String, ByteArray>>()

    fun <T : Any> register(codec: CultDocumentCodec<T>) {
        codecs[codec.documentType] = codec
        codecsBySchema[codec.schemaVersion] = codec
    }

    fun <T : Any> register(document: CultDocumentDefinition<T>) {
        register(document.codec)
    }

    fun <T : Any> put(codec: CultDocumentCodec<T>, key: String, value: T) {
        register(codec)
        values.getOrPut(codec.documentType) { linkedMapOf() }[key] = codec.encode(value)
    }

    fun <T : Any> put(document: CultDocumentDefinition<T>, key: String, value: T) {
        if (document.global && key != GLOBAL_KEY) throw IOException("Global document ${document.documentType} must use putGlobal")
        put(document.codec, key, value)
    }

    fun <T : Any> get(codec: CultDocumentCodec<T>, key: String): T? {
        register(codec)
        return values[codec.documentType]?.get(key)?.let { codec.decode(it.copyOf()) }
    }

    fun <T : Any> get(document: CultDocumentDefinition<T>, key: String): T? {
        if (document.global && key != GLOBAL_KEY) throw IOException("Global document ${document.documentType} must use getGlobal")
        return get(document.codec, key)
    }

    fun <T : Any> getRequired(codec: CultDocumentCodec<T>, key: String): T =
        get(codec, key) ?: throw NoSuchElementException("No ${codec.documentType} record for key $key")

    fun <T : Any> getRequired(document: CultDocumentDefinition<T>, key: String): T =
        get(document, key) ?: throw NoSuchElementException("No ${document.documentType} record for key $key")

    fun <T : Any> getAll(codec: CultDocumentCodec<T>): List<CultCacheRecord<T>> {
        register(codec)
        return values[codec.documentType]
            ?.map { (key, payload) -> CultCacheRecord(key, codec.decode(payload.copyOf())) }
            ?: emptyList()
    }

    fun <T : Any> getAll(document: CultDocumentDefinition<T>): List<CultCacheRecord<T>> = getAll(document.codec)

    fun snapshotEnvelopes(storedAt: String = Instant.now().toString()): List<CultCacheEnvelope> =
        values.toSortedMap().flatMap { (documentType, records) ->
            val codec = codecs[documentType] ?: return@flatMap emptyList()
            records.toSortedMap().map { (key, payload) ->
                CultCacheEnvelope(
                    documentType = documentType,
                    schemaId = codec.schemaVersion,
                    key = key,
                    storedAt = storedAt,
                    payload = payload.copyOf(),
                )
            }
        }

    fun putRaw(schemaId: String, key: String, payload: ByteArray): Any {
        val codec = codecForSchema(schemaId)
        values.getOrPut(codec.documentType) { linkedMapOf() }[key] = payload.copyOf()
        return decodeRaw(codec, payload.copyOf())
    }

    fun codecForSchema(schemaId: String): CultDocumentCodec<*> =
        codecsBySchema[schemaId] ?: codecs[schemaId] ?: throw IOException("No registered Kotlin document codec for schema $schemaId")

    fun <T : Any> delete(codec: CultDocumentCodec<T>, key: String): Boolean {
        register(codec)
        val records = values[codec.documentType] ?: return false
        val removed = records.remove(key) != null
        if (records.isEmpty()) values.remove(codec.documentType)
        return removed
    }

    fun <T : Any> delete(document: CultDocumentDefinition<T>, key: String): Boolean {
        if (document.global && key != GLOBAL_KEY) throw IOException("Global document ${document.documentType} must use deleteGlobal")
        return delete(document.codec, key)
    }

    fun deleteBySchema(schemaId: String, key: String): Boolean =
        deleteRawByDocumentType(codecForSchema(schemaId).documentType, key)

    fun <T : Any> putGlobal(document: CultDocumentDefinition<T>, value: T) {
        put(document.codec, GLOBAL_KEY, value)
    }

    fun <T : Any> getGlobal(document: CultDocumentDefinition<T>): T? = get(document.codec, GLOBAL_KEY)

    fun <T : Any> getRequiredGlobal(document: CultDocumentDefinition<T>): T =
        getGlobal(document) ?: throw NoSuchElementException("No global ${document.documentType} record")

    fun <T : Any> deleteGlobal(document: CultDocumentDefinition<T>): Boolean = delete(document.codec, GLOBAL_KEY)

    @Suppress("UNCHECKED_CAST")
    private fun decodeRaw(codec: CultDocumentCodec<*>, payload: ByteArray): Any =
        (codec as CultDocumentCodec<Any>).decode(payload)

    private fun deleteRawByDocumentType(documentType: String, key: String): Boolean {
        val records = values[documentType] ?: return false
        val removed = records.remove(key) != null
        if (records.isEmpty()) values.remove(documentType)
        return removed
    }
}

fun <T : Any> cultDocument(
    codec: CultDocumentCodec<T>,
    global: Boolean = false,
): CultDocumentDefinition<T> = CultDocumentDefinition(codec, global)

class CultCacheDocumentHandle<T : Any>(
    private val cache: CultCache,
    private val document: CultDocumentDefinition<T>,
    private val key: String,
) {
    fun get(): T? = cache.get(document, key)

    fun require(): T = cache.getRequired(document, key)

    fun put(value: T) = cache.put(document, key, value)

    fun delete(): Boolean = cache.delete(document, key)
}

fun <T : Any> CultCache.document(document: CultDocumentDefinition<T>, key: String): CultCacheDocumentHandle<T> =
    CultCacheDocumentHandle(this, document, key)

fun <T : Any> CultCache.global(document: CultDocumentDefinition<T>): CultCacheDocumentHandle<T> =
    CultCacheDocumentHandle(this, document, CultCache.GLOBAL_KEY)

data class CultReactiveDocumentOptions(
    val flushDelayMs: Long = 16,
    val detectLocalChanges: Boolean = true,
    val replaceDirtyCurrentOnCanonicalSnapshot: Boolean = false,
)

data class CultReactiveDocumentReconciliation<T : Any>(
    val canonical: T,
    val predicted: T,
    val delta: Map<String, Any?>,
    val version: Long,
    val receivedAt: Instant,
)

class CultReactiveDocument<T : Any>(
    private val cache: CultCache,
    private val document: CultDocumentDefinition<T>,
    private val key: String,
    private val options: CultReactiveDocumentOptions = CultReactiveDocumentOptions(),
) : AutoCloseable {
    private val lock = Any()
    private val disposed = AtomicBoolean(false)
    private var lastCleanPayload = ByteArray(0)
    private var flushing = false
    private var flushQueued = false
    private var reconciliationVersion = 0L
    private val detectWorker: Thread?

    @Volatile
    var current: T = cache.getRequired(document, key)
        private set

    @Volatile
    var isDirty: Boolean = false
        private set

    @Volatile
    var reconciliation: CultReactiveDocumentReconciliation<T>? = null
        private set

    @Volatile
    var lastError: Throwable? = null
        private set

    init {
        require(key.isNotBlank()) { "key must be non-empty" }
        lastCleanPayload = document.codec.encode(current)
        detectWorker = if (options.detectLocalChanges) {
            Thread {
                val delay = options.flushDelayMs.coerceAtLeast(1)
                while (!disposed.get()) {
                    try {
                        Thread.sleep(delay)
                        if (disposed.get()) return@Thread
                        if (detectLocalChanges()) flush()
                    } catch (_: InterruptedException) {
                        if (disposed.get()) return@Thread
                    } catch (error: Throwable) {
                        lastError = error
                    }
                }
            }.apply {
                isDaemon = true
                name = "cultmesh-reactive-document-${document.documentType}-$key"
                start()
            }
        } else {
            null
        }
    }

    fun update(update: (T) -> T): T {
        val predicted = synchronized(lock) {
            current = update(current)
            isDirty = true
            current
        }
        if (options.flushDelayMs <= 0) flush()
        return predicted
    }

    fun setCurrent(value: T): T {
        synchronized(lock) {
            current = value
            isDirty = true
        }
        if (options.flushDelayMs <= 0) flush()
        return value
    }

    fun markDirty() {
        synchronized(lock) {
            isDirty = true
        }
    }

    fun refresh(): T {
        val canonical = cache.getRequired(document, key)
        synchronized(lock) {
            current = canonical
            lastCleanPayload = document.codec.encode(canonical)
            isDirty = false
            flushQueued = false
            reconciliation = null
        }
        return canonical
    }

    fun flush() {
        val predicted = synchronized(lock) {
            if (!isDirty && !detectLocalChangesLocked()) return
            if (flushing) {
                flushQueued = true
                return
            }
            flushing = true
            isDirty = false
            current
        }
        val payload = document.codec.encode(predicted)
        cache.put(document, key, predicted)
        val shouldFlushAgain = synchronized(lock) {
            flushing = false
            lastCleanPayload = payload
            reconciliation = null
            lastError = null
            if (!document.codec.encode(current).contentEquals(lastCleanPayload)) {
                isDirty = true
            }
            val queued = flushQueued || isDirty
            flushQueued = false
            queued
        }
        if (shouldFlushAgain) flush()
    }

    fun applyRawDocumentPut(message: CultNetMessage): T {
        cache.applyRawDocumentPut(message)
        return applyCanonical(cache.getRequired(document, key))
    }

    fun clearReconciliation() {
        synchronized(lock) {
            reconciliation = null
        }
    }

    override fun close() {
        disposed.set(true)
        detectWorker?.interrupt()
    }

    private fun detectLocalChanges(): Boolean = synchronized(lock) {
        detectLocalChangesLocked() && !flushing
    }

    private fun detectLocalChangesLocked(): Boolean {
        if (!isDirty && !flushing && !document.codec.encode(current).contentEquals(lastCleanPayload)) {
            isDirty = true
        }
        return isDirty
    }

    private fun applyCanonical(canonical: T): T {
        synchronized(lock) {
            if (isDirty || flushing) {
                val predicted = current
                val delta = reconciliationDelta(document.codec.encode(predicted), document.codec.encode(canonical))
                reconciliation = if (delta.isEmpty()) {
                    null
                } else {
                    reconciliationVersion += 1
                    CultReactiveDocumentReconciliation(
                        canonical = canonical,
                        predicted = predicted,
                        delta = delta,
                        version = reconciliationVersion,
                        receivedAt = Instant.now(),
                    )
                }
                if (!options.replaceDirtyCurrentOnCanonicalSnapshot) return canonical
            }
            current = canonical
            lastCleanPayload = document.codec.encode(canonical)
            reconciliation = null
            return canonical
        }
    }
}

fun <T : Any> CultCache.reactiveDocument(
    document: CultDocumentDefinition<T>,
    key: String,
    options: CultReactiveDocumentOptions = CultReactiveDocumentOptions(),
): CultReactiveDocument<T> = CultReactiveDocument(this, document, key, options)

class StringDocumentCodec(
    override val documentType: String,
    override val schemaVersion: String,
) : CultDocumentCodec<String> {
    override fun encode(value: String): ByteArray = value.toByteArray(StandardCharsets.UTF_8)

    override fun decode(payload: ByteArray): String = String(payload, StandardCharsets.UTF_8)
}

class ByteArrayDocumentCodec(
    override val documentType: String,
    override val schemaVersion: String,
) : CultDocumentCodec<ByteArray> {
    override fun encode(value: ByteArray): ByteArray = value.copyOf()

    override fun decode(payload: ByteArray): ByteArray = payload.copyOf()
}

fun stringDocument(
    documentType: String,
    schemaVersion: String,
    global: Boolean = false,
): CultDocumentDefinition<String> = cultDocument(StringDocumentCodec(documentType, schemaVersion), global)

fun bytesDocument(
    documentType: String,
    schemaVersion: String,
    global: Boolean = false,
): CultDocumentDefinition<ByteArray> = cultDocument(ByteArrayDocumentCodec(documentType, schemaVersion), global)

private data class KotlinCanonicalNote(
    val schemaVersion: String,
    val body: String,
)

private data class KotlinUiNote(
    val schemaVersion: String,
    val body: String,
)

private data class KotlinReactiveNote(
    var body: String,
    var revision: Long,
)

private class KotlinAliasNoteCodec<T : Any>(
    override val documentType: String,
    override val schemaVersion: String,
    private val create: (String, String) -> T,
    private val readSchemaVersion: (T) -> String,
    private val readBody: (T) -> String,
) : CultDocumentCodec<T> {
    override fun encode(value: T): ByteArray =
        MessagePackWriter()
            .value(listOf(readSchemaVersion(value), readBody(value)))
            .toByteArray()

    override fun decode(payload: ByteArray): T {
        val slots = anyList(MessagePackReader(payload).readAny())
        return create(slots[0] as String, slots[1] as String)
    }
}

private class KotlinReactiveNoteCodec(
    override val documentType: String,
    override val schemaVersion: String,
) : CultDocumentCodec<KotlinReactiveNote> {
    override fun encode(value: KotlinReactiveNote): ByteArray =
        MessagePackWriter()
            .value(listOf(value.body, value.revision))
            .toByteArray()

    override fun decode(payload: ByteArray): KotlinReactiveNote {
        val slots = anyList(MessagePackReader(payload).readAny())
        return KotlinReactiveNote(slots[0] as String, (slots[1] as Number).toLong())
    }
}

class CultMeshNode(
    val cache: CultCache = CultCache(),
    private val random: SecureRandom = SecureRandom(),
) {
    fun connect(uri: URI): CultNetWebSocketClient = CultNetWebSocketClient.connect(uri, random)

    fun connectTransport(uri: URI): CultNetWebSocketTransportConnection =
        CultNetWebSocketTransportConnection.connect(uri, random)

    fun <T : Any> remember(codec: CultDocumentCodec<T>, key: String, value: T) {
        cache.put(codec, key, value)
    }

    fun <T : Any> remember(document: CultDocumentDefinition<T>, key: String, value: T) {
        cache.put(document, key, value)
    }

    fun <T : Any> rememberGlobal(document: CultDocumentDefinition<T>, value: T) {
        cache.putGlobal(document, value)
    }

    fun <T : Any> recall(codec: CultDocumentCodec<T>, key: String): T? = cache.get(codec, key)

    fun <T : Any> recall(document: CultDocumentDefinition<T>, key: String): T? = cache.get(document, key)

    fun <T : Any> require(document: CultDocumentDefinition<T>, key: String): T = cache.getRequired(document, key)

    fun <T : Any> recallGlobal(document: CultDocumentDefinition<T>): T? = cache.getGlobal(document)

    fun <T : Any> requireGlobal(document: CultDocumentDefinition<T>): T = cache.getRequiredGlobal(document)

    fun <T : Any> forget(document: CultDocumentDefinition<T>, key: String): Boolean = cache.delete(document, key)

    fun <T : Any> forgetGlobal(document: CultDocumentDefinition<T>): Boolean = cache.deleteGlobal(document)

    fun createRawSnapshotResponse(
        request: CultNetMessage,
        storedAt: String = Instant.now().toString(),
        sourceRuntimeId: String? = null,
    ): CultNetMessage = cache.createRawSnapshotResponse(request, storedAt, sourceRuntimeId)

    fun applyRawDocumentPut(message: CultNetMessage): Any = cache.applyRawDocumentPut(message)

    fun applyRawSnapshotResponse(response: CultNetMessage): List<Any> = cache.applyRawSnapshotResponse(response)

    fun <T : Any> syncDocument(response: CultNetMessage, document: CultDocumentDefinition<T>, key: String): T =
        cache.syncDocument(response, document, key)

    fun <T : Any> reactiveDocument(
        document: CultDocumentDefinition<T>,
        key: String,
        options: CultReactiveDocumentOptions = CultReactiveDocumentOptions(),
    ): CultReactiveDocument<T> = cache.reactiveDocument(document, key, options)

    fun applyDocumentDelete(message: CultNetMessage): Boolean = cache.applyDocumentDelete(message)

    fun applyShardLogResponse(response: CultNetMessage): List<Any?> = cache.applyShardLogResponse(response)
}

object CultMesh {
    fun createNode(cache: CultCache = CultCache()): CultMeshNode = CultMeshNode(cache)

    fun startNode(cache: CultCache = CultCache()): CultMeshNode = createNode(cache)

    fun <T : Any> reactiveDocument(
        node: CultMeshNode,
        document: CultDocumentDefinition<T>,
        key: String,
        options: CultReactiveDocumentOptions = CultReactiveDocumentOptions(),
    ): CultReactiveDocument<T> = node.reactiveDocument(document, key, options)

    fun createVerseCatalog(): CultMeshVerseCatalog = CultMeshVerseCatalog()

    fun createPeerCatalog(): CultMeshPeerCatalog = CultMeshPeerCatalog()

    fun createAuthorityLeaseCatalog(): CultMeshAuthorityLeaseCatalog = CultMeshAuthorityLeaseCatalog()

    fun createStreamCatalog(): CultMeshStreamCatalog = CultMeshStreamCatalog()

    fun createSchemaCatalog(): CultNetSchemaCatalog = CultNetSchemaCatalog()

    fun createBuiltInSchemaCatalog(
        includeSchemaJson: Boolean = false,
        schemaIds: List<String> = emptyList(),
        kinds: List<String> = emptyList(),
    ): CultNetSchemaCatalog = cultNetBuiltInSchemaCatalog(
        includeSchemaJson = includeSchemaJson,
        schemaIds = schemaIds,
        kinds = kinds,
    )

    fun createShardCatalog(): CultNetShardCatalog = CultNetShardCatalog()

    fun createRudpServer(
        runtimeId: String,
        connectionId: Long,
        bindHost: String = "127.0.0.1",
        bindPort: Int = 0,
        tuning: CultNetRudpSocketTuning = CultNetRudpSocketTuning(),
    ): CultNetRudpSocketTransportConnection = cultNetRudpServer(
        runtimeId = runtimeId,
        connectionId = connectionId,
        bindHost = bindHost,
        bindPort = bindPort,
        tuning = tuning,
    )

    fun createRudpClient(
        runtimeId: String,
        connectionId: Long,
        remoteHost: String,
        remotePort: Int,
        bindHost: String = "127.0.0.1",
        bindPort: Int = 0,
        tuning: CultNetRudpSocketTuning = CultNetRudpSocketTuning(),
    ): CultNetRudpSocketTransportConnection = cultNetRudpClient(
        runtimeId = runtimeId,
        connectionId = connectionId,
        remoteHost = remoteHost,
        remotePort = remotePort,
        bindHost = bindHost,
        bindPort = bindPort,
        tuning = tuning,
    )

    fun parseRudpEndpoint(endpoint: String): CultNetRudpEndpoint = cultNetRudpEndpoint(endpoint)

    fun createRudpClient(
        runtimeId: String,
        connectionId: Long,
        endpoint: CultNetRudpEndpoint,
        bindHost: String = "127.0.0.1",
        bindPort: Int = 0,
        tuning: CultNetRudpSocketTuning = CultNetRudpSocketTuning(),
    ): CultNetRudpSocketTransportConnection = createRudpClient(
        runtimeId = runtimeId,
        connectionId = connectionId,
        remoteHost = endpoint.host,
        remotePort = endpoint.port,
        bindHost = bindHost,
        bindPort = bindPort,
        tuning = tuning,
    )

    fun createRudpClient(
        runtimeId: String,
        connectionId: Long,
        endpoint: String,
        bindHost: String = "127.0.0.1",
        bindPort: Int = 0,
        tuning: CultNetRudpSocketTuning = CultNetRudpSocketTuning(),
    ): CultNetRudpSocketTransportConnection = createRudpClient(
        runtimeId = runtimeId,
        connectionId = connectionId,
        endpoint = parseRudpEndpoint(endpoint),
        bindHost = bindHost,
        bindPort = bindPort,
        tuning = tuning,
    )

    fun createRudpClientForPeer(
        runtimeId: String,
        connectionId: Long,
        peer: CultMeshPeerCard,
        bindHost: String = "127.0.0.1",
        bindPort: Int = 0,
        tuning: CultNetRudpSocketTuning = CultNetRudpSocketTuning(),
    ): CultNetRudpSocketTransportConnection {
        val endpoint = peer.endpoints.firstOrNull { it.startsWith("rudp://", ignoreCase = true) }
            ?: throw IOException("Peer ${peer.peerId} does not advertise a RUDP endpoint")
        return createRudpClient(runtimeId, connectionId, endpoint, bindHost, bindPort, tuning)
    }

    fun createRudpClientForAuthorizedPeer(
        runtimeId: String,
        connectionId: Long,
        peers: CultMeshPeerCatalog,
        leases: CultMeshAuthorityLeaseCatalog,
        verseId: String,
        role: String,
        shardId: String? = null,
        at: Instant = Instant.now(),
        bindHost: String = "127.0.0.1",
        bindPort: Int = 0,
        tuning: CultNetRudpSocketTuning = CultNetRudpSocketTuning(),
    ): CultNetRudpSocketTransportConnection {
        val peer = peers.firstAuthorized(verseId, role, leases, shardId, at)
            ?: throw IOException("No authorized RUDP peer for role $role in Verse $verseId")
        return createRudpClientForPeer(runtimeId, connectionId, peer, bindHost, bindPort, tuning)
    }

    fun connectRudpClient(
        runtimeId: String,
        connectionId: Long,
        endpoint: CultNetRudpEndpoint,
        bindHost: String = "127.0.0.1",
        bindPort: Int = 0,
        tuning: CultNetRudpSocketTuning = CultNetRudpSocketTuning(),
        connectPayload: ByteArray = ByteArray(0),
        timeoutMs: Long = 1_000,
        pollIntervalMs: Long = 5,
    ): CultNetRudpSocketTransportConnection {
        val client = createRudpClient(runtimeId, connectionId, endpoint, bindHost, bindPort, tuning)
        if (!client.connectAndWait(connectPayload, timeoutMs, pollIntervalMs)) {
            client.close()
            throw IOException("Timed out waiting for RUDP client $runtimeId to connect")
        }
        return client
    }

    fun connectRudpClient(
        runtimeId: String,
        connectionId: Long,
        endpoint: String,
        bindHost: String = "127.0.0.1",
        bindPort: Int = 0,
        tuning: CultNetRudpSocketTuning = CultNetRudpSocketTuning(),
        connectPayload: ByteArray = ByteArray(0),
        timeoutMs: Long = 1_000,
        pollIntervalMs: Long = 5,
    ): CultNetRudpSocketTransportConnection = connectRudpClient(
        runtimeId = runtimeId,
        connectionId = connectionId,
        endpoint = parseRudpEndpoint(endpoint),
        bindHost = bindHost,
        bindPort = bindPort,
        tuning = tuning,
        connectPayload = connectPayload,
        timeoutMs = timeoutMs,
        pollIntervalMs = pollIntervalMs,
    )

    fun connectRudpClientForPeer(
        runtimeId: String,
        connectionId: Long,
        peer: CultMeshPeerCard,
        bindHost: String = "127.0.0.1",
        bindPort: Int = 0,
        tuning: CultNetRudpSocketTuning = CultNetRudpSocketTuning(),
        connectPayload: ByteArray = ByteArray(0),
        timeoutMs: Long = 1_000,
        pollIntervalMs: Long = 5,
    ): CultNetRudpSocketTransportConnection {
        val endpoint = peer.endpoints.firstOrNull { it.startsWith("rudp://", ignoreCase = true) }
            ?: throw IOException("Peer ${peer.peerId} does not advertise a RUDP endpoint")
        return connectRudpClient(
            runtimeId,
            connectionId,
            endpoint,
            bindHost,
            bindPort,
            tuning,
            connectPayload,
            timeoutMs,
            pollIntervalMs,
        )
    }

    fun connectRudpClientForAuthorizedPeer(
        runtimeId: String,
        connectionId: Long,
        peers: CultMeshPeerCatalog,
        leases: CultMeshAuthorityLeaseCatalog,
        verseId: String,
        role: String,
        shardId: String? = null,
        at: Instant = Instant.now(),
        bindHost: String = "127.0.0.1",
        bindPort: Int = 0,
        tuning: CultNetRudpSocketTuning = CultNetRudpSocketTuning(),
        connectPayload: ByteArray = ByteArray(0),
        timeoutMs: Long = 1_000,
        pollIntervalMs: Long = 5,
    ): CultNetRudpSocketTransportConnection {
        val peer = peers.firstAuthorized(verseId, role, leases, shardId, at)
            ?: throw IOException("No authorized RUDP peer for role $role in Verse $verseId")
        return connectRudpClientForPeer(
            runtimeId,
            connectionId,
            peer,
            bindHost,
            bindPort,
            tuning,
            connectPayload,
            timeoutMs,
            pollIntervalMs,
        )
    }

    fun createRudpReconnectLoop(
        reconnectPolicy: CultNetReconnectPolicy = createReconnectPolicy(),
        connectPayload: ByteArray = ByteArray(0),
        createTransport: () -> CultNetRudpSocketTransportConnection,
    ): CultNetRudpReconnectLoop = CultNetRudpReconnectLoop(
        reconnectPolicy = reconnectPolicy,
        connectPayload = connectPayload,
        createTransport = createTransport,
    )
}

data class CultNetFrame(val opcode: Int, val payload: ByteArray)

data class CultNetTransportStats(
    val bytesReceived: Long = 0,
    val bytesSent: Long = 0,
    val framesReceived: Long = 0,
    val framesSent: Long = 0,
)

data class CultNetTransportFrame(val channelId: String, val payload: ByteArray)

interface CultNetSchemaMessageTransport : AutoCloseable {
    val profile: CultNetTransportProfile
    val stats: CultNetTransportStats

    fun sendSchemaMessage(message: CultNetMessage)

    fun receiveSchemaMessage(timeoutMs: Long = 1_000, pollIntervalMs: Long = 5): CultNetMessage?
}

data class CultNetRudpEndpoint(val host: String, val port: Int) {
    val uri: String get() = "rudp://$host:$port"
}

data class CultNetReconnectPolicy(
    val schemaVersion: String = "cultnet.reconnect_policy.v0",
    val policyId: String = "default",
    val baseDelayMs: Long = 1_000,
    val maxDelayMs: Long = 30_000,
    val maxJitterMs: Long = 250,
    val maxAttempts: Int? = null,
) {
    fun toWireMap(): Map<String, Any?> {
        val wire = linkedMapOf<String, Any?>(
            "schemaVersion" to schemaVersion,
            "policyId" to policyId,
            "baseDelayMs" to baseDelayMs,
            "maxDelayMs" to maxDelayMs,
            "maxJitterMs" to maxJitterMs,
        )
        if (maxAttempts != null) wire["maxAttempts"] = maxAttempts
        return wire
    }
}

fun createReconnectPolicy(
    policyId: String = "default",
    baseDelayMs: Long = 1_000,
    maxDelayMs: Long = 30_000,
    maxJitterMs: Long = 250,
    maxAttempts: Int? = null,
): CultNetReconnectPolicy = CultNetReconnectPolicy(
    policyId = policyId.ifBlank { "default" },
    baseDelayMs = baseDelayMs,
    maxDelayMs = maxDelayMs,
    maxJitterMs = maxJitterMs,
    maxAttempts = maxAttempts,
)

fun computeReconnectDelayMs(policy: CultNetReconnectPolicy, attempt: Int, jitterMs: Long = 0): Long {
    val normalizedAttempt = attempt.coerceAtLeast(1)
    val exponent = (normalizedAttempt - 1).coerceAtMost(62)
    val multiplier = 1L shl exponent
    val base = policy.baseDelayMs.coerceAtLeast(0)
    val cappedBaseDelay = if (base == 0L) {
        0L
    } else {
        val maxMultiplier = Long.MAX_VALUE / base
        val product = if (multiplier > maxMultiplier) Long.MAX_VALUE else base * multiplier
        product.coerceAtMost(policy.maxDelayMs)
    }
    val boundedJitter = jitterMs.coerceIn(0, policy.maxJitterMs)
    return cappedBaseDelay + boundedJitter
}

data class CultNetReconnectDecision(
    val attempt: Int,
    val shouldRetry: Boolean,
    val delayMs: Long = 0,
    val nextAttemptAtMs: Long? = null,
    val exhausted: Boolean = false,
)

class CultNetReconnectController(
    val policy: CultNetReconnectPolicy = createReconnectPolicy(),
) {
    var attempt: Int = 0
        private set
    var nextAttemptAtMs: Long? = null
        private set
    var exhausted: Boolean = false
        private set

    fun reset() {
        attempt = 0
        nextAttemptAtMs = null
        exhausted = false
    }

    fun canAttempt(nowMs: Long): Boolean {
        val next = nextAttemptAtMs
        return !exhausted && (next == null || nowMs >= next)
    }

    fun recordFailure(nowMs: Long, jitterMs: Long = 0): CultNetReconnectDecision {
        val nextAttempt = attempt + 1
        if (policy.maxAttempts != null && nextAttempt > policy.maxAttempts) {
            exhausted = true
            nextAttemptAtMs = null
            return CultNetReconnectDecision(
                attempt = attempt,
                shouldRetry = false,
                exhausted = true,
            )
        }

        attempt = nextAttempt
        val delay = computeReconnectDelayMs(policy, attempt, jitterMs)
        val nextAt = nowMs + delay
        nextAttemptAtMs = nextAt
        return CultNetReconnectDecision(
            attempt = attempt,
            shouldRetry = true,
            delayMs = delay,
            nextAttemptAtMs = nextAt,
        )
    }
}

class CultNetRudpReconnectLoop(
    reconnectPolicy: CultNetReconnectPolicy = createReconnectPolicy(),
    private val connectPayload: ByteArray = ByteArray(0),
    private val createTransport: () -> CultNetRudpSocketTransportConnection,
    private val nowMsProvider: () -> Long = { nowMs() },
    private val jitterMsProvider: () -> Long = { 0 },
    private val scheduler: (delayMs: Long, callback: () -> Unit) -> AutoCloseable = { delayMs, callback ->
        val thread = Thread {
            try {
                Thread.sleep(delayMs)
                callback()
            } catch (_: InterruptedException) {
                // Timer cancelled.
            }
        }
        thread.isDaemon = true
        thread.start()
        AutoCloseable { thread.interrupt() }
    },
) {
    val reconnectController = CultNetReconnectController(reconnectPolicy)
    var transport: CultNetRudpSocketTransportConnection? = null
        private set
    private var timer: AutoCloseable? = null
    private var stopped = true

    fun start(): CultNetRudpSocketTransportConnection {
        stopped = false
        reconnectController.reset()
        return openTransport()
    }

    fun stop() {
        stopped = true
        timer?.close()
        timer = null
        transport?.close()
        transport = null
        reconnectController.reset()
    }

    fun markConnected() {
        reconnectController.reset()
    }

    fun handleClosed(): CultNetReconnectDecision? {
        transport = null
        return scheduleReconnect()
    }

    private fun openTransport(): CultNetRudpSocketTransportConnection {
        val next = createTransport()
        transport = next
        next.connect(connectPayload)
        return next
    }

    private fun scheduleReconnect(): CultNetReconnectDecision? {
        if (stopped || timer != null) return null
        val decision = reconnectController.recordFailure(nowMsProvider(), jitterMsProvider())
        if (!decision.shouldRetry) return decision
        timer = scheduler(decision.delayMs) {
            timer = null
            if (!stopped && reconnectController.canAttempt(nowMsProvider())) {
                openTransport()
            }
        }
        return decision
    }
}

data class CultNetTransportProfile(
    val schemaVersion: String = "cultnet.transport_profile.v0",
    val runtimeId: String,
    val transports: List<CultNetTransportDescriptor>,
)

data class CultNetTransportDescriptor(
    val transportId: String,
    val protocol: String,
    val host: String? = null,
    val port: Int? = null,
    val wireContracts: List<String> = listOf("cultnet.schema.v0"),
    val reconnectPolicy: CultNetReconnectPolicy? = null,
    val channels: List<CultNetTransportChannel>,
)

data class CultNetTransportChannel(
    val channelId: String,
    val delivery: String,
    val ordering: String,
    val maxPayloadBytes: Int? = null,
    val maxFragmentBytes: Int? = null,
    val maxPendingReliablePackets: Int? = null,
)

fun createRudpTransportProfile(
    runtimeId: String,
    transportId: String = "rudp",
    host: String? = null,
    port: Int? = null,
    maxPayloadBytes: Int? = null,
    maxFragmentBytes: Int? = null,
    maxPendingReliablePackets: Int? = null,
    reconnectPolicy: CultNetReconnectPolicy = createReconnectPolicy(),
): CultNetTransportProfile = CultNetTransportProfile(
    runtimeId = runtimeId,
    transports = listOf(
        CultNetTransportDescriptor(
            transportId = transportId.ifBlank { "rudp" },
            protocol = "rudp",
            host = host,
            port = port,
            reconnectPolicy = reconnectPolicy,
            channels = listOf(
                CultNetTransportChannel("schema", "reliable", "ordered", maxPayloadBytes, maxFragmentBytes, maxPendingReliablePackets),
                CultNetTransportChannel("latest", "unreliable", "sequenced", maxPayloadBytes, maxFragmentBytes, maxPendingReliablePackets),
                CultNetTransportChannel("realtime", "unreliable", "unordered", maxPayloadBytes, maxFragmentBytes, maxPendingReliablePackets),
            ),
        ),
    ),
)

fun createWebSocketTransportProfile(
    runtimeId: String,
    transportId: String = "websocket",
    host: String? = null,
    port: Int? = null,
    maxPayloadBytes: Int? = null,
): CultNetTransportProfile = CultNetTransportProfile(
    runtimeId = runtimeId,
    transports = listOf(
        CultNetTransportDescriptor(
            transportId = transportId.ifBlank { "websocket" },
            protocol = "websocket",
            host = host,
            port = port,
            wireContracts = listOf("cultnet.schema.v0"),
            channels = listOf(
                CultNetTransportChannel("schema", "reliable", "ordered", maxPayloadBytes),
            ),
        ),
    ),
)

data class CultNetMessage(
    val schemaVersion: String,
    val body: Map<String, Any?> = emptyMap(),
) {
    fun toWireMap(): Map<String, Any?> {
        val wire = linkedMapOf<String, Any?>("schemaVersion" to schemaVersion)
        wire.putAll(body)
        return wire
    }

    fun toBytes(): ByteArray = encodeCultNetMessage(this)
}

data class CultNetRawDocumentRecord(
    val schemaId: String,
    val recordKey: String,
    val storedAt: String,
    val payload: ByteArray,
    val payloadEncoding: String = "messagepack",
    val sourceRuntimeId: String? = null,
    val sourceAgentId: String? = null,
    val sourceRole: String? = null,
    val tags: List<String> = emptyList(),
) {
    fun toWireMap(): Map<String, Any?> {
        val wire = linkedMapOf<String, Any?>(
            "schemaId" to schemaId,
            "recordKey" to recordKey,
            "storedAt" to storedAt,
            "payloadEncoding" to payloadEncoding,
            "payload" to payload.copyOf(),
        )
        if (!sourceRuntimeId.isNullOrBlank()) wire["sourceRuntimeId"] = sourceRuntimeId
        if (!sourceAgentId.isNullOrBlank()) wire["sourceAgentId"] = sourceAgentId
        if (!sourceRole.isNullOrBlank()) wire["sourceRole"] = sourceRole
        if (tags.isNotEmpty()) wire["tags"] = tags
        return wire
    }
}

data class CultNetSchemaDescriptor(
    val schemaId: String,
    val kind: String,
    val schemaVersion: String? = null,
    val documentType: String? = null,
    val title: String? = null,
    val wireContracts: List<String> = listOf("cultnet.schema.v0"),
    val contentHash: String,
    val schemaJson: String? = null,
) {
    fun toWireMap(includeSchemaJson: Boolean = schemaJson != null): Map<String, Any?> {
        val wire = linkedMapOf<String, Any?>(
            "schemaId" to schemaId,
            "kind" to kind,
            "wireContracts" to wireContracts,
            "contentHash" to contentHash,
        )
        if (!schemaVersion.isNullOrBlank()) wire["schemaVersion"] = schemaVersion
        if (!documentType.isNullOrBlank()) wire["documentType"] = documentType
        if (!title.isNullOrBlank()) wire["title"] = title
        if (includeSchemaJson && schemaJson != null) wire["schemaJson"] = schemaJson
        return wire
    }
}

data class CultNetShardDescriptor(
    val shardId: String,
    val ownerRuntimeId: String,
    val epoch: Long = 0,
    val isPrimary: Boolean = false,
    val schemaIds: List<String> = emptyList(),
    val keyPrefix: String? = null,
    val primaryEndpoints: List<String> = emptyList(),
    val replicaEndpoints: List<String> = emptyList(),
    val readReplicaEndpoints: List<String> = emptyList(),
    val region: String? = null,
    val authorityLeaseId: String? = null,
) {
    fun serves(schemaId: String? = null, recordKey: String? = null): Boolean =
        (schemaId == null || schemaIds.isEmpty() || schemaId in schemaIds) &&
            (recordKey == null || keyPrefix.isNullOrBlank() || recordKey.startsWith(keyPrefix))

    fun toWireMap(): Map<String, Any?> {
        val wire = linkedMapOf<String, Any?>(
            "shardId" to shardId,
            "ownerRuntimeId" to ownerRuntimeId,
            "epoch" to epoch,
            "isPrimary" to isPrimary,
            "schemaIds" to schemaIds,
            "primaryEndpoints" to primaryEndpoints,
            "replicaEndpoints" to replicaEndpoints,
            "readReplicaEndpoints" to readReplicaEndpoints,
        )
        if (!keyPrefix.isNullOrBlank()) wire["keyPrefix"] = keyPrefix
        if (!region.isNullOrBlank()) wire["region"] = region
        if (!authorityLeaseId.isNullOrBlank()) wire["authorityLeaseId"] = authorityLeaseId
        return wire
    }
}

data class CultNetShardLogEntry(
    val sequence: Long,
    val changeKind: String,
    val put: CultNetMessage? = null,
    val delete: CultNetMessage? = null,
    val committedAt: String? = null,
) {
    fun toWireMap(): Map<String, Any?> {
        if (sequence <= 0) throw IOException("shard log entry sequence must be positive")
        if (changeKind !in setOf("added", "updated", "removed")) throw IOException("unsupported shard log changeKind $changeKind")
        val wire = linkedMapOf<String, Any?>(
            "sequence" to sequence,
            "changeKind" to changeKind,
        )
        if (put != null) wire["put"] = put.toWireMap()
        if (delete != null) wire["delete"] = delete.toWireMap()
        if (!committedAt.isNullOrBlank()) wire["committedAt"] = committedAt
        return wire
    }
}

data class CultNetShardLogResponse(
    val messageId: String,
    val shardId: String,
    val shardEpoch: Long,
    val entries: List<CultNetShardLogEntry>,
    val resyncRequired: Boolean = false,
    val reason: String? = null,
    val compactedThrough: Long? = null,
) {
    val lastSequence: Long
        get() = entries.maxOfOrNull { it.sequence } ?: compactedThrough ?: 0

    fun requireUsable(): CultNetShardLogResponse {
        if (resyncRequired) throw IOException("Shard log response requires resync: ${reason ?: "unspecified"}")
        return this
    }

    fun toMessage(): CultNetMessage {
        val body = linkedMapOf<String, Any?>(
            "messageId" to messageId,
            "shardId" to shardId,
            "shardEpoch" to shardEpoch,
            "entries" to entries.map { it.toWireMap() },
            "resyncRequired" to resyncRequired,
        )
        if (!reason.isNullOrBlank()) body["reason"] = reason
        if (compactedThrough != null) body["compactedThrough"] = compactedThrough
        return CultNetMessage("cultnet.shard_log_response.v0", body)
    }
}

data class CultNetShardReplicaCursor(
    val shardId: String,
    val shardEpoch: Long,
    val lastAppliedSequence: Long,
    val updatedAt: String,
)

class CultNetInMemoryShardReplicaCursorStore {
    private val cursors = linkedMapOf<String, CultNetShardReplicaCursor>()

    fun read(shardId: String): CultNetShardReplicaCursor? {
        requireNonBlank(shardId, "shardId")
        return cursors[shardId]
    }

    fun write(cursor: CultNetShardReplicaCursor) {
        requireNonBlank(cursor.shardId, "cursor.shardId")
        cursors[cursor.shardId] = cursor
    }
}

class CultNetSchemaCatalog {
    private val descriptorsBySchemaId = linkedMapOf<String, CultNetSchemaDescriptor>()
    private val subscribers = mutableListOf<(CultNetSchemaDescriptor) -> Unit>()

    val schemas: List<CultNetSchemaDescriptor>
        get() = descriptorsBySchemaId.toSortedMap().values.toList()

    fun watch(callback: (CultNetSchemaDescriptor) -> Unit): () -> Unit {
        subscribers.add(callback)
        return { subscribers.remove(callback) }
    }

    fun upsert(descriptor: CultNetSchemaDescriptor): CultNetSchemaDescriptor {
        requireNonBlank(descriptor.schemaId, "schema.schemaId")
        requireNonBlank(descriptor.kind, "schema.kind")
        if (descriptor.wireContracts.isEmpty()) throw IOException("schema.wireContracts must not be empty")
        descriptorsBySchemaId[descriptor.schemaId] = descriptor
        subscribers.toList().forEach { it(descriptor) }
        return descriptor
    }

    fun get(schemaId: String): CultNetSchemaDescriptor? {
        requireNonBlank(schemaId, "schemaId")
        return descriptorsBySchemaId[schemaId]
    }

    fun list(
        schemaIds: List<String> = emptyList(),
        kinds: List<String> = emptyList(),
        includeSchemaJson: Boolean = false,
    ): List<CultNetSchemaDescriptor> {
        val requestedIds = schemaIds.toSet()
        val requestedKinds = kinds.toSet()
        return schemas.filter { descriptor ->
            (requestedIds.isEmpty() || descriptor.schemaId in requestedIds) &&
                (requestedKinds.isEmpty() || descriptor.kind in requestedKinds)
        }.map { descriptor ->
            if (includeSchemaJson) descriptor else descriptor.copy(schemaJson = null)
        }
    }

    fun createResponse(request: CultNetMessage): CultNetMessage {
        require(request.schemaVersion == "cultnet.schema_catalog_request.v0") {
            "Expected cultnet.schema_catalog_request.v0, received ${request.schemaVersion}"
        }
        val includeSchemaJson = request.body["includeSchemaJson"] as? Boolean ?: false
        return cultNetSchemaCatalogResponse(
            messageId = request.body["messageId"] as? String ?: "",
            schemas = list(
                schemaIds = stringList(request.body["schemaIds"]),
                kinds = stringList(request.body["kinds"]),
                includeSchemaJson = includeSchemaJson,
            ),
            includeSchemaJson = includeSchemaJson,
        )
    }

    fun applyResponse(response: CultNetMessage): List<CultNetSchemaDescriptor> {
        require(response.schemaVersion == "cultnet.schema_catalog_response.v0") {
            "Expected cultnet.schema_catalog_response.v0, received ${response.schemaVersion}"
        }
        val applied = mapList(response.body["schemas"]).map { schemaDescriptorFromWire(it) }
        applied.forEach { upsert(it) }
        return applied
    }
}

class CultNetShardCatalog {
    private val shardsById = linkedMapOf<String, CultNetShardDescriptor>()
    private val subscribers = mutableListOf<(CultNetShardDescriptor) -> Unit>()

    val shards: List<CultNetShardDescriptor>
        get() = shardsById.toSortedMap().values.toList()

    fun watch(callback: (CultNetShardDescriptor) -> Unit): () -> Unit {
        subscribers.add(callback)
        return { subscribers.remove(callback) }
    }

    fun upsert(descriptor: CultNetShardDescriptor): CultNetShardDescriptor {
        requireNonBlank(descriptor.shardId, "shard.shardId")
        requireNonBlank(descriptor.ownerRuntimeId, "shard.ownerRuntimeId")
        shardsById[descriptor.shardId] = descriptor
        subscribers.toList().forEach { it(descriptor) }
        return descriptor
    }

    fun get(shardId: String): CultNetShardDescriptor? {
        requireNonBlank(shardId, "shardId")
        return shardsById[shardId]
    }

    fun list(schemaIds: List<String> = emptyList(), recordKeys: List<String> = emptyList()): List<CultNetShardDescriptor> {
        val requestedSchemas = schemaIds.toSet()
        return shards.filter { shard ->
            (requestedSchemas.isEmpty() || requestedSchemas.any { shard.serves(schemaId = it) }) &&
                (recordKeys.isEmpty() || recordKeys.any { shard.serves(recordKey = it) })
        }
    }

    fun createResponse(request: CultNetMessage): CultNetMessage {
        require(request.schemaVersion == "cultnet.shard_catalog_request.v0") {
            "Expected cultnet.shard_catalog_request.v0, received ${request.schemaVersion}"
        }
        return cultNetShardCatalogResponse(
            messageId = request.body["messageId"] as? String ?: "",
            shards = list(
                schemaIds = stringList(request.body["schemaIds"]),
                recordKeys = stringList(request.body["recordKeys"]),
            ),
        )
    }

    fun applyResponse(response: CultNetMessage): List<CultNetShardDescriptor> {
        require(response.schemaVersion == "cultnet.shard_catalog_response.v0") {
            "Expected cultnet.shard_catalog_response.v0, received ${response.schemaVersion}"
        }
        val applied = mapList(response.body["shards"]).map { shardDescriptorFromWire(it) }
        applied.forEach { upsert(it) }
        return applied
    }
}

fun defineCultNetSchemaDescriptor(
    schemaId: String,
    kind: String,
    schemaVersion: String? = null,
    documentType: String? = null,
    title: String? = null,
    wireContracts: List<String> = listOf("cultnet.schema.v0"),
    schemaJson: String? = null,
    contentHash: String? = null,
): CultNetSchemaDescriptor = CultNetSchemaDescriptor(
    schemaId = schemaId,
    kind = kind,
    schemaVersion = schemaVersion,
    documentType = documentType,
    title = title,
    wireContracts = wireContracts,
    contentHash = contentHash ?: sha256Hex(schemaJson ?: schemaId),
    schemaJson = schemaJson,
)

private const val cultNetSchemaBase = "https://github.com/GameCult/cultnet-ts/contracts"

private data class CultNetBuiltInSchemaSpec(
    val schemaVersion: String,
    val title: String,
    val schemaId: String,
    val kind: String = "wire_message",
    val wireContracts: List<String> = listOf("cultnet.schema.v0"),
    val requiredFields: List<String> = listOf("schemaVersion", "messageId"),
    val contentHash: String? = null,
)

private val cultNetBuiltInSchemaSpecs = listOf(
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultnet.hello.v0",
        title = "CultNet Hello Message",
        schemaId = "$cultNetSchemaBase/cultnet.hello.schema.json",
        requiredFields = listOf("schemaVersion", "runtimeId", "runtimeKind"),
        contentHash = "1d2114bf0c92a2dd5e07dd735778ceeb939402c6424550d6d281d9db423f56b3",
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultnet.document_delete.v0",
        title = "CultNet Document Delete Message",
        schemaId = "$cultNetSchemaBase/cultnet.document-delete.schema.json",
        requiredFields = listOf("schemaVersion", "messageId", "schemaId", "recordKey"),
        contentHash = "037f7cf1b30756904e0bfada6bcff486269378997afbe3c4b6fc5c573fc8a4df",
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultnet.document_put_raw.v0",
        title = "CultNet Raw Document Put Message",
        schemaId = "$cultNetSchemaBase/cultnet.document-put-raw.schema.json",
        contentHash = "b61e7e6cce8508653fbeccc4d956ef211c853a174d67c7e1903ce294e15985a2",
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultnet.snapshot_request.v0",
        title = "CultNet Snapshot Request Message",
        schemaId = "$cultNetSchemaBase/cultnet.snapshot-request.schema.json",
        contentHash = "9f6546f51a1aab85978554e6908d7a59e96b3329feff7b35022be210495dd721",
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultnet.snapshot_response_raw.v0",
        title = "CultNet Raw Snapshot Response Message",
        schemaId = "$cultNetSchemaBase/cultnet.snapshot-response-raw.schema.json",
        contentHash = "0dbca3338a3ae5d4011aed2c973c75b9b47b6b9dfc0874459ac56da01fef4582",
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultnet.schema_catalog_request.v0",
        title = "CultNet Schema Catalog Request Message",
        schemaId = "$cultNetSchemaBase/cultnet.schema-catalog-request.schema.json",
        wireContracts = listOf("cultnet.schema.v0", "gamecult.networking.v0"),
        contentHash = "bdd49b44428daf24b514c521e53027ec944aecfebb39ca0d10967661c303e096",
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultnet.schema_catalog_response.v0",
        title = "CultNet Schema Catalog Response Message",
        schemaId = "$cultNetSchemaBase/cultnet.schema-catalog-response.schema.json",
        wireContracts = listOf("cultnet.schema.v0", "gamecult.networking.v0"),
        contentHash = "b158de53d1ca614eacf2e05767122fb4b9e06504c037ad76c649cffdf71dd648",
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultnet.shard_catalog_request.v0",
        title = "CultNet Shard Catalog Request Message",
        schemaId = "cultnet.shard_catalog_request.v0",
        contentHash = "0abd131a1db12f6731010333b86e104d550160bd58f539b18413a2b61198fa38",
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultnet.shard_catalog_response.v0",
        title = "CultNet Shard Catalog Response Message",
        schemaId = "cultnet.shard_catalog_response.v0",
        contentHash = "93a0e90ac81bc6ab21b68925ad25e381edfac8773232333679d3275141cff37b",
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultnet.shard_log_request.v0",
        title = "CultNet Shard Log Request Message",
        schemaId = "cultnet.shard_log_request.v0",
        requiredFields = listOf("schemaVersion", "messageId", "shardId"),
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultnet.shard_log_response.v0",
        title = "CultNet Shard Log Response Message",
        schemaId = "cultnet.shard_log_response.v0",
        requiredFields = listOf("schemaVersion", "messageId", "shardId", "shardEpoch", "entries"),
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultmesh.verse_catalog_request.v0",
        title = "CultMesh Verse Catalog Request Message",
        schemaId = "cultmesh.verse_catalog_request.v0",
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultmesh.verse_catalog_response.v0",
        title = "CultMesh Verse Catalog Response Message",
        schemaId = "cultmesh.verse_catalog_response.v0",
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultmesh.peer_exchange_request.v0",
        title = "CultMesh Peer Exchange Request Message",
        schemaId = "cultmesh.peer_exchange_request.v0",
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultmesh.peer_exchange_response.v0",
        title = "CultMesh Peer Exchange Response Message",
        schemaId = "cultmesh.peer_exchange_response.v0",
    ),
    CultNetBuiltInSchemaSpec(
        schemaVersion = "cultnet.transport_profile.v0",
        title = "CultNet Transport Profile",
        schemaId = "$cultNetSchemaBase/cultnet.transport-profile.schema.json",
        kind = "shared_contract",
        requiredFields = listOf("schemaVersion", "runtimeId", "transports"),
        contentHash = "84473264f543ca3443267999e43d72b7993b61b7b2ac53c642c9cbe8b6de0217",
    ),
)

fun cultNetBuiltInSchemaCatalog(
    includeSchemaJson: Boolean = false,
    schemaIds: List<String> = emptyList(),
    kinds: List<String> = emptyList(),
): CultNetSchemaCatalog {
    val catalog = CultNetSchemaCatalog()
    val requestedSchemaIds = schemaIds.toSet()
    val requestedKinds = kinds.toSet()
    cultNetBuiltInSchemaSpecs.filter { spec ->
        (requestedSchemaIds.isEmpty() || spec.schemaId in requestedSchemaIds) &&
            (requestedKinds.isEmpty() || spec.kind in requestedKinds)
    }.forEach { spec ->
        val schemaJson = cultNetBuiltInSchemaJson(spec, spec.schemaId)
        val inlineSchemaJson = if (spec.contentHash == null && includeSchemaJson) schemaJson else null
        catalog.upsert(
            defineCultNetSchemaDescriptor(
                schemaId = spec.schemaId,
                kind = spec.kind,
                schemaVersion = spec.schemaVersion,
                title = spec.title,
                wireContracts = spec.wireContracts,
                contentHash = spec.contentHash ?: sha256Hex(schemaJson),
                schemaJson = inlineSchemaJson,
            ),
        )
    }
    return catalog
}

private fun cultNetBuiltInSchemaJson(spec: CultNetBuiltInSchemaSpec, schemaId: String): String =
    "{" +
        "\"\$schema\":\"https://json-schema.org/draft/2020-12/schema\"," +
        "\"\$id\":\"${jsonEscape(schemaId)}\"," +
        "\"title\":\"${jsonEscape(spec.title)}\"," +
        "\"type\":\"object\"," +
        "\"required\":${jsonStringArray(spec.requiredFields)}," +
        "\"additionalProperties\":true," +
        "\"properties\":{\"schemaVersion\":{\"const\":\"${jsonEscape(spec.schemaVersion)}\"}}" +
        "}"

private fun jsonStringArray(values: List<String>): String =
    values.joinToString(prefix = "[", postfix = "]") { "\"${jsonEscape(it)}\"" }

private fun jsonEscape(value: String): String = buildString {
    value.forEach { char ->
        when (char) {
            '\\' -> append("\\\\")
            '"' -> append("\\\"")
            '\b' -> append("\\b")
            '\u000C' -> append("\\f")
            '\n' -> append("\\n")
            '\r' -> append("\\r")
            '\t' -> append("\\t")
            else -> append(char)
        }
    }
}

data class CultMeshVerseCompatibility(
    val transportVersion: String,
    val rulesHash: String,
    val compatibleVerseIds: List<String> = emptyList(),
    val requiredPluginIds: List<String> = emptyList(),
    val optionalPluginIds: List<String> = emptyList(),
) {
    fun toWireMap(): Map<String, Any?> = linkedMapOf(
        "transportVersion" to transportVersion,
        "rulesHash" to rulesHash,
        "compatibleVerseIds" to compatibleVerseIds,
        "requiredPluginIds" to requiredPluginIds,
        "optionalPluginIds" to optionalPluginIds,
    )
}

data class CultMeshVerseDescriptor(
    val verseId: String,
    val displayName: String,
    val authorityModel: String,
    val compatibility: CultMeshVerseCompatibility,
    val discoveryEndpoints: List<String> = emptyList(),
    val authorityRuntimeIds: List<String> = emptyList(),
    val parentVerseId: String? = null,
    val description: String? = null,
) {
    fun toWireMap(): Map<String, Any?> = linkedMapOf(
        "verseId" to verseId,
        "displayName" to displayName,
        "authorityModel" to authorityModel,
        "compatibility" to compatibility.toWireMap(),
        "discoveryEndpoints" to discoveryEndpoints,
        "authorityRuntimeIds" to authorityRuntimeIds,
        "parentVerseId" to parentVerseId,
        "description" to description,
    )

    fun canTransferFrom(source: CultMeshVerseDescriptor): Boolean =
        compatibility.transportVersion == source.compatibility.transportVersion &&
            (compatibility.rulesHash == source.compatibility.rulesHash || source.verseId in compatibility.compatibleVerseIds)
}

data class CultMeshPeerCard(
    val peerId: String,
    val verseId: String,
    val endpoints: List<String>,
    val roles: List<String> = emptyList(),
    val shardIds: List<String> = emptyList(),
    val region: String? = null,
    val authorityLeaseId: String? = null,
    val expiresAt: String? = null,
    val signature: String? = null,
) {
    fun toWireMap(): Map<String, Any?> = linkedMapOf(
        "peerId" to peerId,
        "verseId" to verseId,
        "endpoints" to endpoints,
        "roles" to roles,
        "shardIds" to shardIds,
        "region" to region,
        "authorityLeaseId" to authorityLeaseId,
        "expiresAt" to expiresAt,
        "signature" to signature,
    )

    fun hasRole(role: String): Boolean {
        requireNonBlank(role, "role")
        return role in roles
    }
}

data class CultMeshAuthorityLease(
    val leaseId: String,
    val verseId: String,
    val peerId: String,
    val roles: List<String>,
    val validFrom: Instant,
    val expiresAt: Instant,
    val shardIds: List<String> = emptyList(),
    val issuerRuntimeId: String,
    val signature: String? = null,
) {
    init {
        requireNonBlank(leaseId, "leaseId")
        requireNonBlank(verseId, "verseId")
        requireNonBlank(peerId, "peerId")
        requireNonBlank(issuerRuntimeId, "issuerRuntimeId")
        if (!expiresAt.isAfter(validFrom)) throw IOException("CultMesh authority lease expiry must be after validFrom")
    }

    fun isValidAt(at: Instant): Boolean = !at.isBefore(validFrom) && at.isBefore(expiresAt)

    fun covers(peer: CultMeshPeerCard, role: String, shardId: String? = null, at: Instant = Instant.now()): Boolean {
        requireNonBlank(role, "role")
        return isValidAt(at) &&
            verseId == peer.verseId &&
            peerId == peer.peerId &&
            leaseId == peer.authorityLeaseId &&
            role in roles &&
            peer.hasRole(role) &&
            (shardId.isNullOrBlank() || shardIds.isEmpty() || shardId in shardIds)
    }
}

class CultMeshAuthorityLeaseCatalog {
    private val knownLeases = linkedMapOf<String, CultMeshAuthorityLease>()
    private val subscribers = mutableListOf<(CultMeshAuthorityLease) -> Unit>()

    val leases: List<CultMeshAuthorityLease>
        get() = knownLeases.toSortedMap().values.toList()

    fun watch(callback: (CultMeshAuthorityLease) -> Unit): () -> Unit {
        subscribers.add(callback)
        return { subscribers.remove(callback) }
    }

    fun upsert(lease: CultMeshAuthorityLease) {
        knownLeases[lease.leaseId] = lease
        subscribers.toList().forEach { it(lease) }
    }

    fun get(leaseId: String): CultMeshAuthorityLease? {
        requireNonBlank(leaseId, "leaseId")
        return knownLeases[leaseId]
    }

    fun isAuthorized(peer: CultMeshPeerCard, role: String, shardId: String? = null, at: Instant = Instant.now()): Boolean {
        requireNonBlank(role, "role")
        val leaseId = peer.authorityLeaseId ?: return false
        val lease = knownLeases[leaseId] ?: return false
        return lease.covers(peer, role, shardId, at)
    }
}

fun createAuthorityLeaseCatalog(): CultMeshAuthorityLeaseCatalog = CultMeshAuthorityLeaseCatalog()

object CultMeshStreamKinds {
    const val Audio = "audio"
    const val Video = "video"
    const val Tensor = "tensor"
    const val Bytes = "bytes"
}

object CultMeshStreamBodyTransports {
    const val SharedMemory = "shared-memory"
    const val SharedD3D12Texture = "shared-d3d12-texture"
    const val SharedD3D11Texture = "shared-d3d11-texture"
    const val DmaBuf = "dma-buf"
    const val IOSurface = "iosurface"
    const val AHardwareBuffer = "ahardwarebuffer"
    const val CultCachePage = "cultcache-page"
    const val InlineBytes = "inline-bytes"
}

data class CultMeshStreamClock(
    val clockDomainId: String,
    val sourceId: String? = null,
    val sampleRate: Int? = null,
    val offsetToVerseTimeNs: Long? = null,
    val confidence: Double? = null,
    val evidenceKind: String? = null,
) {
    init {
        requireNonBlank(clockDomainId, "clock.clockDomainId")
    }
}

data class CultMeshAudioStreamFormat(
    val sampleRate: Int,
    val channels: Int,
    val sampleFormat: String,
    val framesPerPacket: Int? = null,
) {
    init {
        if (sampleRate <= 0) throw IOException("audio.sampleRate must be greater than zero")
        if (channels <= 0) throw IOException("audio.channels must be greater than zero")
        requireNonBlank(sampleFormat, "audio.sampleFormat")
    }
}

data class CultMeshVideoStreamFormat(
    val width: Int,
    val height: Int,
    val pixelFormat: String,
    val framesPerSecond: Double? = null,
    val planeCount: Int? = null,
) {
    init {
        if (width <= 0) throw IOException("video.width must be greater than zero")
        if (height <= 0) throw IOException("video.height must be greater than zero")
        if (planeCount != null && planeCount <= 0) throw IOException("video.planeCount must be greater than zero")
        requireNonBlank(pixelFormat, "video.pixelFormat")
    }
}

data class CultMeshStreamDescriptor(
    val streamId: String,
    val verseId: String,
    val ownerPeerId: String,
    val kind: String,
    val clock: CultMeshStreamClock,
    val preferredTransports: List<String>,
    val label: String? = null,
    val audio: CultMeshAudioStreamFormat? = null,
    val video: CultMeshVideoStreamFormat? = null,
    val requiredAccess: String = "read",
    val maxInFlightFrames: Int? = null,
    val metadataSchemaId: String? = null,
) {
    init {
        requireNonBlank(streamId, "stream.streamId")
        requireNonBlank(verseId, "stream.verseId")
        requireNonBlank(ownerPeerId, "stream.ownerPeerId")
        requireNonBlank(kind, "stream.kind")
        if (preferredTransports.isEmpty()) throw IOException("stream.preferredTransports must not be empty")
        if (maxInFlightFrames != null && maxInFlightFrames <= 0) throw IOException("stream.maxInFlightFrames must be greater than zero")
    }
}

data class CultMeshStreamConsumerProfile(
    val peerId: String,
    val verseId: String,
    val supportedTransports: List<String>,
    val acceptedKinds: List<String> = emptyList(),
    val canImportGpuHandles: Boolean = false,
    val canMapSharedMemory: Boolean = false,
    val maxInFlightFrames: Int? = null,
) {
    init {
        requireNonBlank(peerId, "consumer.peerId")
        requireNonBlank(verseId, "consumer.verseId")
        if (supportedTransports.isEmpty()) throw IOException("consumer.supportedTransports must not be empty")
        if (maxInFlightFrames != null && maxInFlightFrames <= 0) throw IOException("consumer.maxInFlightFrames must be greater than zero")
    }
}

data class CultMeshStreamNegotiation(
    val streamId: String,
    val producerPeerId: String,
    val consumerPeerId: String,
    val transport: String,
    val access: String,
    val maxInFlightFrames: Int,
    val copyBudget: String,
)

data class CultMeshStreamFrameHandle(
    val streamId: String,
    val sequence: Long,
    val timestampNs: Long,
    val transport: String,
    val durationNs: Long? = null,
    val byteLength: Int? = null,
    val nativeHandle: String? = null,
    val resourceKey: String? = null,
    val pageRef: String? = null,
    val fenceHandle: String? = null,
    val fenceValue: Long? = null,
    val unavoidableCopyCount: Int? = null,
    val metadata: Map<String, Any?> = emptyMap(),
) {
    init {
        requireNonBlank(streamId, "frame.streamId")
        requireNonBlank(transport, "frame.transport")
        if (sequence < 0) throw IOException("frame.sequence must not be negative")
        if (timestampNs < 0) throw IOException("frame.timestampNs must not be negative")
    }
}

class CultMeshStreamCatalog {
    private val knownStreams = linkedMapOf<String, CultMeshStreamDescriptor>()
    private val latestFrames = linkedMapOf<String, CultMeshStreamFrameHandle>()
    private val streamSubscribers = mutableListOf<(CultMeshStreamDescriptor) -> Unit>()
    private val frameSubscribers = mutableListOf<(CultMeshStreamFrameHandle) -> Unit>()

    val streams: List<CultMeshStreamDescriptor>
        get() = knownStreams.toSortedMap().values.toList()

    fun watch(callback: (CultMeshStreamDescriptor) -> Unit): () -> Unit {
        streamSubscribers.add(callback)
        return { streamSubscribers.remove(callback) }
    }

    fun watchFrames(callback: (CultMeshStreamFrameHandle) -> Unit): () -> Unit {
        frameSubscribers.add(callback)
        return { frameSubscribers.remove(callback) }
    }

    fun declare(stream: CultMeshStreamDescriptor): CultMeshStreamDescriptor {
        knownStreams[stream.streamId] = stream
        streamSubscribers.toList().forEach { it(stream) }
        return stream
    }

    fun get(streamId: String): CultMeshStreamDescriptor? {
        requireNonBlank(streamId, "streamId")
        return knownStreams[streamId]
    }

    fun find(verseId: String, kind: String? = null): List<CultMeshStreamDescriptor> {
        requireNonBlank(verseId, "verseId")
        return streams.filter { stream -> stream.verseId == verseId && (kind == null || stream.kind == kind) }
    }

    fun negotiate(streamId: String, consumer: CultMeshStreamConsumerProfile): CultMeshStreamNegotiation {
        val stream = get(streamId) ?: throw IOException("Unknown CultMesh stream '$streamId'")
        if (consumer.verseId != stream.verseId) throw IOException("stream and consumer must belong to the same Verse")
        if (consumer.acceptedKinds.isNotEmpty() && stream.kind !in consumer.acceptedKinds) {
            throw IOException("consumer does not accept ${stream.kind} streams")
        }
        val transport = stream.preferredTransports.firstOrNull { it in consumer.supportedTransports }
            ?: throw IOException("stream and consumer have no compatible body transport")
        val streamMax = stream.maxInFlightFrames ?: Int.MAX_VALUE
        val consumerMax = consumer.maxInFlightFrames ?: Int.MAX_VALUE
        return CultMeshStreamNegotiation(
            streamId = stream.streamId,
            producerPeerId = stream.ownerPeerId,
            consumerPeerId = consumer.peerId,
            transport = transport,
            access = stream.requiredAccess,
            maxInFlightFrames = minOf(streamMax, consumerMax),
            copyBudget = copyBudgetForStreamTransport(transport),
        )
    }

    fun publishFrame(frame: CultMeshStreamFrameHandle): CultMeshStreamFrameHandle {
        if (frame.streamId !in knownStreams) throw IOException("Unknown CultMesh stream '${frame.streamId}'")
        latestFrames[frame.streamId] = frame
        frameSubscribers.toList().forEach { it(frame) }
        return frame
    }

    fun latestFrame(streamId: String): CultMeshStreamFrameHandle? {
        requireNonBlank(streamId, "streamId")
        return latestFrames[streamId]
    }
}

fun createStreamCatalog(): CultMeshStreamCatalog = CultMeshStreamCatalog()

private fun copyBudgetForStreamTransport(transport: String): String = when (transport) {
    CultMeshStreamBodyTransports.SharedMemory,
    CultMeshStreamBodyTransports.SharedD3D12Texture,
    CultMeshStreamBodyTransports.SharedD3D11Texture,
    CultMeshStreamBodyTransports.DmaBuf,
    CultMeshStreamBodyTransports.IOSurface,
    CultMeshStreamBodyTransports.AHardwareBuffer -> "zero-copy-target"
    CultMeshStreamBodyTransports.CultCachePage -> "one-copy-fallback"
    else -> "opaque-runtime"
}

class CultMeshVerseCatalog {
    private val knownVerses = linkedMapOf<String, CultMeshVerseDescriptor>()
    private val subscribers = mutableListOf<(CultMeshVerseDescriptor) -> Unit>()

    val verses: List<CultMeshVerseDescriptor>
        get() = knownVerses.toSortedMap().values.toList()

    fun watch(callback: (CultMeshVerseDescriptor) -> Unit): () -> Unit {
        subscribers.add(callback)
        return { subscribers.remove(callback) }
    }

    fun upsert(verse: CultMeshVerseDescriptor) {
        requireNonBlank(verse.verseId, "verse.verseId")
        knownVerses[verse.verseId] = verse
        subscribers.toList().forEach { it(verse) }
    }

    fun get(verseId: String): CultMeshVerseDescriptor? {
        requireNonBlank(verseId, "verseId")
        return knownVerses[verseId]
    }

    fun findTransferTargets(source: CultMeshVerseDescriptor): List<CultMeshVerseDescriptor> =
        verses.filter { it.verseId != source.verseId && it.canTransferFrom(source) }

    fun createResponse(request: CultNetMessage): CultNetMessage {
        require(request.schemaVersion == "cultmesh.verse_catalog_request.v0") {
            "Expected cultmesh.verse_catalog_request.v0, received ${request.schemaVersion}"
        }
        val requested = stringList(request.body["verseIds"]).toSet()
        val transportVersion = request.body["transportVersion"] as? String
        val filtered = verses.filter { verse ->
            (requested.isEmpty() || verse.verseId in requested) &&
                (transportVersion.isNullOrBlank() || verse.compatibility.transportVersion == transportVersion)
        }
        return cultMeshVerseCatalogResponse(
            messageId = request.body["messageId"] as? String ?: "",
            verses = filtered,
        )
    }

    fun applyResponse(response: CultNetMessage): List<CultMeshVerseDescriptor> {
        require(response.schemaVersion == "cultmesh.verse_catalog_response.v0") {
            "Expected cultmesh.verse_catalog_response.v0, received ${response.schemaVersion}"
        }
        val applied = mapList(response.body["verses"]).map { verseFromWire(it) }
        applied.forEach { upsert(it) }
        return applied
    }
}

class CultMeshPeerCatalog {
    private val knownPeers = linkedMapOf<String, CultMeshPeerCard>()
    private val subscribers = mutableListOf<(CultMeshPeerCard) -> Unit>()

    val peers: List<CultMeshPeerCard>
        get() = knownPeers.toSortedMap().values.toList()

    fun watch(callback: (CultMeshPeerCard) -> Unit): () -> Unit {
        subscribers.add(callback)
        return { subscribers.remove(callback) }
    }

    fun upsert(peer: CultMeshPeerCard) {
        requireNonBlank(peer.peerId, "peer.peerId")
        requireNonBlank(peer.verseId, "peer.verseId")
        knownPeers[peer.peerId] = peer
        subscribers.toList().forEach { it(peer) }
    }

    fun get(peerId: String): CultMeshPeerCard? {
        requireNonBlank(peerId, "peerId")
        return knownPeers[peerId]
    }

    fun find(verseId: String, role: String? = null): List<CultMeshPeerCard> {
        requireNonBlank(verseId, "verseId")
        return peers.filter { peer -> peer.verseId == verseId && (role == null || peer.hasRole(role)) }
    }

    fun findAuthorized(
        verseId: String,
        role: String,
        leases: CultMeshAuthorityLeaseCatalog,
        shardId: String? = null,
        at: Instant = Instant.now(),
    ): List<CultMeshPeerCard> {
        requireNonBlank(verseId, "verseId")
        requireNonBlank(role, "role")
        return find(verseId, role).filter { peer -> leases.isAuthorized(peer, role, shardId, at) }
    }

    fun firstAuthorized(
        verseId: String,
        role: String,
        leases: CultMeshAuthorityLeaseCatalog,
        shardId: String? = null,
        at: Instant = Instant.now(),
    ): CultMeshPeerCard? = findAuthorized(verseId, role, leases, shardId, at).firstOrNull()

    fun createResponse(request: CultNetMessage): CultNetMessage {
        require(request.schemaVersion == "cultmesh.peer_exchange_request.v0") {
            "Expected cultmesh.peer_exchange_request.v0, received ${request.schemaVersion}"
        }
        val verseId = request.body["verseId"] as? String ?: ""
        val roles = stringList(request.body["roles"]).toSet()
        val knownPeerIds = stringList(request.body["knownPeerIds"]).toSet()
        val limit = (request.body["limit"] as? Number)?.toInt()
        val filtered = peers.asSequence()
            .filter { it.verseId == verseId }
            .filter { it.peerId !in knownPeerIds }
            .filter { peer -> roles.isEmpty() || peer.roles.any { it in roles } }
            .let { sequence -> if (limit != null) sequence.take(limit) else sequence }
            .toList()
        return cultMeshPeerExchangeResponse(
            messageId = request.body["messageId"] as? String ?: "",
            peers = filtered,
        )
    }

    fun applyResponse(response: CultNetMessage): List<CultMeshPeerCard> {
        require(response.schemaVersion == "cultmesh.peer_exchange_response.v0") {
            "Expected cultmesh.peer_exchange_response.v0, received ${response.schemaVersion}"
        }
        val applied = mapList(response.body["peers"]).map { peerFromWire(it) }
        applied.forEach { upsert(it) }
        return applied
    }
}

fun cultNetHello(
    runtimeId: String,
    runtimeKind: String = "kotlin",
    displayName: String? = null,
    supportedDocumentTypes: List<String> = emptyList(),
    supportedMutationContracts: List<Map<String, Any?>> = emptyList(),
    supportedMessageVersions: List<String> = emptyList(),
    transportProfiles: List<CultNetTransportProfile> = emptyList(),
    supportsSchemaCatalog: Boolean = true,
): CultNetMessage {
    val body = linkedMapOf<String, Any?>(
        "runtimeId" to runtimeId,
        "runtimeKind" to runtimeKind,
        "supportedDocumentTypes" to supportedDocumentTypes,
        "supportedMutationContracts" to supportedMutationContracts,
        "supportedMessageVersions" to supportedMessageVersions,
        "transportProfiles" to transportProfiles.map { it.toWireMap() },
        "supportsSchemaCatalog" to supportsSchemaCatalog,
    )
    if (!displayName.isNullOrBlank()) body["displayName"] = displayName
    return CultNetMessage("cultnet.hello.v0", body)
}

fun cultNetSchemaCatalogRequest(
    messageId: String,
    includeSchemaJson: Boolean = false,
    schemaIds: List<String> = emptyList(),
    kinds: List<String> = emptyList(),
): CultNetMessage = CultNetMessage(
    "cultnet.schema_catalog_request.v0",
    linkedMapOf(
        "messageId" to messageId,
        "includeSchemaJson" to includeSchemaJson,
        "schemaIds" to schemaIds,
        "kinds" to kinds,
    ),
)

fun cultNetSchemaCatalogResponse(
    messageId: String,
    schemas: List<CultNetSchemaDescriptor>,
    includeSchemaJson: Boolean = true,
): CultNetMessage = CultNetMessage(
    "cultnet.schema_catalog_response.v0",
    linkedMapOf(
        "messageId" to messageId,
        "schemas" to schemas.map { it.toWireMap(includeSchemaJson) },
    ),
)

fun cultNetDocumentPutRaw(
    messageId: String,
    document: CultNetRawDocumentRecord,
    shardId: String? = null,
    shardEpoch: Long? = null,
): CultNetMessage {
    val body = linkedMapOf<String, Any?>(
        "messageId" to messageId,
        "document" to document.toWireMap(),
    )
    if (!shardId.isNullOrBlank()) body["shardId"] = shardId
    if (shardEpoch != null) body["shardEpoch"] = shardEpoch
    return CultNetMessage("cultnet.document_put_raw.v0", body)
}

fun cultNetDocumentDelete(
    messageId: String,
    schemaId: String,
    recordKey: String,
    shardId: String? = null,
    shardEpoch: Long? = null,
): CultNetMessage {
    val body = linkedMapOf<String, Any?>(
        "messageId" to messageId,
        "schemaId" to schemaId,
        "recordKey" to recordKey,
    )
    if (!shardId.isNullOrBlank()) body["shardId"] = shardId
    if (shardEpoch != null) body["shardEpoch"] = shardEpoch
    return CultNetMessage("cultnet.document_delete.v0", body)
}

fun cultNetSnapshotRequest(
    messageId: String,
    schemaIds: List<String> = emptyList(),
    recordKeys: List<String> = emptyList(),
    shardId: String? = null,
    shardEpoch: Long? = null,
): CultNetMessage {
    val body = linkedMapOf<String, Any?>(
        "messageId" to messageId,
        "schemaIds" to schemaIds,
        "recordKeys" to recordKeys,
    )
    if (!shardId.isNullOrBlank()) body["shardId"] = shardId
    if (shardEpoch != null) body["shardEpoch"] = shardEpoch
    return CultNetMessage("cultnet.snapshot_request.v0", body)
}

fun cultNetSnapshotResponseRaw(
    messageId: String,
    documents: List<CultNetRawDocumentRecord>,
    shardId: String? = null,
    shardEpoch: Long? = null,
    shardLogSequence: Long? = null,
): CultNetMessage {
    val body = linkedMapOf<String, Any?>(
        "messageId" to messageId,
        "documents" to documents.map { it.toWireMap() },
    )
    if (!shardId.isNullOrBlank()) body["shardId"] = shardId
    if (shardEpoch != null) body["shardEpoch"] = shardEpoch
    if (shardLogSequence != null) body["shardLogSequence"] = shardLogSequence
    return CultNetMessage("cultnet.snapshot_response_raw.v0", body)
}

fun cultNetShardCatalogRequest(
    messageId: String,
    schemaIds: List<String> = emptyList(),
    recordKeys: List<String> = emptyList(),
): CultNetMessage = CultNetMessage(
    "cultnet.shard_catalog_request.v0",
    linkedMapOf(
        "messageId" to messageId,
        "schemaIds" to schemaIds,
        "recordKeys" to recordKeys,
    ),
)

fun cultNetShardCatalogResponse(
    messageId: String,
    shards: List<CultNetShardDescriptor>,
): CultNetMessage = CultNetMessage(
    "cultnet.shard_catalog_response.v0",
    linkedMapOf(
        "messageId" to messageId,
        "shards" to shards.map { it.toWireMap() },
    ),
)

fun cultNetShardLogRequest(
    messageId: String,
    shardId: String,
    shardEpoch: Long? = null,
    afterSequence: Long = 0,
    limit: Int? = null,
): CultNetMessage {
    val body = linkedMapOf<String, Any?>(
        "messageId" to messageId,
        "shardId" to shardId,
        "afterSequence" to afterSequence,
    )
    if (shardEpoch != null) body["shardEpoch"] = shardEpoch
    if (limit != null) body["limit"] = limit.toLong()
    return CultNetMessage("cultnet.shard_log_request.v0", body)
}

fun cultNetShardLogResponse(
    messageId: String,
    shardId: String,
    shardEpoch: Long,
    entries: List<CultNetShardLogEntry>,
    resyncRequired: Boolean = false,
    reason: String? = null,
    compactedThrough: Long? = null,
): CultNetMessage = CultNetShardLogResponse(
    messageId = messageId,
    shardId = shardId,
    shardEpoch = shardEpoch,
    entries = entries,
    resyncRequired = resyncRequired,
    reason = reason,
    compactedThrough = compactedThrough,
).toMessage()

fun CultCache.createRawSnapshotResponse(
    request: CultNetMessage,
    storedAt: String = Instant.now().toString(),
    sourceRuntimeId: String? = null,
): CultNetMessage {
    require(request.schemaVersion == "cultnet.snapshot_request.v0") {
        "Expected cultnet.snapshot_request.v0, received ${request.schemaVersion}"
    }
    val schemaIds = stringList(request.body["schemaIds"]).toSet()
    val recordKeys = stringList(request.body["recordKeys"]).toSet()
    val documents = snapshotEnvelopes(storedAt)
        .filter { schemaIds.isEmpty() || it.schemaId in schemaIds }
        .filter { recordKeys.isEmpty() || it.key in recordKeys }
        .map {
            CultNetRawDocumentRecord(
                schemaId = it.schemaId,
                recordKey = it.key,
                storedAt = it.storedAt,
                payload = it.payload,
                sourceRuntimeId = sourceRuntimeId,
            )
        }
    return cultNetSnapshotResponseRaw(
        messageId = request.body["messageId"] as? String ?: "",
        documents = documents,
        shardId = request.body["shardId"] as? String,
        shardEpoch = (request.body["shardEpoch"] as? Number)?.toLong(),
    )
}

fun CultCache.applyRawDocumentPut(message: CultNetMessage): Any {
    require(message.schemaVersion == "cultnet.document_put_raw.v0") {
        "Expected cultnet.document_put_raw.v0, received ${message.schemaVersion}"
    }
    return applyRawDocumentRecord(rawDocumentRecordFromWire(mapValue(message.body["document"])))
}

fun CultCache.applyRawSnapshotResponse(response: CultNetMessage): List<Any> {
    require(response.schemaVersion == "cultnet.snapshot_response_raw.v0") {
        "Expected cultnet.snapshot_response_raw.v0, received ${response.schemaVersion}"
    }
    return mapList(response.body["documents"])
        .map { rawDocumentRecordFromWire(it) }
        .map { applyRawDocumentRecord(it) }
}

fun <T : Any> CultCache.syncDocument(
    response: CultNetMessage,
    document: CultDocumentDefinition<T>,
    key: String,
): T {
    require(response.schemaVersion == "cultnet.snapshot_response_raw.v0") {
        "Expected cultnet.snapshot_response_raw.v0, received ${response.schemaVersion}"
    }
    val rawDocument = mapList(response.body["documents"])
        .map { rawDocumentRecordFromWire(it) }
        .firstOrNull { it.schemaId == document.schemaVersion && it.recordKey == key }
        ?: throw NoSuchElementException("No ${document.schemaVersion} document for key $key")
    applyRawDocumentRecord(rawDocument)
    return getRequired(document, key)
}

fun CultCache.applyDocumentDelete(message: CultNetMessage): Boolean {
    require(message.schemaVersion == "cultnet.document_delete.v0") {
        "Expected cultnet.document_delete.v0, received ${message.schemaVersion}"
    }
    val schemaId = requireWireString(message.body, "schemaId")
    val recordKey = requireWireString(message.body, "recordKey")
    return deleteBySchema(schemaId, recordKey)
}

fun CultCache.applyShardLogResponse(response: CultNetMessage): List<Any?> {
    val shardLog = shardLogResponseFromMessage(response).requireUsable()
    return shardLog.entries.map { entry ->
        when (entry.changeKind) {
            "added", "updated" -> entry.put?.let { applyRawDocumentPut(it) }
            "removed" -> {
                entry.delete?.let { applyDocumentDelete(it) }
                null
            }
            else -> throw IOException("unsupported shard log changeKind ${entry.changeKind}")
        }
    }
}

private fun CultCache.applyRawDocumentRecord(document: CultNetRawDocumentRecord): Any {
    if (document.payloadEncoding != "messagepack") throw IOException("Unsupported raw document payload encoding ${document.payloadEncoding}")
    return putRaw(document.schemaId, document.recordKey, document.payload)
}

fun cultMeshVerseCatalogRequest(
    messageId: String,
    verseIds: List<String> = emptyList(),
    transportVersion: String? = null,
): CultNetMessage {
    val body = linkedMapOf<String, Any?>(
        "messageId" to messageId,
        "verseIds" to verseIds,
    )
    if (!transportVersion.isNullOrBlank()) body["transportVersion"] = transportVersion
    return CultNetMessage("cultmesh.verse_catalog_request.v0", body)
}

fun cultMeshVerseCatalogResponse(
    messageId: String,
    verses: List<CultMeshVerseDescriptor>,
): CultNetMessage = CultNetMessage(
    "cultmesh.verse_catalog_response.v0",
    linkedMapOf(
        "messageId" to messageId,
        "verses" to verses.map { it.toWireMap() },
    ),
)

fun cultMeshPeerExchangeRequest(
    messageId: String,
    verseId: String,
    roles: List<String> = emptyList(),
    knownPeerIds: List<String> = emptyList(),
    limit: Int? = null,
): CultNetMessage {
    val body = linkedMapOf<String, Any?>(
        "messageId" to messageId,
        "verseId" to verseId,
        "roles" to roles,
        "knownPeerIds" to knownPeerIds,
    )
    if (limit != null) body["limit"] = limit.toLong()
    return CultNetMessage("cultmesh.peer_exchange_request.v0", body)
}

fun cultMeshPeerExchangeResponse(
    messageId: String,
    peers: List<CultMeshPeerCard>,
): CultNetMessage = CultNetMessage(
    "cultmesh.peer_exchange_response.v0",
    linkedMapOf(
        "messageId" to messageId,
        "peers" to peers.map { it.toWireMap() },
    ),
)

fun encodeCultNetMessage(message: CultNetMessage): ByteArray =
    MessagePackWriter().value(message.toWireMap()).toByteArray()

fun parseCultNetMessage(payload: ByteArray): CultNetMessage {
    val decoded = MessagePackReader(payload).readAny()
    if (decoded !is Map<*, *>) throw IOException("CultNet schema-v0 messages must be MessagePack maps")
    val schemaVersion = decoded["schemaVersion"]
    if (schemaVersion !is String || schemaVersion.isBlank()) {
        throw IOException("CultNet schema-v0 messages must declare schemaVersion")
    }
    val body = linkedMapOf<String, Any?>()
    for ((key, value) in decoded) {
        if (key !is String) throw IOException("CultNet schema-v0 message keys must be strings")
        if (key != "schemaVersion") body[key] = value
    }
    return CultNetMessage(schemaVersion, body)
}

fun verseFromWire(wire: Map<String, Any?>): CultMeshVerseDescriptor = CultMeshVerseDescriptor(
    verseId = requireWireString(wire, "verseId"),
    displayName = wire["displayName"] as? String ?: "",
    authorityModel = wire["authorityModel"] as? String ?: "",
    compatibility = compatibilityFromWire(mapValue(wire["compatibility"])),
    discoveryEndpoints = stringList(wire["discoveryEndpoints"]),
    authorityRuntimeIds = stringList(wire["authorityRuntimeIds"]),
    parentVerseId = wire["parentVerseId"] as? String,
    description = wire["description"] as? String,
)

fun compatibilityFromWire(wire: Map<String, Any?>): CultMeshVerseCompatibility = CultMeshVerseCompatibility(
    transportVersion = requireWireString(wire, "transportVersion"),
    rulesHash = requireWireString(wire, "rulesHash"),
    compatibleVerseIds = stringList(wire["compatibleVerseIds"]),
    requiredPluginIds = stringList(wire["requiredPluginIds"]),
    optionalPluginIds = stringList(wire["optionalPluginIds"]),
)

fun peerFromWire(wire: Map<String, Any?>): CultMeshPeerCard = CultMeshPeerCard(
    peerId = requireWireString(wire, "peerId"),
    verseId = requireWireString(wire, "verseId"),
    endpoints = stringList(wire["endpoints"]),
    roles = stringList(wire["roles"]),
    shardIds = stringList(wire["shardIds"]),
    region = wire["region"] as? String,
    authorityLeaseId = wire["authorityLeaseId"] as? String,
    expiresAt = wire["expiresAt"] as? String,
    signature = wire["signature"] as? String,
)

fun rawDocumentRecordFromWire(wire: Map<String, Any?>): CultNetRawDocumentRecord = CultNetRawDocumentRecord(
    schemaId = requireWireString(wire, "schemaId"),
    recordKey = requireWireString(wire, "recordKey"),
    storedAt = wire["storedAt"] as? String ?: "",
    payloadEncoding = wire["payloadEncoding"] as? String ?: "messagepack",
    payload = wire["payload"] as? ByteArray ?: throw IOException("payload must be binary MessagePack bytes"),
    sourceRuntimeId = wire["sourceRuntimeId"] as? String,
    sourceAgentId = wire["sourceAgentId"] as? String,
    sourceRole = wire["sourceRole"] as? String,
    tags = stringList(wire["tags"]),
)

fun schemaDescriptorFromWire(wire: Map<String, Any?>): CultNetSchemaDescriptor = CultNetSchemaDescriptor(
    schemaId = requireWireString(wire, "schemaId"),
    kind = requireWireString(wire, "kind"),
    schemaVersion = wire["schemaVersion"] as? String,
    documentType = wire["documentType"] as? String,
    title = wire["title"] as? String,
    wireContracts = stringList(wire["wireContracts"]),
    contentHash = requireWireString(wire, "contentHash"),
    schemaJson = wire["schemaJson"] as? String,
)

fun shardDescriptorFromWire(wire: Map<String, Any?>): CultNetShardDescriptor = CultNetShardDescriptor(
    shardId = requireWireString(wire, "shardId"),
    ownerRuntimeId = requireWireString(wire, "ownerRuntimeId"),
    epoch = (wire["epoch"] as? Number)?.toLong() ?: 0,
    isPrimary = wire["isPrimary"] == true,
    schemaIds = stringList(wire["schemaIds"]),
    keyPrefix = wire["keyPrefix"] as? String,
    primaryEndpoints = stringList(wire["primaryEndpoints"]),
    replicaEndpoints = stringList(wire["replicaEndpoints"]),
    readReplicaEndpoints = stringList(wire["readReplicaEndpoints"]),
    region = wire["region"] as? String,
    authorityLeaseId = wire["authorityLeaseId"] as? String,
)

fun shardLogEntryFromWire(wire: Map<String, Any?>): CultNetShardLogEntry {
    val sequence = (wire["sequence"] as? Number)?.toLong() ?: 0
    val changeKind = requireWireString(wire, "changeKind")
    if (sequence <= 0) throw IOException("shard log entry sequence must be positive")
    if (changeKind !in setOf("added", "updated", "removed")) throw IOException("unsupported shard log changeKind $changeKind")
    return CultNetShardLogEntry(
        sequence = sequence,
        changeKind = changeKind,
        put = (wire["put"] as? Map<*, *>)?.let { messageFromWireMap(mapValue(it)) },
        delete = (wire["delete"] as? Map<*, *>)?.let { messageFromWireMap(mapValue(it)) },
        committedAt = wire["committedAt"] as? String,
    )
}

fun shardLogResponseFromMessage(message: CultNetMessage): CultNetShardLogResponse {
    require(message.schemaVersion == "cultnet.shard_log_response.v0") {
        "Expected cultnet.shard_log_response.v0, received ${message.schemaVersion}"
    }
    return CultNetShardLogResponse(
        messageId = message.body["messageId"] as? String ?: "",
        shardId = requireWireString(message.body, "shardId"),
        shardEpoch = (message.body["shardEpoch"] as? Number)?.toLong() ?: 0,
        entries = mapList(message.body["entries"]).map { shardLogEntryFromWire(it) },
        resyncRequired = message.body["resyncRequired"] == true,
        reason = message.body["reason"] as? String,
        compactedThrough = (message.body["compactedThrough"] as? Number)?.toLong(),
    )
}

private fun messageFromWireMap(wire: Map<String, Any?>): CultNetMessage {
    val schemaVersion = requireWireString(wire, "schemaVersion")
    return CultNetMessage(schemaVersion, wire.filterKeys { it != "schemaVersion" })
}

private fun requireNonBlank(value: String, fieldName: String) {
    if (value.isBlank()) throw IOException("$fieldName must not be blank")
}

private fun requireWireString(wire: Map<String, Any?>, fieldName: String): String {
    val value = wire[fieldName]
    if (value !is String || value.isBlank()) throw IOException("$fieldName must be a non-empty string")
    return value
}

private fun stringList(value: Any?): List<String> = when (value) {
    null -> emptyList()
    is Iterable<*> -> value.mapNotNull { it as? String }
    is Array<*> -> value.mapNotNull { it as? String }
    else -> throw IOException("Expected string array")
}

private fun mapList(value: Any?): List<Map<String, Any?>> = when (value) {
    null -> emptyList()
    is Iterable<*> -> value.map { mapValue(it) }
    is Array<*> -> value.map { mapValue(it) }
    else -> throw IOException("Expected map array")
}

@Suppress("UNCHECKED_CAST")
private fun mapValue(value: Any?): Map<String, Any?> {
    if (value !is Map<*, *>) throw IOException("Expected map")
    val map = linkedMapOf<String, Any?>()
    for ((key, nested) in value) {
        if (key !is String) throw IOException("Expected string map keys")
        map[key] = nested
    }
    return map
}

private fun reconciliationDelta(predictedPayload: ByteArray, canonicalPayload: ByteArray): Map<String, Any?> {
    val predicted = MessagePackReader(predictedPayload).readAny()
    val canonical = MessagePackReader(canonicalPayload).readAny()
    val delta = linkedMapOf<String, Any?>()
    when {
        predicted is Map<*, *> && canonical is Map<*, *> -> {
            val canonicalMap = mapValue(canonical)
            for ((key, predictedValue) in mapValue(predicted)) {
                val canonicalValue = canonicalMap[key]
                if (predictedValue == canonicalValue) continue
                delta[key] = numericDelta(predictedValue, canonicalValue) ?: predictedValue
            }
        }
        predicted is List<*> && canonical is List<*> -> {
            predicted.forEachIndexed { index, predictedValue ->
                val canonicalValue = canonical.getOrNull(index)
                if (predictedValue == canonicalValue) return@forEachIndexed
                delta[index.toString()] = numericDelta(predictedValue, canonicalValue) ?: predictedValue
            }
        }
        predicted != canonical -> {
            delta["value"] = numericDelta(predicted, canonical) ?: predicted
        }
    }
    return delta
}

private fun numericDelta(predicted: Any?, canonical: Any?): Double? {
    val predictedNumber = predicted as? Number ?: return null
    val canonicalNumber = canonical as? Number ?: return null
    return predictedNumber.toDouble() - canonicalNumber.toDouble()
}

private fun sha256Hex(value: String): String {
    val digest = MessageDigest.getInstance("SHA-256").digest(value.toByteArray(StandardCharsets.UTF_8))
    return digest.joinToString("") { "%02x".format(it.toInt() and 0xff) }
}

fun CultNetTransportProfile.toWireMap(): Map<String, Any?> = linkedMapOf(
    "schemaVersion" to schemaVersion,
    "runtimeId" to runtimeId,
    "transports" to transports.map { it.toWireMap() },
)

fun CultNetTransportDescriptor.toWireMap(): Map<String, Any?> {
    val wire = linkedMapOf<String, Any?>(
        "transportId" to transportId,
        "protocol" to protocol,
        "wireContracts" to wireContracts,
        "channels" to channels.map { it.toWireMap() },
    )
    if (!host.isNullOrBlank()) wire["host"] = host
    if (port != null) wire["port"] = port.toLong()
    if (reconnectPolicy != null) wire["reconnectPolicy"] = reconnectPolicy.toWireMap()
    return wire
}

fun CultNetTransportChannel.toWireMap(): Map<String, Any?> {
    val wire = linkedMapOf<String, Any?>(
        "channelId" to channelId,
        "delivery" to delivery,
        "ordering" to ordering,
    )
    if (maxPayloadBytes != null) wire["maxPayloadBytes"] = maxPayloadBytes.toLong()
    if (maxFragmentBytes != null) wire["maxFragmentBytes"] = maxFragmentBytes.toLong()
    if (maxPendingReliablePackets != null) wire["maxPendingReliablePackets"] = maxPendingReliablePackets.toLong()
    return wire
}

enum class CultNetRudpPacketType(val code: Int) {
    Connect(1),
    Accept(2),
    Data(3),
    Ack(4),
    Ping(5),
    Pong(6),
    Disconnect(7);

    companion object {
        fun fromCode(code: Int): CultNetRudpPacketType =
            values().firstOrNull { it.code == code } ?: throw IOException("Unsupported CultNet RUDP packet type $code")
    }
}

data class CultNetRudpPacket(
    val packetType: CultNetRudpPacketType,
    val connectionId: Long,
    val sequence: Long,
    val ack: Long,
    val ackMask: Long,
    val channelId: String,
    val reliable: Boolean = false,
    val ordered: Boolean = false,
    val sequenced: Boolean = false,
    val fragmentId: Int = 0,
    val fragmentIndex: Int = 0,
    val fragmentCount: Int = 0,
    val payload: ByteArray = ByteArray(0),
)

data class CultNetRudpDeliveredFrame(val channelId: String, val payload: ByteArray, val sequence: Long)
data class CultNetRudpReceiveResult(
    val delivered: List<CultNetRudpDeliveredFrame> = emptyList(),
    val readyToSend: List<CultNetRudpPacket> = emptyList(),
    val reply: CultNetRudpPacket? = null,
    val pong: Boolean = false,
    val pongPayload: ByteArray = ByteArray(0),
    val disconnected: Boolean = false,
    val disconnectReason: ByteArray = ByteArray(0),
)

data class CultNetRudpSessionOptions(
    val connectionId: Long,
    val initialSequence: Long = 1,
    val resendDelayMs: Long = 250,
    val maxPendingReliablePackets: Int? = null,
)

data class CultNetRudpSendOptions(
    val reliable: Boolean = false,
    val ordered: Boolean = false,
    val sequenced: Boolean = false,
    val nowMs: Long = 0,
)

private data class PendingReliablePacket(var packet: CultNetRudpPacket, var lastSentAtMs: Long)
private data class PendingOrderedFrame(val frame: CultNetRudpDeliveredFrame, val nextSequence: Long)
private data class FragmentBuffer(
    val channelId: String,
    val ordered: Boolean,
    val fragmentCount: Int,
    val payloads: MutableMap<Int, ByteArray> = linkedMapOf(),
    val sequences: MutableMap<Int, Long> = linkedMapOf(),
)

class CultNetRudpSession(options: CultNetRudpSessionOptions) {
    companion object {
        const val ReliableSendWindowPackets = 32
        private const val ReceivedSequenceWindow = 4_096
    }

    val connectionId: Long = uint32(options.connectionId, "connectionId")
    val resendDelayMs: Long = options.resendDelayMs
    private val maxPendingReliablePackets: Int? = options.maxPendingReliablePackets
    private var nextSequence = uint32(options.initialSequence, "initialSequence")
    private var nextFragmentId = 1
    var connected: Boolean = false
        private set
    var lastReceivedAtMs: Long? = null
        private set
    private var highestReceivedSequence: Long? = null
    private val receivedSequences = linkedSetOf<Long>()
    private val pendingReliable = linkedMapOf<Long, PendingReliablePacket>()
    private val queuedReliable = ArrayDeque<CultNetRudpPacket>()
    private val orderedNextSequenceByChannel = linkedMapOf<String, Long>()
    private val orderedBuffers = linkedMapOf<String, TreeMap<Long, PendingOrderedFrame>>()
    private val fragmentBuffers = linkedMapOf<Pair<String, Int>, FragmentBuffer>()

    val pendingReliableSequences: List<Long>
        get() = pendingReliable.keys.sorted()
    val queuedReliablePacketCount: Int
        get() = queuedReliable.size
    val outstandingReliablePacketCount: Int
        get() = pendingReliable.size + queuedReliable.size

    init {
        if (maxPendingReliablePackets != null && maxPendingReliablePackets <= 0) {
            throw IOException("RUDP maxPendingReliablePackets must be greater than zero")
        }
    }

    fun createConnect(nowMs: Long = 0, payload: ByteArray = ByteArray(0)): CultNetRudpPacket {
        ensureReliableCapacity(1)
        val packet = createPacket(CultNetRudpPacketType.Connect, "control", payload, reliable = true, ordered = true)
        trackReliable(packet, nowMs)
        return packet
    }

    fun acceptConnect(packet: CultNetRudpPacket, nowMs: Long = 0, payload: ByteArray = ByteArray(0)): CultNetRudpPacket {
        requireConnection(packet)
        if (packet.packetType != CultNetRudpPacketType.Connect) throw IOException("Expected RUDP connect packet, got ${packet.packetType}")
        ensureReliableCapacity(1)
        rememberReceived(packet.sequence)
        connected = true
        val response = createPacket(CultNetRudpPacketType.Accept, "control", payload, reliable = true, ordered = true)
        trackReliable(response, nowMs)
        return response
    }

    fun send(channelId: String, payload: ByteArray, options: CultNetRudpSendOptions = CultNetRudpSendOptions()): CultNetRudpPacket {
        if (options.reliable && pendingReliable.size >= ReliableSendWindowPackets) {
            throw IOException("RUDP reliable send window is full; receive acknowledgements before sending")
        }
        return sendMany(channelId, payload, options).first()
    }

    fun sendMany(
        channelId: String,
        payload: ByteArray,
        options: CultNetRudpSendOptions = CultNetRudpSendOptions(),
        maxFragmentBytes: Int? = null,
    ): List<CultNetRudpPacket> {
        if (!connected) throw IOException("Cannot send RUDP data before the session is connected")
        if (maxFragmentBytes != null && maxFragmentBytes <= 0) throw IOException("RUDP maxFragmentBytes must be greater than zero")
        if (maxFragmentBytes != null && payload.size > maxFragmentBytes) {
            val fragmentCount = (payload.size + maxFragmentBytes - 1) / maxFragmentBytes
            if (fragmentCount > 0xffff) throw IOException("RUDP payload requires more than 65535 fragments")
            ensureReliableCapacity(if (options.reliable) fragmentCount else 0)
            val fragmentId = allocateFragmentId()
            val packets = (0 until fragmentCount).map { index ->
                val start = index * maxFragmentBytes
                val packet = createPacket(
                    channelId = channelId,
                    packetType = CultNetRudpPacketType.Data,
                    payload = payload.copyOfRange(start, minOf(start + maxFragmentBytes, payload.size)),
                    reliable = options.reliable,
                    ordered = options.ordered,
                    sequenced = options.sequenced,
                    fragmentId = fragmentId,
                    fragmentIndex = index,
                    fragmentCount = fragmentCount,
                )
                packet
            }
            return if (options.reliable) admitReliablePackets(packets, options.nowMs) else packets
        }
        ensureReliableCapacity(if (options.reliable) 1 else 0)
        val packet = createPacket(channelId = channelId, packetType = CultNetRudpPacketType.Data, payload = payload, reliable = options.reliable, ordered = options.ordered, sequenced = options.sequenced)
        return if (packet.reliable) admitReliablePackets(listOf(packet), options.nowMs) else listOf(packet)
    }

    fun receive(packet: CultNetRudpPacket, nowMs: Long = 0): CultNetRudpReceiveResult {
        @Suppress("UNUSED_VARIABLE")
        val ignoredNow = nowMs
        requireConnection(packet)
        applyAcknowledgements(packet)
        val readyToSend = promoteQueuedReliable(nowMs)
        lastReceivedAtMs = nowMs
        val expectedSequenceIfUninitialized = (highestReceivedSequence ?: (packet.sequence - 1)) + 1
        when (packet.packetType) {
            CultNetRudpPacketType.Accept -> {
                rememberReceived(packet.sequence)
                connected = true
                return CultNetRudpReceiveResult(readyToSend = readyToSend)
            }
            CultNetRudpPacketType.Ping -> {
                rememberReceived(packet.sequence)
                return CultNetRudpReceiveResult(readyToSend = readyToSend, reply = createPacket(CultNetRudpPacketType.Pong, "control", packet.payload))
            }
            CultNetRudpPacketType.Ack, CultNetRudpPacketType.Pong -> {
                rememberReceived(packet.sequence)
                return CultNetRudpReceiveResult(
                    readyToSend = readyToSend,
                    pong = packet.packetType == CultNetRudpPacketType.Pong,
                    pongPayload = if (packet.packetType == CultNetRudpPacketType.Pong) packet.payload.copyOf() else ByteArray(0),
                )
            }
            CultNetRudpPacketType.Disconnect -> {
                rememberReceived(packet.sequence)
                connected = false
                return CultNetRudpReceiveResult(readyToSend = readyToSend, disconnected = true, disconnectReason = packet.payload.copyOf())
            }
            CultNetRudpPacketType.Data -> Unit
            else -> return CultNetRudpReceiveResult(readyToSend = readyToSend)
        }

        val duplicate = receivedSequences.contains(packet.sequence) || (
            highestReceivedSequence != null &&
                packet.sequence < highestReceivedSequence!! &&
                highestReceivedSequence!! - packet.sequence >= ReceivedSequenceWindow.toLong()
            )
        rememberReceived(packet.sequence)
        if (duplicate) return CultNetRudpReceiveResult(readyToSend = readyToSend)
        val reassembled = reassemble(packet) ?: return CultNetRudpReceiveResult(readyToSend = readyToSend)
        return CultNetRudpReceiveResult(
            delivered = if (reassembled.ordered) deliverOrdered(reassembled.frame, reassembled.nextSequence, expectedSequenceIfUninitialized) else listOf(reassembled.frame),
            readyToSend = readyToSend,
        )
    }

    fun createAck(): CultNetRudpPacket = createPacket(CultNetRudpPacketType.Ack, "control", ByteArray(0))

    fun createAckFor(sequence: Long): CultNetRudpPacket = CultNetRudpPacket(
        packetType = CultNetRudpPacketType.Ack,
        connectionId = connectionId,
        sequence = 0,
        ack = uint32(sequence, "ack sequence"),
        ackMask = 0,
        channelId = "control",
    )

    fun createPing(payload: ByteArray = ByteArray(0)): CultNetRudpPacket =
        createPacket(CultNetRudpPacketType.Ping, "control", payload)

    fun createDisconnect(reason: ByteArray = ByteArray(0)): CultNetRudpPacket {
        connected = false
        return createPacket(CultNetRudpPacketType.Disconnect, "control", reason)
    }

    fun checkTimeout(nowMs: Long, timeoutMs: Long): Boolean {
        val lastReceived = lastReceivedAtMs ?: return false
        if (!connected || nowMs - lastReceived <= timeoutMs) return false
        connected = false
        return true
    }

    fun dueResends(nowMs: Long): List<CultNetRudpPacket> =
        pendingReliable.values
            .filter { nowMs - it.lastSentAtMs >= resendDelayMs }
            .onEach { it.lastSentAtMs = nowMs }
            .map { it.packet.copy(payload = it.packet.payload.copyOf()) }
            .sortedBy { it.sequence }

    private fun createPacket(
        packetType: CultNetRudpPacketType,
        channelId: String,
        payload: ByteArray,
        reliable: Boolean = false,
        ordered: Boolean = false,
        sequenced: Boolean = false,
        fragmentId: Int = 0,
        fragmentIndex: Int = 0,
        fragmentCount: Int = 0,
    ): CultNetRudpPacket {
        val sequence = nextSequence
        nextSequence = uint32(nextSequence + 1, "sequence")
        val (ack, ackMask) = ackState()
        return CultNetRudpPacket(packetType, connectionId, sequence, ack, ackMask, channelId, reliable, ordered, sequenced, fragmentId, fragmentIndex, fragmentCount, payload.copyOf())
    }

    private fun trackReliable(packet: CultNetRudpPacket, nowMs: Long) {
        pendingReliable[packet.sequence] = PendingReliablePacket(packet.copy(payload = packet.payload.copyOf()), nowMs)
    }

    private fun admitReliablePackets(packets: List<CultNetRudpPacket>, nowMs: Long): List<CultNetRudpPacket> {
        val available = maxOf(0, ReliableSendWindowPackets - pendingReliable.size)
        val ready = packets.take(available)
        ready.forEach { trackReliable(it, nowMs) }
        packets.drop(available).forEach { queuedReliable.addLast(it.copy(payload = it.payload.copyOf())) }
        return ready
    }

    private fun promoteQueuedReliable(nowMs: Long): List<CultNetRudpPacket> {
        val available = maxOf(0, ReliableSendWindowPackets - pendingReliable.size)
        val ready = mutableListOf<CultNetRudpPacket>()
        while (ready.size < available && !queuedReliable.isEmpty()) {
            val packet = queuedReliable.removeFirst()
            trackReliable(packet, nowMs)
            ready.add(packet)
        }
        return ready
    }

    private fun ensureReliableCapacity(packetCount: Int) {
        if (packetCount == 0 || maxPendingReliablePackets == null) return
        if (outstandingReliablePacketCount + packetCount > maxPendingReliablePackets) {
            throw IOException("RUDP reliable send queue is full")
        }
    }

    private fun applyAcknowledgements(packet: CultNetRudpPacket) {
        pendingReliable.remove(packet.ack)
        for (bit in 0 until 32) {
            if ((packet.ackMask and (1L shl bit)) != 0L && packet.ack > bit) {
                pendingReliable.remove(packet.ack - bit - 1)
            }
        }
    }

    private fun rememberReceived(sequence: Long) {
        receivedSequences.add(sequence)
        if (highestReceivedSequence == null || sequence > highestReceivedSequence!!) highestReceivedSequence = sequence
        if (receivedSequences.size > ReceivedSequenceWindow) {
            val keepFrom = maxOf(0L, (highestReceivedSequence ?: sequence) - ReceivedSequenceWindow.toLong() + 1)
            receivedSequences.removeIf { it < keepFrom }
        }
    }

    private fun ackState(): Pair<Long, Long> {
        val ack = highestReceivedSequence ?: 0
        var ackMask = 0L
        for (bit in 0 until 32) {
            if (ack > bit && receivedSequences.contains(ack - bit - 1)) ackMask = ackMask or (1L shl bit)
        }
        return ack to ackMask
    }

    private data class ReassembledFrame(val frame: CultNetRudpDeliveredFrame, val ordered: Boolean, val nextSequence: Long)

    private fun reassemble(packet: CultNetRudpPacket): ReassembledFrame? {
        if (packet.fragmentCount == 0) {
            return ReassembledFrame(CultNetRudpDeliveredFrame(packet.channelId, packet.payload.copyOf(), packet.sequence), packet.ordered, packet.sequence + 1)
        }
        if (packet.fragmentId == 0) throw IOException("RUDP fragmented packet must have a non-zero fragment id")
        if (packet.fragmentIndex >= packet.fragmentCount) throw IOException("RUDP fragment index must be lower than fragment count")
        val key = packet.channelId to packet.fragmentId
        val buffer = fragmentBuffers.getOrPut(key) {
            FragmentBuffer(packet.channelId, packet.ordered, packet.fragmentCount)
        }
        if (buffer.fragmentCount != packet.fragmentCount || buffer.ordered != packet.ordered) {
            throw IOException("RUDP fragment metadata changed within a fragment set")
        }
        buffer.payloads[packet.fragmentIndex] = packet.payload.copyOf()
        buffer.sequences[packet.fragmentIndex] = packet.sequence
        if (buffer.payloads.size < packet.fragmentCount) return null
        val payload = ByteArray(buffer.payloads.values.sumOf { it.size })
        var offset = 0
        for (index in 0 until packet.fragmentCount) {
            val chunk = buffer.payloads[index] ?: return null
            chunk.copyInto(payload, offset)
            offset += chunk.size
        }
        val sequences = buffer.sequences.values
        fragmentBuffers.remove(key)
        return ReassembledFrame(CultNetRudpDeliveredFrame(buffer.channelId, payload, sequences.minOrNull() ?: packet.sequence), buffer.ordered, (sequences.maxOrNull() ?: packet.sequence) + 1)
    }

    private fun deliverOrdered(
        frame: CultNetRudpDeliveredFrame,
        nextSequenceAfterFrame: Long,
        expectedSequenceIfUninitialized: Long,
    ): List<CultNetRudpDeliveredFrame> {
        var next = orderedNextSequenceByChannel[frame.channelId] ?: minOf(expectedSequenceIfUninitialized, frame.sequence).also {
            orderedNextSequenceByChannel[frame.channelId] = it
        }
        while (frame.sequence > next && receivedSequences.contains(next) && orderedBuffers[frame.channelId]?.containsKey(next) != true) {
            next += 1
            orderedNextSequenceByChannel[frame.channelId] = next
        }
        if (frame.sequence < next) return emptyList()
        if (frame.sequence > next) {
            orderedBuffers.getOrPut(frame.channelId) { TreeMap() }[frame.sequence] = PendingOrderedFrame(frame, nextSequenceAfterFrame)
            return emptyList()
        }
        orderedNextSequenceByChannel[frame.channelId] = nextSequenceAfterFrame
        return listOf(frame) + drainOrdered(frame.channelId)
    }

    private fun drainOrdered(channelId: String): List<CultNetRudpDeliveredFrame> {
        val delivered = mutableListOf<CultNetRudpDeliveredFrame>()
        val buffer = orderedBuffers[channelId] ?: return delivered
        while (true) {
            val next = orderedNextSequenceByChannel[channelId] ?: break
            val pending = buffer.remove(next) ?: break
            delivered.add(pending.frame)
            orderedNextSequenceByChannel[channelId] = pending.nextSequence
            skipReceivedNonChannelSequences(channelId)
        }
        return delivered
    }

    private fun skipReceivedNonChannelSequences(channelId: String) {
        var next = orderedNextSequenceByChannel[channelId] ?: return
        while (receivedSequences.contains(next) && orderedBuffers[channelId]?.containsKey(next) != true) {
            next += 1
            orderedNextSequenceByChannel[channelId] = next
        }
    }

    private fun allocateFragmentId(): Int {
        val fragmentId = nextFragmentId
        nextFragmentId += 1
        if (nextFragmentId > 0xffff) nextFragmentId = 1
        return fragmentId
    }

    private fun requireConnection(packet: CultNetRudpPacket) {
        if (packet.connectionId != connectionId) throw IOException("RUDP packet connection id ${packet.connectionId} does not match $connectionId")
    }
}

enum class CultNetRudpSocketMode { Client, Server }

data class CultNetRudpSocketTuning(
    val initialSequence: Long = 1,
    val resendDelayMs: Long = 250,
    val maxFragmentBytes: Int? = null,
    val maxPendingReliablePackets: Int? = null,
)

fun cultNetRudpEndpoint(endpoint: String): CultNetRudpEndpoint {
    val uri = URI(endpoint)
    if (!uri.scheme.equals("rudp", ignoreCase = true)) throw IOException("RUDP endpoint must use the rudp scheme")
    val host = uri.host ?: throw IOException("RUDP endpoint must include a host")
    val port = uri.port
    if (port <= 0 || port > 65535) throw IOException("RUDP endpoint must include a valid port")
    return CultNetRudpEndpoint(host, port)
}

fun cultNetRudpServer(
    runtimeId: String,
    connectionId: Long,
    bindHost: String = "127.0.0.1",
    bindPort: Int = 0,
    tuning: CultNetRudpSocketTuning = CultNetRudpSocketTuning(initialSequence = 100),
): CultNetRudpSocketTransportConnection {
    val socket = DatagramSocket(bindPort, InetAddress.getByName(bindHost)).also { it.soTimeout = 20 }
    return CultNetRudpSocketTransportConnection(
        socket = socket,
        mode = CultNetRudpSocketMode.Server,
        runtimeId = runtimeId,
        connectionId = connectionId,
        initialSequence = tuning.initialSequence,
        resendDelayMs = tuning.resendDelayMs,
        maxFragmentBytes = tuning.maxFragmentBytes,
        maxPendingReliablePackets = tuning.maxPendingReliablePackets,
    )
}

fun cultNetRudpClient(
    runtimeId: String,
    connectionId: Long,
    remoteHost: String,
    remotePort: Int,
    bindHost: String = "127.0.0.1",
    bindPort: Int = 0,
    tuning: CultNetRudpSocketTuning = CultNetRudpSocketTuning(),
): CultNetRudpSocketTransportConnection {
    val socket = DatagramSocket(bindPort, InetAddress.getByName(bindHost)).also { it.soTimeout = 20 }
    return CultNetRudpSocketTransportConnection(
        socket = socket,
        mode = CultNetRudpSocketMode.Client,
        runtimeId = runtimeId,
        connectionId = connectionId,
        remoteAddress = InetSocketAddress(InetAddress.getByName(remoteHost), remotePort),
        initialSequence = tuning.initialSequence,
        resendDelayMs = tuning.resendDelayMs,
        maxFragmentBytes = tuning.maxFragmentBytes,
        maxPendingReliablePackets = tuning.maxPendingReliablePackets,
    )
}

fun pumpRudpPairUntilConnected(
    first: CultNetRudpSocketTransportConnection,
    second: CultNetRudpSocketTransportConnection,
    timeoutMs: Long = 1_000,
    pollIntervalMs: Long = 5,
): Boolean {
    val deadline = System.nanoTime() + timeoutMs * 1_000_000L
    while (System.nanoTime() < deadline) {
        first.receiveOnce()
        second.receiveOnce()
        first.pollResends()
        second.pollResends()
        if (first.connected && second.connected) return true
        Thread.sleep(pollIntervalMs)
    }
    return first.connected && second.connected
}

class CultNetRudpSocketTransportConnection(
    private val socket: DatagramSocket,
    private val mode: CultNetRudpSocketMode,
    runtimeId: String,
    connectionId: Long,
    remoteAddress: InetSocketAddress? = null,
    initialSequence: Long = 1,
    resendDelayMs: Long = 250,
    private val maxFragmentBytes: Int? = null,
    maxPendingReliablePackets: Int? = null,
) : CultNetSchemaMessageTransport {
    private val session = CultNetRudpSession(CultNetRudpSessionOptions(connectionId, initialSequence, resendDelayMs, maxPendingReliablePackets))
    private var remote = remoteAddress
    private val delivered = ArrayDeque<CultNetTransportFrame>()
    private val pongPayloads = ArrayDeque<ByteArray>()
    private var bytesReceived = 0L
    private var bytesSent = 0L
    private var framesReceived = 0L
    private var framesSent = 0L
    var disconnectReason: ByteArray? = null
        private set

    override val profile: CultNetTransportProfile = createRudpTransportProfile(
        runtimeId = runtimeId,
        host = socket.localAddress.hostAddress,
        port = socket.localPort,
        maxFragmentBytes = maxFragmentBytes,
        maxPendingReliablePackets = maxPendingReliablePackets,
    )
    val connected: Boolean get() = session.connected
    val outstandingReliablePacketCount: Int get() = session.outstandingReliablePacketCount
    val localPort: Int get() = socket.localPort
    override val stats: CultNetTransportStats get() = CultNetTransportStats(bytesReceived, bytesSent, framesReceived, framesSent)

    fun connect(payload: ByteArray = ByteArray(0)) {
        if (mode != CultNetRudpSocketMode.Client) throw IOException("Only a client RUDP socket transport can initiate connect")
        sendPacket(session.createConnect(nowMs(), payload))
    }

    fun connect(payload: String) = connect(payload.toByteArray(StandardCharsets.UTF_8))

    fun connectAndWait(payload: ByteArray = ByteArray(0), timeoutMs: Long = 1_000, pollIntervalMs: Long = 5): Boolean {
        connect(payload)
        return awaitConnected(timeoutMs, pollIntervalMs)
    }

    fun connectAndWait(payload: String, timeoutMs: Long = 1_000, pollIntervalMs: Long = 5): Boolean =
        connectAndWait(payload.toByteArray(StandardCharsets.UTF_8), timeoutMs, pollIntervalMs)

    fun awaitConnected(timeoutMs: Long = 1_000, pollIntervalMs: Long = 5): Boolean {
        val deadline = System.nanoTime() + timeoutMs * 1_000_000L
        while (System.nanoTime() < deadline) {
            receiveOnce()
            pollResends()
            if (connected) return true
            Thread.sleep(pollIntervalMs)
        }
        return connected
    }

    fun send(channelId: String, payload: ByteArray) {
        session.sendMany(channelId, payload, channelSendOptions(channelId, nowMs()), maxFragmentBytes).forEach { sendPacket(it) }
        framesSent += 1
    }

    fun send(channelId: String, payload: String) = send(channelId, payload.toByteArray(StandardCharsets.UTF_8))

    fun sendSchema(payload: ByteArray) = send("schema", payload)

    fun sendSchema(payload: String) = sendSchema(payload.toByteArray(StandardCharsets.UTF_8))

    override fun sendSchemaMessage(message: CultNetMessage) = sendSchema(message.toBytes())

    fun sendLatest(payload: ByteArray) = send("latest", payload)

    fun sendLatest(payload: String) = sendLatest(payload.toByteArray(StandardCharsets.UTF_8))

    fun sendRealtime(payload: ByteArray) = send("realtime", payload)

    fun sendRealtime(payload: String) = sendRealtime(payload.toByteArray(StandardCharsets.UTF_8))

    fun disconnect(reason: ByteArray = ByteArray(0)) {
        sendPacket(session.createDisconnect(reason))
    }

    fun disconnect(reason: String) = disconnect(reason.toByteArray(StandardCharsets.UTF_8))

    fun ping(payload: ByteArray = ByteArray(0)) {
        sendPacket(session.createPing(payload))
    }

    fun ping(payload: String) = ping(payload.toByteArray(StandardCharsets.UTF_8))

    fun pollPongPayload(): ByteArray? = if (pongPayloads.isEmpty()) null else pongPayloads.removeFirst()

    fun checkTimeout(timeoutMs: Long): Boolean = session.checkTimeout(nowMs(), timeoutMs)

    fun receiveUntil(timeoutMs: Long, pollIntervalMs: Long = 5, predicate: (CultNetTransportFrame) -> Boolean = { true }): CultNetTransportFrame? {
        val deadline = System.nanoTime() + timeoutMs * 1_000_000L
        while (System.nanoTime() < deadline) {
            val frame = receiveOnce()
            if (frame != null && predicate(frame)) return frame
            pollResends()
            Thread.sleep(pollIntervalMs)
        }
        return null
    }

    fun receiveSchema(timeoutMs: Long, pollIntervalMs: Long = 5): ByteArray? =
        receiveUntil(timeoutMs, pollIntervalMs) { it.channelId == "schema" }?.payload

    override fun receiveSchemaMessage(timeoutMs: Long, pollIntervalMs: Long): CultNetMessage? =
        receiveSchema(timeoutMs, pollIntervalMs)?.let { parseCultNetMessage(it) }

    fun flushReliable(timeoutMs: Long = 30_000, pollIntervalMs: Long = 5) {
        val deadline = System.nanoTime() + timeoutMs.coerceAtLeast(0) * 1_000_000L
        val preserved = ArrayDeque<CultNetTransportFrame>()
        while (!delivered.isEmpty()) preserved.addLast(delivered.removeFirst())
        try {
            while (session.outstandingReliablePacketCount > 0) {
                if (System.nanoTime() >= deadline) {
                    throw SocketTimeoutException(
                        "RUDP reliable flush timed out with ${session.outstandingReliablePacketCount} packets outstanding"
                    )
                }
                receiveOnce()?.let { preserved.addLast(it) }
                pollResends()
                Thread.sleep(pollIntervalMs)
            }
        } finally {
            while (!delivered.isEmpty()) preserved.addLast(delivered.removeFirst())
            while (!preserved.isEmpty()) delivered.addLast(preserved.removeFirst())
        }
    }

    fun receiveOnce(): CultNetTransportFrame? {
        if (!delivered.isEmpty()) return delivered.removeFirst()
        val buffer = ByteArray(65535)
        val datagram = DatagramPacket(buffer, buffer.size)
        try {
            socket.receive(datagram)
        } catch (_: SocketTimeoutException) {
            return null
        }
        bytesReceived += datagram.length.toLong()
        val remoteNow = InetSocketAddress(datagram.address, datagram.port)
        if (remote == null) {
            remote = remoteNow
        } else if (remote != remoteNow) {
            return null
        }
        val packet = decodeRudpPacket(buffer.copyOf(datagram.length))
        if (mode == CultNetRudpSocketMode.Server && packet.packetType == CultNetRudpPacketType.Connect) {
            sendPacket(session.acceptConnect(packet, nowMs()))
            return null
        }
        val result = session.receive(packet, nowMs())
        result.reply?.let { sendPacket(it) }
        result.readyToSend.forEach { sendPacket(it) }
        if (result.pong) {
            pongPayloads.add(result.pongPayload.copyOf())
        }
        if (result.disconnected) {
            disconnectReason = result.disconnectReason.copyOf()
            return null
        }
        result.delivered.forEach {
            delivered.add(CultNetTransportFrame(it.channelId, it.payload))
            framesReceived += 1
        }
        val frame = if (delivered.isEmpty()) null else delivered.removeFirst()
        if (packet.reliable || packet.packetType == CultNetRudpPacketType.Accept || frame != null) sendPacket(session.createAckFor(packet.sequence))
        return frame
    }

    fun pollResends() {
        session.dueResends(nowMs()).forEach { sendPacket(it) }
    }

    private fun sendPacket(packet: CultNetRudpPacket) {
        val target = remote ?: throw IOException("RUDP socket transport does not have a remote endpoint")
        val wire = encodeRudpPacket(packet)
        socket.send(DatagramPacket(wire, wire.size, target.address, target.port))
        bytesSent += wire.size.toLong()
    }

    override fun close() {
        socket.close()
    }
}

private val rudpMagic = byteArrayOf(0x43, 0x4e, 0x52, 0x30)
private const val rudpVersion = 0
private const val rudpFixedHeaderBytes = 36

fun encodeRudpPacket(packet: CultNetRudpPacket): ByteArray {
    val channelId = packet.channelId.toByteArray(StandardCharsets.UTF_8)
    if (channelId.size > 255) throw IOException("CultNet RUDP channel id cannot exceed 255 UTF-8 bytes")
    val headerBytes = rudpFixedHeaderBytes + channelId.size
    val payload = packet.payload
    val wire = ByteArray(headerBytes + payload.size)
    val view = ByteBuffer.wrap(wire)
    view.put(rudpMagic)
    view.put(rudpVersion.toByte())
    view.put(packet.packetType.code.toByte())
    view.put(encodeRudpFlags(packet).toByte())
    view.put(headerBytes.toByte())
    view.putInt(uint32(packet.connectionId, "connectionId").toInt())
    view.putInt(uint32(packet.sequence, "sequence").toInt())
    view.putInt(uint32(packet.ack, "ack").toInt())
    view.putInt(uint32(packet.ackMask, "ackMask").toInt())
    view.putShort(uint16(packet.fragmentId, "fragmentId").toShort())
    view.putShort(uint16(packet.fragmentIndex, "fragmentIndex").toShort())
    view.putShort(uint16(packet.fragmentCount, "fragmentCount").toShort())
    view.putInt(payload.size)
    view.put(channelId.size.toByte())
    view.put(0)
    view.put(channelId)
    view.put(payload)
    return wire
}

fun decodeRudpPacket(wire: ByteArray): CultNetRudpPacket {
    if (wire.size < rudpFixedHeaderBytes) throw IOException("CultNet RUDP packet is shorter than the fixed header")
    if (!wire.copyOfRange(0, 4).contentEquals(rudpMagic)) throw IOException("CultNet RUDP packet has the wrong magic")
    val view = ByteBuffer.wrap(wire)
    if ((wire[4].toInt() and 0xff) != rudpVersion) throw IOException("Unsupported CultNet RUDP packet version ${wire[4].toInt() and 0xff}")
    val type = CultNetRudpPacketType.fromCode(wire[5].toInt() and 0xff)
    val flags = wire[6].toInt() and 0xff
    val headerBytes = wire[7].toInt() and 0xff
    val channelIdLength = wire[34].toInt() and 0xff
    if (headerBytes != rudpFixedHeaderBytes + channelIdLength) throw IOException("CultNet RUDP packet header length does not match the channel id length")
    val payloadLength = view.getInt(30)
    if (wire.size != headerBytes + payloadLength) throw IOException("CultNet RUDP packet payload length does not match the packet size")
    return CultNetRudpPacket(
        packetType = type,
        connectionId = view.getInt(8).toLong() and 0xffffffffL,
        sequence = view.getInt(12).toLong() and 0xffffffffL,
        ack = view.getInt(16).toLong() and 0xffffffffL,
        ackMask = view.getInt(20).toLong() and 0xffffffffL,
        fragmentId = view.getShort(24).toInt() and 0xffff,
        fragmentIndex = view.getShort(26).toInt() and 0xffff,
        fragmentCount = view.getShort(28).toInt() and 0xffff,
        channelId = String(wire, rudpFixedHeaderBytes, channelIdLength, StandardCharsets.UTF_8),
        reliable = (flags and 0b0000_0001) != 0,
        ordered = (flags and 0b0000_0010) != 0,
        sequenced = (flags and 0b0000_0100) != 0,
        payload = wire.copyOfRange(headerBytes, wire.size),
    )
}

private fun encodeRudpFlags(packet: CultNetRudpPacket): Int =
    (if (packet.reliable) 0b0000_0001 else 0) or
        (if (packet.ordered) 0b0000_0010 else 0) or
        (if (packet.sequenced) 0b0000_0100 else 0) or
        (if (packet.fragmentCount > 0) 0b0000_1000 else 0)

private fun channelSendOptions(channelId: String, nowMs: Long): CultNetRudpSendOptions = when (channelId) {
    "schema" -> CultNetRudpSendOptions(reliable = true, ordered = true, nowMs = nowMs)
    "latest" -> CultNetRudpSendOptions(sequenced = true, nowMs = nowMs)
    else -> CultNetRudpSendOptions(nowMs = nowMs)
}

private fun uint32(value: Long, fieldName: String): Long {
    if (value < 0 || value > 0xffffffffL) throw IOException("CultNet RUDP $fieldName must fit in uint32")
    return value
}

private fun uint16(value: Int, fieldName: String): Int {
    if (value < 0 || value > 0xffff) throw IOException("CultNet RUDP $fieldName must fit in uint16")
    return value
}

private fun nowMs(): Long = Instant.now().toEpochMilli()

fun main(args: Array<String>) {
    if (args.isEmpty()) {
        cultCacheErgonomicsCoverTypedDocumentsAndGlobals()
        cultMeshFacadeRoutesErgonomicEntrypoints()
        cultCacheRawSnapshotsRoundTripThroughCultNetMessages()
        cultNetSchemaMessagesUseMessagePackMaps()
        cultNetReconnectPolicyExposesPortableDelayContract()
        cultNetReconnectControllerSchedulesAttemptsAndReset()
        cultNetRudpReconnectLoopConsumesSharedController()
        cultNetWebSocketTransportCarriesSchemaFramesWithStats()
        cultNetBuiltInSchemaCatalogAdvertisesWireContracts()
        cultNetSchemaCatalogsRoundTripDescriptors()
        cultMeshCatalogsRoundTripDiscoveryMessages()
        cultMeshAuthorityLeasesGatePeerTrust()
        cultMeshStreamCatalogNegotiatesBodyTransports()
        cultNetShardCatalogsAndLogsRoundTrip()
        rudpPacketCodecUsesDeterministicReliableOrderedFixture()
        rudpSessionPingsAndDetectsReceiveTimeout()
        rudpSessionBoundsPendingReliablePacketsBeforeEnqueue()
        rudpSessionFragmentsAndReassemblesReliableOrderedPayloads()
        rudpSessionAdvancesLargeFragmentSetsThroughBoundedReliableWindow()
        rudpSocketTransportErgonomicFactoriesCarrySchemaFrames()
        rudpSocketTransportHandshakesAndCarriesReliableOrderedSchemaFrames()
        rudpSocketTransportCarriesFragmentedReliableOrderedSchemaFrames()
        return
    }

    val options = parseArgs(args.drop(1))
    when (args[0]) {
        "rudp-serve-once" -> rudpServeOnce(options)
        "rudp-dial-once" -> rudpDialOnce(options)
        "rudp-serve-message-once" -> rudpServeSchemaMessageOnce(options)
        "rudp-dial-message-once" -> rudpDialSchemaMessageOnce(options)
        "serve" -> interopServe(options)
        "dial" -> interopDial(options)
        else -> error("Unknown mode ${args[0]}")
    }
}

private fun cultCacheErgonomicsCoverTypedDocumentsAndGlobals() {
    val notes = stringDocument("kotlin.note", "kotlin.note.v1")
    val settings = stringDocument("kotlin.settings", "kotlin.settings.v1", global = true)
    val blobs = bytesDocument("kotlin.blob", "kotlin.blob.v1")
    val cache = CultCache()

    cache.put(notes, "note:1", "hello")
    check(cache.getRequired(notes, "note:1") == "hello")
    val handle = cache.document(notes, "note:2")
    handle.put("second")
    check(handle.require() == "second")
    check(cache.getAll(notes).map { it.key to it.value } == listOf("note:1" to "hello", "note:2" to "second"))
    check(cache.delete(notes, "note:1"))
    check(cache.get(notes, "note:1") == null)

    cache.putGlobal(settings, "dark-mode")
    check(cache.global(settings).require() == "dark-mode")
    check(cache.deleteGlobal(settings))
    check(cache.getGlobal(settings) == null)

    val payload = byteArrayOf(1, 2, 3)
    cache.put(blobs, "blob:1", payload)
    payload[0] = 9
    check(cache.getRequired(blobs, "blob:1").contentEquals(byteArrayOf(1, 2, 3)))

    val node = CultMeshNode()
    node.remember(notes, "node-note", "remembered")
    check(node.require(notes, "node-note") == "remembered")
    check(node.forget(notes, "node-note"))
}

private fun cultMeshFacadeRoutesErgonomicEntrypoints() {
    val notes = stringDocument("kotlin.facade_note", "kotlin.facade_note.v1")
    val cache = CultCache()
    cache.register(notes)
    val node = CultMesh.startNode(cache)
    node.remember(notes, "note:facade", "facade")
    check(node.require(notes, "note:facade") == "facade")

    val verses = CultMesh.createVerseCatalog()
    verses.upsert(
        CultMeshVerseDescriptor(
            verseId = "facade",
            displayName = "Facade Verse",
            authorityModel = "federated",
            compatibility = CultMeshVerseCompatibility("cultmesh.v0", "rules"),
        ),
    )
    check(verses.get("facade")?.displayName == "Facade Verse")

    val peers = CultMesh.createPeerCatalog()
    peers.upsert(CultMeshPeerCard("facade-peer", "facade", listOf("rudp://127.0.0.1:4100"), roles = listOf("schema")))
    check(peers.find("facade", "schema").single().peerId == "facade-peer")

    val leases = CultMesh.createAuthorityLeaseCatalog()
    check(leases.leases.isEmpty())

    val streams = CultMesh.createStreamCatalog()
    check(streams.streams.isEmpty())

    val schemas = CultMesh.createSchemaCatalog()
    schemas.upsert(
        defineCultNetSchemaDescriptor(
            schemaId = notes.schemaVersion,
            kind = "document_payload",
            documentType = notes.documentType,
            title = "Facade Note",
            schemaJson = """{"type":"string"}""",
        ),
    )
    check(schemas.get(notes.schemaVersion)?.documentType == notes.documentType)

    val shards = CultMesh.createShardCatalog()
    shards.upsert(
        CultNetShardDescriptor(
            shardId = "facade-shard",
            ownerRuntimeId = "facade-owner",
            epoch = 1,
            isPrimary = true,
            schemaIds = listOf(notes.schemaVersion),
            keyPrefix = "note:",
        ),
    )
    check(
        shards.list(
            schemaIds = listOf(notes.schemaVersion),
            recordKeys = listOf("note:facade"),
        ).single().shardId == "facade-shard",
    )
}

private fun cultCacheRawSnapshotsRoundTripThroughCultNetMessages() {
    val notes = stringDocument("kotlin.note", "kotlin.note.v1")
    val settings = stringDocument("kotlin.settings", "kotlin.settings.v1", global = true)
    val source = CultMeshNode()
    val target = CultMeshNode()
    source.cache.register(notes)
    source.cache.register(settings)
    target.cache.register(notes)
    target.cache.register(settings)

    source.remember(notes, "note:1", "first")
    source.remember(notes, "note:2", "second")
    source.rememberGlobal(settings, "dark-mode")

    val snapshotRequest = cultNetSnapshotRequest(
        messageId = "snapshot-notes",
        schemaIds = listOf(notes.schemaVersion),
        recordKeys = listOf("note:2"),
    )
    val snapshotResponse = parseCultNetMessage(
        source.createRawSnapshotResponse(
            snapshotRequest,
            storedAt = "2026-06-15T00:00:00Z",
            sourceRuntimeId = "kotlin-source",
        ).toBytes(),
    )
    check(snapshotResponse.schemaVersion == "cultnet.snapshot_response_raw.v0")
    val applied = target.applyRawSnapshotResponse(snapshotResponse)
    check(applied == listOf("second"))
    check(target.recall(notes, "note:1") == null)
    check(target.require(notes, "note:2") == "second")
    check(target.recallGlobal(settings) == null)

    val canonicalNote = cultDocument(KotlinAliasNoteCodec(
        documentType = "kotlin.alias_note",
        schemaVersion = "kotlin.alias_note.v1",
        create = ::KotlinCanonicalNote,
        readSchemaVersion = KotlinCanonicalNote::schemaVersion,
        readBody = KotlinCanonicalNote::body,
    ))
    val uiNote = cultDocument(KotlinAliasNoteCodec(
        documentType = "kotlin.alias_note.ui",
        schemaVersion = "kotlin.alias_note.v1",
        create = ::KotlinUiNote,
        readSchemaVersion = KotlinUiNote::schemaVersion,
        readBody = KotlinUiNote::body,
    ))
    source.cache.register(canonicalNote)
    target.cache.register(uiNote)
    source.remember(canonicalNote, "note:alias", KotlinCanonicalNote("kotlin.alias_note.v1", "canonical-to-ui"))
    val aliasSnapshotResponse = source.createRawSnapshotResponse(
        cultNetSnapshotRequest(
            messageId = "snapshot-alias-note",
            schemaIds = listOf(uiNote.schemaVersion),
            recordKeys = listOf("note:alias"),
        ),
        storedAt = "2026-06-15T00:00:02Z",
        sourceRuntimeId = "kotlin-source",
    )
    val syncedAlias = target.syncDocument(aliasSnapshotResponse, uiNote, "note:alias")
    check(syncedAlias == KotlinUiNote("kotlin.alias_note.v1", "canonical-to-ui"))
    check(target.require(uiNote, "note:alias") == syncedAlias)

    val reactiveUiNote = cultDocument(KotlinReactiveNoteCodec(
        documentType = "kotlin.reactive_note.ui",
        schemaVersion = "kotlin.reactive_note.v1",
    ))
    target.cache.register(reactiveUiNote)
    target.remember(reactiveUiNote, "note:reactive", KotlinReactiveNote("initial", 1))
    CultMesh.reactiveDocument(
        target,
        reactiveUiNote,
        "note:reactive",
        CultReactiveDocumentOptions(flushDelayMs = 5),
    ).use { reactive ->
        reactive.current.body = "first-local-edit"
        reactive.current.body = "second-local-edit"
        reactive.current.revision = 2
        val deadline = System.nanoTime() + 500_000_000L
        while (System.nanoTime() < deadline && target.require(reactiveUiNote, "note:reactive").body != "second-local-edit") {
            Thread.sleep(5)
        }
        check(target.require(reactiveUiNote, "note:reactive") == KotlinReactiveNote("second-local-edit", 2))
        check(!reactive.isDirty)
        check(reactive.lastError == null)
    }

    val reactiveCanonicalNote = cultDocument(KotlinReactiveNoteCodec(
        documentType = "kotlin.reactive_note",
        schemaVersion = "kotlin.reactive_note.v1",
    ))
    target.cache.register(reactiveCanonicalNote)
    target.remember(reactiveCanonicalNote, "note:reconcile", KotlinReactiveNote("initial", 1))
    target.reactiveDocument(
        reactiveCanonicalNote,
        "note:reconcile",
        CultReactiveDocumentOptions(flushDelayMs = 60_000, detectLocalChanges = false),
    ).use { reactive ->
        reactive.update {
            it.body = "local-prediction"
            it.revision = 2
            it
        }
        val authoritative = cultNetDocumentPutRaw(
            messageId = "put-authoritative-note",
            document = CultNetRawDocumentRecord(
                schemaId = reactiveCanonicalNote.schemaVersion,
                recordKey = "note:reconcile",
                storedAt = "2026-06-15T00:00:03Z",
                payload = reactiveCanonicalNote.codec.encode(KotlinReactiveNote("authoritative", 7)),
                sourceRuntimeId = "kotlin-authority",
            ),
        )
        reactive.applyRawDocumentPut(authoritative)
        check(reactive.current == KotlinReactiveNote("local-prediction", 2))
        val reconciliation = reactive.reconciliation ?: error("Expected Kotlin reactive reconciliation")
        check(reconciliation.canonical == KotlinReactiveNote("authoritative", 7))
        check(reconciliation.predicted == KotlinReactiveNote("local-prediction", 2))
        check(reconciliation.delta["0"] == "local-prediction")
        check(reconciliation.delta["1"] == -5.0)
        reactive.flush()
        check(target.require(reactiveCanonicalNote, "note:reconcile") == KotlinReactiveNote("local-prediction", 2))
        check(reactive.reconciliation == null)
    }

    val rawPut = parseCultNetMessage(
        cultNetDocumentPutRaw(
            messageId = "put-note",
            document = CultNetRawDocumentRecord(
                schemaId = notes.schemaVersion,
                recordKey = "note:3",
                storedAt = "2026-06-15T00:00:01Z",
                payload = notes.codec.encode("third"),
                sourceRuntimeId = "kotlin-source",
            ),
        ).toBytes(),
    )
    check(target.applyRawDocumentPut(rawPut) == "third")
    check(target.require(notes, "note:3") == "third")

    val delete = cultNetDocumentDelete(
        messageId = "delete-note",
        schemaId = notes.schemaVersion,
        recordKey = "note:2",
    )
    check(target.applyDocumentDelete(delete))
    check(target.recall(notes, "note:2") == null)
}

private fun cultNetSchemaMessagesUseMessagePackMaps() {
    val hello = cultNetHello(
        runtimeId = "kotlin-peer",
        displayName = "Kotlin Peer",
        supportedDocumentTypes = listOf("gamecult.note"),
        supportedMessageVersions = listOf("cultnet.hello.v0", "cultnet.schema_catalog_request.v0"),
        transportProfiles = listOf(createRudpTransportProfile("kotlin-peer", port = 4000, maxPendingReliablePackets = 32)),
    )
    val parsedHello = parseCultNetMessage(hello.toBytes())
    check(parsedHello.schemaVersion == "cultnet.hello.v0")
    check(parsedHello.body["runtimeId"] == "kotlin-peer")
    check(parsedHello.body["supportsSchemaCatalog"] == true)
    val profiles = parsedHello.body["transportProfiles"] as List<*>
    val profile = profiles.single() as Map<*, *>
    check(profile["schemaVersion"] == "cultnet.transport_profile.v0")
    val transports = profile["transports"] as List<*>
    val transport = transports.single() as Map<*, *>
    check(transport["protocol"] == "rudp")
    val reconnectPolicy = transport["reconnectPolicy"] as Map<*, *>
    check(reconnectPolicy["schemaVersion"] == "cultnet.reconnect_policy.v0")
    check(reconnectPolicy["baseDelayMs"] == 1_000L)

    val payload = MessagePackWriter().map(1).string("body").string("wire smoke").toByteArray()
    val put = cultNetDocumentPutRaw(
        messageId = "kotlin-put",
        document = CultNetRawDocumentRecord(
            schemaId = "gamecult.note",
            recordKey = "note:1",
            storedAt = "2026-06-15T00:00:00Z",
            payload = payload,
            sourceRuntimeId = "kotlin-peer",
            tags = listOf("interop"),
        ),
    )
    val parsedPut = parseCultNetMessage(put.toBytes())
    check(parsedPut.schemaVersion == "cultnet.document_put_raw.v0")
    val document = parsedPut.body["document"] as Map<*, *>
    check(document["payloadEncoding"] == "messagepack")
    check((document["payload"] as ByteArray).contentEquals(payload))
}

private fun cultNetReconnectPolicyExposesPortableDelayContract() {
    val policy = createReconnectPolicy(policyId = "rudp-default", maxAttempts = 8)
    val wire = policy.toWireMap()

    check(policy.schemaVersion == "cultnet.reconnect_policy.v0")
    check(policy.policyId == "rudp-default")
    check(policy.maxAttempts == 8)
    check(wire["policyId"] == "rudp-default")
    check(wire["maxAttempts"] == 8)
    check(computeReconnectDelayMs(policy, 1) == 1_000L)
    check(computeReconnectDelayMs(policy, 3, 17) == 4_017L)
    check(computeReconnectDelayMs(policy, 9, 999) == 30_250L)
    check(computeReconnectDelayMs(policy, 0, -5) == 1_000L)
}

private fun cultNetReconnectControllerSchedulesAttemptsAndReset() {
    val controller = CultNetReconnectController(createReconnectPolicy(maxAttempts = 2))

    val first = controller.recordFailure(10_000)
    check(first.attempt == 1)
    check(first.shouldRetry)
    check(first.delayMs == 1_000L)
    check(first.nextAttemptAtMs == 11_000L)
    check(!first.exhausted)
    check(!controller.canAttempt(10_999))
    check(controller.canAttempt(11_000))

    val second = controller.recordFailure(11_000, 17)
    check(second.attempt == 2)
    check(second.delayMs == 2_017L)
    check(second.nextAttemptAtMs == 13_017L)
    check(second.shouldRetry)

    val exhausted = controller.recordFailure(13_017)
    check(exhausted.attempt == 2)
    check(!exhausted.shouldRetry)
    check(exhausted.delayMs == 0L)
    check(exhausted.nextAttemptAtMs == null)
    check(exhausted.exhausted)
    check(!controller.canAttempt(99_000))

    controller.reset()
    check(controller.attempt == 0)
    check(controller.nextAttemptAtMs == null)
    check(!controller.exhausted)
    check(controller.canAttempt(99_000))
}

private const val interopDocumentType = "cultnet.interop-note"
private const val interopSchemaVersion = "cultnet.interop_note.v0"
private const val interopMutationIntentType = "cultnet.interop-note-mutation-intent"
private const val interopMutationIntentSchemaId = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-note-mutation-intent.schema.json"
private const val interopMutationIntentSchemaVersion = "cultnet.interop_note_mutation_intent.v0"
private const val interopMutationReceiptType = "cultnet.interop-note-mutation-receipt"
private const val interopMutationReceiptSchemaId = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-note-mutation-receipt.schema.json"
private const val interopMutationReceiptSchemaVersion = "cultnet.interop_note_mutation_receipt.v0"
private const val interopFireCommandType = "cultnet.interop-fire-weapon-command"
private const val interopFireCommandSchemaId = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-fire-weapon-command.schema.json"
private const val interopFireCommandSchemaVersion = "cultnet.interop_fire_weapon_command.v0"
private const val interopFireReceiptType = "cultnet.interop-fire-weapon-receipt"
private const val interopFireReceiptSchemaId = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-fire-weapon-receipt.schema.json"
private const val interopFireReceiptSchemaVersion = "cultnet.interop_fire_weapon_receipt.v0"
private const val rudpInteropConnectionId = 0x43554c54L
private const val rudpInteropMaxFragmentBytes = 1024
private const val rudpInteropResendDelayMs = 25L
private val interopReadTimeoutMs = 15_000L

private data class InteropServeConfig(
    val runtimeId: String,
    val runtimeKind: String,
    val displayName: String,
    val agentId: String,
    val bindHost: String,
    val advertiseHost: String,
    val tcpPort: Int,
    val rudpPort: Int,
    val discoveryPort: Int,
    val discoveryGroup: String,
    val schemaPath: String,
)

private data class InteropDialConfig(
    val runtimeId: String,
    val runtimeKind: String,
    val displayName: String,
    val agentId: String,
    val targetHost: String,
    val targetPort: Int?,
    val targetRudpPort: Int?,
    val schemaPath: String,
)

private data class InteropSchemaRegistration(val schemaId: String, val title: String?, val schemaJson: String)
private data class InteropDocuments(
    val note: CultDocumentDefinition<InteropNote>,
    val mutationIntent: CultDocumentDefinition<InteropMutationIntent>,
    val mutationReceipt: CultDocumentDefinition<InteropMutationReceipt>,
    val fireCommand: CultDocumentDefinition<InteropFireCommand>,
    val fireReceipt: CultDocumentDefinition<InteropFireReceipt>,
)

private data class InteropNote(
    val schemaVersion: String = interopSchemaVersion,
    val documentId: String,
    val authorRuntimeId: String,
    val title: String,
    val body: String,
    val tags: List<String>,
)

private data class InteropMutationIntent(
    val schemaVersion: String = interopMutationIntentSchemaVersion,
    val intentId: String,
    val targetDocumentId: String,
    val appendBody: String,
    val appendTag: String,
)

private data class InteropMutationReceipt(
    val schemaVersion: String = interopMutationReceiptSchemaVersion,
    val intentId: String,
    val accepted: Boolean,
    val documentId: String,
    val body: String,
    val tags: List<String>,
)

private data class InteropFireCommand(
    val schemaVersion: String = interopFireCommandSchemaVersion,
    val commandId: String,
    val characterId: String,
    val weaponId: String,
)

private data class InteropFireReceipt(
    val schemaVersion: String = interopFireReceiptSchemaVersion,
    val commandId: String,
    val accepted: Boolean,
    val characterId: String,
    val weaponId: String,
    val shotsFired: Long,
    val ammoRemaining: Long,
)

private class InteropCodec<T : Any>(
    override val documentType: String,
    override val schemaVersion: String,
    private val encodeValue: (T) -> List<Any?>,
    private val decodeValue: (List<Any?>) -> T,
) : CultDocumentCodec<T> {
    override fun encode(value: T): ByteArray = MessagePackWriter().value(encodeValue(value)).toByteArray()
    override fun decode(payload: ByteArray): T = decodeValue(anyList(MessagePackReader(payload).readAny()))
}

private fun interopServe(options: Map<String, String>) {
    val config = parseInteropServeConfig(options)
    val registration = loadInteropSchemaRegistration(config.schemaPath)
    val documents = defineInteropDocuments(registration.schemaId)
    val cache = CultCache()
    registerInteropDocuments(cache, documents)
    val schemaCatalog = CultNetSchemaCatalog()
    schemaCatalog.upsert(
        defineCultNetSchemaDescriptor(
            schemaId = registration.schemaId,
            kind = "document_payload",
            schemaVersion = interopSchemaVersion,
            documentType = interopDocumentType,
            title = registration.title,
            wireContracts = listOf("cultnet.schema.v0"),
            schemaJson = registration.schemaJson,
        ),
    )
    cache.put(documents.note, "note:${config.runtimeId}", buildInteropNote(config.runtimeId, config.displayName))

    startInteropTcpServer(config, cache, documents, schemaCatalog)
    startInteropRudpServer(config, cache, documents, schemaCatalog)

    println(jsonLine(linkedMapOf(
        "status" to "ready",
        "mode" to "serve",
        "runtimeId" to config.runtimeId,
        "runtimeKind" to config.runtimeKind,
        "tcpPort" to config.tcpPort,
        "rudpPort" to config.rudpPort,
        "discoveryPort" to config.discoveryPort,
        "discoveryGroup" to config.discoveryGroup,
    )))

    while (true) Thread.sleep(3_600_000)
}

private fun interopDial(options: Map<String, String>) {
    val config = parseInteropDialConfig(options)
    val registration = loadInteropSchemaRegistration(config.schemaPath)
    val documents = defineInteropDocuments(registration.schemaId)
    val cache = CultCache()
    registerInteropDocuments(cache, documents)
    val transportName = if (config.targetRudpPort != null) "rudp" else "tcp_framed"
    val transport = openInteropDialTransport(config)
    transport.use {
        it.sendSchemaMessage(
            cultNetHello(
                runtimeId = config.runtimeId,
                runtimeKind = config.runtimeKind,
                displayName = config.displayName,
                supportedDocumentTypes = listOf(interopDocumentType),
                supportedMutationContracts = listOf(interopMutationContract()),
                supportedMessageVersions = listOf(interopSchemaVersion),
                transportProfiles = dialInteropTransportProfiles(config),
                supportsSchemaCatalog = true,
            ),
        )
        val remoteHello = requireSchemaResponse(it.receiveSchemaMessage(interopReadTimeoutMs, 5), "cultnet.hello.v0", "interop hello")
        val remoteRuntimeId = requireWireString(remoteHello.body, "runtimeId")

        it.sendSchemaMessage(
            CultNetMessage(
                "cultnet.schema_catalog_request.v0",
                linkedMapOf("messageId" to "${config.runtimeId}-catalog", "includeSchemaJson" to true),
            ),
        )
        val catalogResponse = requireSchemaResponse(it.receiveSchemaMessage(interopReadTimeoutMs, 5), "cultnet.schema_catalog_response.v0", "interop schema catalog")
        val hasInteropSchema = mapList(catalogResponse.body["schemas"]).any {
            it["schemaId"] == registration.schemaId && it["documentType"] == interopDocumentType
        }

        it.sendSchemaMessage(CultNetMessage("cultnet.snapshot_request.v0", linkedMapOf("messageId" to "${config.runtimeId}-snapshot")))
        val snapshotResponse = requireSchemaResponse(it.receiveSchemaMessage(interopReadTimeoutMs, 5), "cultnet.snapshot_response_raw.v0", "interop snapshot")
        val snapshotDocuments = mapList(snapshotResponse.body["documents"]).map { rawDocumentRecordFromWire(it) }
        val decodedSnapshotNotes = snapshotDocuments
            .mapNotNull { document -> runCatching { documents.note.codec.decode(document.payload) }.getOrNull() }
        val note = cache.applyRawSnapshotResponse(snapshotResponse)
            .filterIsInstance<InteropNote>()
            .firstOrNull { candidate -> candidate.authorRuntimeId == remoteRuntimeId }
            ?: decodedSnapshotNotes.firstOrNull { candidate -> candidate.authorRuntimeId == remoteRuntimeId }
            ?: throw IOException(
                "did not receive an interop note from $remoteRuntimeId; snapshotDocuments=${snapshotDocuments.size}; " +
                    "schemaIds=${snapshotDocuments.map { document -> document.schemaId }}; " +
                    "decodedAuthors=${decodedSnapshotNotes.map { decoded -> decoded.authorRuntimeId }}",
            )

        val mutationIntent = InteropMutationIntent(
            intentId = "${config.runtimeId}-decorate",
            targetDocumentId = note.documentId,
            appendBody = " Decorated by ${config.runtimeId}.",
            appendTag = "decorated:${config.runtimeId}",
        )
        it.sendSchemaMessage(rawPut("${config.runtimeId}-decorate-put", interopMutationIntentSchemaId, mutationIntent.intentId, documents.mutationIntent.codec.encode(mutationIntent)))
        val mutationReceiptMessage = readDocumentPutFor(it, interopMutationReceiptSchemaId)
        val mutationReceipt = cache.applyRawDocumentPut(mutationReceiptMessage) as InteropMutationReceipt
        val mutatedNoteMessage = readDocumentPutFor(it, registration.schemaId)
        val mutatedNote = cache.applyRawDocumentPut(mutatedNoteMessage) as InteropNote

        val fireCommand = InteropFireCommand(
            commandId = "${config.runtimeId}-fire",
            characterId = remoteRuntimeId,
            weaponId = "interop-rifle",
        )
        it.sendSchemaMessage(rawPut("${config.runtimeId}-fire-put", interopFireCommandSchemaId, fireCommand.commandId, documents.fireCommand.codec.encode(fireCommand)))
        val fireReceipt = cache.applyRawDocumentPut(readDocumentPutFor(it, interopFireReceiptSchemaId)) as InteropFireReceipt

        println(jsonLine(linkedMapOf(
            "mode" to "dial",
            "runtimeId" to config.runtimeId,
            "targetHost" to config.targetHost,
            "targetPort" to (config.targetRudpPort ?: config.targetPort),
            "transport" to transportName,
            "remoteHello" to linkedMapOf("schemaVersion" to "cultnet.hello.v0", "runtimeId" to remoteRuntimeId),
            "hasInteropSchema" to hasInteropSchema,
            "retrievedNote" to interopNoteWire(note),
            "mutatedNote" to interopNoteWire(mutatedNote),
            "mutationReceipt" to interopMutationReceiptWire(mutationReceipt),
            "fireReceipt" to interopFireReceiptWire(fireReceipt),
        )))
    }
}

private fun startInteropTcpServer(
    config: InteropServeConfig,
    cache: CultCache,
    documents: InteropDocuments,
    schemaCatalog: CultNetSchemaCatalog,
) {
    val server = ServerSocket(config.tcpPort, 50, InetAddress.getByName(config.bindHost))
    val thread = Thread {
        while (true) {
            try {
                val socket = server.accept()
                Thread {
                    try {
                        InteropTcpFramedTransport(
                            socket,
                            createTcpFramedInteropProfile(config.runtimeId, config.advertiseHost, config.tcpPort),
                        ).use { transport ->
                            while (true) {
                                val message = transport.receiveSchemaMessage(interopReadTimeoutMs, 5) ?: break
                                handleInteropServerMessage(transport, message, config, cache, documents, schemaCatalog)
                            }
                        }
                    } catch (error: Throwable) {
                        System.err.println("kotlin interop tcp error: ${error.message}")
                    }
                }.also { it.isDaemon = true; it.start() }
            } catch (error: Throwable) {
                System.err.println("kotlin interop tcp accept error: ${error.message}")
            }
        }
    }
    thread.isDaemon = true
    thread.start()
}

private fun startInteropRudpServer(
    config: InteropServeConfig,
    cache: CultCache,
    documents: InteropDocuments,
    schemaCatalog: CultNetSchemaCatalog,
) {
    val socket = DatagramSocket(config.rudpPort, InetAddress.getByName(config.bindHost)).also { it.soTimeout = 20 }
    val sessions = linkedMapOf<InetSocketAddress, CultNetRudpSession>()
    val thread = Thread {
        val buffer = ByteArray(65535)
        while (true) {
            try {
                val datagram = DatagramPacket(buffer, buffer.size)
                socket.receive(datagram)
                val remote = InetSocketAddress(datagram.address, datagram.port)
                val packet = try {
                    decodeRudpPacket(buffer.copyOf(datagram.length))
                } catch (_: IOException) {
                    continue
                }
                if (packet.connectionId != rudpInteropConnectionId) continue
                val session = sessions.getOrPut(remote) {
                    CultNetRudpSession(CultNetRudpSessionOptions(rudpInteropConnectionId, 100, rudpInteropResendDelayMs))
                }
                if (packet.packetType == CultNetRudpPacketType.Connect) {
                    sendInteropRudpPacket(socket, remote, session.acceptConnect(packet, nowMs(), "cultnet-interop-rudp".toByteArray(StandardCharsets.UTF_8)))
                    continue
                }
                val result = session.receive(packet, nowMs())
                result.reply?.let { sendInteropRudpPacket(socket, remote, it) }
                result.readyToSend.forEach { sendInteropRudpPacket(socket, remote, it) }
                if (packet.reliable || packet.packetType == CultNetRudpPacketType.Data) sendInteropRudpPacket(socket, remote, session.createAckFor(packet.sequence))
                for (frame in result.delivered) {
                    if (frame.channelId != "schema") continue
                    val sender = InteropRudpSessionSender(socket, remote, session)
                    handleInteropServerMessage(sender, parseCultNetMessage(frame.payload), config, cache, documents, schemaCatalog)
                }
            } catch (_: SocketTimeoutException) {
                val now = nowMs()
                for ((remote, session) in sessions) {
                    session.dueResends(now).forEach { sendInteropRudpPacket(socket, remote, it) }
                }
            } catch (error: Throwable) {
                System.err.println("kotlin interop rudp error: ${error.message}")
            }
        }
    }
    thread.isDaemon = true
    thread.start()
}

private fun handleInteropServerMessage(
    transport: CultNetSchemaMessageTransport,
    message: CultNetMessage,
    config: InteropServeConfig,
    cache: CultCache,
    documents: InteropDocuments,
    schemaCatalog: CultNetSchemaCatalog,
) {
    synchronized(cache) {
        when (message.schemaVersion) {
            "cultnet.hello.v0" -> transport.sendSchemaMessage(
                cultNetHello(
                    runtimeId = config.runtimeId,
                    runtimeKind = config.runtimeKind,
                    displayName = config.displayName,
                    supportedDocumentTypes = listOf(interopDocumentType),
                    supportedMutationContracts = listOf(interopMutationContract()),
                    supportedMessageVersions = listOf(interopSchemaVersion),
                    transportProfiles = listOf(
                        createTcpFramedInteropProfile(config.runtimeId, config.advertiseHost, config.tcpPort),
                        createRudpTransportProfile(config.runtimeId, transportId = "interop-rudp", host = config.advertiseHost, port = config.rudpPort, maxFragmentBytes = rudpInteropMaxFragmentBytes),
                    ),
                    supportsSchemaCatalog = true,
                ),
            )
            "cultnet.schema_catalog_request.v0" -> transport.sendSchemaMessage(schemaCatalog.createResponse(message))
            "cultnet.snapshot_request.v0" -> {
                val response = cache.createRawSnapshotResponse(message, sourceRuntimeId = config.runtimeId)
                val documentsWithSource = mapList(response.body["documents"]).map {
                    rawDocumentRecordFromWire(it).copy(
                        sourceRuntimeId = config.runtimeId,
                        sourceAgentId = config.agentId,
                        sourceRole = "peer",
                        tags = listOf("interop", config.runtimeId),
                    )
                }
                transport.sendSchemaMessage(cultNetSnapshotResponseRaw(response.body["messageId"] as? String ?: "", documentsWithSource))
            }
            "cultnet.document_put_raw.v0" -> handleInteropRawPut(transport, message, config, cache, documents)
        }
    }
}

private fun handleInteropRawPut(
    transport: CultNetSchemaMessageTransport,
    message: CultNetMessage,
    config: InteropServeConfig,
    cache: CultCache,
    documents: InteropDocuments,
) {
    val document = rawDocumentRecordFromWire(mapValue(message.body["document"]))
    when (document.schemaId) {
        interopMutationIntentSchemaId -> {
            val intent = cache.applyRawDocumentPut(message) as InteropMutationIntent
            val note = cache.getRequired(documents.note, intent.targetDocumentId)
            val mutated = note.copy(
                body = note.body + intent.appendBody,
                tags = note.tags + intent.appendTag,
            )
            cache.put(documents.note, mutated.documentId, mutated)
            val receipt = InteropMutationReceipt(
                intentId = intent.intentId,
                accepted = true,
                documentId = mutated.documentId,
                body = mutated.body,
                tags = mutated.tags,
            )
            transport.sendSchemaMessage(rawPut("${config.runtimeId}-mutation-receipt", interopMutationReceiptSchemaId, receipt.intentId, documents.mutationReceipt.codec.encode(receipt), config, "mutation"))
            transport.sendSchemaMessage(rawPut("${config.runtimeId}-mutated-note", documents.note.schemaVersion, mutated.documentId, documents.note.codec.encode(mutated), config, "mutation"))
        }
        interopFireCommandSchemaId -> {
            val command = cache.applyRawDocumentPut(message) as InteropFireCommand
            val receipt = InteropFireReceipt(
                commandId = command.commandId,
                accepted = true,
                characterId = command.characterId,
                weaponId = command.weaponId,
                shotsFired = 1,
                ammoRemaining = 29,
            )
            transport.sendSchemaMessage(rawPut("${config.runtimeId}-fire-receipt", interopFireReceiptSchemaId, receipt.commandId, documents.fireReceipt.codec.encode(receipt), config, "side-effect"))
        }
    }
}

private class InteropTcpFramedTransport(
    private val socket: Socket,
    override val profile: CultNetTransportProfile,
) : CultNetSchemaMessageTransport {
    private val input = DataInputStream(socket.getInputStream())
    private val output = DataOutputStream(socket.getOutputStream())
    override var stats: CultNetTransportStats = CultNetTransportStats()
        private set

    override fun sendSchemaMessage(message: CultNetMessage) {
        val payload = message.toBytes()
        synchronized(output) {
            output.writeInt(payload.size)
            output.write(payload)
            output.flush()
        }
        stats = stats.copy(bytesSent = stats.bytesSent + payload.size + 4, framesSent = stats.framesSent + 1)
    }

    override fun receiveSchemaMessage(timeoutMs: Long, pollIntervalMs: Long): CultNetMessage? {
        val previousTimeout = socket.soTimeout
        socket.soTimeout = timeoutMs.coerceAtLeast(1).coerceAtMost(Int.MAX_VALUE.toLong()).toInt()
        return try {
            val length = input.readInt()
            val payload = ByteArray(length)
            input.readFully(payload)
            stats = stats.copy(bytesReceived = stats.bytesReceived + payload.size + 4, framesReceived = stats.framesReceived + 1)
            parseCultNetMessage(payload)
        } catch (_: SocketTimeoutException) {
            null
        } catch (_: EOFException) {
            null
        } finally {
            socket.soTimeout = previousTimeout
        }
    }

    override fun close() {
        socket.close()
    }
}

private class InteropRudpSessionSender(
    private val socket: DatagramSocket,
    private val remote: InetSocketAddress,
    private val session: CultNetRudpSession,
) : CultNetSchemaMessageTransport {
    override val profile: CultNetTransportProfile = createRudpTransportProfile("kotlin-interop-rudp-session", transportId = "interop-rudp")
    override val stats: CultNetTransportStats = CultNetTransportStats()

    override fun sendSchemaMessage(message: CultNetMessage) {
        session.sendMany("schema", message.toBytes(), CultNetRudpSendOptions(reliable = true, ordered = true, nowMs = nowMs()), rudpInteropMaxFragmentBytes)
            .forEach { sendInteropRudpPacket(socket, remote, it) }
    }

    override fun receiveSchemaMessage(timeoutMs: Long, pollIntervalMs: Long): CultNetMessage? = null
    override fun close() {}
}

private fun openInteropDialTransport(config: InteropDialConfig): CultNetSchemaMessageTransport {
    val rudpPort = config.targetRudpPort
    if (rudpPort != null) {
        val transport = cultNetRudpClient(
            runtimeId = "${config.runtimeId}-interop-rudp-dial",
            connectionId = rudpInteropConnectionId,
            remoteHost = config.targetHost,
            remotePort = rudpPort,
            tuning = CultNetRudpSocketTuning(resendDelayMs = rudpInteropResendDelayMs, maxFragmentBytes = rudpInteropMaxFragmentBytes),
        )
        if (!transport.connectAndWait("cultnet-interop-rudp", timeoutMs = 5_000, pollIntervalMs = 5)) {
            throw IOException("timed out waiting for RUDP interop connect")
        }
        return transport
    }
    val tcpPort = config.targetPort ?: throw IOException("dial requires --target-port or --target-rudp-port")
    val socket = Socket(config.targetHost, tcpPort)
    return InteropTcpFramedTransport(socket, createTcpFramedInteropProfile(config.runtimeId, config.targetHost, tcpPort))
}

private fun createTcpFramedInteropProfile(runtimeId: String, host: String, port: Int): CultNetTransportProfile =
    CultNetTransportProfile(
        runtimeId = runtimeId,
        transports = listOf(
            CultNetTransportDescriptor(
                transportId = "interop-tcp",
                protocol = "tcp_framed",
                wireContracts = listOf("cultnet.schema.v0"),
                host = host,
                port = port,
                channels = listOf(CultNetTransportChannel("schema", "reliable", "ordered")),
            ),
        ),
    )

private fun dialInteropTransportProfiles(config: InteropDialConfig): List<CultNetTransportProfile> {
    val profiles = mutableListOf<CultNetTransportProfile>()
    if (config.targetPort != null) profiles.add(createTcpFramedInteropProfile(config.runtimeId, config.targetHost, config.targetPort))
    if (config.targetRudpPort != null) profiles.add(createRudpTransportProfile(config.runtimeId, transportId = "interop-rudp", host = config.targetHost, port = config.targetRudpPort, maxFragmentBytes = rudpInteropMaxFragmentBytes))
    return profiles
}

private fun sendInteropRudpPacket(socket: DatagramSocket, remote: InetSocketAddress, packet: CultNetRudpPacket) {
    val wire = encodeRudpPacket(packet)
    socket.send(DatagramPacket(wire, wire.size, remote.address, remote.port))
}

private fun readDocumentPutFor(transport: CultNetSchemaMessageTransport, schemaId: String): CultNetMessage {
    val deadline = nowMs() + interopReadTimeoutMs
    while (nowMs() < deadline) {
        val message = transport.receiveSchemaMessage(250, 5) ?: continue
        if (message.schemaVersion == "cultnet.document_put_raw.v0") {
            val document = mapValue(message.body["document"])
            if (document["schemaId"] == schemaId) return message
        }
    }
    throw IOException("timed out waiting for document put $schemaId")
}

private fun rawPut(
    messageId: String,
    schemaId: String,
    recordKey: String,
    payload: ByteArray,
    config: InteropServeConfig? = null,
    tag: String? = null,
): CultNetMessage = cultNetDocumentPutRaw(
    messageId = messageId,
    document = CultNetRawDocumentRecord(
        schemaId = schemaId,
        recordKey = recordKey,
        storedAt = Instant.now().toString(),
        payload = payload,
        sourceRuntimeId = config?.runtimeId,
        sourceAgentId = config?.agentId,
        sourceRole = config?.let { "peer" },
        tags = listOfNotNull(tag, config?.runtimeId),
    ),
)

private fun defineInteropDocuments(noteSchemaId: String): InteropDocuments {
    val note = CultDocumentDefinition(InteropCodec(interopDocumentType, noteSchemaId, ::interopNoteSlots, ::interopNoteFromSlots))
    val mutationIntent = CultDocumentDefinition(InteropCodec(interopMutationIntentType, interopMutationIntentSchemaId, ::interopMutationIntentSlots, ::interopMutationIntentFromSlots))
    val mutationReceipt = CultDocumentDefinition(InteropCodec(interopMutationReceiptType, interopMutationReceiptSchemaId, ::interopMutationReceiptSlots, ::interopMutationReceiptFromSlots))
    val fireCommand = CultDocumentDefinition(InteropCodec(interopFireCommandType, interopFireCommandSchemaId, ::interopFireCommandSlots, ::interopFireCommandFromSlots))
    val fireReceipt = CultDocumentDefinition(InteropCodec(interopFireReceiptType, interopFireReceiptSchemaId, ::interopFireReceiptSlots, ::interopFireReceiptFromSlots))
    return InteropDocuments(note, mutationIntent, mutationReceipt, fireCommand, fireReceipt)
}

private fun registerInteropDocuments(cache: CultCache, documents: InteropDocuments) {
    cache.register(documents.note)
    cache.register(documents.mutationIntent)
    cache.register(documents.mutationReceipt)
    cache.register(documents.fireCommand)
    cache.register(documents.fireReceipt)
}

private fun buildInteropNote(runtimeId: String, displayName: String): InteropNote = InteropNote(
    documentId = "note:$runtimeId",
    authorRuntimeId = runtimeId,
    title = "$displayName keeps a little note",
    body = "$runtimeId can move CultNet state without begging the gods for translation.",
    tags = listOf(runtimeId, "interop", "cultnet"),
)

private fun interopMutationContract(): Map<String, Any?> = linkedMapOf(
    "documentType" to interopDocumentType,
    "payloadSchemaVersion" to interopSchemaVersion,
    "operations" to listOf("snapshot", "documentPut", "intentSubmit", "receiptWatch"),
    "authority" to "runtime",
    "intentDocumentTypes" to listOf(interopMutationIntentType, interopFireCommandType),
    "receiptDocumentTypes" to listOf(interopMutationReceiptType, interopFireReceiptType),
)

private fun loadInteropSchemaRegistration(schemaPath: String): InteropSchemaRegistration {
    val schemaJson = File(schemaPath).readText(StandardCharsets.UTF_8)
    return InteropSchemaRegistration(
        schemaId = extractJsonString(schemaJson, "\$id") ?: throw IOException("schema $schemaPath is missing \$id"),
        title = extractJsonString(schemaJson, "title"),
        schemaJson = schemaJson,
    )
}

private fun extractJsonString(json: String, field: String): String? {
    val pattern = Regex(""""${Regex.escape(field)}"\s*:\s*"((?:\\.|[^"\\])*)"""")
    return pattern.find(json)?.groupValues?.get(1)?.replace("\\\"", "\"")
}

private fun parseInteropServeConfig(options: Map<String, String>): InteropServeConfig {
    val tcpPort = requiredIntArg(options, "tcp-port")
    return InteropServeConfig(
        runtimeId = requiredArg(options, "runtime-id"),
        runtimeKind = requiredArg(options, "runtime-kind"),
        displayName = requiredArg(options, "display-name"),
        agentId = requiredArg(options, "agent-id"),
        bindHost = options["bind-host"] ?: "127.0.0.1",
        advertiseHost = requiredArg(options, "advertise-host"),
        tcpPort = tcpPort,
        rudpPort = options["rudp-port"]?.toInt() ?: tcpPort,
        discoveryPort = requiredIntArg(options, "discovery-port"),
        discoveryGroup = requiredArg(options, "discovery-group"),
        schemaPath = requiredArg(options, "schema-path"),
    )
}

private fun parseInteropDialConfig(options: Map<String, String>): InteropDialConfig = InteropDialConfig(
    runtimeId = requiredArg(options, "runtime-id"),
    runtimeKind = requiredArg(options, "runtime-kind"),
    displayName = requiredArg(options, "display-name"),
    agentId = requiredArg(options, "agent-id"),
    targetHost = requiredArg(options, "target-host"),
    targetPort = options["target-port"]?.toInt(),
    targetRudpPort = options["target-rudp-port"]?.toInt(),
    schemaPath = requiredArg(options, "schema-path"),
).also {
    if (it.targetPort == null && it.targetRudpPort == null) throw IOException("dial requires --target-port or --target-rudp-port")
}

private fun requiredArg(options: Map<String, String>, name: String): String =
    options[name] ?: throw IOException("missing --$name")

private fun requiredIntArg(options: Map<String, String>, name: String): Int =
    requiredArg(options, name).toInt()

private fun interopNoteWire(value: InteropNote): Map<String, Any?> = linkedMapOf(
    "schemaVersion" to value.schemaVersion,
    "documentId" to value.documentId,
    "authorRuntimeId" to value.authorRuntimeId,
    "title" to value.title,
    "body" to value.body,
    "tags" to value.tags,
)

private fun interopNoteSlots(value: InteropNote): List<Any?> = listOf(
    value.schemaVersion,
    value.documentId,
    value.authorRuntimeId,
    value.title,
    value.body,
    value.tags,
)

private fun interopNoteFromSlots(slots: List<Any?>): InteropNote = InteropNote(
    schemaVersion = slots.stringAt(0, interopSchemaVersion),
    documentId = slots.requiredStringAt(1, "documentId"),
    authorRuntimeId = slots.requiredStringAt(2, "authorRuntimeId"),
    title = slots.requiredStringAt(3, "title"),
    body = slots.requiredStringAt(4, "body"),
    tags = stringList(slots.getOrNull(5)),
)

private fun interopMutationIntentWire(value: InteropMutationIntent): Map<String, Any?> = linkedMapOf(
    "schemaVersion" to value.schemaVersion,
    "intentId" to value.intentId,
    "targetDocumentId" to value.targetDocumentId,
    "appendBody" to value.appendBody,
    "appendTag" to value.appendTag,
)

private fun interopMutationIntentSlots(value: InteropMutationIntent): List<Any?> = listOf(
    value.schemaVersion,
    value.intentId,
    value.targetDocumentId,
    value.appendBody,
    value.appendTag,
)

private fun interopMutationIntentFromSlots(slots: List<Any?>): InteropMutationIntent = InteropMutationIntent(
    schemaVersion = slots.stringAt(0, interopMutationIntentSchemaVersion),
    intentId = slots.requiredStringAt(1, "intentId"),
    targetDocumentId = slots.requiredStringAt(2, "targetDocumentId"),
    appendBody = slots.requiredStringAt(3, "appendBody"),
    appendTag = slots.requiredStringAt(4, "appendTag"),
)

private fun interopMutationReceiptWire(value: InteropMutationReceipt): Map<String, Any?> = linkedMapOf(
    "schemaVersion" to value.schemaVersion,
    "intentId" to value.intentId,
    "accepted" to value.accepted,
    "documentId" to value.documentId,
    "body" to value.body,
    "tags" to value.tags,
)

private fun interopMutationReceiptSlots(value: InteropMutationReceipt): List<Any?> = listOf(
    value.schemaVersion,
    value.intentId,
    value.accepted,
    value.documentId,
    value.body,
    value.tags,
)

private fun interopMutationReceiptFromSlots(slots: List<Any?>): InteropMutationReceipt = InteropMutationReceipt(
    schemaVersion = slots.stringAt(0, interopMutationReceiptSchemaVersion),
    intentId = slots.requiredStringAt(1, "intentId"),
    accepted = slots.getOrNull(2) == true,
    documentId = slots.requiredStringAt(3, "documentId"),
    body = slots.requiredStringAt(4, "body"),
    tags = stringList(slots.getOrNull(5)),
)

private fun interopFireCommandWire(value: InteropFireCommand): Map<String, Any?> = linkedMapOf(
    "schemaVersion" to value.schemaVersion,
    "commandId" to value.commandId,
    "characterId" to value.characterId,
    "weaponId" to value.weaponId,
)

private fun interopFireCommandSlots(value: InteropFireCommand): List<Any?> = listOf(
    value.schemaVersion,
    value.commandId,
    value.characterId,
    value.weaponId,
)

private fun interopFireCommandFromSlots(slots: List<Any?>): InteropFireCommand = InteropFireCommand(
    schemaVersion = slots.stringAt(0, interopFireCommandSchemaVersion),
    commandId = slots.requiredStringAt(1, "commandId"),
    characterId = slots.requiredStringAt(2, "characterId"),
    weaponId = slots.requiredStringAt(3, "weaponId"),
)

private fun interopFireReceiptWire(value: InteropFireReceipt): Map<String, Any?> = linkedMapOf(
    "schemaVersion" to value.schemaVersion,
    "commandId" to value.commandId,
    "accepted" to value.accepted,
    "characterId" to value.characterId,
    "weaponId" to value.weaponId,
    "shotsFired" to value.shotsFired,
    "ammoRemaining" to value.ammoRemaining,
)

private fun interopFireReceiptSlots(value: InteropFireReceipt): List<Any?> = listOf(
    value.schemaVersion,
    value.commandId,
    value.accepted,
    value.characterId,
    value.weaponId,
    value.shotsFired,
    value.ammoRemaining,
)

private fun interopFireReceiptFromSlots(slots: List<Any?>): InteropFireReceipt = InteropFireReceipt(
    schemaVersion = slots.stringAt(0, interopFireReceiptSchemaVersion),
    commandId = slots.requiredStringAt(1, "commandId"),
    accepted = slots.getOrNull(2) == true,
    characterId = slots.requiredStringAt(3, "characterId"),
    weaponId = slots.requiredStringAt(4, "weaponId"),
    shotsFired = (slots.getOrNull(5) as? Number)?.toLong() ?: 0,
    ammoRemaining = (slots.getOrNull(6) as? Number)?.toLong() ?: 0,
)

private fun anyList(value: Any?): List<Any?> = when (value) {
    is Iterable<*> -> value.toList()
    is Array<*> -> value.toList()
    else -> throw IOException("Expected MessagePack array")
}

private fun List<Any?>.requiredStringAt(index: Int, fieldName: String): String {
    val value = getOrNull(index)
    if (value !is String || value.isBlank()) throw IOException("$fieldName must be a non-empty string")
    return value
}

private fun List<Any?>.stringAt(index: Int, fallback: String): String =
    (getOrNull(index) as? String)?.takeIf { it.isNotBlank() } ?: fallback

private fun jsonLine(value: Map<String, Any?>): String = jsonValue(value)

private fun jsonValue(value: Any?): String = when (value) {
    null -> "null"
    is String -> "\"" + value.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", "\\n").replace("\r", "\\r") + "\""
    is Boolean -> if (value) "true" else "false"
    is Number -> value.toString()
    is Map<*, *> -> value.entries.joinToString(prefix = "{", postfix = "}") { (key, nested) -> jsonValue(key.toString()) + ":" + jsonValue(nested) }
    is Iterable<*> -> value.joinToString(prefix = "[", postfix = "]") { jsonValue(it) }
    is Array<*> -> value.joinToString(prefix = "[", postfix = "]") { jsonValue(it) }
    else -> jsonValue(value.toString())
}

private fun cultNetRudpReconnectLoopConsumesSharedController() {
    var now = 10_000L
    var capturedTimer: (() -> Unit)? = null
    val created = mutableListOf<CultNetRudpSocketTransportConnection>()
    val loop = CultNetRudpReconnectLoop(
        reconnectPolicy = createReconnectPolicy(maxAttempts = 2),
        connectPayload = "join".toByteArray(StandardCharsets.UTF_8),
        createTransport = {
            CultMesh.createRudpClient(
                runtimeId = "kotlin-reconnect-client-${created.size}",
                connectionId = 0x20304050,
                remoteHost = "127.0.0.1",
                remotePort = 9,
                tuning = CultNetRudpSocketTuning(resendDelayMs = 25, maxPendingReliablePackets = 16),
            ).also { created.add(it) }
        },
        nowMsProvider = { now },
        jitterMsProvider = { 17 },
        scheduler = { delayMs, callback ->
            check(delayMs == 1_017L)
            capturedTimer = callback
            AutoCloseable { capturedTimer = null }
        },
    )

    val first = loop.start()
    check(created.size == 1)
    check(loop.transport === first)
    first.close()
    val decision = loop.handleClosed()
    check(decision?.attempt == 1)
    check(decision?.delayMs == 1_017L)
    check(loop.reconnectController.nextAttemptAtMs == 11_017L)
    check(capturedTimer != null)

    now = 11_017L
    capturedTimer?.invoke()
    check(created.size == 2)
    check(loop.transport === created[1])

    loop.markConnected()
    check(loop.reconnectController.attempt == 0)
    loop.stop()
    check(loop.transport == null)
}

private fun cultNetWebSocketTransportCarriesSchemaFramesWithStats() {
    val loopback = InetAddress.getByName("127.0.0.1")
    ServerSocket(0, 1, loopback).use { server ->
        var serverError: Throwable? = null
        val thread = Thread {
            try {
                server.accept().use { socket ->
                    socket.soTimeout = 1_000
                    val input = socket.getInputStream()
                    val output = socket.getOutputStream()
                    readWebSocketHandshake(input)
                    output.write(
                        (
                            "HTTP/1.1 101 Switching Protocols\r\n" +
                                "Upgrade: websocket\r\n" +
                                "Connection: Upgrade\r\n\r\n"
                            ).toByteArray(StandardCharsets.US_ASCII),
                    )
                    output.flush()
                    val request = parseCultNetMessage(readMaskedWebSocketBinaryPayload(input))
                    check(request.schemaVersion == "cultnet.schema_catalog_request.v0")
                    val catalog = CultNetSchemaCatalog()
                    catalog.upsert(
                        defineCultNetSchemaDescriptor(
                            schemaId = "kotlin.websocket.note.v1",
                            kind = "document_payload",
                            documentType = "kotlin.websocket.note",
                            title = "Kotlin WebSocket Note",
                            schemaJson = """{"type":"string"}""",
                        ),
                    )
                    writeUnmaskedWebSocketBinaryPayload(
                        output,
                        catalog.createResponse(request).toBytes(),
                    )
                }
            } catch (error: Throwable) {
                serverError = error
            }
        }
        thread.isDaemon = true
        thread.start()

        CultNetWebSocketTransportConnection.connect(
            URI("ws://127.0.0.1:${server.localPort}/mesh"),
            runtimeId = "kotlin-websocket-test",
        ).use { transport ->
            val descriptor = transport.profile.transports.single()
            check(descriptor.protocol == "websocket")
            check(descriptor.channels == listOf(CultNetTransportChannel("schema", "reliable", "ordered")))

            val synced = CultNetSchemaCatalog()
            val descriptors = transport.syncSchemaCatalog(
                synced,
                messageId = "websocket-schema",
                includeSchemaJson = true,
                kinds = listOf("document_payload"),
            )
            check(descriptors.single().schemaId == "kotlin.websocket.note.v1")
            check(synced.get("kotlin.websocket.note.v1")?.documentType == "kotlin.websocket.note")
            check(transport.stats.framesSent == 1L)
            check(transport.stats.framesReceived == 1L)
        }

        thread.join(1_000)
        if (thread.isAlive) error("WebSocket transport test server did not finish")
        serverError?.let { throw it }
    }
}

private fun cultNetBuiltInSchemaCatalogAdvertisesWireContracts() {
    val catalog = CultMesh.createBuiltInSchemaCatalog()
    val request = cultNetSchemaCatalogRequest(
        messageId = "kotlin-builtins",
        includeSchemaJson = false,
        kinds = listOf("wire_message"),
    )
    val response = catalog.createResponse(request)
    val parsed = parseCultNetMessage(response.toBytes())
    val schemas = mapList(parsed.body["schemas"])
    val schemaCatalogRequest = schemas.single { it["schemaVersion"] == "cultnet.schema_catalog_request.v0" }
    check(schemaCatalogRequest["schemaId"] == "$cultNetSchemaBase/cultnet.schema-catalog-request.schema.json")
    check(schemaCatalogRequest["wireContracts"] == listOf("cultnet.schema.v0", "gamecult.networking.v0"))
    check(!schemaCatalogRequest.containsKey("schemaJson"))
    val shardCatalogRequest = schemas.single { it["schemaVersion"] == "cultnet.shard_catalog_request.v0" }
    check(shardCatalogRequest["schemaId"] == "cultnet.shard_catalog_request.v0")
    check(shardCatalogRequest["wireContracts"] == listOf("cultnet.schema.v0"))
    check(shardCatalogRequest["contentHash"] == "0abd131a1db12f6731010333b86e104d550160bd58f539b18413a2b61198fa38")
    val shardCatalogResponse = schemas.single { it["schemaVersion"] == "cultnet.shard_catalog_response.v0" }
    check(shardCatalogResponse["schemaId"] == "cultnet.shard_catalog_response.v0")
    check(shardCatalogResponse["wireContracts"] == listOf("cultnet.schema.v0"))

    val inlineCatalog = cultNetBuiltInSchemaCatalog(includeSchemaJson = true)
    val transportResponse = inlineCatalog.createResponse(
        cultNetSchemaCatalogRequest(
            messageId = "kotlin-builtins-transport",
            includeSchemaJson = true,
            schemaIds = listOf("$cultNetSchemaBase/cultnet.transport-profile.schema.json"),
        ),
    )
    val transportProfile = mapList(transportResponse.body["schemas"]).single()
    check(transportProfile["kind"] == "shared_contract")
    check(transportProfile["contentHash"] == "84473264f543ca3443267999e43d72b7993b61b7b2ac53c642c9cbe8b6de0217")
    check(!transportProfile.containsKey("schemaJson"))

    val filteredTransportCatalog = CultMesh.createBuiltInSchemaCatalog(
        includeSchemaJson = true,
        schemaIds = listOf("$cultNetSchemaBase/cultnet.transport-profile.schema.json"),
    )
    check(filteredTransportCatalog.schemas.single().schemaVersion == "cultnet.transport_profile.v0")
    val filteredSharedContracts = CultMesh.createBuiltInSchemaCatalog(kinds = listOf("shared_contract"))
    check(filteredSharedContracts.schemas.single().schemaId == "$cultNetSchemaBase/cultnet.transport-profile.schema.json")
    val filteredInlineCatalog = CultMesh.createBuiltInSchemaCatalog(
        includeSchemaJson = true,
        schemaIds = listOf("cultmesh.peer_exchange_request.v0"),
    )
    check(filteredInlineCatalog.schemas.single().schemaJson?.contains("cultmesh.peer_exchange_request.v0") == true)

    val inlineResponse = inlineCatalog.createResponse(
        cultNetSchemaCatalogRequest(
            messageId = "kotlin-builtins-inline",
            includeSchemaJson = true,
            schemaIds = listOf("cultmesh.peer_exchange_request.v0"),
        ),
    )
    val inlineSchemas = mapList(inlineResponse.body["schemas"])
    val peerExchangeRequest = inlineSchemas.single()
    check(peerExchangeRequest["kind"] == "wire_message")
    check((peerExchangeRequest["schemaJson"] as String).contains("cultmesh.peer_exchange_request.v0"))
}

private fun cultNetSchemaCatalogsRoundTripDescriptors() {
    val catalog = CultNetSchemaCatalog()
    var watched: CultNetSchemaDescriptor? = null
    val unsubscribe = catalog.watch { watched = it }
    val wireSchemaJson = """{"type":"object","properties":{"schemaVersion":{"const":"kotlin.note.v1"}}}"""
    val wireDescriptor = defineCultNetSchemaDescriptor(
        schemaId = "kotlin.note.v1",
        kind = "wire_message",
        schemaVersion = "kotlin.note.v1",
        title = "Kotlin Note",
        wireContracts = listOf("cultnet.schema.v0"),
        schemaJson = wireSchemaJson,
    )
    val documentDescriptor = defineCultNetSchemaDescriptor(
        schemaId = "kotlin.document.v1",
        kind = "document_payload",
        documentType = "kotlin.document",
        title = "Kotlin Document",
        wireContracts = listOf("cultnet.schema.v0"),
        schemaJson = """{"type":"object","properties":{"body":{"type":"string"}}}""",
    )
    catalog.upsert(wireDescriptor)
    catalog.upsert(documentDescriptor)
    unsubscribe()
    check(watched?.schemaId == "kotlin.document.v1")
    check(catalog.get("kotlin.note.v1")?.contentHash == sha256Hex(wireSchemaJson))

    val withoutJson = catalog.createResponse(
        cultNetSchemaCatalogRequest(
            messageId = "schema-without-json",
            includeSchemaJson = false,
            kinds = listOf("wire_message"),
        ),
    )
    val parsedWithoutJson = parseCultNetMessage(withoutJson.toBytes())
    val schemasWithoutJson = mapList(parsedWithoutJson.body["schemas"])
    check(schemasWithoutJson.single()["schemaId"] == "kotlin.note.v1")
    check(!schemasWithoutJson.single().containsKey("schemaJson"))

    val withJson = catalog.createResponse(
        cultNetSchemaCatalogRequest(
            messageId = "schema-with-json",
            includeSchemaJson = true,
            schemaIds = listOf("kotlin.document.v1"),
        ),
    )
    val parsedWithJson = parseCultNetMessage(withJson.toBytes())
    val appliedCatalog = CultNetSchemaCatalog()
    val applied = appliedCatalog.applyResponse(parsedWithJson)
    check(applied.single().schemaId == "kotlin.document.v1")
    check(applied.single().schemaJson?.contains("body") == true)
    check(appliedCatalog.get("kotlin.document.v1")?.kind == "document_payload")
}

private fun cultMeshCatalogsRoundTripDiscoveryMessages() {
    val sourceVerse = CultMeshVerseDescriptor(
        verseId = "public",
        displayName = "Public Verse",
        authorityModel = "federated",
        compatibility = CultMeshVerseCompatibility(
            transportVersion = "cultmesh.v0",
            rulesHash = "rules-a",
        ),
        discoveryEndpoints = listOf("rudp://127.0.0.1:4000"),
        authorityRuntimeIds = listOf("kotlin-authority"),
    )
    val targetVerse = CultMeshVerseDescriptor(
        verseId = "private",
        displayName = "Private Verse",
        authorityModel = "coordinator",
        compatibility = CultMeshVerseCompatibility(
            transportVersion = "cultmesh.v0",
            rulesHash = "rules-b",
            compatibleVerseIds = listOf("public"),
        ),
    )
    val verseCatalog = CultMeshVerseCatalog()
    var watchedVerse: CultMeshVerseDescriptor? = null
    val unsubscribeVerse = verseCatalog.watch { watchedVerse = it }
    verseCatalog.upsert(sourceVerse)
    verseCatalog.upsert(targetVerse)
    unsubscribeVerse()
    check(watchedVerse?.verseId == "private")
    check(verseCatalog.findTransferTargets(sourceVerse).map { it.verseId } == listOf("private"))

    val verseRequest = cultMeshVerseCatalogRequest(
        messageId = "verse-request",
        transportVersion = "cultmesh.v0",
    )
    val verseResponse = parseCultNetMessage(verseCatalog.createResponse(verseRequest).toBytes())
    check(verseResponse.schemaVersion == "cultmesh.verse_catalog_response.v0")

    val appliedVerseCatalog = CultMeshVerseCatalog()
    val appliedVerses = appliedVerseCatalog.applyResponse(verseResponse)
    check(appliedVerses.map { it.verseId } == listOf("private", "public"))
    check(appliedVerseCatalog.get("public")?.displayName == "Public Verse")

    val peerCatalog = CultMeshPeerCatalog()
    var watchedPeer: CultMeshPeerCard? = null
    val unsubscribePeer = peerCatalog.watch { watchedPeer = it }
    peerCatalog.upsert(
        CultMeshPeerCard(
            peerId = "peer-a",
            verseId = "public",
            endpoints = listOf("rudp://127.0.0.1:4100"),
            roles = listOf("read-replica", "schema"),
            shardIds = listOf("shard-a"),
            region = "local",
        ),
    )
    peerCatalog.upsert(
        CultMeshPeerCard(
            peerId = "peer-b",
            verseId = "public",
            endpoints = listOf("rudp://127.0.0.1:4200"),
            roles = listOf("writer"),
        ),
    )
    unsubscribePeer()
    check(watchedPeer?.peerId == "peer-b")
    check(peerCatalog.find("public", "read-replica").single().peerId == "peer-a")

    val peerResponse = parseCultNetMessage(
        peerCatalog.createResponse(
            cultMeshPeerExchangeRequest(
                messageId = "peer-request",
                verseId = "public",
                roles = listOf("writer", "schema"),
                knownPeerIds = listOf("peer-b"),
                limit = 1,
            ),
        ).toBytes(),
    )
    val appliedPeerCatalog = CultMeshPeerCatalog()
    val appliedPeers = appliedPeerCatalog.applyResponse(peerResponse)
    check(appliedPeers.single().peerId == "peer-a")
    check(appliedPeerCatalog.get("peer-a")?.hasRole("schema") == true)
}

private fun cultMeshAuthorityLeasesGatePeerTrust() {
    val peer = CultMeshPeerCard(
        peerId = "peer-authority",
        verseId = "public",
        endpoints = listOf("rudp://127.0.0.1:4100"),
        roles = listOf("shard-primary", "schema"),
        shardIds = listOf("players"),
        authorityLeaseId = "lease:peer-authority",
    )
    val leases = createAuthorityLeaseCatalog()
    val validFrom = Instant.parse("2026-06-15T00:00:00Z")
    val expiresAt = Instant.parse("2026-06-15T01:00:00Z")
    val duringLease = Instant.parse("2026-06-15T00:30:00Z")
    var watchedLease: CultMeshAuthorityLease? = null
    val unsubscribeLease = leases.watch { watchedLease = it }
    val peers = CultMesh.createPeerCatalog()
    peers.upsert(peer)

    check(!leases.isAuthorized(peer, "shard-primary", "players", duringLease))
    check(peers.findAuthorized("public", "shard-primary", leases, "players", duringLease).isEmpty())
    leases.upsert(
        CultMeshAuthorityLease(
            leaseId = "lease:peer-authority",
            verseId = "public",
            peerId = "peer-authority",
            roles = listOf("shard-primary"),
            shardIds = listOf("players"),
            issuerRuntimeId = "kotlin-authority",
            validFrom = validFrom,
            expiresAt = expiresAt,
        ),
    )

    check(watchedLease?.leaseId == "lease:peer-authority")
    unsubscribeLease()
    leases.upsert(watchedLease!!.copy(signature = "after-unsubscribe"))
    check(watchedLease?.roles == listOf("shard-primary"))
    check(leases.get("lease:peer-authority")?.issuerRuntimeId == "kotlin-authority")
    check(leases.leases.map { it.leaseId } == listOf("lease:peer-authority"))
    check(leases.isAuthorized(peer, "shard-primary", "players", duringLease))
    check(peers.findAuthorized("public", "shard-primary", leases, "players", duringLease).single().peerId == "peer-authority")
    check(peers.firstAuthorized("public", "shard-primary", leases, "players", duringLease)?.peerId == "peer-authority")
    check(peers.firstAuthorized("public", "schema", leases, "players", duringLease) == null)
    check(!leases.isAuthorized(peer, "schema", "players", duringLease))
    check(!leases.isAuthorized(peer, "shard-primary", "inventory", duringLease))
    check(!leases.isAuthorized(peer, "shard-primary", "players", expiresAt))
}

private fun cultMeshStreamCatalogNegotiatesBodyTransports() {
    val streams = createStreamCatalog()
    var watchedStream: CultMeshStreamDescriptor? = null
    var watchedFrame: CultMeshStreamFrameHandle? = null
    val unsubscribeStream = streams.watch { watchedStream = it }
    val unsubscribeFrame = streams.watchFrames { watchedFrame = it }
    val stream = CultMeshStreamDescriptor(
        streamId = "mimir:kiyo-pro",
        verseId = "studio",
        ownerPeerId = "starfire",
        kind = CultMeshStreamKinds.Video,
        label = "Kiyo Pro",
        clock = CultMeshStreamClock(
            clockDomainId = "starfire-qpc",
            confidence = 0.25,
            evidenceKind = "provisional-clock-domain-edge-fit",
        ),
        video = CultMeshVideoStreamFormat(
            width = 1920,
            height = 1080,
            pixelFormat = "YUY2",
            framesPerSecond = 30.0,
        ),
        preferredTransports = listOf(
            CultMeshStreamBodyTransports.SharedD3D12Texture,
            CultMeshStreamBodyTransports.SharedMemory,
            CultMeshStreamBodyTransports.CultCachePage,
        ),
        maxInFlightFrames = 3,
    )
    streams.declare(stream)
    check(watchedStream?.streamId == "mimir:kiyo-pro")

    val negotiation = streams.negotiate(
        "mimir:kiyo-pro",
        CultMeshStreamConsumerProfile(
            peerId = "fensalir",
            verseId = "studio",
            supportedTransports = listOf(
                CultMeshStreamBodyTransports.SharedD3D12Texture,
                CultMeshStreamBodyTransports.CultCachePage,
            ),
            acceptedKinds = listOf(CultMeshStreamKinds.Video),
            canImportGpuHandles = true,
            maxInFlightFrames = 2,
        ),
    )

    check(negotiation.streamId == "mimir:kiyo-pro")
    check(negotiation.producerPeerId == "starfire")
    check(negotiation.consumerPeerId == "fensalir")
    check(negotiation.transport == CultMeshStreamBodyTransports.SharedD3D12Texture)
    check(negotiation.access == "read")
    check(negotiation.maxInFlightFrames == 2)
    check(negotiation.copyBudget == "zero-copy-target")
    check(streams.find("studio", CultMeshStreamKinds.Video).single().streamId == "mimir:kiyo-pro")

    val frame = CultMeshStreamFrameHandle(
        streamId = "mimir:kiyo-pro",
        sequence = 42,
        timestampNs = 1_000_000_000,
        durationNs = 33_333_334,
        transport = negotiation.transport,
        nativeHandle = "0xfeed",
        fenceHandle = "0xbeef",
        fenceValue = 7,
        unavoidableCopyCount = 0,
    )
    streams.publishFrame(frame)
    check(watchedFrame?.sequence == 42L)
    check(streams.latestFrame("mimir:kiyo-pro")?.sequence == 42L)
    unsubscribeStream()
    unsubscribeFrame()
    streams.declare(stream.copy(streamId = "mimir:kiyo-pro-alt"))
    streams.publishFrame(frame.copy(sequence = 43))
    check(watchedStream?.streamId == "mimir:kiyo-pro")
    check(watchedFrame?.sequence == 42L)
}

private fun cultNetShardCatalogsAndLogsRoundTrip() {
    val notes = stringDocument("kotlin.note", "kotlin.note.v1")
    val cache = CultCache()
    cache.register(notes)

    val shardCatalog = CultNetShardCatalog()
    var watchedShard: CultNetShardDescriptor? = null
    val unsubscribeShard = shardCatalog.watch { watchedShard = it }
    shardCatalog.upsert(
        CultNetShardDescriptor(
            shardId = "notes-a",
            ownerRuntimeId = "kotlin-owner",
            epoch = 7,
            isPrimary = true,
            schemaIds = listOf(notes.schemaVersion),
            keyPrefix = "note:",
            primaryEndpoints = listOf("rudp://127.0.0.1:5000"),
            readReplicaEndpoints = listOf("rudp://127.0.0.1:5001"),
            region = "local",
        ),
    )
    shardCatalog.upsert(
        CultNetShardDescriptor(
            shardId = "other",
            ownerRuntimeId = "kotlin-owner",
            epoch = 1,
            schemaIds = listOf("other.v1"),
            keyPrefix = "other:",
        ),
    )
    unsubscribeShard()
    check(watchedShard?.shardId == "other")
    val catalogResponse = parseCultNetMessage(
        shardCatalog.createResponse(
            cultNetShardCatalogRequest(
                messageId = "shards",
                schemaIds = listOf(notes.schemaVersion),
                recordKeys = listOf("note:1"),
            ),
        ).toBytes(),
    )
    val appliedShardCatalog = CultNetShardCatalog()
    val appliedShards = appliedShardCatalog.applyResponse(catalogResponse)
    check(appliedShards.single().shardId == "notes-a")
    check(appliedShardCatalog.get("notes-a")?.serves(schemaId = notes.schemaVersion, recordKey = "note:2") == true)

    val put = cultNetDocumentPutRaw(
        messageId = "put-note",
        document = CultNetRawDocumentRecord(
            schemaId = notes.schemaVersion,
            recordKey = "note:1",
            storedAt = "2026-06-15T00:00:00Z",
            payload = notes.codec.encode("hello"),
        ),
        shardId = "notes-a",
        shardEpoch = 7,
    )
    val delete = cultNetDocumentDelete(
        messageId = "delete-note",
        schemaId = notes.schemaVersion,
        recordKey = "note:1",
        shardId = "notes-a",
        shardEpoch = 7,
    )
    val response = parseCultNetMessage(
        cultNetShardLogResponse(
            messageId = "log",
            shardId = "notes-a",
            shardEpoch = 7,
            entries = listOf(
                CultNetShardLogEntry(1, "added", put = put, committedAt = "2026-06-15T00:00:01Z"),
                CultNetShardLogEntry(2, "removed", delete = delete, committedAt = "2026-06-15T00:00:02Z"),
            ),
        ).toBytes(),
    )
    val parsedLog = shardLogResponseFromMessage(response)
    check(parsedLog.lastSequence == 2L)
    val applied = cache.applyShardLogResponse(response)
    check(applied == listOf("hello", null))
    check(cache.get(notes, "note:1") == null)

    val resync = shardLogResponseFromMessage(
        cultNetShardLogResponse(
            messageId = "log-resync",
            shardId = "notes-a",
            shardEpoch = 7,
            entries = emptyList(),
            resyncRequired = true,
            reason = "compacted_history",
            compactedThrough = 4,
        ),
    )
    check(resync.lastSequence == 4L)
    check(runCatching { resync.requireUsable() }.exceptionOrNull() is IOException)

    val cursors = CultNetInMemoryShardReplicaCursorStore()
    cursors.write(CultNetShardReplicaCursor("notes-a", 7, parsedLog.lastSequence, "2026-06-15T00:00:03Z"))
    check(cursors.read("notes-a")?.lastAppliedSequence == 2L)
}

private fun rudpServeOnce(options: Map<String, String>) {
    val bindHost = options["bind-host"] ?: "127.0.0.1"
    val bindPort = options["bind-port"]?.toInt() ?: 0
    val expectedClientPayload = (options["client-payload"] ?: "ts-kotlin-client-state").toByteArray(StandardCharsets.UTF_8)
    val serverPayload = (options["server-payload"] ?: "kotlin-server-state").toByteArray(StandardCharsets.UTF_8)
    val serverExtraPayload = options["server-extra-payload"]?.toByteArray(StandardCharsets.UTF_8)
    val disconnectReason = options["disconnect-reason"]?.toByteArray(StandardCharsets.UTF_8)
    val maxFragmentBytes = options["max-fragment-bytes"]?.toInt()
    val socket = DatagramSocket(bindPort, InetAddress.getByName(bindHost)).also { it.soTimeout = 20 }
    CultNetRudpSocketTransportConnection(
        socket = socket,
        mode = CultNetRudpSocketMode.Server,
        runtimeId = "kotlin-rudp-interop",
        connectionId = 0x446688aaL,
        initialSequence = 100,
        resendDelayMs = 25,
        maxFragmentBytes = maxFragmentBytes,
    ).use { transport ->
        println("""{"status":"ready","port":${socket.localPort}}""")
        val deadline = System.nanoTime() + 5_000_000_000L
        while (System.nanoTime() < deadline) {
            val frame = transport.receiveOnce()
            if (frame != null) {
                requireRudpFrame(frame, "schema", expectedClientPayload)
                transport.send("schema", serverPayload)
                if (serverExtraPayload != null) {
                    transport.send("schema", serverExtraPayload)
                }
                if (disconnectReason != null) {
                    transport.disconnect(disconnectReason)
                }
                pollRudpAfterSend(transport, 250)
                println("""{"status":"ok"}""")
                return
            }
            transport.pollResends()
            Thread.sleep(5)
        }
    }
    error("Timed out waiting for TypeScript RUDP frame")
}

private fun rudpDialOnce(options: Map<String, String>) {
    val targetHost = options.getValue("target-host")
    val targetPort = options.getValue("target-port").toInt()
    val loopback = InetAddress.getByName("127.0.0.1")
    val socket = DatagramSocket(0, loopback).also { it.soTimeout = 20 }
    CultNetRudpSocketTransportConnection(
        socket = socket,
        mode = CultNetRudpSocketMode.Client,
        runtimeId = "kotlin-rudp-client-interop",
        connectionId = 0xaa886644L,
        remoteAddress = InetSocketAddress(InetAddress.getByName(targetHost), targetPort),
        initialSequence = 1,
        resendDelayMs = 25,
    ).use { transport ->
        transport.connect("kotlin-join".toByteArray(StandardCharsets.UTF_8))
        var sent = false
        val deadline = System.nanoTime() + 5_000_000_000L
        while (System.nanoTime() < deadline) {
            val frame = transport.receiveOnce()
            if (frame != null) {
                requireRudpFrame(frame, "schema", "ts-kotlin-server-state")
                println("""{"status":"ok"}""")
                return
            }
            transport.pollResends()
            if (transport.connected && !sent) {
                transport.send("schema", "kotlin-client-state".toByteArray(StandardCharsets.UTF_8))
                sent = true
            }
            Thread.sleep(5)
        }
    }
    error("Timed out waiting for TypeScript RUDP response")
}

private fun rudpServeSchemaMessageOnce(options: Map<String, String>) {
    val bindHost = options["bind-host"] ?: "127.0.0.1"
    val bindPort = options["bind-port"]?.toInt() ?: 0
    val socket = DatagramSocket(bindPort, InetAddress.getByName(bindHost)).also { it.soTimeout = 20 }
    CultNetRudpSocketTransportConnection(
        socket = socket,
        mode = CultNetRudpSocketMode.Server,
        runtimeId = "kotlin-rudp-message-interop",
        connectionId = 0x446688abL,
        initialSequence = 100,
        resendDelayMs = 25,
    ).use { transport ->
        println("""{"status":"ready","port":${socket.localPort}}""")
        val deadline = System.nanoTime() + 5_000_000_000L
        while (System.nanoTime() < deadline) {
            val frame = transport.receiveOnce()
            if (frame != null) {
                require(frame.channelId == "schema") { "Unexpected RUDP channel ${frame.channelId}" }
                val request = parseCultNetMessage(frame.payload)
                require(request.schemaVersion == "cultnet.schema_catalog_request.v0") { "Unexpected schema message ${request.schemaVersion}" }
                require(request.body["messageId"] == "ts-kotlin-schema-message") { "Unexpected messageId ${request.body["messageId"]}" }
                transport.sendSchemaMessage(
                    cultNetHello(
                        runtimeId = "kotlin-rudp-message-interop",
                        displayName = "Kotlin RUDP Interop",
                        supportedMessageVersions = listOf("cultnet.hello.v0", "cultnet.schema_catalog_request.v0"),
                        transportProfiles = listOf(transport.profile),
                    ),
                )
                pollRudpAfterSend(transport, 250)
                println("""{"status":"ok"}""")
                return
            }
            transport.pollResends()
            Thread.sleep(5)
        }
    }
    error("Timed out waiting for TypeScript schema-v0 message")
}

private fun rudpDialSchemaMessageOnce(options: Map<String, String>) {
    val targetHost = options.getValue("target-host")
    val targetPort = options.getValue("target-port").toInt()
    val loopback = InetAddress.getByName("127.0.0.1")
    val socket = DatagramSocket(0, loopback).also { it.soTimeout = 20 }
    CultNetRudpSocketTransportConnection(
        socket = socket,
        mode = CultNetRudpSocketMode.Client,
        runtimeId = "kotlin-rudp-message-client-interop",
        connectionId = 0xaa886645L,
        remoteAddress = InetSocketAddress(InetAddress.getByName(targetHost), targetPort),
        initialSequence = 1,
        resendDelayMs = 25,
    ).use { transport ->
        transport.connect("kotlin-message-join")
        var sent = false
        val deadline = System.nanoTime() + 5_000_000_000L
        while (System.nanoTime() < deadline) {
            val frame = transport.receiveOnce()
            if (frame != null) {
                require(frame.channelId == "schema") { "Unexpected RUDP channel ${frame.channelId}" }
                val response = parseCultNetMessage(frame.payload)
                require(response.schemaVersion == "cultnet.hello.v0") { "Unexpected schema message ${response.schemaVersion}" }
                require(response.body["runtimeId"] == "ts-kotlin-rudp-message-server") { "Unexpected runtimeId ${response.body["runtimeId"]}" }
                println("""{"status":"ok"}""")
                return
            }
            transport.pollResends()
            if (transport.connected && !sent) {
                transport.sendSchemaMessage(
                    cultNetSchemaCatalogRequest(
                        messageId = "kotlin-ts-schema-message",
                        kinds = listOf("wire_message"),
                    ),
                )
                sent = true
            }
            Thread.sleep(5)
        }
    }
    error("Timed out waiting for TypeScript schema-v0 response")
}

private fun pollRudpAfterSend(transport: CultNetRudpSocketTransportConnection, durationMs: Long) {
    val deadline = System.nanoTime() + durationMs * 1_000_000L
    while (System.nanoTime() < deadline) {
        transport.receiveOnce()
        transport.pollResends()
        Thread.sleep(5)
    }
}

private fun requireRudpFrame(frame: CultNetTransportFrame, expectedChannelId: String, expectedPayload: String) {
    requireRudpFrame(frame, expectedChannelId, expectedPayload.toByteArray(StandardCharsets.UTF_8))
}

private fun requireRudpFrame(frame: CultNetTransportFrame, expectedChannelId: String, expectedPayload: ByteArray) {
    if (frame.channelId != expectedChannelId || !frame.payload.contentEquals(expectedPayload)) {
        error("Unexpected RUDP frame: channel=${frame.channelId}, payload=${String(frame.payload, StandardCharsets.UTF_8)}")
    }
}

private fun parseArgs(args: List<String>): Map<String, String> {
    val parsed = linkedMapOf<String, String>()
    var index = 0
    while (index < args.size) {
        val token = args[index]
        if (!token.startsWith("--")) {
            index += 1
            continue
        }
        parsed[token.removePrefix("--")] = args.getOrNull(index + 1) ?: error("Missing value for $token")
        index += 2
    }
    return parsed
}

private fun rudpPacketCodecUsesDeterministicReliableOrderedFixture() {
    val encoded = encodeRudpPacket(
        CultNetRudpPacket(
            packetType = CultNetRudpPacketType.Data,
            connectionId = 0x01020304,
            sequence = 0x0000002a,
            ack = 0x00000029,
            ackMask = 0x80000001,
            channelId = "schema",
            reliable = true,
            ordered = true,
            fragmentId = 7,
            fragmentIndex = 1,
            fragmentCount = 3,
            payload = "hello".toByteArray(StandardCharsets.UTF_8),
        ),
    )

    check(encoded.joinToString("") { "%02x".format(it.toInt() and 0xff) } == "434e523000030b2a010203040000002a0000002980000001000700010003000000050600736368656d6168656c6c6f")
    val decoded = decodeRudpPacket(encoded)
    check(decoded.packetType == CultNetRudpPacketType.Data)
    check(decoded.connectionId == 0x01020304L)
    check(decoded.sequence == 0x0000002aL)
    check(decoded.ack == 0x00000029L)
    check(decoded.ackMask == 0x80000001L)
    check(decoded.channelId == "schema")
    check(decoded.reliable)
    check(decoded.ordered)
    check(!decoded.sequenced)
    check(decoded.fragmentId == 7)
    check(decoded.fragmentIndex == 1)
    check(decoded.fragmentCount == 3)
    check(String(decoded.payload, StandardCharsets.UTF_8) == "hello")
}

private fun rudpSessionPingsAndDetectsReceiveTimeout() {
    val client = CultNetRudpSession(CultNetRudpSessionOptions(connectionId = 101, initialSequence = 1))
    val server = CultNetRudpSession(CultNetRudpSessionOptions(connectionId = 101, initialSequence = 100))
    val connect = client.createConnect(0, "join".toByteArray(StandardCharsets.UTF_8))
    val accept = server.acceptConnect(connect, 10)
    client.receive(accept, 20)

    val ping = client.createPing("pulse".toByteArray(StandardCharsets.UTF_8))
    val pingResult = server.receive(ping, 30)
    val pong = pingResult.reply ?: error("Ping did not produce a pong")
    check(pong.packetType == CultNetRudpPacketType.Pong)
    check(String(pong.payload, StandardCharsets.UTF_8) == "pulse")

    val pongResult = client.receive(pong, 40)
    check(pongResult.pong)
    check(String(pongResult.pongPayload, StandardCharsets.UTF_8) == "pulse")
    check(!client.checkTimeout(90, 50))
    check(client.checkTimeout(91, 50))
    check(!client.connected)
}

private fun rudpSessionBoundsPendingReliablePacketsBeforeEnqueue() {
    val session = CultNetRudpSession(CultNetRudpSessionOptions(connectionId = 102, initialSequence = 1, maxPendingReliablePackets = 2))
    session.receive(CultNetRudpPacket(CultNetRudpPacketType.Accept, 102, 50, 0, 0, "control"))
    session.send("schema", "first".toByteArray(StandardCharsets.UTF_8), CultNetRudpSendOptions(reliable = true, ordered = true))
    session.send("schema", "second".toByteArray(StandardCharsets.UTF_8), CultNetRudpSendOptions(reliable = true, ordered = true))
    val third = runCatching {
        session.send("schema", "third".toByteArray(StandardCharsets.UTF_8), CultNetRudpSendOptions(reliable = true, ordered = true))
    }.exceptionOrNull()
    check(third is IOException)
    check(third.message?.contains("reliable send queue is full") == true)
    check(session.pendingReliableSequences == listOf(1L, 2L))

    val fragmented = CultNetRudpSession(CultNetRudpSessionOptions(connectionId = 103, initialSequence = 1, maxPendingReliablePackets = 3))
    fragmented.receive(CultNetRudpPacket(CultNetRudpPacketType.Accept, 103, 50, 0, 0, "control"))
    val fragmentError = runCatching {
        fragmented.sendMany(
            "schema",
            "fragment-me".toByteArray(StandardCharsets.UTF_8),
            CultNetRudpSendOptions(reliable = true, ordered = true),
            maxFragmentBytes = 3,
        )
    }.exceptionOrNull()
    check(fragmentError is IOException)
    check(fragmentError.message?.contains("reliable send queue is full") == true)
    check(fragmented.pendingReliableSequences.isEmpty())
}

private fun rudpSessionFragmentsAndReassemblesReliableOrderedPayloads() {
    val sender = CultNetRudpSession(CultNetRudpSessionOptions(connectionId = 456, initialSequence = 1))
    val receiver = CultNetRudpSession(CultNetRudpSessionOptions(connectionId = 456, initialSequence = 100))
    sender.receive(CultNetRudpPacket(CultNetRudpPacketType.Accept, 456, 90, 0, 0, "control"))
    receiver.receive(CultNetRudpPacket(CultNetRudpPacketType.Accept, 456, 91, 0, 0, "control"))

    val packets = sender.sendMany(
        "schema",
        "fragment-me-please".toByteArray(StandardCharsets.UTF_8),
        CultNetRudpSendOptions(reliable = true, ordered = true, nowMs = 10),
        maxFragmentBytes = 5,
    )
    check(packets.size == 4)
    check(packets.map { it.fragmentCount } == listOf(4, 4, 4, 4))
    check(packets.map { it.fragmentIndex } == listOf(0, 1, 2, 3))
    check(packets.all { it.fragmentId == packets.first().fragmentId })
    check(receiver.receive(packets[0]).delivered.isEmpty())
    check(receiver.receive(packets[1]).delivered.isEmpty())
    check(receiver.receive(packets[2]).delivered.isEmpty())
    val delivered = receiver.receive(packets[3]).delivered
    check(delivered.size == 1)
    check(String(delivered.first().payload, StandardCharsets.UTF_8) == "fragment-me-please")
    check(delivered.first().sequence == packets.first().sequence)
}

private fun rudpSessionAdvancesLargeFragmentSetsThroughBoundedReliableWindow() {
    val connectionId = 457L
    val sender = CultNetRudpSession(CultNetRudpSessionOptions(connectionId = connectionId, initialSequence = 1))
    val receiver = CultNetRudpSession(CultNetRudpSessionOptions(connectionId = connectionId, initialSequence = 100))
    sender.receive(CultNetRudpPacket(CultNetRudpPacketType.Accept, connectionId, 0, 0, 0, "control"))
    receiver.receive(CultNetRudpPacket(CultNetRudpPacketType.Accept, connectionId, 0, 0, 0, "control"))

    val fragmentCount = CultNetRudpSession.ReliableSendWindowPackets + 17
    val payload = ByteArray(fragmentCount * 8) { index -> (index % 251).toByte() }
    val wire = ArrayDeque(sender.sendMany(
        "schema",
        payload,
        CultNetRudpSendOptions(reliable = true, ordered = true, nowMs = 1),
        maxFragmentBytes = 8,
    ))
    check(wire.size == CultNetRudpSession.ReliableSendWindowPackets)
    check(sender.pendingReliableSequences.size == wire.size)
    check(sender.queuedReliablePacketCount == 17)

    val delivered = mutableListOf<CultNetRudpDeliveredFrame>()
    while (!wire.isEmpty()) {
        val packet = wire.removeFirst()
        delivered.addAll(receiver.receive(packet, 2).delivered)
        val acknowledged = sender.receive(receiver.createAckFor(packet.sequence), 3)
        acknowledged.readyToSend.forEach { wire.addLast(it) }
    }
    check(sender.outstandingReliablePacketCount == 0)
    check(delivered.size == 1)
    check(delivered.first().payload.contentEquals(payload))
}

private fun rudpSocketTransportErgonomicFactoriesCarrySchemaFrames() {
    val connectionId = 0x10203042L
    CultMesh.createRudpServer(
        runtimeId = "kotlin-rudp-sugar-server",
        connectionId = connectionId,
        tuning = CultNetRudpSocketTuning(resendDelayMs = 25, maxFragmentBytes = 1024, maxPendingReliablePackets = 16),
    ).use { server ->
        val endpoint = CultMesh.parseRudpEndpoint("rudp://127.0.0.1:${server.localPort}")
        check(endpoint.host == "127.0.0.1")
        check(endpoint.port == server.localPort)
        val peer = CultMeshPeerCard(
            peerId = "kotlin-rudp-sugar-peer",
            verseId = "local",
            endpoints = listOf(endpoint.uri),
            roles = listOf("schema"),
            authorityLeaseId = "lease:kotlin-rudp-sugar-peer",
        )
        val peers = CultMesh.createPeerCatalog()
        val leases = CultMesh.createAuthorityLeaseCatalog()
        peers.upsert(peer)
        val unauthorized = runCatching {
            CultMesh.createRudpClientForAuthorizedPeer(
                runtimeId = "kotlin-rudp-sugar-client",
                connectionId = connectionId,
                peers = peers,
                leases = leases,
                verseId = "local",
                role = "schema",
                tuning = CultNetRudpSocketTuning(resendDelayMs = 25, maxFragmentBytes = 1024, maxPendingReliablePackets = 16),
            )
        }.exceptionOrNull()
        check(unauthorized is IOException)
        leases.upsert(
            CultMeshAuthorityLease(
                leaseId = "lease:kotlin-rudp-sugar-peer",
                verseId = "local",
                peerId = "kotlin-rudp-sugar-peer",
                roles = listOf("schema"),
                issuerRuntimeId = "kotlin-authority",
                validFrom = Instant.now().minusSeconds(60),
                expiresAt = Instant.now().plusSeconds(60),
            ),
        )
        val serverPumpDone = AtomicBoolean(false)
        val serverPump = Thread {
            while (!server.connected && !serverPumpDone.get()) {
                server.receiveOnce()
                server.pollResends()
                Thread.sleep(5)
            }
        }
        serverPump.isDaemon = true
        serverPump.start()

        val client = try {
            CultMesh.connectRudpClientForAuthorizedPeer(
                runtimeId = "kotlin-rudp-sugar-client",
                connectionId = connectionId,
                peers = peers,
                leases = leases,
                verseId = "local",
                role = "schema",
                tuning = CultNetRudpSocketTuning(resendDelayMs = 25, maxFragmentBytes = 1024, maxPendingReliablePackets = 16),
                connectPayload = "join".toByteArray(StandardCharsets.UTF_8),
            )
        } finally {
            serverPumpDone.set(true)
            serverPump.join(1_000)
        }

        client.use {
            check(client.connected)
            check(server.connected)
            val schemaCatalog = CultNetSchemaCatalog()
            schemaCatalog.upsert(
                defineCultNetSchemaDescriptor(
                    schemaId = "kotlin.rudp.note.v1",
                    kind = "document_payload",
                    documentType = "kotlin.rudp.note",
                    title = "Kotlin RUDP Note",
                    schemaJson = """{"type":"string"}""",
                ),
            )
            val syncedSchemas = CultNetSchemaCatalog()
            var schemaDescriptors: List<CultNetSchemaDescriptor>? = null
            var clientError: Throwable? = null
            val schemaThread = Thread {
                try {
                    schemaDescriptors = client.syncSchemaCatalog(
                        syncedSchemas,
                        messageId = "rudp-schema",
                        includeSchemaJson = true,
                        kinds = listOf("document_payload"),
                        timeoutMs = 2_000,
                    )
                } catch (error: Throwable) {
                    clientError = error
                }
            }
            schemaThread.isDaemon = true
            schemaThread.start()
            val schemaRequest = server.receiveSchemaMessage(2_000)
                ?: error("RUDP server did not receive schema catalog request")
            server.sendSchemaMessage(schemaCatalog.createResponse(schemaRequest))
            pollRudpAfterSend(server, 50)
            schemaThread.join(2_000)
            if (schemaThread.isAlive) error("RUDP schema helper client did not finish")
            clientError?.let { throw it }
            check(schemaDescriptors?.single()?.schemaId == "kotlin.rudp.note.v1")
            check(syncedSchemas.get("kotlin.rudp.note.v1")?.documentType == "kotlin.rudp.note")
            server.receiveOnce()

            val shardCatalog = CultNetShardCatalog()
            shardCatalog.upsert(
                CultNetShardDescriptor(
                    shardId = "kotlin-rudp-shard",
                    ownerRuntimeId = "kotlin-rudp-sugar-server",
                    epoch = 3,
                    isPrimary = true,
                    schemaIds = listOf("kotlin.rudp.note.v1"),
                    keyPrefix = "note:",
                ),
            )
            var shardDescriptors: List<CultNetShardDescriptor>? = null
            clientError = null
            val shardThread = Thread {
                try {
                    shardDescriptors = client.fetchShardDescriptors(
                        messageId = "rudp-shards",
                        schemaIds = listOf("kotlin.rudp.note.v1"),
                        recordKeys = listOf("note:1"),
                        timeoutMs = 2_000,
                    )
                } catch (error: Throwable) {
                    clientError = error
                }
            }
            shardThread.isDaemon = true
            shardThread.start()
            val shardRequest = server.receiveSchemaMessage(2_000)
                ?: error("RUDP server did not receive shard catalog request")
            server.sendSchemaMessage(shardCatalog.createResponse(shardRequest))
            pollRudpAfterSend(server, 50)
            shardThread.join(2_000)
            if (shardThread.isAlive) error("RUDP shard helper client did not finish")
            clientError?.let { throw it }
            check(shardDescriptors?.single()?.shardId == "kotlin-rudp-shard")

            client.sendSchema("client-state")
            check(String(server.receiveSchema(1_000) ?: error("Server did not receive schema frame"), StandardCharsets.UTF_8) == "client-state")
            client.receiveOnce()
            server.sendSchema("server-state")
            check(String(client.receiveSchema(1_000) ?: error("Client did not receive schema frame"), StandardCharsets.UTF_8) == "server-state")
            server.receiveOnce()
        }
    }
}

private fun rudpSocketTransportHandshakesAndCarriesReliableOrderedSchemaFrames() {
    val loopback = InetAddress.getByName("127.0.0.1")
    val serverSocket = DatagramSocket(0, loopback).also { it.soTimeout = 20 }
    val clientSocket = DatagramSocket(0, loopback).also { it.soTimeout = 20 }
    val connectionId = 0x10203040L
    CultNetRudpSocketTransportConnection(
        socket = serverSocket,
        mode = CultNetRudpSocketMode.Server,
        runtimeId = "kotlin-rudp-server",
        connectionId = connectionId,
        initialSequence = 100,
        resendDelayMs = 25,
    ).use { server ->
        CultNetRudpSocketTransportConnection(
            socket = clientSocket,
            mode = CultNetRudpSocketMode.Client,
            runtimeId = "kotlin-rudp-client",
            connectionId = connectionId,
            remoteAddress = InetSocketAddress(loopback, serverSocket.localPort),
            initialSequence = 1,
            resendDelayMs = 25,
        ).use { client ->
            client.connect("join".toByteArray(StandardCharsets.UTF_8))
            pumpRudpHandshake(client, server)
            check(client.connected)
            check(server.connected)

            client.send("schema", "client-state".toByteArray(StandardCharsets.UTF_8))
            val serverFrame = receiveRudpFrame(server)
            check(serverFrame.channelId == "schema")
            check(String(serverFrame.payload, StandardCharsets.UTF_8) == "client-state")

            server.send("schema", "server-state".toByteArray(StandardCharsets.UTF_8))
            val clientFrame = receiveRudpFrame(client)
            check(clientFrame.channelId == "schema")
            check(String(clientFrame.payload, StandardCharsets.UTF_8) == "server-state")
            check(server.profile.transports.first().protocol == "rudp")
            check(client.stats.framesSent == 1L)
            check(server.stats.framesReceived == 1L)
        }
    }
}

private fun rudpSocketTransportCarriesFragmentedReliableOrderedSchemaFrames() {
    val loopback = InetAddress.getByName("127.0.0.1")
    val serverSocket = DatagramSocket(0, loopback).also { it.soTimeout = 20 }
    val clientSocket = DatagramSocket(0, loopback).also { it.soTimeout = 20 }
    val connectionId = 0x10203041L
    CultNetRudpSocketTransportConnection(
        socket = serverSocket,
        mode = CultNetRudpSocketMode.Server,
        runtimeId = "kotlin-rudp-fragment-server",
        connectionId = connectionId,
        initialSequence = 100,
        resendDelayMs = 25,
        maxFragmentBytes = 8,
    ).use { server ->
        CultNetRudpSocketTransportConnection(
            socket = clientSocket,
            mode = CultNetRudpSocketMode.Client,
            runtimeId = "kotlin-rudp-fragment-client",
            connectionId = connectionId,
            remoteAddress = InetSocketAddress(loopback, serverSocket.localPort),
            initialSequence = 1,
            resendDelayMs = 25,
            maxFragmentBytes = 8,
        ).use { client ->
            val payload = "this-schema-frame-is-larger-than-one-rudp-fragment".toByteArray(StandardCharsets.UTF_8)
            client.connect("join".toByteArray(StandardCharsets.UTF_8))
            pumpRudpHandshake(client, server)
            client.send("schema", payload)
            val serverFrame = receiveRudpFrame(server)
            check(serverFrame.channelId == "schema")
            check(serverFrame.payload.contentEquals(payload))
            check(client.stats.framesSent == 1L)
            check(server.stats.framesReceived == 1L)
        }
    }
}

private fun pumpRudpHandshake(
    client: CultNetRudpSocketTransportConnection,
    server: CultNetRudpSocketTransportConnection,
) {
    repeat(20) {
        server.receiveOnce()
        client.receiveOnce()
        server.receiveOnce()
        if (client.connected && server.connected) return
        Thread.sleep(5)
    }
    error("RUDP socket handshake did not complete")
}

private fun receiveRudpFrame(transport: CultNetRudpSocketTransportConnection): CultNetTransportFrame {
    repeat(20) {
        val frame = transport.receiveOnce()
        if (frame != null) return frame
        Thread.sleep(5)
    }
    error("RUDP socket frame was not delivered")
}

class CultNetWebSocketClient private constructor(
    private val socket: Socket,
    private val input: InputStream,
    private val output: OutputStream,
    private val random: SecureRandom,
) : AutoCloseable {
    companion object {
        fun connect(uri: URI, random: SecureRandom = SecureRandom()): CultNetWebSocketClient {
            val port = if (uri.port > 0) uri.port else 80
            val socket = Socket(uri.host, port)
            socket.tcpNoDelay = true
            val input = socket.getInputStream()
            val output = socket.getOutputStream()
            val nonce = ByteArray(16)
            random.nextBytes(nonce)
            val key = Base64.getEncoder().encodeToString(nonce)
            val request =
                "GET ${uri.rawPath} HTTP/1.1\r\n" +
                    "Host: ${uri.host}:$port\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Key: $key\r\n" +
                    "Sec-WebSocket-Version: 13\r\n\r\n"
            output.write(request.toByteArray(StandardCharsets.US_ASCII))
            output.flush()
            val response = readHttpHeaders(input)
            if (!response.startsWith("HTTP/1.1 101")) {
                throw IOException(response.lineSequence().firstOrNull() ?: "websocket handshake failed")
            }
            return CultNetWebSocketClient(socket, input, output, random)
        }

        private fun readHttpHeaders(input: InputStream): String {
            val bytes = ByteArrayOutputStream()
            var a = -1
            var b = -1
            var c = -1
            while (true) {
                val d = input.read()
                if (d < 0) break
                bytes.write(d)
                if (a == '\r'.code && b == '\n'.code && c == '\r'.code && d == '\n'.code) break
                a = b
                b = c
                c = d
            }
            return bytes.toString(StandardCharsets.US_ASCII.name())
        }
    }

    fun readFrame(): CultNetFrame {
        val b0 = input.read()
        val b1 = input.read()
        if (b0 < 0 || b1 < 0) throw EOFException("websocket closed")
        val opcode = b0 and 0x0f
        val masked = (b1 and 0x80) != 0
        var length = (b1 and 0x7f).toLong()
        if (length == 126L) {
            length = ((input.read() and 0xff) shl 8 or (input.read() and 0xff)).toLong()
        } else if (length == 127L) {
            length = 0
            repeat(8) { length = (length shl 8) or (input.read() and 0xff).toLong() }
        }
        val mask = ByteArray(4)
        if (masked) input.readExact(mask)
        val payload = ByteArray(length.toInt())
        input.readExact(payload)
        if (masked) payload.indices.forEach { payload[it] = (payload[it].toInt() xor mask[it % 4].toInt()).toByte() }
        return CultNetFrame(opcode, payload)
    }

    fun readFrame(timeoutMs: Long): CultNetFrame? {
        val previousTimeout = socket.soTimeout
        socket.soTimeout = timeoutMs.coerceAtLeast(1).coerceAtMost(Int.MAX_VALUE.toLong()).toInt()
        return try {
            readFrame()
        } catch (_: SocketTimeoutException) {
            null
        } finally {
            socket.soTimeout = previousTimeout
        }
    }

    @Synchronized
    fun sendBinary(payload: ByteArray) {
        val mask = ByteArray(4)
        random.nextBytes(mask)
        val frame = ByteArrayOutputStream()
        frame.write(0x82)
        when {
            payload.size < 126 -> frame.write(0x80 or payload.size)
            payload.size <= 65535 -> {
                frame.write(0x80 or 126)
                frame.write((payload.size shr 8) and 0xff)
                frame.write(payload.size and 0xff)
            }
            else -> {
                frame.write(0x80 or 127)
                frame.write(ByteBuffer.allocate(8).putLong(payload.size.toLong()).array())
            }
        }
        frame.write(mask)
        payload.indices.forEach { frame.write(payload[it].toInt() xor mask[it % 4].toInt()) }
        output.write(frame.toByteArray())
        output.flush()
    }

    override fun close() {
        try {
            socket.close()
        } catch (_: IOException) {
        }
    }
}

private fun InputStream.readExact(buffer: ByteArray) {
    var offset = 0
    while (offset < buffer.size) {
        val read = read(buffer, offset, buffer.size - offset)
        if (read < 0) throw EOFException("stream closed")
        offset += read
    }
}

private fun readWebSocketHandshake(input: InputStream): String {
    val bytes = ByteArrayOutputStream()
    var a = -1
    var b = -1
    var c = -1
    while (true) {
        val d = input.read()
        if (d < 0) throw EOFException("websocket handshake closed")
        bytes.write(d)
        if (a == '\r'.code && b == '\n'.code && c == '\r'.code && d == '\n'.code) break
        a = b
        b = c
        c = d
    }
    return bytes.toString(StandardCharsets.US_ASCII.name())
}

private fun readMaskedWebSocketBinaryPayload(input: InputStream): ByteArray {
    val b0 = input.read()
    val b1 = input.read()
    if (b0 < 0 || b1 < 0) throw EOFException("websocket frame closed")
    if ((b0 and 0x0f) != 2) throw IOException("Expected websocket binary opcode, received ${b0 and 0x0f}")
    if ((b1 and 0x80) == 0) throw IOException("Expected masked websocket client frame")
    var length = (b1 and 0x7f).toLong()
    if (length == 126L) {
        val extended = ByteArray(2)
        input.readExact(extended)
        length = ((extended[0].toInt() and 0xff) shl 8 or (extended[1].toInt() and 0xff)).toLong()
    } else if (length == 127L) {
        val extended = ByteArray(8)
        input.readExact(extended)
        length = ByteBuffer.wrap(extended).getLong()
    }
    val mask = ByteArray(4)
    input.readExact(mask)
    val payload = ByteArray(length.toInt())
    input.readExact(payload)
    payload.indices.forEach { payload[it] = (payload[it].toInt() xor mask[it % 4].toInt()).toByte() }
    return payload
}

private fun writeUnmaskedWebSocketBinaryPayload(output: OutputStream, payload: ByteArray) {
    val frame = ByteArrayOutputStream()
    frame.write(0x82)
    when {
        payload.size < 126 -> frame.write(payload.size)
        payload.size <= 65535 -> {
            frame.write(126)
            frame.write((payload.size shr 8) and 0xff)
            frame.write(payload.size and 0xff)
        }
        else -> {
            frame.write(127)
            frame.write(ByteBuffer.allocate(8).putLong(payload.size.toLong()).array())
        }
    }
    frame.write(payload)
    output.write(frame.toByteArray())
    output.flush()
}

class CultNetWebSocketTransportConnection(
    private val client: CultNetWebSocketClient,
    override val profile: CultNetTransportProfile,
) : CultNetSchemaMessageTransport {
    override var stats: CultNetTransportStats = CultNetTransportStats()
        private set

    companion object {
        fun connect(
            uri: URI,
            random: SecureRandom = SecureRandom(),
            runtimeId: String = "kotlin-websocket-client",
            transportId: String = "websocket",
            maxPayloadBytes: Int? = null,
        ): CultNetWebSocketTransportConnection {
            val port = if (uri.port > 0) uri.port else 80
            return CultNetWebSocketTransportConnection(
                CultNetWebSocketClient.connect(uri, random),
                createWebSocketTransportProfile(
                    runtimeId = runtimeId,
                    transportId = transportId,
                    host = uri.host,
                    port = port,
                    maxPayloadBytes = maxPayloadBytes,
                ),
            )
        }
    }

    fun send(channelId: String, payload: ByteArray) {
        if (channelId != "schema") throw IOException("websocket transport only supports the reliable ordered schema channel")
        client.sendBinary(payload)
        stats = stats.copy(bytesSent = stats.bytesSent + payload.size, framesSent = stats.framesSent + 1)
    }

    fun sendSchema(payload: ByteArray) = send("schema", payload)

    fun sendSchema(payload: String) = sendSchema(payload.toByteArray(StandardCharsets.UTF_8))

    override fun sendSchemaMessage(message: CultNetMessage) = sendSchema(message.toBytes())

    fun receive(): CultNetTransportFrame? {
        val frame = client.readFrame()
        if (frame.opcode != 2) return null
        stats = stats.copy(bytesReceived = stats.bytesReceived + frame.payload.size, framesReceived = stats.framesReceived + 1)
        return CultNetTransportFrame("schema", frame.payload)
    }

    fun receive(timeoutMs: Long): CultNetTransportFrame? {
        val frame = client.readFrame(timeoutMs) ?: return null
        if (frame.opcode != 2) return null
        stats = stats.copy(bytesReceived = stats.bytesReceived + frame.payload.size, framesReceived = stats.framesReceived + 1)
        return CultNetTransportFrame("schema", frame.payload)
    }

    fun receiveSchema(): ByteArray? = receive()?.takeIf { it.channelId == "schema" }?.payload

    fun receiveSchema(timeoutMs: Long): ByteArray? = receive(timeoutMs)?.takeIf { it.channelId == "schema" }?.payload

    fun receiveSchemaMessage(): CultNetMessage? = receiveSchema()?.let { parseCultNetMessage(it) }

    override fun receiveSchemaMessage(timeoutMs: Long, pollIntervalMs: Long): CultNetMessage? =
        receiveSchema(timeoutMs)?.let { parseCultNetMessage(it) }

    override fun close() {
        client.close()
    }
}

fun CultNetSchemaMessageTransport.fetchSchemaCatalog(
    messageId: String = "kotlin-schema-catalog",
    includeSchemaJson: Boolean = false,
    schemaIds: List<String> = emptyList(),
    kinds: List<String> = emptyList(),
    timeoutMs: Long = 1_000,
    pollIntervalMs: Long = 5,
): CultNetMessage {
    sendSchemaMessage(cultNetSchemaCatalogRequest(messageId, includeSchemaJson, schemaIds, kinds))
    return requireSchemaResponse(
        receiveSchemaMessage(timeoutMs, pollIntervalMs),
        "cultnet.schema_catalog_response.v0",
        "${schemaTransportLabel()} schema catalog",
    )
}

fun CultNetSchemaMessageTransport.fetchSchemaDescriptors(
    messageId: String = "kotlin-schema-catalog",
    includeSchemaJson: Boolean = false,
    schemaIds: List<String> = emptyList(),
    kinds: List<String> = emptyList(),
    timeoutMs: Long = 1_000,
    pollIntervalMs: Long = 5,
): List<CultNetSchemaDescriptor> =
    CultNetSchemaCatalog().applyResponse(fetchSchemaCatalog(messageId, includeSchemaJson, schemaIds, kinds, timeoutMs, pollIntervalMs))

fun CultNetSchemaMessageTransport.syncSchemaCatalog(
    catalog: CultNetSchemaCatalog,
    messageId: String = "kotlin-schema-catalog",
    includeSchemaJson: Boolean = false,
    schemaIds: List<String> = emptyList(),
    kinds: List<String> = emptyList(),
    timeoutMs: Long = 1_000,
    pollIntervalMs: Long = 5,
): List<CultNetSchemaDescriptor> =
    catalog.applyResponse(fetchSchemaCatalog(messageId, includeSchemaJson, schemaIds, kinds, timeoutMs, pollIntervalMs))

fun CultNetSchemaMessageTransport.fetchShardCatalog(
    messageId: String = "kotlin-shard-catalog",
    schemaIds: List<String> = emptyList(),
    recordKeys: List<String> = emptyList(),
    timeoutMs: Long = 1_000,
    pollIntervalMs: Long = 5,
): CultNetMessage {
    sendSchemaMessage(cultNetShardCatalogRequest(messageId, schemaIds, recordKeys))
    return requireSchemaResponse(
        receiveSchemaMessage(timeoutMs, pollIntervalMs),
        "cultnet.shard_catalog_response.v0",
        "${schemaTransportLabel()} shard catalog",
    )
}

fun CultNetSchemaMessageTransport.fetchShardDescriptors(
    messageId: String = "kotlin-shard-catalog",
    schemaIds: List<String> = emptyList(),
    recordKeys: List<String> = emptyList(),
    timeoutMs: Long = 1_000,
    pollIntervalMs: Long = 5,
): List<CultNetShardDescriptor> =
    CultNetShardCatalog().applyResponse(fetchShardCatalog(messageId, schemaIds, recordKeys, timeoutMs, pollIntervalMs))

fun CultNetSchemaMessageTransport.syncShardCatalog(
    catalog: CultNetShardCatalog,
    messageId: String = "kotlin-shard-catalog",
    schemaIds: List<String> = emptyList(),
    recordKeys: List<String> = emptyList(),
    timeoutMs: Long = 1_000,
    pollIntervalMs: Long = 5,
): List<CultNetShardDescriptor> =
    catalog.applyResponse(fetchShardCatalog(messageId, schemaIds, recordKeys, timeoutMs, pollIntervalMs))

private fun CultNetSchemaMessageTransport.schemaTransportLabel(): String =
    profile.transports.firstOrNull()?.protocol ?: "CultNet schema-message transport"

fun CultNetRudpSocketTransportConnection.fetchSchemaCatalog(
    messageId: String = "kotlin-schema-catalog",
    includeSchemaJson: Boolean = false,
    schemaIds: List<String> = emptyList(),
    kinds: List<String> = emptyList(),
    timeoutMs: Long = 1_000,
    pollIntervalMs: Long = 5,
): CultNetMessage {
    return (this as CultNetSchemaMessageTransport).fetchSchemaCatalog(
        messageId,
        includeSchemaJson,
        schemaIds,
        kinds,
        timeoutMs,
        pollIntervalMs,
    )
}

fun CultNetRudpSocketTransportConnection.fetchSchemaDescriptors(
    messageId: String = "kotlin-schema-catalog",
    includeSchemaJson: Boolean = false,
    schemaIds: List<String> = emptyList(),
    kinds: List<String> = emptyList(),
    timeoutMs: Long = 1_000,
    pollIntervalMs: Long = 5,
): List<CultNetSchemaDescriptor> =
    (this as CultNetSchemaMessageTransport).fetchSchemaDescriptors(messageId, includeSchemaJson, schemaIds, kinds, timeoutMs, pollIntervalMs)

fun CultNetRudpSocketTransportConnection.syncSchemaCatalog(
    catalog: CultNetSchemaCatalog,
    messageId: String = "kotlin-schema-catalog",
    includeSchemaJson: Boolean = false,
    schemaIds: List<String> = emptyList(),
    kinds: List<String> = emptyList(),
    timeoutMs: Long = 1_000,
    pollIntervalMs: Long = 5,
): List<CultNetSchemaDescriptor> =
    (this as CultNetSchemaMessageTransport).syncSchemaCatalog(catalog, messageId, includeSchemaJson, schemaIds, kinds, timeoutMs, pollIntervalMs)

fun CultNetRudpSocketTransportConnection.fetchShardCatalog(
    messageId: String = "kotlin-shard-catalog",
    schemaIds: List<String> = emptyList(),
    recordKeys: List<String> = emptyList(),
    timeoutMs: Long = 1_000,
    pollIntervalMs: Long = 5,
): CultNetMessage {
    return (this as CultNetSchemaMessageTransport).fetchShardCatalog(
        messageId,
        schemaIds,
        recordKeys,
        timeoutMs,
        pollIntervalMs,
    )
}

fun CultNetRudpSocketTransportConnection.fetchShardDescriptors(
    messageId: String = "kotlin-shard-catalog",
    schemaIds: List<String> = emptyList(),
    recordKeys: List<String> = emptyList(),
    timeoutMs: Long = 1_000,
    pollIntervalMs: Long = 5,
): List<CultNetShardDescriptor> =
    (this as CultNetSchemaMessageTransport).fetchShardDescriptors(messageId, schemaIds, recordKeys, timeoutMs, pollIntervalMs)

fun CultNetRudpSocketTransportConnection.syncShardCatalog(
    catalog: CultNetShardCatalog,
    messageId: String = "kotlin-shard-catalog",
    schemaIds: List<String> = emptyList(),
    recordKeys: List<String> = emptyList(),
    timeoutMs: Long = 1_000,
    pollIntervalMs: Long = 5,
): List<CultNetShardDescriptor> =
    (this as CultNetSchemaMessageTransport).syncShardCatalog(catalog, messageId, schemaIds, recordKeys, timeoutMs, pollIntervalMs)

private fun requireSchemaResponse(message: CultNetMessage?, expectedSchemaVersion: String, label: String): CultNetMessage {
    val response = message ?: throw IOException("$label request did not receive a schema message")
    if (response.schemaVersion != expectedSchemaVersion) {
        throw IOException("Expected $expectedSchemaVersion, received ${response.schemaVersion}")
    }
    return response
}

class MessagePackReader(payload: ByteArray) {
    private val input = DataInputStream(ByteArrayInputStream(payload))
    private var pushed = -1

    fun readArrayHeader(): Int {
        val code = readCode()
        if (code and 0xf0 == 0x90) return code and 0x0f
        if (code == 0xdc) return input.readUnsignedShort()
        if (code == 0xdd) return input.readInt()
        throw IOException("expected array")
    }

    fun readMapHeader(): Int {
        val code = readCode()
        if (code and 0xf0 == 0x80) return code and 0x0f
        if (code == 0xde) return input.readUnsignedShort()
        if (code == 0xdf) return input.readInt()
        throw IOException("expected map")
    }

    fun readNullableArrayHeader(): Int? {
        val code = readCode()
        if (code == 0xc0) return null
        unread(code)
        return readArrayHeader()
    }

    fun readString(): String = readNullableString() ?: ""

    fun readNullableString(): String? {
        val code = readCode()
        if (code == 0xc0) return null
        return readStringAfterCode(code)
    }

    fun readBinary(): ByteArray {
        val code = readCode()
        val length = when (code) {
            0xc4 -> input.readUnsignedByte()
            0xc5 -> input.readUnsignedShort()
            0xc6 -> input.readInt()
            else -> throw IOException("expected binary")
        }
        return readPayload(length)
    }

    fun readBoolean(): Boolean = when (readCode()) {
        0xc2 -> false
        0xc3 -> true
        else -> throw IOException("expected bool")
    }

    fun readLong(): Long {
        val code = readCode()
        if (code <= 0x7f) return code.toLong()
        if (code >= 0xe0) return code.toByte().toLong()
        return when (code) {
            0xcc -> input.readUnsignedByte().toLong()
            0xcd -> input.readUnsignedShort().toLong()
            0xce -> input.readInt().toLong() and 0xffffffffL
            0xcf -> input.readLong()
            0xd0 -> input.readByte().toLong()
            0xd1 -> input.readShort().toLong()
            0xd2 -> input.readInt().toLong()
            0xd3 -> input.readLong()
            else -> throw IOException("expected integer")
        }
    }

    fun readDouble(): Double {
        val code = readCode()
        if (code == 0xca) return input.readFloat().toDouble()
        if (code == 0xcb) return input.readDouble()
        unread(code)
        return readLong().toDouble()
    }

    fun readAny(): Any? {
        val code = readCode()
        if (code == 0xc0) return null
        if (code == 0xc2) return false
        if (code == 0xc3) return true
        if (code <= 0x7f) return code.toLong()
        if (code >= 0xe0) return code.toByte().toLong()
        if (code and 0xe0 == 0xa0) return readStringAfterCode(code)
        if (code and 0xf0 == 0x90) return readArrayAfterHeader(code and 0x0f)
        if (code and 0xf0 == 0x80) return readMapAfterHeader(code and 0x0f)
        return when (code) {
            0xcc -> input.readUnsignedByte().toLong()
            0xcd -> input.readUnsignedShort().toLong()
            0xce -> input.readInt().toLong() and 0xffffffffL
            0xcf -> input.readLong()
            0xd0 -> input.readByte().toLong()
            0xd1 -> input.readShort().toLong()
            0xd2 -> input.readInt().toLong()
            0xd3 -> input.readLong()
            0xca -> input.readFloat().toDouble()
            0xcb -> input.readDouble()
            0xd9, 0xda, 0xdb -> readStringAfterCode(code)
            0xc4, 0xc5, 0xc6 -> readBinaryAfterCode(code)
            0xdc -> readArrayAfterHeader(input.readUnsignedShort())
            0xdd -> readArrayAfterHeader(input.readInt())
            0xde -> readMapAfterHeader(input.readUnsignedShort())
            0xdf -> readMapAfterHeader(input.readInt())
            else -> throw IOException("unsupported MessagePack value")
        }
    }

    fun readNullableDouble(): Double? {
        val code = readCode()
        if (code == 0xc0) return null
        unread(code)
        return readDouble()
    }

    fun skip() {
        val code = readCode()
        if (code == 0xc0 || code == 0xc2 || code == 0xc3 || code <= 0x7f || code >= 0xe0) return
        if (code and 0xe0 == 0xa0) { input.skipBytes(code and 0x1f); return }
        if (code and 0xf0 == 0x90) { repeat(code and 0x0f) { skip() }; return }
        if (code and 0xf0 == 0x80) { repeat(code and 0x0f) { skip(); skip() }; return }
        when (code) {
            0xcc, 0xd0 -> input.skipBytes(1)
            0xcd, 0xd1 -> input.skipBytes(2)
            0xce, 0xd2, 0xca -> input.skipBytes(4)
            0xcf, 0xd3, 0xcb -> input.skipBytes(8)
            0xd9 -> input.skipBytes(input.readUnsignedByte())
            0xda -> input.skipBytes(input.readUnsignedShort())
            0xdb -> input.skipBytes(input.readInt())
            0xc4 -> input.skipBytes(input.readUnsignedByte())
            0xc5 -> input.skipBytes(input.readUnsignedShort())
            0xc6 -> input.skipBytes(input.readInt())
            0xdc -> repeat(input.readUnsignedShort()) { skip() }
            0xdd -> repeat(input.readInt()) { skip() }
            0xde -> repeat(input.readUnsignedShort()) { skip(); skip() }
            0xdf -> repeat(input.readInt()) { skip(); skip() }
            else -> throw IOException("cannot skip")
        }
    }

    private fun readPayload(length: Int): ByteArray {
        if (length < 0) throw IOException("negative MessagePack length")
        val bytes = ByteArray(length)
        input.readFully(bytes)
        return bytes
    }

    private fun readStringAfterCode(code: Int): String {
        val length = when {
            code and 0xe0 == 0xa0 -> code and 0x1f
            code == 0xd9 -> input.readUnsignedByte()
            code == 0xda -> input.readUnsignedShort()
            code == 0xdb -> input.readInt()
            else -> throw IOException("expected string")
        }
        return String(readPayload(length), StandardCharsets.UTF_8)
    }

    private fun readBinaryAfterCode(code: Int): ByteArray {
        val length = when (code) {
            0xc4 -> input.readUnsignedByte()
            0xc5 -> input.readUnsignedShort()
            0xc6 -> input.readInt()
            else -> throw IOException("expected binary")
        }
        return readPayload(length)
    }

    private fun readArrayAfterHeader(count: Int): List<Any?> {
        if (count < 0) throw IOException("negative MessagePack array length")
        return List(count) { readAny() }
    }

    private fun readMapAfterHeader(count: Int): Map<String, Any?> {
        if (count < 0) throw IOException("negative MessagePack map length")
        val map = linkedMapOf<String, Any?>()
        repeat(count) {
            val key = readAny()
            if (key !is String) throw IOException("CultNet MessagePack maps must use string keys")
            map[key] = readAny()
        }
        return map
    }

    private fun readCode(): Int {
        if (pushed >= 0) {
            val code = pushed
            pushed = -1
            return code
        }
        return input.readUnsignedByte()
    }

    private fun unread(code: Int) {
        pushed = code
    }
}

class MessagePackWriter {
    private val out = ByteArrayOutputStream()
    fun toByteArray(): ByteArray = out.toByteArray()

    fun array(count: Int): MessagePackWriter = apply {
        if (count < 16) out.write(0x90 or count)
        else {
            out.write(0xdc)
            out.write((count shr 8) and 0xff)
            out.write(count and 0xff)
        }
    }

    fun map(count: Int): MessagePackWriter = apply {
        if (count < 16) out.write(0x80 or count)
        else {
            out.write(0xde)
            out.write((count shr 8) and 0xff)
            out.write(count and 0xff)
        }
    }

    fun string(value: String?): MessagePackWriter = apply {
        val bytes = (value ?: "").toByteArray(StandardCharsets.UTF_8)
        when {
            bytes.size < 32 -> out.write(0xa0 or bytes.size)
            bytes.size < 256 -> { out.write(0xd9); out.write(bytes.size) }
            else -> { out.write(0xda); out.write((bytes.size shr 8) and 0xff); out.write(bytes.size and 0xff) }
        }
        out.write(bytes)
    }

    fun binary(value: ByteArray): MessagePackWriter = apply {
        when {
            value.size < 256 -> { out.write(0xc4); out.write(value.size) }
            value.size <= 65535 -> { out.write(0xc5); out.write((value.size shr 8) and 0xff); out.write(value.size and 0xff) }
            else -> { out.write(0xc6); out.write(ByteBuffer.allocate(4).putInt(value.size).array()) }
        }
        out.write(value)
    }

    fun nullableString(value: String?): MessagePackWriter = apply { if (value == null) out.write(0xc0) else string(value) }
    fun nullableBoolean(value: Boolean?): MessagePackWriter = apply { if (value == null) out.write(0xc0) else out.write(if (value) 0xc3 else 0xc2) }
    fun nullableDouble(value: Double?): MessagePackWriter = apply { if (value == null) out.write(0xc0) else doubleValue(value) }
    fun nullableLong(value: Long?): MessagePackWriter = apply { if (value == null) out.write(0xc0) else longValue(value) }
    fun nil(): MessagePackWriter = apply { out.write(0xc0) }
    fun boolean(value: Boolean): MessagePackWriter = apply { out.write(if (value) 0xc3 else 0xc2) }
    fun longValue(value: Long): MessagePackWriter = apply { out.write(0xd3); out.write(ByteBuffer.allocate(8).putLong(value).array()) }
    fun doubleValue(value: Double): MessagePackWriter = apply { out.write(0xcb); out.write(ByteBuffer.allocate(8).putDouble(value).array()) }

    fun value(value: Any?): MessagePackWriter = apply {
        when (value) {
            null -> nil()
            is String -> string(value)
            is ByteArray -> binary(value)
            is Boolean -> boolean(value)
            is Byte -> longValue(value.toLong())
            is Short -> longValue(value.toLong())
            is Int -> longValue(value.toLong())
            is Long -> longValue(value)
            is Float -> doubleValue(value.toDouble())
            is Double -> doubleValue(value)
            is Map<*, *> -> {
                map(value.size)
                for ((key, nested) in value) {
                    if (key !is String) throw IOException("CultNet MessagePack map keys must be strings")
                    string(key)
                    this.value(nested)
                }
            }
            is Iterable<*> -> {
                val items = value.toList()
                array(items.size)
                items.forEach { this.value(it) }
            }
            is Array<*> -> {
                array(value.size)
                value.forEach { this.value(it) }
            }
            else -> throw IOException("Unsupported MessagePack value type ${value::class.java.name}")
        }
    }
}
