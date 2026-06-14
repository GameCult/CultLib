# CultNet Transport Parity Map

CultNet payload parity is not enough. The runtimes can already trade schema-v0
MessagePack documents through the interop lane, but realtime networking still
has one real owner: the C# `GameCult.Networking` stack on LiteNetLib. The other
runtimes mostly speak a TCP-framed dialect for tests and local services.

This document is the working map for promoting transport into CultNet itself so
C#, TypeScript, Rust, Python, Kotlin, and future runtimes can speak the same
network language.

## Objective

CultNet owns cross-runtime transport semantics:

- peers discover each other without runtime-specific side channels;
- connection/session lifecycle is described the same way in every runtime;
- reliable ordered delivery is portable and testable across runtimes;
- unreliable and sequenced realtime channels can exist beside reliable state
  sync without changing payload contracts;
- LiteNetLib becomes one adapter, not the hidden specification.

## Current Mechanism

| Runtime | Current data transport | Discovery | Transport authority today |
| --- | --- | --- | --- |
| C# `GameCult.Networking` | LiteNetLib UDP `NetManager` / `NetPeer`; sends legacy union messages and schema-v0 messages with `DeliveryMethod.ReliableOrdered` | LiteNetLib connection requests and app-level peer/catalog surfaces | C# owns the production-shaped realtime semantics |
| C# interop peer | TCP stream with 4-byte length-prefixed MessagePack frames | UDP multicast probe/announce | Test harness only |
| TypeScript `cultnet-ts` | `CultNetPeer` over any Node `Duplex`; interop uses TCP | UDP multicast probe/announce in the interop peer | Byte-stream abstraction; no UDP reliability owner |
| Rust `cultnet-rs` | Interop example uses TCP length-prefixed MessagePack frames | UDP multicast probe/announce | Example harness; no UDP reliability owner |
| Python `cultcache-py` | TCP sockets with 4-byte length-prefixed MessagePack frames for local CultMesh/CultNet server and client | Endpoint lists and CultMesh peer/Verse catalogs | Local framed service body; no UDP reliability owner |
| Kotlin `cultmesh-kotlin` | Minimal WebSocket-like lane over TCP socket | None found in the live package | Thin client surface |

The live split is therefore: payload language is converging, transport language
is not.

## Invariants

- `cultnet.schema.v0` payloads are transport-neutral. Message schemas must not
  know whether they rode TCP, LiteNetLib, WebSocket, or CultNet reliable UDP.
- Discovery is its own concern. A discovery packet may advertise transport
  endpoints and capabilities, but it does not own reliable delivery semantics.
- The first portable realtime channel must provide reliable ordered delivery,
  because schema catalogs, snapshots, shard logs, auth/session messages, and
  mutation forwarding already depend on ordered request/response behavior.
- Transport capability negotiation must happen before higher-level CultMesh
  state claims a peer is usable for realtime work.
- Auth/session primitives bind to a connection, but credential encryption and
  session token formats remain owned by CultNet security, not by UDP packet
  framing.
- Backpressure, resend pressure, MTU limits, fragmentation, ping/timeout, and
  reconnect posture are transport-owned signals. They must not leak upward as
  ad hoc message-level retry loops.

## Intended Ownership

`cultnet.transport.v0` owns the transport contract:

- endpoint description: protocol, host, port, path/group when relevant;
- channel descriptions: reliable ordered, reliable unordered, sequenced, and
  unreliable;
- packet envelope: connection id, sequence/ack state, channel id, fragment id,
  payload encoding, and payload bytes;
- session lifecycle: connect, accept/reject, disconnect reason, timeout, ping,
  reconnect state, and feature negotiation;
- flow control: send queue limits, resend window, fragmentation limit, and
  backpressure reporting.

Adapters own socket mechanics:

- `cultnet.transport.tcp_framed.v0`: existing 4-byte length-prefixed MessagePack
  stream, reliable ordered only;
- `cultnet.transport.litenetlib.v0`: C# adapter preserving current
  `GameCult.Networking` behavior while exposing CultNet channel semantics;
