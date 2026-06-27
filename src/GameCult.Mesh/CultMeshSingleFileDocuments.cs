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
            WriteSingleFileDocumentPayload(
                path,
                key,
                descriptor.ToCatalogEntry(),
                storedAt,
                CultDocumentMessagePackSerialization.SerializeUntyped(document, typeof(TDocument)));
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
            return (TDocument)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(TDocument), payload);
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
            WriteSingleFileDocumentPayload(path, key, schema.ToCatalogEntry(payload), storedAt, payload);
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

        private static void WriteSingleFileDocumentPayload(
            string path,
            CultRecordKey key,
            CultSchemaCatalogEntry catalogEntry,
            string? storedAt,
            byte[] payload)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Value must be non-empty.", nameof(path));
            if (catalogEntry == null) throw new ArgumentNullException(nameof(catalogEntry));
            if (string.IsNullOrWhiteSpace(catalogEntry.SchemaId))
                throw new ArgumentException("Catalog entry must include a schema id.", nameof(catalogEntry));

            payload ??= Array.Empty<byte>();
            var snapshot = new CultPersistedStoreSnapshot
            {
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

        private static CultPersistedStoreSnapshot ReadSingleFileSnapshot(string path)
        {
            var bytes = File.ReadAllBytes(path);
            try
            {
                return CultDocumentMessagePackSerialization.DeserializeSnapshot(bytes);
            }
            catch (Exception ex) when (ex is MessagePackSerializationException or InvalidOperationException)
            {
                return ReadLegacySingleFileSnapshot(bytes);
            }
        }

        private static CultPersistedStoreSnapshot ReadLegacySingleFileSnapshot(byte[] bytes)
        {
            var reader = new MessagePackReader(bytes);
            var fieldCount = reader.ReadArrayHeader();
            var snapshot = new CultPersistedStoreSnapshot();
            if (fieldCount > 0)
                snapshot.FormatVersion = reader.ReadString() ?? "cultcache.store.v1";

            if (fieldCount > 1)
            {
                var catalogCount = reader.ReadArrayHeader();
                snapshot.SchemaCatalog = new CultSchemaCatalogEntry[catalogCount];
                for (var index = 0; index < catalogCount; index++)
                    snapshot.SchemaCatalog[index] = ReadLegacySchemaCatalogEntry(ref reader);
            }

            if (fieldCount > 2)
            {
                var recordCount = reader.ReadArrayHeader();
                snapshot.Records = new CultPersistedRecord[recordCount];
                for (var index = 0; index < recordCount; index++)
                    snapshot.Records[index] = ReadLegacyPersistedRecord(ref reader);
            }

            for (var index = 3; index < fieldCount; index++)
                reader.Skip();

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

        private static CultPersistedRecord ReadLegacyPersistedRecord(ref MessagePackReader reader)
        {
            var fieldCount = reader.ReadArrayHeader();
            var record = new CultPersistedRecord();
            if (fieldCount > 0) record.Key = reader.ReadString() ?? string.Empty;
            if (fieldCount > 1) record.SchemaId = reader.ReadString() ?? string.Empty;
            if (fieldCount > 2) record.StoredAt = reader.ReadString() ?? string.Empty;
            if (fieldCount > 3)
            {
                var payload = reader.ReadBytes();
                record.Payload = payload.HasValue ? payload.Value.ToArray() : Array.Empty<byte>();
            }
            for (var index = 4; index < fieldCount; index++)
                reader.Skip();
            return record;
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
