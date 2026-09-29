# CultNet RUDP: Acknowledged means delivered, and the receiver owns ordered delivery with one watermark

Status: cut map, Imagination pass 1, 2026-09-30, committed by Self.

**Rulings (operator, 2026-09-30).** These supersede the two-state proposal in the text below. Cuts 4 and 5
need re-mapping to them before Hands.
- **Q-A1 (a):** Delivered is derived at the sender from a correct receiver's cumulative watermark. The wire
  does not change.
- **Q-A2:** "I think this is telling us we don't actually need both received and delivered."
  - There is one public meaning: Acknowledged means delivered.
  - Receipts go Pending, then Acknowledged (delivered), or Invalidated. No public Received state exists.
  - Retransmission still stops on each packet's own ack, so resending stays bounded.
  - Flush waits for Acknowledged.
- **Open:** Q-A3 (expiring reliable traffic next to ordered traffic), Q-A4 (porting receipts), Q-A5 (Kotlin).
- **Added to Cut 1's scope** (from stray-packet Soul pass 3, pre-existing): a restarted client on the same
  endpoint and payload is locked out by repeated-Connect answers. A reconnect on the same transport treats a new
  server session's frames as duplicates. Session generation belongs to the sequence space this cut owns.
- **Also in Cut 1's scope** (stray-packet Soul pass 4): the ending paths disagree about what a session owed.
  - In Rust, a peer's Disconnect invalidates receipts.
  - A local `disconnect()` or a timeout carries owed writes into the next session. There, a receipt from the old
    session can become Acknowledged (probe R1).
  - TS, Python and C# also carry owed writes across a peer Disconnect.
  - One rule: a write belongs to the session it was issued in.

