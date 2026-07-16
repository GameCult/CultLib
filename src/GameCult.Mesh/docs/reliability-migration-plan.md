# CultMesh Reliability Migration Plan

## Objective

CultMesh should give an application one typed, watchable route to a Verse,
service, document, content object, or stream without making that application own
bootstrap discovery, physical topology, reconnect loops, refresh polling, or
transfer recovery.

The migration preserves CultCache as the typed persistence substrate and
CultNet as the transport substrate. It moves reliability policy out of callers
and the broad `CultMesh` convenience facade into six explicit organs:

1. discovery control plane
2. connection and session manager
3. authority resolver
4. content transfer scheduler
5. stream control plane
6. negotiated body transport

This is an ownership migration, not a new layer wrapped around the existing
caller-driven machinery. Each phase cuts an obsolete decision path before its
replacement becomes the public default.

## Current Body

| Organ | Current owner | Current output | Reliability gap |
| --- | --- | --- | --- |
| Typed persistence | CultCache | Documents, indexes, watches, `.cc` state | Not the owner of network freshness or authority |
| Wire transport | CultNet | Schema messages, RUDP sessions, framed transports | Connection supervision and path policy are caller-owned |
| Node composition | `CultMeshNode` over `CultNetHost` | Database, server, cache, document handles | Facade appears to own more lifecycle than it does |
| Verse discovery | Discovery client plus caller-owned catalogs | Verse descriptors | Sequential, fail-fast, request-scoped, no durable freshness |
| Peer exchange | Peer client plus mutable peer catalog | Contact candidates | Last-write-wins, no intrinsic expiry or provenance |
| Authority | Lease catalog plus caller composition | Authorized peer selection | Optional composition; peer contact and trust are too easy to conflate |
| CDN | Manifest/chunk helpers | Verified complete byte arrays | No resumable transfer, provider selection, scheduling, or eviction |
| Streaming | In-process stream catalog | Negotiation and latest local frame handles | No distributed liveness, epochs, gaps, backpressure, or failover |

The low-level RUDP implementation already covers acknowledgement, resend,
ordering, duplicate suppression, fragmentation, timeout, and bounded pending
work. The first migration target is therefore not a transport rewrite. It is
the missing control plane that keeps discovery results, sessions, and transfers
usable while endpoints fail or move.

## Target Authority Map

### Discovery control plane

**Owner:** `CultMeshDiscoveryService`.

**Inputs:** endpoint or service identity, Verse constraints, configured
bootstrap sources, Odin publications, LAN sources, optional DNS/DHT sources,
clock, persistent discovery store, and source-specific lookup ports.

**Outputs:** a watchable stream of signed route candidates with source,
observed time, expiry, capabilities, transport kind, and health evidence.

**Derived state:** preferred ordering, stale/degraded status, source health,
and negative lookup cache entries.

**Forbidden writers:** discovery transports, UI runtimes, peer gossip, and
bootstrap configuration must not directly replace the resolved candidate set.
They may only submit observations to the discovery owner.

**Demotion:** `CultMeshVerseCatalog` and `CultMeshPeerCatalog` are no longer
owners of current reachability. They become typed projections of discovery
state for inspection and compatibility.

### Connection and session manager

**Owner:** `CultMeshSessionManager`.

**Inputs:** stable endpoint identity, application protocol identity, discovery
candidate stream, transport connectors, path policy, clock, and retry policy.

**Outputs:** a reusable `CultMeshSession` with typed connection state, chosen
path, protocol channel, diagnostics, and watches for state/path changes.

**Derived state:** candidate ranking, backoff, circuit state, relay/direct
preference, path migration, and last failure.

**Forbidden writers:** snapshot helpers, applications, renderers, and content
downloaders must not create their own reconnect loops or decide which endpoint
is currently healthy.

**Demotion:** physical endpoint strings are no longer application routing
authority. They are candidates observed by discovery. Request-scoped schema
clients become a compatibility path that delegates to a session.

### Authority resolver

**Owner:** `CultMeshAuthorityResolver`.

**Inputs:** peer observations, signed leases, Verse policy, shard epoch,
revocations, requested role, and requested resource scope.

**Outputs:** an authorized endpoint identity and scope, or a typed denial with
the evidence used to decide it.

**Derived state:** currently valid role and shard assignments.

**Forbidden writers:** peer catalogs, transport success, caches, and UI state
must never confer authority.

**Demotion:** an advertised peer card is only contact evidence. It is never an
authorization result.

#### Phase 3 body map

**Owner:** `CultMeshAuthorityResolver` is the only component that decides
whether a peer may act. Decisions are evaluated on demand and are never cached
as grants.

**Inputs:** `CultMeshAuthorityRequest`, an `ICultMeshAuthorityLeaseSource`, an
`ICultMeshAuthoritySignatureVerifier`, an
`ICultMeshAuthorityRevocationSource`, the current authority epoch, and an
injected `ICultMeshClock`.

**Outputs:** `CultMeshAuthorityDecision`, carrying either the accepted lease
evidence identifiers or one typed denial reason. Authority diagnostics carry
lease, peer, and issuer identifiers but never signature material.

