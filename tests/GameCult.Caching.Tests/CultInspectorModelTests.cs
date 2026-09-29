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
            CultDocumentRegistry.ForTypes(new[] { typeof(InspectItem), typeof(InspectOther), typeof(InspectFixed), typeof(InspectLabeled) });

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
        public void AssetGuidMemberExposesItsAttributeAndDeclaredType()
        {
            var member = Model().MembersOf(typeof(InspectAsset)).Single();

            Assert.That(member.Metadata.AssetGuid, Is.Not.Null);
            Assert.That(member.Metadata.AssetGuid!.AssetType, Is.EqualTo(typeof(Uri)));
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
        public void OneRuleDecidesWhichRecordsMayBeARefsValue()
        {
            var model = Model();
            CultStoredDocument Record(string key, object document) =>
                new(new CultRecordKey(key), "test", Registry.GetRequired(document.GetType()), document);
            var leaf = Record("leaf", new InspectLabeled { Name = "a" });
            var sibling = Record("other", new InspectOther { Name = "b" });
            var unkeyed = Record("", new InspectLabeled { Name = "c" });
            var records = new[] { sibling, leaf, unkeyed };
            var baseRef = typeof(CultRecordRef<InspectLabeledBase>);

            Assert.That(model.IsRecordCandidate(baseRef, leaf), Is.True, "a subtype of the target");
            Assert.That(model.IsRecordCandidate(baseRef, sibling), Is.False, "a sibling type");
            Assert.That(model.IsRecordCandidate(baseRef, unkeyed), Is.False, "an empty key");
            Assert.That(model.RecordCandidates(baseRef, records), Is.EqualTo(records.Where(record => model.IsRecordCandidate(baseRef, record))));
            Assert.That(() => model.IsRecordCandidate(typeof(string), leaf), Throws.ArgumentException);
        }

        [Test]
        public void ABaseMembersLabelReachesItsOverride()
        {
            var member = Model().MembersOf(typeof(InspectLabeled)).Single(candidate => candidate.Name == nameof(InspectLabeled.Tag));

            Assert.That(member.Metadata.Label, Is.EqualTo("Base Label"));
            var claims = new CultInspectorDrawerClaims(new[] { typeof(LabelClaimDrawer) }, typeof(IFakeDrawer));
            Assert.That(claims.Resolve(typeof(string), member.Member).Drawer, Is.EqualTo(typeof(LabelClaimDrawer)), "attribute claims see the base attribute too");
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

        // ---- grouping ----

        private static readonly CultDocumentRegistry GroupRegistry = CultDocumentRegistry.ForTypes(new[]
        {
            typeof(GroupLeaf), typeof(GroupRedeclared), typeof(GroupOptOut), typeof(GroupMixed), typeof(GroupGlobal), typeof(GroupNoCtor), typeof(InspectOther)
        });

        private string _groupPath = string.Empty;

        [SetUp]
        public void PickGroupPath() => _groupPath = Path.Combine(Path.GetTempPath(), $"cultlib-group-{Guid.NewGuid():N}.cc");

        [TearDown]
        public void DeleteGroupPath()
        {
            if (File.Exists(_groupPath))
                File.Delete(_groupPath);
        }

        private static CultInspectorModel GroupModel() => CultCacheMessagePack.CreateInspectorModel(GroupRegistry);

        private CultCache OpenGroups() => CultCacheMessagePack.Create(_groupPath, new CultCacheOpenOptions { Registry = GroupRegistry });

        private static CultRecordRef<InspectOther> OwnerRef(string key) => new(new CultRecordKey(key));

        private static Task PutOwner(CultCache cache, string key, string name) =>
            cache.UpsertAsync(typeof(InspectOther), new InspectOther { Name = name }, new CultRecordKey(key));

        private static Task PutLeaf(CultCache cache, string key, string name, string? owner, GroupHull hull) =>
            cache.UpsertAsync(typeof(GroupLeaf),
                new GroupLeaf { Name = name, Owner = owner == null ? default : OwnerRef(owner), Hull = hull }, new CultRecordKey(key));

        private static IEnumerable<string> Dump(IEnumerable<CultInspectorRecordGroup> groups) =>
            groups.SelectMany(group => new[]
            {
                new string(' ', group.Depth * 2) + group.Label + " (" + group.Count + ")" +
                (group.Records.Count > 0 ? ": " + string.Join(",", group.Records.Select(record => record.Key.Value)) : string.Empty)
            }.Concat(Dump(group.Children)));

        private static IEnumerable<CultInspectorRecordGroup> Leaves(IEnumerable<CultInspectorRecordGroup> groups) =>
            groups.SelectMany(group => group.Children.Count == 0 ? new[] { group } : Leaves(group.Children));

        private static bool Holds(CultInspectorRecordGroup group, string key) =>
            group.Records.Any(record => record.Key.Value == key) || group.Children.Any(child => Holds(child, key));

        private static IEnumerable<CultInspectorRecordGroup> Containing(IEnumerable<CultInspectorRecordGroup> groups, string key) =>
            groups.Where(group => Holds(group, key)).SelectMany(group => new[] { group }.Concat(Containing(group.Children, key)));

        private async Task<CultCache> SeededGroups()
        {
            var cache = OpenGroups();
            await PutOwner(cache, "o-a", "Alpha");
            await PutOwner(cache, "o-b", "Beta");
            await PutLeaf(cache, "r1", "zeta", "o-b", GroupHull.Cruiser);
            await PutLeaf(cache, "r2", "alpha", "o-a", GroupHull.Frigate);
            await PutLeaf(cache, "r3", "mid", "o-a", GroupHull.Cruiser);
            await PutLeaf(cache, "r4", "beta", "o-a", GroupHull.Cruiser);
            return cache;
        }

        [Test]
        public async Task GroupLevelsNestInDeclaredOrderNotSlotOrder()
        {
            using var cache = await SeededGroups();
            var model = GroupModel();

            Assert.That(model.GroupingOf(typeof(GroupLeaf)).Members.Select(member => member.Name), Is.EqualTo(new[] { "Owner", "Hull" }));
            Assert.That(Dump(model.GroupRecords(typeof(GroupLeaf), cache.AllStoredDocuments)), Is.EqualTo(new[]
            {
                "Alpha (3)",
                "  Cruiser (2): r4,r3",
                "  Frigate (1): r2",
                "Beta (1)",
                "  Cruiser (1): r1"
            }), "an interior node holds no records; a leaf holds its records in candidate order");
        }

        [Test]
        public void GroupADerivedListedTypeGroupsByItsBasesDeclarationAndTheNearestDeclarationWins()
        {
            var model = GroupModel();

            Assert.That(model.GroupingOf(typeof(GroupLeaf)).Members.Select(member => member.Name), Is.EqualTo(new[] { "Owner", "Hull" }), "inherited from GroupBase");
            Assert.That(model.GroupingOf(typeof(GroupRedeclared)).Members.Select(member => member.Name), Is.EqualTo(new[] { "Kind" }));

            var optOut = model.GroupingOf(typeof(GroupOptOut));
            Assert.That(optOut.Members, Is.Empty, "an empty declaration opts out of the base's");
            Assert.That(optOut.Notice, Is.Null);
            Assert.That(model.GroupingOf(typeof(GroupLeaf)), Is.SameAs(model.GroupingOf(typeof(GroupLeaf))), "cached");
        }

        [Test]
        public async Task GroupLeavesPartitionTheCandidatesWhateverTheirGroupedValues()
        {
            using var cache = OpenGroups();
            await PutOwner(cache, "o-a", "Alpha");
            await PutLeaf(cache, "r1", "n1", null, GroupHull.Cruiser);
            await PutLeaf(cache, "r2", "n2", "ghost", (GroupHull)99);
            await PutLeaf(cache, "r3", "n3", "o-a", GroupHull.Frigate);
            var model = GroupModel();
            var records = cache.AllStoredDocuments.ToArray();

            var groups = model.GroupRecords(typeof(GroupLeaf), records);

            Assert.That(Dump(groups), Is.EqualTo(new[]
            {
                "None (1)", "  Cruiser (1): r1",
                "Alpha (1)", "  Frigate (1): r3",
                "Missing ghost (1)", "  99 (1): r2"
            }));
            var candidates = model.RecordCandidates(typeof(CultRecordRef<GroupLeaf>), records);
            Assert.That(Leaves(groups).SelectMany(leaf => leaf.Records), Is.EquivalentTo(candidates), "each candidate once, the owner record is no candidate");
        }

        [Test]
        public async Task GroupRefsGroupByKeyNotLabelWithNoneFirstAndMissingLast()
        {
            using var cache = OpenGroups();
            await PutOwner(cache, "o1", "Same");
            await PutOwner(cache, "o2", "Same");
            await PutOwner(cache, "o3", "Aaa");
            await PutOwner(cache, "o4", "Zzz");
            foreach (var (key, owner) in new[] { ("r1", "o2"), ("r2", "o1"), ("r3", (string?)null), ("r4", "ghost-b"), ("r5", "ghost-a"), ("r6", "o3"), ("r7", "o4") })
                await PutLeaf(cache, key, key, owner, GroupHull.Cruiser);
            var model = GroupModel();

            var roots = model.GroupRecords(typeof(GroupLeaf), cache.AllStoredDocuments);

            Assert.That(roots.Select(group => group.Label),
                Is.EqualTo(new[] { "None", "Aaa", "Same", "Same", "Zzz", "Missing ghost-a", "Missing ghost-b" }));
            Assert.That(roots.Select(group => CultInspectorModel.RecordKey(group.Values[0])), Is.EqualTo(new[] { "", "o3", "o1", "o2", "o4", "ghost-a", "ghost-b" }),
                "equal labels order by key");
            Assert.That(roots.Select(group => group.Id).Distinct().Count(), Is.EqualTo(7));

            var sameId = roots[2].Id;
            await PutOwner(cache, "o1", "Renamed");
            var regrouped = model.GroupRecords(typeof(GroupLeaf), cache.AllStoredDocuments);
            Assert.That(regrouped.Select(group => group.Id), Does.Contain(sameId), "identity is the key, not the label");
            Assert.That(regrouped.Select(group => group.Label), Does.Contain("Renamed"));
        }

        [Test]
        public async Task GroupRefLabelsAreOwnedByTheModel()
        {
            using var cache = OpenGroups();
            await PutOwner(cache, "o1", "Alpha");
            await PutOwner(cache, "blank", " ");
            await PutLeaf(cache, "x", "x", null, GroupHull.Cruiser);
            var model = GroupModel();
            var records = cache.AllStoredDocuments.ToArray();
            var refType = typeof(CultRecordRef<InspectOther>);

            Assert.That(model.RecordRefLabel(refType, default(CultRecordRef<InspectOther>), records), Is.EqualTo("None"));
            Assert.That(model.RecordRefLabel(refType, OwnerRef("o1"), records), Is.EqualTo("Alpha"));
            Assert.That(model.RecordRefLabel(refType, OwnerRef("blank"), records), Is.EqualTo("blank"), "an unnamed record shows its key");
            Assert.That(model.RecordRefLabel(refType, OwnerRef("gone"), records), Is.EqualTo("Missing gone"));
            Assert.That(model.RecordRefLabel(refType, OwnerRef("x"), records), Is.EqualTo("Missing x"), "a record that is no candidate is missing");
            Assert.That(model.RecordRefLabel(refType, null, records), Is.EqualTo("None"));
            Assert.That(() => model.RecordRefLabel(typeof(string), null, records), Throws.ArgumentException);
        }

        [Test]
        public async Task GroupEnumNodesOrderByValueAndScalarLevelsByTheirDefaults()
        {
            using var cache = OpenGroups();
            foreach (var (key, hull) in new[] { ("h1", GroupHull.Frigate), ("h2", GroupHull.Cruiser), ("h3", GroupHull.Carrier), ("h4", (GroupHull)99) })
                await PutLeaf(cache, key, key, null, hull);
            var model = GroupModel();

            Assert.That(model.GroupRecords(typeof(GroupLeaf), cache.AllStoredDocuments).Single().Children.Select(group => group.Label),
                Is.EqualTo(new[] { "Cruiser", "Carrier", "Frigate", "99" }), "by underlying value (10, 20, 30, 99), not by name");

            foreach (var (key, kind, count, flag) in new[]
                     {
                         ("m1", "b", 10, true), ("m2", "A", 9, false), ("m3", "a", 9, true), ("m4", "", -1, false),
                         ("m5", "a", 10, true), ("m6", "a", 10, false), ("m7", "a", -1, true), ("m8", "B", 10, true)
                     })
                await cache.UpsertAsync(typeof(GroupMixed), new GroupMixed { Kind = kind, Count = count, Flag = flag }, new CultRecordKey(key));
            Assert.That(Dump(model.GroupRecords(typeof(GroupMixed), cache.AllStoredDocuments)), Is.EqualTo(new[]
            {
                "(empty) (1)", "  -1 (1)", "    False (1): m4",
                "A (1)", "  9 (1)", "    False (1): m2",
                "a (4)", "  -1 (1)", "    True (1): m7", "  9 (1)", "    True (1): m3", "  10 (2)", "    False (1): m6", "    True (1): m5",
                "B (1)", "  10 (1)", "    True (1): m8",
                "b (1)", "  10 (1)", "    True (1): m1"
            }), "strings ignore case then compare exactly, integers order numerically, false before true");
        }

        [Test]
        public async Task GroupNodeIdsStayDistinctWhateverTheValuesContain()
        {
            using var cache = OpenGroups();
            await cache.UpsertAsync(typeof(GroupMixed), new GroupMixed { Kind = "x/Count=1/Flag=True" }, new CultRecordKey("a"));
            await cache.UpsertAsync(typeof(GroupMixed), new GroupMixed { Kind = "x", Count = 1, Flag = true }, new CultRecordKey("b"));
            await cache.UpsertAsync(typeof(GroupMixed), new GroupMixed { Kind = "aCount0" }, new CultRecordKey("c"));
            await cache.UpsertAsync(typeof(GroupMixed), new GroupMixed { Kind = "a" }, new CultRecordKey("d"));

            var ids = new List<string>();
            void Collect(IEnumerable<CultInspectorRecordGroup> groups)
            {
                foreach (var group in groups)
                {
                    ids.Add(group.Id);
                    Collect(group.Children);
                }
            }

            Collect(GroupModel().GroupRecords(typeof(GroupMixed), cache.AllStoredDocuments));

            Assert.That(ids, Has.Count.EqualTo(12));
            Assert.That(ids.Distinct().Count(), Is.EqualTo(12), "a path never spells another node's id");
        }

        [Test]
        public async Task GroupCreateInGroupRoundTripsIntoTheSameNode()
        {
            using var cache = await SeededGroups();
            await PutLeaf(cache, "r5", "ghosted", "ghost", GroupHull.Carrier);
            var model = GroupModel();
            var groups = model.GroupRecords(typeof(GroupLeaf), cache.AllStoredDocuments);
            var alpha = groups.Single(group => group.Label == "Alpha");
            var alphaCruiser = alpha.Children.Single(group => group.Label == "Cruiser");
            var ghost = groups.Single(group => group.Label == "Missing ghost");

            var deep = (GroupLeaf)model.CreateInGroup(typeof(GroupLeaf), alphaCruiser, out var notice)!;
            Assert.That(notice, Is.Null);
            Assert.That(CultInspectorModel.RecordKey(deep.Owner), Is.EqualTo("o-a"));
            Assert.That(deep.Hull, Is.EqualTo(GroupHull.Cruiser));
            var shallow = (GroupLeaf)model.CreateInGroup(typeof(GroupLeaf), alpha, out _)!;
            Assert.That(CultInspectorModel.RecordKey(shallow.Owner), Is.EqualTo("o-a"));
            var dangling = (GroupLeaf)model.CreateInGroup(typeof(GroupLeaf), ghost, out _)!;
            Assert.That(CultInspectorModel.RecordKey(dangling.Owner), Is.EqualTo("ghost"), "a missing node presets the dangling key");

            await cache.UpsertAsync(typeof(GroupLeaf), deep, new CultRecordKey("deep"));
            await cache.UpsertAsync(typeof(GroupLeaf), shallow, new CultRecordKey("shallow"));
            await cache.UpsertAsync(typeof(GroupLeaf), dangling, new CultRecordKey("dangling"));
            var after = model.GroupRecords(typeof(GroupLeaf), cache.AllStoredDocuments);

            Assert.That(Containing(after, "deep").Select(group => group.Id), Is.EqualTo(new[] { alpha.Id, alphaCruiser.Id }));
            Assert.That(Containing(after, "shallow").Select(group => group.Id), Does.Contain(alpha.Id));
            Assert.That(Containing(after, "dangling").Select(group => group.Id), Does.Contain(ghost.Id));
        }

        [Test]
        public async Task GroupCreateInGroupRefusesANodeOfAnotherGrouping()
        {
            using var cache = await SeededGroups();
            var model = GroupModel();
            var alphaCruiser = model.GroupRecords(typeof(GroupLeaf), cache.AllStoredDocuments)[0].Children[0];

            Assert.That(() => model.CreateInGroup(typeof(GroupRedeclared), alphaCruiser, out _), Throws.ArgumentException, "a deeper path than the grouping has");
            Assert.That(() => model.CreateInGroup(typeof(GroupLeaf), null!, out _), Throws.ArgumentNullException);

            await cache.UpsertAsync(typeof(GroupRedeclared), new GroupRedeclared { Kind = "k" }, new CultRecordKey("g1"));
            var kindNode = model.GroupRecords(typeof(GroupRedeclared), cache.AllStoredDocuments).Single();
            Assert.That(() => model.CreateInGroup(typeof(GroupLeaf), kindNode, out _), Throws.ArgumentException, "same depth, other members");
        }

        [Test]
        public async Task GroupCreateInGroupReportsATypeItCannotCreate()
        {
            using var cache = OpenGroups();
            await cache.UpsertAsync(typeof(GroupNoCtor), new GroupNoCtor("n") { Kind = "k" }, new CultRecordKey("c1"));
            var model = GroupModel();
            var node = model.GroupRecords(typeof(GroupNoCtor), cache.AllStoredDocuments).Single();

            Assert.That(model.CreateInGroup(typeof(GroupNoCtor), node, out var notice), Is.Null);
            Assert.That(notice, Does.Contain("parameterless constructor"));
        }

        [Test]
        public void GroupATypeWithoutADeclarationIsNotGrouped()
        {
            var model = GroupModel();

            var grouping = model.GroupingOf(typeof(InspectOther));

            Assert.That(grouping.ListedType, Is.EqualTo(typeof(InspectOther)));
            Assert.That(grouping.Members, Is.Empty);
            Assert.That(grouping.Notice, Is.Null);
            Assert.That(model.GroupRecords(typeof(InspectOther), Array.Empty<CultStoredDocument>()), Is.Empty);
            Assert.That(() => model.GroupingOf(null!), Throws.ArgumentNullException);
        }

        [Test]
        public async Task GroupANullStringSharesTheEmptyStringsNode()
        {
            using var cache = OpenGroups();
            await cache.UpsertAsync(typeof(GroupMixed), new GroupMixed { Kind = null! }, new CultRecordKey("n"));
            await cache.UpsertAsync(typeof(GroupMixed), new GroupMixed { Kind = "" }, new CultRecordKey("e"));

            var roots = GroupModel().GroupRecords(typeof(GroupMixed), cache.AllStoredDocuments);

            Assert.That(roots.Select(group => group.Label), Is.EqualTo(new[] { "(empty)" }));
            Assert.That(roots.Single().Count, Is.EqualTo(2));
        }

        [TestCase(typeof(GroupUnknown), "Nope")]
        [TestCase(typeof(GroupFloat), "Mass")]
        [TestCase(typeof(GroupFlagsEnum), "Traits")]
        [TestCase(typeof(GroupHiddenMember), "Secret")]
        [TestCase(typeof(GroupReadOnlyMember), "Locked")]
        [TestCase(typeof(GroupDuplicate), "Kind")]
        [TestCase(typeof(GroupValidThenInvalid), "Mass")]
        [TestCase(typeof(GroupGlobal), "global")]
        [TestCase(typeof(GroupGlobalChild), "global")]
        public void GroupInvalidDeclarationsGiveANoticeAndAFlatList(Type listed, string mentions)
        {
            var model = GroupModel();

            var grouping = model.GroupingOf(listed);

            Assert.That(grouping.Members, Is.Empty);
            Assert.That(grouping.Notice, Does.Contain(mentions).And.Contain("CultInspectorGroupBy").And.Contain("not grouped"));
            Assert.That(model.GroupRecords(listed, Array.Empty<CultStoredDocument>()), Is.Empty);
        }

        [Test]
        public async Task GroupAVariantGroupsByItsResolvedValues()
        {
            using var cache = OpenGroups();
            var baseKey = new CultRecordKey("base");
            await PutLeaf(cache, "base", "base", null, GroupHull.Cruiser);
            await cache.UpsertVariantAsync(new CultRecordKey("over"), baseKey, new[] { cache.Override<GroupLeaf>(nameof(GroupLeaf.Name), "over-name"), cache.Override<GroupLeaf>(nameof(GroupLeaf.Hull), GroupHull.Frigate) });
            await cache.UpsertVariantAsync(new CultRecordKey("inherit"), baseKey, new[] { cache.Override<GroupLeaf>(nameof(GroupLeaf.Name), "inheritor") });
            var model = GroupModel();

            Assert.That(Dump(model.GroupRecords(typeof(GroupLeaf), cache.AllStoredDocuments)), Is.EqualTo(new[]
            {
                "None (3)", "  Cruiser (2): base,inherit", "  Frigate (1): over"
            }));

            await PutLeaf(cache, "base", "base", null, GroupHull.Carrier);
            Assert.That(Dump(model.GroupRecords(typeof(GroupLeaf), cache.AllStoredDocuments)), Is.EqualTo(new[]
            {
                "None (3)", "  Carrier (2): base,inherit", "  Frigate (1): over"
            }), "an inheriting variant follows its base; an overriding one stays");
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

        public abstract class InspectLabeledBase
        {
            [Key(1)] [CultInspectorLabel("Base Label")]
            public abstract string Tag { get; set; }
        }

        [CultDocument("tests.inspect_labeled", "tests.inspect_labeled.v1")]
        [MessagePackObject]
        public sealed class InspectLabeled : InspectLabeledBase
        {
            [Key(0)] [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public override string Tag { get; set; } = string.Empty;
        }

        [CultInspectorDrawer(typeof(CultInspectorLabelAttribute))]
        public sealed class LabelClaimDrawer : IFakeDrawer
        {
        }

        [CultDocument("tests.inspect_asset", "tests.inspect_asset.v1")]
        [MessagePackObject]
        public sealed class InspectAsset
        {
            [Key(0)] [CultInspectorAssetGuid(typeof(Uri))]
            public string Guid = string.Empty;
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

        public enum GroupHull
        {
            Frigate = 30,
            Cruiser = 10,
            Carrier = 20
        }

        [Flags]
        public enum GroupTraits
        {
            Fast = 1,
            Armored = 2
        }

        [CultInspectorGroupBy(nameof(GroupLeaf.Owner), nameof(GroupLeaf.Hull))]
        public abstract class GroupBase
        {
        }

        [CultDocument("tests.group_leaf", "tests.group_leaf.v1")]
        [MessagePackObject]
        public sealed class GroupLeaf : GroupBase
        {
            [Key(0)] [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public GroupHull Hull;

            [Key(2)]
            public CultRecordRef<InspectOther> Owner;
        }

        [CultInspectorGroupBy("Kind")]
        [CultDocument("tests.group_redeclared", "tests.group_redeclared.v1")]
        [MessagePackObject]
        public sealed class GroupRedeclared : GroupBase
        {
            [Key(0)]
            public string Kind = string.Empty;
        }

        [CultInspectorGroupBy]
        [CultDocument("tests.group_opt_out", "tests.group_opt_out.v1")]
        [MessagePackObject]
        public sealed class GroupOptOut : GroupBase
        {
            [Key(0)]
            public string Kind = string.Empty;
        }

        [CultInspectorGroupBy("Kind", "Count", "Flag")]
        [CultDocument("tests.group_mixed", "tests.group_mixed.v1")]
        [MessagePackObject]
        public sealed class GroupMixed
        {
            [Key(0)]
            public string Kind = string.Empty;

            [Key(1)]
            public int Count;

            [Key(2)]
            public bool Flag;
        }

        [CultInspectorGroupBy("Kind")]
        [CultGlobal]
        [CultDocument("tests.group_global", "tests.group_global.v1")]
        [MessagePackObject]
        public class GroupGlobal
        {
            [Key(0)]
            public string Kind = string.Empty;
        }

        [CultInspectorGroupBy("Kind")]
        public sealed class GroupGlobalChild : GroupGlobal
        {
        }

        [CultInspectorGroupBy("Kind")]
        [CultDocument("tests.group_no_ctor", "tests.group_no_ctor.v1")]
        [MessagePackObject]
        public sealed class GroupNoCtor
        {
            [SerializationConstructor]
            public GroupNoCtor(string name)
            {
                Name = name;
            }

            [Key(0)]
            public string Name { get; }

            [Key(1)]
            public string Kind = string.Empty;
        }

        [MessagePackObject]
        public class GroupShape
        {
            [Key(0)]
            public string Kind = string.Empty;

            [Key(1)]
            public float Mass;

            [Key(2)]
            public GroupTraits Traits;

            [Key(3)] [CultInspectorHidden]
            public int Secret;

            [Key(4)] [CultInspectorReadOnly]
            public int Locked;
        }

        [CultInspectorGroupBy("Nope")]
        public sealed class GroupUnknown : GroupShape
        {
        }

        [CultInspectorGroupBy("Mass")]
        public sealed class GroupFloat : GroupShape
        {
        }

        [CultInspectorGroupBy("Traits")]
        public sealed class GroupFlagsEnum : GroupShape
        {
        }

        [CultInspectorGroupBy("Secret")]
        public sealed class GroupHiddenMember : GroupShape
        {
        }

        [CultInspectorGroupBy("Locked")]
        public sealed class GroupReadOnlyMember : GroupShape
        {
        }

        [CultInspectorGroupBy("Kind", "Kind")]
        public sealed class GroupDuplicate : GroupShape
        {
        }

        [CultInspectorGroupBy("Kind", "Mass")]
        public sealed class GroupValidThenInvalid : GroupShape
        {
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
