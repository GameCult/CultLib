# Postmortem: CultMesh bulk content over custom RUDP

**Incident window:** 2026-07-12 through 2026-07-19

**Detected:** 2026-07-19 during the released EveUnity/Aetheria cold-cache witness

**Scope:** CultMesh Phase 4 content delivery and the physical transport assumptions beneath it

**Status:** architectural cause identified; RUDP tuning stopped; replacement data planes not yet integrated

## Executive summary

The CultMesh reliability migration correctly moved discovery, session reuse,
authority, content verification, resumable state, and final cache promotion out
of applications and renderers. It failed at the boundary between those control
planes and physical byte delivery.

The canonical plan stated that the existing RUDP implementation already
covered acknowledgement, resend, ordering, duplicate suppression,
fragmentation, timeout, and bounded pending work. It therefore said the first
migration target was not a transport rewrite. That sequencing was reasonable
while CultMesh lacked identity-first sessions: replacing the wire first would
have preserved caller-owned reconnect and topology policy.

The mistake came later. Once `CultMeshSessionManager` existed, the condition
named by the plan for adopting Iroh or QUIC had been satisfied. Instead of
benchmarking and attaching a proven content stream at that boundary, Phase 4
introduced `CultMeshContentChunkRequestMessage` and
`CultMeshContentChunkResponseMessage` over the existing schema session. The
response embedded each raw chunk payload and therefore sent bulk bytes through
the custom RUDP fragmentation and retransmission engine.

This preserved schema identity and moved cache authority to the correct owner,
but it did not produce the intended data plane. It changed the envelope around
the bytes while leaving the physical bottleneck intact.

The failure was not detected promptly because the gates proved eventual
delivery and ownership, not transport fitness:

- the initial content-session test used an in-memory schema client and server;
- the deterministic hostile-network harness tested its own schedule but was
  not connected to the public content path or a physical connector;
- the later real-UDP regression allowed one MiB to take nearly ten seconds;
- the released cold witness allowed the artifact to finish under a 300-second
  deadline;
- no test compared goodput, wire amplification, copies, or allocation against
  TCP, QUIC, Iroh, or a same-machine mapping baseline;
- `SharedFileMapping` in witness output described mapping the client cache
  after the network transfer, not direct access to a provider-owned local body.

The result was a locally reliable but grossly inefficient bulk path. A
56,204,750-byte released Aetheria body caused 105,911,320 daemon transmit bytes
in one cold witness. A later controlled parity run emitted 129,241,066 server
bytes and delivered only 10.1 MiB/s, while the same body moved and verified at
250.7 MiB/s over TCP and 71.1 MiB/s over .NET's MsQuic-backed QUIC stream. An
existing mapped file opened in 10.9 ms without transferring the body.

The ownership migration is not discarded. The physical content connector and
its gates must be replaced. CultNet remains the typed schema and dispatch
substrate; it must not imply that every payload belongs inside the custom RUDP
message plane.

## Was the branch following the plan?

Yes, substantially—and that is why this incident cannot be dismissed as one
implementation ignoring good documentation.

| Plan commitment | Branch result | Verdict |
| --- | --- | --- |
| Stable identity and shared sessions before transport replacement | Discovery and session owners were implemented before content migration | Followed |
| Authority independent of contact and transport success | A mandatory resolver and provider/body authorization were added | Followed |
| One verified content-transfer owner | Resume state, range verification, failover, hashing, and atomic promotion moved into `CultMeshContentTransferService` | Followed |
| Remove renderer-owned chunk and cache authority | EveUnity delegates transfer and only lowers the verified result | Followed |
| Bounded adaptive concurrency | A fixed four-request window was added; adaptation was explicitly deferred | Partial |
| Caller-provided streaming destinations and backpressure | Not implemented before the managed content session became the released default | Not followed |
| Real consumer plus hostile-network proof at phase closure | A real released consumer ran, but the hostile harness did not drive that physical path | Not followed |
| Borrow QUIC/Iroh boundaries before dependencies | Connector/session/body boundaries were borrowed | Followed |
| Consider Iroh or QUIC after the boundary and harness exist | The prerequisite existed, but no mandatory comparison or adoption decision occurred | Missed transition |
| Public surface becomes smaller and more obvious | Application ownership shrank, but the physical RUDP machinery expanded | Mixed |