**Derived state:** peer selection is a projection over contact candidates and
fresh resolver decisions. The lease catalog is only a mutable evidence source;
the peer catalog is only a contact source.

**Forbidden writers:** lease replacement, peer-card publication, successful
transport connection, cached presence, and wall-clock defaults cannot grant or
restore authority. Revocation and epoch policy remain outside both catalogs.

**Shared paths:** gameplay sessions expose their resolver; peer selection and
both privileged RUDP creation paths call that resolver. Compatibility overloads
delegate to the same resolver with deny-by-default signature policy.

**Cut line:** `CultMeshAuthorityLease.Covers`,
`CultMeshAuthorityLeaseCatalog.IsAuthorized`, and catalog-owned
`FindAuthorized`/`FirstAuthorized` no longer contain authorization logic.
Version-zero or unsigned leases are not reinterpreted as trusted epoch-zero
leases. Callers must supply a version-one lease, explicit epoch, signature
verifier, and revocation source.

**Compatibility inventory:** the old lease/catalog peer-selection and RUDP
overloads remain source-compatible for migration, but cannot authorize because
they have no signature-verification authority. They delegate to a
deny-by-default resolver and should be replaced by resolver-taking overloads.

### Content transfer scheduler

**Owner:** `CultMeshContentTransferService`.

**Inputs:** content hash or artifact manifest, provider observations,
authorized sessions, cache, range/chunk verifier, transfer policy, bandwidth
budget, and cancellation.

**Outputs:** incrementally verified ranges, durable resume state, progress
events, and an atomically committed complete artifact.

**Derived state:** missing ranges, provider score, retry schedule, parallelism,
and eviction eligibility.

**Forbidden writers:** renderers and application packages must not implement
their own chunk loops, partial-file conventions, provider failover, or final
cache commit.

**Demotion:** CDN manifests describe content. They do not own provider
selection or prove signer authority merely by containing valid hashes.

### Stream control plane

**Owner:** `CultMeshStreamService`.

**Inputs:** publisher identity and lease, stream descriptor, epoch, clock
evidence, subscriber constraints, available body transports, and session
health.

**Outputs:** stream subscriptions with negotiated body transport, explicit
sequence/gap state, liveness, priority, expiry, and failover events.

**Derived state:** preferred publisher, body transport, copy budget, buffered
window, and obsolete-object cancellation.

**Forbidden writers:** a shared texture, memory ring, QUIC stream, renderer, or
latest-frame cache must not own stream identity or publisher liveness.

**Demotion:** the current in-process stream catalog becomes a local projection
and body adapter. It is not the distributed stream registry.

### Negotiated body transport

**Owner:** `CultMeshBodyTransportService` owns representation negotiation and
the lifetime of transport-specific body access. It does not own body identity,
publisher authority, content integrity, or application semantics.

**Inputs:** a typed body descriptor, authorized producer lease, consumer
capabilities, locality evidence, schema/layout support, transport adapters,
clock, and access policy.

**Outputs:** a read-only body lease by default, bound to one logical body
identity, schema/layout version, producer epoch, sequence, and negotiated
transport. Local peers may receive an OS-handle capability token; remote peers
receive the same typed body through the network representation.

**Derived state:** transport preference, local/remote eligibility, mapped
region or file-handle lifetime, slot availability, and lease expiry.

**Forbidden writers:** process IDs, virtual addresses, mapping names, OS
handles, and transport endpoints cannot become logical identity. A successful
mapping cannot authorize a producer, extend a lease, bless content, or permit a
consumer to mutate the body.

**Demotion:** shared-memory regions and file mappings are body adapters. They
do not become a local message bus, alternate discovery plane, or source of
authority.

## Shared Public Shape

The target API is identity-first and watchable:

```csharp
await using var session = await mesh.ConnectAsync(
    CultMeshEndpointId.Parse("odin:yggdrasil"),
    CultMeshProtocols.Documents,
    cancellationToken);

await foreach (var state in session.WatchStateAsync(cancellationToken))
    diagnostics.Render(state);

var document = session.Document<DaemonHealth>("daemon:mimir.health.v1");
await foreach (var current in document.WatchAsync(cancellationToken))
    Render(current);
```

Content retrieval uses a separate owner:

```csharp
await foreach (var progress in mesh.Content.FetchAsync(
    manifest.ContentHash,
    destination,
    cancellationToken))
{
    Render(progress);
}
```

The convenience facade may expose these entry points, but it delegates to
long-lived injected services. It does not contain their policy.

## Phase 0: Evidence and Failure Harness

Before changing ownership, establish probes at the layer where callers observe
failure.

### Build

- Introduce injectable clocks, lookup sources, transport connectors, and fault
  schedules for the new organs.
- Add a deterministic network harness capable of loss, duplication,
  reordering, latency, partition, endpoint rotation, corruption, and restart.
- Define typed diagnostic events for discovery observations, candidate
  rejection, connection attempts, path changes, retry decisions, authority
  decisions, verified ranges, and stream gaps.
- Record served library/schema versions in diagnostics so stale deployments
  cannot impersonate protocol defects.

