# CultMesh content sessions

Large immutable artifacts use CultMesh content sessions. Typed snapshots carry
manifests, body descriptors, leases, and progress; they do not carry artifact
payloads.

The provider attaches one generic content server to its existing CultNet
schema host:

```csharp
using var content = new CultMeshContentServer(schemaServer, providerCache);
```

The consumer creates a provider over its existing identity-first session
manager, then gives that provider to the transfer owner:

```csharp
var target = new CultMeshSessionTarget(
    "aetheria.public",
    "service:aetheria.daemon");
var provider = mesh.ContentProvider(
    "aetheria.daemon",
    target);

var transfer = new CultMeshContentTransferService(
    transferStateCache,
    new[] { provider },
    new CultMeshContentTransferOptions(cacheDirectory));

var verifiedBodyPath = await transfer.FetchAsync(manifest, cancellationToken);
```

`CultMeshContentTransferService` remains the only owner of verified partial
state and final cache promotion. It validates each chunk, resumes durable work,
verifies the complete SHA-256 identity, and atomically publishes
`<content-hash>.body`. The session provider owns no cache truth and does not
implement a second retry, failover, or promotion policy.

For local consumers, configure `CultMeshVerifiedBodyMappingBroker` and call
`FetchMappedContentAsync`. The result binds the transfer owner's verified path
to the mapped descriptor, so consumers do not infer cache filenames. The older
`FetchMappedBodyAsync` shape delegates to the same owner when only the
descriptor is needed. Mapping failure remains observable and falls back through
the equivalent network descriptor without changing logical identity.

The content protocol uses one bounded response per manifest chunk. A snapshot
response containing `gamecult.mesh.cdn_artifact_chunk.v1` payload records is a
legacy transport path and must not be used for bulk delivery.

The transfer owner may keep a bounded window of requests in flight per content
hash (four by default, never more than 32). Candidate chunks can arrive out of
order, but only the transfer owner writes them, and it flushes and checkpoints
them in manifest-offset order. If one request fails, the owner observes the
rest of that window before returning; fetched-but-uncommitted bytes never become
trusted resume state. Concurrent artifacts have independent windows.

RUDP schema hosts drain immediately available transport work with
`PollAvailableAsync`, and the managed schema client pump applies the same
bounded-drain rule. Transport packets, including fragments, ACKs, and connection
traffic, count as progress without impersonating an application message or
paying one operating-system scheduler delay per packet. Reliable sends are
paced in one 32-packet acknowledgement window rather than one fragment at a
time.