The plan also lacked a throughput, amplification, flow-control, copy-budget, or
comparative transport gate. Consequently, the branch could follow much of the
written plan and still release an unfit bulk path. This is both a plan defect
and an execution defect:

- **Plan defect:** it treated RUDP feature completeness as a sufficient reason
  to defer physical transport evaluation and did not define the evaluation
  trigger or fitness budget.
- **Execution defect:** once repeated cold failures supplied contrary evidence,
  the work kept repairing RUDP locally instead of reopening the premise.

## User-visible impact

- A same-machine Aetheria daemon and Unity client copied a provider-owned asset
  bundle through UDP instead of opening an authorized mapped body.
- The 0.3.91 released Unity test took 54.803 seconds. File timestamps place the
  56,204,750-byte body promotion roughly 31 seconds after partial creation.
- The 0.3.92/CultLib 1.0.33 witness reduced the Unity test to 36.028 seconds and
  body transfer to roughly 12 seconds, but daemon transmit volume rose to
  105,911,320 bytes. The apparent speedup came partly from more aggressive
  packet emission and retransmission.
- Cold startup competed with transient gameplay state and initially allowed a
  live body publication lease to expire before subscription.
- Release and witness effort was repeatedly spent changing drains, sleeps,
  ACK handling, request windows, and lifecycle ordering rather than selecting
  the intended data plane.
- Remote behavior under real latency, loss, and bandwidth contention remains
  unproven and is likely worse than loopback evidence.

Content hashes, incremental chunk verification, final SHA-256 verification,
and atomic promotion prevented corrupted or partial bodies from becoming cache
truth. The incident is a performance and architecture failure, not known data
corruption.

## Intended architecture

The reliability plan's durable intent was:

1. stable endpoint identity rather than application-owned physical endpoints;
2. shared long-lived sessions rather than request-scoped clients;
3. authority independent of contact and transport success;
4. content identity independent of provider and location;
5. verified resumable transfer owned outside renderers;
6. negotiated local mappings and remote streams behind the same body identity;
7. Iroh/QUIC lessons expressed in boundaries before dependencies were adopted.

The intended phrase “CultNet as the transport substrate” meant that registered
schemas, typed envelopes, dispatch, and diagnostics remain portable across
transport connectors. It did not require every body byte to be serialized as a
schema-message field or fragmented by CultNet's RUDP implementation.

## What was implemented

The following pieces match the plan and remain useful:

- `CultMeshDiscoveryService` owns route observations and freshness.
- `CultMeshSessionManager` owns identity-first connection reuse and path state.
- `CultMeshAuthorityResolver` separates contact from authorization.
- `CultMeshContentTransferService` owns partial files, verified ranges,
  provider failover, final hashing, and atomic cache promotion.
- content hashes remain independent of provider identity and location.
- body publications preserve schema, layout, producer epoch, sequence, access,
  lease, and transport representations.
- exact reactive subscriptions and SoA demand avoid broad per-frame snapshots.
- verified client cache files can be mapped without allocating another managed
  body after transfer.

The incorrect slice was the managed content session:

```text
manifest chunk
  -> CultMeshContentChunkRequestMessage
  -> CultMeshContentChunkResponseMessage.Payload
  -> MessagePack schema frame
  -> custom RUDP fragmentation
  -> per-packet ACK/resend/reassembly
  -> managed byte[] chunk
  -> verified partial file
```

The desired path is:

```text
typed content request / authorized body capability
  -> CultMesh session selects an advertised physical plane
  -> same machine: provider-owned mapped file/shared memory
  -> remote: proven independent byte stream/range fetch
  -> existing verified partial/final promotion owner
```

Registered schemas remain the control language. They identify the body, hash,
range, lease, stream, progress, cancellation, and receipt. They do not need to
contain the body itself.

## Timeline

### Before the migration plan

- **2026-06-14 to 2026-06-17:** commits `8ce89688` through `1c85eabe`
  introduced the C# transport port, custom RUDP reliability core, socket
  transport, fragmentation, schema ergonomics, reconnect support, and a
  multi-peer listener.
- **2026-07-10 to 2026-07-12:** snapshot connection reuse and large-snapshot
  fixes made RUDP the working physical path for the first Aetheria witness.

### Reliability migration

- **2026-07-12, `f0b5e103`:** the canonical reliability plan was committed. It
  explicitly treated low-level RUDP reliability as present and deferred Iroh or
  QUIC until session ownership and a failure harness existed.
