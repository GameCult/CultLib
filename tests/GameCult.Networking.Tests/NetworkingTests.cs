#nullable enable
using System;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    public class NetworkingTests
    {
        private static readonly ServerSecurityOptions DevelopmentServerSecurity = ServerSecurityOptions.Development();
        private static readonly ClientSecurityOptions DevelopmentClientSecurity = ClientSecurityOptions.Development();

        [Test]
        public void EncryptDecrypt_Roundtrip()
        {
            var plaintext = Encoding.UTF8.GetBytes("test");
            var nonce = Secret.NewNonce;
            var encrypted = Secret.EncryptBytes(plaintext, nonce, DevelopmentClientSecurity);
            var decrypted = Secret.DecryptBytes(encrypted, nonce, DevelopmentClientSecurity);
            Assert.That(plaintext, Is.EqualTo(decrypted));
        }

        [Test]
        public void SessionToken_Validates_AndRejectsTampering()
        {
            var userId = Guid.NewGuid();
            var token = Secret.CreateSessionToken(userId, DateTimeOffset.UtcNow.AddMinutes(5), DevelopmentServerSecurity);

            Assert.That(Secret.TryValidateSessionToken(token, DevelopmentServerSecurity, out var parsedUserId, out var expiresAt), Is.True);
            Assert.That(parsedUserId, Is.EqualTo(userId));
            Assert.That(expiresAt, Is.GreaterThan(DateTimeOffset.UtcNow));

            var tamperedToken = $"{token}tampered";
            Assert.That(Secret.TryValidateSessionToken(tamperedToken, DevelopmentServerSecurity, out _, out _), Is.False);
        }

        [Test]
        public void SessionToken_RejectsExpiredTokens()
        {
            var token = Secret.CreateSessionToken(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), DevelopmentServerSecurity);

            Assert.That(Secret.TryValidateSessionToken(token, DevelopmentServerSecurity, out _, out _), Is.False);
        }

        [Test]
        public void SecurityOptions_FromEnvironment_Rejects_MissingSecrets()
        {
            using var _ = new EnvironmentVariableScope(
                (ServerSecurityOptions.ConnectionKeyEnvironmentVariable, null),
                (ServerSecurityOptions.SessionSigningSecretEnvironmentVariable, null));

            Assert.That(
                () => ServerSecurityOptions.FromEnvironment(),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Server security configuration is not configured"));
        }

        [Test]
        public void SecurityOptions_FromEnvironment_Rejects_PartialConfiguration()
        {
            using var _ = new EnvironmentVariableScope(
                (ServerSecurityOptions.ConnectionKeyEnvironmentVariable, "connection-key"),
                (ServerSecurityOptions.SessionSigningSecretEnvironmentVariable, null));

            Assert.That(
                () => ServerSecurityOptions.FromEnvironment(),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains(ServerSecurityOptions.SessionSigningSecretEnvironmentVariable));
        }

        [Test]
        public void ServerSecurityOptions_FromEnvironment_Can_OptInto_DevelopmentDefaults()
        {
            using var _ = new EnvironmentVariableScope(
                (ServerSecurityOptions.ConnectionKeyEnvironmentVariable, null),
                (ServerSecurityOptions.SessionSigningSecretEnvironmentVariable, null));

            var options = ServerSecurityOptions.FromEnvironment(allowDevelopmentDefaults: true);

            Assert.That(options.IsDevelopment, Is.True);
            Assert.That(options.ConnectionKey, Is.Not.Empty);
        }

        [Test]
        public void MessageSerialization_RoundTrips_KnownMessageUnion()
        {
            var client = new Client(DevelopmentClientSecurity);
            var message = new LoginMessage
            {
                Nonce = Secret.NewNonce,
                Auth = Encoding.UTF8.GetBytes("auth"),
                Password = Encoding.UTF8.GetBytes("password")
            };

            var serializationType = client.GetType().Assembly
                .GetType("GameCult.Networking.MessageSerialization", throwOnError: true)!;
            var serialize = serializationType.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeof(Message));
            var deserialize = serializationType.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeof(Message));

            var payload = (byte[])serialize.Invoke(null, [message])!;
            var roundTrip = (LoginMessage)deserialize.Invoke(null, [payload])!;

            Assert.That(roundTrip.Auth, Is.EqualTo(message.Auth));
            Assert.That(roundTrip.Password, Is.EqualTo(message.Password));
            Assert.That(roundTrip.Nonce, Is.EqualTo(message.Nonce));
        }

        [Test]
        public void MessageSerialization_RoundTrips_SchemaCatalogMessages()
        {
            var client = new Client(DevelopmentClientSecurity);
            var message = new SchemaCatalogResponseMessage
            {
                MessageId = "catalog-1",
                Schemas =
                [
                    new SchemaDescriptorMessage
                    {
                        SchemaId = "https://example.test/contracts/example.schema.json",
                        Kind = "shared_contract",
                        SchemaVersion = "example.contract.v0",
                        Title = "Example Contract",
                        WireContracts = ["cultnet.schema.v0", "gamecult.networking.v0"],
                        ContentHash = "deadbeef",
                        SchemaJson = "{\"type\":\"object\"}"
                    }
                ]
            };

            var serializationType = client.GetType().Assembly
                .GetType("GameCult.Networking.MessageSerialization", throwOnError: true)!;
            var serialize = serializationType.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeof(Message));
            var deserialize = serializationType.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeof(Message));

            var payload = (byte[])serialize.Invoke(null, [message])!;
            var roundTrip = (SchemaCatalogResponseMessage)deserialize.Invoke(null, [payload])!;

            Assert.That(roundTrip.MessageId, Is.EqualTo("catalog-1"));
            Assert.That(roundTrip.Schemas, Has.Length.EqualTo(1));
            Assert.That(roundTrip.Schemas[0].SchemaId, Is.EqualTo("https://example.test/contracts/example.schema.json"));
            Assert.That(roundTrip.Schemas[0].WireContracts, Is.EqualTo(["cultnet.schema.v0", "gamecult.networking.v0"]));
            Assert.That(roundTrip.Schemas[0].SchemaJson, Is.EqualTo("{\"type\":\"object\"}"));
        }

        [Test]
        public void MessageSerialization_Rejects_InvalidPayload()
        {
            var client = new Client(DevelopmentClientSecurity);
            var serializationType = client.GetType().Assembly
                .GetType("GameCult.Networking.MessageSerialization", throwOnError: true)!;
            var deserialize = serializationType.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeof(Message));

            Assert.That(
                () => deserialize.Invoke(null, [new byte[] { 0xC1 }]),
                Throws.TypeOf<TargetInvocationException>()
                    .With.InnerException.InstanceOf<MessagePack.MessagePackSerializationException>());
        }

        [Test]
        public void CultNetSchemaMessageSerialization_RoundTrips_RawSnapshotResponse()
        {
            var message = new CultNetSnapshotResponseRawMessage
            {
                MessageId = "snapshot-1",
                Documents =
                [
                    new CultNetRawDocumentRecord
                    {
                        SchemaId = "sha256:ghostlight-agent-state",
                        RecordKey = "world/main",
                        StoredAt = "2026-05-06T12:34:56.0000000+00:00",
                        PayloadEncoding = "messagepack",
                        Payload = [0x91, 0xA3, 0x66, 0x6F, 0x6F],
                        SourceRuntimeId = "voidbot",
                        SourceAgentId = "void",
                        SourceRole = "herald",
                        Tags = ["swarm", "dream"]
                    }
                ]
            };

            var payload = CultNetSchemaMessageSerialization.Serialize(message);
            var roundTrip = (CultNetSnapshotResponseRawMessage)CultNetSchemaMessageSerialization.Deserialize(payload);

            Assert.That(roundTrip.MessageId, Is.EqualTo("snapshot-1"));
            Assert.That(roundTrip.Documents, Has.Length.EqualTo(1));
            Assert.That(roundTrip.Documents[0].SchemaId, Is.EqualTo("sha256:ghostlight-agent-state"));
            Assert.That(roundTrip.Documents[0].PayloadEncoding, Is.EqualTo("messagepack"));
            Assert.That(roundTrip.Documents[0].Payload, Is.EqualTo(message.Documents[0].Payload));
            Assert.That(roundTrip.Documents[0].Tags, Is.EqualTo(["swarm", "dream"]));
        }

        [Test]
        public async Task CultNetDocumentRegistry_RawSnapshotReplication_PreservesPayloadBytes()
        {
            var sourceCache = new CultCache();
            var targetCache = new CultCache();
            var registry = new CultNetDocumentRegistry()
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));

            var sourceEntry = new PlayerData
            {
                PlayerId = Guid.NewGuid(),
                Email = "cult@example.test",
                PasswordHash = "not-a-real-hash",
                Username = "CultGhost"
            };

            var handle = await sourceCache.AddAsync(sourceEntry);

            var expectedPayload = SerializePlayerDataPayload(sourceEntry);
            var request = registry.CreateSnapshotRequest(
                "request-1",
                schemaIds: [sourceCache.Registry.GetRequired<PlayerData>().SchemaId],
                recordKeys: [handle.Key.Value]);
            var response = registry.CreateRawSnapshotResponse(sourceCache, "snapshot-1", request);
            var serializedResponse = CultNetSchemaMessageSerialization.Serialize(response);
            var roundTrip = (CultNetSnapshotResponseRawMessage)CultNetSchemaMessageSerialization.Deserialize(serializedResponse);

            Assert.That(roundTrip.Documents, Has.Length.EqualTo(1));
            Assert.That(roundTrip.Documents[0].Payload, Is.EqualTo(expectedPayload));

            await registry.ApplyRawSnapshotResponseAsync(targetCache, roundTrip);
            var replicated = targetCache.GetByIndex<PlayerData>("PlayerId", sourceEntry.PlayerId.ToString("D"));

            Assert.That(replicated, Is.Not.Null);
            Assert.That(SerializePlayerDataPayload(replicated!), Is.EqualTo(expectedPayload));
        }

        [Test]
        public void CultNetSchemaRegistry_BuiltInCatalog_AdvertisesRawLane_AndSharedGhostlightContract()
        {
            var response = CultNetSchemaRegistry.BuiltIn.CreateCatalogResponse(
                new CultNetSchemaCatalogRequestMessage
                {
                    MessageId = "catalog-raw",
                    IncludeSchemaJson = true
                });

            var rawPut = Array.Find(response.Schemas,
                schema => schema.SchemaVersion == CultNetSchemaVersions.DocumentPutRaw);
            var rawSnapshot = Array.Find(response.Schemas,
                schema => schema.SchemaVersion == CultNetSchemaVersions.SnapshotResponseRaw);
            var ghostlight = Array.Find(response.Schemas,
                schema => schema.SchemaVersion == "ghostlight.agent_state.v0");

            Assert.That(rawPut, Is.Not.Null);
            Assert.That(rawPut!.WireContracts, Is.EqualTo([CultNetWireContracts.SchemaV0]));
            Assert.That(rawPut.SchemaJson, Does.Contain("cultnet.document_put_raw.v0"));
            Assert.That(rawPut.ContentHash, Has.Length.EqualTo(64));

            Assert.That(rawSnapshot, Is.Not.Null);
            Assert.That(rawSnapshot!.SchemaJson, Does.Contain("cultnet.snapshot_response_raw.v0"));

            Assert.That(ghostlight, Is.Not.Null);
            Assert.That(ghostlight!.DocumentType, Is.EqualTo("ghostlight.agent-state"));
            Assert.That(ghostlight.Kind, Is.EqualTo("document_payload"));
        }

        private sealed class EnvironmentVariableScope : IDisposable
        {
            private readonly (string Name, string? Value)[] _originalValues;

            public EnvironmentVariableScope(params (string Name, string? Value)[] values)
            {
                _originalValues = new (string Name, string? Value)[values.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    _originalValues[i] = (values[i].Name, Environment.GetEnvironmentVariable(values[i].Name));
                    Environment.SetEnvironmentVariable(values[i].Name, values[i].Value);
                }
            }

            public void Dispose()
            {
                foreach (var original in _originalValues)
                {
                    Environment.SetEnvironmentVariable(original.Name, original.Value);
                }
            }
        }

        [MessagePack.MessagePackObject]
        public sealed class PlayerDataPayload
        {
            [MessagePack.Key(0)] public Guid PlayerId { get; set; }
            [MessagePack.Key(1)] public string Email { get; set; } = string.Empty;
            [MessagePack.Key(2)] public string PasswordHash { get; set; } = string.Empty;
            [MessagePack.Key(3)] public string Username { get; set; } = string.Empty;
        }

        private static byte[] SerializePlayerDataPayload(PlayerData entry)
        {
            return MessagePack.MessagePackSerializer.Serialize(new PlayerDataPayload
            {
                PlayerId = entry.PlayerId,
                Email = entry.Email,
                PasswordHash = entry.PasswordHash,
                Username = entry.Username
            });
        }

        private static PlayerData DeserializePlayerDataPayload(byte[] payload)
        {
            var decoded = MessagePack.MessagePackSerializer.Deserialize<PlayerDataPayload>(payload);
            return new PlayerData
            {
                PlayerId = decoded.PlayerId,
                Email = decoded.Email,
                PasswordHash = decoded.PasswordHash,
                Username = decoded.Username
            };
        }
    }
}
