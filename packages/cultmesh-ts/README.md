# CultMesh TS

`cultmesh-ts` is the TypeScript projection of CultMesh contracts for local
tools and browser-adjacent runtimes. The C# `GameCult.Mesh` package owns the
native CultMesh stream implementation used by Mimir and Fensalir; this package
mirrors the typed declaration and negotiation shapes without owning the hot
frame body path.

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

Current implementation status: this package mirrors typed stream declaration,
negotiation, and latest-frame cursor contracts for non-C# clients. The native
C# path owns shared-memory rings and GPU-handle integration points. CultCache
page append/readback remains the durable recording or nonlocal fallback.