- **2026-07-12, `fc65e25f`:** Phase 0 added a deterministic fault scheduler and
  diagnostics. The harness could replay hostile events, but it was not wired to
  the public content transfer or actual transport connectors.
- **2026-07-13, `b9f17a71`:** `CultMeshContentTransferService` correctly took
  ownership of resumable verification and atomic promotion.
- **2026-07-14, `7d571402`:** the decisive wrong fork. Managed content sessions
  embedded chunk bytes in typed schema responses over the existing session.
  The primary test used an in-memory loopback transport and therefore proved
  ownership and message shape without exercising RUDP.
- **2026-07-14, `a311cbe8`:** fixed request concurrency and RUDP polling to
  improve throughput. The plan explicitly deferred adaptive scheduling and
  streaming destinations instead of reopening physical-plane selection.
- **2026-07-14, `b943fa7e`:** a 13,006,384-byte cold run completing under the
  unchanged 300-second deadline was recorded as content-session delivery proof.
- **2026-07-16, `25231429`:** a physical RUDP content test was finally added.
  Its one-MiB payload only had to finish within ten seconds. It recorded wire
  statistics on failure but asserted no goodput or lossless amplification
  budget.
- **2026-07-16, `15bffc6d`:** a 46,412,384-byte released cold witness was
  recorded as proof while explicitly acknowledging that the managed session
  still copied and fragmented the body.
- **2026-07-17 to 2026-07-19:** ACK synchronization, concurrent session
  serialization, exact body demand, reactive delivery, and live subscription
  work made the path more correct under concurrency. None tested whether RUDP
  deserved to carry bulk bytes.
- **2026-07-19, `2afec5c5`:** per-32-packet `Thread.Sleep(1)` calls were replaced
  by `Thread.Yield`. The cold transfer became faster while wire volume nearly
  doubled, revealing that the system was flooding loopback rather than
  controlling flow.
- **2026-07-19, `ee0fb235`:** the first dedicated transport parity harness
  compared the Aetheria-sized public content path with mapped-file, TCP, and
  MsQuic-backed QUIC baselines. The result rejected further RUDP bulk tuning.

## Detection

The incident surfaced only after the released-package witness became fully
cold and provider-owned assets grew to 56,204,750 bytes. Earlier warm-cache
runs bypassed transfer. Earlier cold runs were framed as timeout/reliability
problems rather than fitness problems.

The decisive signals were:

1. transfer duration remained many seconds on loopback;
2. removing scheduler sleeps reduced duration but drove daemon transmit volume
   to almost twice the body size in the released witness;
3. the dedicated parity harness showed a stable order-of-magnitude gap;
4. opening an existing provider body locally required milliseconds and no
   transfer, proving that the same-machine witness was selecting the wrong
   plane before RUDP performance even mattered.

## Root cause

### Primary cause: capability was mistaken for fitness

The plan listed ACK, resend, ordering, duplicate suppression, fragmentation,
timeout, and bounded pending work and concluded that transport replacement was
not the first target. Those properties can establish eventual reliable message
delivery. They do not establish congestion control, receiver flow control,
packet pacing, batching, stream multiplexing, loss recovery quality, or bulk
goodput.

The custom RUDP path had a 32-packet acknowledgement mask, 1,024-byte fragments,
managed per-fragment bookkeeping, and no congestion window tied to receiver
progress. A sender could emit multiple 256-KiB chunk responses concurrently.
Yielding after 32 sends yielded CPU time; it did not create backpressure or
wait for acknowledgement progress.

### Secondary cause: “CultNet substrate” was underspecified

The plan did not say plainly enough that CultNet owns typed schema semantics
independently of physical transport. Implementers treated “preserve CultNet as
the transport substrate” as a reason to carry content through the available
RUDP schema channel. This collapsed two distinct claims:

- CultNet documents and registered schemas remain the portable protocol.
- the current CultNet RUDP implementation is suitable for every payload class.

Only the first claim was required.

### Secondary cause: the QUIC/Iroh adoption trigger had no gate

The plan correctly warned against adopting Iroh or QUIC before session and
failure boundaries existed. It did not define a mandatory decision point once
those boundaries existed. Phase 2 delivered the connector/session seam, but
Phase 4 did not require candidate transports to pass a fitness comparison
before becoming the content default.

The deferral therefore became sticky. “Not yet” silently became “continue using
RUDP.”

### Secondary cause: evidence harnesses observed the wrong layer

