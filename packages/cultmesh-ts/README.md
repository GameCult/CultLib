# CultMesh TS

`cultmesh-ts` is the TypeScript projection of CultMesh contracts for local
tools and browser-adjacent runtimes. The C# `GameCult.Mesh` package owns the
native CultMesh stream implementation used by Mimir and Fensalir; this package
mirrors the typed declaration and negotiation shapes without owning the hot
frame body path.

## Local Catalogs

The TypeScript projection keeps the same local catalog ergonomics as the other
runtimes: sorted views, direct lookup, and unsubscribe-able watches.

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

const client = await CultMesh.createRudpClientForAuthorizedPeer(
  "ts-client",
  0x1020_3040,
  peers,
  leases,
  "local",
  "schema",
);
client.connect(new TextEncoder().encode("join"));
```

`CultMesh.createRudpClientForPeer(...)` remains available for already trusted
call sites. Discovery-first paths should prefer
`createRudpClientForAuthorizedPeer(...)`, which composes
`CultMeshPeerCatalog.firstAuthorized(...)` with the authority lease catalog
before using the peer-card endpoint as a dial target.

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
