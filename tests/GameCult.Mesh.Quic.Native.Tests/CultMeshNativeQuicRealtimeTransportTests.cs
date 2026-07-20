using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using GameCult.Mesh.Quic;
using GameCult.Mesh.Quic.Native;
using NUnit.Framework;

namespace GameCult.Mesh.Quic.Native.Tests;

[TestFixture]
public sealed class CultMeshNativeQuicRealtimeTransportTests
{
    [Test]
    public async Task NativeConnectorReceivesFromExternalManagedProvider()
    {
        var endpoint = Environment.GetEnvironmentVariable("CULTMESH_NATIVE_EXTERNAL_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
            Assert.Ignore("Set CULTMESH_NATIVE_EXTERNAL_ENDPOINT to exercise cross-process native/managed parity.");

        var connector = new CultMeshNativeQuicRealtimeTransportConnector();
        using var client = await connector.ConnectAsync(
            new CultMeshTransportCandidate(endpoint!),
            CultMeshEndpointId.Parse("aetheria.local"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var frame = await client.ReceiveAsync(timeout.Token);

        client.TransportId.Should().Be("msquic-native-realtime");
        frame.SchemaId.Should().NotBeNullOrWhiteSpace();
        frame.BodyId.Should().NotBeNullOrWhiteSpace();
        frame.Payload.Should().NotBeEmpty();
    }

    [Test]
    public async Task NativeConnectorAuthenticatesAndReceivesLatestOnlyFrame()
    {
        if (!OperatingSystem.IsWindows() || !QuicListener.IsSupported)
            Assert.Ignore("Native MsQuic integration requires Windows QUIC support.");
        using var certificate = CreateCertificate();
        await using var server = await CultMeshQuicRealtimeServer.ListenAsync(new CultMeshQuicRealtimeServerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ServerCertificate = certificate
        });
        var pin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var endpoint = $"cultmesh-state+quic://127.0.0.1:{server.LocalEndPoint.Port}?cert-sha256={pin}";
        var connector = new CultMeshNativeQuicRealtimeTransportConnector();

        using var client = await connector.ConnectAsync(
            new CultMeshTransportCandidate(endpoint),
            CultMeshEndpointId.Parse("aetheria.local"));
        var expected = new CultMeshRealtimeFrame
        {
            ChannelId = "aetheria.entities",
            SchemaId = "gamecult.aetheria.entity_soa.v1",
            BodyId = "aetheria.entities.current-zone",
            ProducerEpoch = 17,
            Sequence = 23,
            Delivery = CultMeshRealtimeDelivery.LatestOnly,
            Payload = new byte[] { 1, 3, 3, 7 }
        };
        await WaitUntilAsync(() => server.ConnectionCount == 1);
        await server.BroadcastAsync(expected);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var actual = await client.ReceiveAsync(timeout.Token);

        client.TransportId.Should().Be("msquic-native-realtime");
        actual.ChannelId.Should().Be(expected.ChannelId);
        actual.SchemaId.Should().Be(expected.SchemaId);
        actual.BodyId.Should().Be(expected.BodyId);
        actual.ProducerEpoch.Should().Be(expected.ProducerEpoch);
        actual.Sequence.Should().Be(expected.Sequence);
        actual.Delivery.Should().Be(CultMeshRealtimeDelivery.LatestOnly);
        actual.Payload.ToArray().Should().Equal(expected.Payload.ToArray());
    }

    [Test]
    public async Task NativeConnectorRejectsWrongAdvertisedPin()
    {
        if (!OperatingSystem.IsWindows() || !QuicListener.IsSupported)
            Assert.Ignore("Native MsQuic integration requires Windows QUIC support.");
        using var certificate = CreateCertificate();
        await using var server = await CultMeshQuicRealtimeServer.ListenAsync(new CultMeshQuicRealtimeServerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ServerCertificate = certificate
        });
        var connector = new CultMeshNativeQuicRealtimeTransportConnector();
        var endpoint = $"cultmesh-state+quic://127.0.0.1:{server.LocalEndPoint.Port}?cert-sha256={new string('0', 64)}";

        var connect = () => connector.ConnectAsync(
            new CultMeshTransportCandidate(endpoint),
            CultMeshEndpointId.Parse("aetheria.local"));

        await connect.Should().ThrowAsync<IOException>()
            .WithMessage("*certificate*pin*");
    }

    [Test]
    public async Task NativeClientDepartureCannotBreakProviderBroadcast()
    {
        if (!OperatingSystem.IsWindows() || !QuicListener.IsSupported)
            Assert.Ignore("Native MsQuic integration requires Windows QUIC support.");
        using var certificate = CreateCertificate();
        await using var server = await CultMeshQuicRealtimeServer.ListenAsync(new CultMeshQuicRealtimeServerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ServerCertificate = certificate
        });
        var pin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var endpoint = $"cultmesh-state+quic://127.0.0.1:{server.LocalEndPoint.Port}?cert-sha256={pin}";
        var connector = new CultMeshNativeQuicRealtimeTransportConnector();
        var departedClient = await connector.ConnectAsync(
            new CultMeshTransportCandidate(endpoint),
            CultMeshEndpointId.Parse("aetheria.local"));
        using var healthyClient = await connector.ConnectAsync(
            new CultMeshTransportCandidate(endpoint),
            CultMeshEndpointId.Parse("aetheria.local"));
        await WaitUntilAsync(() => server.ConnectionCount == 2);
        await server.BroadcastAsync(Frame(sequence: 1));
        using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await departedClient.ReceiveAsync(receiveTimeout.Token);
        await healthyClient.ReceiveAsync(receiveTimeout.Token);

        departedClient.Dispose();

        for (var sequence = 2; sequence <= 100; sequence++)
            await server.BroadcastAsync(Frame(sequence)).WaitAsync(TimeSpan.FromSeconds(1));

        CultMeshRealtimeFrame received;
        do
        {
            received = await healthyClient.ReceiveAsync(receiveTimeout.Token);
        }
        while (received.Sequence < 100);

        received.Sequence.Should().Be(100,
            "a departed observer cannot stall provider publication or starve healthy peers");
    }

    private static CultMeshRealtimeFrame Frame(long sequence) => new()
    {
        ChannelId = "aetheria.entities",
        SchemaId = "gamecult.aetheria.entity_soa.v1",
        BodyId = "aetheria.entities.current-zone",
        ProducerEpoch = 17,
        Sequence = sequence,
        Delivery = CultMeshRealtimeDelivery.LatestOnly,
        Payload = new byte[] { 1, 3, 3, 7 }
    };

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

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
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(10));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
    }
}
