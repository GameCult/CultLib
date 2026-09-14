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

        private static CultInspectorModel Model() => new CultInspectorModel(
            Registry,
            (value, type) => CultDocumentMessagePackSerialization.SerializeUntyped(value, type, Registry),
            (type, bytes) => CultDocumentMessagePackSerialization.DeserializeUntyped(type, bytes, Registry));

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
        public void UnionChoicesAreTheDeclaredSubtypesOnly()
        {
            var model = Model();
            var shape = model.ShapeOf(typeof(InspectShape));

            Assert.That(shape.Kind, Is.EqualTo(CultInspectorValueKind.Union));
            Assert.That(shape.UnionChoices, Is.EqualTo(new[] { typeof(InspectCircle) }));
            Assert.That(model.CreateUnionValue(typeof(InspectShape), typeof(InspectCircle)), Is.InstanceOf<InspectCircle>());
            Assert.That(() => model.CreateUnionValue(typeof(InspectShape), typeof(InspectSquare)), Throws.InvalidOperationException);
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
            var others = new object?[] { Ref("b") };

            Assert.That(model.KeyIdentity(default(CultRecordRef<InspectItem>)), Is.EqualTo(model.KeyIdentity(Ref(""))));
            Assert.That(model.RefuseKey(Ref(""), Ref("a"), others), Does.Contain("empty"));
            Assert.That(model.RefuseKey(default(CultRecordRef<InspectItem>), Ref("a"), others), Does.Contain("empty"));
            Assert.That(model.RefuseKey(Ref("b"), Ref("a"), others), Does.Contain("duplicate"));
            Assert.That(model.RefuseKey(null, Ref("a"), others), Does.Contain("null"));
            Assert.That(model.RefuseKey(Ref("c"), Ref("a"), others), Is.Null);
            Assert.That(model.RefuseKey(Ref("a"), Ref("a"), others), Is.Null, "an unchanged key is never refused");
            Assert.That(model.RefuseKey("x", "y", new object?[] { "x" }), Does.Contain("duplicate"));

            using var cache = new CultCache(Registry);
            var first = await cache.UpsertAsync(typeof(InspectItem), new InspectItem { Name = "first" });
            var second = await cache.UpsertAsync(typeof(InspectItem), new InspectItem { Name = "second" });
            var fresh = model.FreshKey(typeof(CultRecordRef<InspectItem>), new object?[] { new CultRecordRef<InspectItem>(first) }, cache.AllStoredDocuments);
            Assert.That(CultInspectorModel.RecordKey(fresh), Is.EqualTo(second.Value));
            Assert.That(model.FreshKey(typeof(CultRecordRef<InspectItem>),
                new object?[] { new CultRecordRef<InspectItem>(first), new CultRecordRef<InspectItem>(second) }, cache.AllStoredDocuments), Is.Null);
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
        public void ShapesClassifyCollectionsCompositesAndMultiDimensionalArrays()
        {
            var model = Model();

            Assert.That(model.ShapeOf(typeof(float[,])).Kind, Is.EqualTo(CultInspectorValueKind.Unsupported));
            Assert.That(model.ShapeOf(typeof(float[,])).Reason, Does.Contain("multi-dimensional"));
            Assert.That(model.ShapeOf(typeof(List<int>)).ElementType, Is.EqualTo(typeof(int)));
            Assert.That(model.BuildList(typeof(float[]), new object?[] { 1f, 2f }), Is.EqualTo(new[] { 1f, 2f }));

            var pair = model.ShapeOf(typeof(InspectPair));
            Assert.That(pair.Kind, Is.EqualTo(CultInspectorValueKind.Composite));
            Assert.That(pair.Members.Select(member => member.Name), Is.EqualTo(new[] { "x", "y" }));
            Assert.That(model.Compose(pair, new object?[] { 1f, 2f }), Is.EqualTo(new InspectPair(1f, 2f)));

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

        public readonly record struct InspectPair(float x, float y);

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
