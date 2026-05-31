package org.gamecult.cultmesh.eve

import org.gamecult.cultmesh.CultDocumentCodec
import org.gamecult.cultmesh.MessagePackReader
import org.gamecult.cultmesh.MessagePackWriter
import java.io.IOException

data class EveDashboardNodeSnapshot(
    val id: String,
    val label: String,
    val kind: String,
    val visible: Boolean,
    val x: Double,
    val y: Double,
    val z: Double,
    val rotation: Double,
    val scale: Double,
    val width: Double,
    val height: Double,
    val health: String,
    val providerId: String?,
    val command: String?,
    val endpoint: String?,
) {
    companion object {
        fun decode(reader: MessagePackReader): EveDashboardNodeSnapshot {
            val count = reader.readArrayHeader()
            val node = EveDashboardNodeSnapshot(
                reader.readString(),
                reader.readString(),
                reader.readString(),
                reader.readBoolean(),
                reader.readDouble(),
                reader.readDouble(),
                reader.readDouble(),
                reader.readDouble(),
                reader.readDouble(),
                reader.readDouble(),
                reader.readDouble(),
                reader.readString(),
                reader.readNullableString(),
                reader.readNullableString(),
                reader.readNullableString(),
            )
            repeat((count - 15).coerceAtLeast(0)) { reader.skip() }
            return node
        }
    }
}

data class EveDashboardStateDocument(
    val providerId: String,
    val title: String,
    val version: Long,
    val updatedAt: String,
    val selectedNodeId: String,
    val lutPreset: String,
    val nodes: List<EveDashboardNodeSnapshot>,
    val surface: EveDashboardSurface? = null,
) {
    companion object Codec : CultDocumentCodec<EveDashboardStateDocument> {
        override val documentType = "mimir.eve_dashboard_state"
        override val schemaVersion = "mimir.eve_dashboard_state.v1"
        override fun encode(value: EveDashboardStateDocument): ByteArray =
            throw UnsupportedOperationException("dashboard state is provider-authored")

        override fun decode(payload: ByteArray): EveDashboardStateDocument {
            val reader = MessagePackReader(payload)
            val count = reader.readArrayHeader()
            if (count < 7) throw IOException("dashboard state document too short")
            val state = EveDashboardStateDocument(
                reader.readString(),
                reader.readString(),
                reader.readLong(),
                reader.readString(),
                reader.readString(),
                reader.readString(),
                List(reader.readArrayHeader()) { EveDashboardNodeSnapshot.decode(reader) },
                if (count > 7) EveDashboardSurface.decodeNullable(reader) else null,
            )
            repeat((count - 8).coerceAtLeast(0)) { reader.skip() }
            return state
        }
    }
}

data class EveDashboardSurface(
    val schema: String,
    val id: String,
    val title: String,
    val root: EveDashboardUiElement,
    val assets: List<EveDashboardSurfaceAsset>,
) {
    companion object {
        fun decodeNullable(reader: MessagePackReader): EveDashboardSurface? {
            val count = reader.readNullableArrayHeader() ?: return null
            if (count < 5) throw IOException("dashboard surface document too short")
            val surface = EveDashboardSurface(
                reader.readString(),
                reader.readString(),
                reader.readString(),
                EveDashboardUiElement.decode(reader),
                List(reader.readArrayHeader()) { EveDashboardSurfaceAsset.decode(reader) },
            )
            repeat((count - 5).coerceAtLeast(0)) { reader.skip() }
            return surface
        }
    }
}

data class EveDashboardSurfaceAsset(
    val id: String,
    val kind: String,
    val uri: String,
) {
    companion object {
        fun decode(reader: MessagePackReader): EveDashboardSurfaceAsset {
            val count = reader.readArrayHeader()
            val asset = EveDashboardSurfaceAsset(reader.readString(), reader.readString(), reader.readString())
            repeat((count - 3).coerceAtLeast(0)) { reader.skip() }
            return asset
        }
    }
}

