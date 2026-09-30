# CultNet RUDP: Acknowledged means delivered, one watermark owns ordered delivery, and a write belongs to its session

Status: cut map, Imagination pass 3, 2026-09-30. Pass 3 maps the four rulings below, re-maps Cuts 1c and
4, and adds Cut F (FORWARD-TSN), Cuts 6a-6c (receipts), Cut 7 (expiry, if Q-A8), Cut K (Kotlin), Cut 2c (the
C# fragment bound) and Cut D (a failed send to one peer stays that peer's loss). The text of Cuts 1, 1b, 2
and 3 is pass 2's; Hands are executing Cuts 1 and 1b (`hands/ack-cut1`) and Cut 2 (`hands/ack-cut2`) from
it. One thing moves for them: Cut 2 merges only together with Cut F (the Cut 2 Soul finding below).

**Rulings (operator, 2026-09-30).**
- **Q-A1 (a):** Delivered is derived at the sender from a correct receiver's cumulative watermark. The
  acknowledgement wire does not change. (Cut F adds one notice to the wire; it carries abandonment, not
  delivery.)
- **Q-A2:** "I think this is telling us we don't actually need both received and delivered."
  - There is one public meaning: Acknowledged means delivered.
  - Receipts go Pending, then Acknowledged (delivered), or Invalidated. No public Received state exists.
  - Retransmission still stops on each packet's own ack, so resending stays bounded.
  - Flush waits for Acknowledged.
- **Scope added from the stray-packet Soul passes 3 and 4:** session generation. One rule: a write belongs
  to the session it was issued in. Mapped as **Cut 1b** (the ending rule) and **Cut 1c** (how a new
  generation is recognised), separate from Cut 1 (§8 says why).
- **Q-A3 B:** FORWARD-TSN (RFC 3758) now. On expiry the sender sends an abandonment notice, Ack-typed with
  flag `0x10` so old peers ignore it. New receivers treat the sequence as received with no frame and
  acknowledge it. The notice's resend has its own bound. A new wire cut in every receiver, before Cut 4.
  Mapped as **Cut F** (§6). The recommendation was A; the operator chose B. (Supersedes pass 2's Cut 4
  expiry half, which assumed A.)
- **Q-A4 A:** port per-send receipts (Pending, Acknowledged, Invalidated, and a wait for Acknowledged) to C#,
  TypeScript, Python and Kotlin in this campaign, after Cut 4. Mapped as **Cuts 6a-6c** and **K4** (§9).
  The recommendation was B.
- **Q-A5 A:** Kotlin is in, as trailing **Cut K**, verified on a Kotlin image added to Yggdrasil first
  (Cut K0).
- **Q-A6 A:** the Connect's sequence identifies the session; the default initial sequence becomes random in
  [1, 2^31), as TCP's ISN. No wire change; the public default changes. Cut 1c is no longer blocked on a
  question.
- **Cut 2 Soul finding (2026-09-30):** Cut 2's never-evict rule refuses a session whose sender expires
  fragmented reliable frames (the default Rust `media` channel). Pass 3 absorbs it: Cut F's receiver drops
  every incomplete fragment set a notice covers; Cut 2 merges only together with Cut F; C# gets the bound
  it never had (Cut 2c). What a receiver does with a sender that never sends notices is **Q-A9**.
- **Ruled after pass 3 (operator, 2026-09-30), each the recommendation:**
  - **Q-A7 A:** a notice names one sequence, not a cumulative point.
  - **Q-A8 A:** C#, TypeScript, Python and Kotlin gain reliable expiry (Cut 7, K5).
  - **Q-A9 B:** a capability bit on Connect and Accept; Cut 2's never-evict rule applies only to peers
    that advertise it, and other peers keep pre-Cut-2 eviction.
  Cut F is unblocked once Cuts 1 and 3 merge; the text below mapped these answers already.

- **Body.** CultLib `main` at `e9469cb3`. Every transport file is byte-identical to `3bf1c0c` there
  (`git diff 3bf1c0c e9469cb3 -- src packages` is empty), so every `file:line` below is against both unless
  it names another revision.
  - Odin `origin/main` (`5c37860`) pins `3bf1c0ce` (`crates/odin-core/Cargo.toml:14-17`). A stale local
    checkout at `379b826` still shows `a8aeddac`; read Odin's pin from `origin/main`.
  - Pass 3 probes: `3e2c7efb` (FN) and `c6f1012c` (CAP) on the Yggdrasil mirror `probe3` (§ Probe
    evidence, pass 3).
- **Base of every cut.** `main` at `e9469cb3` or later.
- **Scope.** The RUDP session and socket transports in all five runtimes: C#
  `src/GameCult.Networking/CultNetTransport.cs`, Rust `packages/cultnet-rs/src/rudp.rs` (plus
  `packages/cultmesh-rs`), TypeScript `packages/cultnet-ts/src/rudp.ts` (plus the two hand-rolled
  publishers and `cultmesh-ts`'s document server), Python `packages/cultnet-py/src/cultnet_py/transport.py`
  (plus `cultmesh-py`'s server), and Kotlin
  `packages/cultmesh-kotlin/src/main/kotlin/org/gamecult/cultmesh/CultMesh.kt` (Cut K).
- **Wire.** One addition, in Cut F: an Ack packet with flag bit 4 (`0x10`) is an abandonment notice for the
  sequence in its sequence field. Every current decoder ignores the bit and every current session ignores
  an Ack's sequence field (P7, re-probed as FN in pass 3 in all five runtimes). If Q-A9 is B, the same bit
  on Connect and Accept advertises support (probe CAP: old peers ignore it there too). Nothing else changes
  on the wire. Cut 1c changes the default initial sequence, which is a value, not a format.


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
| P2 | An expired reliable packet leaves a permanent hole. Ordered frames after it are held forever, and `flush_reliable` succeeds (outstanding 0) | Rust (the only runtime with reliable expiry) | Cut F (FORWARD-TSN, Q-A3 B); Cut 4 reports it honestly where a receiver never acknowledges the notice |
| P9 | A receipt for an expiring send reads `Acknowledged` once the packet expires unacknowledged | Rust | Cut 4 |
| P10 | `send_reliable` with a full window queues the frame, then returns an error ("receipts require a non-empty reliable packet set"), so a caller that retries sends it twice | Rust | Cut 4 |

And two faults found by Soul passes during pass 3:

| # | Fault | Where | Fixed by |
|---|---|---|---|
| D1 | One peer whose datagrams cannot be sent fails every poll for every peer: the first failed `send_to` aborts `maintain` and `poll_once` (Odin Soul pass) | cultmesh-rs document server, Rust hub, C# listener; the cultmesh-py server's thread dies | Cut D (§10) |
| F2 | After Cut 2, a sender with reliable expiry pins one reliable fragment set per lossy frame, and the 65th refuses the session (Cut 2 Soul pass, default Rust `media`) | Rust, TS, Python receivers with Cut 2 | Cut F releases the sets; Cut 2 merges with it; Q-A9 for senders without notices |


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

Pass 3 (base `e9469cb3`, transport files identical to `3bf1c0c`; mirror `probe3`, commits `3e2c7efb` for FN
and `c6f1012c` for CAP):

- `packages/cultnet-rs/tests/probe_fn.rs`, `packages/cultnet-ts/test/probe-fn.test.ts`,
  `packages/cultnet-py/tests/probe_fn.py`, `tests/GameCult.Networking.Tests/ProbeFnTests.cs` and
  `packages/cultmesh-kotlin/src/main/kotlin/org/gamecult/cultmesh/ProbeFn.kt`.
- Images: `eureka-verify-rust`, `mcr.microsoft.com/dotnet/sdk:10.0`, `node:24`, `python:3.12`, and for
  Kotlin `eclipse-temurin:21-jdk` with `kotlin-compiler-2.2.21.zip` fetched in the job (the recipe Cut K0
  bakes into an image).

| # | Probe | Result at `e9469cb3`, all five runtimes |
|---|---|---|
| FN | Sender `a` sends ordered `o1`, `o2`, `o3`; `o2` is lost, so the receiver `b` holds `o3`. `b` sends reliable `x`, which `a` receives. `a` then sends the Cut F notice exactly as specified: `create_ack()` encoded, flag byte `\|= 0x10`, sequence field = `o2`'s sequence. `b` decodes and receives it; then `o2` arrives late | **Old receivers ignore the notice and apply its acks.** It decodes as an Ack; `b`'s pending `[501]` (`x`) empties, so the header's acks were applied; nothing is delivered; `reply` is empty; `b` stays connected; `o2` is **not** marked received (not in `b`'s ack or mask). The late `o2` then delivers `[o2, o3]`: the hole was untouched. Identical in Rust, C#, TypeScript, Python and Kotlin |
| FN-T | Transport path (code, not run) | Every socket transport acks only a reliable, Accept or Data packet, or (TS, Python, Kotlin) one whose receive delivered a frame (`rudp.rs:2152-2155`, `:1713-1715`, `CultNetTransport.cs:2281-2283`, `:2787-2788`, `rudp.ts:1089-1091`, `transport.py:1086-1088`, K:3243-3245). An old receiver delivers nothing on a notice (FN), so the notice draws no reply datagram and is not counted dropped. Every transport already forwards a session's `reply` (`rudp.rs:2135-2137`, `:1708`, `CultNetTransport.cs:2277-2278`, `:2748-2749`, `rudp.ts:1067-1068`, `transport.py:1070-1071`, K:3229, `rudp_document_server.rs:334-336`, `cultmesh-ts/src/index.ts:5828-5830`, `operation-service.ts:186`, `cultmesh-py/.../server.py:246-247`), which is how a new receiver answers a notice |
| CAP | Q-A9 B's capability bit on old peers: `a`'s Connect and `b`'s Accept are each encoded, given flag bit 4, and decoded before delivery; then `a` sends one ordered frame (`probe_cap.rs`, `probe-cap.test.ts`, `probe_cap.py`, `ProbeCapTests.cs`, `ProbeCap.kt`) | **Old peers ignore the bit on the handshake.** Both sides connect, the frame is delivered, and the Connect is acknowledged (the sender's pending holds only the data frame). Identical in all five runtimes |
| K-img | Kotlin self-tests on Linux | `kotlinc-jvm 2.2.21` on OpenJDK 21.0.12 compiles `CultMesh.kt` plus a probe file, and `java -cp out.jar:kotlin-stdlib.jar org.gamecult.cultmesh.CultMeshKt` (the self-tests, K:3349-3373) exits 0 |

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

- An additive field fits in an Ack: flag bit 4 set, the value in the Ack's unused sequence field, zero
  extra bytes. Old peers apply the ack and ignore the rest. Q-A1 ruled out a watermark there; Cut F puts
  the FORWARD-TSN notice there (Q-A3 B), and pass 3's FN probe confirms the old-peer behaviour in all five
  runtimes.
- A new packet type or a version bump is not additive: old peers refuse the datagram and count it dropped.
- Cut F's notice needs no negotiation to be safe: it reads as a plain ack to every current peer. Q-A9 asks
  whether to advertise support anyway, for the receiver's sake (§6).


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
- **Owner of abandonment (sender, Cut F): the abandoned set.** An expired sequence is announced by notices
  until an ack names it or 16 sends pass; then it is stranded (`first_stranded`). The receiver's `Abandon`
  branch is the only path that marks a sequence received without its packet, and it drops every incomplete
  fragment set the sequence falls in.
- **Owner of Acknowledged (sender): the acknowledged-through watermark `A`**, one below the lowest sequence
  that is pending, queued, awaiting its abandonment notice's ack, or stranded in the current generation. An ordered send is Acknowledged when `A`
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
  - No ending path leaves a write owed. No path retransmits a write, or sends a notice, into a generation
    after the one it was issued in.
  - No path marks a sequence received without a packet, except the `Abandon` branch (Cut F).
  - No per-peer send failure ends a poll, a loop, a thread or another peer's session (Cut D).
  - No transport decides whether a Connect repeats: not by payload (Rust hub `rudp.rs:1637-1647`, C#
    listener `CultNetTransport.cs:2670-2688`), not by `Connected` (C# `:2226-2236`, Python
    `transport.py:1047-1053`, TS `rudp.ts:249-251`, `:1031-1049`), not by server key or payload
    (cultmesh-rs document server `rudp_document_server.rs:413-427`, cultmesh-ts document server
    `index.ts:5794-5802`), and not by resetting on every Connect (Rust server mode `rudp.rs:2105-2116`,
    cultmesh-py server `server.py:225-240`).
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

- **Rust receipts** (the only runtime that has them today; §9 ports them to the other four):
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
    Under Cut F an abandoned sequence is resolved by its notice's ack, so flush waits for that too. The
    only exception is a stranded sequence (a notice the receiver never acknowledged). There
    `flush_reliable` (`rudp.rs:1992-2040`) fails at once when an ordered send follows it: that send can
    never be Acknowledged, and today flush reports success (P2).
  - C# `FlushReliable`, TS `flush`, Python `flush_reliable` and Kotlin `flushReliable`
    (`CultMesh.kt:3186`) keep their success condition. Their contract text becomes "every reliable frame
    sent in this session has been delivered to the peer's application". These runtimes have no expiry
    unless Q-A8 gives them some (Cut 7).
- **Defaults.** Plain flush. No parameter. No second flush.
- **Compatibility.** No signature changes in any runtime except additions (`wait_acknowledged`; TS
  `connectAndWait`, §4; the receipts of §9, where C# and Kotlin `SendSchema*` return a receipt instead of
  nothing, which is source-compatible). The Rust enum is unchanged. No downstream caller has to change code at its next
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
| `cultnet-ts/src/idunn-odin-presence-publisher.ts:89-215` `publishDocument`, behind `createIdunnRuntimePresencePublisher`. **StreamPixels' service runs this**: `apps/service/scripts/idunn-entry.mjs` runs `deployment/idunn/runtime-presence.mjs`, which calls `createIdunnRuntimePresencePublisher` (`:46`) from its vendored CultLib `542ddd3` (StreamPixels `a49f7d8`; pass 2's `30ee8b9` is stale), where it lives under its older name `cultnet-ts/src/idunn-runtime-authority.ts:206` | a hand-rolled session loop (`:186-208`) until `outstandingReliablePacketCount === 0`. It does resend (`:205`). It drops `readyToSend` (`:198`), so promoted queued packets wait for the resend timer. It pins `initialSequence: 1` (`:103`) and never sends a Disconnect, so Odin's hub keeps each publish's peer until it times out (each publish binds a new port, so each is a new peer) | Cut 5: the socket transport, `connectAndWait`, `send("schema")`, `flush`, `close` (which sends the Disconnect, `rudp.ts:956-969`) |
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

