# CultNet RUDP: Acknowledged means delivered, one watermark owns ordered delivery, and a write belongs to its session

Status: cut map, Imagination pass 2, 2026-09-30, committed by Self. Pass 2 re-maps every cut against
`main` at `3bf1c0c` and applies the operator rulings below.

**Rulings (operator, 2026-09-30).**
- **Q-A1 (a):** Delivered is derived at the sender from a correct receiver's cumulative watermark. The wire
  does not change.
- **Q-A2:** "I think this is telling us we don't actually need both received and delivered."
  - There is one public meaning: Acknowledged means delivered.
  - Receipts go Pending, then Acknowledged (delivered), or Invalidated. No public Received state exists.
  - Retransmission still stops on each packet's own ack, so resending stays bounded.
  - Flush waits for Acknowledged.
- **Scope added from the stray-packet Soul passes 3 and 4:** session generation. A restarted client on the
  same endpoint and payload is locked out. A reconnect on the same transport treats a new server session's
  frames as duplicates. The ending paths disagree about owed writes. One rule: a write belongs to the
  session it was issued in. Pass 2 maps this as **Cut 1b** (the ending rule) and **Cut 1c** (how a new
  generation is recognised), separate from Cut 1 (§8 says why).
- **Open:** Q-A3 (expiring reliable traffic next to ordered traffic), Q-A4 (porting receipts), Q-A5
  (Kotlin), and a new **Q-A6** (how a new generation is recognised; blocks Cut 1c only).

- **Body.** CultLib `main` at `3bf1c0c` ("Merge RUDP stray-packet hardening across all four runtimes").
  Every `file:line` is against it unless it names another revision.
  - Since pass 1's base `d85877a7`, only `rudp.rs` and `rudp.ts` moved among the transport files.
    `CultNetTransport.cs`, `transport.py`, `CultMesh.kt`, `cultmesh-rs`, `cultmesh-ts`, `peer.ts`, the two
    TS publishers and `docs/cultnet-transport-parity.md` are byte-identical to `d85877a7`.
  - `rudp.rs` moved +6 lines from `:283` and +12 from `:596`. `rudp.ts` moved only after `:824`.
  - Odin pins `3bf1c0c` (`Odin crates/odin-core/Cargo.toml:14-17`).
- **Base of every cut.** `main` at `3bf1c0c` or later.
- **Scope.** The RUDP session and socket transports in all five runtimes: C#
  `src/GameCult.Networking/CultNetTransport.cs`, Rust `packages/cultnet-rs/src/rudp.rs` (plus
  `packages/cultmesh-rs`), TypeScript `packages/cultnet-ts/src/rudp.ts` (plus the two hand-rolled
  publishers), Python `packages/cultnet-py/src/cultnet_py/transport.py`, and Kotlin
  `packages/cultmesh-kotlin/src/main/kotlin/org/gamecult/cultmesh/CultMesh.kt`. **Q-A5** decides whether
  Kotlin is in. Until it is ruled, every cut is mapped for four runtimes and Kotlin trails (§ Sequencing).
- **Wire.** This map changes no byte on the wire (§1). Cut 1c changes the default initial sequence, which
  is a value, not a format.


## The ends (operator, 2026-09-30)

- A frame the peer has buffered but not delivered must not look delivered. Today an ordered frame that
  arrives behind a gap is held and acknowledged; if the session ends it is lost, and the sender saw
  "Acknowledged".
- Resending stays bounded. The sender stops retransmitting a packet when the peer acknowledges that packet,
  exactly as today. Delivery-only acknowledgement was rejected: "I don't like the possibility of unbounded
  resending on an unresponsive peer application. TCP sounds like it has the right idea."
- One public meaning: **Acknowledged means delivered** to the peer's application boundary (§2). Unordered
  reliable frames are delivered on receipt. Receipts go Pending, then Acknowledged, or Invalidated.
- A write belongs to the session it was issued in. When that session ends, a write not yet Acknowledged is
  Invalidated. It is never carried into, retransmitted in, or acknowledged by a later session.


## The finding that shapes this map

Self's first proposal put a second acknowledgement on the wire: a per-channel "delivered through N"
watermark beside the received ack. Probing the receivers changed the picture in two ways.

