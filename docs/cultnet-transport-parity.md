# CultNet Transport Parity Map

CultNet payload parity is not enough. The runtimes can already trade schema-v0
MessagePack documents through the interop lane, and the shared
`cultnet.transport.rudp.v0` packet/session language now exists across C#,
TypeScript, Rust, Python, and Kotlin. The remaining split is service adoption:
C# still carries the production LiteNetLib path, and older TCP-framed or
WebSocket-like bodies still exist for local services and harnesses.

This document is the working map for promoting transport into CultNet itself so
C#, TypeScript, Rust, Python, Kotlin, and future runtimes can speak the same
network language. Runtime role boundaries live in
[runtime-parity-scope.md](runtime-parity-scope.md).

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
| C# `GameCult.Networking` | LiteNetLib UDP `NetManager` / `NetPeer`; sends legacy union messages and schema-v0 messages with `DeliveryMethod.ReliableOrdered`; single-peer RUDP socket transport exists in the library | LiteNetLib connection requests and app-level peer/catalog surfaces | Production LiteNetLib adapter profile plus UDP socket binding for the shared RUDP reliability owner |
| C# interop peer | Schema-v0 MessagePack over TCP-framed or shared RUDP transport | UDP multicast probe/announce with TCP and RUDP transport profiles | Test harness transport parity |
| TypeScript `cultnet-ts` | `CultNetPeer` over any Node `Duplex`, TCP-framed transport, or single-peer RUDP socket transport; interop uses TCP | UDP multicast probe/announce in the interop peer | First UDP socket binding for the shared RUDP reliability owner |
| Rust `cultnet-rs` | Interop example serves and dials schema-v0 MessagePack over TCP-framed or shared RUDP transport; single-peer RUDP socket transport exists in the library | UDP multicast probe/announce with TCP and RUDP transport profiles | UDP socket binding for the shared RUDP reliability owner |
| Python `cultcache-py` | TCP sockets with 4-byte length-prefixed MessagePack frames for local CultMesh/CultNet server and client; single-peer RUDP socket transport exists in the library | Endpoint lists and CultMesh peer/Verse catalogs | UDP socket binding for the shared RUDP reliability owner |
| Kotlin `cultmesh-kotlin` | Channel-aware WebSocket transport connection for reliable ordered `schema` frames; single-peer RUDP socket transport exists in the library; interop CLI serves and dials schema-v0 MessagePack over TCP-framed or shared RUDP transport | CultMesh Verse and peer catalogs, plus endpoint lists carried in schema-v0 catalog messages | WebSocket/TCP adapters plus UDP socket binding for the shared RUDP reliability owner, with build-script and TypeScript harness proof |

The live split is therefore: payload language and the native RUDP packet/session
language now converge across the targeted runtimes, while production service
adoption still varies by runtime and older TCP/LiteNetLib/WebSocket bodies have
not all been lowered behind the shared transport port.

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

1. Add `cultnet.transport_profile.v0` as a shared contract in C#, TS, Rust,
   Python, and Kotlin catalogs.
2. Make every runtime advertise its current transport honestly:
   `tcp_framed`, `litenetlib`, or `websocket`.
3. Add a cross-runtime transport-profile interop check so parody surfaces fail
   loudly.
4. Introduce a narrow transport port in each runtime:
   `connect`, `accept`, `send(channel, payload)`, `receive`, `close`, `stats`.
5. Put current TCP/LiteNetLib/WebSocket bodies behind that port.
6. Build `cultnet.transport.rudp.v0` once against the shared port and prove it
   with C#/TS/Rust/Python/Kotlin ping, loss, reordering, fragmentation,
   reconnect, and schema-message tests.

Current progress:

- Steps 1-3 are live in C#, TypeScript, Rust, Python, and Kotlin.
- C# now has the shared `tcp_framed` transport profile helper plus a
  `TcpFramedTransportConnection` with schema-channel `SendAsync`,
  `ReceiveAsync`, and transfer stats. The C# interop peer advertises and uses
  that shared port instead of owning raw TCP frame I/O directly. The C# interop
  peer now also advertises and serves the full schema-v0 flow over
  `interop-rudp`, and its dial path can use the shared RUDP transport while
  keeping TCP-framed as compatibility.
