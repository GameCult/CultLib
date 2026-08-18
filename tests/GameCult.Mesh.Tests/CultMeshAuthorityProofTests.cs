using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Networking;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

public sealed class CultMeshAuthorityProofTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

    [Test]
    public void SignedRouteBindsEveryAuthorityFieldToConsumerTrust()
    {
        using var odin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var route = SignedRoute(odin, provider);
        var trust = Trust(odin);

        trust.Validate("aetheria", route, Now);
        var wireRoundTrip = new CultMeshVerseDescriptor(
                "aetheria",
                "Aetheria",
                CultMeshVerseAuthorityModel.OperatorCluster,
                new CultMeshVerseCompatibility("cultmesh.v0", "rules"),
                authorityRoutes: new[] { route })
            .ToMessage()
            .ToVerseDescriptor()
            .AuthorityRoutes[0];
        trust.Validate("aetheria", wireRoundTrip, Now);
        wireRoundTrip.Certificate!.ProviderKey.KeyId.Should().Be("provider-1");

        Action mutatedEndpoint = () => trust.Validate("aetheria", new CultMeshAuthorityRoute(
            route.AuthorityRuntimeId,
            "wss://evil.example/mesh",
            route.ProtocolIds,
            route.Priority,
            route.Generation,
            route.Certificate), Now);
        mutatedEndpoint.Should().Throw<CultMeshSessionException>()
            .Which.Failure.Reason.Should().Be(CultMeshSessionFailureReason.Authentication);
    }

    [Test]
    public void RemoteTrustRejectsUnknownExpiredAndUnsignedRoutes()
    {
        using var odin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var route = SignedRoute(odin, provider);

        Action unknownRoot = () => Trust(stranger).Validate("aetheria", route, Now);
        Action expired = () => Trust(odin).Validate("aetheria", route, Now.AddHours(2));
        Action unsigned = () => Trust(odin).Validate("aetheria", new CultMeshAuthorityRoute(
            "aetheria-daemon", "wss://provider.example/mesh",
            new[] { CultMeshProtocols.Documents.Value }, generation: "generation-1"), Now);

        unknownRoot.Should().Throw<CultMeshSessionException>();
        expired.Should().Throw<CultMeshSessionException>();
        unsigned.Should().Throw<CultMeshSessionException>();
    }

    [Test]
    public void UnsignedRouteRequiresExplicitLoopbackDevelopmentPolicy()
    {
        var local = new CultMeshAuthorityRoute(
            "aetheria-daemon", "rudp://127.0.0.1:3076",
            new[] { CultMeshProtocols.Documents.Value }, generation: "local");
        var remote = new CultMeshAuthorityRoute(
            "aetheria-daemon", "rudp://192.0.2.10:3076",
            new[] { CultMeshProtocols.Documents.Value }, generation: "remote");
        var policy = new CultMeshAuthorityTrustPolicy(CultMeshAuthorityTrustMode.LocalDevelopment);

        policy.Validate("aetheria", local, Now);
        Action remoteAttempt = () => policy.Validate("aetheria", remote, Now);
        remoteAttempt.Should().Throw<CultMeshSessionException>();
    }

    [Test]
    public void ProviderProofRejectsCredentialFreeEchoAndNonceReplay()
    {
        using var odin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var route = SignedRoute(odin, provider);
        var trust = Trust(odin);
        var signer = new CultMeshSessionProofSigner(route, provider);
        var request = Request("nonce-a");
        var signed = Accepted(request, signer.Sign(request));

        CultMeshAuthorityProof.VerifySessionProof(request, signed, "aetheria", route, trust, Now).Should().BeTrue();

        var echo = Accepted(request, string.Empty);
        CultMeshAuthorityProof.VerifySessionProof(request, echo, "aetheria", route, trust, Now).Should().BeFalse();

        var replayedRequest = Request("nonce-b");
        CultMeshAuthorityProof.VerifySessionProof(replayedRequest, signed, "aetheria", route, trust, Now).Should().BeFalse();
    }

    [Test]
    public async Task SessionManagerRejectsPeerThatOnlyEchoesCertifiedIdentityStrings()
    {
        using var odin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var route = CultMeshAuthorityProof.CreateSignedRoute(
            "aetheria", "aetheria-daemon", "wss://provider.example/mesh",
            new[] { CultMeshProtocols.Documents.Value }, 0, "generation-live",
            CultMeshEcdsaP256PublicKey.From("provider-live", provider), "odin-live",
            now.AddMinutes(-1), now.AddMinutes(5), odin);
        var descriptor = new CultMeshVerseDescriptor(
            "aetheria", "Aetheria", CultMeshVerseAuthorityModel.OperatorCluster,
            new CultMeshVerseCompatibility("cultmesh.v0", "rules"),
            authorityRoutes: new[] { route });
        using var discovery = new CultMeshDiscoveryService(new[] { new SignedRouteSource(descriptor) });
        using var manager = new CultMeshSessionManager(
            discovery,
            new[] { new EchoConnector() },
            new CultMeshSessionManagerOptions
            {
                Trust = new CultMeshAuthorityTrustPolicy(
                    CultMeshAuthorityTrustMode.AuthenticatedRemote,
                    new[] { CultMeshEcdsaP256PublicKey.From("odin-live", odin) })
            });

        Func<Task> connect = async () => await manager.ConnectAsync(
            new CultMeshSessionTarget("aetheria", "aetheria-daemon"),
            CultMeshProtocols.Documents);

        (await connect.Should().ThrowAsync<CultMeshSessionException>())
            .Which.Failure.Reason.Should().Be(CultMeshSessionFailureReason.Authority);
    }

    private static CultMeshAuthorityRoute SignedRoute(ECDsa odin, ECDsa provider) =>
        CultMeshAuthorityProof.CreateSignedRoute(
            "aetheria",
            "aetheria-daemon",
            "wss://provider.example/mesh",
            new[] { CultMeshProtocols.Documents.Value },
            0,
            "generation-1",
            CultMeshEcdsaP256PublicKey.From("provider-1", provider),
            "odin-root-1",
            Now.AddMinutes(-1),
            Now.AddHours(1),
            odin);

    private static CultMeshAuthorityTrustPolicy Trust(ECDsa odin) => new(
        CultMeshAuthorityTrustMode.AuthenticatedRemote,
        new[] { CultMeshEcdsaP256PublicKey.From("odin-root-1", odin) });

    private static GameCult.Networking.CultMeshSessionOpenMessage Request(string nonceLabel)
    {
        using var hash = SHA256.Create();
        return new GameCult.Networking.CultMeshSessionOpenMessage
        {
            MessageId = "message-1",
            SourceRuntimeId = "browser-1",
            VerseId = "aetheria",
            AuthorityRuntimeId = "aetheria-daemon",
            ProtocolId = CultMeshProtocols.Documents.Value,
            RouteGeneration = "generation-1",
            ClientNonce = Convert.ToBase64String(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(nonceLabel)))
        };
    }

    private static GameCult.Networking.CultMeshSessionAcceptedMessage Accepted(
        GameCult.Networking.CultMeshSessionOpenMessage request,
        string signature) => new()
        {
            MessageId = request.MessageId,
            Accepted = true,
            VerseId = request.VerseId,
            AuthorityRuntimeId = request.AuthorityRuntimeId,
            ProtocolId = request.ProtocolId,
            RouteGeneration = request.RouteGeneration,
            ClientNonce = request.ClientNonce,
            ProviderKeyId = "provider-1",
            ProviderSignature = signature
        };

    private sealed class SignedRouteSource : ICultMeshLookupSource
    {
        private readonly CultMeshVerseDescriptor _descriptor;
        public SignedRouteSource(CultMeshVerseDescriptor descriptor) => _descriptor = descriptor;
        public string SourceId => "signed-odin";
        public Task<IReadOnlyList<CultMeshDiscoveryObservation>> LookupAsync(
            CultMeshDiscoveryQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(new[]
            {
                new CultMeshDiscoveryObservation(
                    _descriptor, SourceId, DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddMinutes(1), CultMeshDiscoveryTrust.Signed)
            });
    }

    private sealed class EchoConnector : ICultMeshTransportConnector
    {
        public string ConnectorId => "credential-free-echo";
        public int Priority => 0;
        public bool CanConnect(CultMeshTransportCandidate candidate) => true;
        public Task<ICultNetSchemaClient> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ICultNetSchemaClient>(new EchoClient());
    }

    private sealed class EchoClient : ICultNetSchemaClient
    {
        private Action<CultMeshSessionAcceptedMessage>? _accepted;
        public bool Connected => true;
        public void Connect(string host, int port) { }
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
        {
            if (message is not CultMeshSessionOpenMessage request) return;
            _accepted?.Invoke(new CultMeshSessionAcceptedMessage
            {
                MessageId = request.MessageId,
                Accepted = true,
                VerseId = request.VerseId,
                AuthorityRuntimeId = request.AuthorityRuntimeId,
                ProtocolId = request.ProtocolId,
                RouteGeneration = request.RouteGeneration,
                ClientNonce = request.ClientNonce,
                ProviderKeyId = "provider-live",
                ProviderSignature = string.Empty
            });
        }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (typeof(T) == typeof(CultMeshSessionAcceptedMessage))
                _accepted = response => callback((T)(object)response);
        }
        public void Dispose() { }
    }
}
