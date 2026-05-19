#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;
using R3;

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
        public void SessionToken_Validates_SessionVersion()
        {
            var userId = Guid.NewGuid();
            var token = Secret.CreateSessionToken(userId, DateTimeOffset.UtcNow.AddMinutes(5), 42, DevelopmentServerSecurity);

            Assert.That(
                Secret.TryValidateSessionToken(token, DevelopmentServerSecurity, out var parsedUserId, out _, out var sessionVersion),
                Is.True);
            Assert.That(parsedUserId, Is.EqualTo(userId));
            Assert.That(sessionVersion, Is.EqualTo(42));
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
        public void CultNetSchemaMessageSerialization_RoundTrips_DatabaseChangeRaw()
        {
            var message = new CultNetDatabaseChangeRawMessage
            {
                MessageId = "change-1",
                SubscriptionId = "sub-1",
                ChangeKind = "updated",
                Document = new CultNetRawDocumentRecord
                {
                    SchemaId = "schema-1",
                    RecordKey = "record-1",
                    StoredAt = "2026-05-19T12:00:00.0000000+00:00",
                    PayloadEncoding = "messagepack",
                    Payload = [0x91, 0x01]
                }
            };

            var payload = CultNetSchemaMessageSerialization.Serialize(message);
            var roundTrip = (CultNetDatabaseChangeRawMessage)CultNetSchemaMessageSerialization.Deserialize(payload);

            Assert.That(roundTrip.SubscriptionId, Is.EqualTo("sub-1"));
            Assert.That(roundTrip.ChangeKind, Is.EqualTo("updated"));
            Assert.That(roundTrip.Document, Is.Not.Null);
            Assert.That(roundTrip.Document!.RecordKey, Is.EqualTo("record-1"));
        }

        [Test]
        public void CultNetSchemaMessageSerialization_RoundTrips_ShardCatalogResponse()
        {
            var message = new CultNetShardCatalogResponseMessage
            {
                MessageId = "shards-1",
                Shards =
                [
                    new CultNetShardDescriptorMessage
                    {
                        ShardId = "players-eu",
                        OwnerRuntimeId = "runtime-a",
                        Epoch = 12,
                        IsPrimary = true,
                        SchemaIds = ["schema-player"],
                        KeyPrefix = "player:",
                        PrimaryEndpoints = ["cultnet://runtime-a:3075"],
                        ReplicaEndpoints = ["cultnet://runtime-b:3075"],
                        ReadReplicaEndpoints = ["cultnet://edge-1:3075"],
                        Region = "eu-west"
                    }
                ]
            };

            var payload = CultNetSchemaMessageSerialization.Serialize(message);
            var roundTrip = (CultNetShardCatalogResponseMessage)CultNetSchemaMessageSerialization.Deserialize(payload);

            Assert.That(roundTrip.MessageId, Is.EqualTo("shards-1"));
            Assert.That(roundTrip.Shards, Has.Length.EqualTo(1));
            Assert.That(roundTrip.Shards[0].ShardId, Is.EqualTo("players-eu"));
            Assert.That(roundTrip.Shards[0].Epoch, Is.EqualTo(12));
            Assert.That(roundTrip.Shards[0].PrimaryEndpoints, Is.EqualTo(["cultnet://runtime-a:3075"]));
            Assert.That(roundTrip.Shards[0].Region, Is.EqualTo("eu-west"));
        }

        [Test]
        public void CultNetSchemaMessageSerialization_RoundTrips_ShardLogResponse()
        {
            var message = new CultNetShardLogResponseMessage
            {
                MessageId = "log-1",
                ShardId = "players-eu",
                ShardEpoch = 12,
                Entries =
                [
                    new CultNetShardLogEntryMessage
                    {
                        Sequence = 42,
                        CommittedAt = "2026-05-19T12:00:00.0000000Z",
                        ChangeKind = "updated",
                        Put = new CultNetDocumentPutRawMessage
                        {
                            MessageId = "put-42",
                            ShardId = "players-eu",
                            ShardEpoch = 12,
                            Document = new CultNetRawDocumentRecord
                            {
                                SchemaId = "schema-player",
                                RecordKey = "player:42",
                                Payload = [0x91, 0x2A]
                            }
                        }
                    }
                ]
            };

            var payload = CultNetSchemaMessageSerialization.Serialize(message);
            var roundTrip = (CultNetShardLogResponseMessage)CultNetSchemaMessageSerialization.Deserialize(payload);

            Assert.That(roundTrip.MessageId, Is.EqualTo("log-1"));
            Assert.That(roundTrip.ShardId, Is.EqualTo("players-eu"));
            Assert.That(roundTrip.ShardEpoch, Is.EqualTo(12));
            Assert.That(roundTrip.Entries, Has.Length.EqualTo(1));
            Assert.That(roundTrip.Entries[0].Sequence, Is.EqualTo(42));
            Assert.That(roundTrip.Entries[0].Put, Is.Not.Null);
            Assert.That(roundTrip.Entries[0].Put!.Document.RecordKey, Is.EqualTo("player:42"));
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
        public async Task CultNetDatabase_PutGet_AndWatchByIndex_Uses_PrimaryShard()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            var key = new CultRecordKey("player:watch");
            var playerId = Guid.NewGuid();
            var changes = new List<CultNetDatabaseChange<PlayerData>>();
            using var subscription = database
                .WatchByIndex<PlayerData>("PlayerId", playerId.ToString("D"))
                .Subscribe(change => changes.Add(change));

            var player = new PlayerData
            {
                PlayerId = playerId,
                Email = "watch@example.test",
                PasswordHash = "hash",
                Username = "Watcher"
            };

            await database.PutAsync(key, player);
            var roundTrip = await database.GetAsync<PlayerData>(key);

            Assert.That(roundTrip, Is.SameAs(player));
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].Kind, Is.EqualTo(CultNetDatabaseChangeKind.Added));
            Assert.That(changes[0].Key, Is.EqualTo(key));
            Assert.That(changes[0].Shard.IsPrimary, Is.True);
        }

        [Test]
        public void CultNetDatabase_Rejects_LocalWrites_To_ReadOnlyShard()
        {
            var cache = new CultCache();
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                Shards =
                [
                    CultNetShardDescriptor.ReadOnly(
                        "remote-players",
                        "remote-runtime",
                        schemaIds: [schemaId])
                ]
            });

            Assert.That(
                async () => await database.PutAsync(
                    new CultRecordKey("player:remote"),
                    new PlayerData
                    {
                        PlayerId = Guid.NewGuid(),
                        Email = "remote@example.test",
                        PasswordHash = "hash",
                        Username = "Remote"
                    }),
                Throws.TypeOf<CultNetShardAuthorityException>()
                    .With.Property(nameof(CultNetShardAuthorityException.Shard))
                    .Property(nameof(CultNetShardDescriptor.ShardId))
                    .EqualTo("remote-players"));
        }

        [Test]
        public async Task CultNetDatabase_Rejects_RawPut_With_StaleShardEpoch()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry,
                Shards =
                [
                    new CultNetShardDescriptor(
                        "players",
                        "runtime-a",
                        epoch: 7,
                        isPrimary: true,
                        schemaIds: [schemaId],
                        keyPrefix: "player:",
                        primaryEndpoints: ["cultnet://runtime-a:3075"])
                ]
            });
            var key = new CultRecordKey("player:stale");
            var message = registry.CreateRawDocumentPutMessage(
                "put-stale",
                new CultRecordHandle<PlayerData>(key),
                new PlayerData
                {
                    PlayerId = Guid.NewGuid(),
                    Email = "stale@example.test",
                    PasswordHash = "hash",
                    Username = "Stale"
                });
            message.ShardId = "players";
            message.ShardEpoch = 6;

            Assert.That(
                async () => await database.ApplyPutAsync(message),
                Throws.TypeOf<CultNetShardAuthorityException>()
                    .With.Property(nameof(CultNetShardAuthorityException.Shard))
                    .Property(nameof(CultNetShardDescriptor.Epoch))
                    .EqualTo(7));
        }

        [Test]
        public async Task CultNetDatabase_ApplyRawPut_Publishes_DomainChange()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry
            });
            var key = new CultRecordKey("player:raw");
            var player = new PlayerData
            {
                PlayerId = Guid.NewGuid(),
                Email = "raw@example.test",
                PasswordHash = "hash",
                Username = "Raw"
            };
            var changes = new List<CultNetDatabaseChange<PlayerData>>();
            using var subscription = database.WatchRecord<PlayerData>(key)
                .Subscribe(change => changes.Add(change));

            var message = registry.CreateRawDocumentPutMessage(
                "put-raw",
                new CultRecordHandle<PlayerData>(key),
                player);

            var applied = await database.ApplyPutAsync(message);

            Assert.That(applied, Is.TypeOf<PlayerData>());
            Assert.That(cache.Get<PlayerData>(key), Is.Not.Null);
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].Kind, Is.EqualTo(CultNetDatabaseChangeKind.Added));
            Assert.That(changes[0].Document, Is.Not.Null);
            Assert.That(changes[0].Document!.Username, Is.EqualTo("Raw"));
        }

        [Test]
        public async Task CultNetDatabaseServer_Creates_Filtered_SnapshotResponse()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry
            });
            using var server = new Server(cache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(server, database);
            var player = new PlayerData
            {
                PlayerId = Guid.NewGuid(),
                Email = "snapshot@example.test",
                PasswordHash = "hash",
                Username = "Snapshot"
            };
            var handle = await database.PutAsync(new CultRecordKey("player:snapshot"), player);

            var response = databaseServer.CreateSnapshotResponse(registry.CreateSnapshotRequest(
                "snapshot-request",
                recordKeys: [handle.Key.Value]));

            Assert.That(response.MessageId, Is.EqualTo("snapshot-request"));
            Assert.That(response.Documents, Has.Length.EqualTo(1));
            Assert.That(response.Documents[0].RecordKey, Is.EqualTo(handle.Key.Value));
            Assert.That(response.Documents[0].Payload, Is.EqualTo(SerializePlayerDataPayload(player)));
        }

        [Test]
        public void CultNetDatabaseServer_Creates_Filtered_ShardCatalogResponse()
        {
            var cache = new CultCache();
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                Shards =
                [
                    new CultNetShardDescriptor(
                        "players",
                        "runtime-a",
                        epoch: 3,
                        isPrimary: true,
                        schemaIds: [schemaId],
                        keyPrefix: "player:",
                        primaryEndpoints: ["cultnet://runtime-a:3075"],
                        replicaEndpoints: ["cultnet://runtime-b:3075"],
                        readReplicaEndpoints: ["cultnet://edge-a:3075"],
                        region: "eu-west"),
                    new CultNetShardDescriptor(
                        "world",
                        "runtime-c",
                        epoch: 1,
                        isPrimary: false,
                        schemaIds: ["schema-world"],
                        keyPrefix: "world:")
                ]
            });
            using var server = new Server(cache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(server, database);

            var response = databaseServer.CreateShardCatalogResponse(new CultNetShardCatalogRequestMessage
            {
                MessageId = "catalog-players",
                SchemaIds = [schemaId],
                RecordKeys = ["player:one"]
            });

            Assert.That(response.MessageId, Is.EqualTo("catalog-players"));
            Assert.That(response.Shards, Has.Length.EqualTo(1));
            Assert.That(response.Shards[0].ShardId, Is.EqualTo("players"));
            Assert.That(response.Shards[0].Epoch, Is.EqualTo(3));
            Assert.That(response.Shards[0].PrimaryEndpoints, Is.EqualTo(["cultnet://runtime-a:3075"]));
            Assert.That(response.Shards[0].ReadReplicaEndpoints, Is.EqualTo(["cultnet://edge-a:3075"]));
        }

        [Test]
        public async Task CultNetDatabaseServer_Forwards_NonPrimaryWrites_WhenConfigured()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry,
                Shards =
                [
                    new CultNetShardDescriptor(
                        "players-remote",
                        "runtime-owner",
                        epoch: 9,
                        isPrimary: false,
                        schemaIds: [schemaId],
                        keyPrefix: "player:",
                        primaryEndpoints: ["cultnet://runtime-owner:3075"])
                ]
            });
            var forwarder = new CapturingShardWriteForwarder();
            using var server = new Server(cache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(
                server,
                database,
                new CultNetDatabaseServerOptions
                {
                    ForwardNonPrimaryWrites = true,
                    WriteForwarder = forwarder
                });
            var key = new CultRecordKey("player:forward");
            var message = registry.CreateRawDocumentPutMessage(
                "put-forward",
                new CultRecordHandle<PlayerData>(key),
                new PlayerData
                {
                    PlayerId = Guid.NewGuid(),
                    Email = "forward@example.test",
                    PasswordHash = "hash",
                    Username = "Forward"
                });
            var exception = Assert.ThrowsAsync<CultNetShardAuthorityException>(
                async () => await database.ApplyPutAsync(message));

            var forwarded = await databaseServer.TryForwardPutAsync(exception!, message);

            Assert.That(forwarded, Is.True);
            Assert.That(forwarder.PutCount, Is.EqualTo(1));
            Assert.That(forwarder.LastPutShard!.ShardId, Is.EqualTo("players-remote"));
            Assert.That(forwarder.LastPutMessage!.ShardId, Is.EqualTo("players-remote"));
            Assert.That(forwarder.LastPutMessage.ShardEpoch, Is.EqualTo(9));
        }

        [Test]
        public void CultNetSchemaWriteForwarder_Parses_CultNetEndpoints()
        {
            var parsed = CultNetSchemaWriteForwarder.ParseEndpoint("cultnet://primary.example.test:4075");

            Assert.That(parsed.Host, Is.EqualTo("primary.example.test"));
            Assert.That(parsed.Port, Is.EqualTo(4075));
        }

        [Test]
        public void CultNetSchemaWriteForwarder_Uses_DefaultPort()
        {
            var parsed = CultNetSchemaWriteForwarder.ParseEndpoint("cultnet://primary.example.test");

            Assert.That(parsed.Host, Is.EqualTo("primary.example.test"));
            Assert.That(parsed.Port, Is.EqualTo(3075));
        }

        [Test]
        public void CultNetSchemaWriteForwarder_Rejects_InvalidEndpoint()
        {
            Assert.That(
                () => CultNetSchemaWriteForwarder.ParseEndpoint("http://primary.example.test:3075"),
                Throws.TypeOf<FormatException>());
        }

        [Test]
        public async Task CultNetDatabase_Appends_PerShardMutationLog()
        {
            var cache = new CultCache();
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                Shards =
                [
                    new CultNetShardDescriptor(
                        "players-log",
                        "runtime-a",
                        epoch: 4,
                        isPrimary: true,
                        schemaIds: [schemaId],
                        keyPrefix: "player:")
                ]
            });
            var key = new CultRecordKey("player:log");
            var player = new PlayerData
            {
                PlayerId = Guid.NewGuid(),
                Email = "log@example.test",
                PasswordHash = "hash",
                Username = "Log"
            };

            await database.PutAsync(key, player);
            player.Username = "LogUpdated";
            await database.PutAsync(key, player);
            await database.DeleteAsync<PlayerData>(key);

            var entries = database.GetMutationLog("players-log");

            Assert.That(entries, Has.Count.EqualTo(3));
            Assert.That(new[] { entries[0].Sequence, entries[1].Sequence, entries[2].Sequence }, Is.EqualTo([1, 2, 3]));
            Assert.That(entries[0].Kind, Is.EqualTo(CultNetDatabaseChangeKind.Added));
            Assert.That(entries[1].Kind, Is.EqualTo(CultNetDatabaseChangeKind.Updated));
            Assert.That(entries[2].Kind, Is.EqualTo(CultNetDatabaseChangeKind.Removed));
            Assert.That(entries[0].ShardEpoch, Is.EqualTo(4));
        }

        [Test]
        public async Task CultNetDatabase_MutationLog_CanCatchUpAfterSequence()
        {
            var cache = new CultCache();
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                Shards =
                [
                    new CultNetShardDescriptor(
                        "players-catchup",
                        "runtime-a",
                        epoch: 1,
                        isPrimary: true,
                        schemaIds: [schemaId],
                        keyPrefix: "player:")
                ]
            });

            await database.PutAsync(
                new CultRecordKey("player:one"),
                new PlayerData { PlayerId = Guid.NewGuid(), Email = "one@example.test", PasswordHash = "hash", Username = "One" });
            await database.PutAsync(
                new CultRecordKey("player:two"),
                new PlayerData { PlayerId = Guid.NewGuid(), Email = "two@example.test", PasswordHash = "hash", Username = "Two" });

            var entries = database.GetMutationLog("players-catchup", afterSequence: 1);

            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Sequence, Is.EqualTo(2));
            Assert.That(entries[0].Key.Value, Is.EqualTo("player:two"));
        }

        [Test]
        public async Task CultNetDatabaseServer_Creates_ShardLogResponse_AfterSequence()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry,
                Shards =
                [
                    new CultNetShardDescriptor(
                        "players-wire-log",
                        "runtime-a",
                        epoch: 5,
                        isPrimary: true,
                        schemaIds: [schemaId],
                        keyPrefix: "player:")
                ]
            });
            using var server = new Server(cache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(server, database);
            var first = new PlayerData { PlayerId = Guid.NewGuid(), Email = "one@example.test", PasswordHash = "hash", Username = "One" };
            var second = new PlayerData { PlayerId = Guid.NewGuid(), Email = "two@example.test", PasswordHash = "hash", Username = "Two" };

            await database.PutAsync(new CultRecordKey("player:one"), first);
            await database.PutAsync(new CultRecordKey("player:two"), second);

            var response = databaseServer.CreateShardLogResponse(new CultNetShardLogRequestMessage
            {
                MessageId = "wire-log",
                ShardId = "players-wire-log",
                ShardEpoch = 5,
                AfterSequence = 1
            });

            Assert.That(response.MessageId, Is.EqualTo("wire-log"));
            Assert.That(response.ShardId, Is.EqualTo("players-wire-log"));
            Assert.That(response.ShardEpoch, Is.EqualTo(5));
            Assert.That(response.ResyncRequired, Is.False);
            Assert.That(response.Entries, Has.Length.EqualTo(1));
            Assert.That(response.Entries[0].Sequence, Is.EqualTo(2));
            Assert.That(response.Entries[0].ChangeKind, Is.EqualTo("added"));
            Assert.That(response.Entries[0].Put, Is.Not.Null);
            Assert.That(response.Entries[0].Put!.ShardId, Is.EqualTo("players-wire-log"));
            Assert.That(response.Entries[0].Put!.ShardEpoch, Is.EqualTo(5));
            Assert.That(response.Entries[0].Put!.Document.Payload, Is.EqualTo(SerializePlayerDataPayload(second)));
        }

        [Test]
        public async Task CultNetDatabaseServer_Creates_ShardLogDeleteEntry()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry,
                Shards =
                [
                    new CultNetShardDescriptor(
                        "players-wire-delete",
                        "runtime-a",
                        epoch: 6,
                        isPrimary: true,
                        schemaIds: [schemaId],
                        keyPrefix: "player:")
                ]
            });
            using var server = new Server(cache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(server, database);
            var key = new CultRecordKey("player:delete-log");

            await database.PutAsync(
                key,
                new PlayerData { PlayerId = Guid.NewGuid(), Email = "delete@example.test", PasswordHash = "hash", Username = "Delete" });
            await database.DeleteAsync<PlayerData>(key);

            var response = databaseServer.CreateShardLogResponse(new CultNetShardLogRequestMessage
            {
                ShardId = "players-wire-delete",
                AfterSequence = 1
            });

            Assert.That(response.Entries, Has.Length.EqualTo(1));
            Assert.That(response.Entries[0].Sequence, Is.EqualTo(2));
            Assert.That(response.Entries[0].ChangeKind, Is.EqualTo("removed"));
            Assert.That(response.Entries[0].Put, Is.Null);
            Assert.That(response.Entries[0].Delete, Is.Not.Null);
            Assert.That(response.Entries[0].Delete!.SchemaId, Is.EqualTo(schemaId));
            Assert.That(response.Entries[0].Delete!.RecordKey, Is.EqualTo(key.Value));
            Assert.That(response.Entries[0].Delete!.ShardEpoch, Is.EqualTo(6));
        }

        [Test]
        public async Task CultNetDatabase_Applies_ShardLogResponse_ToReplica()
        {
            var sourceCache = new CultCache();
            var targetCache = new CultCache();
            var sourceRegistry = new CultNetDocumentRegistry(sourceCache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    sourceCache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var targetRegistry = new CultNetDocumentRegistry(targetCache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    targetCache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var schemaId = sourceCache.Registry.GetRequired<PlayerData>().SchemaId;
            var sourceDatabase = new CultNetDatabase(sourceCache, new CultNetDatabaseOptions
            {
                DocumentRegistry = sourceRegistry,
                Shards =
                [
                    new CultNetShardDescriptor(
                        "players-replica",
                        "runtime-a",
                        epoch: 10,
                        isPrimary: true,
                        schemaIds: [schemaId],
                        keyPrefix: "player:")
                ]
            });
            var targetDatabase = new CultNetDatabase(targetCache, new CultNetDatabaseOptions
            {
                DocumentRegistry = targetRegistry,
                Shards =
                [
                    new CultNetShardDescriptor(
                        "players-replica",
                        "runtime-a",
                        epoch: 10,
                        isPrimary: false,
                        schemaIds: [schemaId],
                        keyPrefix: "player:")
                ]
            });
            using var server = new Server(sourceCache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(server, sourceDatabase);
            var key = new CultRecordKey("player:replica");
            var player = new PlayerData
            {
                PlayerId = Guid.NewGuid(),
                Email = "replica@example.test",
                PasswordHash = "hash",
                Username = "Replica"
            };

            await sourceDatabase.PutAsync(key, player);
            var response = databaseServer.CreateShardLogResponse(new CultNetShardLogRequestMessage
            {
                ShardId = "players-replica",
                ShardEpoch = 10
            });

            var sequence = await targetDatabase.ApplyShardLogResponseAsync(response);
            var replicated = targetCache.Get<PlayerData>(key);
            var replayedSequence = await targetDatabase.ApplyShardLogResponseAsync(response);

            Assert.That(sequence, Is.EqualTo(1));
            Assert.That(replayedSequence, Is.EqualTo(1));
            Assert.That(targetDatabase.GetAppliedShardSequence("players-replica"), Is.EqualTo(1));
            Assert.That(replicated, Is.Not.Null);
            Assert.That(replicated!.Username, Is.EqualTo("Replica"));
            Assert.That(targetDatabase.GetMutationLog("players-replica"), Has.Count.EqualTo(1));
        }

        [Test]
        public void CultNetDatabase_Rejects_ShardLogGap()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry,
                Shards =
                [
                    new CultNetShardDescriptor(
                        "players-gap",
                        "runtime-a",
                        epoch: 1,
                        isPrimary: false,
                        schemaIds: [schemaId],
                        keyPrefix: "player:")
                ]
            });

            Assert.That(
                async () => await database.ApplyShardLogResponseAsync(new CultNetShardLogResponseMessage
                {
                    ShardId = "players-gap",
                    ShardEpoch = 1,
                    Entries =
                    [
                        new CultNetShardLogEntryMessage
                        {
                            Sequence = 2,
                            ChangeKind = "removed",
                            Delete = new CultNetDocumentDeleteMessage
                            {
                                SchemaId = schemaId,
                                RecordKey = "player:gap",
                                ShardId = "players-gap",
                                ShardEpoch = 1
                            }
                        }
                    ]
                }),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("log has a gap"));
        }

        [Test]
        public void CultNetDatabaseServer_Creates_Filtered_SubscriptionChange()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry
            });
            using var server = new Server(cache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(server, database);
            var key = new CultRecordKey("player:change");
            var player = new PlayerData
            {
                PlayerId = Guid.NewGuid(),
                Email = "change@example.test",
                PasswordHash = "hash",
                Username = "Change"
            };
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var change = new CultNetDatabaseChange<PlayerData>(
                CultNetDatabaseChangeKind.Added,
                key,
                schemaId,
                database.Shards[0],
                player,
                previousDocument: null);
            var method = typeof(CultNetDatabaseServer).GetMethod(
                "CreateChangeMessage",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var message = (CultNetDatabaseChangeRawMessage?)method.Invoke(databaseServer, new object[]
            {
                change,
                "sub-1",
                new CultNetDatabaseSubscribeMessage
                {
                    SubscriptionId = "sub-1",
                    SchemaIds = [schemaId],
                    RecordKeys = [key.Value]
                }
            });

            Assert.That(message, Is.Not.Null);
            Assert.That(message!.SubscriptionId, Is.EqualTo("sub-1"));
            Assert.That(message.ChangeKind, Is.EqualTo("added"));
            Assert.That(message.Document, Is.Not.Null);
            Assert.That(message.Document!.Payload, Is.EqualTo(SerializePlayerDataPayload(player)));
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

        [Test]
        public void Server_Separates_Connection_And_Auth_RateLimits()
        {
            var cache = new CultCache();
            var server = new Server(cache, DevelopmentServerSecurity);
            var serverType = typeof(Server);
            var connectionMethod = serverType.GetMethod("CheckConnectionRateLimit", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            var authMethod = serverType.GetMethod("CheckAuthRateLimit", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

            for (var index = 0; index < 5; index++)
            {
                Assert.That((bool)connectionMethod.Invoke(server, new object[] { "127.0.0.1" })!, Is.True);
            }

            Assert.That((bool)authMethod.Invoke(server, new object[] { "127.0.0.1" })!, Is.True);

            for (var index = 0; index < 5; index++)
            {
                _ = authMethod.Invoke(server, new object[] { "127.0.0.2" });
            }

            Assert.That((bool)authMethod.Invoke(server, new object[] { "127.0.0.2" })!, Is.False);
            Assert.That((bool)connectionMethod.Invoke(server, new object[] { "127.0.0.2" })!, Is.True);
        }

        [Test]
        public void Client_Reconnect_Backoff_Is_Bounded_And_Grows()
        {
            var method = typeof(Client).GetMethod("GetReconnectDelayForAttempt", BindingFlags.Static | BindingFlags.NonPublic)!;

            var first = (TimeSpan)method.Invoke(null, new object[] { 1 })!;
            var third = (TimeSpan)method.Invoke(null, new object[] { 3 })!;
            var tenth = (TimeSpan)method.Invoke(null, new object[] { 10 })!;

            Assert.That(first, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(first, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(1.25)));
            Assert.That(third, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(4)));
            Assert.That(third, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(4.25)));
            Assert.That(tenth, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(tenth, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(30.25)));
        }

        [Test]
        public void SensitivePayloadLogging_Is_Gated_By_Default()
        {
            var client = new Client(DevelopmentClientSecurity);
            var productionServer = new Server(new CultCache(), new ServerSecurityOptions("prod-connection-key", "prod-session-signing-secret"));
            var developmentServer = new Server(new CultCache(), DevelopmentServerSecurity);

            Assert.That(client.LogSensitivePayloads, Is.False);
            Assert.That(productionServer.LogSensitivePayloads, Is.False);
            Assert.That(developmentServer.LogSensitivePayloads, Is.True);
        }

        [Test]
        public async Task CultNetLocal_CreateHostAsync_Wires_Durable_Cache_Without_Starting_Server()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"cultnet-host-{Guid.NewGuid():N}.msgpack");

            try
            {
                var host = await CultNetLocal.CreateHostAsync(filePath, new CultNetHostOptions
                {
                    StartServer = false
                });

                Assert.That(host.Cache, Is.Not.Null);
                Assert.That(host.Store, Is.Not.Null);
                Assert.That(host.Server, Is.Not.Null);

                await host.Cache.UpsertAsync(new PlayerData
                {
                    PlayerId = Guid.NewGuid(),
                    Email = "host@example.test",
                    PasswordHash = "hash",
                    Username = "HostUser"
                });
                await host.FlushAsync();
                host.Dispose();

                var reopened = await CultCacheMessagePack.OpenAsync(filePath);
                Assert.That(reopened.GetByName<PlayerData>("HostUser"), Is.Not.Null);
            }
            finally
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        [Test]
        public void CultNetLocal_CreateClient_Uses_Development_Defaults()
        {
            var client = CultNetLocal.CreateClient();

            Assert.That(client, Is.Not.Null);
            Assert.That(client.LogSensitivePayloads, Is.False);
            Assert.That(client.ReconnectState, Is.EqualTo(ClientReconnectState.Idle));
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

        private sealed class CapturingShardWriteForwarder : ICultNetShardWriteForwarder
        {
            public int PutCount { get; private set; }
            public CultNetShardDescriptor? LastPutShard { get; private set; }
            public CultNetDocumentPutRawMessage? LastPutMessage { get; private set; }

            public Task ForwardPutAsync(CultNetShardDescriptor shard, CultNetDocumentPutRawMessage message)
            {
                PutCount++;
                LastPutShard = shard;
                LastPutMessage = message;
                return Task.CompletedTask;
            }

            public Task ForwardDeleteAsync(CultNetShardDescriptor shard, CultNetDocumentDeleteMessage message)
            {
                return Task.CompletedTask;
            }
        }

        [MessagePack.MessagePackObject]
        public sealed class PlayerDataPayload
        {
            [MessagePack.Key(0)] public Guid PlayerId { get; set; }
            [MessagePack.Key(1)] public string Email { get; set; } = string.Empty;
            [MessagePack.Key(2)] public string PasswordHash { get; set; } = string.Empty;
            [MessagePack.Key(3)] public string Username { get; set; } = string.Empty;
            [MessagePack.Key(4)] public long SessionVersion { get; set; }
        }

        private static byte[] SerializePlayerDataPayload(PlayerData entry)
        {
            return MessagePack.MessagePackSerializer.Serialize(new PlayerDataPayload
            {
                PlayerId = entry.PlayerId,
                Email = entry.Email,
                PasswordHash = entry.PasswordHash,
                Username = entry.Username,
                SessionVersion = entry.SessionVersion
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
                Username = decoded.Username,
                SessionVersion = decoded.SessionVersion
            };
        }
    }
}
