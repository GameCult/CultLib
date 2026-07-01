using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Caching;
using MessagePack;

#pragma warning disable CS1591

namespace GameCult.Mesh
{
    /// <summary>
    /// Portable CultUI/Eve surface document published through CultMesh.
    /// </summary>
    [CultDocument("gamecult.eve.surface", "gamecult.eve.surface.v1")]
    [MessagePackObject]
    public sealed class EveSurfaceDocument
    {
        [SerializationConstructor]
        public EveSurfaceDocument(
            string providerId,
            string providerKind,
            string title,
            long version,
            string updatedAtUtc,
            EveSurfaceTree surface,
            IReadOnlyList<EveSurfaceCommandTemplate> commands)
        {
            ProviderId = providerId ?? "";
            ProviderKind = providerKind ?? "";
            Title = title ?? "";
            Version = version;
            UpdatedAtUtc = updatedAtUtc ?? "";
            Surface = surface ?? throw new ArgumentNullException(nameof(surface));
            Commands = commands ?? Array.Empty<EveSurfaceCommandTemplate>();
        }

        [Key(0)]
        public string ProviderId { get; }

        [Key(1)]
        public string ProviderKind { get; }

        [Key(2)]
        public string Title { get; }

        [Key(3)]
        public long Version { get; }

        [Key(4)]
        public string UpdatedAtUtc { get; }

        [Key(5)]
        public EveSurfaceTree Surface { get; }

        [Key(6)]
        public IReadOnlyList<EveSurfaceCommandTemplate> Commands { get; }
    }

    [MessagePackObject]
    public sealed class EveSurfaceTree
    {
        [SerializationConstructor]
        public EveSurfaceTree(
            string id,
            EveSurfaceComponent root,
            IReadOnlyList<EveSurfaceStyleToken> styles)
        {
            Id = id ?? "";
            Root = root ?? throw new ArgumentNullException(nameof(root));
            Styles = styles ?? Array.Empty<EveSurfaceStyleToken>();
        }

        [Key(0)]
        public string Id { get; }

        [Key(1)]
        public EveSurfaceComponent Root { get; }

        [Key(2)]
        public IReadOnlyList<EveSurfaceStyleToken> Styles { get; }
    }

    [MessagePackObject]
    public sealed class EveSurfaceComponent
    {
        public EveSurfaceComponent(
            string id,
            string kind,
            IReadOnlyDictionary<string, string>? props = null,
            IReadOnlyList<EveSurfaceComponent>? children = null)
            : this(id, kind, props, children, StateBindingsFromProps(props), Array.Empty<EveEmbeddedDocumentSlot>())
        {
        }

        public EveSurfaceComponent(
            string id,
            string kind,
            IReadOnlyDictionary<string, string>? props,
            IReadOnlyList<EveSurfaceComponent>? children,
            IReadOnlyList<EveSurfaceStateBinding>? stateBindings)
            : this(id, kind, props, children, stateBindings, Array.Empty<EveEmbeddedDocumentSlot>())
        {
        }

        [SerializationConstructor]
        public EveSurfaceComponent(
            string id,
            string kind,
            IReadOnlyDictionary<string, string>? props,
            IReadOnlyList<EveSurfaceComponent>? children,
            IReadOnlyList<EveSurfaceStateBinding>? stateBindings,
            IReadOnlyList<EveEmbeddedDocumentSlot>? embeddedDocuments)
        {
            Id = id ?? "";
            Kind = kind ?? "";
            Props = props ?? new Dictionary<string, string>(StringComparer.Ordinal);
            Children = children ?? Array.Empty<EveSurfaceComponent>();
            StateBindings = stateBindings ?? Array.Empty<EveSurfaceStateBinding>();
            EmbeddedDocuments = embeddedDocuments ?? Array.Empty<EveEmbeddedDocumentSlot>();
        }

        [Key(0)]
        public string Id { get; }

        [Key(1)]
        public string Kind { get; }

        [Key(2)]
        public IReadOnlyDictionary<string, string> Props { get; }

        [Key(3)]
        public IReadOnlyList<EveSurfaceComponent> Children { get; }

        [Key(4)]
        public IReadOnlyList<EveSurfaceStateBinding> StateBindings { get; }

        [Key(5)]
        public IReadOnlyList<EveEmbeddedDocumentSlot> EmbeddedDocuments { get; }

