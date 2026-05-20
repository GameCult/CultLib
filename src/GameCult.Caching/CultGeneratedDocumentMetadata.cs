using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace GameCult.Caching
{
    /// <summary>
    /// Registers a generated CultCache document metadata provider with an assembly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class CultGeneratedDocumentMetadataProviderAttribute : Attribute
    {
        /// <summary>
        /// Creates an assembly metadata provider registration.
        /// </summary>
        public CultGeneratedDocumentMetadataProviderAttribute(Type providerType)
        {
            ProviderType = providerType ?? throw new ArgumentNullException(nameof(providerType));
        }

        /// <summary>
        /// Gets the provider type that can create generated document definitions.
        /// </summary>
        public Type ProviderType { get; }
    }

    /// <summary>
    /// Supplies generated CultCache document metadata for an assembly.
    /// </summary>
    public interface ICultGeneratedDocumentMetadataProvider
    {
        /// <summary>
        /// Gets the generated document definitions exposed by the provider.
        /// </summary>
        IEnumerable<CultGeneratedDocumentDefinition> GetDocumentDefinitions();
    }

    /// <summary>
    /// Describes one generated CultCache document contract.
    /// </summary>
    public sealed class CultGeneratedDocumentDefinition
    {
        /// <summary>
        /// Creates a generated document contract definition.
        /// </summary>
        public CultGeneratedDocumentDefinition(
            Type documentType,
            string schemaName,
            string schemaVersion,
            bool isGlobal,
            string? nameMember,
            Func<object, string?>? nameAccessor,
            Func<object, byte[]>? serializePayload,
            Func<byte[], object>? deserializePayload,
            IReadOnlyList<CultGeneratedDocumentIndexAccessor> indexAccessors,
            IReadOnlyList<CultGeneratedDocumentMemberDefinition> members)
        {
            DocumentType = documentType ?? throw new ArgumentNullException(nameof(documentType));
            SchemaName = string.IsNullOrWhiteSpace(schemaName)
                ? throw new ArgumentException("Schema name must be non-empty.", nameof(schemaName))
                : schemaName;
            SchemaVersion = string.IsNullOrWhiteSpace(schemaVersion)
                ? throw new ArgumentException("Schema version must be non-empty.", nameof(schemaVersion))
                : schemaVersion;
            IsGlobal = isGlobal;
            NameMember = nameMember;
            NameAccessor = nameAccessor;
            SerializePayload = serializePayload;
            DeserializePayload = deserializePayload;
            IndexAccessors = indexAccessors ?? Array.Empty<CultGeneratedDocumentIndexAccessor>();
            Members = members ?? Array.Empty<CultGeneratedDocumentMemberDefinition>();
        }

        /// <summary>
        /// Gets the CLR document type.
        /// </summary>
        public Type DocumentType { get; }

        /// <summary>
        /// Gets the stable CultCache schema name.
        /// </summary>
        public string SchemaName { get; }

        /// <summary>
        /// Gets the schema version string.
        /// </summary>
        public string SchemaVersion { get; }

        /// <summary>
        /// Gets whether this document type stores one global record.
        /// </summary>
        public bool IsGlobal { get; }

        /// <summary>
        /// Gets the member used as the document name, if any.
        /// </summary>
        public string? NameMember { get; }

        /// <summary>
        /// Gets the generated name accessor, if any.
        /// </summary>
        public Func<object, string?>? NameAccessor { get; }

        /// <summary>
        /// Gets the generated payload serializer, if one was emitted.
        /// </summary>
        public Func<object, byte[]>? SerializePayload { get; }

        /// <summary>
        /// Gets the generated payload deserializer, if one was emitted.
        /// </summary>
        public Func<byte[], object>? DeserializePayload { get; }

        /// <summary>
        /// Gets the generated index accessors.
        /// </summary>
        public IReadOnlyList<CultGeneratedDocumentIndexAccessor> IndexAccessors { get; }

        /// <summary>
        /// Gets the generated persisted member definitions.
        /// </summary>
        public IReadOnlyList<CultGeneratedDocumentMemberDefinition> Members { get; }
    }

    /// <summary>
    /// Describes a generated accessor for one CultCache index.
    /// </summary>
    public sealed class CultGeneratedDocumentIndexAccessor
    {
        /// <summary>
        /// Creates a generated index accessor.
        /// </summary>
        public CultGeneratedDocumentIndexAccessor(string alias, Func<object, string> accessor)
        {
            Alias = string.IsNullOrWhiteSpace(alias)
                ? throw new ArgumentException("Alias must be non-empty.", nameof(alias))
                : alias;
            Accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        }

        /// <summary>
        /// Gets the index alias.
        /// </summary>
        public string Alias { get; }

        /// <summary>
        /// Gets the function that extracts the index value.
        /// </summary>
        public Func<object, string> Accessor { get; }
    }

    /// <summary>
    /// Describes one generated persisted document member.
    /// </summary>
    public sealed class CultGeneratedDocumentMemberDefinition
    {
        /// <summary>
        /// Creates a generated persisted member definition.
        /// </summary>
        public CultGeneratedDocumentMemberDefinition(
            string memberName,
            int slot,
            string typeName,
            bool isReference,
            bool isMany,
            string? targetSchemaName,
            bool isName,
            string? indexAlias)
        {
            MemberName = string.IsNullOrWhiteSpace(memberName)
                ? throw new ArgumentException("Member name must be non-empty.", nameof(memberName))
                : memberName;
            Slot = slot;
            TypeName = string.IsNullOrWhiteSpace(typeName)
                ? throw new ArgumentException("Type name must be non-empty.", nameof(typeName))
                : typeName;
            IsReference = isReference;
            IsMany = isMany;
            TargetSchemaName = targetSchemaName;
            IsName = isName;
            IndexAlias = indexAlias;
        }

        /// <summary>
        /// Gets the CLR member name.
        /// </summary>
        public string MemberName { get; }

        /// <summary>
        /// Gets the persisted slot number.
        /// </summary>
        public int Slot { get; }

        /// <summary>
        /// Gets the persisted type name.
        /// </summary>
        public string TypeName { get; }

        /// <summary>
        /// Gets whether this member stores a document reference.
        /// </summary>
        public bool IsReference { get; }

        /// <summary>
        /// Gets whether this member stores many references.
        /// </summary>
        public bool IsMany { get; }

        /// <summary>
        /// Gets the target schema name for reference members.
        /// </summary>
        public string? TargetSchemaName { get; }

        /// <summary>
        /// Gets whether this member is the document name.
        /// </summary>
        public bool IsName { get; }

        /// <summary>
        /// Gets the index alias for indexed members.
        /// </summary>
        public string? IndexAlias { get; }
    }

    internal static class CultGeneratedDocumentMetadataLoader
    {
        public static IEnumerable<CultGeneratedDocumentDefinition> LoadDefinitions()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .SelectMany(LoadDefinitions);
        }

        public static IEnumerable<CultGeneratedDocumentDefinition> LoadDefinitions(Assembly assembly)
        {
            foreach (var attribute in assembly.GetCustomAttributes<CultGeneratedDocumentMetadataProviderAttribute>())
            {
                if (!typeof(ICultGeneratedDocumentMetadataProvider).IsAssignableFrom(attribute.ProviderType))
                {
                    continue;
                }

                if (Activator.CreateInstance(attribute.ProviderType) is not ICultGeneratedDocumentMetadataProvider provider)
                {
                    continue;
                }

                foreach (var definition in provider.GetDocumentDefinitions())
                {
                    yield return definition;
                }
            }
        }
    }

    internal static class CultSchemaTypeNames
    {
        public static string FromType(Type type)
        {
            if (type.IsArray)
            {
                return FromType(type.GetElementType()!) + "[]";
            }

            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                var name = definition.FullName ?? definition.Name;
                var tickIndex = name.IndexOf('`');
                if (tickIndex >= 0)
                {
                    name = name[..tickIndex];
                }

                var arguments = string.Join(", ", type.GetGenericArguments().Select(FromType));
                return $"{name}<{arguments}>";
            }

            return type.FullName ?? type.Name;
        }

        public static string EscapeForLiteral(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                builder.Append(character switch
                {
                    '\\' => "\\\\",
                    '"' => "\\\"",
                    '\r' => "\\r",
                    '\n' => "\\n",
                    '\t' => "\\t",
                    _ => character.ToString()
                });
            }

            return builder.ToString();
        }
    }
}
