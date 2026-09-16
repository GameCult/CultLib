using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    [Test]
    public async Task SessionManagerRejectsOnlyThePoisonedRouteAndUsesCertifiedFallback()
    {
        using var odin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var valid = CultMeshAuthorityProof.CreateSignedRoute(
            "aetheria", "aetheria-daemon", "wss://provider.example/mesh",
            new[] { CultMeshProtocols.Documents.Value }, 10, "generation-valid",
            CultMeshEcdsaP256PublicKey.From("provider-valid", provider), "odin-valid",
            now.AddMinutes(-1), now.AddMinutes(5), odin);
        var poisoned = new CultMeshAuthorityRoute(
            "aetheria-daemon", "wss://poison.example/mesh",
            new[] { CultMeshProtocols.Documents.Value }, priority: 0, generation: "generation-poisoned");
        var descriptor = new CultMeshVerseDescriptor(
            "aetheria", "Aetheria", CultMeshVerseAuthorityModel.OperatorCluster,
            new CultMeshVerseCompatibility("cultmesh.v0", "rules"),
            authorityRoutes: new[] { poisoned, valid });
        using var discovery = new CultMeshDiscoveryService(new[] { new SignedRouteSource(descriptor) });
        var signer = new CultMeshSessionProofSigner(valid, provider);
        var connector = new SigningConnector(signer);
        using var manager = new CultMeshSessionManager(
            discovery,
            new[] { connector },
            new CultMeshSessionManagerOptions
            {
                Trust = new CultMeshAuthorityTrustPolicy(
                    CultMeshAuthorityTrustMode.AuthenticatedRemote,
                    new[] { CultMeshEcdsaP256PublicKey.From("odin-valid", odin) })
            });

        var session = await manager.ConnectAsync(
            new CultMeshSessionTarget("aetheria", "aetheria-daemon"),
            CultMeshProtocols.Documents);

        session.State.Path!.Endpoint.Should().Be(valid.Endpoint);
        connector.Attempts.Should().Equal(valid.Endpoint);
    }

    [Test]
    public async Task AuthenticatedRemoteContentUsesCertifiedHttpsAndStreamsTheAdvertisedChunk()
    {
        using var odin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = Encoding.UTF8.GetBytes("certified-content");
        var route = CultMeshAuthorityProof.CreateSignedRoute(
            "aetheria", "aetheria-daemon", "https://provider.example/cultmesh/content",
            new[] { CultMeshProtocols.Content.Value }, 0, "content-generation",
            CultMeshEcdsaP256PublicKey.From("provider-content", provider), "odin-content",
            Now.AddMinutes(-1), Now.AddMinutes(5), odin);
        var descriptor = new CultMeshVerseDescriptor(
            "aetheria", "Aetheria", CultMeshVerseAuthorityModel.OperatorCluster,
            new CultMeshVerseCompatibility("cultmesh.v0", "rules"),
            authorityRoutes: new[] { route });
        using var discovery = new CultMeshDiscoveryService(new[] { new SignedRouteSource(descriptor) });
        var handler = new FixedContentHandler(payload);
        using var manager = new CultMeshSessionManager(
            discovery,
            Array.Empty<ICultMeshTransportConnector>(),
            new ICultMeshContentTransportConnector[]
            {
                new CultMeshHttpsContentTransportConnector(() => handler)
            },
            new CultMeshSessionManagerOptions
            {
                Trust = new CultMeshAuthorityTrustPolicy(
                    CultMeshAuthorityTrustMode.AuthenticatedRemote,
                    new[] { CultMeshEcdsaP256PublicKey.From("odin-content", odin) }),
                Clock = new FixedClock(Now)
            });

        var session = await manager.ConnectContentAsync(
            new CultMeshSessionTarget("aetheria", "aetheria-daemon"));
        using var destination = new MemoryStream();
        await session.CopyChunkToAsync(new CultMeshCdnChunkRef
        {
            ChunkHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            RecordKey = "cdn:chunk:certified",
            SizeBytes = payload.Length
        }, destination);

        destination.ToArray().Should().Equal(payload);
        handler.RequestUri.Should().NotBeNull();
        handler.RequestUri!.Scheme.Should().Be(Uri.UriSchemeHttps);
        handler.RequestUri.Query.Should().Contain("chunkHash=").And.Contain("recordKey=");
    }

    // Shared-vector check with `packages/cultnet-ts/src/cultmesh-authority.ts`.
    // The C# reference signs a route and a session proof with fixed keys and
    // writes them when CULTMESH_WRITE_VECTORS=1; otherwise it asserts the
    // committed file still verifies here. The TypeScript test verifies the same
    // file, so both runtimes are pinned to the same transcript bytes.
    [Test]
    public void AuthorityRouteVectorsAreSharedWithTypeScript()
    {
        var path = Path.Combine(RepoRoot(), "contracts", "cultmesh", "authority-route-vectors.json");
        using var odin = ECDsa.Create();
        odin.ImportFromPem(VectorOdinPrivateKeyPem);
        using var provider = ECDsa.Create();
        provider.ImportFromPem(VectorProviderPrivateKeyPem);
        var odinPublic = CultMeshEcdsaP256PublicKey.From("odin-vector", odin);
        var providerPublic = CultMeshEcdsaP256PublicKey.From("provider-vector", provider);
        var request = new GameCult.Networking.CultMeshSessionOpenMessage
        {
            MessageId = "vector-message-1",
            SourceRuntimeId = "vector-consumer",
            VerseId = "aetheria",
            AuthorityRuntimeId = "aetheria-daemon",
            ProtocolId = CultMeshProtocols.Documents.Value,
            RouteGeneration = "vector-generation-1",
            ClientNonce = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("vector-nonce")))
        };

        if (Environment.GetEnvironmentVariable("CULTMESH_WRITE_VECTORS") == "1")
        {
            var signedRoute = CultMeshAuthorityProof.CreateSignedRoute(
                "aetheria", "aetheria-daemon", "wss://provider.example/mesh",
                new[] { CultMeshProtocols.Documents.Value, CultMeshProtocols.Content.Value }, 7, "vector-generation-1",
                providerPublic, odinPublic.KeyId, Now.AddMinutes(-1), Now.AddHours(1), odin);
            var signer = new CultMeshSessionProofSigner(signedRoute, provider);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                nowUnixMilliseconds = Now.ToUnixTimeMilliseconds(),
                odinRoot = new { keyId = odinPublic.KeyId, x = odinPublic.X, y = odinPublic.Y },
                route = new
                {
                    verseId = "aetheria",
                    authorityRuntimeId = signedRoute.AuthorityRuntimeId,
                    endpoint = signedRoute.Endpoint,
                    protocolIds = signedRoute.ProtocolIds,
                    priority = signedRoute.Priority,
                    generation = signedRoute.Generation,
                    certificate = new
                    {
                        providerKey = new { keyId = providerPublic.KeyId, x = providerPublic.X, y = providerPublic.Y },
                        odinKeyId = signedRoute.Certificate!.OdinKeyId,
                        issuedAtUnixMilliseconds = signedRoute.Certificate.IssuedAtUnixMilliseconds,
                        expiresAtUnixMilliseconds = signedRoute.Certificate.ExpiresAtUnixMilliseconds,
                        signature = signedRoute.Certificate.Signature
                    }
                },
                sessionProof = new
                {
                    request = new
                    {
                        schemaVersion = request.SchemaVersion,
                        messageId = request.MessageId,
                        sourceRuntimeId = request.SourceRuntimeId,
                        verseId = request.VerseId,
                        authorityRuntimeId = request.AuthorityRuntimeId,
                        protocolId = request.ProtocolId,
                        routeGeneration = request.RouteGeneration,
                        clientNonce = request.ClientNonce
                    },
                    providerKeyId = signer.ProviderKeyId,
                    providerSignature = signer.Sign(request)
                }
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }) + "\n");
        }

        using var vectors = JsonDocument.Parse(File.ReadAllText(path));
        var rootElement = vectors.RootElement;
        var routeElement = rootElement.GetProperty("route");
        var certificateElement = routeElement.GetProperty("certificate");
        var route = new CultMeshAuthorityRoute(
            routeElement.GetProperty("authorityRuntimeId").GetString()!,
            routeElement.GetProperty("endpoint").GetString()!,
            routeElement.GetProperty("protocolIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            routeElement.GetProperty("priority").GetInt32(),
            routeElement.GetProperty("generation").GetString(),
            new CultMeshRouteCertificate(
                providerPublic,
                certificateElement.GetProperty("odinKeyId").GetString()!,
                certificateElement.GetProperty("issuedAtUnixMilliseconds").GetInt64(),
                certificateElement.GetProperty("expiresAtUnixMilliseconds").GetInt64(),
                certificateElement.GetProperty("signature").GetString()!));
        var trust = new CultMeshAuthorityTrustPolicy(CultMeshAuthorityTrustMode.AuthenticatedRemote, new[] { odinPublic });
        var now = DateTimeOffset.FromUnixTimeMilliseconds(rootElement.GetProperty("nowUnixMilliseconds").GetInt64());
        var verseId = routeElement.GetProperty("verseId").GetString()!;

        trust.Validate(verseId, route, now);
        var proofElement = rootElement.GetProperty("sessionProof");
        var accepted = Accepted(request, proofElement.GetProperty("providerSignature").GetString()!);
        accepted.ProviderKeyId = proofElement.GetProperty("providerKeyId").GetString()!;
        CultMeshAuthorityProof.VerifySessionProof(request, accepted, verseId, route, trust, now).Should().BeTrue();
    }

    // The other direction. `scripts/sign-cultmesh-authority-vectors.mjs` builds
    // the transcripts with the TypeScript module and signs them with
    // TypeScript-owned keys over awkward inputs (a quic scheme, unsorted
    // protocol ids, non-ASCII generation text, a non-default priority). Only
    // the reference implementation judges the file here; the keys come from
    // the file, never from this side.
    [Test]
    public void AuthorityRouteVectorsSignedByTypeScriptVerifyHere()
    {
        var path = Path.Combine(RepoRoot(), "contracts", "cultmesh", "authority-route-vectors.ts-signed.json");
        using var vectors = JsonDocument.Parse(File.ReadAllText(path));
        var rootElement = vectors.RootElement;
        var routeElement = rootElement.GetProperty("route");
        var certificateElement = routeElement.GetProperty("certificate");
        var odinRoot = PublicKey(rootElement.GetProperty("odinRoot"));
        var providerKey = PublicKey(certificateElement.GetProperty("providerKey"));
        var certificate = new CultMeshRouteCertificate(
            providerKey,
            certificateElement.GetProperty("odinKeyId").GetString()!,
            certificateElement.GetProperty("issuedAtUnixMilliseconds").GetInt64(),
            certificateElement.GetProperty("expiresAtUnixMilliseconds").GetInt64(),
            certificateElement.GetProperty("signature").GetString()!);
        var protocolIds = routeElement.GetProperty("protocolIds").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var priority = routeElement.GetProperty("priority").GetInt32();
        CultMeshAuthorityRoute Route(int routePriority) => new(
            routeElement.GetProperty("authorityRuntimeId").GetString()!,
            routeElement.GetProperty("endpoint").GetString()!,
            protocolIds,
            routePriority,
            routeElement.GetProperty("generation").GetString(),
            certificate);
        var route = Route(priority);
        var trust = new CultMeshAuthorityTrustPolicy(CultMeshAuthorityTrustMode.AuthenticatedRemote, new[] { odinRoot });
        var now = DateTimeOffset.FromUnixTimeMilliseconds(rootElement.GetProperty("nowUnixMilliseconds").GetInt64());
        var verseId = routeElement.GetProperty("verseId").GetString()!;

        protocolIds.Should().NotBeInAscendingOrder(StringComparer.Ordinal, "the vector must exercise the sort");
        trust.Validate(verseId, route, now);
        Action tampered = () => trust.Validate(verseId, Route(priority + 1), now);
        tampered.Should().Throw<CultMeshSessionException>()
            .Which.Failure.Message.Should().Be("The Odin route certificate signature is invalid.");

        var proofElement = rootElement.GetProperty("sessionProof");
        var requestElement = proofElement.GetProperty("request");
        var request = new GameCult.Networking.CultMeshSessionOpenMessage
        {
            MessageId = requestElement.GetProperty("messageId").GetString(),
            SourceRuntimeId = requestElement.GetProperty("sourceRuntimeId").GetString(),
            VerseId = requestElement.GetProperty("verseId").GetString(),
            AuthorityRuntimeId = requestElement.GetProperty("authorityRuntimeId").GetString(),
            ProtocolId = requestElement.GetProperty("protocolId").GetString(),
            RouteGeneration = requestElement.GetProperty("routeGeneration").GetString(),
            ClientNonce = requestElement.GetProperty("clientNonce").GetString()
        };
        var accepted = Accepted(request, proofElement.GetProperty("providerSignature").GetString()!);
        accepted.ProviderKeyId = proofElement.GetProperty("providerKeyId").GetString()!;
        CultMeshAuthorityProof.VerifySessionProof(request, accepted, verseId, route, trust, now).Should().BeTrue();

        var replayed = new GameCult.Networking.CultMeshSessionOpenMessage
        {
            MessageId = request.MessageId,
            SourceRuntimeId = request.SourceRuntimeId,
            VerseId = request.VerseId,
            AuthorityRuntimeId = request.AuthorityRuntimeId,
            ProtocolId = request.ProtocolId,
            RouteGeneration = request.RouteGeneration,
            ClientNonce = "replayed"
        };
        var replayedAccepted = Accepted(replayed, accepted.ProviderSignature!);
        replayedAccepted.ProviderKeyId = accepted.ProviderKeyId;
        CultMeshAuthorityProof.VerifySessionProof(replayed, replayedAccepted, verseId, route, trust, now).Should().BeFalse();
    }

    private static CultMeshEcdsaP256PublicKey PublicKey(JsonElement element) => new(
        element.GetProperty("keyId").GetString()!,
        element.GetProperty("x").GetString()!,
        element.GetProperty("y").GetString()!);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CultLib.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("CultLib.sln was not found above the test directory.");
    }

    // Fixed test keys. They sign only the committed vector and prove nothing else.
    private const string VectorOdinPrivateKeyPem = @"-----BEGIN PRIVATE KEY-----
MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgHY0//QOheSwARzXE
iX/nGqSD7XFAy530QQqKeXPNxMChRANCAASyEksa2ZGtboPztVAq/TFhB6Qsh8SR
43t1VrDCmYgpY4kehsd2lQoIc0sqO06l6q/td/ey+9ygzWbszdnGeeSB
-----END PRIVATE KEY-----";

    private const string VectorProviderPrivateKeyPem = @"-----BEGIN PRIVATE KEY-----
MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgVC+9X4t+1HAiIxP4
TEuAuBAY+3ryKK8SaitB0TqsPQqhRANCAATpci+DHdbWruUkURXTJht4pY0WCyyr
ngEcG6H2ALBwf6ItRYiq2rni4nPlXpszsdfDDcBurYZmTFFnqjcOrzTp
-----END PRIVATE KEY-----";

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

    private sealed class SigningConnector : ICultMeshTransportConnector
    {
        private readonly CultMeshSessionProofSigner _signer;
        public SigningConnector(CultMeshSessionProofSigner signer) => _signer = signer;
        public List<string> Attempts { get; } = new();
        public string ConnectorId => "signed-provider";
        public int Priority => 0;
        public bool CanConnect(CultMeshTransportCandidate candidate) => true;
        public Task<ICultNetSchemaClient> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshProtocolId protocol,
            CancellationToken cancellationToken = default)
        {
            Attempts.Add(candidate.Endpoint);
            return Task.FromResult<ICultNetSchemaClient>(new SigningClient(_signer));
        }
    }

    private sealed class SigningClient : ICultNetSchemaClient
    {
        private readonly CultMeshSessionProofSigner _signer;
        private Action<CultMeshSessionAcceptedMessage>? _accepted;
        public SigningClient(CultMeshSessionProofSigner signer) => _signer = signer;
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
                ProviderKeyId = _signer.ProviderKeyId,
                ProviderSignature = _signer.Sign(request)
            });
        }
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (typeof(T) == typeof(CultMeshSessionAcceptedMessage))
                _accepted = response => callback((T)(object)response);
        }
        public void Dispose() { }
    }

    private sealed class FixedContentHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        public FixedContentHandler(byte[] payload) => _payload = payload;
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            });
        }
    }

    private sealed class FixedClock : ICultMeshClock
    {
        public FixedClock(DateTimeOffset now) => UtcNow = now;
        public DateTimeOffset UtcNow { get; }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            Task.Delay(delay, cancellationToken);
    }
}
