# CultMesh Kotlin

Kotlin/JVM and Android client substrate for CultCache, CultNet, and CultMesh.

It provides typed MessagePack document codecs, a tiny WebSocket CultNet lane,
a single-peer CultNet RUDP socket transport, an in-memory CultCache, and the
first Eve dashboard/sensor document contracts.

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

## RUDP Happy Path

Kotlin exposes factory helpers around the shared RUDP transport so callers can
write client/server code without hand-binding sockets or repeating channel
strings:

```kotlin
cultNetRudpServer(
    runtimeId = "kotlin-server",
    connectionId = 0x10203040,
    tuning = CultNetRudpSocketTuning(maxFragmentBytes = 1024),
).use { server ->
    cultNetRudpClient(
        runtimeId = "kotlin-client",
        connectionId = 0x10203040,
        remoteHost = "127.0.0.1",
        remotePort = server.localPort,
    ).use { client ->
        client.connect("join")
        check(pumpRudpPairUntilConnected(client, server))
        client.sendSchema("client-state")
        val payload = server.receiveSchema(timeoutMs = 1_000)
    }
}
```

The sugar delegates to the same `CultNetRudpSocketTransportConnection` and
`cultnet.transport.rudp.v0` packet codec used by the cross-runtime interop
harness. `sendSchema`, `sendLatest`, and `sendRealtime` select the shared
channel semantics; they do not create a Kotlin-only dialect. For a remote peer
that already has its own receive loop, use `connectAndWait(...)`; for two
same-process transports, use `pumpRudpPairUntilConnected(...)` to drive both
sides through the handshake.

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
