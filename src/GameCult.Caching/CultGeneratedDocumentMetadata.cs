using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace GameCult.Caching
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class CultGeneratedDocumentMetadataProviderAttribute : Attribute
    {
        public CultGeneratedDocumentMetadataProviderAttribute(Type providerType)
        {
            ProviderType = providerType ?? throw new ArgumentNullException(nameof(providerType));
        }

        public Type ProviderType { get; }
    }

    public interface ICultGeneratedDocumentMetadataProvider
    {
        IEnumerable<CultGeneratedDocumentDefinition> GetDocumentDefinitions();
    }

    public sealed class CultGeneratedDocumentDefinition
    {
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

        public Type DocumentType { get; }
        public string SchemaName { get; }
        public string SchemaVersion { get; }
        public bool IsGlobal { get; }
        public string? NameMember { get; }
        public Func<object, string?>? NameAccessor { get; }
        public Func<object, byte[]>? SerializePayload { get; }
        public Func<byte[], object>? DeserializePayload { get; }
        public IReadOnlyList<CultGeneratedDocumentIndexAccessor> IndexAccessors { get; }
        public IReadOnlyList<CultGeneratedDocumentMemberDefinition> Members { get; }
    }

    public sealed class CultGeneratedDocumentIndexAccessor
    {
        public CultGeneratedDocumentIndexAccessor(string alias, Func<object, string> accessor)
        {
            Alias = string.IsNullOrWhiteSpace(alias)
                ? throw new ArgumentException("Alias must be non-empty.", nameof(alias))
                : alias;
            Accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        }

        public string Alias { get; }
        public Func<object, string> Accessor { get; }
    }

    public sealed class CultGeneratedDocumentMemberDefinition
    {
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

        public string MemberName { get; }
        public int Slot { get; }
        public string TypeName { get; }
        public bool IsReference { get; }
        public bool IsMany { get; }
        public string? TargetSchemaName { get; }
        public bool IsName { get; }
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