**1. The ordered receivers are broken in every runtime, and the break is bigger than the session-end case.**
A held frame is not only lost at session end. In a live session it can be stranded forever, or dropped,
while the sender sees it acknowledged. The per-channel `next` pointer (`CultNetTransport.cs:918`,
`rudp.rs:196`, `rudp.ts:160`, `transport.py:424`, `CultMesh.kt:2643`) has been patched twice (`436654de`
added the first-use initialisation, `4335ab10` added the skip over other channels' sequences) and is still
wrong:

- **P1: a gap filled by another channel strands the ordered channel.** Ordered `o1`, reliable `u` on
  another channel (lost), `o2`, `o3`. When `u` arrives, only `u`'s channel is drained. `o2` and `o3` are
  never delivered, and the sender's pending set is empty. The same holds with two ordered channels (P1b).
- **P6: a channel first used after other traffic loses frames.** `C1`, `C2` on a new channel, then `X` on
  another. `X` arrives, then `C2`, then `C1`. The channel initialises its `next` from
  `min(highest+1, frame)`, so `C2` is delivered first and `C1` is **dropped as old**
  (`frame.sequence < next`). It was acknowledged.
- Both reproduce at `3bf1c0c` in C#, Rust, TS and Python (pass 2), and did at `d85877a7`, Odin's old pin
  `a8aeddac` and Muninn's pin `c2a9a6e5` (pass 1).

**2. Once the receiver is correct, "delivered" is not new information.** A conforming receiver holds an
ordered frame for exactly one reason: some reliable sequence below it has not arrived. When the last
missing sequence arrives, the same `Receive` call drains the held frames, before the acknowledgement is
built. So:

> An ordered frame is delivered exactly when every reliable sequence up to its last one has been
> acknowledged.

The sender already knows that. It is the sender's own **acknowledged-through** watermark: one below the
lowest sequence that is still pending, queued or abandoned. TCP's cumulative ACK means the same thing,
"every byte below N is in the receive buffer", and it sits beside SACK. Here the sender can compute the
cumulative value itself, because every sequence is acknowledged individually and removed from its pending
set. The operator ruled this (Q-A1 a), and then ruled that the per-packet ack is not a public meaning at
all (Q-A2): it only stops retransmission.

The derivation is sound only if nothing the receiver acknowledged is later dropped while the session
lives, and nothing one session acknowledges is credited to another. The probes found these places where
that fails:

| # | Where an acknowledged frame is lost, or credited to the wrong session | Runtimes | Fixed by |
|---|---|---|---|
| P1, P1b | A cross-channel gap strands the ordered channel | all five | Cut 1 |
| P6 | Late channel initialisation drops the earlier frame | all five | Cut 1 |
| P5 | A reliable fragment set is evicted after its fragments were acknowledged | Rust, TS, Python (C# and Kotlin have no eviction) | Cut 2 |
| P8 | A retransmit more than 4,096 sequences behind the receiver's highest counts as a duplicate: acknowledged and dropped | all five | Cut 3 (the sender never lets its span reach it) |
| G2 | After `connect()` again on the same transport, a new server session's frames are duplicates of the old one's sequences: acknowledged and dropped | C#, Rust, TS, Python | Cut 1c |
| G3 | A write left owed when a session ends is retransmitted into the next session and delivered there, and in Rust its receipt reads `Acknowledged` | Rust (local `disconnect()`, timeout); C#, TS, Python (every ending) | Cut 1b |

And places where the sender loses track, or is locked out, without the receiver's help:

| # | Sender-side lie or lockout | Runtime | Fixed by |
|---|---|---|---|
| G1 | A restarted client on the same endpoint and Connect payload gets an Ack, never an Accept, and stays unconnected | Rust hub, C# listener and C# server mode, TS and Python server mode (Rust server mode resets on every Connect instead) | Cut 1c |
| G1b | C# and Python server-mode transports drop a Connect from any endpoint but the first, so a client that restarts on a new port is locked out for good | C#, Python | Cut 1c |
| P2 | An expired reliable packet leaves a permanent hole. Ordered frames after it are held forever, and `flush_reliable` succeeds (outstanding 0) | Rust (the only runtime with reliable expiry) | Cut 4 reports it honestly; **Q-A3** decides the real fix |
| P9 | A receipt for an expiring send reads `Acknowledged` once the packet expires unacknowledged | Rust | Cut 4 |
| P10 | `send_reliable` with a full window queues the frame, then returns an error ("receipts require a non-empty reliable packet set"), so a caller that retries sends it twice | Rust | Cut 4 |


## Probe evidence

Everything ran on Yggdrasil through `ygg-verify.sh`, from local-only probe branches in throwaway
worktrees. Each probe commit survives on the Yggdrasil mirror as `refs/verify/<sha>`.

Pass 1 (base `d85877a7` and the pins; mirror `CultLib-imag-ack`):

- `de2bdfe8`, `09318d13`, `03eac6ff`: Rust session and socket probes (`packages/cultnet-rs/tests/probe_ack.rs`).
- `b70a69ca`, `2320a4e5`, `7fe5d726`: the C#, TS and Python probes (`ProbeAckTests.cs`,
  `probe-ack.test.ts`, `probe_ack.py`).
- `f0e7136c`: the Rust probes at Odin's old pin `a8aeddac`. `bf8fc826`: all four at Muninn's pin `c2a9a6e5`.
- `db7d2886`: **the prototype**, the Rust receiver rebuilt on one watermark plus sender-derived receipt
  states (about +130/−125 in `rudp.rs`). Its `Received`/`Delivered` receipt states are superseded by Q-A2;
  its receiver is what Cut 1 builds.

Pass 2 (base `3bf1c0c`; mirror `CultLib-imag-ack2`):

- `a383e27d`, then `1519a20a` (the C# server-mode G1 probe acknowledges the Accept before the restart, so
  it tests a restart and not a lost Accept): the pass-1 probe files restored unchanged, plus
  `packages/cultnet-rs/tests/probe_gen.rs`, `packages/cultnet-ts/test/probe-gen.test.ts`,
  `packages/cultnet-py/tests/probe_gen.py` and `tests/GameCult.Networking.Tests/ProbeGenTests.cs`.
- Rust and C# G1 run over real loopback UDP (hub, listener, server-mode transport), with the restarted
  client bound to the first client's exact address. Python G1 runs over UDP against the server-mode
  transport. TS G1 calls `acceptConnect` on the connected session, which is what the socket transport does
  for a same-endpoint Connect (`rudp.ts:1034-1049` calls it without a reset, and `rudp.ts:249-251` answers
  it as a repeat). G2 and G3 are session-level: the socket transports' `connect()` is `create_connect` and
  nothing else (`rudp.rs:1874-1883`, `CultNetTransport.cs:1836-1846`, `rudp.ts:891-897`,
  `transport.py:994-998`).

| # | Probe | Pass 1 (`d85877a7`, pins) | Pass 2 (`3bf1c0c`) | Prototype `db7d2886` |
|---|---|---|---|---|
| P1 | `o1`, reliable `u` lost, `o2`, `o3`; then `u`, then `o3` | Delivered `[o1, u]`; `o2`, `o3` never; sender pending `[]`. Rust, C#, TS, Python; same at both pins | Same, all four | `[o1, u, o2, o3]` |
| P1b | `A1`, `B1` (second ordered channel) lost, `A2`, `A3` | `[A1, B1]`, all four | Same, all four | `[A1, B1, A2, A3]` |
| P6 | `C1`, `C2` on a new channel, then `X`; arrival `X`, `C2`, `C1` | `[C2]`; `C1` dropped. All four | Same, all four | `[C1, C2, X]` |
| P2 | Rust: reliable media with expiry 1 ms, lost; then `s1`, `s2` on `schema` | Nothing delivered. Sender outstanding 0, expired 1, no resends | Same | Unchanged (held behind the hole). Cut 4 makes the sender say so |
| P3 | `s1` lost, `s2` arrives and is acknowledged | `s2` not delivered, but acknowledged: sender pending `[s1]` only | Same | Receipt for `s2` stays short of delivered until `s1` arrives |
| P4 | Rust: `s0` delivered, `s1` lost, then a 5 MiB ordered frame in 60 kB fragments | `RUDP ordered hold buffer is full`: the session is refused. With no gap, delivered | Same | The prototype held every ordered frame, so a 5 MiB frame was refused **even with no gap**: hold only what is not yet deliverable (Cut 1) |
| P5 | 3-fragment reliable frame, 2 fragments acked; 70 lossy fragment sets; last fragment arrives | Rust, TS, Python: never delivered, sender pending `[]` | Same, Rust, TS, Python | Unchanged (Cut 2) |
| P7 | An Ack with flag bit `0x10`, a nonzero sequence field, or a 5-byte payload | Decoded and applied in Rust, C#, TS, Python | Same, all four | n/a |
| P8 | Reliable `g` lost while 4,200+ later sequences are sent and acked; then `g` arrives | `g` not delivered, and acknowledged. All four | Same, all four | Unchanged (Cut 3) |
| P9 | Rust socket: `send_reliable("media")` with expiry 1 ms, never read | `Pending`, then `Acknowledged` | Same | `Pending` |
| P10 | Rust socket: 40 `schema` sends, then `send_reliable` | Error "receipts require a non-empty reliable packet set", after the frame was queued | Same (the probe's `?` fails on that error) | Not addressed (Cut 4) |
| G1 | Client A connects, sends one ordered frame (acknowledging the Accept), and exits. Client B, a fresh session with the same initial sequence, binds A's exact address and sends the same Connect payload five times | n/a | **Locked out.** Rust hub (payload `"svc"` and empty): 5 Acks, 0 Accepts, B unconnected, the hub still holds A's generation. C# listener: same, no `PeerDisconnected`. C# server mode: 5 Acks. Python server mode: 5 Acks. TS session: the answer is an Ack. Rust server mode (control): B gets an Accept and its frame is delivered, because it resets on every Connect | Not probed |
| G1b | C# and Python server mode: a third client on a new port sends a Connect | n/a | No reply; the datagram is counted dropped | Not probed |
| G2 | Client connects to server 1 (initial sequence 1) and receives `old1..3`. Server 1 is gone; the client connects again on the same session to server 2 (initial sequence 1), which sends `new1`, `new2` | n/a | **`new1`, `new2` never delivered, and acknowledged** (server 2's pending set empties). C#, Rust, TS, Python | Not probed |
| G3 | Client sends one ordered write that is lost; the session ends; the client connects to a new server; the client's resends run | n/a | Owed after the end, and **delivered in the next session**: C#, TS, Python on peer Disconnect, local disconnect and timeout; Rust on local disconnect and timeout. Rust on peer Disconnect: owed 0, nothing delivered (`5f1e883d`) | Not probed |
| G3R | Rust socket: `send_reliable`, the write is lost, `disconnect()`, `connect()` on the same transport to a new peer | n/a | Receipt `Pending` after the disconnect; the write is delivered to the new peer; **receipt `Acknowledged`** (the Soul R1 shape) | Not probed |

**Suites on the prototype `db7d2886`** (pass 1; baseline `03eac6ff` was all green):

- `cultnet-rs`: 110 + 4 + 24 pass; `tests/cultnet.rs` 51 pass and 2 fail. Both failures are test shape,
  not mechanism:
  - `rudp_session_suppresses_duplicates_and_delivers_reliable_ordered_payloads_in_sequence`
    (`tests/cultnet.rs:1000`) hands the receiver an Accept with sequence 91 from a sender whose data starts
    at 1. That handshake is impossible. Under a watermark seeded by the Accept, every data sequence is
    "already below" it. The test must use a consistent handshake.
  - `rudp_ordered_channel_tracking_is_bounded` (`:2655`) sends 256 ordered channels with no gap. The
    prototype keeps state only for channels with held frames, so nothing is refused. The bound becomes
    "channels with held frames", and the test must hold frames behind a gap.
- `cultmesh-rs` did not compile only because the prototype added a status variant. Under Q-A2 the status
  enum keeps its three variants, so that break does not recur.

**Wire compatibility (P7).** Every decoder ignores flag bits 4-7 and byte 35. Every session ignores an
Ack's sequence field and payload (`CultNetTransport.cs:1177`, `rudp.rs:578-592`, `rudp.ts:397`,
`transport.py:612`, `CultMesh.kt:2738`). Every decoder rejects an unknown packet type
(`CultNetTransport.cs` `Enum.IsDefined`, `rudp.rs` `packet_type_from_code`, `rudp.ts:1313`,
`transport.py:1212`, `CultMesh.kt` `fromCode`) and an unknown version byte. So:

- An additive watermark would fit in an Ack: flag bit 4 set, the value in the Ack's unused sequence field,
  zero extra bytes. Old peers apply the ack and ignore the rest. Q-A1 ruled it out; it stays the known
  place to put a FORWARD-TSN notice if Q-A3 is ever B.
- A new packet type or a version bump is not additive: old peers refuse the datagram and count it dropped.
- No capability negotiation is needed under this map, because nothing on the wire changes.


## Authority map

- **Owner of ordered delivery (receiver): the session's contiguous received-through watermark `R`.**
  - Every reliable sequence up to `R` has been received since the peer state was last reset.
  - A held ordered frame whose first sequence is at most `R + 1` is delivered, in sequence order, in the
    same call that advanced `R`.
  - `R` is seeded by the handshake. `AcceptConnect` seeds it with the Connect's sequence, and receiving
    the Accept seeds it with the Accept's sequence. A session connected without a handshake (Rust
    `assume_connected`, `rudp.rs:243`, test-only today) seeds it from its first reliable packet.
  - It advances in `RememberReceived` and resets with the rest of the peer state (`ResetPeerState`).
- **Owner of retransmission (sender): the pending and queued sets**, as today. A sequence leaves them when
  an ack names it. Retransmission stops there. This is not a public meaning.
- **Owner of Acknowledged (sender): the acknowledged-through watermark `A`**, one below the lowest sequence
  that is pending, queued or abandoned in the current generation. An ordered send is Acknowledged when `A`
  reaches its last sequence. A reliable unordered send is Acknowledged when every one of its sequences is
  acked.
- **Owner of the session boundary: the session's generation.** One primitive ends a generation: the
  session stops being connected, every write not yet Acknowledged is Invalidated and dropped from pending
  and queued, and the generation counter advances. Every ending path calls it: a peer's Disconnect, a
  local disconnect, a timeout and a refused packet (Cut 1b), and from Cut 1c a client's `connect()` on a
  session that already had a peer, and a server accepting a Connect that does not repeat the current one.
  - What was learned from the peer (`R`, the received set, held frames) is forgotten only where both sides
    start fresh: a refusal (as today) and, from Cut 1c, a non-repeated handshake. Forgetting it at a
    one-sided ending (a timeout the peer did not see) would let the peer's retransmits be delivered twice.
- **Owner of "is this Connect a repeat": the session**, from the Connect's sequence (Cut 1c; Q-A6).
- **Inputs.** Received sequences and handshake packets (receiver); acks, expiry and ending events
  (sender). Nothing else.
- **Outputs.** Delivered frames, in order (receiver). Receipt status and flush completion (sender).
- **Derived, not owners.**
  - The per-channel `next` map is **deleted**, not demoted: `_orderedNextSequenceByChannel`
    (`CultNetTransport.cs:918`), `ordered_next_sequence_by_channel` (`rudp.rs:196`),
    `#orderedNextSequenceByChannel` (`rudp.ts:160`), `_ordered_next_sequence_by_channel`
    (`transport.py:424`), `orderedNextSequenceByChannel` (`CultMesh.kt:2643`).
  - `expectedSequenceIfUninitialized` and its equivalents are deleted (`CultNetTransport.cs:1151`,
    `rudp.rs:540`, `rudp.ts:375`, `transport.py:595`, `CultMesh.kt:2728`).
  - `PendingOrderedFrame.NextSequence` is deleted (`rudp.rs:160-163` and equivalents).
  - `OutstandingReliablePacketCount` stays, and means "not yet acked in this generation". It is a
    retransmission count, not a delivery claim.
  - Rust `session_scope: Uuid` (`rudp.rs:135`, `:179`, `:206`, rotated at `:345` and `:601`) is no longer an
    owner. It is replaced by the generation (Cut 1b) and then by receipts resolved at their source
    (Cut 4).
  - TS `#generation`, `#endedGeneration` and `#endGeneration` in the socket transport (`rudp.ts:830-831`,
    `:937-940`) are no longer owners; the session's generation replaces them.
  - `DisconnectReason` / `disconnect_reason` / `#endedReason` are report-only: why the last generation
    ended. They no longer gate flush.
- **Forbidden writers.**
  - No path delivers an ordered frame except the drain after `R` advances.
  - No receive path drops a frame whose sequence it acknowledged while the generation lives.
  - No sender path reports Acknowledged except through `A` (or the unordered rule).
  - No ending path leaves a write owed. No path retransmits a write into a generation after the one it was
    issued in.
  - No transport decides whether a Connect repeats: not by payload (Rust hub `rudp.rs:1637-1647`, C#
    listener `CultNetTransport.cs:2670-2688`), not by `Connected` (C# `:2226-2236`, Python
    `transport.py:1047-1053`, TS `rudp.ts:249-251`, `:1031-1049`), and not by resetting on every Connect
    (Rust server mode `rudp.rs:2105-2116`).
- **Shared paths.** Accept, reliable data, fragments, duplicates and retransmits all advance `R` through
  `RememberReceived` and then call the drain. Direct sends, fragmented sends, queued promotion and resends
  all feed the pending and queued sets that `A` reads. Every ending path goes through the one generation
  primitive.
- **Deletion line.** `DeliverOrdered`, `DrainOrdered` and `SkipReceivedNonChannelSequences` and their
  equivalents go before the drain is added: `CultNetTransport.cs:1628-1700`, `rudp.rs:1138-1246`,
  `rudp.ts:737-800`, `transport.py:868-920`, `CultMesh.kt:2939-2978`. The transport-level repeat decisions
  above go before the session's is added.


## The design, question by question

### 1. Where Acknowledged comes from (Q-A1 a, ruled)

- Derived at the sender from `A` (§ The finding). No wire change.
- **Why it is sound after Cuts 1-3.** A conforming receiver:
  - drops nothing it acknowledged while the session lives (P1, P6: Cut 1; P5: Cut 2; P8: made unreachable
    by Cut 3);
  - holds an ordered frame only while a sequence below it is missing (Cut 1);
  - drains held frames in the call that fills the last gap, before it acknowledges (Cut 1).
  - So every sequence up to `s` acked means `s` was delivered.
- **And after Cuts 1b and 1c,** an ack can only be credited to the generation that issued the write
  (G3, G2).
- **Mixed versions.** A new sender with an old receiver can be wrong only when that receiver strands a
  frame (P1, P6). That needs reliable traffic on more than one channel of one session. The C#, TS, Python
  and Kotlin socket transports send reliably only on `schema` (`CultNetTransport.cs:2372-2376`,
  `rudp.ts:1254-1262`), so they cannot trigger it. Rust sends reliable `media`/`audio` too
  (`rudp.rs:2523-2562` `channel_send_options`). The pass-1 Eyes survey found no live session that mixes
  those with `schema`: Muninn's media session carries no `schema` traffic, and neither does Sleipnir's HID
  stream. The old receiver loses such frames today in any case. Bumping the pin is the fix.
- **Every Odin-bound publisher's receiver is Odin.** Acknowledged means delivered for Idunn health,
  presence and catalog publishing only once Odin pins a CultLib with Cuts 1-3.

### 2. What "delivered to the application" means

- **Delivered means the frame left the session's hold and was handed to the transport's application
  boundary.** That boundary is:
  - C#: enqueued in the socket transport's `_deliveredFrames` (read by `TryReceiveOnce`), or returned in
    `CultNetRudpReceiveResult.Delivered` from a bare session;
  - Rust: pushed to `delivered_frames` (`rudp.rs:2147`) or the hub's `pending_events`, or handed to the
    document server's sink (`cultmesh-rs/src/rudp_document_server.rs:343-360`), which acknowledges only
    after the sink accepted;
  - TS: the `"frame"` event was emitted (`rudp.ts:1076-1082`);
  - Python and Kotlin: the delivered queue.
- **It is not "consumed".** The operator rejected application-driven acknowledgement, and TCP's cumulative
  ACK has the same boundary: in the receive buffer, readable, not read.
- A listener or sink that throws or rejects has still been delivered to. The handler-rejection paths from
  the stray-packet work end the session, which is the application's answer, not the transport's.

### 3. The public API (Q-A2, ruled: one meaning)

- **Rust receipts** (the only runtime that has them):
  - `CultNetRudpReliableSendStatus` keeps its three variants, `Pending`, `Acknowledged`, `Invalidated`
    (`rudp.rs:146-150`). No variant is added, removed or renamed. `Acknowledged` changes meaning from
    "every packet was acked" to "delivered".
    - The visible difference: an ordered send behind a missing sequence reads `Pending` until the gap
      fills (P3), where today it reads `Acknowledged`.
    - Every external caller already means delivered, so none changes code: `cultmesh-rs/src/lib.rs:832`
      and `:892`, `tests/rudp_stray_packets.rs:309`, `cultmesh-rs/tests/rudp_document_server.rs:408,415`,
      CodexConnector `src/idunn_health.rs:727-745`, Ghostlight
      `crates/ghostlight-dungeon/src/idunn_health.rs:1126-1145`.
  - **A receipt is resolved once, by its session.** It becomes `Acknowledged` when `A` passes its last
    sequence (ordered) or when all its sequences are acked (unordered). It becomes `Invalidated` when its
    generation ends first. After that it never changes.
    - Mechanism: the receipt carries its sequences, whether it was ordered, and a shared status cell
      (`Arc<AtomicU8>`). The session keeps the unresolved cells, at most one per outstanding send, and
      writes each once. `reliable_send_status(&receipt)` (`rudp.rs:1931-1936`) reads the cell; its
      signature does not change.
    - This deletes the session scope (`rudp.rs:135`, `:179`, `:206`, `:283-285`, `:291-293`, `:345`,
      `:601`) and needs no history of ended generations. Today a delivered write reads `Invalidated` after
      any reset, because the scope rotates (`rudp.rs:291-293`). The pass-1 design needed a frozen snapshot
      of one ended epoch and still misreported older ones.
    - `PartialEq` on the receipt compares its sequences, which are unique within a session object because
      issued sequences survive resets.
  - `send_reliable` receipts cover queued packets (P10). The receipt lists every sequence the send
    assigned, not only the ones admitted to the wire (`send_many`, `rudp.rs:456-528`, returns only the
    admitted ones; `non_expiring_reliable_receipt`, `:305-318`, refuses an empty set).
  - `send_reliable` refuses a channel with reliable expiry (P9). The comment at `rudp.rs:1915-1916` says
    this lineage has no expiry, which is false since `bac5be88`. Restore the guard it describes.
  - New: `CultNetRudpSocketTransportConnection::wait_acknowledged(&receipt, timeout) -> Result<()>`. It is
    the ack wait: it pumps receive and resends like `flush_reliable` and returns when the receipt is
    `Acknowledged`. It fails at once when the receipt is `Invalidated`, naming the end reason. It replaces
    the hand-rolled loops at `cultmesh-rs/src/lib.rs:806-838` and `:870-900`; CodexConnector and
    Ghostlight may adopt it at their next bump, and their existing polling stays correct without it.
- **Flush: one wait, and it means every send of this generation is Acknowledged.**
  - A flush belongs to the generation it started in. It fails when that generation ends, whatever comes
    after. Rust binds it to the scope today (`rudp.rs:2001-2014`) and TS to its transport generation
    (`rudp.ts:911-935`). C# (`CultNetTransport.cs:1914-1931`) and Python (`transport.py:1101-1124`) check
    only the disconnect reason, which the next Connect clears (`CultNetTransport.cs:1843`, `:2235`;
    `transport.py:997`, `:1054`). Once Cut 1b drops owed writes at every ending, "outstanding is 0" after a
    reconnect would read as success, so every runtime binds flush to the session's generation in Cut 1b.
  - When every sent sequence is acked, `A` is the highest sequence sent, so every send is Acknowledged.
    The only exception is behind an abandoned sequence (Rust expiry). There `flush_reliable`
    (`rudp.rs:1992-2040`) fails at once when an ordered send follows the generation's first abandoned
    sequence: that send can never be Acknowledged, and today flush reports success (P2).
  - C# `FlushReliable`, TS `flush`, Python `flush_reliable` and Kotlin `flushReliable`
    (`CultMesh.kt:3186`) keep their success condition. Their contract text becomes "every reliable frame
    sent in this session has been delivered to the peer's application". These runtimes have no expiry.
- **Defaults.** Plain flush. No parameter. No second flush.
- **Compatibility.** No signature changes in any runtime except additions (`wait_acknowledged`; TS
  `connectAndWait`, §4). The Rust enum is unchanged. No downstream caller has to change code at its next
  bump.

### 4. Consumers, each assigned

Surveyed across `F:\Projects` in pass 1; the rows that moved are re-checked at the revision named. Every
consumer that waits wants delivered, which is now just Acknowledged.

| Consumer | What it waits on today | Change |
|---|---|---|
| `cultmesh-rs/src/lib.rs:781-838` `request_raw_snapshot_from_rudp_catalog` (Idunn `src/drivers.rs:5527` uses it) | a hand-rolled loop until `Acknowledged`, then the response | Cut 4: `wait_acknowledged` |
| `cultmesh-rs/src/lib.rs:870-900` `publish_cultnet_message_to_rudp_catalog` (Odin `odin-daemon`) | a hand-rolled loop until `Acknowledged`, then disconnect. The doc at `:869` claims "application admission" | Cut 4: `wait_acknowledged`; the doc says delivered to the document server's sink (`rudp_document_server.rs:343-372`) |
| `cultmesh-rs/src/lib.rs:918-938` `publish_cultnet_messages_to_rudp_catalog` (Ghostlight `mesh.rs:323`) | `flush_reliable`, then drop. **Several ordered frames, then close** | None; flush now means it |
| `cultmesh-ts/src/index.ts:6070-6098` `publishRudpDocumentOnce` (Heimdall, StreamPixels, Stonks, weksa, Bifrost, VoidBot, Hermodr) | `peer.flush` (`cultnet-ts/src/peer.ts:109-110`), then close | None. Cut 5 moves its connect-and-wait helper (`index.ts:6603-6650`) into cultnet-ts |
| `cultnet-ts/src/idunn-odin-presence-publisher.ts:89-215` `publishDocument`, behind `createIdunnRuntimePresencePublisher`. **StreamPixels' service runs this**: `apps/service/scripts/idunn-entry.mjs` calls `createIdunnRuntimePresencePublisher` from its vendored CultLib `30ee8b9`, where the file is byte-identical to `3bf1c0c` | a hand-rolled session loop (`:186-208`) until `outstandingReliablePacketCount === 0`. It does resend (`:205`). It drops `readyToSend` (`:198`), so promoted queued packets wait for the resend timer. It pins `initialSequence: 1` (`:103`) and never sends a Disconnect, so Odin's hub keeps each publish's peer until it times out (each publish binds a new port, so each is a new peer) | Cut 5: the socket transport, `connectAndWait`, `send("schema")`, `flush`, `close` (which sends the Disconnect, `rudp.ts:956-969`) |
| `cultnet-ts/src/signed-daemon-health.ts:104-162` `publishSignedDaemonHealth` (Hermodr, Odin `idunn-rudp.cjs`) | a hand-rolled loop: sends the packets `sendMany` admitted, waits up to 500 ms for the **first** Ack packet of any content (`:146-153`), swallows the timeout, closes. Resends run inside that wait (`:303`) but promoted queued packets are never sent | Cut 5, as above. Today a write not acked within 500 ms, or any payload over 32 packets, is reported published |
| `cultnet-ts/src/operation-service.ts` | the operation response | none (the response is the proof) |
| C# `FlushReliable` callers: Gjallar `Program.cs:3055-3080` (two ordered frames, flush, dispose) | flush | None |
| Heimdall `src/idunn-rudp-health.ts:55-87` (vendored CultLib `b6b1d9c`, Heimdall `d5acc94`) | the first Ack packet | Heimdall's own cut at its bump: call cultnet-ts's publisher after Cut 5 |
| CodexConnector `idunn_health.rs:727-745`, Ghostlight `idunn_health.rs:1126-1145` | `Acknowledged` | None required. Optional `wait_acknowledged` at their next bump |
| Muninn `main.rs:9446-9458`, Sleipnir `main.rs:880-904` | nothing (send and drop) | Follow-up: fire-and-forget health can be silently lost |
| Kotlin `flushReliable` | no callers | Contract text only |

### 5. The hold buffer and the 4 MiB cap

- **It belongs here**, because the held frame is exactly the state this map names.
- **Mechanism (P4).** Rust caps held ordered frames at 1,024 frames and 4 MiB (`rudp.rs:47-48`, checked
  at `:1181-1187`). A refusal ends the session. The default payload limit is 16 MiB (`rudp.rs:33`). With
  one lost packet under a large ordered transfer, a legal 5 MiB frame is refused and kills the session.
  - The sender's window counts pending packets, not span or bytes (`rudp.rs:910-942`). While one packet
    stays lost, acked packets keep freeing slots, so the held data behind the gap grows without limit.
- **Fix: a sender-side flow window, TCP's receive window without a wire field** (Cut 3). A reliable
  packet is admitted to the wire only while:
  - its sequence is at most 1,023 above the lowest unacked sequence; and
  - the payload bytes of sequences above the lowest unacked one stay within 4 MiB, with at least one
    packet always admissible.
  - Otherwise it waits in the existing queue.
- **What this buys:**
  - a conforming sender never makes a Rust receiver hold more than 4 MiB, so the cap stops refusing legal
    traffic;
  - a conforming sender never gets 1,024 sequences ahead of the receiver's highest, so the Rust far-ahead
    refusal (`rudp.rs:46`, checked at `:626-630`) never drops its packets;
  - a conforming sender never leaves a sequence 4,096 behind the receiver's highest, so P8 cannot happen.
    The receive-history window stays 4,096 in every runtime (`CultNetTransport.cs:884`, `rudp.rs:35`,
    `rudp.ts:20`, `transport.py:404`, `CultMesh.kt:2625`).
- **What is kept.** The cap and the far-ahead refusal stay. They protect against hostile peers.
- **Rejected:** raising the cap to the payload limit or above (16-64 MiB per hostile session instead of
  4); lowering the ordered payload limit to 4 MiB (an API regression for large snapshots).
- **Cost.** Normal flow never has a span beyond the 32-packet window, so the new limits bind only while a
  packet is lost.
- **Old senders** (the pinned and vendored ones) can still trip the Rust cap until they bump.
- **Not in scope.** C#, TS, Python and Kotlin receivers have no hold cap. Follow-up.

### 6. Reliable expiry (P2)

- Only Rust has it (`rudp.rs:39`, `:898-908`). An expired packet's sequence never reaches the receiver.
  `R` stops below it, and so did the old `next` pointer. Ordered frames after it are held for the rest of
  the session.
- **Today's users.** Only Muninn's reliable `audio`, and its session carries no ordered traffic.
  `hands/media-fec-cut3` (unmerged, `4b85a173`) deletes the `audio` profile entry and the `"audio"` send
  arm. The `media` channel keeps a caller-chosen reliable-with-expiry mode.
- **This map makes it honest (Cut 4):**
  - the sender records its generation's first abandoned sequence;
  - `A` never passes it;
  - ordered receipts after it stay `Pending`, and become `Invalidated` when the generation ends;
  - `flush_reliable` fails at once.
- **The real fix is a fork (Q-A3).** The literature's answer is PR-SCTP's FORWARD-TSN (RFC 3758): the
  sender tells the receiver to move its cumulative point past abandoned sequences.

### 7. Interaction with the merged stray-packet work

The ack cuts build on these behaviours and do not alter them:

- A refusing session ends, and its goodbye is built after the reset (`EndRefused`
  `CultNetTransport.cs:1315-1319`, `end_refused_session` `rudp.rs:364-367`). The refused sequence is never
  acked, so a refusal never becomes a false Acknowledged.
- Issued sequences survive a reset in every runtime (`rudp.rs:341-343`, `CultNetTransport.cs:1292-1308`,
  `rudp.ts:214-227`, `transport.py:458-471`). Cut 1c relies on it: a client's second Connect always
  carries a new sequence.
- One Accept per generation, and a repeated Connect answered with the Accept still owed, or an Ack. Cut 1c
  keeps the answer and moves the decision of what repeats into the session.
- An ended session fails its flush. Cut 1b generalises the TS and Rust binding to the generation into all
  four runtimes.
- `5f1e883d` made a peer's Disconnect end Rust receipts (`rudp.rs:596-615`) and removed the
  transport-level Pending→Invalidated override. Cut 1b makes the other ending paths agree with it.
- The TS operation service's expiry and handler rejection are above the transport and do not change.

### 8. Session generation: why it is two cuts, and how a new one is recognised

- **Why not inside Cut 1.** Cut 1 is a receiver change with a deletion line: one watermark replaces five
  per-channel state machines. Its falsifiers are P1, P1b and P6. The generation work touches the ending
  paths, the handshake, flush and receipts, and its falsifiers are G1-G3. Both touch `ResetPeerState` and
  the Accept path, which is a merge cost, not a shared owner. Keeping them apart lets Soul falsify each
  against its own probes.
- **Why two cuts.** The ending rule (Cut 1b) is ruled and needs no mechanism choice: every ending path
  drops what it owed. How a new generation is recognised (Cut 1c) needs one (Q-A6). The two are separable
  because 1b does not forget peer state: the peer may not know the session ended, and forgetting what was
  received would let its retransmits be delivered twice.
- **What must hold after both.** A write is Acknowledged or Invalidated within the generation it was
  issued in. A restarted client is admitted. A new server session's frames are delivered after a
  reconnect. A retransmitted Connect is still a repeat.
- **Recognising a new generation (Cut 1c, Q-A6 recommendation A).** The Connect's sequence identifies the
  generation, and the initial sequence is random by default, as TCP's initial sequence number is
  (RFC 9293 §3.4.1, RFC 6528).
  - A Connect repeats exactly when it carries the sequence of the Connect that started the current
    generation. Its retransmits do; a client's second `connect()` cannot, because issued sequences
    survive resets; a restarted process almost never does, because it drew a fresh random initial
    sequence.
  - A stale duplicate of the accepted Connect, delayed in the network, still repeats: same sequence.
  - Anything else ends the current generation and starts a new one, at both ends: the client forgets peer
    state when it sends a Connect on a session that had a peer, and the server when it accepts a Connect
    that does not repeat.
  - A client honours an Accept only while its Connect is pending, and only if the Accept's ack field names
    that Connect. A stale Accept from an earlier generation cannot seed `R`.
  - The initial sequence is drawn from [1, 2^31), which leaves at least 2^31 sequences before the
    exhaustion error (`rudp.rs:944-962`) in every runtime. An explicit `initial_sequence` still works, for
    tests.
  - The Connect payload stays application evidence (the provider layer's `clientSessionId`,
    `cultnet-rs/src/provider_session.rs:70-79`), carried to the application as today. It no longer decides
    anything in the transport.


## Cuts

Every cut is on CultLib, branch `hands/rudp-delivered` from `main` at `3bf1c0c` or later, in a worktree
Self creates. Heavy verification runs on Yggdrasil. Each cut starts by committing its probes as failing
tests named for the rule, in every runtime it touches. The pass-1 and pass-2 probe files
(`refs/verify/1519a20a` on the `CultLib-imag-ack2` mirror) are the starting point; they print, so each
must become an asserting test.

Kotlin: every cut below is mapped for C#, Rust, TS and Python. If Q-A5 is A, a trailing Cut K applies
Cuts 1, 1b, 1c and 3 to `CultMesh.kt` at the anchors given, on the verification host Q-A5 picks.

**Negative checks that stay green in every cut:**

- the cross-runtime reorder, drop and fragment proofs in the TS interop harness
  (`docs/cultnet-transport-parity.md:227-248`);
- the stray-packet tests (`packages/cultnet-rs/tests/rudp_stray_packets.rs`, and their C#, TS and Python
  equivalents), except the ones each cut names as encoding the old rule;
- the codec fixture vectors: no wire byte changes.

### Cut 1. One watermark owns ordered delivery (receiver) — ready

- **First.** Failing tests P1, P1b and P6 in each runtime.
- **Deletes first:**
  - the per-channel `next` map and its resets: `CultNetTransport.cs:918`, `:1305`; `rudp.rs:196`, `:223`,
    `:354`; `rudp.ts:160`, `:223`; `transport.py:424`, `:468`; `CultMesh.kt:2643`;
  - `expectedSequenceIfUninitialized`: `CultNetTransport.cs:1151-1153`, `rudp.rs:540-543` (used at
    `:679`), `rudp.ts:375-377` (used at `:450`), `transport.py:595-599`, `CultMesh.kt:2728`;
  - `DeliverOrdered`, `DrainOrdered` and `SkipReceivedNonChannelSequences`: `CultNetTransport.cs:1628-1700`,
    `rudp.rs:1138-1246`, `rudp.ts:737-800`, `transport.py:868-920`, `CultMesh.kt:2939-2978`;
  - `PendingOrderedFrame.NextSequence` (`rudp.rs:160-163` and equivalents).
- **Adds:**
  - `R`, seeded in `AcceptConnect` (`CultNetTransport.cs:1012`, `rudp.rs:383`, `rudp.ts:242`,
    `transport.py:485`, `CultMesh.kt:2667`) and on Accept receipt (`CultNetTransport.cs:1155`,
    `rudp.rs:544`, `rudp.ts:379`, `transport.py:601`, `CultMesh.kt:2730`);
  - `R` advances in `RememberReceived` (`CultNetTransport.cs:1518`, `rudp.rs:973`, `rudp.ts:619`,
    `transport.py:809`, `CultMesh.kt:2891`) and resets in `ResetPeerState`;
  - a reassembled ordered frame is delivered at once when its first sequence is at most `R + 1` and its
    channel holds nothing below it. Otherwise it is held. The caps apply only to what is held: the
    prototype held everything and so refused a 5 MiB frame with no gap;
  - after every reliable receipt (data, fragment, duplicate, Accept), drain every channel's held frames
    whose first sequence is at most `R + 1`, in sequence order;
  - Rust only: the 64-channel cap (`rudp.rs:42`) counts channels with held frames.
- **Tests changed:** `packages/cultnet-rs/tests/cultnet.rs:1000` (consistent handshake) and `:2655`
  (hold frames behind a gap). The equivalents in the other runtimes are whatever the change breaks. Each
  must be read before it is edited, because a test that encodes `min(highest+1, frame)` encodes P6.
- **Verification:**
  - P1, P1b and P6 pass in every runtime;
  - the full suites: `cargo test` in `cultnet-rs` and `cultmesh-rs`, `dotnet test
    tests/GameCult.Networking.Tests`, `npm test` in `cultnet-ts`, the Python suite;
  - the TS interop harness across runtimes;
  - negative grep: `rg -n "orderedNextSequence|ordered_next_sequence|expectedSequenceIfUninitialized|expected_sequence_if_uninitialized|SkipReceivedNonChannel|skip_received_non_channel" src packages`
    returns nothing;
  - mutation testing on the diff: cargo-mutants, Stryker.NET, StrykerJS, and mutmut for Python.
- **Ledger.** Per runtime about −70 and +40. Across four, about −280 and +160.

### Cut 1b. A write belongs to the session it was issued in: every ending drops what it owed — ready

- **First.** Failing tests G3 (each ending path: peer Disconnect, local disconnect, timeout, refusal) in
  C#, TS and Python, and local disconnect and timeout in Rust; G3R in Rust; and in every runtime, a flush
  started before a timeout or disconnect fails even after a later `connect()` succeeds.
- **The owner.** Each session gets a generation counter and one private primitive that ends the current
  generation: not connected; pending and queued dropped; generation advanced. In Rust it also invalidates
  the generation's receipts (by the scope today; Cut 4 moves that into the receipts). It does not touch
  what was received from the peer (§8).
- **Deletes first:**
  - Rust: the inline clearing in the Disconnect branch (`rudp.rs:596-615`) and the scope rotation in
    `reset_peer_state` (`:345`), both replaced by the primitive; the receipt doc that says local endings
    carry writes (`rudp.rs:128-133`); the flush's scope capture (`:2001-2014`), replaced by the generation;
  - TS: the transport's `#generation`, `#endedGeneration`, `#endGeneration` (`rudp.ts:826-831`,
    `:937-940`) and their uses in `connect` (`:895`), `flush` (`:913-920`), `checkTimeout` (`:949`),
    `#endRefusedSession` (`:976`), the server Connect path (`:1037-1046`) and the disconnect result
    (`:1084`). `#endedReason` stays, as the report of why;
  - C# and Python: flush's reliance on the disconnect reason (`CultNetTransport.cs:1924-1929`,
    `transport.py:1110-1116`). The reason stays, as a report.
