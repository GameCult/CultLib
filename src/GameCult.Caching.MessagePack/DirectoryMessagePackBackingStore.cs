using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <summary>Fault-probe stage emitted before each immutable record page write.</summary>
    public const string BeforeRecordPageWriteStage = "before-record-page-write";
    /// <summary>Fault-probe stage emitted after all pages exist and before manifest finality.</summary>
    public const string BeforeManifestCommitStage = "before-manifest-commit";
    /// <summary>Read-probe stage emitted while the selected manifest generation is leased.</summary>
    public const string AfterManifestReadStage = "after-manifest-read";
    private const string LegacyIndexedFormatVersion = "cultcache.store.v2.directory-indexed";
    private const string LegacyMetadataPageFormatVersion = "cultcache.store.v3.directory-immutable-pages";
    private const string IndexedFormatVersion = "cultcache.store.v4.directory-content-addressed-pages";
    private readonly FileInfo _manifestFile;
    private readonly DirectoryInfo _recordDirectory;
    private readonly object _mutationGate = new();
    private readonly ConcurrentDictionary<string, bool> _dirtyKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _deletedKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hydratedKeys = new(StringComparer.Ordinal);
    private Dictionary<string, CultPersistedRecord> _durableIndex = new(StringComparer.Ordinal);
    private Dictionary<string, CultPersistedRecord> _legacyInlineRecords = new(StringComparer.Ordinal);
    private CultSchemaCatalogEntry[] _durableCatalog = Array.Empty<CultSchemaCatalogEntry>();
    private bool _needsIndexUpgrade;
    private bool _manifestUsesImmutablePages;
    private bool _manifestUsesMetadataPages;

    /// <summary>
    /// Gets or sets the predicate that selects indexed record payloads for hydration.
    /// A null predicate hydrates every record.
    /// </summary>
    public Func<CultPersistedRecordMetadata, bool>? HydrationFilter { get; set; }

    /// <summary>Optional deterministic fault/progress hook for durability tests and host diagnostics.</summary>
    public Action<string, int>? FlushStageProbe { get; set; }

    /// <summary>Optional deterministic read hook for generation-consistency tests and host diagnostics.</summary>
    public Action<string>? ReadStageProbe { get; set; }

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

    /// <summary>
    /// Reads one complete persisted generation without deserializing documents through a
    /// runtime registry. Indexed manifests are resolved only through the pages they name;
    /// orphaned pages from older generations are never included.
    /// </summary>
    public CultPersistedStoreSnapshot ReadPersistedGeneration()
    {
        lock (_mutationGate)
        using (AcquireCommitLease())
        {
            var manifest = ReadManifest();
            var indexed = string.Equals(manifest.FormatVersion, IndexedFormatVersion, StringComparison.Ordinal) ||
                string.Equals(manifest.FormatVersion, LegacyMetadataPageFormatVersion, StringComparison.Ordinal) ||
                string.Equals(manifest.FormatVersion, LegacyIndexedFormatVersion, StringComparison.Ordinal);
            var records = indexed
                ? manifest.Records
                    .OrderBy(record => record.Key, StringComparer.Ordinal)
                    .Select(record => ReadPersistedRecordPage(record, manifest.FormatVersion, out _))
                    .ToArray()
                : manifest.Records
                    .OrderBy(record => record.Key, StringComparer.Ordinal)
                    .ToArray();
            return new CultPersistedStoreSnapshot
            {
                FormatVersion = manifest.FormatVersion,
                SchemaCatalog = manifest.SchemaCatalog.ToArray(),
                Records = records
            };
        }
    }

    /// <inheritdoc />
    public override void PullAll()
    {
        lock (_mutationGate)
            PullAllCore();
    }

    private void PullAllCore()
    {
        var traceStartup = string.Equals(
            Environment.GetEnvironmentVariable("CULTCACHE_TRACE_STARTUP_PHASES"),
            "1",
            StringComparison.Ordinal);
        var startupPhase = Stopwatch.StartNew();
        void Trace(string phase)
        {
            if (traceStartup)
                Console.WriteLine($"CultCache directory-pull phase {phase} took {startupPhase.Elapsed.TotalMilliseconds:0.###}ms.");
            startupPhase.Restart();
        }

        var reports = new List<CultSchemaMigrationReport>();
        var loaded = new Dictionary<string, CultStoredDocument>(StringComparer.Ordinal);
        using (AcquireCommitLease())
        {
            _recordDirectory.Refresh();
            if (!File.Exists(_manifestFile.FullName) && !_recordDirectory.Exists)
            {
                SetLastSchemaMigrationReports(Array.Empty<CultSchemaMigrationReport>());
                IsDirty = false;
                return;
            }

            var manifest = ReadManifest();
            ReadStageProbe?.Invoke(AfterManifestReadStage);
            Trace($"manifest records={manifest.Records.Length}");
            _durableCatalog = manifest.SchemaCatalog;
            var indexed = string.Equals(manifest.FormatVersion, IndexedFormatVersion, StringComparison.Ordinal) ||
                string.Equals(manifest.FormatVersion, LegacyMetadataPageFormatVersion, StringComparison.Ordinal) ||
                string.Equals(manifest.FormatVersion, LegacyIndexedFormatVersion, StringComparison.Ordinal);
            if (indexed)
            {
                _manifestUsesImmutablePages = string.Equals(manifest.FormatVersion, IndexedFormatVersion, StringComparison.Ordinal);
                _manifestUsesMetadataPages = string.Equals(manifest.FormatVersion, LegacyMetadataPageFormatVersion, StringComparison.Ordinal);
                _needsIndexUpgrade = !_manifestUsesImmutablePages;
                _durableIndex = manifest.Records
                    .Where(record => !string.IsNullOrWhiteSpace(record.Key))
                    .GroupBy(record => record.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

                LoadIndexedRecords(manifest, loaded, reports);
                Trace($"indexed-pages loaded={loaded.Count}");
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
                Trace($"legacy-pages loaded={loaded.Count}");
            }
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
        Trace("publish");
    }

    /// <inheritdoc />
    public override void PullSelected(Func<CultPersistedRecordMetadata, bool> selector)
    {
        lock (_mutationGate)
            PullSelectedCore(selector);
    }

    private void PullSelectedCore(Func<CultPersistedRecordMetadata, bool> selector)
    {
        if (selector == null) throw new ArgumentNullException(nameof(selector));
        var fallbackToFullPull = false;
        CultPersistedRecord[] selected = Array.Empty<CultPersistedRecord>();
        var loaded = new Dictionary<string, CultStoredDocument>(StringComparer.Ordinal);
        var reports = new List<CultSchemaMigrationReport>();
        using (AcquireCommitLease())
        {
            var manifest = ReadManifest();
            ReadStageProbe?.Invoke(AfterManifestReadStage);
            fallbackToFullPull = !string.Equals(manifest.FormatVersion, IndexedFormatVersion, StringComparison.Ordinal) &&
                !string.Equals(manifest.FormatVersion, LegacyMetadataPageFormatVersion, StringComparison.Ordinal) &&
                !string.Equals(manifest.FormatVersion, LegacyIndexedFormatVersion, StringComparison.Ordinal);
            if (!fallbackToFullPull)
            {
                _manifestUsesImmutablePages = string.Equals(manifest.FormatVersion, IndexedFormatVersion, StringComparison.Ordinal);
                _manifestUsesMetadataPages = string.Equals(manifest.FormatVersion, LegacyMetadataPageFormatVersion, StringComparison.Ordinal);
                _needsIndexUpgrade = !_manifestUsesImmutablePages;
                _durableCatalog = manifest.SchemaCatalog;
                _durableIndex = manifest.Records
                    .Where(record => !string.IsNullOrWhiteSpace(record.Key))
                    .GroupBy(record => record.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
                selected = manifest.Records
                    .Where(record => selector(ToMetadata(record)))
                    .OrderBy(record => record.Key, StringComparer.Ordinal)
                    .ToArray();
                LoadRecordPages(selected, manifest.SchemaCatalog, loaded, reports);
            }
        }
        if (fallbackToFullPull)
        {
            PullAllCore();
            return;
        }
        PublishSelected(loaded, selected.Select(record => record.Key).ToHashSet(StringComparer.Ordinal));
        SetLastSchemaMigrationReports(reports);
        IsDirty = !_dirtyKeys.IsEmpty || !_deletedKeys.IsEmpty;
    }

    /// <inheritdoc />
    public override bool ContainsDurableRecord(CultRecordKey key)
    {
        lock (_mutationGate)
            return _durableIndex.ContainsKey(key.Value) || base.ContainsDurableRecord(key);
    }

    /// <inheritdoc />
    public override void Push(CultStoredDocument entry)
    {
        lock (_mutationGate)
        {
            Entries[entry.Key.Value] = entry;
            _dirtyKeys[entry.Key.Value] = true;
            _deletedKeys.TryRemove(entry.Key.Value, out _);
            IsDirty = true;
        }
    }

    /// <inheritdoc />
    public override void Delete(CultStoredDocument entry)
    {
        lock (_mutationGate)
        {
            Entries.TryRemove(entry.Key.Value, out _);
            _dirtyKeys.TryRemove(entry.Key.Value, out _);
            _deletedKeys[entry.Key.Value] = true;
            IsDirty = true;
        }
    }

    /// <inheritdoc />
    public override void CommitBatch(
        IReadOnlyCollection<CultStoredDocument> upserts,
        IReadOnlyCollection<CultStoredDocument> deletes,
        bool soft)
    {
        lock (_mutationGate)
        {
            var previousEntries = Entries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var previousDirtyKeys = _dirtyKeys.Keys.ToArray();
            var previousDeletedKeys = _deletedKeys.Keys.ToArray();
            var wasDirty = IsDirty;
            try
            {
                foreach (var entry in deletes)
                {
                    Entries.TryRemove(entry.Key.Value, out _);
                    _dirtyKeys.TryRemove(entry.Key.Value, out _);
                    _deletedKeys[entry.Key.Value] = true;
                }
                foreach (var entry in upserts)
                {
                    Entries[entry.Key.Value] = entry;
                    _dirtyKeys[entry.Key.Value] = true;
                    _deletedKeys.TryRemove(entry.Key.Value, out _);
                }
                IsDirty = true;
                PushAllCore(soft);
            }
            catch
            {
                Entries.Clear();
                foreach (var pair in previousEntries)
                    Entries[pair.Key] = pair.Value;
                _dirtyKeys.Clear();
                foreach (var key in previousDirtyKeys)
                    _dirtyKeys[key] = true;
                _deletedKeys.Clear();
                foreach (var key in previousDeletedKeys)
                    _deletedKeys[key] = true;
                IsDirty = wasDirty;
                throw;
            }
        }
    }

    /// <inheritdoc />
    public override void PushAll(bool soft = false)
    {
        lock (_mutationGate)
            PushAllCore(soft);
    }

    private void PushAllCore(bool soft)
    {
        if (!IsDirty && !_needsIndexUpgrade)
        {
            return;
        }

        Directory.CreateDirectory(_manifestFile.DirectoryName!);
        Directory.CreateDirectory(_recordDirectory.FullName);
        using var commitLease = AcquireCommitLease();

        var currentManifest = ReadManifest();
        var currentManifestIsIndexed = string.Equals(currentManifest.FormatVersion, IndexedFormatVersion, StringComparison.Ordinal) ||
            string.Equals(currentManifest.FormatVersion, LegacyMetadataPageFormatVersion, StringComparison.Ordinal) ||
            string.Equals(currentManifest.FormatVersion, LegacyIndexedFormatVersion, StringComparison.Ordinal);
        var currentManifestUsesImmutablePages = string.Equals(currentManifest.FormatVersion, IndexedFormatVersion, StringComparison.Ordinal);
        var currentManifestUsesMetadataPages = string.Equals(currentManifest.FormatVersion, LegacyMetadataPageFormatVersion, StringComparison.Ordinal);
        var currentIndex = currentManifestIsIndexed
            ? currentManifest.Records.ToDictionary(record => record.Key, record => record, StringComparer.Ordinal)
            : new Dictionary<string, CultPersistedRecord>(_durableIndex, StringComparer.Ordinal);
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
        var keysToWrite = (_needsIndexUpgrade ? Entries.Keys : _dirtyKeys.Keys)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var pageWriteIndex = 0;
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
            var pagePayload = CultDocumentMessagePackSerialization.SerializePersistedRecord(record);
            var indexRecord = ToContentAddressedIndexRecord(record, pagePayload);
            currentIndex[key] = indexRecord;
            FlushStageProbe?.Invoke(BeforeRecordPageWriteStage, pageWriteIndex++);
            WriteFileAtomically(
                ContentAddressedRecordPath(indexRecord),
                pagePayload);
        }

        if (!currentManifestUsesImmutablePages)
        {
            foreach (var metadata in currentManifest.Records
                         .Where(record => !_deletedKeys.ContainsKey(record.Key) && !_dirtyKeys.ContainsKey(record.Key))
                         .OrderBy(record => record.Key, StringComparer.Ordinal))
            {
                var sourcePath = currentManifestUsesMetadataPages
                    ? MetadataRecordPath(metadata)
                    : LegacyRecordPath(metadata.Key);
                if (!File.Exists(sourcePath))
                    continue;
                var pagePayload = ReadAllBytesShared(sourcePath);
                var indexRecord = ToContentAddressedIndexRecord(metadata, pagePayload);
                currentIndex[metadata.Key] = indexRecord;
                var targetPath = ContentAddressedRecordPath(indexRecord);
                if (!File.Exists(targetPath))
                    WriteFileAtomically(targetPath, pagePayload);
            }
        }

        foreach (var pair in _legacyInlineRecords.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (_deletedKeys.ContainsKey(pair.Key) || _dirtyKeys.ContainsKey(pair.Key))
                continue;
            var pagePayload = CultDocumentMessagePackSerialization.SerializePersistedRecord(pair.Value);
            var indexRecord = ToContentAddressedIndexRecord(pair.Value, pagePayload);
            currentIndex[pair.Key] = indexRecord;
            WriteFileAtomically(
                ContentAddressedRecordPath(indexRecord),
                pagePayload);
        }

        FlushStageProbe?.Invoke(BeforeManifestCommitStage, pageWriteIndex);
        WriteManifest(targetCatalog, currentIndex.Values
            .OrderBy(record => record.Key, StringComparer.Ordinal)
            .ToArray());

        _manifestUsesImmutablePages = true;
        _manifestUsesMetadataPages = false;
        DeleteUnreferencedRecordPages(currentIndex.Values);

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
        var tracePages = string.Equals(
            Environment.GetEnvironmentVariable("CULTCACHE_TRACE_STARTUP_PHASES"),
            "1",
            StringComparison.Ordinal);
        var recordReports = new CultSchemaMigrationReport[records.Length];
        var storedRecords = new CultStoredDocument?[records.Length];
        var pageBytes = tracePages ? new long[records.Length] : Array.Empty<long>();
        var pageElapsedTicks = tracePages ? new long[records.Length] : Array.Empty<long>();
        Parallel.For(
            0,
            records.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) },
            index =>
            {
                var started = tracePages ? Stopwatch.GetTimestamp() : 0L;
                var metadata = records[index];
                var record = ReadPersistedRecordPage(
                    metadata,
                    _manifestUsesImmutablePages,
                    _manifestUsesMetadataPages,
                    out var pagePayload);
                var catalog = ResolveLegacyUncataloguedRecordCatalog(record, catalogEntries);
                recordReports[index] = Registry.ResolvePersistedSchemaReport(record.SchemaId, catalog);
                storedRecords[index] = ToStoredDocument(
                    record,
                    catalog,
                    (type, payload) => CultDocumentMessagePackSerialization.DeserializeUntyped(type, payload, Registry));
                if (tracePages)
                {
                    pageBytes[index] = pagePayload.LongLength;
                    pageElapsedTicks[index] = Stopwatch.GetTimestamp() - started;
                }
            });

        if (tracePages)
        {
            var schemaNames = catalogEntries.ToDictionary(entry => entry.SchemaId, entry => entry.SchemaName, StringComparer.Ordinal);
            foreach (var group in records
                         .Select((record, index) => new { record.SchemaId, Index = index })
                         .Where(item => pageElapsedTicks[item.Index] > 0)
                         .GroupBy(item => item.SchemaId, StringComparer.Ordinal)
                         .Select(group => new
                         {
                             SchemaId = group.Key,
                             Count = group.Count(),
                             Bytes = group.Sum(item => pageBytes[item.Index]),
                             TotalMs = group.Sum(item => pageElapsedTicks[item.Index]) * 1000d / Stopwatch.Frequency,
                             MaxMs = group.Max(item => pageElapsedTicks[item.Index]) * 1000d / Stopwatch.Frequency
                         })
                         .OrderByDescending(group => group.TotalMs)
                         .Take(10))
            {
                var schemaName = schemaNames.TryGetValue(group.SchemaId, out var name) ? name : group.SchemaId;
                Console.WriteLine(
                    $"CultCache directory-page schema={schemaName} count={group.Count} bytes={group.Bytes} " +
                    $"cumulative={group.TotalMs:0.###}ms max={group.MaxMs:0.###}ms.");
            }
        }

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

    private static CultPersistedRecord ToContentAddressedIndexRecord(
        CultPersistedRecord record,
        byte[] pagePayload) => new()
    {
        Key = record.Key,
        SchemaId = record.SchemaId,
        StoredAt = record.StoredAt,
        Payload = HashPayload(pagePayload)
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

    private string LegacyRecordPath(string key) => Path.Combine(_recordDirectory.FullName, $"{HashKey(key)}.msgpack");

    private string MetadataRecordPath(CultPersistedRecord metadata) => Path.Combine(
        _recordDirectory.FullName,
        $"{HashKey(metadata.Key + "\0" + metadata.SchemaId + "\0" + metadata.StoredAt)}.msgpack");

    private string ContentAddressedRecordPath(CultPersistedRecord metadata)
    {
        if (metadata.Payload == null || metadata.Payload.Length != 32)
            throw new InvalidDataException($"Record '{metadata.Key}' has no committed SHA-256 page identity.");
        var hash = BitConverter.ToString(metadata.Payload).Replace("-", string.Empty).ToLowerInvariant();
        return Path.Combine(_recordDirectory.FullName, $"{hash}.msgpack");
    }

    private static byte[] HashPayload(byte[] payload)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(payload);
    }

    private void DeleteUnreferencedRecordPages(IEnumerable<CultPersistedRecord> durableRecords)
    {
        var referenced = durableRecords
            .Select(ContentAddressedRecordPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _recordDirectory.EnumerateFiles("*.msgpack"))
        {
            if (referenced.Contains(file.FullName))
                continue;
            try
            {
                File.Delete(file.FullName);
            }
            catch (IOException)
            {
                // An orphan is harmless: no committed manifest references it.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup is best effort and never changes the committed generation.
            }
        }
    }

    private CultPersistedRecord ReadPersistedRecordPage(
        CultPersistedRecord metadata,
        string formatVersion,
        out byte[] pagePayload) =>
        ReadPersistedRecordPage(
            metadata,
            string.Equals(formatVersion, IndexedFormatVersion, StringComparison.Ordinal),
            string.Equals(formatVersion, LegacyMetadataPageFormatVersion, StringComparison.Ordinal),
            out pagePayload);

    private CultPersistedRecord ReadPersistedRecordPage(
        CultPersistedRecord metadata,
        bool usesContentAddressedPages,
        bool usesMetadataPages,
        out byte[] pagePayload)
    {
        var path = usesContentAddressedPages
            ? ContentAddressedRecordPath(metadata)
            : usesMetadataPages
                ? MetadataRecordPath(metadata)
                : LegacyRecordPath(metadata.Key);
        if (!File.Exists(path))
            throw new InvalidDataException($"Committed record page '{path}' is missing from the selected manifest generation.");
        pagePayload = ReadAllBytesShared(path);
        if (usesContentAddressedPages &&
            !HashPayload(pagePayload).SequenceEqual(metadata.Payload))
            throw new InvalidDataException($"Record page '{path}' does not match its committed content hash.");
        var record = CultDocumentMessagePackSerialization.DeserializePersistedRecord(pagePayload);
        if (!string.Equals(record.Key, metadata.Key, StringComparison.Ordinal))
            throw new InvalidDataException($"Record page '{path}' contains key '{record.Key}', expected '{metadata.Key}'.");
        return record;
    }

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

    private FileStream AcquireCommitLease()
    {
        Directory.CreateDirectory(_recordDirectory.FullName);
        var lockPath = Path.Combine(_recordDirectory.FullName, ".commit.lock");
        var started = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException) when (started.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(10);
            }
        }
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

        File.Replace(sourcePath, destinationPath, null);
    }
}
