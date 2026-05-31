# CultMesh Kotlin

Kotlin/JVM and Android client substrate for CultCache, CultNet, and CultMesh.

It provides typed MessagePack document codecs, a tiny WebSocket CultNet lane,
an in-memory CultCache, and the first Eve dashboard/sensor document contracts.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\packages\cultmesh-kotlin\build.ps1
```

The build writes `artifacts/cultmesh-kotlin/cultmesh-kotlin.jar`.