### 6. Reliable expiry and FORWARD-TSN (P2; Q-A3 B, ruled)

- **The hole.** Only Rust has reliable expiry (`rudp.rs:39`, `:898-908`; the Python README's claim of
  expiry at `packages/cultnet-py/README.md:103-111` is false, `transport.py` has none). An expired packet's
  sequence never reaches the receiver, `R` stops below it, and every ordered frame after it on the session
  is held until the session ends.
- **Who expires.** Every Rust `media` sender that does not opt into lossy delivery: the profile's media
  delivery defaults to Reliable (`channel_delivery`, `rudp.rs:2515-2521`) and its expiry to 75 ms
  (`rudp.rs:39`, set on the client and hub transports at `:1303` and through the profile at `:1548-1549`).
  Media frames fragment. Muninn's reliable `audio` also expires; `hands/media-fec-cut3` deletes that profile
  entry and moves Muninn's media to lossy delivery, but the default stays reliable-with-expiry for every
  other caller.

**What RFC 3758 does, and what this map keeps.**

- The sender treats an abandoned chunk as "finally acked and no longer outstanding" (§3.5 A2) and advances
  an `Advanced.Peer.Ack.Point` over consecutive abandoned TSNs. When that point passes the peer's
  cumulative ack it sends FORWARD TSN (C1-C3), and tries again whenever T3-rtx expires (A5).
- FORWARD TSN carries a **New Cumulative TSN** plus (stream, stream sequence) pairs. The receiver "MUST
  consider any missing TSNs earlier than or equal to this value as received" (§3.2), advances its
  cumulative point, releases the listed streams' reordering queues, and acks "as if a DATA chunk had been
  received" (§3.6). A FORWARD TSN at or behind its cumulative point is out of date, changes nothing, and
  still draws a SACK.
- PR-SCTP is negotiated: an endpoint that did not see Forward-TSN-Supported in the peer's INIT must never
  send FORWARD TSN (§3.1, §3.3).

**How CultNet adapts it.**

- **The notice names one sequence, not a cumulative point.** SCTP's SACK is cumulative, so a cumulative
  FORWARD TSN is confirmed by the next SACK. CultNet's ack is not: it names the highest received sequence
  plus a 32-bit mask, or one exact older sequence (`create_ack_for_received`, `rudp.rs:751-758`). A
  cumulative notice could not be confirmed by that ack, which would mean a second wire field. So each
  abandoned sequence gets its own notice, and the receiver answers it with the ack a reliable data packet
  with that sequence would draw. That ack removes the sequence from the sender's abandoned set exactly as it
  removes a data packet from pending. **Q-A7** puts the cumulative alternative to the operator.
- **No stream pairs.** One watermark `R` owns ordered delivery for every channel (Cut 1), so advancing `R`
  and draining releases every channel's held frames. There is nothing per channel to name.
- **Negotiation: only if Q-A9 is B.** The ruling puts the notice in an Ack so an old peer reads it as an
  ack (FN), and bounds the resend: an old receiver never acks the notice, and the sender stops after a
  fixed number of sends. The Cut 2 Soul finding adds a reason to negotiate after all (Q-A9): a *receiver*
  needs to know whether its peer will ever send notices. If Q-A9 is B, the sender also sends notices only
  to a peer that advertised the bit (RFC 3758 §3.3), and an old receiver's sequence is stranded at once
  instead of after the bound. The abandoned sequence is then *stranded*: the Cut 4 honest
  report applies to it (ordered sends after it stay Pending, become Invalidated at the end, and flush fails
  fast). That is the behaviour ruling A would have given everywhere; under B only old receivers get it.

**Wire.** Packet type `4` (ack), flag bit 4 (`0x10`, "abandon"), and no other flag bit. Sequence field: the
abandoned sequence. Ack and ack mask: the sender's current ack state, so the notice is also a genuine ack.
Channel `control`, fragment fields `0`, empty payload. Decoders map type 4 with bit 4 to a new packet type,
**`Abandon`** (Rust `CultNetRudpPacketType::Abandon`, C# `CultNetRudpPacketType.Abandon`, TS `"abandon"`,
Python `CultNetRudpPacketType.ABANDON`, Kotlin `CultNetRudpPacketType.Abandon`), and the encoder maps it
back. Bit 4 on any other type is ignored, as today. No public packet struct gains a field, and no
downstream repo constructs packets or matches the type exhaustively (surveyed: Muninn tests compare
`== Data`; Mimir `CultMeshMedia` compares `Connect` and `Accept`).

**Golden** (added beside the existing fixture in every runtime and in `docs/cultnet-transport-parity.md`
after the canonical fixture, `:505-517`):

```text
packetType=abandon (wire: type 4, flags 0x10)
connectionId=0x01020304
sequence=0x0000002a
ack=0x00000029
ackMask=0x80000001
channelId=control
payload=(empty)
hex=434e52300004102b010203040000002a0000002980000001000000000000000000000700636f6e74726f6c
```

**Sender (Rust now; any runtime that gains expiry under Q-A8).**

- An expired packet, pending or queued, leaves the pending and queued sets as today and enters an
  **abandoned set**: sequence, sends so far, last sent. It is due at once.
- `due_resends` emits a fresh notice for each due entry (built from the current ack state, not stored),
  once per resend delay. After `RUDP_ABANDON_NOTICE_SENDS = 16` sends without an ack the entry leaves the set
  and the session records the lowest such sequence as **`first_stranded`**. Sixteen sends at the default
  250 ms is four seconds. At 50% loss in each direction a round trip fails with probability 3/4, and all
  sixteen with about 1%; at 20% each way, about 10^-7. The bound is for deaf receivers, not bad links, and
  a new receiver that misses every notice degrades to the honest report, never to a lie.
- Any ack naming the sequence removes it from the abandoned set. That ack means the receiver holds the
  sequence, from the original packet arriving late or from the notice; either way it is no longer a hole.
- Notices take no send-window slot and no payload bytes. For Cut 3's span rule, an abandoned sequence with
  an outstanding notice counts as unacked (the receiver's `R` is held below it); a stranded one does not
  (an old receiver has no `R` to hold, and counting it would stop the session after 1,023 more sequences).
- `outstanding_reliable_packet_count` counts abandoned entries, so a flush waits for their notices.
- The session-ending primitive (Cut 1b) and `reset_peer_state` clear the abandoned set and
  `first_stranded`: they belong to the generation.

**Receiver (all five runtimes).**

- `receive` handles `Abandon` after applying its acks, as RFC 3758 §3.6 does:
  - outside `[highest − 4,095, highest + 1,024]`: far-ahead is ignored without remembering (as Rust data
    is, `rudp.rs:626-641`), far-behind is a duplicate;
  - every incomplete fragment set whose sequence span contains the sequence is dropped (a set's span is
    `sequence − fragment_index` for any fragment it holds, for `fragment_count` sequences), whether or not
    the sequence was received: that frame can never complete;
  - if the sequence was not received, it is remembered through `RememberReceived`, which advances `R`
    (Cut 1), and held frames up to `R + 1` drain into `delivered`;
  - `reply` is `create_ack_for_received(sequence)`, whether or not anything changed (the out-of-date case
    still acks).
- Transports send the reply as they already do, and skip their own ack for an `Abandon` packet
  (`rudp.ts:1089`, `transport.py:1087`, K:3243 would otherwise ack it a second time when the drain
  delivered).

**Interaction with the other cuts.**

- **Cut 1 (`R`).** The notice is the only receive path besides data and Accept that calls
  `RememberReceived`; it shares the drain. A late original of an abandoned packet is then a duplicate: acked
  and dropped, as PR-SCTP drops it.
- **Cut 1b (ending).** An abandoned entry is a write of its generation; the ending primitive drops it. No
  notice is ever sent into a later generation.
- **Cut 1c (generations).** A stale notice from an earlier generation of the same sender carries a
  sequence below the new generation's Connect (issued sequences survive resets), so it is a duplicate. A
  stale notice from a dead process on the same endpoint and connection id could mark a sequence of the new
  process's session, exactly as a stale data packet can be delivered into it today; the random initial
  sequence makes both land inside the 5,120-sequence acceptance range with probability about 2^-18.
- **Cut 2 (fragment eviction), from the Cut 2 Soul pass.** Cut 2 never evicts a reliable fragment set and
  refuses the session when every pending set is reliable. Its claim that a conforming sender cannot reach
  the 64-set bound is **false for an expiring sender**: fragment 0 arrives and is acked, the deadline drops
  the rest, and the set is pinned for the session. Soul reproduced the refusal on the 65th lossy default
  `media` frame (`soul_probe_expiring_reliable_frames_kill_receiver`, `packages/cultnet-rs/tests/cultnet.rs:3192`
  at probe `3be97b34`). Before Cut 2, eviction kept that session alive.
  - **With a Cut F sender** the notice for each dropped fragment lands inside the set's span and the
    receiver drops the set: the rule above, explicitly "every incomplete set whose span covers a notified
    sequence". Dead sets then live one round trip, not the session.
  - **With a sender that never sends notices** (every build before Cut F) the receiver cannot tell an
    expiring set from a set whose missing fragment is still coming. **Q-A9** decides what the receiver does
    then; its recommendation is PR-SCTP's own answer, a capability bit on Connect and Accept (RFC 3758
    §3.1, Forward-TSN-Supported): Cut 2's never-evict rule applies to a peer that advertised it, and the
    pre-Cut-2 stalest-set eviction stays for a peer that did not.
  - So Cut 2 does not merge alone: it merges together with Cut F, which carries the capability and the
    gate (§ Sequencing).
