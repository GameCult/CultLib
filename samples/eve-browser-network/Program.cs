using System.Net;
using System.Diagnostics;
using System.Text.Json;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using GameCult.Networking;
using GameCult.Networking.WebSockets;
using MessagePack;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

const string counterKey = "counter:main";
const string surfaceKey = "sample.counter";
var arguments = Args.Parse(args);
if (arguments.Mode == "provider")
{
    await RunProviderAsync(arguments);
    return;
}
if (arguments.Mode == "headless")
{
    await RunHeadlessAsync(arguments);
    return;
}
if (arguments.Mode == "odin")
{
    await RunOdinAsync(arguments);
    return;
}
throw new InvalidOperationException("Use 'provider', 'headless', or 'odin'.");

static async Task RunOdinAsync(Args arguments)
{
    if (string.IsNullOrWhiteSpace(arguments.ProviderEndpoint))
        throw new ArgumentException("The Odin fixture requires --provider-endpoint.");
    using var catalog = new CultMeshVerseCatalog();
    var routes = new List<CultMeshAuthorityRoute>
    {
        new(
            arguments.AuthorityRuntimeId,
            arguments.ProviderEndpoint,
            [CultMeshProtocols.Documents.Value],
            priority: 10,
            generation: arguments.RouteGeneration)
    };
    if (!string.IsNullOrWhiteSpace(arguments.DecoyEndpoint))
    {
        routes.Add(new CultMeshAuthorityRoute(
            arguments.DecoyAuthorityRuntimeId,
            arguments.DecoyEndpoint,
            [CultMeshProtocols.Documents.Value],
            priority: 0,
            generation: arguments.DecoyRouteGeneration));
    }
    catalog.Upsert(new CultMeshVerseDescriptor(
        arguments.VerseId,
        arguments.VerseName,
        CultMeshVerseAuthorityModel.OperatorCluster,
        new CultMeshVerseCompatibility(arguments.TransportVersion, arguments.RulesHash),
        authorityRoutes: routes,
        description: "Local executable Odin fixture for the browser/network onboarding witness."));

    await using var schemaServer = new CultNetWebSocketSchemaServer();
    schemaServer.OnCultNet<CultMeshVerseCatalogRequestMessage>((request, peer) =>
    {
        peer.SendCultNet(CultMeshVerseMessages.CreateCatalogResponse(catalog, request));
        return Task.CompletedTask;
    });
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, arguments.Port));
    var app = builder.Build();
    app.UseWebSockets();
    app.MapCultNetWebSocket("/odin", schemaServer, new CultNetWebSocketEndpointOptions
    {
        AuthorizeAsync = context => ValueTask.FromResult(
            context.Request.Cookies.TryGetValue("cultnet_session", out var token) &&
            string.Equals(token, arguments.Token, StringComparison.Ordinal))
    });
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    Console.WriteLine("ODIN_READY " + address.Replace("http://", "ws://", StringComparison.Ordinal) + "/odin");
    await app.WaitForShutdownAsync();
    await app.DisposeAsync();
}

