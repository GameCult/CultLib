using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using NUnit.Framework;

namespace GameCult.Mesh.Quic.Tests;

[TestFixture]
public sealed class CultMeshQuicRealtimeTransportTests
{
    [Test]
    public async Task ConnectorNegotiatesAgainstAPlainMsQuicListener()
    {
        if (!QuicListener.IsSupported) Assert.Ignore("MsQuic is unavailable on this test host.");
        var protocol = new System.Net.Security.SslApplicationProtocol("cultmesh-state-v1");
        using var certificate = CreateCertificate();
        await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = new List<System.Net.Security.SslApplicationProtocol> { protocol },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = 0,
                DefaultStreamErrorCode = 0,
                ServerAuthenticationOptions = new System.Net.Security.SslServerAuthenticationOptions
                {
                    ApplicationProtocols = new List<System.Net.Security.SslApplicationProtocol> { protocol },
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
                    ServerCertificate = certificate
                }
            })
        });
        var accept = listener.AcceptConnectionAsync().AsTask();
        var connector = new CultMeshQuicRealtimeTransportConnector(new CultMeshQuicRealtimeConnectorOptions
        {
            ApplicationProtocol = protocol,
            ValidateProviderCertificate = (_, _, _, _) => true
        });
        using var client = await connector.ConnectAsync(
            new CultMeshTransportCandidate($"cultmesh-state+quic://127.0.0.1:{listener.LocalEndPoint.Port}"),
            CultMeshEndpointId.Parse("service:aetheria.daemon"));
        await using var serverConnection = await accept;

        client.TransportId.Should().Be("msquic-realtime");
    }

    [Test]
    public async Task AdvertisedCertificatePinAuthenticatesASelfSignedProvider()
    {
        if (!QuicListener.IsSupported) Assert.Ignore("MsQuic is unavailable on this test host.");
        using var certificate = CreateCertificate();
        await using var server = await CultMeshQuicRealtimeServer.ListenAsync(new CultMeshQuicRealtimeServerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ServerCertificate = certificate
        });
        var pin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var endpoint = $"{CultMeshQuicRealtimeTransportConnector.Scheme}://127.0.0.1:{server.LocalEndPoint.Port}?cert-sha256={pin}";
        var connector = new CultMeshQuicRealtimeTransportConnector();

        using var client = await connector.ConnectAsync(
            new CultMeshTransportCandidate(endpoint),
            CultMeshEndpointId.Parse("service:aetheria.daemon"));

        client.TransportId.Should().Be("msquic-realtime");
        await WaitUntilAsync(() => server.ConnectionCount == 1);
    }

    [Test]
    public async Task ReliableOrderedFramesShareAnOrderedStreamInBothDirections()
    {
        if (!QuicListener.IsSupported) Assert.Ignore("MsQuic is unavailable on this test host.");
        using var certificate = CreateCertificate();
        await using var server = await CultMeshQuicRealtimeServer.ListenAsync(new CultMeshQuicRealtimeServerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ServerCertificate = certificate
        });
        var endpointId = CultMeshEndpointId.Parse("service:aetheria.daemon");
        using var sessions = CreateSessions(server, out var validatedIdentity);
        CultMeshRealtimeSession session;
        try
        {
            session = await sessions.ConnectRealtimeAsync(endpointId);
        }
        catch when (server.BackgroundFailure.IsCompleted)
        {
            throw new InvalidOperationException("QUIC server accept loop failed.", await server.BackgroundFailure);
        }
        await WaitUntilAsync(() => server.ConnectionCount == 1);

        await session.SendAsync(Frame(1, CultMeshRealtimeDelivery.ReliableOrdered));
        await session.SendAsync(Frame(2, CultMeshRealtimeDelivery.ReliableOrdered));
        var firstAtServer = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var secondAtServer = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await server.BroadcastAsync(Frame(3, CultMeshRealtimeDelivery.ReliableOrdered));
        await server.BroadcastAsync(Frame(4, CultMeshRealtimeDelivery.ReliableOrdered));
        var firstAtClient = await session.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var secondAtClient = await session.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

        new[] { firstAtServer.Sequence, secondAtServer.Sequence }.Should().Equal(1, 2);
        new[] { firstAtClient.Sequence, secondAtClient.Sequence }.Should().Equal(3, 4);
        firstAtClient.Payload.ToArray().Should().Equal(3, 4, 5);
        validatedIdentity.Value.Should().Be(endpointId.Value);
    }

    [Test]
    public async Task LatestOnlyCoalescesPendingStateAndDropsOlderGenerations()
    {
        if (!QuicListener.IsSupported) Assert.Ignore("MsQuic is unavailable on this test host.");
        using var certificate = CreateCertificate();
        await using var server = await CultMeshQuicRealtimeServer.ListenAsync(new CultMeshQuicRealtimeServerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ServerCertificate = certificate
        });
        var endpointId = CultMeshEndpointId.Parse("service:aetheria.daemon");
        using var sessions = CreateSessions(server, out _);
        var session = await sessions.ConnectRealtimeAsync(endpointId);
        await WaitUntilAsync(() => server.ConnectionCount == 1);

        await server.BroadcastAsync(Frame(100, CultMeshRealtimeDelivery.LatestOnly, producerEpoch: 7));
        await server.BroadcastAsync(Frame(0, CultMeshRealtimeDelivery.LatestOnly, producerEpoch: 8));
        await Task.Delay(100);
        var latest = await session.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await server.BroadcastAsync(Frame(999, CultMeshRealtimeDelivery.LatestOnly, producerEpoch: 7));
        using var staleDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Func<Task> receiveStale = () => session.ReceiveAsync(staleDeadline.Token);

        latest.ProducerEpoch.Should().Be(8);
        latest.Sequence.Should().Be(0);
        await receiveStale.Should().ThrowAsync<OperationCanceledException>(
            "a late older generation must not twitch the consumer backwards");
    }

    [Test]
    public async Task UnreliableDeliveryFailsClosedUntilANativeDatagramConnectorExists()
    {
        if (!QuicListener.IsSupported) Assert.Ignore("MsQuic is unavailable on this test host.");
        using var certificate = CreateCertificate();
        await using var server = await CultMeshQuicRealtimeServer.ListenAsync(new CultMeshQuicRealtimeServerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ServerCertificate = certificate
        });
        var endpointId = CultMeshEndpointId.Parse("service:aetheria.daemon");
        using var sessions = CreateSessions(server, out _);
        var session = await sessions.ConnectRealtimeAsync(endpointId);

        Func<Task> send = () => session.SendAsync(Frame(1, CultMeshRealtimeDelivery.Unreliable));

        await send.Should().ThrowAsync<NotSupportedException>().WithMessage("*datagrams*");
        session.State.Status.Should().Be(CultMeshSessionStatus.Offline);
    }

    private static CultMeshSessionManager CreateSessions(
        CultMeshQuicRealtimeServer server,
        out IdentityObservation validatedIdentity)
    {
        validatedIdentity = new IdentityObservation();
        var observation = validatedIdentity;
        var endpoint = $"{CultMeshQuicRealtimeTransportConnector.Scheme}://127.0.0.1:{server.LocalEndPoint.Port}";
        var discovery = new CultMeshDiscoveryService(new[] { new RouteSource(endpoint) });
        var connector = new CultMeshQuicRealtimeTransportConnector(new CultMeshQuicRealtimeConnectorOptions
        {
            ValidateProviderCertificate = (identity, _, _, _) =>
            {
                observation.Value = identity.Value;
                return true;
            }
        });
        return new CultMeshSessionManager(
            discovery,
            Array.Empty<ICultMeshTransportConnector>(),
            Array.Empty<ICultMeshContentTransportConnector>(),
            new ICultMeshRealtimeTransportConnector[] { connector });
    }

    private static CultMeshRealtimeFrame Frame(
        long sequence,
        CultMeshRealtimeDelivery delivery,
        long producerEpoch = 7) => new()
    {
        ChannelId = "aetheria.entities",
        SchemaId = "eve.entity_soa.v1",
        BodyId = "body:aetheria:entities",
        ProducerEpoch = producerEpoch,
        Sequence = sequence,
        Delivery = delivery,
        Payload = new byte[] { 3, 4, 5 }
    };

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for QUIC connection state.");
            await Task.Delay(10);
        }
    }

    private sealed class RouteSource(string endpoint) : ICultMeshLookupSource
    {
        public string SourceId => "quic-test";

        public Task<IReadOnlyList<CultMeshDiscoveryObservation>> LookupAsync(
            CultMeshDiscoveryQuery query,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            IReadOnlyList<CultMeshDiscoveryObservation> result = new[]
            {
                new CultMeshDiscoveryObservation(
                    new CultMeshVerseDescriptor(
                        "aetheria",
                        "Aetheria",
                        CultMeshVerseAuthorityModel.OperatorCluster,
                        new CultMeshVerseCompatibility("cultmesh.v1", "test"),
                        new[] { endpoint }),
                    SourceId,
                    now,
                    now.AddMinutes(1),
                    CultMeshDiscoveryTrust.Signed)
            };
            return Task.FromResult(result);
        }
    }

    private sealed class IdentityObservation
    {
        public string? Value { get; set; }
    }
}