- C# now has a `litenetlib` transport profile helper, and the production
  `Client`/`Server` expose profiles for the LiteNetLib lane. The profile names
  both the modern reliable ordered `schema` channel and the legacy reliable
  ordered `legacy` union-message channel, so LiteNetLib is described as an
  adapter instead of remaining the hidden default. `NetPeer` send helpers now
  route through `LiteNetLibTransportConnection`; `Client` and `Server` now keep
  a per-peer adapter and route their own schema/legacy sends through it as
  well. Inbound payload classification and transfer stats also belong to that
  channel-aware adapter before dispatching schema-v0 or legacy messages. The
  C# database, simulation-observation, Verse-discovery, and peer-exchange
  service wrappers now enter their built-in handlers through
  `CultNetServerPeer`, a transport-aware server peer context. Responses send
  through that context instead of reaching around the per-peer adapter with
  direct `NetPeer` extension sends. The C# schema-client port now also has an
  RUDP adapter; shard log fetch, shard snapshot fetch, and shard write
  forwarding select it for `rudp://` primary endpoints and keep LiteNetLib for
  `cultnet://` endpoints, so those service clients no longer need custom
  injection to leave the LiteNetLib lane.
- TypeScript has the first narrow transport connection port:
  `TcpFramedTransportConnection` owns length-prefixed frame delivery, exposes
  frame/close/error events, `send(channel, payload)`, `close`, and transfer
  stats, and destroys its owned stream on close. `CultNetPeer` can still run
  over either the compatibility `Duplex` stream path or the transport
  connection path, while the TypeScript interop peer now accepts and dials TCP
  peers through `TcpFramedTransportConnection` so the parity harness exercises
  the shared port instead of raw socket framing. `createTcpFramedCultNetPeer`
  gives application callers the same transport-first path without hand-building
  the profile and adapter; raw `Duplex` peer construction is now explicitly the
  compatibility lane.
- Python now has the same `tcp_framed` transport profile helper plus a
  synchronous `TcpFramedTransportConnection` with `send`, `receive`, `close`,
  and stats. `CultNetRawClient`, database subscriptions, and the local
  CultMesh server connection loop send/read through that port instead of owning
  frame I/O directly. The Python TCP interop peer now also accepts and dials
  through that port, so the parity harness no longer keeps a raw framing
  side-channel for Python's schema lane.
- Rust now has the same `tcp_framed` transport profile helper plus a
  `TcpFramedTransportConnection` with schema-channel `send`, `receive`, and
  transfer stats. The Rust interop peer advertises TCP and RUDP profiles and
  serves/dials schema-v0 through the shared RUDP path without giving RUDP its
  own document semantics.
- Kotlin now has a `websocket` transport profile helper plus
  `CultNetWebSocketTransportConnection`, which wraps the older binary
  WebSocket client behind the same `CultNetTransportFrame` and stats shape for
  reliable ordered `schema` frames. The adapter now also has `sendSchemaMessage`
  and `receiveSchemaMessage` sugar so JVM callers can exchange schema-v0
  MessagePack messages without hand-parsing transport frames.
- TypeScript, C#, Rust, Python, and Kotlin now share the first
  `cultnet.transport.rudp.v0` packet codec fixture: the same reliable ordered
  fragmented data packet encodes to the same bytes in every runtime. This is not
  the RUDP runtime yet; it is the binary packet language the runtimes must
  converge on before resend loops, windows, and timeout behavior are allowed to
  claim parity.
- TypeScript, C#, Rust, Python, and Kotlin now share the first deterministic RUDP
  reliability state machine: connect/accept handshake, packet-level
  ack/ack-mask accounting, reliable resend scheduling, duplicate suppression,
  and reliable ordered channel delivery. It is in-memory and socket-free so the
  behavior can be tested before any runtime binds it to UDP I/O.
- C#, TypeScript, Rust, Python, and Kotlin now have socket-backed RUDP transport
  connections that bind a single UDP peer to the shared RUDP session. TypeScript
  emits the same `frame` events as the TCP-framed transport and can carry
  `CultNetPeer` schema messages over reliable ordered `schema` frames; C#,
  Rust, Python, and Kotlin expose the same transport frame/stats shape through
  synchronous socket polling.