- **Adds:**
  - the primitive, called from every ending path:
    - peer Disconnect: `CultNetTransport.cs:1190-1199`, `rudp.rs:596-615`, `rudp.ts:406-414`,
      `transport.py:619-625`;
    - local disconnect: `CreateDisconnect` `CultNetTransport.cs:1324-1328`, `create_disconnect`
      `rudp.rs:771-781`, `createDisconnect` `rudp.ts:508-515`, `create_disconnect` `transport.py:695-697`;
    - timeout, when it fires: `CultNetTransport.cs:1333-1348`, `rudp.rs:783-796`, `rudp.ts:517-526`,
      `transport.py:699-706`;
    - refusal, through `ResetPeerState` (`CultNetTransport.cs:1292-1308`, `rudp.rs:344-358`,
      `rudp.ts:214-227`, `transport.py:458-471`);
  - a read-only generation on each session, which each transport's flush captures at start and fails on
    when it changes (`CultNetTransport.cs:1914-1945`, `rudp.rs:1992-2040`, `rudp.ts:911-935`,
    `transport.py:1101-1124`).
- **Hubs.** The Rust hub and the C# listener already discard a peer's session at its end, so its owed
  writes die with it. No change.
- **Tests changed:** `tests/rudp_stray_packets.rs:638-690` keeps passing unchanged (peer Disconnect already
  obeys the rule). Tests that expect a write to survive a local disconnect or timeout encode the old rule;
  read each before editing.