- **Cut 4.** Reads `first_stranded` where pass 2 read `first_abandoned`, and counts outstanding notices as
  not yet Acknowledged.

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
  **Correction (Cut 1/1b Soul, 2026-09-30): they are separable as specs, not as merges.** 1b drops owed
  writes, which leaves holes the sender will never fill; the receiver keeps `R` across the ending, so if the
  next handshake does not reset it, every later ordered frame is held and acked, and flush succeeds while
  nothing is delivered. Only 1c's reset on a non-repeated handshake removes that. **1b merges only together
  with 1c.** Soul also found `R` seeded by the first reliable packet rather than the handshake; ruled:
  `R` is seeded only by the handshake, and reliable data before the Accept is neither acked nor delivered.
- **What must hold after both.** A write is Acknowledged or Invalidated within the generation it was
  issued in. A restarted client is admitted. A new server session's frames are delivered after a
  reconnect. A retransmitted Connect is still a repeat.
- **Recognising a new generation (Cut 1c, Q-A6 A, ruled).** The Connect's sequence identifies the
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

### 9. Receipts in C#, TypeScript, Python and Kotlin (Q-A4 A, ruled)

- **One design, ported.** Cut 4 builds it in Rust: a receipt carries the sequences its send assigned,
  whether it was ordered, and a status cell the session writes once. The session keeps the unresolved
  receipts and resolves them after every ack it applies (Acknowledged: ordered when `A` reaches its last
  sequence, unordered when every sequence is acked) and in the ending primitive (Invalidated). The four
  ports copy that shape; none invents a second one.
- **What every runtime has to change first** (the receipts survey, `e9469cb3`):
  - `SendMany`/`sendMany`/`send_many` returns only the packets admitted to the 32-packet window
    (`CultNetTransport.cs:1439-1465`, `rudp.ts:586-592`, `transport.py:776-786`, K:2856-2862). A receipt
    must be built from every sequence the send *assigned*, queued ones included, which is Rust's P10.
  - `ApplyAcknowledgements` only deletes from the pending map (`CultNetTransport.cs:1503-1516`,
    `rudp.ts:610-617`, `transport.py:803-807`, K:2882-2889). Resolution hooks in after it.
  - Invalidation needs the ending primitive. C#, TS and Python get it in Cut 1b; Kotlin in K2.
- **Where the receipt lives.** On the single-peer socket transport connection, as in Rust
  (`CultNetRudpSocketTransportConnection::send_reliable`, `rudp.rs:1903-1930`). The Rust hub has no
  receipts, so neither do the C# listener (`CultNetRudpSocketTransportServer`, `:2487`), `cultmesh-ts`'s
  document server or `cultmesh-py`'s server. Hub receipts are a follow-up for all five together.
- **Public API, per runtime.** Every runtime's RUDP surface is synchronous-with-timeout except TypeScript's,
  which is Promise-and-event. Each port matches its own runtime:

| | Send | Receipt | Status | Wait |
|---|---|---|---|---|
| Rust (Cut 4) | `send_reliable(&mut self, channel, payload) -> Result<CultNetRudpReliableSendReceipt>` | `packet_sequences()` | `reliable_send_status(&receipt) -> CultNetRudpReliableSendStatus` | `wait_acknowledged(&receipt, timeout) -> Result<()>` |
| C# (6a) | `CultNetRudpSendReceipt SendReliable(string channelId, byte[] payload)`; `SendSchema(...)` and `SendSchemaMessage<T>(...)` return the receipt instead of `void` (source-compatible) | sealed class, `IReadOnlyList<uint> PacketSequences` | `receipt.Status` (`CultNetRudpSendStatus { Pending, Acknowledged, Invalidated }`), read without the transport | `void WaitAcknowledged(CultNetRudpSendReceipt receipt, TimeSpan? timeout = null)`, the `FlushReliable` loop (`:1914-1955`); throws `InvalidOperationException` naming the end reason when Invalidated, `TimeoutException` at the deadline |
| TypeScript (6b) | `sendReliable(channelId: string, payload: Uint8Array): CultNetRudpSendReceipt`. `send` keeps `void`, because the transport interface declares it (`transport.ts:132-142`) | `readonly packetSequences: readonly number[]` | `receipt.status: "pending" \| "acknowledged" \| "invalidated"` (string union, as packet types are) | `waitAcknowledged(receipt, timeoutMs = 30_000): Promise<void>`, polling like `flush` (`rudp.ts:911-935`); rejects with the end reason when invalidated |
| Python (6c) | `send_reliable(self, channel_id: str, payload: bytes) -> CultNetRudpSendReceipt` | frozen-looking object, `packet_sequences: tuple[int, ...]` | `receipt.status` (`CultNetRudpSendStatus` enum: `PENDING`, `ACKNOWLEDGED`, `INVALIDATED`) | `wait_acknowledged(self, receipt, timeout_seconds: float = 30.0) -> None`, the `flush_reliable` loop (`:1101-1131`); raises `ConnectionError` when invalidated, `TimeoutError` at the deadline |
| Kotlin (K4) | `fun sendReliable(channelId: String, payload: ByteArray): CultNetRudpSendReceipt`; `sendSchema`/`sendSchemaMessage` return it | `val packetSequences: List<Long>` | `receipt.status` (`enum class CultNetRudpSendStatus { Pending, Acknowledged, Invalidated }`), `@Volatile` | `fun waitAcknowledged(receipt, timeoutMs: Long = 30_000, pollIntervalMs: Long = 5)`, the `flushReliable` loop (K:3186-3205); throws `IOException` when invalidated, `SocketTimeoutException` at the deadline |

  - The status is on the receipt, not a transport query, in the four ports: a C#, TS, Python or Kotlin
    caller reading `receipt.Status` is the idiom there, and it is what Rust's cell already is underneath.
    Rust keeps `reliable_send_status` because its signature is public and used (Cut 4, Unchanged).
  - `SendReliable` refuses a channel whose send options are not reliable (as Rust's `send_reliable` does,
    `rudp.rs:1903-1930`). If Q-A8 brings expiry to a runtime, it refuses an expiring channel too, as Rust's
    does after Cut 4 (P9).
  - A receipt from an ended session is Invalidated and stays so; `WaitAcknowledged` on it fails at once.
- **Thread safety.** C# resolves under `_pendingReliableGate` (`:915`) and publishes the status with a
  volatile write; Kotlin marks the field `@Volatile`; TS and Python are single-threaded per transport.
- **Docs.** Each runtime's README RUDP section gains the three states and the wait
  (`src/GameCult.Networking/README.md`, `packages/cultnet-ts/README.md:24-25`,
  `packages/cultnet-py/README.md:103-111`, `packages/cultmesh-kotlin/README.md:229-305`), and the parity
  doc's flush paragraph (`docs/cultnet-transport-parity.md:260-273`) names receipts in all five.

### 10. A failed send to one peer is that peer's lost datagram (Cut D; from the Odin Soul pass)

- **Finding.** `CultMeshRudpDocumentServer::poll_once` begins with `self.maintain()?`
  (`cultmesh-rs/src/rudp_document_server.rs:264`). `maintain` sends every peer's due resends and returns on
  the first failed `send_to` (`:392-398`, through `send_packet` `:628-632`). Every per-peer send in
  `poll_once` does the same: the reply (`:334-336`), the Accept (`:451`), a snapshot response (`:586-588`),
  the goodbye (`:597-604`) and the ack (`:366-373`).
  - So one peer whose datagrams cannot be sent fails every poll for every peer. Its resends come due every
    50 ms (`:158`); a poll loop that pauses longer than that between attempts never gets past `maintain`
    again while the peer lives, and a peer that keeps sending never idles out (`:384-388`).
  - Odin `origin/main` (`crates/odin-daemon/src/main.rs:661-675`) propagates any `poll_once` error, so
    today the daemon exits on the first one; the Odin Soul pass reports a branch that retries every 100 ms
    and exits after 30 s of failures. Either way one bad peer takes Odin down.
  - The same socket is **non-blocking** (`:230`), so `send_to` also fails with `WouldBlock` when the send
    buffer is full: a burst of snapshot responses to one peer can fail a poll for everyone with no bad peer
    at all.
- **Rule: a send failure is a lost datagram, attributed to its peer.** It is counted and the poll goes on.
  RUDP already recovers lost datagrams: the reliable packet stays pending and is resent; an unreliable one
  was allowed to be lost. The peer's session ends by the rules that already end sessions: idle timeout,
  maximum lifetime, a refusal or a Disconnect. This is TCP's treatment of ICMP unreachables as soft errors
  (RFC 1122 §4.2.3.9): report them, do not abort the connection on them.
  - Only failures that are not about one peer stay errors: `recv_from` failing with anything but
    `WouldBlock` or `ConnectionReset` (Windows reports an earlier ICMP port-unreachable on the *next*
    receive; the socket transport already treats it as nothing, `rudp.rs:2059-2066`, the document server does
    not, `:266-272`), and encode failures, which are the server's own bug.
  - Rejected: ending the peer's session on its first failed send. `WouldBlock` is transient and not the
    peer's fault; ending on it would drop healthy peers under load.
