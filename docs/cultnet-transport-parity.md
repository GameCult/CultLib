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
| C# `GameCult.Networking` | LiteNetLib UDP `NetManager` / `NetPeer`; sends legacy union messages and schema-v0 messages with `DeliveryMethod.ReliableOrdered`; single-peer RUDP socket transport exists in the library | LiteNetLib connection requests and app-level peer/catalog surfaces | Production LiteNetLib path plus UDP socket binding for the shared RUDP reliability owner |
| C# interop peer | TCP stream with 4-byte length-prefixed MessagePack frames | UDP multicast probe/announce | Test harness only |
| TypeScript `cultnet-ts` | `CultNetPeer` over any Node `Duplex`, TCP-framed transport, or single-peer RUDP socket transport; interop uses TCP | UDP multicast probe/announce in the interop peer | First UDP socket binding for the shared RUDP reliability owner |
| Rust `cultnet-rs` | Interop example uses TCP length-prefixed MessagePack frames; single-peer RUDP socket transport exists in the library | UDP multicast probe/announce | UDP socket binding for the shared RUDP reliability owner |
| Python `cultcache-py` | TCP sockets with 4-byte length-prefixed MessagePack frames for local CultMesh/CultNet server and client; single-peer RUDP socket transport exists in the library | Endpoint lists and CultMesh peer/Verse catalogs | UDP socket binding for the shared RUDP reliability owner |
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
- C# now has the shared `tcp_framed` transport profile helper plus a
  `TcpFramedTransportConnection` with schema-channel `SendAsync`,
  `ReceiveAsync`, and transfer stats. The C# interop peer advertises and uses
  that shared port instead of owning raw TCP frame I/O directly.
- TypeScript has the first narrow transport connection port:
  `TcpFramedTransportConnection` owns length-prefixed frame delivery, exposes
  frame/close/error events, `send(channel, payload)`, `close`, and transfer
  stats, and `CultNetPeer` can now run over either the legacy `Duplex` stream
  path or the transport connection path.
- Python now has the same `tcp_framed` transport profile helper plus a
  synchronous `TcpFramedTransportConnection` with `send`, `receive`, `close`,
  and stats. `CultNetRawClient` and database subscriptions send/read through
  that port instead of owning frame I/O directly.
- Rust now has the same `tcp_framed` transport profile helper plus a
  `TcpFramedTransportConnection` with schema-channel `send`, `receive`, and
  transfer stats. The Rust interop peer advertises and uses that shared port
  instead of owning raw TCP frame I/O directly.
- TypeScript, C#, Rust, and Python now share the first
  `cultnet.transport.rudp.v0` packet codec fixture: the same reliable ordered
  fragmented data packet encodes to the same bytes in every runtime. This is not
  the RUDP runtime yet; it is the binary packet language the runtimes must
  converge on before resend loops, windows, and timeout behavior are allowed to
  claim parity.
- TypeScript, C#, Rust, and Python now share the first deterministic RUDP
  reliability state machine: connect/accept handshake, packet-level
  ack/ack-mask accounting, reliable resend scheduling, duplicate suppression,
  and reliable ordered channel delivery. It is in-memory and socket-free so the
  behavior can be tested before any runtime binds it to UDP I/O.
- C#, TypeScript, Rust, and Python now have socket-backed RUDP transport
  connections that bind a single UDP peer to the shared RUDP session. TypeScript
  emits the same `frame` events as the TCP-framed transport and can carry
  `CultNetPeer` schema messages over reliable ordered `schema` frames; C#,
  Rust, and Python expose the same transport frame/stats shape through
  synchronous socket polling.
- The remaining parity work is to add the equivalent port to Kotlin, deepen
  Python's server-side use of its port, and move each runtime's existing
  TCP/LiteNetLib/WebSocket bodies behind those ports.

## RUDP Packet Contract V0

`cultnet.transport.rudp.v0` packets start with a fixed binary header, followed
by a UTF-8 channel id and payload bytes. All integer fields are unsigned
big-endian. The first deterministic fixture is live in TypeScript, C#, Rust,
and Python; every future runtime must port it byte-for-byte before
runtime-specific resend loops are allowed to claim parity.

| Offset | Size | Field | Notes |
| --- | ---: | --- | --- |
| 0 | 4 | magic | ASCII `CNR0` |
| 4 | 1 | version | `0` |
| 5 | 1 | packet type | `1=connect`, `2=accept`, `3=data`, `4=ack`, `5=ping`, `6=pong`, `7=disconnect` |
| 6 | 1 | flags | bit 0 reliable, bit 1 ordered, bit 2 sequenced, bit 3 fragmented |
| 7 | 1 | header bytes | fixed header plus channel id bytes |
| 8 | 4 | connection id | peer/session binding token |
| 12 | 4 | sequence | packet sequence number |
| 16 | 4 | ack | latest received sequence being acknowledged |
| 20 | 4 | ack mask | selective acknowledgement mask for prior packets |
| 24 | 2 | fragment id | `0` when not fragmented |
| 26 | 2 | fragment index | zero-based fragment index |
| 28 | 2 | fragment count | total fragments, `0` when not fragmented |
| 30 | 4 | payload bytes | payload length |
| 34 | 1 | channel id bytes | UTF-8 byte count, max 255 |
| 35 | 1 | reserved | `0` |
| 36 | n | channel id | UTF-8 channel id |
| 36+n | m | payload | transport-neutral payload bytes |

Canonical reliable ordered data fixture:

```text
packetType=data
connectionId=0x01020304
sequence=0x0000002a
ack=0x00000029
ackMask=0x80000001
channelId=schema
flags=reliable|ordered|fragmented
fragmentId=7
fragmentIndex=1
fragmentCount=3
payload=hello
hex=434e523000030b2a010203040000002a0000002980000001000700010003000000050600736368656d6168656c6c6f
```

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
