# 1. Persist The Counter

The tutorial builds one application all the way through: a provider-owned
counter, an Increment operation, one browser lowerer, and one C# headless
client. Start with the state the provider actually owns.

```csharp
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Mesh;
using GameCult.Networking;
using MessagePack;

[CultDocument("sample.counter_state", "sample.counter_state.v1")]
[MessagePackObject]
public sealed class CounterState
{
    [Key("counterId")] public string CounterId { get; set; } = "";
    [Key("count")] public int Count { get; set; }
}
```

Open a `.cc` node, register the document once, and write through its database
boundary:

```csharp
var key = new CultRecordKey("counter:main");
var documentTypes = new[] { typeof(CounterState) };
var registry = CultMesh.CreateCultCacheDocumentRegistry(documentTypes);
var documents = CultMesh.CreateCultNetDocumentRegistry(documentTypes, registry);
using var node = await CultMesh.CreateNodeAsync("counter.cc", new CultMeshNodeOptions
{
    StartServer = false,
    CacheOptions = new CultCacheOpenOptions { Registry = registry },
    DatabaseOptions = new CultNetDatabaseOptions
    {
        RuntimeId = "sample.counter-daemon",
        DocumentRegistry = documents
    }
});

await node.Database.PutAsync(key, new CounterState
{
    CounterId = key.Value,
    Count = 0
});
await node.FlushAsync();
```

CultCache owns durable local truth. CultNet owns the portable schema mapping.
CultMesh exposes the database and later carries subscriptions and operations.
The renderer owns none of them.

The executable provider adds its receipt index to this document so the counter
mutation and idempotency decision are committed together. See
[`samples/eve-browser-network/Program.cs`](../../../../samples/eve-browser-network/Program.cs)
for the exact running type and transaction.

Fast persistence check:

```powershell
dotnet test tests/GameCult.Networking.Tests/GameCult.Networking.Tests.csproj --filter FullyQualifiedName~CultMeshNode_CanRoundTrip_DurableTypedDocument_Through_PublicDatabaseSurface
```

This check proves the local typed state boundary. Chapter 4 boots the real
provider and proves the same state across Chromium and C# after restart.

Next: [connect a client by stable identity](02-connect-by-identity.md).