- **Body.** `origin/hands/rudp-stray-packets` at `d85877a7` ("the session no
  longer keeps an initial sequence nothing reads"). Every `file:line` is against
  it unless it names another revision.
  - `main` (`ae8576d8`) has not touched any RUDP file since the branch forked at
    `60663f95`. `git diff --stat` over the four transport files and
    `packages/cultmesh-kotlin` is empty.
  - Hands is still working on that branch. The line numbers are good until it
    moves again; the anchors (function names) are the durable reference.
- **Base of every cut.** `main` after `hands/rudp-stray-packets` merges. This
  campaign does not start before that merge, and it does not rebase that branch.
- **Scope.** The RUDP session and socket transports in all five runtimes: C#
  `src/GameCult.Networking/CultNetTransport.cs`, Rust
  `packages/cultnet-rs/src/rudp.rs` (plus `packages/cultmesh-rs`), TypeScript
  `packages/cultnet-ts/src/rudp.ts`, Python
  `packages/cultnet-py/src/cultnet_py/transport.py`, and Kotlin
  `packages/cultmesh-kotlin/.../CultMesh.kt`. Kotlin was not in the brief. It is
  a fifth RUDP implementation with the same ordered-delivery code
  (`CultMesh.kt:2939-2978`) and the same codec. **Q-A5** decides whether it is in.
- **Wire.** This map changes no byte on the wire (§1).


## The ends (operator, 2026-09-30)

- A frame the peer has buffered but not delivered must not look delivered.
  Today an ordered frame that arrives behind a gap is held and acknowledged; if
  the session ends it is lost, and the sender saw "Acknowledged".
- Resending stays bounded. The sender stops retransmitting when the peer has
  **received** the frame, exactly as today. Delivery-only acknowledgement was
  rejected: "I don't like the possibility of unbounded resending on an
  unresponsive peer application. TCP sounds like it has the right idea."
- Two meanings: **Received** (the frame is in the peer's session) and
  **Delivered** (handed to the peer's application). Unordered and unreliable
  channels deliver on receipt. Receipts go Pending, then Received, then
  Delivered, plus the existing Invalidated.
- A session that ends with frames held leaves their receipts at Received,
  visibly not Delivered.


## The finding that shapes this map

Self's proposal puts a second acknowledgement on the wire: a per-channel
"delivered through N" watermark beside the received ack. Probing the receivers
changed the picture in two ways.

**1. The ordered receivers are broken in every runtime, and the break is
bigger than the session-end case.** A held frame is not only lost at session
end. In a live session it can be stranded forever, or dropped, while the sender
sees it acknowledged. The per-channel `next` pointer
(`CultNetTransport.cs:918`, `rudp.rs:194`, `rudp.ts:160`, `transport.py:424`,
`CultMesh.kt:2643`) has been patched twice (`436654de` added the
first-use initialisation, `4335ab10` added the skip over other channels'
sequences) and is still wrong:

- **P1: a gap filled by another channel strands the ordered channel.** Ordered
  `o1`, reliable `u` on another channel (lost), `o2`, `o3`. When `u` arrives,
  only `u`'s channel is drained. `o2` and `o3` are never delivered, and the
  sender's pending set is empty. The same holds with two ordered channels
  (P1b).
- **P6: a channel first used after other traffic loses frames.** `C1`, `C2` on
  a new channel, then `X` on another. `X` arrives, then `C2`, then `C1`. The
  channel initialises its `next` from `min(highest+1, frame)`, so `C2` is
  delivered first and `C1` is **dropped as old** (`frame.sequence < next`). It
  was acknowledged.
- Both reproduce at the branch tip, at Odin's pin `a8aeddac` and at Muninn's
  pin `c2a9a6e5`, in every runtime probed.

**2. Once the receiver is correct, "Delivered" is not new information.** A
conforming receiver holds an ordered frame for exactly one reason: some
reliable sequence below it has not arrived. When the last missing sequence
arrives, the same `Receive` call drains the held frames, before the
acknowledgement is built. So:

> An ordered frame is Delivered exactly when every reliable sequence up to its
> last one has been Received.

The sender already knows that. It is the sender's own **acknowledged-through**
watermark: one below the lowest sequence that is still pending or queued.
TCP's cumulative ACK means the same thing, "every byte below N is in the
receive buffer", and it sits beside SACK, which means Received. Here the
sender can compute the cumulative value itself, because every sequence is
acknowledged individually and removed from its pending set.

A delivered watermark on the wire would be a second writer of a value the
first one already determines. Old receivers would never send it, so the sender
would need the derived rule as a fallback anyway. **This map derives Delivered
at the sender and changes no wire byte.** The explicit watermark was probed
and is available as an additive option, but it is not recommended. **Q-A1**
puts it to the operator, because it departs from Self's proposal.

The derivation is sound only if nothing the receiver acknowledged is later
dropped while the session lives. The probes found four places where it is:

| # | Where an acknowledged frame is lost while the session lives | Runtimes | Fixed by |
|---|---|---|---|
| P1, P1b | A cross-channel gap strands the ordered channel | all five | Cut 1 |
| P6 | Late channel initialisation drops the earlier frame | all five | Cut 1 |
| P5 | A reliable fragment set is evicted after its fragments were acknowledged | Rust, TS, Python (C# and Kotlin have no eviction) | Cut 2 |
| P8 | A retransmit more than 4,096 sequences behind the receiver's highest counts as a duplicate: acknowledged and dropped | all five | Cut 3 (the sender never lets its span reach it) |

And one place where the sender loses track without the receiver's help:

| # | Sender-side lie | Runtime | Fixed by |
|---|---|---|---|
| P2 | An expired reliable packet leaves a permanent hole. Ordered frames after it are held forever, and `flush_reliable` succeeds (outstanding 0) | Rust (the only runtime with reliable expiry) | Cut 4 reports it honestly; **Q-A3** decides the real fix |
| P9 | A receipt for an expiring send reads `Acknowledged` once the packet expires unacknowledged | Rust | Cut 4 |
| P10 | `send_reliable` with a full window queues the frame, then returns an error ("receipts require a non-empty reliable packet set"), so a caller that retries sends it twice | Rust | Cut 4 |


## Probe evidence

Everything ran on Yggdrasil through `ygg-verify.sh`, from local-only probe
branches in the throwaway worktrees `F:\Projects\CultLib-imag-ack` and
`-imag-ack-old`. Each probe commit also survives on the Yggdrasil mirror as
`refs/verify/<sha>`.

- `de2bdfe8`, `09318d13`, `03eac6ff`: Rust session and socket probes on the
  branch at `ca18dfac` (`packages/cultnet-rs/tests/probe_ack.rs`).
- `b70a69ca`: the C#, TS and Python probes (`ProbeAckTests.cs`,
  `probe-ack.test.ts`, `probe_ack.py`) on `ca18dfac`.
- `2320a4e5`: the C# probes rebased on `d85877a7`.
  - At `ca18dfac`, `GameCult.Networking` did not compile (`CultNetTransport.cs(2707)`: `ReplacedPeerReason` not in scope). `afd52e97` on the branch fixes it.
- `7fe5d726`: P5 and P8 in C#, TS and Python.
- `f0e7136c`: the Rust probes at Odin's pin `a8aeddac`.
- `bf8fc826`: all four runtimes' probes at Muninn's pin `c2a9a6e5`.
- `db7d2886`: **the prototype**, the Rust receiver rebuilt on one watermark
  plus sender-derived receipt states (about +130/−125 in `rudp.rs`).

| # | Probe | Branch tip (all runtimes probed) | Pins `a8aeddac` / `c2a9a6e5` | Prototype `db7d2886` |
|---|---|---|---|---|
| P1 | `o1`, reliable `u` lost, `o2`, `o3`; then `u`, then `o3` | Delivered `[o1, u]`. `o2`, `o3` never. Sender pending `[]`. Rust, C#, TS, Python. | Same in Rust (both pins), C#, TS, Python (`c2a9a6e5`) | `[o1, u, o2, o3]` |
| P1b | `A1`, `B1` (second ordered channel) lost, `A2`, `A3` | `[A1, B1]`, all four | Same | `[A1, B1, A2, A3]` |
| P6 | `C1`, `C2` on a new channel, then `X`; arrival `X`, `C2`, `C1` | `[C2]`. `C1` dropped. All four. | Same | `[C1, C2, X]` |
| P2 | Rust: reliable media with expiry 1 ms, lost; then `s1`, `s2` on `schema` | Nothing delivered. Sender outstanding 0, expired 1, no resends. `flush_reliable` would return Ok. | Same | Unchanged (held behind the hole). Cut 4 makes the sender say so. |
| P3 | `s1` lost, `s2` arrives and is acknowledged | `s2` not delivered, but acknowledged: sender pending `[s1]` only. A reset then loses `s2`. | Same | Receipt for `s2`: `Received`; after `s1` arrives, 2 frames delivered and `Delivered` |
| P4 | Rust: `s0` delivered, `s1` lost, then a 5 MiB ordered frame in 60 kB fragments, pumped with acks | `RUDP ordered hold buffer is full`: the session is refused. After the gap it would have delivered. With no gap, delivered. | (probe predates the fix; not evidence) | The prototype held every ordered frame, so a 5 MiB frame was refused **even with no gap**: hold only what is not yet deliverable (Cut 1) |
| P5 | 3-fragment reliable frame, 2 fragments arrive and are acked; 70 lossy unreliable fragment sets; last fragment arrives | Rust, TS, Python: reliable frame never delivered, sender pending `[]` | Same (Rust) | Unchanged (Cut 2) |
| P7 | An Ack with flag bit `0x10`, a nonzero sequence field, or a 5-byte payload | Decoded and applied (ack cleared the pending sequence) in Rust, C#, TS, Python | Same at both pins | n/a |
| P8 | Reliable `g` lost while 4,200+ later sequences are sent and acked; then `g` arrives | `g` not delivered, and acknowledged: the sender stops. Rust, C#, TS, Python. | n/a | Unchanged (Cut 3) |
| P9 | Rust socket: `send_reliable("media")` with expiry 1 ms, never read; a ping triggers the purge | `Pending`, then `Acknowledged` | n/a | `Pending` (the abandoned sequence is never acknowledged) |
| P10 | Rust socket: 40 `schema` sends, then `send_reliable` | Error "receipts require a non-empty reliable packet set", after the frame was queued | n/a | Not addressed by the prototype (Cut 4) |

**Suites on the prototype `db7d2886`** (baseline `03eac6ff`, the probe commit
without the prototype, was all green):

- `cultnet-rs`: 110 + 4 + 24 pass; `tests/cultnet.rs` 51 pass and 2 fail.
  Both failures are test shape, not mechanism:
  - `rudp_session_suppresses_duplicates_and_delivers_reliable_ordered_payloads_in_sequence`
    (`tests/cultnet.rs:1000`) hands the receiver an Accept with sequence 91 from a
    sender whose data starts at 1. That handshake is impossible. Under a
    watermark seeded by the Accept, every data sequence is "already below" it.
    The test must use a consistent handshake.
  - `rudp_ordered_channel_tracking_is_bounded` (`:2655`) sends 256 ordered
    channels with no gap. The prototype keeps state only for channels with held
    frames, so nothing is refused. The bound becomes "channels with held
    frames", and the test must hold frames behind a gap.
- `cultmesh-rs` did not compile: `lib.rs:892` matches the status enum
  exhaustively and does not cover `Received`. Cut 4 rewrites that call site.
- The probe suite: P1, P1b, P6 and P3b are fixed as the table shows.

**Wire compatibility (P7).** Every decoder ignores flag bits 4-7 and byte 35.
Every session ignores an Ack's sequence field and payload
(`CultNetTransport.cs:1177`, `rudp.rs:572-586`, `rudp.ts:397`,
`transport.py:612`, `CultMesh.kt:2738`). Every decoder rejects an unknown
packet type (`CultNetTransport.cs` `Enum.IsDefined`, `rudp.rs`
`packet_type_from_code`, `rudp.ts:1290`, `transport.py:1212`,
`CultMesh.kt` `fromCode`) and an unknown version byte. So:

- An additive watermark fits in an Ack: flag bit 4 set, the watermark in the
  Ack's unused sequence field, zero extra bytes. Old peers apply the ack and
  ignore the rest, proven at both pins.
- A new packet type or a version bump is not additive: old peers refuse the
  datagram, and since the stray-packet branch they count it as dropped.
- No capability negotiation is needed under this map, because nothing on the
  wire changes.


## Authority map

- **Owner of ordered delivery (receiver): the session's contiguous
  received-through watermark `R`.**
  - Every reliable sequence up to `R` has been received.
  - A held ordered frame whose first sequence is at most `R + 1` is delivered,
    in sequence order, in the same call that advanced `R`.
  - `R` is seeded by the handshake. `AcceptConnect` seeds it with the Connect's
    sequence, and receiving the Accept seeds it with the Accept's sequence. A
    session connected without a handshake (Rust `assume_connected`,
    `rudp.rs:241`, test-only today) seeds it from its first reliable packet.
  - It advances in `RememberReceived` and resets in `ResetPeerState`.
- **Owner of Received (sender): the pending and queued sets**, as today. A
  sequence leaves them when an ack names it. Retransmission stops there.
- **Owner of Delivered (sender): the acknowledged-through watermark `A`**, one
  below the lowest sequence that is pending, queued or abandoned. An ordered
  send is Delivered when `A` reaches its last sequence. A reliable unordered
  send is Delivered when it is Received.
- **Inputs.** Received sequences (receiver); acks and expiry (sender). Nothing
  else.
- **Outputs.** Delivered frames, in order (receiver). Receipt status and flush
  completion (sender).
- **Derived, not owners.**
  - The per-channel `next` map is **deleted**, not demoted:
    `_orderedNextSequenceByChannel` (`CultNetTransport.cs:918`),
    `ordered_next_sequence_by_channel` (`rudp.rs:194`),
    `#orderedNextSequenceByChannel` (`rudp.ts:160`),
    `_ordered_next_sequence_by_channel` (`transport.py:424`),
    `orderedNextSequenceByChannel` (`CultMesh.kt:2643`).
  - `expectedSequenceIfUninitialized` and its equivalents are deleted
    (`CultNetTransport.cs:1151`, `rudp.rs:534`, `rudp.ts:375`,
    `transport.py:595`, `CultMesh.kt:2728`).
  - `PendingOrderedFrame.NextSequence` is deleted.
  - `OutstandingReliablePacketCount` stays, and means "not yet Received".
- **Forbidden writers.** No path delivers an ordered frame except the drain
  after `R` advances. No receive path drops a frame whose sequence it
  acknowledged while the session lives. No sender path reports Delivered
  except through `A`.
- **Shared paths.** Accept, reliable data, fragments, duplicates and
  retransmits all advance `R` through `RememberReceived` and then call the
  drain. Direct sends, fragmented sends, queued promotion and resends all feed
  the same pending and queued sets that `A` reads.
- **Deletion line.** `DeliverOrdered`, `DrainOrdered` and
  `SkipReceivedNonChannelSequences` and their equivalents go before the drain
  is added: `CultNetTransport.cs:1628-1700`, `rudp.rs:1126-1234`,
  `rudp.ts:737-800`, `transport.py:868-920`, `CultMesh.kt:2939-2978`.


## The design, question by question

### 1. Where Delivered comes from

- Derived at the sender from `A` (§ The finding). No wire change.
- **Why it is sound after Cuts 1-3.** A conforming receiver:
  - drops nothing it acknowledged while the session lives (P1, P6: Cut 1; P5:
    Cut 2; P8: made unreachable by Cut 3);
  - holds an ordered frame only while a sequence below it is missing (Cut 1);
  - drains held frames in the call that fills the last gap, before it
    acknowledges (Cut 1).
  - So every sequence up to `s` acknowledged means `s` was delivered.
- **Mixed versions.** A new sender with an old receiver can be wrong only when
  that receiver strands a frame (P1, P6). That needs reliable traffic on more
  than one channel of one session. The C#, TS, Python and Kotlin socket
  transports send reliably only on `schema` (`CultNetTransport.cs:2372-2376`,
  `rudp.ts:1233-1241`), so they cannot trigger it. Rust sends reliable
  `media`/`audio` too (`rudp.rs` `channel_send_options`). The Eyes survey found
  no live session that mixes those with `schema`: Muninn's media session
  carries no `schema` traffic (`Muninn crates/muninn-daemon/src/main.rs:3099-3158`),
  and neither does Sleipnir's HID stream (`Odin crates/sleipnir-daemon/src/main.rs:2217-2272`).
  The old receiver loses such frames today in any case. Bumping the pin is the
  fix.
- **Rejected: a wire watermark (Q-A1 B).** It is additive (P7), but it is a
  second authority for a value the first one determines. Old receivers never
  send it, so the derived rule is needed as a fallback anyway.

### 2. What "delivered to the application" means

- **Delivered means the frame left the session's hold and was handed to the
  transport's application boundary.** That boundary is:
  - C#: enqueued in the socket transport's `_deliveredFrames` (read by
    `TryReceiveOnce`), or returned in `CultNetRudpReceiveResult.Delivered` from
    a bare session;
  - Rust: pushed to `delivered_frames` (`rudp.rs:2137`) or the hub's
    `pending_events`, or handed to the document server's sink
    (`rudp_document_server.rs:343-360`), which acknowledges only after the sink
    accepted;
  - TS: the `"frame"` event was emitted (`rudp.ts:1055-1061`);
  - Python and Kotlin: the delivered queue.
- **It is not "consumed".** The operator rejected application-driven
  acknowledgement, and TCP's cumulative ACK has the same boundary: in the
  receive buffer, readable, not read.
- A listener or sink that throws or rejects has still been delivered to. The
  handler-rejection paths from the stray-packet branch end the session, which
  is the application's answer, not the transport's.

### 3. The public API

- **Rust receipts** (the only runtime that has them):
  - `CultNetRudpReliableSendStatus` becomes `Pending`, `Received`, `Delivered`,
    `Invalidated` (`rudp.rs:144-148`). `Acknowledged` is removed, not aliased.
    Every caller must choose, and every caller today means Delivered.
  - The receipt records whether its send was ordered (`rudp.rs:132-135`).
  - After the session ends without a reset: `Pending` becomes `Invalidated`
    (as today, `rudp.rs:1919-1928`). `Received` stays `Received`, which the
    ruling asks for. `Delivered` stays.
  - After a reset (`reset_peer_state`: a new Connect generation, or
    `end_refused_session`), the session keeps one frozen snapshot of the ended
    epoch: its scope, `A`, and its still-unacknowledged sequences, at most the
    reliable window plus the queue. A receipt from that epoch resolves against
    the snapshot, so `Received` stays `Received` and `Delivered` stays
    `Delivered`. Older epochs are `Invalidated`.
    - Today every receipt, delivered or not, reads `Invalidated` after a
      reset, because the scope rotates (`rudp.rs:338-339`).
  - `send_reliable` receipts cover queued packets (P10). The receipt lists every
    sequence the send assigned, not only the ones admitted to the wire.
  - `send_reliable` refuses a channel with reliable expiry (P9). The stale
    comment at `rudp.rs:1903-1904` says this lineage has no expiry, which is
    false since `bac5be88`. Restore the guard it describes.
  - New: `CultNetRudpSocketTransportConnection::wait_delivered(&receipt, timeout) -> Result<()>`.
    It pumps receive and resends like `flush_reliable`. It fails at once when
    the session ended, naming the frozen status. Today four places hand-roll
    that loop: `cultmesh-rs/src/lib.rs:806-838` and `:870-900`,
    `CodexConnector src/idunn_health.rs:727-745` and
    `Ghostlight crates/ghostlight-dungeon/src/idunn_health.rs:1126-1145`.
- **Flush: one wait, and it means Delivered** (**Q-A2**).
  - When every sent sequence is Received, `A` is the highest sequence sent, so
    every send is Delivered. The two waits coincide, except behind an abandoned
    sequence.
  - Rust `flush_reliable` (`rudp.rs:1985`) therefore fails at once when an
    ordered send follows the session's first abandoned sequence. That case
    never becomes Delivered, and today it reports success (P2).
  - C# `FlushReliable` (`CultNetTransport.cs:1914`), TS `flush`
    (`rudp.ts:906`), Python `flush_reliable` (`transport.py:1101`) and Kotlin
    `flushReliable` (`CultMesh.kt:3186`) keep their code. Their contract text
    changes to "every reliable frame sent has been delivered to the peer's
    application". These runtimes have no expiry.
  - A second "flush until received" variant would differ only in the
    abandoned-sequence case, where it would report success on frames that
    will never be delivered. It has no consumer. Not added.
- **Defaults.** Plain flush waits for Delivered. No parameter.
- **Compatibility.** No C#, TS, Python or Kotlin signature changes. Rust:
  removing `Acknowledged` breaks the external callers at their next bump. Each
  is a one-line change to `Delivered`, or a switch to `wait_delivered`. The
  pinned callers are CodexConnector (`e171eca`) and Ghostlight (`85f7024`).

### 4. Consumers, each assigned

Surveyed across `F:\Projects` (the HEAD of each repo is recorded in the Eyes
report). Every consumer that waits wants **Delivered**. None wants Received
alone.

| Consumer | What it waits on today | Assigned | Change |
|---|---|---|---|
| `cultmesh-rs/src/lib.rs:781-838` `request_raw_snapshot_from_rudp_catalog` (Idunn `src/drivers.rs:5527` uses it) | `Acknowledged`, then the response | Delivered (the response proves it anyway) | Cut 4: `wait_delivered` |
| `cultmesh-rs/src/lib.rs:870-900` `publish_cultnet_message_to_rudp_catalog` (Odin `odin-daemon/src/main.rs:616-658`) | `Acknowledged`, then disconnect. The doc at `:869` claims "application admission". | Delivered. That is true at the document server, which acknowledges after its sink accepted (`rudp_document_server.rs:343-372`). | Cut 4: `wait_delivered`; the doc says Delivered |
| `cultmesh-rs/src/lib.rs:918-938` `publish_cultnet_messages_to_rudp_catalog` (Ghostlight `mesh.rs:323`) | `flush_reliable`, then drop. **Several ordered frames, then close.** | Delivered | None; flush now means it |
| `cultmesh-ts/src/index.ts:6070-6098` `publishRudpDocumentOnce` (Heimdall, StreamPixels, Stonks, weksa, Bifrost, VoidBot, Hermodr) | `peer.flush` (`cultnet-ts/src/peer.ts:109-110`), then close | Delivered | None |
| `cultnet-ts/src/idunn-odin-presence-publisher.ts:100-127` | a hand-rolled session loop that waits for `outstandingReliablePacketCount === 0`, then closes. It never resends, and queued packets are never sent. | Delivered | Cut 5: use the socket transport and `flush` |
| `cultnet-ts/src/signed-daemon-health.ts:113-160` (Hermodr, Odin `idunn-rudp.cjs`) | a hand-rolled loop: sends only the packets `sendMany` admitted, once; waits up to 500 ms for **any** ack packet, swallows the timeout, closes | Delivered | Cut 5: use the socket transport and `flush`. Today a lost datagram is reported as published, and a payload over 32 packets is truncated. |
| `cultnet-ts/src/operation-service.ts` | the operation response | none (the response is the proof) | none |
| C# `FlushReliable` callers: Gjallar `Program.cs:3055-3080` (two ordered frames, flush, dispose) | flush | Delivered | None |
| Heimdall `src/idunn-rudp-health.ts:75-86` (vendored CultLib `b6b1d9c`) | first ack packet | Delivered | Heimdall's own cut at its bump (follow-up) |
| CodexConnector `idunn_health.rs:727-745`, Ghostlight `idunn_health.rs:1126-1145` | `Acknowledged` | Delivered | At their next pin bump |
| Muninn `main.rs:9446-9458`, Sleipnir `main.rs:880-904` (both copies) | nothing (send and drop) | none today | Follow-up: fire-and-forget health can be silently lost |
| Kotlin `flushReliable` | no callers | Delivered | Contract text only |

### 5. The hold buffer and the 4 MiB cap

- **It belongs here**, because the held frame is exactly the state this map
  names.
- **Mechanism (P4).** Rust caps held ordered frames at 1,024 frames and 4 MiB
  (`rudp.rs:47-48`, checked at `:1169-1175`). A refusal ends the session. The
  default payload limit is 16 MiB (`rudp.rs:33`). With one lost packet under a
  large ordered transfer, a legal 5 MiB frame is refused and kills the session.
  - The sender's window counts pending packets, not span or bytes
    (`rudp.rs:898-930`). While one packet stays lost, acknowledged packets keep
    freeing slots, so the held data behind the gap grows without limit.
- **Fix: a sender-side flow window, TCP's receive window without a wire
  field** (Cut 3). A reliable packet is admitted to the wire only while:
  - its sequence is at most 1,023 above the lowest unacknowledged sequence;
    and
  - the payload bytes of sequences above the lowest unacknowledged one stay
    within 4 MiB, with at least one packet always admissible.
  - Otherwise it waits in the existing queue.
- **What this buys:**
  - a conforming sender never makes a Rust receiver hold more than 4 MiB, so
    the cap stops refusing legal traffic;
  - a conforming sender never gets 1,024 sequences ahead of the receiver's
    highest, so the Rust far-ahead refusal (`rudp.rs:46`) never drops its
    packets;
  - a conforming sender never leaves a sequence 4,096 behind the receiver's
    highest, so P8 cannot happen. The receive-history window stays 4,096 in
    every runtime (`CultNetTransport.cs:884`, `rudp.rs:35`, `rudp.ts:20`,
    `transport.py:404`, `CultMesh.kt:2625`).
- **What is kept.** The cap and the far-ahead refusal stay. They protect
  against hostile peers.
- **Rejected:**
  - raising the cap to the payload limit or above: that is 16-64 MiB per
    hostile session instead of 4;
  - lowering the ordered payload limit to 4 MiB: that is an API regression for
    large snapshots.
- **Cost.** Normal flow never has a span beyond the 32-packet window, so the
  new limits bind only while a packet is lost.
- **Old senders** (the pinned and vendored ones) can still trip the Rust cap
  until they bump.
- **Not in scope.** C#, TS, Python and Kotlin receivers have no hold cap at
  all. They hold without limit for a hostile peer. Follow-up.

### 6. Reliable expiry (P2)

- Only Rust has it (`rudp.rs:39`, `:886-896`). An expired packet's sequence
  never reaches the receiver. `R` stops below it, and so did the old `next`
  pointer. Ordered frames after it are held for the rest of the session.
- **Today's users.** Only Muninn's reliable `audio`
  (`Muninn main.rs:3230-3241`), and its session carries no ordered traffic.
  `hands/media-fec-cut3` deletes the `audio` profile entry and the `"audio"`
  send arm. The `media` channel keeps a caller-chosen reliable-with-expiry
  mode.
- **This map makes it honest (Cut 4):**
  - the sender records its first abandoned sequence;
  - `A` never passes it;
  - ordered receipts after it stay `Received`;
  - `flush_reliable` fails at once.
- **The real fix is a fork (Q-A3).** The literature's answer is PR-SCTP's
  FORWARD-TSN (RFC 3758): the sender tells the receiver to move its cumulative
  point past abandoned sequences. That is a wire addition, and every runtime's
  receiver would have to learn it.

### 7. Interaction with the stray-packet branch

The ack cuts build on these branch behaviours and do not alter them:

- An ended session fails its flush (`CultNetTransport.cs:1924-1931`,
  `rudp.rs:1987-2011`, `rudp.ts:906-916`, `transport.py:1101-1124`).
- A refusing session ends, and its goodbye is built after the reset
  (`EndRefused`, `end_refused_session` `rudp.rs:358`). The refused sequence is
  never acknowledged, so a refusal never becomes a false Received.
- Rust keeps issued sequences on reset. Cut 4's frozen epoch snapshot relies
  on that: an old receipt's sequences can never collide with a new epoch's.
- One Accept per session in C#, TS and Python, and `AnswerRepeatedConnect`. A
  repeated Connect's sequence is at most `R` and counts as a duplicate.
- The TS operation service expiry and handler rejection are above the
  transport, and do not change.
- One thing the branch left: the Rust socket transport in server mode still
  calls `reset_peer_state()` on every Connect (`rudp.rs:2099`), even a repeated
  one. Under Cut 4 that rotates the epoch. That makes it visible, but it is the
  branch's parity item, not this map's. It is recorded as a follow-up.


## Cuts

Every cut is on CultLib, branch `hands/rudp-delivered` from `main` after
`hands/rudp-stray-packets` merges, in a worktree Self creates. Heavy
verification runs on Yggdrasil. Each cut starts by committing its probes as
failing tests named for the rule, in every runtime it touches.

**Negative checks that stay green in every cut:**

- the cross-runtime reorder, drop and fragment proofs in the TS interop harness
  (`docs/cultnet-transport-parity.md:227-248`);
- the stray-packet tests (`rudp_stray_packets.rs`, and their C#, TS and Python
  equivalents);
- the codec fixture vectors: no wire byte changes.

### Cut 1. One watermark owns ordered delivery (receiver, all five runtimes)

- **First.** Failing tests P1, P1b and P6 in each runtime. Rust's are in
  `probe_ack.rs` at `db7d2886`, and the others are in the probe commits above.
- **Deletes first:**
  - the per-channel `next` map and its resets:
    - `CultNetTransport.cs:918`, `:1292-1310`;
    - `rudp.rs:194`, `:221`, `:348`;
    - `rudp.ts:160`, `:214-225`;
    - `transport.py:424`, `:458-469`;
    - `CultMesh.kt:2643`;
  - `expectedSequenceIfUninitialized`:
    `CultNetTransport.cs:1151-1153`, `rudp.rs:534-537`, `rudp.ts:375-377`,
    `transport.py:595-599`, `CultMesh.kt:2728`;
  - `DeliverOrdered`, `DrainOrdered` and `SkipReceivedNonChannelSequences`
    (the deletion line above);
  - `PendingOrderedFrame.NextSequence`.
- **Adds:**
  - `R`, seeded in `AcceptConnect` (`CultNetTransport.cs:1012`, `rudp.rs:377`,
    `rudp.ts:242`, `transport.py:485`, `CultMesh.kt:2667`) and on Accept
    receipt (`CultNetTransport.cs:1155`, `rudp.rs:539`, `rudp.ts:379`,
    `transport.py:601`, `CultMesh.kt:2730`);
  - `R` advances in `RememberReceived` (`CultNetTransport.cs:1518`,
    `rudp.rs:961`, `rudp.ts:619`, `transport.py:809`, `CultMesh.kt:2891`);
  - a reassembled ordered frame is delivered at once when its first sequence
    is at most `R + 1` and its channel holds nothing below it. Otherwise it is
    held. The caps apply only to what is held: the prototype held everything
    and so refused a 5 MiB frame with no gap;
  - after every reliable receipt (data, fragment, duplicate, Accept), drain
    every channel's held frames whose first sequence is at most `R + 1`, in
    sequence order;
  - Rust only: the 64-channel cap (`rudp.rs:42`) counts channels with held
    frames.
- **Tests changed:** `tests/cultnet.rs:1000` (consistent handshake) and
  `:2655` (hold frames behind a gap). The equivalents in the other runtimes are
  whatever the change breaks. Each must be read before it is edited, because a
  test that encodes `min(highest+1, frame)` encodes P6.
- **Verification:**
  - P1, P1b and P6 pass in every runtime;
  - the full suites: `cargo test` in `cultnet-rs` and `cultmesh-rs`, `dotnet
    test tests/GameCult.Networking.Tests`, `npm test` in `cultnet-ts`, the
    Python suite, and Kotlin per Q-A5;
  - the TS interop harness across runtimes;
  - negative grep: `rg -n "orderedNextSequence|ordered_next_sequence|expectedSequenceIfUninitialized|expected_sequence_if_uninitialized|SkipReceivedNonChannel|skip_received_non_channel" src packages`
    returns nothing;
  - mutation testing on the diff: cargo-mutants, Stryker.NET, StrykerJS, and
    mutmut for Python.
- **Ledger.** Per runtime about −70 and +40. Across five, about −350 and +200.

### Cut 2. A reliable fragment set is never evicted (receiver: Rust, TS, Python)

- **First.** Failing test P5 in each of the three.
- **Change:**
  - eviction picks the stalest set that holds no reliable fragment
    (`rudp.rs:1015-1035`, `rudp.ts:679-692`, `transport.py:836-842`);
  - if every pending set holds reliable fragments, the packet is refused and
    the session ends, as every other refusal does.
  - A conforming sender cannot reach that bound: its window keeps at most 32
    reliable packets in flight, and the bound is 64 sets.
- **Verification:** P5 delivers the reliable frame. `d3bc6e66`'s test (lossy
  fragment sets keep being evicted) stays green. `fragment_sets_evicted` still
  counts evictions.
- **Ledger.** About −3 and +12 per runtime.

### Cut 3. The sender's flow window: 1,023 sequences and 4 MiB above the lowest unacknowledged (sender, all five runtimes)

- **First.** Failing tests P8 (all runtimes) and P4 (Rust receiver, with a
  sender from each runtime).
- **Change.** Admission (`AdmitReliablePackets` / `PromoteQueuedReliable` at
  `CultNetTransport.cs:1439-1490`, `rudp.rs:898-930`, `rudp.ts:586-600`,
  `transport.py:776-796`, `CultMesh.kt:2856-2872`) admits a packet only while:
  - the window has a slot (as today);
  - its sequence is at most the lowest unacknowledged sequence plus 1,023;
  - the admitted payload bytes above the lowest unacknowledged sequence stay
    within 4 MiB.
  - The first packet is always admissible. Expired packets (Rust) do not count
    as unacknowledged here: they are gone from the wire.
- **Contract.** `docs/cultnet-transport-parity.md` (after `:263`) states the
  receive window: a receiver must hold 1,023 sequences and 4 MiB beyond its
  contiguous watermark, and a conforming sender never exceeds that.
- **Verification:**
  - P8: `g` is delivered;
  - P4: the 5 MiB frame is delivered after the gap fills, and the session is
    never refused;
  - the existing large-snapshot tests (`c398bad1`) and the fragment interop
    stay green;
  - the Rust benchmark or media probe shows no throughput change on a lossless
    link.
- **Ledger.** About +25 per runtime, and nothing removed.

### Cut 4. Rust receipts: Received and Delivered, honest expiry, honest queueing (Rust, cultmesh-rs)

- **First.** Failing tests: P3b (Received, then Delivered), P9, P10, P2
  (`flush_reliable` must fail), and the frozen-epoch case (a receipt at
  `Received` stays `Received` across `end_refused_session`).
- **Deletes first:**
  - `Acknowledged` (`rudp.rs:146`);
  - the stale comment and the half guard at `rudp.rs:1903-1908`;
  - the hand-rolled status loops at `cultmesh-rs/src/lib.rs:806-838` and
    `:870-900`.
- **Adds:**
  - `Received` and `Delivered`;
  - `ordered` on the receipt;
  - `acked_through` (`A`), reading pending, queued and first-abandoned;
  - first-abandoned, recorded in `purge_expired_reliable` (`rudp.rs:886`);
  - the receipt covers queued sequences: `send_many` (`:450`) reports every
    sequence it assigned;
  - `send_reliable` refuses a channel with expiry;
  - the one-epoch frozen snapshot in `reset_peer_state` (`:338`);
  - `wait_delivered`;
  - `flush_reliable` fails fast after an abandoned sequence precedes an ordered
    send.
- **Changes:**
  - `cultmesh-rs/src/lib.rs:832` and `:892` use `wait_delivered`;
  - the doc at `:869` says Delivered;
  - `tests/rudp_stray_packets.rs:309` and `cultmesh-rs/tests/rudp_document_server.rs:408,415` change from `Acknowledged` to `Delivered`.
- **Verification:**
  - the new tests pass;
  - `rg -n "Acknowledged" packages/cultnet-rs packages/cultmesh-rs` has no hit;
  - both suites are green;
  - cargo-mutants on the diff.
- **Ledger.** About −60 and +110 (of which `wait_delivered` is about 35, and
  it replaces two loops of about 30 each).

### Cut 5. CultLib-internal consumers and the contract text (TS, docs)

- **Changes:**
  - `signed-daemon-health.ts:113-160` and
    `idunn-odin-presence-publisher.ts:100-127` send through
    `CultNetRudpSocketTransportConnection` and `flush`. Their hand-rolled
    session loops are deleted.
  - Flush contract text in all five runtimes: "every reliable frame sent has
    been delivered to the peer's application".
  - `docs/cultnet-transport-parity.md:236-240` (the control-packet guard
    paragraph) is replaced by the watermark rule, and `:261-272` gains the
    Received/Delivered meanings and the receive window.
- **Verification:**
  - `runtime-presence-health.test.ts` and `signed-daemon-health.test.ts` stay
    green;
  - a new test drops the first datagram and still publishes;
  - a new test publishes a payload over 32 packets whole.
- **Ledger.** About −100 and +40.


## Subtraction ledger (estimate)

| Cut | Removed | Added | Other |
|---|---|---|---|
| 1 | ~350 | ~200 | five per-channel state machines become one watermark |
| 2 | ~10 | ~35 | none |
| 3 | 0 | ~125 | a documented receive-window contract |
| 4 | ~60 | ~110 | one public method (`wait_delivered`), two enum variants for one |
| 5 | ~100 | ~40 | two hand-rolled RUDP loops gone |

- **Source, net:** about −10. Tests are extra: about 5 × 6 rule tests.
- **Wire:** zero bytes.
- **Downstream:** each external `Acknowledged` caller needs a one-line change
  at its next bump.
- **Prototype check:** the Rust receiver and status prototype is +129/−124 in
  `rudp.rs` and includes throwaway public helpers.


## Operator questions

Each question stands alone.

- **Q-A1. Where does Delivered come from?**
  - **A.** Derive it at the sender. An ordered send is Delivered when every
    reliable sequence up to its last one is Received. A reliable unordered send
    is Delivered when Received. Nothing changes on the wire.
  - **B.** Add a delivered watermark to the wire. The receiver sets Ack flag
    bit `0x10` and puts "delivered through N" in the Ack's unused sequence
    field. That is additive: old C#, Rust, TS and Python peers at the branch tip
    and at both pins ignore it (P7). Senders use it when it is present, and
    fall back to A for old peers.
  - **Recommended: A.**
    - Once Cuts 1-3 make the receiver correct, B's watermark always equals A's
      derivation.
    - B is a second authority for one value, plus a capability sniff, plus five
      encoder and decoder changes.
    - B would only tell the sender more than A if a receiver strands frames,
      and that receiver is old, so it does not send B.
  - **Depends on it.** Cut 4's shape. B adds a Cut 3b in all five runtimes (+~40
    each) and makes the parity codec fixtures change.
