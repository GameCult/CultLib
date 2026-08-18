using System.Net;
using System.Text.Json;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
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
    var registry = CultDocumentRegistry.ForTypes([typeof(CounterState), typeof(CounterSurfaceDocument)]);
    using var cache = await CultCacheMessagePack.OpenAsync(arguments.StatePath, new CultCacheOpenOptions
    {
        Registry = registry,
        FlushOnDispose = true,
        StoreFlushOnDispose = true
    });
    var documents = new CultNetDocumentRegistry(registry)
        .Register(CultNetDocumentBinding.ForDocument<CounterState>(registry, "sample.counter_state.v1"))
        .Register(CultNetDocumentBinding.ForDocument<CounterSurfaceDocument>(registry, "gamecult.eve.surface.v1"));
    using var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
    {
        RuntimeId = "sample.counter-provider",
        DocumentRegistry = documents
    });
    if (cache.Get<CounterState>(new CultRecordKey(counterKey)) == null)
        await database.PutAsync(new CultRecordKey(counterKey), CounterState.Initial(counterKey));
    if (cache.Get<CounterSurfaceDocument>(new CultRecordKey(surfaceKey)) == null)
        await database.PutAsync(new CultRecordKey(surfaceKey), CounterSurfaceDocument.Create());
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
    var registry = CultDocumentRegistry.ForTypes([typeof(CounterState)]);
    using var cache = new CultCache(registry);
    var documents = new CultNetDocumentRegistry(registry)
        .Register(CultNetDocumentBinding.ForDocument<CounterState>(registry, "sample.counter_state.v1"));
    using var transport = new CultNetWebSocketSchemaClient(options =>
        options.SetRequestHeader("Cookie", $"cultnet_session={arguments.Token}"));
    using var subscriptions = new CultNetDatabaseSubscriptionClient(transport, cache, documents);
    var changed = new TaskCompletionSource<CounterState>(TaskCreationOptions.RunContinuationsAsynchronously);
    subscriptions.Changed += change =>
    {
        if (change.Document is CounterState counter) changed.TrySetResult(counter);
    };
    await transport.ConnectAsync(new Uri(arguments.Endpoint));
    var initial = await subscriptions.SubscribeAsync(
        "headless-counter",
        recordKeys: [counterKey],
        schemaIds: ["sample.counter_state.v1"],
        deliveryMode: CultNetDatabaseSubscriptionDeliveryMode.Live);
    var counter = initial.OfType<CounterState>().Single();
    Console.WriteLine("HEADLESS_READY " + JsonSerializer.Serialize(new { count = counter.Count }));
    var update = await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    Console.WriteLine("HEADLESS_UPDATE " + JsonSerializer.Serialize(new { count = update.Count }));
}

sealed class Args
{
    public string Mode { get; private init; } = "";
    public int Port { get; private init; }
    public string StatePath { get; private init; } = "sample-counter.cc";
    public string Endpoint { get; private init; } = "";
    public string ProviderEndpoint { get; private init; } = "";
    public string Token { get; private init; } = "sample-session";
    public string VerseId { get; private init; } = "sample.counter";
    public string VerseName { get; private init; } = "CultMesh browser counter";
    public string AuthorityRuntimeId { get; private init; } = "sample.counter-provider";
    public string TransportVersion { get; private init; } = "cultmesh.v1";
    public string RulesHash { get; private init; } = "sample-counter-v1";

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
            Endpoint = named.GetValueOrDefault("--endpoint", ""),
            ProviderEndpoint = named.GetValueOrDefault("--provider-endpoint", ""),
            Token = named.GetValueOrDefault("--token", "sample-session"),
            VerseId = named.GetValueOrDefault("--verse-id", "sample.counter"),
            VerseName = named.GetValueOrDefault("--verse-name", "CultMesh browser counter"),
            AuthorityRuntimeId = named.GetValueOrDefault("--authority-runtime-id", "sample.counter-provider"),
            TransportVersion = named.GetValueOrDefault("--transport-version", "cultmesh.v1"),
            RulesHash = named.GetValueOrDefault("--rules-hash", "sample-counter-v1")
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

[CultDocument("gamecult.eve.surface", "gamecult.eve.surface.v1")]
[MessagePackObject]
public sealed class CounterSurfaceDocument
{
    [Key("type")] public string Type { get; set; } = "surface-state";
    [Key("schema")] public string Schema { get; set; } = "gamecult.eve.surface.v1";
    [Key("providerId")] public string ProviderId { get; set; } = "sample.counter-provider";
    [Key("providerKind")] public string ProviderKind { get; set; } = "sample.daemon";
    [Key("title")] public string Title { get; set; } = "CultMesh browser counter";
    [Key("version")] public int Version { get; set; } = 1;
    [Key("updatedAtUtc")] public string UpdatedAtUtc { get; set; } = "2026-08-17T00:00:00Z";
    [Key("surface")] public EveSurface Surface { get; set; } = new();
    [Key("commands")] public EveCommand[] Commands { get; set; } = [];

