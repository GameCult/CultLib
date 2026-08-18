using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Networking;
using NUnit.Framework;
using R3;

namespace GameCult.Mesh.Tests;

public sealed class CultMeshSessionManagerTests
{
    private static CultMeshSessionTarget Target => new("aetheria", "aetheria-daemon");

    [Test]
    public async Task Connect_ResolvesOnlyTheRequestedVerseIdentity()
    {
        var clock = new ManualSessionClock();
        using var discovery = new CultMeshDiscoveryService(
            new[] { new MultiVerseRouteSource(clock) },
            new CultMeshDiscoveryServiceOptions { Clock = clock });
        CultMeshTransportCandidate? connected = null;
        var connector = new FakeConnector((candidate, _) =>
        {
            connected = candidate;
            return Task.FromResult<ICultNetSchemaClient>(new FakeSchemaClient());
        });
        using var manager = new CultMeshSessionManager(discovery, new[] { connector }, new CultMeshSessionManagerOptions { Clock = clock });

        await manager.ConnectAsync(Target, CultMeshProtocols.Documents);

        connected.Should().NotBeNull();
        connected!.Endpoint.Should().Be("rudp://aetheria:3076");
    }

    [Test]
    public async Task Connect_ReusesInflightAndEstablishedSession()
    {
        var clock = new ManualSessionClock();
        using var discovery = Discovery(clock, () => new[] { "rudp://direct:3076" });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new FakeConnector(async (_, _) => { await release.Task; return new FakeSchemaClient(); });
        using var manager = new CultMeshSessionManager(discovery, new[] { connector }, new CultMeshSessionManagerOptions { Clock = clock });
        var target = Target;

        var first = manager.ConnectAsync(target, CultMeshProtocols.Documents);
        var second = manager.ConnectAsync(target, CultMeshProtocols.Documents);
        await WaitUntilAsync(() => connector.ConnectCount == 1);
        release.TrySetResult();
        var sessions = await Task.WhenAll(first, second);
        var third = await manager.ConnectAsync(target, CultMeshProtocols.Documents);

        sessions[0].Should().BeSameAs(sessions[1]).And.BeSameAs(third);
        connector.ConnectCount.Should().Be(1);
    }

    [Test]
    public async Task Connect_RacesPathsAndDisposesSuccessfulLoser()
    {
        var clock = new ManualSessionClock();
        using var discovery = Discovery(clock, () => new[] { "rudp://slow:3076", "rudp://fast:3076" });
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowClient = new FakeSchemaClient();
        var fastClient = new FakeSchemaClient();
        var connector = new FakeConnector(async (candidate, _) =>
        {
            if (candidate.Endpoint.Contains("slow")) { await slowRelease.Task; return slowClient; }
            return fastClient;
        });
        using var manager = new CultMeshSessionManager(discovery, new[] { connector }, new CultMeshSessionManagerOptions { Clock = clock });

        var session = await manager.ConnectAsync(Target, CultMeshProtocols.Documents);
        session.State.Path!.Endpoint.Should().Be("rudp://fast:3076");
        slowRelease.TrySetResult();
        await WaitUntilAsync(() => slowClient.DisposeCount == 1);
        fastClient.DisposeCount.Should().Be(0);
    }

    [Test]
    public async Task Connect_NeverAttemptsRouteBoundToAnotherAuthority()
    {
        var clock = new ManualSessionClock();
        var descriptor = new CultMeshVerseDescriptor(
            "aetheria",
            "Aetheria",
            CultMeshVerseAuthorityModel.OperatorCluster,
            new CultMeshVerseCompatibility("cultmesh.v0", "rules"),
            authorityRoutes: new[]
            {
                new CultMeshAuthorityRoute("decoy-daemon", "rudp://decoy:3076"),
                new CultMeshAuthorityRoute("aetheria-daemon", "rudp://authority:3076")
            });
        using var discovery = new CultMeshDiscoveryService(
            new[] { new StaticRouteSource(clock, descriptor) },
            new CultMeshDiscoveryServiceOptions { Clock = clock });
        var attempts = new List<string>();
        var connector = new FakeConnector((candidate, _) =>
        {
            attempts.Add(candidate.Endpoint);
            return Task.FromResult<ICultNetSchemaClient>(new FakeSchemaClient());
        });
        using var manager = new CultMeshSessionManager(discovery, new[] { connector });

        var session = await manager.ConnectAsync(Target, CultMeshProtocols.Documents);

        session.State.Path!.Endpoint.Should().Be("rudp://authority:3076");
        attempts.Should().Equal("rudp://authority:3076");
    }

