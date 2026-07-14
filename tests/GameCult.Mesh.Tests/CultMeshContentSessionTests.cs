using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Networking;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

[TestFixture]
public sealed class CultMeshContentSessionTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() =>
        _directory = Path.Combine(Path.GetTempPath(), "cultmesh-content-session-tests", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Test]
    public async Task ContentSessionTransfersLargeBodyWithoutSnapshotPayloadsAndReusesCommittedBody()
    {
        var payload = Enumerable.Range(0, 700_000).Select(value => (byte)(value % 251)).ToArray();
        var artifact = CultMesh.PackCdnArtifact("aetheria/world/windows", payload);
        using var providerCache = new CultCache(CultMesh.CreateCultCacheDocumentRegistry(
            typeof(CultMeshCdnArtifactManifest), typeof(CultMeshCdnArtifactChunk)));
        await CultMeshCdn.PublishAsync(providerCache, artifact);
        var wireServer = new LoopbackSchemaServer();
        using var contentServer = new CultMeshContentServer(wireServer, providerCache);
        var wireClient = new LoopbackSchemaClient(wireServer);
        var connector = new LoopbackConnector(wireClient);
        using var discovery = new CultMeshDiscoveryService(new[] { new RouteSource() });
        using var sessions = new CultMeshSessionManager(discovery, new[] { connector });
        var provider = new CultMeshSessionContentProvider(
            "aetheria.daemon",
            sessions,
            CultMeshEndpointId.Parse("service:aetheria.daemon"));
        using var transferState = new CultCache(CultMesh.CreateCultCacheDocumentRegistry(
            typeof(CultMeshContentTransferStateDocument)));
        var transfer = new CultMeshContentTransferService(
            transferState,
            new[] { provider },
            new CultMeshContentTransferOptions(_directory));

        var first = await transfer.FetchAsync(artifact.Manifest);
        var requestsAfterCold = wireClient.ContentRequestCount;
        var second = await transfer.FetchAsync(artifact.Manifest);

        File.ReadAllBytes(first).Should().Equal(payload);
        second.Should().Be(first);
        requestsAfterCold.Should().Be(artifact.Manifest.Chunks.Length);
        wireClient.ContentRequestCount.Should().Be(requestsAfterCold,
            "a committed verified body is the warm-cache authority");
        wireClient.SnapshotRequestCount.Should().Be(0,
            "bulk content must never be represented as snapshot records");
        connector.ConnectCount.Should().Be(1,
            "all chunk requests borrow one identity-first content session");
    }

    [Test]
    public void ContentWireMessagesRoundTripThroughTheSchemaDispatcher()
    {
        var request = new CultMeshContentChunkRequestMessage
        {
            MessageId = "request-1",
            ChunkHash = new string('a', 64),
            RecordKey = "mesh:cdn:chunk:" + new string('a', 64),
            ExpectedSizeBytes = 42
        };
        var response = new CultMeshContentChunkResponseMessage
        {
            MessageId = "request-1",
            Found = true,
            ChunkHash = new string('a', 64),
            SizeBytes = 3,
            Payload = new byte[] { 1, 2, 3 }
        };

        CultNetSchemaMessageSerialization.Deserialize(CultNetSchemaMessageSerialization.Serialize(request))
            .Should().BeEquivalentTo(request);
        CultNetSchemaMessageSerialization.Deserialize(CultNetSchemaMessageSerialization.Serialize(response))
            .Should().BeEquivalentTo(response);
    }

    private sealed class RouteSource : ICultMeshLookupSource
    {
        public string SourceId => "odin";

        public Task<IReadOnlyList<CultMeshDiscoveryObservation>> LookupAsync(
            CultMeshDiscoveryQuery query,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(new[]
            {
                new CultMeshDiscoveryObservation(
                    new CultMeshVerseDescriptor(
                        "aetheria",
                        "Aetheria",
                        CultMeshVerseAuthorityModel.OperatorCluster,
                        new CultMeshVerseCompatibility("cultmesh.v1", "rules"),
                        new[] { "rudp://content.test:3076" }),
                    SourceId,
                    now,
                    now.AddMinutes(1),
                    CultMeshDiscoveryTrust.Signed)
            });
        }
    }

    private sealed class LoopbackConnector : ICultMeshTransportConnector
    {
        private readonly ICultNetSchemaClient _client;
        private int _connectCount;
        public LoopbackConnector(ICultNetSchemaClient client) => _client = client;
        public string ConnectorId => "loopback";
        public int ConnectCount => Volatile.Read(ref _connectCount);
        public bool CanConnect(CultMeshTransportCandidate candidate) => true;
        public Task<ICultNetSchemaClient> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            protocol.Should().Be(CultMeshProtocols.Content);
            return Task.FromResult(_client);
        }
    }

    private sealed class LoopbackSchemaServer : ICultNetSchemaServer
    {
        private readonly Dictionary<Type, Delegate> _handlers = new();

        public void OnCultNet<TMessage>(Func<TMessage, ICultNetSchemaServerPeer, Task> callback)
            where TMessage : ICultNetSchemaMessage => _handlers[typeof(TMessage)] = callback;

        public void RemoveCultNetMessageListener<TMessage>(Delegate callback)
            where TMessage : ICultNetSchemaMessage => _handlers.Remove(typeof(TMessage));

        public Task DispatchAsync<TMessage>(TMessage message, ICultNetSchemaServerPeer peer)
            where TMessage : ICultNetSchemaMessage =>
            ((Func<TMessage, ICultNetSchemaServerPeer, Task>)_handlers[typeof(TMessage)])(message, peer);
    }

    private sealed class LoopbackSchemaClient : ICultNetSchemaClient
    {
        private readonly LoopbackSchemaServer _server;
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();
        public LoopbackSchemaClient(LoopbackSchemaServer server) => _server = server;
        public bool Connected => true;
        public int ContentRequestCount { get; private set; }
        public int SnapshotRequestCount { get; private set; }
        public void Connect(string host, int port) { }
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
        {
            if (message is CultMeshContentChunkRequestMessage content)
            {
                ContentRequestCount++;
                _server.DispatchAsync(content, new LoopbackPeer(this)).GetAwaiter().GetResult();
            }
            else if (message is CultNetSnapshotRequestMessage)
            {
                SnapshotRequestCount++;
                throw new InvalidOperationException("Content session attempted to use a snapshot request.");
            }
        }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (!_handlers.TryGetValue(typeof(T), out var handlers))
                _handlers[typeof(T)] = handlers = new List<Delegate>();
            handlers.Add(callback);
        }
        public void Dispose() { }
        public void Emit<T>(T message) where T : ICultNetSchemaMessage
        {
            if (!_handlers.TryGetValue(typeof(T), out var handlers)) return;
            foreach (var handler in handlers.ToArray()) ((Action<T>)handler)(message);
        }
    }

    private sealed class LoopbackPeer : ICultNetSchemaServerPeer
    {
        private readonly LoopbackSchemaClient _client;
        public LoopbackPeer(LoopbackSchemaClient client) => _client = client;
        public void SendCultNet<TMessage>(TMessage message) where TMessage : ICultNetSchemaMessage =>
            _client.Emit(message);
    }
}
