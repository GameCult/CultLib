using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Networking;
using MessagePack;
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

    [Test]
    public async Task Document_ReplicatesTypedRecordBehindOneIntentDeclaration()
    {
        var connector = new DocumentConnector();
        using var mesh = new CultMeshClient(new CultMeshClientOptions
        {
            RendezvousEndpoints = new[] { "rudp://odin:3076" },
            Discovery = new CultMeshVerseDiscoveryClientOptions { CreateClient = () => new RendezvousClient() },
            Connectors = new[] { connector }
        });

        var first = await mesh.DocumentAsync<ClientTestDocument>("aetheria", "surface:pilot");
        var second = await mesh.DocumentAsync<ClientTestDocument>("aetheria", "surface:pilot");

        first.Should().BeSameAs(second);
        (await first.LatestAsync()).Text.Should().Be("pilot surface 1");
        connector.SubscribeCount.Should().Be(1);
    }

    [Test]
    public async Task Document_ReplaysSubscriptionAndRefreshesSameHandleAfterReconnect()
    {
        var connector = new DocumentConnector();
        using var mesh = new CultMeshClient(new CultMeshClientOptions
        {
            RendezvousEndpoints = new[] { "rudp://odin:3076" },
            Discovery = new CultMeshVerseDiscoveryClientOptions { CreateClient = () => new RendezvousClient() },
            Connectors = new[] { connector }
        });
        var document = await mesh.DocumentAsync<ClientTestDocument>("aetheria", "surface:pilot");

        connector.Clients[0].Fail(new InvalidOperationException("path lost"));
        await WaitUntilAsync(() => connector.Clients.Count == 2 && connector.Clients[1].SubscribeCount == 1);
        await WaitForTextAsync(document, "pilot surface 2");

        (await document.LatestAsync()).Text.Should().Be("pilot surface 2");
        connector.Clients[0].SubscribeCount.Should().Be(1);
        connector.Clients[1].SubscribeCount.Should().Be(1);
    }

    [Test]
    public async Task Document_InitialReadinessCanBeSatisfiedByReconnectReplay()
    {
        var connector = new DocumentConnector { SuppressFirstSnapshot = true };
        using var mesh = new CultMeshClient(new CultMeshClientOptions
        {
            RendezvousEndpoints = new[] { "rudp://odin:3076" },
            Discovery = new CultMeshVerseDiscoveryClientOptions { CreateClient = () => new RendezvousClient() },
            Connectors = new[] { connector }
        });

        var opening = mesh.DocumentAsync<ClientTestDocument>("aetheria", "surface:pilot");
        await WaitUntilAsync(() => connector.Clients.Count == 1 && connector.Clients[0].SubscribeCount == 1);
        connector.Clients[0].Fail(new InvalidOperationException("path lost before initial snapshot"));

        var document = await opening;

        (await document.LatestAsync()).Text.Should().Be("pilot surface 2");
        connector.Clients.Should().HaveCount(2);
        connector.Clients[1].SubscribeCount.Should().Be(1);
    }

    [Test]
    public async Task Document_ReplaysUnansweredIntentOnSamePhysicalSession()
    {
        var connector = new DocumentConnector { SuppressFirstSubscriptionSnapshot = true };
        using var mesh = new CultMeshClient(new CultMeshClientOptions
        {
            RendezvousEndpoints = new[] { "rudp://odin:3076" },
            Discovery = new CultMeshVerseDiscoveryClientOptions { CreateClient = () => new RendezvousClient() },
            Connectors = new[] { connector },
            SubscriptionResponseTimeout = TimeSpan.FromMilliseconds(20)
        });

        var document = await mesh.DocumentAsync<ClientTestDocument>("aetheria", "surface:pilot");

        (await document.LatestAsync()).Text.Should().Be("pilot surface 1");
        connector.Clients.Should().ContainSingle();
        connector.Clients[0].SubscribeCount.Should().Be(2);
    }

    [Test]
    public async Task Collection_ReplicatesAllDocumentsOfTypedSchema()
    {
        var connector = new DocumentConnector();
        using var mesh = new CultMeshClient(new CultMeshClientOptions
        {
            RendezvousEndpoints = new[] { "rudp://odin:3076" },
            Discovery = new CultMeshVerseDiscoveryClientOptions { CreateClient = () => new RendezvousClient() },
            Connectors = new[] { connector }
        });

        var collection = await mesh.CollectionAsync<ClientTestDocument>("aetheria");

        (await collection.LatestAsync()).Should().ContainSingle()
            .Which.Text.Should().Be("pilot surface 1");
        connector.SubscribeCount.Should().Be(1);
    }

    [Test]
    public async Task Collection_InitialReadinessCanBeSatisfiedByReconnectReplay()
    {
        var connector = new DocumentConnector { SuppressFirstSnapshot = true };
        using var mesh = new CultMeshClient(new CultMeshClientOptions
        {
            RendezvousEndpoints = new[] { "rudp://odin:3076" },
            Discovery = new CultMeshVerseDiscoveryClientOptions { CreateClient = () => new RendezvousClient() },
            Connectors = new[] { connector }
        });

        var opening = mesh.CollectionAsync<ClientTestDocument>("aetheria");
        await WaitUntilAsync(() => connector.Clients.Count == 1 && connector.Clients[0].SubscribeCount == 1);
        connector.Clients[0].Fail(new InvalidOperationException("path lost before initial collection snapshot"));

        var collection = await opening;

        (await collection.LatestAsync()).Should().ContainSingle()
            .Which.Text.Should().Be("pilot surface 2");
        connector.Clients.Should().HaveCount(2);
        connector.Clients[1].SubscribeCount.Should().Be(1);
    }

    [Test]
    public async Task Dispose_TerminatesDocumentWaitingForInitialSnapshot()
    {
        var connector = new DocumentConnector { SuppressFirstSnapshot = true };
        var mesh = new CultMeshClient(new CultMeshClientOptions
        {
            RendezvousEndpoints = new[] { "rudp://odin:3076" },
            Discovery = new CultMeshVerseDiscoveryClientOptions { CreateClient = () => new RendezvousClient() },
            Connectors = new[] { connector }
        });
        var opening = mesh.DocumentAsync<ClientTestDocument>("aetheria", "surface:pilot");
        await WaitUntilAsync(() => connector.Clients.Count == 1 && connector.Clients[0].SubscribeCount == 1);

        mesh.Dispose();

        Func<Task> awaitOpening = async () => await opening;
        await awaitOpening.Should().ThrowAsync<ObjectDisposedException>();
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

    private sealed class DocumentConnector : ICultMeshTransportConnector
    {
        public List<DocumentClient> Clients { get; } = new();
        public bool SuppressFirstSnapshot { get; set; }
        public bool SuppressFirstSubscriptionSnapshot { get; set; }
        public string ConnectorId => "document-test";
        public int SubscribeCount => Clients.Sum(client => client.SubscribeCount);
        public bool CanConnect(CultMeshTransportCandidate candidate) => true;
        public Task<ICultNetSchemaClient> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default)
        {
            var client = new DocumentClient(
                "pilot surface " + (Clients.Count + 1),
                snapshotsToSuppress: SuppressFirstSnapshot && Clients.Count == 0
                    ? int.MaxValue
                    : SuppressFirstSubscriptionSnapshot && Clients.Count == 0 ? 1 : 0);
            Clients.Add(client);
            return Task.FromResult<ICultNetSchemaClient>(client);
        }
    }

    private sealed class DocumentClient : ICultNetSchemaClient, ICultNetSchemaClientHealth
    {
        private readonly List<Action<CultNetSnapshotResponseRawMessage>> _snapshots = new();
        private readonly CultNetDocumentRegistry _documents;
        private readonly string _text;
        private readonly TaskCompletionSource<Exception> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _snapshotsToSuppress;
        public DocumentClient(string text, int snapshotsToSuppress = 0)
        {
            _text = text;
            _snapshotsToSuppress = snapshotsToSuppress;
            var cache = CultMesh.CreateCultCacheDocumentRegistry(typeof(ClientTestDocument));
            _documents = CultMesh.CreateCultNetDocumentRegistry(new[] { typeof(ClientTestDocument) }, cache);
        }
        public bool Connected => true;
        public Task<Exception> BackgroundFailure => _failure.Task;
        public int SubscribeCount { get; private set; }
        public void Connect(string host, int port) { }
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
        {
            if (message is not CultNetDatabaseSubscribeMessage request) return;
            SubscribeCount++;
            if (_snapshotsToSuppress > 0)
            {
                _snapshotsToSuppress--;
                return;
            }
            var put = _documents.CreateRawDocumentPutMessage(
                "test-put",
                new CultRecordHandle<ClientTestDocument>(new CultRecordKey("surface:pilot")),
                new ClientTestDocument { Id = "surface:pilot", Text = _text });
            var response = new CultNetSnapshotResponseRawMessage
            {
                MessageId = request.MessageId,
                Documents = new[] { put.Document }
            };
            foreach (var handler in _snapshots.ToArray()) handler(response);
        }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (typeof(T) == typeof(CultNetSnapshotResponseRawMessage))
                _snapshots.Add(response => callback((T)(object)response));
        }
        public void Dispose() { }
        public void Fail(Exception error) => _failure.TrySetResult(error);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(1);
        condition().Should().BeTrue();
    }

    private static async Task WaitForTextAsync(
        CultMeshDocumentHandle<ClientTestDocument> document,
        string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (string.Equals((await document.LatestAsync()).Text, expected, StringComparison.Ordinal)) return;
            await Task.Delay(1);
        }
        (await document.LatestAsync()).Text.Should().Be(expected);
    }

    [CultDocument("tests.cultmesh_client_document", "tests.cultmesh_client_document.v1")]
    [MessagePackObject]
    public sealed class ClientTestDocument
    {
        [Key(0), CultName] public string Id { get; set; } = string.Empty;
        [Key(1)] public string Text { get; set; } = string.Empty;
    }
}
