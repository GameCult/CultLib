using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;

namespace GameCult.Networking
{
    public sealed class CultNetDocumentMessageOptions
    {
        public string? StoredAt { get; set; }
        public string? SourceRuntimeId { get; set; }
        public string? SourceAgentId { get; set; }
        public string? SourceRole { get; set; }
        public string[]? Tags { get; set; }
    }

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

        public Type DocumentType { get; }
        public string SchemaId { get; }
        public Func<object, byte[]> PayloadSerializer { get; }
        public Func<byte[], object> PayloadDeserializer { get; }

        public static CultNetDocumentBinding ForDocument<T>(
            CultDocumentRegistry? registry = null,
            Func<T, byte[]>? payloadSerializer = null,
            Func<byte[], T>? payloadDeserializer = null)
            where T : class
        {
            var descriptor = (registry ?? CultDocumentRegistry.Shared).GetRequired<T>();
            return new CultNetDocumentBinding(
                typeof(T),
                descriptor.SchemaId,
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
    }

    public sealed class CultNetDocumentRegistry
    {
        private readonly CultDocumentRegistry _documents;
        private readonly Dictionary<string, CultNetDocumentBinding> _bindingsBySchemaId =
            new(StringComparer.Ordinal);
        private readonly Dictionary<Type, CultNetDocumentBinding> _bindingsByType = new();

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

        public CultNetDocumentRegistry Register(CultNetDocumentBinding binding)
        {
            _bindingsBySchemaId[binding.SchemaId] = binding;
            _bindingsByType[binding.DocumentType] = binding;
            return this;
        }

        public CultNetDocumentBinding? GetBySchemaId(string schemaId)
        {
            return _bindingsBySchemaId.TryGetValue(schemaId, out var binding) ? binding : null;
        }

        public CultNetDocumentBinding? GetByDocumentType(Type documentType)
        {
            return _bindingsByType.TryGetValue(documentType, out var binding) ? binding : null;
        }

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
                    SchemaId = descriptor.SchemaId,
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
                if (requestedSchemaIds != null && !requestedSchemaIds.Contains(descriptor.SchemaId))
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

                var binding = GetByDocumentType(document.GetType()) ??
                              new CultNetDocumentBinding(
                                  document.GetType(),
                                  descriptor.SchemaId,
                                  value => CultDocumentMessagePackSerialization.SerializeUntyped(value, value.GetType()),
                                  payload => CultDocumentMessagePackSerialization.DeserializeUntyped(document.GetType(), payload));

                documents.Add(new CultNetRawDocumentRecord
                {
                    SchemaId = descriptor.SchemaId,
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

        public async Task<object> ApplyRawDocumentPutMessageAsync(
            CultCache cache,
            CultNetDocumentPutRawMessage message)
        {
            if (message.Document == null)
            {
                throw new ArgumentException("CultNet raw document message is missing its document payload.", nameof(message));
            }

            ValidateRawDocumentRecord(message.Document);
            var descriptor = _documents.GetRequiredBySchemaId(message.Document.SchemaId);
            var binding = GetBySchemaId(message.Document.SchemaId) ??
                          new CultNetDocumentBinding(
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

        public async Task<T> ApplyRawDocumentPutMessageAsync<T>(
            CultCache cache,
            CultNetDocumentPutRawMessage message)
            where T : class
        {
            return (T)await ApplyRawDocumentPutMessageAsync(cache, message).ConfigureAwait(false);
        }

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