data class EveDashboardUiElement(
    val id: String,
    val kind: String,
    val role: String?,
    val text: String?,
    val assetRef: String?,
    val assetUri: String?,
    val bindNodeId: String?,
    val commandId: String?,
    val layout: EveDashboardUiLayout?,
    val style: EveDashboardUiStyle?,
    val metric: EveDashboardUiMetric?,
    val children: List<EveDashboardUiElement>,
) {
    companion object {
        fun decode(reader: MessagePackReader): EveDashboardUiElement {
            val count = reader.readArrayHeader()
            if (count < 12) throw IOException("dashboard ui element document too short")
            val element = EveDashboardUiElement(
                reader.readString(),
                reader.readString(),
                reader.readNullableString(),
                reader.readNullableString(),
                reader.readNullableString(),
                reader.readNullableString(),
                reader.readNullableString(),
                reader.readNullableString(),
                EveDashboardUiLayout.decodeNullable(reader),
                EveDashboardUiStyle.decodeNullable(reader),
                EveDashboardUiMetric.decodeNullable(reader),
                List(reader.readArrayHeader()) { decode(reader) },
            )
            repeat((count - 12).coerceAtLeast(0)) { reader.skip() }
            return element
        }
    }
}

data class EveDashboardUiLayout(
    val direction: String,
    val width: Double?,
    val height: Double?,
    val grow: Double?,
    val gap: Double?,
    val padding: Double?,
    val overflow: String?,
) {
    companion object {
        fun decodeNullable(reader: MessagePackReader): EveDashboardUiLayout? {
            val count = reader.readNullableArrayHeader() ?: return null
            if (count < 7) throw IOException("dashboard ui layout document too short")
            val layout = EveDashboardUiLayout(
                reader.readString(),
                reader.readNullableDouble(),
                reader.readNullableDouble(),
                reader.readNullableDouble(),
                reader.readNullableDouble(),
                reader.readNullableDouble(),
                reader.readNullableString(),
            )
            repeat((count - 7).coerceAtLeast(0)) { reader.skip() }
            return layout
        }
    }
}

data class EveDashboardUiStyle(
    val variant: String,
    val tone: String?,
) {
    companion object {
        fun decodeNullable(reader: MessagePackReader): EveDashboardUiStyle? {
            val count = reader.readNullableArrayHeader() ?: return null
            if (count < 2) throw IOException("dashboard ui style document too short")
            val style = EveDashboardUiStyle(reader.readString(), reader.readNullableString())
            repeat((count - 2).coerceAtLeast(0)) { reader.skip() }
            return style
        }
    }
}

data class EveDashboardUiMetric(
    val label: String,
    val value: Double,
    val tone: String,
) {
    companion object {
        fun decodeNullable(reader: MessagePackReader): EveDashboardUiMetric? {
            val count = reader.readNullableArrayHeader() ?: return null
            if (count < 3) throw IOException("dashboard ui metric document too short")
            val metric = EveDashboardUiMetric(reader.readString(), reader.readDouble(), reader.readString())
            repeat((count - 3).coerceAtLeast(0)) { reader.skip() }
            return metric
        }
    }
}

data class EveDashboardCommandDocument(
    val commandId: String,
    val deviceId: String,
    val clientId: String,
    val providerId: String,
    val type: String,
    val nodeId: String,
    val x: Double? = null,
    val y: Double? = null,
    val rotation: Double? = null,
    val scale: Double? = null,
    val visible: Boolean? = null,
    val sequence: Long,
    val deviceTimestampNs: Long,
) {
    companion object Codec : CultDocumentCodec<EveDashboardCommandDocument> {
        override val documentType = "mimir.eve_dashboard_command"
        override val schemaVersion = "mimir.eve_dashboard_command.v1"
        override fun encode(value: EveDashboardCommandDocument): ByteArray =
            MessagePackWriter()
                .array(13)
                .string(value.commandId)
                .string(value.deviceId)
                .string(value.clientId)
                .string(value.providerId)
                .string(value.type)
                .string(value.nodeId)
                .nullableDouble(value.x)
                .nullableDouble(value.y)
                .nullableDouble(value.rotation)
                .nullableDouble(value.scale)
                .nullableBoolean(value.visible)
                .longValue(value.sequence)
                .longValue(value.deviceTimestampNs)
                .toByteArray()

        override fun decode(payload: ByteArray): EveDashboardCommandDocument =
            throw UnsupportedOperationException("command decode is server-owned")
    }
}

