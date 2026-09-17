using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MessagePack;

namespace GameCult.Caching
{
    // The engine-free inspection model: what an inspector draws for a document and which edits it may make. Lowerings
    // (the Unity editor Studio's IMGUI, a runtime CultUI panel) own only widgets and layout; members, metadata, value
    // shapes, drawer claims and edit rules come from here.

    public enum CultInspectorValueKind
    {
        Unsupported,
        String,
        Integer,
        Float,
        Bool,
        Enum,
        RecordRef,
        List,
        Dictionary,
        Union,
        Nested
    }

    // What the inspection attributes on one member say. A bare value has none.
    public sealed class CultInspectorMetadata
    {
        internal static readonly CultInspectorMetadata None = new CultInspectorMetadata(null);

        internal CultInspectorMetadata(MemberInfo? member)
        {
            Attributes = member == null ? Array.Empty<Attribute>() : member.GetCustomAttributes(true).OfType<Attribute>().ToArray();
            var label = Find<CultInspectorLabelAttribute>()?.Label;
            Label = string.IsNullOrWhiteSpace(label) ? null : label;
            Hidden = Find<CultInspectorHiddenAttribute>() != null;
            ReadOnly = Find<CultInspectorReadOnlyAttribute>() != null;
            Order = Find<CultInspectorOrderAttribute>()?.Order;
            Range = Find<CultInspectorRangeAttribute>();
            TextArea = Find<CultInspectorTextAreaAttribute>();
            AssetGuid = Find<CultInspectorAssetGuidAttribute>();
        }

        public IReadOnlyList<Attribute> Attributes { get; }

        // The declared label, or null; the lowering derives one from the member name.
        public string? Label { get; }
        public bool Hidden { get; }
        public bool ReadOnly { get; }
        public int? Order { get; }
        public CultInspectorRangeAttribute? Range { get; }
        public CultInspectorTextAreaAttribute? TextArea { get; }
        public CultInspectorAssetGuidAttribute? AssetGuid { get; }

        public T? Find<T>() where T : Attribute => Attributes.OfType<T>().FirstOrDefault();
    }

    public sealed class CultInspectorMember
    {
        internal CultInspectorMember(MemberInfo member, Type valueType, int slot, bool assignable, CultInspectorMetadata metadata)
        {
            Member = member;
            ValueType = valueType;
            Slot = slot;
            IsAssignable = assignable;
            Metadata = metadata;
            IsReadOnly = metadata.ReadOnly || !assignable;
        }

        public MemberInfo Member { get; }
        public string Name => Member.Name;
        public Type ValueType { get; }
        public int Slot { get; }
        public CultInspectorMetadata Metadata { get; }

        // SetValue works: MessagePack's own rule, the one the registry persists by (a public setter or non-readonly field, or
        // any setter or readonly field under AllowPrivate).
        public bool IsAssignable { get; }

        // The lowering offers no edit: declared [CultInspectorReadOnly], or not assignable.
        public bool IsReadOnly { get; }

        public object? GetValue(object target) =>
            Member is FieldInfo field ? field.GetValue(target) : ((PropertyInfo)Member).GetValue(target);

        public void SetValue(object target, object? value)
        {
            if (!IsAssignable)
                throw new InvalidOperationException($"{Member.DeclaringType?.Name}.{Name} is not assignable.");
            if (Member is FieldInfo field) field.SetValue(target, value);
            else ((PropertyInfo)Member).SetValue(target, value);
        }
    }

    public sealed class CultInspectorShape
    {
        internal CultInspectorShape(Type type, CultInspectorValueKind kind)
        {
            Type = type;
            Kind = kind;
        }

        public Type Type { get; }
        public CultInspectorValueKind Kind { get; }

        // List
        public Type? ElementType { get; internal set; }

        // Dictionary
        public Type? KeyType { get; internal set; }
        public Type? ValueType { get; internal set; }

        // RecordRef: the T of CultRecordRef<T>.
        public Type? RecordTarget { get; internal set; }

        // Union: the declared [Union] subtypes in key order, and nothing else.
        public IReadOnlyList<Type> UnionChoices { get; internal set; } = Array.Empty<Type>();