    public static CounterSurfaceDocument Create() => new()
    {
        Surface = new EveSurface
        {
            Id = "sample.counter",
            Title = "Counter",
            Root = new EveComponent
            {
                Id = "counter.root",
                Kind = "column",
                Children =
                [
                    new EveComponent
                    {
                        Id = "counter.value",
                        Kind = "metric",
                        Props = new Dictionary<string, object> { ["label"] = "Canonical count", ["value"] = 0 },
                        StateBindings =
                        [
                            new EveStateBinding
                            {
                                TargetProp = "value",
                                PointerId = "sample.counter.count",
                                SourceId = "sample.counter_state:counter:main",
                                SchemaId = "sample.counter_state.v1",
                                RouteKind = "cultmesh"
                            }
                        ]
                    },
                    new EveComponent
                    {
                        Id = "counter.increment",
                        Kind = "control.button",
                        Props = new Dictionary<string, object>
                        {
                            ["label"] = "Increment",
                            ["command"] = "sample.counter.increment"
                        }
                    }
                ]
            }
        },
        Commands =
        [
            new EveCommand
            {
                Command = "sample.counter.increment",
                Label = "Increment",
                SurfaceId = "sample.counter"
            }
        ]
    };
}

[MessagePackObject]
public sealed class EveSurface
{
    [Key("id")] public string Id { get; set; } = "";
    [Key("title")] public string Title { get; set; } = "";
    [Key("root")] public EveComponent Root { get; set; } = new();
    [Key("styles")] public Dictionary<string, object> Styles { get; set; } = [];
}

[MessagePackObject]
public sealed class EveComponent
{
    [Key("id")] public string Id { get; set; } = "";
    [Key("kind")] public string Kind { get; set; } = "";
    [Key("props")] public Dictionary<string, object> Props { get; set; } = [];
    [Key("children")] public EveComponent[] Children { get; set; } = [];
    [Key("stateBindings")] public EveStateBinding[] StateBindings { get; set; } = [];
}

[MessagePackObject]
public sealed class EveStateBinding
{
    [Key("targetProp")] public string TargetProp { get; set; } = "";
    [Key("pointerId")] public string PointerId { get; set; } = "";
    [Key("sourceId")] public string SourceId { get; set; } = "";
    [Key("schemaId")] public string SchemaId { get; set; } = "";
    [Key("routeKind")] public string RouteKind { get; set; } = "";
}

[MessagePackObject]
public sealed class EveCommand
{
    [Key("schema")] public string Schema { get; set; } = "gamecult.eve.command.v1";
    [Key("command")] public string Command { get; set; } = "";
    [Key("label")] public string Label { get; set; } = "";
    [Key("surfaceId")] public string SurfaceId { get; set; } = "";
    [Key("transport")] public string Transport { get; set; } = "cultmesh";
    [Key("authority")] public string Authority { get; set; } = "provider-daemon";
    [Key("result")] public string Result { get; set; } = "gamecult.eve.command_receipt.v1";
}
