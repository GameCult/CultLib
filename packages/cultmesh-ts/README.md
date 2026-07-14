# CultMesh TS

`cultmesh-ts` is the TypeScript projection of CultMesh contracts for local
tools and browser-adjacent runtimes. The C# `GameCult.Mesh` package owns the
native CultMesh stream implementation used by Mimir and Fensalir; this package
mirrors the typed declaration and negotiation shapes without owning the hot
frame body path.

## Local Catalogs

The TypeScript projection keeps the same local catalog ergonomics as the other
runtimes: sorted views, direct lookup, and unsubscribe-able watches.

## Shared Primitives

CultMesh primitives are shared cross-runtime developer affordances, not just
DTOs. They bundle the typed surface, metadata, routing hints, diagnostics, and
fast-path hooks that let application code feel like it is talking to native
state while CultMesh chooses the right local, shared-memory, IPC, network, or
WASM route. Runtime code can ask for a whole XY rect, then adapt it to the
current wire schema at the edge:

```ts
import { CultMesh } from "cultmesh-ts";

const viewport = CultMesh.rectFromBounds(-500, -300, 500, 300);
const request = CultMesh.viewportRequest(viewport, [3, 8]);
```

The request currently projects to the compact `{ minX, minY, maxX, maxY }`
shape used by Aetheria viewport queries, while the authored code keeps the
designer-facing concept as a `CultMeshRect`.

Typed operations follow the same rule: client code holds a method-shaped handle
and a runtime context, while CultMesh hides the route and authority metadata
from call sites that do not need to inspect it.

```ts
const move = CultMesh.operation("game.pilot.move.v1", async (request, context) => {
  return submitToBestRoute(request, context);
});

await move.invoke({ direction: CultMesh.vec2(1, 0) }, CultMesh.operationContext("rts-client", {
  routeHint: CultMesh.routeHint("network", "starfire Verse"),
}));
```

Typed query surfaces can be read once or watched through the same handle. A
projection recipe keeps its source metadata, route preference, and reactive
watcher when it becomes a query surface:

```ts
const objects = CultMesh.projectionRecipe(
  "aetheria.zone.objects.visible",
  [CultMesh.projectionSource("daemon:aetheria.frame.latest.v1")],
  projectObjects,
  {
    routeHint: CultMesh.routeHint("shared-memory", "co-located frame slab"),
    watchProjection: watchObjects,
  },
).asQuerySurface();

const stop = objects.watch(
  { viewport: CultMesh.viewportRequest(CultMesh.rectFromBounds(-10, -10, 10, 10)) },
  "browser-starfire",
  (next) => render(next),
);
```

Typed document handles use the same shape for local cache reads, network
snapshots, replacement, and client prediction. Authority-specific code plugs in
the submitter once; gameplay callers keep using the document:

```ts
const input = CultMesh.document(
  "input:pilot-a:thermal",
  pilotInputDocument,
  async (context) => readInput(context),
  {
    routeHint: CultMesh.routeHint("network", "Starbridge Verse"),
    watchDocument: watchInput,
    submitPrediction: async (context, value) => submitPredictedInput(context, value),
  },
);

await input.submitPrediction("pilot-a", predictedInput);
const stop = input.watch("pilot-a", latest => reconcileInput(latest));
```

Durable nodes resolve same-schema aliases at the boundary. Register the
canonical document that owns storage and replication, then ask the node for the
typed view the current runtime wants:

```ts
const station = await CultMesh.startNode(statePath, {
  documents: [stationStockDocument],
});

const stock = station.document(stationStockUiDocument, "station:starbridge:stock");
const current = await stock.latest();

await stock.set(updatedStockFromUi);

const reactive = station.reactiveDocument(stationStockUiDocument, "station:starbridge:stock");
await reactive.ready;
reactive.current.availableMissiles -= 4;
```

State pointers are the same kind of managed surface for UI and tools. They can
advertise source documents, inherit a Verse route when bound, and resolve
through that Verse without caller-side context plumbing:

```ts
const framePointer = CultMesh.statePointer(
  "daemon:aetheria.frame.latest.v1",
  async (context) => readFrameViaBestRoute(context),
  undefined,
  {
    sources: [CultMesh.projectionSource("daemon:aetheria.frame.latest.v1")],
    routeHint: CultMesh.routeHint("shared-memory", "co-located daemon frame"),
  },
);

const frame = await CultMesh
  .bindStatePointer(verse.withRoute("ipc", "Bifrost tool bridge"), framePointer)
  .resolve();

const stateBinding = CultMesh.stateBinding("value", framePointer);
const stateBindingRecord = CultMesh.stateBindingRecord(stateBinding);
const restoredStateBinding = CultMesh.stateBindingFromRecord(stateBindingRecord);
```

