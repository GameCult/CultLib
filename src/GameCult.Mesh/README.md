# CultMesh

CultMesh is the distributed realtime database and simulation-consensus layer for
GameCult projects. The NuGet package and assembly are named `GameCult.Mesh`;
the public surface is branded as CultMesh.

CultMesh sits over:

- `GameCult.Caching` for typed domain documents, local indexes, persistence, and
  file-compatible cache storage.
- `GameCult.Networking` for CultNet schema-v0 wire messages, native RUDP,
  transport adapters, and secure client/server sessions.

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

If you want one inspectable path that starts with a durable local cache file and
ends with a typed read/write through the public CultMesh surface, see
[docs/durable-node-quickstart.md](docs/durable-node-quickstart.md).
If you want the lower-level typed-to-raw handoff that CultNet moves across the
wire, see [docs/typed-document-path.md](docs/typed-document-path.md).

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

`CultMeshPeerCatalog.FindAuthorized(...)` and `FirstAuthorized(...)` compose
peer lookup with the lease catalog, so contact gossip stays separate from
trust. The branded RUDP helpers use the same boundary:

```csharp
using var rudpClient = CultMesh.ConnectRudpClientForAuthorizedPeer(
    "aetheria-client",
    connectionId: 0x10203040,
    peers,
    leases,
    "aetheria-main",
    CultMeshPeerRoles.ShardPrimary,
    shardId: "players-us-east");
```

`CreateRudpServer(...)`, `ParseRudpEndpoint(...)`,
`ConnectRudpClient(...)`, and `ConnectRudpClientForPeer(...)` delegate to the
CultNet RUDP socket transport and return the same schema-message-capable
transport after handshake. `CreateRudpClient(...)` and
`CreateRudpClientForPeer(...)` remain available when a caller intentionally owns
the handshake and polling loop. CultMesh owns peer/authority ergonomics;
CultNet owns packet reliability, resend pressure, and channel semantics.

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
var input = CultMesh.Document<PlayerInput>(db, inputKey, verse);

await input.SubmitPredictionAsync(inputState);

using var sub = input.Watch()
    .Subscribe(latest =>
    {
        ReconcilePrediction(latest);
    });
```

Read-only CultCache publications can be lifted into the same document handle
shape without local store plumbing:

```csharp
var health = CultMesh.DocumentFromSingleFile<DaemonHealth>(
    "daemon-publication.ccmp",
    new CultRecordKey("daemon:aetheria.health.v1"),
    verse);

using var healthSub = health.Watch().Subscribe(RenderHealth);
var latestHealth = await health.LatestAsync();
```

`DocumentFromStore<T>()` accepts any `CacheBackingStore`; both helpers pull,
decode, resolve persisted schema metadata, and poll for updates behind the
`CultMeshDocumentHandle<T>` surface.

## Game Session

`CultMeshGameSession` wires the common gameplay loop: local catalogs,
observation hub, observation server, peer exchange, Verse discovery, prediction,
and quorum fact commits.

```csharp
using var session = CultMesh.CreateGameSession(node, new CultMeshGameSessionOptions
{
    ConsensusOptions = new CultNetSimulationConsensusOptions
    {
        MinimumWitnesses = 2,
        QuorumRatio = 0.66d
    }
});

await session.PredictAsync(inputKey, inputState);

var committed = await session.SubmitAndCommitAsync(new CultNetSimulationObservation
{
    WitnessRuntimeId = "watcher-1",
    ShardId = "arena",
    ShardEpoch = 4,
    Frame = 100,
    SubjectId = "bob",
    ClaimKind = "hit",
    ClaimHash = CultNetSimulationObservation.ComputeClaimHash("hit", "alice", "bob", "frame:100")
});
```

## Managed Documents

CultMesh presents distributed state as the same managed POCO document shape as
CultCache. Local edits commit through shard authority; remote shard-log or
network events update the same watched value.

```csharp
using GameCult.Caching;
using GameCult.Mesh;

var player = node.Database.Document<PlayerTransform>(new CultRecordKey("player:alice"));

using var subscription = player.Watch().Subscribe(current => Render(current));

await player.ReplaceAsync(new PlayerTransform
{
    Name = "Alice",
    PositionX = 4,
    Health = 100
});

player.Value!.Health = 90;
await player.CommitAsync();
```

Remote publications use the same document handle shape:

```csharp
var health = CultMesh.DocumentFromPeerSnapshot<DaemonHealthDocument>(
    "cultnet://daemon.local:3075",
    "daemon:aetheria.health.v1",
    verse);

