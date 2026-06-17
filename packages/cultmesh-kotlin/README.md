# CultMesh Kotlin

Kotlin/JVM and Android client substrate for CultCache, CultNet, and CultMesh.

It provides typed MessagePack document codecs, a channel-aware WebSocket
CultNet transport, a single-peer CultNet RUDP socket transport, stream catalog negotiation, an
in-memory CultCache, and the first Eve dashboard/sensor document contracts.

The branded entrypoint mirrors C#/TypeScript/Python:

```kotlin
val node = CultMesh.startNode()
val verses = CultMesh.createVerseCatalog()
val peers = CultMesh.createPeerCatalog()
val leases = CultMesh.createAuthorityLeaseCatalog()
val streams = CultMesh.createStreamCatalog()
val schemas = CultMesh.createSchemaCatalog()
val builtIns = CultMesh.createBuiltInSchemaCatalog(kinds = listOf("shared_contract"))
val shards = CultMesh.createShardCatalog()
```

## CultCache Happy Path

Kotlin keeps the cache small but typed: document definitions wrap codecs,
records are stored as encoded payload bytes, and callers work through typed
helpers:

```kotlin
val notes = stringDocument("kotlin.note", "kotlin.note.v1")
val cache = CultCache()

cache.put(notes, "note:1", "hello")
val note = cache.getRequired(notes, "note:1")

val handle = cache.document(notes, "note:2")
handle.put("second")

val allNotes = cache.getAll(notes)
cache.delete(notes, "note:1")
```

`CultMeshNode` exposes the same typed cache surface through `remember`,
`recall`, `require`, and `forget`. This is not the C# SoA store; it is the
Kotlin client/runtime cache shape needed for feature and ergonomic parity
without pretending Android wants Unity's hot-loop memory layout.

Raw snapshot sync uses the same schema-v0 MessagePack documents as the other
runtimes:

```kotlin
val notes = stringDocument("kotlin.note", "kotlin.note.v1")
val source = CultMeshNode()
val target = CultMeshNode()

source.cache.register(notes)
target.cache.register(notes)
source.remember(notes, "note:1", "hello")

val request = cultNetSnapshotRequest(
    messageId = "sync-notes",
    schemaIds = listOf(notes.schemaVersion),
)
val response = source.createRawSnapshotResponse(request)

target.applyRawSnapshotResponse(response)
val note = target.require(notes, "note:1")
```

`applyRawDocumentPut(...)`, `applyRawSnapshotResponse(...)`, and
`applyDocumentDelete(...)` require matching codecs to be registered first, so
raw bytes re-enter the typed cache through the same document definitions that
local Kotlin callers use.

## WebSocket Transport

For browser/mobile-friendly schema lanes, `CultNetWebSocketTransportConnection`
wraps the tiny WebSocket client behind the same transport-frame shape used by
the other runtimes:

```kotlin
val transport = CultMesh.startNode().connectTransport(URI("ws://127.0.0.1:3075/mesh"))

transport.sendSchema(cultNetSchemaCatalogRequest(messageId = "schemas").toBytes())
val frame = transport.receive()
check(frame?.channelId == "schema")

// Or stay at the schema-message level.
transport.sendSchemaMessage(cultNetSchemaCatalogRequest(messageId = "schemas"))
val message = transport.receiveSchemaMessage()
check(message?.schemaVersion == "cultnet.schema_catalog_response.v0")

// Or let the transport perform the standard request/response/apply cycle.
val schemas = CultMesh.createSchemaCatalog()
transport.syncSchemaCatalog(
    schemas,
    messageId = "schemas",
    includeSchemaJson = true,
    kinds = listOf("document_payload"),
)
```

