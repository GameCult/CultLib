using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MessagePack;

namespace GameCult.Caching.MessagePack;

/// <summary>
/// MessagePack backing store that keeps a small schema manifest hot and stores each record as its own cold file.
/// </summary>
public sealed class DirectoryMessagePackBackingStore : CacheBackingStore
{
    private readonly FileInfo _manifestFile;
    private readonly DirectoryInfo _recordDirectory;
    private readonly ConcurrentDictionary<string, bool> _dirtyKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _deletedKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a paged MessagePack backing store.
    /// </summary>
    public DirectoryMessagePackBackingStore(string manifestPath, string? recordDirectoryPath = null)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("Manifest path must be non-empty.", nameof(manifestPath));
        }

        _manifestFile = new FileInfo(manifestPath);
        _recordDirectory = new DirectoryInfo(recordDirectoryPath ?? DefaultRecordDirectoryPath(manifestPath));
    }

    /// <summary>
    /// Gets the default record directory path for a manifest path.
    /// </summary>
    public static string DefaultRecordDirectoryPath(string manifestPath) => manifestPath + ".records";

    /// <inheritdoc />
    public override void PullAll()
    {
        if (!_manifestFile.Exists && !_recordDirectory.Exists)
        {
            SetLastSchemaMigrationReports(Array.Empty<CultSchemaMigrationReport>());
            IsDirty = false;
            return;
        }

        var manifest = ReadManifest();
        var reports = new List<CultSchemaMigrationReport>();
        foreach (var record in manifest.Records)
        {
            var catalog = ResolveCatalogForRecord(record, manifest.SchemaCatalog);
            reports.Add(Registry.ResolvePersistedSchemaReport(record.SchemaId, catalog));
            var stored = ToStoredDocument(record, catalog, CultDocumentMessagePackSerialization.DeserializeUntyped);
            Entries[stored.Key.Value] = stored;
            _dirtyKeys[stored.Key.Value] = true;
            EntryAdded.OnNext(stored);
        }

        if (_recordDirectory.Exists)
        {
            foreach (var recordFile in _recordDirectory.EnumerateFiles("*.msgpack").OrderBy(file => file.Name, StringComparer.Ordinal))
            {
                var record = CultDocumentMessagePackSerialization.DeserializePersistedRecord(File.ReadAllBytes(recordFile.FullName));
                var catalog = ResolveCatalogForRecord(record, manifest.SchemaCatalog);
                reports.Add(Registry.ResolvePersistedSchemaReport(record.SchemaId, catalog));
                var stored = ToStoredDocument(record, catalog, CultDocumentMessagePackSerialization.DeserializeUntyped);
                Entries[stored.Key.Value] = stored;
                EntryAdded.OnNext(stored);
            }
        }

        SetLastSchemaMigrationReports(reports);
        if (manifest.Records.Length == 0)
        {
            _dirtyKeys.Clear();
        }

        _deletedKeys.Clear();
        IsDirty = _dirtyKeys.Count > 0;
    }

    /// <inheritdoc />
    public override void Push(CultStoredDocument entry)
    {
        Entries[entry.Key.Value] = entry;
        _dirtyKeys[entry.Key.Value] = true;
        _deletedKeys.TryRemove(entry.Key.Value, out _);
        IsDirty = true;
    }

    /// <inheritdoc />
    public override void Delete(CultStoredDocument entry)
    {
        Entries.TryRemove(entry.Key.Value, out _);
        _dirtyKeys.TryRemove(entry.Key.Value, out _);
        _deletedKeys[entry.Key.Value] = true;
        IsDirty = true;
    }

    /// <inheritdoc />
    public override void PushAll(bool soft = false)
    {
        Directory.CreateDirectory(_manifestFile.DirectoryName!);
        Directory.CreateDirectory(_recordDirectory.FullName);

        foreach (var key in _deletedKeys.Keys)
        {
            var path = RecordPath(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        foreach (var key in _dirtyKeys.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!Entries.TryGetValue(key, out var stored))
            {
                continue;
            }

            var record = ToPersistedRecord(stored, document =>
                CultDocumentMessagePackSerialization.SerializeUntyped(document, stored.Descriptor.DocumentType));
            WriteFileAtomically(RecordPath(key), CultDocumentMessagePackSerialization.SerializePersistedRecord(record));
        }

        var manifest = new CultPersistedStoreSnapshot
        {
            FormatVersion = "cultcache.store.v1.directory",
            SchemaCatalog = Entries.Values
                .Select(entry => entry.Descriptor.ToCatalogEntry())
                .GroupBy(entry => entry.SchemaId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(entry => entry.SchemaName, StringComparer.Ordinal)
                .ToArray(),
            Records = Array.Empty<CultPersistedRecord>()
        };
        WriteFileAtomically(_manifestFile.FullName, CultDocumentMessagePackSerialization.SerializeSnapshot(manifest));

        _dirtyKeys.Clear();
        _deletedKeys.Clear();
        MarkFlushSucceeded();
    }

    private CultPersistedStoreSnapshot ReadManifest()
    {
        if (_manifestFile.Exists)
        {
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(_manifestFile.FullName));
            if (snapshot.SchemaCatalog.Length > 0 || snapshot.Records.Length == 0)
            {
                return snapshot;
            }
        }

        if (_manifestFile.Exists)
        {
            return CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(_manifestFile.FullName));
        }

        return new CultPersistedStoreSnapshot
        {
            FormatVersion = "cultcache.store.v1.directory",
            SchemaCatalog = Array.Empty<CultSchemaCatalogEntry>(),
            Records = Array.Empty<CultPersistedRecord>()
        };
    }

    private IReadOnlyCollection<CultSchemaCatalogEntry> ResolveCatalogForRecord(
        CultPersistedRecord record,
        IReadOnlyCollection<CultSchemaCatalogEntry> manifestCatalog)
    {
        if (manifestCatalog.Any(entry => string.Equals(entry.SchemaId, record.SchemaId, StringComparison.Ordinal)))
        {
            return manifestCatalog;
        }

        var schemaVersion = TryReadPayloadSchemaVersion(record.Payload);
        if (string.IsNullOrWhiteSpace(schemaVersion))
        {
            return manifestCatalog;
        }

        var schemaName = InferSchemaName(schemaVersion);
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            return manifestCatalog;
        }

        return manifestCatalog
            .Append(new CultSchemaCatalogEntry
            {
                SchemaId = record.SchemaId,
                SchemaName = schemaName,
                SchemaVersion = schemaVersion,
                ContentHash = record.SchemaId,
                CanonicalSchemaJson = "",
                CompatibleSchemaIds = Array.Empty<string>(),
                Members = Array.Empty<CultSchemaMemberCatalogEntry>()
            })
            .ToArray();
    }

    private static string TryReadPayloadSchemaVersion(byte[] payload)
    {
        if (payload == null || payload.Length == 0)
        {
            return "";
        }

        try
        {
            var reader = new MessagePackReader(payload);
            if (reader.NextMessagePackType != MessagePackType.Array)
            {
                return "";
            }

            if (reader.ReadArrayHeader() <= 0)
            {
                return "";
            }

            return reader.NextMessagePackType == MessagePackType.String
                ? reader.ReadString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static string InferSchemaName(string schemaVersion)
    {
        var versionMarker = schemaVersion.LastIndexOf(".v", StringComparison.Ordinal);
        return versionMarker > 0 && versionMarker + 2 < schemaVersion.Length && char.IsDigit(schemaVersion[versionMarker + 2])
            ? schemaVersion.Substring(0, versionMarker)
            : "";
    }

    private string RecordPath(string key) => Path.Combine(_recordDirectory.FullName, $"{HashKey(key)}.msgpack");

    private static string HashKey(string key)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void WriteFileAtomically(string path, byte[] payload)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
