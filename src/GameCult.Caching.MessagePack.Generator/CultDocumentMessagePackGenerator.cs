using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace GameCult.Caching.MessagePack.Generator
{
    [Generator]
    public sealed class CultDocumentMessagePackGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var documentShapes = context.SyntaxProvider
                .CreateSyntaxProvider(
                    static (node, _) => node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0,
                    static (ctx, _) => BuildDocumentShape(ctx))
                .Where(static shape => shape != null)
                .Select(static (shape, _) => shape!);

            var compilationAndShapes = context.CompilationProvider.Combine(documentShapes.Collect());
            context.RegisterSourceOutput(
                compilationAndShapes,
                static (spc, pair) => EmitProvider(spc, pair.Left, pair.Right));
        }

        private static DocumentShape? BuildDocumentShape(GeneratorSyntaxContext context)
        {
            if (context.SemanticModel.GetDeclaredSymbol(context.Node) is not INamedTypeSymbol typeSymbol)
            {
                return null;
            }

            var documentAttribute = GetAttribute(typeSymbol, "GameCult.Caching.CultDocumentAttribute");
            if (documentAttribute == null ||
                documentAttribute.ConstructorArguments.Length < 2 ||
                documentAttribute.ConstructorArguments[0].Value is not string schemaName ||
                documentAttribute.ConstructorArguments[1].Value is not string schemaVersion)
            {
                return null;
            }

            var members = DiscoverMembers(typeSymbol);
            var nameMember = members.FirstOrDefault(member => member.IsName);
            return new DocumentShape(
                typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                schemaName,
                schemaVersion,
                GetAttribute(typeSymbol, "GameCult.Caching.CultGlobalAttribute") != null,
                nameMember?.Name,
                nameMember?.AccessorMethodName,
                members.Where(member => member.IndexAlias != null)
                    .Select(member => new IndexAccessorShape(member.IndexAlias!, member.IndexAccessorMethodName!))
                    .ToImmutableArray(),
                members.ToImmutableArray());
        }

        private static ImmutableArray<MemberShape> DiscoverMembers(INamedTypeSymbol typeSymbol)
        {
            var candidates = new List<MemberShapeSeed>();
            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is IFieldSymbol field)
                {
                    if (field.IsStatic || field.DeclaredAccessibility != Accessibility.Public || IsIgnored(field))
                    {
                        continue;
                    }

                    candidates.Add(MemberShapeSeed.From(field, field.Type));
                    continue;
                }

                if (member is IPropertySymbol property)
                {
                    if (property.IsStatic ||
                        property.IsIndexer ||
                        property.DeclaredAccessibility != Accessibility.Public ||
                        property.GetMethod == null ||
                        property.SetMethod == null ||
                        IsIgnored(property))
                    {
                        continue;
                    }

                    candidates.Add(MemberShapeSeed.From(property, property.Type));
                }
            }

            var explicitMembers = candidates
                .Where(member => member.ExplicitSlot.HasValue)
                .OrderBy(member => member.ExplicitSlot!.Value)
                .ThenBy(member => member.SortKey, StringComparer.Ordinal)
                .ToArray();
            var implicitMembers = candidates
                .Where(member => !member.ExplicitSlot.HasValue)
                .OrderBy(member => member.SortKey, StringComparer.Ordinal)
                .ToArray();
            var nextSlot = explicitMembers.Length == 0 ? 0 : explicitMembers.Max(member => member.ExplicitSlot!.Value) + 1;
            var shaped = new List<MemberShape>(candidates.Count);

            foreach (var member in explicitMembers)
            {
                shaped.Add(member.WithSlot(member.ExplicitSlot!.Value));
            }

            foreach (var member in implicitMembers)
            {
                shaped.Add(member.WithSlot(nextSlot++));
            }

            return shaped
                .OrderBy(member => member.Slot)
                .ToImmutableArray();
        }

        private static bool IsIgnored(ISymbol member)
        {
            return GetAttribute(member, "MessagePack.IgnoreMemberAttribute") != null;
        }

        private static AttributeData? GetAttribute(ISymbol symbol, string fullyQualifiedName)
        {
            return symbol.GetAttributes().FirstOrDefault(attribute =>
                attribute.AttributeClass?.ToDisplayString() == fullyQualifiedName);
        }

        private static int? GetExplicitSlot(ISymbol member)
        {
            var keyAttribute = GetAttribute(member, "MessagePack.KeyAttribute");
            if (keyAttribute == null || keyAttribute.ConstructorArguments.Length == 0)
            {
                return null;
            }

            var argument = keyAttribute.ConstructorArguments[0];
            return argument.Kind == TypedConstantKind.Primitive && argument.Value is int slot
                ? slot
                : null;
        }

        private static void EmitProvider(SourceProductionContext context, Compilation compilation, ImmutableArray<DocumentShape> shapes)
        {
            var documents = shapes
                .Where(shape => shape != null)
                .GroupBy(shape => shape.DocumentTypeName, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(shape => shape.DocumentTypeName, StringComparer.Ordinal)
                .ToArray();

            if (documents.Length == 0)
            {
                return;
            }

            var canEmitPayloadCodecs =
                compilation.GetTypeByMetadataName("MessagePack.MessagePackWriter") != null &&
                compilation.GetTypeByMetadataName("GameCult.Caching.MessagePack.CultDocumentMessagePackSerialization") != null;

            var providerName = $"GeneratedCultDocumentMetadataProvider_{SanitizeIdentifier(compilation.AssemblyName ?? "GameCult")}";
            var builder = new StringBuilder();
            builder.AppendLine("// <auto-generated/>");
            builder.AppendLine("#nullable enable");
            builder.AppendLine($"[assembly: global::GameCult.Caching.CultGeneratedDocumentMetadataProviderAttribute(typeof(global::GameCult.Caching.Generated.{providerName}))]");
            builder.AppendLine("namespace GameCult.Caching.Generated");
            builder.AppendLine("{");
            builder.AppendLine($"    internal sealed class {providerName} : global::GameCult.Caching.ICultGeneratedDocumentMetadataProvider");
            builder.AppendLine("    {");
            builder.AppendLine("        private static readonly global::GameCult.Caching.CultGeneratedDocumentDefinition[] s_documents =");
            builder.AppendLine("        {");

            foreach (var document in documents)
            {
                builder.AppendLine("            new global::GameCult.Caching.CultGeneratedDocumentDefinition(");
                builder.AppendLine($"                typeof({document.DocumentTypeName}),");
                builder.AppendLine($"                \"{Escape(document.SchemaName)}\",");
                builder.AppendLine($"                \"{Escape(document.SchemaVersion)}\",");
                builder.AppendLine($"                {(document.IsGlobal ? "true" : "false")},");
                builder.AppendLine(document.NameMember == null
                    ? "                null,"
                    : $"                \"{Escape(document.NameMember)}\",");
                builder.AppendLine(document.NameAccessorMethodName == null
                    ? "                null,"
                    : $"                {document.NameAccessorMethodName},");
                builder.AppendLine(canEmitPayloadCodecs
                    ? $"                {document.PayloadSerializerMethodName},"
                    : "                null,");
                builder.AppendLine(canEmitPayloadCodecs
                    ? $"                {document.PayloadDeserializerMethodName},"
                    : "                null,");
                builder.AppendLine("                new global::GameCult.Caching.CultGeneratedDocumentIndexAccessor[]");
                builder.AppendLine("                {");
                foreach (var indexAccessor in document.IndexAccessors)
                {
                    builder.AppendLine($"                    new global::GameCult.Caching.CultGeneratedDocumentIndexAccessor(\"{Escape(indexAccessor.Alias)}\", {indexAccessor.AccessorMethodName}),");
                }
                builder.AppendLine("                },");
                builder.AppendLine("                new global::GameCult.Caching.CultGeneratedDocumentMemberDefinition[]");
                builder.AppendLine("                {");
                foreach (var member in document.Members.OrderBy(member => member.Slot))
                {
                    builder.AppendLine("                    new global::GameCult.Caching.CultGeneratedDocumentMemberDefinition(");
                    builder.AppendLine($"                        \"{Escape(member.Name)}\",");
                    builder.AppendLine($"                        {member.Slot},");
                    builder.AppendLine($"                        \"{Escape(member.SchemaTypeName)}\",");
                    builder.AppendLine($"                        {(member.IsReference ? "true" : "false")},");
                    builder.AppendLine($"                        {(member.IsMany ? "true" : "false")},");
                    builder.AppendLine(member.TargetSchemaName == null
                        ? "                        null,"
                        : $"                        \"{Escape(member.TargetSchemaName)}\",");
                    builder.AppendLine($"                        {(member.IsName ? "true" : "false")},");
                    builder.AppendLine(member.IndexAlias == null
                        ? "                        null),"
                        : $"                        \"{Escape(member.IndexAlias)}\"),");
                }
                builder.AppendLine("                }),");
            }

            builder.AppendLine("        };");
            builder.AppendLine();
            builder.AppendLine("        public global::System.Collections.Generic.IEnumerable<global::GameCult.Caching.CultGeneratedDocumentDefinition> GetDocumentDefinitions()");
            builder.AppendLine("        {");
            builder.AppendLine("            return s_documents;");
            builder.AppendLine("        }");
            builder.AppendLine();

            foreach (var document in documents)
            {
                if (canEmitPayloadCodecs)
                {
                    EmitPayloadSerializer(builder, document);
                    EmitPayloadDeserializer(builder, document);
                }

                foreach (var member in document.Members.Where(member => member.AccessorMethodName != null))
                {
                    builder.AppendLine($"        private static string? {member.AccessorMethodName}(object document)");
                    builder.AppendLine("        {");
                    builder.AppendLine(member.CanBeNull
                        ? $"            return (({document.DocumentTypeName})document).{member.Name}?.ToString();"
                        : $"            return (({document.DocumentTypeName})document).{member.Name}.ToString();");
                    builder.AppendLine("        }");
                    builder.AppendLine();
                }

                foreach (var member in document.Members.Where(member => member.IndexAccessorMethodName != null))
                {
                    builder.AppendLine($"        private static string {member.IndexAccessorMethodName}(object document)");
                    builder.AppendLine("        {");
                    builder.AppendLine(member.CanBeNull
                        ? $"            return (({document.DocumentTypeName})document).{member.Name}?.ToString() ?? string.Empty;"
                        : $"            return (({document.DocumentTypeName})document).{member.Name}.ToString();");
                    builder.AppendLine("        }");
                    builder.AppendLine();
                }
            }

            builder.AppendLine("    }");
            builder.AppendLine("}");
            context.AddSource($"{providerName}.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
        }

        private static void EmitPayloadSerializer(StringBuilder builder, DocumentShape document)
        {
            builder.AppendLine($"        private static byte[] {document.PayloadSerializerMethodName}(object document)");
            builder.AppendLine("        {");
            builder.AppendLine($"            var typed = ({document.DocumentTypeName})document;");
            builder.AppendLine("            var options = global::GameCult.Caching.MessagePack.CultDocumentMessagePackSerialization.Options;");
            builder.AppendLine("            var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();");
            builder.AppendLine("            var writer = new global::MessagePack.MessagePackWriter(buffer);");
            builder.AppendLine($"            writer.WriteArrayHeader({document.Members.Length});");
            foreach (var member in document.Members.OrderBy(member => member.Slot))
            {
                builder.AppendLine($"            global::MessagePack.FormatterResolverExtensions.GetFormatterWithVerify<{member.TypeSyntaxName}>(options.Resolver).Serialize(ref writer, typed.{member.Name}, options);");
            }
            builder.AppendLine("            writer.Flush();");
            builder.AppendLine("            return buffer.WrittenSpan.ToArray();");
            builder.AppendLine("        }");
            builder.AppendLine();
        }

        private static void EmitPayloadDeserializer(StringBuilder builder, DocumentShape document)
        {
            builder.AppendLine($"        private static object {document.PayloadDeserializerMethodName}(byte[] payload)");
            builder.AppendLine("        {");
            builder.AppendLine("            var options = global::GameCult.Caching.MessagePack.CultDocumentMessagePackSerialization.Options;");
            builder.AppendLine("            var reader = new global::MessagePack.MessagePackReader(payload);");
            builder.AppendLine("            var count = reader.ReadArrayHeader();");
            builder.AppendLine($"            var value = new {document.DocumentTypeName}();");
            foreach (var member in document.Members.OrderBy(member => member.Slot))
            {
                builder.AppendLine($"            if (count > {member.Slot})");
                builder.AppendLine("            {");
                builder.AppendLine($"                value.{member.Name} = global::MessagePack.FormatterResolverExtensions.GetFormatterWithVerify<{member.TypeSyntaxName}>(options.Resolver).Deserialize(ref reader, options);");
                builder.AppendLine("            }");
            }
            builder.AppendLine($"            for (var index = {document.Members.Length}; index < count; index++)");
            builder.AppendLine("            {");
            builder.AppendLine("                reader.Skip();");
            builder.AppendLine("            }");
            builder.AppendLine("            return value;");
            builder.AppendLine("        }");
            builder.AppendLine();
        }

        private static string Escape(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private static string SanitizeIdentifier(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                builder.Append(char.IsLetterOrDigit(character) ? character : '_');
            }

            if (builder.Length == 0 || !char.IsLetter(builder[0]) && builder[0] != '_')
            {
                builder.Insert(0, '_');
            }

            return builder.ToString();
        }

        private static string GetSchemaTypeName(ITypeSymbol typeSymbol)
        {
            if (typeSymbol is IArrayTypeSymbol arrayType)
            {
                return GetSchemaTypeName(arrayType.ElementType) + "[]";
            }

            if (typeSymbol is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                var baseName = GetNamedTypeBaseName(namedType.ConstructedFrom);
                var arguments = string.Join(", ", namedType.TypeArguments.Select(GetSchemaTypeName));
                return $"{baseName}<{arguments}>";
            }

            return typeSymbol.SpecialType switch
            {
                SpecialType.System_Boolean => "System.Boolean",
                SpecialType.System_Byte => "System.Byte",
                SpecialType.System_Char => "System.Char",
                SpecialType.System_Decimal => "System.Decimal",
                SpecialType.System_Double => "System.Double",
                SpecialType.System_Int16 => "System.Int16",
                SpecialType.System_Int32 => "System.Int32",
                SpecialType.System_Int64 => "System.Int64",
                SpecialType.System_Object => "System.Object",
                SpecialType.System_SByte => "System.SByte",
                SpecialType.System_Single => "System.Single",
                SpecialType.System_String => "System.String",
                SpecialType.System_UInt16 => "System.UInt16",
                SpecialType.System_UInt32 => "System.UInt32",
                SpecialType.System_UInt64 => "System.UInt64",
                _ => GetNamedTypeBaseName((INamedTypeSymbol)typeSymbol)
            };
        }

        private static string GetNamedTypeBaseName(INamedTypeSymbol typeSymbol)
        {
            var parts = new Stack<string>();
            var current = typeSymbol;
            while (current != null)
            {
                parts.Push(current.Name);
                current = current.ContainingType;
            }

            var namespaceName = typeSymbol.ContainingNamespace?.ToDisplayString();
            return string.IsNullOrWhiteSpace(namespaceName)
                ? string.Join(".", parts)
                : namespaceName + "." + string.Join(".", parts);
        }

        private static bool CanBeNull(ITypeSymbol typeSymbol)
        {
            if (typeSymbol.NullableAnnotation == NullableAnnotation.Annotated)
            {
                return true;
            }

            if (typeSymbol.IsReferenceType)
            {
                return true;
            }

            return typeSymbol is INamedTypeSymbol namedType &&
                   namedType.IsGenericType &&
                   namedType.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;
        }

        private sealed class MemberShapeSeed
        {
            private MemberShapeSeed(
                string name,
                string schemaTypeName,
                string typeSyntaxName,
                int? explicitSlot,
                string sortKey,
                bool isName,
                string? indexAlias,
                bool isReference,
                bool isMany,
                bool canBeNull,
                string? targetSchemaName)
            {
                Name = name;
                SchemaTypeName = schemaTypeName;
                TypeSyntaxName = typeSyntaxName;
                ExplicitSlot = explicitSlot;
                SortKey = sortKey;
                IsName = isName;
                IndexAlias = indexAlias;
                IsReference = isReference;
                IsMany = isMany;
                CanBeNull = canBeNull;
                TargetSchemaName = targetSchemaName;
            }

            public string Name { get; }
            public string SchemaTypeName { get; }
            public string TypeSyntaxName { get; }
            public int? ExplicitSlot { get; }
            public string SortKey { get; }
            public bool IsName { get; }
            public string? IndexAlias { get; }
            public bool IsReference { get; }
            public bool IsMany { get; }
            public bool CanBeNull { get; }
            public string? TargetSchemaName { get; }

            public MemberShape WithSlot(int slot)
            {
                var accessorStem = SanitizeIdentifier(SortKey);
                return new MemberShape(
                    Name,
                    slot,
                    SchemaTypeName,
                    TypeSyntaxName,
                    IsReference,
                    IsMany,
                    TargetSchemaName,
                    IsName,
                    IndexAlias,
                    CanBeNull,
                    IsName ? $"Access_{accessorStem}_Name" : null,
                    IndexAlias != null ? $"Access_{accessorStem}_Index" : null);
            }

            public static MemberShapeSeed From(ISymbol member, ITypeSymbol memberType)
            {
                var referenceAttribute = GetAttribute(member, "GameCult.Caching.CultReferenceAttribute");
                var explicitTarget = referenceAttribute?.ConstructorArguments.Length > 0
                    ? referenceAttribute.ConstructorArguments[0].Value as INamedTypeSymbol
                    : null;
                var many = referenceAttribute?.ConstructorArguments.Length > 1 &&
                           referenceAttribute.ConstructorArguments[1].Value is bool manyValue &&
                           manyValue;
                var targetType = ResolveReferenceTarget(memberType, explicitTarget);
                var targetSchemaName = targetType == null
                    ? null
                    : GetAttribute(targetType, "GameCult.Caching.CultDocumentAttribute")?.ConstructorArguments[0].Value as string;
                var indexAttribute = GetAttribute(member, "GameCult.Caching.CultIndexAttribute");
                var indexAlias = indexAttribute == null
                    ? null
                    : indexAttribute.ConstructorArguments.Length == 0 || indexAttribute.ConstructorArguments[0].Value is not string alias || string.IsNullOrWhiteSpace(alias)
                        ? member.Name
                        : alias;

                return new MemberShapeSeed(
                    member.Name,
                    GetSchemaTypeName(memberType),
                    memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    GetExplicitSlot(member),
                    BuildAccessorStem(member),
                    GetAttribute(member, "GameCult.Caching.CultNameAttribute") != null,
                    indexAlias,
                    targetType != null || referenceAttribute != null,
                    many,
                    CanBeNull(memberType),
                    targetSchemaName);
            }

            private static INamedTypeSymbol? ResolveReferenceTarget(ITypeSymbol memberType, INamedTypeSymbol? explicitTarget)
            {
                if (explicitTarget != null)
                {
                    return explicitTarget;
                }

                return memberType is INamedTypeSymbol namedType &&
                       namedType.IsGenericType &&
                       namedType.Name == "CultRecordRef" &&
                       namedType.ContainingNamespace.ToDisplayString() == "GameCult.Caching"
                    ? namedType.TypeArguments[0] as INamedTypeSymbol
                    : null;
            }

            private static string BuildAccessorStem(ISymbol member)
            {
                return member.ContainingType == null
                    ? member.Name
                    : member.ContainingType.Name + "_" + member.Name;
            }
        }

        private sealed class DocumentShape
        {
            public DocumentShape(
                string documentTypeName,
                string schemaName,
                string schemaVersion,
                bool isGlobal,
                string? nameMember,
                string? nameAccessorMethodName,
                ImmutableArray<IndexAccessorShape> indexAccessors,
                ImmutableArray<MemberShape> members)
            {
                DocumentTypeName = documentTypeName;
                SchemaName = schemaName;
                SchemaVersion = schemaVersion;
                IsGlobal = isGlobal;
                NameMember = nameMember;
                NameAccessorMethodName = nameAccessorMethodName;
                var safeStem = SanitizeIdentifier(documentTypeName.Replace("global::", string.Empty).Replace(".", "_"));
                PayloadSerializerMethodName = $"Serialize_{safeStem}_Payload";
                PayloadDeserializerMethodName = $"Deserialize_{safeStem}_Payload";
                IndexAccessors = indexAccessors;
                Members = members;
            }

            public string DocumentTypeName { get; }
            public string SchemaName { get; }
            public string SchemaVersion { get; }
            public bool IsGlobal { get; }
            public string? NameMember { get; }
            public string? NameAccessorMethodName { get; }
            public string PayloadSerializerMethodName { get; }
            public string PayloadDeserializerMethodName { get; }
            public ImmutableArray<IndexAccessorShape> IndexAccessors { get; }
            public ImmutableArray<MemberShape> Members { get; }
        }

        private sealed class IndexAccessorShape
        {
            public IndexAccessorShape(string alias, string accessorMethodName)
            {
                Alias = alias;
                AccessorMethodName = accessorMethodName;
            }

            public string Alias { get; }
            public string AccessorMethodName { get; }
        }

        private sealed class MemberShape
        {
            public MemberShape(
                string name,
                int slot,
                string schemaTypeName,
                string typeSyntaxName,
                bool isReference,
                bool isMany,
                string? targetSchemaName,
                bool isName,
                string? indexAlias,
                bool canBeNull,
                string? accessorMethodName,
                string? indexAccessorMethodName)
            {
                Name = name;
                Slot = slot;
                SchemaTypeName = schemaTypeName;
                TypeSyntaxName = typeSyntaxName;
                IsReference = isReference;
                IsMany = isMany;
                TargetSchemaName = targetSchemaName;
                IsName = isName;
                IndexAlias = indexAlias;
                CanBeNull = canBeNull;
                AccessorMethodName = accessorMethodName;
                IndexAccessorMethodName = indexAccessorMethodName;
            }

            public string Name { get; }
            public int Slot { get; }
            public string SchemaTypeName { get; }
            public string TypeSyntaxName { get; }
            public bool IsReference { get; }
            public bool IsMany { get; }
            public string? TargetSchemaName { get; }
            public bool IsName { get; }
            public string? IndexAlias { get; }
            public bool CanBeNull { get; }
            public string? AccessorMethodName { get; }
            public string? IndexAccessorMethodName { get; }
        }
    }
}