        private static IReadOnlyList<EveSurfaceStateBinding> StateBindingsFromProps(
            IReadOnlyDictionary<string, string>? props)
        {
            if (props == null || props.Count == 0)
                return Array.Empty<EveSurfaceStateBinding>();

            var bindings = new List<EveSurfaceStateBinding>();
            foreach (var prop in props)
            {
                if (string.IsNullOrWhiteSpace(prop.Value) ||
                    !prop.Key.EndsWith("PointerId", StringComparison.Ordinal))
                {
                    continue;
                }

                var targetProp = prop.Key.Substring(0, prop.Key.Length - "PointerId".Length);
                if (targetProp.Length == 0)
                    targetProp = "value";
                bindings.Add(EveSurfaceStateBinding.FromDescriptor(new CultMeshStateBindingDescriptor(targetProp, prop.Value)));
            }

            return bindings;
        }
    }

    [MessagePackObject]
    public sealed class EveSurfaceStateBinding
    {
        [SerializationConstructor]
        public EveSurfaceStateBinding(
            string targetProp,
            string pointerId,
            string sourceId,
            string schemaId,
            EveSurfaceRouteHint routeHint)
        {
            TargetProp = string.IsNullOrWhiteSpace(targetProp) ? "value" : targetProp;
            PointerId = pointerId ?? "";
            SourceId = sourceId ?? "";
            SchemaId = schemaId ?? "";
            RouteHint = routeHint ?? EveSurfaceRouteHint.Automatic;
        }

        [Key(0)]
        public string TargetProp { get; }

        [Key(1)]
        public string PointerId { get; }

        [Key(2)]
        public string SourceId { get; }

        [Key(3)]
        public string SchemaId { get; }

        [Key(4)]
        public EveSurfaceRouteHint RouteHint { get; }

        public static EveSurfaceStateBinding FromDescriptor(CultMeshStateBindingDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            return new EveSurfaceStateBinding(
                descriptor.TargetProp,
                descriptor.PointerId,
                descriptor.SourceId ?? "",
                descriptor.SchemaId ?? "",
                EveSurfaceRouteHint.FromRoute(descriptor.RouteHint));
        }
    }

    [MessagePackObject]
    public sealed class EveSurfaceOperationBinding
    {
        [SerializationConstructor]
        public EveSurfaceOperationBinding(
            string operationId,
            string label,
            string schemaId,
            EveSurfaceRouteHint routeHint)
        {
            OperationId = operationId ?? "";
            Label = label ?? "";
            SchemaId = schemaId ?? "";
            RouteHint = routeHint ?? EveSurfaceRouteHint.Automatic;
        }

        [Key(0)]
        public string OperationId { get; }

        [Key(1)]
        public string Label { get; }

        [Key(2)]
        public string SchemaId { get; }

        [Key(3)]
        public EveSurfaceRouteHint RouteHint { get; }

        public static EveSurfaceOperationBinding FromDescriptor(CultMeshOperationBindingDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            return new EveSurfaceOperationBinding(
                descriptor.OperationId,
                descriptor.Label,
                descriptor.SchemaId,
                EveSurfaceRouteHint.FromRoute(descriptor.RouteHint));
        }
    }

    [MessagePackObject]
    public sealed class EveSurfaceRouteHint
    {
        public static readonly EveSurfaceRouteHint Automatic =
            new(nameof(CultMeshLocalityKind.Automatic), "");

        [SerializationConstructor]
        public EveSurfaceRouteHint(string kind, string description)
        {
            Kind = string.IsNullOrWhiteSpace(kind) ? nameof(CultMeshLocalityKind.Automatic) : kind;
            Description = description ?? "";
        }

        [Key(0)]
        public string Kind { get; }

        [Key(1)]
        public string Description { get; }

        public static EveSurfaceRouteHint FromRoute(CultMeshRouteHint routeHint)
        {
            var record = CultMeshRouteRecord.FromRoute(routeHint);
            return new EveSurfaceRouteHint(record.Kind, record.Description);
        }
    }

    [MessagePackObject]
    public sealed class EveEmbeddedDocumentSlot
    {
        [SerializationConstructor]
        public EveEmbeddedDocumentSlot(
            string slotId,
            string documentId,
            string schemaId,
            string presentationKind,
            EveSurfaceRouteHint routeHint)
        {
            SlotId = slotId ?? "";
            DocumentId = documentId ?? "";
            SchemaId = schemaId ?? "";
            PresentationKind = presentationKind ?? "";
            RouteHint = routeHint ?? EveSurfaceRouteHint.Automatic;
        }

