#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameCult.Caching.MessagePack;
using MessagePack;
using MessagePack.Formatters;
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

        // Slot 1 and 2 are retired gaps.
        [CultDocument("tests.slot_gap", "tests.slot_gap.v1")]
        [MessagePackObject]
        public sealed class GapDoc
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(3)] public string Tail = string.Empty;
        }

        private static readonly CultDocumentRegistry SurfaceRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(Surface) });
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
            Seed(SurfaceRegistry, typeof(Surface), () => new Surface { Id = "x" }, VectorFiles.Committed("union-parity", vector));

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

        // Every row of slot-outcomes.msgpack, from the committed files alone: the mark, Explained and IsWritable of each record of
        // extra-slot-store.cc and extra-slot-consent.cc, and a removal of the key refused exactly where IsWritable is false.
        [Test]
        public void EverySlotOutcomeRowHoldsAndARemovalIsRefusedExactlyWhereTheRecordIsNotWritable()
        {
            var reader = new MessagePackReader(VectorFiles.Committed("union-parity", "slot-outcomes.msgpack"));
            var rows = reader.ReadArrayHeader();
            Assert.That(rows, Is.EqualTo(8));
            for (var i = 0; i < rows; i++)
            {
                Assert.That(reader.ReadArrayHeader(), Is.EqualTo(9));
                var store = reader.ReadString()!;
                var key = new CultRecordKey(reader.ReadString()!);
                var writable = reader.ReadBoolean();
                bool? explained = reader.TryReadNil() ? null : reader.ReadBoolean();
                var type = reader.TryReadNil() ? null : reader.ReadString();
                var counts = new int?[3];
                for (var c = 0; c < 3; c++) counts[c] = reader.TryReadNil() ? null : reader.ReadInt32();
                var (slots, declared, occurrences) = (counts[0], counts[1], counts[2]);
                var flattenRefusedBy = reader.TryReadNil() ? null : reader.ReadString();
                var row = $"{store} {key.Value}";

                var path = Path.Combine(_directory, $"{i}-{store}");
                File.WriteAllBytes(path, VectorFiles.Committed("union-parity", store));
                var before = File.ReadAllBytes(path);
                using var cache = Open(path, UnionParityVectorTests.SlotReader(store));
                var unread = Stored(cache, key).UnreadSlots;
                if (explained == null)
                {
                    Assert.That(unread, Is.Null, row);
                }
                else
                {
                    Assert.That(unread, Is.Not.Null, row);
                    Assert.That((unread!.Explained, unread.TypeName, unread.SlotCount, unread.DeclaredCount, unread.Occurrences),
                        Is.EqualTo((explained.Value, type, slots, declared, occurrences)), row);
                }

                Assert.That(cache.IsWritable(key), Is.EqualTo(writable), row);
                Assert.That(cache.IsWritable(Unheld), Is.True, "a key no store holds is writable");
                if (flattenRefusedBy != null)
                {
                    var refusal = Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch => batch.Flatten(key)), $"{row}: flatten")!;
                    Assert.That(refusal.RecordKey, Is.EqualTo(flattenRefusedBy), $"{row}: the refusal is the base's own");
                    Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), $"{row}: a refused flatten leaves the store as it was");
                }

                if (writable)
                {
                    Assert.That(cache.Commit(batch => batch.Remove(key)), Is.True, row);
                }
                else
                {
                    var refusal = Assert.Throws<CultSchemaConflictException>(() => cache.Commit(batch => batch.Remove(key)), row)!;
                    Assert.That(refusal.UnreadSlots, Is.Not.Null, row);
                    Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), $"{row}: a refused removal leaves the store as it was");
                }
            }
        }

        // Consent reaches only the slots the catalog comparison reports: the wide id's catalog reports slot 7, so a payload carrying
        // slots 7 and 8 at depth 0 is not consented to, and a payload carrying slot 7 alone is.
        [Test]
        public void ConsentStopsAtTheSlotsTheCatalogReports()
        {
            var path = Path.Combine(_directory, "extra-slot-consent.cc");
            File.WriteAllBytes(path, VectorFiles.Committed("union-parity", "extra-slot-consent.cc"));
            var before = File.ReadAllBytes(path);
            using var cache = Open(path, UnionParityVectorTests.SlotReader("extra-slot-consent.cc"));
            var over = new CultRecordHandle<UnionParityVectorTests.SurfaceDeclaring>(new CultRecordKey("over-consent"));
            AssertWriteAgrees(cache, over.Key, () => cache.Commit(batch => batch.Upsert(new UnionParityVectorTests.SurfaceDeclaring { Id = "over" }, over)));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "the refused write leaves the store as it was");
            var consented = new CultRecordHandle<UnionParityVectorTests.SurfaceDeclaring>(new CultRecordKey("consented"));
            AssertWriteAgrees(cache, consented.Key, () => cache.Commit(batch => batch.Upsert(new UnionParityVectorTests.SurfaceDeclaring { Id = "consented" }, consented)));
            Assert.That(cache.IsWritable(consented.Key), Is.True);
        }

        // ---- carried verbatim ----

        [Test]
        public void AMarkedRecordIsCarriedByteForByteWhenAnotherRecordIsWritten()
        {
            var path = Path.Combine(_directory, "extra-slot-store.cc");
            File.WriteAllBytes(path, VectorFiles.Committed("union-parity", "extra-slot-store.cc"));
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

        // ---- declared slots are the slots the type's own formatter writes ----

        // MessagePack writes a non-public [Key] member of an AllowPrivate type: 2 slots, where a public-member count says 1.
        [MessagePackObject(AllowPrivate = true)]
        public sealed class PrivInner
        {
            [Key(0)] public string A = string.Empty;
            [Key(1)] internal string B = string.Empty;
        }

        // A [Key] getter that throws on an uninitialized instance; the parameterless constructor's instance is written instead.
        [MessagePackObject]
        public sealed class Counted
        {
            private List<string> _items = new();
            [Key(0)] public int Count { get => _items.Count; set => _items = Enumerable.Repeat(string.Empty, value).ToList(); }
        }

        // The same getter and no parameterless constructor: no instance can be written, so the guard leaves the type alone.
        [MessagePackObject]
        public sealed class Uncounted
        {
            private readonly List<string> _items;
            [SerializationConstructor] public Uncounted(int count) => _items = Enumerable.Repeat(string.Empty, count).ToList();
            [Key(0)] public int Count => _items.Count;
        }

        // A type naming its own formatter owns its layout: one [Key] slot, but the formatter writes the item count and each item.
        [MessagePackObject]
        [MessagePackFormatter(typeof(BagFormatter))]
        public sealed class Bag
        {
            [Key(0)] public List<string> Items = new();
        }

        public sealed class BagFormatter : IMessagePackFormatter<Bag?>
        {
            public void Serialize(ref MessagePackWriter writer, Bag? value, MessagePackSerializerOptions options)
            {
                if (value == null) { writer.WriteNil(); return; }
                writer.WriteArrayHeader(1 + value.Items.Count);
                writer.Write(value.Items.Count);
                foreach (var item in value.Items) writer.Write(item);
            }

            public Bag? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                if (reader.TryReadNil()) return null;
                var count = reader.ReadArrayHeader();
                reader.ReadInt32();
                var bag = new Bag();
                for (var i = 1; i < count; i++) bag.Items.Add(reader.ReadString()!);
                return bag;
            }
        }

        // A [MessagePackObject] type an assembly resolver formats as [count, items...], with no [MessagePackFormatter] attribute: the
        // resolver owns its layout.
        [MessagePackObject]
        public sealed class ResolvedBag
        {
            [Key(0)] public List<string> Items = new();
        }

        public sealed class ResolvedBagResolver : IFormatterResolver
        {
            public static readonly IFormatterResolver Instance = new ResolvedBagResolver();
            private static readonly IMessagePackFormatter<ResolvedBag?> Formatter = new ResolvedBagFormatter();

            public IMessagePackFormatter<T>? GetFormatter<T>() => typeof(T) == typeof(ResolvedBag) ? (IMessagePackFormatter<T>)Formatter : null;

            private sealed class ResolvedBagFormatter : IMessagePackFormatter<ResolvedBag?>
            {
                private static readonly BagFormatter Bag = new();

                public void Serialize(ref MessagePackWriter writer, ResolvedBag? value, MessagePackSerializerOptions options) =>
                    Bag.Serialize(ref writer, value == null ? null : new Bag { Items = value.Items }, options);

                public ResolvedBag? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
                    Bag.Deserialize(ref reader, options) is { } bag ? new ResolvedBag { Items = bag.Items } : null;
            }
        }

        // A [MessagePackObject] collection: MessagePack writes it as its items, not by [Key].
        [MessagePackObject]
        public class TagList : List<string> { }

        [CultDocument("tests.slot_measured", "tests.slot_measured.v1")]
        [MessagePackObject]
        public sealed class MeasuredDoc
        {
            [Key(0)] [CultName] public string Name = string.Empty;
            [Key(1)] public PrivInner? Private;
            [Key(2)] public Counted? Counted;
            [Key(3)] public Uncounted? Uncounted;
            [Key(4)] public Bag? Bag;
            [Key(5)] public ResolvedBag? Resolved;
            [Key(6)] public TagList? Tags;
        }

        private static readonly CultDocumentRegistry MeasuredRegistry = CultDocumentRegistry.ForTypes(new[] { typeof(MeasuredDoc) });

        private static MeasuredDoc Measured(string member) => member switch
        {
            "private" => new MeasuredDoc { Name = member, Private = new PrivInner { A = "a", B = "bee" } },
            "constructed" => new MeasuredDoc { Name = member, Counted = new Counted { Count = 2 } },
            "unmeasurable" => new MeasuredDoc { Name = member, Uncounted = new Uncounted(2) },
            "own-formatter" => new MeasuredDoc { Name = member, Bag = new Bag { Items = { "x", "y", "z" } } },
            "resolver-formatted" => new MeasuredDoc { Name = member, Resolved = new ResolvedBag { Items = { "x", "y", "z" } } },
            "collection" => new MeasuredDoc { Name = member, Tags = new TagList { "a", "b", "c" } },
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, null)
        };

        [TestCase("private", TestName = "APrivateKeyedMemberDoesNotMark")]
        [TestCase("constructed", TestName = "ATypeMeasuredThroughItsConstructorDoesNotMark")]
        [TestCase("unmeasurable", TestName = "AnUnmeasurableTypeIsNotGuarded")]
        [TestCase("own-formatter", TestName = "ATypeWithItsOwnFormatterIsNotMeasuredByKeys")]
        [TestCase("resolver-formatted", TestName = "ATypeAResolverFormatsIsNotMeasuredByKeys")]
        [TestCase("collection", TestName = "AMessagePackObjectCollectionIsNotMeasuredByKeys")]
        public void ARecordWrittenByItsOwnTypeDoesNotMark(string member)
        {
            var path = Path.Combine(_directory, "measured.cc");
            using (var cache = Open(path, MeasuredRegistry))
            {
                Assert.That(cache.Commit(batch => batch.Upsert(Measured(member), new CultRecordHandle<MeasuredDoc>(Marked))), Is.True);
            }

            using (var cache = Open(path, MeasuredRegistry))
            {
                Assert.That(Stored(cache, Marked).UnreadSlots, Is.Null, member);
                Assert.That(cache.IsWritable(Marked), Is.True, member);
                Assert.That(cache.Commit(batch => batch.Upsert(Measured(member), new CultRecordHandle<MeasuredDoc>(Marked))), Is.True, member);
                Assert.That(cache.Commit(batch => batch.Remove(Marked)), Is.True, member);
            }
        }

        // One slot past what the formatter writes still marks, with the written count as declared.
        [Test]
        public void AnExtraSlotPastTheWrittenCountStillMarks()
        {
            var privatePayload = Emit((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(5); w.Write("p");
                w.WriteArrayHeader(3); w.Write("a"); w.Write("bee"); w.Write(Extra);
                w.WriteNil(); w.WriteNil(); w.WriteNil();
            });
            var path = Seed(MeasuredRegistry, typeof(MeasuredDoc), () => new MeasuredDoc { Name = "c" }, privatePayload, "private.cc");
            using (var cache = Open(path, MeasuredRegistry))
            {
                AssertSlotRefusal(() => cache.Commit(batch => batch.Remove(Marked)), "AllowPrivate type", nameof(PrivInner), 3, 2);
                Assert.That(cache.IsWritable(Clean), Is.True);
            }

            var countedPayload = Emit((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(5); w.Write("c"); w.WriteNil();
                w.WriteArrayHeader(2); w.Write(0); w.Write(Extra);
                w.WriteNil(); w.WriteNil();
            });
            path = Seed(MeasuredRegistry, typeof(MeasuredDoc), () => new MeasuredDoc { Name = "c" }, countedPayload, "counted.cc");
            using (var cache = Open(path, MeasuredRegistry))
                AssertSlotRefusal(() => cache.Commit(batch => batch.Remove(Marked)), "measured through its constructor", nameof(Counted), 2, 1);
        }

        // ---- a flatten writes its base chain's decoded view: refused when a record on the chain is not owned ----

        private static readonly CultRecordKey Base = new("base");
        private static readonly CultRecordKey Var = new("var");
        private static readonly CultRecordKey Fresh = new("fresh");

        // A store holding plain record base and its variant var, base then given the payload.
        private string SeedVariant(byte[]? basePayload, string name)
        {
            var path = Path.Combine(_directory, name);
            using (var cache = Open(path, SurfaceRegistry))
            {
                Assert.That(cache.Commit(batch => batch.Upsert(new Surface { Id = "base" }, new CultRecordHandle<Surface>(Base))), Is.True);
                Assert.That(cache.Commit(batch => batch.UpsertVariant(Var, Base, Named(cache, Var))), Is.True);
            }

            if (basePayload == null) return path;
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            snapshot.Records.Single(record => record.Key == Base.Value).Payload = basePayload;
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            return path;
        }

        // A variant overrides its name.
        private static CultVariantOverride[] Named(CultCache cache, CultRecordKey key) => new[] { cache.Override<Surface>(nameof(Surface.Id), key.Value) };

        private static void AssertBaseRefusal(TestDelegate act, string what, bool unreadSlots)
        {
            var refusal = Assert.Throws<CultSchemaConflictException>(act, what)!;
            Assert.That(refusal.RecordKey, Is.EqualTo(Base.Value), $"{what}: the refusal is the base's own");
            Assert.That(refusal.UnreadSlots, unreadSlots ? Is.Not.Null : Is.Null, what);
        }

        [Test]
        public void AFlattenThroughABaseHeldForUnreadSlotsIsRefusedAndTheVariantStillWritesItsDelta()
        {
            var nested = VectorFiles.Committed("union-parity", "extra-slot-nested-record.msgpack");
            var path = SeedVariant(nested, "flatten.cc");
            var before = File.ReadAllBytes(path);
            using (var cache = Open(path, SurfaceRegistry))
            {
                Assert.That(cache.IsWritable(Base), Is.False);
                Assert.That(cache.IsWritable(Var), Is.True, "the variant persists only its delta");
                AssertBaseRefusal(() => cache.Commit(batch => batch.Flatten(Var)), "Commit(Flatten)", unreadSlots: true);
                AssertBaseRefusal(() => cache.FlattenAsync(Var).GetAwaiter().GetResult(), "FlattenAsync", unreadSlots: true);
                AssertBaseRefusal(() => cache.Commit(batch => { batch.UpsertVariant(Fresh, Base, Named(cache, Fresh)); batch.Flatten(Fresh); }), "a new variant flattened as it lands", unreadSlots: true);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "refused flattens leave the store as it was");
                Assert.That(cache.Commit(batch => batch.UpsertVariant(Var, Base, Named(cache, Var))), Is.True, "a variant write persists only its delta");
                Assert.That(cache.Commit(batch => batch.Remove(Var)), Is.True);
            }

            var after = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));
            Assert.That(after.Records.Single(record => record.Key == Base.Value).Payload, Is.EqualTo(nested), "the base's bytes stay");
        }

        [Test]
        public void AFlattenThroughABaseUnderAnUndeclaredDroppingIdIsRefused()
        {
            var path = Path.Combine(_directory, "drift.cc");
            using (var writer = Open(path, CultDocumentRegistry.ForTypes(new[] { typeof(WideDoc) })))
                Assert.That(writer.Commit(batch => batch.Upsert(new WideDoc { Name = "base", Extra = "kept?" }, new CultRecordHandle<WideDoc>(Base))), Is.True);

            using var reader = Open(path, CultDocumentRegistry.ForTypes(new[] { typeof(NarrowDoc) }));
            Assert.That(reader.IsWritable(Base), Is.False);
            Assert.That(reader.Commit(batch => batch.UpsertVariant(Var, Base, new[] { reader.Override<NarrowDoc>(nameof(NarrowDoc.Name), Var.Value) })), Is.True, "a variant over the unowned base persists only its delta");
            var before = File.ReadAllBytes(path);
            AssertBaseRefusal(() => reader.Commit(batch => batch.Flatten(Var)), "flatten over undeclared drift", unreadSlots: false);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(reader.Commit(batch => batch.Remove(Var)), Is.True);
        }

        // ---- IsWritable is the write's own answer ----

        // False exactly where the write throws.
        private static void AssertWriteAgrees(CultCache cache, CultRecordKey key, Action write)
        {
            var writable = cache.IsWritable(key);
            Exception? thrown = null;
            try { write(); }
            catch (InvalidOperationException exception) { thrown = exception; }
            Assert.That(thrown == null, Is.EqualTo(writable), $"IsWritable({key.Value}) is {writable}; the write {(thrown == null ? "landed" : "threw: " + thrown.Message)}");
        }

        [Test]
        public void IsWritableIsFalseInAReadOnlyStoreAndAFlattenThereIsNotRefusedForItsCleanBase()
        {
            var path = SeedVariant(null, "readonly.cc");
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = SurfaceRegistry, ReadOnly = true });
            Assert.That(cache.IsWritable(Base), Is.False);
            AssertWriteAgrees(cache, Base, () => cache.Commit(batch => batch.Upsert(new Surface { Id = "new" }, new CultRecordHandle<Surface>(Base))));
            AssertWriteAgrees(cache, Var, () => cache.Commit(batch => batch.Remove(Var)));
            var refusal = Assert.Throws<InvalidOperationException>(() => cache.Commit(batch => batch.Flatten(Var)))!;
            Assert.That(refusal, Is.Not.InstanceOf<CultSchemaConflictException>(), "nothing on the chain is lost: the refusal is the store's read-only one");
            Assert.That(refusal.Message, Does.Contain("read-only backing store"));
        }

        // A key the cache does not hold, but a store carries as a foreign record: the store's removal check refuses it.
        [Test]
        public void IsWritableIsFalseForAnUnheldKeyAStoreListsForeign()
        {
            var path = Path.Combine(_directory, "foreign.cc");
            using (var writer = Open(path, GapRegistry))
                Assert.That(writer.Commit(batch => batch.Upsert(new GapDoc { Name = "g" }, new CultRecordHandle<GapDoc>(Marked))), Is.True);

            using var cache = Open(path, SurfaceRegistry);
            Assert.That(cache.GetStored(Marked), Is.Null, "the cache does not hold a record no registered type reads");
            Assert.That(cache.IsWritable(Marked), Is.False);
            AssertWriteAgrees(cache, Marked, () => cache.Commit(batch => batch.Remove(Marked)));
            Assert.That(cache.IsWritable(Unheld), Is.True);
        }

        [Test]
        public void IsWritableIsFalseForARecordNoAttachedStoreIsHomeTo()
        {
            using var cache = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(Surface), typeof(GapDoc) }));
            Assert.That(cache.Commit(batch => batch.Upsert(new Surface { Id = "memory" }, new CultRecordHandle<Surface>(Clean))), Is.True);
            Assert.That(cache.IsWritable(Clean), Is.True, "a store-less cache writes its own records");
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(Path.Combine(_directory, "gaps.cc")), typeof(GapDoc));
            Assert.That(cache.IsWritable(Clean), Is.False);
            AssertWriteAgrees(cache, Clean, () => cache.Commit(batch => batch.Upsert(new Surface { Id = "x" }, new CultRecordHandle<Surface>(Clean))));
            AssertWriteAgrees(cache, Clean, () => cache.Commit(batch => batch.Remove(Clean)));
            Assert.That(cache.IsWritable(Unheld), Is.True, "an unheld key no store lists foreign is writable");
        }
    }
}
