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
            var descriptor = (registry ?? CultDocumentRegistry.Shared).GetRequired<T>();
            return new CultNetDocumentBinding(
                typeof(T),
                string.IsNullOrWhiteSpace(schemaId) ? descriptor.SchemaId : schemaId!,
                document =>
                {
                    var typed = (T)document;
                    return payloadSerializer != null
                        ? payloadSerializer(typed)
                        : CultDocumentMessagePackSerialization.Serialize(typed);
                },
                payload => payloadDeserializer != null
                    ? payloadDeserializer(payload)
                    : CultDocumentMessagePackSerialization.Deserialize<T>(payload));
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

            var descriptor = (registry ?? CultDocumentRegistry.Shared).GetRequired(documentType);
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
                        : CultDocumentMessagePackSerialization.SerializeUntyped(document, documentType);
                },
                payload => payloadDeserializer != null
                    ? payloadDeserializer(payload)
                    : CultDocumentMessagePackSerialization.DeserializeUntyped(documentType, payload));
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
        /// Creates a raw snapshot response from the cache.
        /// </summary>
        public CultNetSnapshotResponseRawMessage CreateRawSnapshotResponse(
            CultCache cache,
            string messageId,
            CultNetSnapshotRequestMessage? filter = null,
            CultNetDocumentMessageOptions? options = null)
        {
            var requestedSchemaIds = filter?.SchemaIds != null
                ? new HashSet<string>(filter.SchemaIds, StringComparer.Ordinal)
                : null;
            var requestedRecordKeys = filter?.RecordKeys != null
                ? new HashSet<string>(filter.RecordKeys, StringComparer.Ordinal)
                : null;
            var storedAt = ResolveStoredAt(options);

            var documents = new List<CultNetRawDocumentRecord>();
            foreach (var document in cache.AllEntries)
            {
                var descriptor = _documents.GetRequired(document.GetType());
                var binding = GetByDocumentType(document.GetType()) ??
                              new CultNetDocumentBinding(
                                  document.GetType(),
                                  descriptor.SchemaId,
                                  value => CultDocumentMessagePackSerialization.SerializeUntyped(value, value.GetType()),
                                  payload => CultDocumentMessagePackSerialization.DeserializeUntyped(document.GetType(), payload));

                if (requestedSchemaIds != null && !MatchesRequestedSchema(descriptor, binding, requestedSchemaIds))
                {
                    continue;
                }

                var handleMethod = typeof(CultCache)
                    .GetMethod(nameof(CultCache.TryGetHandle))!
                    .MakeGenericMethod(document.GetType());
                var handleObject = handleMethod.Invoke(cache, new[] { document });
                if (handleObject == null)
                {
                    continue;
                }

                var keyProperty = handleObject.GetType().GetProperty("Value");
                var handleValue = keyProperty?.GetValue(handleObject) ?? handleObject;
                var recordKeyProperty = handleValue.GetType().GetProperty("Key");
                var recordKey = recordKeyProperty?.GetValue(handleValue);
                var valueProperty = recordKey?.GetType().GetProperty("Value");
                var key = (string?)(valueProperty?.GetValue(recordKey)) ?? string.Empty;
                if (requestedRecordKeys != null && !requestedRecordKeys.Contains(key))
                {
                    continue;
                }

                documents.Add(new CultNetRawDocumentRecord
                {
                    SchemaId = binding.SchemaId,
                    RecordKey = key,
                    StoredAt = storedAt,
                    PayloadEncoding = "messagepack",
                    Payload = binding.PayloadSerializer(document),
                    SourceRuntimeId = options?.SourceRuntimeId,
                    SourceAgentId = options?.SourceAgentId,
                    SourceRole = options?.SourceRole,
                    Tags = options?.Tags
                });
            }

            return new CultNetSnapshotResponseRawMessage
            {
                MessageId = RequireNonEmpty(messageId, nameof(messageId)),
                Documents = documents.ToArray()
            };
        }

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

            ValidateRawDocumentRecord(message.Document);
            var binding = GetBySchemaId(message.Document.SchemaId);
            var descriptor = binding != null
                ? _documents.GetRequired(binding.DocumentType)
                : ResolveDescriptorForRawDocument(message.Document);
            binding ??= new CultNetDocumentBinding(
                descriptor.DocumentType,
                descriptor.SchemaId,
                value => CultDocumentMessagePackSerialization.SerializeUntyped(value, value.GetType()),
                payload => CultDocumentMessagePackSerialization.DeserializeUntyped(descriptor.DocumentType, payload));
            var document = binding.PayloadDeserializer(message.Document.Payload);

            var addMethod = typeof(CultCache).GetMethod(nameof(CultCache.AddAsync))!
                .MakeGenericMethod(descriptor.DocumentType);
            var handleType = typeof(CultRecordHandle<>).MakeGenericType(descriptor.DocumentType);
            var optionalHandle = Activator.CreateInstance(typeof(Nullable<>).MakeGenericType(handleType), new object[] { Activator.CreateInstance(handleType, new object[] { new CultRecordKey(message.Document.RecordKey) })! });
            var task = (Task)addMethod.Invoke(cache, [document, optionalHandle])!;
            await task.ConfigureAwait(false);
            return document;
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

        private CultDocumentDescriptor? TryResolveDescriptorByPayloadSchema(byte[] payload)
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

        private static bool MatchesRequestedSchema(
            CultDocumentDescriptor descriptor,
            CultNetDocumentBinding binding,
            ISet<string> requestedSchemaIds)
        {
            if (requestedSchemaIds.Count == 0)
                return true;

            if (requestedSchemaIds.Contains(descriptor.SchemaId) ||
                requestedSchemaIds.Contains(binding.SchemaId))
                return true;

            if (descriptor.ToCatalogEntry().CompatibleSchemaIds.Any(requestedSchemaIds.Contains))
                return true;

            return requestedSchemaIds
                .Select(InferSchemaName)
                .Where(schemaName => !string.IsNullOrWhiteSpace(schemaName))
                .Any(schemaName => string.Equals(schemaName, descriptor.SchemaName, StringComparison.Ordinal));
        }

        private static string? TryReadSchemaVersion(byte[] payload)
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
