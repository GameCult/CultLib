# CultMesh Kotlin

Kotlin/JVM and Android client substrate for CultCache, CultNet, and CultMesh.

It provides typed MessagePack document codecs, a tiny WebSocket CultNet lane,
a single-peer CultNet RUDP socket transport, an in-memory CultCache, and the
first Eve dashboard/sensor document contracts.

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
