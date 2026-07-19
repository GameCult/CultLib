# CultLib for Unity

This package is generated from the CultLib .NET projects. It carries the same
CultCache, CultNet, and CultMesh assemblies consumed through NuGet by normal
.NET applications, staged as Unity-compatible managed plugins. Windows x64
builds also receive the native MsQuic realtime connector and its Schannel
runtime; the Microsoft license is included under `Third Party Notices`.

Build it with `scripts/build-unity-package.ps1`. Do not edit generated plugin
assemblies in the package output.

Install version `1.0.38` through Unity Package Manager with:

```text
https://github.com/GameCult/CultLib.git?path=/unity/org.gamecult.cultlib#cultlib-unity-v1.0.38
```

CultMesh chooses mapped memory for reachable same-machine bodies and advertised
QUIC for remote realtime state. Applications register the packaged
`CultMeshNativeQuicRealtimeTransportConnector`; they do not load or configure
MsQuic directly. Schemas, commands, receipts, and immutable content remain on
their typed control/content planes.