- The TypeScript interop harness now proves bidirectional cross-runtime RUDP
  socket exchange with C#, Kotlin, Python, and Rust: TypeScript can dial those
  UDP peers, C#/Kotlin/Python/Rust can dial a TypeScript UDP peer, and all sides
  exchange reliable ordered `schema` frames through the shared handshake and
  packet language.
- The same harness now proves schema-v0 MessagePack messages over RUDP in both
  directions for C#, Kotlin, Python, and Rust peers against TypeScript. Those tests
  send `cultnet.schema_catalog_request.v0` payloads through reliable ordered
  `schema` frames and validate `cultnet.hello.v0` responses with advertised
  RUDP transport profiles where the responding runtime owns them, including the
  shared `reconnectPolicy` field.
- The same harness now proves reliable ordered `schema` delivery survives one
  dropped data packet in both directions between TypeScript and C#/Kotlin/Python/Rust.
  The drops happen below the transport connection through a UDP bridge, so the
  recovery is RUDP resend/ack behavior, not application-level retry.
- The same harness now proves reliable ordered `schema` delivery survives
  reordered data packets in both directions between TypeScript and
  C#/Kotlin/Python/Rust. The bridge delays one packet below the transport, sends
  the following packet first, and then releases the held packet; delivered frames
  still surface in sequence.
- C#, TypeScript, Rust, Python, and Kotlin now also guard against control-packet
  sequence bleed in ordered channels: a received `ack`/control packet may
  advance the global acknowledgement window, but it must not block the next
  ordered `schema` frame for that channel. Runtime-local RUDP tests prove the
  data-control-data sequence in each implementation.
- TypeScript, C#, Rust, Python, and Kotlin now share the first RUDP
  fragmentation implementation: the session can split oversized reliable
  ordered payloads into fragment packets, reassemble them before delivery, and
  the socket transport can carry fragmented `schema` frames when
  `maxFragmentBytes` is configured.
- The TypeScript interop harness now proves fragmented reliable ordered
  `schema` frames in both request and response direction between TypeScript and
  C#/Kotlin/Python/Rust one-shot UDP peers.
- TypeScript, C#, Rust, Python, and Kotlin now share a transport-level RUDP
  disconnect primitive: sessions create and consume `disconnect` packets with
  raw reason bytes, socket transports surface the remote reason, and the
  TypeScript interop harness proves reason propagation from C#/Kotlin/Python/Rust
  one-shot UDP peers.
- TypeScript, C#, Rust, Python, and Kotlin now share transport-level ping/pong
  and receive-timeout state: sessions create pings, answer pings with pongs,
  expose pong payloads, and mark themselves disconnected after a configured
  silent receive window. The TypeScript interop harness proves ping/pong payload
  echo against C#/Kotlin/Python/Rust UDP peers; local runtime tests prove timeout
  state transitions.
- TypeScript, C#, Rust, Python, and Kotlin now share bounded reliable-send
  backpressure for RUDP sessions. `maxPendingReliablePackets` is advertised on
  RUDP transport-profile channels and enforced by the session before reliable
  connect, accept, single-packet sends, or fragmented sends enqueue anything;
  TCP-framed profiles do not publish the field because they do not own the RUDP
  pending-reliable queue.
- TypeScript, C#, Rust, Python, and Kotlin now share a portable
  `cultnet.reconnect_policy.v0` document and deterministic exponential delay
  helper, and RUDP transport profiles advertise that policy under
  `reconnectPolicy`. This gives discovery the same policy language for
  reconnect posture.
- TypeScript, C#, Rust, Python, and Kotlin now share a portable reconnect
  controller shape over that policy: failed connection attempts produce the
  same attempt number, delay, next-attempt time, exhaustion signal, and
  successful-connection reset semantics. This is the orchestration primitive
  service loops must use; it does not yet claim every production daemon or
  socket path automatically redials through it.