### Gate

- Existing RUDP and Mesh suites remain green.
- A failing discovery timeline can be reproduced deterministically.
- Tests can observe the public session/document state, not only internal
  counters.
- Diagnostics have bounded cost and can be disabled or sampled on hot paths.

### Phase 0 Body Map

**Owner:** reliability organs own their clocks and emit typed observations;
`CultMeshDiagnosticBuffer` owns only a bounded inspection projection.

**Inputs:** injected `ICultMeshClock`, organ state transitions, endpoint and
source identity, reason code, and served schema/library version.

**Outputs:** ordered `CultMeshDiagnosticEvent` observations. The deterministic
test network emits scheduled packet deliveries under loss, duplication,
reordering, latency, partition, endpoint rotation, corruption, and restart.

**Derived state:** diagnostic sequence and the bounded recent-event window.
Neither confers reachability, authority, or retry policy.

**Forbidden writers:** diagnostic sinks and fault harnesses cannot choose a
route, mutate discovery candidates, authorize a peer, or repair a session.

**Shared paths:** the existing Verse discovery client now uses the injected
clock for connection and response deadlines and emits the same typed timeline
for success, timeout, and transport failure.

**Cut line:** wall-clock reads and unobservable timeout delays have been removed
from Verse discovery. Caller-owned catalog mutation and endpoint sequencing
remain compatibility authority until Phase 1 replaces and demotes them.

**Verification layer:** tests observe the emitted discovery timeline and served
version, while the deterministic network harness proves exact replay of every
required hostile-network action.

## Phase Closure Contract

Every implementation phase must close with all of the following:

- **Visible outcome:** name the consumer behavior that improved, including its
  behavior during failure rather than only after recovery.
- **Authority reduction:** identify the old writer, loop, registry, or cache
  opinion that was deleted or demoted.
- **State transition:** document what survives restart, what is reconstructed,
  and how existing durable state is migrated or ignored.
- **Compatibility inventory:** list every remaining caller of a compatibility
  surface and the condition for deleting that surface.
- **Rollback:** roll back the consumer to the previous complete owner as one
  unit. Never run old and new owners concurrently behind a flag that lets both
  decide current state.
- **Body map:** update owner, inputs, outputs, derived state, forbidden writers,
  shared paths, cut line, and verification evidence before closing the phase.
- **Proof:** migrate at least one real consumer and pass the hostile-network
  cases relevant to the phase.

Wire compatibility and API compatibility are tracked separately. A wire shim
may preserve an external protocol while delegating all policy to the new owner.
An API shim may preserve a call shape while delegating to the new service. A
shim may not retain endpoint choice, retry policy, cache truth, or authority.

## Phase 1: Discovery Ownership

### Cut first

- Stop discovery clients from mutating caller-owned catalogs directly.
- Stop multi-endpoint lookup from being sequential and fail-fast.
- Stop transient lookup failure from replacing last-known-good results with an
  empty set.

### Build

- Add a narrow `ICultMeshLookupSource` port returning asynchronous candidate
  observations with provenance and freshness.
- Query eligible sources concurrently and merge results by stable endpoint
  identity.
- Deduplicate concurrent lookups for the same identity.
- Persist last-known-good candidates and explicit positive/negative expiry in
  CultCache.
- Expose fresh, stale, degraded, and unavailable states distinctly.
- Make Odin the default service/rendezvous source without making Odin the owner
  of provider state.

### Compatibility

Existing `DiscoverAsync` methods delegate to the discovery service, take a
snapshot of its current projection, and retain their result shapes. They do not
retain an independent endpoint loop or catalog opinion.

### Gate

- One dead source does not discard successful results from other sources.
- Restart reconstructs last-known-good state with preserved provenance.
- Expired results remain inspectable but cannot masquerade as fresh.
- Concurrent callers share lookup work without stealing results from one
  another.
- A poisoned or unsigned observation cannot override an accepted signed
  candidate.

### Phase 1 Body Map

**Owner:** `CultMeshDiscoveryService` owns current candidate observations,
freshness, source failures, positive/negative expiry, and shared in-flight
lookup work for a stable identity plus constraints.

**Inputs:** `ICultMeshLookupSource` observations, injected clock, optional
`ICultMeshDiscoveryStore`, query identity/Verse constraints, and caller-local
cancellation.

**Outputs:** watchable `CultMeshDiscoveryState` projections with fresh,
degraded, stale, or unavailable status; persisted
`gamecult.mesh.discovery_state.v1` last-known-good documents; typed diagnostic
events.

**Derived state:** failed-source lists, retry-after time, candidate ordering,
and legacy `CultMeshVerseCatalog` contents. The catalog is no longer a lookup
writer.

**Forbidden writers:** lookup sources cannot replace the candidate set, caller
cancellation cannot cancel shared lookup work, and rejected observations cannot
override accepted candidates.

**Shared paths:** direct service callers and compatibility `DiscoverAsync`
both use concurrent source lookup and the same freshness merge. Compatibility
projects the service result into the old catalog only after resolution.

