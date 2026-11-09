package org.gamecult.cultmesh

// Replays contracts/cultnet/interop/write-receipt-vectors.json against this runtime: constants, contentHash, every
// `wire` frame (decode to the vector's fields, re-encode to its bytes), every `schemaInvalid` frame (must be refused),
// and every `scenarios` event replay through WriteTracker. `dedupe` and `blankFields` are server inputs and are not
// run here. Mode: `java -cp ... org.gamecult.cultmesh.CultMeshKt write-receipt-vectors <vectors.json>`.

import java.io.File
import java.io.IOException

// The event actions scenarios.semantics names; anything else is refused, not skipped.
private val EVENT_KEYS = listOf("send", "frame", "tick", "sessionEnd", "sendFailed", "transportAck")

fun replayWriteReceiptVectors(path: String): Boolean {
    @Suppress("UNCHECKED_CAST")
    val vectors = JsonReader(File(path).readText(Charsets.UTF_8)).readDocument() as Map<String, Any?>
    val results = linkedMapOf<String, List<String>>()
    fun check(name: String, run: () -> List<String>) {
        results[name] = try { run() } catch (e: Throwable) { listOf("threw: $e") }
    }

    val constants = obj(vectors["constants"])
    check("constants") {
        listOfNotNull(
            "RECEIPT window".takeIf { constants["RECEIPT_RESULT_WINDOW_MS"] != RECEIPT_RESULT_WINDOW_MS },
            "LEGACY window".takeIf { constants["LEGACY_RESULT_WINDOW_MS"] != LEGACY_RESULT_WINDOW_MS },
        )
    }

    for (case in list(obj(vectors["contentHash"])["cases"])) {
        val c = obj(case)
        check("contentHash/${c["name"]}") {
            val got = writeContentHash(c["operation"] as String, c["schemaId"] as String, c["recordKey"] as String, (c["payloadHex"] as String?)?.let(::unhex))
            if (got == c["sha256"]) emptyList() else listOf("$got != ${c["sha256"]}")
        }
    }

    for (case in list(vectors["wire"])) {
        val c = obj(case)
        check("wire/${c["name"]}") {
            val bytes = unhex(c["messagePackHex"] as String)
            val problems = mutableListOf<String>()
            val message = decodeReceiptFrame(parseCultNetMessage(bytes))
            if (message.toWireMap() != c["message"]) problems += "decoded ${message.toWireMap()} != ${c["message"]}"
            val again = message.toBytes().joinToString("") { "%02X".format(it) }
            if (again != (c["messagePackHex"] as String).uppercase()) problems += "re-encoded bytes $again != captured ${c["messagePackHex"]}"
            problems
        }
    }

    // admittedMessageId: a write_admitted messageId needs a letter or number and no Default_Ignorable code point, compared
    // raw. The frame is built with this runtime's own encoder; a case with `wireHex` (the lone surrogate, invalid UTF-8)
    // supplies the whole frame.
    for (case in list(obj(vectors["admittedMessageId"])["cases"])) {
        val c = obj(case)
        check("admittedMessageId/${c["name"]}") {
            val id = list(c["codePoints"]).joinToString("") { String(Character.toChars((it as String).toInt(16))) }.repeat(((c["repeat"] as Long?) ?: 1L).toInt())
            val bytes = (c["wireHex"] as String?)?.let(::unhex) ?: MessagePackWriter().value(linkedMapOf(
                "schemaVersion" to WRITE_ADMITTED_VERSION, "messageId" to id, "operation" to "put", "schemaId" to "note.v0", "recordKey" to "note:1",
            )).toByteArray()
            val decoded = try { decodeReceiptFrame(parseCultNetMessage(bytes)) } catch (_: IOException) { null }
            when {
                c["expect"] == "refuse" -> if (decoded == null) emptyList() else listOf("accepted an id that must be refused (${c["why"]})")
                decoded == null -> listOf("refused an id that must be accepted (${c["why"]})")
                decoded.toWireMap()["messageId"] != id -> listOf("id not echoed raw: ${decoded.toWireMap()["messageId"]}")
                else -> emptyList()
            }
        }
    }

    // The generated ranges file is the independent copy of the embedded tables: first, last and the points either side of every
    // range, so deleting or narrowing any range fails here.
    check("unicodeTableRanges") {
        @Suppress("UNCHECKED_CAST")
        val ranges = JsonReader(File(File(path).parentFile, "write-admitted-message-id-ranges.json").readText(Charsets.UTF_8)).readDocument() as Map<String, Any?>
        fun pairs(key: String) = list(ranges[key]).map { list(it).map { n -> (n as Long).toInt() } }
        val letters = pairs("letterOrNumber")
        val ignorable = pairs("defaultIgnorable")
        val problems = mutableListOf<String>()
        if (letters.size <= 700 || ignorable.size != 17) problems += "ranges file holds ${letters.size} and ${ignorable.size} ranges"
        fun accepted(id: String): Boolean = try {
            decodeReceiptFrame(parseCultNetMessage(MessagePackWriter().value(linkedMapOf(
                "schemaVersion" to WRITE_ADMITTED_VERSION, "messageId" to id, "operation" to "put", "schemaId" to "note.v0", "recordKey" to "note:1",
            )).toByteArray()))
            true
        } catch (_: IOException) { false }
        for ((low, high) in letters) for ((point, want) in listOf(low to true, high to true, low - 1 to false, high + 1 to false)) {
            if (accepted(String(Character.toChars(point))) != want) problems += "U+${point.toString(16)} alone should be ${if (want) "accepted" else "refused"}"
        }
        for ((low, high) in ignorable) for ((point, want) in listOf(low to false, high to false, low - 1 to true, high + 1 to true)) {
            if (accepted("a" + String(Character.toChars(point))) != want) problems += "a + U+${point.toString(16)} should be ${if (want) "accepted" else "refused"}"
        }
        problems
    }

    // A document payload string with a lone surrogate (ED A0 80) is not a write_admitted id: the shared MessagePack reader keeps
    // reading it. Strictness is scoped to the write_admitted frame, never to the reader.
    check("documentPayloadStringWithLoneSurrogateStillDecodes") {
        val text = MessagePackReader(unhex("A3EDA080")).readString()
        if (text.isNotEmpty()) emptyList() else listOf("decoded to an empty string")
    }

    for (case in list(vectors["schemaInvalid"])) {
        val c = obj(case)
        check("schemaInvalid/${c["name"]}") {
            try {
                decodeReceiptFrame(parseCultNetMessage(unhex(c["messagePackHex"] as String)))
                listOf("accepted a frame that must be refused (${c["why"]})")
            } catch (_: IOException) {
                emptyList()
            }
        }
    }

    // Not a vector: `limit_reached` is in error.v1's enum only (write-receipt.md), as the TS and Rust suites also pin.
    check("error_v0_refuses_the_v1_only_limit_reached_code") {
        val body = linkedMapOf<String, Any?>("error" to "x", "routingHint" to null, "code" to "limit_reached", "details" to null)
        val v1 = decodeErrorFrame(CultNetMessage(ERROR_V1_VERSION, body + ("messageId" to null))).code
        try {
            decodeErrorFrame(CultNetMessage(ERROR_V0_VERSION, body))
            listOf("error.v0 accepted limit_reached")
        } catch (_: IOException) {
            if (v1 == "limit_reached") emptyList() else listOf("error.v1 lost limit_reached")
        }
    }

    for (case in list(obj(vectors["scenarios"])["cases"])) {
        val scenario = obj(case)
        check("scenario/${scenario["name"]}") {
            val problems = mutableListOf<String>()
            // The fake server states its capability the only way a real one does: in its hello.
            val hello = cultNetHello(
                "fake-server", "fake",
                supportedMessageVersions = if (scenario["server"] == "receipt") CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS else emptyList(),
            )
            val windowMs = resultWindowMs(helloAdvertisesWriteReceipts(hello))
            if (windowMs != constants[scenario["window"] as String]) problems += "window $windowMs != ${scenario["window"]}"
            val told = linkedMapOf<String, WriteSettlement>()
            val tracker = WriteTracker(windowMs) { id, settlement ->
                if (id in told) problems += "$id settled twice"
                told[id] = settlement
            }
            for (event in list(scenario["events"]).map(::obj).sortedBy { it["atMs"] as Long }) {
                val at = event["atMs"] as Long
                val actions = event.keys - "atMs"
                if (actions.size != 1 || actions.first() !in EVENT_KEYS) { problems += "event at $at has keys $actions, wanted atMs and exactly one of $EVENT_KEYS"; continue }
                when {
                    "send" in event -> tracker.send(event["send"] as String, at)
                    // A frame reaches the client as bytes: the fake server encodes it, the client decodes it.
                    "frame" in event -> tracker.frame(parseCultNetMessage(MessagePackWriter().value(event["frame"]).toByteArray()), at)
                    "tick" in event -> tracker.tick(at)
                    "sessionEnd" in event -> tracker.sessionEnd(at)
                    "sendFailed" in event -> tracker.sendFailed(event["sendFailed"] as String, at)
                    // transportAck: delivery is not an answer; the tracker has no such input and must not need one.
                }
            }
            val expect = obj(scenario["expect"])
            for ((id, wantAny) in expect) {
                val want = obj(wantAny)
                val got = told[id]
                if (got == null) { problems += "$id: never settled, wanted ${want["outcome"]}"; continue }
                val (outcome, code, reason) = when (got) {
                    is WriteSettlement.Admitted -> Triple("admitted", null, null)
                    is WriteSettlement.Refused -> Triple("refused", got.code, null)
                    is WriteSettlement.Unconfirmed -> Triple("unconfirmed", null, got.reason.wire)
                }
                if (outcome != want["outcome"]) problems += "$id: told $outcome, wanted ${want["outcome"]}"
                if ("code" in want && code != want["code"]) problems += "$id: code $code, wanted ${want["code"]}"
                if ("reason" in want && reason != want["reason"]) problems += "$id: reason $reason, wanted ${want["reason"]}"
                if (tracker.recorded(id)?.wire != want["recorded"]) problems += "$id: recorded ${tracker.recorded(id)?.wire}, wanted ${want["recorded"]}"
            }
            for (id in told.keys) if (id !in expect) problems += "$id: settled but the scenario expects nothing for it"
            problems
        }
    }

    // A runner that read fewer vectors than the file declares has silently skipped some.
    check("counts") {
        val declared = obj(vectors["counts"])
        fun ran(prefix: String) = results.keys.count { it.startsWith(prefix) }
        val loaded = mapOf(
            "wire" to list(vectors["wire"]).size, "schemaInvalid" to list(vectors["schemaInvalid"]).size,
            "contentHashCases" to list(obj(vectors["contentHash"])["cases"]).size, "scenarioCases" to list(obj(vectors["scenarios"])["cases"]).size,
            "admittedMessageIdCases" to list(obj(vectors["admittedMessageId"])["cases"]).size,
        )
        val executed = mapOf("wire" to ran("wire/"), "schemaInvalid" to ran("schemaInvalid/"), "contentHashCases" to ran("contentHash/"), "scenarioCases" to ran("scenario/"), "admittedMessageIdCases" to ran("admittedMessageId/"))
        loaded.keys.filter { loaded[it] != (declared[it] as Long).toInt() || executed[it] != loaded[it] }
            .map { "$it: declared ${declared[it]}, loaded ${loaded[it]}, ran ${executed[it]}" }
    }

    for ((name, problems) in results) {
        println(if (problems.isEmpty()) "PASS $name" else "FAIL $name")
        problems.forEach { println("     $it") }
    }
    val failed = results.values.count { it.isNotEmpty() }
    println("${results.size - failed}/${results.size} passed")
    return failed == 0
}

