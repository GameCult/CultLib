using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.OutsideStoreTests
{
    // variants:ruling:store-kind-per-key-judge: a store kind written outside the library applies the whole per-key ownership rule
    // through the one protected judge (CacheBackingStore.RefuseUndeclaredOnDisk), using only public and protected members. This
    // assembly has no InternalsVisibleTo, so a judge parameter type, or something it needs, turning internal breaks it here.
    public class OutsideStoreJudgeTests
    {
        [CultDocument("tests.out_a", "tests.out_a.v1")]
        [MessagePackObject]
        public sealed class OutA
        {
            [Key(0)] [CultName] public string Name { get; set; } = string.Empty;
            [Key(1)] public int Value { get; set; }
        }

        [CultDocument("tests.out_b", "tests.out_b.v1")]
        [MessagePackObject]
        public sealed class OutB
        {
            [Key(0)] [CultName] public string Name { get; set; } = string.Empty;
            [Key(1)] public int Value { get; set; }
        }

        // RbX's schema id, which RbY lists; the test below asserts it, so a changed derivation fails there.
        private const string RbXId = "sha256:25087e0d76fd5b9ac950723ccd47f3f3ac1428b53b49ca9bda9aae8b38a0220a";

        [CultDocument("tests.rb_x", "tests.rb_x.v1")]
        [MessagePackObject]
        public sealed class RbX
        {
            [Key(0)] [CultName] public string Name { get; set; } = string.Empty;
            [Key(1)] public int Value { get; set; }
        }

        [CultDocument("tests.rb_y", "tests.rb_y.v1", CompatibleSchemaIds = new[] { RbXId })]
        [MessagePackObject]
        public sealed class RbY
        {
            [Key(0)] [CultName] public string Name { get; set; } = string.Empty;
            [Key(1)] public int Value { get; set; }
        }

        [CultDocument("tests.rb_z", "tests.rb_z.v1")]
        [MessagePackObject]
        public sealed class RbZ
        {
            [Key(0)] [CultName] public string Name { get; set; } = string.Empty;
            [Key(1)] public int Value { get; set; }
        }

        // What the "disk" holds: records by key and the catalog that publishes their ids.
        private sealed class Disk
        {
            public readonly Dictionary<string, (CultPersistedRecord Record, string Version)> Records = new();
            public readonly List<CultSchemaCatalogEntry> Catalog = new();
            private int _version;

            public void Put(CultPersistedRecord record, CultSchemaCatalogEntry entry)
            {
                if (Catalog.All(known => known.SchemaId != entry.SchemaId))
                    Catalog.Add(entry);
                Records[record.Key] = (record, (++_version).ToString());
            }

            public string? SchemaIdAt(string key) => Records.TryGetValue(key, out var held) ? held.Record.SchemaId : null;
        }

        // A store kind with no InternalsVisibleTo: an in-memory disk, flushed and committed under the protected judge.
        private sealed class OutsideStore : CacheBackingStore
        {
            private readonly Disk _disk;
            private readonly CultCodec _codec;
            private readonly HashSet<string> _staged = new(StringComparer.Ordinal);
            // StoredVersion is internal to the library, so a store kind tracks the version it last took of each key itself.
            private readonly Dictionary<string, string> _versions = new(StringComparer.Ordinal);

            public OutsideStore(Disk disk, CultCodec codec)
            {
                _disk = disk;
                _codec = codec;
            }

            private object Decode(Type type, byte[] payload) => _codec.Deserialize(type, type, payload)!;

            private CultStoredDocument? DurableAt(string key)
            {
                if (!_disk.Records.TryGetValue(key, out var held))
                    return null;
                try
                {
                    return ToStoredDocument(held.Record, held.Version, _disk.Catalog, Decode, out _);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            public override void PullAll() => Held(() =>
            {
                var loaded = _disk.Records
                    .Where(pair => !_versions.TryGetValue(pair.Key, out var known) || !SameStoredIdentity(known, pair.Value.Version))
                    .Select(pair => DurableAt(pair.Key))
                    .OfType<CultStoredDocument>()
                    .ToArray();
                var dropped = Entries.Values.Where(known => !_disk.Records.ContainsKey(known.Key.Value)).ToArray();
                if (loaded.Length > 0 || dropped.Length > 0)
                    Loaded?.Invoke(loaded, dropped);
                foreach (var stored in dropped)
                    Entries.TryRemove(stored.Key.Value, out _);
                foreach (var stored in loaded)
                {
                    Entries[stored.Key.Value] = stored;
                    _versions[stored.Key.Value] = _disk.Records[stored.Key.Value].Version;
                }
            });

            public override void Push(CultStoredDocument entry) => Held(() =>
            {
                Entries[entry.Key.Value] = entry;
                _staged.Add(entry.Key.Value);
                IsDirty = true;
            });

            public override void Delete(CultStoredDocument entry) => Held(() =>
            {
                Entries.TryRemove(entry.Key.Value, out _);
                _staged.Add(entry.Key.Value);
                IsDirty = true;
            });

            public override void PushAll() => Held(() => Apply(Array.Empty<CultStoredDocument>(), Array.Empty<CultStoredDocument>(), Array.Empty<string>()));

            public override CultCommitOutcome CommitBatch(CultCommitRequest request, bool wait) => Held(() =>
            {
                Apply(request.Upserts, request.Deletes, request.Checks);
                foreach (var entry in request.Deletes)
                    Entries.TryRemove(entry.Key.Value, out _);
                foreach (var entry in request.Upserts)
                    Entries[entry.Key.Value] = entry;
                return CultCommitOutcome.Committed;
            });

            private void Apply(IReadOnlyList<CultStoredDocument> upserts, IReadOnlyList<CultStoredDocument> deletes, IEnumerable<string> checks)
            {
                var writes = upserts.ToDictionary(entry => entry.Key.Value, StringComparer.Ordinal);
                foreach (var key in _staged)
                {
                    if (Entries.TryGetValue(key, out var pushed))
                        writes.TryAdd(key, pushed);
                }

                var removals = deletes.Select(entry => entry.Key.Value)
                    .Concat(_staged.Where(key => !writes.ContainsKey(key) && !Entries.ContainsKey(key)))
                    .ToArray();
                var judged = removals.Concat(checks).ToArray();
                var entries = writes.ToDictionary(pair => pair.Key, pair => pair.Value.Descriptor.ToCatalogEntry(), StringComparer.Ordinal);

                // The whole rule, from the protected judge: the same call both shipped stores make.
                RefuseUndeclaredOnDisk(
                    writes.Keys.Concat(judged).Concat(StagedChecks),
                    _disk.Catalog,
                    key => _disk.Records.TryGetValue(key, out var held) ? held.Record : null,
                    key => entries.TryGetValue(key, out var entry) ? entry : null,
                    judged,
                    DurableAt,
                    _ => null,
                    key =>
                    {
                        _staged.Remove(key);
                        IsDirty = _staged.Count > 0;
                    });

                foreach (var (key, document) in writes)
                {
                    var type = document.Descriptor.DocumentType;
                    _disk.Put(
                        new CultPersistedRecord
                        {
                            Key = key,
                            SchemaId = document.Descriptor.SchemaId,
                            StoredAt = document.StoredAt,
                            Payload = _codec.Serialize(document.Document, type, type)
                        },
                        entries[key]);
                    _versions[key] = _disk.Records[key].Version;
                }

                foreach (var key in removals.Where(key => !writes.ContainsKey(key)))
                {
                    _disk.Records.Remove(key);
                    _versions.Remove(key);
                }
                _staged.Clear();
                MarkFlushSucceeded();
            }
        }

        private static readonly CultDocumentRegistry AB = CultDocumentRegistry.ForTypes(new[] { typeof(OutA), typeof(OutB) });
        private static readonly CultDocumentRegistry Rb = CultDocumentRegistry.ForTypes(new[] { typeof(RbX), typeof(RbY), typeof(RbZ) });
        private static readonly CultRecordKey K = new("k");
        private static readonly CultRecordKey K2 = new("k2");

        private static CultCache Open(Disk disk, CultDocumentRegistry registry)
        {
            var cache = new CultCache(registry, CultCacheMessagePack.CreateCodec(registry));
            cache.AddBackingStore(new OutsideStore(disk, cache.Codec!));
            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();
            return cache;
        }

        // Another writer puts a record of this type at the key on the disk, as the cache's own store would have.
        private static void PutOnDisk<T>(Disk disk, CultDocumentRegistry registry, string key, T document, string? asSchemaId = null, CultSchemaCatalogEntry? entry = null)
            where T : class
        {
            var codec = CultCacheMessagePack.CreateCodec(registry);
            var descriptor = registry.GetRequired<T>();
            disk.Put(
                new CultPersistedRecord
                {
                    Key = key,
                    SchemaId = asSchemaId ?? descriptor.SchemaId,
                    StoredAt = DateTimeOffset.UtcNow.ToString("O"),
                    Payload = codec.Serialize(document, typeof(T), typeof(T))
                },
                entry ?? descriptor.ToCatalogEntry());
        }

        private static Type HeldType(CultCache cache, CultRecordKey key) => cache.GetStored(key)!.Descriptor.DocumentType;

        // The written type's own id: a record of another registered type that this cache never loaded.
        [Test]
        public void AnotherRegisteredTypesUnheldRecordIsRefusedUnderTheLockAndReloaded()
        {
            var disk = new Disk();
            using var cache = Open(disk, AB);
            PutOnDisk(disk, AB, "k", new OutB { Name = "k", Value = 2 });
            var before = disk.Records["k"];

            Assert.That(
                () => cache.Commit(batch => batch.Upsert(typeof(OutA), new OutA { Name = "k", Value = 1 }, K)),
                Throws.InstanceOf<CultSchemaConflictException>());
            Assert.That(disk.Records["k"], Is.EqualTo(before), "the refused write changed nothing on the disk");
            Assert.That(HeldType(cache, K), Is.EqualTo(typeof(OutB)), "the refusal reloaded the record the store holds");
        }

        [Test]
        public void AFlushOfAnotherRegisteredTypesUnheldRecordIsRefusedToo()
        {
            var disk = new Disk();
            using var cache = Open(disk, AB);
            PutOnDisk(disk, AB, "k", new OutB { Name = "k", Value = 2 });

            cache.UpsertAsync(typeof(OutA), new OutA { Name = "k", Value = 1 }, K).GetAwaiter().GetResult();
            Assert.That(() => cache.FlushAsync().GetAwaiter().GetResult(), Throws.InstanceOf<CultSchemaConflictException>());
            Assert.That(disk.SchemaIdAt("k"), Is.EqualTo(AB.GetRequired<OutB>().SchemaId));
            Assert.That(HeldType(cache, K), Is.EqualTo(typeof(OutB)));
        }

        // Claimant precedence, a listed id: the written type owns a record under an id it lists, and a type that lists nothing does not.
        [Test]
        public void ARecordUnderAnIdTheWrittenTypeListsIsOwnedAndOneItDoesNotListIsRefused()
        {
            Assert.That(Rb.GetRequired<RbX>().SchemaId, Is.EqualTo(RbXId));
            var disk = new Disk();
            using var cache = Open(disk, Rb);
            PutOnDisk(disk, Rb, "k", new RbX { Name = "k" });
            PutOnDisk(disk, Rb, "k2", new RbX { Name = "k2" });

            Assert.That(cache.Commit(batch => batch.Upsert(typeof(RbY), new RbY { Name = "k", Value = 1 }, K)), Is.True);
            Assert.That(disk.SchemaIdAt("k"), Is.EqualTo(Rb.GetRequired<RbY>().SchemaId));

            Assert.That(
                () => cache.Commit(batch => batch.Upsert(typeof(RbZ), new RbZ { Name = "k2" }, K2)),
                Throws.InstanceOf<CultSchemaConflictException>());
            Assert.That(disk.SchemaIdAt("k2"), Is.EqualTo(RbXId), "the type that lists nothing replaced nothing");
        }

        // Reads whole: an older id of the written type's own schema name, which the type reads whole, is its own and is restamped.
        [Test]
        public void AnOlderIdOfTheWrittenSchemaNameThatIsReadWholeIsOwnedAndRestamped()
        {
            var disk = new Disk();
            using var cache = Open(disk, AB);
            var current = AB.GetRequired<OutA>().ToCatalogEntry();
            var older = new CultSchemaCatalogEntry
            {
                SchemaId = "sha256:older-out-a",
                SchemaName = current.SchemaName,
                SchemaVersion = "tests.out_a.v0",
                ContentHash = current.ContentHash,
                CanonicalSchemaJson = current.CanonicalSchemaJson,
                Members = current.Members
            };
            PutOnDisk(disk, AB, "k", new OutA { Name = "k" }, asSchemaId: older.SchemaId, entry: older);

            Assert.That(cache.Commit(batch => batch.Upsert(typeof(OutA), new OutA { Name = "k", Value = 3 }, K)), Is.True);
            Assert.That(disk.SchemaIdAt("k"), Is.EqualTo(current.SchemaId));
        }

        // Staged retype removals: a held key retypes as a removal then an add, in one batch and staged apart.
        [TestCase(true)]
        [TestCase(false)]
        public void ARetypeOfAHeldKeyCommitsInOneBatchAndStagedApart(bool oneBatch)
        {
            var disk = new Disk();
            PutOnDisk(disk, AB, "k", new OutB { Name = "k", Value = 2 });
            using var cache = Open(disk, AB);
            Assert.That(HeldType(cache, K), Is.EqualTo(typeof(OutB)));

            if (oneBatch)
                Assert.That(cache.Commit(batch =>
                {
                    batch.Remove(K);
                    batch.Upsert(typeof(OutA), new OutA { Name = "k", Value = 1 }, K);
                }), Is.True);
            else
            {
                Assert.That(cache.Remove(K), Is.True);
                Assert.That(cache.Commit(batch => batch.Upsert(typeof(OutA), new OutA { Name = "k", Value = 1 }, K)), Is.True);
            }

            Assert.That(disk.SchemaIdAt("k"), Is.EqualTo(AB.GetRequired<OutA>().SchemaId));
            Assert.That(HeldType(cache, K), Is.EqualTo(typeof(OutA)));
        }

        [Test]
        public void ARetypeOfAKeyTheCacheNeverLoadedIsJudgedByTheRemovalRule()
        {
            var disk = new Disk();
            using var cache = Open(disk, AB);
            PutOnDisk(disk, AB, "k", new OutB { Name = "k", Value = 2 });

            Assert.That(cache.Commit(batch =>
            {
                batch.Remove(K);
                batch.Upsert(typeof(OutA), new OutA { Name = "k", Value = 1 }, K);
            }), Is.True, "a record a registered type reads may be removed, so the key retypes");
            Assert.That(disk.SchemaIdAt("k"), Is.EqualTo(AB.GetRequired<OutA>().SchemaId));
        }

        // A removal is never licensed over a record no registered type owns, retype or not.
        [Test]
        public void ACommitNeverRemovesOrRetypesARecordNoRegisteredTypeOwns()
        {
            var disk = new Disk();
            using var cache = Open(disk, AB);
            var unknown = new CultSchemaCatalogEntry { SchemaId = "tests.out_unknown", SchemaName = "tests.out_unknown" };
            disk.Put(new CultPersistedRecord { Key = "k", SchemaId = unknown.SchemaId, StoredAt = "t", Payload = new byte[] { 0x90 } }, unknown);
            var before = disk.Records["k"];

            Assert.That(() => cache.Commit(batch =>
            {
                batch.Remove(K);
                batch.Upsert(typeof(OutA), new OutA { Name = "k" }, K);
            }), Throws.InstanceOf<CultSchemaConflictException>(), "retype");
            Assert.That(() => cache.Commit(batch => batch.Remove(K)), Throws.InstanceOf<CultSchemaConflictException>(), "removal");
            Assert.That(disk.Records["k"], Is.EqualTo(before));
        }
    }
}
