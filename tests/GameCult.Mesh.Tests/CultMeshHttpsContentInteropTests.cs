using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

/// <summary>
/// The C# half of the cross-process HTTPS content parity. The provider is the TypeScript one, in another process; it is
/// started, and these tests are run, by packages/cultmesh-browser/test/interop/content-https.test.ts. Trust is the
/// fixture certificate through CustomRootTrust: chain and name validation stay on, and no other certificate is accepted.
/// </summary>
[TestFixture]
public sealed class CultMeshHttpsContentInteropTests
{
    private const string Prefix = "CULTMESH_HTTPS_CONTENT_";
    private string _dir = null!;
    private string _manifests = null!;
    private string _honest = null!, _tampered = null!, _corrupt = null!;
    private X509Certificate2 _anchor = null!;

    [SetUp]
    public void SetUp()
    {
        string? Var(string name) => Environment.GetEnvironmentVariable(Prefix + name);
        var values = new[] { "ENDPOINT", "TAMPERED_ENDPOINT", "CORRUPT_ENDPOINT", "MANIFESTS", "CA_PEM" }.Select(Var).ToArray();
        if (values.Any(string.IsNullOrEmpty))
            Assert.Ignore("Run by packages/cultmesh-browser/test/interop/content-https.test.ts, which starts the TypeScript provider and sets " + Prefix + "*.");
        (_honest, _tampered, _corrupt, _manifests) = (values[0]!, values[1]!, values[2]!, values[3]!);
        _anchor = X509Certificate2.CreateFromPem(File.ReadAllText(values[4]!));
        _dir = Path.Combine(Path.GetTempPath(), "cultmesh-https-interop", Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (_dir != null && Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private CultMeshCdnArtifactManifest Manifest(string name) =>
        (CultMeshCdnArtifactManifest)CultDocumentMessagePackSerialization.DeserializeUntyped(
            typeof(CultMeshCdnArtifactManifest), File.ReadAllBytes(Path.Combine(_manifests, name + ".manifest")));

    private static readonly CultMeshSessionTarget Target = new("aetheria", "aetheria.daemon");

    private sealed class Rig : IDisposable
    {
        public CultMeshDiscoveryService Discovery = null!;
        public CultMeshSessionManager Sessions = null!;
        public CultCache State = null!;
        public CultMeshContentTransferService Transfer = null!;
        public void Dispose() { State.Dispose(); Sessions.Dispose(); Discovery.Dispose(); }
    }

    /// <summary>The production path: discovery, session manager with the unmodified HTTPS connector, session provider, transfer service.</summary>
    private Rig Build(string endpoint, X509Certificate2 anchor, string cacheDir)
    {
        var rig = new Rig { Discovery = new CultMeshDiscoveryService(new[] { new RouteSource(endpoint) }) };
        rig.Sessions = new CultMeshSessionManager(rig.Discovery, Array.Empty<ICultMeshTransportConnector>(),
            new ICultMeshContentTransportConnector[]
            {
                new CultMeshHttpsContentTransportConnector(() => new SocketsHttpHandler
                {
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        CertificateChainPolicy = new X509ChainPolicy
                        {
                            TrustMode = X509ChainTrustMode.CustomRootTrust,
                            CustomTrustStore = { anchor },
                            RevocationMode = X509RevocationMode.NoCheck
                        }
                    }
                })
            }, CultMeshTestTrust.LocalSessions);
        var provider = new CultMeshSessionContentProvider("ts-https", rig.Sessions, Target);
        rig.State = new CultCache(CultMesh.CreateCultCacheDocumentRegistry(typeof(CultMeshContentTransferStateDocument)));
        rig.Transfer = new CultMeshContentTransferService(rig.State, new[] { provider }, new CultMeshContentTransferOptions(cacheDir));
        return rig;
    }

    private static IEnumerable<Exception> Chain(Exception e)
    {
        for (var current = e; current != null; current = current.InnerException) yield return current;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Test]
    public async Task HttpsTransferFetchesFromExternalTypeScriptProvider()
    {
        using var rig = Build(_honest, _anchor, _dir);
        foreach (var name in new[] { "multi", "single", "empty" })
        {
            var manifest = Manifest(name);
            var path = await rig.Transfer.FetchAsync(manifest);
            Sha(path).Should().Be(manifest.ContentHash.Replace("sha256:", ""), name);
            if (name == "multi") manifest.Chunks.Should().HaveCount(4);
            if (name == "empty") manifest.SizeBytes.Should().Be(0);
        }

        var session = await rig.Sessions.ConnectContentAsync(Target);
        var unknown = new CultMeshCdnChunkRef { ChunkHash = new string('0', 64), SizeBytes = 1 };
        var missing = async () => await session.CopyChunkToAsync(unknown, new MemoryStream());
        (await missing.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The anchor is scoped: a different self-signed certificate does not validate the same server.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=someone-else", key, HashAlgorithmName.SHA256);
        using var stranger = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var other = Build(_honest, stranger, Path.Combine(_dir, "other"));
        var refused = async () => await other.Transfer.FetchAsync(Manifest("single"));
        var thrown = (await refused.Should().ThrowAsync<Exception>()).Which;
        Chain(thrown).Should().Contain(e => e is AuthenticationException);
    }

    [Test]
    public async Task HttpsTransferRefusesTamperedBytesFromExternalTypeScriptProvider()
    {
        using var rig = Build(_tampered, _anchor, _dir);
        var multi = Manifest("multi");
        var fetch = async () => await rig.Transfer.FetchAsync(multi);
        var thrown = (await fetch.Should().ThrowAsync<AggregateException>()).Which;
        Chain(thrown).Should().Contain(e => e is InvalidDataException && e.Message == "CDN artifact chunk hash does not match its manifest.");
        (Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.body", SearchOption.AllDirectories) : Array.Empty<string>()).Should().BeEmpty();

        // The connector alone passes the wrong bytes: verification lives in the transfer owner.
        var session = await rig.Sessions.ConnectContentAsync(Target);
        using var bytes = new MemoryStream();
        await session.CopyChunkToAsync(multi.Chunks[0], bytes);
        Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant().Should().NotBe(multi.Chunks[0].ChunkHash.Replace("sha256:", ""));
    }

    [Test]
    public async Task HttpsTransferRefusesCorruptStoreFromExternalTypeScriptProvider()
    {
        using var rig = Build(_corrupt, _anchor, _dir);
        var fetch = async () => await rig.Transfer.FetchAsync(Manifest("single"));
        var thrown = (await fetch.Should().ThrowAsync<Exception>()).Which;
        Chain(thrown).OfType<HttpRequestException>().Should().Contain(e => e.StatusCode == HttpStatusCode.InternalServerError);
        (Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.body", SearchOption.AllDirectories) : Array.Empty<string>()).Should().BeEmpty();
    }

    private sealed class RouteSource(string endpoint) : ICultMeshLookupSource
    {
        public string SourceId => "test";
        public Task<IReadOnlyList<CultMeshDiscoveryObservation>> LookupAsync(CultMeshDiscoveryQuery query, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(new[]
            {
                new CultMeshDiscoveryObservation(new CultMeshVerseDescriptor("aetheria", "Aetheria",
                    CultMeshVerseAuthorityModel.OperatorCluster, new CultMeshVerseCompatibility("cultmesh.v1", "rules"),
                    new[] { endpoint }, new[] { "aetheria.daemon" }), SourceId, now, now.AddMinutes(1), CultMeshDiscoveryTrust.Signed)
            });
        }
    }
}
