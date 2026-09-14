#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    public class CultInspectorModelTests
    {
        private static readonly CultDocumentRegistry Registry =
            CultDocumentRegistry.ForTypes(new[] { typeof(InspectItem), typeof(InspectOther), typeof(InspectFixed) });

        private static CultInspectorModel Model() => CultCacheMessagePack.CreateInspectorModel(Registry);

        private static CultRecordRef<InspectItem> Ref(string key) => new CultRecordRef<InspectItem>(new CultRecordKey(key));

        [Test]
        public void MembersFollowTheCatalogWithTheirMetadata()
        {
            var members = Model().MembersOf(typeof(InspectItem));

            Assert.That(members.Select(member => member.Name),
                Is.EqualTo(new[] { "Value", "Name", "Secret", "Locked", "Shape", "Links", "Numbers" }), "order first, then slot");
            Assert.That(members.Single(member => member.Name == "Name").Metadata.Label, Is.EqualTo("Display Name"));
            Assert.That(members.Single(member => member.Name == "Name").IsReadOnly, Is.False);
            Assert.That(members.Single(member => member.Name == "Value").Metadata.Range!.Max, Is.EqualTo(10f));
            Assert.That(members.Single(member => member.Name == "Secret").Metadata.Hidden, Is.True);
            Assert.That(members.Single(member => member.Name == "Locked").IsReadOnly, Is.True);

            var id = Model().MembersOf(typeof(InspectFixed)).Single();
            Assert.That(id.IsAssignable, Is.False, "a get-only member its constructor fills cannot be assigned");
            Assert.That(id.IsReadOnly, Is.True);

            var hidden = Model().MembersOf(typeof(InspectHider)).Single();
            Assert.That(hidden.Member.DeclaringType, Is.EqualTo(typeof(InspectHider)), "a `new` member is its most-derived declaration");
            Assert.That(hidden.ValueType, Is.EqualTo(typeof(string)));
        }

        [Test]
        public void ClaimsResolveAttributeThenTypeThenBuiltIn()
        {
            var claims = new CultInspectorDrawerClaims(
                new[] { typeof(MarkDrawer), typeof(IntDrawer), typeof(ListDrawer), typeof(StringDrawerA), typeof(StringDrawerB), typeof(NotADrawer) },
                typeof(IFakeDrawer));
            FieldInfo Field(string name) => typeof(ClaimHost).GetField(name)!;

            Assert.That(claims.Resolve(typeof(int), Field(nameof(ClaimHost.Marked))).Drawer, Is.EqualTo(typeof(MarkDrawer)));
            Assert.That(claims.Resolve(typeof(int), Field(nameof(ClaimHost.Plain))).Drawer, Is.EqualTo(typeof(IntDrawer)),
                "an invalid drawer claiming int does not contest the valid one");
            Assert.That(claims.Resolve(typeof(List<float>), null).Drawer, Is.EqualTo(typeof(ListDrawer)));
            Assert.That(claims.Resolve(typeof(float), null).Drawer, Is.Null);
            Assert.That(claims.Resolve(typeof(float), null).Conflict, Is.Null);

            var contested = claims.Resolve(typeof(string), null);
            Assert.That(contested.Drawer, Is.Null);
            Assert.That(contested.Conflict, Does.Contain(nameof(StringDrawerA)).And.Contain(nameof(StringDrawerB)));
            Assert.That(claims.Errors.Any(error => error.Contains(nameof(NotADrawer))), Is.True);
            Assert.That(claims.Errors.Any(error => error.Contains(nameof(StringDrawerA)) && error.Contains(nameof(StringDrawerB))), Is.True);
        }

        [Test]
        public void ElementChoicesAreTheDeclaredUnionSubtypesOrTheTypeItself()
        {
            var model = Model();

            Assert.That(model.ShapeOf(typeof(InspectShape)).Kind, Is.EqualTo(CultInspectorValueKind.Union));
            Assert.That(model.ElementChoices(typeof(InspectShape)), Is.EqualTo(new[] { typeof(InspectCircle) }));
            Assert.That(model.CreateElement(typeof(InspectShape), typeof(InspectCircle), out var made), Is.InstanceOf<InspectCircle>());
            Assert.That(made, Is.Null);
            Assert.That(() => model.CreateElement(typeof(InspectShape), typeof(InspectSquare), out _), Throws.ArgumentException,
                "an undeclared subtype is not a choice");

            Assert.That(model.ElementChoices(typeof(List<int>)), Is.EqualTo(new[] { typeof(List<int>) }));
            Assert.That(model.CreateElement(typeof(List<int>), typeof(List<int>), out _), Is.EqualTo(new List<int>()));

            Assert.That(model.CreateElement(typeof(InspectPicky), typeof(InspectNeedsArgs), out var refused), Is.Null);
            Assert.That(refused, Does.Contain("parameterless"));
            Assert.That(model.CreateElement(typeof(InspectNeedsArgs), typeof(InspectNeedsArgs), out var bare), Is.Null);
            Assert.That(bare, Does.Contain("parameterless"), "a plain element type that cannot be made says so; nothing is added");
            Assert.That(model.CreateElement(typeof(int?), typeof(int?), out var nullable), Is.Null);
            Assert.That(nullable, Is.Null, "null is a Nullable<T> element, not a failure");

            Assert.That(model.CreateElement(typeof(InspectShape), typeof(InspectShape), out var @abstract), Is.Null);
            Assert.That(@abstract, Does.Contain("abstract"), "a union's own type cannot be made; it says so instead of throwing");
            Assert.That(new[] { typeof(InspectShape), typeof(InspectNeedsArgs), typeof(IList<int>) }.Any(model.CanCreate), Is.False);
            Assert.That(new[] { typeof(InspectItem), typeof(string), typeof(int[]), typeof(IDictionary<string, int>), typeof(InspectOpenStruct) }.All(model.CanCreate), Is.True);
        }

        [Test]
        public void IntegerEditsClampToTheirType()
        {
            var model = Model();

            Assert.That(model.NarrowInteger(typeof(byte), 300), Is.TypeOf<byte>().And.EqualTo(byte.MaxValue));
            Assert.That(model.NarrowInteger(typeof(sbyte), -300), Is.TypeOf<sbyte>().And.EqualTo(sbyte.MinValue));
            Assert.That(model.NarrowInteger(typeof(uint), -1), Is.TypeOf<uint>().And.EqualTo(0u));
            Assert.That(model.NarrowInteger(typeof(int), 42), Is.TypeOf<int>().And.EqualTo(42));
            Assert.That(() => model.NarrowInteger(typeof(float), 1), Throws.ArgumentException);
        }

        [Test]
        public async Task RecordRefCandidatesAreRecordsAssignableToTheTarget()
        {
            using var cache = new CultCache(Registry);
            var a = await cache.UpsertAsync(typeof(InspectItem), new InspectItem { Name = "a" });
            var b = await cache.UpsertAsync(typeof(InspectItem), new InspectItem { Name = "b" });
            await cache.UpsertAsync(typeof(InspectOther), new InspectOther { Name = "o" });

            var candidates = Model().RecordCandidates(typeof(CultRecordRef<InspectItem>), cache.AllStoredDocuments);

            Assert.That(candidates.Select(candidate => candidate.Key), Is.EqualTo(new[] { a, b }));
            Assert.That(CultInspectorModel.RecordKey(default(CultRecordRef<InspectItem>)), Is.EqualTo(string.Empty));
            Assert.That(CultInspectorModel.RecordKey(new CultRecordRef<InspectItem>(a)), Is.EqualTo(a.Value));
        }

        [Test]
        public async Task DictionaryKeysRefuseNullEmptyReferencesAndDuplicates()
        {
            var model = Model();
            var links = typeof(Dictionary<CultRecordRef<InspectItem>, int>);
            var keys = new object?[] { Ref("a"), Ref("b") };
            string? Refusal(object? candidate)
            {
                var kept = model.ReplaceKey(typeof(InspectItem),links, keys, 0, candidate, out var notice);
                Assert.That(kept, Is.EqualTo(notice == null ? candidate : keys[0]), "a refused key keeps the entry's key");
                return notice;
            }

            Assert.That(Refusal(Ref("")), Does.Contain("empty"));
            Assert.That(Refusal(default(CultRecordRef<InspectItem>)), Does.Contain("empty"), "null and \"\" are one empty reference");
            Assert.That(Refusal(Ref("b")), Does.Contain("duplicate").And.Contain("entry 0"));
            Assert.That(Refusal(null), Does.Contain("null"));
            Assert.That(Refusal(Ref("c")), Is.Null);

            // An entry an old store left with an empty reference key keeps it through an edit of its value.
            Assert.That(model.ReplaceKey(typeof(InspectItem),links, new object?[] { Ref(""), Ref("b") }, 0, default(CultRecordRef<InspectItem>), out var unchanged),
                Is.EqualTo(default(CultRecordRef<InspectItem>)));
            Assert.That(unchanged, Is.Null, "an unchanged key is never refused");

            using var cache = new CultCache(Registry);
            var first = await cache.UpsertAsync(typeof(InspectItem), new InspectItem { Name = "first" });
            var second = await cache.UpsertAsync(typeof(InspectItem), new InspectItem { Name = "second" });
            var fresh = model.FreshKey(typeof(InspectItem),links, new object?[] { new CultRecordRef<InspectItem>(first) }, cache.AllStoredDocuments, out _);
            Assert.That(CultInspectorModel.RecordKey(fresh), Is.EqualTo(second.Value));
            Assert.That(model.FreshKey(typeof(InspectItem),links, new object?[] { new CultRecordRef<InspectItem>(first), new CultRecordRef<InspectItem>(second) },
                cache.AllStoredDocuments, out var none), Is.Null);
            Assert.That(none, Does.Contain("No unused"));
        }

        [Test]
        public void DictionaryKeysAgreeWithTheDictionaryComparer()
        {
            var model = Model();
            var doubles = typeof(Dictionary<double, int>);
            var keys = new object?[] { 0.0, 1.0 };

            Assert.That(model.ReplaceKey(typeof(InspectItem),doubles, keys, 1, -0.0, out var notice), Is.EqualTo(1.0));
            Assert.That(notice, Does.Contain("duplicate"), "-0.0 serializes apart from 0.0 but Equals it");
            Assert.That(() => model.BuildDictionary(doubles, keys.Select(key => new KeyValuePair<object?, object?>(key, 0))), Throws.Nothing);
            Assert.That(model.FreshKey(typeof(InspectItem),doubles, new object?[] { -0.0 }, Array.Empty<CultStoredDocument>(), out _), Is.Null,
                "the default 0.0 is taken by -0.0");
            Assert.That(model.ReplaceKey(typeof(InspectItem),doubles, new object?[] { 0.0 }, 0, -0.0, out _), Is.EqualTo(-0.0), "a key may replace itself");
        }

        [Test]
        public void DictionaryKeysCompareBySerializedValueNotText()
        {
            var model = Model();
            var dictionary = typeof(Dictionary<InspectKey, int>);

            Assert.That(model.FreshKey(typeof(InspectItem),dictionary, new object?[] { new InspectKey(1) }, Array.Empty<CultStoredDocument>(), out _),
                Is.EqualTo(new InspectKey(0)), "keys whose text collides are still distinct keys");
            Assert.That(model.ReplaceKey(typeof(InspectItem),dictionary, new object?[] { new InspectKey(1), new InspectKey(2) }, 1, new InspectKey(3), out var notice),
                Is.EqualTo(new InspectKey(3)));
            Assert.That(notice, Is.Null);
        }

        // Pair's formatter is declared only by this test assembly (AssemblyInfo). A key's bytes come from its owning document's
        // options, so a document here stores it and a document from an assembly declaring no resolver cannot.
        [Test]
        public void DictionaryKeysSerializeUnderTheOwningDocument()
        {
            var model = Model();
            var pairs = typeof(Dictionary<Pair, int>);
            var keys = new object?[] { new Pair { A = 1 }, new Pair { A = 2 } };

            Assert.That(model.ReplaceKey(typeof(InspectItem), pairs, keys, 1, new Pair { A = 3 }, out var accepted), Is.EqualTo(new Pair { A = 3 }));
            Assert.That(accepted, Is.Null);
            Assert.That(model.ReplaceKey(typeof(object), pairs, keys, 1, new Pair { A = 3 }, out var refused), Is.EqualTo(keys[1]),
                "a key its document cannot serialize is refused, not compared by text");
            Assert.That(refused, Does.Contain("cannot serialize"));
            Assert.That(model.FreshKey(typeof(object), pairs, new object?[] { new Pair { A = 1 } }, Array.Empty<CultStoredDocument>(), out var none), Is.Null);
            Assert.That(none, Does.Contain("cannot serialize"));
        }

        [Test]
        public async Task RecordIsTheStateTheEditBeganFrom()
        {
            using var cache = new CultCache(Registry);
            await cache.UpsertAsync(typeof(InspectItem), new InspectItem { Name = "original" });
            var record = cache.AllStoredDocuments.Single();
            var edit = Model().BeginEdit(record);
            ((InspectItem)record.Document).Name = "changed in place before Record was read";

            Assert.That(((InspectItem)edit.Record).Name, Is.EqualTo(((InspectItem)edit.Document).Name).And.EqualTo("original"));
        }

        [Test]
        public async Task RefusedCommitLeavesTheCachedDocumentUnchanged()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultlib-tests-{Guid.NewGuid():N}.cc");
            try
            {
                using (var seed = CultCacheMessagePack.Create(filePath, new CultCacheOpenOptions { Registry = Registry }))
                {
                    await seed.UpsertAsync(typeof(InspectItem), new InspectItem { Name = "original", Numbers = { 1, 2 } });
                    await seed.FlushAsync();
                }

                var model = Model();
                using var readOnly = CultCacheMessagePack.Create(filePath, new CultCacheOpenOptions { Registry = Registry, ReadOnly = true });
                var record = readOnly.AllStoredDocuments.Single();
                var edit = model.BeginEdit(record);
                Assert.That(edit.Document, Is.Not.SameAs(record.Document));
                var drawn = (InspectItem)edit.Record;
                Assert.That(drawn, Is.Not.SameAs(record.Document).And.Not.SameAs(edit.Document));
                Assert.That(edit.Record, Is.SameAs(drawn), "one record copy per edit");
                drawn.Name = "a drawer wrote here";
                Assert.That(((InspectItem)edit.Document).Name, Is.EqualTo("original"), "the record copy is not the committed copy");
                ((InspectItem)edit.Document).Name = "changed";
                ((InspectItem)edit.Document).Numbers.Add(3);

                Assert.That(edit.Commit(readOnly, out var error), Is.False);
                Assert.That(error, Is.Not.Null.And.Not.Empty);
                Assert.That(readOnly.Get<InspectItem>(record.Key)!.Name, Is.EqualTo("original"));
                Assert.That(readOnly.Get<InspectItem>(record.Key)!.Numbers, Is.EqualTo(new[] { 1, 2 }));
                Assert.That(edit.IsFor(record), Is.False, "a commit spends the edit");

                using var writable = new CultCache(Registry);
                var key = await writable.UpsertAsync(typeof(InspectItem), new InspectItem { Name = "original" });
                var stored = writable.AllStoredDocuments.Single();
                var accepted = model.BeginEdit(stored);
                ((InspectItem)accepted.Document).Name = "changed";
                Assert.That(accepted.Commit(writable, out _), Is.True);
                Assert.That(writable.Get<InspectItem>(key)!.Name, Is.EqualTo("changed"));
                Assert.That(((InspectItem)stored.Document).Name, Is.EqualTo("original"), "the replaced object was never mutated");
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
            }
        }

        [Test]
        public void ShapesClassifyCollectionsStructsAndMultiDimensionalArrays()
        {
            var model = Model();

            Assert.That(model.ShapeOf(typeof(float[,])).Kind, Is.EqualTo(CultInspectorValueKind.Unsupported));
            Assert.That(model.ShapeOf(typeof(float[,])).Reason, Does.Contain("multi-dimensional"));
            Assert.That(model.ShapeOf(typeof(List<int>)).ElementType, Is.EqualTo(typeof(int)));
            Assert.That(model.BuildList(typeof(float[]), new object?[] { 1f, 2f }), Is.EqualTo(new[] { 1f, 2f }));

            var open = model.ShapeOf(typeof(InspectOpenStruct));
            Assert.That(open.Kind, Is.EqualTo(CultInspectorValueKind.Nested));
            Assert.That(open.Members.Select(member => member.Name), Is.EqualTo(new[] { "x", "y" }));
            Assert.That(open.Members.All(member => member.IsAssignable), Is.True);
            (string, bool)[] Rows(Type type) => model.ShapeOf(type).Members.Select(member => (member.Name, member.IsReadOnly)).ToArray();
            Assert.That(Rows(typeof(InspectMixedStruct)), Is.EqualTo(new[] { ("x", false), ("y", true) }),
                "a struct with public fields shows only them; its settable properties are aliases, not rows");
            Assert.That(Rows(typeof(InspectPropertyStruct)), Is.EqualTo(new[] { ("Settable", false), ("Initable", true) }),
                "without public fields, settable properties are rows; init is read-only; get-only is not a row");
            Assert.That(Rows(typeof(InspectRecordStruct)), Is.EqualTo(new[] { ("A", false), ("B", false) }));
            Assert.That(Rows(typeof(InspectReadonlyRecordStruct)), Is.EqualTo(new[] { ("A", true), ("B", true) }));
            object boxed = new InspectRecordStruct(1, 2f);
            model.ShapeOf(typeof(InspectRecordStruct)).Members[0].SetValue(boxed, 5);
            Assert.That(((InspectRecordStruct)boxed).A, Is.EqualTo(5), "a property edit lands in the boxed struct in place");

            Assert.That(model.ShapeOf(typeof(IDictionary<string, int>)).Kind, Is.EqualTo(CultInspectorValueKind.Dictionary));
            Assert.That(model.BuildDictionary(typeof(IDictionary<string, int>), new[] { new KeyValuePair<object?, object?>("a", 1) }),
                Is.EqualTo(new Dictionary<string, int> { ["a"] = 1 }));
        }

        [CultDocument("tests.inspect_item", "tests.inspect_item.v1")]
        [MessagePackObject]
        public sealed class InspectItem
        {
            [Key(0)] [CultName] [CultInspectorLabel("Display Name")]
            public string Name = string.Empty;

            [Key(1)] [CultInspectorRange(0, 10)] [CultInspectorOrder(-1)]
            public int Value;

            [Key(2)] [CultInspectorHidden]
            public string Secret = string.Empty;

            [Key(3)] [CultInspectorReadOnly]
            public string Locked = string.Empty;

            [Key(4)]
            public InspectShape? Shape;

            [Key(5)]
            public Dictionary<CultRecordRef<InspectItem>, int> Links = new Dictionary<CultRecordRef<InspectItem>, int>();

            [Key(6)]
            public List<int> Numbers = new List<int>();
        }

        [CultDocument("tests.inspect_other", "tests.inspect_other.v1")]
        [MessagePackObject]
        public sealed class InspectOther
        {
            [Key(0)] [CultName]
            public string Name = string.Empty;
        }

        [CultDocument("tests.inspect_fixed", "tests.inspect_fixed.v1")]
        [MessagePackObject]
        public sealed class InspectFixed
        {
            [SerializationConstructor]
            public InspectFixed(string id)
            {
                Id = id;
            }

            [Key(0)]
            public string Id { get; }
        }

        [Union(0, typeof(InspectCircle))]
        public abstract class InspectShape
        {
        }

        [MessagePackObject]
        public sealed class InspectCircle : InspectShape
        {
            [Key(0)]
            public float Radius;
        }

        [MessagePackObject]
        public sealed class InspectSquare : InspectShape
        {
            [Key(0)]
            public float Side;
        }

        [MessagePackObject]
        public class InspectHidden
        {
            [Key(0)]
            public int Value;
        }

        [MessagePackObject]
        public sealed class InspectHider : InspectHidden
        {
            [Key(0)]
            public new string Value = string.Empty;
        }

        [Union(0, typeof(InspectNeedsArgs))]
        public abstract class InspectPicky
        {
        }

        public sealed class InspectNeedsArgs : InspectPicky
        {
            public InspectNeedsArgs(int side)
            {
            }
        }

        public struct InspectOpenStruct
        {
            public float x;
            public float y;
            public float Length => x + y;
        }

        public struct InspectMixedStruct
        {
            public float x;
            public readonly float y;
            public float Alias { get => x; set => x = value; }
        }

        public struct InspectPropertyStruct
        {
            public int Settable { get; set; }
            public int Initable { get; init; }
            public int Computed => Settable + 1;
        }

        public record struct InspectRecordStruct(int A, float B);

        public readonly record struct InspectReadonlyRecordStruct(int A, float B);

        [MessagePackObject]
        public readonly record struct InspectKey([property: Key(0)] int Value)
        {
            public override string ToString() => "key";
        }

        public interface IFakeDrawer
        {
        }

        [AttributeUsage(AttributeTargets.Field)]
        public sealed class InspectMarkAttribute : Attribute
        {
        }

        public sealed class ClaimHost
        {
            [InspectMark] public int Marked;
            public int Plain;
        }

        [CultInspectorDrawer(typeof(InspectMarkAttribute))]
        public sealed class MarkDrawer : IFakeDrawer
        {
        }

        [CultInspectorDrawer(typeof(int))]
        public sealed class IntDrawer : IFakeDrawer
        {
        }

        [CultInspectorDrawer(typeof(List<>))]
        public sealed class ListDrawer : IFakeDrawer
        {
        }

        [CultInspectorDrawer(typeof(string))]
        public sealed class StringDrawerA : IFakeDrawer
        {
        }

        [CultInspectorDrawer(typeof(string))]
        public sealed class StringDrawerB : IFakeDrawer
        {
        }

        [CultInspectorDrawer(typeof(int))]
        public sealed class NotADrawer
        {
        }
    }
}