- **Verification:**
  - G3 in every runtime: after every ending path, owed 0 and nothing delivered to the next session;
  - G3R: the receipt reads `Invalidated` after `disconnect()`, and stays so after reconnecting;
  - the flush test above in every runtime;
  - negative: `rg -n "endedGeneration|#endGeneration" packages/cultnet-ts/src` returns nothing; a TS
    reconnect from inside a `disconnect` listener still cannot make an ended flush succeed (the case the
    TS generation pair was written for, `rudp.ts:826-829`);
  - mutation testing on the diff.
- **Ledger.** About −40 and +45 across four runtimes. TS is net negative.

### Cut 1c. A Connect's sequence says whether it repeats — blocked on Q-A6

Mapped for Q-A6's recommended answer A. Blocked on Cut 1b (it uses the ending primitive).

- **First.** Failing tests G1 (the hub, the C# listener, and server mode in every runtime: a restarted
  client on the same address and payload is admitted and its frames delivered), G1b (C# and Python server
  mode admit a Connect from a new endpoint), G2 (after `connect()` on the same session, a new server
  session's frames are delivered), and a stale Accept (its ack field does not name the pending Connect) is
  ignored.
- **Deletes first:**
  - the transport-level repeat decisions: Rust server mode's reset on every Connect
    (`rudp.rs:2105-2116`); the Rust hub's payload comparison (`rudp.rs:1637-1647`); C# server mode's
    `_session.Connected` branch (`CultNetTransport.cs:2226-2236`); the C# listener's payload comparison
    (`CultNetTransport.cs:2670-2688`); TS's `movedEndpoint`/`repeated` computation (`rudp.ts:1011-1049`)
    and the session's own `connected` shortcut (`rudp.ts:249-251`); Python's `connected` branch
    (`transport.py:1047-1053`);
  - C# and Python server mode's refusal of a Connect from a new endpoint (`CultNetTransport.cs:2212-2216`,
    `transport.py:1042-1044`), for Connect packets only. Rust and TS already admit it.