    [Test]
    public void Connect_RejectsRouteWhoseNativeIdentityDoesNotMatchTarget()
    {
        var clock = new ManualSessionClock();
        using var discovery = Discovery(clock, () => new[] { "rudp://decoy:3076" });
        var connector = new FakeConnector((_, _) =>
            Task.FromResult<ICultNetSchemaClient>(new NativeMismatchClient()));
        using var manager = new CultMeshSessionManager(discovery, new[] { connector });

        var error = Assert.ThrowsAsync<CultMeshSessionException>(() =>
            manager.ConnectAsync(Target, CultMeshProtocols.Documents));

        error!.Failure.Reason.Should().Be(CultMeshSessionFailureReason.Authority);
    }

    [Test]
    public void Connect_RejectsPortableHandshakeFromWrongRuntime()
    {
        var clock = new ManualSessionClock();
        using var discovery = Discovery(clock, () => new[] { "rudp://decoy:3076" });
        var connector = new FakeConnector((_, _) =>
            Task.FromResult<ICultNetSchemaClient>(new HandshakeSchemaClient("decoy-daemon")));
        using var manager = new CultMeshSessionManager(discovery, new[] { connector });

        var error = Assert.ThrowsAsync<CultMeshSessionException>(() =>
            manager.ConnectAsync(Target, CultMeshProtocols.Documents));

        error!.Failure.Reason.Should().Be(CultMeshSessionFailureReason.Authority);
    }

    [Test]
    public void Descriptor_RejectsAmbiguousLegacyMultiAuthorityRoutes()
    {
        var action = () => new CultMeshVerseDescriptor(
            "aetheria",
            "Aetheria",
            CultMeshVerseAuthorityModel.OperatorCluster,
            new CultMeshVerseCompatibility("cultmesh.v0", "rules"),
            discoveryEndpoints: new[] { "rudp://shared:3076" },
            authorityRuntimeIds: new[] { "authority-a", "authority-b" });

        action.Should().Throw<InvalidOperationException>().WithMessage("*ambiguous*");
    }

    [Test]
    public async Task Connect_CallerCancellationDoesNotCancelSharedAttempt()
    {
        var clock = new ManualSessionClock();
        using var discovery = Discovery(clock, () => new[] { "rudp://direct:3076" });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new FakeConnector(async (_, token) =>
        {
            token.CanBeCanceled.Should().BeFalse();
            await release.Task;
            return new FakeSchemaClient();
        });
        using var manager = new CultMeshSessionManager(discovery, new[] { connector }, new CultMeshSessionManagerOptions { Clock = clock });
        using var cancellation = new CancellationTokenSource();
        var target = Target;

        var cancelled = manager.ConnectAsync(target, CultMeshProtocols.Documents, cancellation.Token);
        var survivor = manager.ConnectAsync(target, CultMeshProtocols.Documents);
        await WaitUntilAsync(() => connector.ConnectCount == 1);
        cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelled);
        release.TrySetResult();

