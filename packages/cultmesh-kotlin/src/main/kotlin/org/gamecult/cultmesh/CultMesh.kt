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
)

fun createRudpTransportProfile(
    runtimeId: String,
    transportId: String = "rudp",
    host: String? = null,
    port: Int? = null,
    maxPayloadBytes: Int? = null,
    maxFragmentBytes: Int? = null,
): CultNetTransportProfile = CultNetTransportProfile(
    runtimeId = runtimeId,
    transports = listOf(
        CultNetTransportDescriptor(
            transportId = transportId.ifBlank { "rudp" },
            protocol = "rudp",
            host = host,
            port = port,
            channels = listOf(
                CultNetTransportChannel("schema", "reliable", "ordered", maxPayloadBytes, maxFragmentBytes),
                CultNetTransportChannel("latest", "unreliable", "sequenced", maxPayloadBytes, maxFragmentBytes),
                CultNetTransportChannel("realtime", "unreliable", "unordered", maxPayloadBytes, maxFragmentBytes),
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
)

data class CultNetRudpSessionOptions(
    val connectionId: Long,
    val initialSequence: Long = 1,
    val resendDelayMs: Long = 250,
)

data class CultNetRudpSendOptions(
    val reliable: Boolean = false,
    val ordered: Boolean = false,
    val sequenced: Boolean = false,
    val nowMs: Long = 0,
)

private data class PendingReliablePacket(var packet: CultNetRudpPacket, var lastSentAtMs: Long)

class CultNetRudpSession(options: CultNetRudpSessionOptions) {
    val connectionId: Long = uint32(options.connectionId, "connectionId")
    val resendDelayMs: Long = options.resendDelayMs
    private var nextSequence = uint32(options.initialSequence, "initialSequence")
    var connected: Boolean = false
        private set
    private var highestReceivedSequence: Long? = null
    private val receivedSequences = linkedSetOf<Long>()
    private val pendingReliable = linkedMapOf<Long, PendingReliablePacket>()
    private val orderedNextSequenceByChannel = linkedMapOf<String, Long>()
    private val orderedBuffers = linkedMapOf<String, TreeMap<Long, CultNetRudpDeliveredFrame>>()

    val pendingReliableSequences: List<Long>
        get() = pendingReliable.keys.sorted()

    fun createConnect(nowMs: Long = 0, payload: ByteArray = ByteArray(0)): CultNetRudpPacket {
        val packet = createPacket(CultNetRudpPacketType.Connect, "control", payload, reliable = true, ordered = true)
        trackReliable(packet, nowMs)
        return packet
    }

    fun acceptConnect(packet: CultNetRudpPacket, nowMs: Long = 0, payload: ByteArray = ByteArray(0)): CultNetRudpPacket {
        requireConnection(packet)
        if (packet.packetType != CultNetRudpPacketType.Connect) throw IOException("Expected RUDP connect packet, got ${packet.packetType}")
        rememberReceived(packet.sequence)
        connected = true
        val response = createPacket(CultNetRudpPacketType.Accept, "control", payload, reliable = true, ordered = true)
        trackReliable(response, nowMs)
        return response
    }

    fun send(channelId: String, payload: ByteArray, options: CultNetRudpSendOptions = CultNetRudpSendOptions()): CultNetRudpPacket {
        if (!connected) throw IOException("Cannot send RUDP data before the session is connected")
        val packet = createPacket(channelId = channelId, packetType = CultNetRudpPacketType.Data, payload = payload, reliable = options.reliable, ordered = options.ordered, sequenced = options.sequenced)
        if (packet.reliable) trackReliable(packet, options.nowMs)
        return packet
    }

    fun receive(packet: CultNetRudpPacket, nowMs: Long = 0): CultNetRudpReceiveResult {
        @Suppress("UNUSED_VARIABLE")
        val ignoredNow = nowMs
        requireConnection(packet)
        applyAcknowledgements(packet)
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
                return CultNetRudpReceiveResult()
            }
            CultNetRudpPacketType.Data -> Unit
            else -> return CultNetRudpReceiveResult()
        }

        val duplicate = receivedSequences.contains(packet.sequence)
        rememberReceived(packet.sequence)
        if (duplicate) return CultNetRudpReceiveResult()
        val frame = CultNetRudpDeliveredFrame(packet.channelId, packet.payload.copyOf(), packet.sequence)
        return CultNetRudpReceiveResult(delivered = if (packet.ordered) deliverOrdered(frame) else listOf(frame))
    }

    fun createAck(): CultNetRudpPacket = createPacket(CultNetRudpPacketType.Ack, "control", ByteArray(0))

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
    ): CultNetRudpPacket {
        val sequence = nextSequence
        nextSequence = uint32(nextSequence + 1, "sequence")
        val (ack, ackMask) = ackState()
        return CultNetRudpPacket(packetType, connectionId, sequence, ack, ackMask, channelId, reliable, ordered, sequenced, payload = payload.copyOf())
    }

    private fun trackReliable(packet: CultNetRudpPacket, nowMs: Long) {
        pendingReliable[packet.sequence] = PendingReliablePacket(packet.copy(payload = packet.payload.copyOf()), nowMs)
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

    private fun deliverOrdered(frame: CultNetRudpDeliveredFrame): List<CultNetRudpDeliveredFrame> {
        val next = orderedNextSequenceByChannel[frame.channelId]
        if (next == null) {
            orderedNextSequenceByChannel[frame.channelId] = frame.sequence + 1
            return listOf(frame) + drainOrdered(frame.channelId)
        }
        if (frame.sequence < next) return emptyList()
        if (frame.sequence > next) {
            orderedBuffers.getOrPut(frame.channelId) { TreeMap() }[frame.sequence] = frame
            return emptyList()
        }
        orderedNextSequenceByChannel[frame.channelId] = next + 1
        return listOf(frame) + drainOrdered(frame.channelId)
    }

    private fun drainOrdered(channelId: String): List<CultNetRudpDeliveredFrame> {
        val delivered = mutableListOf<CultNetRudpDeliveredFrame>()
        val buffer = orderedBuffers[channelId] ?: return delivered
        while (true) {
            val next = orderedNextSequenceByChannel[channelId] ?: break
            val frame = buffer.remove(next) ?: break
            delivered.add(frame)
            orderedNextSequenceByChannel[channelId] = next + 1
        }
        return delivered
    }

    private fun requireConnection(packet: CultNetRudpPacket) {
        if (packet.connectionId != connectionId) throw IOException("RUDP packet connection id ${packet.connectionId} does not match $connectionId")
    }
}

enum class CultNetRudpSocketMode { Client, Server }

class CultNetRudpSocketTransportConnection(
    private val socket: DatagramSocket,
    private val mode: CultNetRudpSocketMode,
    runtimeId: String,
    connectionId: Long,
    remoteAddress: InetSocketAddress? = null,
    initialSequence: Long = 1,
    resendDelayMs: Long = 250,
) : AutoCloseable {
    private val session = CultNetRudpSession(CultNetRudpSessionOptions(connectionId, initialSequence, resendDelayMs))
    private var remote = remoteAddress
    private val delivered = ArrayDeque<CultNetTransportFrame>()
    private var bytesReceived = 0L
    private var bytesSent = 0L
    private var framesReceived = 0L
    private var framesSent = 0L

    val profile: CultNetTransportProfile = createRudpTransportProfile(
        runtimeId = runtimeId,
        host = socket.localAddress.hostAddress,
        port = socket.localPort,
    )
    val connected: Boolean get() = session.connected
    val stats: CultNetTransportStats get() = CultNetTransportStats(bytesReceived, bytesSent, framesReceived, framesSent)

    fun connect(payload: ByteArray = ByteArray(0)) {
        if (mode != CultNetRudpSocketMode.Client) throw IOException("Only a client RUDP socket transport can initiate connect")
        sendPacket(session.createConnect(nowMs(), payload))
    }

    fun send(channelId: String, payload: ByteArray) {
        sendPacket(session.send(channelId, payload, channelSendOptions(channelId, nowMs())))
        framesSent += 1
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
        rudpSocketTransportHandshakesAndCarriesReliableOrderedSchemaFrames()
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
    val socket = DatagramSocket(bindPort, InetAddress.getByName(bindHost)).also { it.soTimeout = 20 }
    CultNetRudpSocketTransportConnection(
        socket = socket,
        mode = CultNetRudpSocketMode.Server,
        runtimeId = "kotlin-rudp-interop",
        connectionId = 0x446688aaL,
        initialSequence = 100,
        resendDelayMs = 25,
    ).use { transport ->
        println("""{"status":"ready","port":${socket.localPort}}""")
        val deadline = System.nanoTime() + 5_000_000_000L
        while (System.nanoTime() < deadline) {
            val frame = transport.receiveOnce()
            if (frame != null) {
                requireRudpFrame(frame, "schema", "ts-kotlin-client-state")
                transport.send("schema", "kotlin-server-state".toByteArray(StandardCharsets.UTF_8))
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

private fun requireRudpFrame(frame: CultNetTransportFrame, expectedChannelId: String, expectedPayload: String) {
    val expectedBytes = expectedPayload.toByteArray(StandardCharsets.UTF_8)
    if (frame.channelId != expectedChannelId || !frame.payload.contentEquals(expectedBytes)) {
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
