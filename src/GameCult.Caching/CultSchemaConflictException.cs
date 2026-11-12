using System;

namespace GameCult.Caching
{
    /// <summary>
    /// A write the catalog cannot describe: two schemas that share a schema id and disagree on the schema name (both arrived with the
    /// records, neither registered), a record whose schema id no chosen entry publishes, or a write that would replace or remove a
    /// <see cref="CultForeignRecord"/>, or a write or removal of a record held read-only because its payload carries slots past the
    /// reader's declared slots (<see cref="UnreadSlots"/> is set). Nothing is written, so the store stays as it was. It names the id, the schema names involved
    /// and a record key.
    /// </summary>
    public sealed class CultSchemaConflictException : InvalidOperationException
    {
        public CultSchemaConflictException(string message, string schemaId, string[] schemaNames, string recordKey)
            : base(message)
        {
            SchemaId = schemaId;
            SchemaNames = schemaNames;
            RecordKey = recordKey;
        }

        /// <summary>The same refusal when the reload it owes (what the store holds now, loaded into the cache) itself failed: that failure is the inner exception.</summary>
        public CultSchemaConflictException(string message, string schemaId, string[] schemaNames, string recordKey, Exception? innerException)
            : base(message, innerException)
        {
            SchemaId = schemaId;
            SchemaNames = schemaNames;
            RecordKey = recordKey;
        }

        /// <summary>The refusal of a record held read-only for the slots it carries past this build's: the same refusal, with the finding.</summary>
        public CultSchemaConflictException(string message, string schemaId, string[] schemaNames, string recordKey, CultUnreadSlots unreadSlots)
            : this(message, schemaId, schemaNames, recordKey)
        {
            UnreadSlots = unreadSlots;
        }

        public string SchemaId { get; }
        public string[] SchemaNames { get; }
        public string RecordKey { get; }

        /// <summary>Set when the refusal is for unread slots; null for every other refusal.</summary>
        public CultUnreadSlots? UnreadSlots { get; }
    }
}