data class EveSensorObservationDocument(
    val observationId: String,
    val deviceId: String,
    val streamId: String,
    val kind: String,
    val sequence: Long,
    val sensorTimestampNs: Long,
    val elapsedRealtimeNs: Long,
    val wallClockUtc: String,
    val clockDomainId: String,
    val values: DoubleArray,
    val action: String? = null,
    val pointerCount: Int? = null,
    val x: Double? = null,
    val y: Double? = null,
    val accuracy: Int? = null,
) {
    companion object Codec : CultDocumentCodec<EveSensorObservationDocument> {
        override val documentType = "mimir.eve_sensor_observation"
        override val schemaVersion = "mimir.eve_sensor_observation.v1"
        override fun encode(value: EveSensorObservationDocument): ByteArray {
            val writer = MessagePackWriter()
                .array(15)
                .string(value.observationId)
                .string(value.deviceId)
                .string(value.streamId)
                .string(value.kind)
                .longValue(value.sequence)
                .longValue(value.sensorTimestampNs)
                .longValue(value.elapsedRealtimeNs)
                .string(value.wallClockUtc)
                .string(value.clockDomainId)
                .array(value.values.size)
            value.values.forEach { writer.doubleValue(it) }
            return writer
                .nullableString(value.action)
                .nullableLong(value.pointerCount?.toLong())
                .nullableDouble(value.x)
                .nullableDouble(value.y)
                .nullableLong(value.accuracy?.toLong())
                .toByteArray()
        }

        override fun decode(payload: ByteArray): EveSensorObservationDocument =
            throw UnsupportedOperationException("sensor decode is consumer-owned")
    }
}

data class EveMediaObservationDocument(
    val observationId: String,
    val deviceId: String,
    val streamId: String,
    val kind: String,
    val sequence: Long,
    val sensorTimestampNs: Long,
    val elapsedRealtimeNs: Long,
    val wallClockUtc: String,
    val clockDomainId: String,
    val format: String,
    val width: Int? = null,
    val height: Int? = null,
    val sampleRate: Int? = null,
    val channels: Int? = null,
    val frameCount: Int? = null,
    val payloadEncoding: String,
    val payload: ByteArray,
) {
    companion object Codec : CultDocumentCodec<EveMediaObservationDocument> {
        override val documentType = "mimir.eve_media_observation"
        override val schemaVersion = "mimir.eve_media_observation.v1"
        override fun encode(value: EveMediaObservationDocument): ByteArray =
            MessagePackWriter()
                .array(17)
                .string(value.observationId)
                .string(value.deviceId)
                .string(value.streamId)
                .string(value.kind)
                .longValue(value.sequence)
                .longValue(value.sensorTimestampNs)
                .longValue(value.elapsedRealtimeNs)
                .string(value.wallClockUtc)
                .string(value.clockDomainId)
                .string(value.format)
                .nullableLong(value.width?.toLong())
                .nullableLong(value.height?.toLong())
                .nullableLong(value.sampleRate?.toLong())
                .nullableLong(value.channels?.toLong())
                .nullableLong(value.frameCount?.toLong())
                .string(value.payloadEncoding)
                .binary(value.payload)
                .toByteArray()

        override fun decode(payload: ByteArray): EveMediaObservationDocument =
            throw UnsupportedOperationException("media decode is consumer-owned")
    }
}
