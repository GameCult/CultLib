using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GameCult.Logging;
using R3;

namespace GameCult.Caching
{
    public sealed class CultSchemaCatalogEntry
    {
        public string SchemaId { get; set; } = string.Empty;
        public string SchemaName { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
        public string CanonicalSchemaJson { get; set; } = string.Empty;
        public string[] CompatibleSchemaIds { get; set; } = Array.Empty<string>();
    }

    public sealed class CultPersistedRecord
    {
        public string Key { get; set; } = string.Empty;
        public string SchemaId { get; set; } = string.Empty;
        public string StoredAt { get; set; } = string.Empty;
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }

    public sealed class CultPersistedStoreSnapshot
    {
        public string FormatVersion { get; set; } = "cultcache.store.v1";
        public CultSchemaCatalogEntry[] SchemaCatalog { get; set; } = Array.Empty<CultSchemaCatalogEntry>();
        public CultPersistedRecord[] Records { get; set; } = Array.Empty<CultPersistedRecord>();
    }

    public sealed class CultDocumentDescriptor
    {
        internal CultDocumentDescriptor(
            Type documentType,
            string schemaName,
            string schemaVersion,
            string schemaId,
            string contentHash,
            string canonicalSchemaJson,
            bool isGlobal,
            string? nameMember,
            Func<object, string?>? nameAccessor,
            IReadOnlyDictionary<string, Func<object, string>> indexAccessors,
            IReadOnlyList<CultDocumentMemberDescriptor> members)
        {
            DocumentType = documentType;
            SchemaName = schemaName;
            SchemaVersion = schemaVersion;
            SchemaId = schemaId;
            ContentHash = contentHash;
            CanonicalSchemaJson = canonicalSchemaJson;
            IsGlobal = isGlobal;
            NameMember = nameMember;
            NameAccessor = nameAccessor;
            IndexAccessors = indexAccessors;
            Members = members;
        }

        public Type DocumentType { get; }
        public string SchemaName { get; }
        public string SchemaVersion { get; }
        public string SchemaId { get; }
        public string ContentHash { get; }
        public string CanonicalSchemaJson { get; }
        public bool IsGlobal { get; }
        public string? NameMember { get; }
        internal Func<object, string?>? NameAccessor { get; }
        internal IReadOnlyDictionary<string, Func<object, string>> IndexAccessors { get; }
        internal IReadOnlyList<CultDocumentMemberDescriptor> Members { get; }

        public CultSchemaCatalogEntry ToCatalogEntry()
        {
            return new CultSchemaCatalogEntry
            {
                SchemaId = SchemaId,
                SchemaName = SchemaName,
                SchemaVersion = SchemaVersion,
                ContentHash = ContentHash,
                CanonicalSchemaJson = CanonicalSchemaJson,
                CompatibleSchemaIds = [SchemaId]
            };
        }

        internal string GetPreferredFileStem(object document)
        {
            var preferred = NameAccessor?.Invoke(document);
            return string.IsNullOrWhiteSpace(preferred)
                ? SchemaName
                : preferred!;
        }
    }

    internal sealed class CultDocumentMemberDescriptor
    {
        public string MemberName { get; set; } = string.Empty;
        public int Slot { get; set; }
        public string TypeName { get; set; } = string.Empty;
        public bool IsReference { get; set; }
        public bool IsMany { get; set; }
        public string? TargetSchemaName { get; set; }
        public bool IsName { get; set; }
        public string? IndexAlias { get; set; }
    }

    public sealed class CultStoredDocument
    {
        public CultStoredDocument(
            CultRecordKey key,
            string storedAt,
            CultDocumentDescriptor descriptor,
            object document)
        {
            Key = key;
            StoredAt = storedAt;
            Descriptor = descriptor;
            Document = document;
        }

        public CultRecordKey Key { get; }
        public string StoredAt { get; }
        public CultDocumentDescriptor Descriptor { get; }
        public object Document { get; }
    }

    public sealed class CultDocumentRegistry
    {
        private static readonly Lazy<CultDocumentRegistry> SharedRegistry =
            new(() => new CultDocumentRegistry());

        private readonly ConcurrentDictionary<Type, CultDocumentDescriptor> _byType = new();
        private readonly ConcurrentDictionary<string, CultDocumentDescriptor> _bySchemaId =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, CultDocumentDescriptor> _bySchemaName =
            new(StringComparer.Ordinal);

        public static CultDocumentRegistry Shared => SharedRegistry.Value;

