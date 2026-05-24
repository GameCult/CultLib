# Worker Payload Transfer

CultMesh needs to move files and executable assemblies between compatible
runtimes so local worker pools can run trusted simulation payloads and publish
witness observations for distributed cache consensus.

This is two systems, not one:

- artifact transfer moves bytes
- worker admission decides whether those bytes may run

Do not collapse them. A runtime that has received bytes has not accepted
authority to execute them.

## Authority Map

Owner: CultMesh owns artifact and worker-payload policy for a Verse.

Inputs:

- local runtime id and runtime kind
- peer card, roles, shard ids, and authority lease id
- Verse compatibility: transport version, rules hash, required plugin ids, and
  compatible Verse ids
- artifact manifest: content hash, media type, size, chunking, signatures, and
  declared target runtimes
- worker admission policy: trusted signers, allowed runtimes, resource limits,
  allowed capabilities, and target shard/frame scope

Outputs:

- artifact manifest documents for discovery and compatibility checks
- artifact chunk request/response wire messages for byte transfer
- worker payload manifests for executable assemblies or bundles
- admission receipts that either reject the payload or grant a bounded worker
  lease
- worker result observations, not direct committed state

Derived state:

- downloaded bytes are cache-only until their hash matches the manifest
- installed payloads are local runtime state, not mesh truth
- worker availability is telemetry until a Verse policy turns it into a role
- worker results are witness observations until quorum and shard authority
  commit them as `CultMeshSimulationFact`

Forbidden writers:

- artifact chunk handlers must not install or execute payloads
- payload installers must not bypass admission policy
- workers must not write committed simulation facts directly
- non-primary shards must not accept worker results as committed state
- peer cards must not grant execution authority without lease/signature checks

Shared paths:

- files, assemblies, plugin packs, and rule bundles all use the same artifact
  manifest and chunk transfer path
- direct operator install and peer-to-peer transfer both validate the same
  manifest hash and signature material
- worker execution and remote execution requests both pass through the same
  admission primitive
- worker output always enters the mesh as simulation observations or explicit
  intent documents, then follows the normal quorum and shard commit path

Deletion line:

- do not add a generic "run remote code" message
- do not hide transfer bytes inside raw document puts
- do not let file transfer create its own authority lane parallel to shard
  authority

## Artifact Model

An artifact is content-addressed. The artifact id should be the canonical hash
of the full byte stream, for example `sha256:<hex>`.

Manifest fields:

- `artifactId`: stable content id
- `kind`: `file`, `dotnet-assembly`, `wasm-module`, `rules-bundle`, or
  `plugin-pack`
- `mediaType`: MIME-ish type such as `application/vnd.microsoft.portable-executable`
- `byteLength`: total size
- `chunkSize`: requested transfer chunk size
- `contentHash`: full stream hash
- `createdAt`: manifest timestamp
- `sourceRuntimeId`: runtime that offers the artifact
- `signatures`: signatures over the canonical manifest and content hash
- `targetRuntimes`: compatible runtime descriptors
- `requiredCapabilities`: capability names the payload may request

Chunk transfer fields:

- `messageId`
- `artifactId`
- `offset`
- `length`
- `chunkHash`
- `bytes`

Chunks are only storage facts. The receiver writes them to a staging store,
verifies the full hash, then reports availability. No installation happens in
the chunk handler.

## Assembly Payload Model

Assembly transfer is a specialization of artifact transfer.

Assembly manifests add:

- `assemblyName`
- `assemblyVersion`
- `targetFramework`
- `runtimeKind`: for example `dotnet`, `node`, `wasm`, or `native`
- `entrypoint`: a named worker entrypoint, not an arbitrary symbol hunt
- `abiVersion`: worker host ABI expected by the payload
- `determinismProfile`: `pure`, `bounded-io`, or `host-observed`
- `permissionClaims`: capabilities requested by the payload

The runtime must reject assembly payloads when:

- artifact hash verification fails
- signature policy fails
- Verse compatibility fails
- ABI version is unsupported
- requested permissions exceed local worker policy
- the entrypoint is missing or ambiguous

## Worker Admission

Admission is local and explicit. A remote peer can offer a payload; it cannot
force execution.

Admission inputs:

- verified artifact manifest
- local trust policy
- Verse id and rules hash
- shard ids, frames, and simulation task kind
- requested resource limits
- requested capabilities

Admission output:

- `accepted`
- `workerLeaseId`
- `payloadId`
- `runtimeId`
- `verseId`
- `shardIds`
- `validFrom`
- `expiresAt`
- `resourceLimits`
- `grantedCapabilities`
- `rejectionReason`

The lease owns permission to schedule local workers. It does not own shard
authority and does not grant permission to commit world state.

## Simulation Work Path

The worker path is:

1. Discover compatible peers through Verse catalog and peer exchange.
2. Fetch or receive an artifact manifest.
3. Transfer missing chunks into a staging store.
4. Verify content hash and signatures.
5. Run worker admission against local policy.
6. Schedule bounded local workers under the worker lease.
7. Emit `CultNetSimulationObservation` for results.
8. Let consensus candidates form from observations.
9. Commit quorum candidates through `CultMeshSimulationFactCommitter`.
10. Replicate committed facts through the normal shard log.

The important part is step 7. Workers produce witness reports. They do not
become a secret second database.

## Wire Families

The first wire families should be:

- `cultmesh.artifact_manifest.v0`: document payload for transfer discovery
- `cultmesh.artifact_chunk_request.v0`: request a byte range
- `cultmesh.artifact_chunk_response.v0`: return a verified chunk
- `cultmesh.worker_payload_manifest.v0`: document payload for executable bundles
- `cultmesh.worker_admission_request.v0`: ask the local policy to admit a
  verified payload
- `cultmesh.worker_admission_receipt.v0`: accepted lease or rejection

Only the chunk response carries raw bytes. Every other surface carries metadata,
compatibility, policy, or receipt state.

## Compatibility

Compatibility is conjunction, not vibes:

- transport version matches
- Verse rules hash matches or the destination Verse explicitly accepts the
  source Verse
- plugin requirements are satisfied
- payload ABI is supported
- runtime kind and target framework match
- required capabilities are granted locally
- trust policy accepts the signer

If any part is unknown, the payload remains transferable but not executable.

## First Implementation Slice

The first code slice should add runtime-neutral message and document contracts
with tests in C#, TypeScript, and Rust:

- artifact and worker manifest shapes
- chunk request/response shapes
- admission request/receipt shapes
- schema catalog entries
- round-trip serialization tests

The second slice should add a local staging store and hash verifier.

The third slice should add worker admission and local scheduling behind an
interface. Real execution should wait until transfer, verification, and policy
are boring.
