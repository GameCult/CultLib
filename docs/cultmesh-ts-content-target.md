# TypeScript CultMesh Content Plane Target

Campaign `cultmesh-ts-content`. Self wrote this on 2026-10-19 for the
StreamPixels M1 live character preview. That preview is a three.js page that
loads glTF bodies and accessories by content hash from Yggdrasil.

## Why now

CultMesh moves large immutable artifacts as content-addressed chunks described
by a `gamecult.mesh.cdn_artifact_manifest`. C# packs manifests, serves chunks
over TCP (`CultMeshContentServer`), fetches them over HTTPS
(`CultMeshHttpsContentTransportConnector`), and verifies each chunk and the
whole body (`CultMeshContentTransferService`). Rust speaks the chunk messages
through `cultnet-rs` `fetch_content` and `answer_content_chunk_request`.
TypeScript has none of this: no manifest type, no packer, no provider and no
reader. And no runtime has an HTTPS content *server*; the C# HTTPS connector
has never had a peer.

Standing ruling (StreamPixels `state/map.yaml`, "Content plane", operator
2026-09-15): HTTPS content transport serves both browser and overlay.
Yggdrasil plus SHA-256 verification is the trust boundary for now.
Distributed zero-trust distribution is future work. The catalog never stores
URLs; clients resolve content through CultMesh.

## Target state

- **One TypeScript owner of the CDN document shapes.** It holds the manifest
  and chunk-ref types, their MessagePack wire form, `normalizeHash`, the
  record-key grammar (`mesh:cdn:chunk:<hex>` and
  `mesh:cdn:artifact:<token>:<version>:<hex>`), and the packer. A manifest
  that TypeScript packs is byte-identical to the one C# packs from the same
  bytes and options. The owner can be imported in a browser, because the
  reader needs it there.
- **An HTTPS content provider in `cultmesh-ts`.** It answers
  `GET <endpoint>?chunkHash=<hex>&recordKey=<key>` with exactly the chunk's
  bytes and a `Content-Length` equal to the chunk's size: the request shape
  the C# connector sends (`CultMeshHttpsContentTransport.cs` `BuildChunkUri`).
  Its core is framework-neutral: a request maps to an answer, and a host can
  mount it on `node:https` or on any HTTP framework. It reads from a content
  store port. The store shipped with it serves content-addressed files from a
  directory outside git. The provider never serves a byte it has not
  verified against its hash, and it never serves anything but a chunk:
  there is no generic static-file route.
- **A verifying reader in `cultmesh-browser`.** Given a resolved endpoint and
  a manifest, it fetches the manifest's chunks over HTTPS with the C# query
  shape. It checks each response's length, each chunk's SHA-256 and the whole
  body's SHA-256 with WebCrypto, and returns the bytes or throws a typed
  error. No byte that failed its hash reaches the caller, in whole or in part.
  It runs in browsers and in Node 20+.
- **Endpoints come from CultMesh, not from content.** The reader takes the
  endpoint as an argument that the caller resolved through CultMesh. Manifests
  carry no URL.

## Invariants

- `wire-parity`: manifests, chunk refs and record keys are byte-identical
  across C#, Rust and TypeScript for the same input. The C# runtime is the
  reference. TypeScript writes and reads the shared vectors in
  `contracts/cultmesh/`.
- `https-shape-parity`: the C# `CultMeshHttpsContentTransportConnector`
  fetches content from the TypeScript provider unmodified, in a cross-process
  test. The TypeScript reader fetches the same chunks from the same provider.
- `verify-before-use`: the reader releases no byte that failed its chunk hash
  or the whole-body hash. It refuses a body longer than the advertised size at
  its first excess byte, and a shorter one at its end. It does not trust
  response length headers, because Fetch decodes content codings and a proxy
  can rewrite them (research 2026-10-19, section 6).
- `verify-before-serve`: the provider serves only bytes whose SHA-256 equals
  the requested chunk hash.
- `chunk-only-route`: the provider answers only chunk requests. A path, a
  filename or a hash outside the store can never select a file.
- `no-urls-in-content`: no manifest, chunk ref or record carries an endpoint.
- `one-owner-per-shape`: one TypeScript module owns the CDN shapes, and both
  packages consume it. Neither package keeps a private copy.
- `general-purpose`: the plane is judged by what a reasonable consumer
  expects, not only by StreamPixels' slice (operator, 2026-09-30). Multi-chunk
  artifacts, empty artifacts, cancellation, bounded concurrency and browser
  cross-origin use all work.
- `released`: the npm packages StreamPixels vendors are published at versions
  that contain this plane. The release is its own cut with a Soul pass before
  the tag.

## Not in this campaign

- Odin route verification for content. Single-host trust is ruled for now.
- Resumable or durable transfer state in TypeScript. The browser holds
  artifacts in memory.
- Distributed or zero-trust distribution, and peer serving.
- A TypeScript TCP or RUDP content server, and an HTTPS content server in Rust
  or C#.
- Garbage collection of stored blobs.
- StreamPixels' own export, publish and service wiring (its B2 and B3 cuts).
  StreamPixels is the consumer, not part of this change.

## Consumer

StreamPixels `map/web-customize` (dfbe2a6),
`docs/web-character-customization-map.md` section B1. Each option's artifact is
10-125 KB, and a full avatar is 0.5-0.8 MB. The browser fetches them. The
service serves them from `/srv/streampixels/content/` on Yggdrasil, outside
git, and hashes every referenced blob at boot.