        public CultDocumentRegistry()
        {
            Refresh();
        }

        public IEnumerable<CultDocumentDescriptor> AllDescriptors => _byType.Values.OrderBy(d => d.SchemaName, StringComparer.Ordinal);

        public void Refresh()
        {
            _byType.Clear();
            _bySchemaId.Clear();
            _bySchemaName.Clear();

            var generatedTypes = new HashSet<Type>();
            foreach (var definition in CultGeneratedDocumentMetadataLoader.LoadDefinitions())
            {
                var descriptor = BuildDescriptor(definition);
                RegisterDescriptor(descriptor);
                generatedTypes.Add(descriptor.DocumentType);
            }

            foreach (var type in ReflectionExtensions.GetAttributedDocumentTypes().Where(type => !generatedTypes.Contains(type)))
            {
                RegisterDescriptor(BuildDescriptor(type));
            }
        }

        public CultDocumentDescriptor GetRequired(Type type)
        {
            if (_byType.TryGetValue(type, out var descriptor))
            {
                return descriptor;
            }

            descriptor = TryBuildGeneratedDescriptor(type);
            if (descriptor != null)
            {
                RegisterDescriptor(descriptor);
                return descriptor;
            }

            var attribute = type.GetCustomAttribute<CultDocumentAttribute>();
            if (attribute == null)
            {
                throw new InvalidOperationException(
                    $"Type {type.FullName} is not marked with {nameof(CultDocumentAttribute)}.");
            }

            descriptor = BuildDescriptor(type);
            RegisterDescriptor(descriptor);
            return descriptor;
        }

        public CultDocumentDescriptor GetRequired<T>() where T : class
        {
            return GetRequired(typeof(T));
        }

        public CultDocumentDescriptor GetRequiredBySchemaId(string schemaId)
        {
            if (_bySchemaId.TryGetValue(schemaId, out var descriptor))
            {
                return descriptor;
            }

            throw new InvalidOperationException($"Unknown CultCache schema id '{schemaId}'.");
        }

        public CultDocumentDescriptor ResolvePersistedSchema(string schemaId, IReadOnlyCollection<CultSchemaCatalogEntry> catalog)
        {
            if (_bySchemaId.TryGetValue(schemaId, out var exact))
            {
                return exact;
            }

            var persisted = catalog.FirstOrDefault(entry => string.Equals(entry.SchemaId, schemaId, StringComparison.Ordinal));
            if (persisted == null)
            {
                throw new InvalidOperationException($"Persisted schema '{schemaId}' is not present in the embedded catalog.");
            }

            if (_bySchemaName.TryGetValue(persisted.SchemaName, out var local))
            {
                return local;
            }

            throw new InvalidOperationException(
                $"No local CultCache schema matches persisted schema '{persisted.SchemaName}' ({schemaId}).");
        }

        private void RegisterDescriptor(CultDocumentDescriptor descriptor)
        {
            _byType[descriptor.DocumentType] = descriptor;
            _bySchemaId[descriptor.SchemaId] = descriptor;
            _bySchemaName[descriptor.SchemaName] = descriptor;
        }

        private static CultDocumentDescriptor? TryBuildGeneratedDescriptor(Type type)
        {
            var definition = CultGeneratedDocumentMetadataLoader.LoadDefinitions(type.Assembly)
                .FirstOrDefault(candidate => candidate.DocumentType == type);
            return definition == null
                ? null
                : BuildDescriptor(definition);
        }

        private static CultDocumentDescriptor BuildDescriptor(Type type)
        {
            var attribute = type.GetCustomAttribute<CultDocumentAttribute>()
                            ?? throw new InvalidOperationException(
                                $"Type {type.FullName} is not marked with {nameof(CultDocumentAttribute)}.");
            var members = DiscoverMembers(type);
            var nameMember = members.FirstOrDefault(member => member.IsName);
            var indexAccessors = members
                .Where(member => member.IndexAlias != null)
                .ToDictionary(
                    member => member.IndexAlias!,
                    member => member.Getter,
                    StringComparer.Ordinal);
            var descriptorMembers = members
                .Select(member => new CultDocumentMemberDescriptor
                {
                    MemberName = member.Member.Name,
                    Slot = member.Slot,
                    TypeName = CultSchemaTypeNames.FromType(member.MemberType),
                    IsReference = member.IsReference,
                    IsMany = member.IsMany,
                    TargetSchemaName = member.TargetSchemaName,
                    IsName = member.IsName,
                    IndexAlias = member.IndexAlias
                })
                .ToArray();
            var schemaJson = BuildCanonicalSchemaJson(attribute.SchemaName, attribute.SchemaVersion, descriptorMembers);
            var contentHash = Sha256(schemaJson);
            var semanticFingerprint = BuildSemanticFingerprint(attribute.SchemaName, attribute.SchemaVersion, descriptorMembers);
            var schemaId = Sha256(semanticFingerprint);

            return new CultDocumentDescriptor(
                type,
                attribute.SchemaName,
                attribute.SchemaVersion,
                schemaId,
                contentHash,
                schemaJson,
                type.GetCustomAttribute<CultGlobalAttribute>() != null,
                nameMember?.Member.Name,
                nameMember?.GetterNullable,
                indexAccessors,
                descriptorMembers);
        }