- **Q-A2. One flush, or a "received" flush beside the "delivered" one?**
  - **A.** One flush. It means Delivered.
  - **B.** Add `flushReceived` beside it.
  - **Recommended: A.** After this map, all-Received means all-Delivered, except
    behind an abandoned sequence. There a received-flush would report success
    on frames that will never be delivered, and plain flush fails fast. No
    consumer wants Received alone (§4). Receipts keep both states.
  - **Depends on it.** B adds a method in five runtimes and a test each.
- **Q-A3. Reliable expiry next to ordered traffic (P2).** An expired reliable
  packet leaves a permanent hole, and every ordered frame after it on the same
  session is held until the session ends.
  - **A.** Report it honestly (Cut 4: receipts stay Received and flush fails
    fast). Document "do not send ordered traffic on a session that uses
    reliable expiry". No live consumer does.
  - **B.** PR-SCTP's FORWARD-TSN (RFC 3758). On expiry the sender sends an
    abandonment notice, which rides an Ack-typed packet with flag `0x10` so
    that old peers ignore it (P7). New receivers treat the sequence as received
    with no frame, and acknowledge it. The sender resends the notice until it
    is acknowledged. That is a new cut in all five receivers. Old receivers
    never acknowledge it, so the resend needs its own bound.
  - **C.** Refuse ordered sends on a session after its first expiry.
  - **D.** Delete reliable expiry. Muninn's `audio` is its only user and Muninn
    Q4 retires it. This subtracts, but CultLib would lose partial reliability,
    which a reasonable consumer can expect.
  - **Recommended: A now.** Record B as the complete answer for when a consumer
    mixes the two.
  - **Depends on it.** Whether Cut 4 carries `first_abandoned` alone (A) or a
    new wire notice (B).
