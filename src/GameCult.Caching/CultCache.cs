using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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

    public enum CultCommitOutcome
    {
        Committed,
        Mismatch,
        Contended
    }

    public sealed class CultCommitRequest
    {
        internal CultCommitRequest(
            IReadOnlyList<CultStoredDocument> upserts,
            IReadOnlyList<CultStoredDocument> deletes,
            IReadOnlyList<(CultRecordKey Key, string? SchemaId, string? StoredAt)> expected,
            bool expectUnchanged)
        {
            Upserts = upserts;
            Deletes = deletes;
            Expected = expected;
            ExpectUnchanged = expectUnchanged;
        }

        public IReadOnlyList<CultStoredDocument> Upserts { get; }
        public IReadOnlyList<CultStoredDocument> Deletes { get; }
        public IReadOnlyList<(CultRecordKey Key, string? SchemaId, string? StoredAt)> Expected { get; }
        public bool ExpectUnchanged { get; }
        public bool HasConditions => Expected.Count > 0 || ExpectUnchanged;

        // durable is what the store holds on disk now; observed is what its cache last loaded or committed.
        // Identity is (schemaId, storedAt), sound because every write to a key mints a later storedAt.
        public bool ConditionsHold(IReadOnlyCollection<CultPersistedRecord> durable, IEnumerable<CultStoredDocument> observed)
        {
            var byKey = durable.ToDictionary(record => record.Key, StringComparer.Ordinal);
            foreach (var (key, schemaId, storedAt) in Expected)
            {
                byKey.TryGetValue(key.Value, out var record);
                var holds = schemaId == null
                    ? record == null
                    : record != null && record.SchemaId == schemaId && record.StoredAt == storedAt;
                if (!holds)
                    return false;
            }

            return !ExpectUnchanged ||
                   durable.Select(record => Identity(record.Key, record.SchemaId, record.StoredAt))
                       .OrderBy(identity => identity, StringComparer.Ordinal)
                       .SequenceEqual(observed
                           .Select(stored => Identity(stored.Key.Value, stored.Descriptor.SchemaId, stored.StoredAt))
                           .OrderBy(identity => identity, StringComparer.Ordinal));
        }

        private static string Identity(string key, string schemaId, string storedAt) => $"{key}\n{schemaId}\n{storedAt}";
    }

    // An explicit value: nothing staged here is visible to anyone until Commit returns.
    public sealed class CultCacheBatch
    {
        private readonly CultCache _cache;
        internal readonly Dictionary<string, (CultRecordKey Key, CultStoredDocument? Stored)> Operations = new(StringComparer.Ordinal);
        internal readonly List<(CultRecordKey Key, string? SchemaId, string? StoredAt)> Expected = new();
        internal bool ExpectsUnchanged;
        internal bool Sealed;

        internal CultCacheBatch(CultCache cache)
        {
            _cache = cache;
        }

        public CultRecordHandle<T> Upsert<T>(T document, CultRecordHandle<T>? handle = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            return new CultRecordHandle<T>(Stage(document, handle?.Key));
        }

        public CultRecordKey Upsert(Type type, object document, CultRecordKey? key = null)
        {
            CultCache.RequireInstanceOf(type, document);
            return Stage(document, key);
        }

        public void Remove(CultRecordKey key)
        {
            ThrowIfSealed();
            Operations[key.Value] = (key, null);
        }

        public void Expect(CultRecordKey key, object? current)
        {
            ThrowIfSealed();
            var observed = _cache.Observe(key, current);
            Expected.Add((key, observed?.Descriptor.SchemaId, observed?.StoredAt));
        }

        public void ExpectUnchanged()
        {
            ThrowIfSealed();
            ExpectsUnchanged = true;
        }

        private CultRecordKey Stage(object document, CultRecordKey? key)
        {
            ThrowIfSealed();
            var stored = _cache.CreateStoredDocument(document, key);
            Operations[stored.Key.Value] = (stored.Key, stored);
            return stored.Key;
        }

        private void ThrowIfSealed()
        {
            if (Sealed)
                throw new InvalidOperationException("This batch has already been committed or abandoned.");
        }
    }

    public sealed class CultCache : IDisposable
    {
        private readonly CultDocumentRegistry _registry;
        private readonly List<(CacheBackingStore Store, Type[] Homes)> _stores = new();
        private readonly Dictionary<string, CultStoredDocument> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<Type, Dictionary<string, string>> _names = new();
        private readonly Dictionary<(Type Type, string Alias), Dictionary<string, string>> _indexes = new();
        private readonly Dictionary<Type, string> _globals = new();
        private readonly ConditionalWeakTable<object, KeyBox> _handles = new();
        private readonly Subject<Change> _changes = new();
        private readonly object _gate = new();
        private long _sequence;
        // The changes admitted by this thread's outermost hold, published by that hold when it exits.
        [ThreadStatic] private static List<(Change Change, bool Loaded)>? _held;
        private bool _dirtyInMemory;

        public CultCache(CultDocumentRegistry? registry = null)
        {
            _registry = registry ?? CultDocumentRegistry.Shared;
        }

        public CultDocumentRegistry Registry => _registry;

        public bool IsDirty
        {
            get
            {
                lock (_gate)
                    return _stores.Count == 0 ? _dirtyInMemory : _stores.Any(entry => entry.Store.IsDirty);
            }
        }

        public bool FlushAttachedStoresOnDispose { get; set; }

        public event Action<object?, object?>? OnUpdate;

        public IReadOnlyList<CacheBackingStore> BackingStores
        {
            get
            {
                lock (_gate)
                    return _stores.Select(entry => entry.Store).ToArray();
            }
        }

        public IEnumerable<object> AllEntries
        {
            get
            {
                lock (_gate)
                    return _entries.Values.Select(entry => entry.Document).ToArray();
            }
        }

        public IEnumerable<CultStoredDocument> AllStoredDocuments
        {
            get
            {
                lock (_gate)
                    return _entries.Values
                        .OrderBy(entry => entry.Descriptor.SchemaName, StringComparer.Ordinal)
                        .ThenBy(entry => entry.Key.Value, StringComparer.Ordinal)
                        .ToArray();
            }
        }

        public Observable<CultCacheDocumentChange<T>> Watch<T>() where T : class
        {
            return _changes
                .Where(change => typeof(T).IsAssignableFrom(change.Stored.Descriptor.DocumentType))
                .Select(change => new CultCacheDocumentChange<T>(
                    change.Kind,
                    change.Stored.Key,
                    change.Document as T,
                    change.Previous as T,
                    change.Sequence));
        }

        public Observable<CultCacheDocumentChange<T>> WatchRecord<T>(CultRecordKey key) where T : class
        {
            return Watch<T>().Where(change => change.Key.Equals(key));
        }

        // Attaching reads the store; there is no interval in which it is attached but unread.
        public void AddBackingStore(CacheBackingStore store, params Type[] homes)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            homes ??= Array.Empty<Type>();
            Held(() =>
            {
                if (store.Loaded != null)
                    throw new InvalidOperationException($"Backing store {store} is already attached to a cache.");
                if (store.IsDirty)
                    throw new InvalidOperationException($"Backing store {store} has staged writes; attach it clean.");
                if (homes.Length == 0 && _stores.Any(entry => entry.Homes.Length == 0))
                    throw new InvalidOperationException(
                        $"Backing store {store} would be a second untyped store; name the types it is home to.");
                foreach (var home in homes)
                {
                    var owner = _stores.FirstOrDefault(entry => entry.Homes.Contains(home)).Store;
                    if (owner != null)
                        throw new InvalidOperationException($"{home.FullName} is already routed to {owner}; it cannot also route to {store}.");
                }

                var candidate = (store, homes);
                foreach (var type in _entries.Values.Select(entry => entry.Descriptor).Distinct())
                {
                    var before = Home(type.DocumentType);
                    _stores.Add(candidate);
                    var after = Home(type.DocumentType);
                    _stores.RemoveAt(_stores.Count - 1);
                    if (before != after)
                        throw new InvalidOperationException(
                            $"Attaching {store} would move {type.SchemaName} from {before?.ToString() ?? "memory"} to {after}; " +
                            "attach routed stores before the untyped store.");
                }

                store.AttachRegistry(_registry);
                store.Cache = this;
                store.Loaded = (loaded, dropped) => Admit(loaded, dropped, store, _ => CultCommitOutcome.Committed);
                _stores.Add(candidate);
                try
                {
                    store.PullAll();
                }
                catch
                {
                    _stores.Remove(candidate);
                    store.Loaded = null;
                    store.Cache = null;
                    throw;
                }
                return true;
            });
        }

        public Task PullAllBackingStoresAsync()
        {
            // Our stores take the gate themselves; a third-party store might not, so the cache takes it here.
            // One store's throwing OnUpdate handler does not stop the others from loading.
            var failures = new List<Exception>();
            foreach (var store in BackingStores)
            {
                try
                {
                    Held(() => { store.PullAll(); return true; });
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (failures.Count == 1)
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1)
                throw new AggregateException(failures);
            return Task.CompletedTask;
        }

        public void FlushAllBackingStores() => Held(() =>
        {
            foreach (var (store, _) in _stores)
            {
                if (!store.IsReadOnly && store.IsDirty)
                    store.PushAll();
            }
            return true;
        });

        // soft is dead. CultCacheStudioWindow reflects on FlushAsync(bool) until Cut 6 rewrites it.
        public Task FlushAsync(bool soft = false)
        {
            FlushAllBackingStores();
            return Task.CompletedTask;
        }

        public Task<CultRecordHandle<T>> AddAsync<T>(T document, CultRecordHandle<T>? handle = null)
        {
            return UpsertAsync(document, handle);
        }

        public Task<CultRecordHandle<T>> UpsertAsync<T>(T document, CultRecordHandle<T>? handle = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            return Task.FromResult(new CultRecordHandle<T>(Write(document, handle?.Key)));
        }

        public Task<CultRecordKey> UpsertAsync(Type documentType, object document, CultRecordKey? key = null)
        {
            RequireInstanceOf(documentType, document);
            return Task.FromResult(Write(document, key));
        }

        public bool Remove(CultRecordKey key) => Held(() =>
        {
            if (!_entries.TryGetValue(key.Value, out var existing))
                return false;
            Admit(Array.Empty<CultStoredDocument>(), new[] { existing }, null, home =>
            {
                home?.Delete(existing);
                return CultCommitOutcome.Committed;
            });
            return true;
        });

        public void Remove<T>(CultRecordHandle<T> handle)
        {
            Remove(handle.Key);
        }

        // false: a condition failed; nothing was written, changed in memory, or published.
        public bool Commit(Action<CultCacheBatch> stage)
        {
            return Land(stage, wait: true) == CultCommitOutcome.Committed;
        }

        public CultCommitOutcome TryCommit(Action<CultCacheBatch> stage)
        {
            return Land(stage, wait: false);
        }

        public CultRecordHandle<T>? TryGetHandle<T>(T document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            lock (_gate)
                return _handles.TryGetValue(document, out var box) ? new CultRecordHandle<T>(box.Key) : null;
        }

        public object? Get(CultRecordKey key)
        {
            lock (_gate)
                return _entries.TryGetValue(key.Value, out var stored) ? stored.Document : null;
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
            lock (_gate)
                return _entries.Values.Select(entry => entry.Document).OfType<T>().ToArray();
        }

        public T? GetGlobal<T>() where T : class
        {
            return Single<T>("global", () => _globals
                .Where(pair => typeof(T).IsAssignableFrom(pair.Key))
                .Select(pair => pair.Value));
        }

        public T? GetByName<T>(string name) where T : class
        {
            return Single<T>($"name '{name}'", () => _names
                .Where(pair => typeof(T).IsAssignableFrom(pair.Key))
                .SelectMany(pair => pair.Value.TryGetValue(name, out var key) ? new[] { key } : Array.Empty<string>()));
        }

        public T? GetByIndex<T>(string alias, string value) where T : class
        {
            return Single<T>($"index {alias}='{value}'", () => _indexes
                .Where(pair => pair.Key.Alias == alias && typeof(T).IsAssignableFrom(pair.Key.Type))
                .SelectMany(pair => pair.Value.TryGetValue(value, out var key) ? new[] { key } : Array.Empty<string>()));
        }

        public void Dispose()
        {
            Held(() =>
            {
                if (FlushAttachedStoresOnDispose && IsDirty)
                    FlushAllBackingStores();
                return true;
            });

            foreach (var store in BackingStores)
                store.Dispose();

            _changes.Dispose();
        }

        internal static void RequireInstanceOf(Type documentType, object document)
        {
            if (documentType == null) throw new ArgumentNullException(nameof(documentType));
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (!documentType.IsInstanceOfType(document))
                throw new ArgumentException($"Document instance must be assignable to {documentType.FullName}.", nameof(document));
        }

        // The runtime type decides the schema; a minted storedAt is always later than the record it replaces.
        internal CultStoredDocument CreateStoredDocument(object document, CultRecordKey? key)
        {
            var descriptor = _registry.GetRequired(document.GetType());
            lock (_gate)
                return Stamp(
                    key ?? (_handles.TryGetValue(document, out var box) ? box.Key
                        : descriptor.IsGlobal ? new CultRecordKey($"global:{descriptor.SchemaId}")
                        : new CultRecordKey(Guid.NewGuid().ToString("N"))),
                    descriptor,
                    document);
        }

        // Call under the gate that admits the result, so no load can land a later storedAt in between.
        private CultStoredDocument Stamp(CultRecordKey key, CultDocumentDescriptor descriptor, object document) =>
            new(key, MintStoredAt(_entries.TryGetValue(key.Value, out var previous) ? previous.StoredAt : null), descriptor, document);

        internal CultStoredDocument? Observe(CultRecordKey key, object? current)
        {
            if (current == null)
                return null;
            lock (_gate)
            {
                if (_handles.TryGetValue(current, out var box) && box.Key.Equals(key) &&
                    _entries.TryGetValue(key.Value, out var stored) && ReferenceEquals(stored.Document, current))
                    return stored;
            }

            throw new ArgumentException($"Expect({key.Value}) was given an instance this cache does not hold at that key.", nameof(current));
        }

        private static string MintStoredAt(string? previous)
        {
            var now = DateTimeOffset.UtcNow;
            if (previous != null &&
                DateTimeOffset.TryParse(previous, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last) &&
                now <= last)
                now = last.ToUniversalTime().AddTicks(1);
            return now.ToString("O", CultureInfo.InvariantCulture);
        }

        private CultRecordKey Write(object document, CultRecordKey? key) => Held(() =>
        {
            var stored = CreateStoredDocument(document, key);
            Admit(new[] { stored }, Array.Empty<CultStoredDocument>(), null, home =>
            {
                home?.Push(stored);
                return CultCommitOutcome.Committed;
            });
            return stored.Key;
        });

        private CultCommitOutcome Land(Action<CultCacheBatch> stage, bool wait)
        {
            if (stage == null) throw new ArgumentNullException(nameof(stage));
            var batch = new CultCacheBatch(this);
            try
            {
                stage(batch);
            }
            finally
            {
                batch.Sealed = true;
            }

            // The in-process gate is always entered; only the store lock attempt honors wait, so Contended means another writer holds the store.
            return Held(() =>
            {
                // Staging stamped outside this gate; a pull since then may hold a later storedAt, so stamp again.
                var upserts = batch.Operations.Values
                    .Where(op => op.Stored != null)
                    .Select(op => Stamp(op.Key, op.Stored!.Descriptor, op.Stored.Document))
                    .ToArray();
                var deletes = batch.Operations.Values
                    .Where(op => op.Stored == null && _entries.ContainsKey(op.Key.Value))
                    .Select(op => _entries[op.Key.Value])
                    .ToArray();
                var request = new CultCommitRequest(upserts, deletes, batch.Expected.ToArray(), batch.ExpectsUnchanged);
                return Admit(upserts, deletes, null, home =>
                {
                    if (home != null)
                    {
                        if (request.HasConditions && home.IsDirty)
                            throw new InvalidOperationException($"Backing store {home} has staged writes; flush before a conditional commit.");
                        return home.CommitBatch(request, wait);
                    }

                    if (request.HasConditions && _stores.Count > 0)
                        throw new InvalidOperationException("A conditional batch names its home store through a record it upserts or removes.");
                    var inMemory = _entries.Values
                        .Select(entry => new CultPersistedRecord { Key = entry.Key.Value, SchemaId = entry.Descriptor.SchemaId, StoredAt = entry.StoredAt })
                        .ToArray();
                    return request.ConditionsHold(inMemory, _entries.Values) ? CultCommitOutcome.Committed : CultCommitOutcome.Mismatch;
                });
            });
        }

        // Every add and remove passes here: single writes, committed batches, and loads (source set).
        // Nothing lands unless all of it is admissible; land is the durable step between validation and memory.
        private CultCommitOutcome Admit(
            IReadOnlyList<CultStoredDocument> admitted,
            IReadOnlyList<CultStoredDocument> evicted,
            CacheBackingStore? source,
            Func<CacheBackingStore?, CultCommitOutcome> land) => Held(() =>
        {
            if (_held == null)
                throw new InvalidOperationException("An admission reached the cache under a plain lock on its gate; every admission runs in a hold.");
            var outcome = land(Validate(admitted, evicted, source));
            if (outcome != CultCommitOutcome.Committed)
                return outcome;
            foreach (var change in Apply(admitted, evicted, source))
                _held.Add((change, source != null));
            return CultCommitOutcome.Committed;
        });

        // Every admission runs in a hold. A nested hold on the same cache adds to the outermost one, which publishes
        // exactly its own changes after it leaves the gate, before it returns, on its caller's thread: observers never
        // run under the gate, and a write an observer makes is a new outermost hold. Cross-thread delivery order is not
        // guaranteed; each change carries the Sequence it was admitted with. Every change is delivered even if an
        // OnUpdate handler throws; then the first handler exception (an AggregateException for several) is rethrown.
        // If the body threw, its exception wins.
        internal T Held<T>(Func<T> body)
        {
            if (Monitor.IsEntered(_gate))
                return body();
            if (_held != null)
                throw new InvalidOperationException(
                    "A cache hold was entered inside another cache's hold; a thread holds one cache's gate at a time.");
            var mine = _held = new List<(Change Change, bool Loaded)>();
            T result;
            try
            {
                lock (_gate)
                    result = body();
            }
            catch
            {
                _held = null;
                Publish(mine);
                throw;
            }

            _held = null;
            var failures = Publish(mine);
            if (failures.Count == 1)
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1)
                throw new AggregateException(failures);
            return result;
        }

        private List<Exception> Publish(List<(Change Change, bool Loaded)> changes)
        {
            var failures = new List<Exception>();
            foreach (var (change, loaded) in changes)
            {
                // R3 routes a throwing subscriber to its unhandled-exception handler; OnNext does not throw.
                _changes.OnNext(change);
                if (!loaded)
                    continue;
                try
                {
                    OnUpdate?.Invoke(change.Previous, change.Document);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            return failures;
        }

        private CacheBackingStore? Validate(
            IReadOnlyList<CultStoredDocument> admitted,
            IReadOnlyList<CultStoredDocument> evicted,
            CacheBackingStore? source)
        {
            var homes = new List<CacheBackingStore>();
            var globals = new Dictionary<Type, string>(_globals);
            foreach (var stored in evicted)
            {
                var type = stored.Descriptor.DocumentType;
                if (globals.TryGetValue(type, out var key) && key == stored.Key.Value)
                    globals.Remove(type);
                if (source == null)
                    Claim(stored);
            }

            foreach (var stored in admitted)
            {
                var descriptor = stored.Descriptor;
                if (source == null)
                    Claim(stored);
                else if (Home(descriptor.DocumentType) is var home && home != source)
                    throw new InvalidOperationException(
                        $"{descriptor.SchemaName} record {stored.Key.Value} was loaded from {source} but its home is {home?.ToString() ?? "no store"}.");

                // One key, one store: a key never moves between homes, and two stores never both deliver it.
                if (_entries.TryGetValue(stored.Key.Value, out var present) &&
                    Home(present.Descriptor.DocumentType) is var held &&
                    Home(descriptor.DocumentType) is var incoming &&
                    held != incoming)
                    throw new InvalidOperationException(
                        $"Record {stored.Key.Value} is held by {held?.ToString() ?? "memory"}; " +
                        $"{(source == null ? "writing" : "loading")} it as {descriptor.SchemaName} from {incoming?.ToString() ?? "memory"} would give the key a second store.");

                if (!descriptor.IsGlobal)
                    continue;
                if (globals.TryGetValue(descriptor.DocumentType, out var existing) && existing != stored.Key.Value)
                    throw new InvalidOperationException(
                        $"{descriptor.SchemaName} is a global and already has record {existing}; {stored.Key.Value} would be a second.");
                globals[descriptor.DocumentType] = stored.Key.Value;
            }

            var distinct = homes.Distinct().ToArray();
            if (distinct.Length > 1)
                throw new InvalidOperationException(
                    $"The batch spans {distinct[0]} and {distinct[1]}; a commit lands in one home store.");
            return distinct.FirstOrDefault();

            void Claim(CultStoredDocument stored)
            {
                var home = Home(stored.Descriptor.DocumentType);
                if (home == null)
                {
                    if (_stores.Count > 0)
                        throw new InvalidOperationException($"No backing store is home to {stored.Descriptor.SchemaName}.");
                    return;
                }

                if (home.IsReadOnly)
                    throw new InvalidOperationException(
                        $"{stored.Descriptor.SchemaName} record {stored.Key.Value} routes to read-only backing store {home}.");
                homes.Add(home);
            }
        }

        private List<Change> Apply(
            IReadOnlyList<CultStoredDocument> admitted,
            IReadOnlyList<CultStoredDocument> evicted,
            CacheBackingStore? source)
        {
            var changes = new List<Change>(admitted.Count + evicted.Count);
            foreach (var stored in evicted)
            {
                if (!_entries.TryGetValue(stored.Key.Value, out var existing))
                    continue;
                Unindex(existing);
                _entries.Remove(stored.Key.Value);
                _handles.Remove(existing.Document);
                changes.Add(new Change(CultCacheDocumentChangeKind.Removed, existing, null, existing.Document, ++_sequence));
            }

            foreach (var stored in admitted)
            {
                _entries.TryGetValue(stored.Key.Value, out var previous);
                if (previous != null)
                {
                    Unindex(previous);
                    _handles.Remove(previous.Document);
                }

                _entries[stored.Key.Value] = stored;
                _handles.AddOrUpdate(stored.Document, new KeyBox(stored.Key));
                Index(stored);
                changes.Add(new Change(
                    previous == null ? CultCacheDocumentChangeKind.Added : CultCacheDocumentChangeKind.Updated,
                    stored,
                    stored.Document,
                    previous?.Document,
                    ++_sequence));
            }

            if (source == null && _stores.Count == 0)
                _dirtyInMemory = true;
            return changes;
        }

        // The most derived routed home type assignable from the document type, else the untyped store.
        private CacheBackingStore? Home(Type documentType)
        {
            CacheBackingStore? untyped = null;
            CacheBackingStore? routed = null;
            Type? best = null;
            foreach (var (store, homes) in _stores)
            {
                if (homes.Length == 0)
                {
                    untyped = store;
                    continue;
                }

                foreach (var home in homes)
                {
                    if (home.IsAssignableFrom(documentType) && (best == null || best.IsAssignableFrom(home)))
                    {
                        best = home;
                        routed = store;
                    }
                }
            }

            return routed ?? untyped;
        }

        private T? Single<T>(string lookup, Func<IEnumerable<string>> keys) where T : class
        {
            lock (_gate)
            {
                var found = keys().Distinct().ToArray();
                if (found.Length > 1)
                    throw new InvalidOperationException(
                        $"{typeof(T).Name} {lookup} matches records {string.Join(", ", found)}; look it up by its concrete type.");
                return found.Length == 1 && _entries.TryGetValue(found[0], out var stored) ? stored.Document as T : null;
            }
        }

        private void Index(CultStoredDocument stored)
        {
            var type = stored.Descriptor.DocumentType;
            if (stored.Descriptor.IsGlobal)
                _globals[type] = stored.Key.Value;
            if (stored.Descriptor.NameAccessor?.Invoke(stored.Document) is { Length: > 0 } name)
                MapOf(_names, type)[name] = stored.Key.Value;
            foreach (var pair in stored.Descriptor.IndexAccessors)
            {
                var value = pair.Value(stored.Document);
                if (!string.IsNullOrWhiteSpace(value))
                    MapOf(_indexes, (type, pair.Key))[value] = stored.Key.Value;
            }
        }

        // Documents are mutable, so a name or index value may have changed since it was indexed: drop by key.
        private void Unindex(CultStoredDocument stored)
        {
            var type = stored.Descriptor.DocumentType;
            if (_globals.TryGetValue(type, out var globalKey) && globalKey == stored.Key.Value)
                _globals.Remove(type);
            if (_names.TryGetValue(type, out var names))
                RemoveKey(names, stored.Key.Value);
            foreach (var pair in _indexes.Where(pair => pair.Key.Type == type))
                RemoveKey(pair.Value, stored.Key.Value);
        }

        private static Dictionary<string, string> MapOf<TKey>(Dictionary<TKey, Dictionary<string, string>> maps, TKey key)
            where TKey : notnull
        {
            if (!maps.TryGetValue(key, out var map))
                maps[key] = map = new Dictionary<string, string>(StringComparer.Ordinal);
            return map;
        }

        private static void RemoveKey(Dictionary<string, string> map, string key)
        {
            foreach (var stale in map.Where(pair => pair.Value == key).Select(pair => pair.Key).ToArray())
                map.Remove(stale);
        }

        private sealed class KeyBox
        {
            public KeyBox(CultRecordKey key)
            {
                Key = key;
            }

            public CultRecordKey Key { get; }
        }

        private sealed class Change
        {
            public Change(CultCacheDocumentChangeKind kind, CultStoredDocument stored, object? document, object? previous, long sequence)
            {
                Kind = kind;
                Stored = stored;
                Document = document;
                Previous = previous;
                Sequence = sequence;
            }

            public CultCacheDocumentChangeKind Kind { get; }
            public CultStoredDocument Stored { get; }
            public object? Document { get; }
            public object? Previous { get; }
            public long Sequence { get; }
        }
    }

    public abstract class CacheBackingStore : IDisposable
    {
        private CultDocumentRegistry? _registry;
        private CultSchemaMigrationReport[] _lastSchemaMigrationReports = Array.Empty<CultSchemaMigrationReport>();

        protected CacheBackingStore(bool readOnly = false)
        {
            IsReadOnly = readOnly;
        }

        protected CultDocumentRegistry Registry =>
            _registry ?? throw new InvalidOperationException("Backing store is not attached to a CultDocumentRegistry.");

        protected ConcurrentDictionary<string, CultStoredDocument> Entries { get; } =
            new(StringComparer.Ordinal);

        public bool IsReadOnly { get; }

        public bool IsDirty { get; protected set; }

        public bool FlushOnDispose { get; set; }

        public IReadOnlyList<CultSchemaMigrationReport> LastSchemaMigrationReports => _lastSchemaMigrationReports;

        // Set by the cache at attach. A pull hands over everything it loaded and dropped in one call before it adopts
        // any of it; if the cache refuses a record the call throws and the store keeps its previous view.
        protected internal Action<IReadOnlyList<CultStoredDocument>, IReadOnlyList<CultStoredDocument>>? Loaded;

        private readonly object _detachedGate = new();
        internal CultCache? Cache;

        // Once attached, a hold on the cache's gate: the store and its cache share one lock, so a load calling back into
        // the cache can never take the gate after the store lock, and a direct call publishes what it loaded when it
        // returns. File locks are always taken inside it.
        protected T Held<T>(Func<T> body)
        {
            if (Cache is { } cache)
                return cache.Held(body);
            lock (_detachedGate)
                return body();
        }

        protected void Held(Action body) => Held(() => { body(); return true; });

        internal void AttachRegistry(CultDocumentRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public abstract void PullAll();
        public abstract void Push(CultStoredDocument entry);
        public abstract void Delete(CultStoredDocument entry);
        public abstract void PushAll();

        // One durable step under the store's exclusive lock; conditions are evaluated against what is on disk then.
        // A failure restores the store's staged view.
        public abstract CultCommitOutcome CommitBatch(CultCommitRequest request, bool wait);

        public virtual void Dispose()
        {
            if (FlushOnDispose && IsDirty && !IsReadOnly)
            {
                PushAll();
            }
        }

        protected void ThrowIfReadOnly()
        {
            if (IsReadOnly)
                throw new InvalidOperationException($"Backing store {this} is read-only.");
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
        protected SingleFileBackingStore(string filePath, bool readOnly = false)
            : base(readOnly)
        {
            FileInfo = new FileInfo(filePath);
        }

        protected FileInfo FileInfo { get; }
        protected abstract byte[] SerializeSnapshot(CultPersistedStoreSnapshot snapshot);
        protected abstract CultPersistedStoreSnapshot DeserializeSnapshot(byte[] data);
        protected abstract byte[] SerializePayload(object document);
        protected abstract object DeserializePayload(Type documentType, byte[] payload);

        public override string ToString() => FileInfo.FullName;

        public override void PullAll() => Held(PullAllCore);

        private void PullAllCore()
        {
            // A single-file snapshot cannot distinguish local dirty keys from clean keys. Pulling while local mutations
            // are staged would let an older disk snapshot erase them before the next flush. Flush first.
            if (IsDirty)
                return;

            var snapshot = ReadSnapshot();
            if (snapshot == null)
            {
                SetLastSchemaMigrationReports(Array.Empty<CultSchemaMigrationReport>());
                return;
            }

            var reports = new List<CultSchemaMigrationReport>(snapshot.Records.Length);
            var persisted = new Dictionary<string, CultStoredDocument>(StringComparer.Ordinal);
            foreach (var record in snapshot.Records)
            {
                reports.Add(Registry.ResolvePersistedSchemaReport(record.SchemaId, snapshot.SchemaCatalog));
                var stored = ToStoredDocument(record, snapshot.SchemaCatalog, DeserializePayload);
                persisted[stored.Key.Value] = stored;
            }

            var loaded = persisted.Values
                .Where(stored => !Entries.TryGetValue(stored.Key.Value, out var existing) ||
                                 existing.StoredAt != stored.StoredAt ||
                                 existing.Descriptor.SchemaId != stored.Descriptor.SchemaId)
                .ToArray();
            var dropped = Entries.Values.Where(existing => !persisted.ContainsKey(existing.Key.Value)).ToArray();
            if (loaded.Length > 0 || dropped.Length > 0)
                Loaded?.Invoke(loaded, dropped);

            foreach (var stored in dropped)
                Entries.TryRemove(stored.Key.Value, out _);
            foreach (var stored in loaded)
                Entries[stored.Key.Value] = stored;
            SetLastSchemaMigrationReports(reports);
        }

        public override void Push(CultStoredDocument entry)
        {
            ThrowIfReadOnly();
            Held(() =>
            {
                Entries[entry.Key.Value] = entry;
                IsDirty = true;
            });
        }

        public override void Delete(CultStoredDocument entry)
        {
            ThrowIfReadOnly();
            Held(() =>
            {
                Entries.TryRemove(entry.Key.Value, out _);
                IsDirty = true;
            });
        }

        // A plain flush writes this store's whole view under the lock: two writers never interleave bytes, but the last
        // one wins. Processes sharing a store must use conditional commit.
        public override void PushAll()
        {
            ThrowIfReadOnly();
            Held(() =>
            {
                using (AcquireLock(wait: true))
                {
                    WriteSnapshot(
                        Entries.Values.Select(entry => ToPersistedRecord(entry, SerializePayload)),
                        Entries.Values.Select(entry => entry.Descriptor.ToCatalogEntry()));
                }

                MarkFlushSucceeded();
            });
        }

        public override CultCommitOutcome CommitBatch(CultCommitRequest request, bool wait)
        {
            ThrowIfReadOnly();
            return Held(() => CommitBatchCore(request, wait));
        }

        private CultCommitOutcome CommitBatchCore(CultCommitRequest request, bool wait)
        {
            using var fileLock = AcquireLock(wait);
            if (fileLock == null)
                return CultCommitOutcome.Contended;

            var disk = ReadSnapshot() ?? new CultPersistedStoreSnapshot();
            if (!request.ConditionsHold(disk.Records, Entries.Values))
                return CultCommitOutcome.Mismatch;

            // An unconditional commit writes what a flush would, this store's whole view plus the batch: last-writer-wins,
            // and staged single writes land with it. A conditional commit (store clean) lands the batch onto the file as it is.
            var ontoDisk = request.HasConditions && !IsDirty;
            var records = ontoDisk
                ? disk.Records.ToDictionary(record => record.Key, StringComparer.Ordinal)
                : Entries.Values.Select(entry => ToPersistedRecord(entry, SerializePayload)).ToDictionary(record => record.Key, StringComparer.Ordinal);
            var catalog = ontoDisk
                ? disk.SchemaCatalog
                : Entries.Values.Select(entry => entry.Descriptor.ToCatalogEntry());
            foreach (var entry in request.Deletes)
                records.Remove(entry.Key.Value);
            foreach (var entry in request.Upserts)
                records[entry.Key.Value] = ToPersistedRecord(entry, SerializePayload);

            WriteSnapshot(records.Values, catalog.Concat(request.Upserts.Select(entry => entry.Descriptor.ToCatalogEntry())));
            foreach (var entry in request.Deletes)
                Entries.TryRemove(entry.Key.Value, out _);
            foreach (var entry in request.Upserts)
                Entries[entry.Key.Value] = entry;
            MarkFlushSucceeded();
            return CultCommitOutcome.Committed;
        }

        private CultPersistedStoreSnapshot? ReadSnapshot()
        {
            FileInfo.Refresh();
            if (!FileInfo.Exists)
                return null;
            try
            {
                return DeserializeSnapshot(ReadAllBytesShared(FileInfo.FullName));
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        private void WriteSnapshot(IEnumerable<CultPersistedRecord> records, IEnumerable<CultSchemaCatalogEntry> catalog)
        {
            var ordered = records.OrderBy(record => record.Key, StringComparer.Ordinal).ToArray();
            var used = new HashSet<string>(ordered.Select(record => record.SchemaId), StringComparer.Ordinal);
            var snapshot = new CultPersistedStoreSnapshot
            {
                SchemaCatalog = catalog
                    .GroupBy(entry => entry.SchemaId, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .Where(entry => used.Contains(entry.SchemaId))
                    .OrderBy(entry => entry.SchemaName, StringComparer.Ordinal)
                    .ToArray(),
                Records = ordered
            };

            Directory.CreateDirectory(FileInfo.DirectoryName!);
            WriteSnapshotAtomically(FileInfo.FullName, SerializeSnapshot(snapshot));
        }

        // The lock is a sidecar opened exclusively, which excludes other handles in this process and in others alike.
        private FileStream? AcquireLock(bool wait)
        {
            Directory.CreateDirectory(FileInfo.DirectoryName!);
            var lockPath = FileInfo.FullName + ".lock";
            var started = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
                }
                catch (IOException) when (!wait)
                {
                    return null;
                }
                catch (IOException) when (started.Elapsed < TimeSpan.FromSeconds(30))
                {
                    Thread.Sleep(10);
                }
            }
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