**Cut line:** sequential fail-fast endpoint lookup and per-response catalog
mutation have been removed from Verse discovery. Physical endpoint lookup
remains available only as a compatibility source observation.

**State transition:** restart loads the typed CultCache discovery document.
Expired candidates remain inspectable as stale; unavailable results retain a
bounded negative expiry before sources are queried again.

**Compatibility inventory:** `AetheriaRuntimeVerseDiscovery` remains the only
known caller of `DiscoverAsync(catalog, endpoints)`. It receives the service
projection now and should migrate to watching `CultMeshDiscoveryState` during
the Phase 2 Aetheria session migration.

**Rollback:** revert the service, store document, and compatibility delegation
as one unit. Do not run sequential catalog mutation alongside service-owned
resolution.

**Proof:** Mesh tests cover concurrent good/dead sources, shared in-flight work,
caller-local cancellation, negative expiry, signed precedence, stale-on-error,
public state watches, and CultCache restart reconstruction. Networking tests
prove the compatibility method retains a good concurrent source.

## Phase 2: Long-Lived Sessions

### Cut first

- Remove direct request-scoped client creation from snapshot, Verse discovery,
  and peer-exchange implementations.
- Remove application-owned reconnect and endpoint-selection loops from the
  first migrated consumer.

### Build

- Introduce `ICultMeshTransportConnector` and a session manager keyed by
  endpoint identity plus application protocol.
- Reuse in-flight connection attempts and established sessions.
- Race eligible physical candidates under a bounded policy.
- Provide typed reason codes for resolution, authentication, transport,
  protocol, timeout, cancellation, and authority failures.
- Expose watchable online, degraded, reconnecting, and offline states.
- Keep relay/tunnel/direct choice behind the connector boundary.
- Replace ambiguous physical schemes with typed transport candidates. Logical
  `cultmesh://` routes remain identity/service references.

### First consumer

Migrate Aetheria runtime discovery or EveUnity provider discovery end to end.
The selected consumer must no longer block a main thread, clear good state on a
transient outage, or hand-assemble snapshot options from physical topology.

### Gate

- Endpoint rotation does not change application identity or document handles.
- A partition produces degraded/stale state before unavailable state according
  to explicit policy.
- Reconnect does not duplicate subscriptions or operations.
- UDP denial exercises a supported relay/tunnel/fallback path or emits a precise
  unsupported-path result.
- The legacy request path cannot open a second competing session.

### Phase 2 Body Map

**Owner:** `CultMeshSessionManager` owns physical path selection, replacement,
and reconnect. A live document or collection binding owns only its logical
subscription intent and readiness promise.

**Inputs:** stable endpoint and protocol identity, discovery candidates,
connector results, session state transitions, typed schema and record filters,
and client disposal.

**Outputs:** one reusable logical session, subscription replay on each new
online physical incarnation, cache-backed typed handles, and readiness after
the first successful snapshot from any incarnation.

**Derived state:** physical client identity and generation, attempt count,
cached snapshots, and online/reconnecting/offline projections. The session
increments the generation only after replacing its physical channel. A binding
may coalesce duplicate subscription attempts within that generation, but a
pending request from an older generation cannot suppress replay. No individual
request owns the logical handle. Within one healthy generation, an unanswered
subscribe request expires on the client policy deadline and the binding
replays the same idempotent subscription intent; the caller does not own this
retry loop.

**Forbidden writers:** bindings and schema clients cannot choose endpoints,
replace channels, reconnect, or declare session state. An abandoned initial
subscribe request cannot block a replacement request from satisfying logical
readiness.

**Shared paths:** initial open and reconnect both use session-manager path
resolution. Document and collection activation both replay the same binding
intent once per physical generation when the logical session returns online.

**Cut line:** the initial subscribe no longer runs before reconnect observation,
and a per-binding semaphore no longer lets an unanswered physical request hold
all later replay attempts. Bindings become client-owned resources before
waiting for readiness, so disposal terminates pending opens.

**Verification layer:** Mesh tests lose the first document and collection
snapshot, fail the physical client, and prove the replacement snapshot opens
the same logical handle exactly once. A disposal test proves a pending open
cannot survive its owning client. Another test drops a subscription response
without dropping the physical path and proves the same binding becomes ready
after bounded replay. The Aetheria/EveUnity witness proves provider
discovery, live SoA state, commands, receipts, and assets through the migrated
session path.

### Phase 2 TypeScript provider-lifecycle slice

**Owner:** `CultMeshProviderSession` owns one TypeScript provider's discovery
registration, lease renewal, physical reconnect, desired typed publication
replay, command dispatch, receipt publication, and explicit withdrawal. It is
keyed by provider, service-instance, endpoint, and Verse identity; those
identities are never collapsed.

**Inputs:** an injected `CultMeshProviderTransport`, stable provider identity,
lease/reconnect policy, desired typed publications, domain command handlers, a
required durable receipt store, and an injected scheduler.

**Outputs:** watchable connecting/active/reconnecting/withdrawing/stopped
state, current lease evidence, ordered document publication, a durable typed
receipt outbox, and withdrawal of the lease plus remaining publications.

