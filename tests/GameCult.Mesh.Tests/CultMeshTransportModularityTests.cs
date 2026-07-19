using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Networking;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

[TestFixture]
public sealed class CultMeshTransportModularityTests
{
    [Test]
    public async Task SchemaSessionPrefersTcpTierWithoutTouchingLegacyDatagrams()
    {
        using var server = new TcpFramedCultNetSchemaServer(new TcpListener(IPAddress.Loopback, 0));
        server.OnCultNet<CultNetHelloMessage>((message, peer) =>
        {
            peer.SendCultNet(new CultNetErrorMessage { Error = "tcp:" + message.RuntimeId });
            return Task.CompletedTask;
        });
        var tcpEndpoint = $"cultnet+tcp://127.0.0.1:{server.LocalEndPoint.Port}";
        var legacy = new RejectingLegacySchemaConnector();
        using var discovery = new CultMeshDiscoveryService(new[]
        {
            new RouteSource("rudp://127.0.0.1:9", tcpEndpoint)
        });
        using var sessions = new CultMeshSessionManager(
            discovery,
            new ICultMeshTransportConnector[]
            {
                legacy,
                new CultMeshTcpSchemaTransportConnector()
            });

        var session = await sessions.ConnectAsync(
            CultMeshEndpointId.Parse("service:aetheria.daemon"),
            CultMeshProtocols.Documents);
        var response = new TaskCompletionSource<CultNetErrorMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = session.OnCultNet<CultNetErrorMessage>(message => response.TrySetResult(message));
        session.SendCultNet(new CultNetHelloMessage { RuntimeId = "eve-unity" });
        var received = await response.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.State.Path!.Endpoint.Should().Be(tcpEndpoint);
        received.Error.Should().Be("tcp:eve-unity");
        legacy.ConnectCount.Should().Be(0,
            "a lower transport tier is fallback, not a race against the preferred TCP tier");
    }

    [Test]
    public async Task ContentSessionPrefersTcpAndStreamsOutsideSchemaPayloads()
    {
        var payload = new byte[512 * 1024];
        new Random(1234).NextBytes(payload);
        var artifact = CultMesh.PackCdnArtifact("transport/modularity", payload);
        using var content = new CultCache(CultMesh.CreateCultCacheDocumentRegistry(
            typeof(CultMeshCdnArtifactManifest), typeof(CultMeshCdnArtifactChunk)));
        await CultMeshCdn.PublishAsync(content, artifact);
        using var server = new CultMeshTcpContentServer(new TcpListener(IPAddress.Loopback, 0), content);
        var tcpEndpoint = $"{CultMeshTcpContentTransportConnector.Scheme}://127.0.0.1:{server.LocalEndPoint.Port}";
        var legacy = new RejectingLegacyContentConnector();
        using var discovery = new CultMeshDiscoveryService(new[]
        {
            new RouteSource("rudp://127.0.0.1:9", tcpEndpoint)
        });
        using var sessions = new CultMeshSessionManager(
            discovery,
            Array.Empty<ICultMeshTransportConnector>(),
            new ICultMeshContentTransportConnector[]
            {
                legacy,
                new CultMeshTcpContentTransportConnector()
            });

        var session = await sessions.ConnectContentAsync(CultMeshEndpointId.Parse("service:aetheria.daemon"));
        using var destination = new MemoryStream();
        await session.CopyChunkToAsync(artifact.Manifest.Chunks[0], destination);

        session.TransportId.Should().Be("tcp-content");
        destination.ToArray().Should().Equal(artifact.Chunks[0].Payload);
        legacy.ConnectCount.Should().Be(0);
    }

    [Test]
    public async Task ContentSessionFailsClosedWithoutAnExplicitConnector()
    {
        using var discovery = new CultMeshDiscoveryService(new[]
        {
            new RouteSource("rudp://127.0.0.1:3076")
        });
        using var sessions = new CultMeshSessionManager(
            discovery,
            Array.Empty<ICultMeshTransportConnector>());

        Func<Task> connect = async () => await sessions.ConnectContentAsync(
            CultMeshEndpointId.Parse("service:aetheria.daemon"));

        await connect.Should().ThrowAsync<CultMeshSessionException>()
            .WithMessage("*No streaming content connectors are configured*");
    }