Phase 0's deterministic network proved that a fault schedule could replay
loss, duplication, delay, reordering, partition, corruption, endpoint rotation,
and restart. It did not drive the RUDP socket transport, session manager, or
content transfer service. A self-test of the harness was treated as sufficient
foundation for later phase proof.

The first managed-content test used an in-memory schema bridge. It proved that
content did not use snapshot records, sessions were reused, hashes matched, and
warm cache avoided another request. It could not reveal fragmentation cost,
socket flooding, resend amplification, scheduling delays, or contention with
other messages.

The later physical test's ten-second bound for one MiB was a hang detector, not
a performance contract. The 300-second released witness deadline was likewise
a liveness ceiling, not a native-experience gate.

### Secondary cause: local failures were repaired locally

Each symptom had a plausible narrow explanation:

- snapshot payloads starved peers;
- poll loops slept after control packets;
- fragment sends slept per packet;
- ACKs fell outside the rolling mask;
- concurrent publishers mutated one reliability session;
- content subscription leases expired during cold loading.

The fixes were individually defensible and often necessary for small-message
correctness. Together they kept attention inside RUDP. No rule required a
transport parity check after two transport-level fixes failed to produce an
acceptable cold path.

This is the process failure described by the project's Jenga warning: the
machine supplied enough local context to justify each patch while withholding
the comparative evidence needed to question the owner.

### Secondary cause: post-transfer mapping obscured pre-transfer cost

The witness reported `SharedFileMapping` because EveUnity mapped the verified
client cache file before loading the Unity bundle. That was true at the body
lease layer, but incomplete at the user-visible layer: the provider-owned file
had already crossed RUDP into the client cache.

The metric did not lie, but it observed the wrong interval. No durable witness
fact distinguished:

- provider-owned same-machine mapping with zero body transfer;
- client-cache mapping after remote delivery;
- managed network-buffer fallback.

### Secondary cause: the plan became both specification and evidence ledger

The plan accumulated phase body maps, implementation notes, witness results,
and partial-proof language in the same document that still contained the
original `Cut first`, `Build`, and `Gate` contracts. That made useful progress
visible, but it blurred the difference between these two statements:

- a coherent slice of the phase has evidence;
- the phase's closure contract has been satisfied.

In Phase 4, content ownership, verification, and released-consumer delivery had
evidence while adaptive scheduling, caller-provided streaming destinations,
backpressure, hostile-network coverage of the real physical path, and transport
fitness remained open. The document could therefore tell an increasingly
convincing success story without forcing the remaining gate failures back to
the top of the decision process.

Evidence belongs beside the plan, but phase status must be derived only from an
explicit closure checklist. A partial proof may close a named slice; it cannot
silently weaken the parent phase gate.

## Five whys

1. **Why was Aetheria cold asset delivery slow?**

   The provider sent a large immutable body as thousands of reliable UDP
   fragments with heavy retransmission and managed per-packet work.

2. **Why was bulk content sent through that path?**

   Phase 4 represented chunks as typed request/response schema messages on the
   existing CultMesh session.

3. **Why was the existing session assumed suitable?**

   The plan equated implemented reliability features with transport fitness and
   deferred Iroh/QUIC until the session boundary existed.

4. **Why was the assumption not revisited after the boundary existed?**

   There was no mandatory transport parity gate, and every test emphasized
   ownership, eventual completion, or failure recovery rather than comparative
   goodput and amplification.

5. **Why did repeated fixes not trigger redesign?**

   The workflow classified each failure as another RUDP correctness defect and
   rewarded a passing witness under generous timeouts. It lacked an escalation
   rule that says repeated physical-transport repairs reopen transport
   selection.

## What went well

- Content identity, verification, restart state, provider failover, and atomic
  promotion were moved to a coherent owner and survived the incident.
- The released-package witness eventually exercised a truly cold cache and
  exposed the problem at the user-visible boundary.
- Daemon transport counters made wire amplification observable.
- The later parity harness reused the public transfer path instead of comparing
  only synthetic packet loops.
- The unreleased two-chunk/Sleep(0) tuning experiment was reverted rather than
  released after it showed improved duration with unacceptable wire behavior.
- The plan had preserved a transport connector boundary, so replacing the
  physical plane does not require returning authority to EveUnity or Aetheria.

## What went poorly

- Research direction was treated as future inspiration after its adoption
  prerequisite had already been met.
- The deterministic failure harness was not integrated with the systems whose
  reliability it purported to de-risk.
