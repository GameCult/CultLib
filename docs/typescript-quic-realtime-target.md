# TypeScript QUIC Realtime Plane Target

Date: 2026-09-16

Status: target accepted by the operator (2026-09-16), ready for Eureka. No cut
map exists yet. When the pipeline starts, Imagination maps the cut into
`docs/typescript-quic-realtime-cut.md` against `main` at `a0813c6` or later.

The TypeScript runtime gains the CultMesh QUIC realtime state plane that the C#
runtime already has: a provider that serves frames and a consumer that receives
them, byte-compatible with `GameCult.Mesh.Quic` and the native MsQuic connector
the Unity package ships. This document states the end state and the invariants
that constrain getting there.

Operator ruling, 2026-09-16, on providing QUIC for a Node service: "Option 2 all
the way, this is what CultLib is supposed to be, we're not half assing it." A
separate .NET provider process and a WebSocket shortcut were both rejected.

## Why now

The first consumer is StreamPixels. Its service (Node 24, TypeScript, on
Yggdrasil) must publish realtime state to a Unity overlay running on a
streamer's machine. The overlay is a receive-only client using the Unity
package's `CultMeshNativeQuicRealtimeTransportConnector`. Today no TypeScript
path exists:

- Node v24.15.0 has no `node:quic`, with or without `--experimental-quic`.
- `packages/cultmesh-ts` and `packages/cultnet-ts` contain no QUIC code.
- `GameCult.Mesh.Quic` (.NET 10, `System.Net.Quic`) is the only QUIC provider.
- The C# TCP control and content connectors are plaintext and restricted to
  loopback (`src/GameCult.Mesh/docs/transport-planes.md`, "Security and release
  gate"), so they cannot carry the overlay's remote session either.

## This touches the foundation

- **Wire parity is an invariant.** The C# codec is the reference:
  `src/GameCult.Mesh/CultMeshRealtimeWireProtocol.cs`. The TypeScript runtime
  must produce and accept identical bytes:
  - ALPN `cultmesh-state-v1`; connection close code `0x43554c54`, stream abort
    code `0x53544154`; reliable stream type `1`, latest-only stream type `2`.
  - Frame: magic `0x31545343`, then a 37-byte little-endian fixed header
    (delivery byte, producer epoch i64, sequence i64, channel/schema/body id
    lengths u16, payload length i32, header size i32 = 37, wire version u16 =
    1), then UTF-8 channel id, schema id, body id, then payload.
  - Limits: payload at most 64 MiB; each identity at most 65,535 UTF-8 bytes;
    decode rejects wrong magic, unknown version, out-of-range delivery, and any
    length that does not sum exactly.
- **Delivery semantics are an invariant**
  (`src/GameCult.Mesh.Quic/README.md`, `CultMeshRealtimeTransports.cs`):
  - `ReliableOrdered`: one persistent ordered stream; broadcast is
    completion-bearing.
  - `LatestOnly`: independent streams; at most one pending frame per
    `(channel, body)`, replaced by a newer `(producer epoch, sequence)`
    generation; a keyed coalescing outbox per physical peer, so a slow or
    departed consumer never backpressures the producer.
  - `Unreliable`: fails closed until datagrams are exposed, matching the managed
    C# adapter.
- **Trust is an invariant.** No remote session comes online without the chain
  in `transport-planes.md`: an Odin-signed `CultMeshRouteCertificate` (Verse,
  authority runtime, protocol set, endpoint, generation, validity, provider
  P-256 key) checked against consumer-pinned Odin roots, TLS 1.3, and the
  endpoint certificate bound to that route, including the
  `cultmesh-state+quic://host:port?cert-sha256=<uppercase SHA-256>` pin.
  Signatures are ECDSA P-256 over SHA-256 with P1363 `r || s` encoding and the
  length-prefixed canonical transcript. Unsigned traffic exists only under an
  explicit `LocalDevelopment` policy on loopback.
- **One implementation of TypeScript route verification.** `cultmesh-browser`
  already verifies Odin route certificates and provider nonce possession with
  WebCrypto (`packages/cultmesh-browser/src/index.ts`, `verifyAuthorityRoute`,
  `canonicalRoute`, `canonicalSession`, `verifyP256`). The Node QUIC path must
  reuse that owner, not grow a second verifier.
- **The realtime plane carries state frames only.** Schemas, commands,
  receipts, manifests, and immutable content do not travel through it
  (`ICultMeshRealtimeTransport` doc comment). RUDP is never installed
  implicitly in its place.
- **Existing consumers survive.** The StreamPixels service already imports
  `cultcache-ts`, `cultnet-ts`, and `cultmesh-ts` (`verse-state.ts`,
  `idunn-rudp-health.ts`) for Odin advertisement and Idunn health over RUDP.
  Imagination audits every other TypeScript consumer across `F:\Projects`
  before sizing the cut.

## Target state

- `cultmesh-ts` exposes a QUIC realtime provider and a QUIC realtime consumer
  with the same ergonomic shape as the C# `CultMeshQuicRealtimeServer`
  (listen, broadcast with an explicit delivery mode) and
  `ICultMeshRealtimeTransportConnector` (connect by advertised candidate,
  receive frames), under the branded `CultMesh` entrypoint.
- QUIC comes from native MsQuic bindings owned by CultLib, not from a Node
  polyfill or a third-party QUIC stack with different semantics. How the binding
  is built and shipped is the means (Imagination probes it); what it must
  deliver is TLS 1.3, ALPN, independent unidirectional or bidirectional streams,
  certificate validation with a caller hook, and clean close and abort codes.
- Frame encoding and decoding exist once in TypeScript and are tested against
  bytes produced by the C# codec.
- Application code never picks endpoints, ranks routes, or writes reconnect
  loops; `CultMeshSessionManager`-equivalent route selection owns that, as in C#.
- The build produces binaries for the hosts that run them: Windows x64 for
  local development, and Linux x64 for Yggdrasil, built on Linux or the deploy
  path's builder, not cross-compiled on Starfire.

## Not in this migration

One foundation per pipeline. These are separate, named pipelines or consumer
work:

- **TypeScript content plane** (a provider in `cultmesh-ts`, a verifying HTTPS
  reader in `cultmesh-browser`) for exported character artifacts. HTTPS content
  transport and single-host trust on Yggdrasil were already ruled.
- **Authenticated remote control plane** (TLS on the TCP schema and content
  connectors). See Q1.
- **Browser QUIC** (WebTransport). Browsers keep the verified WebSocket path.
- **Python, Rust, and Kotlin QUIC.** The parity scope is role-scoped; this
  migration claims TypeScript and C# only.
- **StreamPixels integration** (service publication, overlay subscription, and
  the catalog and scene-intent schemas) is consumer work in StreamPixels after a
  released `cultmesh-ts` exists.

## Verification the cut must reach

- Golden frame bytes from the C# codec decode in TypeScript, and TypeScript
  encodings decode in C#, for every delivery mode and at the length limits.
  Malformed inputs are rejected identically.
- Separate processes, both directions: a TypeScript provider to the C# native
  connector (the one Unity uses) and to the managed connector, and a C# managed
  provider to the TypeScript consumer. Extend the existing parity harnesses
  (`CULTMESH_NATIVE_EXTERNAL_ENDPOINT`, `packages/cultnet-ts/test/interop`) rather
  than adding a parallel one.
- `LatestOnly` coalescing: a stalled consumer does not delay the producer, and
  the consumer converges on the terminal generation.
- Trust negatives: an expired or unsigned route, the wrong Odin root, a
  mismatched `cert-sha256` pin, and a replayed nonce are each refused, with a
  mutation per rule.
- The Linux x64 binary runs on the Yggdrasil deploy path, verified on that host.

## Rulings (operator, 2026-09-16)

The operator accepted every recommendation below ("You got it"). Each question
is kept with its options as the record of what was decided against.

- **Q1. Which remote plane carries the overlay's non-frame data?** The realtime
  plane forbids schemas, commands, and content, and the TCP planes are
  loopback-only. A: publish her character, the subathon window, and scene
  intents as QUIC state frames with their CultCache schema id and payload, and
  ship the costume catalog as a versioned immutable artifact on the HTTPS
  content plane; the overlay sends no commands, so it needs no remote control
  plane. B: add TLS to the TCP schema and content connectors as its own
  pipeline first. **Recommended: A.** It needs no new plane for M1, matches a
  receive-only client, and leaves TLS TCP to the first consumer that sends
  commands.
- **Q2. Where does shared TypeScript route verification live?** It is inside
  `cultmesh-browser` today, and `cultmesh-ts` is Node-only. A: move it to a
  browser-safe shared module that both import (for example beside
  `cultnet-ts/contracts`). B: have `cultmesh-ts` depend on `cultmesh-browser`.
  **Recommended: A**, so neither transport package owns the other's trust.
- **Q3. Which platforms must the native binding support at release?**
  **Recommended: Windows x64 and Linux x64 only**, the two hosts that run it
  today.
- **Q4. Are QUIC datagrams (`Unreliable`) in scope?** **Recommended: no.** Fail
  closed like the managed C# adapter; no consumer needs them.
