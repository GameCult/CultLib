#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // The C# reference's own bytes for the selection/v1 message carriers and the cultnet.error.v0
    // code/details shape, written to contracts/cultnet/interop/carrier-vectors.cs-written.json for
    // the runtimes that decode and re-encode them (packages/cultnet-ts reads it; a Rust or Python
    // reader may follow). Each vector carries the whole message's MessagePack bytes plus `expected`,
    // the reference's own camelCase JSON of the same object (byte[] as base64), so a reader judges
    // against what the reference serialized rather than a second hand-written spec.
    //
    // Gated like SelectionParityVectorTests: CULTNET_WRITE_VECTORS=1 rewrites the committed file;
    // otherwise the committed bytes are compared with a fresh serialization so reference drift fails
    // here instead of in a reader.
    public sealed class CarrierVectorTests
    {
        private const string LeafA = "sha256:ff35f3d9d6de3f1066b9369e73fe6c7648e88368d18efc2275c994730cd84cff";
        private const string CiterId = "sha256:9d0bc9aebc3619b07aabf564f98667afde41444c1b866661d777687d443608dc";

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private static CultNetSelection FullSelection() => new()
        {
            Schemas = new[] { "leaf_a.v9", LeafA },
            Keys = new[] { "a-eq", "\U0001F600-row" },
            Fields = new[]
            {
                new CultNetFieldPredicate { Index = "kind", Op = "any_of", Values = new[] { "weapon", "tool" } },
                new CultNetFieldPredicate { Index = "mass", Op = "ge", Number = "-2233759.25" }
            },
            Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = LeafA, RecordKey = "a-eq" }, Role = "Design" },
            Cited = new CultNetIncoming { Role = "Design", Exists = false },
            Projection = CultNetSelectionProjections.Document,
            Descending = true,
            Limit = 200,
            Cursor = "opaque-cursor"
        };

        private static CultNetRawDocumentHeader Header(string key, params string[] tags) => new()
        {
            SchemaId = LeafA,
            SchemaName = "leaf_a",
            SchemaVersion = "1",
            SchemaContentHash = "abc",
            RecordKey = key,
            StoredAt = "1970-01-01T00:00:00.000Z",
            SourceRuntimeId = "rt",
            SourceAgentId = "agent",
            SourceRole = "role",
            Tags = tags.Length == 0 ? null : tags
        };

        private static IEnumerable<(string Name, ICultNetSchemaMessage Message)> Cases()
        {
            yield return ("snapshot_request_v1_defaults", new CultNetSnapshotRequestV1Message { MessageId = "m" });
            yield return ("snapshot_request_v1_full_selection_and_shard", new CultNetSnapshotRequestV1Message
            {
                MessageId = "m-full", Selection = FullSelection(), ShardId = "shard-1", ShardEpoch = 7
            });
            yield return ("database_subscribe_v1_defaults", new CultNetDatabaseSubscribeV1Message { MessageId = "m", SubscriptionId = "sub" });
            yield return ("database_subscribe_v1_full", new CultNetDatabaseSubscribeV1Message
            {
                MessageId = "m-full", SubscriptionId = "sub-full", Selection = FullSelection(), IncludeSnapshot = false,
                ConsumerRuntimeId = "consumer", BodyIds = new[] { "body-a" }, SupportedBodyTransports = new[] { "rudp", "quic" }
            });
            yield return ("snapshot_response_raw_v1_headers", new CultNetSnapshotResponseRawV1Message
            {
                MessageId = "m", Matched = 2, AsOf = 5, Next = "next-cursor",
                Headers = new[] { Header("a-eq"), Header("a-lo", "alpha", "beta") },
                ShardId = "shard-1", ShardEpoch = 3, ShardLogSequence = 41
            });
            yield return ("snapshot_response_raw_v1_documents_and_edges", new CultNetSnapshotResponseRawV1Message
            {
                MessageId = "m", Matched = 1, AsOf = 5,
                Documents = new[]
                {
                    new CultNetRawDocumentRecord { SchemaId = LeafA, RecordKey = "a-eq", StoredAt = "1970-01-01T00:00:00.000Z", Payload = new byte[] { 0x81, 0xA1, 0x6B, 0x01 } }
                },
                Edges = new[]
                {
                    new CultNetEdge
                    {
                        From = new CultNetRecordRef { SchemaId = CiterId, RecordKey = "citer-1" }, Role = "Design",
                        To = new CultNetRecordRef { SchemaId = LeafA, RecordKey = "a-eq" }
                    },
                    new CultNetEdge
                    {
                        From = new CultNetRecordRef { SchemaId = CiterId, RecordKey = "citer-1" }, Role = "Design",
                        To = new CultNetRecordRef { SchemaId = LeafA, RecordKey = "a-eq" },
                        PayloadEncoding = "messagepack", Payload = new byte[] { 1, 2, 3 }
                    }
                }
            });
            yield return ("error_plain", new CultNetErrorMessage { Error = "boom" });
            yield return ("error_selection_invalid", CultNetErrorMessage.ForSelectionInvalid(
                new CultNetSelectionInvalidException("fields[0].index", "kind", "fields[0].index \"kind\" is not declared.")));
            yield return ("error_cursor_stale", new CultNetErrorMessage
            {
                Error = "cursor_stale: stale", Code = "cursor_stale", Details = new CultNetErrorDetails { AsOf = 3, Current = 9 }
            });
            yield return ("error_cursor_invalid_no_details", new CultNetErrorMessage { Error = "cursor_invalid: bad", Code = "cursor_invalid" });
            yield return ("error_write_not_authorized", new CultNetErrorMessage
            {
                Error = "write_not_authorized", Code = "write_not_authorized", Details = new CultNetErrorDetails { Field = "recordKey", Value = "k" }
            });
        }

        private static object Vector(string name, ICultNetSchemaMessage message)
        {
            var bytes = MessagePackSerializer.Serialize(message.GetType(), message, CultNetSchemaMessageSerialization.Options);
            return new
            {
                name,
                schemaVersion = message.SchemaVersion,
                messagePackBase64 = Convert.ToBase64String(bytes),
                expected = WithoutDerivedMembers(JsonSerializer.SerializeToNode(message, message.GetType(), JsonOptions))
            };
        }

        // CultNetSelection.HasHop is an [IgnoreMember] convenience, not wire; System.Text.Json would emit it.
        private static JsonNode? WithoutDerivedMembers(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    obj.Remove("hasHop");
                    foreach (var child in obj.Select(pair => pair.Value).ToArray()) WithoutDerivedMembers(child);
                    break;
                case JsonArray array:
                    foreach (var child in array) WithoutDerivedMembers(child);
                    break;
            }
            return node;
        }

        private static string VectorPath()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CultLib.sln")))
                directory = directory.Parent;
            var root = directory?.FullName ?? throw new InvalidOperationException("CultLib.sln was not found above the test directory.");
            return Path.Combine(root, "contracts", "cultnet", "interop", "carrier-vectors.cs-written.json");
        }

        [Test]
        public void WriteCarrierVectors()
        {
            if (Environment.GetEnvironmentVariable("CULTNET_WRITE_VECTORS") != "1")
                Assert.Ignore("Set CULTNET_WRITE_VECTORS=1 to (re)write the committed vector file.");

            var vectors = Cases().Select(c => Vector(c.Name, c.Message)).ToArray();
            File.WriteAllText(VectorPath(), JsonSerializer.Serialize(new { vectors }, new JsonSerializerOptions { WriteIndented = true }));
        }

        [Test]
        public void CommittedCarrierVectorsMatchAFreshSerialization()
        {
            using var committed = JsonDocument.Parse(File.ReadAllText(VectorPath()));
            var byName = committed.RootElement.GetProperty("vectors").EnumerateArray()
                .ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("messagePackBase64").GetString()!);

            Assert.That(byName.Count, Is.EqualTo(Cases().Count()), "the committed file must carry exactly the cases this writer produces");
            foreach (var (name, message) in Cases())
            {
                var fresh = Convert.ToBase64String(MessagePackSerializer.Serialize(message.GetType(), message, CultNetSchemaMessageSerialization.Options));
                Assert.That(byName[name], Is.EqualTo(fresh), $"{name}: the committed bytes drifted from the reference serializer");
            }
        }
    }
}