- “Large” tests used 128 KiB, 700 KiB, or one MiB while the consumer used 13 to
  56 MiB artifacts.
- Test thresholds detected hangs but certified them as performance proof.
- Lossless wire amplification was not a release gate.
- The cold witness separated gameplay from content for valid lifecycle reasons,
  but that separation also reduced pressure to make cold startup feel native.
- Plan updates faithfully described partial slices yet allowed “proven” wording
  before the full Phase 4 build and gate lists were complete.
- The same-machine deployment advertised and selected network delivery before
  mapping the resulting client cache.

## Corrective architecture

### Owner

`CultMeshSessionManager` owns physical-plane selection for an endpoint identity
and application protocol. `CultMeshContentTransferService` owns verification,
resume state, and final promotion. A selected transport connector owns byte
movement. CultNet schema dispatch owns none of the physical reliability policy.

### Inputs

- stable endpoint and provider identity;
- authorized content hash, byte size, ranges, and lease;
- provider-advertised physical candidates;
- consumer-supported candidates;
- same-machine evidence;
- platform capabilities and measured connector health.

### Outputs

- same-machine: an authorized provider-owned mapped file or shared-memory body;
- remote: an independent proven stream/range fetch feeding the transfer owner;
- typed progress, cancellation, failure, and receipt state over CultNet.

### Derived state

Selected plane, endpoint address, stream identifiers, byte progress, path
health, and performance telemetry are derived. They do not alter content hash,
provider authority, or cache truth.

### Forbidden writers

- schema-message helpers cannot embed bulk body payloads;
- renderers cannot choose physical endpoints or write final cache entries;
- a body adapter cannot confer provider authority;
- RUDP retry, chunk-window, or timeout tuning cannot make RUDP the preferred
  bulk plane;
- a client-cache mapping cannot claim same-machine zero-copy provenance.

### Shared paths

Every remote connector terminates in the existing verified-range and atomic
promotion primitives. Local mapping and remote fallback preserve the same body
identity, schema/layout, epoch/sequence, access mode, and lease semantics.

### Deletion line

1. Stop adding bulk-performance behavior to custom RUDP.
2. Prevent `CultMeshContentChunkResponseMessage.Payload` from being the default
   content body path.
3. Make RUDP an explicit compatibility/control candidate, not the implicit
   connector for `cultmesh.content.v1`.
4. Delete RUDP content serving once every released consumer negotiates local
   mapping or a dedicated remote stream.
5. Retain RUDP only for payload classes whose own fitness gates justify it;
   eventual cleanup may remove the custom reliability engine entirely.

## Corrective actions

### Completed

- [x] Add `tools/GameCult.TransportParity` using the public CultMesh transfer
  owner and Aetheria-sized payloads.
- [x] Compare mapped-file open, TCP stream, MsQuic-backed QUIC stream, and
  CultMesh RUDP on the same host.
- [x] Record duration, goodput, and RUDP server wire bytes.
- [x] Stop and revert the unreleased request-window/pacing experiment.
- [x] Amend the reliability plan with the physical body-plane ownership cut.

### Required before another bulk transport release

- [ ] Make same-machine evidence select a provider-owned mapped body before any
  network fetch.
- [ ] Attach at least one proven remote stream connector behind
  `CultMeshSessionManager` and the content protocol identity.
- [ ] Keep content request, authorization, range, progress, cancellation, and
  receipt state typed while moving body bytes out of schema response payloads.
- [ ] Integrate the deterministic hostile-network schedule with the actual
  connector and public content transfer path.
- [ ] Exercise Aetheria-sized payloads, not only unit-sized fixtures.
- [ ] Add cold and warm released-consumer witnesses that report plane
  provenance, bytes sent, bytes copied, promotion time, and total lowering time.
- [ ] Prove remote cancellation, range resume, provider failover, and corruption
  recovery over the selected stream.
- [ ] Define and enforce platform packaging for Unity, daemon, TypeScript, and
  other promoted runtimes before making the connector the public default.

### Required transport fitness gates

The precise budgets may be tightened as deployment data accumulates, but a
candidate cannot become the default without all of these categories:

- **Comparative goodput:** integrated verified transfer measured against the
  same connector's raw stream baseline on the same host and route.
- **Lossless amplification:** total wire bytes bounded relative to body bytes;
  success alone is insufficient.
- **Small-message latency:** control messages and receipts remain responsive
  while a body transfers.
