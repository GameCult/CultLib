# CultMesh Kotlin

Kotlin/JVM and Android client substrate for CultCache, CultNet, and CultMesh.

It provides typed MessagePack document codecs, a tiny WebSocket CultNet lane,
a single-peer CultNet RUDP socket transport, an in-memory CultCache, and the
first Eve dashboard/sensor document contracts.

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
