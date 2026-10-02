using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using GameCult.Caching;
using GameCult.Caching.MessagePack;

namespace GameCult.Mesh
{
    /// <summary>
    /// Schema metadata used when publishing one raw document payload as a CultCache single-file snapshot.
    /// </summary>
    public sealed class CultMeshSingleFileDocumentSchema
    {
        /// <summary>
        /// Creates schema metadata for a single-file document publication.
        /// </summary>
        public CultMeshSingleFileDocumentSchema(string schemaId, string schemaName, string schemaVersion)
        {
            if (string.IsNullOrWhiteSpace(schemaId)) throw new ArgumentException("Value must be non-empty.", nameof(schemaId));
            if (string.IsNullOrWhiteSpace(schemaName)) throw new ArgumentException("Value must be non-empty.", nameof(schemaName));
            if (string.IsNullOrWhiteSpace(schemaVersion)) throw new ArgumentException("Value must be non-empty.", nameof(schemaVersion));

            SchemaId = schemaId;
            SchemaName = schemaName;
            SchemaVersion = schemaVersion;
        }

        /// <summary>
        /// Gets the content-derived schema identifier.
        /// </summary>
        public string SchemaId { get; }

        /// <summary>
        /// Gets the stable schema name.
        /// </summary>
        public string SchemaName { get; }

        /// <summary>
        /// Gets the schema version.
        /// </summary>
        public string SchemaVersion { get; }

        /// <summary>
        /// Gets or sets an optional canonical schema description.
        /// </summary>
        public string CanonicalSchemaJson { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets compatible schema identifiers accepted for this document.
        /// </summary>
        public string[] CompatibleSchemaIds { get; set; } = Array.Empty<string>();

        internal CultSchemaCatalogEntry ToCatalogEntry(byte[] payload)
        {
            return new CultSchemaCatalogEntry
            {
                SchemaId = SchemaId,
                SchemaName = SchemaName,
                SchemaVersion = SchemaVersion,
                ContentHash = StableHash(payload),
                CanonicalSchemaJson = CanonicalSchemaJson,
                CompatibleSchemaIds = CompatibleSchemaIds
                    .Append(SchemaId)
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                Members = Array.Empty<CultSchemaMemberCatalogEntry>()
            };
        }

        private static string StableHash(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return "empty";

            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        }
    }

    public static partial class CultMesh
    {
        /// <summary>
        /// Writes one typed document as a canonical single-file MessagePack CultCache snapshot.
        /// </summary>
        public static void WriteSingleFileDocument<TDocument>(
            string path,
            CultRecordKey key,
            TDocument document,
            string? storedAt = null,
            CultDocumentRegistry? registry = null)
            where TDocument : class
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            var resolvedRegistry = registry ?? CultDocumentRegistry.Shared;
            var descriptor = resolvedRegistry.GetRequired<TDocument>();
            // Element ids are decided as a cache write decides them, before anything is written: an unset id is minted, a
            // refused list refuses the write, and the file is marked when it holds an id. A failed write gives the ids back.
            var ids = CultElementIds.Plan(document, key.Value, deterministic: false);
            ids.Apply();
            try
            {
                WriteSingleFileDocumentPayload(
                    path,
                    key,
                    descriptor.ToCatalogEntry(),
                    storedAt,
                    CultDocumentMessagePackSerialization.SerializeUntyped(document, typeof(TDocument)),
                    CultElementIds.Holds(document),
                    contentKnown: true);
            }
            catch
            {
                ids.Undo();
                throw;
            }
        }

        /// <summary>
        /// Reads one typed document from a single-file MessagePack CultCache snapshot.
        /// </summary>
        public static TDocument ReadSingleFileDocument<TDocument>(
            string path,
            CultRecordKey key,
            CultDocumentRegistry? registry = null)
            where TDocument : class
        {
            var resolvedRegistry = registry ?? CultDocumentRegistry.Shared;
            var descriptor = resolvedRegistry.GetRequired<TDocument>();
            var payload = ReadSingleFileDocumentPayload(path, key, descriptor.SchemaId);
            var document = (TDocument)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(TDocument), payload);
            // A load refuses what a write refuses, and mints the ids a cache load of the same file would mint.
            CultElementIds.Plan(document, key.Value, deterministic: true).Apply();
            return document;
        }