- The C# LiteNetLib `Client` reconnect loop now consumes
  `CultNetReconnectController` directly instead of owning a private attempt
  counter and duplicate backoff constants. It still owns LiteNetLib socket
  mechanics and UI-facing reconnect state; the controller owns retry attempt
  scheduling.
- C# now has an `ICultNetSchemaClient` service-client port for schema-v0
  request/response bodies. Verse discovery, peer exchange, shard-log fetch,
  shard-snapshot fetch, and shard write forwarding clients consume the port and
  choose the native RUDP schema client for `rudp://` endpoints while preserving
  LiteNetLib for `cultnet://` endpoints. This moves transport choice below
  service payload ownership without claiming every daemon uses native RUDP yet.
- C# now also has a poll-driven `CultNetRudpReconnectLoop` for the native RUDP
  socket transport. Caller-owned game/service loops report closure, ask
  `ReconnectIfDue(nowMs)` inside their own scheduler, and the shared controller
  owns retry delay/exhaustion state while the caller-owned factory opens the
  next `CultNetRudpSocketTransportConnection`.
- C# now has `CultNetRudpSocketTransportServer`, a multi-peer UDP listener that
  demultiplexes remote endpoints into separate RUDP sessions and delivers
  `(peer, frame)` records to higher service owners. This is the production
  server substrate cut: UDP/session ownership is no longer limited to the
  single-peer interop/client helper, but authentication and game-session
  authority still remain above the transport.
- C# `GameCult.Mesh` now mirrors the branded RUDP facade shape used by
  TypeScript, Python, Kotlin, and Rust: it parses `rudp://host:port` endpoints,
  creates RUDP client/server transports from endpoint or peer-card contact
  hints, and can create a RUDP client from the first peer authorized by the
  authority lease catalog for a Verse role. The facade can also return a
  connected RUDP client from a direct endpoint, peer card, or authorized peer
  lookup, so schema-message helpers can ride the RUDP pipe without caller-side
  handshake ceremony. RUDP packet/session behavior remains owned by
  `GameCult.Networking`.
- TypeScript now has a `CultNetRudpReconnectLoop` that consumes the shared
  controller while keeping socket construction caller-owned. A closed RUDP
  transport schedules the next attempt with the portable policy, opens a fresh
  caller-provided transport when the controller says it may retry, and resets
  attempt state when the caller marks the connection established.
- Python now has the same `CultNetRudpReconnectLoop` shape for synchronous
  callers: the caller-owned receive loop reports closure, the shared controller
  owns retry delay/exhaustion state, and the caller-provided factory opens the
  next `CultNetRudpSocketTransportConnection`.
- Python raw CultNet clients now consume a caller-provided
  `CultNetSchemaTransport` factory for request/response and database
  subscription flows. The default factory opens the existing TCP-framed schema
  transport, but the raw client no longer owns inline socket setup as its
  request truth. Python now also exposes `create_rudp_schema_transport(...)`,
  which handshakes a socket-backed RUDP client and gives `CultNetRawClient` the
  same reliable ordered `schema` request path over `cultnet.transport.rudp.v0`;
  `CultMesh.create_client(endpoint="rudp://...")` is the branded ergonomic
  helper for that path.
- Python's branded `CultMesh` facade now creates schema catalogs, built-in
  schema catalogs, and shard catalogs by delegating to the existing
  `cultnet_py` owners, matching the TypeScript/Kotlin/Rust entrypoint pattern
  without creating a second catalog authority. The facade now also parses
  `rudp://host:port` endpoints and creates RUDP client/server transports from
  endpoint or peer-card contact hints while leaving packet/session semantics in
  `cultnet_py`. Python peer catalogs also expose authorized-peer lookup, and
  the branded facade can create a RUDP client from the first peer authorized by
  the authority lease catalog for a Verse role. `CultMeshLocalServer` now also
  starts a RUDP listener beside the TCP-framed listener by default and advertises
  both transport profiles in `cultnet.hello.v0`; the RUDP listener keeps schema
  request handling in the same server authority while demultiplexing per-remote
  `CultNetRudpSession` state. The Python interop peer now also serves and dials
  schema-v0 over `interop-rudp`, advertises TCP-framed and RUDP in discovery and
  hello, and caps interop RUDP schema frames into 1024-byte fragments so catalog
  responses stay inside UDP datagram limits while remaining session-owned.
