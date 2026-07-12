using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Networking;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

public sealed class CultMeshClientTests
{
    [Test]
    public void Construction_RequiresOneRendezvousEndpoint()
    {
        var create = () => new CultMeshClient(new CultMeshClientOptions());

        create.Should().Throw<ArgumentException>()
            .WithMessage("*rendezvous endpoint*");
    }

    [Test]
    public async Task Connect_ResolvesStableIdentityAndReusesOneSession()
    {
        var discoveryClients = 0;
        var connector = new RecordingConnector();
        using var mesh = new CultMeshClient(new CultMeshClientOptions
        {
            RendezvousEndpoints = new[] { "rudp://odin:3076" },
            Discovery = new CultMeshVerseDiscoveryClientOptions
            {
                CreateClient = () =>
                {
                    Interlocked.Increment(ref discoveryClients);
                    return new RendezvousClient();
                }
            },
            Connectors = new[] { connector }
        });

        var first = await mesh.ConnectAsync("aetheria", CultMeshProtocols.Documents);
        var second = await mesh.ConnectAsync("aetheria", CultMeshProtocols.Documents);

        first.Should().BeSameAs(second);
        first.EndpointId.Value.Should().Be("aetheria");
        first.State.Path!.Endpoint.Should().Be("rudp://aetheria:3081");
        discoveryClients.Should().Be(1);
        connector.ConnectCount.Should().Be(1);
    }

    private sealed class RendezvousClient : ICultNetSchemaClient
    {
        private readonly List<Action<CultMeshVerseCatalogResponseMessage>> _handlers = new();
        public bool Connected => true;
        public void Connect(string host, int port) { }
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
        {
            var request = (CultMeshVerseCatalogRequestMessage)(object)message;
            var response = new CultMeshVerseCatalogResponseMessage
            {
                MessageId = request.MessageId,
                Verses = new[]
                {
                    new CultMeshVerseDescriptor(
                        "aetheria",
                        "Aetheria",
                        CultMeshVerseAuthorityModel.OperatorCluster,
                        new CultMeshVerseCompatibility("cultmesh.v0", "rules"),
                        new[] { "rudp://aetheria:3081" }).ToMessage()
                }
            };
            foreach (var handler in _handlers.ToArray()) handler(response);
        }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (typeof(T) == typeof(CultMeshVerseCatalogResponseMessage))
                _handlers.Add(response => callback((T)(object)response));
        }
        public void Dispose() { }
    }

    private sealed class RecordingConnector : ICultMeshTransportConnector
    {
        private int _connectCount;
        public string ConnectorId => "test";
        public int ConnectCount => Volatile.Read(ref _connectCount);
        public bool CanConnect(CultMeshTransportCandidate candidate) => true;
        public Task<ICultNetSchemaClient> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            return Task.FromResult<ICultNetSchemaClient>(new ConnectedClient());
        }
    }

    private sealed class ConnectedClient : ICultNetSchemaClient
    {
        public bool Connected => true;
        public void Connect(string host, int port) { }
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage { }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage { }
        public void Dispose() { }
    }
}
