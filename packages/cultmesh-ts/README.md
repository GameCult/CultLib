# CultMesh TS

`cultmesh-ts` is the TypeScript surface for local CultMesh nodes: typed
CultCache documents, peer catalogs, authority leases, and realtime stream
contracts for runtimes that need to share media without turning the mesh into a
base64 soup kitchen.

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

The first public API is `CultMeshStreamCatalog`:

```ts
import { CultMesh } from "cultmesh-ts";

const streams = CultMesh.createStreamCatalog();

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
```

Current implementation status: this package owns the typed stream declaration,
negotiation, and latest-frame cursor surface. Runtime-specific transports still
need platform adapters: named shared memory on local processes, D3D/Metal/Vulkan
handle import for GPU frames, and CultCache page append/readback for durable
recording or nonlocal fallbacks.