**Derived state:** reconnect attempt, backoff, current physical connection, and
the in-flight duplicate-command set. Pending receipt delivery is derived from
the durable receipt store, not volatile session memory. Provider state, Eve surface semantics,
command effects, endpoint discovery evidence, and body-producer identity are
not derived by the session.

**Forbidden writers:** renderers, HTML exporters, and scheduled projection
scripts may not register providers, renew leases, reconnect, publish receipts,
or withdraw provider state. A transport connection cannot mutate provider
domain state or treat successful delivery as authorization.

**Shared paths:** initial connect and reconnect both register, subscribe, and
replay the same desired publication set. Live publication updates use one
serialized lane, so an older update cannot arrive after a newer one. Duplicate
command deliveries share one in-flight transaction and later deliveries reuse
the durable receipt. A receipt is stored before publication and marked only
after acceptance; reconnect drains pending receipts before command intake.

**Cut line:** TypeScript one-shot RUDP publication helpers remain low-level
compatibility surfaces, not provider lifecycle owners. VoidBot's swarm renderer
is the first identified obsolete owner; its provider catalog mutation,
publication, reconnect, and receipt paths must be deleted when it adopts this
session. That consumer migration is still open and must not run both owners.

**Verification layer:** deterministic TypeScript tests prove initial
registration, renewal, stable publication order, reconnect and replay,
duplicate receipt reuse, explicit withdrawal, and the negative stop-during-
registration timeline, durable receipt replay, observer isolation, conflicting
command IDs, and exception-safe shutdown. The empty-consumer tarball smoke proves the session is
present in the installable `cultmesh-ts` package. A live Odin transport adapter
and the VoidBot migration remain required for the production proof.

## Phase 3: Authority as a Mandatory Boundary

### Cut first

- Remove helpers that select an advertised peer for authoritative work without
  an authority decision.
- Prevent transport success or catalog presence from being treated as trust.

### Build

- Resolve authority through one injected resolver before opening privileged
  document, operation, shard, or publisher routes.
- Verify signatures, scope, epoch, validity interval, and revocation policy.
- Publish authority diagnostics without leaking secret material.
- Make authorization results short-lived derived state, never durable truth.

### Gate

- Expired, revoked, wrong-Verse, wrong-role, and wrong-shard leases are rejected.
- A newer peer card cannot revive an expired lease.
- Cache replay and clock movement cannot grant authority.
- Manual and programmatic operations use the same authority primitive.

## Phase 4: Verified Content Transfer

### Phase 4 first-slice body map

**Owner:** `CultMeshContentTransferService` owns verified partial state and the
only promotion path into the completed content cache. Work is serialized and
shared by normalized content hash; provider identity is never part of content
identity or the destination name.

**Inputs:** a validated CDN manifest, ordered `ICultMeshContentProvider`
observations, a typed CultCache checkpoint store, a canonical cache directory,
and caller cancellation.

**Outputs:** a complete read-only-by-convention body path named by SHA-256, or
a failure with no completed body. `gamecult.mesh.content_transfer_state.v1`
records verified chunk indexes keyed by content hash.

**Derived state:** missing chunks, provider failover order, deterministic
partial-file path, manifest fingerprint, and completed cache path. A provider
success is evidence only for the returned chunk; it does not confer publisher
authority.

**Forbidden writers:** callers, renderers, providers, manifests, and the legacy
async CDN reader cannot publish a final body or mark a range verified. CultCache
checkpoint claims are rehashed against the partial body before reuse.

**Shared paths:** direct transfers and the legacy distributed-database reader
both pass through the same chunk verification, full-hash verification, and
atomic promotion primitive. Concurrent requests for one hash share the same
per-content transfer lock and verified work.

**Cut line:** the legacy distributed reader no longer owns a sequential chunk
loop or whole-artifact allocation. The transfer owner now opens a bounded
per-content request window while preserving one ordered writer/checkpointer.
Adaptive scheduling, quotas, pinning, signer policy, arbitrary streaming
destinations, and cleanup scheduling remain later Phase 4 work.

**State transition:** each verified chunk flushes a typed checkpoint. Restart
rehydrates that record, rehashes every claimed range, and fetches corrupt or
unclaimed chunks again. Completion verifies the entire partial file, atomically
promotes it, and removes the checkpoint. Cancellation leaves only re-verifiable
partial state.

**Verification layer:** focused tests cover durable cancellation/restart,
provider corruption and failover, forged checkpoint rejection, final-hash
failure, atomic visibility, concurrent request coalescing, bounded concurrent
fetch, ordered checkpointing under out-of-order completion, and observation of
the whole request window after an early failure. Existing CDN tests cover the
compatibility reader.

### Phase 4 managed content-session slice

**Owner:** `CultMeshContentTransferService` still owns verification, resumable
state, provider failover, and atomic promotion. `CultMeshSessionContentProvider`
owns only one bounded request/response projection over a reusable
`cultmesh.content.v1` session; `CultMeshContentServer` owns only serving
canonical content-addressed chunks from provider storage.

**Inputs:** stable provider endpoint identity, the shared session manager, a
validated manifest chunk reference, provider CultCache content, response
deadline, and caller cancellation.