- Rust now has a poll-driven `CultNetRudpReconnectLoop` for substrate callers:
  service loops report closure, ask `reconnect_if_due(now_ms)` inside their own
  reactor, and the shared controller owns retry delay/exhaustion state while the
  caller-owned factory opens the next socket transport.
- Kotlin now has the same RUDP reconnect-loop shape for JVM/Android callers:
  the app-owned receive loop reports closure, `CultNetRudpReconnectLoop`
  schedules retry through `CultNetReconnectController`, and the caller-provided
  factory opens the next `CultNetRudpSocketTransportConnection`.
- Kotlin now mirrors the C#/TypeScript/Python local authority lease gate:
  peer cards may advertise `authorityLeaseId`, but `CultMeshAuthorityLeaseCatalog`
  owns role/shard/time authorization so discovery contact cannot impersonate
  trust.
- TypeScript CultMesh local catalogs now expose the same practical discovery
  ergonomics as the other runtimes: sorted local views, direct `get(...)`
  lookup, unsubscribe-able `watch(...)` callbacks for Verse and peer catalogs,
  watch callbacks for authority leases and stream declarations/frame cursors,
  plus lease listing and lookup beside the authority check. The branded
  `CultMesh` facade also creates schema catalogs, built-in schema catalogs, and
  shard catalogs by delegating to the existing `cultnet-ts` owners, so callers
  do not have to leave the CultMesh entrypoint for schema discovery or shard
  topology. The facade now also parses `rudp://host:port` endpoints and creates
  RUDP client/server transports from endpoint or peer-card contact hints while
  leaving packet/session semantics in `cultnet-ts`. TypeScript now also mirrors
  Kotlin's authorized-peer lookup and RUDP client helper, so peer cards remain
  contact hints until the authority lease catalog authorizes the requested
  Verse role. The facade can now also create a connected `CultNetPeer` from a
  direct RUDP endpoint, peer card, or authorized peer lookup, so schema and shard
  catalog request helpers ride the RUDP transport without callers hand-wrapping
  the socket transport. The TypeScript interop peer now also serves and dials
  the schema-v0 interop flow over `interop-rudp` while keeping TCP-framed as the
  compatibility lane; discovery and hello responses advertise both profiles.
- Kotlin now has the same full interop RUDP proof shape as the other targeted
  runtimes: the JVM CLI can serve schema-v0 hello/catalog/snapshot/mutation/fire
  over `interop-rudp`, and it can dial a TypeScript full peer over the same
  shared RUDP connection id and reliable ordered `schema` channel. TCP-framed
  remains a compatibility lane for the interop peer; WebSocket remains the
  JVM/Android-friendly client transport surface.
- TypeScript now also has a `CultNetSchemaCatalog` for remote schema
  descriptors plus `CultNetPeer` request/fetch/sync helpers for
  `cultnet.schema_catalog_request.v0` / `cultnet.schema_catalog_response.v0`.
  `CultNetSchemaRegistry` remains the local definition owner; the catalog owns
  imported descriptors so callers do not treat raw response objects as state.
- TypeScript now has the matching `CultNetShardCatalog` and peer
  request/fetch/sync helpers for
  `cultnet.shard_catalog_request.v0` / `cultnet.shard_catalog_response.v0`.
  Shard descriptors can be filtered by schema id and record key without
  requiring callers to inspect raw response maps. The built-in schema registry
  also advertises the shard catalog request/response wire-message schemas, so
  discovery matches the helper surface.
- Kotlin now mirrors the CultMesh stream catalog shape for Android-adjacent
  media, sensor, tensor, and byte streams: stream declaration, clock metadata,
  preferred body transports, consumer negotiation, copy-budget classification,
  max in-flight frame pressure, latest-frame cursors, and watch callbacks for
  stream declarations plus latest-frame publication.