- `cultnet.transport.websocket.v0`: browser/mobile-friendly stream adapter,
  reliable ordered only;
- `cultnet.transport.rudp.v0`: native cross-runtime reliable UDP transport,
  replacing LiteNetLib as the portable realtime specification.

Payload handlers own documents:

- `cultnet.login.v0`, `cultnet.register.v0`, `cultnet.verify.v0`, and
  `cultnet.login_success.v0` are security/session messages;
- `cultnet.schema_catalog_*`, snapshot, shard catalog, shard log, subscription,
  and simulation messages remain schema-v0 payloads;
- no payload handler decides packet resend, channel ordering, or fragmentation.

## Cut Line

- Do not build a Python-only LiteNetLib clone.
- Do not let the TCP interop harness become the implied production transport.
- Do not hide transport capability in loose endpoint strings.
- Do not add message-level retry compensators for missing reliable UDP.
- Do not put UDP packet mechanics into CultMesh database/session code.
- Do not treat C# LiteNetLib behavior as optional folklore; mine it for the
  required semantics, then express those semantics in CultNet-owned contracts.

## First Portable Contract

The first shippable slice should be a transport profile document that every
runtime can advertise through schema catalogs and hello/peer descriptors:

```text
cultnet.transport_profile.v0
  runtimeId
  transports[]
    transportId
    protocol
    endpoint
    wireContracts[]
    channels[]
      channelId
      delivery
      ordering
      maxPayloadBytes
      maxFragmentBytes
```

This is not the reliable UDP implementation. It is the shared language that
lets the runtimes stop guessing what kind of pipe a peer is offering.

## Implementation Sequence

1. Add `cultnet.transport_profile.v0` as a shared contract in C#, TS, Rust, and
   Python catalogs.
2. Make every runtime advertise its current transport honestly:
   `tcp_framed`, `litenetlib`, or `websocket`.
3. Add a cross-runtime transport-profile interop check so parody surfaces fail
   loudly.
4. Introduce a narrow transport port in each runtime:
   `connect`, `accept`, `send(channel, payload)`, `receive`, `close`, `stats`.
5. Put current TCP/LiteNetLib/WebSocket bodies behind that port.
6. Build `cultnet.transport.rudp.v0` once against the shared port and prove it
   with C#/TS/Rust/Python ping, loss, reordering, fragmentation, reconnect, and
   schema-message tests.

Current progress:

- Steps 1-3 are live in C#, TypeScript, Rust, and Python.
- TypeScript has the first narrow transport connection port:
  `TcpFramedTransportConnection` owns length-prefixed frame delivery, exposes
  frame/close/error events, `send(channel, payload)`, `close`, and transfer
  stats, and `CultNetPeer` can now run over either the legacy `Duplex` stream
  path or the transport connection path.
- Python now has the same `tcp_framed` transport profile helper plus a
  synchronous `TcpFramedTransportConnection` with `send`, `receive`, `close`,
  and stats. `CultNetRawClient` and database subscriptions send/read through
  that port instead of owning frame I/O directly.
- The remaining parity work is to add equivalent ports to C#, Rust, and Kotlin,
  deepen Python's server-side use of its port, and move each runtime's existing
  TCP/LiteNetLib/WebSocket bodies behind those ports before implementing
  `rudp`.

## Reliable UDP Semantics To Lift From LiteNetLib

The portable RUDP contract must cover at least:

- connection handshake with application connection key support;
- peer id/session id binding;
- ping and timeout detection;
- reliable ordered channel delivery;
- unreliable channel delivery for realtime observations/input;
- sequenced delivery for latest-state streams;
- duplicate suppression;
- ack masks or equivalent selective acknowledgement;
- resend windows and retransmission timers;
- MTU-safe fragmentation and reassembly;
- disconnect reason propagation;
- bounded send queues and observable backpressure;
- deterministic wire fixtures for packet encode/decode in every runtime.

Until those are runtime-owned CultNet contracts, the runtimes do not truly
"speak the same language"; they are only translating documents after somebody
else already solved the pipe.
