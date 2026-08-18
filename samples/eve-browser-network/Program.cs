using System.Net;
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
    catalog.Upsert(new CultMeshVerseDescriptor(
        arguments.VerseId,
        arguments.VerseName,
        CultMeshVerseAuthorityModel.OperatorCluster,
        new CultMeshVerseCompatibility(arguments.TransportVersion, arguments.RulesHash),
        discoveryEndpoints: [arguments.ProviderEndpoint],
        authorityRuntimeIds: [arguments.AuthorityRuntimeId],
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
    var registry = CultDocumentRegistry.ForTypes([typeof(CounterState), typeof(EveSurfaceDocument)]);
    using var cache = await CultCacheMessagePack.OpenAsync(arguments.StatePath, new CultCacheOpenOptions
    {
        Registry = registry,
        FlushOnDispose = true,
        StoreFlushOnDispose = true
    });
    var documents = new CultNetDocumentRegistry(registry)
        .Register(CultNetDocumentBinding.ForDocument<CounterState>(registry, "sample.counter_state.v1"))
        .Register(CultNetDocumentBinding.ForDocument<EveSurfaceDocument>(registry, EveSurfaceDocument.SchemaId));
    using var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
    {
        RuntimeId = "sample.counter-provider",
        DocumentRegistry = documents
    });
    if (cache.Get<CounterState>(new CultRecordKey(counterKey)) == null)
        await database.PutAsync(new CultRecordKey(counterKey), CounterState.Initial(counterKey));
    if (cache.Get<EveSurfaceDocument>(new CultRecordKey(surfaceKey)) == null)
        await database.PutAsync(new CultRecordKey(surfaceKey), CreateCounterSurface());
    await cache.FlushAsync();

    await using var schemaServer = new CultNetWebSocketSchemaServer();
    using var subscriptions = new CultNetDatabaseSubscriptionServer(schemaServer, database);
    var operationGate = new SemaphoreSlim(1, 1);
    schemaServer.OnCultNet<CultNetOperationRequestMessage>(async (request, peer) =>
    {
        if (request.ServiceId != "sample.counter" || request.Operation != "sample.counter.increment")
        {
            peer.SendCultNet(new CultNetErrorMessage { Error = "Unsupported sample operation." });
            return;
        }
        await operationGate.WaitAsync();
        try
        {
            var current = cache.Get<CounterState>(new CultRecordKey(counterKey))
                ?? throw new InvalidOperationException("Counter state is unavailable.");
            if (!current.Receipts.TryGetValue(request.MessageId, out var receipt))
            {
                var input = MessagePackSerializer.Deserialize<IncrementRequest>(
                    Convert.FromBase64String(request.Payload));
                receipt = IncrementReceipt.Accepted(request.MessageId, current.Count + input.Amount);
                var receipts = new Dictionary<string, IncrementReceipt>(current.Receipts, StringComparer.Ordinal)
                {
                    [request.MessageId] = receipt
                };
                await database.PutAsync(new CultRecordKey(counterKey), new CounterState
                {
                    CounterId = counterKey,
                    Count = receipt.Count,
                    Receipts = receipts
                });
                await cache.FlushAsync();
            }
            peer.SendCultNet(new CultNetOperationResponseMessage
            {
                MessageId = request.MessageId,
                ServiceId = request.ServiceId,
                Operation = request.Operation,
                Status = "accepted",
                PayloadSchema = "gamecult.eve.command_receipt.v1",
                Payload = Convert.ToBase64String(MessagePackSerializer.Serialize(receipt)),
                SourceRuntimeId = "sample.counter-provider"
            });
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
    using var lease = await mesh.LeaseDocumentAsync<CounterState>(arguments.VerseId, counterKey);
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
            arguments.VerseId,
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
    });
    await Task.WhenAll(completion.Task, commandTask).WaitAsync(TimeSpan.FromSeconds(45));
}

static EveSurfaceDocument CreateCounterSurface()
{
    var networkRoute = new CultMeshRouteHint(CultMeshLocalityKind.Network, "cultmesh");
    return new EveSurfaceDocument(
        providerId: "sample.counter-provider",
        providerKind: "sample.daemon",
        title: "CultMesh browser counter",
        version: 1,
        updatedAtUtc: "2026-08-17T00:00:00Z",
        surface: new EveSurfaceTree(
            surfaceKey,
            new EveSurfaceComponent(
                "counter.root",
                "column",
                new Dictionary<string, string>(StringComparer.Ordinal),
                [
                    new EveSurfaceComponent(
                        "counter.value",
                        "metric",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["label"] = "Canonical count",
                            ["value"] = "0"
                        },
                        [],
                        [
                            new CultMeshStateBindingDescriptor(
                                "value",
                                "sample.counter.count",
                                "sample.counter_state:counter:main",
                                "sample.counter_state.v1",
                                networkRoute)
                        ]),
                    new EveSurfaceComponent(
                        "counter.increment",
                        "control.button",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["label"] = "Increment",
                            ["command"] = "sample.counter.increment"
                        },
                        [])
                ]),
            []),
        commands:
        [
            new EveCommandTemplate(new CultMeshOperationBindingDescriptor(
                "sample.counter.increment",
                "Increment",
                routeHint: networkRoute))
        ]);
}

sealed class Args
{
    public string Mode { get; private init; } = "";
    public int Port { get; private init; }
    public string StatePath { get; private init; } = "sample-counter.cc";
    public string OdinEndpoint { get; private init; } = "";
    public string ProviderEndpoint { get; private init; } = "";
    public string Token { get; private init; } = "sample-session";
    public string VerseId { get; private init; } = "sample.counter";
    public string VerseName { get; private init; } = "CultMesh browser counter";
    public string AuthorityRuntimeId { get; private init; } = "sample.counter-provider";
    public string TransportVersion { get; private init; } = "cultmesh.v0";
    public string RulesHash { get; private init; } = "sample-counter-v1";
    public int ExpectedCount { get; private init; } = 2;

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
            Token = named.GetValueOrDefault("--token", "sample-session"),
            VerseId = named.GetValueOrDefault("--verse-id", "sample.counter"),
            VerseName = named.GetValueOrDefault("--verse-name", "CultMesh browser counter"),
            AuthorityRuntimeId = named.GetValueOrDefault("--authority-runtime-id", "sample.counter-provider"),
            TransportVersion = named.GetValueOrDefault("--transport-version", "cultmesh.v0"),
            RulesHash = named.GetValueOrDefault("--rules-hash", "sample-counter-v1"),
            ExpectedCount = named.TryGetValue("--expected-count", out var expectedCount)
                ? int.Parse(expectedCount)
                : 2
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
    [Key("providerId")] public string ProviderId { get; set; } = "sample.counter-provider";
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