The WebSocket adapter advertises a `websocket` transport profile with one
reliable ordered `schema` channel and exposes transfer stats. `sendSchema`,
`sendSchemaMessage`, `receiveSchema`, and `receiveSchemaMessage` keep the
schema lane at the same ergonomic level as RUDP. WebSocket and RUDP both
implement `CultNetSchemaMessageTransport`, so `fetchSchemaCatalog`,
`syncSchemaCatalog`, `fetchShardCatalog`, and `syncShardCatalog` are shared
schema-message transport helpers rather than duplicated adapter opinions. The
catalogs still own imported state; the transport only performs the standard
request/response hop. WebSocket is the stream adapter; RUDP remains the
portable realtime UDP path.

Schema catalogs are also first-class, so Kotlin peers can publish and consume
descriptor responses instead of hand-assembling maps:

```kotlin
val catalog = CultNetSchemaCatalog()
catalog.upsert(
    defineCultNetSchemaDescriptor(
        schemaId = "kotlin.note.v1",
        kind = "document_payload",
        documentType = "kotlin.note",
        title = "Kotlin Note",
        schemaJson = """{"type":"object","properties":{"body":{"type":"string"}}}""",
    ),
)

val response = catalog.createResponse(
    cultNetSchemaCatalogRequest(
        messageId = "schema-catalog",
        includeSchemaJson = true,
        kinds = listOf("document_payload"),
    ),
)
```

`CultNetSchemaCatalog.applyResponse(...)` imports
`cultnet.schema_catalog_response.v0` descriptors from other runtimes and keeps
their content hashes, wire contracts, schema ids, and optional schema JSON.
`CultMesh.createBuiltInSchemaCatalog(...)` returns the Kotlin-supported
CultNet/CultMesh descriptors on the same catalog surface, with optional
`schemaIds`, `kinds`, and inline-schema filters. It includes schema catalog,
shard catalog, shard log, Verse catalog, peer exchange, and the shared transport
profile contract. Descriptors for shared schema files keep the canonical schema
ids and content hashes; Kotlin-local descriptor bodies use schema-version ids
until shared schema files exist. Inline schema JSON is opt-in and only emitted
for descriptor bodies Kotlin owns.

Shard catalogs and shard-log responses use the same schema-v0 lane:

```kotlin
val shards = CultNetShardCatalog()
shards.upsert(
    CultNetShardDescriptor(
        shardId = "notes-a",
        ownerRuntimeId = "kotlin-owner",
        epoch = 7,
        isPrimary = true,
        schemaIds = listOf(notes.schemaVersion),
        keyPrefix = "note:",
        primaryEndpoints = listOf("rudp://127.0.0.1:5000"),
    ),
)

val shardResponse = shards.createResponse(
    cultNetShardCatalogRequest(
        messageId = "discover-shards",
        schemaIds = listOf(notes.schemaVersion),
        recordKeys = listOf("note:1"),
    ),
)

val logRequest = cultNetShardLogRequest(
    messageId = "pull-notes",
    shardId = "notes-a",
    shardEpoch = 7,
    afterSequence = 12,
)
```

`CultCache.applyShardLogResponse(...)` applies `added` and `updated` entries
through raw document put messages and `removed` entries through document delete
messages. `CultNetInMemoryShardReplicaCursorStore` keeps the last applied
sequence for lightweight Kotlin clients.

## RUDP Happy Path

Kotlin exposes factory helpers around the shared RUDP transport so callers can
write client/server code without hand-binding sockets or repeating channel
strings:

```kotlin
CultMesh.createRudpServer(
    runtimeId = "kotlin-server",
    connectionId = 0x10203040,
    tuning = CultNetRudpSocketTuning(maxFragmentBytes = 1024),
).use { server ->
    val peer = CultMeshPeerCard(
        peerId = "kotlin-server",
        verseId = "local",
        endpoints = listOf("rudp://127.0.0.1:${server.localPort}"),
        roles = listOf("schema"),
    )
    CultMesh.createRudpClientForPeer(
        runtimeId = "kotlin-client",
        connectionId = 0x10203040,
        peer = peer,
    ).use { client ->
        client.connect("join")
        check(pumpRudpPairUntilConnected(client, server))
        client.sendSchema("client-state")
        val payload = server.receiveSchema(timeoutMs = 1_000)

        val remoteSchemas = CultMesh.createSchemaCatalog()
        client.syncSchemaCatalog(
            remoteSchemas,
            messageId = "rudp-schemas",
            kinds = listOf("document_payload"),
        )
    }
}
```

