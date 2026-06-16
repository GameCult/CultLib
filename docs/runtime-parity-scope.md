# Runtime Parity Scope

This is the claim CultLib can make without turning evidence into incense.
Parity means the targeted runtimes speak the same wire language and expose
similar developer-facing entrypoints for the roles they own. It does not mean
every runtime clones the C# production server, LiteNetLib history, or hot-loop
memory layout.

## Objective

Cross-runtime parity has three layers:

- Wire parity: shared CultCache file/state contracts, schema-v0 MessagePack
  documents, transport profiles, and RUDP packets must cross runtime boundaries
  without translation folklore.
- Feature parity: each runtime must expose the CultCache/CultNet/CultMesh
  surfaces needed for its intended role, with unsupported production-server
  ownership called out plainly.
- Ergonomic parity: callers should find a branded `CultMesh` entrypoint,
  typed catalogs, discovery helpers, authority checks, stream surfaces, and
  transport helpers instead of assembling raw maps and channel strings.

SoA performance parity is deliberately not a cross-runtime requirement. C#
owns the CPU-local SoA table and shared-memory streaming body path. Other
runtimes may add native hot paths when their runtime role earns them, but they
do not need to mimic Unity/C# memory layout to speak CultNet or participate in
CultMesh.

## Current Targeted Runtimes

| Runtime | Role | Claimed parity | Not claimed |
| --- | --- | --- | --- |
| C# | Reference implementation and production game/server substrate | CultCache typed documents and SoA, CultNet schema-v0, LiteNetLib production path, shared transport profiles, shared RUDP socket transport, RUDP reconnect policy/controller, LiteNetLib client reconnect-controller adoption, CultMesh node/session/catalog/stream/simulation surfaces | No need to abandon LiteNetLib before the CultNet adapter fully owns production service adoption |
| Python | Package runtime, local daemon endpoint, raw interop peer, typed state participant | CultCache/CultNet/CultMesh package surface, schema catalogs, shard catalogs/logs, local TCP CultMesh server, session/prediction/simulation fact helpers, authority leases, streams, RUDP codec/session/socket transport, RUDP reconnect policy/controller, schema-v0 MessagePack over RUDP evidence, benchmark evidence against C# cache paths | Universal C# throughput, full C# daemon/server stack, and RUDP as the only public service pipe |
| TypeScript | Node/browser-adjacent projection and interop runtime | CultCache parity tests, CultNet schema-v0 peer, transport profiles, TCP-framed transport port, shared RUDP socket transport, RUDP reconnect policy/controller, local CultMesh node/catalog/authority/stream ergonomics | Full C# game session/server stack |
| Rust | Low-level cache/transport/runtime substrate | CultCache/CultNet interop lane, transport profiles, TCP-framed port, shared RUDP codec/session/socket transport, RUDP reconnect policy/controller, schema-v0 MessagePack over RUDP evidence, small CultMesh facade for peer cards, authority leases, RUDP endpoint parsing, and peer-card RUDP client construction | Full CultMesh game-session/server facade, stream catalog, or production game-session server |
| Kotlin | JVM/Android client substrate | Typed MessagePack cache, schema/shard catalogs, authority leases, stream catalog, branded `CultMesh` facade, RUDP client/server sugar, RUDP reconnect policy/controller, schema-v0 MessagePack over RUDP evidence | C# SoA store, full C# server daemon, or Unity hot-loop memory layout |

## Evidence Gates

- `packages/cultnet-ts/test/interop/cultnet-interop.test.ts` is the main live
  wire harness. It covers schema-v0 interop and RUDP exchange across C#,
  TypeScript, Rust, Python, and Kotlin, including loss, reordering,
  fragmentation, disconnect reason propagation, ping/pong, and schema-v0
  MessagePack over RUDP for C#, Rust, Python, and Kotlin peers against
  TypeScript. Those RUDP schema-message tests also assert the advertised
  `reconnectPolicy` profile field from C#, Rust, Python, and Kotlin peers.
  Runtime-local tests cover the deterministic delay helper and reconnect
  controller in C#, TypeScript, Rust, Python, and Kotlin.
- `packages/cultcache-py/PARITY.md` is the Python-specific audit ledger. Its
  "Still Not Claimed" section is part of the contract, not an apology.
- `docs/cultnet-transport-parity.md` is the transport ownership map. It tracks
  the remaining work to lower older TCP/LiteNetLib/WebSocket bodies behind the
  shared transport port and expand RUDP adoption beyond the core proof.
- `packages/cultmesh-kotlin/README.md` records Kotlin's runtime role and
  ergonomic surface, including the explicit non-claim around C# SoA storage.

## Remaining Work Before A Full Completion Claim

- Broaden service adoption so remaining daemon and production paths use the
  shared transport port consistently instead of keeping raw
  TCP/LiteNetLib/WebSocket ownership in individual bodies.
- Extend RUDP interop evidence through daemon/socket reconnect loops and the
  wider schema-message matrix. RUDP profiles now advertise the portable
  reconnect policy, runtime-local tests prove the shared controller across the
  targeted runtimes, and the C# LiteNetLib client uses that controller; broader
  service adoption remains a separate claim.
- Keep Kotlin ergonomic parity moving with Android/JVM-shaped APIs instead of
  backfilling C# server responsibilities it should not own.
- Keep performance claims evidence-bound: C# owns SoA; Python has benchmark
  comparison gates; other runtimes should advertise measured native hot paths
  only when those paths exist.