Eve/CultUI lowerers that still receive authored state refs should use a named
state-ref resolver instead of local string utilities. Resolvers compose, carry
source and route metadata, and can be described by tools:

```ts
const daemonRefs = CultMesh.stateRefResolver(
  "aetheria.daemon.refs",
  (stateRef, context) => resolveDaemonStateRef(stateRef, context),
  {
    sources: [CultMesh.projectionSource("daemon:aetheria.frame.latest.v1")],
    routeHint: CultMesh.routeHint("shared-memory", "co-located daemon state"),
  },
);

const catalogRefs = CultMesh.stateRefResolver(
  "aetheria.item_stats.refs",
  (stateRef) => resolveItemStatRef(stateRef),
);

const resolver = daemonRefs.or(catalogRefs);
const value = resolver.resolve("aetheria:daemon:item-stat:laser.output");
const diagnostic = CultMesh.describeStateRefResolver(resolver);
```

Operation surfaces should preserve typed CultMesh identity all the way through
UI/runtime boundaries. Use invocation records and payload records at persistence
edges instead of local route parsers or string-map conventions:

```ts
const binding = CultMesh.operationBinding("gamecult.aetheria.pilot.set_target.v1", {
  schemaId: "gamecult.aetheria.pilot.set_target.request.v1",
  routeHint: CultMesh.routeHint("network", "co-op Verse"),
});
const bindingRecord = CultMesh.operationBindingRecord(binding);
const restoredBinding = CultMesh.operationBindingFromRecord(bindingRecord);

const invocation = CultMesh.operationInvocation(binding, {
  idempotencyKey: "target:starfire:42",
});
const invocationRecord = CultMesh.operationInvocationRecord(invocation);
const payloadRecord = CultMesh.operationPayload({ targetEntityKey: "bandit-7" }).toRecord();
const restored = CultMesh.operationInvocationFromRecord(invocationRecord);
```

Route fields use the same shared primitive:

```ts
const routeRecord = CultMesh.routeRecord(CultMesh.routeHint("shared-memory", "co-located slab"));
const route = CultMesh.routeFromRecord(routeRecord);
```

When a client needs one coherent live view composed from several query surfaces,
use a live feed instead of putting a refresh loop in the UI:

```ts
const viewportFeed = CultMesh.liveFeed(
  "aetheria.rts.viewport.feed",
  snapshotViewport,
  {
    sources: objects.sources,
    routeHint: objects.routeHint,
    watchFeed: CultMesh.pollingQueryWatcher(snapshotViewport, { intervalMs: 50 }),
  },
);

const stopFeed = viewportFeed.watch(request, "browser-starfire", render);
```

```ts
import { CultMesh } from "cultmesh-ts";

const verses = CultMesh.createVerseCatalog<{ verseId: string; label: string }>();
const stopWatching = verses.watch((verse) => {
  console.log("updated", verse.verseId);
});

verses.upsert("public", { verseId: "public", label: "Public Verse" });
stopWatching();

const peers = CultMesh.createPeerCatalog();
peers.upsert({
  peerId: "ts-peer",
  verseId: "public",
  endpoints: ["rudp://127.0.0.1:4100"],
  roles: ["read-replica"],
  authorityLeaseId: "lease:ts-peer",
});

const readReplicas = peers.find("public", "read-replica");

const leases = CultMesh.createAuthorityLeaseCatalog();
const stopWatchingLeases = leases.watch((lease) => {
  console.log("lease changed", lease.leaseId);
});

leases.upsert({
  leaseId: "lease:ts-peer",
  verseId: "public",
  peerId: "ts-peer",
  roles: ["read-replica"],
  validFrom: new Date(Date.now() - 1000),
  expiresAt: new Date(Date.now() + 60_000),
});
const authorizedReplicas = peers.findAuthorized(
  "public",
  "read-replica",
  leases,
);
stopWatchingLeases();

const schemas = CultMesh.createBuiltInSchemaCatalog();
const shardCatalogRequest = schemas.get("cultnet.shard_catalog_request.v0");

const shards = CultMesh.createShardCatalog();
shards.upsert({
  shardId: "notes-a",
  ownerRuntimeId: "ts-peer",
  epoch: 1,
  schemaIds: ["cultmesh.note.v0"],
  keyPrefix: "note:",
});

const noteShards = shards.list({
  schemaIds: ["cultmesh.note.v0"],
  recordKeys: ["note:intro"],
});
```

