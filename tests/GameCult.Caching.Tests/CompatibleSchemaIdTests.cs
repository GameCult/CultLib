#nullable enable
using System;
using System.IO;
using System.Linq;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using static GameCult.Caching.Tests.EmittedDocumentTypes;

namespace GameCult.Caching.Tests
{
    // A document type declares the older schema ids its records may sit under (CultDocumentAttribute.CompatibleSchemaIds). The
    // registered catalog entry lists them, a cache resolves a record under one to that type and stamps it with the type's own id,
    // and a store writer never changes the identity of a record it did not receive.
    public class CompatibleSchemaIdTests
    {
        private const string OldId = "old.id";
        private const string OwnerId = "sha256:86186abf7d63d8a326c725d39a10f3a4db11b1e8698e8c0fcf6d9720031bc480";

        [CultDocument("tests.compat_deck", "tests.compat_deck.v2", CompatibleSchemaIds = new[] { OldId, OldId })]
        [MessagePackObject]
        public sealed class DeclaringDeck
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
        }

        // The same schema as DeclaringDeck (the class name is not part of a schema id), declaring nothing. Emitted, because a
        // declared type that differs from DeclaringDeck only in its declaration is refused beside it by the shared registry.
        private static readonly Type BareDeck = Emit("BareDeck", "tests.compat_deck", "tests.compat_deck.v2", new[] { new Field("Name", typeof(string), 0, IsName: true) });

