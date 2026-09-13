using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        public CultSchemaMemberCatalogEntry[] Members { get; set; } = Array.Empty<CultSchemaMemberCatalogEntry>();
    }

    public sealed class CultSchemaMemberCatalogEntry
    {
        public int Slot { get; set; }

        public string MemberName { get; set; } = string.Empty;

        public string TypeName { get; set; } = string.Empty;

        public bool IsReference { get; set; }

        public bool IsMany { get; set; }

        public string? TargetSchemaName { get; set; }

        public bool IsName { get; set; }

        public string? IndexAlias { get; set; }
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

    public enum CultSchemaMigrationKind
    {
        Exact,

        CompatibleDrift
    }

    public sealed class CultSchemaMigrationWarning
    {
        public string Code { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;
    }

    public sealed class CultSchemaMigrationReport
    {
        public string PersistedSchemaId { get; set; } = string.Empty;

        public string LocalSchemaId { get; set; } = string.Empty;

        public string PersistedSchemaName { get; set; } = string.Empty;

        public string LocalSchemaName { get; set; } = string.Empty;

        public CultSchemaMigrationKind Kind { get; set; }

        public int[] IgnoredExtraSlots { get; set; } = Array.Empty<int>();

        public int[] DefaultedMissingSlots { get; set; } = Array.Empty<int>();

        public CultSchemaMigrationWarning[] Warnings { get; set; } = Array.Empty<CultSchemaMigrationWarning>();
    }

    internal sealed class CultSchemaResolutionResult
    {
        public CultSchemaResolutionResult(CultDocumentDescriptor descriptor, CultSchemaMigrationReport report)
        {
            Descriptor = descriptor;
            Report = report;
        }

        public CultDocumentDescriptor Descriptor { get; }

        public CultSchemaMigrationReport Report { get; }
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
            Func<object, byte[]>? generatedPayloadSerializer,
            Func<byte[], object>? generatedPayloadDeserializer,
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
            GeneratedPayloadSerializer = generatedPayloadSerializer;
            GeneratedPayloadDeserializer = generatedPayloadDeserializer;
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
        public Func<object, byte[]>? GeneratedPayloadSerializer { get; }
        public Func<byte[], object>? GeneratedPayloadDeserializer { get; }
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
                CompatibleSchemaIds = [SchemaId],
                Members = Members
                    .OrderBy(member => member.Slot)
                    .Select(member => new CultSchemaMemberCatalogEntry
                    {
                        Slot = member.Slot,
                        MemberName = member.MemberName,
                        TypeName = member.TypeName,
                        IsReference = member.IsReference,
                        IsMany = member.IsMany,
                        TargetSchemaName = member.TargetSchemaName,
                        IsName = member.IsName,
                        IndexAlias = member.IndexAlias
                    })
                    .ToArray()
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

        private volatile RegistryIndexes _indexes = new();
        private readonly object _registrationGate = new();

        public static CultDocumentRegistry Shared => SharedRegistry.Value;

        public CultDocumentRegistry()
        {
            Refresh();
        }

        private CultDocumentRegistry(bool discoverLoadedDocuments)
        {
            if (discoverLoadedDocuments)
                Refresh();
        }

        public static CultDocumentRegistry ForTypes(IEnumerable<Type> documentTypes)
        {
            if (documentTypes == null) throw new ArgumentNullException(nameof(documentTypes));
            var registry = new CultDocumentRegistry(discoverLoadedDocuments: false);
            foreach (var type in documentTypes.Distinct())
            {
                registry.GetRequired(type ?? throw new ArgumentException(
                    "Document type collections cannot contain null entries.",
                    nameof(documentTypes)));
            }
            return registry;
        }

        public IEnumerable<CultDocumentDescriptor> AllDescriptors =>
            _indexes.ByType.Values.OrderBy(d => d.SchemaName, StringComparer.Ordinal);

        public void Refresh()
        {
            lock (_registrationGate)
            {
                var rebuilt = new RegistryIndexes();
                var generatedTypes = new HashSet<Type>();
                foreach (var definition in CultGeneratedDocumentMetadataLoader.LoadDefinitions())
                {
                    var descriptor = BuildDescriptor(definition);
                    RegisterDescriptor(rebuilt, descriptor);
                    generatedTypes.Add(descriptor.DocumentType);
                }

                foreach (var type in ReflectionExtensions.GetAttributedDocumentTypes().Where(type => !generatedTypes.Contains(type)))
                {
                    RegisterDescriptor(rebuilt, BuildDescriptor(type));
                }

                _indexes = rebuilt;
            }
        }

        public CultDocumentDescriptor GetRequired(Type type)
        {
            if (_indexes.ByType.TryGetValue(type, out var descriptor))
            {
                return descriptor;
            }

            descriptor = TryBuildGeneratedDescriptor(type);
            if (descriptor != null)
            {
                return RegisterDescriptor(descriptor);
            }

            var attribute = type.GetCustomAttribute<CultDocumentAttribute>();
            if (attribute == null)
            {
                throw new InvalidOperationException(
                    $"Type {type.FullName} is not marked with {nameof(CultDocumentAttribute)}.");
            }

            descriptor = BuildDescriptor(type);
            return RegisterDescriptor(descriptor);
        }

        public CultDocumentDescriptor GetRequired<T>() where T : class
        {
            return GetRequired(typeof(T));
        }

        public CultDocumentDescriptor GetRequiredBySchemaId(string schemaId)
        {
            if (_indexes.BySchemaId.TryGetValue(schemaId, out var descriptor))
            {
                return descriptor;
            }

            throw new InvalidOperationException($"Unknown CultCache schema id '{schemaId}'.");
        }

        public CultDocumentDescriptor ResolvePersistedSchema(string schemaId, IReadOnlyCollection<CultSchemaCatalogEntry> catalog)
        {
            return ResolvePersistedSchemaDetailed(schemaId, catalog).Descriptor;
        }

        public CultSchemaMigrationReport ResolvePersistedSchemaReport(string schemaId, IReadOnlyCollection<CultSchemaCatalogEntry> catalog)
        {
            return ResolvePersistedSchemaDetailed(schemaId, catalog).Report;
        }

        internal CultSchemaResolutionResult ResolvePersistedSchemaDetailed(string schemaId, IReadOnlyCollection<CultSchemaCatalogEntry> catalog)
        {
            var indexes = _indexes;
            if (indexes.BySchemaId.TryGetValue(schemaId, out var exact))
            {
                return new CultSchemaResolutionResult(
                    exact,
                    new CultSchemaMigrationReport
                    {
                        PersistedSchemaId = schemaId,
                        LocalSchemaId = exact.SchemaId,
                        PersistedSchemaName = exact.SchemaName,
                        LocalSchemaName = exact.SchemaName,
                        Kind = CultSchemaMigrationKind.Exact
                    });
            }

            var persisted = catalog.FirstOrDefault(entry => string.Equals(entry.SchemaId, schemaId, StringComparison.Ordinal));
            if (persisted == null)
            {
                throw new InvalidOperationException($"Persisted schema '{schemaId}' is not present in the embedded catalog.");
            }

            foreach (var compatibleSchemaId in persisted.CompatibleSchemaIds.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
            {
                if (indexes.BySchemaId.TryGetValue(compatibleSchemaId, out var compatibleLocal))
                {
                    return BuildCompatibleResolutionResult(persisted, compatibleLocal);
                }
            }

            if (indexes.BySchemaName.TryGetValue(persisted.SchemaName, out var localVersions))
            {
                if (localVersions.Length == 1)
                {
                    return BuildCompatibleResolutionResult(persisted, localVersions[0]);
                }

                throw new InvalidOperationException(
                    $"CultCache schema name '{persisted.SchemaName}' is ambiguous across local versions " +
                    $"[{string.Join(", ", localVersions.Select(candidate => candidate.SchemaVersion).OrderBy(version => version, StringComparer.Ordinal))}]. " +
                    "Persisted schema compatibility must identify an explicit schema id.");
            }

            throw new InvalidOperationException(
                $"No local CultCache schema matches persisted schema '{persisted.SchemaName}' ({schemaId}).");
        }

        private CultDocumentDescriptor RegisterDescriptor(CultDocumentDescriptor descriptor)
        {
            lock (_registrationGate)
            {
                var current = _indexes;
                if (current.ByType.TryGetValue(descriptor.DocumentType, out var registered))
                    return registered;

                var updated = new RegistryIndexes(current);
                registered = RegisterDescriptor(updated, descriptor);
                _indexes = updated;
                return registered;
            }
        }

        private static CultDocumentDescriptor RegisterDescriptor(
            RegistryIndexes indexes,
            CultDocumentDescriptor descriptor)
        {
            if (indexes.ByType.TryGetValue(descriptor.DocumentType, out var registered))
                return registered;

            indexes.BySchemaName.TryGetValue(descriptor.SchemaName, out var schemaNameVersions);
            var schemaVersionOwner = schemaNameVersions?.FirstOrDefault(candidate =>
                string.Equals(candidate.SchemaVersion, descriptor.SchemaVersion, StringComparison.Ordinal));
            if (schemaVersionOwner != null)
            {
                if (IsExactWireAlias(schemaVersionOwner, descriptor))
                {
                    indexes.ByType[descriptor.DocumentType] = descriptor;
                    return descriptor;
                }

                throw DuplicateSchemaRegistration(
                    $"schema name '{descriptor.SchemaName}' version '{descriptor.SchemaVersion}'",
                    schemaVersionOwner.DocumentType,
                    descriptor.DocumentType);
            }

            if (indexes.BySchemaId.TryGetValue(descriptor.SchemaId, out var schemaIdOwner))
            {
                throw DuplicateSchemaRegistration(
                    $"schema id '{descriptor.SchemaId}'",
                    schemaIdOwner.DocumentType,
                    descriptor.DocumentType);
            }

            indexes.ByType[descriptor.DocumentType] = descriptor;
            indexes.BySchemaId[descriptor.SchemaId] = descriptor;
            indexes.BySchemaName[descriptor.SchemaName] = schemaNameVersions == null
                ? [descriptor]
                : [.. schemaNameVersions, descriptor];
            return descriptor;
        }

        private static bool IsExactWireAlias(
            CultDocumentDescriptor canonical,
            CultDocumentDescriptor candidate) =>
            string.Equals(canonical.SchemaName, candidate.SchemaName, StringComparison.Ordinal) &&
            string.Equals(canonical.SchemaVersion, candidate.SchemaVersion, StringComparison.Ordinal) &&
            string.Equals(canonical.SchemaId, candidate.SchemaId, StringComparison.Ordinal) &&
            string.Equals(canonical.ContentHash, candidate.ContentHash, StringComparison.Ordinal) &&
            string.Equals(canonical.CanonicalSchemaJson, candidate.CanonicalSchemaJson, StringComparison.Ordinal);

        private static InvalidOperationException DuplicateSchemaRegistration(
            string identity,
            Type existingType,
            Type claimedType) =>
            new(
                $"CultCache {identity} is already registered to CLR type " +
                $"'{existingType.FullName}' and cannot also be claimed by '{claimedType.FullName}'. " +
                "Schema compatibility must be declared through explicit persisted-schema alias metadata.");

        private sealed class RegistryIndexes
        {
            public RegistryIndexes()
            {
                ByType = new Dictionary<Type, CultDocumentDescriptor>();
                BySchemaId = new Dictionary<string, CultDocumentDescriptor>(StringComparer.Ordinal);
                BySchemaName = new Dictionary<string, CultDocumentDescriptor[]>(StringComparer.Ordinal);
            }

            public RegistryIndexes(RegistryIndexes source)
            {
                ByType = new Dictionary<Type, CultDocumentDescriptor>(source.ByType);
                BySchemaId = new Dictionary<string, CultDocumentDescriptor>(source.BySchemaId, StringComparer.Ordinal);
                BySchemaName = new Dictionary<string, CultDocumentDescriptor[]>(source.BySchemaName, StringComparer.Ordinal);
            }

            public Dictionary<Type, CultDocumentDescriptor> ByType { get; }
            public Dictionary<string, CultDocumentDescriptor> BySchemaId { get; }
            public Dictionary<string, CultDocumentDescriptor[]> BySchemaName { get; }
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
                null,
                null,
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
                definition.SerializePayload,
                definition.DeserializePayload,
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

        private static CultSchemaResolutionResult BuildCompatibleResolutionResult(
            CultSchemaCatalogEntry persisted,
            CultDocumentDescriptor local)
        {
            if (string.Equals(persisted.ContentHash, local.ContentHash, StringComparison.Ordinal))
            {
                return new CultSchemaResolutionResult(
                    local,
                    new CultSchemaMigrationReport
                    {
                        PersistedSchemaId = persisted.SchemaId,
                        LocalSchemaId = local.SchemaId,
                        PersistedSchemaName = persisted.SchemaName,
                        LocalSchemaName = local.SchemaName,
                        Kind = CultSchemaMigrationKind.Exact
                    });
            }

            var persistedSchema = new CanonicalSchemaShape(
                persisted.SchemaName,
                persisted.SchemaVersion,
                persisted.Members.Select(member => new CanonicalSchemaMember(
                    member.Slot,
                    member.MemberName,
                    member.TypeName,
                    member.IsReference,
                    member.IsMany,
                    member.TargetSchemaName,
                    member.IsName,
                    member.IndexAlias)).ToArray());
            var localSchema = new CanonicalSchemaShape(
                local.SchemaName,
                local.SchemaVersion,
                local.Members.Select(member => new CanonicalSchemaMember(
                    member.Slot,
                    member.MemberName,
                    member.TypeName,
                    member.IsReference,
                    member.IsMany,
                    member.TargetSchemaName,
                    member.IsName,
                    member.IndexAlias)).ToArray());
            var comparison = CompareSchemaShapes(persistedSchema, localSchema);
            if (!comparison.IsCompatible)
            {
                throw new InvalidOperationException(
                    $"Persisted schema '{persisted.SchemaName}' ({persisted.SchemaId}) is incompatible with local schema '{local.SchemaName}' ({local.SchemaId}): {string.Join("; ", comparison.Errors)}");
            }

            var warnings = new List<CultSchemaMigrationWarning>();
            if (!string.Equals(persisted.SchemaId, local.SchemaId, StringComparison.Ordinal))
            {
                warnings.Add(new CultSchemaMigrationWarning
                {
                    Code = "compatible_schema_id_drift",
                    Message = $"Persisted schema '{persisted.SchemaId}' resolved to local schema '{local.SchemaId}'."
                });
            }

            if (!string.Equals(persisted.ContentHash, local.ContentHash, StringComparison.Ordinal))
            {
                warnings.Add(new CultSchemaMigrationWarning
                {
                    Code = "content_hash_drift",
                    Message = $"Persisted schema content hash '{persisted.ContentHash}' differs from local hash '{local.ContentHash}'."
                });
            }

            warnings.AddRange(comparison.Warnings);

            return new CultSchemaResolutionResult(
                local,
                new CultSchemaMigrationReport
                {
                    PersistedSchemaId = persisted.SchemaId,
                    LocalSchemaId = local.SchemaId,
                    PersistedSchemaName = persisted.SchemaName,
                    LocalSchemaName = local.SchemaName,
                    Kind = CultSchemaMigrationKind.CompatibleDrift,
                    IgnoredExtraSlots = comparison.IgnoredExtraSlots.OrderBy(slot => slot).ToArray(),
                    DefaultedMissingSlots = comparison.DefaultedMissingSlots.OrderBy(slot => slot).ToArray(),
                    Warnings = warnings.ToArray()
                });
        }

        private static SchemaShapeComparison CompareSchemaShapes(CanonicalSchemaShape persisted, CanonicalSchemaShape local)
        {
            var errors = new List<string>();
            var warnings = new List<CultSchemaMigrationWarning>();
            var ignoredExtraSlots = new List<int>();
            var defaultedMissingSlots = new List<int>();

            if (!string.Equals(persisted.SchemaName, local.SchemaName, StringComparison.Ordinal))
            {
                errors.Add($"schema name drift '{persisted.SchemaName}' -> '{local.SchemaName}'");
            }

            var persistedBySlot = persisted.Members.ToDictionary(member => member.Slot);
            var localBySlot = local.Members.ToDictionary(member => member.Slot);

            foreach (var localMember in local.Members.OrderBy(member => member.Slot))
            {
                if (!persistedBySlot.TryGetValue(localMember.Slot, out var persistedMember))
                {
                    defaultedMissingSlots.Add(localMember.Slot);
                    warnings.Add(new CultSchemaMigrationWarning
                    {
                        Code = "defaulted_missing_slot",
                        Message = $"Local slot {localMember.Slot} ('{localMember.Name}') is missing from the persisted schema and will use the local default value."
                    });
                    continue;
                }

                if (!string.Equals(localMember.TypeName, persistedMember.TypeName, StringComparison.Ordinal))
                {
                    errors.Add($"slot {localMember.Slot} changed type '{persistedMember.TypeName}' -> '{localMember.TypeName}'");
                }

                if (localMember.IsReference != persistedMember.IsReference)
                {
                    errors.Add($"slot {localMember.Slot} changed reference semantics");
                }

                if (localMember.IsMany != persistedMember.IsMany)
                {
                    errors.Add($"slot {localMember.Slot} changed cardinality");
                }

                if (!string.Equals(localMember.TargetSchemaName, persistedMember.TargetSchemaName, StringComparison.Ordinal))
                {
                    errors.Add($"slot {localMember.Slot} changed target schema '{persistedMember.TargetSchemaName ?? "<none>"}' -> '{localMember.TargetSchemaName ?? "<none>"}'");
                }

                if (localMember.IsName != persistedMember.IsName)
                {
                    errors.Add($"slot {localMember.Slot} changed name-lookup semantics");
                }

                if (!string.Equals(localMember.IndexAlias, persistedMember.IndexAlias, StringComparison.Ordinal))
                {
                    errors.Add($"slot {localMember.Slot} changed index alias '{persistedMember.IndexAlias ?? "<none>"}' -> '{localMember.IndexAlias ?? "<none>"}'");
                }
            }

            foreach (var persistedMember in persisted.Members.OrderBy(member => member.Slot))
            {
                if (localBySlot.ContainsKey(persistedMember.Slot))
                {
                    continue;
                }

                ignoredExtraSlots.Add(persistedMember.Slot);
                warnings.Add(new CultSchemaMigrationWarning
                {
                    Code = "ignored_extra_slot",
                    Message = $"Persisted slot {persistedMember.Slot} ('{persistedMember.Name}') is not present locally and will be ignored."
                });
            }

            return new SchemaShapeComparison(
                errors.Count == 0,
                errors.ToArray(),
                warnings.ToArray(),
                ignoredExtraSlots.ToArray(),
                defaultedMissingSlots.ToArray());
        }

        private sealed class CanonicalSchemaShape
        {
            public CanonicalSchemaShape(string schemaName, string schemaVersion, CanonicalSchemaMember[] members)
            {
                SchemaName = schemaName;
                SchemaVersion = schemaVersion;
                Members = members;
            }

            public string SchemaName { get; }

            public string SchemaVersion { get; }

            public CanonicalSchemaMember[] Members { get; }
        }

        private sealed class CanonicalSchemaMember
        {
            public CanonicalSchemaMember(
                int slot,
                string name,
                string typeName,
                bool isReference,
                bool isMany,
                string? targetSchemaName,
                bool isName,
                string? indexAlias)
            {
                Slot = slot;
                Name = name;
                TypeName = typeName;
                IsReference = isReference;
                IsMany = isMany;
                TargetSchemaName = targetSchemaName;
                IsName = isName;
                IndexAlias = indexAlias;
            }

            public int Slot { get; }

            public string Name { get; }

            public string TypeName { get; }

            public bool IsReference { get; }

            public bool IsMany { get; }

            public string? TargetSchemaName { get; }

            public bool IsName { get; }

            public string? IndexAlias { get; }
        }

        private sealed class SchemaShapeComparison
        {
            public SchemaShapeComparison(
                bool isCompatible,
                string[] errors,
                CultSchemaMigrationWarning[] warnings,
                int[] ignoredExtraSlots,
                int[] defaultedMissingSlots)
            {
                IsCompatible = isCompatible;
                Errors = errors;
                Warnings = warnings;
                IgnoredExtraSlots = ignoredExtraSlots;
                DefaultedMissingSlots = defaultedMissingSlots;
            }

            public bool IsCompatible { get; }

            public string[] Errors { get; }

            public CultSchemaMigrationWarning[] Warnings { get; }

            public int[] IgnoredExtraSlots { get; }

            public int[] DefaultedMissingSlots { get; }
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

            var explicitMembers = members.Where(member => member.ExplicitSlot.HasValue).OrderBy(member => member.ExplicitSlot.GetValueOrDefault()).ToArray();
            var implicitMembers = members.Where(member => !member.ExplicitSlot.HasValue).OrderBy(member => member.MetadataToken).ToArray();
            var assigned = new List<PersistedMember>(members.Count);
            var nextSlot = explicitMembers.Length == 0 ? 0 : explicitMembers.Max(member => member.ExplicitSlot.GetValueOrDefault()) + 1;

            foreach (var member in explicitMembers)
            {
                member.Slot = member.ExplicitSlot.GetValueOrDefault();
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
        private readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, CultStoredDocument>> _typeMaps = new();
        private readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, string>> _nameMaps = new();
        private readonly ConcurrentDictionary<(Type Type, string Alias), ConcurrentDictionary<string, string>> _indexMaps = new();
        private readonly ConcurrentDictionary<Type, string> _globalKeys = new();
        private readonly ConditionalWeakTable<object, DocumentHandleBox> _documentHandles = new();
        private readonly Subject<object> _changes = new();
        private readonly SemaphoreSlim _transactionGate = new(1, 1);
        private readonly AsyncLocal<CultCacheTransaction?> _ambientTransaction = new();
        private readonly object _stateGate = new();
        private bool _hasUnflushedMutations;

        public CultCache(CultDocumentRegistry? registry = null)
            : this(registry, initializeGlobals: true)
        {
        }

        public CultCache(CultDocumentRegistry? registry, bool initializeGlobals)
        {
            _registry = registry ?? CultDocumentRegistry.Shared;
            if (initializeGlobals)
            {
                MaterializeMissingGlobals();
            }
        }

        // Durable open paths call this after hydration so persisted globals remain authoritative.
        public void MaterializeMissingGlobals()
        {
            InitializeGlobals();
        }

        public bool IsDirty => _hasUnflushedMutations || _backingStores.Any(store => store.IsDirty);

        public bool FlushAttachedStoresOnDispose { get; set; }

        public event Action<object?, object?>? OnUpdate;

        public IReadOnlyList<CacheBackingStore> BackingStores => _backingStores;

        public IEnumerable<object> AllEntries
        {
            get
            {
                lock (_stateGate)
                    return VisibleStoredDocuments().Select(entry => entry.Document).ToArray();
            }
        }

        public IEnumerable<CultStoredDocument> AllStoredDocuments
        {
            get
            {
                lock (_stateGate)
                    return VisibleStoredDocuments()
                        .OrderBy(entry => entry.Descriptor.SchemaName, StringComparer.Ordinal)
                        .ThenBy(entry => entry.Key.Value, StringComparer.Ordinal)
                        .ToArray();
            }
        }

        public CultDocumentRegistry Registry => _registry;

        public Observable<CultCacheDocumentChange<T>> Watch<T>() where T : class
        {
            return _changes
                .Where(change => change is CultCacheDocumentChange<T>)
                .Select(change => (CultCacheDocumentChange<T>)change);
        }

        public Observable<CultCacheDocumentChange<T>> WatchRecord<T>(CultRecordKey key) where T : class
        {
            return Watch<T>().Where(change => change.Key.Equals(key));
        }

        public void AddBackingStore(CacheBackingStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            store.AttachRegistry(_registry);
            store.Loaded = entry => AddStoredDocumentInternal(entry, store, raiseUpdate: true);
            store.Unloaded = entry => RemoveStoredDocumentInternal(entry, store, raiseUpdate: true);

            foreach (var entry in _entries.Values.OrderBy(entry => entry.Key.Value, StringComparer.Ordinal))
            {
                store.Push(entry);
            }

            _backingStores.Add(store);
        }

        public async Task PullAllBackingStoresAsync()
        {
            if (_ambientTransaction.Value != null)
                throw new InvalidOperationException(
                    "CultCache hydration cannot run inside a mutation transaction; hydrate before opening the commit scope.");

            await _transactionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var store in _backingStores)
                    store.PullAll();

                _hasUnflushedMutations = _backingStores.Any(store => store.IsDirty);
            }
            finally
            {
                _transactionGate.Release();
            }
        }

        public void FlushAllBackingStores()
        {
            if (_ambientTransaction.Value != null)
                throw new InvalidOperationException("A CultCache transaction owns durable commit; do not flush inside its stage callback.");

            _transactionGate.Wait();
            try
            {
                foreach (var store in _backingStores)
                {
                    store.PushAll();
                    RecomputeDirtyState();
                }
            }
            finally
            {
                _transactionGate.Release();
            }
        }

        // soft is dead. CultCacheStudioWindow reflects on FlushAsync(bool) until Cut 6 rewrites it.
        public Task FlushAsync(bool soft = false)
        {
            FlushAllBackingStores();
            return Task.CompletedTask;
        }

        // Staged records are visible only to the executing async flow. The durable backing store
        // commits before the live cache and its observers advance; an exception discards the whole batch.
        public async Task ExecuteTransactionAsync(Func<Task> stageAsync)
        {
            if (stageAsync == null) throw new ArgumentNullException(nameof(stageAsync));
            if (_ambientTransaction.Value != null)
            {
                await stageAsync().ConfigureAwait(false);
                return;
            }

            await _transactionGate.WaitAsync().ConfigureAwait(false);
            var transaction = new CultCacheTransaction();
            IReadOnlyList<(CultStoredDocument Stored, object? Previous, bool Removed)> changes;
            _ambientTransaction.Value = transaction;
            try
            {
                await stageAsync().ConfigureAwait(false);
                transaction.Seal();
                changes = CommitTransaction(transaction);
            }
            finally
            {
                transaction.Seal();
                _ambientTransaction.Value = null;
                _transactionGate.Release();
            }

            foreach (var change in changes)
                PublishChange(change.Stored, change.Previous, change.Removed);
        }

        public async Task<T> ExecuteTransactionAsync<T>(Func<Task<T>> stageAsync)
        {
            if (stageAsync == null) throw new ArgumentNullException(nameof(stageAsync));
            T result = default!;
            await ExecuteTransactionAsync(async () =>
            {
                result = await stageAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
            return result;
        }

        public async Task<CultRecordHandle<T>> AddAsync<T>(T document, CultRecordHandle<T>? handle = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (_ambientTransaction.Value is { } transaction)
            {
                var staged = CreateStoredDocument(typeof(T), document, handle?.Key);
                transaction.Stage(staged);
                return new CultRecordHandle<T>(staged.Key);
            }

            await _transactionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var stored = CreateStoredDocument(typeof(T), document, handle?.Key);
                AddStoredDocumentInternal(stored, source: null, raiseUpdate: false);
                return new CultRecordHandle<T>(stored.Key);
            }
            finally
            {
                _transactionGate.Release();
            }
        }

        public Task<CultRecordHandle<T>> UpsertAsync<T>(T document, CultRecordHandle<T>? handle = null)
        {
            return AddAsync(document, handle);
        }

        public async Task<CultRecordKey> UpsertAsync(Type documentType, object document, CultRecordKey? key = null)
        {
            if (documentType == null) throw new ArgumentNullException(nameof(documentType));
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (!documentType.IsInstanceOfType(document))
            {
                throw new ArgumentException(
                    $"Document instance must be assignable to {documentType.FullName}.",
                    nameof(document));
            }

            if (_ambientTransaction.Value is { } transaction)
            {
                var staged = CreateStoredDocument(documentType, document, key);
                transaction.Stage(staged);
                return staged.Key;
            }

            await _transactionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var stored = CreateStoredDocument(documentType, document, key);
                AddStoredDocumentInternal(stored, source: null, raiseUpdate: false);
                return stored.Key;
            }
            finally
            {
                _transactionGate.Release();
            }
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
            lock (_stateGate)
            {
                if (_ambientTransaction.Value is { } transaction &&
                    transaction.TryGet(key, out var staged))
                    return staged?.Document;
                return _entries.TryGetValue(key.Value, out var stored)
                    ? stored.Document
                    : null;
            }
        }

        public T? Get<T>(CultRecordKey key) where T : class
        {
            return Get(key) as T;
        }

        public bool TryGet<T>(CultRecordKey key, out T? document) where T : class
        {
            document = Get<T>(key);
            return document != null;
        }

        public IEnumerable<T> GetAll<T>() where T : class
        {
            lock (_stateGate)
            {
                var type = typeof(T);
                return VisibleStoredDocuments()
                    .Where(entry => type.IsAssignableFrom(entry.Descriptor.DocumentType))
                    .Select(entry => (T)entry.Document)
                    .ToArray();
            }
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

        public void Remove<T>(CultRecordHandle<T> handle)
        {
            if (_ambientTransaction.Value is { } transaction)
            {
                transaction.Delete(handle.Key);
                return;
            }

            _transactionGate.Wait();
            try
            {
                if (_entries.TryGetValue(handle.Key.Value, out var stored))
                    RemoveStoredDocumentInternal(stored, source: null, raiseUpdate: false);
            }
            finally
            {
                _transactionGate.Release();
            }
        }

        public bool Remove(CultRecordKey key)
        {
            if (Get(key) == null)
            {
                return false;
            }

            if (_ambientTransaction.Value is { } transaction)
            {
                transaction.Delete(key);
                return true;
            }

            _transactionGate.Wait();
            try
            {
                if (_entries.TryGetValue(key.Value, out var stored))
                    RemoveStoredDocumentInternal(stored, source: null, raiseUpdate: false);
            }
            finally
            {
                _transactionGate.Release();
            }
            return true;
        }

        public Task DeleteAsync<T>(CultRecordHandle<T> handle)
        {
            Remove(handle);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (FlushAttachedStoresOnDispose && IsDirty)
            {
                FlushAllBackingStores();
            }

            foreach (var store in _backingStores.OfType<IDisposable>())
            {
                store.Dispose();
            }

            _transactionGate.Dispose();
            _changes.Dispose();
        }

        private IEnumerable<CultStoredDocument> VisibleStoredDocuments()
        {
            if (_ambientTransaction.Value == null)
                return _entries.Values.ToArray();

            var visible = _entries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var pair in _ambientTransaction.Value.Mutations)
            {
                if (pair.Value == null)
                    visible.Remove(pair.Key);
                else
                    visible[pair.Key] = pair.Value;
            }
            return visible.Values.ToArray();
        }

        private IReadOnlyList<(CultStoredDocument Stored, object? Previous, bool Removed)> CommitTransaction(
            CultCacheTransaction transaction)
        {
            var mutations = transaction.SnapshotForCommit()
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray();
            if (mutations.Length == 0)
                return Array.Empty<(CultStoredDocument, object?, bool)>();

            if (_backingStores.Count > 1)
            {
                throw new InvalidOperationException(
                    "A CultCache transaction requires zero or one durable backing store; multiple stores cannot share one atomic commit boundary.");
            }

            CultStoredDocument[] previous;
            lock (_stateGate)
            {
                previous = mutations
                    .Select(pair => _entries.TryGetValue(pair.Key, out var stored) ? stored : null)
                    .Where(stored => stored != null)
                    .Cast<CultStoredDocument>()
                    .ToArray();
            }

            var upserts = mutations.Where(pair => pair.Value != null).Select(pair => pair.Value!).ToArray();
            var deleted = mutations
                .Where(pair => pair.Value == null)
                .Select(pair => previous.FirstOrDefault(stored => string.Equals(stored.Key.Value, pair.Key, StringComparison.Ordinal)))
                .Where(stored => stored != null)
                .Cast<CultStoredDocument>()
                .ToArray();

            foreach (var store in _backingStores)
                store.CommitBatch(upserts, deleted);

            var changes = new List<(CultStoredDocument Stored, object? Previous, bool Removed)>();
            lock (_stateGate)
            {
                foreach (var pair in mutations)
                {
                    _entries.TryGetValue(pair.Key, out var existing);
                    if (existing != null)
                    {
                        RemoveIndexes(existing);
                        _documentHandles.Remove(existing.Document);
                    }

                    if (pair.Value == null)
                    {
                        if (existing != null)
                        {
                            _entries.TryRemove(pair.Key, out _);
                            changes.Add((existing, existing.Document, true));
                        }
                        continue;
                    }

                    var stored = pair.Value;
                    _entries[pair.Key] = stored;
                    _documentHandles.Remove(stored.Document);
                    _documentHandles.Add(stored.Document, new DocumentHandleBox(stored.Key));
                    AddIndexes(stored);
                    changes.Add((stored, existing?.Document, false));
                }
                _hasUnflushedMutations = _backingStores.Any(store => store.IsDirty);
            }

            return changes;
        }

        private sealed class CultCacheTransaction
        {
            private readonly object _gate = new();
            private readonly Dictionary<string, CultStoredDocument?> _mutations = new(StringComparer.Ordinal);
            private bool _sealed;

            public IReadOnlyDictionary<string, CultStoredDocument?> Mutations
            {
                get
                {
                    lock (_gate)
                    {
                        ThrowIfSealedForAmbientAccess();
                        return new Dictionary<string, CultStoredDocument?>(_mutations, StringComparer.Ordinal);
                    }
                }
            }

            public void Stage(CultStoredDocument stored)
            {
                lock (_gate)
                {
                    ThrowIfSealedForAmbientAccess();
                    _mutations[stored.Key.Value] = stored;
                }
            }

            public void Delete(CultRecordKey key)
            {
                lock (_gate)
                {
                    ThrowIfSealedForAmbientAccess();
                    _mutations[key.Value] = null;
                }
            }

            public bool TryGet(CultRecordKey key, out CultStoredDocument? stored)
            {
                lock (_gate)
                {
                    ThrowIfSealedForAmbientAccess();
                    return _mutations.TryGetValue(key.Value, out stored);
                }
            }

            public void Seal()
            {
                lock (_gate)
                    _sealed = true;
            }

            public IReadOnlyDictionary<string, CultStoredDocument?> SnapshotForCommit()
            {
                lock (_gate)
                {
                    if (!_sealed)
                        throw new InvalidOperationException("A CultCache transaction must be sealed before commit.");
                    return new Dictionary<string, CultStoredDocument?>(_mutations, StringComparer.Ordinal);
                }
            }

            private void ThrowIfSealedForAmbientAccess()
            {
                if (_sealed)
                    throw new InvalidOperationException(
                        "This CultCache transaction has already completed; escaped async work cannot read or mutate it.");
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

        private void AddStoredDocumentInternal(
            CultStoredDocument stored,
            CacheBackingStore? source,
            bool raiseUpdate)
        {
            CultStoredDocument? existing = null;
            lock (_stateGate)
            {
                _entries.TryGetValue(stored.Key.Value, out existing);
                if (existing != null)
                    RemoveIndexes(existing);

                _entries[stored.Key.Value] = stored;
                _documentHandles.Remove(stored.Document);
                _documentHandles.Add(stored.Document, new DocumentHandleBox(stored.Key));
                AddIndexes(stored);

                foreach (var store in _backingStores)
                {
                    if (store != source)
                        store.Push(stored);
                }

                if (source == null)
                    _hasUnflushedMutations = true;
                else
                    RecomputeDirtyState();
            }

            PublishChange(stored, existing?.Document);

            if (raiseUpdate)
            {
                OnUpdate?.Invoke(existing?.Document, stored.Document);
            }
        }

        private void RemoveStoredDocumentInternal(
            CultStoredDocument stored,
            CacheBackingStore? source,
            bool raiseUpdate)
        {
            CultStoredDocument? existing;
            lock (_stateGate)
            {
                if (!_entries.TryRemove(stored.Key.Value, out existing))
                    return;

                RemoveIndexes(existing);
                _documentHandles.Remove(existing.Document);

                foreach (var store in _backingStores)
                {
                    if (store != source)
                        store.Delete(existing);
                }

                if (source == null)
                    _hasUnflushedMutations = true;
                else
                    RecomputeDirtyState();
            }

            PublishChange(existing, existing.Document, removed: true);

            if (raiseUpdate)
            {
                OnUpdate?.Invoke(existing.Document, null);
            }
        }

        private void InitializeGlobals()
        {
            foreach (var descriptor in _registry.AllDescriptors.Where(candidate => candidate.IsGlobal))
            {
                var key = new CultRecordKey($"global:{descriptor.SchemaId}");
                if (_entries.ContainsKey(key.Value) ||
                    _backingStores.Any(store => store.ContainsDurableRecord(key)))
                {
                    continue;
                }

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
                        key,
                        DateTimeOffset.UtcNow.ToString("O"),
                        descriptor,
                        instance),
                    source: null,
                    raiseUpdate: false);
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
            var typeMap = _typeMaps.GetOrAdd(
                stored.Descriptor.DocumentType,
                _ => new ConcurrentDictionary<string, CultStoredDocument>(StringComparer.Ordinal));
            typeMap[stored.Key.Value] = stored;

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
            if (_typeMaps.TryGetValue(stored.Descriptor.DocumentType, out var typeMap))
                typeMap.TryRemove(stored.Key.Value, out _);

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

        private void RecomputeDirtyState()
        {
            _hasUnflushedMutations = _backingStores.Count == 0
                ? _hasUnflushedMutations
                : _backingStores.Any(store => store.IsDirty);
        }

        private void PublishChange(CultStoredDocument stored, object? previousDocument, bool removed = false)
        {
            var changeType = typeof(CultCacheDocumentChange<>).MakeGenericType(stored.Descriptor.DocumentType);
            var change = Activator.CreateInstance(
                changeType,
                removed ? CultCacheDocumentChangeKind.Removed :
                previousDocument == null ? CultCacheDocumentChangeKind.Added :
                CultCacheDocumentChangeKind.Updated,
                stored.Key,
                removed ? null : stored.Document,
                previousDocument);
            if (change != null)
            {
                _changes.OnNext(change);
            }
        }
    }

    public abstract class CacheBackingStore : IDisposable
    {
        private CultDocumentRegistry? _registry;
        private bool _isDirty;
        private CultSchemaMigrationReport[] _lastSchemaMigrationReports = Array.Empty<CultSchemaMigrationReport>();

        protected CultDocumentRegistry Registry =>
            _registry ?? throw new InvalidOperationException("Backing store is not attached to a CultDocumentRegistry.");

        protected ConcurrentDictionary<string, CultStoredDocument> Entries { get; } =
            new(StringComparer.Ordinal);

        public bool IsDirty
        {
            get => _isDirty;
            protected set => _isDirty = value;
        }

        public bool FlushOnDispose { get; set; }

        public IReadOnlyList<CultSchemaMigrationReport> LastSchemaMigrationReports => _lastSchemaMigrationReports;

        // Set by the cache at attach; a store calls them for records it loads or drops.
        protected internal Action<CultStoredDocument>? Loaded;
        protected internal Action<CultStoredDocument>? Unloaded;

        internal void AttachRegistry(CultDocumentRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public abstract void PullAll();
        public virtual bool ContainsDurableRecord(CultRecordKey key)
        {
            return Entries.ContainsKey(key.Value);
        }
        public abstract void Push(CultStoredDocument entry);
        public abstract void Delete(CultStoredDocument entry);
        // Stages and durably commits a batch as one step. Implementations must restore their prior
        // staged view when finality fails.
        public virtual void CommitBatch(
            IReadOnlyCollection<CultStoredDocument> upserts,
            IReadOnlyCollection<CultStoredDocument> deletes)
        {
            var previousEntries = Entries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var wasDirty = IsDirty;
            try
            {
                foreach (var entry in deletes)
                    Delete(entry);
                foreach (var entry in upserts)
                    Push(entry);
                PushAll();
            }
            catch
            {
                Entries.Clear();
                foreach (var pair in previousEntries)
                    Entries[pair.Key] = pair.Value;
                IsDirty = wasDirty;
                throw;
            }
        }
        public abstract void PushAll();
        public virtual void Dispose()
        {
            if (FlushOnDispose && IsDirty)
            {
                PushAll();
            }
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
            var resolution = Registry.ResolvePersistedSchemaDetailed(record.SchemaId, catalog);
            var document = deserializePayload(resolution.Descriptor.DocumentType, record.Payload);
            return new CultStoredDocument(
                new CultRecordKey(record.Key),
                record.StoredAt,
                resolution.Descriptor,
                document);
        }

        protected void SetLastSchemaMigrationReports(IEnumerable<CultSchemaMigrationReport> reports)
        {
            _lastSchemaMigrationReports = reports?.ToArray() ?? Array.Empty<CultSchemaMigrationReport>();
        }

        protected void MarkFlushSucceeded()
        {
            IsDirty = false;
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
            FileInfo.Refresh();
            if (!FileInfo.Exists)
            {
                SetLastSchemaMigrationReports(Array.Empty<CultSchemaMigrationReport>());
                IsDirty = false;
                return;
            }

            // A single-file snapshot cannot distinguish local dirty keys from clean keys.
            // Pulling while local mutations are staged would let an older disk snapshot
            // erase those mutations before the next flush. Flush first; clean readers can
            // still poll external snapshots incrementally.
            if (IsDirty)
                return;

            CultPersistedStoreSnapshot snapshot;
            try
            {
                snapshot = DeserializeSnapshot(ReadAllBytesShared(FileInfo.FullName));
            }
            catch (FileNotFoundException)
            {
                FileInfo.Refresh();
                if (!FileInfo.Exists)
                    return;
                snapshot = DeserializeSnapshot(ReadAllBytesShared(FileInfo.FullName));
            }
            var reports = new List<CultSchemaMigrationReport>(snapshot.Records.Length);
            var persistedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in snapshot.Records)
            {
                persistedKeys.Add(record.Key);
                reports.Add(Registry.ResolvePersistedSchemaReport(record.SchemaId, snapshot.SchemaCatalog));
                var stored = ToStoredDocument(record, snapshot.SchemaCatalog, DeserializePayload);
                if (Entries.TryGetValue(stored.Key.Value, out var existing) &&
                    string.Equals(existing.StoredAt, stored.StoredAt, StringComparison.Ordinal) &&
                    string.Equals(existing.Descriptor.SchemaId, stored.Descriptor.SchemaId, StringComparison.Ordinal))
                {
                    continue;
                }
                Entries[stored.Key.Value] = stored;
                Loaded?.Invoke(stored);
            }

            foreach (var removedKey in Entries.Keys.Where(key => !persistedKeys.Contains(key)).ToArray())
            {
                if (Entries.TryRemove(removedKey, out var removed))
                    Unloaded?.Invoke(removed);
            }

            SetLastSchemaMigrationReports(reports);
            IsDirty = false;
        }

        public override void Push(CultStoredDocument entry)
        {
            Entries[entry.Key.Value] = entry;
            IsDirty = true;
        }

        public override void Delete(CultStoredDocument entry)
        {
            Entries.TryRemove(entry.Key.Value, out _);
            IsDirty = true;
        }

        public override void PushAll()
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
            WriteSnapshotAtomically(FileInfo.FullName, SerializeSnapshot(snapshot));
            MarkFlushSucceeded();
        }

        private static void WriteSnapshotAtomically(string path, byte[] payload)
        {
            var directory = Path.GetDirectoryName(path)!;
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var stream = new FileStream(
                           tempPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static byte[] ReadAllBytesShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
