# CultMesh

CultMesh is the distributed realtime database and simulation-consensus layer for
GameCult projects. The NuGet package and assembly are named `GameCult.Mesh`;
the public surface is branded as CultMesh.

CultMesh sits over:

- `GameCult.Caching` for typed domain documents, local indexes, persistence, and
  file-compatible cache storage.
- `GameCult.Networking` for LiteNetLib transport, schema-v0 wire messages, and
  secure client/server sessions.

The intent is direct: a game can treat a mesh of clients and servers as one
reactive database for persistent state, prediction-friendly input state, and
simulation observations.

## First Node

```csharp
using GameCult.Mesh;

using var node = await CultMesh.StartNodeAsync("world.ccmp");

var player = await node.Database.GetAsync<PlayerData>(playerKey);
await node.Database.PutAsync(playerKey, player);
```

Enable durable authoritative shard logs when a node should serve replica
catch-up after restart:

```csharp
using var node = await CultMesh.StartNodeAsync("world.ccmp", new CultMeshNodeOptions
{
    EnableDurableShardLogs = true
});
```

If `ShardLogPath` is omitted, CultMesh stores logs beside the cache under
`world.cultmesh/shard-logs`.

## Verses

A Verse is a rule-bearing consensus graph. Aetheria Main can be a
cloud-authoritative Verse backed by GameCult regional simulators. A modded
community branch can be transport-compatible with Aetheria Main while declaring
different rules, required plugins, and authority policy. A peer-hosted game can
choose a peer-to-peer authority model.

```csharp
var rulesHash = CultMeshVerseDescriptor.ComputeRulesHash(
    "aetheria",
    "rules:v1",
    "plugins:vanilla");

var aetheria = new CultMeshVerseDescriptor(
    "aetheria-main",
    "Aetheria",
    CultMeshVerseAuthorityModel.OperatorCluster,
    new CultMeshVerseCompatibility("cultmesh.v0", rulesHash),
    discoveryEndpoints: ["cultmesh://us-east.aetheria.gamecult.net:3075"]);

using var catalog = CultMesh.CreateVerseCatalog();
catalog.Upsert(aetheria);

using var discovery = CultMesh.ServeVerseCatalog(node, catalog);
```

Peers can request Verse catalogs over schema-v0 with
`cultmesh.verse_catalog_request.v0` and receive transport-filtered
`cultmesh.verse_catalog_response.v0` descriptors.

## Peer Exchange

Verse discovery tells a node which consensus graph exists. Peer exchange tells
it who else is already in that graph.

```csharp
using var peers = CultMesh.CreatePeerCatalog();
peers.Upsert(new CultMeshPeerCard(
    "gc-us-east-1",
    "aetheria-main",
    ["cultnet://us-east.aetheria.gamecult.net:3075"],
    roles: [CultMeshPeerRoles.Discovery, CultMeshPeerRoles.ShardPrimary],
    shardIds: ["players-us-east"],
    authorityLeaseId: "lease:players-us-east:42"));

using var exchange = CultMesh.ServePeerExchange(node, peers);
```

Peer cards are contact candidates, not authority by themselves. Public Verses
should validate authority leases or signatures before trusting a peer for
committed state.

```csharp
var leases = CultMesh.CreateAuthorityLeaseCatalog();
leases.Upsert(new CultMeshAuthorityLease(
    "lease:players-us-east:42",
    "aetheria-main",
    "gc-us-east-1",
    [CultMeshPeerRoles.ShardPrimary],
    ["players-us-east"],
    "gamecult-operator",
    DateTimeOffset.UtcNow.AddMinutes(-1),
    DateTimeOffset.UtcNow.AddMinutes(10),
    signature: "operator-signature"));
```

Clients can fetch more peers from known peers:

```csharp
var exchangeClient = CultMesh.CreatePeerExchangeClient();
await exchangeClient.DiscoverAsync(
    peers,
    aetheria.DiscoveryEndpoints,
    "aetheria-main",
    roles: [CultMeshPeerRoles.ReadReplica]);
```

## Client-Side Prediction

CultMesh lets a runtime declare which input documents it may author
optimistically. The local cache updates immediately, game simulation can proceed
against that predicted state, and the authoritative shard log later reconciles
the record.

```csharp
var db = node.Database;

await db.PutPredictedAsync(inputKey, inputState);

using var sub = db.WatchRecord<PlayerInput>(inputKey)
    .Subscribe(change =>
    {
        ReconcilePrediction(change);
    });
```

## Witness Consensus

CultMesh nodes can publish observations about simulation facts: shard epoch,
frame, subject, claim kind, and claim hash. `CultNetSimulationConsensus`
aggregates those observations deterministically into candidate facts with
support weight, total observed weight, confidence, and quorum status.

Those candidates are not committed world state by themselves. They are the fast
mesh opinion layer that can feed an authoritative shard commit path.

## Durable Catch-Up

CultMesh can persist authoritative shard logs through
`ICultNetShardMutationLogStore`. The included file store writes one MessagePack
log per shard, so a restarted primary can still serve replica catch-up without
depending on warm process memory. When retained log history is compacted, stale
replicas get an explicit resync requirement instead of a partial log pretending
to be enough. The replicator can answer that by fetching a shard-bounded
snapshot, replacing the local shard view, and advancing its cursor to the
snapshot's represented log sequence.

## Documentation

- [Public API](docs/public-api.md)
- [Research Notes](docs/research.md)
- [Verse Model](docs/verses.md)
