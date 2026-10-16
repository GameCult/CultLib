using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using MessagePack;

namespace GameCult.Caching
{
    // The identity of one element of an object list. A string member, keyed like any other, on every object type that
    // sits in a list of a registered document type (union bases included: every concrete subtype carries the member).
    // [CultElementId(nameof(Offset))] declares the id derived, never random: an unset id becomes the invariant-culture text of
    // that member (a keyed string or number on the same type, unique within its list). Content-addressed elements use it so the
    // same content always encodes to the same bytes.
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class CultElementIdAttribute : Attribute
    {
        public CultElementIdAttribute(string? derivedFrom = null)
        {
            DerivedFrom = derivedFrom;
        }

        public string? DerivedFrom { get; }
    }

    // A record the id rule refuses: a duplicate id in one list, a random id that is not 12 lowercase hex characters, or a derived
    // id whose source member is null or empty. It names the record, the list (record key, member slot, dictionary key and
    // element index) and, where there is one, the element id and the source member.
    public sealed class CultElementIdException : InvalidOperationException
    {
        public CultElementIdException(string message, string recordKey, string listPath, string? elementId = null, string? member = null)
            : base(message)
        {
            RecordKey = recordKey;
            ListPath = listPath;
            ElementId = elementId;
            Member = member;
        }

        public string RecordKey { get; }
        public string ListPath { get; }
        public string? ElementId { get; }
        public string? Member { get; }
    }

    // The one owner of element identity: which types need an id, and how unset ids are filled.
    // A random id is 12 lowercase hex characters (48 bits); a derived id is the invariant text of its source and only has to be
    // non-empty. Either need only be unique within one list. A write mints a random id; a load of a store written before ids
    // existed mints from SHA-256(record key, element path), so every reader of the same store mints the same id and a reload is
    // identical to what was read. A write and a load refuse the same things.
    internal static class CultElementIds
    {
        private const int IdBytes = 6;

        // The ids a pass would fill, decided before anything is written: nothing changes until Apply, and Undo restores what
        // Apply replaced.
        internal sealed class IdPlan
        {
            private readonly List<(object Item, Shape Shape, string? Before, string Id)> _fills = new();

            public int Count => _fills.Count;

            internal void Add(object item, Shape shape, string? before, string id) => _fills.Add((item, shape, before, id));

            public void Apply()
            {
                foreach (var (item, shape, _, id) in _fills)
                    shape.SetId!(item, id);
            }

            public void Undo()
            {
                foreach (var (item, shape, before, _) in _fills)
                    shape.SetId!(item, before!);
            }
        }

        internal sealed class Shape
        {
            public Func<object, string?>? GetId;
            public Action<object, string>? SetId;
            public Func<object, string?>? Derive;
            public string? DerivedFrom;
            public (int Slot, Type Type, Func<object, object?> Get)[] Walk = Array.Empty<(int, Type, Func<object, object?>)>();
        }

        private static readonly ConcurrentDictionary<Type, Shape> Shapes = new();

        // ---- registration: which element types must carry an id ----

        internal sealed class Survey
        {
            public List<string> Problems { get; } = new();

            // Every type whose bytes carry an element id: the types a walk descends through to reach an object list, the list's
            // element types and every concrete type under them. A serialization codec must write all of these by their [Key]
            // layout, so it checks them against the formatters it composed.
            public List<Type> IdPath { get; } = new();

            // The concrete element types already judged in this document, so each is refused at most once however many lists hold it.
            internal HashSet<Type> Judged { get; } = new();
        }

        // What the walk saw, settled once it is complete: a type reaches a list only through types reached later, and a type
        // can sit in a cycle, so the path is a fixed point over these edges and not a stack.
        private sealed class Graph
        {
            public List<(Type Owner, MemberInfo? Member, Type Child, Type[] Through)> Edges { get; } = new();
            public List<(Type Owner, MemberInfo Member, Type Element, List<Type> Concrete, Type[] Through)> Lists { get; } = new();

            // Keyed types that also enumerate objects: the walk reads them by key, so the codec must confirm MessagePack does too.
            public List<Type> Enumerating { get; } = new();
        }

        private static readonly ConcurrentDictionary<Type, bool> IdCapable = new();