    [Test]
    public async Task RealtimeSessionUsesDedicatedQuicShapedConnectorContract()
    {
        var connector = new LoopbackRealtimeConnector();
        using var discovery = new CultMeshDiscoveryService(new[]
        {
            new RouteSource("cultmesh-state+quic://127.0.0.1:3077")
        });
        using var sessions = new CultMeshSessionManager(
            discovery,
            Array.Empty<ICultMeshTransportConnector>(),
            Array.Empty<ICultMeshContentTransportConnector>(),
            new ICultMeshRealtimeTransportConnector[] { connector });
        var session = await sessions.ConnectRealtimeAsync(
            CultMeshEndpointId.Parse("service:aetheria.daemon"));
        var sent = new CultMeshRealtimeFrame
        {
            ChannelId = "aetheria.entities",
            SchemaId = "eve.entity_soa.v1",
            BodyId = "body:aetheria:entities",
            ProducerEpoch = 7,
            Sequence = 42,
            Delivery = CultMeshRealtimeDelivery.LatestOnly,
            Payload = new byte[] { 1, 2, 3 }
        };

        await session.SendAsync(sent);
        var received = await session.ReceiveAsync();

        session.TransportId.Should().Be("test-quic-state");
        received.Should().BeSameAs(sent);
        connector.ConnectCount.Should().Be(1);
    }

    [Test]
    public async Task RealtimeSessionFailsClosedWithoutAnExplicitConnector()
    {
        using var discovery = new CultMeshDiscoveryService(new[]
        {
            new RouteSource("rudp://127.0.0.1:3076")
        });
        using var sessions = new CultMeshSessionManager(
            discovery,
            Array.Empty<ICultMeshTransportConnector>());

        Func<Task> connect = async () => await sessions.ConnectRealtimeAsync(
            CultMeshEndpointId.Parse("service:aetheria.daemon"));

        await connect.Should().ThrowAsync<CultMeshSessionException>()
            .WithMessage("*No realtime state connectors are configured*");
    }

    private sealed class RouteSource : ICultMeshLookupSource
    {
        private readonly string[] _endpoints;

        public RouteSource(params string[] endpoints) => _endpoints = endpoints;
        public string SourceId => "transport-modularity-test";

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
                        _endpoints),
                    SourceId,
                    now,
                    now.AddMinutes(1),
                    CultMeshDiscoveryTrust.Signed)
            });
        }
    }

    private sealed class RejectingLegacySchemaConnector : ICultMeshTransportConnector
    {
        private int _connectCount;
        public string ConnectorId => "test-legacy-schema";
        public int Priority => 10_000;
        public int ConnectCount => Volatile.Read(ref _connectCount);
        public bool CanConnect(CultMeshTransportCandidate candidate) =>
            candidate.Endpoint.StartsWith("rudp://", StringComparison.OrdinalIgnoreCase);
        public Task<ICultNetSchemaClient> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            throw new InvalidOperationException("Legacy schema connector should not be touched.");
        }
    }

    private sealed class RejectingLegacyContentConnector : ICultMeshContentTransportConnector
    {
        private int _connectCount;
        public string ConnectorId => "test-legacy-content";
        public int Priority => 10_000;
        public int ConnectCount => Volatile.Read(ref _connectCount);
        public bool CanConnect(CultMeshTransportCandidate candidate) =>
            candidate.Endpoint.StartsWith("rudp://", StringComparison.OrdinalIgnoreCase);
        public Task<ICultMeshContentTransport> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshEndpointId endpointId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            throw new InvalidOperationException("Legacy content connector should not be touched.");
        }
    }

    private sealed class LoopbackRealtimeConnector : ICultMeshRealtimeTransportConnector
    {
        private int _connectCount;
        public string ConnectorId => "test-quic-state";
        public int Priority => 0;
        public int ConnectCount => Volatile.Read(ref _connectCount);
        public bool CanConnect(CultMeshTransportCandidate candidate) =>
            candidate.Endpoint.StartsWith("cultmesh-state+quic://", StringComparison.OrdinalIgnoreCase);
        public Task<ICultMeshRealtimeTransport> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshEndpointId endpointId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            return Task.FromResult<ICultMeshRealtimeTransport>(new LoopbackRealtimeTransport(candidate.Endpoint));
        }
    }

    private sealed class LoopbackRealtimeTransport : ICultMeshRealtimeTransport
    {
        private readonly Queue<CultMeshRealtimeFrame> _frames = new();
        private readonly SemaphoreSlim _available = new(0);
        private bool _disposed;

        public LoopbackRealtimeTransport(string endpoint) => Endpoint = endpoint;
        public string TransportId => "test-quic-state";
        public string Endpoint { get; }

        public Task SendAsync(CultMeshRealtimeFrame frame, CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LoopbackRealtimeTransport));
            lock (_frames) _frames.Enqueue(frame);
            _available.Release();
            return Task.CompletedTask;
        }

        public async Task<CultMeshRealtimeFrame> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            await _available.WaitAsync(cancellationToken);
            lock (_frames) return _frames.Dequeue();
        }

        public void Dispose()
        {
            _disposed = true;
            _available.Dispose();
        }
    }
}
