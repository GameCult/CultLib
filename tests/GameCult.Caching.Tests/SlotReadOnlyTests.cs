#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using Surface = GameCult.Caching.Tests.UnionParityVectorTests.UnionSurface;

namespace GameCult.Caching.Tests
{
    // cultlib-gaps:ruling:extra-trailing-slots and :slot-mark-consent. A record whose payload carries a slot past its reader's declared
    // slots, at any depth, is held read-only: every staged write or removal of it is refused before anything is staged. A compatible-id
    // declaration consents only to depth-0 slots the catalog comparison reports ignored under a listed compatible id.
    public class SlotReadOnlyTests
    {
        private const string Extra = "extra-slot";

        // Same members as UnionSurface under a new version, listing OldId compatible: the declaration that must not consent to a nested mark.
        private const string OldId = "tests.slot_readonly.old";

        [CultDocument("tests.union_surface", "tests.union_surface.v2", CompatibleSchemaIds = new[] { OldId })]
        [MessagePackObject]
        public sealed class SurfaceDeclaring
        {
            [Key(0)] [CultName] public string Id = string.Empty;
            [Key(1)] public int Version;
            [Key(2)] public UnionParityVectorTests.Node? Root;
            [Key(3)] public List<UnionParityVectorTests.Outcome> Outcomes = new();
            [Key(4)] public UnionParityVectorTests.Item? Part;
            [Key(5)] public UnionParityVectorTests.CraftedItem? Crafted;
            [Key(6)] public UnionParityVectorTests.Node? Missing;
        }

        [CultDocument("tests.slot_unit", "tests.slot_unit.v1")]
        [MessagePackObject]
        public sealed class UnitDoc
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public UnionParityVectorTests.UnitOutcome? Outcome;
        }

