using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MessagePack;

namespace GameCult.Caching
{
    // The identity of one element of an object list. A string member, keyed like any other, on every object type that
    // sits in a list of a registered document type (union bases included: subclasses inherit the member).
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

    // The one owner of element identity: which types need an id, and how unset ids are filled.
    // An id is 12 lowercase hex characters (48 bits); it need only be unique within one list. A write mints a random id; a
    // load of a store written before ids existed mints from SHA-256(record key, element path), so every reader of the same
    // store mints the same id and a reload is identical to what was read.
    internal static class CultElementIds
    {
        private const int IdBytes = 6;

        private sealed class Shape
        {
            public Func<object, string?>? GetId;
            public Action<object, string>? SetId;
            public Func<object, string>? Derive;
            public (int Slot, Func<object, object?> Get)[] Walk = Array.Empty<(int, Func<object, object?>)>();
        }

        private static readonly ConcurrentDictionary<Type, Shape> Shapes = new();

        // ---- registration: which element types must carry an id ----

        // Every reason this document type cannot be registered under the id rule, one per offending element type.
        internal static List<string> Problems(Type documentType)
        {
            var problems = new List<string>();
            VisitObject(documentType, documentType, new HashSet<Type>(), problems);
            return problems;
        }

        private static void VisitObject(Type documentType, Type type, HashSet<Type> seen, List<string> problems)
        {
            if (!seen.Add(type))
                return;
            foreach (var (member, memberType) in KeyedMembers(type))
                VisitMemberType(documentType, type, member, memberType, seen, problems);
            foreach (var union in type.GetCustomAttributes<UnionAttribute>(true))
                VisitObject(documentType, union.SubType, seen, problems);
        }

        private static void VisitMemberType(Type documentType, Type owner, MemberInfo member, Type memberType, HashSet<Type> seen, List<string> problems)
        {
            memberType = Nullable.GetUnderlyingType(memberType) ?? memberType;
            if (IsLeaf(memberType))
                return;
            if (DictionaryValueType(memberType) is { } valueType)
            {
                VisitMemberType(documentType, owner, member, valueType, seen, problems);
                return;
            }

            if (ElementTypeOf(memberType) is { } element)
            {
                if (IsObjectType(element))
                {
                    var problem = IdProblem(element);
                    if (problem != null)
                        problems.Add($"Cult document {documentType.Name}: list element type {element.Name} (in {owner.Name}.{member.Name}) {problem}");
                    VisitObject(documentType, element, seen, problems);
                }
                else
                    VisitMemberType(documentType, owner, member, element, seen, problems);
                return;
            }

            if (IsObjectType(memberType))
                VisitObject(documentType, memberType, seen, problems);
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
            if (KeyOf(id) == null)
                return $"declares [CultElementId] on {id.Name}, which has no integer [Key], so it is never persisted.";
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

        // ---- write and load: mint and check ----

        // Fills every unset element id under root and returns how many were unset. Writes refuse a duplicate id within one
        // list. dryRun counts and changes nothing. rootPath (the record key) seeds deterministic minting.
        internal static int Assign(object root, string rootPath, bool deterministic, bool refuseDuplicates, bool dryRun = false)
        {
            var count = 0;
            Visit(root, rootPath);
            return count;

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
                        Visit(entry.Value, path + "{" + entry.Key + "}");
                }
                else if (value is IEnumerable list)
                    Elements(list, path);
                else if (IsObjectType(type))
                {
                    foreach (var (slot, get) in ShapeOf(type).Walk)
                        Visit(get(value), path + "." + slot);
                }
            }

            void Elements(IEnumerable list, string path)
            {
                var items = list.Cast<object?>().ToArray();
                var taken = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in items)
                {
                    var id = item != null && IsObjectType(item.GetType()) ? ShapeOf(item.GetType()).GetId?.Invoke(item) : null;
                    if (!string.IsNullOrEmpty(id) && !taken.Add(id!) && refuseDuplicates)
                        throw new InvalidOperationException(
                            $"Element id '{id}' appears twice in one list ({path}); an element id is unique within its list.");
                }

                for (var index = 0; index < items.Length; index++)
                {
                    var item = items[index];
                    if (item == null)
                        continue;
                    var itemPath = path + "/" + index;
                    if (IsObjectType(item.GetType()) && ShapeOf(item.GetType()) is { SetId: { } set, GetId: { } get } itemShape)
                    {
                        if (string.IsNullOrEmpty(get(item)))
                        {
                            count++;
                            if (!dryRun)
                            {
                                var id = itemShape.Derive?.Invoke(item) ?? Mint(itemPath, deterministic, rootPath, taken);
                                if (itemShape.Derive != null && !taken.Add(id) && refuseDuplicates)
                                    throw new InvalidOperationException(
                                        $"Element id '{id}' appears twice in one list ({path}); an element id is unique within its list.");
                                set(item, id);
                            }
                        }
                    }

                    Visit(item, itemPath);
                }
            }
        }

        private static string Mint(string itemPath, bool deterministic, string rootPath, HashSet<string> taken)
        {
            for (var attempt = 0; ; attempt++)
            {
                var bytes = new byte[IdBytes];
                if (deterministic)
                {
                    using var sha = SHA256.Create();
                    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(rootPath + "|" + itemPath + "|" + attempt));
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
                text.Append(value.ToString("x2"));
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
                    shape.Derive = o => Convert.ToString(from is FieldInfo ff ? ff.GetValue(o) : ((PropertyInfo)from).GetValue(o), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
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