- Kotlin now has a branded `CultMesh` facade matching the C#/TypeScript/Python
  entrypoint pattern for local nodes, schema catalogs, shard catalogs, Verse
  catalogs, peer catalogs, authority lease catalogs, stream catalogs, and RUDP
  client/server construction. Kotlin also parses advertised
  `rudp://host:port` endpoints and can build a RUDP client directly from a peer
  card contact hint or the first peer authorized by the authority lease catalog.
  The facade can now also return a connected RUDP client from a direct endpoint,
  peer card, or authorized peer lookup, so schema-message transport helpers can
  use RUDP without caller-side handshake ceremony. The facade delegates to the
  same owners as the top-level helpers instead of creating a second authority
  path.
- Kotlin WebSocket and RUDP transports now expose catalog fetch/sync helpers for
  schema and shard catalogs. The helpers send standard schema-v0 requests,
  require the matching response schema, and apply through the caller's catalog,
  giving JVM/Android callers the same client ergonomics Python already had
  without making the transport own catalog truth. Both adapters implement the
  shared `CultNetSchemaMessageTransport` shape so the helper logic has one
  owner across Kotlin stream and RUDP paths.
- Kotlin now also has a built-in schema catalog factory on the branded
  `CultMesh` surface. It advertises the Kotlin-supported CultNet/CultMesh
  request and response message descriptors, including schema catalog, shard
  catalog, shard log, Verse catalog, peer exchange, and the shared transport
  profile contract. The branded factory accepts the same schema-id, kind, and
  inline-schema filters as the catalog request path, while still returning
  ordinary `CultNetSchemaCatalog` state. Descriptors for contracts already
  present in the canonical schema set use those schema ids and content hashes;
  Kotlin-local descriptor bodies use schema-version ids until shared schema
  files exist. Transports do not own catalog truth.
- Kotlin authority lease catalogs now also expose watch callbacks, so JVM and
  Android clients can react to lease changes without treating peer-card contact
  hints as trust. Kotlin peer catalogs also expose authorized-peer lookup
  helpers, and the branded `CultMesh` facade can create a RUDP client from the
  first peer authorized for a Verse role while still delegating the trust check
  to the authority lease catalog.
- Rust now has a small `CultMesh` facade for its substrate role: peer cards,
  sorted peer catalogs, authority lease catalogs, schema registry factories,
  shard catalog factories, `rudp://host:port` endpoint parsing, and RUDP
  client/server construction from endpoint, peer-card contact hints, or the
  first peer authorized by the authority lease catalog for a Verse role. The
  facade delegates to the existing Rust schema registry, shard catalog, and RUDP
  socket transport owners; discovery contact remains separate from trust, which
  is still gated by the authority lease catalog. The facade can now also return
  a connected RUDP client from a direct endpoint, peer card, or authorized peer
  lookup. Rust RUDP transports now also send and receive typed
  `cultnet.schema.v0` messages on the reliable ordered `schema` channel, so
  callers do not have to hand-encode MessagePack payloads or hand-roll the
  client handshake before using the shared RUDP pipe.
- Rust now also has substrate-shaped shard catalog helpers:
  `CultNetShardDescriptor`, `CultNetShardCatalog`, request filtering by schema
  id and record key, response creation, response application, and built-in
  schema registry entries for `cultnet.shard_catalog_request.v0` /
  `cultnet.shard_catalog_response.v0`. This gives Rust the same topology
  vocabulary without claiming stream catalog or production game-session server
  ownership.
- The remaining adoption work beyond the current role-scoped parity claim is
  to finish production service-body migration behind the shared transport
  ports, then wire any remaining daemon/socket reconnect loops through the
  shared controller. C# built-in LiteNetLib service wrappers now register
  transport-aware `CultNetServerPeer` listeners instead of requiring each bridge
  to bounce through `NetPeer`, TypeScript exposes a transport-first TCP peer
  helper, C# schema-v0 service clients consume `ICultNetSchemaClient`, and the
  core schema-message RUDP matrix is live in the TypeScript interop gate;
  broader service adoption remains the unclaimed layer.

## RUDP Packet Contract V0

`cultnet.transport.rudp.v0` packets start with a fixed binary header, followed
by a UTF-8 channel id and payload bytes. All integer fields are unsigned
big-endian. The first deterministic fixture is live in TypeScript, C#, Rust,
Python, and Kotlin; every future runtime must port it byte-for-byte before
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