        (await survivor).State.Status.Should().Be(CultMeshSessionStatus.Online);
        connector.ConnectCount.Should().Be(1);
    }

    [Test]
    public async Task BackgroundFailureMigratesSameSessionAcrossEndpointRotation()
    {
        var clock = new ManualSessionClock();
        var route = "rudp://first:3076";
        using var discovery = Discovery(clock, () => new[] { route }, TimeSpan.FromSeconds(5));
        var clients = new List<FakeSchemaClient>();
        var connector = new FakeConnector((_, _) =>
        {
            var client = new FakeSchemaClient();
            clients.Add(client);
            return Task.FromResult<ICultNetSchemaClient>(client);
        });
        using var manager = new CultMeshSessionManager(discovery, new[] { connector }, new CultMeshSessionManagerOptions { Clock = clock });
        var target = Target;
        var first = await manager.ConnectAsync(target, CultMeshProtocols.Documents);
        var states = new List<CultMeshSessionStatus>();
        using var stateWatch = first.WatchState().Subscribe(state => states.Add(state.Status));
        using var schemaLease = first.OpenSchemaClient();
        var deliveries = 0;
        schemaLease.OnCultNet<CultNetErrorMessage>(_ => deliveries++);
        clients[0].Emit(new CultNetErrorMessage { Error = "before rotation" });

        route = "rudp://second:3076";
        clients[0].Fail(new IOException("partition"));
        await WaitUntilAsync(() => first.State.Status == CultMeshSessionStatus.Online && connector.ConnectCount == 2);
        var second = await manager.ConnectAsync(target, CultMeshProtocols.Documents);
        clients[1].Emit(new CultNetErrorMessage { Error = "after rotation" });

        second.Should().BeSameAs(first);
        second.State.Path!.Endpoint.Should().Be("rudp://second:3076");
        states.Should().ContainInOrder(CultMeshSessionStatus.Reconnecting, CultMeshSessionStatus.Online);
        connector.ConnectCount.Should().Be(2);
        clients[0].DisposeCount.Should().Be(1);
        deliveries.Should().Be(2);
    }

    [Test]
    public async Task UriConnectorPreservesTheAdvertisedPath()
    {
        var client = new FakeUriSchemaClient();
        var connector = new CultMeshUriSchemaTransportConnector(
            "websocket",
            new[] { "ws", "wss" },
            _ => client);
        var candidate = new CultMeshTransportCandidate("wss://odin.example/game/cultmesh?generation=7");

        var connected = await connector.ConnectAsync(candidate, CultMeshProtocols.Documents);

        connected.Should().BeSameAs(client);
        client.Endpoint.Should().Be(new Uri(candidate.Endpoint));
    }

    [Test]
    public void Connect_ReportsUnsupportedPathPrecisely()
    {
        var clock = new ManualSessionClock();
        using var discovery = Discovery(clock, () => new[] { "quic://unimplemented:443" });
        using var manager = new CultMeshSessionManager(discovery, Array.Empty<ICultMeshTransportConnector>(), new CultMeshSessionManagerOptions { Clock = clock });

        var error = Assert.ThrowsAsync<CultMeshSessionException>(() =>
            manager.ConnectAsync(Target, CultMeshProtocols.Documents));

        error!.Failure.Reason.Should().Be(CultMeshSessionFailureReason.Transport);
        error.InnerException.Should().BeOfType<CultMeshSessionException>()
            .Which.Failure.Reason.Should().Be(CultMeshSessionFailureReason.UnsupportedPath);
    }

    [Test]
    public async Task SnapshotSessionsBorrowOneManagedChannelAndDisposeRegistrationsIndependently()
    {
        var clock = new ManualSessionClock();
        using var discovery = Discovery(clock, () => new[] { "rudp://direct:3076" });
        var client = new RespondingSchemaClient();
        var connector = new FakeConnector((_, _) => Task.FromResult<ICultNetSchemaClient>(client));
        using var manager = new CultMeshSessionManager(discovery, new[] { connector }, new CultMeshSessionManagerOptions { Clock = clock });
        var target = Target;
        using var first = await CultMeshSnapshotSession.ConnectAsync(manager, target, new CultMeshSnapshotRequestOptions());
        using var second = await CultMeshSnapshotSession.ConnectAsync(manager, target, new CultMeshSnapshotRequestOptions());

        (await first.FetchSnapshotAsync()).MessageId.Should().NotBeEmpty();
        first.Dispose();
        (await second.FetchSnapshotAsync()).MessageId.Should().NotBeEmpty();

        connector.ConnectCount.Should().Be(1);
        client.DisposeCount.Should().Be(0);
        client.SentCount.Should().Be(2);
    }

    [Test]
    public async Task PeerExchangeBorrowsManagedProtocolSessionAcrossRequests()
    {
        var clock = new ManualSessionClock();
        using var discovery = Discovery(clock, () => new[] { "rudp://direct:3076" });
        var client = new PeerExchangeSchemaClient();
        var connector = new FakeConnector((_, _) => Task.FromResult<ICultNetSchemaClient>(client));
        using var manager = new CultMeshSessionManager(discovery, new[] { connector }, new CultMeshSessionManagerOptions { Clock = clock });
        var exchange = new CultMeshPeerExchangeClient(new CultMeshPeerExchangeClientOptions { Sessions = manager, Clock = clock });
        var target = Target;

        var first = await exchange.FetchAsync(target, new CultMeshPeerExchangeRequestMessage { VerseId = "aetheria" });
        var second = await exchange.FetchAsync(target, new CultMeshPeerExchangeRequestMessage { VerseId = "aetheria" });

        first.Peers.Single().PeerId.Should().Be("peer-a");
        second.Peers.Single().PeerId.Should().Be("peer-a");
        connector.ConnectCount.Should().Be(1);
        client.SentCount.Should().Be(2);
    }

    [Test]
    public async Task SchemaClientLeasesDisposeHandlersWithoutClosingSession()
    {
        var clock = new ManualSessionClock();
        using var discovery = Discovery(clock, () => new[] { "rudp://direct:3076" });
        var client = new LeaseSchemaClient();
        var connector = new FakeConnector((_, _) => Task.FromResult<ICultNetSchemaClient>(client));
        using var manager = new CultMeshSessionManager(discovery, new[] { connector }, new CultMeshSessionManagerOptions { Clock = clock });
        var session = await manager.ConnectAsync(Target, CultMeshProtocols.Subscriptions);
        using var first = session.OpenSchemaClient();
        using var second = session.OpenSchemaClient();
        var firstCount = 0;
        var secondCount = 0;
        first.OnCultNet<CultNetErrorMessage>(_ => firstCount++);
        second.OnCultNet<CultNetErrorMessage>(_ => secondCount++);

        client.Emit(new CultNetErrorMessage { Error = "first" });
        first.Dispose();
        client.Emit(new CultNetErrorMessage { Error = "second" });

        firstCount.Should().Be(1);
        secondCount.Should().Be(2);
        client.DisposeCount.Should().Be(0);
        session.State.Status.Should().Be(CultMeshSessionStatus.Online);
    }

    private static CultMeshDiscoveryService Discovery(
        ManualSessionClock clock,
        Func<string[]> endpoints,
        TimeSpan? ttl = null)
    {
        return new CultMeshDiscoveryService(
            new[] { new RouteSource(clock, endpoints, ttl ?? TimeSpan.FromMinutes(1)) },
            new CultMeshDiscoveryServiceOptions { Clock = clock });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(1);
        condition().Should().BeTrue();
    }

    private sealed class RouteSource : ICultMeshLookupSource
    {
        private readonly ManualSessionClock _clock;
        private readonly Func<string[]> _endpoints;
        private readonly TimeSpan _ttl;
        public RouteSource(ManualSessionClock clock, Func<string[]> endpoints, TimeSpan ttl) { _clock = clock; _endpoints = endpoints; _ttl = ttl; }
        public string SourceId => "odin";
        public Task<IReadOnlyList<CultMeshDiscoveryObservation>> LookupAsync(CultMeshDiscoveryQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(new[]
            {
                new CultMeshDiscoveryObservation(
                    new CultMeshVerseDescriptor("aetheria", "Aetheria", CultMeshVerseAuthorityModel.OperatorCluster,
                        new CultMeshVerseCompatibility("cultmesh.v0", "rules"), _endpoints(), new[] { "aetheria-daemon" }),
                    SourceId, _clock.UtcNow, _clock.UtcNow + _ttl, CultMeshDiscoveryTrust.Signed)
            });
    }

    private sealed class MultiVerseRouteSource : ICultMeshLookupSource
    {
        private readonly ManualSessionClock _clock;
        public MultiVerseRouteSource(ManualSessionClock clock) => _clock = clock;
        public string SourceId => "odin";

        public Task<IReadOnlyList<CultMeshDiscoveryObservation>> LookupAsync(
            CultMeshDiscoveryQuery query,
            CancellationToken cancellationToken = default)
        {
            var expires = _clock.UtcNow.AddMinutes(1);
            return Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(new[]
            {
                Observation("norn", "rudp://norn:3076", expires),
                Observation("aetheria", "rudp://aetheria:3076", expires)
            });
        }

        private CultMeshDiscoveryObservation Observation(string verseId, string endpoint, DateTimeOffset expires) =>
            new(
                new CultMeshVerseDescriptor(
                    verseId,
                    verseId,
                    CultMeshVerseAuthorityModel.OperatorCluster,
                    new CultMeshVerseCompatibility("cultmesh.v0", "rules"),
                    new[] { endpoint },
                    new[] { "aetheria-daemon" }),
                SourceId,
                _clock.UtcNow,
                expires,
                CultMeshDiscoveryTrust.Signed);
    }

    private sealed class StaticRouteSource : ICultMeshLookupSource
    {
        private readonly ManualSessionClock _clock;
        private readonly CultMeshVerseDescriptor _descriptor;
        public StaticRouteSource(ManualSessionClock clock, CultMeshVerseDescriptor descriptor)
        {
            _clock = clock;
            _descriptor = descriptor;
        }
        public string SourceId => "odin";
        public Task<IReadOnlyList<CultMeshDiscoveryObservation>> LookupAsync(
            CultMeshDiscoveryQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(new[]
            {
                new CultMeshDiscoveryObservation(
                    _descriptor,
                    SourceId,
                    _clock.UtcNow,
                    _clock.UtcNow.AddMinutes(1),
                    CultMeshDiscoveryTrust.Signed)
            });
    }

    private sealed class FakeConnector : ICultMeshTransportConnector
    {
        private readonly Func<CultMeshTransportCandidate, CancellationToken, Task<ICultNetSchemaClient>> _connect;
        private int _connectCount;
        public FakeConnector(Func<CultMeshTransportCandidate, CancellationToken, Task<ICultNetSchemaClient>> connect) { _connect = connect; }
        public string ConnectorId => "fake";
        public int Priority => 0;
        public int ConnectCount => Volatile.Read(ref _connectCount);
        public bool CanConnect(CultMeshTransportCandidate candidate) => true;
        public Task<ICultNetSchemaClient> ConnectAsync(CultMeshTransportCandidate candidate, CultMeshProtocolId protocol, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            return _connect(candidate, cancellationToken);
        }
    }

    private sealed class FakeSchemaClient : ICultNetSchemaClient, ICultNetSchemaClientHealth, ICultMeshVerifiedSchemaClient
    {
        private readonly TaskCompletionSource<Exception> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Action<CultNetErrorMessage>> _handlers = new();
        private int _disposeCount;
        public bool Connected => true;
        public Task<Exception> BackgroundFailure => _failure.Task;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Connect(string host, int port) { }
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage { }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (typeof(T) == typeof(CultNetErrorMessage))
                _handlers.Add(message => callback((T)(object)message));
        }
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
        public bool IsVerifiedFor(string verseId, string authorityRuntimeId, string protocolId, string routeGeneration) =>
            verseId == "aetheria" && authorityRuntimeId == "aetheria-daemon";
        public void Fail(Exception error) => _failure.TrySetResult(error);
        public void Emit(CultNetErrorMessage message) { foreach (var handler in _handlers.ToArray()) handler(message); }
    }

    private sealed class FakeUriSchemaClient : ICultNetUriSchemaClient
    {
        public bool Connected => Endpoint != null;
        public Uri? Endpoint { get; private set; }
        public void Connect(string host, int port) => throw new AssertionException("URI connection was not used.");
        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Endpoint = endpoint;
            return Task.CompletedTask;
        }
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage { }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage { }
        public void Dispose() { }
    }

    private sealed class NativeMismatchClient : ICultNetSchemaClient, ICultMeshVerifiedSchemaClient
    {
        public bool Connected => true;
        public void Connect(string host, int port) { }
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage { }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage { }
        public bool IsVerifiedFor(string verseId, string authorityRuntimeId, string protocolId, string routeGeneration) => false;
        public void Dispose() { }
    }

    private sealed class HandshakeSchemaClient : ICultNetSchemaClient
    {
        private readonly string _actualRuntimeId;
        private readonly List<Action<CultMeshSessionAcceptedMessage>> _handlers = new();
        public HandshakeSchemaClient(string actualRuntimeId) => _actualRuntimeId = actualRuntimeId;
        public bool Connected => true;
        public void Connect(string host, int port) { }
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
        {
            if (message is not CultMeshSessionOpenMessage request) return;
            var response = new CultMeshSessionAcceptedMessage
            {
                MessageId = request.MessageId,
                Accepted = true,
                VerseId = request.VerseId,
                AuthorityRuntimeId = _actualRuntimeId,
                ProtocolId = request.ProtocolId,
                RouteGeneration = request.RouteGeneration
            };
            foreach (var handler in _handlers.ToArray()) handler(response);
        }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (typeof(T) == typeof(CultMeshSessionAcceptedMessage))
                _handlers.Add(message => callback((T)(object)message));
        }
        public void Dispose() { }
    }

    private sealed class RespondingSchemaClient : ICultNetSchemaClient, ICultMeshVerifiedSchemaClient
    {
        private readonly List<Action<CultNetSnapshotResponseRawMessage>> _handlers = new();
        private int _disposeCount;
        public bool Connected => true;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public int SentCount { get; private set; }
        public void Connect(string host, int port) { }
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
        {
            SentCount++;
            var request = (CultNetSnapshotRequestMessage)(object)message;
            var response = new CultNetSnapshotResponseRawMessage
            {
                MessageId = request.MessageId,
                Documents = Array.Empty<CultNetRawDocumentRecord>()
            };
            foreach (var handler in _handlers.ToArray()) handler(response);
        }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (typeof(T) == typeof(CultNetSnapshotResponseRawMessage))
                _handlers.Add(message => callback((T)(object)message));
        }
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
        public bool IsVerifiedFor(string verseId, string authorityRuntimeId, string protocolId, string routeGeneration) =>
            verseId == "aetheria" && authorityRuntimeId == "aetheria-daemon";
    }

    private sealed class PeerExchangeSchemaClient : ICultNetSchemaClient, ICultMeshVerifiedSchemaClient
    {
        private readonly List<Action<CultMeshPeerExchangeResponseMessage>> _handlers = new();
        public bool Connected => true;
        public int SentCount { get; private set; }
        public void Connect(string host, int port) { }
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
        {
            SentCount++;
            var request = (CultMeshPeerExchangeRequestMessage)(object)message;
            var response = new CultMeshPeerExchangeResponseMessage
            {
                MessageId = request.MessageId,
                Peers = new[]
                {
                    new CultMeshPeerCard("peer-a", request.VerseId, new[] { "rudp://peer-a:3076" }).ToMessage()
                }
            };
            foreach (var handler in _handlers.ToArray()) handler(response);
        }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (typeof(T) == typeof(CultMeshPeerExchangeResponseMessage))
                _handlers.Add(message => callback((T)(object)message));
        }
        public void Dispose() { }
        public bool IsVerifiedFor(string verseId, string authorityRuntimeId, string protocolId, string routeGeneration) =>
            verseId == "aetheria" && authorityRuntimeId == "aetheria-daemon";
    }

    private sealed class LeaseSchemaClient : ICultNetSchemaClient, ICultMeshVerifiedSchemaClient
    {
        private readonly List<Action<CultNetErrorMessage>> _handlers = new();
        private int _disposeCount;
        public bool Connected => true;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Connect(string host, int port) { }
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage { }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (typeof(T) == typeof(CultNetErrorMessage))
                _handlers.Add(message => callback((T)(object)message));
        }
        public void Emit(CultNetErrorMessage message) { foreach (var handler in _handlers.ToArray()) handler(message); }
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
        public bool IsVerifiedFor(string verseId, string authorityRuntimeId, string protocolId, string routeGeneration) =>
            verseId == "aetheria" && authorityRuntimeId == "aetheria-daemon";
    }

    private sealed class ManualSessionClock : ICultMeshClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new DateTimeOffset(2026, 7, 12, 21, 0, 0, TimeSpan.Zero);
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); UtcNow += delay; return Task.CompletedTask; }
        public void Advance(TimeSpan delay) => UtcNow += delay;
    }
}