- **Adds:**
  - the session records the sequence of the Connect that started its generation, and decides: a Connect
    repeats exactly when it is connected and the Connect carries that sequence. `AcceptConnect` answers a
    repeat as `AnswerRepeatedConnect` does today, and otherwise ends the generation, resets peer state and
    accepts (`CultNetTransport.cs:1012-1059`, `rudp.rs:383-435`, `rudp.ts:242-290`,
    `transport.py:485-525`). `AnswerRepeatedConnect` stops being public; a public predicate
    (`ConnectRepeats(packet)`) serves the hubs, which keep building a fresh session for a new generation and
    emit Disconnected for the old one (`rudp.rs:1648-1690`, `CultNetTransport.cs:2690-2710`);
  - `CreateConnect` on a session that has had a peer ends the generation and resets peer state first
    (`CultNetTransport.cs:1001-1010`, `rudp.rs:369-381`, `rudp.ts:229-240`, `transport.py:473-483`);
  - on Accept receipt, the session honours it only while its Connect is pending and only if the Accept's
    ack field names that Connect (`CultNetTransport.cs:1155-1160`, `rudp.rs:544-557`, `rudp.ts:379-390`,
    `transport.py:601-604`);
  - the default initial sequence is random in [1, 2^31): Rust `CultNetRudpSessionOptions::default`
    (`rudp.rs:104-113`), the transport constructors (`:1311`, `:1332`) and hub options (`:1377`); C#
    `CultNetTransport.cs:773`, `:850`, `:2404`; TS `rudp.ts:172`; Python `transport.py:298`, `:323`;
  - CultLib's own fixed `initial_sequence: 1` call sites take the default: `cultmesh-rs/src/lib.rs:964`,
    `:1409`, `cultmesh-rs/src/rudp_document_server.rs:432`, `cultnet-rs/src/cultmesh.rs:259`,
    `cultnet-ts/src/signed-daemon-health.ts:116`.
