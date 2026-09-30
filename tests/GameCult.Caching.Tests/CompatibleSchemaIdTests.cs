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
        public void ARecordUnderAnOldIdNoTypeDeclaresIsNotOpenedByThatSchemaName()
        {
            var path = OldStore("undeclared.cc");

            Assert.That(() => Open(path, Bare), Throws.InstanceOf<Exception>(), "the entry's name is metadata; only a declared id resolves it");
        }

        // An unconditional commit writes the cache's whole view: the record the batch does not name is stamped with the registered
        // id too, because the cache stamped it when it loaded it.
        [Test]
        public void ACommitThatWritesTheWholeViewStampsEveryLoadedRecordWithTheRegisteredId()
        {
            var path = OldStore("whole-view.cc");
            var descriptor = Declaring.GetRequired<DeclaringDeck>();

            using (var cache = Open(path, Declaring))
                cache.Commit(batch => batch.Upsert(typeof(DeclaringDeck), new DeclaringDeck { Name = "e" }, new CultRecordKey("e")));

            var written = Read(path);
            Assert.That(written.Records.Select(record => record.SchemaId), Is.All.EqualTo(descriptor.SchemaId));
            Assert.That(written.Records.Select(record => record.Key), Is.EquivalentTo(new[] { "d", "e" }));
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
        // is unchanged. A whole-view commit then writes every record under its type's own id, and the cache holds each under that
        // id: the same conditions hold against the rewritten file.
        [Test]
        public void ConditionsOnRecordsLoadedUnderAnOldIdHoldBeforeAndAfterAWholeViewCommit()
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
            Assert.That(Read(path).Records.Select(record => record.SchemaId), Is.All.EqualTo(Declaring.GetRequired<DeclaringDeck>().SchemaId),
                "the whole-view commit rewrote every record under the registered id");
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
        // v1 reads v2's record through the entry that lists v1's id, and its whole-view commit writes it back under v1's id with
        // the storedAt it loaded and without the member v1 lacks. A v2 cache that loaded the record before that holds a condition
        // on bytes that are gone: the condition fails, and nothing derived from the lost member lands. Pulling then shows the
        // record as v1 left it, and a condition on that holds.
        [TestCase(false)]
        [TestCase(true)]
        public void AWholeViewRewriteByAnOlderVersionFailsANewerVersionsCondition(bool unchangedOnly)
        {
            var name = new Field("Name", typeof(string), 0, IsName: true);
            var v1 = Emit("RollV1", "tests.compat_roll", "tests.compat_roll.v1", new[] { name });
            var v1Id = CultDocumentRegistry.ForTypes(new[] { v1 }).GetRequired(v1).SchemaId;
            var v2 = Emit("RollV2", "tests.compat_roll", "tests.compat_roll.v2", new[] { name, new Field("Extra", typeof(string), 1) }, new[] { v1Id });
            CultCache OpenAs(Type type) => Open(Path.Combine(_directory, "roll.cc"), CultDocumentRegistry.ForTypes(new[] { type }));
            string Extra(object? document) => (string?)EmittedDocumentTypes.Read(document!, "Extra") ?? "";
            var path = Path.Combine(_directory, "roll.cc");
            var k = new CultRecordKey("k");
            var y = new CultRecordKey("y");

            using (var writer = OpenAs(v2))
                writer.Commit(batch => batch.Upsert(v2, New(v2, ("Name", "k"), ("Extra", "important")), k));
            var written = Read(path).Records.Single();

            using var newer = OpenAs(v2);
            var current = newer.Get(k)!;
            using (var older = OpenAs(v1))
            {
                Assert.That(EmittedDocumentTypes.Read(older.Get(k)!, "Name"), Is.EqualTo("k"), "v1 reads v2's record");
                older.Commit(batch => batch.Upsert(v1, New(v1, ("Name", "z")), new CultRecordKey("z")));
                older.Commit(batch => batch.Remove(new CultRecordKey("z")));
            }

            var rewritten = Read(path).Records.Single();
            Assert.That((rewritten.SchemaId, rewritten.StoredAt), Is.EqualTo((v1Id, written.StoredAt)), "v1 rewrote k under its id, keeping storedAt");
            Assert.That(rewritten.Payload, Is.Not.EqualTo(written.Payload), "and without Extra");

            void Derive(CultCacheBatch batch, object held)
            {
                if (unchangedOnly)
                    batch.ExpectUnchanged();
                else
                    batch.Expect(k, held);
                batch.Upsert(v2, New(v2, ("Name", "y"), ("Extra", "derived-from-" + Extra(held))), y);
            }

            Assert.That(newer.TryCommit(batch => Derive(batch, current)), Is.EqualTo(CultCommitOutcome.Mismatch));
            using (var reopened = OpenAs(v2))
            {
                Assert.That(reopened.Get(y), Is.Null, "nothing derived from the shed member landed");
                Assert.That(Extra(reopened.Get(k)), Is.Empty);
            }

            newer.PullAllBackingStoresAsync().GetAwaiter().GetResult();
            var pulled = newer.Get(k)!;
            Assert.That(Extra(pulled), Is.Empty, "a pull replaces the record v1 rewrote");
            Assert.That(newer.TryCommit(batch => Derive(batch, pulled)), Is.EqualTo(CultCommitOutcome.Committed));
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
