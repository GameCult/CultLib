package org.gamecult.cultmesh

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.net.URI
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets
import java.security.SecureRandom
import java.time.Instant
import java.util.ArrayDeque
import java.util.Base64
import java.util.TreeMap

interface CultDocumentCodec<T> {
    val documentType: String
    val schemaVersion: String
    fun encode(value: T): ByteArray
    fun decode(payload: ByteArray): T
}

class CultCache {
    private val codecs = linkedMapOf<String, CultDocumentCodec<*>>()
    private val values = linkedMapOf<String, Any>()

    fun <T : Any> register(codec: CultDocumentCodec<T>) {
        codecs[codec.documentType] = codec
    }

    fun <T : Any> put(codec: CultDocumentCodec<T>, key: String, value: T) {
        register(codec)
        values["${codec.documentType}\n$key"] = value
    }

    @Suppress("UNCHECKED_CAST")
    fun <T : Any> get(codec: CultDocumentCodec<T>, key: String): T? {
        register(codec)
        return values["${codec.documentType}\n$key"] as? T
    }
}

class CultMeshNode(
    val cache: CultCache = CultCache(),
    private val random: SecureRandom = SecureRandom(),
) {
    fun connect(uri: URI): CultNetWebSocketClient = CultNetWebSocketClient.connect(uri, random)

    fun <T : Any> remember(codec: CultDocumentCodec<T>, key: String, value: T) {
        cache.put(codec, key, value)
    }

    fun <T : Any> recall(codec: CultDocumentCodec<T>, key: String): T? = cache.get(codec, key)
}

data class CultNetFrame(val opcode: Int, val payload: ByteArray)

data class CultNetTransportStats(
    val bytesReceived: Long = 0,
    val bytesSent: Long = 0,
    val framesReceived: Long = 0,
    val framesSent: Long = 0,
)