- **Tests changed** (they encode payload identity or a fixed initial sequence):
  - `packages/cultnet-rs/tests/rudp_server_hub.rs:171-240` calls `connect()` again with the same payload
    and expects no replacement. Under this rule that is a new generation. Its retransmit half is already
    covered by `rudp_stray_packets.rs:487` (the same Connect packet twenty times), which stays;
  - `tests/GameCult.Networking.Tests/NetworkingTests.cs:1507` replaces a peer by payload with two sessions
    at the default initial sequence; it passes under a random default, but must say it relies on distinct
    sequences;
  - `packages/cultnet-ts/test/cultnet.test.ts:2225` (another endpoint replaces the peer) stays;
  - any test that asserts a sequence value with the default initial sequence.
- **Verification:**
  - G1, G1b, G2 and the stale-Accept test in every runtime;
  - the retransmit tests stay green: `rudp_stray_packets.rs:487`, `:511`, `NetworkingTests.cs:1483`,
    `cultnet.test.ts:2119`, `:2134`, `test_cultnet.py:1134`;
  - negative grep: `rg -n "SequenceEqual\(connectPayload\)|connect_payload == packet.payload|movedEndpoint" src packages`
    returns nothing;
  - mutation testing on the diff.
- **Ledger.** About −60 and +70 across four runtimes. Ten call sites lose an explicit initial sequence.

### Cut 2. A reliable fragment set is never evicted (receiver: Rust, TS, Python) — ready

- **First.** Failing test P5 in each of the three.
- **Change:**
  - eviction picks the stalest set that holds no reliable fragment (`rudp.rs:1027-1047`,
    `rudp.ts:679-692`, `transport.py:836-842`);
  - if every pending set holds reliable fragments, the packet is refused and the session ends, as every
    other refusal does;
  - a conforming sender cannot reach that bound: its window keeps at most 32 reliable packets in flight,
    and the bound is 64 sets.