static async Task RunProviderAsync(Args arguments)
{
    var documentTypes = new[] { typeof(CounterState), typeof(EveSurfaceDocument) };
    var registry = CultMesh.CreateCultCacheDocumentRegistry(documentTypes);
    var documents = CultMesh.CreateCultNetDocumentRegistry(documentTypes, registry);
    using var node = await CultMesh.CreateNodeAsync(arguments.StatePath, new CultMeshNodeOptions
    {
        StartServer = false,
        CacheOptions = new CultCacheOpenOptions
        {
            Registry = registry,
            FlushOnDispose = true,
            StoreFlushOnDispose = true
        },
        DatabaseOptions = new CultNetDatabaseOptions
        {
            RuntimeId = arguments.AuthorityRuntimeId,
            DocumentRegistry = documents
        }
    });
    if (node.Cache.Get<CounterState>(new CultRecordKey(counterKey)) == null)
        await node.Database.PutAsync(new CultRecordKey(counterKey), CounterState.Initial(counterKey));
    if (node.Cache.Get<EveSurfaceDocument>(new CultRecordKey(surfaceKey)) == null)
        await node.Database.PutAsync(new CultRecordKey(surfaceKey), CreateCounterSurface());
    await node.FlushAsync();

    await using var schemaServer = new CultNetWebSocketSchemaServer();
    using var identity = new CultMeshSessionIdentityServer(
        schemaServer,
        arguments.AuthorityRuntimeId,
        [arguments.VerseId],
        [CultMeshProtocols.Documents.Value],
        [arguments.RouteGeneration]);
    using var subscriptions = new CultNetDatabaseSubscriptionServer(schemaServer, node.Database);
    using var operationGate = new SemaphoreSlim(1, 1);
    using var operations = new CultNetOperationServer(schemaServer, arguments.AuthorityRuntimeId)
        .Register<PingRequest, PingReceipt>(
            "sample.counter",
            "sample.counter.ping",
            "sample.ping_request.v1",
            "sample.ping_receipt.v1",
            context => Task.FromResult(new PingReceipt { Sequence = context.Value.Sequence }))
        .Register<IncrementRequest, IncrementReceipt>(
            "sample.counter",
            "sample.counter.increment",
            "sample.increment.v1",
            "gamecult.eve.command_receipt.v1",
            async context =>
            {
                await operationGate.WaitAsync();
                try
                {
                    var current = node.Cache.Get<CounterState>(new CultRecordKey(counterKey))
                        ?? throw new InvalidOperationException("Counter state is unavailable.");
                    if (current.Receipts.TryGetValue(context.IdempotencyKey, out var existing))
                        return existing;

                    var receipt = IncrementReceipt.Accepted(
                        context.IdempotencyKey,
                        current.Count + context.Value.Amount);
                    var receipts = new Dictionary<string, IncrementReceipt>(current.Receipts, StringComparer.Ordinal)
                    {
                        [context.IdempotencyKey] = receipt
                    };
                    await node.Database.PutAsync(new CultRecordKey(counterKey), new CounterState
                    {
                        CounterId = counterKey,
                        Count = receipt.Count,
                        Receipts = receipts
                    });
                    await node.FlushAsync();
                    return receipt;
                }
                finally
                {
                    operationGate.Release();
                }
            });

    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, arguments.Port));
    var app = builder.Build();
    app.UseWebSockets();
    app.MapCultNetWebSocket("/cultmesh", schemaServer, new CultNetWebSocketEndpointOptions
    {
        AuthorizeAsync = context => ValueTask.FromResult(
            context.Request.Cookies.TryGetValue("cultnet_session", out var token) &&
            string.Equals(token, arguments.Token, StringComparison.Ordinal))
    });
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    Console.WriteLine("PROVIDER_READY " + address.Replace("http://", "ws://", StringComparison.Ordinal) + "/cultmesh");
    await app.WaitForShutdownAsync();
    await app.DisposeAsync();
}

static async Task RunHeadlessAsync(Args arguments)
{
    if (string.IsNullOrWhiteSpace(arguments.OdinEndpoint))
        throw new ArgumentException("The headless client requires --odin.");
    CultNetWebSocketSchemaClient CreateClient() => new(options =>
        options.SetRequestHeader("Cookie", $"cultnet_session={arguments.Token}"));
    using var mesh = new CultMeshClient(new CultMeshClientOptions
    {
        RendezvousEndpoints = [arguments.OdinEndpoint],
        Discovery = new CultMeshVerseDiscoveryClientOptions
        {
            CreateClient = CreateClient,
            TransportVersion = arguments.TransportVersion
        },
        Connectors =
        [
            new CultMeshUriSchemaTransportConnector(
                "cultnet-websocket",
                ["ws", "wss"],
                _ => CreateClient())
        ]
    });
    var target = new CultMeshSessionTarget(arguments.VerseId, arguments.AuthorityRuntimeId);
    using var lease = await mesh.LeaseDocumentAsync<CounterState>(target, counterKey);
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var lastCount = -1;
    using var watch = lease.Handle.Watch(counter =>
    {
        if (counter.Count <= lastCount) return;
        lastCount = counter.Count;
        if (counter.Count == 0)
            Console.WriteLine("HEADLESS_READY " + JsonSerializer.Serialize(new { count = counter.Count }));
        else
            Console.WriteLine($"HEADLESS_UPDATE_{counter.Count} " + JsonSerializer.Serialize(new
            {
                count = counter.Count,
                receiptIds = counter.Receipts.Values
                    .Select(receipt => receipt.ReceiptId)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()
            }));
        if (counter.Count >= arguments.ExpectedCount) completion.TrySetResult();
    });
    var initial = await lease.Handle.LatestAsync();
    if (lastCount < 0)
    {
        lastCount = initial.Count;
        Console.WriteLine("HEADLESS_READY " + JsonSerializer.Serialize(new { count = initial.Count }));
    }
    if (initial.Count >= arguments.ExpectedCount) completion.TrySetResult();
    var commandTask = Task.Run(async () =>
    {
        var line = await Console.In.ReadLineAsync();
        if (line == null || !line.StartsWith("INVOKE ", StringComparison.Ordinal))
            throw new InvalidOperationException("The headless sample expected 'INVOKE <idempotency-key>' on stdin.");
        var commandId = line["INVOKE ".Length..].Trim();
        var result = await mesh.InvokeAsync<IncrementRequest, IncrementReceipt>(
            target,
            "sample.counter",
            "sample.counter.increment",
            "sample.increment.v1",
            "gamecult.eve.command_receipt.v1",
            new IncrementRequest { Amount = 1 },
            sourceRuntimeId: "sample.csharp",
            idempotencyKey: commandId);
        Console.WriteLine("HEADLESS_RECEIPT " + JsonSerializer.Serialize(new
        {
            receiptId = result.Value.ReceiptId,
            count = result.Value.Count,
            status = result.Status
        }));
        Console.WriteLine("HEADLESS_NETWORK_BENCHMARK " + JsonSerializer.Serialize(
            await MeasureOperationSessionAsync(mesh, target, arguments)));
    });
    await Task.WhenAll(completion.Task, commandTask).WaitAsync(TimeSpan.FromSeconds(45));
}