When peer discovery and authority leases are already local, callers can ask the
branded entrypoint to select an authorized RUDP peer for a Verse role:

```kotlin
val peers = CultMesh.createPeerCatalog()
val leases = CultMesh.createAuthorityLeaseCatalog()

val client = CultMesh.connectRudpClientForAuthorizedPeer(
    runtimeId = "kotlin-client",
    connectionId = 0x10203040,
    peers = peers,
    leases = leases,
    verseId = "local",
    role = "schema",
    connectPayload = "join".toByteArray(),
)
```

The helper uses `CultMeshPeerCatalog.firstAuthorized(...)`, which delegates
trust to `CultMeshAuthorityLeaseCatalog.isAuthorized(...)`; peer endpoints
remain contact hints, not authority.

The connected helper delegates to the same `CultNetRudpSocketTransportConnection`
after authorized peer selection and handshake. The lower-level
`createRudpClient...` helpers remain available when a caller intentionally owns
the handshake or in-process polling loop. The sugar delegates to the same
transport and
`cultnet.transport.rudp.v0` packet codec used by the cross-runtime interop
harness. `sendSchema`, `sendLatest`, and `sendRealtime` select the shared
channel semantics; they do not create a Kotlin-only dialect. For two
same-process transports, use `pumpRudpPairUntilConnected(...)` or an explicit
server pump to drive both sides through the handshake. Because RUDP also implements
`CultNetSchemaMessageTransport`, it uses the same catalog helpers as WebSocket,
with timeout-aware receive loops: `fetchSchemaCatalog`,
`fetchSchemaDescriptors`, `syncSchemaCatalog`, `fetchShardCatalog`,
`fetchShardDescriptors`, and `syncShardCatalog`.

For lower-level code, `CultMesh.parseRudpEndpoint("rudp://127.0.0.1:4100")`
returns the host/port pair, and `CultMesh.createRudpClient(...)` accepts either
that parsed endpoint or the endpoint string directly.

Kotlin also exposes the shared reconnect-policy helper for peers that need a
portable backoff document without inventing a JVM-only delay dialect:

```kotlin
val policy = createReconnectPolicy(policyId = "rudp-default", maxAttempts = 8)
val nextDelayMs = computeReconnectDelayMs(policy, attempt = 3, jitterMs = 17)
```

The helper publishes `cultnet.reconnect_policy.v0`, matches the C#,
TypeScript, Rust, and Python defaults, and is advertised by Kotlin RUDP
transport profiles as `reconnectPolicy`. `CultNetRudpReconnectLoop` consumes
the same controller for caller-owned RUDP client factories: the receive loop
decides when a transport is closed, then the reconnect loop schedules the next
attempt and opens a fresh transport through the supplied factory.

`EveDashboardStateDocument` mirrors the CultUI-shaped dashboard surface contract:
`surface.root` is the retained UI tree, `surface.assets` carries cacheable media
references, and the flat `nodes` projection remains a compatibility and binding
surface for selection, commands, and fallback rendering.

## Discovery Catalogs

Kotlin can model and exchange the same CultMesh discovery documents as the
other runtimes:

```kotlin
val verses = CultMeshVerseCatalog()
verses.upsert(
    CultMeshVerseDescriptor(
        verseId = "public",
        displayName = "Public Verse",
        authorityModel = "federated",
        compatibility = CultMeshVerseCompatibility(
            transportVersion = "cultmesh.v0",
            rulesHash = "rules-a",
        ),
        discoveryEndpoints = listOf("rudp://127.0.0.1:4000"),
    ),
)

val response = verses.createResponse(
    cultMeshVerseCatalogRequest(
        messageId = "discover-verses",
        transportVersion = "cultmesh.v0",
    ),
)

val peers = CultMeshPeerCatalog()
peers.upsert(
    CultMeshPeerCard(
        peerId = "kotlin-peer",
        verseId = "public",
        endpoints = listOf("rudp://127.0.0.1:4100"),
        roles = listOf("read-replica", "schema"),
    ),
)

val peerResponse = peers.createResponse(
    cultMeshPeerExchangeRequest(
        messageId = "discover-peers",
        verseId = "public",
        roles = listOf("schema"),
    ),
)
```