- **Same shape elsewhere** (multi-peer servers only; a single-peer transport's failed send is already only
  that peer's):

| Server | Where one peer's failed send stops the others | Change |
|---|---|---|
| cultmesh-rs document server | as above | Cut D |
| Rust hub `CultNetRudpServerHub` | `poll_resends` returns on the first failure (`rudp.rs:1762-1777`); the receive path's sends to one peer abort `receive_event_once` (`:1644`, `:1673`, `:1708`, `:1711`, `:1714`, `:1759`, through `send_packet` `:1799-1804`) | Cut D |
| C# `CultNetRudpSocketTransportServer` (and `RudpCultNetSchemaServer` over it) | `PollResends` walks every peer and the first `SocketException` from `SendTo` (`CultNetTransport.cs:2829-2834`) escapes the loop (`:2796-2805`); `TryReceiveOnce`'s sends are unguarded except the refusal goodbye (`:2687`, `:2704`, `:2749-2750`, `:2788`; guarded `:2739-2745`) | Cut D |
| cultmesh-py server | worse: an `OSError` from `sendto` (`server.py:302-307`) in `_poll_rudp_resends` (`:277-285`) or the receive path escapes `_rudp_loop` (`:203-…`, which catches only `recvfrom`'s errors), and the RUDP thread dies for every peer | Cut D |
| cultmesh-ts document server | none: Node's `socket.send` is asynchronous and reports failures on the socket's `error` event (`cultmesh-ts/src/index.ts:5858`), which `reportError` logs (`:5778-5785`) | none |
| Kotlin interop server | none that matters: a catch-all logs and continues (K:4025), and it is interop-only | none (Cut K1 reviews it) |


## Cuts

Every cut is on CultLib, on its own branch (`hands/ack-cut<N>`) from `main` at `e9469cb3` or later, in a
worktree Self creates. Heavy verification runs on Yggdrasil. Each cut starts by committing its probes as failing
tests named for the rule, in every runtime it touches. The pass-1 and pass-2 probe files
(`refs/verify/1519a20a` on the `CultLib-imag-ack2` mirror) are the starting point; they print, so each
must become an asserting test.

Kotlin: every numbered cut below is mapped for C#, Rust, TS and Python. Cut K (Q-A5 A) brings Kotlin to
the same state on a Yggdrasil Kotlin image, in steps K0-K5.

**Negative checks that stay green in every cut:**

- the cross-runtime reorder, drop and fragment proofs in the TS interop harness
  (`docs/cultnet-transport-parity.md:227-248`);
- the stray-packet tests (`packages/cultnet-rs/tests/rudp_stray_packets.rs`, and their C#, TS and Python
  equivalents), except the ones each cut names as encoding the old rule;
- the codec fixture vectors: the existing fixture's bytes never change. Cut F adds one fixture.

### Cut 1. One watermark owns ordered delivery (receiver) — in flight (`hands/ack-cut1`)

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

### Cut 1b. A write belongs to the session it was issued in: every ending drops what it owed — in flight (`hands/ack-cut1`)

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

### Cut 1c. A Connect's sequence says whether it repeats — ready after Cut 1b (Q-A6 A, ruled)

Pass 3 changes against pass 2: the ruling; three more servers that decide repeats themselves (the
cultmesh-rs and cultmesh-ts document servers and the cultmesh-py server); the second C# default of 100;
the exact default mechanism; the full caller survey; `cultmesh-rs/src/lib.rs:1409` removed from the
production list (it is inside `#[cfg(test)]`, which starts at `:1140`).

- **Repo/branch:** CultLib `hands/ack-cut1c` from `main` after Cut 1b merges. It uses the ending primitive.
- **First.** Failing tests:
  - G1: a restarted client on the same address, connection id and payload is admitted and its frames
    delivered, against the Rust hub, the C# listener, server mode in every runtime, the cultmesh-rs
    document server (same `(addr, connection_id)` key), the cultmesh-ts document server (same payload) and
    the TS operation service;
  - G1b: C# and Python server mode admit a Connect from a new endpoint;
  - G2: after `connect()` on the same session, a new server session's frames are delivered;
  - a stale Accept (its ack field does not name the pending Connect) is ignored;
  - the cultmesh-py server and Rust server mode: a *retransmitted* Connect after data has flowed does not
    reset the session (today both reset on every Connect).
- **Deletes first:**
  - the transport-level repeat decisions: Rust server mode's reset on every Connect (`rudp.rs:2105-2116`);
    the Rust hub's payload comparison (`rudp.rs:1637-1647`); C# server mode's `_session.Connected` branch
    (`CultNetTransport.cs:2226-2236`); the C# listener's payload comparison (`:2670-2688`); TS's
    `movedEndpoint`/`repeated` computation (`rudp.ts:1011-1049`) and the session's `connected` shortcut
    (`rudp.ts:249-251`); Python's `connected` branch (`transport.py:1047-1053`);
  - **new in pass 3:** the cultmesh-rs document server's "same key is a repeat" arm
    (`cultmesh-rs/src/rudp_document_server.rs:413-427`, and its comment at `:414-415` that asks a fresh
    client to choose a fresh id); the cultmesh-ts document server's `connectPayload` comparison and field
    (`cultmesh-ts/src/index.ts:5794-5802`, `:5806`); the cultmesh-py server's replace-on-every-Connect
    (`cultmesh-py/src/cultmesh_py/server.py:225-240`);
  - C# and Python server mode's refusal of a Connect from a new endpoint (`CultNetTransport.cs:2212-2216`,
    `transport.py:1042-1044`), for Connect packets only.
- **Adds:**
  - the session records the sequence of the Connect that started its generation. A Connect repeats exactly
    when the session is connected and the Connect carries that sequence. `AcceptConnect` answers a repeat
    as `AnswerRepeatedConnect` does today, and otherwise ends the generation, resets peer state and accepts
    (`CultNetTransport.cs:1012-1059`, `rudp.rs:383-435`, `rudp.ts:242-290`, `transport.py:485-525`).
    `AnswerRepeatedConnect` stops being public; a public predicate `ConnectRepeats(packet)` /
    `connect_repeats` / `connectRepeats` serves servers that keep one session per peer and must report the
    old one ended (the Rust hub `rudp.rs:1648-1690`, the C# listener `:2690-2710`, the cultmesh-rs document
    server). Servers that only call `AcceptConnect` (TS operation service `operation-service.ts:167-175`,
    cultmesh-ts document server, cultmesh-py server, server-mode transports) need nothing else: the session
    decides;
  - `CreateConnect` on a session that has had a peer ends the generation and resets peer state first
    (`CultNetTransport.cs:1001-1010`, `rudp.rs:369-381`, `rudp.ts:229-240`, `transport.py:473-483`);
  - on Accept receipt, the session honours it only while its Connect is pending and only if the Accept's
    ack field names that Connect (`CultNetTransport.cs:1155-1160`, `rudp.rs:544-557`, `rudp.ts:379-390`,
    `transport.py:601-604`);
  - **the default initial sequence is drawn from a CSPRNG in [1, 2^31)** wherever a default is produced.
    The option types do not change (`u32`/`uint`/`number`/`int`), so every explicit value keeps working and
    no caller changes code. One private helper per runtime:
    - Rust: `rand::random_range(1..1u32 << 31)` (`rand` 0.9 is already a dependency,
      `packages/cultnet-rs/Cargo.toml`), in `CultNetRudpSessionOptions::default` (`rudp.rs:108`),
      `CultNetRudpSocketTransportOptions::client()` (`:1310`) and `::server()` (`:1329`),
      `CultNetRudpServerHubOptions::new()` (`:1377`), and `CultMeshRudpSocketOptions::default`
      (`cultnet-rs/src/cultmesh.rs:259`);
    - C#: `RandomNumberGenerator.GetInt32(1, int.MaxValue)` as the property initializer at
      `CultNetTransport.cs:773`, `:850`, **`:2404` (the listener's 100)**, **`CultNetRudpSchemaServer.cs:48`
      (a second 100)** and `src/GameCult.Mesh/CultMesh.cs:547`;
    - TypeScript: `crypto.getRandomValues` in the session constructor's `options.initialSequence ?? …`
      (`rudp.ts:172`); `cultmesh-ts`'s optional `initialSequence` (`index.ts:4336`) already falls through to it;
    - Python: `secrets.randbelow(2**31 - 1) + 1` as a `default_factory` at `transport.py:298`, `:323`, and
      as the default of `client.py:396` and `cultmesh-py/src/cultmesh_py/facade.py:160`, `:194` (keyword
      defaults become `None` meaning "draw one", since a Python default is evaluated once);
    - Kotlin: in Cut K2;
  - CultLib's own production pins take the default: `cultmesh-rs/src/lib.rs:964` (client),
    `cultmesh-rs/src/rudp_document_server.rs:432` (server). The two TS publishers
    (`signed-daemon-health.ts:116`, `idunn-odin-presence-publisher.ts:103`) lose theirs in whichever of
    Cut 5 or Cut 1c lands second. Interop peers, the parity tool and tests keep explicit values on purpose.
- **Tests changed** (they encode payload identity or a fixed default):
  - `packages/cultnet-rs/tests/rudp_server_hub.rs:171-240` calls `connect()` again with the same payload
    and expects no replacement; under this rule that is a new generation. Its retransmit half stays covered
    by `rudp_stray_packets.rs:487`;
  - `tests/GameCult.Networking.Tests/NetworkingTests.cs:1507` (replace a peer by payload) passes but must
    say it relies on distinct sequences; `:700` asserts `PendingReliableSequences == {100}` against the
    listener's default;
  - asserts that may rely on a default Connect sequence of 1 (read each; pin `1` in the test if it does not
    already): `cultnet-rs/tests/cultnet.rs:574`, `:820`, `:851-852`; `cultnet-ts/test/cultnet.test.ts:472`,
    `:576`, `:592-593`, `:1536`; `cultnet-py/tests/test_cultnet.py:596`, `:764`, `:779-780`;
  - `cultnet-ts/test/cultnet.test.ts:2225` (another endpoint replaces the peer) stays.
- **Verification:**
  - G1 (every server above), G1b, G2, the stale Accept and the retransmitted-Connect tests;
  - the retransmit tests stay green: `rudp_stray_packets.rs:487`, `:511`, `NetworkingTests.cs:1483`,
    `cultnet.test.ts:2119`, `:2134`, `test_cultnet.py:1134`;
  - a test per runtime that two default-constructed sessions draw different initial sequences in [1, 2^31);
  - negative grep: `rg -n "SequenceEqual\(connectPayload\)|connect_payload == packet.payload|movedEndpoint|byteArraysEqual\(record.connectPayload" src packages`
    returns nothing;
  - mutation testing on the diff.
- **Ledger.** About −90 and +85 across four runtimes and the three servers. Two CultLib production pins go.

**Pin-bump notes: callers outside CultLib that pin an initial sequence** (survey, 2026-09-30). A pinned
value keeps a deterministic Connect sequence. It matters on a **client that can restart on the same
endpoint and connection id** (the G1 lockout survives there); on a server or a one-shot client that binds a
fresh port per run it only forgoes the stale-packet protection. Recommendation at each repo's bump: delete
the value.

| Repo@rev | Site | Value | Role | Consumes CultLib by |
|---|---|---|---|---|
| Muninn@`bd69598` | `crates/muninn-daemon/src/main.rs:5975`; `crates/sleipnir-daemon/src/main.rs:2223` | 1 | client (restart risk) | Cargo pin `c2a9a6e5` |
| Muninn@`bd69598` | `muninn-daemon/src/main.rs:780`, `:5426`; `sleipnir-daemon/src/main.rs:2265` | 1 | server-mode transports | same |
| Gjallar@`6a92976` | `src/Gjallar/Program.cs:431`, `:2955`, `:3027` | 1 | clients (restart risk) | sibling `../CultLib` |
| Mimir@`d88f2e6` | `src/Mimir.EveBrowserReference/Program.cs:682`, `src/Mimir.EveDashboard/Program.cs:801` | 1 | clients (restart risk) | sibling ProjectReference |
| Mimir@`d88f2e6` | `src/Mimir.CultMeshMedia/Program.cs:485` | `peers.Count + 1` | hand-rolled per-peer relay; its Connect handling (`:365`) is its own repeat decision | same |
| weksa@`d16cff5` | `scripts/weksa-rudp-command.cjs:88` (per-peer server loop), `:192` (client); `scripts/idunn-rudp-health.cjs:43` | 1 | server loop and clients | sibling CultLib |
| Bifrost@`42728a6` | `tools/persona-discord-rudp.mjs:9` | 100 | server-mode transport | sibling CultLib |
| Heimdall@`e8e2832` | `src/idunn-rudp-health.ts:65` | 1 | one-shot health client (replaced at its bump, § Sequencing) | submodule `b6b1d9c` |
| Stonks@`981c484`, Vili@`8773c15`, VoidBot@`46d891b` | `src/idunn-rudp.cjs:43`, `scripts/idunn-rudp.cjs:33`, `scripts/publish-idunn-rudp-health.cjs:64` | 1 | one-shot health clients | `file:../CultLib` / sibling path |

- None in Odin `origin/main` (`5c37860`; Sleipnir now lives in Muninn), Idunn, Hermodr, CodexConnector,
  Ghostlight, Huginn or Epiphany. StreamPixels' own code has none; its vendored CultLib (`542ddd3`, at
  StreamPixels `a49f7d8`) carries CultLib's own old defaults and pins, which a bump replaces.
- Sibling-path consumers (Gjallar, Mimir, Stonks, Vili, VoidBot, weksa, Bifrost) take the random default
  as soon as the local CultLib checkout moves; their pinned sites do not change behaviour until edited.

### Cut 2. A reliable fragment set is never evicted (receiver: Rust, TS, Python) — in flight (`hands/ack-cut2`); merges with Cut F

- **First.** Failing test P5 in each of the three.
- **Change:**
  - eviction picks the stalest set that holds no reliable fragment (`rudp.rs:1027-1047`,
    `rudp.ts:679-692`, `transport.py:836-842`);
  - if every pending set holds reliable fragments, the packet is refused and the session ends, as every
    other refusal does;
  - ~~a conforming sender cannot reach that bound~~ (false for an expiring sender; see the pass 3
    correction below): its window keeps at most 32 reliable packets in flight, and the bound is 64 sets.
- **Verification:** P5 delivers the reliable frame. `d3bc6e66`'s test (lossy fragment sets keep being
  evicted) stays green. `fragment_sets_evicted` still counts evictions.