data class CultNetTransportFrame(val channelId: String, val payload: ByteArray)

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
): CultNetTransportProfile = CultNetTransportProfile(
    runtimeId = runtimeId,
    transports = listOf(
        CultNetTransportDescriptor(
            transportId = transportId.ifBlank { "rudp" },
            protocol = "rudp",
            host = host,
            port = port,
            channels = listOf(
                CultNetTransportChannel("schema", "reliable", "ordered", maxPayloadBytes, maxFragmentBytes, maxPendingReliablePackets),
                CultNetTransportChannel("latest", "unreliable", "sequenced", maxPayloadBytes, maxFragmentBytes, maxPendingReliablePackets),
                CultNetTransportChannel("realtime", "unreliable", "unordered", maxPayloadBytes, maxFragmentBytes, maxPendingReliablePackets),
            ),
        ),
    ),
)

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
    private val orderedNextSequenceByChannel = linkedMapOf<String, Long>()
    private val orderedBuffers = linkedMapOf<String, TreeMap<Long, PendingOrderedFrame>>()
    private val fragmentBuffers = linkedMapOf<Pair<String, Int>, FragmentBuffer>()

    val pendingReliableSequences: List<Long>
        get() = pendingReliable.keys.sorted()

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
            return (0 until fragmentCount).map { index ->
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
                if (packet.reliable) trackReliable(packet, options.nowMs)
                packet
            }
        }
        ensureReliableCapacity(if (options.reliable) 1 else 0)
        val packet = createPacket(channelId = channelId, packetType = CultNetRudpPacketType.Data, payload = payload, reliable = options.reliable, ordered = options.ordered, sequenced = options.sequenced)
        if (packet.reliable) trackReliable(packet, options.nowMs)
        return listOf(packet)
    }

    fun receive(packet: CultNetRudpPacket, nowMs: Long = 0): CultNetRudpReceiveResult {
        @Suppress("UNUSED_VARIABLE")
        val ignoredNow = nowMs
        requireConnection(packet)
        applyAcknowledgements(packet)
        lastReceivedAtMs = nowMs
        val expectedSequenceIfUninitialized = (highestReceivedSequence ?: (packet.sequence - 1)) + 1
        when (packet.packetType) {
            CultNetRudpPacketType.Accept -> {
                rememberReceived(packet.sequence)
                connected = true
                return CultNetRudpReceiveResult()
            }
            CultNetRudpPacketType.Ping -> {
                rememberReceived(packet.sequence)
                return CultNetRudpReceiveResult(reply = createPacket(CultNetRudpPacketType.Pong, "control", packet.payload))
            }
            CultNetRudpPacketType.Ack, CultNetRudpPacketType.Pong -> {
                rememberReceived(packet.sequence)
                return CultNetRudpReceiveResult(
                    pong = packet.packetType == CultNetRudpPacketType.Pong,
                    pongPayload = if (packet.packetType == CultNetRudpPacketType.Pong) packet.payload.copyOf() else ByteArray(0),
                )
            }
            CultNetRudpPacketType.Disconnect -> {
                rememberReceived(packet.sequence)
                connected = false
                return CultNetRudpReceiveResult(disconnected = true, disconnectReason = packet.payload.copyOf())
            }
            CultNetRudpPacketType.Data -> Unit
            else -> return CultNetRudpReceiveResult()
        }

        val duplicate = receivedSequences.contains(packet.sequence)
        rememberReceived(packet.sequence)
        if (duplicate) return CultNetRudpReceiveResult()
        val reassembled = reassemble(packet) ?: return CultNetRudpReceiveResult()
        return CultNetRudpReceiveResult(delivered = if (reassembled.ordered) deliverOrdered(reassembled.frame, reassembled.nextSequence, expectedSequenceIfUninitialized) else listOf(reassembled.frame))
    }

    fun createAck(): CultNetRudpPacket = createPacket(CultNetRudpPacketType.Ack, "control", ByteArray(0))

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

    private fun ensureReliableCapacity(packetCount: Int) {
        if (packetCount == 0 || maxPendingReliablePackets == null) return
        if (pendingReliable.size + packetCount > maxPendingReliablePackets) {
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
        val next = orderedNextSequenceByChannel[frame.channelId] ?: minOf(expectedSequenceIfUninitialized, frame.sequence).also {
            orderedNextSequenceByChannel[frame.channelId] = it
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
        }
        return delivered
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
) : AutoCloseable {
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

    val profile: CultNetTransportProfile = createRudpTransportProfile(
        runtimeId = runtimeId,
        host = socket.localAddress.hostAddress,
        port = socket.localPort,
        maxFragmentBytes = maxFragmentBytes,
        maxPendingReliablePackets = maxPendingReliablePackets,
    )
    val connected: Boolean get() = session.connected
    val localPort: Int get() = socket.localPort
    val stats: CultNetTransportStats get() = CultNetTransportStats(bytesReceived, bytesSent, framesReceived, framesSent)

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
        if (packet.packetType == CultNetRudpPacketType.Accept || frame != null) sendPacket(session.createAck())
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
        rudpPacketCodecUsesDeterministicReliableOrderedFixture()
        rudpSessionPingsAndDetectsReceiveTimeout()
        rudpSessionBoundsPendingReliablePacketsBeforeEnqueue()
        rudpSessionFragmentsAndReassemblesReliableOrderedPayloads()
        rudpSocketTransportErgonomicFactoriesCarrySchemaFrames()
        rudpSocketTransportHandshakesAndCarriesReliableOrderedSchemaFrames()
        rudpSocketTransportCarriesFragmentedReliableOrderedSchemaFrames()
        return
    }

    val options = parseArgs(args.drop(1))
    when (args[0]) {
        "rudp-serve-once" -> rudpServeOnce(options)
        "rudp-dial-once" -> rudpDialOnce(options)
        else -> error("Unknown mode ${args[0]}")
    }
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

private fun rudpSocketTransportErgonomicFactoriesCarrySchemaFrames() {
    val connectionId = 0x10203042L
    cultNetRudpServer(
        runtimeId = "kotlin-rudp-sugar-server",
        connectionId = connectionId,
        tuning = CultNetRudpSocketTuning(resendDelayMs = 25, maxFragmentBytes = 8, maxPendingReliablePackets = 16),
    ).use { server ->
        cultNetRudpClient(
            runtimeId = "kotlin-rudp-sugar-client",
            connectionId = connectionId,
            remoteHost = "127.0.0.1",
            remotePort = server.localPort,
            tuning = CultNetRudpSocketTuning(resendDelayMs = 25, maxFragmentBytes = 8, maxPendingReliablePackets = 16),
        ).use { client ->
            client.connect("join")
            check(pumpRudpPairUntilConnected(client, server))
            client.sendSchema("client-state")
            check(String(server.receiveSchema(1_000) ?: error("Server did not receive schema frame"), StandardCharsets.UTF_8) == "client-state")
            server.sendSchema("server-state")
            check(String(client.receiveSchema(1_000) ?: error("Client did not receive schema frame"), StandardCharsets.UTF_8) == "server-state")
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
        val length = when {
            code and 0xe0 == 0xa0 -> code and 0x1f
            code == 0xd9 -> input.readUnsignedByte()
            code == 0xda -> input.readUnsignedShort()
            code == 0xdb -> input.readInt()
            else -> throw IOException("expected string")
        }
        val bytes = ByteArray(length)
        input.readFully(bytes)
        return String(bytes, StandardCharsets.UTF_8)
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
        when (code) {
            0xcc, 0xd0 -> input.skipBytes(1)
            0xcd, 0xd1 -> input.skipBytes(2)
            0xce, 0xd2, 0xca -> input.skipBytes(4)
            0xcf, 0xd3, 0xcb -> input.skipBytes(8)
            0xd9 -> input.skipBytes(input.readUnsignedByte())
            0xda -> input.skipBytes(input.readUnsignedShort())
            0xdb -> input.skipBytes(input.readInt())
            0xdc -> repeat(input.readUnsignedShort()) { skip() }
            0xdd -> repeat(input.readInt()) { skip() }
            else -> throw IOException("cannot skip")
        }
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
    fun longValue(value: Long): MessagePackWriter = apply { out.write(0xd3); out.write(ByteBuffer.allocate(8).putLong(value).array()) }
    fun doubleValue(value: Double): MessagePackWriter = apply { out.write(0xcb); out.write(ByteBuffer.allocate(8).putDouble(value).array()) }
}
