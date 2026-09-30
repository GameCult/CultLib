#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // C2a of docs/document-variants-cut.md: every object element of a list has an id, minted by the cache.
    public class ElementIdTests
    {
        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });

        private static readonly ModuleBuilder Rejected = AssemblyBuilder
            .DefineDynamicAssembly(new AssemblyName("RejectedElementShapes"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("RejectedElementShapes");

        private string _directory = string.Empty;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-element-ids-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        private string PathOf(string name) => Path.Combine(_directory, name);

        private static CultCache Open(string path) =>
            CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });

        private static IdDeck Deck(string name, int reels = 2) => new()
        {
            Name = name,
            Reels = Enumerable.Range(0, reels).Select(_ => new IdReel { Label = "reel", Marks = { new IdMark { Text = "m" }, new IdMark { Text = "m" } } }).ToList()
        };

        private static string[] AllIds(IdDeck deck) =>
            deck.Reels.Select(reel => reel.Id)
                .Concat(deck.Reels.SelectMany(reel => reel.Marks.Select(mark => mark.Id)))
                .Concat(deck.Moves.Select(move => move.Id))
                .ToArray();

        // A store written before ids existed: the current catalog and schema id, payloads without the id slots.
        private static void WritePreIdStore(string path, string? keepKey, params (string Key, string Name, int Reels)[] records)
        {
            using (var seed = Open(path))
            {
                seed.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("seed"), new CultRecordKey("seed")));
                if (keepKey != null)
                    seed.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("kept"), new CultRecordKey(keepKey)));
            }

            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var schemaId = snapshot.Records.First().SchemaId;
            var kept = snapshot.Records.Where(record => record.Key == keepKey);
            snapshot.Records = kept.Concat(records.Select(record => new CultPersistedRecord
            {
                Key = record.Key,
                SchemaId = schemaId,
                StoredAt = DateTimeOffset.UtcNow.ToString("O"),
                Payload = MessagePackSerializer.Serialize(new OldDeck
                {
                    Name = record.Name,
                    Reels = Enumerable.Range(0, record.Reels).Select(_ => new OldReel { Label = "reel", Marks = { new OldMark { Text = "m" }, new OldMark { Text = "m" } } }).ToList()
                })
            })).ToArray();
            // What a store from before ids declared.
            snapshot.FormatVersion = CultPersistedStoreSnapshot.FormatV1;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
        }

        private static CultPersistedRecord DiskRecord(string path, string key) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).Records.Single(record => record.Key == key);

        [Test]
        public void ATypeWhoseListElementLacksAnIdIsRefusedByName()
        {
            var element = Rejected.DefineType("BareElement", TypeAttributes.Public);
            element.SetCustomAttribute(new CustomAttributeBuilder(typeof(MessagePackObjectAttribute).GetConstructor(new[] { typeof(bool) })!, new object[] { false }));
            element.DefineField("Label", typeof(string), FieldAttributes.Public)
                .SetCustomAttribute(new CustomAttributeBuilder(typeof(KeyAttribute).GetConstructor(new[] { typeof(int) })!, new object[] { 0 }));
            var elementType = element.CreateType()!;

            var document = Rejected.DefineType("BareDocument", TypeAttributes.Public);
            document.SetCustomAttribute(new CustomAttributeBuilder(typeof(CultDocumentAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!, new object[] { "shape.bare", "shape.bare.v1" }));
            document.SetCustomAttribute(new CustomAttributeBuilder(typeof(MessagePackObjectAttribute).GetConstructor(new[] { typeof(bool) })!, new object[] { false }));
            document.DefineField("Items", typeof(List<>).MakeGenericType(elementType), FieldAttributes.Public)
                .SetCustomAttribute(new CustomAttributeBuilder(typeof(KeyAttribute).GetConstructor(new[] { typeof(int) })!, new object[] { 0 }));

            var refusal = Assert.Throws<InvalidOperationException>(() => CultDocumentRegistry.ForTypes(new[] { document.CreateType()! }))!;
            Assert.That(refusal.Message, Does.Contain("BareDocument").And.Contain("BareElement").And.Contain("Items").And.Contain("[CultElementId]"));
        }

        [Test]
        public void AWriteMintsAnIdForEveryElementAndTheyRoundTrip()
        {
            var path = PathOf("deck.cc");
            var deck = Deck("d");
            deck.Moves.Add(new IdSlash { Reach = 1 });
            using (var writer = Open(path))
                writer.Commit(batch => batch.Upsert(typeof(IdDeck), deck, new CultRecordKey("d")));
            var written = AllIds(deck);
            Assert.That(written, Has.Length.EqualTo(7));
            Assert.That(written, Has.All.Matches<string>(id => Regex.IsMatch(id, "^[0-9a-f]{12}$")));

            using var reader = Open(path);
            Assert.That(AllIds(reader.Get<IdDeck>(new CultRecordKey("d"))!), Is.EqualTo(written));
        }

        [Test]
        public void TwoLookAlikeUnionElementsGetDistinctIds()
        {
            var deck = new IdDeck { Name = "u", Moves = { new IdSlash { Reach = 3 }, new IdSlash { Reach = 3 }, new IdStab { Depth = 3 } } };
            using var cache = Open(PathOf("u.cc"));
            cache.Commit(batch => batch.Upsert(typeof(IdDeck), deck, new CultRecordKey("u")));
            Assert.That(deck.Moves.Select(move => move.Id).Distinct().Count(), Is.EqualTo(3));
        }

        [Test]
        public void ADuplicateIdInOneListIsRefusedAndTheSameIdInTwoListsIsNot()
        {
            using var cache = Open(PathOf("dup.cc"));
            var dup = Deck("dup");
            dup.Reels[0].Id = "aaaaaaaaaaaa";
            dup.Reels[1].Id = "aaaaaaaaaaaa";
            var refusal = Assert.Throws<CultElementIdException>(() => cache.Commit(batch => batch.Upsert(typeof(IdDeck), dup, new CultRecordKey("dup"))))!;
            Assert.That(refusal.Message, Does.Contain("aaaaaaaaaaaa"));
            Assert.That(cache.Get<IdDeck>(new CultRecordKey("dup")), Is.Null);

            var fine = Deck("fine");
            fine.Reels[0].Id = "aaaaaaaaaaaa";
            fine.Reels[0].Marks[0].Id = "aaaaaaaaaaaa";
            Assert.That(cache.Commit(batch => batch.Upsert(typeof(IdDeck), fine, new CultRecordKey("fine"))), Is.True);
        }

        [Test]
        public void APreIdStoreLoadsAndItsIdsAreMintedExactlyOnce()
        {
            var path = PathOf("old.cc");
            WritePreIdStore(path, null, ("a", "a", 2), ("b", "b", 1));
            var before = DiskRecord(path, "a").Payload;
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v1"), "the fixture is a store from before ids");

            string[] first;
            using (var cache = Open(path))
            {
                first = AllIds(cache.Get<IdDeck>(new CultRecordKey("a"))!);
                Assert.That(first, Has.Length.EqualTo(6));
                Assert.That(first, Has.All.Not.Empty);
                Assert.That(first.Distinct().Count(), Is.EqualTo(first.Length));
                Assert.That(DiskRecord(path, "a").Payload, Is.EqualTo(before), "loading mints in memory and writes nothing");
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("other"), new CultRecordKey("other")));
                Assert.That(AllIds(MessagePackSerializer.Deserialize<IdDeck>(DiskRecord(path, "a").Payload)), Is.EqualTo(first),
                    "the store's first write persists the ids each record minted at load");
                Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v3"), "and the write declares that the store holds ids");
            }

            using (var again = Open(path))
                Assert.That(AllIds(again.Get<IdDeck>(new CultRecordKey("a"))!), Is.EqualTo(first), "a reload is identical to what was read");

            using (var writer = Open(path))
            {
                var deck = writer.Get<IdDeck>(new CultRecordKey("a"))!;
                deck.Name = "a2";
                writer.Commit(batch => batch.Upsert(typeof(IdDeck), deck, new CultRecordKey("a")));
                writer.Commit(batch => batch.Upsert(typeof(IdDeck), deck, new CultRecordKey("a")));
            }

            using var last = Open(path);
            var reread = last.Get<IdDeck>(new CultRecordKey("a"))!;
            Assert.That(reread.Name, Is.EqualTo("a2"));
            Assert.That(AllIds(reread), Is.EqualTo(first), "the ids minted at load are the ids written, and stay through repeated writes");
        }

        [Test]
        public void MintElementIdsRewritesEveryPreIdRecordAndNothingElse()
        {
            var path = PathOf("mint.cc");
            WritePreIdStore(path, "new", ("old1", "o1", 1), ("old2", "o2", 3));
            string[] ids1, ids2;
            var newRecord = DiskRecord(path, "new");

            using (var cache = Open(path))
            {
                ids1 = AllIds(cache.Get<IdDeck>(new CultRecordKey("old1"))!);
                ids2 = AllIds(cache.Get<IdDeck>(new CultRecordKey("old2"))!);
                Assert.That(cache.MintElementIds(), Is.EqualTo(2));
                Assert.That(cache.MintElementIds(), Is.EqualTo(0), "the second run finds nothing to do");
            }

            Assert.That(DiskRecord(path, "new").Payload, Is.EqualTo(newRecord.Payload));
            Assert.That(DiskRecord(path, "new").StoredAt, Is.EqualTo(newRecord.StoredAt), "a record that already had ids is not touched");
            Assert.That(AllIds(MessagePackSerializer.Deserialize<IdDeck>(DiskRecord(path, "old1").Payload)), Is.EqualTo(ids1), "the ids are on disk now");
            using var reader = Open(path);
            Assert.That(AllIds(reader.Get<IdDeck>(new CultRecordKey("old1"))!), Is.EqualTo(ids1));
            Assert.That(AllIds(reader.Get<IdDeck>(new CultRecordKey("old2"))!), Is.EqualTo(ids2));
            Assert.That(reader.MintElementIds(), Is.EqualTo(0));
        }

        // A whole-view write that persists the ids a record minted at load stores other bytes under the same id: a new store of
        // the record, so it takes a later storedAt, and the cache that wrote it holds that storedAt. A record it stores exactly as
        // the file holds it keeps its own.
        [Test]
        public void AWholeViewWriteThatPersistsLoadMintedIdsGivesTheRecordALaterStoredAt()
        {
            var path = PathOf("restamp.cc");
            WritePreIdStore(path, "kept", ("old", "o", 1));
            var old = DiskRecord(path, "old");
            var kept = DiskRecord(path, "kept");

            using var cache = Open(path);
            var current = cache.Get<IdDeck>(new CultRecordKey("old"))!;
            cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("other"), new CultRecordKey("other")));

            var written = DiskRecord(path, "old");
            Assert.That(written.SchemaId, Is.EqualTo(old.SchemaId), "the same id");
            Assert.That(written.Payload, Is.Not.EqualTo(old.Payload), "other bytes: the ids are on disk now");
            Assert.That(string.CompareOrdinal(written.StoredAt, old.StoredAt), Is.GreaterThan(0), "so a later storedAt");
            Assert.That(DiskRecord(path, "kept").StoredAt, Is.EqualTo(kept.StoredAt), "a record stored unchanged keeps its storedAt");
            Assert.That(cache.TryCommit(batch =>
            {
                batch.Expect(new CultRecordKey("old"), current);
                batch.Upsert(typeof(IdDeck), Deck("after"), new CultRecordKey("after"));
            }), Is.EqualTo(CultCommitOutcome.Committed), "the cache holds the storedAt it wrote");
        }

        [Test]
        public void TheByteCostOfAnIdIsMeasured()
        {
            var ids = new IdDeck { Name = "m" };
            var old = new OldDeck { Name = "m" };
            for (var index = 0; index < 100; index++)
            {
                ids.Reels.Add(new IdReel { Label = "reel", Id = "0123456789ab" });
                old.Reels.Add(new OldReel { Label = "reel" });
            }

            // IdDeck also carries the empty Moves list at slot 2: one byte, once.
            var perElement = (MessagePackSerializer.Serialize(ids).Length - MessagePackSerializer.Serialize(old).Length - 1) / 100.0;
            TestContext.Out.WriteLine($"element id cost: {perElement} bytes per element (12-char id in the next free slot)");
            Assert.That(perElement, Is.EqualTo(13.0), "1 byte of string header and 12 of id; the element's array header does not grow");
        }

        [Test]
        public void ARemovedPreIdRecordIsNotRewrittenByMintElementIds()
        {
            var path = PathOf("removed.cc");
            WritePreIdStore(path, null, ("a", "a", 1), ("b", "b", 1));
            using var cache = Open(path);
            // Conditional, so the file is merged onto and b's ids stay in memory only.
            cache.Commit(batch =>
            {
                batch.Expect(new CultRecordKey("elsewhere"), null);
                batch.Remove(new CultRecordKey("a"));
            });
            Assert.That(cache.MintElementIds(), Is.EqualTo(1), "only the survivor is listed; the removed record is not resurrected");
            Assert.That(cache.Get<IdDeck>(new CultRecordKey("a")), Is.Null);
        }

        [Test]
        public void ADerivedIdIsTheSourceTextNeverRandomAndItsDuplicatesAreRefused()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(SpanDoc) });
            SpanDoc Make() => new() { Name = "s", Spans = { new IdSpan { Offset = 0, Size = 4 }, new IdSpan { Offset = 262144, Size = 4 } } };
            var first = Make();
            var second = Make();
            using (var cache = CultCacheMessagePack.Create(PathOf("span1.cc"), new CultCacheOpenOptions { Registry = registry }))
                cache.Commit(batch => batch.Upsert(typeof(SpanDoc), first, new CultRecordKey("s")));
            using (var cache = CultCacheMessagePack.Create(PathOf("span2.cc"), new CultCacheOpenOptions { Registry = registry }))
                cache.Commit(batch => batch.Upsert(typeof(SpanDoc), second, new CultRecordKey("s")));
            Assert.That(first.Spans.Select(span => span.Id), Is.EqualTo(new[] { "0", "262144" }));
            Assert.That(MessagePackSerializer.Serialize(second), Is.EqualTo(MessagePackSerializer.Serialize(first)), "the same content encodes to the same bytes");

            var twice = new SpanDoc { Name = "t", Spans = { new IdSpan { Offset = 7 }, new IdSpan { Offset = 7 } } };
            using var refusing = CultCacheMessagePack.Create(PathOf("span3.cc"), new CultCacheOpenOptions { Registry = registry });
            var refusal = Assert.Throws<CultElementIdException>(() => refusing.Commit(batch => batch.Upsert(typeof(SpanDoc), twice, new CultRecordKey("t"))))!;
            Assert.That(refusal.Message, Does.Contain("'7'"));
        }

        // ---- C2a fix batch: the marker, refusals at load, override values, atomic minting ----

        private const string HexA = "aaaaaaaaaaaa";

        private static CultCache OpenWith(string path, bool directory, params Type[] types) =>
            CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = CultDocumentRegistry.ForTypes(types), UseDirectoryStore = directory });

        private static string HeaderOf(string path) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).FormatVersion;

        private static void UpsertPlain(CultCache cache, string key) =>
            cache.Commit(batch => batch.Upsert(typeof(PreCut2FixtureItem), new PreCut2FixtureItem { Name = key }, new CultRecordKey(key)));

        private static CultElementIdException Refusal(Exception error)
        {
            for (var inner = error; inner != null; inner = inner.InnerException!)
            {
                if (inner is CultElementIdException typed)
                    return typed;
            }

            throw new AssertionException("no CultElementIdException in the chain: " + error);
        }

        private static byte[] OldShapedReels(string label) =>
            MessagePackSerializer.Serialize(new List<OldReel> { new() { Label = label, Marks = { new OldMark { Text = "x" }, new OldMark { Text = "y" } } } });

        [Test]
        public void AStoreThatCanHoldElementIdsCarriesTheV3MarkerAndOneThatCannotStaysV1()
        {
            var ids = PathOf("ids.cc");
            using (var cache = OpenWith(ids, false, typeof(IdDeck)))
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("d"), new CultRecordKey("d")));
            Assert.That(HeaderOf(ids), Is.EqualTo("cultcache.store.v3"));

            var plain = PathOf("plain.cc");
            using (var cache = OpenWith(plain, false, typeof(PreCut2FixtureItem)))
                UpsertPlain(cache, "a");
            Assert.That(HeaderOf(plain), Is.EqualTo("cultcache.store.v1"));

            using var reread = OpenWith(ids, false, typeof(IdDeck));
            Assert.That(reread.Get<IdDeck>(new CultRecordKey("d")), Is.Not.Null, "a v3 store reads");
        }

        [Test]
        public void AStagedWriteThatIsFlushedCarriesTheMarkerAndPersistsLoadMintedIds()
        {
            var path = PathOf("flush.cc");
            WritePreIdStore(path, null, ("a", "a", 1));
            using (var cache = Open(path))
            {
                cache.UpsertAsync(typeof(IdDeck), Deck("staged"), new CultRecordKey("staged")).GetAwaiter().GetResult();
                cache.FlushAllBackingStores();
                Assert.That(cache.MintElementIds(), Is.EqualTo(0), "the flush wrote the whole store, a's minted ids included");
            }

            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v3"));
            Assert.That(AllIds(MessagePackSerializer.Deserialize<IdDeck>(DiskRecord(path, "a").Payload)), Has.All.Not.Empty);
        }

        [Test]
        public void AnUnconditionalWriteOfAPlainRecordKeepsTheMarkerWhileTheStoreHoldsIds()
        {
            var path = PathOf("wholeview.cc");
            using var cache = OpenWith(path, false, typeof(IdDeck), typeof(PreCut2FixtureItem));
            cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("d"), new CultRecordKey("d")));
            UpsertPlain(cache, "plain");
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v3"), "the store still holds a deck, whatever this commit wrote");
        }

        [Test]
        public void AMergeOntoAFileAnotherWriterMarkedKeepsTheMarkerEvenWhenThisWriterHoldsNoIds()
        {
            var path = PathOf("merge.cc");
            Type[] types = { typeof(IdDeck), typeof(PreCut2FixtureItem) };
            using var first = OpenWith(path, false, types);
            UpsertPlain(first, "first");
            using var second = OpenWith(path, false, types);
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v1"));
            first.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("d"), new CultRecordKey("d")));
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v3"));

            Assert.That(second.Commit(batch =>
            {
                batch.Expect(new CultRecordKey("second"), null);
                batch.Upsert(typeof(PreCut2FixtureItem), new PreCut2FixtureItem { Name = "second" }, new CultRecordKey("second"));
            }), Is.True);
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v3"), "the file holds a deck another writer put there; second holds none");
        }

        [Test]
        public void AnUnconditionalWriteOfAPlainRecordMarksAStoreWhoseLoadedRecordsMintedIds()
        {
            var path = PathOf("loaded-minted.cc");
            WritePreIdStore(path, null, ("a", "a", 1));
            using (var cache = OpenWith(path, false, typeof(IdDeck), typeof(PreCut2FixtureItem)))
            {
                Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v1"));
                UpsertPlain(cache, "p");
            }

            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v3"), "the write persisted a's minted ids");
            Assert.That(AllIds(MessagePackSerializer.Deserialize<IdDeck>(DiskRecord(path, "a").Payload)), Has.All.Not.Empty);
        }

        [Test]
        public void AMarkedFileStaysMarkedWhenACommitOnlyTouchesOtherRecords()
        {
            var path = PathOf("sticky.cc");
            using (var cache = OpenWith(path, false, typeof(IdDeck), typeof(PreCut2FixtureItem)))
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("d"), new CultRecordKey("d")));
            using (var cache = OpenWith(path, false, typeof(IdDeck), typeof(PreCut2FixtureItem)))
            {
                Assert.That(cache.Commit(batch =>
                {
                    batch.Expect(new CultRecordKey("plain"), null);
                    batch.Upsert(typeof(PreCut2FixtureItem), new PreCut2FixtureItem { Name = "plain" }, new CultRecordKey("plain"));
                }), Is.True);
            }

            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v3"), "a merge onto a marked file does not shed the marker");
        }

        [Test]
        public void ADirectoryStoreThatCanHoldElementIdsCarriesAV5ManifestAndStaysMarked()
        {
            var ids = PathOf("ids-dir.cc");
            using (var cache = OpenWith(ids, true, typeof(IdDeck), typeof(PreCut2FixtureItem)))
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("d"), new CultRecordKey("d")));
            Assert.That(HeaderOf(ids), Is.EqualTo("cultcache.store.v5.directory-content-addressed-pages"));
            using (var cache = OpenWith(ids, true, typeof(IdDeck), typeof(PreCut2FixtureItem)))
            {
                Assert.That(cache.Get<IdDeck>(new CultRecordKey("d")), Is.Not.Null, "a v5 manifest reads");
                UpsertPlain(cache, "plain");
            }

            Assert.That(HeaderOf(ids), Is.EqualTo("cultcache.store.v5.directory-content-addressed-pages"), "a rewrite that touches a plain record keeps the marker");

            var mixed = PathOf("mixed-dir.cc");
            using (var cache = OpenWith(mixed, true, typeof(IdDeck), typeof(PreCut2FixtureItem)))
            {
                cache.Commit(batch =>
                {
                    batch.Upsert(typeof(PreCut2FixtureItem), new PreCut2FixtureItem { Name = "p" }, new CultRecordKey("p"));
                    batch.Upsert(typeof(IdDeck), Deck("d"), new CultRecordKey("d"));
                });
            }

            Assert.That(HeaderOf(mixed), Is.EqualTo("cultcache.store.v5.directory-content-addressed-pages"), "one written record that can hold ids is enough");

            var plain = PathOf("plain-dir.cc");
            using (var cache = OpenWith(plain, true, typeof(PreCut2FixtureItem)))
                UpsertPlain(cache, "a");
            Assert.That(HeaderOf(plain), Is.EqualTo("cultcache.store.v4.directory-content-addressed-pages"));
        }

        [Test]
        public void AVariantStoreThatCanHoldElementIdsIsV3AndItsVariantsRead()
        {
            var path = PathOf("variant-v3.cc");
            using (var cache = OpenWith(path, false, typeof(IdDeck)))
            {
                cache.Commit(batch =>
                {
                    batch.Upsert(typeof(IdDeck), Deck("base"), new CultRecordKey("base"));
                    batch.UpsertVariant(new CultRecordKey("v"), new CultRecordKey("base"), new[] { cache.Override<IdDeck>(nameof(IdDeck.Name), "v") });
                });
            }

            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v3"));
            using var again = OpenWith(path, false, typeof(IdDeck));
            Assert.That(again.Get<IdDeck>(new CultRecordKey("v"))!.Name, Is.EqualTo("v"));
        }

        [Test]
        public void ADuplicateIdOnDiskRefusesTheLoadNamingTheRecordTheListAndTheId()
        {
            var path = PathOf("dup-disk.cc");
            WritePreIdStore(path, null, ("a", "a", 1));
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var deck = Deck("dup");
            deck.Reels[0].Id = HexA;
            deck.Reels[1].Id = HexA;
            deck.Reels[0].Marks[0].Id = HexA;
            snapshot.Records[0].Payload = MessagePackSerializer.Serialize(deck);
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));

            var refusal = Refusal(Assert.Catch(() => Open(path).Dispose())!);
            Assert.That(refusal.RecordKey, Is.EqualTo("a"));
            Assert.That(refusal.ElementId, Is.EqualTo(HexA));
            Assert.That(refusal.ListPath, Is.EqualTo("a.1"));
        }

        [Test]
        public void AnIdOfTheWrongFormatOnDiskRefusesTheLoadAndAWriteOfItIsRefused()
        {
            var path = PathOf("bad-format.cc");
            WritePreIdStore(path, null, ("a", "a", 1));
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var deck = Deck("bad");
            deck.Reels[0].Id = "t00000000001";
            snapshot.Records[0].Payload = MessagePackSerializer.Serialize(deck);
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            var refusal = Refusal(Assert.Catch(() => Open(path).Dispose())!);
            Assert.That(refusal.RecordKey, Is.EqualTo("a"));
            Assert.That(refusal.ElementId, Is.EqualTo("t00000000001"));

            using var cache = Open(PathOf("bad-write.cc"));
            var written = Deck("w");
            written.Reels[0].Marks[1].Id = "NOTHEX";
            Assert.That(Assert.Throws<CultElementIdException>(() => cache.Commit(batch => batch.Upsert(typeof(IdDeck), written, new CultRecordKey("w"))))!.ElementId, Is.EqualTo("NOTHEX"));
        }

        [Test]
        public void ABatchRefusedForADuplicateLeavesEveryDocumentItHeldUnminted()
        {
            using var cache = Open(PathOf("batch-refused.cc"));
            var fine = Deck("fine");
            var dup = Deck("dup");
            dup.Reels[0].Id = HexA;
            dup.Reels[1].Id = HexA;
            Assert.Throws<CultElementIdException>(() => cache.Commit(batch =>
            {
                batch.Upsert(typeof(IdDeck), fine, new CultRecordKey("fine"));
                batch.Upsert(typeof(IdDeck), dup, new CultRecordKey("dup"));
            }));
            Assert.That(AllIds(fine), Has.All.Empty, "the refused batch minted nothing into the other document");
            Assert.That(dup.Reels[0].Marks.Select(mark => mark.Id), Has.All.Empty);
            Assert.That(cache.Get<IdDeck>(new CultRecordKey("fine")), Is.Null);
        }

        [Test]
        public void AWriteThatDoesNotCommitGivesItsIdsBack()
        {
            using var cache = Open(PathOf("mismatch.cc"));
            cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("existing"), new CultRecordKey("existing")));
            var fresh = Deck("fresh");
            var outcome = cache.TryCommit(batch =>
            {
                batch.Expect(new CultRecordKey("existing"), null);
                batch.Upsert(typeof(IdDeck), fresh, new CultRecordKey("fresh"));
            });
            Assert.That(outcome, Is.EqualTo(CultCommitOutcome.Mismatch));
            Assert.That(AllIds(fresh), Has.All.Empty);
        }

        [Test]
        public void AnUnconditionalWritePersistsLoadMintedIdsAndTakesThemOffTheList()
        {
            var path = PathOf("unconditional.cc");
            WritePreIdStore(path, null, ("a", "a", 1), ("b", "b", 1));
            using var cache = Open(path);
            cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("other"), new CultRecordKey("other")));
            Assert.That(AllIds(MessagePackSerializer.Deserialize<IdDeck>(DiskRecord(path, "b").Payload)), Has.All.Not.Empty, "the write persisted b's ids");
            Assert.That(cache.MintElementIds(), Is.EqualTo(0), "nothing is left that exists only in memory");
        }

        [Test]
        public void AConditionalWriteThatLeavesOtherRecordsAsTheyWereLeavesThemOnTheList()
        {
            var path = PathOf("conditional.cc");
            WritePreIdStore(path, null, ("a", "a", 1), ("b", "b", 1));
            using var cache = Open(path);
            Assert.That(cache.Commit(batch =>
            {
                batch.Expect(new CultRecordKey("fresh"), null);
                batch.Upsert(typeof(IdDeck), Deck("fresh"), new CultRecordKey("fresh"));
            }), Is.True);
            Assert.That(cache.MintElementIds(), Is.EqualTo(2), "a and b were not written; their ids are still only in memory");
        }

        [Test]
        public void AnOverrideValueGetsItsIdsMintedOnWriteWhoeverBuiltIt()
        {
            var path = PathOf("override-write.cc");
            using (var cache = Open(path))
            {
                var reels = new List<IdReel> { new() { Label = "v", Marks = { new IdMark { Text = "x" } } }, new() { Label = "v" } };
                cache.Commit(batch =>
                {
                    batch.Upsert(typeof(IdDeck), Deck("base"), new CultRecordKey("base"));
                    // One built by the cache's own helper, one from bytes the id rule never saw.
                    batch.UpsertVariant(new CultRecordKey("helper"), new CultRecordKey("base"),
                        new[] { cache.Override<IdDeck>(nameof(IdDeck.Name), "helper"), cache.Override<IdDeck>(nameof(IdDeck.Reels), reels) });
                    batch.UpsertVariant(new CultRecordKey("raw"), new CultRecordKey("base"),
                        new[] { cache.Override<IdDeck>(nameof(IdDeck.Name), "raw"), CultVariantOverride.Set(1, OldShapedReels("r")) });
                });
                Assert.That(reels.Select(reel => reel.Id), Has.All.Empty, "the caller's list is not written to; ids are minted in the admitted copy");
                foreach (var key in new[] { "helper", "raw" })
                {
                    var resolved = cache.Get<IdDeck>(new CultRecordKey(key))!;
                    var ids = resolved.Reels.Select(reel => reel.Id).Concat(resolved.Reels.SelectMany(reel => reel.Marks.Select(mark => mark.Id))).ToArray();
                    Assert.That(ids, Has.All.Matches<string>(id => Regex.IsMatch(id, "^[0-9a-f]{12}$")), key);
                }
            }

            foreach (var key in new[] { "helper", "raw" })
            {
                var stored = MessagePackSerializer.Deserialize<List<IdReel>>(DiskRecord(path, key).Variant!.Overrides.Single(entry => entry.Path[0].Slot == 1).Value);
                Assert.That(stored.Select(reel => reel.Id), Has.All.Matches<string>(id => id.Length == 12), $"{key}: the persisted delta carries the ids");
                using var reader = Open(path);
                Assert.That(reader.Get<IdDeck>(new CultRecordKey(key))!.Reels.Select(reel => reel.Id), Is.EqualTo(stored.Select(reel => reel.Id)));
            }
        }

        [Test]
        public void AnOverrideValueWithADuplicateIdIsRefusedNamingTheVariant()
        {
            using var cache = Open(PathOf("override-dup.cc"));
            cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("base"), new CultRecordKey("base")));
            var dup = new List<IdReel> { new() { Id = HexA }, new() { Id = HexA } };
            var refusal = Assert.Throws<CultElementIdException>(() => cache.Commit(batch =>
                batch.UpsertVariant(new CultRecordKey("v"), new CultRecordKey("base"),
                    new[] { cache.Override<IdDeck>(nameof(IdDeck.Name), "v"), cache.Override<IdDeck>(nameof(IdDeck.Reels), dup) })))!;
            Assert.That(refusal.RecordKey, Does.StartWith("v"));
            Assert.That(refusal.ElementId, Is.EqualTo(HexA));
            Assert.That(cache.Get<IdDeck>(new CultRecordKey("v")), Is.Null);
        }

        [Test]
        public void AVariantWrittenBeforeIdsMintsItsOverrideIdsOnLoadAndMintElementIdsRewritesIt()
        {
            var path = PathOf("override-load.cc");
            using (var seed = Open(path))
            {
                seed.Commit(batch =>
                {
                    batch.Upsert(typeof(IdDeck), Deck("base"), new CultRecordKey("base"));
                    batch.UpsertVariant(new CultRecordKey("v"), new CultRecordKey("base"),
                        new[] { seed.Override<IdDeck>(nameof(IdDeck.Name), "v"), seed.Override<IdDeck>(nameof(IdDeck.Reels), new List<IdReel> { new() }) });
                });
            }

            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var record = snapshot.Records.Single(entry => entry.Key == "v");
            record.Variant = new CultVariantDelta(record.Variant!.BaseKey, record.Variant.Overrides
                .Select(entry => entry.Path[0].Slot == 1 ? CultVariantOverride.Set(1, OldShapedReels("old")) : entry).ToArray());
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            var untouched = File.ReadAllBytes(path);

            string[] first;
            using (var peek = Open(path))
                first = AllIds(peek.Get<IdDeck>(new CultRecordKey("v"))!);
            using (var cache = Open(path))
            {
                Assert.That(AllIds(cache.Get<IdDeck>(new CultRecordKey("v"))!), Is.EqualTo(first), "the ids minted at load are a function of the record, so a second load mints the same");
                Assert.That(first, Has.Length.EqualTo(3));
                Assert.That(first, Has.All.Not.Empty);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(untouched), "loading mints in memory and writes nothing");
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("other"), new CultRecordKey("other")));
                // A base edit re-resolves the variant as a dependent; it is still only in memory.
                var baseDeck = cache.Get<IdDeck>(new CultRecordKey("base"))!;
                baseDeck.Reels[0].Label = "edited";
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), baseDeck, new CultRecordKey("base")));
                Assert.That(AllIds(cache.Get<IdDeck>(new CultRecordKey("v"))!), Is.EqualTo(first), "the dependent re-resolves to the same minted ids");
                Assert.That(cache.MintElementIds(), Is.EqualTo(1), "a flush of the store persisted the delta as it was handed over, so the variant is still in memory only");
                Assert.That(cache.MintElementIds(), Is.EqualTo(0));
            }

            var persisted = MessagePackSerializer.Deserialize<List<IdReel>>(DiskRecord(path, "v").Variant!.Overrides.Single(entry => entry.Path[0].Slot == 1).Value);
            Assert.That(persisted.Select(reel => reel.Id).Concat(persisted.SelectMany(reel => reel.Marks.Select(mark => mark.Id))), Is.EqualTo(first));
            using var again = Open(path);
            Assert.That(AllIds(again.Get<IdDeck>(new CultRecordKey("v"))!), Is.EqualTo(first));
        }

        [Test]
        public void ARefusalAfterMintingLeavesEveryPlainDocumentInTheBatchUnminted()
        {
            using var cache = Open(PathOf("late-refusal.cc"));
            cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("base"), new CultRecordKey("base")));
            var plain = Deck("plain");
            var dup = new List<IdReel> { new() { Id = HexA }, new() { Id = HexA } };
            Assert.Throws<CultElementIdException>(() => cache.Commit(batch =>
            {
                batch.Upsert(typeof(IdDeck), plain, new CultRecordKey("plain"));
                batch.UpsertVariant(new CultRecordKey("v"), new CultRecordKey("base"),
                    new[] { cache.Override<IdDeck>(nameof(IdDeck.Name), "v"), cache.Override<IdDeck>(nameof(IdDeck.Reels), dup) });
            }));
            Assert.That(AllIds(plain), Has.All.Empty, "the variant's refusal came after the plain record was minted; the mint was given back");
            Assert.That(cache.Get<IdDeck>(new CultRecordKey("plain")), Is.Null);
        }

        // ---- C2a fix batch 3: mark by content, one object twice ----

        private static IdDeck EmptyDeck(string name) => new() { Name = name };

        [Test]
        public void ASingleFileStoreIsMarkedByTheIdsItHoldsNotByTheTypesItCouldHold()
        {
            var empty = PathOf("empty-deck.cc");
            using (var cache = Open(empty))
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), EmptyDeck("e"), new CultRecordKey("e")));
            Assert.That(HeaderOf(empty), Is.EqualTo("cultcache.store.v1"), "a deck with no elements holds no id");

            var flushed = PathOf("flushed.cc");
            using (var cache = Open(flushed))
            {
                cache.UpsertAsync(typeof(IdDeck), EmptyDeck("e"), new CultRecordKey("e")).GetAwaiter().GetResult();
                cache.FlushAllBackingStores();
            }

            Assert.That(HeaderOf(flushed), Is.EqualTo("cultcache.store.v1"), "the whole-store flush decides by content too");

            using (var cache = Open(flushed))
            {
                cache.UpsertAsync(typeof(IdDeck), Deck("d"), new CultRecordKey("d")).GetAwaiter().GetResult();
                cache.FlushAllBackingStores();
            }

            Assert.That(HeaderOf(flushed), Is.EqualTo("cultcache.store.v3"), "one element with an id marks the flush");

            var marked = PathOf("marked.cc");
            using (var cache = Open(marked))
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("d"), new CultRecordKey("d")));
            using (var cache = Open(marked))
            {
                Assert.That(cache.Commit(batch =>
                {
                    batch.Expect(new CultRecordKey("e"), null);
                    batch.Upsert(typeof(IdDeck), EmptyDeck("e"), new CultRecordKey("e"));
                }), Is.True);
            }

            Assert.That(HeaderOf(marked), Is.EqualTo("cultcache.store.v3"), "a file already marked on disk stays marked");
        }

        [Test]
        public void AnIdAlreadyThereMarksTheStoreAndSoDoesARecordLoadedHoldingOne()
        {
            var preset = PathOf("preset.cc");
            using (var cache = Open(preset))
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "p", Reels = { new IdReel { Id = HexA } } }, new CultRecordKey("p")));
            Assert.That(HeaderOf(preset), Is.EqualTo("cultcache.store.v3"), "the deck minted nothing, and still holds an id");

            // The deck reloads holding ids it did not mint; a whole-store flush of a store now holding only that deck and an empty one.
            using (var cache = Open(preset))
            {
                cache.UpsertAsync(typeof(IdDeck), EmptyDeck("e"), new CultRecordKey("e")).GetAwaiter().GetResult();
                cache.FlushAllBackingStores();
            }

            Assert.That(HeaderOf(preset), Is.EqualTo("cultcache.store.v3"), "a loaded record's ids count");
        }

        // A record a caller builds and hands straight to a store never passes an admission; the flag still means what it persists.
        [TestCase(false, "cultcache.store.v3")]
        [TestCase(true, "cultcache.store.v5.directory-content-addressed-pages")]
        public void ARecordPushedStraightToAStoreIsMarkedByTheIdsItHolds(bool directory, string marked)
        {
            var path = PathOf(directory ? "direct-dir.cc" : "direct.cc");
            using var cache = OpenWith(path, directory, typeof(IdDeck));
            var store = cache.BackingStores[0];
            var descriptor = Registry.GetRequired<IdDeck>();
            var withId = new IdDeck { Name = "d", Reels = { new IdReel { Id = HexA } } };

            store.Push(new CultStoredDocument(new CultRecordKey("d"), "2026-09-29T00:00:00.0000000+00:00", descriptor, withId));
            store.PushAll();
            Assert.That(HeaderOf(path), Is.EqualTo(marked));

            // The directory store writes only its dirty pages and keeps a marked manifest; the single file is whole-store.
            if (directory) return;
            store.Push(new CultStoredDocument(new CultRecordKey("d"), "2026-09-29T00:00:00.0000000+00:00", descriptor, EmptyDeck("d")));
            store.PushAll();
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v1"), "a whole-store flush decides by what the store now holds");
        }

        // Soul's P7 and P10: a store reads the header off what it writes, when it writes it. A document changed after its admission,
        // or loaded and changed in place, is marked by what it holds at the flush, in both directions.
        [TestCase(false)]
        [TestCase(true)]
        public void AFlushIsMarkedByWhatADocumentHoldsNowNotWhatItHeldWhenAdmitted(bool loaded)
        {
            var path = PathOf(loaded ? "mutated-loaded.cc" : "mutated-admitted.cc");
            var key = new CultRecordKey("d");
            using (var seed = Open(path))
                seed.Commit(batch => batch.Upsert(typeof(IdDeck), EmptyDeck("d"), key));
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v1"));

            using var cache = Open(path);
            var deck = EmptyDeck("d");
            if (loaded)
                deck = cache.Get<IdDeck>(key)!;
            else
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), deck, key));

            deck.Reels.Add(new IdReel { Id = HexA });
            cache.BackingStores[0].PushAll();
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v3"), "an id added after admission marks the flush");

            deck.Reels.Clear();
            cache.BackingStores[0].PushAll();
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v1"), "the last id taken away after admission unmarks it");
        }

        // A store with no cache has no codec to read a variant's override values with, so it cannot see whether they hold an id: the
        // variant counts as holding one. The same record read through a cache is marked by what its values hold.
        [Test]
        public void AVariantAStoreCannotReadCountsAsHoldingIds()
        {
            var path = PathOf("detached-variant.cc");
            using (var cache = Open(path))
            {
                cache.Commit(batch =>
                {
                    batch.Upsert(typeof(IdDeck), EmptyDeck("base"), new CultRecordKey("base"));
                    batch.UpsertVariant(new CultRecordKey("v"), new CultRecordKey("base"), new[] { cache.Override<IdDeck>(nameof(IdDeck.Name), "v") });
                });
            }

            using (var cache = Open(path))
                cache.BackingStores[0].PushAll();
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v2"), "read with a codec, a name override holds no id");

            var blind = new SingleFileMessagePackBackingStore(path);
            blind.AttachRegistry(Registry);
            blind.PullAll();
            blind.PushAll();
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v3"), "read without one, it cannot be seen, so it is marked");
        }

        [Test]
        public void ADirectoryStoreIsMarkedByTheIdsItWritesAndAnEmptyDeckDoesNotMarkIt()
        {
            var empty = PathOf("empty-dir.cc");
            using (var cache = OpenWith(empty, true, typeof(IdDeck)))
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), EmptyDeck("e"), new CultRecordKey("e")));
            Assert.That(HeaderOf(empty), Is.EqualTo("cultcache.store.v4.directory-content-addressed-pages"));

            using (var cache = OpenWith(empty, true, typeof(IdDeck)))
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("d"), new CultRecordKey("d")));
            Assert.That(HeaderOf(empty), Is.EqualTo("cultcache.store.v5.directory-content-addressed-pages"));
        }

        [Test]
        public void AVariantIsMarkedByItsOverrideIdsAndOtherwiseIsV2()
        {
            var plain = PathOf("variant-plain.cc");
            using (var cache = Open(plain))
            {
                cache.Commit(batch =>
                {
                    batch.Upsert(typeof(IdDeck), EmptyDeck("base"), new CultRecordKey("base"));
                    batch.UpsertVariant(new CultRecordKey("v"), new CultRecordKey("base"), new[] { cache.Override<IdDeck>(nameof(IdDeck.Name), "v") });
                });
            }

            Assert.That(HeaderOf(plain), Is.EqualTo("cultcache.store.v2"));

            var withIds = PathOf("variant-ids.cc");
            using (var cache = Open(withIds))
            {
                cache.Commit(batch =>
                {
                    batch.Upsert(typeof(IdDeck), EmptyDeck("base"), new CultRecordKey("base"));
                    batch.UpsertVariant(new CultRecordKey("v"), new CultRecordKey("base"),
                        new[] { cache.Override<IdDeck>(nameof(IdDeck.Name), "v"), cache.Override<IdDeck>(nameof(IdDeck.Reels), new List<IdReel> { new() { Label = "r" } }) });
                });
            }

            Assert.That(HeaderOf(withIds), Is.EqualTo("cultcache.store.v3"));

            // Reloaded, the variant is the record the store keeps: a whole-store flush beside an empty deck still marks the file.
            using (var cache = Open(withIds))
            {
                cache.UpsertAsync(typeof(IdDeck), EmptyDeck("e"), new CultRecordKey("e")).GetAwaiter().GetResult();
                cache.FlushAllBackingStores();
            }

            Assert.That(HeaderOf(withIds), Is.EqualTo("cultcache.store.v3"));

            // Flattened, the variant is a plain record holding the ids its override brought; the base holds none.
            using (var cache = Open(withIds))
            {
                cache.FlattenAsync(new CultRecordKey("v")).GetAwaiter().GetResult();
                cache.Remove(new CultRecordKey("e"));
                cache.FlushAllBackingStores();
            }

            Assert.That(HeaderOf(withIds), Is.EqualTo("cultcache.store.v3"));
        }

        // Soul's probe: the base gains ids in the batch that flattens the variant, then leaves. The flattened record is the
        // document it stores, so its ids mark the file whatever the base did before or after.
        [TestCase(false)]
        [TestCase(true)]
        public void AFlattenedVariantIsMarkedByTheDocumentItStoresNotByTheOneItWasPlannedAgainst(bool viaFlush)
        {
            var path = PathOf(viaFlush ? "flatten-flush.cc" : "flatten-commit.cc");
            using (var cache = Open(path))
            {
                cache.Commit(batch =>
                {
                    batch.Upsert(typeof(IdDeck), EmptyDeck("base"), new CultRecordKey("base"));
                    batch.UpsertVariant(new CultRecordKey("v"), new CultRecordKey("base"), new[] { cache.Override<IdDeck>(nameof(IdDeck.Name), "v") });
                });
                Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v2"));

                cache.Commit(batch =>
                {
                    batch.Upsert(typeof(IdDeck), Deck("base", reels: 1), new CultRecordKey("base"));
                    batch.Flatten(new CultRecordKey("v"));
                });
                Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v3"));

                if (viaFlush)
                {
                    cache.Remove(new CultRecordKey("base"));
                    cache.FlushAllBackingStores();
                }
                else
                {
                    Assert.That(cache.Commit(batch => batch.Remove(new CultRecordKey("base"))), Is.True);
                }
            }

            var flattenedIds = AllIds(MessagePackSerializer.Deserialize<IdDeck>(DiskRecord(path, "v").Payload));
            Assert.That(flattenedIds, Is.Not.Empty, "the flattened record holds the base's ids");
            Assert.That(flattenedIds, Has.All.Not.Empty);
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v3"), "the file holds ids, so it is marked");
        }

        // A conditional commit lands onto the file as it is: it sees only its own batch, so ids only this cache minted (they are on no
        // disk) do not mark the file.
        [Test]
        public void AConditionalCommitOntoLoadMintedIdsDoesNotMarkTheFile()
        {
            Type[] types = { typeof(IdDeck), typeof(PreCut2FixtureItem) };
            var preId = PathOf("conditional-minted.cc");
            WritePreIdStore(preId, null, ("a", "a", 1));
            using (var cache = OpenWith(preId, false, types))
            {
                Assert.That(cache.Commit(batch =>
                {
                    batch.Expect(new CultRecordKey("plain"), null);
                    batch.Upsert(typeof(PreCut2FixtureItem), new PreCut2FixtureItem { Name = "plain" }, new CultRecordKey("plain"));
                }), Is.True);
            }

            Assert.That(HeaderOf(preId), Is.EqualTo("cultcache.store.v1"), "a's ids exist only in memory; the file holds none");
        }

        // The last id leaves the store in this commit: an unconditional commit writes the store it will hold, not the one it held.
        [Test]
        public void AnUnconditionalCommitThatRemovesOrReplacesTheLastIdWritesV1()
        {
            var removed = PathOf("last-removed.cc");
            using (var cache = Open(removed))
            {
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("d"), new CultRecordKey("d")));
                Assert.That(HeaderOf(removed), Is.EqualTo("cultcache.store.v3"));
                cache.Commit(batch => batch.Remove(new CultRecordKey("d")));
            }

            Assert.That(HeaderOf(removed), Is.EqualTo("cultcache.store.v1"));

            var replaced = PathOf("last-replaced.cc");
            using (var cache = Open(replaced))
            {
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("d"), new CultRecordKey("d")));
                cache.Commit(batch => batch.Upsert(typeof(IdDeck), EmptyDeck("d"), new CultRecordKey("d")));
            }

            Assert.That(HeaderOf(replaced), Is.EqualTo("cultcache.store.v1"));
        }

        // Soul's probe: a C1 v2 store whose variant override holds no ids. Loading mints them in memory; the store keeps the delta
        // it read, so the file holds none and no write of a plain record beside it may mark the file.
        private static void WriteV2StoreWithAnIdLessVariant(string path)
        {
            using (var seed = OpenWith(path, false, typeof(IdDeck), typeof(PreCut2FixtureItem)))
            {
                seed.Commit(batch =>
                {
                    batch.Upsert(typeof(IdDeck), EmptyDeck("base"), new CultRecordKey("base"));
                    batch.UpsertVariant(new CultRecordKey("v"), new CultRecordKey("base"),
                        new[] { seed.Override<IdDeck>(nameof(IdDeck.Name), "v"), seed.Override<IdDeck>(nameof(IdDeck.Reels), new List<IdReel> { new() }) });
                });
            }

            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var record = snapshot.Records.Single(entry => entry.Key == "v");
            record.Variant = new CultVariantDelta(record.Variant!.BaseKey, record.Variant.Overrides
                .Select(entry => entry.Path[0].Slot == 1 ? CultVariantOverride.Set(1, OldShapedReels("old")) : entry).ToArray());
            snapshot.FormatVersion = CultPersistedStoreSnapshot.FormatV2;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
        }

        [TestCase("unconditional")]
        [TestCase("conditional")]
        [TestCase("flush")]
        public void AVariantWhoseIdsExistOnlyInMemoryDoesNotMarkTheFileAPlainWriteLandsIn(string write)
        {
            var path = PathOf("variant-in-memory-" + write + ".cc");
            WriteV2StoreWithAnIdLessVariant(path);
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v2"));

            using (var cache = OpenWith(path, false, typeof(IdDeck), typeof(PreCut2FixtureItem)))
            {
                Assert.That(AllIds(cache.Get<IdDeck>(new CultRecordKey("v"))!), Has.All.Not.Empty, "the variant holds minted ids in memory");
                var key = new CultRecordKey("p");
                switch (write)
                {
                    case "unconditional":
                        UpsertPlain(cache, "p");
                        break;
                    case "conditional":
                        Assert.That(cache.Commit(batch =>
                        {
                            batch.Expect(key, null);
                            batch.Upsert(typeof(PreCut2FixtureItem), new PreCut2FixtureItem { Name = "p" }, key);
                        }), Is.True);
                        break;
                    default:
                        cache.UpsertAsync(typeof(PreCut2FixtureItem), new PreCut2FixtureItem { Name = "p" }, key).GetAwaiter().GetResult();
                        cache.FlushAllBackingStores();
                        break;
                }
            }

            Assert.That(DiskRecord(path, "p"), Is.Not.Null);
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v2"), "the file holds no id, whatever this cache minted in memory");
        }

        // Soul's A/B probe: a conditional commit lands onto the file as it is, so a stale view of what the file once held does not
        // decide the header. A holds a deck with ids; B removes it; A commits a plain record conditionally.
        [Test]
        public void AConditionalCommitIsNotMarkedByIdsItsStaleViewHoldsAndTheFileNoLongerDoes()
        {
            var path = PathOf("stale-view.cc");
            Type[] types = { typeof(IdDeck), typeof(PreCut2FixtureItem) };
            using var a = OpenWith(path, false, types);
            a.Commit(batch => batch.Upsert(typeof(IdDeck), Deck("d"), new CultRecordKey("d")));
            using (var b = OpenWith(path, false, types))
                b.Commit(batch => batch.Remove(new CultRecordKey("d")));
            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v1"), "B's whole-store commit left no id");
            Assert.That(a.Get<IdDeck>(new CultRecordKey("d")), Is.Not.Null, "A has not pulled: its view is stale");

            Assert.That(a.Commit(batch =>
            {
                batch.Expect(new CultRecordKey("plain"), null);
                batch.Upsert(typeof(PreCut2FixtureItem), new PreCut2FixtureItem { Name = "plain" }, new CultRecordKey("plain"));
            }), Is.True);

            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v1"), "the merged file holds no id");
        }

        // Any override holding an id marks the variant, wherever it sits among the overrides.
        [Test]
        public void AVariantWhoseIdBearingOverrideIsNotTheLastIsStillMarked()
        {
            var path = PathOf("variant-order.cc");
            using (var cache = Open(path))
            {
                cache.Commit(batch =>
                {
                    batch.Upsert(typeof(IdDeck), EmptyDeck("base"), new CultRecordKey("base"));
                    batch.UpsertVariant(new CultRecordKey("v"), new CultRecordKey("base"),
                        new[] { cache.Override<IdDeck>(nameof(IdDeck.Reels), new List<IdReel> { new() { Label = "r" } }), cache.Override<IdDeck>(nameof(IdDeck.Name), "v") });
                });
            }

            Assert.That(HeaderOf(path), Is.EqualTo("cultcache.store.v3"));
        }

        [Test]
        public void AnElementObjectTwiceInOneListIsRefusedAtWriteAndNothingIsPersisted()
        {
            var path = PathOf("shared.cc");
            using (var cache = Open(path))
            {
                var shared = new IdMark { Text = "s" };
                var deck = new IdDeck { Name = "shared", Reels = { new IdReel { Label = "r", Marks = { shared, shared } } } };
                var refusal = Assert.Throws<CultElementIdException>(() => cache.Commit(batch => batch.Upsert(typeof(IdDeck), deck, new CultRecordKey("shared"))))!;
                Assert.That(refusal.Message, Does.Contain("shared").And.Contain("twice"));
                Assert.That(refusal.RecordKey, Is.EqualTo("shared"));
                Assert.That(shared.Id, Is.Empty, "the refusal minted nothing");
                Assert.That(cache.Get<IdDeck>(new CultRecordKey("shared")), Is.Null);
            }

            Assert.That(File.Exists(path), Is.False, "nothing was persisted");
        }

        [Test]
        public void ADerivedIdWhoseSourceIsEmptyIsRefusedAtWriteNamingTheSourceMember()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(LabelDoc) });
            using var cache = CultCacheMessagePack.Create(PathOf("label.cc"), new CultCacheOpenOptions { Registry = registry });
            var doc = new LabelDoc { Name = "l", Labels = { new IdLabel { Label = "ok" }, new IdLabel { Label = null } } };
            var refusal = Assert.Throws<CultElementIdException>(() => cache.Commit(batch => batch.Upsert(typeof(LabelDoc), doc, new CultRecordKey("l"))))!;
            Assert.That(refusal.Member, Is.EqualTo("Label"));
            Assert.That(refusal.RecordKey, Is.EqualTo("l"));
            Assert.That(cache.Get<LabelDoc>(new CultRecordKey("l")), Is.Null);
        }

        [CultDocument("tests.element_id_label_doc", "tests.element_id_label_doc.v1")]
        [MessagePackObject]
        public sealed class LabelDoc
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
            [Key(1)] public List<IdLabel> Labels { get; set; } = new();
        }

        [MessagePackObject]
        public sealed class IdLabel
        {
            [Key(0)] public string? Label { get; set; }
            [Key(1)] [CultElementId(nameof(Label))] public string Id { get; set; } = "";
        }

        [CultDocument("tests.element_id_span_doc", "tests.element_id_span_doc.v1")]
        [MessagePackObject]
        public sealed class SpanDoc
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
            [Key(1)] public List<IdSpan> Spans { get; set; } = new();
        }

        [MessagePackObject]
        public sealed class IdSpan
        {
            [Key(0)] public long Offset { get; set; }
            [Key(1)] public int Size { get; set; }
            [Key(2)] [CultElementId(nameof(Offset))] public string Id { get; set; } = "";
        }

        [MessagePackObject]
        public sealed class IdMark
        {
            [Key(0)] public string Text { get; set; } = "";
            [Key(1)] [CultElementId] public string Id { get; set; } = "";
        }

        [MessagePackObject]
        public sealed class IdReel
        {
            [Key(0)] public string Label { get; set; } = "";
            [Key(1)] public List<IdMark> Marks { get; set; } = new();
            [Key(2)] [CultElementId] public string Id { get; set; } = "";
        }

        [MessagePackObject]
        [Union(0, typeof(IdSlash))]
        [Union(1, typeof(IdStab))]
        public abstract class IdMove
        {
            [Key(0)] [CultElementId] public string Id { get; set; } = "";
        }

        [MessagePackObject]
        public sealed class IdSlash : IdMove
        {
            [Key(1)] public int Reach { get; set; }
        }

        [MessagePackObject]
        public sealed class IdStab : IdMove
        {
            [Key(1)] public int Depth { get; set; }
        }

        [CultDocument("tests.element_id_deck", "tests.element_id_deck.v1")]
        [MessagePackObject]
        public sealed class IdDeck
        {
            [Key(0)] [CultName] public string Name { get; set; } = "";
            [Key(1)] public List<IdReel> Reels { get; set; } = new();
            [Key(2)] public List<IdMove> Moves { get; set; } = new();
        }

        // The shapes before ids: same slots, no id member. Not documents, so never registered.
        [MessagePackObject]
        public sealed class OldMark
        {
            [Key(0)] public string Text { get; set; } = "";
        }

        [MessagePackObject]
        public sealed class OldReel
        {
            [Key(0)] public string Label { get; set; } = "";
            [Key(1)] public List<OldMark> Marks { get; set; } = new();
        }

        [MessagePackObject]
        public sealed class OldDeck
        {
            [Key(0)] public string Name { get; set; } = "";
            [Key(1)] public List<OldReel> Reels { get; set; } = new();
        }
    }
}