- **Q-A4. Receipts in C#, TypeScript, Python and Kotlin?**
  - Only Rust has per-send receipts. The other four offer flush and an
    outstanding count.
  - **A.** Port receipts with the same four states and a `WaitDelivered`, in
    this campaign.
  - **B.** Record it as a parity gap. Flush covers every surveyed consumer.
  - **Recommended: B.** No consumer outside Rust waits per send. A reasonable
    C# consumer might want it, but it is additive later and does not change the
    wire.
  - **Depends on it.** A adds a cut of about +80 per runtime.
- **Q-A5. Is Kotlin in the campaign?**
  - `cultmesh-kotlin` carries a fifth RUDP runtime with P1 and P6 (the same code,
    `CultMesh.kt:2939-2978`). Its flush has no callers. The stray-packet branch
    did not touch it. Its only build path is `build.ps1`, which needs Android
    Studio's `kotlinc` and JBR on Windows. Yggdrasil has no Kotlin image in the
    verify list.
  - **A.** Include it in Cuts 1, 3 and 5, and verify on Starfire as a
    Windows-only job, or add a Kotlin image to Yggdrasil first.
  - **B.** Record it as parity debt, with the stray-packet gaps. Mark the parity
    doc so that nobody reads Kotlin as conforming.
  - **Recommended: A, with a Yggdrasil Kotlin image.** Wire parity is the
    library's promise. A runtime that strands acknowledged frames should not
    stay listed as conforming.
  - **Depends on it.** Cut 1 and Cut 3 scope and their verification host.

