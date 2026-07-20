using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace GameCult.Caching.MessagePack;

/// <summary>
/// MessagePack backing store that keeps a small schema manifest hot and stores each record as its own cold file.
/// </summary>
public sealed class DirectoryMessagePackBackingStore : CacheBackingStore
{
    private const string IndexedFormatVersion = "cultcache.store.v2.directory-indexed";
    private readonly FileInfo _manifestFile;
    private readonly DirectoryInfo _recordDirectory;
    private readonly ConcurrentDictionary<string, bool> _dirtyKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _deletedKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hydratedKeys = new(StringComparer.Ordinal);
    private Dictionary<string, CultPersistedRecord> _durableIndex = new(StringComparer.Ordinal);
    private Dictionary<string, CultPersistedRecord> _legacyInlineRecords = new(StringComparer.Ordinal);
    private CultSchemaCatalogEntry[] _durableCatalog = Array.Empty<CultSchemaCatalogEntry>();
    private bool _needsIndexUpgrade;

    /// <summary>
    /// Gets or sets the predicate that selects indexed record payloads for hydration.
    /// A null predicate hydrates every record.
    /// </summary>
    public Func<CultPersistedRecordMetadata, bool>? HydrationFilter { get; set; }

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
        _recordDirectory.Refresh();
        if (!File.Exists(_manifestFile.FullName) && !_recordDirectory.Exists)
        {
            SetLastSchemaMigrationReports(Array.Empty<CultSchemaMigrationReport>());
            IsDirty = false;
            return;
        }

        var manifest = ReadManifest();
        _durableCatalog = manifest.SchemaCatalog;
        var reports = new List<CultSchemaMigrationReport>();
        var loaded = new Dictionary<string, CultStoredDocument>(StringComparer.Ordinal);
        var indexed = string.Equals(manifest.FormatVersion, IndexedFormatVersion, StringComparison.Ordinal);
        if (indexed)
        {
            _needsIndexUpgrade = false;
            _durableIndex = manifest.Records
                .Where(record => !string.IsNullOrWhiteSpace(record.Key))
                .GroupBy(record => record.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

            LoadIndexedRecords(manifest, loaded, reports);
        }
        else
        {
            // V1 manifests have no page index. Hydrate once to discover the durable keys;
            // the next flush replaces this compatibility path with the indexed manifest.
            _needsIndexUpgrade = true;
            var legacyRecords = LoadLegacyRecords(manifest, loaded, reports);
            _durableIndex = legacyRecords
                .Where(record => !string.IsNullOrWhiteSpace(record.Key))
                .GroupBy(record => record.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => ToIndexRecord(group.Last()), StringComparer.Ordinal);
        }

        foreach (var pair in loaded)
        {
            if (_dirtyKeys.ContainsKey(pair.Key) || _deletedKeys.ContainsKey(pair.Key))
                continue;

            if (Entries.TryGetValue(pair.Key, out var existing) &&
                string.Equals(existing.StoredAt, pair.Value.StoredAt, StringComparison.Ordinal) &&
                string.Equals(existing.Descriptor.SchemaId, pair.Value.Descriptor.SchemaId, StringComparison.Ordinal))
            {
                continue;
            }

            Entries[pair.Key] = pair.Value;
            if (existing == null)
                EntryAdded.OnNext(pair.Value);
            else
                EntryUpdated.OnNext(pair.Value);
        }

        foreach (var removedKey in _hydratedKeys.Where(key => !loaded.ContainsKey(key)).ToArray())
        {
            if (_dirtyKeys.ContainsKey(removedKey) || _deletedKeys.ContainsKey(removedKey))
                continue;

            if (Entries.TryRemove(removedKey, out var removed))
                EntryDeleted.OnNext(removed);
            _hydratedKeys.Remove(removedKey);
        }

        foreach (var key in loaded.Keys)
            _hydratedKeys.Add(key);

        SetLastSchemaMigrationReports(reports);
        IsDirty = !_dirtyKeys.IsEmpty || !_deletedKeys.IsEmpty;
    }

