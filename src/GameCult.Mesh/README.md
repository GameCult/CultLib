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
to be enough.

## Documentation

- [Public API](docs/public-api.md)
- [Research Notes](docs/research.md)
- [Verse Model](docs/verses.md)
