using System;
using MessagePack;
using MessagePack.Formatters;

#nullable enable

namespace GameCult.Mesh
{
    /// <summary>
    /// Reads the current structured invocation record and the legacy operation-id string.
    /// </summary>
    public sealed class CultMeshOperationInvocationRecordFormatter :
        IMessagePackFormatter<CultMeshOperationInvocationRecord?>
    {
        private const int FieldCount = 5;

        /// <inheritdoc />
        public void Serialize(
            ref MessagePackWriter writer,
            CultMeshOperationInvocationRecord? value,
            MessagePackSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNil();
                return;
            }

            writer.WriteArrayHeader(FieldCount);
            writer.Write(value.OperationId);
            writer.Write(value.SchemaId);
            writer.Write(value.RouteKind);
            writer.Write(value.RouteDescription);
            writer.Write(value.IdempotencyKey);
        }

        /// <inheritdoc />
        public CultMeshOperationInvocationRecord? Deserialize(
            ref MessagePackReader reader,
            MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil())
                return new CultMeshOperationInvocationRecord();

            if (reader.NextMessagePackType == MessagePackType.String)
                return new CultMeshOperationInvocationRecord(reader.ReadString());

            if (reader.NextMessagePackType != MessagePackType.Array)
                throw new MessagePackSerializationException(
                    $"CultMesh operation invocation must be a string or array, not {reader.NextMessagePackType}.");

            options.Security.DepthStep(ref reader);
            try
            {
                var fields = reader.ReadArrayHeader();
                var operationId = ReadString(ref reader, fields, 0);
                var schemaId = ReadString(ref reader, fields, 1);
                var routeKind = ReadString(ref reader, fields, 2);
                var routeDescription = ReadString(ref reader, fields, 3);
                var idempotencyKey = ReadString(ref reader, fields, 4);
                for (var index = FieldCount; index < fields; index++)
                    reader.Skip();
                return new CultMeshOperationInvocationRecord(
                    operationId,
                    schemaId,
                    routeKind,
                    routeDescription,
                    idempotencyKey);
            }
            finally
            {
                reader.Depth--;
            }
        }

        private static string ReadString(ref MessagePackReader reader, int fields, int index) =>
            index < fields ? reader.ReadString() ?? "" : "";
    }
}