Not forks, recorded so they are not re-asked:

- `Acknowledged` is removed, not aliased. The ruled states replace it.
- Delivered means handed to the transport's application boundary, not consumed
  (the ruling).
- The 4 MiB cap stays. The sender's window keeps legal traffic under it (§5).
- The receiver's 4,096 receive history stays. The sender's window keeps legal
  traffic inside it.
- A receipt keeps Received across a session end or reset (the ruling). A reset
  keeps one frozen epoch.


## Sequencing and blockers

1. **`hands/rudp-stray-packets` merges to `main`.** It is in Hands now. Every
   cut here is based on that merge.
2. **The Odin CultLib pin bump** follows that merge. It is **not** blocked on
   this campaign: Odin crashed on stray packets, and it should not wait. Odin
   uses `publish_cultnet_message_to_rudp_catalog`, not the enum, so Cut 4 does
   not break its build when it bumps again.
3. **Cut 1** is blocked on step 1 only.
4. **Cut 2 and Cut 3** are blocked on step 1 only. They are independent of Cut 1
   and of each other, and can run in parallel worktrees. Cut 3 and Cut 1 touch
   different functions in the same files. Merge Cut 1 first so that Cut 3's P4
   test exercises the new hold path.
5. **Cut 4** is blocked on Cut 1, because Delivered is sound only against a
   correct receiver. It is also blocked on Q-A1 and Q-A3.