        public EveEmbeddedDocumentSlot(
            string slotId,
            string documentId,
            string schemaId,
            string presentationKind)
            : this(slotId, documentId, schemaId, presentationKind, EveSurfaceRouteHint.Automatic)
        {
        }

        [Key(0)]
        public string SlotId { get; }

        [Key(1)]
        public string DocumentId { get; }

        [Key(2)]
        public string SchemaId { get; }

        [Key(3)]
        public string PresentationKind { get; }

        [Key(4)]
        public EveSurfaceRouteHint RouteHint { get; }
    }

    [MessagePackObject]
    public sealed class EveSurfaceStyleToken
    {
        [SerializationConstructor]
        public EveSurfaceStyleToken(string name, string value)
        {
            Name = name ?? "";
            Value = value ?? "";
        }

        [Key(0)]
        public string Name { get; }

        [Key(1)]
        public string Value { get; }
    }

    [MessagePackObject]
    public sealed class EveSurfaceCommandTemplate
    {
        public EveSurfaceCommandTemplate(string command, string label, string transport = "cultmesh")
            : this(new EveSurfaceOperationBinding(
                command,
                label,
                "",
                new EveSurfaceRouteHint(nameof(CultMeshLocalityKind.Automatic), transport)))
        {
        }

        [SerializationConstructor]
        public EveSurfaceCommandTemplate(EveSurfaceOperationBinding operation)
        {
            Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        }

        public EveSurfaceCommandTemplate(CultMeshOperationBindingDescriptor operation)
            : this(EveSurfaceOperationBinding.FromDescriptor(operation))
        {
        }

        [Key(0)]
        public EveSurfaceOperationBinding Operation { get; }

        [IgnoreMember]
        public string Command => Operation.OperationId;

        [IgnoreMember]
        public string Label => Operation.Label;
    }

    /// <summary>
    /// Fluent CultUI/Eve surface builder. It preserves the old CultUI panel ergonomics while emitting portable documents.
    /// </summary>
    public sealed class EveSurfaceBuilder
    {
        private readonly string _surfaceId;
        private readonly List<EveSurfaceComponent> _children = new();
        private readonly List<EveSurfaceStyleToken> _styles = new();
        private readonly List<EveSurfaceCommandTemplate> _commands = new();
        private long _version = 1;
        private string _providerId = "gamecult";
        private string _providerKind = "cultui.surface";
        private string _title = "";
        private string _updatedAtUtc = "";

        public EveSurfaceBuilder(string surfaceId)
        {
            _surfaceId = RequireNonEmpty(surfaceId, nameof(surfaceId));
        }

        public EveSurfaceBuilder Provider(string providerId, string providerKind)
        {
            _providerId = providerId ?? "";
            _providerKind = providerKind ?? "";
            return this;
        }

        public EveSurfaceBuilder Title(string title)
        {
            _title = title ?? "";
            _children.Add(Text($"{_surfaceId}.title", _title, "text.title"));
            return this;
        }

        public EveSurfaceBuilder TitleSubtitle(string title, string subtitle)
        {
            _title = string.IsNullOrWhiteSpace(subtitle) ? title ?? "" : $"{title} {subtitle}";
            _children.Add(Text($"{_surfaceId}.title", title ?? "", "text.title"));
            _children.Add(Text($"{_surfaceId}.subtitle", subtitle ?? "", "text.subtitle"));
            return this;
        }

        public EveSurfaceBuilder Text(string text, string? id = null)
        {
            _children.Add(Text(id ?? $"{_surfaceId}.text.{_children.Count}", text, "text"));
            return this;
        }

        public EveSurfaceBuilder Button(string label, string command)
        {
            var operation = CultMesh.OperationBinding(command, label);
            _commands.Add(new EveSurfaceCommandTemplate(operation));
            _children.Add(ButtonComponent($"{_surfaceId}.button.{Slug(label)}", label, operation));
            return this;
        }

        public EveSurfaceBuilder Button(string label, CultMeshOperationBindingDescriptor operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            _commands.Add(new EveSurfaceCommandTemplate(operation));
            _children.Add(ButtonComponent($"{_surfaceId}.button.{Slug(label)}", label, operation));
            return this;
        }

