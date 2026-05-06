using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Optional metadata attached when CultNet emits a document message.
    /// </summary>
    public sealed class CultNetDocumentMessageOptions
    {
        public string? StoredAt { get; set; }
        public string? SourceRuntimeId { get; set; }
        public string? SourceAgentId { get; set; }
        public string? SourceRole { get; set; }
        public string[]? Tags { get; set; }
    }

    /// <summary>
    /// Maps a concrete CultCache entry type to a shared CultNet document contract.
    /// </summary>
    public sealed class CultNetDocumentBinding
    {
        internal CultNetDocumentBinding(
            Type entryType,
            string documentType,
            string? payloadSchemaVersion,
            Func<DatabaseEntry, string> documentKeySelector,
            Func<DatabaseEntry, byte[]> payloadSerializer,
            Func<byte[], DatabaseEntry> payloadDeserializer)
        {
            EntryType = entryType ?? throw new ArgumentNullException(nameof(entryType));
            DocumentType = string.IsNullOrWhiteSpace(documentType)
                ? throw new ArgumentException("DocumentType must be non-empty.", nameof(documentType))
                : documentType;
            PayloadSchemaVersion = payloadSchemaVersion;
            DocumentKeySelector = documentKeySelector ?? throw new ArgumentNullException(nameof(documentKeySelector));
            PayloadSerializer = payloadSerializer ?? throw new ArgumentNullException(nameof(payloadSerializer));
            PayloadDeserializer = payloadDeserializer ?? throw new ArgumentNullException(nameof(payloadDeserializer));
        }

        public Type EntryType { get; }
        public string DocumentType { get; }
        public string? PayloadSchemaVersion { get; }
        public Func<DatabaseEntry, string> DocumentKeySelector { get; }
        public Func<DatabaseEntry, byte[]> PayloadSerializer { get; }
        public Func<byte[], DatabaseEntry> PayloadDeserializer { get; }

        public static CultNetDocumentBinding ForEntry<T>(
            string documentType,
            string? payloadSchemaVersion = null,
            Func<T, string>? documentKeySelector = null,
            Func<T, byte[]>? payloadSerializer = null,
            Func<byte[], T>? payloadDeserializer = null)
            where T : DatabaseEntry
        {
            return new CultNetDocumentBinding(
                typeof(T),
                documentType,
                payloadSchemaVersion,
                entry =>
                {
                    if (entry is not T typedEntry)
                    {
                        throw new InvalidOperationException(
                            $"Document binding for {typeof(T).Name} received {entry.GetType().Name}.");
                    }

                    return documentKeySelector != null
                        ? documentKeySelector(typedEntry)
                        : typedEntry.ID.ToString("D", CultureInfo.InvariantCulture);
                },
                entry =>
                {
                    if (entry is not T typedEntry)
                    {
                        throw new InvalidOperationException(
                            $"Document binding for {typeof(T).Name} received {entry.GetType().Name}.");
                    }

                    return payloadSerializer != null
                        ? payloadSerializer(typedEntry)
                        : CultCacheEnvelopeSerialization.SerializePayload(typedEntry);
                },
                payload => payloadDeserializer != null
                    ? payloadDeserializer(payload)
                    : CultCacheEnvelopeSerialization.DeserializePayload<T>(payload));
        }
    }

    /// <summary>
    /// Creates and applies CultNet raw document/snapshot messages for bound CultCache entry types.
    /// </summary>
    public sealed class CultNetDocumentRegistry
    {
        private readonly Dictionary<string, CultNetDocumentBinding> _bindingsByDocumentType =
            new Dictionary<string, CultNetDocumentBinding>(StringComparer.Ordinal);
        private readonly Dictionary<Type, CultNetDocumentBinding> _bindingsByEntryType =
            new Dictionary<Type, CultNetDocumentBinding>();

        public CultNetDocumentRegistry(IEnumerable<CultNetDocumentBinding>? bindings = null)
        {
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
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            _bindingsByDocumentType[binding.DocumentType] = binding;
            _bindingsByEntryType[binding.EntryType] = binding;
            return this;
        }

        public CultNetDocumentBinding? GetByDocumentType(string documentType)
        {
            if (documentType == null) throw new ArgumentNullException(nameof(documentType));
            return _bindingsByDocumentType.TryGetValue(documentType, out var binding) ? binding : null;
        }

        public CultNetDocumentBinding? GetByEntryType(Type entryType)
        {
            if (entryType == null) throw new ArgumentNullException(nameof(entryType));
            return _bindingsByEntryType.TryGetValue(entryType, out var binding) ? binding : null;
        }

        public CultNetDocumentDeleteMessage CreateDocumentDeleteMessage(
            string messageId,
            string documentType,
            string documentKey)
        {
            return new CultNetDocumentDeleteMessage
            {
                MessageId = RequireNonEmpty(messageId, nameof(messageId)),
                DocumentType = RequireNonEmpty(documentType, nameof(documentType)),
                DocumentKey = RequireNonEmpty(documentKey, nameof(documentKey))
            };
        }

        public CultNetDocumentPutRawMessage CreateRawDocumentPutMessage(
            string messageId,
            DatabaseEntry entry,
            CultNetDocumentMessageOptions? options = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var binding = RequireBinding(entry.GetType());
            var storedAt = ResolveStoredAt(options);
            var documentKey = binding.DocumentKeySelector(entry);

            return new CultNetDocumentPutRawMessage
            {
                MessageId = RequireNonEmpty(messageId, nameof(messageId)),
                Document = CreateRawDocumentRecord(binding, entry, documentKey, storedAt, options)
            };
        }

        public CultNetDocumentPutRawMessage CreateRawDocumentPutMessageFromEnvelope(
            string messageId,
            CultCacheEnvelope envelope)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            var binding = RequireBinding(envelope.Type);

            return new CultNetDocumentPutRawMessage
            {
                MessageId = RequireNonEmpty(messageId, nameof(messageId)),
                Document = new CultNetRawDocumentRecord
                {
                    DocumentType = envelope.Type,
                    DocumentKey = envelope.Key,
                    StoredAt = envelope.StoredAt,
                    PayloadSchemaVersion = binding.PayloadSchemaVersion,
                    PayloadEncoding = "messagepack",
                    Payload = envelope.Payload.ToArray()
                }
            };
        }

        public CultNetSnapshotRequestMessage CreateSnapshotRequest(
            string messageId,
            IEnumerable<string>? documentTypes = null,
            IEnumerable<string>? documentKeys = null)
        {
            return new CultNetSnapshotRequestMessage
            {
                MessageId = RequireNonEmpty(messageId, nameof(messageId)),
                DocumentTypes = documentTypes?.ToArray(),
                DocumentKeys = documentKeys?.ToArray()
            };
        }

        public CultNetSnapshotResponseRawMessage CreateRawSnapshotResponse(
            CultCache cache,
            string messageId,
            CultNetSnapshotRequestMessage? filter = null,
            CultNetDocumentMessageOptions? options = null)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));

            var requestedDocumentTypes = filter?.DocumentTypes != null
                ? new HashSet<string>(filter.DocumentTypes, StringComparer.Ordinal)
                : null;
            var requestedDocumentKeys = filter?.DocumentKeys != null
                ? new HashSet<string>(filter.DocumentKeys, StringComparer.Ordinal)
                : null;
            var storedAt = ResolveStoredAt(options);

            var documents = new List<CultNetRawDocumentRecord>();
            foreach (var entry in cache.AllEntries)
            {
                var binding = GetByEntryType(entry.GetType());
                if (binding == null)
                {
                    continue;
                }

                if (requestedDocumentTypes != null && !requestedDocumentTypes.Contains(binding.DocumentType))
                {
                    continue;
                }

                var documentKey = binding.DocumentKeySelector(entry);
                if (requestedDocumentKeys != null && !requestedDocumentKeys.Contains(documentKey))
                {
                    continue;
                }

                documents.Add(CreateRawDocumentRecord(
                    binding,
                    entry,
                    documentKey,
                    storedAt,
                    options));
            }

            return new CultNetSnapshotResponseRawMessage
            {
                MessageId = RequireNonEmpty(messageId, nameof(messageId)),
                Documents = documents.ToArray()
            };
        }

        public async Task<DatabaseEntry> ApplyRawDocumentPutMessageAsync(
            CultCache cache,
            CultNetDocumentPutRawMessage message)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (message.Document == null) throw new ArgumentException("CultNet raw document message is missing its document payload.", nameof(message));

            var binding = RequireBinding(message.Document.DocumentType);
            ValidateRawDocumentRecord(message.Document);

            var entry = binding.PayloadDeserializer(message.Document.Payload);
            if (!binding.EntryType.IsInstanceOfType(entry))
            {
                throw new InvalidOperationException(
                    $"CultNet raw document type \"{message.Document.DocumentType}\" expected {binding.EntryType.Name} but decoded {entry.GetType().Name}.");
            }

            var expectedKey = binding.DocumentKeySelector(entry);
            if (!string.Equals(expectedKey, message.Document.DocumentKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"CultNet raw document key mismatch for \"{message.Document.DocumentType}\": payload resolved to \"{expectedKey}\" but wire metadata said \"{message.Document.DocumentKey}\".");
            }

            await cache.AddAsync(entry);
            return entry;
        }

        public async Task<T> ApplyRawDocumentPutMessageAsync<T>(
            CultCache cache,
            CultNetDocumentPutRawMessage message)
            where T : DatabaseEntry
        {
            var entry = await ApplyRawDocumentPutMessageAsync(cache, message);
            if (entry is not T typedEntry)
            {
                throw new InvalidOperationException(
                    $"CultNet raw document message resolved to {entry.GetType().Name}, not {typeof(T).Name}.");
            }

            return typedEntry;
        }

        public async Task<IReadOnlyList<DatabaseEntry>> ApplyRawSnapshotResponseAsync(
            CultCache cache,
            CultNetSnapshotResponseRawMessage response)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (response == null) throw new ArgumentNullException(nameof(response));

            var applied = new List<DatabaseEntry>(response.Documents.Length);
            foreach (var document in response.Documents)
            {
                var entry = await ApplyRawDocumentPutMessageAsync(
                    cache,
                    new CultNetDocumentPutRawMessage
                    {
                        MessageId = response.MessageId,
                        Document = document
                    });
                applied.Add(entry);
            }

            return applied;
        }

        public async Task<IReadOnlyList<T>> ApplyRawSnapshotResponseAsync<T>(
            CultCache cache,
            CultNetSnapshotResponseRawMessage response)
            where T : DatabaseEntry
        {
            var applied = await ApplyRawSnapshotResponseAsync(cache, response);
            return applied.OfType<T>().ToArray();
        }

        private CultNetRawDocumentRecord CreateRawDocumentRecord(
            CultNetDocumentBinding binding,
            DatabaseEntry entry,
            string documentKey,
            string storedAt,
            CultNetDocumentMessageOptions? options)
        {
            var envelope = new CultCacheEnvelope
            {
                Key = documentKey,
                Type = binding.DocumentType,
                StoredAt = storedAt,
                Payload = binding.PayloadSerializer(entry)
            };

            return new CultNetRawDocumentRecord
            {
                DocumentType = envelope.Type,
                DocumentKey = envelope.Key,
                StoredAt = envelope.StoredAt,
                PayloadSchemaVersion = binding.PayloadSchemaVersion,
                PayloadEncoding = "messagepack",
                Payload = envelope.Payload,
                SourceRuntimeId = options?.SourceRuntimeId,
                SourceAgentId = options?.SourceAgentId,
                SourceRole = options?.SourceRole,
                Tags = options?.Tags
            };
        }

        private CultNetDocumentBinding RequireBinding(Type entryType)
        {
            return GetByEntryType(entryType)
                ?? throw new InvalidOperationException(
                    $"No CultNet document binding is registered for {entryType.FullName}.");
        }

        private CultNetDocumentBinding RequireBinding(string documentType)
        {
            return GetByDocumentType(documentType)
                ?? throw new InvalidOperationException(
                    $"No CultNet document binding is registered for \"{documentType}\".");
        }

        private static void ValidateRawDocumentRecord(CultNetRawDocumentRecord document)
        {
            if (document.PayloadEncoding != "messagepack")
            {
                throw new InvalidOperationException(
                    $"CultNet raw document payloadEncoding must be \"messagepack\", not \"{document.PayloadEncoding}\".");
            }

            if (document.Payload == null || document.Payload.Length == 0)
            {
                throw new InvalidOperationException("CultNet raw document payload must be non-empty.");
            }

            RequireNonEmpty(document.DocumentType, nameof(document.DocumentType));
            RequireNonEmpty(document.DocumentKey, nameof(document.DocumentKey));
            RequireNonEmpty(document.StoredAt, nameof(document.StoredAt));
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