- **Verification:** P5 delivers the reliable frame. `d3bc6e66`'s test (lossy fragment sets keep being
  evicted) stays green. `fragment_sets_evicted` still counts evictions.
- **Ledger.** About −3 and +12 per runtime.

### Cut 3. The sender's flow window: 1,023 sequences and 4 MiB above the lowest unacked — ready

- **First.** Failing tests P8 (all runtimes) and P4 (Rust receiver, with a sender from each runtime).
- **Change.** Admission (`AdmitReliablePackets` / `PromoteQueuedReliable` at
  `CultNetTransport.cs:1439-1490`, `rudp.rs:910-942`, `rudp.ts:586-600`, `transport.py:776-796`,
  `CultMesh.kt:2856-2872`) admits a packet only while:
  - the window has a slot (as today);
  - its sequence is at most the lowest unacked sequence plus 1,023;
  - the admitted payload bytes above the lowest unacked sequence stay within 4 MiB.
  - The first packet is always admissible. Expired packets (Rust) do not count as unacked here: they are
    gone from the wire.
- **Contract.** `docs/cultnet-transport-parity.md` (after `:263`) states the receive window: a receiver
  must hold 1,023 sequences and 4 MiB beyond its contiguous watermark, and a conforming sender never
  exceeds that.
- **Verification:**
  - P8: `g` is delivered;
  - P4: the 5 MiB frame is delivered after the gap fills, and the session is never refused;
  - the existing large-snapshot tests (`c398bad1`) and the fragment interop stay green;
  - the Rust benchmark or media probe shows no throughput change on a lossless link.
- **Ledger.** About +25 per runtime, and nothing removed.

### Cut 4. Rust receipts: Acknowledged means delivered, resolved once; honest expiry and queueing — blocked on Cuts 1 and 1b, and on Q-A3

- **First.** Failing tests: P3 (the receipt for `s2` reads `Pending` until `s1` arrives, then
  `Acknowledged`), P9, P10, P2 (`flush_reliable` must fail), and: a receipt `Acknowledged` before any
  ending stays `Acknowledged` after the ending and after a reconnect; a `Pending` one becomes
  `Invalidated`.
- **Deletes first:**
  - the session scope (`rudp.rs:135`, `:179`, `:206`, `:283-285`, the check at `:291-293`, and what
    Cut 1b left of the rotations);
  - the stale comment and the half guard at `rudp.rs:1915-1920`;
  - the hand-rolled status loops at `cultmesh-rs/src/lib.rs:806-838` and `:870-900`.
- **Adds:**
  - the receipt's status cell and ordered flag; the session's unresolved-receipt list, resolved once
    (Acknowledged through `A`, Invalidated by the ending primitive);
  - `acked_through` (`A`), reading pending, queued and first-abandoned;
  - first-abandoned, recorded in `purge_expired_reliable` (`rudp.rs:898-908`);
  - the receipt covers queued sequences: `send_many` (`:456`) reports every sequence it assigned;
  - `send_reliable` (`:1903-1930`) refuses a channel with expiry;
  - `wait_acknowledged`;
  - `flush_reliable` fails fast when an ordered send follows the first abandoned sequence.
- **Unchanged:** `CultNetRudpReliableSendStatus` (`:146-150`), `reliable_send_status`'s signature
  (`:1931`), and every caller of `Acknowledged`: `tests/rudp_stray_packets.rs:309`,
  `cultmesh-rs/tests/rudp_document_server.rs:408,415`, CodexConnector, Ghostlight.
- **Changes:** `cultmesh-rs/src/lib.rs:832` and `:892` become `wait_acknowledged`; the doc at `:869` says
  delivered to the document server's sink.
- **Verification:**
  - the new tests pass;
  - `rg -n "session_scope" packages/cultnet-rs` has no hit;
  - both suites are green;
  - cargo-mutants on the diff.
- **Ledger.** About −90 (two loops of about 30 each, the scope) and +90 (of which `wait_acknowledged` is
  about 35).

### Cut 5. CultLib-internal TS publishers and the contract text (TS, docs) — blocked on Cut 3, and on Cut 1b in TS

- **Changes:**
  - `idunn-odin-presence-publisher.ts:89-215` and `signed-daemon-health.ts:104-162` (with its helpers
    `:244-252`, `:273-389`, except the exported `parseEndpoint` `:254-271`) send through
    `CultNetRudpSocketTransportConnection`: `connectAndWait`, `send("schema")`, `flush`, `close`. Their
    hand-rolled session loops are deleted. The presence publisher keeps rejecting a `cultnet.error.v0`
    response, from the transport's `frame` events during the flush.
  - `connectAndWait(payload, timeoutMs)` on the TS socket transport, matching C# `ConnectAndWait`
    (`CultNetTransport.cs:1859-1880`). It is `cultmesh-ts/src/index.ts:6603-6650` `waitForRudpConnected` moved
    down; `createRudpPeer` (`index.ts:6220-6245`) uses it and the cultmesh-ts copy is deleted.
  - Flush contract text in all runtimes: "every reliable frame sent in this session has been delivered to
    the peer's application".
  - `docs/cultnet-transport-parity.md:236-240` (the control-packet guard paragraph) is replaced by the
    watermark rule, and `:261-272` gains: Acknowledged means delivered; a write belongs to the session it
    was issued in; how a repeated Connect is recognised (after Cut 1c); the receive window (after Cut 3).
- **Verification:**
  - `runtime-presence-health.test.ts` and `signed-daemon-health.test.ts` stay green;
  - a new test drops the first datagram and still publishes;
  - a new test publishes a payload over 32 packets whole;
  - a new test shows the publisher's peer receives a Disconnect.
- **Ledger.** About −270 and +50.


## Subtraction ledger (estimate, four runtimes)

| Cut | Removed | Added | Other |
|---|---|---|---|
| 1 | ~280 | ~160 | four per-channel state machines become one watermark |
| 1b | ~40 | ~45 | one ending primitive; TS's transport generation pair goes |
| 1c | ~60 | ~70 | four transport-level repeat decisions become one session predicate |
| 2 | ~10 | ~35 | none |
| 3 | 0 | ~100 | a documented receive-window contract |
| 4 | ~90 | ~90 | one public method (`wait_acknowledged`); the session scope goes; no enum change |
| 5 | ~270 | ~50 | two hand-rolled RUDP loops and cultmesh-ts's connect wait go |

- **Source, net:** about −195. Tests are extra: about 4 × 10 rule tests.
- **Wire:** zero bytes.
- **Downstream:** no caller has to change code at its next bump. Callers that fixed `initial_sequence`
  keep a deterministic Connect sequence, and with it the restart lockout, until they drop the value.
- **Kotlin (Q-A5 A):** Cuts 1, 1b, 1c and 3 again, about the same size as one runtime each.


## Operator questions

Each question stands alone.

**Ruled (2026-09-30), recorded so they are not re-asked:**

- **Q-A1. Where does delivered come from?** (a): derived at the sender from the cumulative watermark. No
  wire change.
- **Q-A2. One flush, or a "received" flush beside it?** One meaning: Acknowledged means delivered.
  Receipts are Pending, Acknowledged or Invalidated. Retransmission stops on each packet's own ack. Flush
  waits for Acknowledged.

**Open:**

- **Q-A3. Reliable expiry next to ordered traffic (P2).** An expired reliable packet leaves a permanent
  hole, and every ordered frame after it on the same session is held until the session ends.
  - **A.** Report it honestly (Cut 4): ordered receipts after the hole stay `Pending` and become
    `Invalidated` when the session ends, and flush fails fast. Document "do not send ordered traffic on a
    session that uses reliable expiry". No live consumer does.
  - **B.** PR-SCTP's FORWARD-TSN (RFC 3758). On expiry the sender sends an abandonment notice, which rides
    an Ack-typed packet with flag `0x10` so that old peers ignore it (P7). New receivers treat the sequence
    as received with no frame, and acknowledge it. The sender resends the notice until it is acknowledged.
    That is a new cut in every receiver. Old receivers never acknowledge it, so the resend needs its own
    bound.
  - **C.** Refuse ordered sends on a session after its first expiry.
  - **D.** Delete reliable expiry. Muninn's `audio` is its only user and `hands/media-fec-cut3` retires it.
    This subtracts, but CultLib would lose partial reliability, which a reasonable consumer can expect.
  - **Recommended: A now.** Record B as the complete answer for when a consumer mixes the two.
  - **Blocks:** Cut 4 only. A keeps Cut 4 as mapped (`first_abandoned` alone). B adds a wire cut in every
    receiver before Cut 4. C changes Cut 4's `send_many` guard. D replaces Cut 4's expiry half with a
    deletion. Cuts 1, 1b, 1c, 2, 3 and 5 are unaffected.

