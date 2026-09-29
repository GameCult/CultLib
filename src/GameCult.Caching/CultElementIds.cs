using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
            public (int Slot, Func<object, object?> Get)[] Walk = Array.Empty<(int, Func<object, object?>)>();
        }

        private static readonly ConcurrentDictionary<Type, Shape> Shapes = new();

        // ---- registration: which element types must carry an id ----

        internal sealed class Survey
        {
            public List<string> Problems { get; } = new();
        }

        // Every reason this document type cannot be registered under the id rule, one per offending concrete element type.
        internal static Survey Inspect(Type documentType)
        {
            var survey = new Survey();
            VisitObject(documentType, documentType, new HashSet<Type>(), survey);
            return survey;
        }

        private static void VisitObject(Type documentType, Type type, HashSet<Type> seen, Survey survey)
        {
            if (!seen.Add(type))
                return;
            foreach (var (member, memberType) in KeyedMembers(type))
                VisitMemberType(documentType, type, member, memberType, seen, survey);
            foreach (var union in type.GetCustomAttributes<UnionAttribute>(true))
                VisitObject(documentType, union.SubType, seen, survey);
        }

        private static void VisitMemberType(Type documentType, Type owner, MemberInfo member, Type memberType, HashSet<Type> seen, Survey survey)
        {
            memberType = Nullable.GetUnderlyingType(memberType) ?? memberType;
            if (IsLeaf(memberType))
                return;
            if (DictionaryValueType(memberType) is { } valueType)
            {
                VisitMemberType(documentType, owner, member, valueType, seen, survey);
                return;
            }

            if (ElementTypeOf(memberType) is { } element)
            {
                if (IsObjectType(element))
                {
                    foreach (var concrete in ConcreteTypes(element))
                    {
                        var problem = IdProblem(concrete);
                        if (problem != null)
                            survey.Problems.Add($"Cult document {documentType.Name}: list element type {concrete.Name} (in {owner.Name}.{member.Name}) {problem}");
                    }

                    VisitObject(documentType, element, seen, survey);
                }
                else
                    VisitMemberType(documentType, owner, member, element, seen, survey);
                return;
            }

            if (IsObjectType(memberType))
                VisitObject(documentType, memberType, seen, survey);
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
            var ids = MembersOf(element).Where(member => member.IsDefined(typeof(CultElementIdAttribute), true)).ToArray();
            if (ids.Length == 0)
                return "has no [CultElementId] member; add a public string member with [Key] and [CultElementId] so each element has an identity.";
            if (ids.Length > 1)
                return $"has two [CultElementId] members ({ids[0].Name}, {ids[1].Name}); an element has one identity.";
            var id = ids[0];
            if (TypeOf(id) != typeof(string))
                return $"declares [CultElementId] on {id.Name}, which is not a string.";
            if (KeyOf(id) is not { } idKey)
                return $"declares [CultElementId] on {id.Name}, which has no integer [Key], so it is never persisted.";
            var sharing = KeyedMembers(element).FirstOrDefault(entry => entry.Member != id && KeyOf(entry.Member) == idKey);
            if (sharing.Member != null)
                return $"declares [CultElementId] on {id.Name} with [Key({idKey})], which {sharing.Member.Name} also uses; an id needs a key of its own.";
            if (id is PropertyInfo { SetMethod: null } or FieldInfo { IsInitOnly: true })
                return $"declares [CultElementId] on {id.Name}, which cannot be assigned; the cache mints into it.";
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
        // as it is now, refuses nothing and changes nothing, so a writer asks it of exactly what it is about to write.
        internal static bool Holds(object? root)
        {
            if (root == null)
                return false;
            var type = root.GetType();
            if (IsLeaf(type))
                return false;
            if (root is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (Holds(entry.Value))
                        return true;
                }

                return false;
            }

            if (root is IEnumerable list)
            {
                foreach (var item in list)
                {
                    if (item != null && IsObjectType(item.GetType()) && ShapeOf(item.GetType()).GetId is { } getId &&
                        !string.IsNullOrEmpty(getId(item)))
                        return true;
                    if (Holds(item))
                        return true;
                }

                return false;
            }

            return IsObjectType(type) && ShapeOf(type).Walk.Any(member => Holds(member.Get(root)));
        }

        // ---- write and load: mint and check ----

        // Decides every unset element id under root, and refuses what the rule refuses, without changing anything: a random id
        // that is not 12 lowercase hex characters, a duplicate id in one list, a derived id whose source is null or empty.
        // rootPath (the record key) seeds deterministic minting and names the record in a refusal. An element object that sits
        // twice in one list is refused too: planning would give it two ids and it would then be a duplicate on load.
        internal static IdPlan Plan(object? root, string rootPath, bool deterministic)
        {
            var plan = new IdPlan();
            Visit(root, rootPath);
            return plan;

            void Visit(object? value, string path)
            {
                if (value == null)
                    return;
                var type = value.GetType();
                if (IsLeaf(type))
                    return;
                if (value is IDictionary dictionary)
                {
                    foreach (DictionaryEntry entry in dictionary)
                        Visit(entry.Value, path + "{" + Convert.ToString(entry.Key, CultureInfo.InvariantCulture) + "}");
                }
                else if (value is IEnumerable list)
                    Elements(list, path);
                else if (IsObjectType(type))
                {
                    foreach (var (slot, get) in ShapeOf(type).Walk)
                        Visit(get(value), path + "." + slot.ToString(CultureInfo.InvariantCulture));
                }
            }

            void Elements(IEnumerable list, string path)
            {
                var items = list.Cast<object?>().ToArray();
                var taken = new HashSet<string>(StringComparer.Ordinal);
                var objects = new HashSet<object>(ReferenceComparer.Instance);
                foreach (var item in items)
                {
                    if (item == null || !IsObjectType(item.GetType()) || ShapeOf(item.GetType()) is not { GetId: { } getId } shape)
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
                            $"Record {rootPath}: element id '{id}' in the list at {path} is not 12 lowercase hex characters.",
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
                    if (IsObjectType(item.GetType()) && ShapeOf(item.GetType()) is { SetId: { } , GetId: { } get } itemShape &&
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

                    Visit(item, itemPath);
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

        private static Shape Build(Type type)
        {
            var shape = new Shape();
            var idMember = MembersOf(type).FirstOrDefault(member => member.IsDefined(typeof(CultElementIdAttribute), true));
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
                .Where(entry => !IsLeaf(Nullable.GetUnderlyingType(entry.Type) ?? entry.Type))
                .Select(entry => (Slot: KeyOf(entry.Member)!.Value, Get: entry.Member is FieldInfo fi
                    ? (Func<object, object?>)(o => fi.GetValue(o))
                    : o => ((PropertyInfo)entry.Member).GetValue(o)))
                .ToArray();
            return shape;
        }

        private static IEnumerable<MemberInfo> MembersOf(Type type) =>
            type.GetFields(BindingFlags.Instance | BindingFlags.Public).Cast<MemberInfo>()
                .Concat(type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod != null));

        private static IEnumerable<(MemberInfo Member, Type Type)> KeyedMembers(Type type) =>
            MembersOf(type).Where(member => KeyOf(member) != null).Select(member => (member, TypeOf(member)));

        private static int? KeyOf(MemberInfo member) => member.GetCustomAttribute<KeyAttribute>(true)?.IntKey;

        private static Type TypeOf(MemberInfo member) => member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;

        // ---- type classification ----

        // A value MessagePack writes whole and that cannot hold an element list. Structs are values: never elements, never walked.
        private static bool IsLeaf(Type type)
        {
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type.IsValueType)
                return true;
            return type.IsArray && IsLeaf(type.GetElementType()!);
        }

        // An object a store can hold an element list under, or hold as an element: a class MessagePack serializes by key.
        private static bool IsObjectType(Type type) =>
            (type.IsClass || type.IsInterface) && type != typeof(string) && type != typeof(object) &&
            (type.IsDefined(typeof(MessagePackObjectAttribute), true) || type.IsDefined(typeof(UnionAttribute), true));

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

        private static Type? DictionaryValueType(Type type)
        {
            foreach (var candidate in type.GetInterfaces().Append(type))
            {
                if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                    return candidate.GetGenericArguments()[1];
            }

            return null;
        }
    }
}
