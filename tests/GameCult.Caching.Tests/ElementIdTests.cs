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
            dup.Reels[0].Id = "same";
            dup.Reels[1].Id = "same";
            var refusal = Assert.Throws<InvalidOperationException>(() => cache.Commit(batch => batch.Upsert(typeof(IdDeck), dup, new CultRecordKey("dup"))))!;
            Assert.That(refusal.Message, Does.Contain("same"));
            Assert.That(cache.Get<IdDeck>(new CultRecordKey("dup")), Is.Null);

            var fine = Deck("fine");
            fine.Reels[0].Id = "same";
            fine.Reels[0].Marks[0].Id = "same";
            Assert.That(cache.Commit(batch => batch.Upsert(typeof(IdDeck), fine, new CultRecordKey("fine"))), Is.True);
        }

        [Test]
        public void APreIdStoreLoadsAndItsIdsAreMintedExactlyOnce()
        {
            var path = PathOf("old.cc");
            WritePreIdStore(path, null, ("a", "a", 2), ("b", "b", 1));
            var before = DiskRecord(path, "a").Payload;

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
