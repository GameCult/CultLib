using System;

namespace GameCult.Caching
{
    /// <summary>
    /// A write the catalog cannot describe: two schemas that share a schema id and disagree on the schema name (both arrived with the
    /// records, neither registered), a record whose schema id no chosen entry publishes, or a write that would replace or remove a
    /// <see cref="CultForeignRecord"/>. Nothing is written, so the store stays as it was. It names the id, the schema names involved
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

        public string SchemaId { get; }
        public string[] SchemaNames { get; }
        public string RecordKey { get; }
    }
}