Schema and shard catalog factories delegate to `cultnet-ts`; the branded
`CultMesh` entrypoint does not create a second owner for schema discovery or
topology truth.

## Long-Lived Providers

TypeScript provider daemons use `CultMeshProviderSession` to retain one logical
provider lifecycle across physical reconnects. The session owns registration,
lease renewal, desired typed publication replay, command dispatch, receipt
publication, and withdrawal. Provider code owns domain state and idempotent
command transactions; the injected transport owns CultNet wire details.

```ts
import {
  CultMeshProviderRudpTransport,
  CultMeshProviderSession,
} from "cultmesh-ts";

const odinTransport = new CultMeshProviderRudpTransport({
  endpoint: "rudp://127.0.0.1:17871",
  runtimeId: "voidbot-worker-7",
  connectionId: 0x43554c54,
});

const session = new CultMeshProviderSession({
  identity: {
    providerId: "voidbot.swarm",
    serviceInstanceId: "voidbot-worker-7",
    endpointId: "odin:voidbot-worker-7",
    verseId: "voidbot.local",
  },
  transport: odinTransport,
  receiptStore: durableReceiptStore,
  publications: [advertisementPublication, surfacePublication],
  commandHandlers: {
    "swarm.set_heat": applyHeatCommandTransaction,
  },
});

await session.start();
await session.upsertPublication(nextSurfacePublication);
```

Provider, service-instance, endpoint, and body-producer identities are separate
contracts. The provider session never infers one from another. A durable
receipt store is required; `CultMeshMemoryProviderReceiptStore` is intended
only for tests and disposable tools. The store is a durable outbox: a receipt
is persisted before transport publication, remains pending across reconnects,
and is marked published only after the current connection accepts it. Receipt
store failure degrades command intake without inventing a network outage.

The RUDP provider transport carries lifecycle payloads inside the existing
`cultnet.operation_request.v0` and `cultnet.operation_response.v0` envelopes.
RUDP acknowledgements prove byte delivery only. Registration, renewal,
publication changes, receipts, and withdrawal complete only after the
provider-session broker returns a correlated application response. The broker
owns lease fencing and accepted publication membership; the provider owns its
desired documents and durable receipt outbox.

`CultMeshProviderRudpTransport` is currently a private-development transport.
Do not advertise it as a public provider boundary until the surrounding
CultNet session authenticates a claim authorizing the four-part provider
identity. Source address and successful contact are not authority.

## RUDP Helpers

Node runtimes can build the shared CultNet reliable-UDP transport from the
branded CultMesh entrypoint while the transport semantics stay owned by
`cultnet-ts`:

```ts
const server = await CultMesh.createRudpServer("ts-server", 0x1020_3040);
const endpoint = CultMesh.parseRudpEndpoint(
  `rudp://127.0.0.1:${server.profile.transports[0]?.port}`,
);

const peer = {
  peerId: "ts-server",
  verseId: "local",
  endpoints: [endpoint.uri],
  roles: ["schema"],
  authorityLeaseId: "lease:ts-server",
};

const peers = CultMesh.createPeerCatalog();
const leases = CultMesh.createAuthorityLeaseCatalog();
peers.upsert(peer);
leases.upsert({
  leaseId: "lease:ts-server",
  verseId: "local",
  peerId: "ts-server",
  roles: ["schema"],
  validFrom: new Date(Date.now() - 1_000),
  expiresAt: new Date(Date.now() + 60_000),
});

const client = await CultMesh.createRudpPeerForAuthorizedPeer(
  "ts-client",
  0x1020_3040,
  peers,
  leases,
  "local",
  "schema",
);
const schemas = CultMesh.createSchemaCatalog();
await client.syncSchemaCatalog(schemas, { kinds: ["document_payload"] });

const health = CultMesh.documentFromPeerSnapshot(
  () => client,
  daemonHealthDocument,
  "daemon:aetheria.health.v1",
  {
    routeHint: CultMesh.routeHint("network", endpoint.uri),
    timeoutMs: 5_000,
  },
);

const latestHealth = await health.latest();

const syncedHealth = await station.syncDocumentFromPeerSnapshot(
  () => client,
  daemonHealthUiDocument,
  "daemon:aetheria.health.v1",
  {
    timeoutMs: 5_000,
  },
);
const localHealth = await station
  .document(daemonHealthUiDocument, "daemon:aetheria.health.v1")
  .latest();