        // Nested: the members in inspection order.
        public IReadOnlyList<CultInspectorMember> Members { get; internal set; } = Array.Empty<CultInspectorMember>();

        // Unsupported: why.
        public string? Reason { get; internal set; }

        internal Type? BuildType { get; set; }
    }

    public readonly struct CultInspectorClaim
    {
        internal CultInspectorClaim(Type? drawer, string? conflict)
        {
            Drawer = drawer;
            Conflict = conflict;
        }

        // The claiming drawer class, or null for the lowering's built-in drawing.
        public Type? Drawer { get; }

        // Set when the claim is contested; the lowering shows it instead of drawing.
        public string? Conflict { get; }
    }

    // Which lowering drawer draws a value: a claim on one of the member's attributes, then a claim on the value's type
    // (exact, then its open generic definition), then none. A lowering builds this once from the drawer classes it
    // discovers; an invalid drawer or a contested claim is reported in Errors and draws nothing. An attribute claim
    // reaches every value drawn under the member, list elements and dictionary keys and values included, so the drawer
    // decides by the value type it is handed.
    public sealed class CultInspectorDrawerClaims
    {
        private readonly Dictionary<Type, Type> _claims = new Dictionary<Type, Type>();
        private readonly Dictionary<Type, string> _contested = new Dictionary<Type, string>();
        private readonly List<string> _errors = new List<string>();
        private readonly Dictionary<(Type, MemberInfo?), CultInspectorClaim> _resolved = new Dictionary<(Type, MemberInfo?), CultInspectorClaim>();

        public CultInspectorDrawerClaims(IEnumerable<Type> drawerTypes, Type drawerContract)
        {
            if (drawerTypes == null) throw new ArgumentNullException(nameof(drawerTypes));
            if (drawerContract == null) throw new ArgumentNullException(nameof(drawerContract));
            foreach (var drawer in drawerTypes)
            {
                var attribute = drawer.GetCustomAttribute<CultInspectorDrawerAttribute>(false);
                if (attribute == null)
                    continue;
                if (!drawerContract.IsAssignableFrom(drawer) || drawer.IsAbstract || drawer.GetConstructor(Type.EmptyTypes) == null)
                {
                    _errors.Add($"{drawer.FullName} is marked [CultInspectorDrawer] but is not a concrete {drawerContract.Name} with a parameterless constructor.");
                    continue;
                }

                var claimed = attribute.Claimed;
                if (_contested.TryGetValue(claimed, out var claimants))
                {
                    _contested[claimed] = claimants + ", " + drawer.FullName;
                }
                else if (_claims.TryGetValue(claimed, out var holder))
                {
                    _claims.Remove(claimed);
                    _contested[claimed] = holder.FullName + ", " + drawer.FullName;
                }
                else
                {
                    _claims[claimed] = drawer;
                }
            }

            foreach (var pair in _contested.OrderBy(pair => pair.Key.FullName, StringComparer.Ordinal))
                _errors.Add($"{pair.Value} all claim {pair.Key.FullName}; none of them draws it.");
        }

        public IReadOnlyList<string> Errors => _errors;

        public CultInspectorClaim Resolve(Type valueType, MemberInfo? member)
        {
            if (valueType == null) throw new ArgumentNullException(nameof(valueType));
            lock (_resolved)
            {
                if (!_resolved.TryGetValue((valueType, member), out var claim))
                    _resolved[(valueType, member)] = claim = ResolveCore(valueType, member);
                return claim;
            }
        }