        // Slot 1 and 2 are retired gaps.
        [CultDocument("tests.slot_gap", "tests.slot_gap.v1")]
        [MessagePackObject]
        public sealed class GapDoc
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(3)] public string Tail = string.Empty;
        }

        private static readonly CultDocumentRegistry SurfaceRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(Surface) });
        private static readonly CultDocumentRegistry DeclaringRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(SurfaceDeclaring) });
        private static readonly CultDocumentRegistry UnitRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(UnitDoc) });
        private static readonly CultDocumentRegistry GapRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(GapDoc) });
        private static readonly CultRecordKey Marked = new("marked");
        private static readonly CultRecordKey Clean = new("clean");
        private static readonly CultRecordKey Unheld = new("unheld");

        private string _directory = string.Empty;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-slotro-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }

        private static CultCache Open(string path, CultDocumentRegistry registry) =>
            CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = registry });

        private static byte[] Emit(WriterAction action)
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            action(ref writer);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        private delegate void WriterAction(ref MessagePackWriter writer);

        // A store of two records of the given type, the marked one carrying the payload and the clean one an ordinary value.
        private string Seed(CultDocumentRegistry registry, Type type, Func<object> clean, byte[] markedPayload, string name = "slots.cc")
        {
            var path = Path.Combine(_directory, name);
            using (var cache = Open(path, registry))
            {
                cache.UpsertAsync(type, clean(), Marked).GetAwaiter().GetResult();
                cache.UpsertAsync(type, clean(), Clean).GetAwaiter().GetResult();
                cache.FlushAsync().GetAwaiter().GetResult();
            }

            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            snapshot.Records.Single(record => record.Key == Marked.Value).Payload = markedPayload;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            return path;
        }

        private string SeedSurface(string vector) =>
            Seed(SurfaceRegistry, typeof(Surface), () => new Surface { Id = "x" }, UnionParityVectorTests.Committed(vector));

        private static CultStoredDocument Stored(CultCache cache, CultRecordKey key) => cache.GetStored(key)!;

        // ---- a mark at every depth, with the type and both counts ----

        [TestCase("extra-slot-record.msgpack", "UnionSurface", 8, 7, 1)]
        [TestCase("extra-slot-nested-record.msgpack", nameof(UnionParityVectorTests.Composition), 3, 2, 1)]
        [TestCase("extra-slot-arm.msgpack", nameof(UnionParityVectorTests.Box), 3, 2, 1)]
        [TestCase("extra-slot-list-element.msgpack", nameof(UnionParityVectorTests.Text), 3, 2, 1)]
        [TestCase("depth-skipped-at-limit.msgpack", "UnionSurface", 8, 7, 1)]
        public void AMarkAtAnyDepthHoldsTheRecordReadOnly(string vector, string typeName, int slots, int declared, int occurrences)
        {
            var path = SeedSurface(vector);
            using var cache = Open(path, SurfaceRegistry);
            var unread = Stored(cache, Marked).UnreadSlots;
            Assert.That(unread, Is.Not.Null, vector);
            Assert.That(unread!.TypeName, Is.EqualTo(typeName), vector);
            Assert.That(unread.SlotCount, Is.EqualTo(slots), vector);
            Assert.That(unread.DeclaredCount, Is.EqualTo(declared), vector);
            Assert.That(unread.Occurrences, Is.EqualTo(occurrences), vector);
            Assert.That(unread.Explained, Is.False, "the reader's own id is not a compatible-id declaration: nothing explains the mark");
            Assert.That(cache.IsWritable(Marked), Is.False, vector);
            Assert.That(cache.Get<Surface>(Marked), Is.Not.Null, "a read-only record is still served");
            Assert.That(Stored(cache, Clean).UnreadSlots, Is.Null);
            Assert.That(cache.IsWritable(Clean), Is.True);
            Assert.That(cache.IsWritable(Unheld), Is.True, "a key no store holds is writable");
        }

        [Test]
        public void AUnitArmWithAnExtraSlotHoldsTheRecordReadOnly()
        {
            var arm = UnionParityVectorTests.Committed("extra-slot-unit-arm.msgpack");
            var payload = Emit((ref MessagePackWriter w) => { w.WriteArrayHeader(2); w.Write("u"); w.WriteRaw(arm); });
            var path = Seed(UnitRegistry, typeof(UnitDoc), () => new UnitDoc { Name = "u" }, payload);
            using var cache = Open(path, UnitRegistry);
            var unread = Stored(cache, Marked).UnreadSlots;
            Assert.That(unread, Is.Not.Null);
            Assert.That(unread!.TypeName, Is.EqualTo(nameof(UnionParityVectorTests.Denied2)));
            Assert.That(unread.SlotCount, Is.EqualTo(1));
            Assert.That(unread.DeclaredCount, Is.EqualTo(0));
            Assert.That(cache.IsWritable(Marked), Is.False);
            Assert.That(cache.IsWritable(Clean), Is.True);
        }

        // ---- every staged write and removal of a marked key is refused, nothing else is ----

        private static void AssertSlotRefusal(TestDelegate act, string what, string type, int slots, int declared)
        {
            var refusal = Assert.Throws<CultSchemaConflictException>(act, what)!;
            Assert.That(refusal.UnreadSlots, Is.Not.Null, what);
            Assert.That(refusal.RecordKey, Is.EqualTo("marked"), what);
            Assert.That(refusal.Message, Does.Contain("is read-only: " + type + " was read with " + slots + " slots where this build declares " + declared)
                .And.Contain("record marked"), what);
            Assert.That(refusal.Message, Does.Not.Contain(Extra), $"{what}: the refusal never echoes a slot value");
        }

        [Test]
        public void EveryWriteAndRemovalOfAMarkedRecordIsRefusedAndAnotherKeyStillWrites()
        {
            var path = SeedSurface("extra-slot-nested-record.msgpack");
            var before = File.ReadAllBytes(path);
            using (var cache = Open(path, SurfaceRegistry))
            {
                var composition = nameof(UnionParityVectorTests.Composition);
                AssertSlotRefusal(() => cache.Commit(batch => batch.Upsert(new Surface { Id = "new" }, new CultRecordHandle<Surface>(Marked))), "upsert", composition, 3, 2);
                AssertSlotRefusal(() => cache.Commit(batch => batch.UpsertVariant(Marked, Clean)), "variant", composition, 3, 2);
                AssertSlotRefusal(() => cache.Commit(batch => batch.Remove(Marked)), "remove", composition, 3, 2);
                AssertSlotRefusal(() => cache.Commit(batch => { batch.Upsert(new Surface { Id = "other" }, new CultRecordHandle<Surface>(Clean)); batch.Remove(Marked); }), "mixed batch", composition, 3, 2);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "refused writes leave the store as it was");
                Assert.That(cache.Commit(batch => batch.Upsert(new Surface { Id = "changed" }, new CultRecordHandle<Surface>(Clean))), Is.True);
            }

            Assert.That(CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).Records.Count, Is.EqualTo(2));
        }

        // ---- consent ----

        [Test]
        public void ADeclaredIdDoesNotConsentToAnUnexplainedMark()
        {
            // The reader's own id is declared; the mark at depth 0 is in no compatible id's comparison: still read-only.
            var path = SeedSurface("extra-slot-record.msgpack");
            using var cache = Open(path, SurfaceRegistry);
            AssertSlotRefusal(() => cache.Commit(batch => batch.Upsert(new Surface { Id = "new" }, new CultRecordHandle<Surface>(Marked))), "depth 0 under the reader's own id", "UnionSurface", 8, 7);
            AssertSlotRefusal(() => cache.Commit(batch => batch.Remove(Marked)), "removal", "UnionSurface", 8, 7);
        }

        [Test]
        public void AListedCompatibleIdDoesNotConsentToANestedMark()
        {
            // The record's stored id is one the reader lists compatible; the extra slot is nested, so the declaration does not reach it.
            var path = SeedSurface("extra-slot-nested-record.msgpack");
            Relabel(path, OldId);
            using var cache = Open(path, DeclaringRegistry);
            Assert.That(Stored(cache, Marked).StoredSchemaId, Is.EqualTo(OldId));
            Assert.That(cache.IsWritable(Marked), Is.False);
            AssertSlotRefusal(() => cache.Commit(batch => batch.Upsert(new SurfaceDeclaring { Id = "new" }, new CultRecordHandle<SurfaceDeclaring>(Marked))), "nested under a listed id", nameof(UnionParityVectorTests.Composition), 3, 2);
            Assert.That(cache.IsWritable(Clean), Is.True, "the clean record under the listed id is consented to (declared)");
            Assert.That(cache.Commit(batch => batch.Upsert(new SurfaceDeclaring { Id = "new" }, new CultRecordHandle<SurfaceDeclaring>(Clean))), Is.True);
        }

        // The record stored under a listed compatible id whose catalog entry has one more member than the reader (slot 7), the payload
        // carrying that slot at depth 0: the loss a declaration consents to. With a nested mark beside it, nothing is consented to.
        [TestCase("union-surface.msgpack", true, TestName = "ADepthZeroSlotTheCatalogReportsUnderAListedIdIsConsentedTo")]
        [TestCase("extra-slot-nested-record.msgpack", false, TestName = "ANestedMarkBesideACatalogReportedSlotIsNotConsentedTo")]
        public void ACompatibleIdConsentsOnlyToTheDepthZeroSlotTheCatalogReports(string vector, bool consented)
        {
            var original = UnionParityVectorTests.Committed(vector);
            var path = Seed(SurfaceRegistry, typeof(Surface), () => new Surface { Id = "x" }, AppendRootSlot(original, "wider writer"));
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var entry = snapshot.SchemaCatalog.Single();
            var last = entry.Members.OrderBy(member => member.Slot).Last();
            entry.Members = entry.Members.Append(new CultSchemaMemberCatalogEntry { Slot = 7, MemberName = "Wider", TypeName = last.TypeName }).ToArray();
            entry.SchemaId = OldId;
            foreach (var record in snapshot.Records) record.SchemaId = OldId;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));

            using var cache = Open(path, DeclaringRegistry);
            var unread = Stored(cache, Marked).UnreadSlots;
            Assert.That(unread, Is.Not.Null);
            Assert.That(unread!.Explained, Is.EqualTo(consented));
            Assert.That(cache.IsWritable(Marked), Is.EqualTo(consented));
            if (consented)
                Assert.That(cache.Commit(batch => batch.Upsert(new SurfaceDeclaring { Id = "new" }, new CultRecordHandle<SurfaceDeclaring>(Marked))), Is.True);
            else
                Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch => batch.Remove(Marked)));
        }

        // The payload's root array with one string slot appended.
        private static byte[] AppendRootSlot(byte[] payload, string value)
        {
            var reader = new MessagePackReader(payload);
            var count = reader.ReadArrayHeader();
            var start = (int)reader.Consumed;
            for (var i = 0; i < count; i++) reader.Skip();
            var end = (int)reader.Consumed;
            return Emit((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(count + 1);
                w.WriteRaw(payload.AsSpan(start, end - start));
                w.Write(value);
            });
        }

        private static void Relabel(string path, string id)
        {
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            snapshot.SchemaCatalog.Single().SchemaId = id;
            foreach (var record in snapshot.Records) record.SchemaId = id;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
        }

        // ---- carried verbatim ----

        [Test]
        public void AMarkedRecordIsCarriedByteForByteWhenAnotherRecordIsWritten()
        {
            var path = Path.Combine(_directory, "extra-slot-store.cc");
            File.WriteAllBytes(path, UnionParityVectorTests.Committed("extra-slot-store.cc"));
            var before = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var original = before.Records.Single(record => record.Key == Marked.Value);
            using (var cache = Open(path, SurfaceRegistry))
            {
                Assert.That(cache.IsWritable(Marked), Is.False);
                Assert.That(cache.Commit(batch => batch.Upsert(new Surface { Id = "changed" }, new CultRecordHandle<Surface>(Clean))), Is.True);
            }

            var after = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            var carried = after.Records.Single(record => record.Key == Marked.Value);
            Assert.That(carried.Payload, Is.EqualTo(original.Payload));
            Assert.That(carried.SchemaId, Is.EqualTo(original.SchemaId));
            Assert.That(carried.StoredAt, Is.EqualTo(original.StoredAt));
            Assert.That(after.SchemaCatalog.Select(CultDocumentMessagePackSerialization.SerializeCatalogEntry).Select(Convert.ToHexString),
                Is.EqualTo(before.SchemaCatalog.Select(CultDocumentMessagePackSerialization.SerializeCatalogEntry).Select(Convert.ToHexString)));
            Assert.That(after.Records.Single(record => record.Key == Clean.Value).Payload, Is.Not.EqualTo(before.Records.Single(record => record.Key == Clean.Value).Payload));
        }

        // ---- slots short of the reader's, and gaps below the highest key, do not mark ----

        [Test]
        public void MissingTrailingSlotsDefaultAndAGapSlotDoesNotMark()
        {
            var short1 = Emit((ref MessagePackWriter w) => { w.WriteArrayHeader(1); w.Write("short"); });
            var path = Seed(GapRegistry, typeof(GapDoc), () => new GapDoc { Name = "g" }, short1);
            using (var cache = Open(path, GapRegistry))
            {
                Assert.That(cache.Get<GapDoc>(Marked)!.Tail, Is.EqualTo(string.Empty), "a missing trailing slot keeps its default");
                Assert.That(Stored(cache, Marked).UnreadSlots, Is.Null);
                Assert.That(cache.IsWritable(Marked), Is.True);
                Assert.That(cache.Commit(batch => batch.Upsert(new GapDoc { Name = "short", Tail = "t" }, new CultRecordHandle<GapDoc>(Marked))), Is.True);
            }

            var gapped = Emit((ref MessagePackWriter w) => { w.WriteArrayHeader(4); w.Write("gap"); w.WriteNil(); w.Write("in a retired slot"); w.Write("tail"); });
            var gapPath = Seed(GapRegistry, typeof(GapDoc), () => new GapDoc { Name = "g" }, gapped, "gap.cc");
            using (var cache = Open(gapPath, GapRegistry))
            {
                Assert.That(Stored(cache, Marked).UnreadSlots, Is.Null, "a non-nil value in a retired gap below the highest key does not mark");
                Assert.That(cache.IsWritable(Marked), Is.True);
            }
        }

        // ---- the catalog's consent still works, and is reported as explained ----

        [CultDocument("tests.slot_wide", "tests.slot_wide.v1")]
        [MessagePackObject]
        public sealed class WideDoc
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public string Colour = string.Empty;
            [Key(2)] public string Extra = string.Empty;
        }

        [CultDocument("tests.slot_wide", "tests.slot_wide.v2")]
        [MessagePackObject]
        public sealed class NarrowDoc
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public string Colour = string.Empty;
        }

        [Test]
        public void ADepthZeroSlotTheCatalogReportsIsExplainedAndTheUndeclaredRecordStaysUnwritable([Values] bool directory)
        {
            var path = Path.Combine(_directory, directory ? "store" : "store.cc");
            CacheBackingStore Store() => directory ? new DirectoryMessagePackBackingStore(path) : new SingleFileMessagePackBackingStore(path);
            using (var writer = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(WideDoc) })))
            {
                writer.AddBackingStore(Store());
                Assert.That(writer.Commit(batch => batch.Upsert(new WideDoc { Name = "w", Extra = "kept?" }, new CultRecordHandle<WideDoc>(Marked))), Is.True);
            }

            using var reader = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(NarrowDoc) }));
            reader.AddBackingStore(Store());
            var unread = Stored(reader, Marked).UnreadSlots;
            Assert.That(unread, Is.Not.Null, "the store kind decodes through the marking path");
            Assert.That(unread!.Explained, Is.True, "depth 0 and reported by the catalog comparison");
            Assert.That(unread.TypeName, Is.EqualTo(nameof(NarrowDoc)));
            Assert.That(unread.SlotCount, Is.EqualTo(3));
            Assert.That(unread.DeclaredCount, Is.EqualTo(2));
            Assert.That(reader.IsWritable(Marked), Is.False, "the wide id is undeclared: unowned, though the mark is explained");
            Assert.Throws<CultSchemaConflictException>(() => reader.Commit(batch => batch.Remove(Marked)));
        }
    }
}
