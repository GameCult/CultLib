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