        private static CultDocumentDescriptor BuildDescriptor(CultGeneratedDocumentDefinition definition)
        {
            var descriptorMembers = definition.Members
                .OrderBy(member => member.Slot)
                .Select(member => new CultDocumentMemberDescriptor
                {
                    MemberName = member.MemberName,
                    Slot = member.Slot,
                    TypeName = member.TypeName,
                    IsReference = member.IsReference,
                    IsMany = member.IsMany,
                    TargetSchemaName = member.TargetSchemaName,
                    IsName = member.IsName,
                    IndexAlias = member.IndexAlias
                })
                .ToArray();
            var schemaJson = BuildCanonicalSchemaJson(definition.SchemaName, definition.SchemaVersion, descriptorMembers);
            var contentHash = Sha256(schemaJson);
            var semanticFingerprint = BuildSemanticFingerprint(definition.SchemaName, definition.SchemaVersion, descriptorMembers);
            var schemaId = Sha256(semanticFingerprint);

            return new CultDocumentDescriptor(
                definition.DocumentType,
                definition.SchemaName,
                definition.SchemaVersion,
                schemaId,
                contentHash,
                schemaJson,
                definition.IsGlobal,
                definition.NameMember,
                definition.NameAccessor,
                definition.IndexAccessors.ToDictionary(accessor => accessor.Alias, accessor => accessor.Accessor, StringComparer.Ordinal),
                descriptorMembers);
        }

        private static string BuildSemanticFingerprint(
            string schemaName,
            string schemaVersion,
            IReadOnlyList<CultDocumentMemberDescriptor> members)
        {
            var builder = new StringBuilder();
            builder.Append(schemaName)
                .Append('|')
                .Append(schemaVersion);
            foreach (var member in members.OrderBy(member => member.Slot))
            {
                builder.Append('|')
                    .Append(member.Slot)
                    .Append(':')
                    .Append(member.MemberName)
                    .Append(':')
                    .Append(member.TypeName)
                    .Append(':')
                    .Append(member.IsReference ? "ref" : "value")
                    .Append(':')
                    .Append(member.TargetSchemaName ?? string.Empty)
                    .Append(':')
                    .Append(member.IsMany ? "many" : "one");
            }

            return builder.ToString();
        }

        private static string BuildCanonicalSchemaJson(
            string schemaName,
            string schemaVersion,
            IReadOnlyList<CultDocumentMemberDescriptor> members)
        {
            var builder = new StringBuilder();
            builder.Append("{\"schemaName\":\"")
                .Append(CultSchemaTypeNames.EscapeForLiteral(schemaName))
                .Append("\",\"schemaVersion\":\"")
                .Append(CultSchemaTypeNames.EscapeForLiteral(schemaVersion))
                .Append("\",\"members\":[");
            for (var index = 0; index < members.Count; index++)
            {
                var member = members[index];
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"slot\":")
                    .Append(member.Slot)
                    .Append(",\"name\":\"")
                    .Append(CultSchemaTypeNames.EscapeForLiteral(member.MemberName))
                    .Append("\",\"type\":\"")
                    .Append(CultSchemaTypeNames.EscapeForLiteral(member.TypeName))
                    .Append("\",\"isReference\":")
                    .Append(member.IsReference ? "true" : "false")
                    .Append(",\"many\":")
                    .Append(member.IsMany ? "true" : "false")
                    .Append(",\"targetSchemaName\":");
                if (member.TargetSchemaName == null)
                {
                    builder.Append("null");
                }
                else
                {
                    builder.Append('"').Append(CultSchemaTypeNames.EscapeForLiteral(member.TargetSchemaName)).Append('"');
                }

                builder.Append(",\"indexAlias\":");
                if (member.IndexAlias == null)
                {
                    builder.Append("null");
                }
                else
                {
                    builder.Append('"').Append(CultSchemaTypeNames.EscapeForLiteral(member.IndexAlias)).Append('"');
                }

                builder.Append(",\"isName\":")
                    .Append(member.IsName ? "true" : "false")
                    .Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }

        private static IReadOnlyList<PersistedMember> DiscoverMembers(Type type)
        {
            var members = new List<PersistedMember>();

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (IsIgnored(field))
                {
                    continue;
                }

                members.Add(PersistedMember.FromMember(field, field.FieldType, value => field.GetValue(value), GetKeyValue(field)));
            }

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetMethod == null || property.SetMethod == null || IsIgnored(property))
                {
                    continue;
                }

                members.Add(PersistedMember.FromMember(property, property.PropertyType, value => property.GetValue(value), GetKeyValue(property)));
            }

            var explicitMembers = members.Where(member => member.ExplicitSlot.HasValue).OrderBy(member => member.ExplicitSlot.Value).ToArray();
            var implicitMembers = members.Where(member => !member.ExplicitSlot.HasValue).OrderBy(member => member.MetadataToken).ToArray();
            var assigned = new List<PersistedMember>(members.Count);
            var nextSlot = explicitMembers.Length == 0 ? 0 : explicitMembers.Max(member => member.ExplicitSlot.Value) + 1;

            foreach (var member in explicitMembers)
            {
                member.Slot = member.ExplicitSlot.Value;
                assigned.Add(member);
            }

            foreach (var member in implicitMembers)
            {
                member.Slot = nextSlot++;
                assigned.Add(member);
            }