/** Strict decode of whichever receipt-family frame this is, returned as the message its typed form encodes; anything else is refused. */
private fun decodeReceiptFrame(message: CultNetMessage): CultNetMessage = when (message.schemaVersion) {
    WRITE_ADMITTED_VERSION -> decodeWriteAdmitted(message).toMessage()
    ERROR_V1_VERSION, ERROR_V0_VERSION -> decodeErrorFrame(message).toMessage()
    else -> throw IOException("not a receipt frame: ${message.schemaVersion}")
}

@Suppress("UNCHECKED_CAST")
private fun obj(value: Any?): Map<String, Any?> = value as Map<String, Any?>
private fun list(value: Any?): List<Any?> = value as List<Any?>

private fun unhex(text: String): ByteArray = ByteArray(text.length / 2) { text.substring(it * 2, it * 2 + 2).toInt(16).toByte() }

/** A small JSON reader for the vector file: objects, arrays, strings, integers, true/false/null. */
private class JsonReader(private val text: String) {
    private var at = 0

    fun readDocument(): Any? = readValue().also { skipSpace(); if (at != text.length) throw IOException("trailing JSON") }

    private fun skipSpace() { while (at < text.length && text[at] in " \t\r\n") at++ }

    private fun readValue(): Any? {
        skipSpace()
        return when (val c = text[at]) {
            '{' -> {
                at++
                val map = linkedMapOf<String, Any?>()
                skipSpace()
                if (text[at] == '}') { at++; return map }
                do {
                    skipSpace()
                    val key = readString()
                    skipSpace(); expect(':')
                    map[key] = readValue()
                    skipSpace()
                } while (closes('}'))
                map
            }
            '[' -> {
                at++
                val items = mutableListOf<Any?>()
                skipSpace()
                if (text[at] == ']') { at++; return items }
                do {
                    items += readValue()
                    skipSpace()
                } while (closes(']'))
                items
            }
            '"' -> readString()
            't' -> literal("true", true)
            'f' -> literal("false", false)
            'n' -> literal("null", null)
            else -> {
                if (c != '-' && !c.isDigit()) throw IOException("unexpected '$c' at $at")
                val start = at++
                while (at < text.length && text[at].isDigit()) at++
                text.substring(start, at).toLong()
            }
        }
    }

    /** After an element: false when [close] ends the container, true after a comma. */
    private fun closes(close: Char): Boolean {
        val c = text[at++]
        if (c == close) return false
        if (c != ',') throw IOException("expected , or $close at ${at - 1}")
        return true
    }

    private fun literal(word: String, value: Any?): Any? {
        if (!text.startsWith(word, at)) throw IOException("bad literal at $at")
        at += word.length
        return value
    }

    private fun expect(c: Char) { if (text[at++] != c) throw IOException("expected $c at ${at - 1}") }

    private fun readString(): String {
        expect('"')
        val out = StringBuilder()
        while (true) {
            val c = text[at++]
            when (c) {
                '"' -> return out.toString()
                '\\' -> when (val e = text[at++]) {
                    'n' -> out.append('\n'); 't' -> out.append('\t'); 'r' -> out.append('\r')
                    'b' -> out.append('\b'); 'f' -> out.append('\u000c')
                    'u' -> { out.append(text.substring(at, at + 4).toInt(16).toChar()); at += 4 }
                    '"', '\\', '/' -> out.append(e)
                    else -> throw IOException("bad escape \\$e")
                }
                else -> out.append(c)
            }
        }
    }
}
