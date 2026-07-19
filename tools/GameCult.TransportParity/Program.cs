using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GameCult.Caching;
using GameCult.Mesh;
using GameCult.Networking;

var payloadBytes = ReadPayloadBytes(args);
var payload = new byte[payloadBytes];
new Random(0xAE7E).NextBytes(payload);
var expectedHash = Convert.ToHexString(SHA256.HashData(payload));

Console.WriteLine($"payloadBytes={payloadBytes} quicSupported={QuicListener.IsSupported}");
Print(MeasureMappedFile(payload));
var tcp = await MeasureTcpAsync(payload, expectedHash);
Print(tcp);
Print(await MeasureCultMeshTcpAsync(payload));

if (QuicListener.IsSupported)
    Print(await MeasureQuicAsync(payload, expectedHash));
else
    Console.WriteLine("plane=quic status=unsupported");

Print(await MeasureCultMeshRudpAsync(payload));

static int ReadPayloadBytes(string[] args)
{
    const int defaultBytes = 56_204_750;
    var index = Array.IndexOf(args, "--bytes");
    if (index < 0) return defaultBytes;
    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var bytes) || bytes <= 0)
        throw new ArgumentException("--bytes requires a positive 32-bit integer.");
    return bytes;
}

static void Print(Result result) =>
    Console.WriteLine(
        $"plane={result.Plane} elapsedMs={result.Elapsed.TotalMilliseconds:F1} " +
        (result.GoodputMiBps.HasValue ? $"goodputMiBps={result.GoodputMiBps.Value:F1}" : "") +
        (result.WireBytes.HasValue ? $" wireBytes={result.WireBytes.Value}" : string.Empty));

static Result MeasureMappedFile(byte[] payload)
{
    var source = TempPath("mapped-source");
    File.WriteAllBytes(source, payload);
    var elapsed = Stopwatch.StartNew();
    try
    {
        using var mapping = MemoryMappedFile.CreateFromFile(
            source, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        if (view.ReadByte(0) != payload[0] || view.ReadByte(payload.LongLength - 1) != payload[^1])
            throw new InvalidDataException("Mapped body did not expose the provider file.");
        elapsed.Stop();
        return Result.CreateOpen("mapped-file-open", elapsed.Elapsed);
    }
    finally
    {
        File.Delete(source);
    }
}

static async Task<Result> MeasureTcpAsync(byte[] payload, string expectedHash)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var endpoint = (IPEndPoint)listener.LocalEndpoint;
    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync();
        await peer.GetStream().WriteAsync(payload);
        peer.Client.Shutdown(SocketShutdown.Send);
    });

    var output = TempPath("tcp");
    var elapsed = Stopwatch.StartNew();
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(endpoint.Address, endpoint.Port);
        await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await client.GetStream().CopyToAsync(file);
        await file.FlushAsync();
    }
    await server;
    VerifyAndDelete(output, expectedHash);
    elapsed.Stop();
    return Result.Create("tcp", payload.LongLength, elapsed.Elapsed);
}

static async Task<Result> MeasureQuicAsync(byte[] payload, string expectedHash)
{
    var protocol = new SslApplicationProtocol("cultmesh-parity");
    using var certificate = CreateCertificate();
    await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
    {
        ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        ApplicationProtocols = [protocol],
        ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
        {
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 0,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = [protocol],
                EnabledSslProtocols = SslProtocols.Tls13,
                ServerCertificate = certificate
            }
        })
    });

    var server = Task.Run(async () =>
    {
        await using var connection = await listener.AcceptConnectionAsync();
        await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional);
        await stream.WriteAsync(payload);
        stream.CompleteWrites();
        await stream.WritesClosed;
    });

    var output = TempPath("quic");
    var elapsed = Stopwatch.StartNew();
    await using (var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
    {
        RemoteEndPoint = listener.LocalEndPoint,
        DefaultCloseErrorCode = 0,
        DefaultStreamErrorCode = 0,
        MaxInboundUnidirectionalStreams = 1,
        ClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [protocol],
            EnabledSslProtocols = SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        }
    }))
    await using (var stream = await connection.AcceptInboundStreamAsync())
    await using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
        1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
    {
        await stream.CopyToAsync(file);
        await file.FlushAsync();
    }
    await server;
    VerifyAndDelete(output, expectedHash);
    elapsed.Stop();
    return Result.Create("quic-msquic", payload.LongLength, elapsed.Elapsed);
}