        // A document type whose walk reaches no object list can hold no element id, so a write never decodes one to ask.
        internal static bool CanHoldIds(Type documentType) => IdCapable.GetOrAdd(documentType, type => Inspect(type).IdPath.Count > 0);

        // Every reason this document type cannot be registered under the id rule, one per offending concrete element type.
        internal static Survey Inspect(Type documentType)
        {
            var survey = new Survey();
            var graph = new Graph();
            VisitObject(documentType, documentType, new HashSet<Type>(), survey, graph);
            Settle(documentType, survey, graph);
            return survey;
        }

        private static void VisitObject(Type documentType, Type type, HashSet<Type> seen, Survey survey, Graph graph)
        {
            if (!seen.Add(type))
                return;
            if (EnumeratesObjects(type))
                graph.Enumerating.Add(type);
            foreach (var (member, memberType) in KeyedMembers(type))
                VisitMemberType(documentType, type, member, memberType, Array.Empty<Type>(), seen, survey, graph);
            foreach (var union in type.GetCustomAttributes<UnionAttribute>(true))
            {
                graph.Edges.Add((type, null, union.SubType, Array.Empty<Type>()));
                VisitObject(documentType, union.SubType, seen, survey, graph);
            }
        }

        private static void VisitMemberType(Type documentType, Type owner, MemberInfo member, Type memberType, Type[] through, HashSet<Type> seen, Survey survey, Graph graph)
        {
            memberType = Nullable.GetUnderlyingType(memberType) ?? memberType;
            // A sequence or map whose element is itself (an unkeyed Node : IEnumerable<Node>) ends the type walk here; without
            // this it would recurse until the stack overflows.
            if (Array.IndexOf(through, memberType) >= 0)
                return;
            var (kind, inner) = Classify(memberType);
            switch (kind)
            {
                case Kind.Leaf:
                    // A shape the walk does not own is one whole value, never refused (q3 amended).
                    return;
                case Kind.Map:
                    VisitMemberType(documentType, owner, member, inner, through.Append(memberType).ToArray(), seen, survey, graph);
                    return;
                case Kind.Sequence when IsElement(inner):
                    var concretes = ConcreteTypes(inner).ToList();
                    graph.Lists.Add((owner, member, inner, concretes, through.Append(memberType).ToArray()));
                    foreach (var concrete in concretes)
                    {
                        if (!survey.Judged.Add(concrete))
                            continue;
                        var problem = IsElement(concrete) ? IdProblem(concrete) : UnkeyedElementProblem;
                        if (problem != null)
                            survey.Problems.Add($"Cult document {documentType.Name}: list element type {concrete.Name} (in {owner.Name}.{member.Name}) {problem}");
                    }

                    VisitObject(documentType, inner, seen, survey, graph);
                    return;
                case Kind.Sequence:
                    VisitMemberType(documentType, owner, member, inner, through.Append(memberType).ToArray(), seen, survey, graph);
                    return;
                default:
                    graph.Edges.Add((owner, member, memberType, through));
                    VisitObject(documentType, memberType, seen, survey, graph);
                    return;
            }
        }

        private const string UnkeyedElementProblem =
            "is written by a formatter the cache does not own, so its element id would never reach disk; mark it [MessagePackObject] with [Key] members, or keep it out of lists.";

        // The id path: every type that reaches an object list through members MessagePack writes by key, the lists' element and
        // concrete types, and the keyed types that enumerate objects. The codec verifies that MessagePack writes each of them the
        // way Classify reads it; it never decides ownership.
        private static void Settle(Type documentType, Survey survey, Graph graph)
        {
            var lists = graph.Lists;
            var reaching = new HashSet<Type>(lists.Select(list => list.Owner));
            var through = new List<Type>(lists.SelectMany(list => list.Through));
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var (owner, _, child, via) in graph.Edges)
                {
                    if (!reaching.Contains(child))
                        continue;
                    through.AddRange(via);
                    grew |= reaching.Add(owner);
                }
            }

            var path = new List<Type>();
            void Add(Type type)
            {
                if (!path.Contains(type))
                    path.Add(type);
            }

