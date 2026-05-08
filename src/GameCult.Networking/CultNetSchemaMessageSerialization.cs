using System;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Serializes and deserializes the explicit modern CultNet schema-v0 message family.
    /// </summary>
    public static class CultNetSchemaMessageSerialization
    {
        /// <summary>
        /// Gets the serializer options used for CultNet schema-v0 messages.
        /// </summary>
        public static readonly MessagePackSerializerOptions Options =
            MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

        /// <summary>
        /// Serializes a typed CultNet schema message.
        /// </summary>
        public static byte[] Serialize<T>(T message) where T : ICultNetSchemaMessage
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            return MessagePackSerializer.Serialize(message, Options);
        }

        /// <summary>
        /// Deserializes a typed CultNet schema message.
        /// </summary>
        public static T Deserialize<T>(byte[] payload) where T : ICultNetSchemaMessage
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            return MessagePackSerializer.Deserialize<T>(payload, Options);
        }

        /// <summary>
        /// Deserializes a CultNet schema message by inspecting its schema version.
        /// </summary>
        public static ICultNetSchemaMessage Deserialize(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            var header = MessagePackSerializer.Deserialize<CultNetSchemaHeader>(payload, Options);
            if (header == null || string.IsNullOrWhiteSpace(header.SchemaVersion))
            {
                throw new MessagePackSerializationException("CultNet schema-v0 payload is missing schemaVersion.");
            }

            return header.SchemaVersion switch
            {
                CultNetSchemaVersions.Hello => Deserialize<CultNetHelloMessage>(payload),
                CultNetSchemaVersions.Login => Deserialize<CultNetLoginMessage>(payload),
                CultNetSchemaVersions.Register => Deserialize<CultNetRegisterMessage>(payload),
                CultNetSchemaVersions.Verify => Deserialize<CultNetVerifyMessage>(payload),
                CultNetSchemaVersions.LoginSuccess => Deserialize<CultNetLoginSuccessMessage>(payload),
                CultNetSchemaVersions.Error => Deserialize<CultNetErrorMessage>(payload),
                CultNetSchemaVersions.SampleChangeName => Deserialize<CultNetSampleChangeNameMessage>(payload),
                CultNetSchemaVersions.SampleChat => Deserialize<CultNetSampleChatMessage>(payload),
                CultNetSchemaVersions.DocumentDelete => Deserialize<CultNetDocumentDeleteMessage>(payload),
                CultNetSchemaVersions.DocumentPutRaw => Deserialize<CultNetDocumentPutRawMessage>(payload),
                CultNetSchemaVersions.SnapshotRequest => Deserialize<CultNetSnapshotRequestMessage>(payload),
                CultNetSchemaVersions.SnapshotResponseRaw => Deserialize<CultNetSnapshotResponseRawMessage>(payload),
                CultNetSchemaVersions.SchemaCatalogRequest => Deserialize<CultNetSchemaCatalogRequestMessage>(payload),
                CultNetSchemaVersions.SchemaCatalogResponse => Deserialize<CultNetSchemaCatalogResponseMessage>(payload),
                _ => throw new MessagePackSerializationException(
                    $"Unsupported CultNet schema-v0 message \"{header.SchemaVersion}\".")
            };
        }

        [MessagePackObject(AllowPrivate = true)]
        internal sealed class CultNetSchemaHeader
        {
            [Key("schemaVersion")] public string SchemaVersion { get; set; } = string.Empty;
        }
    }
}
