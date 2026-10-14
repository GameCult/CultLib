#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using MessagePack.Formatters;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // The contract is the Unions section of src/GameCult.Caching/Contracts/cultcache-schema-compatibility.md: an arm is
    // [key, armSlots], keys are the identity and never reused, an unknown arm is refused on read, an unnamed subtype on write.
    // Expected bytes are written by hand with MessagePackWriter, not captured from the code under test.
    public class UnionArmGuardTests
    {
        private const string Canary = "CANARY-9d1e47";

        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(GuardDoc), typeof(ShapeDoc) });

        private delegate void Emit(ref MessagePackWriter writer);

        private static byte[] Raw(Emit emit)
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            emit(ref writer);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        private static readonly Emit Nil = (ref MessagePackWriter w) => w.WriteNil();

        // Leaf: slots [id, content, nil (retired 2), weight].
        private static Emit LeafArm(int key, string id, string content, int weight) => (ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(2); w.Write(key);
            w.WriteArrayHeader(4); w.Write(id); w.Write(content); w.WriteNil(); w.Write(weight);
        };

        // Unit8: slots [id].
        private static Emit UnitArm(int key) => (ref MessagePackWriter w) => { w.WriteArrayHeader(2); w.Write(key); w.WriteArrayHeader(1); w.Write("000000000007"); };

        // Branch: slots [children, next, id].
        private static Emit BranchArm(int key, Emit next, params Emit[] children) => (ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(2); w.Write(key);
            w.WriteArrayHeader(3);
            w.WriteArrayHeader(children.Length);
            foreach (var child in children) child(ref w);
            next(ref w);
            w.Write("000000000008");
        };

        private static Emit List(params Emit[] items) => (ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(items.Length);
            foreach (var item in items) item(ref w);
        };

        // InnerA / InnerB: slots [n]. Inner and Outer both list them, under their own keys.
        private static Emit InnerArm(int key, int n) => (ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(2); w.Write(key);
            w.WriteArrayHeader(1); w.Write(n);
        };

        // GuardDoc: slots [id, root, items, crafted, any].
        private static byte[] Doc(Emit root, Emit? items = null, Emit? crafted = null, Emit? any = null) => Raw((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(5);
            w.Write("d1");
            root(ref w);
            (items ?? List())(ref w);
            (crafted ?? Nil)(ref w);
            (any ?? Nil)(ref w);
        });

        private static Exception Decode(byte[] payload)
        {
            try
            {
                CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(GuardDoc), payload, Registry);
            }
            catch (Exception exception)
            {
                return exception;
            }

            Assert.Fail("The payload was accepted.");
            return null!;
        }

        private static string Chain(Exception exception)
        {
            var parts = new List<string>();
            for (var cause = exception; cause != null; cause = cause.InnerException) parts.Add(cause.Message);
            return string.Join(" <- ", parts);
        }

        private static void AssertRefused(byte[] payload, string union, int key, string declared)
        {
            var message = Chain(Decode(payload));
            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain(union));
                Assert.That(message, Does.Contain($"key {key} "));
                Assert.That(message, Does.Contain($"declared keys: {declared}."));
                Assert.That(message, Does.Not.Contain(Canary), "a refusal never echoes the payload");
            });
        }

        private static readonly string NodeName = typeof(GuardNode).FullName!;

        // Keys the union does not declare: below the first, in a retired gap, and one past the last.
        [TestCase(0)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(7)]
        [TestCase(10)]
        public void UnknownArmInFieldRefused(int key) =>
            AssertRefused(Doc(LeafArm(key, "000000000005", Canary, 7)), NodeName, key, "3, 5, 8, 9");

        [TestCase(0)]
        [TestCase(4)]
        [TestCase(10)]
        public void UnknownArmInListRefused(int key) =>
            AssertRefused(Doc(LeafArm(3, "000000000005", "ok", 1), List(UnitArm(8), LeafArm(key, "000000000004", Canary, 7))), NodeName, key, "3, 5, 8, 9");

        // Inside an arm's own list, and inside an arm's own field: the union nested under a union.
        [TestCase(4)]
        [TestCase(10)]
        public void UnknownArmNestedInArmRefused(int key)
        {
            AssertRefused(Doc(BranchArm(5, Nil, UnitArm(8), LeafArm(key, "000000000006", Canary, 7))), NodeName, key, "3, 5, 8, 9");
            AssertRefused(Doc(BranchArm(5, LeafArm(key, "000000000003", Canary, 7))), NodeName, key, "3, 5, 8, 9");
            AssertRefused(Doc(BranchArm(5, Nil, BranchArm(5, Nil, BranchArm(5, Nil, LeafArm(key, "000000000002", Canary, 7))))), NodeName, key, "3, 5, 8, 9");
        }

        // A union whose arms are also a union has its own key set: 7 is Outer's (OuterOnly) and not Inner's.
        [Test]
        public void InnerUnionUsesItsOwnKeys()
        {
            var innerName = typeof(Inner).FullName!;
            var outerName = typeof(Outer).FullName!;
            AssertRefused(Doc(Nil, crafted: InnerArm(7, 1)), innerName, 7, "0, 1");
            AssertRefused(Doc(Nil, crafted: InnerArm(2, 1)), innerName, 2, "0, 1");

            var accepted = (GuardDoc)CultDocumentMessagePackSerialization.DeserializeUntyped(
                typeof(GuardDoc), Doc(Nil, crafted: InnerArm(1, 5), any: InnerArm(7, 3)), Registry);
            Assert.That(accepted.Crafted, Is.TypeOf<InnerB>());
            Assert.That(accepted.Any, Is.TypeOf<OuterOnly>());
            AssertRefused(Doc(Nil, any: InnerArm(3, 1)), outerName, 3, "0, 1, 2, 7");
        }

        [Test]
        public void ArmOfAnotherShapeIsTheInnerFormattersToRefuse()
        {
            // Not a 2-array: the guard decides keys only, so MessagePack keeps its own refusals and still never echoes.
            var notArray = Doc((ref MessagePackWriter w) => w.Write(Canary));
            var arity = Doc((ref MessagePackWriter w) => { w.WriteArrayHeader(3); w.Write(3); w.WriteNil(); w.WriteNil(); });
            Assert.That(Chain(Decode(notArray)), Does.Not.Contain(Canary));
            Assert.That(Chain(Decode(arity)), Does.Contain("Invalid Union data"));
        }

        [Test]
        public void UnnamedSubtypeWriteRefused()
        {
            Assert.Multiple(() =>
            {
                foreach (var doc in new[]
                {
                    new GuardDoc { Root = new Rogue() },
                    new GuardDoc { Items = { new Unit8 { Id = "000000000007" }, new Rogue() } },
                    new GuardDoc { Root = new Branch { Id = "000000000008", Children = { new Rogue() } } },
                    new GuardDoc { Root = new Branch { Id = "000000000008", Next = new Rogue() } },
                    new GuardDoc { Root = new DerivedLeaf() },
                    new GuardDoc { Crafted = new Unlisted() },
                })
                {
                    var message = Chain(Assert.Catch<MessagePackSerializationException>(
                        () => CultDocumentMessagePackSerialization.SerializeUntyped(doc, typeof(GuardDoc), Registry))!);
                    Assert.That(message, Does.Contain("is not an arm of union"));
                }
            });
        }

        [Test]
        public void NullUnionRoundTrips()
        {
            var bytes = Doc(Nil, List(Nil, UnitArm(8), Nil));
            var decoded = (GuardDoc)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(GuardDoc), bytes, Registry);
            Assert.That(decoded.Root, Is.Null);
            Assert.That(decoded.Items, Has.Count.EqualTo(3));
            Assert.That(decoded.Items[0], Is.Null);
            Assert.That(decoded.Items[2], Is.Null);
            Assert.That(CultDocumentMessagePackSerialization.SerializeUntyped(decoded, typeof(GuardDoc), Registry), Is.EqualTo(bytes));
        }

        private static GuardDoc KnownArms() => new()
        {
            Id = "d1",
            Root = new Branch
            {
                Id = "000000000008",
                Children =
                {
                    new Leaf { Id = "000000000001", Content = "x", Weight = 1 },
                    new Unit8 { Id = "000000000007" },
                    new Branch { Id = "000000000008", Children = { new Branch { Id = "000000000008", Children = { new Leaf { Id = "000000000002", Content = "y", Weight = 2 } } } } }
                },
                Next = new Leaf { Id = "000000000003", Content = "z", Weight = 3 }
            },
            Items = { new Unit8 { Id = "000000000007" }, new Leaf { Id = "000000000004", Content = "w", Weight = 4 }, null! },
            Crafted = new InnerB { N = 9 },
            Any = new InnerA { N = 8 }
        };

        private static byte[] KnownArmBytes() => Doc(
            BranchArm(5, LeafArm(3, "000000000003", "z", 3),
                LeafArm(3, "000000000001", "x", 1),
                UnitArm(8),
                BranchArm(5, Nil, BranchArm(5, Nil, LeafArm(3, "000000000002", "y", 2)))),
            List(UnitArm(8), LeafArm(3, "000000000004", "w", 4), Nil),
            InnerArm(1, 9),
            InnerArm(1, 8));

        // Non-zero-start keys with retired gaps, a unit arm, a union inside an arm four deep: the reference shape, unchanged.
        [Test]
        public void KnownArmsRoundTripByteIdentical()
        {
            var expected = KnownArmBytes();
            var written = CultDocumentMessagePackSerialization.SerializeUntyped(KnownArms(), typeof(GuardDoc), Registry);
            Assert.That(Convert.ToHexString(written), Is.EqualTo(Convert.ToHexString(expected)));
            Assert.That(Convert.ToHexString(MessagePackSerializer.Serialize(KnownArms(), MessagePackSerializerOptions.Standard)), Is.EqualTo(Convert.ToHexString(expected)),
                "MessagePack alone writes the same bytes");

            var decoded = CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(GuardDoc), expected, Registry);
            Assert.That(CultDocumentMessagePackSerialization.SerializeUntyped(decoded, typeof(GuardDoc), Registry), Is.EqualTo(expected));
        }

        [Test]
        public void ConsumerResolverStillEncodesArms()
        {
            var bytes = CultDocumentMessagePackSerialization.SerializeUntyped(new GuardDoc { Id = "d1", Root = new StubArm() }, typeof(GuardDoc), Registry);
            Assert.That(Convert.ToHexString(bytes), Is.EqualTo(Convert.ToHexString(Doc((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(2); w.Write(9); w.WriteArrayHeader(1); w.Write("via-resolver");
            }))));
            var back = (GuardDoc)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(GuardDoc), bytes, Registry);
            Assert.That(back.Root, Is.TypeOf<StubArm>());
        }

        // The guard wraps the composite, so the composite's own order is still the contract: a consumer resolver outranks the
        // document resolver (CultRecordRef) and MessagePack's own object formatter, here for a type it also knows.
        [Test]
        public void ConsumerResolversOutrankTheRestOfTheComposite()
        {
            var options = CultDocumentMessagePackSerialization.OptionsFor(typeof(GuardDoc).Assembly);
            Assert.That(MessagePackSerializer.Serialize(new Overridden { N = 1 }, options), Is.EqualTo(Raw((ref MessagePackWriter w) => w.Write("consumer"))));
            Assert.That(MessagePackSerializer.Serialize(new CultRecordRef<GuardDoc>(new CultRecordKey("k")), options), Is.EqualTo(Raw((ref MessagePackWriter w) => w.Write("consumer-ref"))));
        }

        // A consumer resolver may format the union type itself. The guard wraps whatever the rest of the composite hands out,
        // so that formatter is used and the key check still runs ahead of it.
        [Test]
        public void ConsumerFormatterForTheUnionItselfIsWrappedNotReplaced()
        {
            var options = CultDocumentMessagePackSerialization.OptionsFor(typeof(GuardDoc).Assembly);
            Assert.That(MessagePackSerializer.Serialize<Custom>(new CustomArm(), options), Is.EqualTo(Raw((ref MessagePackWriter w) => w.Write("consumer-union"))));
            Assert.Throws<MessagePackSerializationException>(() => MessagePackSerializer.Deserialize<Custom>(
                Raw((ref MessagePackWriter w) => { w.WriteArrayHeader(2); w.Write(5); w.WriteArrayHeader(0); }), options));
            Assert.Throws<MessagePackSerializationException>(() => MessagePackSerializer.Serialize<Custom>(new CustomRogue(), options));
        }

        [Union(0, typeof(CustomArm))] public abstract class Custom { }
        [MessagePackObject] public sealed class CustomArm : Custom { }
        [MessagePackObject] public sealed class CustomRogue : Custom { }

        // The guard asks for a union's own attributes only. MessagePack declares UnionAttribute not inherited, so an inner union
        // can never see its outer union's keys through the base class; this pins that fact, on which the explicit false rests.
        [Test]
        public void UnionAttributeIsNotInheritedSoAUnionOwnsItsKeys()
        {
            Assert.That(typeof(UnionAttribute).GetCustomAttributes(typeof(AttributeUsageAttribute), false).Cast<AttributeUsageAttribute>().Single().Inherited, Is.False);
            Assert.That(typeof(Inner).GetCustomAttributes(typeof(UnionAttribute), true).Length, Is.EqualTo(2));
        }

        public sealed class OrderResolver : IFormatterResolver
        {
            public static readonly IFormatterResolver Instance = new OrderResolver();

            public IMessagePackFormatter<T>? GetFormatter<T>() =>
                typeof(T) == typeof(Overridden) ? (IMessagePackFormatter<T>)(object)new Fixed<Overridden>(new Overridden(), "consumer")
                : typeof(T) == typeof(Custom) ? (IMessagePackFormatter<T>)(object)new Fixed<Custom>(new CustomArm(), "consumer-union")
                : typeof(T) == typeof(CultRecordRef<GuardDoc>) ? (IMessagePackFormatter<T>)(object)new Fixed<CultRecordRef<GuardDoc>>(default, "consumer-ref")
                : null;

            private sealed class Fixed<V>(V value, string text) : IMessagePackFormatter<V>
            {
                public void Serialize(ref MessagePackWriter writer, V _, MessagePackSerializerOptions options) => writer.Write(text);
                public V Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) { reader.Skip(); return value; }
            }
        }

        [MessagePackObject] public sealed class Overridden { [Key(0)] public int N; }

        // An arm key outside Int32 or not an integer is the guard's own refusal, naming the union and the arm position, not an
        // OverflowException from reading it and not an echo of the key or the payload.
        private static Emit KeyedArm(Emit key) => (ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(2); key(ref w);
            w.WriteArrayHeader(1); w.Write(Canary);
        };

        [TestCase(2147483648L, "2147483648")]
        [TestCase(-2147483649L, "2147483649")]
        [TestCase(4294967295L, "4294967295")]
        public void ArmKeyOutsideInt32IsRefusedNamingTheUnion(long key, string digits) =>
            AssertKeyRefused(Doc(KeyedArm((ref MessagePackWriter w) => w.Write(key))), "outside Int32", digits);

        [Test]
        public void ArmKeyOfUint64MaxIsRefusedNamingTheUnion() =>
            AssertKeyRefused(Doc(KeyedArm((ref MessagePackWriter w) => w.Write(ulong.MaxValue))), "outside Int32", "18446744073709551615");

        [Test]
        public void NegativeArmKeyInInt32IsAnUnknownKey() =>
            AssertRefused(Doc(KeyedArm((ref MessagePackWriter w) => w.Write(-5))), NodeName, -5, "3, 5, 8, 9");

        [Test]
        public void NonIntegerArmKeyIsRefusedNamingTheUnion()
        {
            AssertKeyRefused(Doc(KeyedArm((ref MessagePackWriter w) => w.Write("three"))), "non-integer", "three");
            AssertKeyRefused(Doc(KeyedArm((ref MessagePackWriter w) => w.Write(3.5))), "non-integer", "3.5");
            AssertKeyRefused(Doc(KeyedArm((ref MessagePackWriter w) => w.WriteNil())), "non-integer", "nil");
        }

        // The bounds of Int32 are in range (an unknown key, not an out-of-range one), whatever width encodes them.
        [Test]
        public void ArmKeysAtTheInt32BoundsAreUnknownKeysNotOutOfRange()
        {
            AssertRefused(Doc(KeyedArm((ref MessagePackWriter w) => w.Write(int.MaxValue))), NodeName, int.MaxValue, "3, 5, 8, 9");
            AssertRefused(Doc(KeyedArm((ref MessagePackWriter w) => w.Write(int.MinValue))), NodeName, int.MinValue, "3, 5, 8, 9");
            AssertRefused(Doc(KeyedArm((ref MessagePackWriter w) => w.WriteInt64(int.MinValue))), NodeName, int.MinValue, "3, 5, 8, 9");
            AssertRefused(Doc(KeyedArm((ref MessagePackWriter w) => w.WriteUInt64(int.MaxValue))), NodeName, int.MaxValue, "3, 5, 8, 9");
            AssertKeyRefused(Doc(KeyedArm((ref MessagePackWriter w) => w.WriteUInt64((ulong)int.MaxValue + 1))), "outside Int32", "2147483648");
            AssertKeyRefused(Doc(KeyedArm((ref MessagePackWriter w) => w.WriteInt64(int.MinValue - 1L))), "outside Int32", "2147483649");
        }

        // A known key written at full width is the same key: the guard lets it through to the arm.
        [Test]
        public void KnownArmKeyAtFullWidthIsAccepted()
        {
            foreach (var key in new Emit[] { (ref MessagePackWriter w) => w.WriteUInt64(3), (ref MessagePackWriter w) => w.WriteInt64(3) })
            {
                var payload = Doc((ref MessagePackWriter w) =>
                {
                    w.WriteArrayHeader(2); key(ref w);
                    w.WriteArrayHeader(4); w.Write("r"); w.Write("ok"); w.WriteNil(); w.Write(1);
                });
                var decoded = (GuardDoc)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(GuardDoc), payload, Registry);
                Assert.That(decoded.Root, Is.TypeOf<Leaf>());
            }
        }

        private static void AssertKeyRefused(byte[] payload, string shape, string echoed)
        {
            var exception = Decode(payload);
            var message = Chain(exception);
            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain($"Union {NodeName} has"));
                Assert.That(message, Does.Contain(shape));
                Assert.That(message, Does.Contain("(slot 0 of the arm)"));
                Assert.That(message, Does.Contain("declared keys: 3, 5, 8, 9."));
                Assert.That(message, Does.Not.Contain(Canary));
                Assert.That(message, Does.Not.Contain(echoed), "the offending key is not echoed");
                for (var cause = exception; cause != null; cause = cause.InnerException)
                    Assert.That(cause, Is.Not.InstanceOf<OverflowException>());
            });
        }

        // An interface is a union like an abstract class: the guard reads the interface's own [Union] attributes.
        private static readonly string ShapeName = typeof(IShape).FullName!;

        // ShapeDoc: slots [id, shape, shapes].
        private static byte[] Shapes(Emit shape, Emit? shapes = null) => Raw((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.Write("s1");
            shape(ref w);
            (shapes ?? List())(ref w);
        });

        private static Emit CircleArm(int key, int radius) => InnerArm(key, radius);

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(9)]
        public void UnknownArmOfAnInterfaceUnionRefused(int key)
        {
            foreach (var payload in new[] { Shapes(CircleArm(key, 1)), Shapes(Nil, List(CircleArm(0, 1), CircleArm(key, 1))) })
            {
                Exception? thrown = null;
                try { CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(ShapeDoc), payload, Registry); }
                catch (Exception exception) { thrown = exception; }
                Assert.That(thrown, Is.Not.Null, "an unknown arm of an interface union was accepted");
                var message = Chain(thrown!);
                Assert.That(message, Does.Contain($"Union {ShapeName} has no arm for key {key} "));
                Assert.That(message, Does.Contain("declared keys: 0, 2."));
            }
        }

        [Test]
        public void UnnamedImplementationOfAnInterfaceUnionWriteRefused()
        {
            foreach (var doc in new[] { new ShapeDoc { Shape = new Triangle() }, new ShapeDoc { Shapes = { new Circle(), new Triangle() } }, new ShapeDoc { Shape = new BigCircle() } })
            {
                var message = Chain(Assert.Catch<MessagePackSerializationException>(
                    () => CultDocumentMessagePackSerialization.SerializeUntyped(doc, typeof(ShapeDoc), Registry))!);
                Assert.That(message, Does.Contain("is not an arm of union").And.Contain(ShapeName));
            }
        }

        [Test]
        public void KnownArmsOfAnInterfaceUnionByteIdentical()
        {
            var doc = new ShapeDoc { Id = "s1", Shape = new Square { N = 4 }, Shapes = { new Circle { N = 2 }, null! } };
            var expected = Shapes(CircleArm(2, 4), List(CircleArm(0, 2), Nil));
            Assert.That(Convert.ToHexString(CultDocumentMessagePackSerialization.SerializeUntyped(doc, typeof(ShapeDoc), Registry)), Is.EqualTo(Convert.ToHexString(expected)));
            Assert.That(Convert.ToHexString(MessagePackSerializer.Serialize(doc, MessagePackSerializerOptions.Standard)), Is.EqualTo(Convert.ToHexString(expected)));
            var back = (ShapeDoc)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(ShapeDoc), expected, Registry);
            Assert.That(back.Shape, Is.TypeOf<Square>());
            Assert.That(back.Shapes[0], Is.TypeOf<Circle>());
            Assert.That(back.Shapes[1], Is.Null);
        }

        // A variant record whose override holds an unknown arm is refused naming the record, the schema id, the union and the key.
        [Test]
        public void VariantOverrideHoldingAnUnknownArmIsRefusedNamingTheSchema()
        {
            var folder = Path.Combine(Path.GetTempPath(), $"cultlib-unionguard-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            try
            {
                var descriptor = Registry.GetRequired(typeof(GuardDoc));
                var path = Path.Combine(folder, "variant.cc");
                File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(new CultPersistedStoreSnapshot
                {
                    FormatVersion = CultPersistedStoreSnapshot.FormatV2,
                    SchemaCatalog = new[] { descriptor.ToCatalogEntry() },
                    Records = new[]
                    {
                        new CultPersistedRecord
                        {
                            Key = "base", SchemaId = descriptor.SchemaId, StoredAt = "2026-01-01T00:00:00.0000000Z",
                            Payload = Doc(LeafArm(3, "000000000005", "ok", 1))
                        },
                        new CultPersistedRecord
                        {
                            Key = "variant", SchemaId = descriptor.SchemaId, StoredAt = "2026-01-01T00:00:00.0000000Z",
                            Payload = Array.Empty<byte>(),
                            Variant = new CultVariantDelta("base", new[] { CultVariantOverride.Set(1, Raw(LeafArm(4, "000000000005", Canary, 7))) })
                        }
                    }
                }));
                var thrown = Assert.Catch(() => CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, ReadOnly = true }).Dispose())!;
                var message = Chain(thrown);
                Assert.Multiple(() =>
                {
                    Assert.That(message, Does.Contain("Variant variant"));
                    Assert.That(message, Does.Contain($"(schema '{descriptor.SchemaId}')"));
                    Assert.That(message, Does.Contain(NodeName));
                    Assert.That(message, Does.Contain("key 4 "));
                    Assert.That(message, Does.Not.Contain(Canary));
                });
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        // A store holding an unknown arm fails to load and names the record, its schema, the union and the key; the genuine
        // payload is replaced in the file by one that differs only in the arm key.
        [TestCase(false, 4)]
        [TestCase(false, 10)]
        [TestCase(true, 4)]
        [TestCase(true, 10)]
        public async Task StorePullNamesTheRecord(bool directory, int key)
        {
            var folder = Path.Combine(Path.GetTempPath(), $"cultlib-unionguard-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder, "guard.cc");
                var genuine = Doc(LeafArm(3, "000000000005", Canary, 7));
                using (var writer = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = directory, StoreFlushOnDispose = true }))
                {
                    await writer.UpsertAsync(typeof(GuardDoc), new GuardDoc { Id = "d1", Root = new Leaf { Id = "000000000005", Content = Canary, Weight = 7 } }, new CultRecordKey("d1"));
                    await writer.FlushAsync();
                }

                var forged = Doc(LeafArm(key, "000000000005", Canary, 7));
                Assert.That(forged.Length, Is.EqualTo(genuine.Length));
                // A directory store names a page by the SHA-256 of its bytes and its manifest commits to that hash, so a forged
                // page is renamed and the manifest told its new hash: the store then holds a well-formed page the reader must refuse.
                var patched = 0;
                var rehashed = new List<(byte[] Old, byte[] New)>();
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToArray())
                {
                    var bytes = File.ReadAllBytes(file);
                    var at = bytes.AsSpan().IndexOf(genuine);
                    if (at < 0) continue;
                    var before = System.Security.Cryptography.SHA256.HashData(bytes);
                    forged.CopyTo(bytes, at);
                    if (file.EndsWith(".msgpack", StringComparison.Ordinal))
                    {
                        File.Delete(file);
                        var after = System.Security.Cryptography.SHA256.HashData(bytes);
                        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(file)!, Convert.ToHexString(after).ToLowerInvariant() + ".msgpack"), bytes);
                        rehashed.Add((before, after));
                    }
                    else
                    {
                        File.WriteAllBytes(file, bytes);
                    }

                    patched++;
                }

                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".msgpack", StringComparison.Ordinal)).ToArray())
                {
                    var bytes = File.ReadAllBytes(file);
                    foreach (var (before, after) in rehashed)
                        for (var at = bytes.AsSpan().IndexOf(before); at >= 0; at = bytes.AsSpan().IndexOf(before)) after.CopyTo(bytes, at);
                    File.WriteAllBytes(file, bytes);
                }

                Assert.That(patched, Is.GreaterThan(0), "the genuine payload was found in the store");
                var thrown = Assert.CatchAsync(async () =>
                {
                    using var cache = await CultCacheMessagePack.OpenAsync(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = directory, ReadOnly = true });
                });
                // The outermost message alone says it: a caller that prints only .Message must see the record and the union.
                var message = thrown!.Message;
                Assert.Multiple(() =>
                {
                    Assert.That(message, Does.Contain("Record 'd1'"));
                    Assert.That(message, Does.Contain($"(schema '{Registry.GetRequired(typeof(GuardDoc)).SchemaId}')"));
                    Assert.That(message, Does.Contain(NodeName));
                    Assert.That(message, Does.Contain($"key {key} "));
                    Assert.That(Chain(thrown), Does.Not.Contain(Canary));
                });
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        // An arm is [key, armSlots]; the keys here start at 3, skip 0-2, 4, 6 and 7, and never reuse a retired key.
        [Union(3, typeof(Leaf)), Union(5, typeof(Branch)), Union(8, typeof(Unit8)), Union(9, typeof(StubArm))]
        public abstract class GuardNode { }

        [MessagePackObject]
        public class Leaf : GuardNode
        {
            [Key(0)] [CultElementId] public string Id = string.Empty;
            [Key(1)] public string Content = string.Empty;
            [Key(3)] public int Weight;
        }

        // A subtype of an arm is not the arm: MessagePack would write it as nil.
        [MessagePackObject] public sealed class DerivedLeaf : Leaf { }

        [MessagePackObject]
        public sealed class Branch : GuardNode
        {
            [Key(0)] public List<GuardNode> Children = new();
            [Key(1)] public GuardNode? Next;
            [Key(2)] [CultElementId] public string Id = string.Empty;
        }

        [MessagePackObject] public sealed class Unit8 : GuardNode { [Key(0)] [CultElementId] public string Id = string.Empty; }

        // A subtype the union does not name.
        [MessagePackObject] public sealed class Rogue : GuardNode { [Key(0)] public string Note = string.Empty; }

        // Written and read by a consumer resolver, not by MessagePack.
        public sealed class StubArm : GuardNode { [Key(0)] [CultElementId] public string Id = string.Empty; }

        public sealed class StubArmResolver : IFormatterResolver
        {
            public static readonly IFormatterResolver Instance = new StubArmResolver();
            public IMessagePackFormatter<T>? GetFormatter<T>() => typeof(T) == typeof(StubArm) ? (IMessagePackFormatter<T>)(object)new StubFormatter() : null;

            private sealed class StubFormatter : IMessagePackFormatter<StubArm>
            {
                public void Serialize(ref MessagePackWriter writer, StubArm value, MessagePackSerializerOptions options)
                {
                    writer.WriteArrayHeader(1);
                    writer.Write("via-resolver");
                }

                public StubArm Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    reader.Skip();
                    return new StubArm();
                }
            }
        }

        [Union(0, typeof(OuterA)), Union(1, typeof(InnerA)), Union(2, typeof(InnerB)), Union(7, typeof(OuterOnly))]
        public abstract class Outer { }

        [Union(0, typeof(InnerA)), Union(1, typeof(InnerB))]
        public abstract class Inner : Outer { }

        [MessagePackObject] public sealed class OuterA : Outer { }
        [MessagePackObject] public sealed class OuterOnly : Outer { [Key(0)] public int N; }
        [MessagePackObject] public sealed class InnerA : Inner { [Key(0)] public int N; }
        [MessagePackObject] public sealed class InnerB : Inner { [Key(0)] public int N; }
        [MessagePackObject] public sealed class Unlisted : Inner { }

        [CultDocument("tests.guard_doc", "tests.guard_doc.v1")]
        [MessagePackObject]
        public sealed class GuardDoc
        {
            [Key(0)] [CultName] public string Id = string.Empty;
            [Key(1)] public GuardNode? Root;
            [Key(2)] public List<GuardNode> Items = new();
            [Key(3)] public Inner? Crafted;
            [Key(4)] public Outer? Any;
        }

        // An interface union: arm keys 0 and 2, so 1 is a retired gap.
        [Union(0, typeof(Circle)), Union(2, typeof(Square))]
        public interface IShape { }

        [MessagePackObject] public class Circle : IShape { [Key(0)] public int N; }
        [MessagePackObject] public sealed class Square : IShape { [Key(0)] public int N; }
        [MessagePackObject] public sealed class Triangle : IShape { }
        [MessagePackObject] public sealed class BigCircle : Circle { }

        [CultDocument("tests.shape_doc", "tests.shape_doc.v1")]
        [MessagePackObject]
        public sealed class ShapeDoc
        {
            [Key(0)] [CultName] public string Id = string.Empty;
            [Key(1)] public IShape? Shape;
            [Key(2)] public List<IShape> Shapes = new();
        }
    }
}
