#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Mesh;
using NUnit.Framework;
using R3;

namespace GameCult.Networking.Tests
{
    public class NetworkingTests
    {
        private static readonly ServerSecurityOptions DevelopmentServerSecurity = ServerSecurityOptions.Development();
        private static readonly ClientSecurityOptions DevelopmentClientSecurity = ClientSecurityOptions.Development();

        private static Socket BindUdpSocket()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            socket.ReceiveTimeout = 20;
            return socket;
        }

        private static void PumpRudpHandshake(
            CultNetRudpSocketTransportConnection client,
            CultNetRudpSocketTransportConnection server)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                server.ReceiveOnce();
                client.ReceiveOnce();
                server.ReceiveOnce();
                if (client.Connected && server.Connected)
                {
                    return;
                }

                Task.Delay(5).GetAwaiter().GetResult();
            }

            Assert.Fail("RUDP socket handshake did not complete.");
        }

        private static CultNetTransportFrame ReceiveRudpFrame(CultNetRudpSocketTransportConnection transport)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var frame = transport.ReceiveOnce();
                if (frame != null)
                {
                    return frame;
                }

                Task.Delay(5).GetAwaiter().GetResult();
            }

            Assert.Fail("RUDP socket frame was not delivered.");
            throw new InvalidOperationException("Unreachable.");
        }

        private static TMessage ReceiveRudpSchemaMessage<TMessage>(CultNetRudpSocketTransportConnection transport)
            where TMessage : class, ICultNetSchemaMessage
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var message = transport.ReceiveSchemaMessageOnce<TMessage>();
                if (message != null)
                {
                    return message;
                }

                Task.Delay(5).GetAwaiter().GetResult();
            }

            Assert.Fail($"RUDP schema message {typeof(TMessage).Name} was not delivered.");
            throw new InvalidOperationException("Unreachable.");
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(5));
            }

            Assert.Fail("Condition was not satisfied before timeout.");
        }

        private static async Task<T> AwaitWithTimeout<T>(Task<T> task, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout));
            if (completed != task)
            {
                Assert.Fail("Task did not complete before timeout.");
            }

            return await task;
        }

        private static async Task AwaitWithTimeout(Task task, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout));
            if (completed != task)
            {
                Assert.Fail("Task did not complete before timeout.");
            }

            await task;
        }

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
        public void CultNetSchemaMessageSerialization_RoundTrips_HelloTransportProfile()
        {
            var message = new CultNetHelloMessage
            {
                RuntimeId = "csharp-test",
                RuntimeKind = "dotnet",
                TransportProfiles =
                [
                    new CultNetTransportProfile
                    {
                        RuntimeId = "csharp-test",
                        Transports =
                        [
                            new CultNetTransportDescriptor
                            {
                                TransportId = "test-pipe",
                                Protocol = "tcp_framed",
                                WireContracts = [CultNetWireContracts.SchemaV0],
                                Channels =
                                [
                                    new CultNetTransportChannel
                                    {
                                        ChannelId = "schema",
                                        Delivery = "reliable",
                                        Ordering = "ordered"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            };

            var payload = CultNetSchemaMessageSerialization.Serialize(message);
            var roundTrip = (CultNetHelloMessage)CultNetSchemaMessageSerialization.Deserialize(payload);

            Assert.That(roundTrip.TransportProfiles, Is.Not.Null);
            Assert.That(roundTrip.TransportProfiles![0].Transports[0].Protocol, Is.EqualTo("tcp_framed"));
            Assert.That(roundTrip.TransportProfiles[0].Transports[0].Channels[0].Ordering, Is.EqualTo("ordered"));
        }

        [Test]
        public async Task TcpFramedTransportConnection_CarriesSchemaPayloadsWithStats()
        {
            var payload = Encoding.UTF8.GetBytes("cultnet-payload");
            var profile = CultNetTransportProfiles.CreateTcpFramed(
                "csharp-transport",
                new TcpFramedTransportProfileOptions
                {
                    TransportId = "test-tcp"
                });
            using var stream = new MemoryStream();
            var sender = new TcpFramedTransportConnection(stream, profile);

            await sender.SendAsync("schema", payload);

            Assert.That(sender.Stats.FramesSent, Is.EqualTo(1));
            Assert.That(sender.Stats.BytesSent, Is.EqualTo(payload.Length + 4));
            Assert.That(
                Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await sender.SendAsync("unreliable", Array.Empty<byte>())),
                Is.Not.Null);

            stream.Position = 0;
            var receiver = new TcpFramedTransportConnection(stream, profile);
            var frame = await receiver.ReceiveAsync();

            Assert.That(frame.ChannelId, Is.EqualTo("schema"));
            Assert.That(frame.Payload, Is.EqualTo(payload));
            Assert.That(receiver.Stats.FramesReceived, Is.EqualTo(1));
            Assert.That(receiver.Stats.BytesReceived, Is.EqualTo(frame.Payload.Length + 4));
            Assert.That(receiver.Profile.Transports[0].Protocol, Is.EqualTo("tcp_framed"));
        }

        [Test]
        public void RudpPacketCodec_UsesDeterministicReliableOrderedFixture()
        {
            var encoded = CultNetRudpPacketCodec.Encode(new CultNetRudpPacket
            {
                PacketType = CultNetRudpPacketType.Data,
                ConnectionId = 0x01020304,
                Sequence = 0x0000002a,
                Ack = 0x00000029,
                AckMask = 0x80000001,
                ChannelId = "schema",
                Reliable = true,
                Ordered = true,
                FragmentId = 7,
                FragmentIndex = 1,
                FragmentCount = 3,
                Payload = Encoding.UTF8.GetBytes("hello")
            });

            Assert.That(
                Convert.ToHexString(encoded).ToLowerInvariant(),
                Is.EqualTo("434e523000030b2a010203040000002a0000002980000001000700010003000000050600736368656d6168656c6c6f"));

            var decoded = CultNetRudpPacketCodec.Decode(encoded);
            Assert.That(decoded.PacketType, Is.EqualTo(CultNetRudpPacketType.Data));
            Assert.That(decoded.ConnectionId, Is.EqualTo(0x01020304));
            Assert.That(decoded.Sequence, Is.EqualTo(0x0000002a));
            Assert.That(decoded.Ack, Is.EqualTo(0x00000029));
            Assert.That(decoded.AckMask, Is.EqualTo(0x80000001));
            Assert.That(decoded.ChannelId, Is.EqualTo("schema"));
            Assert.That(decoded.Reliable, Is.True);
            Assert.That(decoded.Ordered, Is.True);
            Assert.That(decoded.Sequenced, Is.False);
            Assert.That(decoded.FragmentId, Is.EqualTo(7));
            Assert.That(decoded.FragmentIndex, Is.EqualTo(1));
            Assert.That(decoded.FragmentCount, Is.EqualTo(3));
            Assert.That(Encoding.UTF8.GetString(decoded.Payload), Is.EqualTo("hello"));
        }

        [Test]
        public void RudpTransportProfile_AdvertisesStateAndRealtimeChannels()
        {
            var profile = CultNetTransportProfiles.CreateRudp(
                "csharp-rudp",
                new RudpTransportProfileOptions
                {
                    TransportId = "public-rudp",
                    Host = "127.0.0.1",
                    Port = 7777,
                    MaxPayloadBytes = 1200,
                    MaxFragmentBytes = 1000,
                    MaxPendingReliablePackets = 64
                });

            Assert.That(profile.Transports[0].Protocol, Is.EqualTo("rudp"));
            Assert.That(profile.Transports[0].ReconnectPolicy?.SchemaVersion, Is.EqualTo("cultnet.reconnect_policy.v0"));
            Assert.That(profile.Transports[0].ReconnectPolicy?.BaseDelayMs, Is.EqualTo(1_000));
            Assert.That(
                profile.Transports[0].Channels.Select(channel => $"{channel.ChannelId}:{channel.Delivery}:{channel.Ordering}").ToArray(),
                Is.EqualTo(new[]
                {
                    "schema:reliable:ordered",
                    "latest:unreliable:sequenced",
                    "realtime:unreliable:unordered"
                }));
            Assert.That(
                profile.Transports[0].Channels.Select(channel => channel.MaxPendingReliablePackets).ToArray(),
                Is.EqualTo(new int?[] { 64, 64, 64 }));
        }

        [Test]
        public void CultNetSchemaMessageSerialization_RoundTrips_OperationEnvelope()
        {
            var request = new CultNetOperationRequestMessage
            {
                MessageId = "plugin-42",
                ServiceId = "sai.vn",
                Operation = "project",
                PayloadSchema = "gamecult.eve.plugin_abi.request.v1",
                Payload = "gaZzY2hlbWHEJ2dhbWVjdWx0LmV2ZS5wbHVnaW5fYWJpLnJlcXVlc3QudjE=",
                SourceRuntimeId = "eve-unity"
            };

            var payload = CultNetSchemaMessageSerialization.Serialize(request);
            var roundTrip = (CultNetOperationRequestMessage)CultNetSchemaMessageSerialization.Deserialize(payload);

            Assert.That(roundTrip.MessageId, Is.EqualTo(request.MessageId));
            Assert.That(roundTrip.ServiceId, Is.EqualTo("sai.vn"));
            Assert.That(roundTrip.PayloadSchema, Is.EqualTo("gamecult.eve.plugin_abi.request.v1"));
        }

        [Test]
        public void LiteNetLibTransportProfile_AdvertisesLegacyAndSchemaChannels()
        {
            var policy = CultNetReconnectPolicies.CreateDefault("client-policy", maxAttempts: 4);
            var profile = CultNetTransportProfiles.CreateLiteNetLib(
                "csharp-litenetlib",
                new LiteNetLibTransportProfileOptions
                {
                    TransportId = "game-server",
                    Host = "127.0.0.1",
                    Port = 3075,
                    MaxPayloadBytes = 65_507,
                    ReconnectPolicy = policy
                });

            var transport = profile.Transports.Single();
            Assert.That(transport.Protocol, Is.EqualTo("litenetlib"));
            Assert.That(transport.TransportId, Is.EqualTo("game-server"));
            Assert.That(transport.Host, Is.EqualTo("127.0.0.1"));
            Assert.That(transport.Port, Is.EqualTo(3075));
            Assert.That(transport.WireContracts, Is.EqualTo(new[] { CultNetWireContracts.SchemaV0, CultNetWireContracts.GameCultNetworkingV0 }));
            Assert.That(transport.ReconnectPolicy, Is.SameAs(policy));
            Assert.That(
                transport.Channels.Select(channel => $"{channel.ChannelId}:{channel.Delivery}:{channel.Ordering}:{channel.MaxPayloadBytes}").ToArray(),
                Is.EqualTo(new[]
                {
                    "schema:reliable:ordered:65507",
                    "legacy:reliable:ordered:65507"
                }));
        }

        [Test]
        public void LiteNetLibClientAndServer_ExposeTransportProfiles()
        {
            var policy = CultNetReconnectPolicies.CreateDefault("client-profile", maxAttempts: 3);
            using var client = new Client(DevelopmentClientSecurity, policy);
            using var server = new Server(new CultCache(), DevelopmentServerSecurity);

            var clientTransport = client.TransportProfile.Transports.Single();
            Assert.That(clientTransport.Protocol, Is.EqualTo("litenetlib"));
            Assert.That(clientTransport.TransportId, Is.EqualTo("litenetlib-client"));
            Assert.That(clientTransport.Host, Is.EqualTo("localhost"));
            Assert.That(clientTransport.ReconnectPolicy, Is.SameAs(policy));
            Assert.That(clientTransport.WireContracts, Is.EqualTo(new[] { CultNetWireContracts.SchemaV0, CultNetWireContracts.GameCultNetworkingV0 }));

            var serverTransport = server.TransportProfile.Transports.Single();
            Assert.That(serverTransport.Protocol, Is.EqualTo("litenetlib"));
            Assert.That(serverTransport.TransportId, Is.EqualTo("litenetlib-server"));
            Assert.That(serverTransport.Host, Is.EqualTo("0.0.0.0"));
            Assert.That(serverTransport.Port, Is.EqualTo(3075));
            Assert.That(serverTransport.Channels.Select(channel => channel.ChannelId).ToArray(), Is.EqualTo(new[] { "schema", "legacy" }));
        }

        [Test]
        public void LiteNetLibTransportConnection_DecodesSchemaAndLegacyChannels()
        {
            var schemaPayload = CultNetSchemaMessageSerialization.Serialize(new CultNetHelloMessage
            {
                RuntimeId = "csharp",
                RuntimeKind = "csharp",
                DisplayName = "C#",
                SupportedMessageVersions = [CultNetSchemaVersions.Hello]
            });
            var schemaFrame = LiteNetLibTransportConnection.Decode(schemaPayload);

            Assert.That(schemaFrame.ChannelId, Is.EqualTo("schema"));
            Assert.That(
                LiteNetLibTransportConnection.DecodeSchema(schemaFrame),
                Is.TypeOf<CultNetHelloMessage>());

            var serializationType = typeof(Client).Assembly
                .GetType("GameCult.Networking.MessageSerialization", throwOnError: true)!;
            var serialize = serializationType.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeof(Message));
            var legacyPayload = (byte[])serialize.Invoke(null, [new ErrorMessage { Error = "Nope" }])!;
            var legacyFrame = LiteNetLibTransportConnection.Decode(legacyPayload);

            Assert.That(legacyFrame.ChannelId, Is.EqualTo("legacy"));
            Assert.That(
                LiteNetLibTransportConnection.DecodeLegacy(legacyFrame),
                Is.TypeOf<ErrorMessage>());
            Assert.That(
                () => LiteNetLibTransportConnection.DecodeSchema(legacyFrame),
                Throws.InvalidOperationException);
            Assert.That(
                () => LiteNetLibTransportConnection.DecodeLegacy(schemaFrame),
                Throws.InvalidOperationException);
        }

        [Test]
        public void LiteNetLibTransportConnection_TracksInboundFrames()
        {
            var payload = CultNetSchemaMessageSerialization.Serialize(new CultNetHelloMessage
            {
                RuntimeId = "csharp",
                RuntimeKind = "csharp",
                DisplayName = "C#",
                SupportedMessageVersions = [CultNetSchemaVersions.Hello]
            });
            var connection = new LiteNetLibTransportConnection(CultNetTransportProfiles.CreateLiteNetLib("stats-test"));

            var frame = connection.Receive(payload);

            Assert.That(frame.ChannelId, Is.EqualTo("schema"));
            Assert.That(frame.Payload, Is.EqualTo(payload));
            Assert.That(connection.Stats.FramesReceived, Is.EqualTo(1));
            Assert.That(connection.Stats.BytesReceived, Is.EqualTo(payload.Length));
        }

        [Test]
        public void ReconnectPolicy_ExposesPortableDelayContract()
        {
            var policy = CultNetReconnectPolicies.CreateDefault("rudp-default", maxAttempts: 8);

            Assert.That(policy.SchemaVersion, Is.EqualTo("cultnet.reconnect_policy.v0"));
            Assert.That(policy.PolicyId, Is.EqualTo("rudp-default"));
            Assert.That(policy.MaxAttempts, Is.EqualTo(8));
            Assert.That(CultNetReconnectPolicies.ComputeDelayMs(policy, 1), Is.EqualTo(1_000));
            Assert.That(CultNetReconnectPolicies.ComputeDelayMs(policy, 3, 17), Is.EqualTo(4_017));
            Assert.That(CultNetReconnectPolicies.ComputeDelayMs(policy, 9, 999), Is.EqualTo(30_250));
            Assert.That(CultNetReconnectPolicies.ComputeDelayMs(policy, 0, -5), Is.EqualTo(1_000));

            var json = MessagePack.MessagePackSerializer.ConvertToJson(
                MessagePack.MessagePackSerializer.Serialize(policy, CultNetSchemaMessageSerialization.Options));
            Assert.That(json, Does.Contain("\"schemaVersion\""));
            Assert.That(json, Does.Contain("\"policyId\""));
            Assert.That(json, Does.Contain("\"maxAttempts\""));
        }

        [Test]
        public void ReconnectController_SchedulesAttemptsAndReset()
        {
            var controller = new CultNetReconnectController(
                CultNetReconnectPolicies.CreateDefault(maxAttempts: 2));

            var first = controller.RecordFailure(10_000);
            Assert.That(first.Attempt, Is.EqualTo(1));
            Assert.That(first.ShouldRetry, Is.True);
            Assert.That(first.DelayMs, Is.EqualTo(1_000));
            Assert.That(first.NextAttemptAtMs, Is.EqualTo(11_000));
            Assert.That(first.Exhausted, Is.False);
            Assert.That(controller.CanAttempt(10_999), Is.False);
            Assert.That(controller.CanAttempt(11_000), Is.True);

            var second = controller.RecordFailure(11_000, 17);
            Assert.That(second.Attempt, Is.EqualTo(2));
            Assert.That(second.DelayMs, Is.EqualTo(2_017));
            Assert.That(second.NextAttemptAtMs, Is.EqualTo(13_017));
            Assert.That(second.ShouldRetry, Is.True);

            var exhausted = controller.RecordFailure(13_017);
            Assert.That(exhausted.Attempt, Is.EqualTo(2));
            Assert.That(exhausted.ShouldRetry, Is.False);
            Assert.That(exhausted.DelayMs, Is.EqualTo(0));
            Assert.That(exhausted.NextAttemptAtMs, Is.Null);
            Assert.That(exhausted.Exhausted, Is.True);
            Assert.That(controller.CanAttempt(99_000), Is.False);

            controller.Reset();
            Assert.That(controller.Attempt, Is.EqualTo(0));
            Assert.That(controller.NextAttemptAtMs, Is.Null);
            Assert.That(controller.Exhausted, Is.False);
            Assert.That(controller.CanAttempt(99_000), Is.True);
        }

        [Test]
        public void RudpReconnectLoop_ConsumesSharedController()
        {
            using var serverSocket = BindUdpSocket();
            var remoteEndPoint = serverSocket.LocalEndPoint!;
            var openedLocalPorts = new List<int>();
            const uint connectionId = 0x22334455;

            using var loop = new CultNetRudpReconnectLoop(
                () =>
                {
                    var socket = BindUdpSocket();
                    openedLocalPorts.Add(((IPEndPoint)socket.LocalEndPoint!).Port);
                    return new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
                    {
                        RuntimeId = "csharp-rudp-reconnect",
                        Socket = socket,
                        Mode = CultNetRudpSocketMode.Client,
                        RemoteEndPoint = remoteEndPoint,
                        ConnectionId = connectionId
                    });
                },
                CultNetReconnectPolicies.CreateDefault(maxAttempts: 2),
                Encoding.UTF8.GetBytes("join"));

            var first = loop.Start();
            Assert.That(first.Stats.BytesSent, Is.GreaterThan(0));
            Assert.That(loop.Transport, Is.SameAs(first));
            Assert.That(openedLocalPorts, Has.Count.EqualTo(1));

            var decision = loop.HandleClosed(10_000, 17);
            Assert.That(decision, Is.Not.Null);
            Assert.That(decision!.Attempt, Is.EqualTo(1));
            Assert.That(decision.ShouldRetry, Is.True);
            Assert.That(decision.DelayMs, Is.EqualTo(1_017));
            Assert.That(decision.NextAttemptAtMs, Is.EqualTo(11_017));
            Assert.That(loop.ReconnectController.Attempt, Is.EqualTo(1));
            Assert.That(loop.ReconnectController.NextAttemptAtMs, Is.EqualTo(11_017));
            Assert.That(loop.Transport, Is.Null);

            Assert.That(loop.ReconnectIfDue(11_016), Is.False);
            Assert.That(openedLocalPorts, Has.Count.EqualTo(1));
            Assert.That(loop.ReconnectIfDue(11_017), Is.True);
            Assert.That(openedLocalPorts, Has.Count.EqualTo(2));
            Assert.That(loop.Transport, Is.Not.Null);

            loop.MarkConnected();
            Assert.That(loop.ReconnectController.Attempt, Is.EqualTo(0));

            loop.Stop();
            Assert.That(loop.Transport, Is.Null);
            Assert.That(loop.ReconnectController.Attempt, Is.EqualTo(0));
        }

        [Test]
        public void RudpSession_HandshakeAcksReliableConnectAndAcceptPackets()
        {
            var client = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 0x0a0b0c0d,
                InitialSequence = 1,
                ResendDelayMs = 50
            });
            var server = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 0x0a0b0c0d,
                InitialSequence = 100,
                ResendDelayMs = 50
            });

            var connect = client.CreateConnect(0, Encoding.UTF8.GetBytes("join"));
            Assert.That(connect.PacketType, Is.EqualTo(CultNetRudpPacketType.Connect));
            Assert.That(connect.Sequence, Is.EqualTo(1));
            Assert.That(client.PendingReliableSequences, Is.EqualTo(new uint[] { 1 }));

            var accept = server.AcceptConnect(connect, 10, Encoding.UTF8.GetBytes("ok"));
            Assert.That(accept.PacketType, Is.EqualTo(CultNetRudpPacketType.Accept));
            Assert.That(accept.Ack, Is.EqualTo(1));
            Assert.That(server.Connected, Is.True);
            Assert.That(server.PendingReliableSequences, Is.EqualTo(new uint[] { 100 }));

            client.Receive(accept, 20);
            Assert.That(client.Connected, Is.True);
            Assert.That(client.PendingReliableSequences, Is.Empty);

            var ack = client.CreateAck();
            Assert.That(ack.Sequence, Is.Zero);
            Assert.That(ack.Ack, Is.EqualTo(100));
            server.Receive(ack, 30);
            Assert.That(server.PendingReliableSequences, Is.Empty);
            var firstData = client.Send("schema", Encoding.UTF8.GetBytes("after-ack"));
            Assert.That(firstData.Sequence, Is.EqualTo(2));
        }

        [Test]
        public void RudpSession_ComputesAckMasksAndClearsPendingReliablePackets()
        {
            var sender = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 7,
                InitialSequence = 10,
                ResendDelayMs = 100
            });
            var receiver = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 7,
                InitialSequence = 200,
                ResendDelayMs = 100
            });
            sender.Receive(new CultNetRudpPacket { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 7, Sequence = 1, ChannelId = "control" });
            receiver.Receive(new CultNetRudpPacket { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 7, Sequence = 2, ChannelId = "control" });

            var first = sender.Send("schema", Encoding.UTF8.GetBytes("first"), new CultNetRudpSendOptions { Reliable = true, Ordered = true });
            var second = sender.Send("schema", Encoding.UTF8.GetBytes("second"), new CultNetRudpSendOptions { Reliable = true, Ordered = true });
            var third = sender.Send("schema", Encoding.UTF8.GetBytes("third"), new CultNetRudpSendOptions { Reliable = true, Ordered = true });
            Assert.That(sender.PendingReliableSequences, Is.EqualTo(new uint[] { 10, 11, 12 }));

            receiver.Receive(first);
            receiver.Receive(third);
            var ackWithGap = receiver.CreateAck();
            Assert.That(ackWithGap.Ack, Is.EqualTo(12));
            Assert.That(ackWithGap.AckMask, Is.EqualTo(0b10u | (1u << 9)));
            sender.Receive(ackWithGap);
            Assert.That(sender.PendingReliableSequences, Is.EqualTo(new uint[] { 11 }));

            receiver.Receive(second);
            var fullAck = receiver.CreateAck();
            Assert.That(fullAck.Ack, Is.EqualTo(12));
            Assert.That(fullAck.AckMask, Is.EqualTo(0b11u | (1u << 9)));
            sender.Receive(fullAck);
            Assert.That(sender.PendingReliableSequences, Is.Empty);
        }

        [Test]
        public void RudpSession_SchedulesReliableResendsUntilAcked()
        {
            var session = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 99,
                InitialSequence = 1,
                ResendDelayMs = 100
            });
            session.Receive(new CultNetRudpPacket { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 99, Sequence = 50, ChannelId = "control" });
            var sent = session.Send("schema", Encoding.UTF8.GetBytes("payload"), new CultNetRudpSendOptions { Reliable = true, Ordered = true, NowMs = 10 });

            Assert.That(session.DueResends(90), Is.Empty);
            Assert.That(session.DueResends(110).Select(packet => packet.Sequence).ToArray(), Is.EqualTo(new[] { sent.Sequence }));
            Assert.That(session.DueResends(150), Is.Empty);

            session.Receive(new CultNetRudpPacket { PacketType = CultNetRudpPacketType.Ack, ConnectionId = 99, Sequence = 51, Ack = sent.Sequence, ChannelId = "control" });
            Assert.That(session.DueResends(250), Is.Empty);
        }

        [Test]
        public void RudpSession_ExplicitlyAcknowledgesRetransmitsOlderThanTheReceiveMask()
        {
            var sender = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 991,
                InitialSequence = 1,
                ResendDelayMs = 10
            });
            var receiver = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 991,
                InitialSequence = 500
            });
            sender.Receive(new CultNetRudpPacket
                { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 991, Sequence = 400, ChannelId = "control" });
            receiver.Receive(new CultNetRudpPacket
                { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 991, Sequence = 0, ChannelId = "control" });

            var packets = sender.SendMany(
                "snapshot",
                Enumerable.Range(0, 80).Select(index => (byte)index).ToArray(),
                new CultNetRudpSendOptions { Reliable = true, Ordered = true, NowMs = 0 },
                maxFragmentBytes: 1);
            foreach (var packet in packets)
                receiver.Receive(packet);

            sender.Receive(receiver.CreateAck());
            Assert.That(sender.PendingReliableSequences.First(), Is.EqualTo(1u));

            var oldRetransmit = sender.DueResends(10).Single(packet => packet.Sequence == 1);
            receiver.Receive(oldRetransmit);
            sender.Receive(receiver.CreateAck(oldRetransmit.Sequence));

            Assert.That(sender.PendingReliableSequences, Does.Not.Contain(1u));
        }

        [Test]
        public async Task RudpSession_ResendsAndAcknowledgementsCanRunConcurrently()
        {
            var session = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 100,
                InitialSequence = 1,
                ResendDelayMs = 1
            });
            session.Receive(new CultNetRudpPacket
            {
                PacketType = CultNetRudpPacketType.Accept,
                ConnectionId = 100,
                Sequence = 5000,
                ChannelId = "control"
            });
            var packets = Enumerable.Range(0, 1000)
                .Select(index => session.Send("schema", Encoding.UTF8.GetBytes(index.ToString()),
                    new CultNetRudpSendOptions { Reliable = true, Ordered = true, NowMs = 0 }))
                .ToArray();

            await Task.WhenAll(
                Task.Run(() =>
                {
                    for (var now = 1; now <= 1000; now++)
                        _ = session.DueResends(now);
                }),
                Task.Run(() =>
                {
                    foreach (var packet in packets)
                    {
                        session.Receive(new CultNetRudpPacket
                        {
                            PacketType = CultNetRudpPacketType.Ack,
                            ConnectionId = 100,
                            Sequence = 5001,
                            Ack = packet.Sequence,
                            ChannelId = "control"
                        });
                    }
                }));

            Assert.That(session.PendingReliableSequences, Is.Empty);
        }

        [Test]
        public void RudpSession_PingsAndDetectsReceiveTimeout()
        {
            var client = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 101,
                InitialSequence = 1
            });
            var server = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 101,
                InitialSequence = 100
            });
            var connect = client.CreateConnect(0, Encoding.UTF8.GetBytes("join"));
            var accept = server.AcceptConnect(connect, 10);
            client.Receive(accept, 20);

            var ping = client.CreatePing(Encoding.UTF8.GetBytes("pulse"));
            var pingResult = server.Receive(ping, 30);
            Assert.That(pingResult.Reply?.PacketType, Is.EqualTo(CultNetRudpPacketType.Pong));
            Assert.That(pingResult.Reply?.Payload, Is.EqualTo(Encoding.UTF8.GetBytes("pulse")));

            var pongResult = client.Receive(pingResult.Reply!, 40);
            Assert.That(pongResult.Pong, Is.True);
            Assert.That(pongResult.PongPayload, Is.EqualTo(Encoding.UTF8.GetBytes("pulse")));
            Assert.That(client.CheckTimeout(90, 50), Is.False);
            Assert.That(client.CheckTimeout(91, 50), Is.True);
            Assert.That(client.Connected, Is.False);
        }

        [Test]
        public void RudpSession_BoundsPendingReliablePacketsBeforeEnqueue()
        {
            var session = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 102,
                InitialSequence = 1,
                MaxPendingReliablePackets = 2
            });
            session.Receive(new CultNetRudpPacket { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 102, Sequence = 50, ChannelId = "control" });
            session.Send("schema", Encoding.UTF8.GetBytes("first"), new CultNetRudpSendOptions { Reliable = true, Ordered = true });
            session.Send("schema", Encoding.UTF8.GetBytes("second"), new CultNetRudpSendOptions { Reliable = true, Ordered = true });

            var error = Assert.Throws<InvalidOperationException>(() =>
                session.Send("schema", Encoding.UTF8.GetBytes("third"), new CultNetRudpSendOptions { Reliable = true, Ordered = true }));
            Assert.That(error!.Message, Does.Contain("reliable send queue is full"));
            Assert.That(session.PendingReliableSequences, Is.EqualTo(new uint[] { 1, 2 }));

            var fragmented = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 103,
                InitialSequence = 1,
                MaxPendingReliablePackets = 3
            });
            fragmented.Receive(new CultNetRudpPacket { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 103, Sequence = 50, ChannelId = "control" });

            error = Assert.Throws<InvalidOperationException>(() =>
                fragmented.SendMany(
                    "schema",
                    Encoding.UTF8.GetBytes("fragment-me"),
                    new CultNetRudpSendOptions { Reliable = true, Ordered = true },
                    maxFragmentBytes: 3));
            Assert.That(error!.Message, Does.Contain("reliable send queue is full"));
            Assert.That(fragmented.PendingReliableSequences, Is.Empty);
        }

        [Test]
        public void RudpSession_SuppressesDuplicatesAndDeliversReliableOrderedPayloadsInSequence()
        {
            var sender = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 123,
                InitialSequence = 1
            });
            var receiver = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 123,
                InitialSequence = 100
            });
            sender.Receive(new CultNetRudpPacket { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 123, Sequence = 90, ChannelId = "control" });
            receiver.Receive(new CultNetRudpPacket { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 123, Sequence = 91, ChannelId = "control" });

            var first = sender.Send("schema", Encoding.UTF8.GetBytes("first"), new CultNetRudpSendOptions { Reliable = true, Ordered = true });
            var second = sender.Send("schema", Encoding.UTF8.GetBytes("second"), new CultNetRudpSendOptions { Reliable = true, Ordered = true });
            var third = sender.Send("schema", Encoding.UTF8.GetBytes("third"), new CultNetRudpSendOptions { Reliable = true, Ordered = true });

            Assert.That(
                receiver.Receive(first).Delivered.Select(frame => Encoding.UTF8.GetString(frame.Payload)).ToArray(),
                Is.EqualTo(new[] { "first" }));
            Assert.That(receiver.Receive(third).Delivered, Is.Empty);
            Assert.That(receiver.Receive(first).Delivered, Is.Empty);
            Assert.That(
                receiver.Receive(second).Delivered.Select(frame => Encoding.UTF8.GetString(frame.Payload)).ToArray(),
                Is.EqualTo(new[] { "second", "third" }));
        }

        [Test]
        public void RudpSession_SequencedControlPacketReleasesBufferedOrderedFrame()
        {
            var receiver = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 199,
                InitialSequence = 1
            });
            receiver.Receive(new CultNetRudpPacket
            {
                PacketType = CultNetRudpPacketType.Accept,
                ConnectionId = 199,
                Sequence = 100,
                ChannelId = "control"
            });
            var first = receiver.Receive(new CultNetRudpPacket
            {
                PacketType = CultNetRudpPacketType.Data,
                ConnectionId = 199,
                Sequence = 101,
                ChannelId = "schema",
                Reliable = true,
                Ordered = true,
                Payload = Encoding.UTF8.GetBytes("first")
            });
            var buffered = receiver.Receive(new CultNetRudpPacket
            {
                PacketType = CultNetRudpPacketType.Data,
                ConnectionId = 199,
                Sequence = 103,
                ChannelId = "schema",
                Reliable = true,
                Ordered = true,
                Payload = Encoding.UTF8.GetBytes("second")
            });
            var released = receiver.Receive(new CultNetRudpPacket
            {
                PacketType = CultNetRudpPacketType.Pong,
                ConnectionId = 199,
                Sequence = 102,
                ChannelId = "control"
            });

            Assert.That(first.Delivered, Has.Count.EqualTo(1));
            Assert.That(buffered.Delivered, Is.Empty);
            Assert.That(released.Delivered, Has.Count.EqualTo(1));
            Assert.That(Encoding.UTF8.GetString(released.Delivered[0].Payload), Is.EqualTo("second"));
        }

        [Test]
        public void RudpSession_SkipsControlPacketsWhileOrderingSchemaPayloads()
        {
            var sender = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 124,
                InitialSequence = 1
            });
            var receiver = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 124,
                InitialSequence = 100
            });
            sender.Receive(new CultNetRudpPacket { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 124, Sequence = 90, ChannelId = "control" });
            receiver.Receive(new CultNetRudpPacket { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 124, Sequence = 91, ChannelId = "control" });

            var first = sender.Send("schema", Encoding.UTF8.GetBytes("first"), new CultNetRudpSendOptions { Reliable = true, Ordered = true });
            var control = sender.CreateAck();
            var second = sender.Send("schema", Encoding.UTF8.GetBytes("second"), new CultNetRudpSendOptions { Reliable = true, Ordered = true });

            Assert.That(
                receiver.Receive(first).Delivered.Select(frame => Encoding.UTF8.GetString(frame.Payload)).ToArray(),
                Is.EqualTo(new[] { "first" }));
            Assert.That(receiver.Receive(control).Delivered, Is.Empty);
            Assert.That(
                receiver.Receive(second).Delivered.Select(frame => Encoding.UTF8.GetString(frame.Payload)).ToArray(),
                Is.EqualTo(new[] { "second" }));
        }

        [Test]
        public void RudpSession_FragmentsAndReassemblesReliableOrderedPayloads()
        {
            var sender = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 456,
                InitialSequence = 1
            });
            var receiver = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = 456,
                InitialSequence = 100
            });
            sender.Receive(new CultNetRudpPacket { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 456, Sequence = 90, ChannelId = "control" });
            receiver.Receive(new CultNetRudpPacket { PacketType = CultNetRudpPacketType.Accept, ConnectionId = 456, Sequence = 91, ChannelId = "control" });

            var packets = sender.SendMany(
                "schema",
                Encoding.UTF8.GetBytes("fragment-me-please"),
                new CultNetRudpSendOptions { Reliable = true, Ordered = true, NowMs = 10 },
                maxFragmentBytes: 5).ToArray();

            Assert.That(packets, Has.Length.EqualTo(4));
            Assert.That(packets.Select(packet => packet.FragmentCount).ToArray(), Is.EqualTo(new ushort[] { 4, 4, 4, 4 }));
            Assert.That(packets.Select(packet => packet.FragmentIndex).ToArray(), Is.EqualTo(new ushort[] { 0, 1, 2, 3 }));
            Assert.That(packets.All(packet => packet.FragmentId == packets[0].FragmentId), Is.True);

            Assert.That(receiver.Receive(packets[0]).Delivered, Is.Empty);
            Assert.That(receiver.Receive(packets[1]).Delivered, Is.Empty);
            Assert.That(receiver.Receive(packets[2]).Delivered, Is.Empty);
            var delivered = receiver.Receive(packets[3]).Delivered.ToArray();
            Assert.That(delivered, Has.Length.EqualTo(1));
            Assert.That(Encoding.UTF8.GetString(delivered[0].Payload), Is.EqualTo("fragment-me-please"));
            Assert.That(delivered[0].Sequence, Is.EqualTo(packets[0].Sequence));
        }

        [Test]
        public void RudpSocketTransport_HandshakesAndCarriesReliableOrderedSchemaFrames()
        {
            using var serverSocket = BindUdpSocket();
            using var clientSocket = BindUdpSocket();
            var serverEndPoint = serverSocket.LocalEndPoint!;
            const uint connectionId = 0x10203040;
            using var server = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-server",
                Socket = serverSocket,
                Mode = CultNetRudpSocketMode.Server,
                ConnectionId = connectionId,
                InitialSequence = 100,
                ResendDelayMs = 25
            });
            using var client = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-client",
                Socket = clientSocket,
                Mode = CultNetRudpSocketMode.Client,
                RemoteEndPoint = serverEndPoint,
                ConnectionId = connectionId,
                InitialSequence = 1,
                ResendDelayMs = 25
            });

            client.Connect(Encoding.UTF8.GetBytes("join"));
            PumpRudpHandshake(client, server);
            Assert.That(client.Connected, Is.True);
            Assert.That(server.Connected, Is.True);

            client.Send("schema", Encoding.UTF8.GetBytes("client-state"));
            var serverFrame = ReceiveRudpFrame(server);
            Assert.That(serverFrame.ChannelId, Is.EqualTo("schema"));
            Assert.That(Encoding.UTF8.GetString(serverFrame.Payload), Is.EqualTo("client-state"));

            server.Send("schema", Encoding.UTF8.GetBytes("server-state"));
            var clientFrame = ReceiveRudpFrame(client);
            Assert.That(clientFrame.ChannelId, Is.EqualTo("schema"));
            Assert.That(Encoding.UTF8.GetString(clientFrame.Payload), Is.EqualTo("server-state"));
            Assert.That(server.Profile.Transports[0].Protocol, Is.EqualTo("rudp"));
            Assert.That(client.Stats.FramesSent, Is.EqualTo(1));
            Assert.That(server.Stats.FramesReceived, Is.EqualTo(1));

        }

        [Test]
        public void RudpSocketTransport_CarriesCultNetSchemaMessages()
        {
            using var serverSocket = BindUdpSocket();
            using var clientSocket = BindUdpSocket();
            var serverEndPoint = serverSocket.LocalEndPoint!;
            const uint connectionId = 0x10203043;
            using var server = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-schema-server",
                Socket = serverSocket,
                Mode = CultNetRudpSocketMode.Server,
                ConnectionId = connectionId,
                InitialSequence = 100,
                ResendDelayMs = 25
            });
            using var client = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-schema-client",
                Socket = clientSocket,
                Mode = CultNetRudpSocketMode.Client,
                RemoteEndPoint = serverEndPoint,
                ConnectionId = connectionId,
                InitialSequence = 1,
                ResendDelayMs = 25
            });

            client.Connect(Encoding.UTF8.GetBytes("join"));
            PumpRudpHandshake(client, server);

            client.SendSchemaMessage(new CultNetSchemaCatalogRequestMessage
            {
                MessageId = "schema-request",
                IncludeSchemaJson = true,
                Kinds = ["wire_message"]
            });
            var request = ReceiveRudpSchemaMessage<CultNetSchemaCatalogRequestMessage>(server);
            Assert.That(request.MessageId, Is.EqualTo("schema-request"));
            Assert.That(request.IncludeSchemaJson, Is.True);
            Assert.That(request.Kinds, Is.EqualTo(new[] { "wire_message" }));

            server.SendSchemaMessage(new CultNetHelloMessage
            {
                RuntimeId = "csharp-rudp-schema-server",
                RuntimeKind = "csharp",
                DisplayName = "C# RUDP Schema Server",
                SupportsSchemaCatalog = true,
                TransportProfiles = [server.Profile]
            });
            var hello = ReceiveRudpSchemaMessage<CultNetHelloMessage>(client);
            Assert.That(hello.RuntimeId, Is.EqualTo("csharp-rudp-schema-server"));
            Assert.That(hello.SupportsSchemaCatalog, Is.True);
            Assert.That(hello.TransportProfiles, Is.Not.Null);
            var profile = hello.TransportProfiles!.Single();
            Assert.That(profile.Transports[0].Protocol, Is.EqualTo("rudp"));
        }

        [Test]
        public void RudpSocketTransportServer_DemuxesMultiplePeers()
        {
            using var serverSocket = BindUdpSocket();
            using var firstClientSocket = BindUdpSocket();
            using var secondClientSocket = BindUdpSocket();
            var serverEndPoint = serverSocket.LocalEndPoint!;
            const uint connectionId = 0x10203045;
            using var server = new CultNetRudpSocketTransportServer(new CultNetRudpSocketTransportServerOptions
            {
                RuntimeId = "csharp-rudp-listener",
                Socket = serverSocket,
                ConnectionId = connectionId,
                InitialSequence = 100,
                ResendDelayMs = 25,
                MaxFragmentBytes = 1024,
                AcceptPayload = Encoding.UTF8.GetBytes("accepted")
            });
            using var firstClient = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-listener-client-a",
                Socket = firstClientSocket,
                Mode = CultNetRudpSocketMode.Client,
                RemoteEndPoint = serverEndPoint,
                ConnectionId = connectionId,
                InitialSequence = 1,
                ResendDelayMs = 25,
                MaxFragmentBytes = 1024
            });
            using var secondClient = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-listener-client-b",
                Socket = secondClientSocket,
                Mode = CultNetRudpSocketMode.Client,
                RemoteEndPoint = serverEndPoint,
                ConnectionId = connectionId,
                InitialSequence = 10,
                ResendDelayMs = 25,
                MaxFragmentBytes = 1024
            });

            firstClient.Connect("join-a");
            secondClient.Connect("join-b");
            for (var attempt = 0; attempt < 40 && (!firstClient.Connected || !secondClient.Connected || server.Peers.Count != 2); attempt++)
            {
                server.ReceiveOnce();
                firstClient.ReceiveOnce();
                secondClient.ReceiveOnce();
                server.PollResends();
                firstClient.PollResends();
                secondClient.PollResends();
                Thread.Sleep(5);
            }

            Assert.That(firstClient.Connected, Is.True);
            Assert.That(secondClient.Connected, Is.True);
            Assert.That(server.Peers, Has.Count.EqualTo(2));

            firstClient.SendSchema("first-payload");
            secondClient.SendSchema("second-payload");
            CultNetRudpSocketServerFrame? firstFrame = null;
            CultNetRudpSocketServerFrame? secondFrame = null;
            for (var attempt = 0; attempt < 40 && (firstFrame == null || secondFrame == null); attempt++)
            {
                var frame = server.ReceiveOnce();
                if (frame != null)
                {
                    var payload = Encoding.UTF8.GetString(frame.Frame.Payload);
                    if (payload == "first-payload")
                    {
                        firstFrame = frame;
                    }
                    else if (payload == "second-payload")
                    {
                        secondFrame = frame;
                    }
                }
                firstClient.ReceiveOnce();
                secondClient.ReceiveOnce();
                server.PollResends();
                firstClient.PollResends();
                secondClient.PollResends();
                Thread.Sleep(5);
            }

            Assert.That(firstFrame, Is.Not.Null);
            Assert.That(secondFrame, Is.Not.Null);
            Assert.That(firstFrame!.Peer.RemoteEndPoint, Is.Not.EqualTo(secondFrame!.Peer.RemoteEndPoint));
            Assert.That(firstFrame.Frame.ChannelId, Is.EqualTo("schema"));
            Assert.That(secondFrame.Frame.ChannelId, Is.EqualTo("schema"));

            server.SendSchema(firstFrame.Peer, "reply-a");
            server.SendSchema(secondFrame.Peer, "reply-b");
            var firstReply = firstClient.ReceiveSchema(TimeSpan.FromSeconds(1));
            var secondReply = secondClient.ReceiveSchema(TimeSpan.FromSeconds(1));

            Assert.That(firstReply, Is.Not.Null);
            Assert.That(secondReply, Is.Not.Null);
            Assert.That(Encoding.UTF8.GetString(firstReply!), Is.EqualTo("reply-a"));
            Assert.That(Encoding.UTF8.GetString(secondReply!), Is.EqualTo("reply-b"));
            Assert.That(server.Profile.Transports[0].Protocol, Is.EqualTo("rudp"));
            Assert.That(server.Stats.FramesReceived, Is.EqualTo(2));
            Assert.That(server.Stats.FramesSent, Is.EqualTo(2));
        }

        [Test]
        public async Task RudpCultNetSchemaServer_Dispatches_SchemaMessages()
        {
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "csharp-rudp-schema-host",
                Socket = BindUdpSocket(),
                ConnectionId = 0x43554c54,
                InitialSequence = 100,
                ResendDelayMs = 25,
                MaxFragmentBytes = 1024
            });
            var serverDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlProgressObserved = 0;
            server.OnCultNet<CultNetSchemaCatalogRequestMessage>((request, peer) =>
            {
                peer.SendCultNet(new CultNetSchemaCatalogResponseMessage
                {
                    MessageId = request.MessageId,
                    Schemas =
                    [
                        new CultNetSchemaDescriptor
                        {
                            SchemaId = "rudp.schema.host",
                            Kind = "wire_message",
                            SchemaVersion = "rudp.schema.host.v0",
                            WireContracts = ["cultnet.schema.v0"]
                        }
                    ]
                });
                serverDone.TrySetResult();
            });

            var serverThread = new Thread(() =>
            {
                try
                {
                    while (!serverDone.Task.IsCompleted)
                    {
                        var poll = server.PollAvailableAsync(64).GetAwaiter().GetResult();
                        if (poll.TransportItemsConsumed > poll.MessagesDispatched)
                            Interlocked.Exchange(ref controlProgressObserved, 1);
                        if (poll.TransportItemsConsumed == 0)
                            Thread.Sleep(5);
                    }
                }
                catch (Exception error)
                {
                    serverDone.TrySetException(error);
                }
            })
            {
                IsBackground = true
            };
            serverThread.Start();

            using var client = CultNetSchemaClients.CreateRudp(runtimeId: "csharp-rudp-schema-host-client");
            var responseCompletion = new TaskCompletionSource<CultNetSchemaCatalogResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnCultNet<CultNetSchemaCatalogResponseMessage>(response => responseCompletion.TrySetResult(response));
            client.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => client.Connected, TimeSpan.FromSeconds(2));
            client.SendCultNet(new CultNetSchemaCatalogRequestMessage
            {
                MessageId = "rudp-schema-host-test",
                IncludeSchemaJson = false
            });

            var response = await AwaitWithTimeout(responseCompletion.Task, TimeSpan.FromSeconds(2));
            await AwaitWithTimeout(serverDone.Task, TimeSpan.FromSeconds(2));

            Assert.That(response.MessageId, Is.EqualTo("rudp-schema-host-test"));
            Assert.That(response.Schemas.Single().SchemaId, Is.EqualTo("rudp.schema.host"));
            Assert.That(server.Profile.Transports[0].Protocol, Is.EqualTo("rudp"));
            Assert.That(Volatile.Read(ref controlProgressObserved), Is.EqualTo(1),
                "connection and ACK traffic must count as transport progress without impersonating an application message");
        }

        [Test]
        public void RudpCultNetSchemaServer_RejectsNonPositiveDrainBound()
        {
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                Socket = BindUdpSocket()
            });

            Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => server.PollAvailableAsync(0));
        }

        [Test]
        public async Task RudpCultNetSchemaServer_AcceptsSequentialShortLivedClients()
        {
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "csharp-rudp-sequential-host",
                Socket = BindUdpSocket(),
                MaxFragmentBytes = 1024
            });
            server.OnCultNet<CultNetSchemaCatalogRequestMessage>((request, peer) =>
                peer.SendCultNet(new CultNetSchemaCatalogResponseMessage
                {
                    MessageId = request.MessageId,
                    Schemas = Array.Empty<CultNetSchemaDescriptor>()
                }));

            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            serverThread.Start();

            async Task FetchOnce(string messageId)
            {
                using var client = CultNetSchemaClients.CreateRudp(runtimeId: $"client-{messageId}");
                var completion = new TaskCompletionSource<CultNetSchemaCatalogResponseMessage>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                client.OnCultNet<CultNetSchemaCatalogResponseMessage>(response => completion.TrySetResult(response));
                client.Connect("127.0.0.1", server.LocalEndPoint.Port);
                await WaitUntilAsync(() => client.Connected, TimeSpan.FromSeconds(2));
                client.SendCultNet(new CultNetSchemaCatalogRequestMessage { MessageId = messageId });
                var response = await AwaitWithTimeout(completion.Task, TimeSpan.FromSeconds(2));
                Assert.That(response.MessageId, Is.EqualTo(messageId));
            }

            await FetchOnce("first");
            await WaitUntilAsync(() => server.Peers.Count == 0, TimeSpan.FromSeconds(2));
            await FetchOnce("second");
            cancellation.Cancel();
        }

        [Test]
        public async Task DatabaseSubscriptionServer_StreamsRecordChangesOverRudp()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "database-subscription-server",
                Socket = BindUdpSocket()
            });
            using var subscriptions = new CultNetDatabaseSubscriptionServer(server, database);
            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            serverThread.Start();

            using var client = CultNetSchemaClients.CreateRudp("database-subscription-client");
            var subscribed = new TaskCompletionSource<CultNetSnapshotResponseRawMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var changed = new TaskCompletionSource<CultNetDatabaseChangeRawMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnCultNet<CultNetSnapshotResponseRawMessage>(message => subscribed.TrySetResult(message));
            client.OnCultNet<CultNetDatabaseChangeRawMessage>(message => changed.TrySetResult(message));
            client.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => client.Connected, TimeSpan.FromSeconds(2));
            const string recordKey = "tests:subscription:note";
            client.SendCultNet(new CultNetDatabaseSubscribeMessage
            {
                MessageId = "subscribe-note",
                SubscriptionId = "note",
                RecordKeys = [recordKey],
                IncludeSnapshot = true
            });
            await AwaitWithTimeout(subscribed.Task, TimeSpan.FromSeconds(2));

            await database.PutAsync(new CultRecordKey(recordKey), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "live"
            });
            var update = await AwaitWithTimeout(changed.Task, TimeSpan.FromSeconds(2));

            cancellation.Cancel();
            Assert.That(update.SubscriptionId, Is.EqualTo("note"));
            Assert.That(update.ChangeKind, Is.EqualTo("added"));
            Assert.That(update.Document, Is.Not.Null);
            Assert.That(update.Document!.RecordKey, Is.EqualTo(recordKey));
        }

        [Test]
        public async Task DatabaseSubscriptionServer_AuthorizesRequestAndFiltersSnapshotAndLiveRecords()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "database-authorized-subscription-server",
                Socket = BindUdpSocket()
            });
            using var subscriptions = new CultNetDatabaseSubscriptionServer(
                server,
                database,
                authorizeRequest: (request, _) => request.ConsumerRuntimeId == "allowed-runtime",
                authorizeRecord: (_, _, recordKey, _) => recordKey.StartsWith("tests:public:", StringComparison.Ordinal),
                projectRecord: (request, _, record) =>
                {
                    record.SourceRuntimeId = "projection:" + request.ConsumerRuntimeId;
                    return record;
                });
            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            serverThread.Start();

            await database.PutAsync(new CultRecordKey("tests:public:initial"), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "public-initial"
            });
            await database.PutAsync(new CultRecordKey("tests:private:initial"), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "private-initial"
            });

            using var client = CultNetSchemaClients.CreateRudp("database-authorized-subscription-client");
            var subscribed = new TaskCompletionSource<CultNetSnapshotResponseRawMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var changed = new TaskCompletionSource<CultNetDatabaseChangeRawMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnCultNet<CultNetSnapshotResponseRawMessage>(message => subscribed.TrySetResult(message));
            client.OnCultNet<CultNetDatabaseChangeRawMessage>(message => changed.TrySetResult(message));
            client.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => client.Connected, TimeSpan.FromSeconds(2));
            client.SendCultNet(new CultNetDatabaseSubscribeMessage
            {
                MessageId = "subscribe-authorized",
                SubscriptionId = "authorized",
                ConsumerRuntimeId = "allowed-runtime",
                IncludeSnapshot = true
            });

            var snapshot = await AwaitWithTimeout(subscribed.Task, TimeSpan.FromSeconds(2));
            Assert.That(snapshot.Documents.Select(document => document.RecordKey),
                Is.EqualTo(new[] { "tests:public:initial" }));
            Assert.That(snapshot.Documents.Single().SourceRuntimeId, Is.EqualTo("projection:allowed-runtime"));

            await database.PutAsync(new CultRecordKey("tests:private:live"), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "private-live"
            });
            await database.PutAsync(new CultRecordKey("tests:public:live"), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "public-live"
            });
            var update = await AwaitWithTimeout(changed.Task, TimeSpan.FromSeconds(2));

            cancellation.Cancel();
            Assert.That(update.Document, Is.Not.Null);
            Assert.That(update.Document!.RecordKey, Is.EqualTo("tests:public:live"));
            Assert.That(update.Document.SourceRuntimeId, Is.EqualTo("projection:allowed-runtime"));
        }

        [Test]
        public async Task DatabaseSubscriptionServer_ReconcilesDeliveredProjectionAndBodyDemandWhenAuthorityChanges()
        {
            const string sourceRecordKey = "tests:authority:source";
            const string bodyId = "tests:authority:body";
            var authorized = true;
            var projectionName = "one";
            var sourceCache = new CultCache();
            var sourceDatabase = new CultNetDatabase(sourceCache);
            await sourceDatabase.PutAsync(new CultRecordKey(sourceRecordKey), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "initial"
            });
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "database-reconciled-subscription-server",
                Socket = BindUdpSocket()
            });
            using var subscriptions = new CultNetDatabaseSubscriptionServer(
                server,
                sourceDatabase,
                authorizeRequest: (_, _) => authorized,
                authorizeRecord: (_, _, _, _) => authorized,
                projectRecord: (_, _, record) =>
                {
                    record.RecordKey = $"tests:authority:projected:{projectionName}";
                    return record;
                });
            using var bodyDemand = new CultMeshBodyDemandTracker(subscriptions);
            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            serverThread.Start();

            var targetCache = new CultCache();
            var targetDocuments = new CultNetDocumentRegistry(targetCache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(targetCache.Registry));
            var transport = CultNetSchemaClients.CreateRudp("database-reconciled-subscription-client");
            using var client = new CultNetDatabaseSubscriptionClient(transport, targetCache, targetDocuments);
            var changes = new ConcurrentQueue<CultNetReplicatedDocumentChange>();
            client.Changed += changes.Enqueue;
            transport.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => transport.Connected, TimeSpan.FromSeconds(2));
            await AwaitWithTimeout(
                client.SubscribeAsync(
                    "authority",
                    recordKeys: [sourceRecordKey],
                    consumerRuntimeId: "authority-client",
                    bodyIds: [bodyId],
                    supportedBodyTransports: [CultMeshBodyTransportKind.SharedMemory.ToString()]),
                TimeSpan.FromSeconds(2));
            Assert.That(targetCache.Get(new CultRecordKey("tests:authority:projected:one")), Is.Not.Null);
            Assert.That(bodyDemand.Plan(bodyId).HasConsumers, Is.True);

            projectionName = "two";
            subscriptions.Reconcile();
            await WaitUntilAsync(
                () => targetCache.Get(new CultRecordKey("tests:authority:projected:one")) == null &&
                      targetCache.Get(new CultRecordKey("tests:authority:projected:two")) != null,
                TimeSpan.FromSeconds(2));
            Assert.That(changes.Any(change => change.ChangeKind == "removed" &&
                change.RecordKey == "tests:authority:projected:one"), Is.True);
            Assert.That(changes.Any(change => change.ChangeKind == "added" &&
                change.RecordKey == "tests:authority:projected:two"), Is.True);

            authorized = false;
            subscriptions.Reconcile();
            await WaitUntilAsync(
                () => targetCache.Get(new CultRecordKey("tests:authority:projected:two")) == null,
                TimeSpan.FromSeconds(2));
            Assert.That(bodyDemand.Plan(bodyId).HasConsumers, Is.False);
            var changeCountAfterRevocation = changes.Count;
            await sourceDatabase.PutAsync(new CultRecordKey(sourceRecordKey), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "must-not-escape"
            });
            await Task.Delay(100);
            cancellation.Cancel();

            Assert.That(changes.Count, Is.EqualTo(changeCountAfterRevocation));
            Assert.That(targetCache.Get(new CultRecordKey("tests:authority:projected:two")), Is.Null);
        }

        [Test]
        public async Task DatabaseSubscriptionServer_ProjectsBodyDemandAndWithdrawalFromExactSubscription()
        {
            var database = new CultNetDatabase(new CultCache());
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "database-body-demand-server",
                Socket = BindUdpSocket()
            });
            using var subscriptions = new CultNetDatabaseSubscriptionServer(server, database);
            var active = new TaskCompletionSource<CultNetDatabaseSubscriptionDemand>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var withdrawn = new TaskCompletionSource<CultNetDatabaseSubscriptionDemand>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            subscriptions.DemandChanged += demand =>
            {
                if (demand.Active) active.TrySetResult(demand);
                else withdrawn.TrySetResult(demand);
            };
            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            serverThread.Start();

            using var client = CultNetSchemaClients.CreateRudp("database-body-demand-client");
            client.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => client.Connected, TimeSpan.FromSeconds(2));
            client.SendCultNet(new CultNetDatabaseSubscribeMessage
            {
                MessageId = "subscribe-body",
                SubscriptionId = "world-body",
                RecordKeys = ["world:entities", "mesh:body:world:latest"],
                ConsumerRuntimeId = "eve-unity",
                BodyIds = ["world"],
                SupportedBodyTransports = ["SharedMemory", "Network"],
                IncludeSnapshot = false
            });

            var observed = await AwaitWithTimeout(active.Task, TimeSpan.FromSeconds(2));
            Assert.That(observed.ConsumerRuntimeId, Is.EqualTo("eve-unity"));
            Assert.That(observed.SubscriptionId, Is.EqualTo("world-body"));
            Assert.That(observed.RecordKeys, Is.EqualTo(new[] { "world:entities", "mesh:body:world:latest" }));
            Assert.That(observed.SchemaIds, Is.Empty);
            Assert.That(observed.BodyIds, Is.EqualTo(new[] { "world" }));
            Assert.That(observed.SupportedBodyTransports, Is.EqualTo(new[] { "SharedMemory", "Network" }));
            Assert.That(observed.SameMachine, Is.True);

            client.SendCultNet(new CultNetDatabaseUnsubscribeMessage
            {
                MessageId = "unsubscribe-body",
                SubscriptionId = "world-body"
            });
            var removed = await AwaitWithTimeout(withdrawn.Task, TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            Assert.That(removed.ConsumerRuntimeId, Is.EqualTo("eve-unity"));
            Assert.That(removed.Active, Is.False);
        }

        [Test]
        public async Task DatabaseSubscriptionServer_ClassifiesLoopbackTcpDemandAsSameMachine()
        {
            var database = new CultNetDatabase(new CultCache());
            using var server = new TcpFramedCultNetSchemaServer(new TcpListener(IPAddress.Loopback, 0));
            using var subscriptions = new CultNetDatabaseSubscriptionServer(server, database);
            var active = new TaskCompletionSource<CultNetDatabaseSubscriptionDemand>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            subscriptions.DemandChanged += demand =>
            {
                if (demand.Active) active.TrySetResult(demand);
            };
            using var client = new TcpFramedCultNetSchemaClient();
            client.Connect("127.0.0.1", server.LocalEndPoint.Port);
            client.SendCultNet(new CultNetDatabaseSubscribeMessage
            {
                MessageId = "subscribe-tcp-body",
                SubscriptionId = "world-body",
                ConsumerRuntimeId = "eve-unity",
                BodyIds = ["world"],
                SupportedBodyTransports = ["SharedMemory", "Network"],
                IncludeSnapshot = false
            });

            var observed = await AwaitWithTimeout(active.Task, TimeSpan.FromSeconds(2));

            Assert.That(observed.SameMachine, Is.True);
            Assert.That(observed.BodyIds, Is.EqualTo(new[] { "world" }));
        }

        [Test]
        public async Task DatabaseSubscriptionServer_ProjectsExactReactiveStateDemandWithoutBodyDemand()
        {
            var database = new CultNetDatabase(new CultCache());
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "database-reactive-demand-server",
                Socket = BindUdpSocket()
            });
            using var subscriptions = new CultNetDatabaseSubscriptionServer(server, database);
            var active = new TaskCompletionSource<CultNetDatabaseSubscriptionDemand>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            subscriptions.DemandChanged += demand =>
            {
                if (demand.Active) active.TrySetResult(demand);
            };
            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            serverThread.Start();

            using var client = CultNetSchemaClients.CreateRudp("database-reactive-demand-client");
            client.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => client.Connected, TimeSpan.FromSeconds(2));
            client.SendCultNet(new CultNetDatabaseSubscribeMessage
            {
                MessageId = "subscribe-reactive",
                SubscriptionId = "fog-field",
                RecordKeys = ["world:field:fog"],
                SchemaIds = ["gamecult.fields.splats.v1"],
                ConsumerRuntimeId = "eve-unity",
                IncludeSnapshot = false
            });

            var observed = await AwaitWithTimeout(active.Task, TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            Assert.That(observed.RecordKeys, Is.EqualTo(new[] { "world:field:fog" }));
            Assert.That(observed.SchemaIds, Is.EqualTo(new[] { "gamecult.fields.splats.v1" }));
            Assert.That(observed.BodyIds, Is.Empty);
        }

        [Test]
        public async Task DatabaseSubscriptionServer_WithdrawsDemandWhenPeerDisconnects()
        {
            var database = new CultNetDatabase(new CultCache());
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "database-disconnect-demand-server",
                Socket = BindUdpSocket()
            });
            using var subscriptions = new CultNetDatabaseSubscriptionServer(server, database);
            var active = new TaskCompletionSource<CultNetDatabaseSubscriptionDemand>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var withdrawn = new TaskCompletionSource<CultNetDatabaseSubscriptionDemand>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            subscriptions.DemandChanged += demand =>
            {
                if (demand.Active) active.TrySetResult(demand);
                else withdrawn.TrySetResult(demand);
            };
            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            serverThread.Start();

            using var client = CultNetSchemaClients.CreateRudp("database-disconnect-demand-client");
            client.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => client.Connected, TimeSpan.FromSeconds(2));
            client.SendCultNet(new CultNetDatabaseSubscribeMessage
            {
                MessageId = "subscribe-disconnect-body",
                SubscriptionId = "disconnect-world-body",
                ConsumerRuntimeId = "eve-unity",
                BodyIds = ["world"],
                SupportedBodyTransports = ["SharedMemory"],
                IncludeSnapshot = false
            });
            await AwaitWithTimeout(active.Task, TimeSpan.FromSeconds(2));

            client.Dispose();
            var removed = await AwaitWithTimeout(withdrawn.Task, TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => server.Peers.Count == 0, TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            Assert.That(removed.SubscriptionId, Is.EqualTo("disconnect-world-body"));
            Assert.That(removed.Active, Is.False);
        }

        [Test]
        public async Task DatabaseSubscriptionClient_ReplicatesInitialAndLiveTypedDocumentsOverRudp()
        {
            var sourceCache = new CultCache();
            var sourceDatabase = new CultNetDatabase(sourceCache);
            const string recordKey = "tests:subscription-client:note";
            await sourceDatabase.PutAsync(new CultRecordKey(recordKey), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "initial"
            });
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "database-subscription-client-server",
                Socket = BindUdpSocket()
            });
            using var subscriptions = new CultNetDatabaseSubscriptionServer(server, sourceDatabase);
            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            serverThread.Start();

            var targetCache = new CultCache();
            var targetDocuments = new CultNetDocumentRegistry(targetCache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(targetCache.Registry));
            var transport = CultNetSchemaClients.CreateRudp("database-subscription-client");
            using var client = new CultNetDatabaseSubscriptionClient(transport, targetCache, targetDocuments);
            transport.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => transport.Connected, TimeSpan.FromSeconds(2));

            var initial = await AwaitWithTimeout(
                client.SubscribeAsync("notes", recordKeys: new[] { recordKey }),
                TimeSpan.FromSeconds(2));
            Assert.That(initial.OfType<NetworkSchemaNote>().Single().Text, Is.EqualTo("initial"));
            Assert.That(targetCache.Get(new CultRecordKey(recordKey)), Is.TypeOf<NetworkSchemaNote>());

            var changed = new TaskCompletionSource<CultNetReplicatedDocumentChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Changed += change => changed.TrySetResult(change);
            await sourceDatabase.PutAsync(new CultRecordKey(recordKey), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "live"
            });
            var update = await AwaitWithTimeout(changed.Task, TimeSpan.FromSeconds(2));

            cancellation.Cancel();
            Assert.That(update.SubscriptionId, Is.EqualTo("notes"));
            Assert.That(update.Document, Is.TypeOf<NetworkSchemaNote>());
            Assert.That(((NetworkSchemaNote)targetCache.Get(new CultRecordKey(recordKey))!).Text, Is.EqualTo("live"));
        }

        [Test]
        public async Task DatabaseSubscriptionClient_LiveDeliveryDecodesWithoutWritingReplicaState()
        {
            var sourceCache = new CultCache();
            var sourceDatabase = new CultNetDatabase(sourceCache);
            const string recordKey = "tests:subscription-client:live-note";
            await sourceDatabase.PutAsync(new CultRecordKey(recordKey), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "initial"
            });
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "database-live-subscription-server",
                Socket = BindUdpSocket()
            });
            using var subscriptions = new CultNetDatabaseSubscriptionServer(server, sourceDatabase);
            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            serverThread.Start();

            var targetCache = new CultCache();
            var targetDocuments = new CultNetDocumentRegistry(targetCache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(targetCache.Registry));
            var transport = CultNetSchemaClients.CreateRudp("database-live-subscription-client");
            using var client = new CultNetDatabaseSubscriptionClient(transport, targetCache, targetDocuments);
            transport.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => transport.Connected, TimeSpan.FromSeconds(2));

            var initial = await AwaitWithTimeout(
                client.SubscribeAsync(
                    "live-notes",
                    recordKeys: new[] { recordKey },
                    deliveryMode: CultNetDatabaseSubscriptionDeliveryMode.Live),
                TimeSpan.FromSeconds(2));
            Assert.That(initial.OfType<NetworkSchemaNote>().Single().Text, Is.EqualTo("initial"));
            Assert.That(targetCache.Get(new CultRecordKey(recordKey)), Is.Null);

            var changed = new TaskCompletionSource<CultNetReplicatedDocumentChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Changed += change => changed.TrySetResult(change);
            await sourceDatabase.PutAsync(new CultRecordKey(recordKey), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "live"
            });
            var update = await AwaitWithTimeout(changed.Task, TimeSpan.FromSeconds(2));

            cancellation.Cancel();
            Assert.That(((NetworkSchemaNote)update.Document!).Text, Is.EqualTo("live"));
            Assert.That(targetCache.Get(new CultRecordKey(recordKey)), Is.Null);
        }

        [Test]
        public async Task DatabaseSubscriptionClient_LiveValueOwnsExactFilteringCurrentValueAndUnsubscribe()
        {
            var sourceCache = new CultCache();
            var sourceDatabase = new CultNetDatabase(sourceCache);
            const string recordKey = "tests:subscription-client:reactive-note";
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "database-live-value-server",
                Socket = BindUdpSocket()
            });
            using var subscriptions = new CultNetDatabaseSubscriptionServer(server, sourceDatabase);
            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            serverThread.Start();

            var targetCache = new CultCache();
            var targetDocuments = new CultNetDocumentRegistry(targetCache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(targetCache.Registry));
            var transport = CultNetSchemaClients.CreateRudp("database-live-value-client");
            using var client = new CultNetDatabaseSubscriptionClient(transport, targetCache, targetDocuments);
            transport.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => transport.Connected, TimeSpan.FromSeconds(2));

            using var value = await AwaitWithTimeout(
                client.SubscribeLiveValueAsync<NetworkSchemaNote>("reactive-note", recordKey),
                TimeSpan.FromSeconds(2));
            Assert.That(value.HasValue, Is.False);
            Assert.That(targetCache.Get(new CultRecordKey(recordKey)), Is.Null);

            var changed = new TaskCompletionSource<NetworkSchemaNote>(TaskCreationOptions.RunContinuationsAsynchronously);
            value.Changed += document => changed.TrySetResult(document);
            await sourceDatabase.PutAsync(new CultRecordKey(recordKey), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "initial"
            });
            var update = await AwaitWithTimeout(changed.Task, TimeSpan.FromSeconds(2));

            Assert.That(update.Text, Is.EqualTo("initial"));
            Assert.That(value.Current.Text, Is.EqualTo("initial"));
            Assert.That(targetCache.Get(new CultRecordKey(recordKey)), Is.Null);

            var updated = new TaskCompletionSource<NetworkSchemaNote>(TaskCreationOptions.RunContinuationsAsynchronously);
            value.Changed += document => updated.TrySetResult(document);
            await sourceDatabase.PutAsync(new CultRecordKey(recordKey), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "updated"
            });
            update = await AwaitWithTimeout(updated.Task, TimeSpan.FromSeconds(2));

            cancellation.Cancel();
            Assert.That(update.Text, Is.EqualTo("updated"));
            Assert.That(value.Current.Text, Is.EqualTo("updated"));
        }

        [Test]
        public async Task DatabaseSubscriptionClient_ReceivesProviderMaterializedValueFromExactDemand()
        {
            var sourceDatabase = new CultNetDatabase(new CultCache());
            const string recordKey = "tests:subscription-client:demanded-note";
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "database-demanded-value-server",
                Socket = BindUdpSocket()
            });
            using var subscriptions = new CultNetDatabaseSubscriptionServer(server, sourceDatabase);
            subscriptions.DemandChanged += demand =>
            {
                if (!demand.Active || !demand.RecordKeys.Contains(recordKey, StringComparer.Ordinal))
                    return;
                sourceDatabase.PutAsync(new CultRecordKey(recordKey), new NetworkSchemaNote
                {
                    Schema = "tests.networking_note.v1",
                    Text = "materialized-on-demand"
                }).GetAwaiter().GetResult();
            };
            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            serverThread.Start();

            var targetCache = new CultCache();
            var targetDocuments = new CultNetDocumentRegistry(targetCache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(targetCache.Registry));
            var transport = CultNetSchemaClients.CreateRudp("database-demanded-value-client");
            using var client = new CultNetDatabaseSubscriptionClient(transport, targetCache, targetDocuments);
            transport.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => transport.Connected, TimeSpan.FromSeconds(2));

            using var value = await AwaitWithTimeout(
                client.SubscribeLiveValueAsync<NetworkSchemaNote>("demanded-note", recordKey),
                TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => value.HasValue, TimeSpan.FromSeconds(2));

            cancellation.Cancel();
            Assert.That(value.Current.Text, Is.EqualTo("materialized-on-demand"));
            Assert.That(targetCache.Get(new CultRecordKey(recordKey)), Is.Null);
        }

        [Test]
        public async Task DatabaseSubscription_FiltersLiveChangesByWireSchemaBinding()
        {
            const string recordKey = "tests:subscription-client:wire-note";
            const string wireSchema = "tests.wire_note.v1";
            var sourceCache = new CultCache();
            var sourceDocuments = new CultNetDocumentRegistry(sourceCache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(sourceCache.Registry, wireSchema));
            using var sourceDatabase = new CultNetDatabase(sourceCache, new CultNetDatabaseOptions
            {
                DocumentRegistry = sourceDocuments
            });
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "database-wire-schema-subscription-server",
                Socket = BindUdpSocket()
            });
            using var subscriptions = new CultNetDatabaseSubscriptionServer(server, sourceDatabase);
            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            serverThread.Start();

            var targetCache = new CultCache();
            var targetDocuments = new CultNetDocumentRegistry(targetCache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(targetCache.Registry, wireSchema));
            var transport = CultNetSchemaClients.CreateRudp("database-wire-schema-subscription-client");
            using var client = new CultNetDatabaseSubscriptionClient(transport, targetCache, targetDocuments);
            transport.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => transport.Connected, TimeSpan.FromSeconds(2));
            await AwaitWithTimeout(
                client.SubscribeAsync("wire-notes", recordKeys: [recordKey], schemaIds: [wireSchema]),
                TimeSpan.FromSeconds(2));
            var changed = new TaskCompletionSource<CultNetReplicatedDocumentChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Changed += change => changed.TrySetResult(change);

            await sourceDatabase.PutAsync(new CultRecordKey(recordKey), new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "wire-live"
            });
            var update = await AwaitWithTimeout(changed.Task, TimeSpan.FromSeconds(2));

            cancellation.Cancel();
            Assert.That(update.SchemaId, Is.EqualTo(wireSchema));
            Assert.That(((NetworkSchemaNote)update.Document!).Text, Is.EqualTo("wire-live"));
        }

        [Test]
        public void DatabaseSubscription_FiltersCanonicalWireChangesByRequestedSchemaAlias()
        {
            var cache = new CultCache();
            var documents = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(cache.Registry));
            using var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = documents
            });
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "database-schema-alias-subscription-server",
                Socket = BindUdpSocket()
            });
            using var subscriptions = new CultNetDatabaseSubscriptionServer(server, database);
            var descriptor = cache.Registry.GetRequired<NetworkSchemaNote>();
            var change = new CultNetDatabaseChange<NetworkSchemaNote>(
                CultNetDatabaseChangeKind.Added,
                new CultRecordKey("tests:subscription-client:alias-note"),
                descriptor.SchemaId,
                database.Shards[0],
                new NetworkSchemaNote { Schema = "tests.networking_note.v1", Text = "alias-live" },
                previousDocument: null);
            var method = typeof(CultNetDatabaseSubscriptionServer).GetMethod(
                "CreateChange",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var message = (CultNetDatabaseChangeRawMessage?)method.Invoke(subscriptions, new object[]
            {
                change,
                new CultNetDatabaseSubscribeMessage
                {
                    SubscriptionId = "alias-notes",
                    SchemaIds = ["tests.networking_note.v1"],
                    RecordKeys = ["tests:subscription-client:alias-note"]
                },
                "alias-notes"
            });

            Assert.That(message, Is.Not.Null);
            Assert.That(message!.Document!.SchemaId, Is.EqualTo(descriptor.SchemaId));
            Assert.That(message.Document.SchemaName, Is.EqualTo(descriptor.SchemaName));
            Assert.That(message.Document.SchemaVersion, Is.EqualTo(descriptor.SchemaVersion));
            Assert.That(message.Document.SchemaContentHash, Is.EqualTo(descriptor.ContentHash));
        }

        [Test]
        public async Task RudpCultNetSchemaServer_DeliversLargeSnapshotResponse()
        {
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "csharp-rudp-large-snapshot-host",
                Socket = BindUdpSocket(),
                MaxFragmentBytes = 1024,
                MaxPendingReliablePackets = 1024
            });
            var responseCompletion = new TaskCompletionSource<CultNetSnapshotResponseRawMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var serverFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.OnCultNet<CultNetSnapshotRequestMessage>((request, peer) => peer.SendCultNet(
                new CultNetSnapshotResponseRawMessage
                {
                    MessageId = request.MessageId,
                    Documents =
                    [
                        new CultNetRawDocumentRecord
                        {
                            SchemaId = "test.large.snapshot.v1",
                            RecordKey = "test:large:snapshot",
                            PayloadEncoding = "messagepack",
                            Payload = Enumerable.Range(0, 128 * 1024)
                                .Select(index => (byte)(index % 251)).ToArray()
                        }
                    ]
                }));

            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                try
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        _ = server.PollOnceAsync().GetAwaiter().GetResult();
                        Thread.Sleep(1);
                    }
                }
                catch (Exception error) { serverFailure.TrySetResult(error); }
            }) { IsBackground = true };
            serverThread.Start();

            using var client = CultNetSchemaClients.CreateRudp(
                runtimeId: "csharp-rudp-large-snapshot-client",
                maxFragmentBytes: 1024);
            client.OnCultNet<CultNetSnapshotResponseRawMessage>(response => responseCompletion.TrySetResult(response));
            client.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => client.Connected, TimeSpan.FromSeconds(2));
            client.SendCultNet(new CultNetSnapshotRequestMessage
            {
                MessageId = "large-snapshot",
                SchemaIds = ["test.large.snapshot.v1"],
                RecordKeys = ["test:large:snapshot"]
            });

            var completed = await Task.WhenAny(responseCompletion.Task, serverFailure.Task, Task.Delay(5000));
            if (completed == serverFailure.Task) throw await serverFailure.Task;
            if (completed != responseCompletion.Task)
                Assert.Fail($"Large snapshot timed out; server sent {server.Stats.BytesSent} bytes, received {server.Stats.BytesReceived} bytes, transports {server.Profile.Transports.Count()}.");
            var response = await AwaitWithTimeout(responseCompletion.Task, TimeSpan.FromMilliseconds(1));
            await WaitUntilAsync(
                () => server.Peers.Count == 1 && server.Peers.Single().PendingReliablePacketCount == 0,
                TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            Assert.That(response.Documents, Has.Length.EqualTo(1));
            Assert.That(response.Documents[0].Payload, Has.Length.EqualTo(128 * 1024));
            Assert.That(server.Stats.BytesSent, Is.LessThan(512 * 1024),
                "A lossless large response must not remain trapped in the reliable resend queue.");
        }

        [Test]
        public async Task RudpCultNetSchemaServer_SerializesConcurrentFragmentedSendsPerPeer()
        {
            const int publisherCount = 4;
            const int messagesPerPublisher = 4;
            const int expectedMessages = publisherCount * messagesPerPublisher;
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
            {
                RuntimeId = "csharp-rudp-concurrent-publish-host",
                Socket = BindUdpSocket(),
                MaxFragmentBytes = 512,
                MaxPendingReliablePackets = 1024
            });
            var receivedIds = new ConcurrentDictionary<string, byte>();
            var receivedAll = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var serverFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                try
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        _ = server.PollOnceAsync().GetAwaiter().GetResult();
                        Thread.Sleep(1);
                    }
                }
                catch (Exception error) { serverFailure.TrySetResult(error); }
            }) { IsBackground = true };
            serverThread.Start();

            using var client = CultNetSchemaClients.CreateRudp(
                runtimeId: "csharp-rudp-concurrent-publish-client",
                maxFragmentBytes: 512);
            client.OnCultNet<CultNetSnapshotResponseRawMessage>(response =>
            {
                if (receivedIds.TryAdd(response.MessageId, 0) && receivedIds.Count == expectedMessages)
                    receivedAll.TrySetResult(true);
            });
            client.Connect("127.0.0.1", server.LocalEndPoint.Port);
            await WaitUntilAsync(() => client.Connected && server.Peers.Count == 1, TimeSpan.FromSeconds(2));
            var peer = server.Peers.Single();
            using var sendBarrier = new Barrier(publisherCount);

            await Task.WhenAll(Enumerable.Range(0, publisherCount).Select(publisher => Task.Run(() =>
            {
                sendBarrier.SignalAndWait();
                for (var message = 0; message < messagesPerPublisher; message++)
                {
                    var messageId = $"publisher-{publisher}-message-{message}";
                    server.SendCultNet(peer, new CultNetSnapshotResponseRawMessage
                    {
                        MessageId = messageId,
                        Documents =
                        [
                            new CultNetRawDocumentRecord
                            {
                                SchemaId = "test.concurrent.publish.v1",
                                RecordKey = $"test:concurrent:{messageId}",
                                PayloadEncoding = "messagepack",
                                Payload = Enumerable.Range(0, 4 * 1024)
                                    .Select(index => (byte)((index + publisher + message) % 251)).ToArray()
                            }
                        ]
                    });
                }
            })));

            var completed = await Task.WhenAny(receivedAll.Task, serverFailure.Task, Task.Delay(5000));
            if (completed == serverFailure.Task) throw await serverFailure.Task;
            if (completed != receivedAll.Task)
                Assert.Fail($"Concurrent publish timed out after receiving {receivedIds.Count} of {expectedMessages} logical messages.");
            cancellation.Cancel();

            Assert.That(receivedIds.Count, Is.EqualTo(expectedMessages));
        }

        [Test]
        public void RudpSocketTransport_ErgonomicHelpersCarryNamedChannels()
        {
            using var serverSocket = BindUdpSocket();
            using var clientSocket = BindUdpSocket();
            var serverEndPoint = serverSocket.LocalEndPoint!;
            const uint connectionId = 0x10203044;
            using var server = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-helper-server",
                Socket = serverSocket,
                Mode = CultNetRudpSocketMode.Server,
                ConnectionId = connectionId,
                InitialSequence = 100,
                ResendDelayMs = 25
            });
            using var client = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-helper-client",
                Socket = clientSocket,
                Mode = CultNetRudpSocketMode.Client,
                RemoteEndPoint = serverEndPoint,
                ConnectionId = connectionId,
                InitialSequence = 1,
                ResendDelayMs = 25
            });

            client.Connect("join");
            PumpRudpHandshake(client, server);

            client.SendSchema("client-state");
            var schemaPayload = server.ReceiveSchema(TimeSpan.FromSeconds(1));
            Assert.That(schemaPayload, Is.Not.Null);
            Assert.That(Encoding.UTF8.GetString(schemaPayload!), Is.EqualTo("client-state"));

            server.SendSchemaMessage(new CultNetHelloMessage
            {
                RuntimeId = "csharp-rudp-helper-server",
                RuntimeKind = "csharp"
            });
            var hello = client.ReceiveSchemaMessage<CultNetHelloMessage>(TimeSpan.FromSeconds(1));
            Assert.That(hello, Is.Not.Null);
            Assert.That(hello!.RuntimeId, Is.EqualTo("csharp-rudp-helper-server"));

            client.SendLatest("latest-state");
            var latest = server.ReceiveUntil(
                TimeSpan.FromSeconds(1),
                frame => string.Equals(frame.ChannelId, "latest", StringComparison.Ordinal));
            Assert.That(latest, Is.Not.Null);
            Assert.That(Encoding.UTF8.GetString(latest!.Payload), Is.EqualTo("latest-state"));

            client.SendRealtime("tick");
            var realtime = server.ReceiveUntil(
                TimeSpan.FromSeconds(1),
                frame => string.Equals(frame.ChannelId, "realtime", StringComparison.Ordinal));
            Assert.That(realtime, Is.Not.Null);
            Assert.That(Encoding.UTF8.GetString(realtime!.Payload), Is.EqualTo("tick"));
        }

        [Test]
        public void RudpSocketTransport_CarriesFragmentedReliableOrderedSchemaFrames()
        {
            using var serverSocket = BindUdpSocket();
            using var clientSocket = BindUdpSocket();
            var serverEndPoint = serverSocket.LocalEndPoint!;
            const uint connectionId = 0x10203041;
            using var server = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-fragment-server",
                Socket = serverSocket,
                Mode = CultNetRudpSocketMode.Server,
                ConnectionId = connectionId,
                InitialSequence = 100,
                ResendDelayMs = 25,
                MaxFragmentBytes = 8
            });
            using var client = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-fragment-client",
                Socket = clientSocket,
                Mode = CultNetRudpSocketMode.Client,
                RemoteEndPoint = serverEndPoint,
                ConnectionId = connectionId,
                InitialSequence = 1,
                ResendDelayMs = 25,
                MaxFragmentBytes = 8
            });

            var payload = Encoding.UTF8.GetBytes("this-schema-frame-is-larger-than-one-rudp-fragment");
            client.Connect(Encoding.UTF8.GetBytes("join"));
            PumpRudpHandshake(client, server);
            client.Send("schema", payload);
            var serverFrame = ReceiveRudpFrame(server);
            Assert.That(serverFrame.ChannelId, Is.EqualTo("schema"));
            Assert.That(serverFrame.Payload, Is.EqualTo(payload));
            Assert.That(client.Stats.FramesSent, Is.EqualTo(1));
            Assert.That(server.Stats.FramesReceived, Is.EqualTo(1));
        }

        [Test]
        public void CultNetSchemaMessageSerialization_RoundTrips_RawSnapshotResponse()
        {
            var message = new CultNetSnapshotResponseRawMessage
            {
                MessageId = "snapshot-1",
                ShardId = "players-eu",
                ShardEpoch = 12,
                ShardLogSequence = 42,
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
            Assert.That(roundTrip.ShardId, Is.EqualTo("players-eu"));
            Assert.That(roundTrip.ShardEpoch, Is.EqualTo(12));
            Assert.That(roundTrip.ShardLogSequence, Is.EqualTo(42));
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
                CompactedThrough = 40,
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
            Assert.That(roundTrip.CompactedThrough, Is.EqualTo(40));
            Assert.That(roundTrip.Entries, Has.Length.EqualTo(1));
            Assert.That(roundTrip.Entries[0].Sequence, Is.EqualTo(42));
            Assert.That(roundTrip.Entries[0].Put, Is.Not.Null);
            Assert.That(roundTrip.Entries[0].Put!.Document.RecordKey, Is.EqualTo("player:42"));
        }

        [Test]
        public void CultNetSchemaMessageSerialization_RoundTrips_VerseCatalogResponse()
        {
            var message = new CultMeshVerseCatalogResponseMessage
            {
                MessageId = "verses-1",
                Verses =
                [
                    new CultMeshVerseDescriptorMessage
                    {
                        VerseId = "aetheria-main",
                        DisplayName = "Aetheria",
                        AuthorityModel = "OperatorCluster",
                        Compatibility = new CultMeshVerseCompatibilityMessage
                        {
                            TransportVersion = "cultmesh.v0",
                            RulesHash = "rules",
                            CompatibleVerseIds = ["aetheria-modded"],
                            RequiredPluginIds = ["core"],
                            OptionalPluginIds = ["skylands"]
                        },
                        DiscoveryEndpoints = ["cultmesh://aetheria.example.test:3075"],
                        AuthorityRuntimeIds = ["runtime-a"],
                        Description = "main branch"
                    }
                ]
            };

            var payload = CultNetSchemaMessageSerialization.Serialize(message);
            var roundTrip = (CultMeshVerseCatalogResponseMessage)CultNetSchemaMessageSerialization.Deserialize(payload);

            Assert.That(roundTrip.MessageId, Is.EqualTo("verses-1"));
            Assert.That(roundTrip.Verses, Has.Length.EqualTo(1));
            Assert.That(roundTrip.Verses[0].VerseId, Is.EqualTo("aetheria-main"));
            Assert.That(roundTrip.Verses[0].Compatibility.RequiredPluginIds, Is.EqualTo(["core"]));
        }

        [Test]
        public void CultNetSchemaMessageSerialization_RoundTrips_PeerExchangeResponse()
        {
            var message = new CultMeshPeerExchangeResponseMessage
            {
                MessageId = "pex-1",
                Peers =
                [
                    new CultMeshPeerCardMessage
                    {
                        PeerId = "peer-a",
                        VerseId = "aetheria-main",
                        Endpoints = ["cultnet://peer-a.example.test:3075"],
                        Roles = [CultMeshPeerRoles.Discovery, CultMeshPeerRoles.ReadReplica],
                        ShardIds = ["players"],
                        Region = "eu-west",
                        AuthorityLeaseId = "lease-1",
                        ExpiresAt = "2026-05-20T12:00:00.0000000Z",
                        Signature = "sig"
                    }
                ]
            };

            var payload = CultNetSchemaMessageSerialization.Serialize(message);
            var roundTrip = (CultMeshPeerExchangeResponseMessage)CultNetSchemaMessageSerialization.Deserialize(payload);

            Assert.That(roundTrip.MessageId, Is.EqualTo("pex-1"));
            Assert.That(roundTrip.Peers, Has.Length.EqualTo(1));
            Assert.That(roundTrip.Peers[0].PeerId, Is.EqualTo("peer-a"));
            Assert.That(roundTrip.Peers[0].Roles, Does.Contain(CultMeshPeerRoles.ReadReplica));
            Assert.That(roundTrip.Peers[0].AuthorityLeaseId, Is.EqualTo("lease-1"));
        }

        [Test]
        public void CultNetSchemaMessageSerialization_RoundTrips_SimulationObservation()
        {
            var claimHash = CultNetSimulationObservation.ComputeClaimHash("hit", "alice", "bob", "frame:100");
            var message = new CultNetSimulationObservationMessage
            {
                MessageId = "observation-1",
                Observation = new CultNetSimulationObservation
                {
                    WitnessRuntimeId = "watcher-1",
                    ShardId = "arena",
                    ShardEpoch = 4,
                    Frame = 100,
                    SubjectId = "bob",
                    ClaimKind = "hit",
                    ClaimHash = claimHash,
                    ClaimSummary = "alice hit bob first",
                    ObservedAt = "2026-05-19T12:00:00.0000000Z"
                }
            };

            var payload = CultNetSchemaMessageSerialization.Serialize(message);
            var roundTrip = (CultNetSimulationObservationMessage)CultNetSchemaMessageSerialization.Deserialize(payload);

            Assert.That(roundTrip.MessageId, Is.EqualTo("observation-1"));
            Assert.That(roundTrip.Observation.WitnessRuntimeId, Is.EqualTo("watcher-1"));
            Assert.That(roundTrip.Observation.Frame, Is.EqualTo(100));
            Assert.That(roundTrip.Observation.ClaimHash, Is.EqualTo(claimHash));
        }

        [Test]
        public void CultNetSchemaMessageSerialization_RoundTrips_SimulationConsensusCandidate()
        {
            var candidate = new CultNetSimulationConsensusCandidate(
                "arena",
                4,
                100,
                "bob",
                "hit",
                "claim-hash",
                "alice hit bob first",
                witnessCount: 3,
                supportWeight: 3d,
                totalWeight: 4d,
                hasQuorum: true);

            var message = CultNetSimulationConsensusCandidateMessage.FromCandidate("candidate-1", candidate);
            var payload = CultNetSchemaMessageSerialization.Serialize(message);
            var roundTrip = (CultNetSimulationConsensusCandidateMessage)CultNetSchemaMessageSerialization.Deserialize(payload);

            Assert.That(roundTrip.MessageId, Is.EqualTo("candidate-1"));
            Assert.That(roundTrip.ShardId, Is.EqualTo("arena"));
            Assert.That(roundTrip.WitnessCount, Is.EqualTo(3));
            Assert.That(roundTrip.SupportWeight, Is.EqualTo(3d));
            Assert.That(roundTrip.TotalWeight, Is.EqualTo(4d));
            Assert.That(roundTrip.Confidence, Is.EqualTo(0.75d));
            Assert.That(roundTrip.HasQuorum, Is.True);
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
        public async Task CultNetDatabase_RequiredTransaction_DefersRawPutPublicationUntilCommit()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry,
                RequireTransactionsForAuthoritativeWrites = true
            });
            var key = new CultRecordKey("player:transactional-raw");
            var message = registry.CreateRawDocumentPutMessage(
                "put-transactional-raw",
                new CultRecordHandle<PlayerData>(key),
                new PlayerData
                {
                    PlayerId = Guid.NewGuid(),
                    Email = "transactional@example.test",
                    PasswordHash = "hash",
                    Username = "Transactional"
                });
            var changes = new List<CultNetDatabaseChange<PlayerData>>();
            using var subscription = database.WatchRecord<PlayerData>(key)
                .Subscribe(change => changes.Add(change));

            Assert.That(
                async () => await database.ApplyPutAsync(message),
                Throws.TypeOf<InvalidOperationException>());

            var staged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transaction = database.ExecuteTransactionAsync(async () =>
            {
                await database.ApplyPutAsync(message);
                staged.SetResult(true);
                await release.Task;
            });
            await staged.Task;

            Assert.That(cache.Get<PlayerData>(key), Is.Null);
            Assert.That(changes, Is.Empty);

            release.SetResult(true);
            await transaction;

            Assert.That(cache.Get<PlayerData>(key)?.Username, Is.EqualTo("Transactional"));
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].Kind, Is.EqualTo(CultNetDatabaseChangeKind.Added));
        }

        [Test]
        public async Task CultNetDatabase_ApplyRawPut_ResolvesForeignSchemaIdFromPayload()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(cache.Registry));
            var descriptor = cache.Registry.GetRequired<NetworkSchemaNote>();
            var shard = new CultNetShardDescriptor(
                "network-notes",
                "runtime-a",
                epoch: 3,
                isPrimary: true,
                schemaIds: [descriptor.SchemaId],
                keyPrefix: "network-note:",
                primaryEndpoints: ["cultnet://runtime-a:3075"]);
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry,
                Shards = [shard]
            });
            var key = new CultRecordKey("network-note:foreign-schema-put");
            var changes = new List<CultNetDatabaseChange<NetworkSchemaNote>>();
            using var subscription = database.WatchRecord<NetworkSchemaNote>(key)
                .Subscribe(change => changes.Add(change));
            var message = new CultNetDocumentPutRawMessage
            {
                MessageId = "put-foreign-schema",
                ShardId = shard.ShardId,
                ShardEpoch = shard.Epoch,
                Document = new CultNetRawDocumentRecord
                {
                    SchemaId = "runtime.generated.network-note.ui.42",
                    RecordKey = key.Value,
                    StoredAt = DateTimeOffset.UtcNow.ToString("O"),
                    PayloadEncoding = "messagepack",
                    Payload = CultDocumentMessagePackSerialization.Serialize(new NetworkSchemaNote
                    {
                        Schema = "tests.networking_note.v1",
                        Text = "payload-routed",
                        Revision = 5
                    })
                }
            };

            var applied = await database.ApplyPutAsync(message);

            Assert.That(applied, Is.TypeOf<NetworkSchemaNote>());
            Assert.That(cache.Get<NetworkSchemaNote>(key)!.Text, Is.EqualTo("payload-routed"));
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].SchemaId, Is.EqualTo(descriptor.SchemaId));
            Assert.That(changes[0].Document!.Revision, Is.EqualTo(5));
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
        public async Task CultNetDatabaseServer_Creates_SnapshotResponse_ForCompatibleSchemaAlias()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(cache.Registry));
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry
            });
            using var server = new Server(cache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(server, database);
            var key = new CultRecordKey("network-note:alias-snapshot");
            var note = new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "alias snapshot",
                Revision = 7
            };
            await database.PutAsync(key, note);

            var response = databaseServer.CreateSnapshotResponse(registry.CreateSnapshotRequest(
                "snapshot-alias-request",
                schemaIds: ["tests.networking_note.v1"],
                recordKeys: [key.Value]));

            Assert.That(response.Documents, Has.Length.EqualTo(1));
            Assert.That(response.Documents[0].RecordKey, Is.EqualTo(key.Value));
            Assert.That(response.Documents[0].Payload, Is.EqualTo(CultDocumentMessagePackSerialization.Serialize(note)));
        }

        [Test]
        public async Task CultNetDatabase_Creates_ShardSnapshotResponse_ForCompatibleSchemaAlias()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(cache.Registry));
            var descriptor = cache.Registry.GetRequired<NetworkSchemaNote>();
            var shard = new CultNetShardDescriptor(
                "network-notes",
                "runtime-a",
                epoch: 4,
                isPrimary: true,
                schemaIds: [descriptor.SchemaId],
                keyPrefix: "network-note:",
                primaryEndpoints: ["cultnet://runtime-a:3075"]);
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry,
                Shards = [shard]
            });
            var key = new CultRecordKey("network-note:alias-shard-snapshot");
            var note = new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "alias shard snapshot",
                Revision = 8
            };
            await database.PutAsync(key, note);

            var response = database.CreateShardSnapshotResponse(
                shard,
                "snapshot-shard-alias-request",
                registry.CreateSnapshotRequest(
                    "snapshot-shard-alias-request",
                    schemaIds: ["tests.networking_note.v1"],
                    recordKeys: [key.Value]));

            Assert.That(response.Documents, Has.Length.EqualTo(1));
            Assert.That(response.Documents[0].RecordKey, Is.EqualTo(key.Value));
            Assert.That(response.Documents[0].Payload, Is.EqualTo(CultDocumentMessagePackSerialization.Serialize(note)));
        }

        [Test]
        public async Task CultNetDatabase_Routes_TypedWrite_ToShardSchemaAlias()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(cache.Registry));
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry,
                Shards =
                [
                    new CultNetShardDescriptor(
                        "wrong-network-notes",
                        "runtime-b",
                        epoch: 1,
                        isPrimary: false,
                        schemaIds: ["other.note.v1"],
                        keyPrefix: "network-note:"),
                    new CultNetShardDescriptor(
                        "network-notes",
                        "runtime-a",
                        epoch: 2,
                        isPrimary: true,
                        schemaIds: ["tests.networking_note.v1"],
                        keyPrefix: "network-note:")
                ]
            });
            var key = new CultRecordKey("network-note:alias-routed-write");

            await database.PutAsync(key, new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "typed write routed by alias",
                Revision = 12
            });

            Assert.That(cache.Get<NetworkSchemaNote>(key)!.Text, Is.EqualTo("typed write routed by alias"));
            Assert.That(database.GetMutationLog("network-notes"), Has.Count.EqualTo(1));
            Assert.That(database.GetMutationLog("wrong-network-notes"), Is.Empty);
        }

        [Test]
        public async Task CultNetDatabase_Applies_Delete_BySchemaAlias()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(cache.Registry));
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry,
                Shards =
                [
                    new CultNetShardDescriptor(
                        "wrong-network-notes",
                        "runtime-b",
                        epoch: 1,
                        isPrimary: false,
                        schemaIds: ["other.note.v1"],
                        keyPrefix: "network-note:"),
                    new CultNetShardDescriptor(
                        "network-notes",
                        "runtime-a",
                        epoch: 2,
                        isPrimary: true,
                        schemaIds: ["tests.networking_note.v1"],
                        keyPrefix: "network-note:")
                ]
            });
            var key = new CultRecordKey("network-note:alias-delete");
            await database.PutAsync(key, new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "delete through alias",
                Revision = 13
            });

            await database.ApplyDeleteAsync(new CultNetDocumentDeleteMessage
            {
                MessageId = "delete-alias",
                SchemaId = "tests.networking_note.v1",
                RecordKey = key.Value,
                ShardId = "network-notes",
                ShardEpoch = 2
            });

            Assert.That(cache.Get<NetworkSchemaNote>(key), Is.Null);
            Assert.That(database.GetMutationLog("network-notes").Last().Kind, Is.EqualTo(CultNetDatabaseChangeKind.Removed));
            Assert.That(database.GetMutationLog("wrong-network-notes"), Is.Empty);
        }

        [Test]
        public async Task CultNetDatabase_Predicts_ClientOwnedInput_ThroughSchemaAliasScope()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(cache.Registry));
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                RuntimeId = "pilot-a",
                DocumentRegistry = registry,
                Shards =
                [
                    new CultNetShardDescriptor(
                        "wrong-network-inputs",
                        "server",
                        epoch: 1,
                        isPrimary: false,
                        schemaIds: ["other.note.v1"],
                        keyPrefix: "network-input:"),
                    new CultNetShardDescriptor(
                        "network-inputs",
                        "server",
                        epoch: 1,
                        isPrimary: false,
                        schemaIds: ["tests.networking_note.v1"],
                        keyPrefix: "network-input:")
                ],
                ClientAuthorityScopes =
                [
                    new CultNetClientAuthorityScope(
                        "pilot-a",
                        schemaIds: ["tests.networking_note.v1"],
                        keyPrefix: "network-input:pilot-a")
                ]
            });
            var key = new CultRecordKey("network-input:pilot-a:alias-prediction");

            await database.PutPredictedAsync(key, new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "predicted through alias scope",
                Revision = 1
            });

            Assert.That(cache.Get<NetworkSchemaNote>(key)!.Text, Is.EqualTo("predicted through alias scope"));
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

            var aliasResponse = databaseServer.CreateShardCatalogResponse(new CultNetShardCatalogRequestMessage
            {
                MessageId = "catalog-players-alias",
                SchemaIds = ["gamecult.player_data.v2"],
                RecordKeys = ["player:one"]
            });

            Assert.That(aliasResponse.Shards, Has.Length.EqualTo(1));
            Assert.That(aliasResponse.Shards[0].ShardId, Is.EqualTo("players"));
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
        public void CultNetSchemaWriteForwarder_Parses_RudpEndpoints()
        {
            var parsed = CultNetSchemaWriteForwarder.ParseEndpoint("rudp://primary.example.test:5075");

            Assert.That(parsed.Host, Is.EqualTo("primary.example.test"));
            Assert.That(parsed.Port, Is.EqualTo(5075));
        }

        [Test]
        public void CultNetSchemaWriteForwarder_Rejects_InvalidEndpoint()
        {
            Assert.That(
                () => CultNetSchemaWriteForwarder.ParseEndpoint("http://primary.example.test:3075"),
                Throws.TypeOf<FormatException>());
        }

        [Test]
        public void CultNetSchemaClients_CreateForEndpoint_Selects_RudpClient()
        {
            using var rudp = CultNetSchemaClients.CreateForEndpoint("rudp://127.0.0.1:5075");
            using var liteNetLib = CultNetSchemaClients.CreateForEndpoint(
                "cultnet://127.0.0.1:3075",
                DevelopmentClientSecurity);

            Assert.That(rudp, Is.TypeOf<RudpCultNetSchemaClient>());
            Assert.That(liteNetLib, Is.TypeOf<LiteNetLibCultNetSchemaClient>());
        }

        [Test]
        public async Task RudpCultNetSchemaClient_Carries_SchemaMessages()
        {
            using var serverSocket = BindUdpSocket();
            using var server = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-schema-server",
                Mode = CultNetRudpSocketMode.Server,
                Socket = serverSocket,
                ConnectionId = 0x43554c54,
                InitialSequence = 100,
                TransportId = "schema-rudp",
                ResendDelayMs = 25,
                MaxFragmentBytes = 1024
            });
            var serverDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serverThread = new Thread(() =>
            {
                try
                {
                    while (!serverDone.Task.IsCompleted)
                    {
                        var request = server.ReceiveSchemaMessageOnce<CultNetSchemaCatalogRequestMessage>();
                        server.PollResends();
                        if (request == null)
                        {
                            Thread.Sleep(5);
                            continue;
                        }

                        server.SendSchemaMessage(new CultNetSchemaCatalogResponseMessage
                        {
                            MessageId = request.MessageId,
                            Schemas =
                            [
                                new CultNetSchemaDescriptor
                                {
                                    SchemaId = "rudp.schema.test",
                                    Kind = "wire_message",
                                    SchemaVersion = "rudp.schema.test.v0",
                                    WireContracts = ["cultnet.schema.v0"]
                                }
                            ]
                        });
                        serverDone.TrySetResult();
                        return;
                    }
                }
                catch (Exception error)
                {
                    serverDone.TrySetException(error);
                }
            })
            {
                IsBackground = true
            };
            serverThread.Start();

            using var client = CultNetSchemaClients.CreateRudp(runtimeId: "csharp-rudp-schema-client");
            var responseCompletion = new TaskCompletionSource<CultNetSchemaCatalogResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnCultNet<CultNetSchemaCatalogResponseMessage>(response => responseCompletion.TrySetResult(response));
            client.Connect("127.0.0.1", ((IPEndPoint)serverSocket.LocalEndPoint!).Port);
            await WaitUntilAsync(() => client.Connected, TimeSpan.FromSeconds(2));
            client.SendCultNet(new CultNetSchemaCatalogRequestMessage
            {
                MessageId = "rudp-schema-client-test",
                IncludeSchemaJson = false
            });

            var response = await AwaitWithTimeout(responseCompletion.Task, TimeSpan.FromSeconds(2));
            await AwaitWithTimeout(serverDone.Task, TimeSpan.FromSeconds(2));

            Assert.That(response.MessageId, Is.EqualTo("rudp-schema-client-test"));
            Assert.That(response.Schemas.Single().SchemaId, Is.EqualTo("rudp.schema.test"));
        }

        [Test]
        public async Task CultMeshVerseDiscoveryClient_Fetches_Over_RudpEndpoint()
        {
            using var serverSocket = BindUdpSocket();
            using var server = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-verse-discovery-server",
                Mode = CultNetRudpSocketMode.Server,
                Socket = serverSocket,
                ConnectionId = 0x43554c54,
                InitialSequence = 100,
                TransportId = "schema-rudp",
                ResendDelayMs = 25,
                MaxFragmentBytes = 1024
            });
            var serverDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serverThread = new Thread(() =>
            {
                try
                {
                    while (!serverDone.Task.IsCompleted)
                    {
                        var request = server.ReceiveSchemaMessageOnce<CultMeshVerseCatalogRequestMessage>();
                        server.PollResends();
                        if (request == null)
                        {
                            Thread.Sleep(5);
                            continue;
                        }

                        server.SendSchemaMessage(new CultMeshVerseCatalogResponseMessage
                        {
                            MessageId = request.MessageId,
                            Verses =
                            [
                                new CultMeshVerseDescriptor(
                                    "rudp-verse",
                                    "RUDP Verse",
                                    CultMeshVerseAuthorityModel.OperatorCluster,
                                    new CultMeshVerseCompatibility("cultmesh.v0", "rules"),
                                    discoveryEndpoints: ["rudp://127.0.0.1"]).ToMessage()
                            ]
                        });
                        serverDone.TrySetResult();
                        return;
                    }
                }
                catch (Exception error)
                {
                    serverDone.TrySetException(error);
                }
            })
            {
                IsBackground = true
            };
            serverThread.Start();

            var client = new CultMeshVerseDiscoveryClient(new CultMeshVerseDiscoveryClientOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                ResponseTimeout = TimeSpan.FromSeconds(2)
            });
            var response = await client.FetchAsync($"rudp://127.0.0.1:{((IPEndPoint)serverSocket.LocalEndPoint!).Port}");
            await AwaitWithTimeout(serverDone.Task, TimeSpan.FromSeconds(2));

            Assert.That(response.Verses.Single().VerseId, Is.EqualTo("rudp-verse"));
        }

        [Test]
        public async Task CultMeshPeerExchangeClient_Fetches_Over_RudpEndpoint()
        {
            using var serverSocket = BindUdpSocket();
            using var server = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = "csharp-rudp-peer-exchange-server",
                Mode = CultNetRudpSocketMode.Server,
                Socket = serverSocket,
                ConnectionId = 0x43554c54,
                InitialSequence = 100,
                TransportId = "schema-rudp",
                ResendDelayMs = 25,
                MaxFragmentBytes = 1024
            });
            var serverDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serverThread = new Thread(() =>
            {
                try
                {
                    while (!serverDone.Task.IsCompleted)
                    {
                        var request = server.ReceiveSchemaMessageOnce<CultMeshPeerExchangeRequestMessage>();
                        server.PollResends();
                        if (request == null)
                        {
                            Thread.Sleep(5);
                            continue;
                        }

                        server.SendSchemaMessage(new CultMeshPeerExchangeResponseMessage
                        {
                            MessageId = request.MessageId,
                            Peers =
                            [
                                new CultMeshPeerCard(
                                    "rudp-peer",
                                    request.VerseId,
                                    ["rudp://127.0.0.1"],
                                    roles: ["mesh-peer"]).ToMessage()
                            ]
                        });
                        serverDone.TrySetResult();
                        return;
                    }
                }
                catch (Exception error)
                {
                    serverDone.TrySetException(error);
                }
            })
            {
                IsBackground = true
            };
            serverThread.Start();

            var client = new CultMeshPeerExchangeClient(new CultMeshPeerExchangeClientOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                ResponseTimeout = TimeSpan.FromSeconds(2)
            });
            var response = await client.FetchAsync(
                $"rudp://127.0.0.1:{((IPEndPoint)serverSocket.LocalEndPoint!).Port}",
                new CultMeshPeerExchangeRequestMessage { VerseId = "local" });
            await AwaitWithTimeout(serverDone.Task, TimeSpan.FromSeconds(2));

            Assert.That(response.Peers.Single().PeerId, Is.EqualTo("rudp-peer"));
        }

        [Test]
        public void CultMesh_CreateClient_Returns_CultNetClient()
        {
            using var client = CultMesh.CreateClient(DevelopmentClientSecurity);

            Assert.That(client, Is.Not.Null);
            Assert.That(client.Connected, Is.False);
        }

        [Test]
        public void CultMesh_CreateVerseDiscoveryClient_Returns_DiscoveryClient()
        {
            var client = CultMesh.CreateVerseDiscoveryClient(new CultMeshVerseDiscoveryClientOptions
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(250),
                ResponseTimeout = TimeSpan.FromMilliseconds(250)
            });

            Assert.That(client, Is.Not.Null);
        }

        [Test]
        public void CultMesh_CreatePeerExchangeClient_Returns_ExchangeClient()
        {
            var client = CultMesh.CreatePeerExchangeClient(new CultMeshPeerExchangeClientOptions
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(250),
                ResponseTimeout = TimeSpan.FromMilliseconds(250)
            });

            Assert.That(client, Is.Not.Null);
        }

        [Test]
        public async Task CultMeshVerseDiscoveryClient_Uses_SchemaClientPort()
        {
            var fake = new CapturingSchemaClient(message =>
            {
                var request = (CultMeshVerseCatalogRequestMessage)message;
                return new CultMeshVerseCatalogResponseMessage
                {
                    MessageId = request.MessageId,
                    Verses =
                    [
                        new CultMeshVerseDescriptor(
                            "local",
                            "Local Verse",
                            CultMeshVerseAuthorityModel.OperatorCluster,
                            new CultMeshVerseCompatibility("cultmesh.v0", "rules"),
                            discoveryEndpoints: ["cultnet://mesh.example.test:4075"]).ToMessage()
                    ]
                };
            });
            var client = new CultMeshVerseDiscoveryClient(new CultMeshVerseDiscoveryClientOptions
            {
                CreateClient = () => fake
            });

            var response = await client.FetchAsync("cultnet://mesh.example.test:4075");

            Assert.That(fake.ConnectedHost, Is.EqualTo("mesh.example.test"));
            Assert.That(fake.ConnectedPort, Is.EqualTo(4075));
            Assert.That(fake.SentMessages.Single(), Is.TypeOf<CultMeshVerseCatalogRequestMessage>());
            Assert.That(response.Verses.Single().VerseId, Is.EqualTo("local"));
        }

        [Test]
        public async Task CultMeshVerseDiscoveryClient_ReportsDeterministicConnectionFailureTimeline()
        {
            var clock = new ManualCultMeshClock(new DateTimeOffset(2026, 7, 12, 18, 0, 0, TimeSpan.Zero));
            var diagnostics = new CultMeshDiagnosticBuffer();
            var client = new CultMeshVerseDiscoveryClient(new CultMeshVerseDiscoveryClientOptions
            {
                CreateClient = () => new NeverConnectedSchemaClient(),
                Clock = clock,
                Diagnostics = diagnostics,
                SourceId = "test-bootstrap",
                ConnectTimeout = TimeSpan.FromMilliseconds(50)
            });

            var error = Assert.ThrowsAsync<TimeoutException>(() =>
                client.FetchAsync("rudp://unavailable.example.test:4075"));
            Assert.That(error!.Message, Does.Contain("unavailable.example.test"));

            var timeline = diagnostics.Snapshot();
            Assert.That(timeline.Select(item => item.Kind), Is.EqualTo(new[]
            {
                CultMeshDiagnosticKind.ConnectionAttempt,
                CultMeshDiagnosticKind.CandidateRejected
            }));
            Assert.That(timeline[0].ObservedAtUtc, Is.EqualTo(new DateTimeOffset(2026, 7, 12, 18, 0, 0, TimeSpan.Zero)));
            Assert.That(timeline[1].ObservedAtUtc, Is.EqualTo(new DateTimeOffset(2026, 7, 12, 18, 0, 0, 50, TimeSpan.Zero)));
            Assert.That(timeline[1].State, Is.EqualTo("unavailable"));
            Assert.That(timeline[1].ReasonCode, Is.EqualTo("timeout"));
            Assert.That(timeline[1].SourceId, Is.EqualTo("test-bootstrap"));
            Assert.That(timeline[1].LibraryVersion, Is.Not.Empty);
        }

        [Test]
        public async Task CultMeshVerseDiscoveryClient_CompatibilityDiscoveryKeepsGoodConcurrentSource()
        {
            var clients = new ConcurrentQueue<ICultNetSchemaClient>();
            clients.Enqueue(new NeverConnectedSchemaClient());
            clients.Enqueue(new CapturingSchemaClient(message =>
            {
                var request = (CultMeshVerseCatalogRequestMessage)message;
                return new CultMeshVerseCatalogResponseMessage
                {
                    MessageId = request.MessageId,
                    Verses = [new CultMeshVerseDescriptor(
                        "aetheria", "Aetheria", CultMeshVerseAuthorityModel.OperatorCluster,
                        new CultMeshVerseCompatibility("cultmesh.v0", "rules"),
                        discoveryEndpoints: ["rudp://aetheria:3076"]).ToMessage()]
                };
            }));
            var client = new CultMeshVerseDiscoveryClient(new CultMeshVerseDiscoveryClientOptions
            {
                CreateClient = () => clients.TryDequeue(out var next) ? next : throw new InvalidOperationException("Unexpected client."),
                Clock = new ManualCultMeshClock(new DateTimeOffset(2026, 7, 12, 18, 0, 0, TimeSpan.Zero)),
                ConnectTimeout = TimeSpan.FromMilliseconds(50)
            });
            using var catalog = new CultMeshVerseCatalog();

            var count = await client.DiscoverAsync(
                catalog,
                new[] { "rudp://dead.example.test:4075", "rudp://good.example.test:4075" });

            Assert.That(count, Is.EqualTo(1));
            Assert.That(catalog.Verses.Single().VerseId, Is.EqualTo("aetheria"));
        }

        [Test]
        public async Task CultMeshPeerExchangeClient_Uses_SchemaClientPort()
        {
            var fake = new CapturingSchemaClient(message =>
            {
                var request = (CultMeshPeerExchangeRequestMessage)message;
                return new CultMeshPeerExchangeResponseMessage
                {
                    MessageId = request.MessageId,
                    Peers =
                    [
                        new CultMeshPeerCard(
                            "peer-a",
                            request.VerseId,
                            ["cultnet://peer-a.example.test:3075"],
                            roles: ["shard-primary"]).ToMessage()
                    ]
                };
            });
            var client = new CultMeshPeerExchangeClient(new CultMeshPeerExchangeClientOptions
            {
                CreateClient = () => fake
            });

            var response = await client.FetchAsync(
                "cultnet://mesh.example.test:4075",
                new CultMeshPeerExchangeRequestMessage { VerseId = "local" });

            Assert.That(fake.ConnectedHost, Is.EqualTo("mesh.example.test"));
            Assert.That(fake.SentMessages.Single(), Is.TypeOf<CultMeshPeerExchangeRequestMessage>());
            Assert.That(response.Peers.Single().PeerId, Is.EqualTo("peer-a"));
        }

        [Test]
        public async Task CultNetShardFetchers_Use_SchemaClientPort()
        {
            var logClient = new CapturingSchemaClient(message =>
            {
                var request = (CultNetShardLogRequestMessage)message;
                return new CultNetShardLogResponseMessage
                {
                    MessageId = request.MessageId,
                    ShardId = request.ShardId,
                    ShardEpoch = request.ShardEpoch ?? 0,
                    Entries = []
                };
            });
            var snapshotClient = new CapturingSchemaClient(message =>
            {
                var request = (CultNetSnapshotRequestMessage)message;
                return new CultNetSnapshotResponseRawMessage
                {
                    MessageId = request.MessageId,
                    ShardId = request.ShardId,
                    ShardEpoch = request.ShardEpoch,
                    Documents = []
                };
            });
            var shard = new CultNetShardDescriptor(
                "notes",
                "primary",
                7,
                isPrimary: false,
                schemaIds: ["note.v0"],
                primaryEndpoints: ["cultnet://primary.example.test:4075"]);

            var logFetcher = new CultNetSchemaShardLogFetcher(new CultNetSchemaShardLogFetcherOptions
            {
                CreateClient = () => logClient
            });
            var snapshotFetcher = new CultNetSchemaShardSnapshotFetcher(new CultNetSchemaShardSnapshotFetcherOptions
            {
                CreateClient = () => snapshotClient
            });

            var log = await logFetcher.FetchAsync(shard, afterSequence: 4, limit: 2);
            var snapshot = await snapshotFetcher.FetchAsync(shard);

            Assert.That(logClient.ConnectedHost, Is.EqualTo("primary.example.test"));
            Assert.That(logClient.SentMessages.Single(), Is.TypeOf<CultNetShardLogRequestMessage>());
            Assert.That(log.ShardId, Is.EqualTo("notes"));
            Assert.That(snapshotClient.SentMessages.Single(), Is.TypeOf<CultNetSnapshotRequestMessage>());
            Assert.That(snapshot.ShardEpoch, Is.EqualTo(7));
        }

        [Test]
        public async Task CultNetSchemaWriteForwarder_Uses_SchemaClientPort()
        {
            var fake = new CapturingSchemaClient(_ => null);
            var forwarder = new CultNetSchemaWriteForwarder(new CultNetSchemaWriteForwarderOptions
            {
                CreateClient = () => fake
            });
            var shard = new CultNetShardDescriptor(
                "notes",
                "primary",
                3,
                isPrimary: false,
                primaryEndpoints: ["cultnet://primary.example.test:4075"]);

            await forwarder.ForwardDeleteAsync(shard, new CultNetDocumentDeleteMessage
            {
                SchemaId = "note.v0",
                RecordKey = "note:1"
            });

            var sent = (CultNetDocumentDeleteMessage)fake.SentMessages.Single();
            Assert.That(fake.ConnectedHost, Is.EqualTo("primary.example.test"));
            Assert.That(sent.ShardId, Is.EqualTo("notes"));
            Assert.That(sent.ShardEpoch, Is.EqualTo(3));
        }

        [Test]
        public void CultMeshVerseCatalog_Finds_CompatibleTransferTargets()
        {
            var vanillaRules = CultMeshVerseDescriptor.ComputeRulesHash("aetheria", "rules:v1", "vanilla");
            var moddedRules = CultMeshVerseDescriptor.ComputeRulesHash("aetheria", "rules:v1", "skylands");
            var aetheria = new CultMeshVerseDescriptor(
                "aetheria-main",
                "Aetheria",
                CultMeshVerseAuthorityModel.OperatorCluster,
                new CultMeshVerseCompatibility("cultmesh.v0", vanillaRules),
                authorityRoutes:
                [
                    new CultMeshAuthorityRoute(
                        "gc-us-east",
                        "cultmesh://us-east.aetheria.example.test:3075",
                        [CultMeshProtocols.Documents.Value],
                        generation: "east-1"),
                    new CultMeshAuthorityRoute(
                        "gc-eu-west",
                        "cultmesh://eu-west.aetheria.example.test:3075",
                        [CultMeshProtocols.Documents.Value],
                        generation: "west-1")
                ]);
            var modded = new CultMeshVerseDescriptor(
                "aetheria-skylands",
                "Aetheria: Skylands",
                CultMeshVerseAuthorityModel.SubscribedOverlay,
                new CultMeshVerseCompatibility(
                    "cultmesh.v0",
                    moddedRules,
                    compatibleVerseIds: ["aetheria-main"],
                    requiredPluginIds: ["skylands"]),
                parentVerseId: "aetheria-main");
            var incompatible = new CultMeshVerseDescriptor(
                "other-game",
                "Other Game",
                CultMeshVerseAuthorityModel.PeerToPeer,
                new CultMeshVerseCompatibility("cultmesh.v0", CultMeshVerseDescriptor.ComputeRulesHash("other")));
            using var catalog = CultMesh.CreateVerseCatalog();
            var updates = new List<CultMeshVerseDescriptor>();
            using var subscription = catalog.Watch().Subscribe(update => updates.Add(update));

            catalog.Upsert(aetheria);
            catalog.Upsert(modded);
            catalog.Upsert(incompatible);
            var targets = catalog.FindTransferTargets(aetheria);

            Assert.That(updates, Has.Count.EqualTo(3));
            Assert.That(targets, Has.Count.EqualTo(1));
            Assert.That(targets[0].VerseId, Is.EqualTo("aetheria-skylands"));
            Assert.That(modded.CanTransferFrom(aetheria), Is.True);
            Assert.That(incompatible.CanTransferFrom(aetheria), Is.False);
        }

        [Test]
        public void CultMeshVerseDiscoveryServer_Creates_FilteredCatalogResponse()
        {
            using var catalog = CultMesh.CreateVerseCatalog();
            var rulesHash = CultMeshVerseDescriptor.ComputeRulesHash("aetheria", "vanilla");
            catalog.Upsert(new CultMeshVerseDescriptor(
                "aetheria-main",
                "Aetheria",
                CultMeshVerseAuthorityModel.OperatorCluster,
                new CultMeshVerseCompatibility("cultmesh.v0", rulesHash),
                discoveryEndpoints: ["cultmesh://aetheria.example.test:3075"],
                authorityRuntimeIds: ["runtime-a"]));
            catalog.Upsert(new CultMeshVerseDescriptor(
                "old-branch",
                "Old Branch",
                CultMeshVerseAuthorityModel.PeerToPeer,
                new CultMeshVerseCompatibility("cultmesh.legacy", rulesHash)));
            using var server = new Server(new CultCache(), DevelopmentServerSecurity);
            using var discovery = new CultMeshVerseDiscoveryServer(server, catalog);

            var response = discovery.CreateResponse(new CultMeshVerseCatalogRequestMessage
            {
                MessageId = "discover-1",
                TransportVersion = "cultmesh.v0"
            });

            Assert.That(response.MessageId, Is.EqualTo("discover-1"));
            Assert.That(response.Verses, Has.Length.EqualTo(1));
            Assert.That(response.Verses[0].VerseId, Is.EqualTo("aetheria-main"));
            Assert.That(response.Verses[0].DiscoveryEndpoints, Is.EqualTo(["cultmesh://aetheria.example.test:3075"]));
        }

        [Test]
        public void CultMeshVerseCatalog_Upserts_WireDiscoveryResponse()
        {
            using var catalog = CultMesh.CreateVerseCatalog();
            var updates = new List<CultMeshVerseDescriptor>();
            using var subscription = catalog.Watch().Subscribe(updates.Add);

            catalog.Upsert(new CultMeshVerseCatalogResponseMessage
            {
                MessageId = "verses-apply",
                Verses =
                [
                    new CultMeshVerseDescriptorMessage
                    {
                        VerseId = "aetheria-branch",
                        DisplayName = "Aetheria Branch",
                        AuthorityModel = nameof(CultMeshVerseAuthorityModel.SubscribedOverlay),
                        Compatibility = new CultMeshVerseCompatibilityMessage
                        {
                            TransportVersion = "cultmesh.v0",
                            RulesHash = "branch-rules",
                            CompatibleVerseIds = ["aetheria-main"]
                        },
                        ParentVerseId = "aetheria-main"
                    }
                ]
            });

            var verse = catalog.Get("aetheria-branch");

            Assert.That(verse, Is.Not.Null);
            Assert.That(verse!.AuthorityModel, Is.EqualTo(CultMeshVerseAuthorityModel.SubscribedOverlay));
            Assert.That(verse.ParentVerseId, Is.EqualTo("aetheria-main"));
            Assert.That(updates, Has.Count.EqualTo(1));
        }

        [Test]
        public void CultMeshPeerExchangeServer_Creates_FilteredPeerResponse()
        {
            using var catalog = CultMesh.CreatePeerCatalog();
            catalog.Upsert(new CultMeshPeerCard(
                "peer-primary",
                "aetheria-main",
                ["cultnet://primary.example.test:3075"],
                roles: [CultMeshPeerRoles.ShardPrimary],
                shardIds: ["players"],
                region: "us-east",
                authorityLeaseId: "lease-primary"));
            catalog.Upsert(new CultMeshPeerCard(
                "peer-read",
                "aetheria-main",
                ["cultnet://read.example.test:3075"],
                roles: [CultMeshPeerRoles.ReadReplica],
                shardIds: ["players"],
                region: "eu-west"));
            using var server = new Server(new CultCache(), DevelopmentServerSecurity);
            using var exchange = new CultMeshPeerExchangeServer(server, catalog);

            var response = exchange.CreateResponse(new CultMeshPeerExchangeRequestMessage
            {
                MessageId = "pex-filter",
                VerseId = "aetheria-main",
                Roles = [CultMeshPeerRoles.ReadReplica],
                KnownPeerIds = ["already-known"]
            });

            Assert.That(response.MessageId, Is.EqualTo("pex-filter"));
            Assert.That(response.Peers, Has.Length.EqualTo(1));
            Assert.That(response.Peers[0].PeerId, Is.EqualTo("peer-read"));
            Assert.That(response.Peers[0].Roles, Does.Contain(CultMeshPeerRoles.ReadReplica));
        }

        [Test]
        public void CultMeshPeerCatalog_Upserts_WirePeerExchangeResponse()
        {
            using var catalog = CultMesh.CreatePeerCatalog();
            var updates = new List<CultMeshPeerCard>();
            using var subscription = catalog.Watch().Subscribe(updates.Add);

            catalog.Upsert(new CultMeshPeerExchangeResponseMessage
            {
                MessageId = "pex-apply",
                Peers =
                [
                    new CultMeshPeerCardMessage
                    {
                        PeerId = "peer-observer",
                        VerseId = "aetheria-main",
                        Endpoints = ["cultnet://observer.example.test:3075"],
                        Roles = [CultMeshPeerRoles.SimulationObserver],
                        ShardIds = ["arena"]
                    }
                ]
            });

            var peers = catalog.Find("aetheria-main", CultMeshPeerRoles.SimulationObserver);

            Assert.That(peers, Has.Count.EqualTo(1));
            Assert.That(peers[0].PeerId, Is.EqualTo("peer-observer"));
            Assert.That(updates, Has.Count.EqualTo(1));
        }

        [Test]
        public void CultMeshAuthorityResolver_Authorizes_PeerRoleAndShard()
        {
            var now = DateTimeOffset.Parse("2026-05-20T12:00:00.0000000Z");
            var catalog = CultMesh.CreateAuthorityLeaseCatalog();
            var resolver = new CultMeshAuthorityResolver(
                catalog,
                new AcceptingAuthoritySignatureVerifier(),
                new NoAuthorityRevocations(),
                new ManualCultMeshClock(now));
            using var peers = CultMesh.CreatePeerCatalog();
            var peer = new CultMeshPeerCard(
                "peer-primary",
                "aetheria-main",
                ["cultnet://primary.example.test:3075"],
                roles: [CultMeshPeerRoles.ShardPrimary],
                shardIds: ["players-us-east"],
                authorityLeaseId: "lease-primary");
            peers.Upsert(peer);

            Assert.That(peers.FindAuthorized(
                "aetheria-main", CultMeshPeerRoles.ShardPrimary, resolver, 1, "players-us-east"), Is.Empty);
            catalog.Upsert(new CultMeshAuthorityLease(
                "lease-primary",
                "aetheria-main",
                "peer-primary",
                [CultMeshPeerRoles.ShardPrimary],
                ["players-us-east"],
                "gc-operator",
                now.AddMinutes(-5),
                now.AddMinutes(5),
                signature: "sig",
                authorityEpoch: 1));

            Assert.That(resolver.Resolve(new CultMeshAuthorityRequest(
                peer, CultMeshPeerRoles.ShardPrimary, "players-us-east", 1)).IsAuthorized, Is.True);
            Assert.That(peers.FindAuthorized(
                "aetheria-main", CultMeshPeerRoles.ShardPrimary, resolver, 1, "players-us-east").Single(), Is.SameAs(peer));
            Assert.That(peers.FirstAuthorized(
                "aetheria-main", CultMeshPeerRoles.ShardPrimary, resolver, 1, "players-us-east"), Is.SameAs(peer));
            Assert.That(peers.FirstAuthorized(
                "aetheria-main", CultMeshPeerRoles.ReadReplica, resolver, 1, "players-us-east"), Is.Null);
            Assert.That(resolver.Resolve(new CultMeshAuthorityRequest(
                peer, CultMeshPeerRoles.ShardPrimary, "players-eu", 1)).IsAuthorized, Is.False);
            Assert.That(resolver.Resolve(new CultMeshAuthorityRequest(
                peer, CultMeshPeerRoles.ReadReplica, "players-us-east", 1)).IsAuthorized, Is.False);
#pragma warning disable CS0618
            Assert.That(catalog.IsAuthorized(peer, CultMeshPeerRoles.ShardPrimary, "players-us-east", now), Is.False);
#pragma warning restore CS0618
        }

        [Test]
        public void CultMeshRudpClient_DefaultBindAllowsRemoteRoutes()
        {
            Assert.That(new CultMeshRudpSocketOptions().BindHost, Is.EqualTo("0.0.0.0"));
        }

        [Test]
        public void CultMeshRudpFacade_CreatesAuthorizedClientFromPeerEndpoint()
        {
            var connectionId = 0x10203045u;
            using var server = CultMesh.CreateRudpServer(
                "csharp-cultmesh-rudp-server",
                connectionId,
                new CultMeshRudpSocketOptions
                {
                    InitialSequence = 100,
                    ResendDelayMs = 25,
                    MaxFragmentBytes = 1024,
                    MaxPendingReliablePackets = 16
                });
            var serverPort = server.Profile.Transports[0].Port;
            Assert.That(serverPort, Is.Not.Null);
            var endpoint = CultMesh.ParseRudpEndpoint($"rudp://127.0.0.1:{serverPort}");
            Assert.That(endpoint.Host, Is.EqualTo("127.0.0.1"));
            Assert.That(endpoint.Port, Is.EqualTo(serverPort));

            using var peers = CultMesh.CreatePeerCatalog();
            var leases = CultMesh.CreateAuthorityLeaseCatalog();
            var now = DateTimeOffset.UtcNow;
            var authority = new CultMeshAuthorityResolver(
                leases,
                new AcceptingAuthoritySignatureVerifier(),
                new NoAuthorityRevocations(),
                new ManualCultMeshClock(now));
            var peer = new CultMeshPeerCard(
                "csharp-cultmesh-rudp-server",
                "local",
                [endpoint.Uri],
                roles: ["schema"],
                authorityLeaseId: "lease:csharp-cultmesh-rudp-server");
            peers.Upsert(peer);

            Assert.Throws<InvalidOperationException>(() => CultMesh.CreateRudpClientForAuthorizedPeer(
                "csharp-cultmesh-rudp-client",
                connectionId,
                peers,
                authority,
                1,
                "local",
                "schema",
                options: new CultMeshRudpSocketOptions
                {
                    ResendDelayMs = 25,
                    MaxFragmentBytes = 1024,
                    MaxPendingReliablePackets = 16
                }));

            leases.Upsert(new CultMeshAuthorityLease(
                "lease:csharp-cultmesh-rudp-server",
                "local",
                "csharp-cultmesh-rudp-server",
                ["schema"],
                [],
                "csharp-authority",
                now.AddSeconds(-1),
                now.AddSeconds(30),
                signature: "sig",
                authorityEpoch: 1));

            using var serverPumpCts = new CancellationTokenSource();
            var serverPump = Task.Run(() =>
            {
                while (!server.Connected && !serverPumpCts.IsCancellationRequested)
                {
                    _ = server.ReceiveOnce();
                    server.PollResends();
                    Thread.Sleep(TimeSpan.FromMilliseconds(5));
                }
            });

            using var client = CultMesh.ConnectRudpClientForAuthorizedPeer(
                "csharp-cultmesh-rudp-client",
                connectionId,
                peers,
                authority,
                1,
                "local",
                "schema",
                options: new CultMeshRudpClientOptions
                {
                    ConnectPayload = Encoding.UTF8.GetBytes("join"),
                    SocketOptions = new CultMeshRudpSocketOptions
                    {
                        ResendDelayMs = 25,
                        MaxFragmentBytes = 1024,
                        MaxPendingReliablePackets = 16
                    }
                });
            serverPumpCts.Cancel();
            Assert.That(serverPump.Wait(TimeSpan.FromSeconds(1)), Is.True);

            Assert.That(client.Connected, Is.True);
            Assert.That(server.Connected, Is.True);

            client.SendSchemaMessage(new CultNetSchemaCatalogRequestMessage
            {
                MessageId = "csharp-cultmesh-rudp-schema-catalog",
                Kinds = ["document_payload"],
                IncludeSchemaJson = true
            });
            var request = ReceiveRudpSchemaMessage<CultNetSchemaCatalogRequestMessage>(server);
            Assert.That(request.MessageId, Is.EqualTo("csharp-cultmesh-rudp-schema-catalog"));
            Assert.That(request.Kinds, Is.EqualTo(new[] { "document_payload" }));
            Assert.That(server.Profile.Transports[0].Protocol, Is.EqualTo("rudp"));
            Assert.That(client.Profile.Transports[0].Protocol, Is.EqualTo("rudp"));
        }

        [Test]
        public async Task CultMeshNode_CanEnable_DurableShardLogs_WithDefaultPath()
        {
            var rootPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cultmesh-node", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var cachePath = Path.Combine(rootPath, "world.ccmp");
            var cache = new CultCache();
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var options = new CultMeshNodeOptions
            {
                StartServer = false,
                EnableDurableShardLogs = true,
                DatabaseOptions = new CultNetDatabaseOptions
                {
                    DocumentRegistry = registry,
                    Shards =
                    [
                        new CultNetShardDescriptor(
                            "players-mesh-default-log",
                            "mesh-runtime",
                            epoch: 1,
                            isPrimary: true,
                            schemaIds: [schemaId],
                            keyPrefix: "player:")
                    ]
                }
            };
            var key = new CultRecordKey("player:mesh-log");
            var player = new PlayerData
            {
                PlayerId = Guid.NewGuid(),
                Email = "mesh-log@example.test",
                PasswordHash = "hash",
                Username = "MeshLog"
            };

            using (var node = await CultMesh.CreateNodeAsync(cachePath, options))
            {
                await node.Database.PutAsync(key, player);
            }

            using var restarted = await CultMesh.CreateNodeAsync(cachePath, options);
            var response = restarted.DatabaseServer.CreateShardLogResponse(new CultNetShardLogRequestMessage
            {
                ShardId = "players-mesh-default-log"
            });

            Assert.That(response.Entries, Has.Length.EqualTo(1));
            Assert.That(response.Entries[0].Put, Is.Not.Null);
            Assert.That(response.Entries[0].Put!.Document.RecordKey, Is.EqualTo(key.Value));
            Assert.That(Directory.Exists(Path.Combine(rootPath, "world.cultmesh", "shard-logs")), Is.True);
        }

        [Test]
        public async Task CultMeshNode_CanRoundTrip_DurableTypedDocument_Through_PublicDatabaseSurface()
        {
            var rootPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cultmesh-quickstart", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var cachePath = Path.Combine(rootPath, "world.ccmp");
            var documents = new CultNetDocumentRegistry()
                .Register(CultNetDocumentBinding.ForDocument<MeshQuickstartNote>());
            var key = new CultRecordKey("note:intro");

            using (var node = await CultMesh.CreateNodeAsync(cachePath, new CultMeshNodeOptions
            {
                StartServer = false,
                DatabaseOptions = new CultNetDatabaseOptions
                {
                    RuntimeId = "quickstart-local",
                    DocumentRegistry = documents
                }
            }))
            {
                await node.Database.PutAsync(key, new MeshQuickstartNote
                {
                    NoteId = key.Value,
                    Body = "hello from a durable CultMesh node"
                });

                var live = await node.Database.GetAsync<MeshQuickstartNote>(key);

                Assert.That(node.Store, Is.Not.Null);
                Assert.That(live, Is.Not.Null);
                Assert.That(live!.Body, Is.EqualTo("hello from a durable CultMesh node"));

                await node.FlushAsync();
            }

            using var reopened = await CultMesh.CreateNodeAsync(cachePath, new CultMeshNodeOptions
            {
                StartServer = false,
                DatabaseOptions = new CultNetDatabaseOptions
                {
                    RuntimeId = "quickstart-local",
                    DocumentRegistry = documents
                }
            });
            var stored = await reopened.Database.GetAsync<MeshQuickstartNote>(key);

            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.NoteId, Is.EqualTo(key.Value));
            Assert.That(stored.Body, Is.EqualTo("hello from a durable CultMesh node"));
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
        public async Task CultNetDatabaseServer_Reads_DurableShardLog_AfterRestart()
        {
            var rootPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "shard-logs", Guid.NewGuid().ToString("N"));
            var schemaId = new CultCache().Registry.GetRequired<PlayerData>().SchemaId;
            var shard = new CultNetShardDescriptor(
                "players-durable-log",
                "runtime-a",
                epoch: 7,
                isPrimary: true,
                schemaIds: [schemaId],
                keyPrefix: "player:");

            var sourceCache = new CultCache();
            var sourceRegistry = new CultNetDocumentRegistry(sourceCache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    sourceCache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var sourceDatabase = new CultNetDatabase(sourceCache, new CultNetDatabaseOptions
            {
                DocumentRegistry = sourceRegistry,
                MutationLogStore = new CultNetFileShardMutationLogStore(rootPath),
                Shards = [shard]
            });
            var player = new PlayerData
            {
                PlayerId = Guid.NewGuid(),
                Email = "durable@example.test",
                PasswordHash = "hash",
                Username = "Durable"
            };

            await sourceDatabase.PutAsync(new CultRecordKey("player:durable"), player);
            sourceDatabase.Dispose();

            var restartedCache = new CultCache();
            var restartedDatabase = new CultNetDatabase(restartedCache, new CultNetDatabaseOptions
            {
                MutationLogStore = new CultNetFileShardMutationLogStore(rootPath),
                Shards = [shard]
            });
            using var server = new Server(restartedCache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(server, restartedDatabase);

            var response = databaseServer.CreateShardLogResponse(new CultNetShardLogRequestMessage
            {
                MessageId = "durable-log",
                ShardId = "players-durable-log",
                ShardEpoch = 7
            });

            Assert.That(response.ResyncRequired, Is.False);
            Assert.That(response.Entries, Has.Length.EqualTo(1));
            Assert.That(response.Entries[0].Sequence, Is.EqualTo(1));
            Assert.That(response.Entries[0].Put, Is.Not.Null);
            Assert.That(response.Entries[0].Put!.Document.SchemaId, Is.EqualTo(schemaId));
            Assert.That(response.Entries[0].Put!.Document.RecordKey, Is.EqualTo("player:durable"));
            Assert.That(response.Entries[0].Put!.Document.Payload, Is.EqualTo(SerializePlayerDataPayload(player)));
        }

        [Test]
        public async Task CultNetDatabaseServer_RequiresResync_ForCompactedShardLogHistory()
        {
            var rootPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "shard-logs", Guid.NewGuid().ToString("N"));
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var store = new CultNetFileShardMutationLogStore(rootPath);
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry,
                MutationLogStore = store,
                Shards =
                [
                    new CultNetShardDescriptor(
                        "players-compacted-log",
                        "runtime-a",
                        epoch: 8,
                        isPrimary: true,
                        schemaIds: [schemaId],
                        keyPrefix: "player:")
                ]
            });
            using var server = new Server(cache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(server, database);

            await database.PutAsync(
                new CultRecordKey("player:first"),
                new PlayerData { PlayerId = Guid.NewGuid(), Email = "first@example.test", PasswordHash = "hash", Username = "First" });
            await database.PutAsync(
                new CultRecordKey("player:second"),
                new PlayerData { PlayerId = Guid.NewGuid(), Email = "second@example.test", PasswordHash = "hash", Username = "Second" });
            store.CompactThrough("players-compacted-log", 1);

            var staleResponse = databaseServer.CreateShardLogResponse(new CultNetShardLogRequestMessage
            {
                ShardId = "players-compacted-log",
                ShardEpoch = 8,
                AfterSequence = 0
            });
            var currentResponse = databaseServer.CreateShardLogResponse(new CultNetShardLogRequestMessage
            {
                ShardId = "players-compacted-log",
                ShardEpoch = 8,
                AfterSequence = 1
            });

            Assert.That(staleResponse.ResyncRequired, Is.True);
            Assert.That(staleResponse.Reason, Is.EqualTo("compacted_history"));
            Assert.That(staleResponse.CompactedThrough, Is.EqualTo(1));
            Assert.That(staleResponse.Entries, Is.Empty);
            Assert.That(currentResponse.ResyncRequired, Is.False);
            Assert.That(currentResponse.Entries, Has.Length.EqualTo(1));
            Assert.That(currentResponse.Entries[0].Sequence, Is.EqualTo(2));
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
        public async Task CultNetDatabase_Predicts_ClientOwnedInput_AndReconciles_AuthoritativeLog()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var key = new CultRecordKey("input:client-a");
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                RuntimeId = "client-a",
                DocumentRegistry = registry,
                Shards =
                [
                    new CultNetShardDescriptor(
                        "inputs",
                        "server",
                        epoch: 1,
                        isPrimary: false,
                        schemaIds: [schemaId],
                        keyPrefix: "input:",
                        primaryEndpoints: ["cultnet://server.example.test:3075"])
                ],
                ClientAuthorityScopes =
                [
                    new CultNetClientAuthorityScope(
                        "client-a",
                        schemaIds: [schemaId],
                        keyPrefix: "input:client-a")
                ]
            });
            var changes = new List<CultNetDatabaseChange<PlayerData>>();
            using var subscription = database.WatchRecord<PlayerData>(key)
                .Subscribe(change => changes.Add(change));
            var predicted = new PlayerData
            {
                PlayerId = Guid.NewGuid(),
                Email = "input@example.test",
                PasswordHash = "hash",
                Username = "Predicted"
            };
            var authoritative = new PlayerData
            {
                PlayerId = predicted.PlayerId,
                Email = predicted.Email,
                PasswordHash = predicted.PasswordHash,
                Username = "Authoritative"
            };
            var put = registry.CreateRawDocumentPutMessage("input-commit", new CultRecordHandle<PlayerData>(key), authoritative);
            put.ShardId = "inputs";
            put.ShardEpoch = 1;

            await database.PutPredictedAsync(key, predicted);
            await database.ApplyShardLogResponseAsync(new CultNetShardLogResponseMessage
            {
                ShardId = "inputs",
                ShardEpoch = 1,
                Entries =
                [
                    new CultNetShardLogEntryMessage
                    {
                        Sequence = 1,
                        CommittedAt = "2026-05-19T12:00:00.0000000Z",
                        ChangeKind = "updated",
                        Put = put
                    }
                ]
            });

            Assert.That(cache.Get<PlayerData>(key)!.Username, Is.EqualTo("Authoritative"));
            Assert.That(changes.Exists(change => change.Kind == CultNetDatabaseChangeKind.Predicted), Is.True);
            Assert.That(changes.Exists(change => change.Kind == CultNetDatabaseChangeKind.Reconciled), Is.True);
        }

        [Test]
        public void CultNetDatabase_Rejects_Prediction_OutsideClientAuthorityScope()
        {
            var cache = new CultCache();
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                RuntimeId = "client-a",
                Shards =
                [
                    new CultNetShardDescriptor(
                        "inputs",
                        "server",
                        epoch: 1,
                        isPrimary: false,
                        schemaIds: [schemaId],
                        keyPrefix: "input:")
                ],
                ClientAuthorityScopes =
                [
                    new CultNetClientAuthorityScope(
                        "client-a",
                        schemaIds: [schemaId],
                        keyPrefix: "input:client-a")
                ]
            });

            Assert.That(
                async () => await database.PutPredictedAsync(
                    new CultRecordKey("input:client-b"),
                    new PlayerData
                    {
                        PlayerId = Guid.NewGuid(),
                        Email = "input-b@example.test",
                        PasswordHash = "hash",
                        Username = "WrongClient"
                    }),
                Throws.TypeOf<CultNetShardAuthorityException>()
                    .With.Property(nameof(CultNetShardAuthorityException.Reason))
                    .EqualTo("not_client_authority"));
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
        public async Task CultNetShardReplicator_Pulls_AndApplies_NextBatch()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var shard = new CultNetShardDescriptor(
                "players-pull",
                "runtime-a",
                epoch: 2,
                isPrimary: false,
                schemaIds: [schemaId],
                keyPrefix: "player:",
                primaryEndpoints: ["cultnet://primary.example.test:3075"]);
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry,
                Shards = [shard]
            });
            var key = new CultRecordKey("player:pull");
            var player = new PlayerData
            {
                PlayerId = Guid.NewGuid(),
                Email = "pull@example.test",
                PasswordHash = "hash",
                Username = "Pull"
            };
            var put = registry.CreateRawDocumentPutMessage("put-pull", new CultRecordHandle<PlayerData>(key), player);
            put.ShardId = "players-pull";
            put.ShardEpoch = 2;
            var fetcher = new CapturingShardLogFetcher(new CultNetShardLogResponseMessage
            {
                ShardId = "players-pull",
                ShardEpoch = 2,
                Entries =
                [
                    new CultNetShardLogEntryMessage
                    {
                        Sequence = 1,
                        CommittedAt = "2026-05-19T12:00:00.0000000Z",
                        ChangeKind = "added",
                        Put = put
                    }
                ]
            });
            using var replicator = new CultNetShardReplicator(database, new CultNetShardReplicatorOptions
            {
                Fetcher = fetcher,
                BatchSize = 7
            });

            var sequence = await replicator.PullOnceAsync("players-pull");

            Assert.That(sequence, Is.EqualTo(1));
            Assert.That(fetcher.FetchCount, Is.EqualTo(1));
            Assert.That(fetcher.LastShard!.ShardId, Is.EqualTo("players-pull"));
            Assert.That(fetcher.LastAfterSequence, Is.EqualTo(0));
            Assert.That(fetcher.LastLimit, Is.EqualTo(7));
            Assert.That(cache.Get<PlayerData>(key)!.Username, Is.EqualTo("Pull"));
        }

        [Test]
        public async Task CultNetShardReplicator_Resumes_From_FileCursorStore()
        {
            var path = Path.Combine(Path.GetTempPath(), $"cultnet-cursors-{Guid.NewGuid():N}.mpack");
            try
            {
                var store = new CultNetFileShardReplicaCursorStore(path);
                await store.WriteAsync(new CultNetShardReplicaCursor
                {
                    ShardId = "players-cursor",
                    ShardEpoch = 3,
                    LastAppliedSequence = 5,
                    UpdatedAt = "2026-05-19T12:00:00.0000000Z"
                });
                var cache = new CultCache();
                var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
                var shard = new CultNetShardDescriptor(
                    "players-cursor",
                    "runtime-a",
                    epoch: 3,
                    isPrimary: false,
                    schemaIds: [schemaId],
                    keyPrefix: "player:",
                    primaryEndpoints: ["cultnet://primary.example.test:3075"]);
                var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
                {
                    Shards = [shard]
                });
                var fetcher = new CapturingShardLogFetcher(new CultNetShardLogResponseMessage
                {
                    ShardId = "players-cursor",
                    ShardEpoch = 3
                });
                using var replicator = new CultNetShardReplicator(database, new CultNetShardReplicatorOptions
                {
                    Fetcher = fetcher,
                    CursorStore = store
                });

                var sequence = await replicator.PullOnceAsync("players-cursor");
                var cursor = await store.ReadAsync("players-cursor");

                Assert.That(fetcher.LastAfterSequence, Is.EqualTo(5));
                Assert.That(sequence, Is.EqualTo(5));
                Assert.That(database.GetAppliedShardSequence("players-cursor"), Is.EqualTo(5));
                Assert.That(cursor, Is.Not.Null);
                Assert.That(cursor!.LastAppliedSequence, Is.EqualTo(5));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public async Task CultNetShardReplicator_AppliesSnapshot_WhenLogHistoryWasCompacted()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<PlayerData>(
                    cache.Registry,
                    payloadSerializer: SerializePlayerDataPayload,
                    payloadDeserializer: DeserializePlayerDataPayload));
            var schemaId = cache.Registry.GetRequired<PlayerData>().SchemaId;
            var shard = new CultNetShardDescriptor(
                "players-snapshot-resync",
                "runtime-a",
                epoch: 9,
                isPrimary: false,
                schemaIds: [schemaId],
                keyPrefix: "player:",
                primaryEndpoints: ["cultnet://primary.example.test:3075"]);
            var keepKey = new CultRecordKey("player:keep");
            var goneKey = new CultRecordKey("player:gone");
            await cache.UpsertAsync(
                new PlayerData { PlayerId = Guid.NewGuid(), Email = "keep-old@example.test", PasswordHash = "hash", Username = "KeepOld" },
                new CultRecordHandle<PlayerData>(keepKey));
            await cache.UpsertAsync(
                new PlayerData { PlayerId = Guid.NewGuid(), Email = "gone@example.test", PasswordHash = "hash", Username = "Gone" },
                new CultRecordHandle<PlayerData>(goneKey));
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry,
                Shards = [shard]
            });
            var keep = new PlayerData
            {
                PlayerId = Guid.NewGuid(),
                Email = "keep-new@example.test",
                PasswordHash = "hash",
                Username = "KeepNew"
            };
            var put = registry.CreateRawDocumentPutMessage(
                "snapshot-keep",
                new CultRecordHandle<PlayerData>(keepKey),
                keep);
            var logFetcher = new CapturingShardLogFetcher(new CultNetShardLogResponseMessage
            {
                ShardId = shard.ShardId,
                ShardEpoch = shard.Epoch,
                ResyncRequired = true,
                Reason = "compacted_history",
                CompactedThrough = 2
            });
            var snapshotFetcher = new CapturingShardSnapshotFetcher(new CultNetSnapshotResponseRawMessage
            {
                MessageId = "snapshot-resync",
                ShardId = shard.ShardId,
                ShardEpoch = shard.Epoch,
                ShardLogSequence = 4,
                Documents = [put.Document]
            });
            using var replicator = new CultNetShardReplicator(database, new CultNetShardReplicatorOptions
            {
                Fetcher = logFetcher,
                SnapshotFetcher = snapshotFetcher
            });

            var sequence = await replicator.PullOnceAsync(shard);

            Assert.That(sequence, Is.EqualTo(4));
            Assert.That(database.GetAppliedShardSequence(shard.ShardId), Is.EqualTo(4));
            Assert.That(snapshotFetcher.FetchCount, Is.EqualTo(1));
            Assert.That(cache.Get<PlayerData>(goneKey), Is.Null);
            Assert.That(cache.Get<PlayerData>(keepKey)!.Username, Is.EqualTo("KeepNew"));
        }

        [Test]
        public void CultNetSimulationConsensus_Builds_QuorumCandidate_FromWitnesses()
        {
            var hitA = CultNetSimulationObservation.ComputeClaimHash("hit", "alice", "bob", "frame:100");
            var hitB = CultNetSimulationObservation.ComputeClaimHash("hit", "charlie", "bob", "frame:100");
            var consensus = new CultNetSimulationConsensus(new CultNetSimulationConsensusOptions
            {
                MinimumWitnesses = 2,
                QuorumRatio = 0.6d
            });

            var candidates = consensus.BuildCandidates(
            [
                new CultNetSimulationObservation
                {
                    WitnessRuntimeId = "watcher-1",
                    ShardId = "arena",
                    ShardEpoch = 4,
                    Frame = 100,
                    SubjectId = "bob",
                    ClaimKind = "hit",
                    ClaimHash = hitA,
                    ClaimSummary = "alice hit bob first"
                },
                new CultNetSimulationObservation
                {
                    WitnessRuntimeId = "watcher-2",
                    ShardId = "arena",
                    ShardEpoch = 4,
                    Frame = 100,
                    SubjectId = "bob",
                    ClaimKind = "hit",
                    ClaimHash = hitA
                },
                new CultNetSimulationObservation
                {
                    WitnessRuntimeId = "watcher-3",
                    ShardId = "arena",
                    ShardEpoch = 4,
                    Frame = 100,
                    SubjectId = "bob",
                    ClaimKind = "hit",
                    ClaimHash = hitB,
                    ClaimSummary = "charlie hit bob first"
                }
            ]);

            Assert.That(candidates, Has.Count.EqualTo(1));
            Assert.That(candidates[0].ClaimHash, Is.EqualTo(hitA));
            Assert.That(candidates[0].WitnessCount, Is.EqualTo(2));
            Assert.That(candidates[0].SupportWeight, Is.EqualTo(2d));
            Assert.That(candidates[0].TotalWeight, Is.EqualTo(3d));
            Assert.That(candidates[0].HasQuorum, Is.True);
        }

        [Test]
        public void CultNetSimulationConsensus_DeduplicatesWitness_AndBreaksTiesDeterministically()
        {
            var lowerHash = CultNetSimulationObservation.ComputeClaimHash("a");
            var higherHash = CultNetSimulationObservation.ComputeClaimHash("b");
            if (string.CompareOrdinal(lowerHash, higherHash) > 0)
            {
                (lowerHash, higherHash) = (higherHash, lowerHash);
            }
            var consensus = new CultNetSimulationConsensus(new CultNetSimulationConsensusOptions
            {
                QuorumRatio = 0.5d
            });

            var candidates = consensus.BuildCandidates(
            [
                new CultNetSimulationObservation
                {
                    WitnessRuntimeId = "watcher-1",
                    ShardId = "arena",
                    ShardEpoch = 1,
                    Frame = 20,
                    SubjectId = "door",
                    ClaimKind = "state",
                    ClaimHash = higherHash,
                    Weight = 1d
                },
                new CultNetSimulationObservation
                {
                    WitnessRuntimeId = "watcher-1",
                    ShardId = "arena",
                    ShardEpoch = 1,
                    Frame = 20,
                    SubjectId = "door",
                    ClaimKind = "state",
                    ClaimHash = lowerHash,
                    Weight = 0.5d
                },
                new CultNetSimulationObservation
                {
                    WitnessRuntimeId = "watcher-2",
                    ShardId = "arena",
                    ShardEpoch = 1,
                    Frame = 20,
                    SubjectId = "door",
                    ClaimKind = "state",
                    ClaimHash = lowerHash,
                    Weight = 1d
                }
            ]);

            Assert.That(candidates, Has.Count.EqualTo(1));
            Assert.That(candidates[0].ClaimHash, Is.EqualTo(lowerHash));
            Assert.That(candidates[0].TotalWeight, Is.EqualTo(2d));
            Assert.That(candidates[0].SupportWeight, Is.EqualTo(1d));
            Assert.That(candidates[0].HasQuorum, Is.True);
        }

        [Test]
        public void CultNetSimulationObservationHub_Emits_ReactiveCandidates()
        {
            var claimHash = CultNetSimulationObservation.ComputeClaimHash("hit", "alice", "bob", "frame:100");
            using var hub = new CultNetSimulationObservationHub(new CultNetSimulationConsensusOptions
            {
                MinimumWitnesses = 2,
                QuorumRatio = 1d
            });
            var observations = new List<CultNetSimulationObservation>();
            var candidates = new List<CultNetSimulationConsensusCandidate>();
            using var observationSubscription = hub.WatchObservations()
                .Subscribe(observation => observations.Add(observation));
            using var candidateSubscription = hub.WatchCandidates()
                .Subscribe(candidate => candidates.Add(candidate));

            hub.Submit(new CultNetSimulationObservation
            {
                WitnessRuntimeId = "watcher-1",
                ShardId = "arena",
                ShardEpoch = 1,
                Frame = 100,
                SubjectId = "bob",
                ClaimKind = "hit",
                ClaimHash = claimHash
            });
            var current = hub.Submit(new CultNetSimulationObservationMessage
            {
                Observation = new CultNetSimulationObservation
                {
                    WitnessRuntimeId = "watcher-2",
                    ShardId = "arena",
                    ShardEpoch = 1,
                    Frame = 100,
                    SubjectId = "bob",
                    ClaimKind = "hit",
                    ClaimHash = claimHash
                }
            });

            Assert.That(observations, Has.Count.EqualTo(2));
            Assert.That(current, Has.Count.EqualTo(1));
            Assert.That(current[0].HasQuorum, Is.True);
            Assert.That(candidates.Exists(candidate => candidate.HasQuorum), Is.True);
        }

        [Test]
        public void CultNetSimulationObservationServer_Creates_CandidateMessages()
        {
            var cache = new CultCache();
            using var server = new Server(cache, DevelopmentServerSecurity);
            using var hub = new CultNetSimulationObservationHub(new CultNetSimulationConsensusOptions
            {
                MinimumWitnesses = 2,
                QuorumRatio = 1d
            });
            using var observationServer = new CultNetSimulationObservationServer(server, hub);
            var claimHash = CultNetSimulationObservation.ComputeClaimHash("hit", "alice", "bob", "frame:100");
            var first = new CultNetSimulationObservationMessage
            {
                MessageId = "observe-1",
                Observation = new CultNetSimulationObservation
                {
                    WitnessRuntimeId = "watcher-1",
                    ShardId = "arena",
                    ShardEpoch = 1,
                    Frame = 100,
                    SubjectId = "bob",
                    ClaimKind = "hit",
                    ClaimHash = claimHash
                }
            };
            var second = new CultNetSimulationObservationMessage
            {
                MessageId = "observe-2",
                Observation = new CultNetSimulationObservation
                {
                    WitnessRuntimeId = "watcher-2",
                    ShardId = "arena",
                    ShardEpoch = 1,
                    Frame = 100,
                    SubjectId = "bob",
                    ClaimKind = "hit",
                    ClaimHash = claimHash
                }
            };

            observationServer.CreateCandidateMessages(first);
            var candidates = observationServer.CreateCandidateMessages(second);

            Assert.That(candidates, Has.Length.EqualTo(1));
            Assert.That(candidates[0].MessageId, Is.EqualTo("observe-2"));
            Assert.That(candidates[0].ClaimHash, Is.EqualTo(claimHash));
            Assert.That(candidates[0].HasQuorum, Is.True);
        }

        [Test]
        public async Task CultMeshSimulationFactCommitter_Commits_QuorumCandidate_ToShardLog()
        {
            var cache = new CultCache();
            var schemaId = cache.Registry.GetRequired<CultMeshSimulationFact>().SchemaId;
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                Shards =
                [
                    new CultNetShardDescriptor(
                        "arena",
                        "runtime-a",
                        epoch: 4,
                        isPrimary: true,
                        schemaIds: [schemaId],
                        keyPrefix: "simulation:")
                ]
            });
            var claimHash = CultNetSimulationObservation.ComputeClaimHash("hit", "alice", "bob", "frame:100");
            var candidate = new CultNetSimulationConsensusCandidate(
                "arena",
                4,
                100,
                "bob",
                "hit",
                claimHash,
                "alice shot bob first",
                witnessCount: 3,
                supportWeight: 3d,
                totalWeight: 3d,
                hasQuorum: true);
            var committer = CultMesh.CreateSimulationFactCommitter(database);

            var handle = await committer.CommitAsync(candidate);
            var fact = cache.Get<CultMeshSimulationFact>(handle.Key);
            var log = database.GetMutationLog("arena");

            Assert.That(handle.Key, Is.EqualTo(CultMeshSimulationFact.CreateRecordKey(candidate)));
            Assert.That(fact, Is.Not.Null);
            Assert.That(fact!.ClaimHash, Is.EqualTo(claimHash));
            Assert.That(fact.ClaimSummary, Is.EqualTo("alice shot bob first"));
            Assert.That(fact.Confidence, Is.EqualTo(1d));
            Assert.That(log, Has.Count.EqualTo(1));
            Assert.That(log[0].Kind, Is.EqualTo(CultNetDatabaseChangeKind.Added));
        }

        [Test]
        public async Task CultMeshGameSession_SubmitsWitnesses_AndCommitsQuorumFact()
        {
            var rootPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cultmesh-session", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var cachePath = Path.Combine(rootPath, "world.ccmp");
            var schemaId = new CultCache().Registry.GetRequired<CultMeshSimulationFact>().SchemaId;
            using var node = await CultMesh.CreateNodeAsync(cachePath, new CultMeshNodeOptions
            {
                StartServer = false,
                DatabaseOptions = new CultNetDatabaseOptions
                {
                    Shards =
                    [
                        new CultNetShardDescriptor(
                            "arena",
                            "runtime-a",
                            epoch: 4,
                            isPrimary: true,
                            schemaIds: [schemaId],
                            keyPrefix: "simulation:")
                    ]
                }
            });
            using var session = CultMesh.CreateGameSession(node, new CultMeshGameSessionOptions
            {
                ConsensusOptions = new CultNetSimulationConsensusOptions
                {
                    MinimumWitnesses = 2,
                    QuorumRatio = 1d
                },
                ServeSimulationObservations = false,
                ServeVerseDiscovery = false,
                ServePeerExchange = false
            });
            var committed = new List<CultNetDatabaseChange<CultMeshSimulationFact>>();
            using var facts = session.WatchSimulationFacts().Subscribe(committed.Add);
            var claimHash = CultNetSimulationObservation.ComputeClaimHash("hit", "alice", "bob", "frame:100");

            var firstCommit = await session.SubmitAndCommitAsync(new CultNetSimulationObservation
            {
                WitnessRuntimeId = "watcher-1",
                ShardId = "arena",
                ShardEpoch = 4,
                Frame = 100,
                SubjectId = "bob",
                ClaimKind = "hit",
                ClaimHash = claimHash,
                ClaimSummary = "alice shot bob first"
            });
            var secondCommit = await session.SubmitAndCommitAsync(new CultNetSimulationObservation
            {
                WitnessRuntimeId = "watcher-2",
                ShardId = "arena",
                ShardEpoch = 4,
                Frame = 100,
                SubjectId = "bob",
                ClaimKind = "hit",
                ClaimHash = claimHash,
                ClaimSummary = "alice shot bob first"
            });

            Assert.That(firstCommit, Is.Empty);
            Assert.That(secondCommit, Has.Count.EqualTo(1));
            Assert.That(node.Cache.Get<CultMeshSimulationFact>(secondCommit[0].Key)!.ClaimHash, Is.EqualTo(claimHash));
            Assert.That(committed.Exists(change => change.Document?.ClaimHash == claimHash), Is.True);
        }

        [Test]
        public void CultMeshSimulationFactCommitter_Rejects_CandidateWithoutQuorum()
        {
            var cache = new CultCache();
            var schemaId = cache.Registry.GetRequired<CultMeshSimulationFact>().SchemaId;
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                Shards =
                [
                    new CultNetShardDescriptor(
                        "arena",
                        "runtime-a",
                        epoch: 4,
                        isPrimary: true,
                        schemaIds: [schemaId],
                        keyPrefix: "simulation:")
                ]
            });
            var candidate = new CultNetSimulationConsensusCandidate(
                "arena",
                4,
                100,
                "bob",
                "hit",
                CultNetSimulationObservation.ComputeClaimHash("hit", "alice", "bob", "frame:100"),
                null,
                witnessCount: 1,
                supportWeight: 1d,
                totalWeight: 3d,
                hasQuorum: false);
            var committer = CultMesh.CreateSimulationFactCommitter(database);

            Assert.That(
                async () => await committer.CommitAsync(candidate),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("before quorum"));
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
        public void CultNetDatabaseServer_Creates_Filtered_SubscriptionChange_ForSchemaAlias()
        {
            var cache = new CultCache();
            var registry = new CultNetDocumentRegistry(cache.Registry)
                .Register(CultNetDocumentBinding.ForDocument<NetworkSchemaNote>(cache.Registry));
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                DocumentRegistry = registry
            });
            using var server = new Server(cache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(server, database);
            var key = new CultRecordKey("network-note:alias-change");
            var note = new NetworkSchemaNote
            {
                Schema = "tests.networking_note.v1",
                Text = "alias change",
                Revision = 11
            };
            var descriptor = cache.Registry.GetRequired<NetworkSchemaNote>();
            var change = new CultNetDatabaseChange<NetworkSchemaNote>(
                CultNetDatabaseChangeKind.Added,
                key,
                descriptor.SchemaId,
                database.Shards[0],
                note,
                previousDocument: null);
            var method = typeof(CultNetDatabaseServer).GetMethod(
                "CreateChangeMessage",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var message = (CultNetDatabaseChangeRawMessage?)method.Invoke(databaseServer, new object[]
            {
                change,
                "sub-alias",
                new CultNetDatabaseSubscribeMessage
                {
                    SubscriptionId = "sub-alias",
                    SchemaIds = ["tests.networking_note.v1"],
                    RecordKeys = [key.Value]
                }
            });

            Assert.That(message, Is.Not.Null);
            Assert.That(message!.SubscriptionId, Is.EqualTo("sub-alias"));
            Assert.That(message.ChangeKind, Is.EqualTo("added"));
            Assert.That(message.Document, Is.Not.Null);
            Assert.That(message.Document!.SchemaId, Is.EqualTo(descriptor.SchemaId));
            Assert.That(message.Document.Payload, Is.EqualTo(CultDocumentMessagePackSerialization.Serialize(note)));
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
            var subscribe = Array.Find(response.Schemas,
                schema => schema.SchemaVersion == CultNetSchemaVersions.DatabaseSubscribe);
            var unsubscribe = Array.Find(response.Schemas,
                schema => schema.SchemaVersion == CultNetSchemaVersions.DatabaseUnsubscribe);
            var change = Array.Find(response.Schemas,
                schema => schema.SchemaVersion == CultNetSchemaVersions.DatabaseChangeRaw);
            var ghostlight = Array.Find(response.Schemas,
                schema => schema.SchemaVersion == "ghostlight.agent_state.v0");
            var transportProfile = Array.Find(response.Schemas,
                schema => schema.SchemaVersion == "cultnet.transport_profile.v0");

            Assert.That(rawPut, Is.Not.Null);
            Assert.That(rawPut!.WireContracts, Is.EqualTo([CultNetWireContracts.SchemaV0]));
            Assert.That(rawPut.SchemaJson, Does.Contain("cultnet.document_put_raw.v0"));
            Assert.That(rawPut.ContentHash, Has.Length.EqualTo(64));

            Assert.That(rawSnapshot, Is.Not.Null);
            Assert.That(rawSnapshot!.SchemaJson, Does.Contain("cultnet.snapshot_response_raw.v0"));

            Assert.That(subscribe, Is.Not.Null);
            Assert.That(subscribe!.SchemaJson, Does.Contain("cultnet.database_subscribe.v0"));
            Assert.That(unsubscribe, Is.Not.Null);
            Assert.That(unsubscribe!.SchemaJson, Does.Contain("cultnet.database_unsubscribe.v0"));
            Assert.That(change, Is.Not.Null);
            Assert.That(change!.SchemaJson, Does.Contain("cultnet.database_change_raw.v0"));

            Assert.That(ghostlight, Is.Not.Null);
            Assert.That(ghostlight!.DocumentType, Is.EqualTo("ghostlight.agent-state"));
            Assert.That(ghostlight.Kind, Is.EqualTo("document_payload"));

            Assert.That(transportProfile, Is.Not.Null);
            Assert.That(transportProfile!.Kind, Is.EqualTo("shared_contract"));
            Assert.That(transportProfile.WireContracts, Is.EqualTo([CultNetWireContracts.SchemaV0]));
            Assert.That(transportProfile.SchemaJson, Does.Contain("cultnet.transport_profile.v0"));
            Assert.That(transportProfile.SchemaJson, Does.Contain("reconnectPolicy"));
            Assert.That(transportProfile.SchemaJson, Does.Contain("cultnet.reconnect_policy.v0"));
            Assert.That(transportProfile.SchemaJson, Does.Contain("litenetlib"));
        }

        [Test]
        public void CultWitnessArtifactBundle_Has_Stable_CacheContract()
        {
            var descriptor = new CultCache().Registry.GetRequired<CultWitnessArtifactBundle>();

            Assert.That(descriptor.SchemaName, Is.EqualTo("cultnet.witness_artifact_bundle"));
            Assert.That(descriptor.SchemaVersion, Is.EqualTo(CultNetSchemaVersions.WitnessArtifactBundle));
            Assert.That(descriptor.NameMember, Is.EqualTo(nameof(CultWitnessArtifactBundle.BundleId)));
            Assert.That(descriptor.CanonicalSchemaJson, Is.EqualTo(
                "{\"schemaName\":\"cultnet.witness_artifact_bundle\",\"schemaVersion\":\"cultnet.witness_artifact_bundle.v0\",\"members\":[{\"slot\":0,\"name\":\"BundleId\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":true},{\"slot\":1,\"name\":\"WitnessKind\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":2,\"name\":\"CapturedAt\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":3,\"name\":\"Subject\",\"type\":\"GameCult.Networking.CultWitnessSubjectPin\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":4,\"name\":\"Contracts\",\"type\":\"GameCult.Networking.CultWitnessContractPin[]\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":5,\"name\":\"Artifacts\",\"type\":\"GameCult.Networking.CultWitnessArtifactEntry[]\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":6,\"name\":\"TimingWitnesses\",\"type\":\"GameCult.Networking.CultWitnessTimingEntry[]\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":7,\"name\":\"Provenance\",\"type\":\"GameCult.Networking.CultWitnessProvenance\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false}]}"));
        }

        [Test]
        public void CultNetSchemaRegistry_BuiltInCatalog_Advertises_WitnessArtifactBundle()
        {
            var response = CultNetSchemaRegistry.BuiltIn.CreateCatalogResponse(
                new CultNetSchemaCatalogRequestMessage
                {
                    MessageId = "catalog-witness",
                    IncludeSchemaJson = true
                });
            var witnessBundle = Array.Find(response.Schemas,
                schema => schema.SchemaVersion == CultNetSchemaVersions.WitnessArtifactBundle);

            Assert.That(witnessBundle, Is.Not.Null);
            Assert.That(witnessBundle!.DocumentType, Is.EqualTo("cultnet.witness-artifact-bundle"));
            Assert.That(witnessBundle.Kind, Is.EqualTo("document_payload"));
            Assert.That(witnessBundle.WireContracts, Is.EqualTo([CultNetWireContracts.SchemaV0]));
            Assert.That(witnessBundle.SchemaJson, Does.Contain("\"bundleId\""));
            Assert.That(witnessBundle.SchemaJson, Does.Contain("\"timingWitnesses\""));
            Assert.That(witnessBundle.SchemaJson, Does.Contain("\"witnessArtifactUri\""));
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
            var method = typeof(Client).GetMethod(
                "GetReconnectDelayForAttempt",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(int) },
                null)!;
            var deterministicMethod = typeof(Client).GetMethod(
                "GetReconnectDelayForAttempt",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(int), typeof(int), typeof(CultNetReconnectPolicy) },
                null)!;

            var first = (TimeSpan)method.Invoke(null, new object[] { 1 })!;
            var third = (TimeSpan)method.Invoke(null, new object[] { 3 })!;
            var tenth = (TimeSpan)method.Invoke(null, new object[] { 10 })!;

            Assert.That(first, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(first, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(1.25)));
            Assert.That(third, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(4)));
            Assert.That(third, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(4.25)));
            Assert.That(tenth, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(tenth, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(30.25)));

            var deterministic = (TimeSpan)deterministicMethod.Invoke(
                null,
                new object[] { 3, 17, CultNetReconnectPolicies.CreateDefault(maxAttempts: 8) })!;
            Assert.That(deterministic, Is.EqualTo(TimeSpan.FromMilliseconds(4_017)));
        }

        [Test]
        public void Client_Reconnect_Uses_Portable_Policy_Controller()
        {
            var policy = CultNetReconnectPolicies.CreateDefault("client-rudp", maxAttempts: 2);
            var client = new Client(DevelopmentClientSecurity, policy);

            Assert.That(client.ReconnectPolicy, Is.SameAs(policy));
            Assert.That(client.ReconnectAttempt, Is.EqualTo(0));
            Assert.That(client.NextReconnectAttemptAtMs, Is.Null);

            var schedule = typeof(Client).GetMethod("ScheduleReconnect", BindingFlags.Instance | BindingFlags.NonPublic)!;
            schedule.Invoke(client, Array.Empty<object>());

            Assert.That(client.ReconnectAttempt, Is.EqualTo(1));
            Assert.That(client.NextReconnectAttemptAtMs, Is.Not.Null);
            Assert.That(client.ReconnectState, Is.EqualTo(ClientReconnectState.WaitingToReconnect));

            client.Dispose();
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

        [CultDocument("sample.mesh_note", "sample.mesh_note.v0")]
        [MessagePack.MessagePackObject]
        public sealed class MeshQuickstartNote
        {
            [MessagePack.Key(0)]
            [CultName]
            public string NoteId { get; set; } = string.Empty;

            [MessagePack.Key(1)]
            public string Body { get; set; } = string.Empty;
        }

        [CultDocument("tests.networking_note", "tests.networking_note.v1")]
        [MessagePack.MessagePackObject]
        public sealed class NetworkSchemaNote
        {
            [MessagePack.Key(0)]
            public string Schema { get; set; } = string.Empty;

            [MessagePack.Key(1)]
            public string Text { get; set; } = string.Empty;

            [MessagePack.Key(2)]
            public int Revision { get; set; }
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

        private sealed class CapturingShardLogFetcher : ICultNetShardLogFetcher
        {
            private readonly CultNetShardLogResponseMessage _response;

            public CapturingShardLogFetcher(CultNetShardLogResponseMessage response)
            {
                _response = response;
            }

            public int FetchCount { get; private set; }
            public CultNetShardDescriptor? LastShard { get; private set; }
            public long LastAfterSequence { get; private set; }
            public int? LastLimit { get; private set; }

            public Task<CultNetShardLogResponseMessage> FetchAsync(
                CultNetShardDescriptor shard,
                long afterSequence,
                int? limit = null)
            {
                FetchCount++;
                LastShard = shard;
                LastAfterSequence = afterSequence;
                LastLimit = limit;
                return Task.FromResult(_response);
            }
        }

        private sealed class CapturingShardSnapshotFetcher : ICultNetShardSnapshotFetcher
        {
            private readonly CultNetSnapshotResponseRawMessage _response;

            public CapturingShardSnapshotFetcher(CultNetSnapshotResponseRawMessage response)
            {
                _response = response;
            }

            public int FetchCount { get; private set; }
            public CultNetShardDescriptor? LastShard { get; private set; }

            public Task<CultNetSnapshotResponseRawMessage> FetchAsync(CultNetShardDescriptor shard)
            {
                FetchCount++;
                LastShard = shard;
                return Task.FromResult(_response);
            }
        }

        private sealed class CapturingSchemaClient : ICultNetSchemaClient
        {
            private readonly Func<ICultNetSchemaMessage, ICultNetSchemaMessage?> _respond;
            private readonly Dictionary<Type, List<Delegate>> _handlers = new Dictionary<Type, List<Delegate>>();

            public CapturingSchemaClient(Func<ICultNetSchemaMessage, ICultNetSchemaMessage?> respond)
            {
                _respond = respond;
            }

            public bool Connected { get; private set; }
            public string? ConnectedHost { get; private set; }
            public int ConnectedPort { get; private set; }
            public List<ICultNetSchemaMessage> SentMessages { get; } = new List<ICultNetSchemaMessage>();

            public void Connect(string host, int port)
            {
                Connected = true;
                ConnectedHost = host;
                ConnectedPort = port;
            }

            public void SendCultNet<T>(T message)
                where T : ICultNetSchemaMessage
            {
                SentMessages.Add(message);
                var response = _respond(message);
                if (response == null)
                {
                    return;
                }

                if (_handlers.TryGetValue(response.GetType(), out var handlers))
                {
                    foreach (var handler in handlers)
                    {
                        handler.DynamicInvoke(response);
                    }
                }
            }

            public void OnCultNet<T>(Action<T> callback)
                where T : ICultNetSchemaMessage
            {
                if (!_handlers.TryGetValue(typeof(T), out var handlers))
                {
                    handlers = new List<Delegate>();
                    _handlers[typeof(T)] = handlers;
                }

                handlers.Add(callback);
            }

            public void Dispose()
            {
            }
        }

        private sealed class NeverConnectedSchemaClient : ICultNetSchemaClient
        {
            public bool Connected => false;

            public void Connect(string host, int port)
            {
            }

            public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
            {
                throw new InvalidOperationException("A disconnected client cannot send.");
            }

            public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class ManualCultMeshClock : ICultMeshClock
        {
            public ManualCultMeshClock(DateTimeOffset utcNow)
            {
                UtcNow = utcNow;
            }

            public DateTimeOffset UtcNow { get; private set; }

            public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UtcNow += delay;
                return Task.CompletedTask;
            }
        }

        private sealed class AcceptingAuthoritySignatureVerifier : ICultMeshAuthoritySignatureVerifier
        {
            public bool Verify(CultMeshAuthorityLease lease) =>
                string.Equals(lease.Signature, "sig", StringComparison.Ordinal);
        }

        private sealed class NoAuthorityRevocations : ICultMeshAuthorityRevocationSource
        {
            public bool IsRevoked(string leaseId, long authorityEpoch) => false;
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
