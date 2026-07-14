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
var provider = mesh.ContentProvider(
    "aetheria.daemon",
    "service:aetheria.daemon");

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
`FetchMappedBodyAsync`. The resulting descriptor maps only the completed body;
mapping failure remains observable and falls back through the equivalent
network descriptor without changing logical identity.

The content protocol uses one bounded response per manifest chunk. A snapshot
response containing `gamecult.mesh.cdn_artifact_chunk.v1` payload records is a
legacy transport path and must not be used for bulk delivery.

The transfer owner may keep a bounded window of requests in flight per content
hash (four by default, never more than 32). Candidate chunks can arrive out of
order, but only the transfer owner writes them, and it flushes and checkpoints
them in manifest-offset order. If one request fails, the owner observes the
rest of that window before returning; fetched-but-uncommitted bytes never become
trusted resume state. Concurrent artifacts have independent windows.

RUDP schema hosts should drain immediately available transport work with
`PollAvailableAsync`. Its result distinguishes transport items consumed from
application messages dispatched, so ACK and connection traffic counts as
progress without impersonating a message or triggering an idle delay.
