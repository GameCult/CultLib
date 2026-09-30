using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;

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

            var snapshot = ReadSingleFileSnapshot(path);
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

            var snapshot = ReadSingleFileSnapshot(path);
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
            // The file being replaced is read first, by the reader every read of it uses: one this runtime cannot read refuses the
            // write instead of being overwritten.
            var existing = File.Exists(path) ? ReadSingleFileSnapshot(path) : null;

            // A typed write sees the document it replaces the file with, so its ids decide. A raw payload is opaque: the writer
            // cannot see whether it holds ids, so a file already marked stays marked.
            var snapshot = new CultPersistedStoreSnapshot
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
            };

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            WriteFileAtomically(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
        }

        // Every refusal is a CultStoreUnreadableException. A store whose catalog entries are in the older layout is read by the
        // same store reader with the older entry reader, so it is judged by the same framing, slot count and records. Only a
        // catalog entry the current layout cannot decode (a MessagePack or shape failure) sends it there; a store refused for its
        // framing, its slot count or a record keeps that refusal.
        private static CultPersistedStoreSnapshot ReadSingleFileSnapshot(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
                return new CultPersistedStoreSnapshot();
            CultPersistedStoreSnapshot snapshot;
            try
            {
                snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(bytes);
            }
            catch (CultStoreUnreadableException ex) when (ex.InnerException is MessagePackSerializationException or InvalidOperationException)
            {
                snapshot = CultDocumentMessagePackSerialization.ReadStore(bytes, ReadLegacySchemaCatalogEntry);
            }

            CultDocumentMessagePackSerialization.RequireSingleFileFormat(snapshot);
            return snapshot;
        }

        private static CultSchemaCatalogEntry ReadLegacySchemaCatalogEntry(ref MessagePackReader reader)
        {
            var fieldCount = reader.ReadArrayHeader();
            var entry = new CultSchemaCatalogEntry();
            if (fieldCount > 0) entry.SchemaId = reader.ReadString() ?? string.Empty;
            if (fieldCount > 1) entry.SchemaName = reader.ReadString() ?? string.Empty;
            if (fieldCount > 2) entry.SchemaVersion = reader.ReadString() ?? string.Empty;
            if (fieldCount > 3) entry.CanonicalSchemaJson = reader.ReadString() ?? string.Empty;
            if (fieldCount > 4) reader.Skip();
            if (fieldCount > 5) entry.ContentHash = reader.ReadString() ?? string.Empty;
            if (fieldCount > 6)
            {
                var memberCount = reader.ReadArrayHeader();
                for (var index = 0; index < memberCount; index++)
                    reader.Skip();
            }

            for (var index = 7; index < fieldCount; index++)
                reader.Skip();

            entry.CompatibleSchemaIds = string.IsNullOrWhiteSpace(entry.SchemaId)
                ? Array.Empty<string>()
                : new[] { entry.SchemaId };
            entry.Members = Array.Empty<CultSchemaMemberCatalogEntry>();
            return entry;
        }

        private static bool PublishesSchema(CultSchemaCatalogEntry[] catalog, string schemaId)
        {
            return catalog.Any(entry =>
                string.Equals(entry.SchemaId, schemaId, StringComparison.Ordinal) ||
                entry.CompatibleSchemaIds.Any(candidate => string.Equals(candidate, schemaId, StringComparison.Ordinal)));
        }

        private static void WriteFileAtomically(string path, byte[] bytes)
        {
            var tempPath = path + ".tmp";
            File.WriteAllBytes(tempPath, bytes);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tempPath, path);
        }
    }
}