static async Task<object> MeasureOperationSessionAsync(
    CultMeshClient mesh,
    CultMeshSessionTarget target,
    Args arguments)
{
    const int warmupOperations = 100;
    if (arguments.BenchmarkOperations <= 0)
        throw new ArgumentOutOfRangeException(nameof(arguments.BenchmarkOperations));
    for (var index = 0; index < warmupOperations; index++)
        await PingAsync(mesh, target, "warmup:" + index, index);

    ForceCollection();
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    var managedHeapBefore = GC.GetTotalMemory(forceFullCollection: false);
    var privateBytesBefore = process.PrivateMemorySize64;
    var latencies = new double[arguments.BenchmarkOperations];
    var clock = Stopwatch.StartNew();
    for (var index = 0; index < arguments.BenchmarkOperations; index++)
    {
        var started = Stopwatch.GetTimestamp();
        await PingAsync(mesh, target, "benchmark:" + index, index);
        latencies[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
    clock.Stop();
    ForceCollection();
    process.Refresh();
    var managedHeapAfter = GC.GetTotalMemory(forceFullCollection: false);
    var privateBytesAfter = process.PrivateMemorySize64;
    Array.Sort(latencies);
    var p99 = latencies[Math.Clamp((int)Math.Ceiling(latencies.Length * 0.99) - 1, 0, latencies.Length - 1)];
    var managedHeapGrowth = managedHeapAfter - managedHeapBefore;
    if (managedHeapGrowth > 8 * 1024 * 1024)
        throw new InvalidOperationException($"Retained operation session grew the managed heap by {managedHeapGrowth} bytes.");
    if (p99 > 250)
        throw new InvalidOperationException($"Retained operation session p99 was {p99:F2} ms.");
    return new
    {
        operations = arguments.BenchmarkOperations,
        p99Milliseconds = p99,
        operationsPerSecond = arguments.BenchmarkOperations / clock.Elapsed.TotalSeconds,
        managedHeapBefore,
        managedHeapAfter,
        managedHeapGrowth,
        privateBytesBefore,
        privateBytesAfter,
        privateBytesGrowth = privateBytesAfter - privateBytesBefore
    };
}

static async Task PingAsync(
    CultMeshClient mesh,
    CultMeshSessionTarget target,
    string idempotencyKey,
    int sequence)
{
    var result = await mesh.InvokeAsync<PingRequest, PingReceipt>(
        target,
        "sample.counter",
        "sample.counter.ping",
        "sample.ping_request.v1",
        "sample.ping_receipt.v1",
        new PingRequest { Sequence = sequence },
        sourceRuntimeId: "sample.csharp",
        idempotencyKey: idempotencyKey);
    if (result.Status != "accepted" || result.Value.Sequence != sequence)
        throw new InvalidOperationException("The typed ping receipt did not match its request.");
}

static void ForceCollection()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}

static EveSurfaceDocument CreateCounterSurface()
{
    var networkRoute = new CultMeshRouteHint(CultMeshLocalityKind.Network, "cultmesh");
    var count = new CultMeshStateBindingDescriptor(
        "value",
        "sample.counter.count",
        "sample.counter_state:counter:main",
        "sample.counter_state.v1",
        networkRoute);
    var increment = CultMesh.OperationBinding(
        "sample.counter.increment",
        "Increment",
        "sample.increment.v1",
        networkRoute);
    return EveSurface.Create(surfaceKey)
        .Provider("sample.counter-ui", "sample.daemon")
        .Title("CultMesh browser counter")
        .Version(1)
        .UpdatedAtUtc("2026-08-17T00:00:00Z")
        .RootColumn("counter.root", root => root
            .Title("counter.title", "CultMesh browser counter")
            .Metric("counter.value", "Canonical count", "0", count)
            .Button("counter.increment", "Increment", increment))
        .Build();
}

sealed class Args
{
    public string Mode { get; private init; } = "";
    public int Port { get; private init; }
    public string StatePath { get; private init; } = "sample-counter.cc";
    public string OdinEndpoint { get; private init; } = "";
    public string ProviderEndpoint { get; private init; } = "";
    public string DecoyEndpoint { get; private init; } = "";
    public string Token { get; private init; } = "sample-session";
    public string VerseId { get; private init; } = "sample.counter";
    public string VerseName { get; private init; } = "CultMesh browser counter";
    public string AuthorityRuntimeId { get; private init; } = "sample.counter-daemon";
    public string DecoyAuthorityRuntimeId { get; private init; } = "sample.decoy-daemon";
    public string TransportVersion { get; private init; } = "cultmesh.v0";
    public string RulesHash { get; private init; } = "sample-counter-v1";
    public string RouteGeneration { get; private init; } = "sample-counter-route-1";
    public string DecoyRouteGeneration { get; private init; } = "sample-decoy-route-1";
    public int ExpectedCount { get; private init; } = 2;
    public int BenchmarkOperations { get; private init; } = 10_000;

    public static Args Parse(string[] values)
    {
        if (values.Length == 0) return new Args();
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index + 1 < values.Length; index += 2)
            named[values[index]] = values[index + 1];
        return new Args
        {
            Mode = values[0],
            Port = named.TryGetValue("--port", out var port) ? int.Parse(port) : 0,
            StatePath = named.GetValueOrDefault("--state", "sample-counter.cc"),
            OdinEndpoint = named.GetValueOrDefault("--odin", ""),
            ProviderEndpoint = named.GetValueOrDefault("--provider-endpoint", ""),
            DecoyEndpoint = named.GetValueOrDefault("--decoy-endpoint", ""),
            Token = named.GetValueOrDefault("--token", "sample-session"),
            VerseId = named.GetValueOrDefault("--verse-id", "sample.counter"),
            VerseName = named.GetValueOrDefault("--verse-name", "CultMesh browser counter"),
            AuthorityRuntimeId = named.GetValueOrDefault("--authority-runtime-id", "sample.counter-daemon"),
            DecoyAuthorityRuntimeId = named.GetValueOrDefault("--decoy-authority-runtime-id", "sample.decoy-daemon"),
            TransportVersion = named.GetValueOrDefault("--transport-version", "cultmesh.v0"),
            RulesHash = named.GetValueOrDefault("--rules-hash", "sample-counter-v1"),
            RouteGeneration = named.GetValueOrDefault("--route-generation", "sample-counter-route-1"),
            DecoyRouteGeneration = named.GetValueOrDefault("--decoy-route-generation", "sample-decoy-route-1"),
            ExpectedCount = named.TryGetValue("--expected-count", out var expectedCount)
                ? int.Parse(expectedCount)
                : 2,
            BenchmarkOperations = named.TryGetValue("--benchmark-operations", out var benchmarkOperations)
                ? int.Parse(benchmarkOperations)
                : 10_000
        };
    }
}