`createResponse(...)` and `applyResponse(...)` operate on `CultNetMessage`
instances, so catalog discovery can ride the same MessagePack schema-v0 lane as
RUDP schema traffic.

Peer cards are contact hints, not trust. Kotlin now mirrors the C#/TS/Python
lease gate so local clients can keep discovery and authority separate:

```kotlin
val peer = CultMeshPeerCard(
    peerId = "kotlin-peer",
    verseId = "public",
    endpoints = listOf("rudp://127.0.0.1:4100"),
    roles = listOf("shard-primary"),
    shardIds = listOf("players"),
    authorityLeaseId = "lease:kotlin-peer",
)

val leases = CultMesh.createAuthorityLeaseCatalog()
val unsubscribeLeaseWatch = leases.watch { lease ->
    println("authority lease changed: ${lease.leaseId}")
}
leases.upsert(
    CultMeshAuthorityLease(
        leaseId = "lease:kotlin-peer",
        verseId = "public",
        peerId = "kotlin-peer",
        roles = listOf("shard-primary"),
        shardIds = listOf("players"),
        issuerRuntimeId = "odin",
        validFrom = Instant.parse("2026-06-15T00:00:00Z"),
        expiresAt = Instant.parse("2026-06-15T01:00:00Z"),
    ),
)

check(leases.isAuthorized(peer, "shard-primary", "players"))
unsubscribeLeaseWatch()
```

## Streaming Catalog

Kotlin also mirrors the CultMesh stream declaration and negotiation surface for
Android-adjacent media, sensor, tensor, and byte streams. The catalog owns
stream identity, clock metadata, body transport negotiation, and latest-frame
cursors; it does not own the frame bytes themselves.

```kotlin
val streams = CultMesh.createStreamCatalog()
val unsubscribeStreamWatch = streams.watch { stream ->
    println("stream declared: ${stream.streamId}")
}
val unsubscribeFrameWatch = streams.watchFrames { frame ->
    println("latest frame ${frame.sequence} for ${frame.streamId}")
}
streams.declare(
    CultMeshStreamDescriptor(
        streamId = "mimir:camera",
        verseId = "studio",
        ownerPeerId = "android-device",
        kind = CultMeshStreamKinds.Video,
        clock = CultMeshStreamClock("android-elapsed-realtime"),
        video = CultMeshVideoStreamFormat(
            width = 1920,
            height = 1080,
            pixelFormat = "rgba8",
            framesPerSecond = 60.0,
        ),
        preferredTransports = listOf(
            CultMeshStreamBodyTransports.AHardwareBuffer,
            CultMeshStreamBodyTransports.SharedMemory,
            CultMeshStreamBodyTransports.CultCachePage,
        ),
    ),
)

val lane = streams.negotiate(
    "mimir:camera",
    CultMeshStreamConsumerProfile(
        peerId = "fensalir",
        verseId = "studio",
        supportedTransports = listOf(CultMeshStreamBodyTransports.AHardwareBuffer),
        acceptedKinds = listOf(CultMeshStreamKinds.Video),
        maxInFlightFrames = 2,
    ),
)

unsubscribeStreamWatch()
unsubscribeFrameWatch()
```

`EveMediaObservationDocument` carries byte-backed device streams such as camera
luma frames and microphone PCM blocks. The document is observation transport,
not synchronization authority: the device owns capture and local timestamps,
while Mimir or another consumer owns alignment and interpretation.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\packages\cultmesh-kotlin\build.ps1
```

The build writes `artifacts/cultmesh-kotlin/cultmesh-kotlin.jar`.
It also runs the built-in RUDP packet fixture and localhost UDP socket
self-test.