        private CultInspectorClaim ResolveCore(Type valueType, MemberInfo? member)
        {
            Type? drawer = null;
            foreach (var attribute in member?.GetCustomAttributes(true).OfType<Attribute>() ?? Enumerable.Empty<Attribute>())
            {
                for (var type = attribute.GetType(); type != null && type != typeof(Attribute); type = type.BaseType)
                {
                    if (_contested.TryGetValue(type, out var claimants))
                        return new CultInspectorClaim(null, $"[{type.Name}] is claimed by {claimants}");
                    if (!_claims.TryGetValue(type, out var claimant))
                        continue;
                    if (drawer != null && drawer != claimant)
                        return new CultInspectorClaim(null, $"{member!.Name} carries attributes claimed by {drawer.FullName} and {claimant.FullName}");
                    drawer = claimant;
                    break;
                }
            }

            if (drawer != null)
                return new CultInspectorClaim(drawer, null);
            foreach (var type in valueType.IsGenericType ? new[] { valueType, valueType.GetGenericTypeDefinition() } : new[] { valueType })
            {
                if (_contested.TryGetValue(type, out var claimants))
                    return new CultInspectorClaim(null, $"{type.FullName} is claimed by {claimants}");
                if (_claims.TryGetValue(type, out var claimant))
                    return new CultInspectorClaim(claimant, null);
            }

            return default;
        }
    }

    // A private copy of one stored document. A lowering draws into Document; the cached object is never touched.
    public sealed class CultInspectorEdit
    {
        private readonly Func<object> _snapshot;
        private object? _record;

        internal CultInspectorEdit(CultStoredDocument source, object document, Func<object> snapshot)
        {
            Source = source;
            Document = document;
            _snapshot = snapshot;
        }

        public CultStoredDocument Source { get; }
        public object Document { get; }

        // The record being drawn, as stored when this edit began, for drawers that read the document they sit in. It is a
        // second copy, decoded on first read from the bytes Document came from, so a cache change in between cannot split
        // them: not the cached object, and not Document, which a lowering commits. Nothing
        // reads it back, so writing to it saves nothing and changes neither the edit nor the cache.
        public object Record => _record ??= _snapshot();

        public bool IsSpent { get; private set; }

        public bool IsFor(CultStoredDocument record) => !IsSpent && ReferenceEquals(Source, record);

        // Upserts the copy under the source key. Committed, the cache holds the copy; refused, the cached object is
        // unchanged. Either way the edit is spent and the copy belongs to nobody: begin a new edit from the cache.
        public bool Commit(CultCache cache, out string? error)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (IsSpent) throw new InvalidOperationException("This edit was already committed; begin a new edit.");
            IsSpent = true;
            try
            {
                cache.UpsertAsync(Source.Descriptor.DocumentType, Document, Source.Key).GetAwaiter().GetResult();
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
        }
    }

    public sealed class CultInspectorModel
    {
        private const string RecordRefIdentity = "ref:";
        private const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;

        private static readonly HashSet<Type> IntegerTypes = new HashSet<Type>
        {
            typeof(int), typeof(long), typeof(uint), typeof(short), typeof(ushort), typeof(byte), typeof(sbyte)
        };

        private readonly Func<object, Type, Type, byte[]> _serialize;
        private readonly Func<Type, byte[], object> _deserialize;
        private readonly Dictionary<Type, CultInspectorShape> _shapes = new Dictionary<Type, CultInspectorShape>();
        private readonly Dictionary<MemberInfo, CultInspectorMetadata> _metadata = new Dictionary<MemberInfo, CultInspectorMetadata>();

