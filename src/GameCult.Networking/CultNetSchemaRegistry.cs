using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace GameCult.Networking
{
    /// <summary>
    /// Registers exchange-safe schema metadata and produces discovery responses.
    /// </summary>
    public sealed class CultNetSchemaRegistry
    {
        private readonly Dictionary<string, RegisteredSchema> _entries = new Dictionary<string, RegisteredSchema>(StringComparer.Ordinal);

        public CultNetSchemaRegistry(IEnumerable<CultNetSchemaRegistration>? entries = null)
        {
            if (entries == null)
            {
                return;
            }

            foreach (var entry in entries)
            {
                Register(entry);
            }
        }

        public CultNetSchemaRegistry Register(CultNetSchemaRegistration entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrWhiteSpace(entry.SchemaId)) throw new ArgumentException("SchemaId must be non-empty.", nameof(entry));
            if (string.IsNullOrWhiteSpace(entry.Kind)) throw new ArgumentException("Kind must be non-empty.", nameof(entry));
            if (entry.WireContracts == null || entry.WireContracts.Length == 0)
            {
                throw new ArgumentException("WireContracts must contain at least one entry.", nameof(entry));
            }

            var canonicalSchemaJson = CanonicalizeJson(entry.SchemaJson);
            var contentHash = ComputeSha256Hex(canonicalSchemaJson);

            _entries[entry.SchemaId] = new RegisteredSchema(entry, canonicalSchemaJson, contentHash);
            return this;
        }

        public CultNetSchemaDescriptor? Get(string schemaId, bool includeSchemaJson = false)
        {
            if (schemaId == null) throw new ArgumentNullException(nameof(schemaId));
            return _entries.TryGetValue(schemaId, out var entry)
                ? entry.ToDescriptor(includeSchemaJson)
                : null;
        }

        public CultNetSchemaDescriptor[] List(
            bool includeSchemaJson = false,
            IEnumerable<string>? schemaIds = null,
            IEnumerable<string>? kinds = null)
        {
            var requestedSchemaIds = schemaIds != null
                ? new HashSet<string>(schemaIds, StringComparer.Ordinal)
                : null;
            var requestedKinds = kinds != null
                ? new HashSet<string>(kinds, StringComparer.Ordinal)
                : null;

            return _entries.Values
                .Where(entry =>
                    (requestedSchemaIds == null || requestedSchemaIds.Contains(entry.SchemaId)) &&
                    (requestedKinds == null || requestedKinds.Contains(entry.Kind)))
                .Select(entry => entry.ToDescriptor(includeSchemaJson))
                .ToArray();
        }

        public CultNetSchemaCatalogResponseMessage CreateCatalogResponse(
            CultNetSchemaCatalogRequestMessage request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            return new CultNetSchemaCatalogResponseMessage
            {
                MessageId = request.MessageId,
                Schemas = List(
                    includeSchemaJson: request.IncludeSchemaJson,
                    schemaIds: request.SchemaIds,
                    kinds: request.Kinds)
            };
        }

        public static CultNetSchemaRegistry CreateBuiltIn()
        {
            return new CultNetSchemaRegistry(BuiltInSchemaManifest.Select(LoadEmbeddedRegistration));
        }

        private static readonly Lazy<CultNetSchemaRegistry> BuiltInRegistry =
            new Lazy<CultNetSchemaRegistry>(CreateBuiltIn);

        public static CultNetSchemaRegistry BuiltIn => BuiltInRegistry.Value;

        private static CultNetSchemaRegistration LoadEmbeddedRegistration(SchemaResourceSpec spec)
        {
            var resourceName = $"GameCult.Networking.Contracts.{spec.FileName}";
            using var stream = typeof(CultNetSchemaRegistry).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Missing embedded CultNet schema resource \"{resourceName}\".");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var schemaJson = reader.ReadToEnd();

            using var document = JsonDocument.Parse(schemaJson);
            var root = document.RootElement;
            var schemaId = root.GetProperty("$id").GetString()
                ?? throw new InvalidOperationException($"Schema resource \"{resourceName}\" is missing $id.");
            var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;

            return new CultNetSchemaRegistration
            {
                SchemaId = schemaId,
                Kind = spec.Kind,
                SchemaVersion = spec.SchemaVersion,
                DocumentType = spec.DocumentType,
                Title = title,
                WireContracts = spec.WireContracts.ToArray(),
                SchemaJson = schemaJson
            };
        }

        private static string CanonicalizeJson(string schemaJson)
        {
            using var document = JsonDocument.Parse(schemaJson);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonicalElement(document.RootElement, writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteCanonicalElement(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteCanonicalElement(property.Value, writer);
                    }

                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteCanonicalElement(item, writer);
                    }

                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private static string ComputeSha256Hex(string canonicalSchemaJson)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(canonicalSchemaJson);
            var hash = sha.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var value in hash)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private sealed class RegisteredSchema
        {
            public RegisteredSchema(CultNetSchemaRegistration registration, string canonicalSchemaJson, string contentHash)
            {
                SchemaId = registration.SchemaId;
                Kind = registration.Kind;
                SchemaVersion = registration.SchemaVersion;
                DocumentType = registration.DocumentType;
                Title = registration.Title;
                WireContracts = registration.WireContracts.ToArray();
                CanonicalSchemaJson = canonicalSchemaJson;
                ContentHash = contentHash;
            }

            public string SchemaId { get; }
            public string Kind { get; }
            public string? SchemaVersion { get; }
            public string? DocumentType { get; }
            public string? Title { get; }
            public string[] WireContracts { get; }
            public string CanonicalSchemaJson { get; }
            public string ContentHash { get; }

            public CultNetSchemaDescriptor ToDescriptor(bool includeSchemaJson)
            {
                return new CultNetSchemaDescriptor
                {
                    SchemaId = SchemaId,
                    Kind = Kind,
                    SchemaVersion = SchemaVersion,
                    DocumentType = DocumentType,
                    Title = Title,
                    WireContracts = WireContracts.ToArray(),
                    ContentHash = ContentHash,
                    SchemaJson = includeSchemaJson ? CanonicalSchemaJson : null
                };
            }
        }

        private sealed class SchemaResourceSpec
        {
            public string FileName { get; set; } = string.Empty;
            public string Kind { get; set; } = string.Empty;
            public string[] WireContracts { get; set; } = Array.Empty<string>();
            public string? SchemaVersion { get; set; }
            public string? DocumentType { get; set; }
        }

        private static readonly SchemaResourceSpec[] BuiltInSchemaManifest =
        {
            new SchemaResourceSpec
            {
                FileName = "cultnet.hello.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.Hello,
                WireContracts = new[] { CultNetWireContracts.SchemaV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.login.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.Login,
                WireContracts = new[] { CultNetWireContracts.SchemaV0, CultNetWireContracts.GameCultNetworkingV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.register.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.Register,
                WireContracts = new[] { CultNetWireContracts.SchemaV0, CultNetWireContracts.GameCultNetworkingV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.verify.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.Verify,
                WireContracts = new[] { CultNetWireContracts.SchemaV0, CultNetWireContracts.GameCultNetworkingV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.login-success.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.LoginSuccess,
                WireContracts = new[] { CultNetWireContracts.SchemaV0, CultNetWireContracts.GameCultNetworkingV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.error.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.Error,
                WireContracts = new[] { CultNetWireContracts.SchemaV0, CultNetWireContracts.GameCultNetworkingV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.sample-change-name.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.SampleChangeName,
                WireContracts = new[] { CultNetWireContracts.SchemaV0, CultNetWireContracts.GameCultNetworkingV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.sample-chat.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.SampleChat,
                WireContracts = new[] { CultNetWireContracts.SchemaV0, CultNetWireContracts.GameCultNetworkingV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.document-delete.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.DocumentDelete,
                WireContracts = new[] { CultNetWireContracts.SchemaV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.raw-document-record.schema.json",
                Kind = "shared_contract",
                WireContracts = new[] { CultNetWireContracts.SchemaV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.document-put-raw.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.DocumentPutRaw,
                WireContracts = new[] { CultNetWireContracts.SchemaV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.snapshot-request.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.SnapshotRequest,
                WireContracts = new[] { CultNetWireContracts.SchemaV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.snapshot-response-raw.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.SnapshotResponseRaw,
                WireContracts = new[] { CultNetWireContracts.SchemaV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.schema-descriptor.schema.json",
                Kind = "shared_contract",
                WireContracts = new[] { CultNetWireContracts.SchemaV0, CultNetWireContracts.GameCultNetworkingV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.schema-catalog-request.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.SchemaCatalogRequest,
                WireContracts = new[] { CultNetWireContracts.SchemaV0, CultNetWireContracts.GameCultNetworkingV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "cultnet.schema-catalog-response.schema.json",
                Kind = "wire_message",
                SchemaVersion = CultNetSchemaVersions.SchemaCatalogResponse,
                WireContracts = new[] { CultNetWireContracts.SchemaV0, CultNetWireContracts.GameCultNetworkingV0 }
            },
            new SchemaResourceSpec
            {
                FileName = "ghostlight.agent-state.schema.json",
                Kind = "document_payload",
                SchemaVersion = "ghostlight.agent_state.v0",
                DocumentType = "ghostlight.agent-state",
                WireContracts = new[] { CultNetWireContracts.SchemaV0 }
            }
        };
    }

    /// <summary>
    /// One exchange-safe schema registration.
    /// </summary>
    public sealed class CultNetSchemaRegistration
    {
        public string SchemaId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string[] WireContracts { get; set; } = Array.Empty<string>();
        public string SchemaJson { get; set; } = string.Empty;
        public string? SchemaVersion { get; set; }
        public string? DocumentType { get; set; }
        public string? Title { get; set; }
    }
}