static async Task<Result> MeasureCultMeshRudpAsync(byte[] payload)
{
    var artifact = CultMesh.PackCdnArtifact(
        "parity/body", payload, new CultMeshCdnPackOptions { ChunkSizeBytes = 1024 * 1024 });
    using var providerCache = new CultCache(CultMesh.CreateCultCacheDocumentRegistry(
        typeof(CultMeshCdnArtifactManifest), typeof(CultMeshCdnArtifactChunk)));
    await CultMeshCdn.PublishAsync(providerCache, artifact);

    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    using var wireServer = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
    {
        RuntimeId = "cultmesh-transport-parity",
        Socket = socket,
        MaxFragmentBytes = 1024,
        MaxPendingReliablePackets = 8192
    });
    using var contentServer = new CultMeshLegacyRudpContentServer(wireServer, providerCache);
    using var pumpCancellation = new CancellationTokenSource();
    var pump = Task.Run(async () =>
    {
        while (!pumpCancellation.IsCancellationRequested)
        {
            var progress = await wireServer.PollAvailableAsync(256);
            wireServer.PollResends();
            if (progress.TransportItemsConsumed == 0)
                await Task.Delay(1, pumpCancellation.Token);
        }
    });

    var endpoint = $"rudp://127.0.0.1:{wireServer.LocalEndPoint.Port}";
    using var discovery = new CultMeshDiscoveryService([new RouteSource(endpoint)]);
    using var sessions = new CultMeshSessionManager(
        discovery,
        Array.Empty<ICultMeshTransportConnector>(),
        [new CultMeshLegacyRudpContentTransportConnector(
            new CultMeshLegacyRudpContentTransportOptions { ResponseTimeout = TimeSpan.FromMinutes(2) })]);
    var provider = new CultMeshSessionContentProvider(
        "parity.provider", sessions, CultMeshEndpointId.Parse("service:parity.provider"));
    var directory = Path.Combine(Path.GetTempPath(), "cultmesh-transport-parity", Guid.NewGuid().ToString("N"));
    using var transferState = new CultCache(CultMesh.CreateCultCacheDocumentRegistry(
        typeof(CultMeshContentTransferStateDocument)));
    var transfer = new CultMeshContentTransferService(
        transferState, [provider], new CultMeshContentTransferOptions(directory));

    var elapsed = Stopwatch.StartNew();
    try
    {
        var path = await transfer.FetchAsync(artifact.Manifest);
        elapsed.Stop();
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("CultMesh RUDP payload did not match source.");
        return Result.Create("cultmesh-rudp", payload.LongLength, elapsed.Elapsed, wireServer.Stats.BytesSent);
    }
    finally
    {
        pumpCancellation.Cancel();
        try { await pump; } catch (OperationCanceledException) { }
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

static async Task<Result> MeasureCultMeshTcpAsync(byte[] payload)
{
    var artifact = CultMesh.PackCdnArtifact(
        "parity/body", payload, new CultMeshCdnPackOptions { ChunkSizeBytes = 1024 * 1024 });
    using var providerCache = new CultCache(CultMesh.CreateCultCacheDocumentRegistry(
        typeof(CultMeshCdnArtifactManifest), typeof(CultMeshCdnArtifactChunk)));
    await CultMeshCdn.PublishAsync(providerCache, artifact);

    using var contentServer = new CultMeshTcpContentServer(
        new TcpListener(IPAddress.Loopback, 0), providerCache);
    var endpoint = $"{CultMeshTcpContentTransportConnector.Scheme}://127.0.0.1:{contentServer.LocalEndPoint.Port}";
    using var discovery = new CultMeshDiscoveryService([new RouteSource(endpoint)]);
    using var sessions = new CultMeshSessionManager(
        discovery,
        Array.Empty<ICultMeshTransportConnector>(),
        [new CultMeshTcpContentTransportConnector()]);
    var provider = new CultMeshSessionContentProvider(
        "parity.provider", sessions, CultMeshEndpointId.Parse("service:parity.provider"));
    var directory = Path.Combine(Path.GetTempPath(), "cultmesh-transport-parity", Guid.NewGuid().ToString("N"));
    using var transferState = new CultCache(CultMesh.CreateCultCacheDocumentRegistry(
        typeof(CultMeshContentTransferStateDocument)));
    var transfer = new CultMeshContentTransferService(
        transferState, [provider], new CultMeshContentTransferOptions(directory));

    var elapsed = Stopwatch.StartNew();
    try
    {
        var path = await transfer.FetchAsync(artifact.Manifest);
        elapsed.Stop();
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("CultMesh TCP payload did not match source.");
        return Result.Create("cultmesh-tcp-content", payload.LongLength, elapsed.Elapsed);
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

static X509Certificate2 CreateCertificate()
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
    var names = new SubjectAlternativeNameBuilder();
    names.AddDnsName("localhost");
    names.AddIpAddress(IPAddress.Loopback);
    request.CertificateExtensions.Add(names.Build());
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    using var certificate = request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
}

static string TempPath(string plane) =>
    Path.Combine(Path.GetTempPath(), $"cultmesh-parity-{plane}-{Guid.NewGuid():N}.body");

static void VerifyAndDelete(string path, string expectedHash)
{
    try
    {
        using var input = File.OpenRead(path);
        var actualHash = Convert.ToHexString(SHA256.HashData(input));
        if (!actualHash.Equals(expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Body hash mismatch: expected {expectedHash}, got {actualHash}.");
    }
    finally
    {
        File.Delete(path);
    }
}

internal sealed class RouteSource(string endpoint) : ICultMeshLookupSource
{
    public string SourceId => "parity-route";

    public Task<IReadOnlyList<CultMeshDiscoveryObservation>> LookupAsync(
        CultMeshDiscoveryQuery query, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<CultMeshDiscoveryObservation> observations =
        [
            new(
                new CultMeshVerseDescriptor(
                    "parity", "Transport parity", CultMeshVerseAuthorityModel.OperatorCluster,
                    new CultMeshVerseCompatibility("cultmesh.v1", "parity"), [endpoint]),
                SourceId, now, now.AddMinutes(1), CultMeshDiscoveryTrust.Signed)
        ];
        return Task.FromResult(observations);
    }
}

internal sealed record Result(string Plane, TimeSpan Elapsed, double? GoodputMiBps, long? WireBytes)
{
    public static Result Create(string plane, long bytes, TimeSpan elapsed, long? wireBytes = null) =>
        new(plane, elapsed, bytes / 1024d / 1024d / elapsed.TotalSeconds, wireBytes);

    public static Result CreateOpen(string plane, TimeSpan elapsed) =>
        new(plane, elapsed, null, null);
}
