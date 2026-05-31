package org.gamecult.cultmesh

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.Socket
import java.net.URI
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets
import java.security.SecureRandom
import java.util.Base64

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

    fun nullableString(value: String?): MessagePackWriter = apply { if (value == null) out.write(0xc0) else string(value) }
    fun nullableBoolean(value: Boolean?): MessagePackWriter = apply { if (value == null) out.write(0xc0) else out.write(if (value) 0xc3 else 0xc2) }
    fun nullableDouble(value: Double?): MessagePackWriter = apply { if (value == null) out.write(0xc0) else doubleValue(value) }
    fun nullableLong(value: Long?): MessagePackWriter = apply { if (value == null) out.write(0xc0) else longValue(value) }
    fun longValue(value: Long): MessagePackWriter = apply { out.write(0xd3); out.write(ByteBuffer.allocate(8).putLong(value).array()) }
    fun doubleValue(value: Double): MessagePackWriter = apply { out.write(0xcb); out.write(ByteBuffer.allocate(8).putDouble(value).array()) }
}
