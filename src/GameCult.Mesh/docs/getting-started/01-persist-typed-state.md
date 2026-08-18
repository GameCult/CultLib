# 1. Persist The Counter

The tutorial builds one application all the way through: a provider-owned
counter, an Increment operation, one browser lowerer, and one C# headless
client. Start with the state the provider actually owns.

```csharp
using GameCult.Caching;
using GameCult.Caching.MessagePack;
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

Open a `.cc` store, register the document once, and write through the CultMesh
database boundary:

```csharp
var key = new CultRecordKey("counter:main");
var registry = CultDocumentRegistry.ForTypes([typeof(CounterState)]);

using var cache = await CultCacheMessagePack.OpenAsync("counter.cc", new CultCacheOpenOptions
{
    Registry = registry,
    FlushOnDispose = true,
    StoreFlushOnDispose = true
});
var documents = new CultNetDocumentRegistry(registry)
    .Register(CultNetDocumentBinding.ForDocument<CounterState>(
        registry,
        "sample.counter_state.v1"));
using var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
{
    RuntimeId = "sample.counter-provider",
    DocumentRegistry = documents
});

await database.PutAsync(key, new CounterState
{
    CounterId = key.Value,
    Count = 0
});
await cache.FlushAsync();
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