**Outputs:** one manifest-bound chunk response to the transfer service. The
session manager retains connection/reconnect/path authority, and the transfer
service decides whether the response becomes verified partial state.

**Derived state:** request correlation identities and content-session physical
channels. Neither is content identity, authority evidence, or cache truth.

**Forbidden writers:** snapshot servers cannot embed artifact payloads in raw
document responses; the session provider cannot write partial/final files,
mark ranges verified, choose a replacement endpoint, or authorize a producer.

**Shared paths:** every cold network chunk enters the same incremental and
final verification path used by database and test providers. Concurrent chunks
borrow the same logical content session.

**Cut line:** bulk CDN bytes no longer require
`CultNetSnapshotResponseRawMessage` records. Snapshots retain manifests and
descriptors; the managed content protocol carries bounded payload responses.

**Verification layer:** Mesh tests transfer a multi-chunk body through one
managed session, assert zero snapshot requests, assert warm committed reuse
without another network request, and round-trip both wire messages through the
schema dispatcher. RUDP schema hosts and managed clients distinguish transport
progress from dispatched messages and drain bounded available work without
idle sleeps. Wire sends pace one 32-packet acknowledgement window at a time
instead of paying a Windows scheduler quantum for every fragment; a real UDP
content-session test transfers a four-chunk, one-megabyte request window through
the public transfer owner within its ten-second regression bound.
The released EveUnity/Aetheria cold run now promotes the 13,006,384-byte bundle
to its exact SHA-256 `.body` under the unchanged 300-second deadline with no
partial left behind. The combined cold gameplay witness remains open because
the authoritative witness world can lose its targeting candidates while the
cold body transfers; the same released client passes gameplay and camera
contracts on the promoted warm body. Content-session delivery is proven, while
the product readiness/lifecycle proof remains separate and incomplete.

### Phase 5 verified CDN mapped-body slice

**Owner:** `CultMeshContentTransferService` remains the sole writer and promoter
of completed CDN bodies. `CultMeshVerifiedBodyMappingBroker` owns only ephemeral,
opaque capability-to-file grants for those completed bodies. Body negotiation
and producer authorization remain owned by `CultMeshBodyTransportService`.

**Inputs:** the atomically committed `<sha256>.body` path returned by transfer,
the equivalent network body descriptor, and a bounded lease.

**Outputs:** a read-only file-mapping descriptor for the same logical generation.
Multiple consumers map the same completed cache file and therefore share the OS
page cache; no process-sized byte array or republished body file is created.
`CultMeshMappedContent` binds that descriptor to the exact final path returned
by the transfer owner, so runtime lowerers never reconstruct cache layout.

**Derived state:** opaque capability tokens and their expiry. Tokens are neither
content hashes nor paths and do not confer producer authority or content trust.

**Forbidden writers:** the mapping broker and adapter cannot create, promote,
rename, repair, or map partial/checkpoint files. `CultMeshMappedBodyPublisher`
is not used for verified CDN content.

**Shared paths:** local and network descriptors preserve body identity,
schema/layout, capacity, producer epoch, sequence, and byte size. Local open
failure is observed by body negotiation and falls back to that network
representation.

**Cut line:** there is no second CDN publication path. Mapping is granted only
after the transfer owner returns its verified final file. The compatibility
descriptor-only method delegates to the path-and-descriptor result rather than
repeating transfer or cache-path policy.

### Cut first

- Delete the first consumer-owned CDN chunk loop and partial-file convention.
- Stop requiring whole-artifact allocation before verification or delivery.

### Build

- Preserve content identity independently of provider and location.
- Add durable partial-transfer state keyed by content hash and verified range.
- Fetch missing ranges/chunks with bounded adaptive concurrency.
- Select and fail over between eligible providers without changing artifact
  identity.
- Verify incrementally and commit completed artifacts atomically.
- Support caller-provided streaming destinations and backpressure.
- Add manifest signer policy separately from content hash integrity.
- Define cache quota, pinning, eviction, and partial-transfer cleanup.

### Gate

- Restart resumes verified work without rereading corrupt or incomplete ranges
  as trusted.
- Provider failure moves remaining work to another provider.
- Corruption identifies the failing range/provider and retries within policy.
- Concurrent requests for one artifact share verified work.
- Cancellation leaves a valid resumable checkpoint or removes unusable partial
  state.
- The old renderer-owned downloader can no longer write the final cache entry.

## Phase 5: Negotiated Zero-Copy Data Plane

CultNet/CultMesh remains the control plane for identity, discovery, schemas,
capabilities, leases, epochs, liveness, receipts, and body-transport
negotiation. The existing network representation remains the remote path and
the fallback whenever local negotiation, handle transfer, schema validation,
or lease validation fails.

### Cut first

- Stop embedding process-local mapping names, process IDs, virtual addresses,
  or native handles in logical body identity.
- Stop local body adapters from interpreting transport access as producer
  authority or content validity.
- Do not create a general shared-memory message bus for reactive documents,
  commands, advertisements, or receipts.

### Body contract

Define one typed shared-body descriptor containing:

- logical body identity
- schema identity and layout version
- byte size and logical capacity
- producer epoch and body sequence
- access mode
- synchronization method
- lease expiry
- transport kind
- an opaque OS-handle capability token when negotiated locally

The capability token is scoped, expiring transport material. It is never
serialized as durable identity. The same descriptor semantics must bind a
local shared-memory body and its remote network representation.

### Build order

1. Define the transport-neutral typed body descriptor, lease, adapter ports,
   and validation rules.
2. Negotiate shared-memory capability and exchange OS handles through an
   authenticated platform adapter; retain network fallback.
3. Map immutable CultMesh CDN artifacts as content-addressed, file-backed,
   read-only bodies keyed by verified hash so processes share OS page cache.
4. Map fixed-capacity, schema-versioned SoA frame regions using triple
   buffering or an epoch/sequence protocol. Consumers receive read-only views,
   detect stale epochs, and never receive raw pointers.
5. Add local/remote equivalence, fallback, reconnect, crash, corruption, and
   conformance tests.
6. Profile copy volume, latency, memory pressure, page-cache reuse, frame
   drops, and lease contention under real EveUnity/Aetheria workloads.
7. Decide from measurements whether ordinary local control traffic merits a
   separate shared-memory lane. Do not build that lane as part of this phase.

### Frame-region authority

**Owner:** `CultMeshFrameRegion` is the sole owner of one fixed-capacity,
schema/layout-versioned, producer-epoch-bound triple buffer. It owns slot
storage, write reservations, reader protection, publication metadata, latest
generation selection, sequence advancement, and contention statistics.

**Inputs:** logical body identity, schema identity, layout version, logical
capacity, producer epoch, slot byte length, frame bytes, and commit metadata.
The bounded producer contract permits at most one outstanding write reservation
for the region, so sequence assignment and slot reservation cannot diverge.

**Outputs:** bounded write leases, immutable generation descriptors, read-only
consumer leases, and owner-derived statistics. Publication installs bytes and
metadata as one locked commit and advances sequence exactly once.

**Derived state:** `CultMeshSharedMemoryFrameRing` handles and statistics are
compatibility projections of region generations. Capability tokens remain
transport material and do not participate in frame identity or generation
validation.

**Forbidden writers:** the compatibility ring has no slot buffers, cursors,
reader counts, latest-slot state, frame-handle catalog, sequence counter, or
independent commit path.

**Shared paths:** direct SoA writes, compatibility zero-copy writes, and
compatibility copy publication all reserve and commit through
`CultMeshFrameRegion`. Direct and compatibility reads acquire the same owner
lease and release the same reader count.

**Cut line:** the former ring-owned arrays and counters are removed. A frame
region always has exactly three slots; construction with another slot count is
rejected rather than creating a second buffering model.

### Required invariants

- Only an explicitly authorized producer may publish or advance a body.
- Consumers are read-only unless a distinct contract explicitly grants write
  authority.
- Producer or broker death invalidates leases without requiring cooperative
  cleanup from the dead process.
- A producer cannot overwrite a slot while any valid consumer lease protects
  it.
- Schema/layout, producer epoch, and sequence mismatches are rejected before
  bytes are interpreted.
- Reconnect creates a new validated lease and cannot reinterpret stale mapped
  memory from an earlier producer epoch.
- Content hash integrity remains distinct from publisher identity and
  authority.
- Negotiation failure is observable and falls back to the network body without
  changing logical body identity.

### Phase 5 local/remote conformance slice

**Owner:** `CultMeshBodyTransportService` owns representation selection for one
validated logical generation. Each transport adapter owns only acquisition and
pre-interpretation validation of its representation. Producer authorization is
an independent input to negotiation; a valid digest never grants authority.

**Inputs:** equivalent local and network descriptors, the consumer's expected
body identity/schema/layout/capacity/epoch/sequence, an authorized-producer
decision, registered transport adapters, and network bytes carrying the
descriptor-bound semantic digest.

**Outputs:** one read-only lease for the selected representation plus a typed
negotiation result that records the selected transport and any rejected local
attempt. Local and network leases expose the same logical descriptor fields and
semantic bytes.

**Derived state:** local preference, adapter availability, and fallback reason
are negotiation state only. Capability tokens and network fetch details remain
transport material and cannot alter logical body identity.

**Invariants:** both descriptors must agree on body identity, schema, layout,
byte size, capacity, producer epoch, sequence, synchronization, access mode,
and semantic digest before either representation is opened. Lease expiry is
representation lifetime, validated independently for each descriptor; it is
not logical generation identity because a local broker may issue a shorter
capability lease than the network representation.
Expected schema, layout, capacity, epoch, and sequence are validated before a
network fetch. Fetched network bytes are digest-verified before a lease is
created or any typed read is possible. Digest integrity does not authorize the
publisher.

**Forbidden writers:** adapters, fallback handling, capability material,
network payloads, and integrity checks cannot rewrite logical identity or grant
producer authority. A failed local adapter cannot repair, mutate, or reinterpret
the network descriptor.