- **Ledger.** About −3 and +12 per runtime.
- **Pass 3 correction (from the Cut 2 Soul pass on `hands/ack-cut2` `c5026aa1`).** The third bullet above
  is false for an expiring sender: the default Rust `media` channel expires reliable packets after 75 ms,
  a frame's first fragment is delivered and acked, the rest are dropped by the deadline, and the set is
  pinned; Soul's `soul_probe_expiring_reliable_frames_kill_receiver` (`packages/cultnet-rs/tests/cultnet.rs:3192`
  at `3be97b34`) refuses the session on the 65th such frame, where eviction used to keep it alive.
  - **What changes for the branch in flight:** nothing in its code. Its rule is right for every sender that
    does not expire. What changes is when it merges: **Cut 2 merges only together with Cut F**, which
    releases dead sets through notices and (Q-A9 B) keeps pre-Cut-2 eviction for a peer that never sends
    notices. Soul's probe becomes Cut F's regression test.
  - C# (`CultNetTransport.cs:921`) and Kotlin (K:2645) have no fragment-set bound at all. **Cut 2c** brings
    C# to the same bound; Kotlin gets it in K1 and the gated rule in K3.

### Cut 2c. C# gets the fragment-set bound (with Cut 2's rule) — merges with Cuts 2 and F

- **Why in this campaign.** Rust, TS and Python bound pending fragment sets at 64 (`d3bc6e66` lineage;
  `rudp.rs:328-340`, `rudp.ts:167`, `transport.py:430`); C# keeps `_fragmentBuffers`
  (`CultNetTransport.cs:921`) unbounded, so a hostile peer grows it without limit and the parity doc cannot
  call C# conforming. A runtime missing a sibling's protection is a gap to fill.
- **Repo/branch:** CultLib `hands/ack-cut2c`, from `main`; merges together with Cuts 2 and F (the gate
  lives in F).
- **First.** C# ports of `d3bc6e66`'s lossy-set test and Cut 2's P5 test.
- **Adds:** `MaxPendingFragmentSets` (default 64, a setter as Rust's `set_max_pending_fragment_sets`,
  `rudp.rs:328-340`), a per-set last-touched ordinal, eviction of the stalest set in `Reassemble`
  (`:1556`), `FragmentSetsEvicted` beside `OutstandingReliablePacketCount` (`:993`), and Cut 2's rule (a
  reliable set is not evicted; if every set is reliable the session is refused through `EndRefused`),
  gated as in Cut F.
- **Verification:** the two tests; `GameCult.Networking.Tests` green; Stryker.NET on the diff.
- **Ledger.** About +45 and 0.

### Cut 3. The sender's flow window: 1,023 sequences and 4 MiB above the lowest unacked — ready (merge after Cut 1)

- **First.** Failing tests P8 (all runtimes) and P4 (Rust receiver, with a sender from each runtime).
- **Change.** Admission (`AdmitReliablePackets` / `PromoteQueuedReliable` at
  `CultNetTransport.cs:1439-1490`, `rudp.rs:910-942`, `rudp.ts:586-600`, `transport.py:776-796`,
  `CultMesh.kt:2856-2872`) admits a packet only while:
  - the window has a slot (as today);
  - its sequence is at most the lowest unacked sequence plus 1,023;
  - the admitted payload bytes above the lowest unacked sequence stay within 4 MiB.
  - The first packet is always admissible. Expired packets (Rust) do not count as unacked here: they are
    gone from the wire. (Cut F refines this: an abandoned sequence counts while its notice is outstanding,
    and stops counting once acknowledged or stranded.)
- **Contract.** `docs/cultnet-transport-parity.md` (after `:263`) states the receive window: a receiver
  must hold 1,023 sequences and 4 MiB beyond its contiguous watermark, and a conforming sender never
  exceeds that.
- **Verification:**
  - P8: `g` is delivered;
  - P4: the 5 MiB frame is delivered after the gap fills, and the session is never refused;
  - the existing large-snapshot tests (`c398bad1`) and the fragment interop stay green;
  - the Rust benchmark or media probe shows no throughput change on a lossless link.
- **Ledger.** About +25 per runtime, and nothing removed.

### Cut D. A failed send to one peer is that peer's lost datagram (Rust document server and hub, C# listener, cultmesh-py server) — ready

Design in §10. Independent of every other cut; it touches send sites, not session logic.

- **Repo/branch:** CultLib `hands/ack-cutD` from `main` at `e9469cb3` or later. Merge cost only with Cut 1c
  in the Rust hub's and C# listener's Connect arms.
- **First.** Failing tests, one per server, each with two peers where every send to peer X fails:
  - cultmesh-rs document server: `poll_once` never returns `Err`; peer Y connects, publishes, is acked and
    gets a snapshot response; `maintain` returns `Ok`; the failure count rises; X's session ends by idle
    timeout, not by the failure. Also: `recv_from` returning `ConnectionReset` is `Idle`;
  - Rust hub: `poll_resends` and `receive_event_once` return `Ok` and keep serving Y;
  - C# listener: `PollResends` reaches Y after X throws; `TryReceiveOnce` serves Y;
  - cultmesh-py server: the RUDP thread survives an `OSError` from `sendto` and keeps serving Y.
  - **The failure seam.** The Rust servers own a `UdpSocket` directly, and a loopback peer's address
    cannot be made to fail on demand. Add the narrowest seam at the one send function: a
    `#[cfg(test)]` set of addresses whose sends fail with a synthetic `io::Error` in `send_packet`
    (`rudp_document_server.rs:628-632`, `rudp.rs:1799-1804`), and the same as an internal test hook in C#
    `SendPacket` (`CultNetTransport.cs:2829-2834`). Python patches `socket.sendto` in the test. A socket
    port trait would be the full dependency-injection answer; it is not bought here, because the only
    consumer is this test.
- **Changes:**
  - cultmesh-rs `rudp_document_server.rs`: `send_packet` (`:628-632`) keeps encode errors as errors and turns
    a `send_to` failure into a counted loss; every per-peer call site drops its `?` on the send
    (`:335`, `:373`, `:397`, `:451`, `:587`, `:603`); a `send_failures()` accessor beside `packets_dropped()`
    (`:246-248`); `recv_from` treats `ConnectionReset` like `WouldBlock` (`:266-272`), as the socket
    transport does (`cultnet-rs/src/rudp.rs:2059-2066`);
  - cultnet-rs hub: `send_packet` (`rudp.rs:1799-1804`) likewise, counted in `CultNetTransportStats`
    as a new `send_failures` field; `poll_resends` (`:1762-1777`) and the receive path (`:1644`, `:1673`,
    `:1708`, `:1711`, `:1714`, `:1759`) stop propagating it;
  - C# `CultNetRudpSocketTransportServer`: `SendPacket` (`:2829-2834`) catches `SocketException`, counts it
    in `CultNetTransportStats.SendFailures`, and returns; `PollResends` (`:2796-2805`) and `TryReceiveOnce`'s
    sends (`:2687`, `:2704`, `:2749-2750`, `:2788`) need no other change; the refusal goodbye's own catch
    (`:2739-2745`) becomes redundant and goes;
  - cultmesh-py `server.py`: `_send_rudp_packet` (`:302-307`) catches `OSError` and counts it.
- **Not changed:** single-peer socket transports (a failed send already concerns only their one peer, and
  their callers, flush among them, get the error); `cultmesh-ts`'s document server (Node reports send
  failures on the socket's `error` event, `index.ts:5858`).
- **Authority map:**
  - Owner: each peer's session owns its peer's fate (idle timeout, lifetime, refusal, Disconnect). A send
    failure is an input to nothing but a counter.
  - Forbidden writers: no per-peer send failure ends a poll, a loop, a thread or another peer's session.
- **Verification:**
  - the four tests; the full `cultmesh-rs`, `cultnet-rs`, `GameCult.Networking.Tests` and `cultmesh-py`
    suites;
  - negative: `rg -n "send_packet\(.*\)\?;" packages/cultmesh-rs/src/rudp_document_server.rs` returns
    nothing (the pattern matches only a propagated send; encode errors propagate inside `send_packet`);
  - operator: an Odin build on this CultLib keeps serving presence while one peer's address is
    unroutable.
- **Ledger.** About −15 and +40 (the counters, the seams); tests about +120.

### Cut F. Abandonment notices (FORWARD-TSN, RFC 3758): every receiver honours one, Rust sends one — blocked on Cuts 1 and 3, on Q-A7 and Q-A9; merges together with Cut 2

