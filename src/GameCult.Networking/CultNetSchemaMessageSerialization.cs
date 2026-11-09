using System;
using System.Buffers;
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
            if (typeof(T) == typeof(CultNetWriteAdmittedMessage)) CultNetAdmittedMessageId.Require(payload);
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
                CultNetSchemaVersions.ErrorV1 => Deserialize<CultNetErrorV1Message>(payload),
                CultNetSchemaVersions.WriteAdmitted => Deserialize<CultNetWriteAdmittedMessage>(payload),
                CultNetSchemaVersions.SampleChangeName => Deserialize<CultNetSampleChangeNameMessage>(payload),
                CultNetSchemaVersions.SampleChat => Deserialize<CultNetSampleChatMessage>(payload),
                CultNetSchemaVersions.OperationRequest => Deserialize<CultNetOperationRequestMessage>(payload),
                CultNetSchemaVersions.OperationResponse => Deserialize<CultNetOperationResponseMessage>(payload),
                CultNetSchemaVersions.DocumentDelete => Deserialize<CultNetDocumentDeleteMessage>(payload),
                CultNetSchemaVersions.DocumentPutRaw => Deserialize<CultNetDocumentPutRawMessage>(payload),
                CultNetSchemaVersions.SnapshotRequest => Deserialize<CultNetSnapshotRequestMessage>(payload),
                CultNetSchemaVersions.SnapshotResponseRaw => Deserialize<CultNetSnapshotResponseRawMessage>(payload),
                CultNetSchemaVersions.SnapshotRequestV1 => Deserialize<CultNetSnapshotRequestV1Message>(payload),
                CultNetSchemaVersions.DatabaseSubscribeV1 => Deserialize<CultNetDatabaseSubscribeV1Message>(payload),
                CultNetSchemaVersions.SnapshotResponseRawV1 => Deserialize<CultNetSnapshotResponseRawV1Message>(payload),
                CultNetSchemaVersions.DatabaseSubscribe => Deserialize<CultNetDatabaseSubscribeMessage>(payload),
                CultNetSchemaVersions.DatabaseUnsubscribe => Deserialize<CultNetDatabaseUnsubscribeMessage>(payload),
                CultNetSchemaVersions.DatabaseChangeRaw => Deserialize<CultNetDatabaseChangeRawMessage>(payload),
                CultNetSchemaVersions.ShardCatalogRequest => Deserialize<CultNetShardCatalogRequestMessage>(payload),
                CultNetSchemaVersions.ShardCatalogResponse => Deserialize<CultNetShardCatalogResponseMessage>(payload),
                CultNetSchemaVersions.ShardLogRequest => Deserialize<CultNetShardLogRequestMessage>(payload),
                CultNetSchemaVersions.ShardLogResponse => Deserialize<CultNetShardLogResponseMessage>(payload),
                CultNetSchemaVersions.SimulationObservation => Deserialize<CultNetSimulationObservationMessage>(payload),
                CultNetSchemaVersions.SimulationConsensusCandidate => Deserialize<CultNetSimulationConsensusCandidateMessage>(payload),
                CultNetSchemaVersions.VerseCatalogRequest => Deserialize<CultMeshVerseCatalogRequestMessage>(payload),
                CultNetSchemaVersions.VerseCatalogResponse => Deserialize<CultMeshVerseCatalogResponseMessage>(payload),
                CultNetSchemaVersions.SessionOpen => Deserialize<CultMeshSessionOpenMessage>(payload),
                CultNetSchemaVersions.SessionAccepted => Deserialize<CultMeshSessionAcceptedMessage>(payload),
                CultNetSchemaVersions.PeerExchangeRequest => Deserialize<CultMeshPeerExchangeRequestMessage>(payload),
                CultNetSchemaVersions.PeerExchangeResponse => Deserialize<CultMeshPeerExchangeResponseMessage>(payload),
                CultNetSchemaVersions.ContentChunkRequest => Deserialize<CultMeshContentChunkRequestMessage>(payload),
                CultNetSchemaVersions.ContentChunkResponse => Deserialize<CultMeshContentChunkResponseMessage>(payload),
                CultNetSchemaVersions.BodyReadRequest => Deserialize<CultMeshBodyReadRequestMessage>(payload),
                CultNetSchemaVersions.BodyReadResponse => Deserialize<CultMeshBodyReadResponseMessage>(payload),
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

    /// <summary>
    /// The write_admitted messageId rule (write-receipt.md): a letter (Lu Ll Lt Lm Lo) or number (Nd Nl No) somewhere, no
    /// Default_Ignorable code point anywhere, as of Unicode 16.0, wire bytes that are valid UTF-8, compared raw (never trimmed).
    /// The tables are embedded (CultNetUnicodeMessageIdTables.cs, generated by scripts/gen-unicode-messageid-tables.py), not
    /// .NET's, so the running runtime's Unicode version changes no result; the other four runtimes embed the same tables.
    /// </summary>
    internal static class CultNetAdmittedMessageId
    {
        private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

        /// <summary>Throws unless every top-level <c>messageId</c> key of the frame is a valid write_admitted id; other keys are left to the serializer.</summary>
        public static void Require(byte[] payload)
        {
            var reader = new MessagePackReader(payload);
            var entries = reader.ReadMapHeader();
            for (var index = 0; index < entries; index++)
            {
                var key = reader.NextMessagePackType == MessagePackType.String ? reader.ReadString() : null;
                if (key == "messageId" && reader.NextMessagePackType == MessagePackType.String)
                {
                    string id;
                    try { id = StrictUtf8.GetString(reader.ReadStringSequence()!.Value.ToArray()); }
                    catch (System.Text.DecoderFallbackException) { throw new MessagePackSerializationException("cultnet.write_admitted.v0 messageId is not valid UTF-8."); }
                    if (!IsValid(id)) throw new MessagePackSerializationException("cultnet.write_admitted.v0 messageId must contain a letter or number and no Default_Ignorable code point.");
                    continue; // a repeated messageId key is checked too: the serializer keeps the last, so every one must be valid
                }

                if (key == null) reader.Skip();
                reader.Skip();
            }
        }

        public static bool IsValid(string id)
        {
            var hasLetterOrNumber = false;
            for (var at = 0; at < id.Length; at += char.IsSurrogatePair(id, at) ? 2 : 1)
            {
                var point = char.IsSurrogatePair(id, at) ? char.ConvertToUtf32(id, at) : id[at];
                if (InRanges(CultNetUnicodeMessageIdTables.DefaultIgnorable, point)) return false;
                hasLetterOrNumber |= InRanges(CultNetUnicodeMessageIdTables.LetterOrNumber, point);
            }

            return hasLetterOrNumber;
        }

        private static bool InRanges(int[] ranges, int point)
        {
            int low = 0, high = ranges.Length / 2 - 1;
            while (low <= high)
            {
                var mid = (low + high) >> 1;
                if (point < ranges[2 * mid]) high = mid - 1;
                else if (point > ranges[2 * mid + 1]) low = mid + 1;
                else return true;
            }

            return false;
        }
    }
}
