# GameCult.Mesh.Quic.Native

This Windows desktop bridge keeps MsQuic's callback-heavy C API below the
managed runtime boundary. It accepts a discovery-authorized SHA-256 certificate
pin, negotiates `cultmesh-state-v1`, and emits complete CultMesh realtime frame
payloads. It owns transport only; frame meaning remains in `GameCult.Mesh`.

The build consumes Microsoft's pinned
`Microsoft.Native.Quic.MsQuic.Schannel` package. It does not contain
Aetheria-specific code.