        // serialize (value, value type, owning document type) and deserialize are the store's codec
        // (CultCacheMessagePack.CreateInspectorModel for .cc stores). Edits clone documents through both; dictionary keys
        // compare by serialize under their owning document, exactly as the store writes them inside it, so serialize must
        // take any value, not only documents.
        public CultInspectorModel(CultDocumentRegistry registry, Func<object, Type, Type, byte[]> serialize, Func<Type, byte[], object> deserialize)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));
            _deserialize = deserialize ?? throw new ArgumentNullException(nameof(deserialize));
        }

        public CultDocumentRegistry Registry { get; }

        public static string RecordLabel(CultStoredDocument record)
        {
            var name = record.Descriptor.NameAccessor?.Invoke(record.Document);
            return string.IsNullOrWhiteSpace(name) ? record.Key.Value : name!;
        }

        // The key a CultRecordRef<T> holds; "" when unset.
        public static string RecordKey(object? recordRef) => (recordRef as ICultRecordRef)?.Key.Value ?? string.Empty;

        public CultInspectorShape ShapeOf(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            lock (_shapes)
            {
                if (!_shapes.TryGetValue(type, out var shape))
                    _shapes[type] = shape = Classify(type);
                return shape;
            }
        }

        public IReadOnlyList<CultInspectorMember> MembersOf(Type type)
        {
            var shape = ShapeOf(type);
            return shape.Kind == CultInspectorValueKind.Nested ? shape.Members : Array.Empty<CultInspectorMember>();
        }

        public CultInspectorMetadata MetadataOf(MemberInfo? member)
        {
            if (member == null)
                return CultInspectorMetadata.None;
            lock (_metadata)
            {
                if (!_metadata.TryGetValue(member, out var metadata))
                    _metadata[member] = metadata = new CultInspectorMetadata(member);
                return metadata;
            }
        }

        public object Clone(object document, Type type) => _deserialize(type, _serialize(document, type, type));

        public CultInspectorEdit BeginEdit(CultStoredDocument record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            var type = record.Descriptor.DocumentType;
            var snapshot = _serialize(record.Document, type, type);
            return new CultInspectorEdit(record, _deserialize(type, snapshot), () => _deserialize(type, snapshot));
        }

        // Whether CreateDefault makes a value: a string, struct, list or dictionary, or a concrete class with a parameterless constructor.
        public bool CanCreate(Type type) =>
            type == typeof(string) || type.IsValueType ||
            ShapeOf(type).Kind is CultInspectorValueKind.List or CultInspectorValueKind.Dictionary ||
            !type.IsAbstract && !type.IsInterface && type.GetConstructor(Type.EmptyTypes) != null;

        public object? CreateDefault(Type type)
        {
            if (!CanCreate(type)) return null;
            if (type == typeof(string)) return string.Empty;
            var shape = ShapeOf(type);
            if (type.IsArray) return Array.CreateInstance(shape.ElementType!, 0);
            return Activator.CreateInstance(shape.Kind == CultInspectorValueKind.Dictionary ? shape.BuildType! : type);
        }

        // What a new value for a slot of elementType (a list element, a dictionary value, a union pick) may be: a union's
        // declared subtypes, or elementType itself.
        public IReadOnlyList<Type> ElementChoices(Type elementType)
        {
            var shape = ShapeOf(elementType);
            return shape.Kind == CultInspectorValueKind.Union ? shape.UnionChoices : new[] { elementType };
        }

        // A new value of choice for a slot of elementType, or null with a notice when choice cannot be made.
        public object? CreateElement(Type elementType, Type choice, out string? notice)
        {
            // A slot's own type is always offered; an abstract one is refused with the notice below.
            if (choice != elementType && !ElementChoices(elementType).Contains(choice))
                throw new ArgumentException($"{choice.Name} is not a choice for {elementType.Name}.", nameof(choice));
            var created = CreateDefault(choice);
            // Null is a value only for a Nullable<T> slot; anywhere else it is nothing made.
            notice = created == null && !choice.IsValueType
                ? $"{choice.Name} is abstract or has no parameterless constructor; nothing was created."
                : null;
            return created;
        }

        // An integer edit as a value of integerType, clamped to that type's range instead of overflowing.
        public object NarrowInteger(Type integerType, long value)
        {
            if (!IntegerTypes.Contains(integerType))
                throw new ArgumentException($"{integerType.Name} is not an integer the inspector edits.", nameof(integerType));
            long Bound(string name) => Convert.ToInt64(integerType.GetField(name)!.GetValue(null));
            return Convert.ChangeType(Math.Max(Bound("MinValue"), Math.Min(Bound("MaxValue"), value)), integerType);
        }

        // A new collection holding items; the value it replaces is never mutated.
        public object BuildList(Type listType, IReadOnlyList<object?> items)
        {
            var shape = ShapeOf(listType);
            if (shape.Kind != CultInspectorValueKind.List)
                throw new ArgumentException($"{listType.Name} is not a list the inspector builds.", nameof(listType));
            if (listType.IsArray)
            {
                var array = Array.CreateInstance(shape.ElementType!, items.Count);
                for (var i = 0; i < items.Count; i++) array.SetValue(items[i], i);
                return array;
            }

            var list = (IList)Activator.CreateInstance(listType)!;
            foreach (var item in items) list.Add(item);
            return list;
        }

        public object BuildDictionary(Type dictionaryType, IEnumerable<KeyValuePair<object?, object?>> entries)
        {
            var dictionary = (IDictionary)Activator.CreateInstance(DictionaryShape(dictionaryType).BuildType!)!;
            foreach (var entry in entries) dictionary.Add(entry.Key!, entry.Value);
            return dictionary;
        }

        // The key entry `index` of a dictionary in a documentType document holding `keys` takes when a lowering offers
        // `candidate`: the candidate, or the kept key and a notice saying why. An unchanged key is never refused. Any other
        // key must be non-null, not an empty record reference, and not taken by another entry, so BuildDictionary cannot
        // throw. A key the document cannot serialize is refused: the store could not write it.
        public object? ReplaceKey(Type documentType, Type dictionaryType, IReadOnlyList<object?> keys, int index, object? candidate, out string? notice)
        {
            var shape = DictionaryShape(dictionaryType);
            string? refusal;
            try
            {
                var identity = KeyIdentity(documentType, shape, candidate);
                refusal = identity == KeyIdentity(documentType, shape, keys[index]) ? null
                    : identity == null ? "a null key"
                    : identity == RecordRefIdentity ? "an empty record reference as a key"
                    : Taken(documentType, shape, candidate!, keys.Where((_, i) => i != index)) ? "a duplicate key"
                    : null;
            }
            catch (Exception exception)
            {
                refusal = $"a key {documentType.Name} cannot serialize ({exception.GetBaseException().Message})";
            }

            notice = refusal == null ? null : $"Refused {refusal} on entry {index}; its key was kept.";
            return refusal == null ? candidate : keys[index];
        }

        // A key to add beside `keys` in a documentType document: a record-reference key takes the first candidate record not
        // already used; any other key is the type's default. Null with a notice when no such key is free or serializable.
        public object? FreshKey(Type documentType, Type dictionaryType, IReadOnlyList<object?> keys, IEnumerable<CultStoredDocument> records, out string? notice)
        {
            var shape = DictionaryShape(dictionaryType);
            var keyType = shape.KeyType!;
            var candidates = ShapeOf(keyType).Kind == CultInspectorValueKind.RecordRef
                ? RecordCandidates(keyType, records).Select(record => CreateRecordRef(keyType, record.Key.Value))
                : new[] { CreateDefault(keyType) };
            try
            {
                var fresh = candidates.FirstOrDefault(key => key != null && !Taken(documentType, shape, key, keys));
                notice = fresh == null ? $"No unused {keyType.Name} key is available; nothing was added." : null;
                return fresh;
            }
            catch (Exception exception)
            {
                notice = $"{documentType.Name} cannot serialize a {keyType.Name} key ({exception.GetBaseException().Message}); nothing was added.";
                return null;
            }
        }

        // A key is taken when another serializes the same (a CultRecordRef<T> by its key string) or when
        // the rebuilt dictionary type's default comparer calls them equal (0.0 and -0.0 serialize apart but are one double key).
        private bool Taken(Type documentType, CultInspectorShape dictionary, object key, IEnumerable<object?> keys)
        {
            var identity = KeyIdentity(documentType, dictionary, key);
            var probe = (IDictionary)Activator.CreateInstance(dictionary.BuildType!)!;
            var filler = dictionary.ValueType!.IsValueType ? Activator.CreateInstance(dictionary.ValueType) : null;
            foreach (var other in keys.Where(other => other != null))
            {
                if (KeyIdentity(documentType, dictionary, other) == identity) return true;
                probe[other!] = filler;
            }

            return probe.Contains(key);
        }

        // A key's bytes as its declared key type under the owning document's serializer, the store's own path. Throws when
        // that serializer cannot write the key.
        private string? KeyIdentity(Type documentType, CultInspectorShape dictionary, object? key) =>
            key == null ? null
            : key is ICultRecordRef reference ? RecordRefIdentity + reference.Key.Value
            : "value:" + Convert.ToBase64String(_serialize(key, dictionary.KeyType!, documentType));

        private CultInspectorShape DictionaryShape(Type dictionaryType)
        {
            var shape = ShapeOf(dictionaryType);
            return shape.Kind == CultInspectorValueKind.Dictionary
                ? shape
                : throw new ArgumentException($"{dictionaryType.Name} is not a dictionary the inspector builds.", nameof(dictionaryType));
        }

        // The records a CultRecordRef<T> may point at: every record whose document is a T, by label.
        public IReadOnlyList<CultStoredDocument> RecordCandidates(Type recordRefType, IEnumerable<CultStoredDocument> records)
        {
            var target = ShapeOf(recordRefType).RecordTarget
                         ?? throw new ArgumentException($"{recordRefType.Name} is not a CultRecordRef<T>.", nameof(recordRefType));
            return records
                .Where(record => target.IsInstanceOfType(record.Document) && record.Key.Value.Length > 0)
                .OrderBy(RecordLabel, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public object CreateRecordRef(Type recordRefType, string key)
        {
            if (ShapeOf(recordRefType).Kind != CultInspectorValueKind.RecordRef)
                throw new ArgumentException($"{recordRefType.Name} is not a CultRecordRef<T>.", nameof(recordRefType));
            return Activator.CreateInstance(recordRefType, new CultRecordKey(key))!;
        }

        private CultInspectorShape Classify(Type type)
        {
            if (type == typeof(string)) return new CultInspectorShape(type, CultInspectorValueKind.String);
            if (type == typeof(bool)) return new CultInspectorShape(type, CultInspectorValueKind.Bool);
            if (type.IsEnum) return new CultInspectorShape(type, CultInspectorValueKind.Enum);
            if (IntegerTypes.Contains(type)) return new CultInspectorShape(type, CultInspectorValueKind.Integer);
            if (type == typeof(float) || type == typeof(double)) return new CultInspectorShape(type, CultInspectorValueKind.Float);
            if (type.IsArray && type.GetArrayRank() != 1)
                return Unsupported(type, $"{type.Name} is a multi-dimensional array, which the inspector does not edit.");
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CultRecordRef<>))
                return new CultInspectorShape(type, CultInspectorValueKind.RecordRef) { RecordTarget = type.GetGenericArguments()[0] };

            var dictionary = type.GetInterfaces().Prepend(type)
                .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            if (dictionary != null)
            {
                var arguments = dictionary.GetGenericArguments();
                var build = type.IsInterface || type.IsAbstract ? typeof(Dictionary<,>).MakeGenericType(arguments) : type;
                return typeof(IDictionary).IsAssignableFrom(build) && build.GetConstructor(Type.EmptyTypes) != null
                    ? new CultInspectorShape(type, CultInspectorValueKind.Dictionary) { KeyType = arguments[0], ValueType = arguments[1], BuildType = build }
                    : Unsupported(type, $"{type.Name} is a dictionary the inspector cannot rebuild.");
            }

            if (type.IsArray)
                return new CultInspectorShape(type, CultInspectorValueKind.List) { ElementType = type.GetElementType() };
            if (type.IsGenericType && !type.IsAbstract && !type.IsInterface && typeof(IList).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null)
                return new CultInspectorShape(type, CultInspectorValueKind.List) { ElementType = type.GetGenericArguments()[0] };

            if (type.IsAbstract || type.IsInterface)
            {
                var choices = type.GetCustomAttributes<UnionAttribute>(false).OrderBy(union => union.Key).Select(union => union.SubType).ToArray();
                return choices.Length == 0
                    ? Unsupported(type, $"{type.Name} declares no [Union] subtypes.")
                    : new CultInspectorShape(type, CultInspectorValueKind.Union) { UnionChoices = choices };
            }

            var keyed = KeyedMembers(type);
            if (keyed.Length > 0)
                return new CultInspectorShape(type, CultInspectorValueKind.Nested) { Members = keyed };
            // An unkeyed struct is edited in place. With public fields it shows only them, readonly ones read-only: its
            // properties alias those values (a Quaternion's eulerAngles, a Rect's min and max). Without, it shows its public
            // properties with a setter, init-only ones read-only (record structs, matrices over private rows). Get-only
            // properties are computed or kept by the in-place edit, so they are never rows.
            if (type.IsValueType)
            {
                var fields = type.GetFields(Public);
                var members = (fields.Length > 0
                        ? fields.OrderBy(field => field.MetadataToken)
                            .Select(field => (Member: (MemberInfo)field, Type: field.FieldType, Assignable: !field.IsInitOnly))
                        : type.GetProperties(Public).OrderBy(property => property.MetadataToken)
                            .Where(property => property.GetIndexParameters().Length == 0 && property.GetGetMethod() != null && property.GetSetMethod() != null)
                            .Select(property => (Member: (MemberInfo)property, Type: property.PropertyType, Assignable: !IsInitOnly(property.GetSetMethod()!))))
                    .Select((member, slot) => new CultInspectorMember(member.Member, member.Type, slot, member.Assignable, MetadataOf(member.Member)))
                    .ToArray();
                if (members.Length > 0)
                    return new CultInspectorShape(type, CultInspectorValueKind.Nested) { Members = members };
            }

            return Unsupported(type, "no drawer for " + type.FullName);
        }

        // Documents take their members and slots from the registry's catalog; other MessagePack objects from their [Key]s.
        private CultInspectorMember[] KeyedMembers(Type type)
        {
            var allowPrivate = type.GetCustomAttribute<MessagePackObjectAttribute>(true)?.AllowPrivate == true;
            var candidates = type.GetMembers(Public)
                .Where(member => member is FieldInfo field && !field.IsLiteral ||
                                 member is PropertyInfo property && property.GetGetMethod() != null && property.GetIndexParameters().Length == 0)
                .Where(member => !member.IsDefined(typeof(IgnoreMemberAttribute), true))
                // A member hidden with `new` is its most-derived declaration, as the registry's own-member scan reads it.
                .GroupBy(member => member.Name)
                .Select(hides => hides.OrderByDescending(member => Ancestry(member.DeclaringType)).First());
            IEnumerable<(MemberInfo Member, int Slot)> slotted;
            if (type.IsDefined(typeof(CultDocumentAttribute), false))
            {
                var slots = Registry.GetRequired(type).ToCatalogEntry().Members.ToDictionary(member => member.MemberName, member => member.Slot, StringComparer.Ordinal);
                slotted = candidates.Where(member => slots.ContainsKey(member.Name)).Select(member => (member, slots[member.Name]));
            }
            else
            {
                slotted = candidates
                    .Select(member => (Member: member, Slot: member.GetCustomAttribute<KeyAttribute>(true)?.IntKey))
                    .Where(entry => entry.Slot != null)
                    .Select(entry => (entry.Member, entry.Slot!.Value));
            }

            return slotted
                .Select(entry => new CultInspectorMember(entry.Member, TypeOf(entry.Member), entry.Slot, Assignable(entry.Member, allowPrivate), MetadataOf(entry.Member)))
                .OrderBy(member => member.Metadata.Order ?? member.Slot)
                .ThenBy(member => member.Slot)
                .ToArray();
        }

        private static CultInspectorShape Unsupported(Type type, string reason) =>
            new CultInspectorShape(type, CultInspectorValueKind.Unsupported) { Reason = reason };

        private static int Ancestry(Type? type) => type == null ? 0 : 1 + Ancestry(type.BaseType);

        // `init` is a setter whose return carries the IsExternalInit modreq; the language contract, matched by name because
        // netstandard2.1 does not ship the type. A runtime that does not report the modreq (unverified on Unity's Mono) shows
        // init members editable instead of read-only; SetValue still assigns them, so their edits round-trip either way.
        private static bool IsInitOnly(MethodInfo setter) =>
            setter.ReturnParameter.GetRequiredCustomModifiers().Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");

        private static Type TypeOf(MemberInfo member) => member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;

        private static bool Assignable(MemberInfo member, bool allowPrivate)
        {
            if (member is FieldInfo field)
                return !field.IsInitOnly || allowPrivate;
            var setter = ((PropertyInfo)member).GetSetMethod(true);
            return setter != null && (setter.IsPublic || allowPrivate);
        }
    }
}
