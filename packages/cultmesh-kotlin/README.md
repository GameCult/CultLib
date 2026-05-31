# CultMesh Kotlin

Kotlin/JVM and Android client substrate for CultCache, CultNet, and CultMesh.

It provides typed MessagePack document codecs, a tiny WebSocket CultNet lane,
an in-memory CultCache, and the first Eve dashboard/sensor document contracts.

`EveDashboardStateDocument` mirrors the CultUI-shaped dashboard surface contract:
`surface.root` is the retained UI tree, `surface.assets` carries cacheable media
references, and the flat `nodes` projection remains a compatibility and binding
surface for selection, commands, and fallback rendering.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\packages\cultmesh-kotlin\build.ps1
```

The build writes `artifacts/cultmesh-kotlin/cultmesh-kotlin.jar`.