6. **Cut 5** is blocked on Cut 3 in TypeScript, because the socket transport it
   routes through must carry the window.
7. **Parallel campaigns:**
   - **`hands/media-fec-cut3`** (Muninn's Cut 3 in CultLib) touches `rudp.rs`
     only in the profile's `audio` entry (`~2215`) and the
     `"media" | "audio"` arm of `channel_send_options` (`~2380`). Neither
     overlaps Cuts 1-4 (the session internals and `send_reliable`
     `:1891-1928`). It can merge in either order, and the second to land
     rebases trivially. Once it lands, reliable expiry is only for `media` when
     the caller chooses Reliable.
   - **Muninn's own cuts** use lossy media plus parity. Its reliable `audio`
     session has no ordered traffic, so P2 does not bite it. Its next CultLib
     pin (the `pins/` commit at Cut 3's CultLib tag, Muninn Q2) picks up
     whatever has landed. Nothing here blocks Muninn, and nothing in Muninn
     blocks this.
   - **CultCache publication Cut 4** (CultNetDatabase on the journal,
     `docs/cultcache-publication-cut.md` §4) touches `CultNetDatabase.cs`
     only, and needs in-order, exactly-once delivery on `schema`. The C#
     socket transports carry one reliable channel (`CultNetTransport.cs:2372-2376`),
     so P1 and P6 cannot occur on their sessions. There is no blocking
     relation in either direction.
8. **Heimdall and StreamPixels vendored bumps** (`b6b1d9c`, `542ddd3`) come
   after Cut 5, so that they pick up honest flushes and the TS window at once.
   Heimdall's `idunn-rudp-health.ts` first-ack wait moves to `flush` at that
   bump.
9. **CodexConnector (`e171eca`) and Ghostlight (`85f7024`)** change
   `Acknowledged` to `Delivered` (or to `wait_delivered`) at their next bump
   after Cut 4.


## Follow-ups outside this campaign

- **Hold caps in C#, TS, Python and Kotlin.** Their ordered hold buffers and
  per-channel maps are unbounded against a hostile peer. Rust has caps.
- **The Rust server-mode socket transport resets on every Connect**
  (`rudp.rs:2099`), even a repeated one. This is a stray-packet parity item.
- **The document server's per-session payload budget never shrinks**
  (`rudp_document_server.rs:321`, `:584`). After 4 MiB in one session it drops
  data without acknowledging it, and the sender retransmits until it gives up.
- **Fire-and-forget health.** Muninn `main.rs:9446`, Sleipnir `main.rs:880-904`
  and Heimdall's first-ack wait can lose a health record silently.
- **FORWARD-TSN** if Q-A3 is A and a consumer later mixes expiry with ordered
  traffic.
- **The P4 probe at the pins** predates its fix, so it proves nothing there. If
  anyone needs old-sender-to-new-receiver evidence for the cap, rerun
  `09318d13`'s P4 against a pinned sender.
