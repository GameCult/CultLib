using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Networking;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

[TestFixture]
public sealed class CultMeshBodySessionTests
{
    [Test]
    public async Task LiveBodySessionTransfersVerifiedGenerationWithoutSnapshotRecords()
    {
        var bytes = new byte[384_000];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index % 251);
        using var store = new CultMeshNetworkBodyStore();
        var descriptor = store.Publish(Generation(sequence: 7), bytes);
        var server = new LoopbackServer();
        using var bodyServer = new CultMeshBodyServer(server, store);
        var client = new LoopbackClient(server);
        var connector = new LoopbackConnector(client);
        using var discovery = new CultMeshDiscoveryService(new[] { new RouteSource() });
        using var sessions = new CultMeshSessionManager(discovery, new[] { connector });
        var provider = new CultMeshSessionBodyProvider(
            "aetheria.daemon", sessions, new CultMeshSessionTarget("aetheria", "aetheria.daemon"));

        var received = await provider.FetchAsync(descriptor);

        received.Should().Equal(bytes);
        client.BodyRequestCount.Should().Be(1);
        client.SnapshotRequestCount.Should().Be(0,
            "live body bytes must never enter database snapshot machinery");
        connector.ConnectCount.Should().Be(1);
    }

    [Test]
    public async Task RudpBodySessionTransfersOneHotGenerationWithoutDatabaseRoundTrip()
    {
        var bytes = new byte[384_000];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index % 251);
        using var store = new CultMeshNetworkBodyStore();
        var descriptor = store.Publish(Generation(sequence: 11), bytes);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
        {
            RuntimeId = "cultmesh-body-rudp-test", Socket = socket, MaxFragmentBytes = 1024,
            MaxPendingReliablePackets = 8192
        });
        var endpoint = $"rudp://127.0.0.1:{server.LocalEndPoint.Port}";
        using var identityServer = new CultMeshSessionIdentityServer(
            server,
            "aetheria.daemon",
            new[] { "aetheria" },
            new[] { CultMeshProtocols.Bodies.Value },
            new[] { "aetheria.daemon@" + endpoint });
        using var bodyServer = new CultMeshBodyServer(server, store);
        using var pumpCancellation = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            while (!pumpCancellation.IsCancellationRequested)
            {
                var progress = await server.PollAvailableAsync(256);
                server.PollResends();
                if (progress.TransportItemsConsumed == 0) await Task.Delay(1, pumpCancellation.Token);
            }
        });
        using var discovery = new CultMeshDiscoveryService(new[] { new RouteSource(endpoint) });
        using var sessions = new CultMeshSessionManager(discovery,
            new ICultMeshTransportConnector[] { new CultMeshSchemaTransportConnector() });
        var provider = new CultMeshSessionBodyProvider("aetheria.daemon", sessions,
            new CultMeshSessionTarget("aetheria", "aetheria.daemon"),
            new CultMeshSessionBodyProviderOptions { ResponseTimeout = TimeSpan.FromSeconds(10) });

        var elapsed = Stopwatch.StartNew();
        byte[] received;
        try { received = await provider.FetchAsync(descriptor); }
        finally
        {
            pumpCancellation.Cancel();
            try { await pump; } catch (OperationCanceledException) { }
        }
        elapsed.Stop();

        received.Should().Equal(bytes);
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void NetworkBodyStoreRejectsGenerationMetadataThatDoesNotMatchCapability()
    {
        using var store = new CultMeshNetworkBodyStore();
        var descriptor = store.Publish(Generation(sequence: 3), new byte[] { 1, 2, 3 });
        var request = Request(descriptor);
        request.Sequence++;

        store.TryRead(request, DateTimeOffset.UtcNow, out _, out _).Should().BeFalse();
    }

    [Test]
    public void BodyWireMessagesRoundTripThroughSchemaDispatcher()
    {
        using var store = new CultMeshNetworkBodyStore();
        var descriptor = store.Publish(Generation(sequence: 9), new byte[] { 1, 2, 3 });
        var request = Request(descriptor);
        var response = new CultMeshBodyReadResponseMessage
        {
            MessageId = request.MessageId, Found = true, CapabilityToken = descriptor.CapabilityToken,
            BodyId = descriptor.BodyId, ProducerEpoch = descriptor.ProducerEpoch,
            Sequence = descriptor.Sequence, SizeBytes = 3, SemanticHash = descriptor.SemanticHash,
            Payload = new byte[] { 1, 2, 3 }
        };

        CultNetSchemaMessageSerialization.Deserialize(CultNetSchemaMessageSerialization.Serialize(request))
            .Should().BeEquivalentTo(request);
        CultNetSchemaMessageSerialization.Deserialize(CultNetSchemaMessageSerialization.Serialize(response))
            .Should().BeEquivalentTo(response);
    }

    private static CultMeshBodyGeneration Generation(long sequence) => new()
    {
        BodyId = "eve:entity-soa:aetheria.daemon:pilot", ProducerId = "aetheria.daemon",
        SchemaId = "gamecult.eve.entity_soa.body.v2", LayoutVersion = 3, Capacity = 4096,
        ProducerEpoch = 2, Sequence = sequence, Synchronization = CultMeshBodySynchronization.TripleBuffer,
        LeaseExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds()
    };

    private static CultMeshBodyReadRequestMessage Request(CultMeshBodyDescriptor descriptor) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"), CapabilityToken = descriptor.CapabilityToken,
        BodyId = descriptor.BodyId, BodySchemaId = descriptor.SchemaId, LayoutVersion = descriptor.LayoutVersion,
        ProducerEpoch = descriptor.ProducerEpoch, Sequence = descriptor.Sequence,
        ExpectedSizeBytes = descriptor.ByteSize, SemanticHash = descriptor.SemanticHash
    };

    private sealed class RouteSource : ICultMeshLookupSource
    {
        private readonly string _endpoint;
        public RouteSource(string endpoint = "rudp://body.test:3076") => _endpoint = endpoint;
        public string SourceId => "test";
        public Task<IReadOnlyList<CultMeshDiscoveryObservation>> LookupAsync(CultMeshDiscoveryQuery query,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(new[]
            {
                new CultMeshDiscoveryObservation(new CultMeshVerseDescriptor("aetheria", "Aetheria",
                    CultMeshVerseAuthorityModel.OperatorCluster, new CultMeshVerseCompatibility("cultmesh.v1", "rules"),
                    new[] { _endpoint }, new[] { "aetheria.daemon" }), SourceId, now, now.AddMinutes(1), CultMeshDiscoveryTrust.Signed)
            });
        }
    }

    private sealed class LoopbackConnector : ICultMeshTransportConnector
    {
        private readonly ICultNetSchemaClient _client;
        public LoopbackConnector(ICultNetSchemaClient client) => _client = client;
        public string ConnectorId => "loopback";
        public int Priority => 0;
        public int ConnectCount { get; private set; }
        public bool CanConnect(CultMeshTransportCandidate candidate) => true;
        public Task<ICultNetSchemaClient> ConnectAsync(CultMeshTransportCandidate candidate, CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default)
        {
            protocol.Should().Be(CultMeshProtocols.Bodies);
            ConnectCount++;
            return Task.FromResult(_client);
        }
    }

    private sealed class LoopbackServer : ICultNetSchemaServer
    {
        private readonly Dictionary<Type, Delegate> _handlers = new();
        public void OnCultNet<T>(Func<T, ICultNetSchemaServerPeer, Task> callback) where T : ICultNetSchemaMessage =>
            _handlers[typeof(T)] = callback;
        public void RemoveCultNetMessageListener<T>(Delegate callback) where T : ICultNetSchemaMessage =>
            _handlers.Remove(typeof(T));
        public Task Dispatch<T>(T message, ICultNetSchemaServerPeer peer) where T : ICultNetSchemaMessage =>
            ((Func<T, ICultNetSchemaServerPeer, Task>)_handlers[typeof(T)])(message, peer);
    }

    private sealed class LoopbackClient : ICultNetSchemaClient, ICultMeshVerifiedSchemaClient
    {
        private readonly LoopbackServer _server;
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();
        public LoopbackClient(LoopbackServer server) => _server = server;
        public bool Connected => true;
        public int BodyRequestCount { get; private set; }
        public int SnapshotRequestCount { get; private set; }
        public void Connect(string host, int port) { }
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
        {
            if (message is CultMeshBodyReadRequestMessage body)
            {
                BodyRequestCount++;
                _server.Dispatch(body, new LoopbackPeer(this)).GetAwaiter().GetResult();
            }
            else if (message is CultNetSnapshotRequestMessage) SnapshotRequestCount++;
        }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (!_handlers.TryGetValue(typeof(T), out var values)) _handlers[typeof(T)] = values = new List<Delegate>();
            values.Add(callback);
        }
        public void Emit<T>(T message) where T : ICultNetSchemaMessage
        {
            if (_handlers.TryGetValue(typeof(T), out var values))
                foreach (var value in values.ToArray()) ((Action<T>)value)(message);
        }
        public void Dispose() { }
        public bool IsVerifiedFor(string verseId, string authorityRuntimeId, string protocolId, string routeGeneration) =>
            verseId == "aetheria" && authorityRuntimeId == "aetheria.daemon";
    }

    private sealed class LoopbackPeer : ICultNetSchemaServerPeer
    {
        private readonly LoopbackClient _client;
        public LoopbackPeer(LoopbackClient client) => _client = client;
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage => _client.Emit(message);
    }
}
