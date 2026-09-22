using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Optional metadata applied when creating CultNet document messages.
    /// </summary>
    public sealed class CultNetDocumentMessageOptions
    {
        /// <summary>
        /// Gets or sets an explicit storage timestamp.
        /// </summary>
        public string? StoredAt { get; set; }
        /// <summary>
        /// Gets or sets the runtime that produced the document.
        /// </summary>
        public string? SourceRuntimeId { get; set; }
        /// <summary>
        /// Gets or sets the agent that produced the document.
        /// </summary>
        public string? SourceAgentId { get; set; }
        /// <summary>
        /// Gets or sets the role that produced the document.
        /// </summary>
        public string? SourceRole { get; set; }
        /// <summary>
        /// Gets or sets optional document tags.
        /// </summary>
        public string[]? Tags { get; set; }
    }

    /// <summary>
    /// Binds a CultCache document type to CultNet payload serialization.
    /// </summary>
    public sealed class CultNetDocumentBinding
    {
        internal CultNetDocumentBinding(
            Type documentType,
            string schemaId,
            Func<object, byte[]> payloadSerializer,
            Func<byte[], object> payloadDeserializer)
        {
            DocumentType = documentType;
            SchemaId = schemaId;
            PayloadSerializer = payloadSerializer;
            PayloadDeserializer = payloadDeserializer;
        }

        /// <summary>
        /// Gets the bound document type.
        /// </summary>
        public Type DocumentType { get; }
        /// <summary>
        /// Gets the bound schema identifier.
        /// </summary>
        public string SchemaId { get; }
        /// <summary>
        /// Gets the payload serializer.
        /// </summary>
        public Func<object, byte[]> PayloadSerializer { get; }
        /// <summary>
        /// Gets the payload deserializer.
        /// </summary>
        public Func<byte[], object> PayloadDeserializer { get; }

        /// <summary>
        /// Creates a document binding for a typed CultCache document.
        /// </summary>
        public static CultNetDocumentBinding ForDocument<T>(
            CultDocumentRegistry? registry = null,
            string? schemaId = null,
            Func<T, byte[]>? payloadSerializer = null,
            Func<byte[], T>? payloadDeserializer = null)
            where T : class
        {
            var documents = registry ?? CultDocumentRegistry.Shared;
            var descriptor = documents.GetRequired<T>();
            return new CultNetDocumentBinding(
                typeof(T),
                string.IsNullOrWhiteSpace(schemaId) ? descriptor.SchemaId : schemaId!,
                document =>
                {
                    var typed = (T)document;
                    return payloadSerializer != null
                        ? payloadSerializer(typed)
                        : CultDocumentMessagePackSerialization.SerializeUntyped(typed, typeof(T), documents);
                },
                payload => payloadDeserializer != null
                    ? payloadDeserializer(payload)
                    : (T)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(T), payload, documents));
        }

        /// <summary>
        /// Creates a document binding for a CultCache document type discovered at runtime.
        /// </summary>
        public static CultNetDocumentBinding ForDocument(
            Type documentType,
            CultDocumentRegistry? registry = null,
            string? schemaId = null,
            Func<object, byte[]>? payloadSerializer = null,
            Func<byte[], object>? payloadDeserializer = null)
        {
            if (documentType == null)
            {
                throw new ArgumentNullException(nameof(documentType));
            }

            var documents = registry ?? CultDocumentRegistry.Shared;
            var descriptor = documents.GetRequired(documentType);
            return new CultNetDocumentBinding(
                documentType,
                string.IsNullOrWhiteSpace(schemaId) ? descriptor.SchemaId : schemaId!,
                document =>
                {
                    if (!documentType.IsInstanceOfType(document))
                    {
                        throw new InvalidOperationException(
                            $"Document payload type {document.GetType().FullName} is not assignable to {documentType.FullName}.");
                    }

                    return payloadSerializer != null
                        ? payloadSerializer(document)
                        : CultDocumentMessagePackSerialization.SerializeUntyped(document, documentType, documents);
                },
                payload => payloadDeserializer != null
                    ? payloadDeserializer(payload)
                    : CultDocumentMessagePackSerialization.DeserializeUntyped(documentType, payload, documents));
        }
    }

    /// <summary>
    /// Creates and applies CultNet messages that replicate CultCache documents.
    /// </summary>
    public sealed class CultNetDocumentRegistry
    {
        private readonly CultDocumentRegistry _documents;
        private readonly Dictionary<string, CultNetDocumentBinding> _bindingsBySchemaId =
            new(StringComparer.Ordinal);
        private readonly Dictionary<Type, CultNetDocumentBinding> _bindingsByType = new();

        /// <summary>
        /// Creates a document registry with optional bindings.
        /// </summary>
        public CultNetDocumentRegistry(
            CultDocumentRegistry? documents = null,
            IEnumerable<CultNetDocumentBinding>? bindings = null)
        {
            _documents = documents ?? CultDocumentRegistry.Shared;
            if (bindings == null)
            {
                return;
            }

            foreach (var binding in bindings)
            {
                Register(binding);
            }
        }

        /// <summary>
        /// Registers a document binding.
        /// </summary>
        public CultNetDocumentRegistry Register(CultNetDocumentBinding binding)
        {
            _bindingsBySchemaId[binding.SchemaId] = binding;
            _bindingsByType[binding.DocumentType] = binding;
            return this;
        }

        /// <summary>
        /// Gets a binding by schema identifier.
        /// </summary>
        public CultNetDocumentBinding? GetBySchemaId(string schemaId)
        {
            return _bindingsBySchemaId.TryGetValue(schemaId, out var binding) ? binding : null;
        }

        /// <summary>
        /// Gets a binding by CLR document type.
        /// </summary>
        public CultNetDocumentBinding? GetByDocumentType(Type documentType)
        {
            return _bindingsByType.TryGetValue(documentType, out var binding) ? binding : null;
        }

        /// <summary>
        /// Creates a document delete message.
        /// </summary>
        public CultNetDocumentDeleteMessage CreateDocumentDeleteMessage(
            string messageId,
            string schemaId,
            string recordKey)
        {
            return new CultNetDocumentDeleteMessage
            {
                MessageId = RequireNonEmpty(messageId, nameof(messageId)),
                SchemaId = RequireNonEmpty(schemaId, nameof(schemaId)),
                RecordKey = RequireNonEmpty(recordKey, nameof(recordKey))
            };
        }

        /// <summary>
        /// Creates a raw document put message for a typed document.
        /// </summary>
        public CultNetDocumentPutRawMessage CreateRawDocumentPutMessage<T>(
            string messageId,
            CultRecordHandle<T> handle,
            T document,
            CultNetDocumentMessageOptions? options = null)
            where T : class
        {
            var descriptor = _documents.GetRequired<T>();
            var binding = GetByDocumentType(typeof(T)) ?? CultNetDocumentBinding.ForDocument<T>(_documents);
            var storedAt = ResolveStoredAt(options);

            return new CultNetDocumentPutRawMessage
            {
                MessageId = RequireNonEmpty(messageId, nameof(messageId)),
                Document = new CultNetRawDocumentRecord
                {
                    SchemaId = binding.SchemaId,
                    SchemaName = descriptor.SchemaName,
                    SchemaVersion = descriptor.SchemaVersion,
                    SchemaContentHash = descriptor.ContentHash,
                    RecordKey = handle.Key.Value,
                    StoredAt = storedAt,
                    PayloadEncoding = "messagepack",
                    Payload = binding.PayloadSerializer(document),
                    SourceRuntimeId = options?.SourceRuntimeId,
                    SourceAgentId = options?.SourceAgentId,
                    SourceRole = options?.SourceRole,
                    Tags = options?.Tags
                }
            };
        }

        /// <summary>
        /// Creates a snapshot request message.
        /// </summary>
        public CultNetSnapshotRequestMessage CreateSnapshotRequest(
            string messageId,
            IEnumerable<string>? schemaIds = null,
            IEnumerable<string>? recordKeys = null)
        {
            return new CultNetSnapshotRequestMessage
            {
                MessageId = RequireNonEmpty(messageId, nameof(messageId)),
                SchemaIds = schemaIds?.ToArray(),
                RecordKeys = recordKeys?.ToArray()
            };
        }

        /// <summary>
        /// Creates a typed-selection snapshot request message (docs/cultnet-selection-cut.md).
        /// </summary>
        public CultNetSnapshotRequestV1Message CreateSnapshotRequestV1(string messageId, CultNetSelection selection)
        {
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            return new CultNetSnapshotRequestV1Message
            {
                MessageId = RequireNonEmpty(messageId, nameof(messageId)),
                Selection = selection
            };
        }

        /// <summary>
        /// Creates a raw snapshot response from the cache. v0 has no selector engine of its own
        /// (docs/cultnet-selection-cut.md, D3/D4): the filter lowers into a <see cref="CultNetSelection"/>
        /// and is answered by <see cref="CultNetSelectionEvaluator"/> in one evaluation (R-G), so a v0
        /// caller sees its whole matching set in one message without re-filtering the cache per page.
        /// </summary>
        public CultNetSnapshotResponseRawMessage CreateRawSnapshotResponse(
            CultCache cache,
            string messageId,
            CultNetSnapshotRequestMessage? filter = null,
            CultNetDocumentMessageOptions? options = null)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            var lowSchemas = CultNetV0SelectionLowering.Lower(filter?.SchemaIds);
            var lowKeys = CultNetV0SelectionLowering.Lower(filter?.RecordKeys);

            // R-R: an explicit empty schemas or keys list is v0's own "answer nothing" - it must not
            // reach the v1 door, which refuses an empty selection.schemas/keys outright
            // (CultNetSelectionValidation.ValidateShape). v0 and v1 disagree about what an empty list
            // means, so v0 decides this before the v1 evaluator ever sees the selection.
            if (lowSchemas is { Length: 0 } || lowKeys is { Length: 0 })
            {
                return new CultNetSnapshotResponseRawMessage
                {
                    MessageId = RequireNonEmpty(messageId, nameof(messageId)),
                    Documents = Array.Empty<CultNetRawDocumentRecord>()
                };
            }

            var selection = new CultNetSelection
            {
                Schemas = lowSchemas,
                Keys = lowKeys,
                Projection = CultNetSelectionProjections.Document
            };

            // R-R: v0's pre-cut CreateRawSnapshotResponse (b3d9cf7) iterated the requested record keys
            // in the order the caller sent them (a HashSet<string> built from the array, enumerated in
            // insertion order). The evaluator's own tiebreak (schema then key) only applies when the
            // caller did not name keys.
            Func<string, CultRecordKey, long> ordinalOf;
            if (lowKeys is { Length: > 0 })
            {
                var order = new Dictionary<string, long>(StringComparer.Ordinal);
                for (var i = 0; i < lowKeys.Length; i++)
                    order.TryAdd(lowKeys[i], i);
                ordinalOf = (_, key) => order.TryGetValue(key.Value, out var index) ? index : lowKeys.Length;
            }
            else
            {
                ordinalOf = static (_, _) => 0;
            }

            var page = SelectAll(cache, selection, ordinalOf, asOf: 0, options);

            return new CultNetSnapshotResponseRawMessage
            {
                MessageId = RequireNonEmpty(messageId, nameof(messageId)),
                Documents = page.Documents ?? Array.Empty<CultNetRawDocumentRecord>()
            };
        }

        /// <summary>
        /// Evaluates a typed selection against the cache and returns its page
        /// (docs/cultnet-selection-cut.md, section 2/6). <paramref name="ordinalOf"/> supplies each
        /// row's ordinal (the reference: <see cref="CultNetDatabase.LastWriteSequence"/>); <paramref name="asOf"/>
        /// is the snapshot the page is exact for.
        /// </summary>
        public CultNetSnapshotResponseRawV1Message CreateSelectionResponse(
            CultCache cache,
            string messageId,
            CultNetSelection selection,
            Func<string, CultRecordKey, long> ordinalOf,
            ulong asOf,
            CultNetDocumentMessageOptions? options = null)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            if (ordinalOf == null) throw new ArgumentNullException(nameof(ordinalOf));

            var page = SelectPage(cache, selection, ordinalOf, asOf, options);
            return new CultNetSnapshotResponseRawV1Message
            {
                MessageId = RequireNonEmpty(messageId, nameof(messageId)),
                Matched = page.Matched,
                AsOf = page.AsOf,
                Next = page.Next,
                Headers = page.Headers,
                Documents = page.Documents,
                Edges = page.Edges
            };
        }

        /// <summary>
        /// Evaluates a selection over every row the cache holds and projects the matched page to wire
        /// records. The one evaluator (<see cref="CultNetSelectionEvaluator"/>) owns matching, order,
        /// the hop and the cursor; this owns turning its result into <see cref="CultNetRawDocumentRecord"/>s.
        /// </summary>
        internal CultNetSelectionPage SelectPage(
            CultCache cache,
            CultNetSelection selection,
            Func<string, CultRecordKey, long> ordinalOf,
            ulong asOf,
            CultNetDocumentMessageOptions? options,
            Func<CultDocumentDescriptor, CultRecordKey, bool>? rowFilter = null)
        {
            selection = ExpandSchemaBindingAliases(selection);
            // R-F: CultNetSelectionEvaluator.Select validates first, every time - there is no separate
            // validate flag here any more, because a public evaluation path that can skip the door is
            // exactly what R-F closes.

            var stored = cache.AllStoredDocuments;
            if (rowFilter != null)
                stored = stored.Where(entry => rowFilter(entry.Descriptor, entry.Key));
            var rows = stored
                .Select(entry => new CultNetSelectionEvaluator.Row(
                    entry.Descriptor, entry.Key, entry.Document, ordinalOf(entry.Descriptor.SchemaId, entry.Key), entry.StoredAt))
                .ToArray();
            var evaluation = CultNetSelectionEvaluator.Select(_documents, rows, selection, asOf);
            var records = evaluation.Rows.Select(row => ToRawRecord(row, options)).ToArray();
            var wantDocument = selection.Projection == CultNetSelectionProjections.Document;

            return new CultNetSelectionPage
            {
                // R-G/C5: the selection's whole matching count, not this page's row count.
                Matched = (uint)evaluation.TotalMatched,
                AsOf = asOf,
                Next = evaluation.NextCursor,
                Documents = wantDocument ? records : null,
                Headers = wantDocument ? null : records.Select(CultNetRawDocumentHeader.FromRecord).ToArray(),
                Edges = selection.HasHop ? evaluation.Edges.Select(edge => ToEdge(edge, wantDocument)).ToArray() : null
            };
        }

        /// <summary>
        /// Evaluates a selection over every row the cache holds and projects every matched row - no
        /// cursor, no 200-row cap. The v0 lowering and the shard-bounded snapshot both answer their
        /// whole matching set in one message, so they call this once instead of paging through
        /// <see cref="SelectPage"/> in a loop that re-filters and re-sorts the same row set per page
        /// (R-G: "answer v0 and shard paging from one evaluation").
        /// </summary>
        internal CultNetSelectionPage SelectAll(
            CultCache cache,
            CultNetSelection selection,
            Func<string, CultRecordKey, long> ordinalOf,
            ulong asOf,
            CultNetDocumentMessageOptions? options,
            Func<CultDocumentDescriptor, CultRecordKey, bool>? rowFilter = null)
        {
            selection = ExpandSchemaBindingAliases(selection);

            var stored = cache.AllStoredDocuments;
            if (rowFilter != null)
                stored = stored.Where(entry => rowFilter(entry.Descriptor, entry.Key));
            var rows = stored
                .Select(entry => new CultNetSelectionEvaluator.Row(
                    entry.Descriptor, entry.Key, entry.Document, ordinalOf(entry.Descriptor.SchemaId, entry.Key), entry.StoredAt))
                .ToArray();

            var full = CultNetSelectionEvaluator.EvaluateAll(_documents, rows, selection);
            var records = full.Ordered.Select(row => ToRawRecord(row, options)).ToArray();
            var wantDocument = selection.Projection == CultNetSelectionProjections.Document;
            var edges = selection.HasHop
                ? CultNetSelectionEvaluator.EdgesFor(full.Ordered, full).Select(edge => ToEdge(edge, wantDocument)).ToArray()
                : null;

            return new CultNetSelectionPage
            {
                Matched = (uint)full.Ordered.Count,
                AsOf = asOf,
                Next = null,
                Documents = wantDocument ? records : null,
                Headers = wantDocument ? null : records.Select(CultNetRawDocumentHeader.FromRecord).ToArray(),
                Edges = edges
            };
        }

        // A CultDocumentDescriptor knows nothing of CultNetDocumentBinding's optional schema-id
        // override; a caller filtering by that wire id (rather than the descriptor's own) needs the
        // descriptor's own id present in the selection too, since CultNetSelectionEvaluator only ever
        // reads descriptors. R-M: this is the one copy - both CultNetDatabaseServer's and
        // CultNetDatabaseSubscriptionServer's single-change fast path call this too instead of carrying
        // their own CultNetSchemaAliasMatching.WithBindingSchemaAlias (deleted).
        internal CultNetSelection ExpandSchemaBindingAliases(CultNetSelection selection)
        {
            if (selection.Schemas is not { Length: > 0 } || _bindingsByType.Count == 0)
                return selection;

            List<string>? expanded = null;
            foreach (var binding in _bindingsByType.Values)
            {
                if (!selection.Schemas.Contains(binding.SchemaId, StringComparer.Ordinal))
                    continue;
                var descriptor = _documents.GetRequired(binding.DocumentType);
                if (selection.Schemas.Contains(descriptor.SchemaId, StringComparer.Ordinal))
                    continue;
                expanded ??= new List<string>(selection.Schemas);
                if (!expanded.Contains(descriptor.SchemaId, StringComparer.Ordinal))
                    expanded.Add(descriptor.SchemaId);
            }

            if (expanded == null)
                return selection;

            return new CultNetSelection
            {
                Schemas = expanded.ToArray(),
                Keys = selection.Keys,
                Fields = selection.Fields,
                Cites = selection.Cites,
                Cited = selection.Cited,
                Projection = selection.Projection,
                Descending = selection.Descending,
                Limit = selection.Limit,
                Cursor = selection.Cursor
            };
        }

        /// <summary>
        /// Builds the wire record for one evaluated row. The shared path under snapshot and change
        /// (docs/cultnet-selection-cut.md, section 8's "shared paths").
        /// </summary>
        internal CultNetRawDocumentRecord ToRawRecord(CultNetSelectionEvaluator.Row row, CultNetDocumentMessageOptions? options = null) =>
            ToRawRecord(row.Descriptor, row.Key, row.Document, string.IsNullOrEmpty(row.StoredAt) ? ResolveStoredAt(options) : row.StoredAt, options);

        internal CultNetRawDocumentRecord ToRawRecord(
            CultDocumentDescriptor descriptor,
            CultRecordKey key,
            object document,
            string storedAt,
            CultNetDocumentMessageOptions? options = null)
        {
            var binding = GetByDocumentType(descriptor.DocumentType) ??
                          new CultNetDocumentBinding(
                              descriptor.DocumentType,
                              descriptor.SchemaId,
                              value => CultDocumentMessagePackSerialization.SerializeUntyped(value, value.GetType(), _documents),
                              payload => CultDocumentMessagePackSerialization.DeserializeUntyped(descriptor.DocumentType, payload, _documents));

            return new CultNetRawDocumentRecord
            {
                SchemaId = binding.SchemaId,
                SchemaName = descriptor.SchemaName,
                SchemaVersion = descriptor.SchemaVersion,
                SchemaContentHash = descriptor.ContentHash,
                RecordKey = key.Value,
                StoredAt = storedAt,
                PayloadEncoding = "messagepack",
                Payload = binding.PayloadSerializer(document),
                SourceRuntimeId = options?.SourceRuntimeId,
                SourceAgentId = options?.SourceAgentId,
                SourceRole = options?.SourceRole,
                Tags = options?.Tags
            };
        }

        private CultNetEdge ToEdge(CultNetSelectionEvaluator.EdgeMatch edge, bool wantDocument)
        {
            // R-E: ToRawRecord and ToEdge emit the same id for the same row - the wire schema id a
            // binding overrides to (WireSchemaId), never the descriptor's own id when the two differ.
            var wire = new CultNetEdge
            {
                From = new CultNetRecordRef { SchemaId = WireSchemaId(edge.From.Descriptor), RecordKey = edge.From.Key.Value },
                Role = edge.Role,
                To = new CultNetRecordRef { SchemaId = WireSchemaId(edge.To.Descriptor), RecordKey = edge.To.Key.Value }
            };
            if (wantDocument && edge.Payload != null)
            {
                wire.PayloadEncoding = "messagepack";
                // A dictionary reference's payload is typed by the member's declared value type (D11) -
                // routinely a plain value like float, not a registered CultDocument - so this serializes
                // it directly rather than through CultDocumentMessagePackSerialization.SerializeUntyped,
                // which requires the type to be a registered document and throws otherwise.
                wire.Payload = MessagePackSerializer.Serialize(edge.Payload.GetType(), edge.Payload, CultNetSchemaMessageSerialization.Options);
            }

            return wire;
        }

        /// <summary>
        /// The wire schema id this registry answers with for a descriptor - its binding's override when
        /// one is registered, else the descriptor's own id (R-E: the same id
        /// <see cref="ToRawRecord(CultNetSelectionEvaluator.Row, CultNetDocumentMessageOptions?)"/> emits).
        /// Internal rather than private (R-P): one id reaches the authorizer, and it is this one, on
        /// every path - CultNetDatabaseSubscriptionServer's live path needs it too.
        /// </summary>
        internal string WireSchemaId(CultDocumentDescriptor descriptor) =>
            GetByDocumentType(descriptor.DocumentType)?.SchemaId ?? descriptor.SchemaId;

        /// <summary>
        /// Applies a raw document put message to a cache.
        /// </summary>
        public async Task<object> ApplyRawDocumentPutMessageAsync(
            CultCache cache,
            CultNetDocumentPutRawMessage message)
        {
            if (message.Document == null)
            {
                throw new ArgumentException("CultNet raw document message is missing its document payload.", nameof(message));
            }

            var document = DeserializeRawDocument(message.Document);
            var descriptor = ResolveDescriptorForRawDocument(message.Document);

            var addMethod = typeof(CultCache).GetMethod(nameof(CultCache.AddAsync))!
                .MakeGenericMethod(descriptor.DocumentType);
            var handleType = typeof(CultRecordHandle<>).MakeGenericType(descriptor.DocumentType);
            var optionalHandle = Activator.CreateInstance(typeof(Nullable<>).MakeGenericType(handleType), new object[] { Activator.CreateInstance(handleType, new object[] { new CultRecordKey(message.Document.RecordKey) })! });
            var task = (Task)addMethod.Invoke(cache, [document, optionalHandle])!;
            await task.ConfigureAwait(false);
            return document;
        }

        /// <summary>
        /// Decodes one validated raw document without materializing it in a CultCache replica.
        /// Use this for ephemeral reactive subscriptions whose callback is the consumer.
        /// </summary>
        public object DeserializeRawDocument(CultNetRawDocumentRecord document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            ValidateRawDocumentRecord(document);
            var binding = GetBySchemaId(document.SchemaId);
            var descriptor = binding != null
                ? _documents.GetRequired(binding.DocumentType)
                : ResolveDescriptorForRawDocument(document);
            binding ??= new CultNetDocumentBinding(
                descriptor.DocumentType,
                descriptor.SchemaId,
                value => CultDocumentMessagePackSerialization.SerializeUntyped(value, value.GetType()),
                payload => CultDocumentMessagePackSerialization.DeserializeUntyped(descriptor.DocumentType, payload));
            return binding.PayloadDeserializer(document.Payload);
        }

        /// <summary>
        /// Applies a raw document put message and returns the typed document.
        /// </summary>
        public async Task<T> ApplyRawDocumentPutMessageAsync<T>(
            CultCache cache,
            CultNetDocumentPutRawMessage message)
            where T : class
        {
            return (T)await ApplyRawDocumentPutMessageAsync(cache, message).ConfigureAwait(false);
        }

        /// <summary>
        /// Applies all documents from a raw snapshot response.
        /// </summary>
        public async Task<IReadOnlyList<object>> ApplyRawSnapshotResponseAsync(
            CultCache cache,
            CultNetSnapshotResponseRawMessage response)
        {
            var applied = new List<object>(response.Documents.Length);
            foreach (var document in response.Documents)
            {
                applied.Add(await ApplyRawDocumentPutMessageAsync(
                    cache,
                    new CultNetDocumentPutRawMessage
                    {
                        MessageId = response.MessageId,
                        Document = document
                    }).ConfigureAwait(false));
            }

            return applied;
        }

        internal CultDocumentDescriptor ResolveDescriptorForRawDocument(CultNetRawDocumentRecord document)
        {
            try
            {
                return ResolveDescriptorForSchemaId(document.SchemaId);
            }
            catch (InvalidOperationException) when (TryResolveDescriptorByPayloadSchema(document.Payload) is { } descriptor)
            {
                return descriptor;
            }
        }

        internal CultDocumentDescriptor ResolveDescriptorForSchemaId(string schemaId)
        {
            try
            {
                return _documents.GetRequiredBySchemaId(schemaId);
            }
            catch (InvalidOperationException) when (TryResolveDescriptorBySchemaAlias(schemaId) is { } descriptor)
            {
                return descriptor;
            }
        }

        private CultDocumentDescriptor? TryResolveDescriptorBySchemaAlias(string schemaId)
        {
            if (string.IsNullOrWhiteSpace(schemaId))
                return null;

            var candidates = ResolvePayloadSchemaCandidates().ToArray();
            var descriptor = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.SchemaId, schemaId, StringComparison.Ordinal) ||
                string.Equals(candidate.SchemaName, schemaId, StringComparison.Ordinal) ||
                string.Equals(candidate.SchemaVersion, schemaId, StringComparison.Ordinal) ||
                candidate.ToCatalogEntry().CompatibleSchemaIds.Any(compatible =>
                    string.Equals(compatible, schemaId, StringComparison.Ordinal)));
            if (descriptor != null)
                return descriptor;

            var schemaName = InferSchemaName(schemaId);
            return string.IsNullOrWhiteSpace(schemaName)
                ? null
                : candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.SchemaName, schemaName, StringComparison.Ordinal));
        }

        /// <summary>
        /// Resolves the descriptor this registry's own bindings (or every registered descriptor, when
        /// none are bound) associate with a raw payload's embedded schema version, when exactly one
        /// registered type owns that schema string. Used by <see cref="ResolveDescriptorForRawDocument"/>
        /// to recover a document's type from a foreign/runtime-generated schema id. Ambiguous - and
        /// so not the right tool - when the caller already knows the target descriptor and two
        /// registered types alias the same schema id: match <see cref="TryReadSchemaVersion"/>'s
        /// result against that descriptor with <see cref="CultNetSchemaAliasMatching"/> directly
        /// instead (docs/cultnet-selection-cut.md, section 4/S2-5; this is what CultMesh's
        /// foreign-schema decode does). Trusts the payload's own schema stamp; it does not re-run a
        /// selection or re-check authorization.
        /// </summary>
        public CultDocumentDescriptor? TryResolveDescriptorByPayloadSchema(byte[] payload)
        {
            var schemaVersion = TryReadSchemaVersion(payload);
            if (string.IsNullOrWhiteSpace(schemaVersion))
                return null;

            var candidates = ResolvePayloadSchemaCandidates().ToArray();
            var descriptor = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.SchemaVersion, schemaVersion, StringComparison.Ordinal));
            if (descriptor != null)
                return descriptor;

            var schemaName = InferSchemaName(schemaVersion!);
            return string.IsNullOrWhiteSpace(schemaName)
                ? null
                : candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.SchemaName, schemaName, StringComparison.Ordinal));
        }

        private IEnumerable<CultDocumentDescriptor> ResolvePayloadSchemaCandidates()
        {
            if (_bindingsBySchemaId.Count == 0 && _bindingsByType.Count == 0)
                return _documents.AllDescriptors;

            return _bindingsBySchemaId.Values
                .Concat(_bindingsByType.Values)
                .Select(binding => _documents.GetRequired(binding.DocumentType))
                .GroupBy(descriptor => descriptor.DocumentType)
                .Select(group => group.First());
        }

        /// <summary>
        /// True when a raw payload's embedded <c>schemaVersion</c> stamp (when it carries one) aliases
        /// the given descriptor - the "foreign schema id" fallback shared by CultMesh's peer-snapshot
        /// read and its typed-document decode (docs/cultnet-selection-cut.md, R-L: one registry method
        /// taking the descriptor, replacing two call sites that each read the payload and alias-matched
        /// it by hand). <see cref="TryReadSchemaVersion"/> stays internal: a caller with an already-known
        /// descriptor never needs the raw schema-version string itself, only this answer.
        /// </summary>
        public static bool PayloadMatchesSchema(byte[] payload, CultDocumentDescriptor descriptor) =>
            TryReadSchemaVersion(payload) is { } schemaVersion &&
            CultNetSchemaAliasMatching.Matches(schemaVersion, descriptor);

        /// <summary>
        /// Reads a raw payload's embedded <c>schemaVersion</c> stamp, when it carries one - the parse
        /// step of the registry's payload-schema decode rule, shared with
        /// <see cref="TryResolveDescriptorByPayloadSchema"/> and <see cref="PayloadMatchesSchema"/>
        /// (docs/cultnet-selection-cut.md, section 4/S2-5, R-L). Internal: a caller outside this
        /// assembly matching against one already-known descriptor uses <see cref="PayloadMatchesSchema"/>;
        /// resolving "whichever registered type owns this schema string" (ambiguous when two document
        /// types alias the same schema id) is <see cref="TryResolveDescriptorByPayloadSchema"/>'s job.
        /// </summary>
        internal static string? TryReadSchemaVersion(byte[] payload)
        {
            try
            {
                var array = MessagePackSerializer.Deserialize<object[]>(
                    payload,
                    CultNetSchemaMessageSerialization.Options);
                if (array.Length > 0 && array[0] is string schemaVersion)
                    return schemaVersion;
            }
            catch (Exception)
            {
                // Fall through to map decoding; different runtimes may encode object-like payloads.
            }

            try
            {
                var map = MessagePackSerializer.Deserialize<IReadOnlyDictionary<string, object?>>(
                    payload,
                    CultNetSchemaMessageSerialization.Options);
                if (map.TryGetValue("schemaVersion", out var schemaVersion) &&
                    schemaVersion is string schemaVersionText)
                    return schemaVersionText;
                if (map.TryGetValue("schema_version", out var snakeSchemaVersion) &&
                    snakeSchemaVersion is string snakeSchemaVersionText)
                    return snakeSchemaVersionText;
            }
            catch (Exception)
            {
                return null;
            }

            return null;
        }

        private static string? InferSchemaName(string schemaVersion)
        {
            var marker = schemaVersion.LastIndexOf(".v", StringComparison.Ordinal);
            if (marker <= 0 || marker + 2 >= schemaVersion.Length)
                return null;

            var version = schemaVersion.Substring(marker + 2);
            return version.All(char.IsDigit)
                ? schemaVersion.Substring(0, marker)
                : null;
        }

        /// <summary>
        /// Applies all typed documents from a raw snapshot response.
        /// </summary>
        public async Task<IReadOnlyList<T>> ApplyRawSnapshotResponseAsync<T>(
            CultCache cache,
            CultNetSnapshotResponseRawMessage response)
            where T : class
        {
            return (await ApplyRawSnapshotResponseAsync(cache, response).ConfigureAwait(false)).OfType<T>().ToArray();
        }

        private static void ValidateRawDocumentRecord(CultNetRawDocumentRecord document)
        {
            if (document.PayloadEncoding != "messagepack")
            {
                throw new InvalidOperationException(
                    $"CultNet raw document payloadEncoding must be \"messagepack\", not \"{document.PayloadEncoding}\".");
            }

            RequireNonEmpty(document.SchemaId, nameof(document.SchemaId));
            RequireNonEmpty(document.RecordKey, nameof(document.RecordKey));
            RequireNonEmpty(document.StoredAt, nameof(document.StoredAt));
            if (document.Payload == null || document.Payload.Length == 0)
            {
                throw new InvalidOperationException("CultNet raw document payload must be non-empty.");
            }
        }

        private static string ResolveStoredAt(CultNetDocumentMessageOptions? options)
        {
            return !string.IsNullOrWhiteSpace(options?.StoredAt)
                ? options!.StoredAt!
                : DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value must be non-empty.", paramName);
            }

            return value;
        }
    }
}