        [CultDocument("tests.compat_owner", "tests.compat_owner.v1", CompatibleSchemaIds = new[] { OwnerId, " " })]
        [MessagePackObject]
        public sealed class Owner
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
        }

        // One schema before and after a member was added, with nothing declared: the later type reads the earlier records by name.
        [CultDocument("tests.compat_evolving", "tests.compat_evolving.v1")]
        [MessagePackObject]
        public sealed class EvolvingV1
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
        }

        [CultDocument("tests.compat_evolving", "tests.compat_evolving.v2")]
        [MessagePackObject]
        public sealed class EvolvingV2
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
            [Key(1)] public string Extra { get; set; } = "";
        }

        private static readonly CultDocumentRegistry Declaring = CultDocumentRegistry.ForTypes(new[] { typeof(DeclaringDeck) });
        private static readonly CultDocumentRegistry Bare = CultDocumentRegistry.ForTypes(new[] { BareDeck });
        private static readonly CultRecordKey D = new("d");
        private string _directory = "";

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cultlib-compat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }

        private static CultCache Open(string path, CultDocumentRegistry registry) =>
            CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = registry });

        private static CultPersistedStoreSnapshot Read(string path) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));

        private static object Deck(string name) => New(BareDeck, ("Name", name));

        private static void Rewrite(string path, Action<CultPersistedStoreSnapshot> edit)
        {
            var snapshot = Read(path);
            edit(snapshot);
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
        }

        // A store written by a runtime that knew the schema under an older id and another name: one entry that owns OldId, one
        // record under it. Nothing but the declaration lets this runtime's type claim it.
        private string OldStore(string name)
        {
            var path = Path.Combine(_directory, name);
            using (var cache = Open(path, Bare))
                cache.Commit(batch => batch.Upsert(BareDeck, Deck("d"), D));
            Rewrite(path, snapshot =>
            {
                var entry = snapshot.SchemaCatalog.Single();
                entry.SchemaId = OldId;
                entry.SchemaName = "tests.legacy_deck";
                entry.ContentHash = "stale";
                entry.CompatibleSchemaIds = new[] { OldId };
                snapshot.Records.Single().SchemaId = OldId;
            });
            return path;
        }

        // The same store with the entry keeping this schema's name and only listing the older id: what a store carries after a
        // writer that did not declare it, so a runtime without the declaration can still read it.
        private string OldStoreUnderTheSameName(string name)
        {
            var path = Path.Combine(_directory, name);
            using (var cache = Open(path, Bare))
                cache.Commit(batch => batch.Upsert(BareDeck, Deck("d"), D));
            Rewrite(path, snapshot =>
            {
                var entry = snapshot.SchemaCatalog.Single();
                entry.ContentHash = "stale";
                entry.CompatibleSchemaIds = new[] { entry.SchemaId, OldId };
                snapshot.Records.Single().SchemaId = OldId;
            });
            return path;
        }

        [Test]
        public void TheEmittedBareDeckIsTheDeclaringDecksSchema()
        {
            Assert.That(Bare.GetRequired(BareDeck).SchemaId, Is.EqualTo(Declaring.GetRequired<DeclaringDeck>().SchemaId));
        }

        [Test]
        public void ARecordUnderADeclaredOldIdOpensAndACommitRewritesItUnderTheRegisteredId()
        {
            var path = OldStore("old.cc");
            var descriptor = Declaring.GetRequired<DeclaringDeck>();

            using (var cache = Open(path, Declaring))
            {
                Assert.That(cache.Get<DeclaringDeck>(D)!.Name, Is.EqualTo("d"), "the declaration resolves the record to the type");
                Assert.That(cache.Commit(batch => batch.Upsert(typeof(DeclaringDeck), new DeclaringDeck { Name = "d2" }, D)), Is.True);
            }

            var written = Read(path);
            Assert.That(written.Records.Single().SchemaId, Is.EqualTo(descriptor.SchemaId));
            var entry = written.SchemaCatalog.Single();
            Assert.That(entry.SchemaId, Is.EqualTo(descriptor.SchemaId));
            Assert.That(entry.SchemaName, Is.EqualTo("tests.compat_deck"));
            Assert.That(entry.CompatibleSchemaIds, Is.EqualTo(new[] { descriptor.SchemaId, OldId }));
            foreach (var registry in new[] { Declaring, Bare })
            {
                using var reopened = Open(path, registry);
                Assert.That(reopened.AllEntries.Count(), Is.EqualTo(1));
            }
        }

        // The name a catalog entry carries is metadata: a record resolves by its schema id, so a schema renamed under the id the
        // registered type owns opens, with the entry's content hash no longer matching the type's.
        [Test]
        public void ASchemaRenamedUnderTheIdARegisteredTypeOwnsOpens()
        {
            var path = Path.Combine(_directory, "renamed.cc");
            using (var cache = Open(path, Bare))
                cache.Commit(batch => batch.Upsert(BareDeck, Deck("d"), D));
            Rewrite(path, snapshot =>
            {
                snapshot.SchemaCatalog.Single().SchemaName = "tests.renamed_deck";
                snapshot.SchemaCatalog.Single().ContentHash = "stale";
            });

            using var reopened = Open(path, Bare);
            Assert.That(EmittedDocumentTypes.Read(reopened.Get(D)!, "Name"), Is.EqualTo("d"));
        }

        [Test]
        public void ARecordUnderAnOldIdNoTypeDeclaresIsCarriedAsForeignNotOpenedByThatSchemaName()
        {
            var path = OldStore("undeclared.cc");

            using var cache = Open(path, Bare);
            Assert.That(cache.Get(D), Is.Null, "the entry's name is metadata; only a declared id resolves it");
            Assert.That(cache.BackingStores.Single().ForeignRecords.Select(record => (record.Key, record.SchemaId, record.SchemaName)),
                Is.EqualTo(new[] { (D.Value, OldId, "tests.legacy_deck") }));
        }

        // An unconditional commit writes what it staged: the record the batch does not name is copied as the file holds it, under
        // the old id and with the entry that publishes that id, though the cache resolved it to the registered type.
        [Test]
        public void AnUnconditionalCommitCopiesALoadedRecordUnderItsOldIdAsTheFileHoldsIt()
        {
            var path = OldStore("copied.cc");
            var descriptor = Declaring.GetRequired<DeclaringDeck>();
            var before = Read(path);
            var entryBefore = StoreSlices.CatalogEntry(path, OldId);

            using (var cache = Open(path, Declaring))
                cache.Commit(batch => batch.Upsert(typeof(DeclaringDeck), new DeclaringDeck { Name = "e" }, new CultRecordKey("e")));

            var written = Read(path);
            Assert.That(written.Records.Single(record => record.Key == "d").SchemaId, Is.EqualTo(OldId));
            Assert.That(written.Records.Single(record => record.Key == "e").SchemaId, Is.EqualTo(descriptor.SchemaId));
            var copied = written.Records.Single(record => record.Key == "d");
            var original = before.Records.Single();
            Assert.That((copied.StoredAt, copied.Payload), Is.EqualTo((original.StoredAt, original.Payload)));
            Assert.That(StoreSlices.CatalogEntry(path, OldId), Is.EqualTo(entryBefore),
                "the entry that published the old id is carried as its bytes");
        }

        // A commit onto the file writes its batch and leaves every other record as the file holds it: the store writer never
        // changes identity. The record stays under the older id, which the registered entry publishes as compatible.
        [Test]
        public void ACommitOntoTheFileLeavesARecordUnderADeclaredOldIdAsItIsAndPublishesTheId()
        {
            var path = OldStoreUnderTheSameName("onto.cc");
            var descriptor = Declaring.GetRequired<DeclaringDeck>();

            using (var cache = Open(path, Declaring))
            {
                Assert.That(cache.Get<DeclaringDeck>(D), Is.Not.Null);
                Assert.That(cache.Commit(batch =>
                {
                    batch.Expect(new CultRecordKey("e"), null);
                    batch.Upsert(typeof(DeclaringDeck), new DeclaringDeck { Name = "e" }, new CultRecordKey("e"));
                }), Is.True);
            }

            var written = Read(path);
            Assert.That(written.Records.Single(record => record.Key == "d").SchemaId, Is.EqualTo(OldId));
            Assert.That(written.Records.Single(record => record.Key == "e").SchemaId, Is.EqualTo(descriptor.SchemaId));
            Assert.That(written.SchemaCatalog.Single().CompatibleSchemaIds, Does.Contain(OldId));
            using var reopened = Open(path, Declaring);
            Assert.That(reopened.Get<DeclaringDeck>(D), Is.Not.Null);
        }

        [Test]
        public void WithoutTheDeclarationACommitOntoTheFileIsRefusedTypedAndTheFileLeftAsItWas()
        {
            var path = OldStoreUnderTheSameName("undeclared-onto.cc");
            var bytes = File.ReadAllBytes(path);

            using var cache = Open(path, Bare);
            var refusal = Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch =>
            {
                batch.Expect(new CultRecordKey("e"), null);
                batch.Upsert(BareDeck, Deck("e"), new CultRecordKey("e"));
            }))!;
            Assert.That(refusal.SchemaId, Is.EqualTo(OldId));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        }

        // A record under a declared old id is the same record the cache holds, loaded under that id: a condition naming it holds.
        [Test]
        public void AConditionOnARecordUnderADeclaredOldIdHoldsAndTheWholeStoreCountsAsUnchanged()
        {
            var path = OldStoreUnderTheSameName("conditions.cc");

            using var cache = Open(path, Declaring);
            var current = cache.Get<DeclaringDeck>(D)!;
            Assert.That(cache.TryCommit(batch =>
            {
                batch.Expect(D, current);
                batch.ExpectUnchanged();
                batch.Upsert(typeof(DeclaringDeck), new DeclaringDeck { Name = "e" }, new CultRecordKey("e"));
            }), Is.EqualTo(CultCommitOutcome.Committed));
        }

        // The directory store judges conditions against its manifest, whose record still sits under the older id.
        [Test]
        public void AConditionOnARecordUnderADeclaredOldIdHoldsInTheDirectoryStore()
        {
            var path = Path.Combine(_directory, "dir.cc");
            var options = new CultCacheOpenOptions { Registry = Bare, UseDirectoryStore = true };
            using (var cache = CultCacheMessagePack.Create(path, options))
                cache.Commit(batch => batch.Upsert(BareDeck, Deck("d"), D));
            Rewrite(path, manifest =>
            {
                var entry = manifest.SchemaCatalog.Single();
                entry.ContentHash = "stale";
                entry.CompatibleSchemaIds = new[] { entry.SchemaId, OldId };
                manifest.Records.Single().SchemaId = OldId;
            });

            using var declaring = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Declaring, UseDirectoryStore = true });
            var current = declaring.Get<DeclaringDeck>(D)!;
            Assert.That(declaring.TryCommit(batch =>
            {
                batch.Expect(D, current);
                batch.ExpectUnchanged();
                batch.Upsert(typeof(DeclaringDeck), new DeclaringDeck { Name = "e" }, new CultRecordKey("e"));
            }), Is.EqualTo(CultCommitOutcome.Committed));
        }

        // Conditions compare the id a record was loaded under with the file's, exactly. A record resolved by some other path than
        // its type's own id is still unchanged when nothing on disk moved: by its schema name under a foreign id, by name across a
        // schema change that declared nothing, or by a file entry that lists this runtime's id.
        private static void AssertUnchangedConditionsHold(CultCache cache, CultRecordKey key, object current, Type type, object added)
        {
            Assert.That(cache.TryCommit(batch =>
            {
                batch.Expect(key, current);
                batch.Upsert(type, added, new CultRecordKey("added-" + Guid.NewGuid().ToString("N")));
            }), Is.EqualTo(CultCommitOutcome.Committed), "Expect");
            Assert.That(cache.TryCommit(batch =>
            {
                batch.ExpectUnchanged();
                batch.Upsert(type, added, new CultRecordKey("added-" + Guid.NewGuid().ToString("N")));
            }), Is.EqualTo(CultCommitOutcome.Committed), "ExpectUnchanged");
        }

        [Test]
        public void AConditionOnARecordResolvedByNameUnderAForeignIdHolds()
        {
            var path = Path.Combine(_directory, "foreign.cc");
            using (var cache = Open(path, Bare))
                cache.Commit(batch => batch.Upsert(BareDeck, Deck("d"), D));
            Rewrite(path, snapshot =>
            {
                var entry = snapshot.SchemaCatalog.Single();
                entry.SchemaId = "foreign.id";
                entry.CompatibleSchemaIds = new[] { "foreign.id" };
                snapshot.Records.Single().SchemaId = "foreign.id";
            });

            using var reader = Open(path, Declaring);
            AssertUnchangedConditionsHold(reader, D, reader.Get<DeclaringDeck>(D)!, typeof(DeclaringDeck), new DeclaringDeck { Name = "e" });
        }

        [Test]
        public void AConditionOnARecordAnEvolvedTypeResolvedByNameHolds()
        {
            var path = Path.Combine(_directory, "evolved.cc");
            using (var cache = Open(path, CultDocumentRegistry.ForTypes(new[] { typeof(EvolvingV1) })))
                cache.Commit(batch => batch.Upsert(typeof(EvolvingV1), new EvolvingV1 { Name = "d" }, D));

            using var reader = Open(path, CultDocumentRegistry.ForTypes(new[] { typeof(EvolvingV2) }));
            AssertUnchangedConditionsHold(reader, D, reader.Get<EvolvingV2>(D)!, typeof(EvolvingV2), new EvolvingV2 { Name = "e" });
        }

        [Test]
        public void AConditionOnARecordAFileEntryListingTheLocalIdResolvedHolds()
        {
            var path = Path.Combine(_directory, "listed.cc");
            var localId = Bare.GetRequired(BareDeck).SchemaId;
            using (var cache = Open(path, Bare))
                cache.Commit(batch => batch.Upsert(BareDeck, Deck("d"), D));
            Rewrite(path, snapshot =>
            {
                var entry = snapshot.SchemaCatalog.Single();
                entry.SchemaId = OldId;
                entry.CompatibleSchemaIds = new[] { OldId, localId };
                snapshot.Records.Single().SchemaId = OldId;
            });

            using var reader = Open(path, Bare);
            AssertUnchangedConditionsHold(reader, D, reader.Get(D)!, BareDeck, Deck("e"));
        }

        // A plain record and a variant loaded under a declared old id carry that id, so a condition on either holds while the file
        // is unchanged. An unconditional commit then copies both as they are, still under the old id, and the cache still holds
        // each under it: the same conditions hold against the rewritten file.
        [Test]
        public void ConditionsOnRecordsLoadedUnderAnOldIdHoldBeforeAndAfterAnUnconditionalCommit()
        {
            var path = Path.Combine(_directory, "restamped.cc");
            var b = new CultRecordKey("b");
            var v = new CultRecordKey("v");
            using (var cache = Open(path, Declaring))
            {
                cache.Commit(batch => batch.Upsert(typeof(DeclaringDeck), new DeclaringDeck { Name = "b" }, b));
                cache.Commit(batch => batch.UpsertVariant(v, b, new[] { cache.Override(typeof(DeclaringDeck), nameof(DeclaringDeck.Name), "v") }));
            }

            Rewrite(path, snapshot =>
            {
                foreach (var record in snapshot.Records)
                    record.SchemaId = OldId;
            });

            using var reader = Open(path, Declaring);
            void ConditionsHold(string phase)
            {
                foreach (var key in new[] { b, v })
                {
                    Assert.That(reader.TryCommit(batch =>
                    {
                        batch.Expect(key, reader.Get(key)!);
                        batch.Upsert(typeof(DeclaringDeck), new DeclaringDeck { Name = "f" }, new CultRecordKey($"f-{phase}-{key.Value}"));
                    }), Is.EqualTo(CultCommitOutcome.Committed), $"{phase}: Expect {key.Value}");
                }

                Assert.That(reader.TryCommit(batch =>
                {
                    batch.ExpectUnchanged();
                    batch.Upsert(typeof(DeclaringDeck), new DeclaringDeck { Name = "g" }, new CultRecordKey($"g-{phase}"));
                }), Is.EqualTo(CultCommitOutcome.Committed), $"{phase}: ExpectUnchanged");
            }

            ConditionsHold("loaded");
            Assert.That(Read(path).Records.Where(record => record.Key is "b" or "v").Select(record => record.SchemaId), Is.All.EqualTo(OldId),
                "commits onto the file left the loaded records under the old id");

            reader.Commit(batch => batch.Upsert(typeof(DeclaringDeck), new DeclaringDeck { Name = "e" }, new CultRecordKey("e")));
            Assert.That(Read(path).Records.Where(record => record.Key is "b" or "v").Select(record => record.SchemaId), Is.All.EqualTo(OldId),
                "the unconditional commit copied the loaded records under the old id");
            ConditionsHold("rewritten");
        }

        // The directory store rereads a record whose manifest entry moved to another id with the same storedAt, as a rewrite that
        // mints no storedAt leaves it: after a pull the cache holds the record under that id, and a condition on it holds.
        [Test]
        public void ADirectoryStorePullRereadsARecordWhoseIdAloneChanged()
        {
            var path = Path.Combine(_directory, "dir-moved.cc");
            var options = new CultCacheOpenOptions { Registry = Declaring, UseDirectoryStore = true };
            using (var writer = CultCacheMessagePack.Create(path, options))
                writer.Commit(batch => batch.Upsert(typeof(DeclaringDeck), new DeclaringDeck { Name = "d" }, D));

            using var cache = CultCacheMessagePack.Create(path, options);
            Rewrite(path, manifest => manifest.Records.Single().SchemaId = OldId);
            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();

            Assert.That(cache.TryCommit(batch =>
            {
                batch.Expect(D, cache.Get<DeclaringDeck>(D)!);
                batch.Upsert(typeof(DeclaringDeck), new DeclaringDeck { Name = "e" }, new CultRecordKey("e"));
            }), Is.EqualTo(CultCommitOutcome.Committed));
        }

        // A rolling deployment shares one single-file store between v1 (one member) and v2 (a second member, declaring v1's id).
        private static readonly Field RollName = new("Name", typeof(string), 0, IsName: true);
        private static readonly Type RollV1 = Emit("RollV1", "tests.compat_roll", "tests.compat_roll.v1", new[] { RollName });
        private static readonly string RollV1Id = CultDocumentRegistry.ForTypes(new[] { RollV1 }).GetRequired(RollV1).SchemaId;
        private static readonly Type RollV2 = Emit("RollV2", "tests.compat_roll", "tests.compat_roll.v2", new[] { RollName, new Field("Extra", typeof(string), 1) }, new[] { RollV1Id });
        private static readonly CultRecordKey RollK = new("k");
        private static readonly CultRecordKey RollY = new("y");

        private CultCache OpenRoll(Type type) => Open(Path.Combine(_directory, "roll.cc"), CultDocumentRegistry.ForTypes(new[] { type }));
        private string RollPath => Path.Combine(_directory, "roll.cc");
        private static string Extra(object? document) => (string?)EmittedDocumentTypes.Read(document!, "Extra") ?? "";

        // Writes k with Extra "important" as v2.
        private void WriteImportant()
        {
            using var writer = OpenRoll(RollV2);
            writer.Commit(batch => batch.Upsert(RollV2, New(RollV2, ("Name", "k"), ("Extra", "important")), RollK));
        }

        // A write by a cache of the given version, through a commit or a plain flush, that leaves the key set as it was.
        private void UnrelatedWrite(Type version, bool viaFlush)
        {
            using var cache = OpenRoll(version);
            Assert.That(EmittedDocumentTypes.Read(cache.Get(RollK)!, "Name"), Is.EqualTo("k"), $"{version.Name} reads k");
            if (viaFlush)
            {
                cache.BackingStores.Single().PushAll();
                return;
            }

            cache.Commit(batch => batch.Upsert(version, New(version, ("Name", "z")), new CultRecordKey("z")));
            cache.Commit(batch => batch.Remove(new CultRecordKey("z")));
        }

        // The derivation a v2 cache commits from the k it holds, under a condition on that k.
        private static CultCommitOutcome Derive(CultCache cache, object held, bool unchangedOnly) => cache.TryCommit(batch =>
        {
            if (unchangedOnly)
                batch.ExpectUnchanged();
            else
                batch.Expect(RollK, held);
            batch.Upsert(RollV2, New(RollV2, ("Name", "y"), ("Extra", "derived-from-" + Extra(held))), RollY);
        });

        // v1 reads v2's record through the entry that lists v1's id: an id v1 does not own. A write of another record by v1 copies k
        // exactly as v2 stored it, the member v1 lacks included, under v2's id and storedAt, so a v2 cache's condition on k still holds
        // and what it derives from that member lands.
        [Test]
        public void AnUnrelatedWriteByAnOlderVersionLeavesANewerRecordAsStored([Values] bool unchangedOnly, [Values] bool viaFlush)
        {
            WriteImportant();
            var written = Read(RollPath);

            using var newer = OpenRoll(RollV2);
            var current = newer.Get(RollK)!;
            UnrelatedWrite(RollV1, viaFlush);

            var rewritten = Read(RollPath);
            var record = rewritten.Records.Single();
            Assert.That((record.SchemaId, record.StoredAt, record.Payload), Is.EqualTo((written.Records.Single().SchemaId, written.Records.Single().StoredAt, written.Records.Single().Payload)));
            Assert.That(CultDocumentMessagePackSerialization.SerializeSnapshot(new CultPersistedStoreSnapshot { SchemaCatalog = rewritten.SchemaCatalog }),
                Is.EqualTo(CultDocumentMessagePackSerialization.SerializeSnapshot(new CultPersistedStoreSnapshot { SchemaCatalog = written.SchemaCatalog })),
                "v2's catalog entry is written as v2 wrote it");

            Assert.That(Derive(newer, current, unchangedOnly), Is.EqualTo(CultCommitOutcome.Committed));
            using var reopened = OpenRoll(RollV2);
            Assert.That(Extra(reopened.Get(RollK)), Is.EqualTo("important"));
            Assert.That(Extra(reopened.Get(RollY)), Is.EqualTo("derived-from-important"));
        }

        // v1 reads v2's k through v2's entry, which lists v1's id: the list is the writer's statement, not v1's declaration, so to v1
        // k is read-only. v1's write of k is refused typed before anything is staged, the file is left byte for byte, and a v2
        // cache's condition on k still holds.
        [Test]
        public void AnOlderVersionCannotWriteANewerRecordItReadsThroughTheNewerEntry([Values] bool commit)
        {
            WriteImportant();
            var before = File.ReadAllBytes(RollPath);
            using var newer = OpenRoll(RollV2);
            var current = newer.Get(RollK)!;

            using (var older = OpenRoll(RollV1))
            {
                var k = older.Get(RollK)!;
                Assert.That(EmittedDocumentTypes.Read(k, "Name"), Is.EqualTo("k"), "v1 reads k");
                var refusal = Assert.Throws<CultSchemaConflictException>(() =>
                {
                    if (commit)
                        older.Commit(batch => batch.Upsert(RollV1, k, RollK));
                    else
                        older.UpsertAsync(RollV1, k, RollK).GetAwaiter().GetResult();
                })!;
                Assert.That((refusal.RecordKey, refusal.SchemaId), Is.EqualTo((RollK.Value, Read(RollPath).Records.Single().SchemaId)));
                Assert.That(older.BackingStores.Single().IsDirty, Is.False, "nothing was staged");
                older.FlushAsync().GetAwaiter().GetResult();
            }

            Assert.That(File.ReadAllBytes(RollPath), Is.EqualTo(before));
            Assert.That(Derive(newer, current, unchangedOnly: false), Is.EqualTo(CultCommitOutcome.Committed));
            using var reopened = OpenRoll(RollV2);
            Assert.That(Extra(reopened.Get(RollK)), Is.EqualTo("important"));
            Assert.That(Extra(reopened.Get(RollY)), Is.EqualTo("derived-from-important"));
        }

        // A record a write copies keeps its storedAt, so a condition another cache holds on it survives an unrelated write.
        [TestCase(false)]
        [TestCase(true)]
        public void AnUnrelatedWriteKeepsTheStoredAtOfARecordItCopies(bool viaFlush)
        {
            WriteImportant();
            var written = Read(RollPath).Records.Single();
            using var held = OpenRoll(RollV2);
            var current = held.Get(RollK)!;

            UnrelatedWrite(RollV2, viaFlush);

            Assert.That(Read(RollPath).Records.Single().StoredAt, Is.EqualTo(written.StoredAt));
            Assert.That(Derive(held, current, unchangedOnly: false), Is.EqualTo(CultCommitOutcome.Committed));
        }

        // The migration CompatibleSchemaIds exists for: v2 declares v1's id and v1's class stays registered beside it, in either
        // order. A record under v1's id is v1's, its owner; one under v2's id is v2's.
        [TestCase(false)]
        [TestCase(true)]
        public void AVersionKeptBesideTheVersionDeclaringItsIdReadsItsOwnRecords(bool reversed)
        {
            using (var older = OpenRoll(RollV1))
                older.Commit(batch => batch.Upsert(RollV1, New(RollV1, ("Name", "k")), RollK));
            using (var newer = OpenRoll(RollV2))
                newer.Commit(batch =>
                {
                    batch.Expect(RollY, null);
                    batch.Upsert(RollV2, New(RollV2, ("Name", "y"), ("Extra", "e")), RollY);
                });
            Assert.That(Read(RollPath).Records.Select(record => record.SchemaId), Is.EquivalentTo(new[] { RollV1Id, CultDocumentRegistry.ForTypes(new[] { RollV2 }).GetRequired(RollV2).SchemaId }));

            using var both = Open(RollPath, CultDocumentRegistry.ForTypes(reversed ? new[] { RollV2, RollV1 } : new[] { RollV1, RollV2 }));
            Assert.That(both.Get(RollK)!.GetType(), Is.EqualTo(RollV1));
            Assert.That(both.Get(RollY)!.GetType(), Is.EqualTo(RollV2));
        }

        [Test]
        public void ADeclarationIsRegisteredWithoutItsOwnIdAndItsEntryListsTheOwnIdFirst()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(Owner) });
            var descriptor = registry.GetRequired<Owner>();

            Assert.That(descriptor.SchemaId, Is.EqualTo(OwnerId), "the pinned id is this schema's id");
            Assert.That(descriptor.CompatibleSchemaIds, Is.Empty, "a type's own id and a blank id are not declared compatible ids");
            Assert.That(descriptor.ToCatalogEntry().CompatibleSchemaIds, Is.EqualTo(new[] { OwnerId }));
        }
    }
}
