#nullable enable
using System;
using System.IO;
using System.Linq;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // A document type declares the older schema ids its records may sit under (CultDocumentAttribute.CompatibleSchemaIds). The
    // registered catalog entry lists them, a cache resolves a record under one to that type and stamps it with the type's own id,
    // and a store writer never changes the identity of a record it did not receive.
    public class CompatibleSchemaIdTests
    {
        private const string OldId = "old.id";
        private const string OwnerId = "sha256:86186abf7d63d8a326c725d39a10f3a4db11b1e8698e8c0fcf6d9720031bc480";
        private const string SharedId = "shared.id";

        [CultDocument("tests.compat_deck", "tests.compat_deck.v2", CompatibleSchemaIds = new[] { OldId })]
        [MessagePackObject]
        public sealed class DeclaringDeck
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
        }

        // The same schema as DeclaringDeck (the class name is not part of a schema id), declaring nothing.
        [CultDocument("tests.compat_deck", "tests.compat_deck.v2")]
        [MessagePackObject]
        public sealed class BareDeck
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
        }

        [CultDocument("tests.compat_owner", "tests.compat_owner.v1", CompatibleSchemaIds = new[] { OwnerId })]
        [MessagePackObject]
        public sealed class Owner
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
        }

        [CultDocument("tests.compat_claimant", "tests.compat_claimant.v1", CompatibleSchemaIds = new[] { OwnerId, SharedId })]
        [MessagePackObject]
        public sealed class Claimant
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
        }

        [CultDocument("tests.compat_second_claimant", "tests.compat_second_claimant.v1", CompatibleSchemaIds = new[] { SharedId })]
        [MessagePackObject]
        public sealed class SecondClaimant
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
        }

        [CultDocument("tests.compat_empty", "tests.compat_empty.v1", CompatibleSchemaIds = new[] { " " })]
        [MessagePackObject]
        public sealed class EmptyClaim
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
        }

        private static readonly CultDocumentRegistry Declaring = CultDocumentRegistry.ForTypes(new[] { typeof(DeclaringDeck) });
        private static readonly CultDocumentRegistry Bare = CultDocumentRegistry.ForTypes(new[] { typeof(BareDeck) });
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

        // A store written by a runtime that knew the schema under an older id and another name: one entry that owns OldId, one
        // record under it. Nothing but the declaration lets this runtime's type claim it.
        private string OldStore(string name)
        {
            var path = Path.Combine(_directory, name);
            using (var cache = Open(path, Bare))
                cache.Commit(batch => batch.Upsert(typeof(BareDeck), new BareDeck { Name = "d" }, D));
            var snapshot = Read(path);
            var entry = snapshot.SchemaCatalog.Single();
            entry.SchemaId = OldId;
            entry.SchemaName = "tests.legacy_deck";
            entry.CompatibleSchemaIds = new[] { OldId };
            snapshot.Records.Single().SchemaId = OldId;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            return path;
        }

        // The same store with the entry keeping this schema's name and only listing the older id: what a store carries after a
        // writer that did not declare it, so a runtime without the declaration can still read it.
        private string OldStoreUnderTheSameName(string name)
        {
            var path = Path.Combine(_directory, name);
            using (var cache = Open(path, Bare))
                cache.Commit(batch => batch.Upsert(typeof(BareDeck), new BareDeck { Name = "d" }, D));
            var snapshot = Read(path);
            var entry = snapshot.SchemaCatalog.Single();
            entry.ContentHash = "stale";
            entry.CompatibleSchemaIds = new[] { entry.SchemaId, OldId };
            snapshot.Records.Single().SchemaId = OldId;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            return path;
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
                batch.Upsert(typeof(BareDeck), new BareDeck { Name = "e" }, new CultRecordKey("e"));
            }))!;
            Assert.That(refusal.SchemaId, Is.EqualTo(OldId));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        }

        // A record under a declared old id is the same record the cache holds under the registered id: a condition naming it holds.
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

        [Test]
        public void ADeclarationIsRegisteredWithoutItsOwnIdAndItsEntryListsTheOwnIdFirst()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(Owner) });
            var descriptor = registry.GetRequired<Owner>();

            Assert.That(descriptor.SchemaId, Is.EqualTo(OwnerId), "the pinned id is this schema's id");
            Assert.That(descriptor.CompatibleSchemaIds, Is.Empty, "a type's own id is not a declared compatible id");
            Assert.That(descriptor.ToCatalogEntry().CompatibleSchemaIds, Is.EqualTo(new[] { OwnerId }));
        }

        [Test]
        public void AnIdATypeOwnsResolvesToItEvenWhenAnEarlierTypeDeclaresItCompatible()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(Claimant), typeof(Owner) });
            var owner = registry.GetRequired<Owner>();
            var entry = owner.ToCatalogEntry();

            Assert.That(registry.CanonicalSchemaId(OwnerId), Is.EqualTo(OwnerId));
            Assert.That(registry.ResolvePersistedSchema(OwnerId, new[] { entry }), Is.SameAs(owner));
        }

        [Test]
        public void AnIdTwoTypesDeclareCompatibleResolvesToTheOneRegisteredFirst()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(Claimant), typeof(SecondClaimant) });
            var first = registry.GetRequired<Claimant>();

            Assert.That(registry.CanonicalSchemaId(SharedId), Is.EqualTo(first.SchemaId));
        }

        [Test]
        public void AnIdNoTypeClaimsIsItsOwnCanonicalId()
        {
            Assert.That(Declaring.CanonicalSchemaId("nobody.id"), Is.EqualTo("nobody.id"));
        }

        [Test]
        public void AnEmptyDeclaredIdIsRefused()
        {
            Assert.That(() => CultDocumentRegistry.ForTypes(new[] { typeof(EmptyClaim) }).GetRequired<EmptyClaim>(),
                Throws.InvalidOperationException.With.Message.Contains("empty compatible schema id"));
        }
    }
}