Design in §6. Mapped for the recommended answers to Q-A7 (one sequence per notice) and Q-A9 (a capability
bit gates Cut 2's rule).

- **Repo/branch:** CultLib `hands/ack-cutF` from `hands/ack-cut2` rebased on `main` after Cuts 1 and 3
  merge. Cut 1 gives the receiver `R` and the drain the notice feeds; Cut 2 is the fragment code the notice
  drops sets from and the rule the capability gates; Cut 3 is the admission rule the notice amends. Cut 2
  and Cut F merge to `main` together (the Cut 2 Soul finding: Cut 2 alone refuses sessions with expiring
  senders). Kotlin's half is Cut K3.
- **First.** Failing tests, in C#, Rust, TypeScript and Python:
  - the golden: `Abandon` encodes to the §6 bytes and decodes back, beside the existing fixture
    (`packages/cultnet-rs/tests/cultnet.rs:319`, `packages/cultnet-ts/test/cultnet.test.ts:311`,
    `packages/cultnet-py/tests/test_cultnet.py:409`, `tests/GameCult.Networking.Tests/NetworkingTests.cs:366`);
  - a type-4 packet with bit 4 decodes as `Abandon`; bit 4 on a Data packet is still ignored;
  - receiver: a notice for a hole advances `R`, drains the held ordered frames behind it, and replies with
    the ack for that sequence (FN, inverted: `[o3]` delivered by the notice, `o2` marked received);
  - receiver: a notice for a received sequence changes nothing and still replies;
  - receiver: a notice inside an incomplete fragment set's span drops the set;
  - receiver: a notice more than 1,024 above the highest received is ignored with no reply;
  - receiver: the late original after its notice is a duplicate: acked, not delivered;
  - Rust sender: an expired pending or queued packet produces a notice at once and then once per resend
    delay; an ack naming the sequence stops it; after 16 unacknowledged sends it stops and records
    `first_stranded`; every ending clears both;
  - Rust end to end (P2 inverted): reliable `media` with expiry is lost, then ordered `s1`, `s2` on
    `schema`; after the notice both are delivered and `flush_reliable` succeeds;
  - Rust, Cut 3's window: an outstanding notice holds the lowest-unacked point; a stranded sequence does not.
  - **the Cut 2 regression**, from Soul's `soul_probe_expiring_reliable_frames_kill_receiver`
    (`packages/cultnet-rs/tests/cultnet.rs:3192` at `3be97b34`): 100 default `media` frames, each losing
    every fragment but the first. With a Cut F sender the receiver's pending sets return to zero after the
    notices and the session lives; with a stand-in sender that never advertises the capability nor sends
    notices (Q-A9 B), the receiver evicts the stalest set as before Cut 2 and the session lives; with a
    capable peer that stops sending notices, the 65th pinned set still refuses (Cut 2's rule holds where it
    is true);
  - (Q-A9 B) a Connect or Accept with flag bit 4 records the peer as capable; one without does not; an old
    peer's accept of a flagged Connect works (the pass 3 CAP probe, kept as a test).
- **Deletes first:**
  - Rust: the plain drop in `purge_expired_reliable` (`rudp.rs:898-908`; the `retain` calls become a move
    into the abandoned set; the expired counter stays);
  - the doc on `reliable_expire_after_ms` (`rudp.rs:121-124`), which says the packet is dropped;
  - `packages/cultnet-py/README.md:103-111`'s claim that the Python session has reliable expiry. It has
    none; the claim is false today.
- **Adds, receiver (C#, Rust, TS, Python):**
  - the `Abandon` packet type and its codec mapping: Rust enum `rudp.rs:51-59`, `encode_flags`
    `:2466-2475`, `packet_type_to_code` `:2477`, `decode_rudp_packet` `:2448-2455`, `packet_type_from_code`
    `:2571`; C# `CultNetRudpPacketType` (`CultNetTransport.cs:509`) and `CultNetRudpPacketCodec` `EncodeFlags` `CultNetTransport.cs:697`,
    `Decode` `:676-690` (its `Enum.IsDefined` check runs on the wire byte, which stays 4); TS union
    `rudp.ts:26` and the codec at `:1264-1320`; Python enum `transport.py:250`, code table `:342`, codec
    `:1175-1240`;
  - the `Abandon` branch in `receive`, after acks are applied and beside the Ack/Pong branch
    (`rudp.rs:578-592`, `CultNetTransport.cs:1177-1187`, `rudp.ts:397`, `transport.py:612`): the range
    check, the fragment-set drop over the fragment buffers (`rudp.rs:1001-1114`, `CultNetTransport.cs:1556`,
    `rudp.ts:643`, `transport.py:825`), `RememberReceived` and the Cut 1 drain, and
    `reply = create_ack_for_received(sequence)` (`rudp.rs:751-758`, `CultNetTransport.cs:1273-1279`,
    `rudp.ts:492-497`, `transport.py:685`);
  - transports skip their own ack for an `Abandon` packet where a delivering receive would draw one:
    `rudp.ts:1089`, `transport.py:1087` (Rust and C# ack only reliable, Accept or Data and need nothing).
- **Adds, capability (Q-A9 B; all four runtimes):**
  - `Connect` and `Accept` carry flag bit 4; the session records whether its peer's handshake packet had
    it, and resets that with peer state. `create_connect` (`rudp.rs:369-381`, `CultNetTransport.cs:1001-1010`,
    `rudp.ts:229-240`, `transport.py:473-483`) and `accept_connect` (`rudp.rs:383-411`,
    `CultNetTransport.cs:1012-1059`, `rudp.ts:242-290`, `transport.py:485-525`) set it; the codec keeps bit 4
    on those types (today every decoder drops it);
  - the fragment-set bound (Cut 2's eviction, `rudp.rs:1027-1047`, `rudp.ts:679-692`, `transport.py:836-842`
    as Cut 2 leaves them) applies the never-evict rule to a capable peer and the pre-Cut-2 stalest-set
    eviction to one that is not;
  - the sender sends notices only to a capable peer; for another, an expired sequence is stranded at once.
- **Adds, sender (Rust):**
  - `RUDP_ABANDON_NOTICE_SENDS = 16` beside `DEFAULT_MEDIA_RELIABLE_EXPIRE_AFTER_MS` (`rudp.rs:39`);
  - the abandoned set and `first_stranded` on the session (fields beside `pending_reliable`,
    `queued_reliable`, `rudp.rs:193-195`);
  - `purge_expired_reliable` moves expired pending and queued sequences into the set;
  - `due_resends` (`:797-807`) emits due notices after due packets and retires entries at the bound;
  - `apply_acknowledgements` (`:964-971`) removes named sequences from the set;
  - `outstanding_reliable_packet_count` (`:270-272`) counts the set;
  - the ending primitive (Cut 1b) and `reset_peer_state` (`:344-358`) clear both;
  - Cut 3's lowest-unacked point (in `admit_reliable_packets` / `promote_queued_reliable`, `:910-942` as
    Cut 3 leaves them) includes the set and excludes `first_stranded`.
- **Docs.** `docs/cultnet-transport-parity.md`: the packet-type and flags rows of the header table
  (`:486-487`) gain "`4` with flag bit 4 = abandon"; the golden goes after the canonical fixture
  (`:505-517`); the bounded-send paragraph (`:260-273`) gains the notice rule and its resend bound.
- **Authority map:**
  - Owner: the sender session's abandoned set decides which sequences are announced and for how long; the
    receiver session decides what a notice changes.
  - Inputs: expiry (sender); a decoded `Abandon` packet in the current generation (receiver).
  - Outputs: notices (sender); a remembered sequence, drained frames and one ack (receiver).
  - Derived: `first_stranded` is derived from the set's retirements and read by Cut 4; the expired counter
    is report-only.
  - Forbidden writers: nothing marks a sequence received without a packet except the `Abandon` branch; no
    notice outlives its generation; no transport interprets bit 4.
  - Shared paths: data, fragments, duplicates, Accept and notices all advance `R` through
    `RememberReceived` and the one drain.
  - Deletion line: the plain drop in `purge_expired_reliable` goes before the set is added.
- **Verification:**
  - the tests above in four runtimes;
  - FN re-run against the new build as a **negative** check: an old-receiver stand-in (a session that drops
    `Abandon` packets) never acks, and the Rust sender strands the sequence after 16 sends;
  - the TS interop harness gains one lane: a Rust peer sends reliable `media` with expiry whose first
    packet the bridge drops, then ordered `schema` frames; the TypeScript receiver delivers the `schema`
    frames (`packages/cultnet-ts/test/interop/cultnet-interop.test.ts`, beside the drop lanes);
  - negative grep: `rg -n "first_abandoned" packages src` returns nothing (pass 2's name must not appear);
  - mutation testing on the diff.
- **Ledger.** Receiver about +40 per runtime (four); Rust sender about +60 and −6; the golden about +25
  per runtime; docs +15 and −8 (the false Python claim).

### Cut 4. Rust receipts: Acknowledged means delivered, resolved once; honest stranding and queueing — blocked on Cuts 1, 1b and F

Re-mapped in pass 3 for Q-A3 B. Pass 2's `first_abandoned` (every expiry leaves a permanent hole) is
replaced by Cut F's `first_stranded` (only a notice the receiver never acknowledged leaves one). The rest is
pass 2's cut.

- **Repo/branch:** CultLib `hands/ack-cut4` from `main` after Cuts 1, 1b and F merge.
- **First.** Failing tests:
  - P3: the receipt for `s2` reads `Pending` until `s1` arrives, then `Acknowledged`;
  - P9 (`send_reliable` on an expiring channel is refused) and P10 (a window-full `send_reliable` returns a
    receipt covering its queued packets, and the frame is sent once);
  - a receipt `Acknowledged` before any ending stays `Acknowledged` after the ending and after a reconnect;
    a `Pending` one becomes `Invalidated`;
  - **P2 with a new receiver:** reliable `media` with expiry is lost, then ordered `s1`, `s2`; their
    receipts read `Pending` while the notice is outstanding and `Acknowledged` once it is acked;
    `flush_reliable` succeeds;
  - **P2 with a deaf receiver** (a stand-in session that drops `Abandon` packets): after 16 notice sends the
    receipts for `s1` and `s2` stay `Pending`, `flush_reliable` fails at once naming the stranded sequence,
    and the ending makes them `Invalidated`.
- **Deletes first:**
  - the session scope (`rudp.rs:135`, `:179`, `:206`, `:283-285`, the check at `:291-293`, and what Cut 1b
    left of the rotations);
  - the stale comment and the half guard at `rudp.rs:1915-1920`;
  - the hand-rolled status loops at `cultmesh-rs/src/lib.rs:806-838` and `:870-900`.
- **Adds:**
  - the receipt's status cell and ordered flag; the session's unresolved-receipt list, resolved once
    (Acknowledged through `A`, Invalidated by the ending primitive);
  - `acked_through` (`A`): one below the lowest sequence that is pending, queued, in Cut F's abandoned set,
    or `first_stranded`;
  - the receipt covers queued sequences: `send_many` (`:456`) reports every sequence it assigned;
  - `send_reliable` (`:1903-1930`) refuses a channel with expiry;
  - `wait_acknowledged`;
  - `flush_reliable` fails fast when an ordered send follows `first_stranded`, and otherwise waits while
    notices are outstanding (Cut F counts them in `outstanding_reliable_packet_count`).
- **Unchanged:** `CultNetRudpReliableSendStatus` (`:146-150`), `reliable_send_status`'s signature
  (`:1931`), and every caller of `Acknowledged`: `tests/rudp_stray_packets.rs:309`,
  `cultmesh-rs/tests/rudp_document_server.rs:408,415`, CodexConnector, Ghostlight.
- **Changes:** `cultmesh-rs/src/lib.rs:832` and `:892` become `wait_acknowledged`; the doc at `:869` says
  delivered to the document server's sink.
- **Verification:**
  - the new tests pass;
  - `rg -n "session_scope|first_abandoned" packages/cultnet-rs` has no hit;
  - both suites are green;
  - cargo-mutants on the diff.
- **Ledger.** About −90 (two loops of about 30 each, the scope) and +85 (of which `wait_acknowledged` is
  about 35). Five lines smaller than pass 2: the abandoned bookkeeping moved to Cut F.

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

### Cuts 6a, 6b, 6c. Receipts in C#, TypeScript and Python — blocked on Cut 4, and on Cut 1b in each runtime

Design and public API in §9. One cut per runtime, in parallel worktrees, so Soul can falsify each alone.
Kotlin's is Cut K4.

- **Repo/branch:** CultLib `hands/ack-cut6a` (C#), `hands/ack-cut6b` (TS), `hands/ack-cut6c` (Python), each
  from `main` after Cut 4 merges. Cut 4 is the reference; Cut 1b supplies each runtime's ending primitive.
- **First.** Failing tests, in each runtime:
  - an ordered receipt reads Pending while a sequence below it is missing, then Acknowledged (P3);
  - a send made while the window is full returns a receipt that covers its queued packets and becomes
    Acknowledged; the frame is sent once (P10's shape);
  - an Acknowledged receipt stays Acknowledged after the session ends and after a reconnect; a Pending one
    becomes Invalidated and stays so;
  - the wait returns on Acknowledged, fails at once on Invalidated with the end reason, and times out;
  - `SendReliable` on an unreliable channel is refused.
- **Deletes first:** nothing. These runtimes have no receipt code to replace.
- **Adds, per runtime:**
  - C# (6a): `CultNetRudpSendReceipt`, `CultNetRudpSendStatus`; in `CultNetRudpSession`, a send path that
    records every sequence `SendMany` assigns (`:1075-1140`, before `AdmitReliablePackets` `:1439-1465`
    splits them), the unresolved list, resolution after `ApplyAcknowledgements` (`:1503-1516`) and in the
    Cut 1b primitive, and `A` over `_pendingReliable` and `_queuedReliable`; on the socket transport,
    `SendReliable`, `WaitAcknowledged` (the `FlushReliable` loop, `:1914-1955`), and `SendSchema`
    (`:1960`, `:1968`) and `SendSchemaMessage<T>` (`:1976`) returning the receipt;
  - TS (6b): `CultNetRudpSendReceipt`, the status union; session changes at `sendMany` (`rudp.ts:304-368`),
    `#admitReliablePackets` (`:586-592`), `#applyAcknowledgements` (`:610-617`); transport
    `sendReliable` and `waitAcknowledged` beside `send` (`:899-909`) and `flush` (`:911-935`); exported from
    the package index;
  - Python (6c): `CultNetRudpSendReceipt`, `CultNetRudpSendStatus`; session changes at `send_many`
    (`transport.py:538-588`), `_admit_reliable_packets` (`:776-786`), `_apply_acknowledgements`
    (`:803-807`); transport `send_reliable` and `wait_acknowledged` beside `send` (`:1000-1009`) and
    `flush_reliable` (`:1101-1131`); exported from `cultnet_py`;
  - README sections per §9, and the parity doc's flush paragraph.
- **Authority map:** the session owns each receipt's status and writes it once; the transport reads it.
  Forbidden writers: no transport, flush or timer writes a status; no receipt resolves from a packet's own
  ack when it is ordered (only through `A`).
- **Verification:** the tests above; each runtime's full suite; the Cut 4 behaviour table held side by side
  with Rust's (same inputs, same states); negative: in each runtime, the only assignment to the status is
  in the session's resolve and end paths (`rg` for the status field outside those two functions returns
  nothing); mutation testing on each diff (Stryker.NET, StrykerJS, mutmut).
- **Ledger.** About +90 per runtime, nothing removed; tests about +80 per runtime.

### Cut 7. Reliable expiry in C#, TypeScript and Python — blocked on Q-A8 and Cut F

Mapped only for Q-A8's recommended answer A. Each runtime gains Rust's send option (a per-send reliable
expiry, and the profile's `media_reliable_expire_after_ms`, `rudp.rs:2492-2508`) and Cut F's sender half
(the abandoned set, notices at the resend delay, the 16-send bound, `first_stranded`), and its receipts
(Cut 6) refuse an expiring channel. Kotlin's is K5. About +70 per runtime. If Q-A8 is B, this cut is not
built and the parity doc records Rust as the only runtime with partial reliability.

### Cut K. Kotlin catches up (Q-A5 A, ruled) — K0 ready now; K1-K5 trail the four-runtime cuts

`CultMesh.kt` (6,332 lines, the only RUDP file) has none of the `3bf1c0c` stray-packet hardening, the P1
and P6 receiver, no reset path and no repeated-Connect answer. K is split so each step is falsifiable on its
own; the anchors below are `e9469cb3`, where `CultMesh.kt` is identical to `3bf1c0c`. Tests follow the
file's existing pattern (private self-test functions called from `main`, K:3349-3373); moving them out of
the production source is a follow-up.

**K0. The Kotlin verify image and a Linux build path — ready now.**
- **Stopgap tooling** (`C:\Users\Meta\.claude\skills\eureka\tools\stopgap\`, owned by Self, not a CultLib
  commit; it dies with `ygg-verify.sh`):
  - `kotlin.Dockerfile`: `FROM eclipse-temurin:21-jdk`; `apt-get install unzip curl`; fetch
    `https://github.com/JetBrains/kotlin/releases/download/v2.2.21/kotlin-compiler-2.2.21.zip` (the
    version CI pins, `.github/workflows/cultnet-interop.yml:20`) into `/opt/kotlinc`; `ENV
    KOTLIN_HOME=/opt/kotlinc PATH=/opt/kotlinc/bin:$PATH`; Node 24 copied from the `node:24` image
    (`COPY --from=node:24 /usr/local /usr/local`) for the interop lane; PowerShell 7 (`pwsh`, Microsoft's
    Debian package) for `build.ps1`. Pass 3's probe proved the JDK-plus-compiler half: the self-tests exit 0
    (K-img).
  - `ygg-verify.sh`: a `kotlin` image alias tagged by that Dockerfile's hash, and the remote build step
    generalised from `eureka-verify-rust:*` to `eureka-verify-<name>:*` built from `<name>.Dockerfile`
    (today it builds and copies only `rust.Dockerfile`). Edit it by writing a temp file and renaming it over
    the old one (the script's own rake).
- **CultLib:**
  - `packages/cultmesh-kotlin/build.ps1` becomes portable PowerShell 7: `kotlinc` or `kotlinc.bat` by
    platform, `[IO.Path]::PathSeparator` for the classpath, `java` or `java.exe`. One build script stays the
    one owner of the Kotlin build; no `build.sh` beside it.
  - The interop harness calls `pwsh` off Windows instead of `powershell`
    (`packages/cultnet-ts/test/interop/cultnet-interop.test.ts:2799-2809`); it already picks `java` off
    Windows (`:31-36`).
  - `docs/cultnet-transport-parity.md` says, beside the runtime table (`:35`) and the RUDP claims
    (`:205-273`), that Kotlin lacks the `3bf1c0c` hardening and the ack cuts until Cut K lands, so nobody
    reads it as conforming meanwhile.
- **Verification:** `ygg-verify.sh <CultLib> <rev> kotlin 'cd packages/cultmesh-kotlin && pwsh -File build.ps1'`
  runs the self-tests green; the interop lane
  (`node --test --test-name-pattern "TypeScript and Kotlin|Kotlin and TypeScript"`) runs green on the same
  image; CI on `windows-latest` stays green with the same script.

**K1. Stray-packet parity** (the `3bf1c0c` behaviours the other four have, each with its test ported from
the C# or TS equivalent):
- a malformed, foreign-id or refused datagram is dropped and counted, never thrown out of `receiveOnce`
  (K:3223, K:3228, K:2724, K:2567), with a `packetsDropped` stat (`CultNetTransportStats`, K:774);
- the connection id is checked before the endpoint is claimed, and in server mode only a Connect claims it
  (K:3217-3222);
- `resetPeerState` and `endRefusedSession`: the refusing session resets, builds its goodbye after the
  reset, and a server forgets its endpoint; a refused packet's sequence is remembered only after
  reassembly accepts it (today K:2758 remembers before `reassemble` can throw, K:2915-2922);
- `answerRepeatedConnect`: a repeated Connect queues nothing and is answered with the owed Accept or an Ack
  (today every Connect makes a new tracked Accept, K:2667-2676, K:3224-3226, K:4005-4006);
- a Connect from another endpoint replaces the peer in server mode (today ignored, K:3220-3221);
- a peer's Disconnect drops owed writes (K:2745-2748); `connect` clears a stale disconnect reason (K:3106);
- `initialSequence` of `0xFFFFFFFF` is refused at construction (K:2631) and sequence exhaustion is checked
  before allocation (K:2841, K:2698-2711);
- the decoder's negative declared length (K:3306-3317) is an ordinary malformed drop;
- the fragment-set bound of 64 (K:2645, K:2911-2937 has none): the stalest set is evicted. Cut 2's
  never-evict rule and its capability gate come in K3 with Cut F, as in the other runtimes.

**K2. Cuts 1, 1b and 1c.**
- Cut 1: delete `orderedNextSequenceByChannel` (K:2643), `expectedSequenceIfUninitialized` (K:2728),
  `deliverOrdered`/`drainOrdered`/`skipReceivedNonChannelSequences` (K:2939-2978); seed `R` in
  `acceptConnect` (K:2667) and on Accept (K:2730); advance in `rememberReceived` (K:2891).
- Cut 1b: the ending primitive from a peer's Disconnect (K:2745), `createDisconnect` (K:2809),
  `checkTimeout` (K:2814-2819) and K1's refusal; `flushReliable` (K:3186-3205) binds to the generation.
- Cut 1c: random defaults at K:2600, K:2996, K:3078 and the server's 100 at K:3016
  (`java.security.SecureRandom`); the session's repeat decision replaces server mode's
  every-Connect `acceptConnect` (K:3224-3226) and the interop server's (K:4005); `createConnect` (K:2660)
  and the Accept check (K:2730).

**K3. Cuts 2, 3 and F.** Admission at K:2856-2872 gains the flow window; Cut 2's reliable-set rule behind
the Q-A9 capability; the `Abandon` type and receiver branch (K:2556-2567, K:2738, K:3296-3325), the golden beside `rudpPacketCodecUsesDeterministicReliableOrderedFixture`
(K:5236), and the transport skips its own ack for `Abandon` (K:3243-3245).

**K4. Receipts** (§9): `sendReliable`, `CultNetRudpSendReceipt`, `CultNetRudpSendStatus`, `waitAcknowledged`,
at `sendMany` (K:2685-2719), `admitReliablePackets` (K:2856-2862), `applyAcknowledgements` (K:2882-2889)
and the transport (K:3132-3205).

**K5. Reliable expiry and the notice sender** — only if Q-A8 is A (as Cut 7).

- **Verification, each step:** the Kotlin image's self-tests and interop lane; the step's ported tests;
  a negative grep per step (K2: `rg -n "orderedNextSequenceByChannel|expectedSequenceIfUninitialized"
  packages/cultmesh-kotlin` returns nothing).
- **Ledger.** K1 about +180 and −40; K2 about −70 and +90; K3 about +80; K4 about +90; K5 about +70.


## Subtraction ledger (estimate)

| Cut | Removed | Added | Other |
|---|---|---|---|
| 1 | ~280 | ~160 | four per-channel state machines become one watermark |
| 1b | ~40 | ~45 | one ending primitive; TS's transport generation pair goes |
| 1c | ~90 | ~85 | seven transport- and server-level repeat decisions become one session predicate; two CultLib production pins go |
| 2 | ~10 | ~35 | none; merges with Cut F |
| 2c | 0 | ~45 | C# gains the fragment-set bound the other runtimes have |
| 3 | 0 | ~100 | a documented receive-window contract |
| D | ~15 | ~40 | one failure counter in three servers; a test seam at each send function |
| F | ~15 | ~350 | **wire: one packet type (`Abandon` = Ack + flag bit 4) and one golden in every runtime; with Q-A9 B, the same bit on Connect and Accept**; the false Python expiry claim goes |
| 4 | ~90 | ~85 | one public method (`wait_acknowledged`); the session scope goes; no enum change |
| 5 | ~270 | ~50 | two hand-rolled RUDP loops and cultmesh-ts's connect wait go |
| 6a-6c | 0 | ~270 | public receipt API in three runtimes (Q-A4 A) |
| 7 | 0 | ~210 | only if Q-A8 is A: partial reliability in three runtimes |
| K0-K5 | ~110 | ~510 | Kotlin reaches parity; one verify image; `build.ps1` portable |

- **Source, net, four runtimes without Cut 7:** about +325. Pass 2 was about −195; the rulings bought
  FORWARD-TSN with its capability gate (about +335 net) and receipts in three runtimes (about +270); Cut D
  adds about +25 and Cut 2c about +45. Tests are extra:
  about 4 × 25 rule tests.
- **Kotlin:** about +400 net, most of it the `3bf1c0c` hardening the other four already carry.
- **Wire:** one additive packet type, ignored by every current peer (FN).
- **Downstream:** no caller has to change code at its next bump. Callers that pin `initial_sequence` keep
  a deterministic Connect sequence until they drop the value (Cut 1c, pin-bump notes).


## Operator questions

Each question stands alone.

**Ruled (2026-09-30), recorded so they are not re-asked:**

- **Q-A1. Where does delivered come from?** (a): derived at the sender from the cumulative watermark.
- **Q-A2. One flush, or a "received" flush beside it?** One meaning: Acknowledged means delivered.
- **Q-A3. Reliable expiry next to ordered traffic (P2)?** B: FORWARD-TSN now (Cut F). The recommendation
  was A (report honestly, document "don't mix").
- **Q-A4. Receipts in C#, TypeScript, Python and Kotlin?** A: port them in this campaign, after Cut 4
  (Cuts 6a-6c, K4). The recommendation was B (record a parity gap).
- **Q-A5. Is Kotlin in the campaign?** A: yes, as trailing Cut K on a Yggdrasil Kotlin image (K0).
- **Q-A6. How does a session recognise a new generation?** A: the Connect's sequence identifies it; the
  default initial sequence is random in [1, 2^31) (Cut 1c).

**Open:**

- **Q-A7. Does an abandonment notice name one sequence, or a new cumulative point? (new in pass 3)**
  RFC 3758's FORWARD TSN carries a *New Cumulative TSN*: "every missing sequence up to here counts as
  received". SCTP can do that because its SACK is cumulative too, so the next SACK confirms it. CultNet's
  ack is not cumulative: it names the highest received sequence plus the 32 below it, or one exact older
  sequence. So the two shapes cost different things:
  - **A. One sequence per notice.** The receiver answers exactly as it would answer a data packet with
    that sequence, and that ack stops the sender's notice as it stops a retransmit. No new ack field. The
    cost is one small notice per abandoned packet per resend: an expired 20-fragment frame sends 20
    notices, again every resend delay until acked.
  - **B. A cumulative point.** One notice covers every abandoned sequence up to it. But nothing on the
    wire can confirm it: an ack naming the point might mean the original of that one packet arrived late,
    while lower holes are still open. So B needs a second addition, a confirmation Ack (another flag bit)
    carrying the receiver's watermark, which puts a cumulative watermark on the wire after all, the thing
    Q-A1 kept off it.
  - **Recommended: A.** It keeps the wire change to one flag, reuses the ack path unchanged, and the extra
    notices only flow while packets are expiring, which is a lossy moment on one session.
  - **Blocks:** Cut F (and K3). Nothing else.

- **Q-A8. Should C#, TypeScript, Python and Kotlin gain reliable expiry? (new in pass 3)** Rust is the only
  runtime that can send a reliable packet that gives up after a deadline (partial reliability, for live
  media). Cut F teaches every runtime to *receive* the notice, because any of them can talk to a Rust
  sender; only Rust can *send* one. A reasonable consumer of the C# or TS library can expect the same send
  option. Today the profile field that configures it (`media_reliable_expire_after_ms`,
  `rudp.rs:2492-2508`) exists only in Rust and cultmesh-rs.
  - **A. Yes, in this campaign** (Cut 7 for C#, TS, Python; K5 for Kotlin): the send option, the profile
    field's behaviour and Cut F's sender half. About +70 per runtime.
  - **B. Not now.** The parity doc records Rust as the only runtime with partial reliability.
  - **Recommended: A.** CultLib is judged by what a reasonable consumer expects (operator, 2026-09-30), and
    after Cut F the sender half is small and already specified. It is independent of every other cut, so it
    can go last.
  - **Blocks:** Cut 7 and K5 only.

- **Q-A9. What does a receiver do with fragment sets from a sender that never sends abandonment
  notices? (new in pass 3, from the Cut 2 Soul pass)** Cut 2 stops evicting reliable fragment sets, because
  evicting one whose fragments were acknowledged loses a frame the sender saw acknowledged (P5). But a
  sender with reliable expiry, which is every default Rust `media` sender, abandons the rest of a frame
  after its first fragment was acknowledged, so the set can never complete; after 64 of them Cut 2 refuses
  the session. Cut F's notices release those sets, but only a Cut F sender sends notices, and the receiver
  cannot tell an old expiring sender's dead set from a live set still waiting for a retransmit.
  - **A. No fallback; bump both ends together.** Cut 2's rule applies to every peer. Any session between a
    Cut 2 receiver and a pre-F expiring sender dies after 64 lossy frames, so every such pair (Muninn's
    media, Mimir's relay, browsers on vendored CultLib) must move in the same deploy.
  - **B. A capability bit, as PR-SCTP negotiates (recommended).** Connect and Accept carry flag bit 4
    ("I send and honour abandonment notices", RFC 3758 §3.1 Forward-TSN-Supported). Old peers ignore it on
    those packets (probe CAP, all five runtimes). Cut 2's never-evict rule applies to a peer that advertised
    it; a peer that did not keeps today's stalest-set eviction, with today's P5 risk, which only its own
    upgrade removes. The sender also stops sending notices to peers that cannot use them. Cost: one flag on
    two packet types and one remembered bit per session.
  - **C. Age-based release.** A reliable set untouched for some multiple of the resend delay is released
    for every peer. It keeps sessions alive without a wire change, but it releases a live set whose missing
    fragment is merely slow, which is the P5 lie Cut 2 exists to remove, now on a timer.
  - **Recommended: B.** It is the literature's answer, it makes Cut 2's rule hold exactly where its premise
    holds, and mixed versions keep working without coordinated deploys.
  - **Blocks:** Cut F's capability half and therefore the merge of Cuts 2, 2c and F. Q-A7 is independent.

**Not forks, recorded so they are not re-asked:**

- `Acknowledged` keeps its name and now means delivered. No variant is added or removed.
- Delivered means handed to the transport's application boundary, not consumed.
- The 4 MiB cap stays; the receiver's 4,096 receive history stays.
- A receipt is resolved once.
- Session generation is its own two cuts, not part of Cut 1 (§8).
- The notice's resend bound is 16 sends at the resend delay (§6). It is a constant, not an option.
- Receipts live on the single-peer socket transport in every runtime, as in Rust; hub receipts are a
  follow-up for all five together.
- A failed send to one peer is that datagram lost (Cut D, RFC 1122's soft errors); it does not end the
  peer's session.


## Sequencing and blockers

1. **Done.** `hands/rudp-stray-packets` merged at `3bf1c0c`. Odin `origin/main` pins `3bf1c0ce`.
2. **In flight** (pass 2 text, unchanged by pass 3): Cuts 1 and 1b on `hands/ack-cut1`, Cut 2 on
   `hands/ack-cut2`. No ruling forces a change to their specs:
   - Q-A3 B adds the abandoned set, but Cut F adds it and hooks it into Cut 1b's primitive; 1b need not know.
   - Q-A4 A needs an ending primitive in C#, TS and Python, which 1b already builds.
   - Q-A6 is Cut 1c's.
   - **Cut 2's merge moves** (the Cut 2 Soul finding): its code stays as specified, but it refuses sessions
     with expiring senders unless Cut F releases their dead sets, so it merges only together with Cut F
     (and Cut 2c). Its spec's "a conforming sender cannot reach that bound" is corrected in place.
3. **Ready now:** Cut 3 (merge after Cut 1, as pass 2 said); **Cut D** (independent); **K0** (tooling and
   `build.ps1`; independent).
4. **Cut 1c** after Cut 1b merges.
5. **Cut F** built on `hands/ack-cut2` after Cuts 1 and 3 merge, once Q-A7 and Q-A9 are ruled. **Cuts 2,
   2c and F merge to `main` together.**
6. **Cut 4** after Cuts 1, 1b and F.
7. **Cuts 6a, 6b, 6c** after Cut 4, in parallel.
8. **Cut 5** after Cut 3 and Cut 1b in TS, as pass 2 said. It may use 6b's `waitAcknowledged` if 6b lands
   first; it does not need it (flush suffices).
9. **Cut 7** after Cut F, once Q-A8 is ruled A.
10. **Cut K** after K0: K1 anytime; K2 after Cuts 1, 1b, 1c; K3 after Cuts 3 and F; K4 after Cut 4
    (and Cuts 6 as the parity reference); K5 after Cut 7.
11. **Pin bumps:**
    - **Odin** after Cuts 1, 1b and 3 (it is the receiver for every Idunn publisher, so this bump makes
      their Acknowledged mean delivered) and Cut D (it is the document server's only production host). Cuts
      2 and F come together at whichever bump follows their merge.
    - **Muninn** (and any consumer of the default reliable `media` channel) takes Cuts 2 and F together.
      Under Q-A9 B its ends may move separately; under A they must move in one deploy. Its client and
      Sleipnir pins of `initial_sequence: 1` should go at that bump (Cut 1c notes).
    - **CodexConnector** and **Ghostlight** need no code change; they may adopt `wait_acknowledged` after
      Cut 4.
12. **Vendored TS bumps after Cut 5** (they pick up honest flushes, the TS window and the rewritten
    publishers at once):
    - **StreamPixels** vendors CultLib `542ddd3` (StreamPixels `a49f7d8`; pass 2's `30ee8b9` is stale).
      Its service runs `createIdunnRuntimePresencePublisher`, which that revision carries under its older
      name (`cultnet-ts/src/idunn-runtime-authority.ts`, pin at `:333`).
    - **Heimdall** vendors `b6b1d9c` and hand-rolls its publisher (`src/idunn-rudp-health.ts:55-87`,
      `initialSequence: 1` at `:65`). At its bump it calls cultnet-ts's publisher instead.
13. **Parallel campaigns:** `hands/media-fec-cut3` touches `rudp.rs` only in the imports, the `audio`
    profile entry and the `"media" | "audio"` send arm; no overlap with Cuts 1-4, D or F beyond a rebase.
    Once it lands, reliable expiry is only `media` in Reliable mode. CultCache publication Cut 4 is
    unrelated in either direction.


## Follow-ups outside this campaign

- **Hold caps in C#, TS, Python and Kotlin.** Their ordered hold buffers and per-channel maps are unbounded
  against a hostile peer. Rust has caps. The far-ahead refusal on data is Rust-only too.
- **The document server's per-session payload budget never shrinks** (`rudp_document_server.rs:321`,
  `:584`).
- **Fire-and-forget health.** Muninn `main.rs:9446`, Sleipnir `main.rs:880-904` and Heimdall's first-ack
  wait can lose a health record silently.
- **Receipts on hubs** (Rust hub, C# listener, cultmesh-ts and cultmesh-py servers) in all five runtimes
  together; and a peer-level `send`-and-wait in TS's `CultNetPeer` (`peer.ts:98-110`).
- **Kotlin self-tests live in the production source** (`CultMesh.kt` K:5236-5633, run from `main`). Move
  them to a test source set once the Kotlin image exists.
- **Callers outside CultLib that pin `initial_sequence`** (Cut 1c's table): drop the value at each bump.
- **Mimir `CultMeshMedia`** hand-rolls a per-peer relay with its own Connect handling (`Program.cs:365`,
  `:485`); it should call the session's repeat predicate after its CultLib moves.
- **`CultNetTransportStats` parity.** Cut D adds `send_failures` in Rust and C#; Kotlin has no
  `packetsDropped` until K1. Record the stats shape in the parity doc once both land.
- **The P4 probe at the old pins** predates its fix; rerun `09318d13`'s P4 against a pinned sender if
  old-sender evidence is needed.

**Follow-up (media FEC Cut 3 Hands, 2026-09-30):** `channel_send_options` in `rudp.rs` has no test for the
`"schema"` and `"latest"` channel arms. Deleting either falls to the default and every test stays green. Pin their send
flags on the wire the way the media arm is now pinned (raw peer socket), in whichever cut next touches channel
profiles.

**Rulings after the Cut 1/1b/1c Soul pass (Self, 2026-09-30):**
- **Stale data after a reconnect.** A reliable sequence at or before the generation's seeded `R` (serial arithmetic
  within the receive window) is a duplicate: acknowledged, not delivered. This is what §6/§8 already promised.
- **Stale Connect (RFC 5961's challenge ACK).** A Connect whose sequence precedes the current generation's Connect
  within the receive window is stale: it is answered with an Ack naming the current generation and does not reset.
  A client whose Connect gets no Accept before its connect attempt times out starts a fresh attempt with a newly
  drawn initial sequence, so a restarted client whose random sequence lands in that window still connects.
- **Server mode.** A Connect from a different endpoint is never a repeat; the session predicate stays sequence-only.
  (Hands' deviation 5 was rejected: pinned clients restarting on a new port were locked out.)
- **The random default belongs to the session.** A session draws its own initial sequence when none is set, so one
  options object reused across sessions or reconnects never repeats a sequence.
- **Connect attempts (Self, 2026-09-30, after the Cut 1c fix-batch Soul pass).** While a Connect awaits its Accept, only an
  Accept the client honours retires it; an Ack never does (an Ack naming the pending Connect had wedged the client for
  good). A fresh attempt after the connect-attempt timeout uses exactly the abandoned sequence + (receive window − 1),
  not a random draw: that keeps a late copy of the abandoned Connect inside the new one's stale window while escaping the
  server's stale window (unless the server sits exactly there, in which case the next attempt escapes). Only a client's
  first Connect draws randomly.
- **Recorded divergence (not this campaign's to fix yet).** Only Rust has a receiver receive-ahead window (1,024). TS,
  C# and Python acknowledge a far-ahead reliable frame by name and hold it behind an ordered gap indefinitely, so a
  stranded ordered channel is silent loss there and "still owed" in Rust. Map a receive window for the three runtimes.
- **Residuals after the final Cut 1c Soul pass (recorded 2026-09-30, low, not fixed):**
  - A late copy of a Connect from two or more attempts back (reordered > ~3 s) still restarts the server; the +window−1
    rule keeps only the immediately previous attempt stale. Rust then times out and reconnects; TS/C#/Python hit the
    receive-window divergence above (silent loss behind the gap).
  - A client pinning `initial_sequence` that restarts while its old data is still in flight after the restarted Connect
    can be matched to the old session's resent Accept (byte-identical; the client cannot tell). Pinning forfeits restart
    detection; the random default closes it. Document on the option.
  - Repeated failed attempts walk the sequence up 4095 per 3 s and clamp at `u32::MAX−1` after ~18-27 days on one session;
    make exhaustion a hard error the caller sees (the clamp removal mutant survives).