        /// <summary>
        /// Writes one raw document payload as a canonical single-file MessagePack CultCache snapshot.
        /// </summary>
        public static void WriteSingleFileDocumentPayload(
            string path,
            CultRecordKey key,
            CultMeshSingleFileDocumentSchema schema,
            string? storedAt,
            byte[] payload)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            WriteSingleFileDocumentPayload(path, key, schema.ToCatalogEntry(payload), storedAt, payload, holdsIds: false, contentKnown: false);
        }

        /// <summary>
        /// Reads one raw document payload from a single-file MessagePack CultCache snapshot.
        /// </summary>
        public static byte[] ReadSingleFileDocumentPayload(
            string path,
            CultRecordKey key,
            string expectedSchemaId)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Value must be non-empty.", nameof(path));
            if (string.IsNullOrWhiteSpace(expectedSchemaId)) throw new ArgumentException("Value must be non-empty.", nameof(expectedSchemaId));

            var snapshot = StoreAt(path).ReadDurable() ?? throw new FileNotFoundException($"CultCache document '{path}' does not exist.", path);
            var record = snapshot.Records.SingleOrDefault(candidate => string.Equals(candidate.Key, key.Value, StringComparison.Ordinal))
                ?? throw new InvalidDataException($"CultCache document '{path}' does not contain record '{key.Value}'.");

            if (!string.Equals(record.SchemaId, expectedSchemaId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"CultCache document '{path}' record '{key.Value}' has schema '{record.SchemaId}', expected '{expectedSchemaId}'.");
            }

            RefuseVariant(path, record);

            if (!PublishesSchema(snapshot.SchemaCatalog, expectedSchemaId))
            {
                throw new InvalidDataException(
                    $"CultCache document '{path}' does not publish schema '{expectedSchemaId}' in its catalog.");
            }

            return record.Payload;
        }

        /// <summary>
        /// Reads the only raw document payload from a single-file MessagePack CultCache snapshot.
        /// </summary>
        public static byte[] ReadSingleFileDocumentPayload(
            string path,
            string expectedSchemaId)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Value must be non-empty.", nameof(path));
            if (string.IsNullOrWhiteSpace(expectedSchemaId)) throw new ArgumentException("Value must be non-empty.", nameof(expectedSchemaId));

            var snapshot = StoreAt(path).ReadDurable() ?? throw new FileNotFoundException($"CultCache document '{path}' does not exist.", path);
            if (snapshot.Records.Length != 1)
            {
                throw new InvalidDataException(
                    $"CultCache document '{path}' must contain exactly one record.");
            }

            var record = snapshot.Records[0];
            if (!string.Equals(record.SchemaId, expectedSchemaId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"CultCache document '{path}' has schema '{record.SchemaId}', expected '{expectedSchemaId}'.");
            }

            if (!PublishesSchema(snapshot.SchemaCatalog, expectedSchemaId))
            {
                throw new InvalidDataException(
                    $"CultCache document '{path}' does not publish schema '{expectedSchemaId}' in its catalog.");
            }

            return record.Payload;
        }

        // A variant record's payload is empty; its document exists only as the cache resolves it against its base.
        private static void RefuseVariant(string path, CultPersistedRecord record)
        {
            if (record.Variant != null)
                throw new NotSupportedException(
                    $"CultCache document '{path}' record '{record.Key}' is a variant of '{record.Variant.BaseKey}'; " +
                    "CultMesh does not resolve document variants, open the store with a CultCache.");
        }

        private static void WriteSingleFileDocumentPayload(
            string path,
            CultRecordKey key,
            CultSchemaCatalogEntry catalogEntry,
            string? storedAt,
            byte[] payload,
            bool holdsIds,
            bool contentKnown)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Value must be non-empty.", nameof(path));
            if (catalogEntry == null) throw new ArgumentNullException(nameof(catalogEntry));
            if (string.IsNullOrWhiteSpace(catalogEntry.SchemaId))
                throw new ArgumentException("Catalog entry must include a schema id.", nameof(catalogEntry));

            payload ??= Array.Empty<byte>();
            // A typed write sees the document it replaces the file with, so its ids decide. A raw payload is opaque: the writer
            // cannot see whether it holds ids, so a file already marked stays marked. The store reads the file it replaces under
            // its lock: one this runtime cannot read refuses the write instead of being overwritten.
            StoreAt(path).ReplaceDurable(existing => new CultPersistedStoreSnapshot
            {
                FormatVersion = CacheBackingStore.HeaderFor(holdsIds, existing?.FormatVersion, wholeStore: contentKnown, directoryStore: false),
                SchemaCatalog = new[] { catalogEntry },
                Records = new[]
                {
                    new CultPersistedRecord
                    {
                        Key = key.Value,
                        SchemaId = catalogEntry.SchemaId,
                        StoredAt = string.IsNullOrWhiteSpace(storedAt) ? DateTimeOffset.UtcNow.ToString("O") : storedAt!,
                        Payload = payload
                    }
                }
            });
        }

        private static SingleFileMessagePackBackingStore StoreAt(string path) => new SingleFileMessagePackBackingStore(path);

        private static bool PublishesSchema(CultSchemaCatalogEntry[] catalog, string schemaId)
        {
            return catalog.Any(entry =>
                string.Equals(entry.SchemaId, schemaId, StringComparison.Ordinal) ||
                entry.CompatibleSchemaIds.Any(candidate => string.Equals(candidate, schemaId, StringComparison.Ordinal)));
        }
    }
}