            foreach (var type in reaching)
                Add(type);
            foreach (var type in through)
                Add(type);
            foreach (var list in lists)
            {
                Add(list.Element);
                foreach (var concrete in list.Concrete)
                    Add(concrete);
            }

            // A keyed type the walk reads by key but whose enumeration holds objects is on the path: the codec asks whether
            // MessagePack writes it by key too.
            foreach (var type in graph.Enumerating)
                Add(type);

            survey.IdPath.AddRange(path);
        }

        // The types an element can actually be: the element type itself when it can be instantiated, and every union subtype
        // under it. Shapes are built from these, so registration judges exactly what minting will meet.
        private static IEnumerable<Type> ConcreteTypes(Type element)
        {
            var seen = new HashSet<Type>();
            return Expand(element).ToList();

            IEnumerable<Type> Expand(Type type)
            {
                if (!seen.Add(type))
                    yield break;
                if (!type.IsAbstract && !type.IsInterface)
                    yield return type;
                foreach (var union in type.GetCustomAttributes<UnionAttribute>(true))
                {
                    foreach (var subtype in Expand(union.SubType))
                        yield return subtype;
                }
            }
        }

        private static string? IdProblem(Type element)
        {
            // Candidates come from every instance member up the base chain, public or not, so an id MessagePack does not write
            // is named as such instead of being reported missing. An override or a hiding member is one member, the most derived.
            var ids = AllInstanceMembers(element).Where(member => Declares(member, typeof(CultElementIdAttribute)))
                .GroupBy(member => member.Name).Select(group => group.First()).ToArray();
            if (ids.Length == 0)
                return "has no [CultElementId] member; add a public string member with [Key] and [CultElementId] so each element has an identity.";
            if (ids.Length > 1)
                return $"has two [CultElementId] members ({ids[0].Name}, {ids[1].Name}); an element has one identity.";
            var id = ids[0];
            if (Declares(id, typeof(MessagePackFormatterAttribute)))
                return $"declares [MessagePackFormatter] on its [CultElementId] member {id.Name}, so the id would not be written by its [Key].";
            if (TypeOf(id) != typeof(string))
                return $"declares [CultElementId] on {id.Name}, which is not a string.";
            if (KeyOf(id) is not { } idKey)
                return $"declares [CultElementId] on {id.Name}, which has no integer [Key], so it is never persisted.";
            if (MembersOf(element).FirstOrDefault(member => member.Name == id.Name && member.MemberType == id.MemberType) is not { } written)
                return $"declares [CultElementId] on {id.Name}, which MessagePack does not write ([IgnoreMember], or non-public on a type without [MessagePackObject(AllowPrivate = true)]); the id would never reach disk.";
            id = written;
            var sharing = KeyedMembers(element).FirstOrDefault(entry => entry.Member != id && KeyOf(entry.Member) == idKey);
            if (sharing.Member != null)
                return $"declares [CultElementId] on {id.Name} with [Key({idKey})], which {sharing.Member.Name} also uses; an id needs a key of its own.";
            if (id is PropertyInfo { SetMethod: null } or FieldInfo { IsInitOnly: true })
                return $"declares [CultElementId] on {id.Name}, which cannot be assigned; the cache mints into it.";
            if (id is PropertyInfo { SetMethod.IsPublic: false } && !AllowsPrivate(element))
                return $"declares [CultElementId] on {id.Name}, whose setter is not public: MessagePack writes the id but does not read it back, so every load would mint a different one. Make the setter public (init is fine).";
            var source = id.GetCustomAttribute<CultElementIdAttribute>(true)!.DerivedFrom;
            if (source != null)
            {
                var from = MembersOf(element).FirstOrDefault(member => member.Name == source);
                if (from == null || KeyOf(from) == null)
                    return $"derives its [CultElementId] from {source}, which is not a keyed member of the type.";
                var kind = Nullable.GetUnderlyingType(TypeOf(from)) ?? TypeOf(from);
                if (kind != typeof(string) && !kind.IsPrimitive)
                    return $"derives its [CultElementId] from {source}, which is not a string or a number.";
            }

            return null;
        }

        // ---- write: what the stored form holds ----

        // True when some element under root holds a non-empty id: the one fact a store marks its header by. It reads the value
        // as it is now, refuses nothing and changes nothing, so a writer asks it of exactly what it is about to write. The walk
        // follows declared types, as registration does; a value's own type picks only a union arm.
        internal static bool Holds(Type declared, object? root)
        {
            if (root == null)
                return false;
            var (kind, inner) = Classify(declared);
            switch (kind)
            {
                case Kind.Leaf:
                    return false;
                case Kind.Map:
                    return MapEntries(declared)(root).Any(entry => Holds(inner, entry.Value));
                case Kind.Sequence:
                    var elements = IsElement(inner);
                    if (!elements && Classify(inner).Kind == Kind.Leaf)
                        return false;
                    foreach (var item in (IEnumerable)root)
                    {
                        if (item == null)
                            continue;
                        if (elements && OwnerShape(inner, item).GetId is { } getId && !string.IsNullOrEmpty(getId(item)))
                            return true;
                        if (Holds(inner, item))
                            return true;
                    }

                    return false;
                default:
                    return OwnerShape(declared, root).Walk.Any(member => Holds(member.Type, member.Get(root)));
            }
        }

        // ---- write and load: mint and check ----

        // Decides every unset element id under root, and refuses what the rule refuses, without changing anything: a random id
        // that is not 12 lowercase hex characters, a duplicate id in one list, a derived id whose source is null or empty.
        // rootPath (the record key) seeds deterministic minting and names the record in a refusal. An element object that sits
        // twice in one list is refused too: planning would give it two ids and it would then be a duplicate on load.
        internal static IdPlan Plan(Type declared, object? root, string rootPath, bool deterministic)
        {
            var plan = new IdPlan();
            Visit(declared, root, rootPath);
            return plan;

            void Visit(Type type, object? value, string path)
            {
                if (value == null)
                    return;
                var (kind, inner) = Classify(type);
                switch (kind)
                {
                    case Kind.Map:
                        foreach (var entry in MapEntries(type)(value))
                            Visit(inner, entry.Value, path + "{" + Convert.ToString(entry.Key, CultureInfo.InvariantCulture) + "}");
                        break;
                    case Kind.Sequence when IsElement(inner):
                        Elements(inner, (IEnumerable)value, path);
                        break;
                    case Kind.Sequence:
                        if (Classify(inner).Kind == Kind.Leaf)
                            break;
                        var index = 0;
                        foreach (var item in (IEnumerable)value)
                            Visit(inner, item, path + "/" + (index++).ToString(CultureInfo.InvariantCulture));
                        break;
                    case Kind.Owner:
                        foreach (var (slot, memberType, get) in OwnerShape(type, value).Walk)
                            Visit(memberType, get(value), path + "." + slot.ToString(CultureInfo.InvariantCulture));
                        break;
                }
            }

            void Elements(Type element, IEnumerable list, string path)
            {
                var items = list.Cast<object?>().ToArray();
                var taken = new HashSet<string>(StringComparer.Ordinal);
                var objects = new HashSet<object>(ReferenceComparer.Instance);
                foreach (var item in items)
                {
                    if (item == null || OwnerShape(element, item) is not { GetId: { } getId } shape)
                        continue;
                    if (!objects.Add(item))
                        throw new CultElementIdException(
                            $"Record {rootPath}: the same {item.GetType().Name} object appears twice in the list at {path}; an element is one object with one id.",
                            rootPath, path);
                    var id = getId(item);
                    if (string.IsNullOrEmpty(id))
                        continue;
                    if (shape.Derive == null && !IsRandomId(id!))
                        throw new CultElementIdException(
                            $"Record {rootPath}: element id '{id}' in the list at {path} is not 12 lowercase hex characters. An id you write by hand must be 12 lowercase hex; for readable ids, derive them from a member with [CultElementId(nameof(Member))].",
                            rootPath, path, id);
                    if (!taken.Add(id!))
                        throw Duplicate(id!, path);
                }

                for (var index = 0; index < items.Length; index++)
                {
                    var item = items[index];
                    if (item == null)
                        continue;
                    var itemPath = path + "/" + index.ToString(CultureInfo.InvariantCulture);
                    if (OwnerShape(element, item) is { SetId: { } , GetId: { } get } itemShape &&
                        string.IsNullOrEmpty(get(item)))
                    {
                        string id;
                        if (itemShape.Derive != null)
                        {
                            id = itemShape.Derive(item) ?? string.Empty;
                            if (id.Length == 0)
                                throw new CultElementIdException(
                                    $"Record {rootPath}: the element at {itemPath} derives its id from {itemShape.DerivedFrom}, which is null or empty.",
                                    rootPath, itemPath, member: itemShape.DerivedFrom);
                            if (!taken.Add(id))
                                throw Duplicate(id, path);
                        }
                        else
                            id = Mint(itemPath, deterministic, rootPath, taken);
                        plan.Add(item, itemShape, get(item), id);
                    }

                    Visit(element, item, itemPath);
                }
            }

            CultElementIdException Duplicate(string id, string path) => new(
                $"Record {rootPath}: element id '{id}' appears twice in the list at {path}; an element id is unique within its list.",
                rootPath, path, id);
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new();

            bool IEqualityComparer<object>.Equals(object? left, object? right) => ReferenceEquals(left, right);

            int IEqualityComparer<object>.GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }

        private static bool IsRandomId(string id)
        {
            if (id.Length != IdBytes * 2)
                return false;
            foreach (var character in id)
            {
                if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                    return false;
            }

            return true;
        }

        private static string Mint(string itemPath, bool deterministic, string rootPath, HashSet<string> taken)
        {
            for (var attempt = 0; ; attempt++)
            {
                var bytes = new byte[IdBytes];
                if (deterministic)
                {
                    using var sha = SHA256.Create();
                    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(rootPath + "|" + itemPath + "|" + attempt.ToString(CultureInfo.InvariantCulture)));
                    Array.Copy(hash, bytes, IdBytes);
                }
                else
                    RandomBytes(bytes);
                var id = Hex(bytes);
                if (taken.Add(id))
                    return id;
            }
        }

        private static void RandomBytes(byte[] bytes)
        {
            using var random = RandomNumberGenerator.Create();
            random.GetBytes(bytes);
        }

        private static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes)
                text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        // ---- shapes ----

        private static Shape ShapeOf(Type type) => Shapes.GetOrAdd(type, Build);

        // The shape of an owner or element a walk holds under a declared type: a union base writes its arm, so the value's own
        // type picks the arm; any other declared type is written by its own [Key] layout, whatever a subclass adds.
        private static Shape OwnerShape(Type declared, object value)
        {
            declared = Nullable.GetUnderlyingType(declared) ?? declared;
            return declared.IsDefined(typeof(UnionAttribute), false) ? ShapeOf(value.GetType()) : ShapeOf(declared);
        }

        private static Shape Build(Type type)
        {
            var shape = new Shape();
            var idMember = MembersOf(type).FirstOrDefault(member => Declares(member, typeof(CultElementIdAttribute)));
            if (idMember != null)
            {
                shape.GetId = idMember is FieldInfo field ? o => (string?)field.GetValue(o) : o => (string?)((PropertyInfo)idMember).GetValue(o);
                shape.SetId = idMember is FieldInfo f ? (o, v) => f.SetValue(o, v) : (o, v) => ((PropertyInfo)idMember).SetValue(o, v);
                if (idMember.GetCustomAttribute<CultElementIdAttribute>(true)!.DerivedFrom is { } source &&
                    MembersOf(type).FirstOrDefault(member => member.Name == source) is { } from)
                {
                    shape.DerivedFrom = source;
                    shape.Derive = o => Convert.ToString(from is FieldInfo ff ? ff.GetValue(o) : ((PropertyInfo)from).GetValue(o), CultureInfo.InvariantCulture);
                }
            }

            shape.Walk = KeyedMembers(type)
                .Where(entry => Classify(entry.Type).Kind != Kind.Leaf)
                .Select(entry => (Slot: KeyOf(entry.Member)!.Value, entry.Type, Get: entry.Member is FieldInfo fi
                    ? (Func<object, object?>)(o => fi.GetValue(o))
                    : o => ((PropertyInfo)entry.Member).GetValue(o)))
                .ToArray();
            return shape;
        }

        // The members MessagePack's StandardResolver writes for an integer-keyed type, none of them [IgnoreMember]. Without
        // AllowPrivate: public instance fields, and public instance properties with a public getter. With it, read from the most
        // derived [MessagePackObject] declaration (a derived type without it is written without it, probe NoApDerived): every
        // instance field and indexer-free property up the base chain at any visibility, the most derived of a name.
        // TheWalkReadsTheMembersMessagePackWrites checks this set against MessagePack's own member discovery.
        private static IEnumerable<MemberInfo> MembersOf(Type type) =>
            AllowsPrivate(type)
                ? AllInstanceMembers(type).Where(member => !Declares(member, typeof(IgnoreMemberAttribute)))
                    .GroupBy(member => member.Name).Select(group => group.First())
                : PublicMembersOf(type);

        private static bool AllowsPrivate(Type type) => type.GetCustomAttribute<MessagePackObjectAttribute>(true)?.AllowPrivate == true;

        private static IEnumerable<MemberInfo> PublicMembersOf(Type type) =>
            type.GetFields(BindingFlags.Instance | BindingFlags.Public).Cast<MemberInfo>()
                .Concat(type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod is { IsPublic: true }))
                .Where(member => !Declares(member, typeof(IgnoreMemberAttribute)));

        // Every instance field and indexer-free property declared on the type or any base, most derived first.
        private static IEnumerable<MemberInfo> AllInstanceMembers(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var level = type; level != null && level != typeof(object); level = level.BaseType)
            {
                foreach (var field in level.GetFields(flags))
                    yield return field;
                foreach (var property in level.GetProperties(flags).Where(property => property.GetIndexParameters().Length == 0))
                    yield return property;
            }
        }

        // The [Key] members MessagePack writes, for the oracle test that checks them against MessagePack.
        internal static IEnumerable<MemberInfo> WrittenKeyedMembers(Type type) => MembersOf(type).Where(member => KeyOf(member) != null);

        private static IEnumerable<(MemberInfo Member, Type Type)> KeyedMembers(Type type) =>
            WrittenKeyedMembers(type).Where(member => !Declares(member, typeof(MessagePackFormatterAttribute)))
                .Select(member => (member, TypeOf(member)));

        // An attribute a member declares, or inherits through an override: MemberInfo.IsDefined ignores inherit for properties, so an
        // override of a base virtual member would not see its base's attribute.
        private static bool Declares(MemberInfo member, Type attribute) => Attribute.IsDefined(member, attribute, inherit: true);

        // True when the walk reads the type by its [Key] members: the codec compares this with how MessagePack writes it.
        internal static bool WalksAsOwner(Type type) => Classify(Nullable.GetUnderlyingType(type) ?? type).Kind == Kind.Owner;

        private static bool EnumeratesObjects(Type type) =>
            (MapTypes(type)?.Value ?? ElementTypeOf(type)) is { } inner && Classify(inner).Kind != Kind.Leaf;

        private static int? KeyOf(MemberInfo member) => member.GetCustomAttribute<KeyAttribute>(true)?.IntKey;

        private static Type TypeOf(MemberInfo member) => member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;

        // ---- type classification ----

        private enum Kind { Leaf, Map, Sequence, Owner }

        private static readonly ConcurrentDictionary<Type, (Kind Kind, Type Inner)> Classes = new();

        // The walk's model of a declared type: registration, Plan, Holds and the codec's id path all go through it. A
        // [MessagePackObject] or [Union] type is read by key even when it is also IEnumerable<T>; the codec checks, for every type
        // on the id path, that MessagePack writes it the way this model reads it, and refuses the document when it does not.
        // Inner is a map's value type, a sequence's element type, or the type itself for an owner.
        // The owned shapes are a closed list (Owner, Sequence, Map); every other shape is Leaf: one whole value with no element
        // ids, replaced whole by a variant (q3 amended 2026-10-16). When in doubt the answer is Leaf.
        //   Leaf      strings (an IEnumerable<char> MessagePack writes whole), a type written whole (IsWrittenWhole: attribute-
        //             formatted, [DataContract], string-keyed), a struct or a class or interface with no [MessagePackObject] or
        //             [Union] and no collection shape (primitives, enums, float2, DateTime, ImmutableArray, ArraySegment, Memory,
        //             KeyValuePair, tuples, Lazy, unkeyed classes), and object, which is untyped on disk.
        //   Map       a class or interface implementing IDictionary<,> or IReadOnlyDictionary<,>.
        //   Sequence  an array or an IEnumerable<T> class or interface.
        //   Owner     a class, interface or struct MessagePack serializes by integer key. A keyed struct is walked as an owner;
        //             it is never an element (IsElement), since a boxed copy cannot carry a minted id.
        private static (Kind Kind, Type Inner) Classify(Type declared) => Classes.GetOrAdd(declared, static type =>
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string))
                return (Kind.Leaf, type);
            if (IsWrittenWhole(type))
                return (Kind.Leaf, type);
            if (IsKeyed(type))
                return (Kind.Owner, type);
            if (type.IsValueType)
                return (Kind.Leaf, type);
            if (MapTypes(type) is { } map)
                return (Kind.Map, map.Value);
            if (ElementTypeOf(type) is { } element)
                return (Kind.Sequence, element);
            return (Kind.Leaf, type);
        });

        // A type MessagePack writes as one value the walk cannot look into: its own formatter ([MessagePackFormatter] on the type,
        // inherited), [DataContract] on a type that is not [MessagePackObject] (the object resolver writes its members), or string
        // keys (KeyAsPropertyName, or a [Key] with a string key on a member MessagePack writes: a private or [IgnoreMember] one is not written). Such a type is Leaf, even when it is also enumerable.
        private static bool IsWrittenWhole(Type type) =>
            type.IsDefined(typeof(MessagePackFormatterAttribute), true) ||
            (type.IsDefined(typeof(DataContractAttribute), true) && !type.IsDefined(typeof(MessagePackObjectAttribute), true)) ||
            type.GetCustomAttribute<MessagePackObjectAttribute>(true)?.KeyAsPropertyName == true ||
            MembersOf(type).Any(member => member.GetCustomAttribute<KeyAttribute>(true)?.StringKey != null);

        // A type MessagePack serializes by key: [MessagePackObject] or [Union].
        private static bool IsKeyed(Type type) =>
            type.IsDefined(typeof(MessagePackObjectAttribute), true) || type.IsDefined(typeof(UnionAttribute), true);

        // What an object list holds: a keyed class or interface. Never a struct.
        private static bool IsElement(Type type) => !type.IsValueType && Classify(type).Kind == Kind.Owner;

        private static Type? ElementTypeOf(Type type)
        {
            if (type.IsArray)
                return type.GetElementType();
            foreach (var candidate in type.GetInterfaces().Append(type))
            {
                if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return candidate.GetGenericArguments()[0];
            }

            return null;
        }

        // The key and value types of a map MessagePack writes as a map: IDictionary<,> or IReadOnlyDictionary<,>.
        private static (Type Key, Type Value)? MapTypes(Type type)
        {
            foreach (var candidate in type.GetInterfaces().Append(type))
            {
                if (candidate.IsGenericType &&
                    (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))
                {
                    var arguments = candidate.GetGenericArguments();
                    return (arguments[0], arguments[1]);
                }
            }

            return null;
        }

        private static readonly ConcurrentDictionary<Type, Func<object, IEnumerable<(object? Key, object? Value)>>> MapReaders = new();

        // A map's entries read through the generic interface Classify found, so no map shape is recognised twice.
        private static Func<object, IEnumerable<(object? Key, object? Value)>> MapEntries(Type map) => MapReaders.GetOrAdd(map, static type =>
        {
            var (key, value) = MapTypes(type)!.Value;
            return (Func<object, IEnumerable<(object? Key, object? Value)>>)Delegate.CreateDelegate(
                typeof(Func<object, IEnumerable<(object? Key, object? Value)>>),
                typeof(CultElementIds).GetMethod(nameof(ReadMap), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(key, value));
        });

        private static IEnumerable<(object? Key, object? Value)> ReadMap<TKey, TValue>(object map) =>
            ((IEnumerable<KeyValuePair<TKey, TValue>>)map).Select(entry => ((object?)entry.Key, (object?)entry.Value));
    }
}