var latest = await health.LatestAsync();
```

The physical cache table remains SoA for CPU-local scans:

```csharp
Span<float> x = node.Cache.Soa<PlayerTransform>()
    .Column<float>(nameof(PlayerTransform.PositionX))
    .Span;
```

## Witness Consensus

CultMesh nodes can publish observations about simulation facts: shard epoch,
frame, subject, claim kind, and claim hash. `CultNetSimulationConsensus`
aggregates those observations deterministically into candidate facts with
support weight, total observed weight, confidence, and quorum status.

Those candidates are not committed world state by themselves. They are the fast
mesh opinion layer that can feed an authoritative shard commit path. Once a
candidate has quorum, a shard-authoritative node can commit it as a
`CultMeshSimulationFact`.

```csharp
var committer = CultMesh.CreateSimulationFactCommitter(node.Database);
await committer.CommitAsync(candidate);
```

The central server does not need to decide what every witness saw. It only
needs to accept the quorum result through the shard log.

## Durable Catch-Up

CultMesh can persist authoritative shard logs through
`ICultNetShardMutationLogStore`. The included file store writes one MessagePack
log per shard, so a restarted primary can still serve replica catch-up without
depending on warm process memory. When retained log history is compacted, stale
replicas get an explicit resync requirement instead of a partial log pretending
to be enough. The replicator can answer that by fetching a shard-bounded
snapshot, replacing the local shard view, and advancing its cursor to the
snapshot's represented log sequence.

## Streaming Mode

CultMesh streaming mode is the low-latency body path for Mimir, Fensalir, Eve,
and other realtime runtimes. CultCache and CultNet own typed state, discovery,
stream descriptors, negotiations, and frame handles. The frame bodies stay in
runtime-native storage: shared D3D12 textures, shared memory rings, platform GPU
handles, DMA buffers, or paged CultCache fallbacks when a zero-copy transport is
not available.

```csharp
using GameCult.Mesh;

var streams = CultMesh.CreateStreamCatalog();

streams.Declare(new CultMeshStreamDescriptor(
    "mimir:kiyo-pro:rgba",
    "mimir-live",
    "starfire",
    CultMeshStreamKind.Video,
    new CultMeshStreamClock("mimir:clock", "kiyo-pro", sampleRate: 90_000, confidence: 0.92),
    new[]
    {
        CultMeshStreamBodyTransport.SharedD3D12Texture,
        CultMeshStreamBodyTransport.SharedMemory,
        CultMeshStreamBodyTransport.CultCachePage
    },
    video: new CultMeshVideoStreamFormat(1920, 1080, "rgba8", framesPerSecond: 60),
    maxInFlightFrames: 4));

var fensalir = new CultMeshStreamConsumerProfile(
    "fensalir",
    "mimir-live",
    new[] { CultMeshStreamBodyTransport.SharedD3D12Texture },
    acceptedKinds: new[] { CultMeshStreamKind.Video },
    canImportGpuHandles: true);

var negotiation = streams.Negotiate("mimir:kiyo-pro:rgba", fensalir);
```

For CPU-visible data, `CultMeshSharedMemoryFrameRing` preallocates fixed slots
and hands producers writable leases. Consumers hold read leases so the producer
does not overwrite a visible frame. `TryPublishCopy` exists for unavoidable
fallbacks and increments copy telemetry; the normal hot path writes directly
into a lease and commits a `CultMeshStreamFrameHandle`.

```csharp
streams.Declare(new CultMeshStreamDescriptor(
    "mimir:leap:depth",
    "mimir-live",
    "starfire",
    CultMeshStreamKind.Tensor,
    new CultMeshStreamClock("mimir:clock", "leap"),
    new[] { CultMeshStreamBodyTransport.SharedMemory }));

using var ring = streams.CreateSharedMemoryRing("mimir:leap:depth", slotCount: 4, slotByteLength: 640 * 480 * 2);

if (ring.TryAcquireWriteSlot(out var write))
{
    FillDepthFrame(write.Span);
    var frame = ring.CommitWriteSlot(write, timestampNs: GetVerseTimeNs(), byteLength: 640 * 480 * 2);
    streams.PublishFrame(frame);
}
```

The invariant is simple: typed mesh state describes and synchronizes streams;
hot frame bodies remain in the cheapest storage both endpoints can share.

## Documentation

- [Public API](docs/public-api.md)
- [Durable Node Quickstart](docs/durable-node-quickstart.md)
- [Typed Document Path](docs/typed-document-path.md)
- [Research Notes](docs/research.md)
- [Verse Model](docs/verses.md)
