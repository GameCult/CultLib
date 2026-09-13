#nullable enable
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
/// <summary>
/// Emits MessagePack payload serializers for CultCache document types.
/// </summary>
[Generator]
public sealed class CultDocumentMessagePackGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Registers the generator pipeline.
    /// </summary>
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

            var rejections = new List<string>();
            var members = DiscoverMembers(typeSymbol, rejections);
            var nameMember = members.FirstOrDefault(member => member.IsName);
            var canConstructForDeserialization = typeSymbol.TypeKind == TypeKind.Struct ||
                typeSymbol.InstanceConstructors.Any(constructor =>
                    constructor.Parameters.Length == 0 &&
                    constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal);
            var hasDenseSlots = members
                .OrderBy(member => member.Slot)
                .Select((member, index) => member.Slot == index)
                .All(matches => matches);
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
                members,
                canConstructForDeserialization,
                hasDenseSlots,
                rejections.ToImmutableArray(),
                typeSymbol.Locations.FirstOrDefault());
        }

        // Mirrors CultDocumentRegistry.DiscoverMembers step for step: public instance fields and get/set properties, most-derived
        // declaration first, a property's attributes resolved up its override chain, rejections in the same order and text.
        private static ImmutableArray<MemberShape> DiscoverMembers(INamedTypeSymbol typeSymbol, List<string> rejections)
        {
            var documentName = typeSymbol.Name;
            var documentStem = SanitizeIdentifier(typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", string.Empty).Replace(".", "_"));
            var candidates = new List<MemberCandidate>();
            var seenRoots = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var depth = 0;
            for (var type = typeSymbol; type != null && type.SpecialType != SpecialType.System_Object; type = type.BaseType, depth++)
            {
                foreach (var member in type.GetMembers())
                {
                    if (member is IFieldSymbol field && !field.IsStatic && !field.IsConst && !field.IsImplicitlyDeclared &&
                        field.DeclaredAccessibility == Accessibility.Public)
                    {
                        if (!IsIgnored(field))
                            candidates.Add(new MemberCandidate(field, field.Type, depth, !field.IsReadOnly, null));
                    }
                    else if (member is IPropertySymbol property && !property.IsStatic && !property.IsIndexer &&
                             property.DeclaredAccessibility == Accessibility.Public && seenRoots.Add(RootOf(property)))
                    {
                        var setter = OverrideChain(property).Select(declaration => declaration.SetMethod).FirstOrDefault(method => method != null);
                        if (OverrideChain(property).All(declaration => declaration.GetMethod == null) || setter == null)
                            continue;
                        var root = RootOf(property);
                        var divergentRoot = OverrideChain(property).Any(declaration =>
                            !Equals(KeyValue(declaration), KeyValue(root)) || IsIgnored(declaration) != IsIgnored(root))
                            ? root
                            : null;
                        if (divergentRoot == null && IsIgnored(property))
                            continue;
                        candidates.Add(new MemberCandidate(property, property.Type, depth,
                            !setter.IsInitOnly && setter.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal,
                            divergentRoot));
                    }
                }
            }

            var keyed = new List<(MemberCandidate Candidate, int Slot)>();
            foreach (var candidate in candidates.OrderBy(candidate => candidate.Depth).ThenBy(candidate => candidate.Member.Name, StringComparer.Ordinal))
            {
                var keyValue = KeyValue(candidate.Member);
                if (candidate.DivergentRoot != null)
                    rejections.Add(DivergentOverrideMessage(documentName, Qualified(candidate.Member), Qualified(candidate.DivergentRoot)));
                else if (keyValue is string)
                    rejections.Add(StringKeyMessage(documentName, candidate.Member.Name));
                else if (keyValue is not int slot)
                    rejections.Add(UnkeyedMemberMessage(documentName, candidate.Member.Name));
                else
                {
                    if (!candidate.Writable)
                        rejections.Add(NotWritableMessage(documentName, candidate.Member.Name));
                    keyed.Add((candidate, slot));
                }
            }

            foreach (var group in keyed.GroupBy(entry => entry.Slot).Where(group => group.Count() > 1).OrderBy(group => group.Key))
            {
                var pair = group.Take(2).ToArray();
                rejections.Add(DuplicateSlotMessage(documentName, Qualified(pair[0].Candidate.Member), Qualified(pair[1].Candidate.Member), group.Key));
            }

            foreach (var group in keyed.GroupBy(entry => entry.Candidate.Member.Name)
                         .Where(group => group.Select(entry => entry.Slot).Distinct().Count() > 1)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var pair = group.Take(2).ToArray();
                rejections.Add(HiddenMemberMessage(documentName, Qualified(pair[0].Candidate.Member), Qualified(pair[1].Candidate.Member)));
            }

            return keyed
                .Select(entry => MemberShapeSeed.From(entry.Candidate.Member, entry.Candidate.Type).WithSlot(entry.Slot, documentStem))
                .OrderBy(member => member.Slot)
                .ToImmutableArray();
        }

        private static IPropertySymbol RootOf(IPropertySymbol property)
        {
            while (property.OverriddenProperty != null)
                property = property.OverriddenProperty;
            return property;
        }

        private static IEnumerable<IPropertySymbol> OverrideChain(IPropertySymbol property)
        {
            for (IPropertySymbol? declaration = property; declaration != null; declaration = declaration.OverriddenProperty)
                yield return declaration;
        }

        private static string Qualified(ISymbol member) => member.ContainingType.Name + "." + member.Name;

        // CultDocumentRegistry throws the same texts.
        private static string UnkeyedMemberMessage(string documentTypeName, string memberName) =>
            $"Cult document {documentTypeName} member {memberName} has no [Key]; every persisted member of a [CultDocument] type needs an explicit [Key(n)].";

        private static string StringKeyMessage(string documentTypeName, string memberName) =>
            $"Cult document {documentTypeName} member {memberName} has a string [Key]; string keys are not supported; use integer [Key(n)].";

        private static string NotWritableMessage(string documentTypeName, string memberName) =>
            $"Cult document {documentTypeName} member {memberName} is not writable; a persisted member needs a non-readonly field or a public or internal set accessor.";

        private static string DuplicateSlotMessage(string documentTypeName, string first, string second, int slot) =>
            $"Cult document {documentTypeName} members {first} and {second} share [Key({slot})]; every persisted member needs a distinct [Key(n)].";

        private static string DivergentOverrideMessage(string documentTypeName, string member, string root) =>
            $"Cult document {documentTypeName} member {member} overrides {root} with a different [Key] or [IgnoreMember]; MessagePack reads the base declaration's, so an override must repeat or omit them.";

        private static object? KeyValue(ISymbol member)
        {
            var key = GetMemberAttribute(member, "MessagePack.KeyAttribute");
            return key != null && key.ConstructorArguments.Length > 0 ? key.ConstructorArguments[0].Value : null;
        }

        private static string HiddenMemberMessage(string documentTypeName, string first, string second) =>
            $"Cult document {documentTypeName} member {first} hides persisted member {second}; persisted member names must be unique.";

#pragma warning disable RS2008
        private static readonly DiagnosticDescriptor RejectedMember = new(
            "GCC001", "Cult document member cannot be persisted", "{0}", "GameCult.Caching", DiagnosticSeverity.Error, isEnabledByDefault: true);
#pragma warning restore RS2008

        private static bool IsIgnored(ISymbol member)
        {
            return GetMemberAttribute(member, "MessagePack.IgnoreMemberAttribute") != null;
        }

        private static AttributeData? GetAttribute(ISymbol symbol, string fullyQualifiedName)
        {
            return symbol.GetAttributes().FirstOrDefault(attribute =>
                attribute.AttributeClass?.ToDisplayString() == fullyQualifiedName);
        }

        // Attribute.GetCustomAttributes(inherit: true) semantics: an override without the attribute inherits its base declaration's.
        private static AttributeData? GetMemberAttribute(ISymbol member, string fullyQualifiedName)
        {
            if (member is not IPropertySymbol property)
                return GetAttribute(member, fullyQualifiedName);
            return OverrideChain(property).Select(declaration => GetAttribute(declaration, fullyQualifiedName)).FirstOrDefault(attribute => attribute != null);
        }

        private static void EmitProvider(SourceProductionContext context, Compilation compilation, ImmutableArray<DocumentShape> shapes)
        {
            var shaped = shapes
                .Where(shape => shape != null)
                .GroupBy(shape => shape.DocumentTypeName, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(shape => shape.DocumentTypeName, StringComparer.Ordinal)
                .ToArray();
            foreach (var shape in shaped)
            {
                foreach (var message in shape.Rejections)
                    context.ReportDiagnostic(Diagnostic.Create(RejectedMember, shape.Location, message));
            }

            var documents = shaped.Where(shape => shape.Rejections.IsEmpty).ToArray();

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
                builder.AppendLine(canEmitPayloadCodecs && document.HasDenseSlots
                    ? $"                {document.PayloadSerializerMethodName},"
                    : "                null,");
                builder.AppendLine(canEmitPayloadCodecs && document.HasDenseSlots && document.CanConstructForDeserialization
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
                if (canEmitPayloadCodecs && document.HasDenseSlots)
                {
                    EmitPayloadSerializer(builder, document);
                    if (document.CanConstructForDeserialization)
                    {
                        EmitPayloadDeserializer(builder, document);
                    }
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
            builder.AppendLine($"            var options = global::GameCult.Caching.MessagePack.CultDocumentMessagePackSerialization.OptionsFor(typeof({document.DocumentTypeName}).Assembly);");
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
            builder.AppendLine($"            var options = global::GameCult.Caching.MessagePack.CultDocumentMessagePackSerialization.OptionsFor(typeof({document.DocumentTypeName}).Assembly);");
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

        private sealed class MemberCandidate
        {
            public MemberCandidate(ISymbol member, ITypeSymbol type, int depth, bool writable, IPropertySymbol? divergentRoot)
            {
                Member = member;
                Type = type;
                Depth = depth;
                Writable = writable;
                DivergentRoot = divergentRoot;
            }

            public ISymbol Member { get; }
            public ITypeSymbol Type { get; }
            public int Depth { get; }
            public bool Writable { get; }
            public IPropertySymbol? DivergentRoot { get; }
        }

        private sealed class MemberShapeSeed
        {
            private MemberShapeSeed(
                string name,
                string schemaTypeName,
                string typeSyntaxName,
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
            public bool IsName { get; }
            public string? IndexAlias { get; }
            public bool IsReference { get; }
            public bool IsMany { get; }
            public bool CanBeNull { get; }
            public string? TargetSchemaName { get; }

            // Accessors are named per document: one provider class holds every document of the assembly, and documents sharing a
            // base member would otherwise emit the same method twice.
            public MemberShape WithSlot(int slot, string documentStem)
            {
                var accessorStem = documentStem + "_" + SanitizeIdentifier(Name);
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
                var referenceAttribute = GetMemberAttribute(member, "GameCult.Caching.CultReferenceAttribute");
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
                var indexAttribute = GetMemberAttribute(member, "GameCult.Caching.CultIndexAttribute");
                var indexAlias = indexAttribute == null
                    ? null
                    : indexAttribute.ConstructorArguments.Length == 0 || indexAttribute.ConstructorArguments[0].Value is not string alias || string.IsNullOrWhiteSpace(alias)
                        ? member.Name
                        : alias;

                return new MemberShapeSeed(
                    member.Name,
                    GetSchemaTypeName(memberType),
                    memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    GetMemberAttribute(member, "GameCult.Caching.CultNameAttribute") != null,
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
                ImmutableArray<MemberShape> members,
                bool canConstructForDeserialization,
                bool hasDenseSlots,
                ImmutableArray<string> rejections,
                Location? location)
            {
                Rejections = rejections;
                Location = location;
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
                CanConstructForDeserialization = canConstructForDeserialization;
                HasDenseSlots = hasDenseSlots;
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
            public bool CanConstructForDeserialization { get; }
            public bool HasDenseSlots { get; }
            public ImmutableArray<string> Rejections { get; }
            public Location? Location { get; }
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