**Shared paths:** successful local open and network fallback use the same
logical-generation equivalence check and consumer validation request. Direct
network consumption uses the same pre-interpretation descriptor and digest
validation as fallback consumption.

**Cut line:** replace exception-only fallback reporting with a typed negotiation
result while retaining the existing overload as a delegating compatibility
surface. Add only descriptor-bound semantic integrity; do not add a shared-memory
control bus, content-authority inference, reconnect policy, or profiling hooks.

**Verification layer:** conformance tests compare descriptor semantics and full
bytes across file mapping, shared memory where supported, direct network, and
network fallback. Negative tests prove stale schema/epoch/sequence are rejected
before fetch, corrupt network bytes never produce a lease, local failure is
reported, fallback preserves identity, and authorization is evaluated separately
from content integrity.

### Gate

- Two local consumers map one verified CDN artifact without duplicate process
  copies and observe identical content identity through the network fallback.
- An EveUnity consumer reads successive SoA generations without torn frames,
  raw pointers, or overwrite of a leased slot.
- Producer crash, lease expiry, schema upgrade, epoch rollover, and reconnect
  all reject stale mappings before interpretation.
- A forged handle token, unauthorized producer, writable consumer request, or
  valid hash from an unauthorized publisher cannot cross its respective
  authority boundary.
- Local and remote conformance packs expose the same typed body lifecycle and
  semantic payload.

## Phase 6: Distributed Stream Control

### Cut first

- Stop treating process-local descriptor presence or a latest frame handle as
  publisher liveness.
- Stop allowing body adapters to overwrite stream ownership.

### Build

- Add publisher leases and monotonically increasing epochs.
- Model streams as named tracks containing independently schedulable groups or
  objects rather than one immortal ordered byte stream.
- Separate reliable control state from expiry-sensitive media bodies.
- Express sequence gaps, end-of-stream, cancellation, and subscriber priority.
- Negotiate shared memory or GPU handles locally and network streams/datagrams
  remotely through the same control state.
- Define failover and clock-confidence policy explicitly.

### Gate

- Publisher death is detected without relying on missing frames alone.
- A stale publisher cannot overwrite a newer epoch.
- Congestion cancels obsolete media work without blocking current control
  state.
- Loss and reordering produce explicit gaps rather than silent frame history
  corruption.
- Local zero-copy and remote fallback paths expose the same stream identity and
  lifecycle.

## Phase 7: Consumer Convergence and API Reduction

Migrate consumers in increasing order of operational risk:

1. one diagnostic/tooling consumer
2. Aetheria runtime discovery
3. EveUnity provider discovery and live documents
4. EveUnity asset delivery
5. Mimir media routes
6. remaining Odin/Bifrost/VoidBot command and state paths

For each consumer:

- inventory local discovery, refresh, reconnect, chunk, and topology code
- delete the local owner before adopting the shared service
- preserve domain policy in the consumer
- verify the full failure timeline
- record the removed physical configuration knobs

After two production consumers use the new services, reduce the broad static
facade. Keep small discoverable entry points and move specialist controls onto
the service or session that owns them. Do not expose the internal workflow as
another forest of static helpers.

## Cross-Runtime Contract

C#, Rust, TypeScript, Kotlin, Unity, and browser runtimes share semantic state,
not necessarily identical implementation machinery. The portable contract must
cover:

- stable endpoint and protocol identity
- discovery candidate and provenance
- freshness and connection state
- typed failure reasons
- authority decision evidence
- content transfer progress and verified ranges
- shared-body identity, schema/layout, epoch/sequence, access, lease, and
  transport negotiation
- stream descriptor, publisher epoch, subscription, and gap state

These are typed CultNet/CultMesh documents. JSON is permitted only for schema
publication, inspection, or non-CultNet boundaries.

## Research Direction

The implementation should borrow boundaries before dependencies:

- Iroh: stable endpoint identity, concurrent address lookup, shared sessions,
  relay reachability floor, direct-path upgrade, and watchable path state.
- Iroh Blobs/Bao: content identity independent of provider, incremental range
  verification, and resumable transfer.
- IPFS delegated routing: provider lookup separated from retrieval, provenance,
  positive/negative freshness, and stale-on-error semantics.
- QUIC: independent cancelable streams, datagrams, and path migration.
- Media over QUIC: tracks, groups, object expiry, priority, explicit gaps, and
  fetch versus subscribe semantics.

Iroh or QUIC may become a transport connector after the session boundary and
failure harness exist. Adopting either before that point would replace a wire
body while preserving the split control plane.

## Completion Conditions

The migration is complete only when:

- applications connect using stable identity and protocol rather than physical
  endpoint choreography
- discovery retains and labels last-known-good state across transient failure
- one owner supervises connection reuse, reconnect, failover, and path state
- peer contact cannot confer authority
- CDN consumers request verified content instead of implementing chunk loops
- local CDN and SoA consumers negotiate zero-copy bodies without changing
  logical identity, authority, or remote behavior
- stream identity and liveness survive changes in body transport
- old discovery, session, transfer, and stream decision paths cannot override
  the new owners
- failure timelines and visible consumer state agree
- CultMesh's public surface is smaller and more obvious than before the
  migration