```

`CultMesh.createRudpPeerForPeer(...)` remains available for already trusted
call sites, and `CultMesh.createRudpClientForPeer(...)` still returns the lower
level transport when a caller really needs to own handshaking or raw frames.
Discovery-first schema/catalog paths should prefer
`createRudpPeerForAuthorizedPeer(...)`, which composes
`CultMeshPeerCatalog.firstAuthorized(...)` with the authority lease catalog
before using the peer-card endpoint as a dial target.

`CultMesh.documentFromPeerSnapshot(...)` is the document-level path for RUDP
snapshots. It hides the snapshot request message id, response listener, raw
payload binary normalization, and MessagePack decode behind the same
`CultMeshDocumentHandle` shape as local documents. The helper prefers an exact
schema id match and falls back to the requested record key, so runtimes that
alias the same logical document through different generated schema names can
still read the publication with one CultMesh call. Pass a CultCache document
definition instead of a raw schema id when you want a typed handle whose
`latest()` result is parsed through that definition.

Use `node.syncDocumentFromPeerSnapshot(...)` or
`CultMesh.syncDocumentFromPeerSnapshot(...)` when a runtime should hydrate its
local node from the remote snapshot. The helper requests the raw snapshot,
applies it through the node's document registry, and returns the requested
typed definition. Same-schema aliases stay local after the call: the caller can
keep using `node.document(aliasDefinition, key)` or `node.reactiveDocument(...)`
without re-threading the RUDP peer.

The same typed-definition overload is available for
`CultMesh.documentFromStore(...)` and `CultMesh.documentFromSingleFile(...)`, so
local schema publications can be read as typed reactive documents without
manually pulling records, decoding MessagePack, or parsing the payload.
When the caller wants source selection to be configuration, use
`CultMesh.documentFromPublication(...)` with a `single-file`, `store`, or
`peer-snapshot` source; it returns the same typed handle while hiding whether
sync came from local persistence or a remote CultNet snapshot.
Use `node.syncDocumentFromPublication(...)` or
`CultMesh.syncDocumentFromPublication(...)` for the same source-agnostic shape
when the publication should hydrate a local node before the caller continues
through `node.document(...)` or `node.reactiveDocument(...)`.

## Streaming Mode

CultMesh streaming mode is for audio, video, tensor, and opaque byte frames that
need to move between diverse runtimes with as few copies as the platform allows.
It is not document replication with a bigger packet.

The split is deliberate:

- CultMesh owns stream identity, authority, clock metadata, negotiated body
  transport, frame cursors, and health/backpressure state.
- The body lane owns frame bytes or native handles: shared memory, D3D11/D3D12
  textures, DMA-BUF, IOSurface, AHardwareBuffer, CultCache page refs, or inline
  bytes as a last resort.
- Producers report `unavoidableCopyCount`; zero-copy is a target and a measured
  property, not a vibe in a lab coat.

The mirrored API shape is `CultMeshStreamCatalog`:

```ts
import { CultMesh } from "cultmesh-ts";

const streams = CultMesh.createStreamCatalog();
const stopWatchingStreams = streams.watch((stream) => {
  console.log("stream declared", stream.streamId);
});
const stopWatchingFrames = streams.watchFrames((frame) => {
  console.log("latest frame", frame.streamId, frame.sequence);
});

streams.declare({
  streamId: "mimir:kiyo-pro",
  verseId: "studio",
  ownerPeerId: "starfire",
  kind: "video",
  clock: { clockDomainId: "starfire-qpc" },
  video: {
    width: 1920,
    height: 1080,
    pixelFormat: "YUY2",
    framesPerSecond: 30,
  },
  preferredTransports: [
    "shared-d3d12-texture",
    "shared-memory",
    "cultcache-page",
  ],
});

const lane = streams.negotiate("mimir:kiyo-pro", {
  peerId: "fensalir",
  verseId: "studio",
  supportedTransports: ["shared-d3d12-texture", "cultcache-page"],
  acceptedKinds: ["video"],
  canImportGpuHandles: true,
});

streams.publishFrame({
  streamId: lane.streamId,
  sequence: 42n,
  timestampNs: 1_000_000_000n,
  durationNs: 33_333_334n,
  transport: lane.transport,
  nativeHandle: "0xfeed",
  fenceHandle: "0xbeef",
  fenceValue: 7n,
  unavoidableCopyCount: 0,
});

stopWatchingStreams();
stopWatchingFrames();
```

Current implementation status: this package mirrors typed stream declaration,
negotiation, watch callbacks, and latest-frame cursor contracts for non-C#
clients. The native C# path owns shared-memory rings and GPU-handle integration
points. CultCache page append/readback remains the durable recording or
nonlocal fallback.
