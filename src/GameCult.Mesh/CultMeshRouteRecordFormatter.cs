using MessagePack;
using MessagePack.Formatters;

#nullable enable

namespace GameCult.Mesh
{
    /// <summary>
    /// Reads current structured route records and legacy locality strings.
    /// </summary>
    public sealed class CultMeshRouteRecordFormatter : IMessagePackFormatter<CultMeshRouteRecord?>
    {
        private const int FieldCount = 2;

        public void Serialize(ref MessagePackWriter writer, CultMeshRouteRecord? value, MessagePackSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNil();
                return;
            }

            writer.WriteArrayHeader(FieldCount);
            writer.Write(value.Kind);
            writer.Write(value.Description);
        }

        public CultMeshRouteRecord? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil())
                return new CultMeshRouteRecord();
            if (reader.NextMessagePackType == MessagePackType.String)
                return new CultMeshRouteRecord(reader.ReadString(), "");
            if (reader.NextMessagePackType != MessagePackType.Array)
                throw new MessagePackSerializationException(
                    $"CultMesh route must be a string or array, not {reader.NextMessagePackType}.");

            options.Security.DepthStep(ref reader);
            try
            {
                var fields = reader.ReadArrayHeader();
                var kind = fields > 0 ? reader.ReadString() ?? "" : "";
                var description = fields > 1 ? reader.ReadString() ?? "" : "";
                for (var index = FieldCount; index < fields; index++)
                    reader.Skip();
                return new CultMeshRouteRecord(kind, description);
            }
            finally
            {
                reader.Depth--;
            }
        }
    }
}