    /// <inheritdoc />
    public override void PullSelected(Func<CultPersistedRecordMetadata, bool> selector)
    {
        if (selector == null) throw new ArgumentNullException(nameof(selector));
        var manifest = ReadManifest();
        if (!string.Equals(manifest.FormatVersion, IndexedFormatVersion, StringComparison.Ordinal))
        {
            PullAll();
            return;
        }

        _durableCatalog = manifest.SchemaCatalog;
        _durableIndex = manifest.Records
            .Where(record => !string.IsNullOrWhiteSpace(record.Key))
            .GroupBy(record => record.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var selected = manifest.Records
            .Where(record => selector(ToMetadata(record)))
            .OrderBy(record => record.Key, StringComparer.Ordinal)
            .ToArray();
        var loaded = new Dictionary<string, CultStoredDocument>(StringComparer.Ordinal);
        var reports = new List<CultSchemaMigrationReport>();
        LoadRecordPages(selected, manifest.SchemaCatalog, loaded, reports);
        PublishSelected(loaded, selected.Select(record => record.Key).ToHashSet(StringComparer.Ordinal));
        SetLastSchemaMigrationReports(reports);
        IsDirty = !_dirtyKeys.IsEmpty || !_deletedKeys.IsEmpty;
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
        if (!IsDirty && !_needsIndexUpgrade)
        {
            return;
        }

        Directory.CreateDirectory(_manifestFile.DirectoryName!);
        Directory.CreateDirectory(_recordDirectory.FullName);

        var currentManifest = ReadManifest();
        var currentIndex = string.Equals(currentManifest.FormatVersion, IndexedFormatVersion, StringComparison.Ordinal)
            ? currentManifest.Records.ToDictionary(record => record.Key, record => record, StringComparer.Ordinal)
            : new Dictionary<string, CultPersistedRecord>(_durableIndex, StringComparer.Ordinal);
        var precommitIndex = currentIndex.Values
            .Select(ToIndexRecord)
            .OrderBy(record => record.Key, StringComparer.Ordinal)
            .ToArray();
        foreach (var key in _deletedKeys.Keys)
            currentIndex.Remove(key);
        foreach (var key in _dirtyKeys.Keys)
        {
            if (Entries.TryGetValue(key, out var stored))
                currentIndex[key] = ToIndexRecord(stored);
        }

        var catalogCandidates = currentManifest.SchemaCatalog
            .Concat(_durableCatalog)
            .Concat(Entries.Values.Select(entry => entry.Descriptor.ToCatalogEntry()))
            .GroupBy(entry => entry.SchemaId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        var usedSchemaIds = currentIndex.Values.Select(record => record.SchemaId).ToHashSet(StringComparer.Ordinal);
        var targetCatalog = catalogCandidates
            .Where(entry => usedSchemaIds.Contains(entry.SchemaId))
            .OrderBy(entry => entry.SchemaName, StringComparer.Ordinal)
            .ToArray();
        var precommitCatalog = currentManifest.SchemaCatalog
            .Concat(targetCatalog)
            .GroupBy(entry => entry.SchemaId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(entry => entry.SchemaName, StringComparer.Ordinal)
            .ToArray();

        WriteManifest(precommitCatalog, precommitIndex);

        var keysToWrite = (_needsIndexUpgrade ? Entries.Keys : _dirtyKeys.Keys)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        foreach (var key in keysToWrite)
        {
            if (!Entries.TryGetValue(key, out var stored))
            {
                continue;
            }

            var record = ToPersistedRecord(stored, document =>
                CultDocumentMessagePackSerialization.SerializeUntyped(
                    document,
                    stored.Descriptor.DocumentType,
                    Registry));
            WriteFileAtomically(RecordPath(key), CultDocumentMessagePackSerialization.SerializePersistedRecord(record));
        }

        foreach (var pair in _legacyInlineRecords.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (_deletedKeys.ContainsKey(pair.Key) || _dirtyKeys.ContainsKey(pair.Key))
                continue;
            WriteFileAtomically(
                RecordPath(pair.Key),
                CultDocumentMessagePackSerialization.SerializePersistedRecord(pair.Value));
        }

        foreach (var key in _deletedKeys.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            var path = RecordPath(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        WriteManifest(targetCatalog, currentIndex.Values
            .OrderBy(record => record.Key, StringComparer.Ordinal)
            .ToArray());

        _durableCatalog = targetCatalog;
        _durableIndex = currentIndex;
        _legacyInlineRecords.Clear();
        _needsIndexUpgrade = false;
        _dirtyKeys.Clear();
        _deletedKeys.Clear();
        MarkFlushSucceeded();
    }

    private void WriteManifest(CultSchemaCatalogEntry[] catalog, CultPersistedRecord[] index)
    {
        var manifest = new CultPersistedStoreSnapshot
        {
            FormatVersion = IndexedFormatVersion,
            SchemaCatalog = catalog,
            Records = index
        };
        WriteFileAtomically(_manifestFile.FullName, CultDocumentMessagePackSerialization.SerializeSnapshot(manifest));
    }

    private void LoadIndexedRecords(
        CultPersistedStoreSnapshot manifest,
        Dictionary<string, CultStoredDocument> loaded,
        List<CultSchemaMigrationReport> reports)
    {
        var selected = manifest.Records
            .Where(ShouldHydrate)
            .OrderBy(record => record.Key, StringComparer.Ordinal)
            .ToArray();
        LoadRecordPages(selected, manifest.SchemaCatalog, loaded, reports);
    }

    private CultPersistedRecord[] LoadLegacyRecords(
        CultPersistedStoreSnapshot manifest,
        Dictionary<string, CultStoredDocument> loaded,
        List<CultSchemaMigrationReport> reports)
    {
        _legacyInlineRecords = manifest.Records
            .Where(record => !string.IsNullOrWhiteSpace(record.Key))
            .GroupBy(record => record.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        foreach (var record in manifest.Records.Where(ShouldHydrate))
        {
            var catalog = ResolveLegacyUncataloguedRecordCatalog(record, manifest.SchemaCatalog);
            reports.Add(Registry.ResolvePersistedSchemaReport(record.SchemaId, catalog));
            var stored = ToStoredDocument(
                record,
                catalog,
                (type, payload) => CultDocumentMessagePackSerialization.DeserializeUntyped(type, payload, Registry));
            loaded[stored.Key.Value] = stored;
        }

        if (!_recordDirectory.Exists)
            return manifest.Records;

        var pages = _recordDirectory.EnumerateFiles("*.msgpack")
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => CultDocumentMessagePackSerialization.DeserializePersistedRecord(ReadAllBytesShared(file.FullName)))
            .ToArray();
        LoadPersistedRecords(pages.Where(ShouldHydrate).ToArray(), manifest.SchemaCatalog, loaded, reports);
        return manifest.Records.Concat(pages).ToArray();
    }

    private void LoadRecordPages(
        CultPersistedRecord[] records,
        IReadOnlyCollection<CultSchemaCatalogEntry> catalogEntries,
        Dictionary<string, CultStoredDocument> loaded,
        List<CultSchemaMigrationReport> reports)
    {
        var recordReports = new CultSchemaMigrationReport[records.Length];
        var storedRecords = new CultStoredDocument?[records.Length];
        Parallel.For(
            0,
            records.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) },
            index =>
            {
                var metadata = records[index];
                var path = RecordPath(metadata.Key);
                if (!File.Exists(path))
                    return;
                var record = CultDocumentMessagePackSerialization.DeserializePersistedRecord(ReadAllBytesShared(path));
                if (!string.Equals(record.Key, metadata.Key, StringComparison.Ordinal))
                    throw new InvalidDataException($"Record page '{path}' contains key '{record.Key}', expected '{metadata.Key}'.");
                var catalog = ResolveLegacyUncataloguedRecordCatalog(record, catalogEntries);
                recordReports[index] = Registry.ResolvePersistedSchemaReport(record.SchemaId, catalog);
                storedRecords[index] = ToStoredDocument(
                    record,
                    catalog,
                    (type, payload) => CultDocumentMessagePackSerialization.DeserializeUntyped(type, payload, Registry));
            });

        for (var index = 0; index < storedRecords.Length; index++)
        {
            var stored = storedRecords[index];
            if (stored == null)
                continue;
            reports.Add(recordReports[index]);
            loaded[stored.Key.Value] = stored;
        }
    }

    private void LoadPersistedRecords(
        CultPersistedRecord[] records,
        IReadOnlyCollection<CultSchemaCatalogEntry> catalogEntries,
        Dictionary<string, CultStoredDocument> loaded,
        List<CultSchemaMigrationReport> reports)
    {
        var recordReports = new CultSchemaMigrationReport[records.Length];
        var storedRecords = new CultStoredDocument[records.Length];
        Parallel.For(
            0,
            records.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) },
            index =>
            {
                var record = records[index];
                var catalog = ResolveLegacyUncataloguedRecordCatalog(record, catalogEntries);
                recordReports[index] = Registry.ResolvePersistedSchemaReport(record.SchemaId, catalog);
                storedRecords[index] = ToStoredDocument(
                    record,
                    catalog,
                    (type, payload) => CultDocumentMessagePackSerialization.DeserializeUntyped(type, payload, Registry));
            });

        for (var index = 0; index < storedRecords.Length; index++)
        {
            reports.Add(recordReports[index]);
            var stored = storedRecords[index];
            loaded[stored.Key.Value] = stored;
        }
    }

    private bool ShouldHydrate(CultPersistedRecord record) =>
        HydrationFilter?.Invoke(ToMetadata(record)) ?? true;

    private static CultPersistedRecordMetadata ToMetadata(CultPersistedRecord record) =>
        new(record.Key, record.SchemaId, record.StoredAt);

    private void PublishSelected(
        IReadOnlyDictionary<string, CultStoredDocument> loaded,
        HashSet<string> selectedKeys)
    {
        foreach (var pair in loaded)
        {
            if (_dirtyKeys.ContainsKey(pair.Key) || _deletedKeys.ContainsKey(pair.Key))
                continue;

            if (Entries.TryGetValue(pair.Key, out var existing) &&
                string.Equals(existing.StoredAt, pair.Value.StoredAt, StringComparison.Ordinal) &&
                string.Equals(existing.Descriptor.SchemaId, pair.Value.Descriptor.SchemaId, StringComparison.Ordinal))
                continue;

            Entries[pair.Key] = pair.Value;
            if (existing == null)
                EntryAdded.OnNext(pair.Value);
            else
                EntryUpdated.OnNext(pair.Value);
        }

        foreach (var missingKey in selectedKeys.Where(key => !loaded.ContainsKey(key)).ToArray())
        {
            if (_dirtyKeys.ContainsKey(missingKey) || _deletedKeys.ContainsKey(missingKey))
                continue;
            if (Entries.TryRemove(missingKey, out var removed))
                EntryDeleted.OnNext(removed);
            _hydratedKeys.Remove(missingKey);
        }

        foreach (var key in loaded.Keys)
            _hydratedKeys.Add(key);
    }

    private static CultPersistedRecord ToIndexRecord(CultStoredDocument stored) => new()
    {
        Key = stored.Key.Value,
        SchemaId = stored.Descriptor.SchemaId,
        StoredAt = stored.StoredAt,
        Payload = Array.Empty<byte>()
    };

    private static CultPersistedRecord ToIndexRecord(CultPersistedRecord record) => new()
    {
        Key = record.Key,
        SchemaId = record.SchemaId,
        StoredAt = record.StoredAt,
        Payload = Array.Empty<byte>()
    };

    private CultPersistedStoreSnapshot ReadManifest()
    {
        if (File.Exists(_manifestFile.FullName))
        {
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(ReadAllBytesShared(_manifestFile.FullName));
            if (snapshot.SchemaCatalog.Length > 0 || snapshot.Records.Length == 0)
            {
                return snapshot;
            }
        }

        if (File.Exists(_manifestFile.FullName))
        {
            return CultDocumentMessagePackSerialization.DeserializeSnapshot(ReadAllBytesShared(_manifestFile.FullName));
        }

        return new CultPersistedStoreSnapshot
        {
            FormatVersion = "cultcache.store.v1.directory",
            SchemaCatalog = Array.Empty<CultSchemaCatalogEntry>(),
            Records = Array.Empty<CultPersistedRecord>()
        };
    }

    // Explicit compatibility path for records written before catalog precommit existed.
    private IReadOnlyCollection<CultSchemaCatalogEntry> ResolveLegacyUncataloguedRecordCatalog(
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
                ReplaceExistingFile(tempPath, path);
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

    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void ReplaceExistingFile(string sourcePath, string destinationPath)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Replace(sourcePath, destinationPath, null);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }
}