- **Backpressure:** sender progress follows receiver and destination capacity;
  thread yields and timeout retries are not accepted as flow control.
- **Hostile network:** loss, duplication, reordering, latency, partition,
  reconnect, and path change drive the actual public path.
- **Memory/copy budget:** allocation and copy counts distinguish provider-owned
  mapping, streamed promotion, client-cache mapping, and managed fallback.
- **User-visible cold start:** a released generic client lowers the world within
  an explicit experience budget; a multi-minute timeout is only a deadlock
  guard.

### Escalation rule

After two fixes in the physical transport layer for the same consumer outcome,
work stops and transport selection is reopened against dedicated baselines.
Further local transport repair requires evidence that the current connector
still satisfies its fitness budget. This is not a ban on fixing bugs; it
prevents repeated correctness patches from impersonating architectural proof.

## Plan amendments

The reliability plan should now be read with these corrections:

1. “CultNet as transport substrate” means typed schema identity, framing,
   dispatch, diagnostics, and connector contracts. It does not bless a single
   physical transport for all payloads.
2. Implemented ACK/resend/fragmentation is evidence of reliable-message
   capability, not evidence of bulk, realtime, or media fitness.
3. Once the session connector boundary exists, physical candidates must pass a
   workload-specific parity gate before a consumer migration closes.
4. Typed control state and body transport are separate. Registered schemas make
   the control plane smaller; they do not require body bytes to become document
   fields.
5. Same-machine mapping is selected before transfer. Mapping a cache file after
   transfer is a useful lowering optimization but not zero-copy delivery.
6. A phase slice may be recorded as partial evidence, but “proven” and “closed”
   require the complete build list, gates, real consumer, and performance
   evidence relevant to that slice.

## Evidence

### Source and history

- Canonical plan: `src/GameCult.Mesh/docs/reliability-migration-plan.md`, commit
  `f0b5e103`.
- Managed content session fork: commit `7d571402`.
- Fixed request concurrency and drain work: commit `a311cbe8`.
- First physical one-MiB RUDP content regression: commit `25231429`.
- Scheduler-yield release: commit `2afec5c5`.
- Dedicated parity harness and initial body-plane correction: commit
  `ee0fb235`.

### Released witness

- EveUnity `artifacts/cold-release-0391/results.xml`: 54.803-second Unity test.
- EveUnity `artifacts/cold-release-0392/results.xml`: 36.028-second Unity test.
- EveUnity `artifacts/cold-release-0392/aetheria-daemon.log`: final daemon
  counters `rx=3903892 tx=105911320` for the cold run.
- Both witness documents report a 56,204,750-byte provider asset and
  `SharedFileMapping`; the mapping occurred after network promotion into the
  client cache.

### Parity harness

- Payload: 56,204,750 bytes.
- Existing mapped file: 10.9 ms to open and touch; no body transfer.
- TCP plus file write and SHA-256 verification: 213.8 ms, 250.7 MiB/s.
- MsQuic-backed QUIC plus file write and SHA-256 verification: 753.4 ms,
  71.1 MiB/s; repeated runs reached 87.0 to 107.4 MiB/s.
- CultMesh RUDP public content transfer: 5,322.9 ms, 10.1 MiB/s,
  129,241,066 server bytes; repeated runs reached 10.1 to 13.0 MiB/s.

## Accountability

Git author identity does not distinguish human-authored work from agent work in
this repository, so this postmortem does not infer personal authorship from the
commit metadata. The failure belongs to the engineering process and especially
to the agent execution that kept accepting narrow transport repairs as progress
toward the user's native-experience requirement.

The user explicitly warned that cold asset delivery was not healthy enough to
claim, that larger timeouts must not conceal it, and that the daemon architecture
could not cost game performance. The eventual switch from “times out” to
“finishes before the deadline” did not satisfy those constraints. The agent
should have introduced comparative transport evidence at the first Phase 4
physical failure, not after several releases and witness iterations.

## Closing statement

The reliability migration did not fail because it learned from Iroh and QUIC.
It failed because it stopped at their control-plane lessons after building the
seam where their transport machinery was meant to attach.

The repair is not to discard the new discovery, session, authority, transfer,
or body ownership. It is to finish the architecture: keep registered schemas as
the small typed language of CultNet, attach proven physical transports beneath
the session boundary, and make every promoted plane prove that it deserves the
workload before a released consumer depends on it.