[CultDocument("sample.counter_state", "sample.counter_state.v1")]
[MessagePackObject]
public sealed class CounterState
{
    [Key("counterId")] public string CounterId { get; set; } = "";
    [Key("count")] public int Count { get; set; }
    [Key("receipts")] public Dictionary<string, IncrementReceipt> Receipts { get; set; } = new(StringComparer.Ordinal);
    public static CounterState Initial(string counterId) => new() { CounterId = counterId };
}

[MessagePackObject]
public sealed class IncrementRequest
{
    [Key("amount")] public int Amount { get; set; } = 1;
}

[MessagePackObject]
public sealed class IncrementReceipt
{
    [Key("receiptId")] public string ReceiptId { get; set; } = "";
    [Key("schema")] public string Schema { get; set; } = "gamecult.eve.command_receipt.v1";
    [Key("commandId")] public string CommandId { get; set; } = "";
    [Key("command")] public string Command { get; set; } = "sample.counter.increment";
    [Key("state")] public string State { get; set; } = "accepted";
    [Key("ownerRepo")] public string OwnerRepo { get; set; } = "CultLib";
    [Key("authority")] public string Authority { get; set; } = "provider-daemon";
    [Key("providerId")] public string ProviderId { get; set; } = "sample.counter-ui";
    [Key("surfaceId")] public string SurfaceId { get; set; } = "sample.counter";
    [Key("issuedAtUtc")] public string IssuedAtUtc { get; set; } = "";
    [Key("sourceVersion")] public int SourceVersion { get; set; }
    [Key("idempotencyKey")] public string IdempotencyKey { get; set; } = "";
    [Key("count")] public int Count { get; set; }

    public static IncrementReceipt Accepted(string id, int count) => new()
    {
        ReceiptId = $"receipt:{id}",
        CommandId = id,
        IssuedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        SourceVersion = count,
        IdempotencyKey = id,
        Count = count
    };
}

[MessagePackObject]
public sealed class PingRequest
{
    [Key("sequence")] public int Sequence { get; set; }
}

[MessagePackObject]
public sealed class PingReceipt
{
    [Key("sequence")] public int Sequence { get; set; }
}
