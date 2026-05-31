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
            )
            repeat((count - 7).coerceAtLeast(0)) { reader.skip() }
            return state
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