- **Q-A4. Receipts in C#, TypeScript, Python and Kotlin?**
  - Only Rust has per-send receipts. The other four offer flush and an outstanding count.
  - **A.** Port receipts with the same three states (Pending, Acknowledged, Invalidated) and a
    `WaitAcknowledged`, in this campaign.
  - **B.** Record it as a parity gap. Flush covers every surveyed consumer.
  - **Recommended: B.** No consumer outside Rust waits per send. A reasonable C# consumer might want it, but
    it is additive later, changes no wire byte, and after Cut 4 the Rust design (a status cell resolved
    once by the session) ports without a second design.
  - **Blocks:** nothing mapped. A adds a cut of about +80 per runtime after Cut 4.

- **Q-A5. Is Kotlin in the campaign?**
  - `cultmesh-kotlin` carries a fifth RUDP runtime with P1 and P6 (the same code, `CultMesh.kt:2939-2978`).
    Its flush has no callers. The stray-packet work did not touch it, so none of those fixes reached it
    (not probed in pass 2). Its only build path is
    `build.ps1`, which needs Android Studio's `kotlinc` and JBR on Windows. Yggdrasil has no Kotlin image in
    the verify list.
  - **A.** Include it, as a trailing Cut K that applies Cuts 1, 1b, 1c and 3, verified on a Yggdrasil
    Kotlin image added first (or on Starfire as a Windows-only job).
  - **B.** Record it as parity debt, with the stray-packet gaps. Mark the parity doc so that nobody reads
    Kotlin as conforming.
  - **Recommended: A, with a Yggdrasil Kotlin image.** Wire parity is the library's promise. A runtime
    that strands acknowledged frames should not stay listed as conforming.
  - **Blocks:** Cut K only. Cuts 1, 1b, 1c and 3 proceed in four runtimes either way; pass 1 had Q-A5
    blocking their scope, and pass 2 removes that by making Kotlin trail.

- **Q-A6. How does a session recognise a new generation? (new in pass 2)** Today a server treats a
  Connect from a connected endpoint as a repeat by payload (Rust hub, C# listener) or unconditionally (C#,
  TS and Python server mode), so a restarted client with the same payload is locked out (G1), and a
  client's second `connect()` is not seen as new, so the client cannot safely forget the old server
  session's sequences (G2). The Connect's bytes are identical for a retransmit and for a restarted process
  that starts at the same initial sequence, so something has to differ.
  - **A.** The Connect's sequence identifies the generation, and the default initial sequence is random in
    [1, 2^31), as TCP's initial sequence number is (RFC 9293 §3.4.1, RFC 6528). A Connect repeats exactly
    when it carries the current generation's Connect sequence. No wire change. The default of a public
    option changes (from 1, or 100 for the C# listener); an explicit value still works for tests, and a
    caller that fixes it keeps the lockout.
  - **B.** Proof of receipt. A byte-identical Connect repeats until the server has seen the client
    acknowledge the Accept; after that any Connect is a new generation. No default changes. But a delayed
    duplicate of the original Connect that arrives after that ack replaces a live session and forgets what
    it received, so its retransmits are delivered twice.
  - **C.** The application's Connect payload identifies the generation (today's hub rule), and every
    client must put a per-process nonce in it, as the provider layer's `clientSessionId` does. Server mode
    adopts the same comparison. This leaves G1 as a documented obligation on every caller, including the
    ones that send an empty payload (`cultmesh-rs/src/lib.rs:973`, the TS publishers).
  - **Recommended: A.** It is the literature's answer, it is robust to delayed duplicates (they carry the
    accepted sequence), and it moves the decision into the session, deleting four transport-level
    decisions.
  - **Blocks:** Cut 1c only. Cut 1b (the ending rule) is ruled and does not depend on it.

**Not forks, recorded so they are not re-asked:**

- `Acknowledged` keeps its name and now means delivered. No variant is added or removed.
- Delivered means handed to the transport's application boundary, not consumed.
- The 4 MiB cap stays. The sender's window keeps legal traffic under it (§5).
- The receiver's 4,096 receive history stays. The sender's window keeps legal traffic inside it.
- A receipt is resolved once. `Acknowledged` before a session ends stays `Acknowledged`; anything else
  becomes `Invalidated` at the end.
- Session generation is its own two cuts, not part of Cut 1 (§8).


## Sequencing and blockers

1. **Done.** `hands/rudp-stray-packets` merged to `main` at `3bf1c0c`. Odin pins `3bf1c0c`.
2. **Ready for Hands now** (base `main` at `3bf1c0c` or later; four runtimes; Kotlin trails per Q-A5):
   - **Cut 1** (the watermark).
   - **Cut 1b** (the ending rule). Same files as Cut 1 in `ResetPeerState` and the Disconnect branches:
     land it after Cut 1 on the same branch, or in parallel and rebase the second.
   - **Cut 2** and **Cut 3**, independent of Cut 1 and of each other, in parallel worktrees. Merge Cut 1
     before Cut 3 so that Cut 3's P4 test exercises the new hold path.
3. **Cut 1c** is blocked on Q-A6 and on Cut 1b.
4. **Cut 4** is blocked on Cut 1 (Acknowledged is sound only against a correct receiver), on Cut 1b (it
   resolves receipts through the ending primitive), and on Q-A3.
5. **Cut 5** is blocked on Cut 3 in TS (the socket transport it routes through must carry the window) and
   on Cut 1b in TS (its flush must be bound to the generation). It does not need Cut 1c, but Cut 5's
   parity-doc text for repeated Connects waits for it.
6. **Odin's next pin bump** follows Cuts 1-3 (and 1b). Odin is the receiver for every Idunn health,
   presence and catalog publisher, so this bump is what makes their Acknowledged mean delivered. Odin uses
   `publish_cultnet_message_to_rudp_catalog` and no receipt type, so Cut 4 never breaks its build.
7. **Parallel campaigns:**
   - **`hands/media-fec-cut3`** (unmerged, `4b85a173`) touches `rudp.rs` only in the imports, the
     profile's `audio` entry (`~2374` at `3bf1c0c`) and the `"media" | "audio"` arm of
     `channel_send_options` (`~2547`). None overlaps Cuts 1-4. Either order; the second rebases trivially.
     Once it lands, reliable expiry is only for `media` when the caller chooses Reliable.
   - **Muninn's own cuts** use lossy media plus parity. Its reliable `audio` session has no ordered
     traffic, so P2 does not bite it. Nothing here blocks Muninn, and nothing in Muninn blocks this.
   - **CultCache publication Cut 4** (`docs/cultcache-publication-cut.md` §4) touches `CultNetDatabase.cs`
     only, and needs in-order, exactly-once delivery on `schema`. The C# socket transports carry one
     reliable channel (`CultNetTransport.cs:2372-2376`), so P1 and P6 cannot occur on their sessions. No
     blocking relation in either direction.
8. **Vendored TS bumps come after Cut 5**, so they pick up honest flushes, the TS window and the rewritten
   publishers at once:
   - **StreamPixels** vendors CultLib at `30ee8b9` (StreamPixels `origin/main`, gitlink bumped in
     `44a8945`). Its service runs `createIdunnRuntimePresencePublisher`
     (`apps/service/scripts/idunn-entry.mjs`), whose file is byte-identical at `30ee8b9` and `3bf1c0c`;
     `30ee8b9` also predates the TS stray-packet hardening in `rudp.ts`.
   - **Heimdall** vendors `b6b1d9c` and hand-rolls its own publisher (`src/idunn-rudp-health.ts:55-87`,
     first-ack wait). At its bump it calls cultnet-ts's publisher instead.
   - Bumping either after Cut 1c as well also gives them the restart fix; neither needs it, because each
     publish binds a new port.
9. **CodexConnector** and **Ghostlight** need no code change at their next bump. They may switch their
   status loops to `wait_acknowledged` after Cut 4.


## Follow-ups outside this campaign

- **Hold caps in C#, TS, Python and Kotlin.** Their ordered hold buffers and per-channel maps are unbounded
  against a hostile peer. Rust has caps.
- **The document server's per-session payload budget never shrinks** (`rudp_document_server.rs:321`,
  `:584`). After 4 MiB in one session it drops data without acknowledging it, and the sender retransmits
  until it gives up.
- **Fire-and-forget health.** Muninn `main.rs:9446`, Sleipnir `main.rs:880-904` and Heimdall's first-ack
  wait can lose a health record silently.
- **FORWARD-TSN** if Q-A3 is A and a consumer later mixes expiry with ordered traffic.
- **Callers outside CultLib that fix `initial_sequence`** keep the restart lockout after Cut 1c until they
  drop the value. Record it in their pin-bump notes.
- **The P4 probe at the old pins** predates its fix, so it proves nothing there. If anyone needs
  old-sender-to-new-receiver evidence for the cap, rerun `09318d13`'s P4 against a pinned sender.
