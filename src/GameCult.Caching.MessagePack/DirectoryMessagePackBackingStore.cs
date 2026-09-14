using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace GameCult.Caching.MessagePack;

// A small hot manifest indexes the store; each record lives in one cold content-addressed page.
public sealed class DirectoryMessagePackBackingStore : CacheBackingStore
{
    private const string IndexedFormatVersion = "cultcache.store.v4.directory-content-addressed-pages";
    // An unleased load that has not settled after this many attempts throws.
    private const int UnleasedLoadAttempts = 5;
    private readonly FileInfo _manifestFile;
    private readonly DirectoryInfo _recordDirectory;
    private readonly ConcurrentDictionary<string, bool> _dirtyKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _deletedKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hydratedKeys = new(StringComparer.Ordinal);
    private CultSchemaCatalogEntry[] _durableCatalog = Array.Empty<CultSchemaCatalogEntry>();

    public DirectoryMessagePackBackingStore(string manifestPath, string? recordDirectoryPath = null, bool readOnly = false)
        : base(readOnly)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("Manifest path must be non-empty.", nameof(manifestPath));
        }

        _manifestFile = new FileInfo(manifestPath);
        _recordDirectory = new DirectoryInfo(recordDirectoryPath ?? DefaultRecordDirectoryPath(manifestPath));
    }

    public static string DefaultRecordDirectoryPath(string manifestPath) => manifestPath + ".records";

    // Test seam: runs between an unleased load's manifest read and its page reads.
    internal Action? UnleasedManifestRead { get; set; }

    public override string ToString() => _manifestFile.FullName;

    public override void PullAll() => Held(PullAllCore);

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
        CultSchemaCatalogEntry[] catalog;
        for (var attempt = 1; ; attempt++)
        {
            loaded.Clear();
            reports.Clear();
            // Only pages named by the manifest read under the lease are loaded; orphaned pages are never loaded. With no
            // lock to lease (none created yet, or a store copied without it) a writer may commit mid-load, so an unleased
            // load stands only if its manifest is still current; if the manifest moved it reloads, under the lease the
            // writer created. A page that fails under an unmoved manifest is corruption, not a race.
            using var lease = AcquireCommitLease(wait: true, create: false);
            var manifestBytes = ReadManifestBytes();
            var manifest = ParseManifest(manifestBytes);
            Trace($"manifest records={manifest.Records.Length}");
            try
            {
                if (lease == null)
                    UnleasedManifestRead?.Invoke();
                LoadRecordPages(
                    manifest.Records.OrderBy(record => record.Key, StringComparer.Ordinal).ToArray(),
                    manifest.SchemaCatalog,
                    loaded,
                    reports);
                if (lease != null || !ManifestMoved(manifestBytes))
                {
                    catalog = manifest.SchemaCatalog;
                    Trace($"indexed-pages loaded={loaded.Count}");
                    break;
                }
            }
            catch (Exception exception) when (lease == null && IsTornGeneration(exception))
            {
                // The re-read stays out of the filter: an exception thrown inside a filter is swallowed as false.
                if (!ManifestMoved(manifestBytes))
                    throw;
                if (attempt == UnleasedLoadAttempts)
                    throw Unsettled(exception);
                continue;
            }

            if (attempt == UnleasedLoadAttempts)
                throw Unsettled(null);
        }

        bool Staged(string key) => _dirtyKeys.ContainsKey(key) || _deletedKeys.ContainsKey(key);
        var arrived = loaded
            .Where(pair => !Staged(pair.Key) &&
                           (!Entries.TryGetValue(pair.Key, out var existing) ||
                            existing.StoredAt != pair.Value.StoredAt ||
                            existing.Descriptor.SchemaId != pair.Value.Descriptor.SchemaId))
            .Select(pair => pair.Value)
            .ToArray();
        var departed = _hydratedKeys
            .Where(key => !loaded.ContainsKey(key) && !Staged(key) && Entries.ContainsKey(key))
            .Select(key => Entries[key])
            .ToArray();
        if (arrived.Length > 0 || departed.Length > 0)
            Loaded?.Invoke(arrived, departed);

        // Adopted only once the cache admitted the load; a refused load leaves the store's durable view untouched.
        _durableCatalog = catalog;
        foreach (var stored in departed)
            Entries.TryRemove(stored.Key.Value, out _);
        _hydratedKeys.RemoveWhere(key => !loaded.ContainsKey(key) && !Staged(key));
        foreach (var stored in arrived)
            Entries[stored.Key.Value] = stored;
        foreach (var key in loaded.Keys)
            _hydratedKeys.Add(key);

        SetLastSchemaMigrationReports(reports);
        IsDirty = !_dirtyKeys.IsEmpty || !_deletedKeys.IsEmpty;
        Trace("publish");
    }

    public override void Push(CultStoredDocument entry)
    {
        ThrowIfReadOnly();
        Held(() =>
        {
            Entries[entry.Key.Value] = entry;
            _dirtyKeys[entry.Key.Value] = true;
            _deletedKeys.TryRemove(entry.Key.Value, out _);
            IsDirty = true;
        });
    }

    public override void Delete(CultStoredDocument entry)
    {
        ThrowIfReadOnly();
        Held(() =>
        {
            Entries.TryRemove(entry.Key.Value, out _);
            _dirtyKeys.TryRemove(entry.Key.Value, out _);
            _deletedKeys[entry.Key.Value] = true;
            IsDirty = true;
        });
    }

    public override CultCommitOutcome CommitBatch(CultCommitRequest request, bool wait)
    {
        ThrowIfReadOnly();
        return Held(() =>
        {
            Directory.CreateDirectory(_manifestFile.DirectoryName!);
            using var commitLease = AcquireCommitLease(wait, create: true);
            if (commitLease == null)
                return CultCommitOutcome.Contended;
            var manifest = ReadManifest();
            if (!request.ConditionsHold(manifest.Records, Entries.Values))
                return CultCommitOutcome.Mismatch;

            var previousEntries = Entries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var previousDirtyKeys = _dirtyKeys.Keys.ToArray();
            var previousDeletedKeys = _deletedKeys.Keys.ToArray();
            var wasDirty = IsDirty;
            try
            {
                foreach (var entry in request.Deletes)
                {
                    Entries.TryRemove(entry.Key.Value, out _);
                    _dirtyKeys.TryRemove(entry.Key.Value, out _);
                    _deletedKeys[entry.Key.Value] = true;
                }
                foreach (var entry in request.Upserts)
                {
                    Entries[entry.Key.Value] = entry;
                    _dirtyKeys[entry.Key.Value] = true;
                    _deletedKeys.TryRemove(entry.Key.Value, out _);
                }
                IsDirty = true;
                WriteGeneration(manifest);
                return CultCommitOutcome.Committed;
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
        });
    }

    public override void PushAll()
    {
        ThrowIfReadOnly();
        Held(() =>
        {
            if (!IsDirty)
                return;

            Directory.CreateDirectory(_manifestFile.DirectoryName!);
            using var commitLease = AcquireCommitLease(wait: true, create: true);
            WriteGeneration(ReadManifest());
        });
    }

    // Runs under the commit lease: pages first, then the manifest that names them.
    private void WriteGeneration(CultPersistedStoreSnapshot currentManifest)
    {
        Directory.CreateDirectory(_recordDirectory.FullName);
        var currentIndex = currentManifest.Records.ToDictionary(record => record.Key, record => record, StringComparer.Ordinal);
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
        var keysToWrite = _dirtyKeys.Keys
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
            var pagePayload = CultDocumentMessagePackSerialization.SerializePersistedRecord(record);
            var indexRecord = ToContentAddressedIndexRecord(record, pagePayload);
            currentIndex[key] = indexRecord;
            WriteFileAtomically(
                ContentAddressedRecordPath(indexRecord),
                pagePayload);
        }

        WriteManifest(targetCatalog, currentIndex.Values
            .OrderBy(record => record.Key, StringComparer.Ordinal)
            .ToArray());

        DeleteUnreferencedRecordPages(currentIndex.Values);

        _durableCatalog = targetCatalog;
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
        try
        {
            ReadPages();
        }
        catch (AggregateException aggregate)
        {
            // The page's own failure propagates, not Parallel.For's wrapper.
            ExceptionDispatchInfo.Capture(aggregate.Flatten().InnerExceptions[0]).Throw();
        }

        void ReadPages() => Parallel.For(
            0,
            records.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) },
            index =>
            {
                var started = tracePages ? Stopwatch.GetTimestamp() : 0L;
                var metadata = records[index];
                var record = ReadPersistedRecordPage(metadata, out var pagePayload);
                recordReports[index] = Registry.ResolvePersistedSchemaReport(record.SchemaId, catalogEntries);
                storedRecords[index] = ToStoredDocument(
                    record,
                    catalogEntries,
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

    private static CultPersistedRecord ToIndexRecord(CultStoredDocument stored) => new()
    {
        Key = stored.Key.Value,
        SchemaId = stored.Descriptor.SchemaId,
        StoredAt = stored.StoredAt,
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

    private CultPersistedStoreSnapshot ReadManifest() => ParseManifest(ReadManifestBytes());

    private byte[]? ReadManifestBytes() => File.Exists(_manifestFile.FullName) ? ReadAllBytesShared(_manifestFile.FullName) : null;

    // A manifest that cannot be re-read (a writer mid-replace) has moved.
    private bool ManifestMoved(byte[]? read)
    {
        try
        {
            var current = ReadManifestBytes();
            return read == null ? current != null : current == null || !read.SequenceEqual(current);
        }
        catch (Exception)
        {
            return true;
        }
    }

    // A page the manifest named that vanished or was replaced. It is a writer committing mid-load only when the manifest
    // moved too; under an unchanged manifest the same failure is corruption and is thrown as it is.
    private static bool IsTornGeneration(Exception exception) =>
        exception is InvalidDataException or FileNotFoundException or DirectoryNotFoundException;

    private InvalidOperationException Unsettled(Exception? last) => new(
        $"Directory store {_manifestFile.FullName} changed under every one of {UnleasedLoadAttempts} unlocked loads; it did not settle.", last);

    // A missing manifest is an empty store. An existing manifest in any other format is refused.
    private CultPersistedStoreSnapshot ParseManifest(byte[]? manifestBytes)
    {
        if (manifestBytes == null)
        {
            return new CultPersistedStoreSnapshot
            {
                FormatVersion = IndexedFormatVersion,
                SchemaCatalog = Array.Empty<CultSchemaCatalogEntry>(),
                Records = Array.Empty<CultPersistedRecord>()
            };
        }

        var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(manifestBytes);
        if (!string.Equals(snapshot.FormatVersion, IndexedFormatVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Directory store {_manifestFile.FullName} is {snapshot.FormatVersion}; only {IndexedFormatVersion} is readable.");
        }

        return snapshot;
    }

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

    private CultPersistedRecord ReadPersistedRecordPage(CultPersistedRecord metadata, out byte[] pagePayload)
    {
        var path = ContentAddressedRecordPath(metadata);
        if (!File.Exists(path))
            throw new InvalidDataException($"Committed record page '{path}' is missing from the selected manifest generation.");
        pagePayload = ReadAllBytesShared(path);
        if (!HashPayload(pagePayload).SequenceEqual(metadata.Payload))
            throw new InvalidDataException($"Record page '{path}' does not match its committed content hash.");
        var record = CultDocumentMessagePackSerialization.DeserializePersistedRecord(pagePayload);
        if (!string.Equals(record.Key, metadata.Key, StringComparison.Ordinal))
            throw new InvalidDataException($"Record page '{path}' contains key '{record.Key}', expected '{metadata.Key}'.");
        return record;
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

    // Only a writer creates the records folder and the lock, so opening the store creates nothing. A reader opens an
    // existing lock and returns null when there is none; that does not mean no writer committed (a store may be copied
    // without its lock, and a first writer may create it mid-load), so an unleased load checks that it settled.
    private FileStream? AcquireCommitLease(bool wait, bool create)
    {
        if (create)
            Directory.CreateDirectory(_recordDirectory.FullName);
        var lockPath = Path.Combine(_recordDirectory.FullName, ".commit.lock");
        var started = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    create ? FileMode.OpenOrCreate : FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (FileNotFoundException) when (!create)
            {
                return null;
            }
            catch (DirectoryNotFoundException) when (!create)
            {
                return null;
            }
            catch (IOException) when (!wait)
            {
                return null;
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
