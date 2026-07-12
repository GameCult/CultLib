# CultMesh Reliability Migration Plan

## Objective

CultMesh should give an application one typed, watchable route to a Verse,
service, document, content object, or stream without making that application own
bootstrap discovery, physical topology, reconnect loops, refresh polling, or
transfer recovery.

The migration preserves CultCache as the typed persistence substrate and
CultNet as the transport substrate. It moves reliability policy out of callers
and the broad `CultMesh` convenience facade into five explicit organs:

1. discovery control plane
2. connection and session manager
3. authority resolver
4. content transfer scheduler
5. stream control plane

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

## Phase 5: Distributed Stream Control

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

## Phase 6: Consumer Convergence and API Reduction

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
- stream identity and liveness survive changes in body transport
- old discovery, session, transfer, and stream decision paths cannot override
  the new owners
- failure timelines and visible consumer state agree
- CultMesh's public surface is smaller and more obvious than before the
  migration