        public EveSurfaceBuilder ButtonColumn(string id, Action<EveSurfaceGroupBuilder> build)
        {
            return Group(id, "column", build);
        }

        public EveSurfaceBuilder ButtonRow(string id, Action<EveSurfaceGroupBuilder> build)
        {
            return Group(id, "row", build);
        }

        public EveSurfaceBuilder Form(string id, Action<EveSurfaceFormBuilder> build)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            var form = new EveSurfaceFormBuilder(id, AddCommand);
            build(form);
            _children.Add(form.Build());
            return this;
        }

        public EveSurfaceBuilder EmbeddedDocument(
            string slotId,
            string documentId,
            string schemaId,
            string presentationKind,
            CultMeshRouteHint? routeHint = null)
        {
            _children.Add(new EveSurfaceComponent(
                $"{_surfaceId}.slot.{Slug(slotId)}",
                "surface.slot",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["slotId"] = slotId ?? "",
                    ["documentId"] = documentId ?? "",
                    ["schemaId"] = schemaId ?? "",
                    ["presentationKind"] = presentationKind ?? ""
                },
                Array.Empty<EveSurfaceComponent>(),
                Array.Empty<EveSurfaceStateBinding>(),
                new[]
                {
                    new EveEmbeddedDocumentSlot(
                        slotId ?? "",
                        documentId ?? "",
                        schemaId ?? "",
                        presentationKind ?? "",
                        routeHint == null ? EveSurfaceRouteHint.Automatic : EveSurfaceRouteHint.FromRoute(routeHint))
                }));
            return this;
        }

        public EveSurfaceBuilder Style(string name, string value)
        {
            _styles.Add(new EveSurfaceStyleToken(name, value));
            return this;
        }

        public EveSurfaceBuilder Version(long version)
        {
            _version = version;
            return this;
        }

        public EveSurfaceBuilder UpdatedAtUtc(string updatedAtUtc)
        {
            _updatedAtUtc = updatedAtUtc ?? "";
            return this;
        }

        public EveSurfaceDocument Build()
        {
            return new EveSurfaceDocument(
                _providerId,
                _providerKind,
                _title,
                _version,
                string.IsNullOrWhiteSpace(_updatedAtUtc) ? DateTime.UtcNow.ToString("O") : _updatedAtUtc,
                new EveSurfaceTree(
                    _surfaceId,
                    new EveSurfaceComponent(
                        $"{_surfaceId}.root",
                        "surface",
                        EmptyProps(),
                        _children.ToArray()),
                    _styles.ToArray()),
                _commands.ToArray());
        }

        private EveSurfaceBuilder Group(string id, string kind, Action<EveSurfaceGroupBuilder> build)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            var group = new EveSurfaceGroupBuilder(id, kind, AddCommand);
            build(group);
            _children.Add(group.Build());
            return this;
        }

        private void AddCommand(EveSurfaceCommandTemplate command)
        {
            if (command != null)
                _commands.Add(command);
        }

        internal static EveSurfaceComponent Text(string id, string value, string kind)
        {
            return new EveSurfaceComponent(
                id,
                kind,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["value"] = value ?? ""
                },
                Array.Empty<EveSurfaceComponent>());
        }

        internal static EveSurfaceComponent ButtonComponent(
            string id,
            string label,
            CultMeshOperationBindingDescriptor operation)
        {
            return new EveSurfaceComponent(
                id,
                "control.button",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["label"] = label ?? operation.Label,
                    ["command"] = operation.OperationId,
                    ["operationId"] = operation.OperationId,
                    ["schemaId"] = operation.SchemaId
                },
                Array.Empty<EveSurfaceComponent>());
        }

        internal static Dictionary<string, string> EmptyProps()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        internal static string Slug(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unnamed";

            var chars = value
                .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '.')
                .ToArray();
            var slug = new string(chars).Trim('.');
            while (slug.Contains("..", StringComparison.Ordinal))
                slug = slug.Replace("..", ".", StringComparison.Ordinal);
            return string.IsNullOrWhiteSpace(slug) ? "unnamed" : slug;
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    public sealed class EveSurfaceGroupBuilder
    {
        private readonly string _id;
        private readonly string _kind;
        private readonly Action<EveSurfaceCommandTemplate> _addCommand;
        private readonly List<EveSurfaceComponent> _children = new();

        internal EveSurfaceGroupBuilder(
            string id,
            string kind,
            Action<EveSurfaceCommandTemplate> addCommand)
        {
            _id = id ?? "";
            _kind = kind ?? "column";
            _addCommand = addCommand ?? throw new ArgumentNullException(nameof(addCommand));
        }

        public EveSurfaceGroupBuilder Button(string label, string command)
        {
            var operation = CultMesh.OperationBinding(command, label);
            _addCommand(new EveSurfaceCommandTemplate(operation));
            _children.Add(EveSurfaceBuilder.ButtonComponent($"{_id}.button.{EveSurfaceBuilder.Slug(label)}", label, operation));
            return this;
        }

        public EveSurfaceGroupBuilder Button(string label, CultMeshOperationBindingDescriptor operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            _addCommand(new EveSurfaceCommandTemplate(operation));
            _children.Add(EveSurfaceBuilder.ButtonComponent($"{_id}.button.{EveSurfaceBuilder.Slug(label)}", label, operation));
            return this;
        }

        internal EveSurfaceComponent Build()
        {
            return new EveSurfaceComponent(_id, _kind, EveSurfaceBuilder.EmptyProps(), _children.ToArray());
        }
    }

    public sealed class EveSurfaceFormBuilder
    {
        private readonly string _id;
        private readonly Action<EveSurfaceCommandTemplate> _addCommand;
        private readonly List<EveSurfaceComponent> _children = new();

        internal EveSurfaceFormBuilder(string id, Action<EveSurfaceCommandTemplate> addCommand)
        {
            _id = id ?? "";
            _addCommand = addCommand ?? throw new ArgumentNullException(nameof(addCommand));
        }

        public EveSurfaceFormBuilder Text(
            string label,
            string value,
            CultMeshOperationBindingDescriptor operation,
            CultMeshStateBindingDescriptor? binding = null)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            _addCommand(new EveSurfaceCommandTemplate(operation));
            _children.Add(Control(
                "control.text",
                label,
                value,
                operation,
                binding));
            return this;
        }

        public EveSurfaceFormBuilder Toggle(
            string label,
            bool value,
            CultMeshOperationBindingDescriptor operation,
            CultMeshStateBindingDescriptor? binding = null)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            _addCommand(new EveSurfaceCommandTemplate(operation));
            _children.Add(Control(
                "control.toggle",
                label,
                value ? "true" : "false",
                operation,
                binding));
            return this;
        }

        public EveSurfaceFormBuilder Metric(
            string label,
            string value,
            CultMeshStateBindingDescriptor? binding = null)
        {
            var bindings = binding == null
                ? Array.Empty<EveSurfaceStateBinding>()
                : new[] { EveSurfaceStateBinding.FromDescriptor(binding) };
            _children.Add(new EveSurfaceComponent(
                $"{_id}.metric.{EveSurfaceBuilder.Slug(label)}",
                "metric",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["label"] = label ?? "",
                    ["value"] = value ?? ""
                },
                Array.Empty<EveSurfaceComponent>(),
                bindings));
            return this;
        }

        internal EveSurfaceComponent Build()
        {
            return new EveSurfaceComponent(_id, "form", EveSurfaceBuilder.EmptyProps(), _children.ToArray());
        }

        private EveSurfaceComponent Control(
            string kind,
            string label,
            string value,
            CultMeshOperationBindingDescriptor operation,
            CultMeshStateBindingDescriptor? binding)
        {
            var bindings = binding == null
                ? Array.Empty<EveSurfaceStateBinding>()
                : new[] { EveSurfaceStateBinding.FromDescriptor(binding) };
            return new EveSurfaceComponent(
                $"{_id}.{EveSurfaceBuilder.Slug(label)}",
                kind,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["label"] = label ?? "",
                    ["value"] = value ?? "",
                    ["command"] = operation.OperationId,
                    ["operationId"] = operation.OperationId,
                    ["schemaId"] = operation.SchemaId
                },
                Array.Empty<EveSurfaceComponent>(),
                bindings);
        }
    }

    public static class EveSurface
    {
        public static EveSurfaceBuilder Create(string surfaceId)
        {
            return new EveSurfaceBuilder(surfaceId);
        }
    }
}