            return assigned.OrderBy(member => member.Slot).ToArray();
        }

        private static bool IsIgnored(MemberInfo member)
        {
            return member.GetCustomAttributes().Any(attribute =>
            {
                var name = attribute.GetType().FullName;
                return name == "MessagePack.IgnoreMemberAttribute";
            });
        }

        private static int? GetKeyValue(MemberInfo member)
        {
            foreach (var attribute in member.GetCustomAttributes())
            {
                var name = attribute.GetType().FullName;
                if (name == "MessagePack.KeyAttribute")
                {
                    var property = attribute.GetType().GetProperty("IntKey");
                    if (property?.GetValue(attribute) is int intKey)
                    {
                        return intKey;
                    }

                    var ctorArg = attribute.GetType().GetProperty("StringKey");
                    if (ctorArg?.GetValue(attribute) is string)
                    {
                        return null;
                    }
                }
            }

            return null;
        }

        private static string Sha256(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            return "sha256:" + BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private sealed class PersistedMember
        {
            public MemberInfo Member { get; set; } = default!;
            public Type MemberType { get; set; } = default!;
            public int MetadataToken { get; set; }
            public int? ExplicitSlot { get; set; }
            public int Slot { get; set; }
            public bool IsName { get; set; }
            public string? IndexAlias { get; set; }
            public bool IsReference { get; set; }
            public bool IsMany { get; set; }
            public string? TargetSchemaName { get; set; }
            public Func<object, string> Getter { get; set; } = default!;
            public Func<object, string?> GetterNullable { get; set; } = default!;

            public static PersistedMember FromMember(
                MemberInfo member,
                Type memberType,
                Func<object, object?> getValue,
                int? explicitSlot)
            {
                var referenceAttribute = member.GetCustomAttribute<CultReferenceAttribute>();
                var targetType = ResolveReferenceTarget(memberType, referenceAttribute?.TargetType);
                var targetSchemaName = targetType?.GetCustomAttribute<CultDocumentAttribute>()?.SchemaName;
                return new PersistedMember
                {
                    Member = member,
                    MemberType = memberType,
                    MetadataToken = member.MetadataToken,
                    ExplicitSlot = explicitSlot,
                    Slot = explicitSlot ?? -1,
                    IsName = member.GetCustomAttribute<CultNameAttribute>() != null,
                    IndexAlias = ResolveIndexAlias(member),
                    IsReference = targetType != null || referenceAttribute != null,
                    IsMany = referenceAttribute?.Many ?? false,
                    TargetSchemaName = targetSchemaName,
                    Getter = document => getValue(document)?.ToString() ?? string.Empty,
                    GetterNullable = document => getValue(document)?.ToString()
                };
            }

            private static string? ResolveIndexAlias(MemberInfo member)
            {
                var indexAttribute = member.GetCustomAttribute<CultIndexAttribute>();
                if (indexAttribute == null)
                {
                    return null;
                }

                return string.IsNullOrWhiteSpace(indexAttribute.Alias)
                    ? member.Name
                    : indexAttribute.Alias;
            }

            private static Type? ResolveReferenceTarget(Type memberType, Type? explicitTarget)
            {
                if (explicitTarget != null)
                {
                    return explicitTarget;
                }

                if (memberType.IsGenericType &&
                    memberType.GetGenericTypeDefinition() == typeof(CultRecordRef<>))
                {
                    return memberType.GetGenericArguments()[0];
                }

                return null;
            }
        }
    }

    public sealed class CultCache : IDisposable
    {
        private readonly CultDocumentRegistry _registry;
        private readonly List<CacheBackingStore> _backingStores = new();
        private readonly ConcurrentDictionary<string, CultStoredDocument> _entries = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, string>> _nameMaps = new();
        private readonly ConcurrentDictionary<(Type Type, string Alias), ConcurrentDictionary<string, string>> _indexMaps = new();
        private readonly ConcurrentDictionary<Type, string> _globalKeys = new();
        private readonly ConditionalWeakTable<object, DocumentHandleBox> _documentHandles = new();
        private ILogger _logger = new NullLogger();

        public CultCache(CultDocumentRegistry? registry = null)
        {
            _registry = registry ?? CultDocumentRegistry.Shared;
            InitializeGlobals();
        }

        public ILogger Logger
        {
            get => _logger;
            set => _logger = value ?? new NullLogger();
        }

        public event Action<object?, object?>? OnUpdate;

        public IEnumerable<object> AllEntries => _entries.Values.Select(entry => entry.Document);

        public CultDocumentRegistry Registry => _registry;

        public void AddBackingStore(CacheBackingStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            store.AttachRegistry(_registry);
            store.Logger = Logger;
            store.EntryAdded.Subscribe(entry => AddStoredDocumentInternal(entry, store, raiseUpdate: true).GetAwaiter().GetResult());
            store.EntryUpdated.Subscribe(entry => AddStoredDocumentInternal(entry, store, raiseUpdate: true).GetAwaiter().GetResult());
            store.EntryDeleted.Subscribe(entry => RemoveStoredDocumentInternal(entry, store, raiseUpdate: true));
            _backingStores.Add(store);
        }

        public async Task PullAllBackingStoresAsync()
        {
            foreach (var store in _backingStores)
            {
                store.PullAll();
            }

            await Task.CompletedTask;
        }

        public async Task<CultRecordHandle<T>> AddAsync<T>(T document, CultRecordHandle<T>? handle = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var stored = await AddStoredDocumentInternal(
                CreateStoredDocument(typeof(T), document, handle?.Key),
                source: null,
                raiseUpdate: false);
            return new CultRecordHandle<T>(stored.Key);
        }

        public CultRecordHandle<T>? TryGetHandle<T>(T document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            return _documentHandles.TryGetValue(document, out var box)
                ? new CultRecordHandle<T>(box.Key)
                : null;
        }

        public object? Get(CultRecordKey key)
        {
            return _entries.TryGetValue(key.Value, out var stored)
                ? stored.Document
                : null;
        }

        public T? Get<T>(CultRecordKey key) where T : class
        {
            return Get(key) as T;
        }

        public IEnumerable<T> GetAll<T>() where T : class
        {
            var type = typeof(T);
            return _entries.Values
                .Where(entry => type.IsAssignableFrom(entry.Descriptor.DocumentType))
                .Select(entry => (T)entry.Document);
        }

        public T? GetGlobal<T>() where T : class
        {
            return _globalKeys.TryGetValue(typeof(T), out var key)
                ? Get<T>(new CultRecordKey(key))
                : null;
        }

        public T? GetByName<T>(string name) where T : class
        {
            var type = typeof(T);
            if (_nameMaps.TryGetValue(type, out var map) &&
                map.TryGetValue(name, out var key))
            {
                return Get<T>(new CultRecordKey(key));
            }

            return null;
        }

        public T? GetByIndex<T>(string alias, string value) where T : class
        {
            if (_indexMaps.TryGetValue((typeof(T), alias), out var map) &&
                map.TryGetValue(value, out var key))
            {
                return Get<T>(new CultRecordKey(key));
            }

            return null;
        }

        public T? Resolve<T>(CultRecordRef<T> reference) where T : class
        {
            return Get<T>(reference.Key);
        }

        public void Remove<T>(CultRecordHandle<T> handle)
        {
            if (_entries.TryGetValue(handle.Key.Value, out var stored))
            {
                RemoveStoredDocumentInternal(stored, source: null, raiseUpdate: false);
            }
        }

        public void Dispose()
        {
            foreach (var store in _backingStores.OfType<IDisposable>())
            {
                store.Dispose();
            }
        }

        internal CultStoredDocument CreateStoredDocument(Type documentType, object document, CultRecordKey? key = null, string? storedAt = null)
        {
            var descriptor = _registry.GetRequired(documentType);
            var resolvedKey = key ?? ResolveKey(document, descriptor);
            return new CultStoredDocument(
                resolvedKey,
                storedAt ?? DateTimeOffset.UtcNow.ToString("O"),
                descriptor,
                document);
        }

        private async Task<CultStoredDocument> AddStoredDocumentInternal(
            CultStoredDocument stored,
            CacheBackingStore? source,
            bool raiseUpdate)
        {
            CultStoredDocument? existing = null;
            _entries.TryGetValue(stored.Key.Value, out existing);
            if (existing != null)
            {
                RemoveIndexes(existing);
            }

            _entries[stored.Key.Value] = stored;
            _documentHandles.Remove(stored.Document);
            _documentHandles.Add(stored.Document, new DocumentHandleBox(stored.Key));
            AddIndexes(stored);

            foreach (var store in _backingStores)
            {
                if (store != source)
                {
                    store.Push(stored);
                }
            }

            if (raiseUpdate)
            {
                OnUpdate?.Invoke(existing?.Document, stored.Document);
            }

            await Task.CompletedTask;
            return stored;
        }

        private void RemoveStoredDocumentInternal(
            CultStoredDocument stored,
            CacheBackingStore? source,
            bool raiseUpdate)
        {
            if (!_entries.TryRemove(stored.Key.Value, out var existing))
            {
                return;
            }

            RemoveIndexes(existing);
            _documentHandles.Remove(existing.Document);

            foreach (var store in _backingStores)
            {
                if (store != source)
                {
                    store.Delete(existing);
                }
            }

            if (raiseUpdate)
            {
                OnUpdate?.Invoke(existing.Document, null);
            }
        }

        private void InitializeGlobals()
        {
            foreach (var descriptor in _registry.AllDescriptors.Where(candidate => candidate.IsGlobal))
            {
                if (descriptor.DocumentType.GetConstructor(Type.EmptyTypes) == null)
                {
                    continue;
                }

                var instance = Activator.CreateInstance(descriptor.DocumentType);
                if (instance == null)
                {
                    continue;
                }

                AddStoredDocumentInternal(
                    new CultStoredDocument(
                        new CultRecordKey($"global:{descriptor.SchemaId}"),
                        DateTimeOffset.UtcNow.ToString("O"),
                        descriptor,
                        instance),
                    source: null,
                    raiseUpdate: false).GetAwaiter().GetResult();
            }
        }

        private CultRecordKey ResolveKey(object document, CultDocumentDescriptor descriptor)
        {
            if (_documentHandles.TryGetValue(document, out var existing))
            {
                return existing.Key;
            }

            if (descriptor.IsGlobal)
            {
                return new CultRecordKey($"global:{descriptor.SchemaId}");
            }

            return new CultRecordKey(Guid.NewGuid().ToString("N"));
        }

        private void AddIndexes(CultStoredDocument stored)
        {
            if (stored.Descriptor.IsGlobal)
            {
                _globalKeys[stored.Descriptor.DocumentType] = stored.Key.Value;
            }

            if (stored.Descriptor.NameAccessor?.Invoke(stored.Document) is { Length: > 0 } name)
            {
                var map = _nameMaps.GetOrAdd(
                    stored.Descriptor.DocumentType,
                    _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
                map[name] = stored.Key.Value;
            }

            foreach (var pair in stored.Descriptor.IndexAccessors)
            {
                var value = pair.Value(stored.Document);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var map = _indexMaps.GetOrAdd(
                    (stored.Descriptor.DocumentType, pair.Key),
                    _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
                map[value] = stored.Key.Value;
            }
        }

        private void RemoveIndexes(CultStoredDocument stored)
        {
            if (stored.Descriptor.IsGlobal)
            {
                _globalKeys.TryRemove(stored.Descriptor.DocumentType, out _);
            }

            if (stored.Descriptor.NameAccessor?.Invoke(stored.Document) is { Length: > 0 } name &&
                _nameMaps.TryGetValue(stored.Descriptor.DocumentType, out var nameMap))
            {
                nameMap.TryRemove(name, out _);
            }

            foreach (var pair in stored.Descriptor.IndexAccessors)
            {
                var value = pair.Value(stored.Document);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (_indexMaps.TryGetValue((stored.Descriptor.DocumentType, pair.Key), out var map))
                {
                    map.TryRemove(value, out _);
                }
            }
        }

        private sealed class DocumentHandleBox
        {
            public DocumentHandleBox(CultRecordKey key)
            {
                Key = key;
            }

            public CultRecordKey Key { get; }
        }
    }

    public interface IRealtimeBackingStore
    {
        void ObserveChanges();
    }

    public abstract class CacheBackingStore : IDisposable
    {
        private CultDocumentRegistry? _registry;
        private ILogger _logger = new NullLogger();

        protected CultDocumentRegistry Registry =>
            _registry ?? throw new InvalidOperationException("Backing store is not attached to a CultDocumentRegistry.");

        protected ConcurrentDictionary<string, CultStoredDocument> Entries { get; } =
            new(StringComparer.Ordinal);

        public ILogger Logger
        {
            get => _logger;
            set => _logger = value ?? new NullLogger();
        }

        public Subject<CultStoredDocument> EntryAdded { get; } = new();
        public Subject<CultStoredDocument> EntryUpdated { get; } = new();
        public Subject<CultStoredDocument> EntryDeleted { get; } = new();

        internal void AttachRegistry(CultDocumentRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public abstract void PullAll();
        public abstract void Push(CultStoredDocument entry);
        public abstract void Delete(CultStoredDocument entry);
        public abstract void PushAll(bool soft = false);
        public virtual void Dispose()
        {
            EntryAdded.Dispose();
            EntryUpdated.Dispose();
            EntryDeleted.Dispose();
        }

        protected CultPersistedRecord ToPersistedRecord(CultStoredDocument entry, Func<object, byte[]> serializePayload)
        {
            return new CultPersistedRecord
            {
                Key = entry.Key.Value,
                SchemaId = entry.Descriptor.SchemaId,
                StoredAt = entry.StoredAt,
                Payload = serializePayload(entry.Document)
            };
        }

        protected CultStoredDocument ToStoredDocument(
            CultPersistedRecord record,
            IReadOnlyCollection<CultSchemaCatalogEntry> catalog,
            Func<Type, byte[], object> deserializePayload)
        {
            var descriptor = Registry.ResolvePersistedSchema(record.SchemaId, catalog);
            var document = deserializePayload(descriptor.DocumentType, record.Payload);
            return new CultStoredDocument(
                new CultRecordKey(record.Key),
                record.StoredAt,
                descriptor,
                document);
        }
    }

    public abstract class MultiFileBackingStore : CacheBackingStore, IRealtimeBackingStore
    {
        private readonly ConcurrentDictionary<string, string> _pathsByKey = new(StringComparer.Ordinal);

        protected MultiFileBackingStore(string rootPath)
        {
            RootDirectory = new DirectoryInfo(rootPath);
        }

        protected DirectoryInfo RootDirectory { get; }
        protected abstract byte[] SerializeRecord(CultPersistedRecord record);
        protected abstract CultPersistedRecord DeserializeRecord(byte[] data);
        protected abstract byte[] SerializeCatalog(CultSchemaCatalogEntry[] catalog);
        protected abstract CultSchemaCatalogEntry[] DeserializeCatalog(byte[] data);
        protected abstract byte[] SerializePayload(object document);
        protected abstract object DeserializePayload(Type documentType, byte[] payload);
        public abstract string Extension { get; }
        protected virtual string CatalogFileName => $"__cultcache.catalog.{Extension}";

        public override void PullAll()
        {
            if (!RootDirectory.Exists)
            {
                return;
            }

            var catalog = LoadCatalog();
            foreach (var file in RootDirectory.EnumerateFiles($"*.{Extension}", SearchOption.AllDirectories)
                         .Where(file => !string.Equals(file.Name, CatalogFileName, StringComparison.OrdinalIgnoreCase)))
            {
                var stored = ToStoredDocument(
                    DeserializeRecord(File.ReadAllBytes(file.FullName)),
                    catalog,
                    DeserializePayload);
                Entries[stored.Key.Value] = stored;
                _pathsByKey[stored.Key.Value] = file.FullName;
                EntryAdded.OnNext(stored);
            }
        }

        public override void Push(CultStoredDocument entry)
        {
            Entries[entry.Key.Value] = entry;
            var filePath = GetFilePath(entry);
            if (_pathsByKey.TryGetValue(entry.Key.Value, out var existingPath) &&
                !string.Equals(existingPath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(existingPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllBytes(filePath, SerializeRecord(ToPersistedRecord(entry, SerializePayload)));
            _pathsByKey[entry.Key.Value] = filePath;
            PersistCatalog();
        }

        public override void Delete(CultStoredDocument entry)
        {
            Entries.TryRemove(entry.Key.Value, out _);
            if (_pathsByKey.TryRemove(entry.Key.Value, out var existingPath))
            {
                TryDeleteFile(existingPath);
            }

            PersistCatalog();
        }

        public override void PushAll(bool soft = false)
        {
            foreach (var entry in Entries.Values.ToArray())
            {
                Push(entry);
            }
        }

        public virtual void ObserveChanges()
        {
        }

        private CultSchemaCatalogEntry[] LoadCatalog()
        {
            var catalogFile = Path.Combine(RootDirectory.FullName, CatalogFileName);
            if (!File.Exists(catalogFile))
            {
                return Array.Empty<CultSchemaCatalogEntry>();
            }

            return DeserializeCatalog(File.ReadAllBytes(catalogFile));
        }

        private void PersistCatalog()
        {
            Directory.CreateDirectory(RootDirectory.FullName);
            var catalogFile = Path.Combine(RootDirectory.FullName, CatalogFileName);
            var catalog = Entries.Values
                .Select(entry => entry.Descriptor.ToCatalogEntry())
                .GroupBy(entry => entry.SchemaId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(entry => entry.SchemaName, StringComparer.Ordinal)
                .ToArray();
            File.WriteAllBytes(catalogFile, SerializeCatalog(catalog));
        }

        private string GetFilePath(CultStoredDocument entry)
        {
            var directory = Path.Combine(RootDirectory.FullName, entry.Descriptor.SchemaName);
            var fileName = SanitizeFileName(entry.Descriptor.GetPreferredFileStem(entry.Document));
            return Path.Combine(directory, $"{fileName}.{Extension}");
        }

        private static string SanitizeFileName(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value;
        }

        private static void TryDeleteFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public abstract class SingleFileBackingStore : CacheBackingStore
    {
        protected SingleFileBackingStore(string filePath)
        {
            FileInfo = new FileInfo(filePath);
        }

        protected FileInfo FileInfo { get; }
        protected abstract byte[] SerializeSnapshot(CultPersistedStoreSnapshot snapshot);
        protected abstract CultPersistedStoreSnapshot DeserializeSnapshot(byte[] data);
        protected abstract byte[] SerializePayload(object document);
        protected abstract object DeserializePayload(Type documentType, byte[] payload);

        public override void PullAll()
        {
            if (!FileInfo.Exists)
            {
                return;
            }

            var snapshot = DeserializeSnapshot(File.ReadAllBytes(FileInfo.FullName));
            foreach (var record in snapshot.Records)
            {
                var stored = ToStoredDocument(record, snapshot.SchemaCatalog, DeserializePayload);
                Entries[stored.Key.Value] = stored;
                EntryAdded.OnNext(stored);
            }
        }

        public override void Push(CultStoredDocument entry)
        {
            Entries[entry.Key.Value] = entry;
        }

        public override void Delete(CultStoredDocument entry)
        {
            Entries.TryRemove(entry.Key.Value, out _);
        }

        public override void PushAll(bool soft = false)
        {
            var snapshot = new CultPersistedStoreSnapshot
            {
                SchemaCatalog = Entries.Values
                    .Select(entry => entry.Descriptor.ToCatalogEntry())
                    .GroupBy(entry => entry.SchemaId, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(entry => entry.SchemaName, StringComparer.Ordinal)
                    .ToArray(),
                Records = Entries.Values
                    .OrderBy(entry => entry.Key.Value, StringComparer.Ordinal)
                    .Select(entry => ToPersistedRecord(entry, SerializePayload))
                    .ToArray()
            };

            Directory.CreateDirectory(FileInfo.DirectoryName!);
            File.WriteAllBytes(FileInfo.FullName, SerializeSnapshot(snapshot));
        }
    }
}
