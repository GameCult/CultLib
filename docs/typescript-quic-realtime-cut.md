# TypeScript QUIC Realtime Plane Cut Map

Date: 2026-09-16

Status: Imagination output for the target in
`docs/typescript-quic-realtime-target.md` (operator-accepted 2026-09-16). No
code in this document has been written. Nothing here is committed by
Imagination; the root agent commits.

Anchor: repo `F:\Projects\CultLib`, branch `main`, HEAD
`f2cd2eb4641269c653977bdba9b4bce2a26d870c`. Every `file:line` below is read
against that commit. If HEAD moves, re-anchor before cutting.

How to read this document: section 1 prices the largest liability first, as
the target requires, because if that price had turned out to dominate the
campaign, the cut order below would have been wrong. Section 2 is the probe
ledger; every mechanism claim in this map carries `(probe)` when it was
established by running something, or `(source read)` when it was established
by reading a file at a named line. Nothing is claimed from a package name or a
README. Section 3 is the consumer audit. Section 4 names rejected options and
why. Section 5 is the authority map for every ownership change. Sections 6-12
are the cuts. Section 13 holds the operator questions; section 14 the
subtraction estimate; section 15 what could not be assigned.

Two corrections to the target, recorded here rather than silently absorbed:

- The target cites `src/GameCult.Mesh.Quic/CultMeshRealtimeTransports.cs`.
  That file is `src/GameCult.Mesh.Quic/CultMeshQuicRealtimeTransport.cs`
  (674 lines). `CultMeshRealtimeTransports.cs` exists, but in
  `src/GameCult.Mesh/`, and holds the transport-neutral contracts
  (`CultMeshRealtimeDelivery` at :8-13, `CultMeshRealtimeFrame` at :19-44,
  `ICultMeshRealtimeTransport` at :46-52,
  `ICultMeshRealtimeTransportConnector` at :55-63) `(source read)`.
- The target says the binding is "shipped through a registry". Today only
  `@gamecult/cultcache-ts` has a registry publish job
  (`.github/workflows/publish-packages.yml:20-24`, `:40-76`); `cultnet-ts`,
  `cultmesh-ts` and `cultmesh-browser` have none, and every external consumer
  reaches them by `file:` path (section 3). Registry delivery of the binding
  therefore also means registry delivery of `cultnet-ts` and `cultmesh-ts`,
  which is a naming decision the operator has not yet made. See Q5.

## 1. The largest liability, priced first

The target's biggest new surface is CultLib owning native MsQuic bindings for
Node on two platforms. The price depends on three things: what Node itself
offers, what the repo already owns, and how a native artifact reaches a
consumer host without a toolchain. Each was probed.

### 1.1 What Node offers

- Node `v24.15.0` on Starfire has no `node:quic`, with or without
  `--experimental-quic` (`ERR_UNKNOWN_BUILTIN_MODULE`), and its internal
  binding table has no `quic` entry (`No such binding: quic` under
  `--expose-internals`). `process.versions` reports `openssl 3.5.5`, no
  `ngtcp2`, no `nghttp3`, `napi 10` `(probe)`. Yggdrasil runs Node `24.14.1`
  from NodeSource (`F:\Projects\gamecult-ops\inventory.md:660`) `(source read)`,
  so the same absence holds on the deploy host.
- Conclusion: there is no first-party QUIC in this Node line. Anything that
  works is native.

### 1.2 What the repo already owns

- `native/GameCult.Mesh.Quic.Native/cultmesh_quic_native.cpp` (440 lines) is a
  CultLib-owned C ABI over MsQuic: `cultmesh_quic_open/state/poll/error/close`
  (`:309-440`). It negotiates ALPN `cultmesh-state-v1` (`:32`, `:341-344`),
  uses deferred certificate validation and answers the pin decision through
  `ConnectionCertificateValidationComplete` (`:239-263`), reassembles the
  1-byte-kind plus 4-byte-LE-length QUIC stream framing (`:123-157`), and
  queues whole encoded frames for a polling host (`:400-418`). It is Windows
  desktop only by construction: `<windows.h>`, `<wincrypt.h>`, `<bcrypt.h>`
  (`:1-3`), `CryptHashCertificate2` for the pin (`:184-200`),
  `__declspec` exports (`:18-22`), and the CMake build refuses non-Windows
  (`CMakeLists.txt:4-6`) `(source read)`.
- The bridge is consumed by the Unity connector through `DllImport` of library
  name `gamecult_mesh_quic_native`
  (`src/GameCult.Mesh.Quic.Native/CultMeshNativeQuicRealtimeTransport.cs:259-277`),
  which polls every 2 ms by default (`:14`, `:167-203`) `(source read)`. The
  built DLL and `msquic.dll` 2.5.9 are committed at
  `unity/org.gamecult.cultlib/Runtime/Plugins/x86_64/` (last rebuilt in
  `a0813c6`, 2026-09-14) `(probe: git log)`.
- The Windows build is scripted and pinned: `scripts/build-quic-native.ps1`
  downloads `Microsoft.Native.Quic.MsQuic.Schannel` `2.5.9` by URL, checks its
  SHA-256 (`:13-14`, `:27-41`), and builds with CMake `-A x64` (`:48-52`).
  The last build on this machine used Visual Studio 17 2022 Build Tools, MSVC
  14.44 (`artifacts/quic-native-build/x64/Release/CMakeCache.txt`) `(probe)`.
  `cl.exe` is not on PATH but Build Tools are installed, so the existing
  script works here as-is.
- `tests/GameCult.Mesh.Quic.Native.Tests/GameCult.Mesh.Quic.Native.Tests.csproj:20-29`
  builds that bridge before the test build and copies it beside the test
  output `(source read)`. `scripts/build-unity-package.ps1:80-82`, `:143-147`
  builds it again for the Unity package `(source read)`.

Conclusion: CultLib already owns the MsQuic body for one platform, one role
(client), and one host (Unity). The cost of the target is extending that body
to a second role (provider), a second platform (Linux x64), and a second host
(Node), not creating a body from nothing.

### 1.3 How Node loads it

The decisive probe: from Node 24.15.0, with `koffi@3.3.0` installed into the
scratchpad, `koffi.load` loaded the committed `msquic.dll` then
`gamecult_mesh_quic_native.dll`, bound the five v1 exports by C prototype,
called `cultmesh_quic_open("127.0.0.1", 9, <64 zero hex>)` which returned `0`
with a non-null handle, observed `cultmesh_quic_state` move from `Connecting`
to `Failed` after 34 ms, read the bridge's structured error string through
`cultmesh_quic_error` ("shut down by the transport (status=0xFFFFFFFF800704D0,
error=0x1, msquic=F:\...\msquic.dll, events=event=1)"), got `-2` from
`cultmesh_quic_poll` on a failed client, and closed cleanly `(probe:
scratchpad/koffi-probe/probe.js)`. MsQuic ran its worker threads inside the
Node process and the poll model crossed the boundary without any callback
into JS.

What that proves and what it does not:

- Proves: the existing CultLib C ABI is directly loadable and drivable from
  the Node line in question with no addon build, no Node headers, no MSVC at
  install time, and no per-Node-version artifact. The `.async` call form
  exists on bound functions (`typeof state.async === "function"`), which is
  the mechanism for a blocking wait off the JS thread `(probe)`.
- Does not prove: a QUIC handshake succeeded (port 9 was intentionally dead);
  that the provider role works (the bridge has none); that Linux works (the
  bridge is Windows-only). Those are cut deliverables, not probe results.

koffi facts used by the design, from its shipped documentation, not from
memory: JS callbacks always run on the main thread and calls from other
threads are queued to it, with an explicit deadlock warning if the main thread
blocks on a secondary thread (`node_modules/koffi/doc/callbacks.md:166-176`);
asynchronous calls run on a worker pool sized to the core count with a default
cap of 256 queued calls (`doc/misc.md:26`, `:37`); prebuilt binaries ship for
Windows x64 and Linux/glibc x64 among others, Node >= 16, no compiler needed
(`doc/index.md:17-38`) `(source read)`. Package weight: 1,713,562 bytes
unpacked, 87 files, MIT `(probe: npm view)`. The design below uses no JS
callbacks at all; the only crossing is one blocking `next_event` wait per
runtime issued through `.async`, so the deadlock class in `callbacks.md:176`
cannot occur.

### 1.4 How it reaches Linux

- `libmsquic` is published by Microsoft for Debian 13 (trixie) at
  `packages.microsoft.com/debian/13/prod`, versions including `2.5.9`
  (`pool/main/libm/libmsquic/libmsquic_2.5.9_amd64.deb`, 2,906,420 bytes,
  SHA-256 `1baa61ade0b7b4a99f6dcb6b00d9aedb12b5566d00918a325be7425e878e51ba`,
  `Depends: libssl3t64, libnuma1, libxdp1, libnl-route-3-200`) `(probe)`.
  Yggdrasil is Debian 13 (`gamecult-ops/inventory.md:356`) `(source read)`.
  The same `2.5.9` pinned for Windows exists for the deploy host.
- The NuGet packages carry Windows binaries only: `Microsoft.Native.Quic.MsQuic.OpenSSL`
  2.5.9 contains `bin/{x64,x86,arm64}/msquic.dll`, `lib/*/msquic.lib`,
  and headers `msquic.h`, `msquicp.h`, `msquic_winuser.h`; no `.so`, no
  `msquic_posix.h` `(probe: nupkg listing)`. The Linux build needs
  `msquic.h`, `msquic_posix.h`, `quic_sal_stub.h` from the msquic source
  tree at tag `v2.5.9`; all three are fetchable at
  `raw.githubusercontent.com/microsoft/msquic/v2.5.9/src/inc/` (HTTP 200,
  79,280 / 17,909 / 4,731 bytes) `(probe)`. GitHub releases `v2.5.9` carry only
  `*_test.zip` bundles; `v2.5.11`, `v2.6.1` carry no assets `(probe)`, so the
  deb is the only distribution-grade Linux runtime for this version.
- Build host for Linux: Docker Desktop on Starfire runs a `linux/amd64` engine
  `(probe: docker version)`, so a `debian:13` container is a Linux build host
  for the development loop. The release artifact must come from a Linux host
  the deploy path trusts: Yggdrasil itself, or a CI runner. Q6.
- Windows toolchain: MSVC Build Tools 2022 present; CMake 4.3.2; also a MinGW
  gcc from StrawberryPerl on PATH `(probe)`. The map keeps MSVC for Windows
  because the committed Unity DLL is built with MSVC `/Brepro` for
  reproducibility (`CMakeLists.txt:17-19`) `(source read)`; mixing toolchains
  for one DLL is a liability with no capability behind it.

### 1.5 Price verdict

The binding does not dominate the campaign. The C body exists; the Node load
path is proven with one 1.7 MB MIT dependency and zero addon tooling; the
Linux runtime is a pinned distro package on the exact target OS; the Windows
build is already scripted. The real cost is the C++ rewrite from a
client-only, Windows-only, per-client poll bridge to a runtime-neutral,
two-role event bridge (section 8), estimated at roughly 1,000 lines of C++
replacing 440, plus a Linux build script and CI job. That is one cut, not the
campaign.

What would have changed this verdict: if the bridge had needed JS callbacks
from MsQuic worker threads (it does not; the wait/event model avoids them), or
if `libmsquic` had not been available for Debian 13 at the pinned version (it
is), or if the FFI load had failed on Node 24 (it did not).

## 2. Probe ledger

| # | Claim | Method | Evidence |
| --- | --- | --- | --- |
| P1 | Node 24.15.0 has no `node:quic` and no internal quic binding | probe | `require('node:quic')` -> `ERR_UNKNOWN_BUILTIN_MODULE` with and without `--experimental-quic`; `--expose-internals` -> `No such binding: quic`; `process.versions.ngtcp2 === undefined` |
| P2 | The committed CultLib MsQuic bridge loads and runs from Node 24 via koffi | probe | `scratchpad/koffi-probe/probe.js`: open rc 0, state 2 after 34 ms, structured error string, poll -2, clean close |
| P3 | koffi ships Windows x64 and Linux glibc x64 prebuilds, needs no compiler, Node >= 16 | source read | `koffi/doc/index.md:17-38` |
| P4 | koffi async calls run on a worker pool; JS callbacks only on the main thread | source read | `koffi/doc/misc.md:26,37`; `koffi/doc/callbacks.md:166-176` |
| P5 | koffi is 1.7 MB unpacked, MIT | probe | `npm view koffi dist.unpackedSize dist.fileCount`; `package.json:27` |
| P6 | MSVC Build Tools 2022 (14.44) and CMake 4.3.2 are present; the last bridge build used them | probe | `artifacts/quic-native-build/x64/Release/CMakeCache.txt`; `cmake --version` |
| P7 | `libmsquic 2.5.9` exists for Debian 13 amd64 with its SHA-256 and dependency set | probe | `packages.microsoft.com/debian/13/prod/dists/trixie/main/binary-amd64/Packages` |
| P8 | Yggdrasil is Debian 13 with Node 24.14.1 from NodeSource | source read | `gamecult-ops/inventory.md:356`, `:660` |
| P9 | MsQuic NuGet OpenSSL 2.5.9 carries no Linux binary or posix header | probe | nupkg listing |
| P10 | MsQuic POSIX headers are fetchable at tag v2.5.9 | probe | HTTP 200 for `msquic.h`, `msquic_posix.h`, `quic_sal_stub.h`, `msquic_winuser.h` |
| P11 | The pinned `msquic.h` has `QUIC_CREDENTIAL_TYPE_CERTIFICATE_PKCS12`, deferred/indicated certificate flags, `QUIC_SEND_FLAG_FIN`, `QUIC_STREAM_EVENT_SEND_COMPLETE`, API version 2 | source read | `artifacts/dependencies/msquic-schannel-2.5.9/package/build/native/include/msquic.h:123,132-133,142,246,394-398,1290,1530,1850` |
| P12 | Docker Desktop on Starfire runs a linux/amd64 engine | probe | `docker version --format '{{.Server.Os}}/{{.Server.Arch}}'` -> `linux/amd64` |
| P13 | `openssl` 3.5.5 CLI is available on Starfire (mingw) | probe | `which openssl` |
| P14 | npm names `cultmesh-ts`, `cultnet-ts`, `cultmesh-browser`, `@gamecult/cultmesh-ts`, `@gamecult/cultnet-ts`, `@gamecult/cultmesh-quic-native` are all unclaimed | probe | `npm view <name> version` returned nothing for each |
| P15 | Only `@gamecult/cultcache-ts` and the three Python packages have publish jobs | source read | `.github/workflows/publish-packages.yml:20-24,40-76` |
| P16 | Every external TS consumer uses `file:` paths; none uses a registry version | probe (Explore agent over `F:\Projects`) | section 3 |
| P17 | The StreamPixels deploy path extracts a CultLib tarball beside the app and runs `pnpm install --frozen-lockfile` and `pnpm build` on Yggdrasil | source read | `gamecult-ops/scripts/deploy-streampixels-preview.sh:21,95-103` |
| P18 | The Unity native connector refuses any endpoint without a 64-hex `cert-sha256` pin | source read | `CultMeshNativeQuicRealtimeTransport.cs:103-105` |
| P19 | C# `IsProtected` accepts `wss`, `https`, or any scheme containing `quic`; the browser verifier accepts only `wss` | source read | `CultMeshAuthorityProof.cs:156-162`; `cultmesh-browser/src/index.ts:965` |
| P20 | C# and browser canonical route transcripts are field-for-field identical | source read | `CultMeshAuthorityProof.cs:275-292` vs `cultmesh-browser/src/index.ts:979-996` |

## 3. Consumer audit

Method: every `package.json` under `F:\Projects` (excluding `node_modules`,
`dist`, `dist-test`, `.git`, `bin`, `obj`, `Library`, `Temp`, and CultLib
itself and its worktree copies) checked for the four package names, plus a
grep of every `*.ts/*.tsx/*.mts/*.cts/*.js/*.mjs/*.cjs` import or require of
them, with symbols read from the import lines. Vendored copies inside
`Heimdall/vendor/CultLib`, `VoidBot/vendor/cultcache-ts`, and
`gamecult-ops/.artifacts/*` were classified as library-internal, not
consumers.

Result:

| Package | Repos | Source files | Symbols actually imported (union) |
| --- | ---: | ---: | --- |
| `cultnet-ts` | 11 (StreamPixels, Heimdall, AetheriaEve, AetheriaEve-cultlib-admission, VoidBot, Stonks, Vili, weksa, Sai, EvePlugins, Bifrost) | 28 | `CultNetRudpSession`, `decodeRudpPacket`, `encodeRudpPacket`, `encodeCultNetMessageForWire`, `parseCultNetMessage`, `invokeCultNetOperation`, `startCultNetOperationServer`, `defineCultNetDocumentBinding`, `CultNetDocumentRegistry`, runtime-presence helpers, and message/packet types |
| `cultnet-ts/contracts` | 0 | 0 | one esbuild alias only (`AetheriaEve-cultlib-admission/scripts/verify-aetheria-browser-provider.mjs:48`); `cultmesh-browser` is its only importer and is inside CultLib |
| `cultmesh-ts` | 7 (StreamPixels, Heimdall, AetheriaEve and its worktree, Stonks, weksa, Odin and its worktree) | 10 | `CultMesh`, `cultMeshRectFromBounds`, `cultMeshViewportRequest`, and types (`CultMeshRudpEndpoint`, `CultMeshDocumentCatalog`, `CultMeshViewportRequest`, diagnostics types) |
| `@gamecult/cultcache-ts` | 1 (Heimdall) | 5 | `SingleFileMessagePackBackingStore`, `defineDocumentType`, `CultCacheEnvelope` |
| `cultcache-ts` (old unscoped name) | 8 | 16 | `CultCache`, `SingleFileMessagePackBackingStore`, `defineDocumentRegistry`, `defineDocumentType`, `/inspection` |
| `cultmesh-browser` | 1 (AetheriaEve and its worktree) | 1 | `CultMeshBrowserClient`, `CultMeshBrowserOdinRendezvous`, `decodeCultNetPayload` |

StreamPixels specifically: `apps/service/package.json:25-27` declares
`cultcache-ts`, `cultmesh-ts`, `cultnet-ts` as `file:../../../CultLib/packages/<pkg>`;
`apps/service/src/verse-state.ts:4-6` imports `defineDocumentType`,
`CultMesh`, `defineCultNetDocumentBinding`; `apps/service/src/idunn-rudp-health.ts:2-7`
imports `encodeCultNetMessageForWire`, `encodeRudpPacket`, and two types.
Nothing else in StreamPixels imports these packages. No TS/JS file anywhere
under `F:\Projects` references `cultmesh-state+quic` or `CultMeshRealtime`.

Sizing consequences:

- Nothing this migration adds has a consumer yet. The QUIC surface is new
  API; no existing export is reshaped, so no external repo changes are
  required by the CultLib cuts. StreamPixels adoption is consumer work after
  release (target, "Not in this migration").
- The `verifyAuthorityRoute` family in `cultmesh-browser` is module-private
  (not exported at `cultmesh-browser/src/index.ts:955`, `:979`, `:998`,
  `:1012`, `:1028`), so moving it (Q2) changes no external import. The three
  symbols AetheriaEve imports stay.
- `cultnet-ts/contracts` has no external consumer; adding a sibling subpath
  export for the shared verifier follows an existing, internally consumed
  pattern rather than inventing a new one.
- The `cultcache-ts` naming split (8 repos on the old unscoped name, Heimdall
  on `@gamecult/`) is pre-existing and not touched here, but it is the
  precedent Q5 has to reckon with.

## 4. Rejected options

- **Node built-in QUIC.** Absent in Node 24 (P1). Not a choice.
- **`@matrixai/quic` 2.0.9.** A Rust/quiche-based QUIC stack with its own
  stream, certificate and event semantics `(probe: npm view description)`.
  Rejected because the target requires the bytes on the wire, the ALPN, the
  stream-kind byte, the deferred certificate pin decision, and the close/abort
  codes to match `GameCult.Mesh.Quic` and the Unity MsQuic connector; two QUIC
  stacks would have to be proven equivalent at every one of those points, and
  CultLib would own neither. Semantically wrong for this campaign, not merely
  unfashionable.
- **`@fails-components/webtransport` 1.6.8.** libquiche behind a WebTransport
  (HTTP/3) session model `(probe)`. Wrong layer: the realtime plane is raw
  QUIC with ALPN `cultmesh-state-v1`, not HTTP/3.
- **`node-quic` 0.1.3.** A wrapper around an abandoned pure-JS QUIC `(probe)`.
  Not TLS 1.3 MsQuic; rejected on the same grounds.
- **A Node-API addon (node-addon-api + node-gyp/cmake-js) wrapping MsQuic
  directly.** Viable, but it creates a second C++ owner of MsQuic callback
  semantics beside the Unity bridge, requires Node headers and MSVC on every
  Windows build host and a Node-ABI artifact per platform, and buys nothing the
  C ABI plus FFI does not (P2). Rejected as duplicate authority. If koffi were
  ever removed, the cheapest replacement is a thin N-API shim over the same C
  ABI, not a second MsQuic body.
- **A separate .NET provider process or a WebSocket shortcut.** Already ruled
  out by the operator (target, line 15-17).
- **JS callbacks from MsQuic worker threads.** koffi supports them by queuing
  to the main thread (P4), but it turns every MsQuic event into a
  cross-thread hop and opens the documented deadlock class. Rejected in favour
  of the event-queue/wait model the existing bridge already uses.
- **Building the Windows DLL with the MinGW gcc on PATH.** Present (P6) but the
  committed Unity artifact is MSVC `/Brepro`; two toolchains for one DLL is a
  reproducibility liability. Rejected.
- **Building the Linux artifact on Starfire and shipping it.** Cross-building
  is explicitly forbidden by the target; a Docker `debian:13` container on
  Starfire is a Linux host and is fine for the development loop, but the
  release artifact comes from the builder the operator names in Q6.
- **Keeping `cert-sha256` verification in C.** The v2 ABI hands the DER
  certificate to the host and waits for the decision, which is the "caller
  hook" the target demands and removes the Windows-only
  `CryptHashCertificate2` from the portable path. The v1 entrypoint keeps its
  in-C pin check only as the Unity compatibility shim (section 8).

## 5. Authority maps

### 5.1 Native MsQuic bridge (`native/GameCult.Mesh.Quic.Native`)

- Owner: `cultmesh_quic_native.cpp` owns MsQuic registration, configuration,
  listener, connection and stream handles, TLS credential loading, the
  1-byte-kind + 4-byte-LE-length stream framing, and a single event queue per
  runtime.
- Inputs: host/port, PKCS12 bytes and password for providers, the host's
  certificate decision for clients, encoded frame bytes to send.
- Outputs: events (`listener_new_connection`, `connection_connected`,
  `connection_certificate_received` with DER payload, `connection_shutdown`
  with code/status/text, `stream_started` with kind, `stream_frame` with the
  whole encoded frame, `stream_send_complete`, `stream_shutdown`); the bound
  listener port; structured error text.
- Derived state: none that the host reads as truth. Connection counts, remote
  addresses and MsQuic status codes are diagnostics.
- Forbidden writers: the bridge never decodes a CultMesh frame, never decides
  delivery semantics, never coalesces, never accepts a certificate on its own
  in the v2 path, never reconnects. The v1 shim is the only place a pin is
  compared in C, for the Unity contract only.
- Shared paths: the Unity managed connector (v1 exports) and the Node binding
  (v2 exports) use the same library, the same MsQuic runtime, the same
  framing code.
- Deletion line: the per-client `Client` struct with its own registration
  (`:51-63`, `:316-393`), Windows-only includes and export macros (`:1-3`,
  `:18-22`), `CertificateMatchesPin` as the sole trust path (`:184-200`).

### 5.2 TypeScript QUIC realtime plane (`packages/cultmesh-ts`)

- Owner: `src/realtime-quic.ts` owns provider and consumer semantics: which
  stream kind carries which delivery mode, the per-peer keyed coalescing
  outbox for `LatestOnly`, the single ordered stream for `ReliableOrdered`,
  `Unreliable` failing closed, the generation filter on receive, and the
  certificate pin decision on the consumer.
- Inputs: bridge events; `CultMeshRealtimeFrame` objects from application
  code; an advertised `CultMeshTransportCandidate`-shaped route from the
  session manager; the consumer's trust policy.
- Outputs: encoded frames to the bridge; decoded frames to the application;
  the advertised endpoint string for a provider (with `cert-sha256`).
- Derived state: connection health, connected peer count, stream ids.
- Forbidden writers: application code never opens a stream, picks an
  endpoint, or writes a reconnect loop (target). `src/realtime-wire.ts` never
  touches a socket. `src/realtime-quic-native.ts` (the koffi binding) never
  interprets frame bytes.
- Shared paths: provider broadcast and consumer send both go through
  `sendFrame` on the same transport object; the coalescing outbox is the only
  path for `LatestOnly` on a provider, whether the caller is a test, a tick, or
  a reconnect.
- Deletion line: none inside `cultmesh-ts` (there is no prior QUIC code); the
  RUDP provider transport at `src/provider-rudp-transport.ts` is untouched.

### 5.3 Shared TypeScript route verification (Q2)

- Owner after the cut: `packages/cultnet-ts/src/cultmesh-authority.ts`,
  published as subpath `cultnet-ts/authority`. It is the only TypeScript
  implementation of: Odin root lookup, validity window, canonical route
  transcript, canonical session transcript, P-256/P1363 verification,
  loopback detection, and the protected-scheme rule.
- Inputs: a route view (Verse, authority runtime, endpoint, protocol ids,
  priority, generation, certificate), a trust policy (`mode`, `odinRoots`,
  `now`), a session-open request and an accepted message.
- Outputs: resolution or a thrown `Error` with the same message texts the
  browser package emits today (its tests pin them).
- Derived state: none.
- Forbidden writers: `cultmesh-browser` must not retain or regrow
  `verifyAuthorityRoute`, `canonicalRoute`, `canonicalSession`,
  `canonicalFields`, `verifyP256`, or `isLoopbackEndpoint`; `cultmesh-ts` must
  not implement any of them; neither transport package may depend on the
  other. Negative greps in section 6 pin this.
- Shared paths: the browser WebSocket client and the Node QUIC consumer verify
  the same route bytes through the same function.
- Deletion line: `cultmesh-browser/src/index.ts:955-1072` and `:797-808`.
- Behavioural note, not a silent change: the protected-scheme rule becomes the
  C# rule (`wss`, `https`, scheme containing `quic`;
  `CultMeshAuthorityProof.cs:156-162`) instead of `wss`-only
  (`cultmesh-browser/src/index.ts:965`). The browser client still refuses
  non-`ws(s)` route endpoints at `:537-540`, so browser behaviour is
  unchanged; the shared module simply stops being wrong for QUIC routes.

### 5.4 Build and packaging

- Owner: `scripts/build-quic-native.ps1` (Windows x64) and the new
  `scripts/build-quic-native.sh` (Linux x64) own producing the bridge
  artifact; `.github/workflows/publish-packages.yml` owns turning artifacts
  into registry packages.
- Inputs: pinned MsQuic 2.5.9 (Schannel NuGet on Windows; `libmsquic` deb on
  Linux) with SHA-256 checks; the bridge source.
- Outputs: `gamecult_mesh_quic_native.dll` + `msquic.dll` (win32-x64),
  `libgamecult_mesh_quic_native.so` + `libmsquic.so.2` (linux-x64), each with
  the MsQuic license.
- Derived state: `artifacts/` (git-ignored, `.gitignore:66`).
- Forbidden writers: no `postinstall` compile step in any published package;
  a consumer host never needs CMake or a compiler.
- Deletion line: none; the Windows script is kept and the Unity package script
  keeps calling it.

## 6. Cut 1 (subtraction): one TypeScript route verifier

Purpose: land Q2 before any QUIC code exists, so the Node consumer has one
owner to call and the browser package has nothing left to grow.

Deletes (exact):

- `packages/cultmesh-browser/src/index.ts:955-977` `verifyAuthorityRoute`
  (23 lines), `:979-996` `canonicalRoute` (18), `:998-1010` `canonicalSession`
  (13), `:1012-1026` `canonicalFields` (15), `:1028-1058` `verifyP256` (31),
  `:1065-1072` `isLoopbackEndpoint` (8), `:797-803` `bytesToBase64` (7),
  `:805-808` `base64ToBytes` (4). Total 119 lines deleted from the browser
  package. `randomNonce` (`:1060-1063`) stays: it is browser-client behaviour,
  not verification.

Keeps and moves:

- The deleted bodies move verbatim into
  `packages/cultnet-ts/src/cultmesh-authority.ts` as exported functions, with
  one edit: the protected-scheme test in `verifyAuthorityRoute` becomes
  `isProtectedEndpoint(endpoint)` implementing the C# rule (P19). Types
  `CultMeshBrowserIdentity`, `CultMeshBrowserRoute`,
  `CultMeshBrowserP256PublicKey`, `CultMeshBrowserRouteCertificate`,
  `CultMeshBrowserAuthorityTrustMode`, `CultMeshBrowserAuthorityTrustPolicy`
  (`cultmesh-browser/src/index.ts:19-53`) move too, renamed without the
  `Browser` infix (`CultMeshAuthorityIdentity`, `CultMeshAuthorityRouteView`,
  `CultMeshP256PublicKey`, `CultMeshAuthorityRouteCertificate`,
  `CultMeshAuthorityTrustMode`, `CultMeshAuthorityTrustPolicy`).
  `cultmesh-browser` re-exports them under the old names as type aliases.
- `validateSessionAcceptance` (`:926-953`) stays in the browser package; it
  compares WebSocket handshake fields. Its crypto lines (`:944-951`) are
  replaced by a call to the new `verifyProviderSessionProof(request, endpoint,
  providerKey, signature)` exported from the shared module.

Adds:

- `packages/cultnet-ts/src/cultmesh-authority.ts` (about 190 lines: the moved
  119 plus types and `isProtectedEndpoint`). Browser-safe by construction: it
  may use only `globalThis.crypto.subtle`, `TextEncoder`, `atob`, `btoa`,
  `URL`, `DataView`, `Uint8Array`. No `node:` import.
- `packages/cultnet-ts/package.json:7-18` gains an `"./authority"` export
  beside `"./contracts"` (`:12-16`), same three-key shape.
- `packages/cultnet-ts/src/index.ts` re-exports the module's public names.
- `packages/cultnet-ts/test/cultmesh-authority.test.ts` (about 150 lines):
  signs a route with `node:crypto` `generateKeyPairSync("ec", { namedCurve:
  "P-256" })` and verifies through the shared module; one negative per rule
  (expired, unsigned remote, wrong Odin root, mutated endpoint, wrong signature
  length); and asserts `isProtectedEndpoint` accepts `wss://`, `https://`,
  `cultmesh-state+quic://` and rejects `ws://`, `cultnet+tcp://`.
- A cross-runtime vector: `tests/GameCult.Mesh.Tests/CultMeshAuthorityProofTests.cs`
  gains a test that calls `CultMeshAuthorityProof.CreateSignedRoute`
  (`src/GameCult.Mesh/CultMeshAuthorityProof.cs:221`) with a fixed test key
  and writes `contracts/cultmesh/authority-route-vectors.json` (route fields,
  Odin public key, signature) when `CULTMESH_WRITE_VECTORS=1`, and otherwise
  asserts the committed file still verifies. The TS test verifies the same
  file through the shared module. This is a shared-vector check, not
  cross-process interop; it proves the transcript bytes, which is what Q2
  needs.

Per-file changes:

- `packages/cultmesh-browser/src/index.ts:1-17` add
  `import { verifyAuthorityRoute, verifyProviderSessionProof, isLoopbackEndpoint, type ... } from "cultnet-ts/authority";`
  (the package already imports `cultnet-ts/contracts` at `:2-17`, so
  resolution is proven). `:536` and `:914` call the imported function
  unchanged. `:944-951` call `verifyProviderSessionProof`. `:961` and `:965`
  are gone with the function.
- `packages/cultmesh-browser/tsconfig.json` unchanged (`moduleResolution:
  "Bundler"` already resolves the `exports` map).

Verification:

- `npm run test --workspace packages/cultnet-ts` (build + unit; pins the
  shared verifier and the vector file).
- `npm run test --workspace packages/cultmesh-browser` (all 17 existing tests
  at `test/cultmesh-browser.test.ts:29-472` must pass unchanged; they pin the
  error strings and the refusal order, including "rejects a mutated or expired
  signed route before opening a provider socket" at `:239-258`).
- `dotnet test tests/GameCult.Mesh.Tests --filter FullyQualifiedName~AuthorityProof`
  (vector generation/verification; focused, one project).
- `node scripts/test-typescript-package-closure.mjs` (the packed
  `cultmesh-browser` tarball must resolve `cultnet-ts/authority` from the
  registry-shaped install at `scripts/test-typescript-package-closure.mjs:63-72`).
- Negative greps (must return nothing):
  `rg -n "crypto\.subtle\.(verify|importKey)|function (verifyAuthorityRoute|canonicalRoute|canonicalSession|canonicalFields|verifyP256|isLoopbackEndpoint)" packages/cultmesh-browser/src packages/cultmesh-ts/src`
  and `rg -n "cultmesh-browser" packages/cultmesh-ts/package.json packages/cultnet-ts/package.json`.
- Operator-only: none.

## 7. Cut 2 (behaviour, pure TypeScript): the realtime frame codec

Purpose: byte parity with `CultMeshRealtimeWireProtocol` before any transport
exists, proven in both directions with shared vectors.

Deletes: none.

Adds:

- `packages/cultmesh-ts/src/realtime-wire.ts` (about 130 lines). Exports
  `CULTMESH_REALTIME_ALPN = "cultmesh-state-v1"`,
  `CULTMESH_REALTIME_CONNECTION_CLOSE_CODE = 0x43554c54n`,
  `CULTMESH_REALTIME_STREAM_ABORT_CODE = 0x53544154n`,
  `CULTMESH_REALTIME_RELIABLE_STREAM = 1`, `CULTMESH_REALTIME_LATEST_ONLY_STREAM = 2`,
  `CULTMESH_REALTIME_MAX_PAYLOAD_BYTES = 64 * 1024 * 1024`,
  `CULTMESH_REALTIME_MAX_ENCODED_FRAME_BYTES` (= max payload + 37 + 3 * 65535,
  matching `CultMeshRealtimeWireProtocol.cs:20`), type
  `CultMeshRealtimeDelivery = "reliable-ordered" | "latest-only" | "unreliable"`
  with wire bytes 0/1/2 in that order (`CultMeshRealtimeTransports.cs:8-13`),
  interface `CultMeshRealtimeFrame { channelId; schemaId; bodyId;
  producerEpoch: bigint; sequence: bigint; delivery; payload: Uint8Array }`,
  `encodeRealtimeFrame(frame): Uint8Array`, `decodeRealtimeFrame(bytes):
  CultMeshRealtimeFrame`. Layout exactly `CultMeshRealtimeWireProtocol.cs:37-54`
  (magic `0x31545343` LE u32, delivery u8, epoch i64 LE, sequence i64 LE,
  three u16 LE lengths, payload i32 LE, header size i32 LE = 37, version u16
  LE = 1, then UTF-8 identities, then payload). Decode rejections match
  `:60-76` one for one: truncated header, wrong magic, unsupported version or
  header size, delivery out of range, negative payload length, length sum
  mismatch. Epoch and sequence are `bigint` because i64 exceeds the safe
  integer range; validation mirrors `CultMeshRealtimeTransports.cs:29-39`
  (non-empty identities, non-negative epoch/sequence).
- `contracts/cultmesh/realtime-frame-vectors.json`: written by a new C# test
  `tests/GameCult.Mesh.Tests/CultMeshRealtimeWireProtocolTests.cs` under
  `CULTMESH_WRITE_VECTORS=1` and otherwise asserted against. Cases: one frame
  per delivery mode with small payload; empty payload; identities at exactly
  65,535 UTF-8 bytes (multi-byte characters included so byte length and
  character length differ); epoch and sequence at `long.MaxValue`; and a
  64 MiB-payload case recorded as `{ sha256OfEncoding, encodedLength }` rather
  than bytes. Malformed cases: wrong magic, version 2, header size 36,
  delivery 3, payload length -1, length sum off by one, each with the C#
  exception message. There is no existing C# test for this codec
  (`rg CultMeshRealtimeWireProtocol tests` returns nothing `(probe)`), so this
  is also the first direct C# codec test.
- `packages/cultmesh-ts/test/realtime-wire.test.ts` (about 120 lines): every
  vector decodes to the recorded fields and re-encodes to the recorded bytes;
  every malformed vector throws; the 64 MiB case is built in-process and its
  SHA-256 compared; TypeScript-produced bytes for the same fields equal the
  C#-produced bytes (this direction is what "TypeScript encodings decode in
  C#" means when the vector file is the medium; the cross-process direction is
  cut 5).
- `packages/cultmesh-ts/src/index.ts:37-40` gains `export * from "./realtime-wire";`.

Verification:

- `dotnet test tests/GameCult.Mesh.Tests --filter FullyQualifiedName~RealtimeWireProtocol`.
- `npm run test --workspace packages/cultmesh-ts`.
- Negative grep: `rg -n "0x31545343|cultmesh-state-v1" packages --glob '!**/dist*/**'`
  must hit only `realtime-wire.ts` and its test in TypeScript (one codec).
- Operator-only: none.

## 8. Cut 3 (native, subtraction then behaviour): the runtime-neutral bridge

Purpose: one MsQuic body for Unity and Node, two roles, two platforms, no
callbacks across the host boundary.

Deletes first (in `native/GameCult.Mesh.Quic.Native/cultmesh_quic_native.cpp`):

- `:1-3` Windows includes; `:18-22` `__declspec` macros; `:51-63` per-client
  `Client` with its own `api/registration/configuration`; `:184-200`
  `CertificateMatchesPin` as the trust path; `:288-305` `CloseHandles`;
  `:309-393` `cultmesh_quic_open` as the primary API. About 200 of 440 lines
  are removed or rewritten; the framing consumer (`:123-157`), stream callback
  (`:159-182`), trace/error helpers (`:65-86`, `:202-221`) are kept in spirit
  and re-homed on the runtime.
- `CMakeLists.txt:4-6` (Windows-only fatal) and `:7-9` (`MSQUIC_ROOT`
  mandatory) are replaced by a platform branch.

Adds (C ABI v2, all `extern "C"`, exported with a portable macro
`CULTMESH_API` that is `__declspec(dllexport)` on MSVC and
`__attribute__((visibility("default")))` elsewhere):

```
int32_t cultmesh_quic_runtime_open(const char* app_name, void** out_runtime);
void    cultmesh_quic_runtime_close(void* runtime);
int32_t cultmesh_quic_listener_open(void* runtime, const char* host, uint16_t port,
            const uint8_t* pkcs12, int32_t pkcs12_len, const char* password,
            uint64_t* out_listener_id, uint16_t* out_bound_port);
void    cultmesh_quic_listener_close(void* runtime, uint64_t listener_id);
int32_t cultmesh_quic_connection_open(void* runtime, const char* host, uint16_t port,
            uint64_t* out_connection_id);
int32_t cultmesh_quic_connection_certificate_complete(void* runtime, uint64_t connection_id,
            int32_t accept);
void    cultmesh_quic_connection_shutdown(void* runtime, uint64_t connection_id, uint64_t code);
int32_t cultmesh_quic_stream_open(void* runtime, uint64_t connection_id, uint8_t kind,
            uint64_t* out_stream_id);
int32_t cultmesh_quic_stream_send_frame(void* runtime, uint64_t stream_id,
            const uint8_t* encoded_frame, int32_t length, int32_t fin);
void    cultmesh_quic_stream_shutdown(void* runtime, uint64_t stream_id, uint64_t code);
int32_t cultmesh_quic_next_event(void* runtime, int32_t timeout_ms,
            cultmesh_quic_event* out_event, uint8_t* payload, int32_t payload_capacity,
            int32_t* out_required);
int32_t cultmesh_quic_last_error(void* runtime, char* destination, int32_t capacity);
```

- `cultmesh_quic_event` is a fixed 64-byte struct: `uint32 type; uint32
  stream_kind; uint64 listener_id; uint64 connection_id; uint64 stream_id;
  uint64 code; int32 status; int32 payload_length; uint8 reserved[16]`.
  Types: 1 `listener_new_connection`, 2 `connection_connected`, 3
  `connection_certificate_received` (payload = DER), 4 `connection_shutdown`
  (payload = UTF-8 reason), 5 `stream_started` (inbound, `stream_kind` set
  from the first byte), 6 `stream_frame` (payload = one whole encoded frame,
  length prefix stripped), 7 `stream_send_complete`, 8 `stream_shutdown`, 9
  `listener_stopped`. `next_event` returns 0 on timeout, 1 with the event
  filled and payload copied, 2 when `payload_capacity` is too small (event
  stays at the head; `out_required` set), negative on error. This is the v1
  two-phase poll convention (`:400-418`) applied to a queue of typed events.
- Client connections use `QUIC_CREDENTIAL_TYPE_NONE` with
  `INDICATE_CERTIFICATE_RECEIVED | DEFER_CERTIFICATE_VALIDATION |
  USE_PORTABLE_CERTIFICATES` (already at `:358-364`), return
  `QUIC_STATUS_PENDING` from the certificate event, enqueue event 3, and call
  `ConnectionCertificateValidationComplete` only when the host calls
  `certificate_complete` (P11: `msquic.h:132-133,142,1290`). Timeout: the
  bridge sets a 10 s handshake idle; if the host never answers, MsQuic's
  handshake timeout closes the connection and event 4 is emitted.
- Listeners load `QUIC_CREDENTIAL_TYPE_CERTIFICATE_PKCS12`
  (`msquic.h:123,394-398`), one credential type for Schannel and OpenSSL; the
  provider's advertised pin is the SHA-256 of the DER certificate computed on
  the TypeScript side from the same PKCS12 with `node:crypto` (no hashing in
  C).
- Settings mirror the C# server/connector: `PeerUnidiStreamCount 1024`
  (`CultMeshQuicRealtimeTransport.cs:77`, `:202`), idle timeout 30 s and
  keep-alive 5 s as in the existing bridge (`:335-338`).
- `stream_send_frame` copies the bytes, prepends the 4-byte LE length, calls
  `StreamSend` (with `QUIC_SEND_FLAG_FIN` when `fin`), and emits event 7 on
  `QUIC_STREAM_EVENT_SEND_COMPLETE`. `stream_open` writes the kind byte as the
  first send. Framing therefore lives in C on both directions, exactly where
  the v1 bridge already keeps it (`:123-157`), and `CultMeshQuicRealtimeProtocol`
  keeps it in C# (`CultMeshQuicRealtimeTransport.cs:635-655`).
- The v1 exports `cultmesh_quic_open/state/poll/error/close` are kept, Windows
  only (`#ifdef _WIN32`), implemented on top of a private runtime with the pin
  check retained from `:184-200`. External contract protected: the Unity
  package `DllImport` at `CultMeshNativeQuicRealtimeTransport.cs:259-277` and
  `CanConnect`'s Windows gate at `:39-41`. They delegate; they decide nothing
  new.
- `CMakeLists.txt`: Windows branch unchanged in effect (MSVC, `/Brepro`,
  `MSQUIC_ROOT` from the NuGet layout, links `msquic crypt32 bcrypt`); Linux
  branch links `msquic` from `MSQUIC_LIB_DIR` (the extracted deb's
  `usr/lib/x86_64-linux-gnu`) with headers from `MSQUIC_INCLUDE_DIR`, `-fvisibility=hidden`,
  output `libgamecult_mesh_quic_native.so`, `RPATH $ORIGIN` so the sibling
  `libmsquic.so.2` resolves.
- `scripts/build-quic-native.sh` (about 70 lines): fetches
  `libmsquic_2.5.9_amd64.deb` from the packages.microsoft.com pool URL in P7,
  verifies its SHA-256, extracts with `dpkg-deb -x`, fetches the three headers
  at tag `v2.5.9` (P10) and verifies their SHA-256 (record the hashes at first
  run into the script, the same way `build-quic-native.ps1:14` pins the
  NuGet), configures CMake with Ninja, and copies `libgamecult_mesh_quic_native.so`,
  `libmsquic.so.2`, and the MsQuic LICENSE into `artifacts/quic-native/linux-x64`.
- `native/GameCult.Mesh.Quic.Native/README.md` rewritten to describe both
  roles, both platforms, the v1/v2 split, and the "no host callbacks" rule.

Per-file changes outside `native/`:

- `scripts/build-quic-native.ps1:54-58` output path becomes
  `artifacts/quic-native/win32-x64` by default (parameter default at `:4`);
  callers `build-unity-package.ps1:80-82` and the Native.Tests csproj `:20-22`
  pass explicit `-OutputDirectory` today, so they are unaffected.
- No change to `src/GameCult.Mesh.Quic.Native/*.cs`. The Unity managed
  connector keeps polling v1.

Verification:

- Windows: `powershell -File scripts/build-quic-native.ps1` then
  `dotnet test tests/GameCult.Mesh.Quic.Native.Tests` (the csproj rebuilds the
  bridge itself at `:20-22`; all four tests at
  `CultMeshNativeQuicRealtimeTransportTests.cs:15-141` must pass, including the
  wrong-pin refusal at `:79-99` and the departed-client test at `:101-141`).
  This is the regression gate for the v1 shim.
- Windows FFI smoke, before any TypeScript transport exists:
  `scratchpad`-style script (to be committed as
  `packages/cultmesh-ts/test/realtime-quic-native.test.ts` in cut 4) that
  opens a runtime, a listener with a PKCS12 generated by `openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 ... | openssl pkcs12 -export`
  (P13), a client connection to it, answers event 3 with accept, sees event 2
  on both sides, opens a latest-only stream, sends one frame with `fin`, and
  reads event 6 on the server side with the identical bytes.
- Linux, development loop: `docker run --rm -v F:\Projects\CultLib:/src debian:13 bash -lc "apt-get update && apt-get install -y cmake ninja-build g++ curl ca-certificates dpkg && /src/scripts/build-quic-native.sh"`
  then the same FFI smoke under `node:24` in the container. This is a Linux
  build on a Linux host; it is not the release artifact (Q6).
- Build economy: the only builds are the native CMake target and
  `tests/GameCult.Mesh.Quic.Native.Tests` (which pulls `GameCult.Mesh.Quic`,
  `GameCult.Mesh.Quic.Native`, `GameCult.Mesh`, `GameCult.Networking`,
  `GameCult.Caching` by project reference). Before the first `dotnet build`,
  take `Get-ChildItem -Recurse F:\Projects\CultLib\bin,F:\Projects\CultLib\obj,F:\Projects\CultLib\artifacts | Select FullName,Length`
  to a scratch file and diff afterwards; restore or delete anything outside
  those five projects' outputs.
- Negative greps: `rg -n "CryptHashCertificate2|wincrypt" native/` must hit
  only inside the `#ifdef _WIN32` v1 block; `rg -n "windows.h" native/` only
  inside `#ifdef _WIN32`.
- Operator-only: rebuilding and re-committing the Unity plugin DLL
  (`unity/org.gamecult.cultlib/Runtime/Plugins/x86_64/gamecult_mesh_quic_native.dll`)
  and cutting a Unity package version through `scripts/build-unity-package.ps1`
  is a release action; the map does not schedule it. Until it happens, Unity
  keeps the old DLL, which is fine because v1 exports are unchanged.

## 9. Cut 4 (behaviour): Node binding, consumer, session manager, trust

Purpose: a TypeScript consumer that connects by advertised candidate, verifies
the Odin route through the cut-1 module, pins the TLS certificate, and
receives frames from the C# managed provider in a separate process.

Deletes: none.

Adds:

- `packages/cultmesh-ts/src/realtime-quic-native.ts` (about 260 lines): the
  koffi binding. Resolves the platform package (`@gamecult/cultmesh-quic-native-win32-x64`
  or `-linux-x64`, Q5) or, when `CULTMESH_QUIC_NATIVE_DIR` is set, a directory
  (this is how the repo's own tests point at `artifacts/quic-native/<platform>`
  without a registry). Loads `msquic` first, then the bridge, by absolute path
  (the probe showed the dependent library must be resolvable before the
  bridge loads). Declares the twelve v2 prototypes. Exposes
  `CultMeshQuicNativeRuntime` with a single event pump: a loop that awaits
  `nextEvent.async(runtime, 250, ...)` and dispatches typed events to
  registered listeners/connections by id. One pump per process-wide runtime;
  reference-counted open/close. No koffi callbacks anywhere.
- `packages/cultmesh-ts/src/realtime-quic.ts` (about 650 lines across cuts 4
  and 5). Cut 4 lands: `CultMeshQuicRealtimeTransport` (implements
  `sendFrame`, `receiveFrame`, `dispose`, `transportId = "msquic-realtime"`,
  `endpoint`; the `isVerifiedFor(verseId, authorityRuntimeId, protocolId,
  routeGeneration)` check mirroring `CultMeshQuicRealtimeTransport.cs:352-361`),
  `CultMeshQuicRealtimeConnector` (`connectorId`, `priority`, `canConnect(candidate)`,
  `connect(candidate, target, signal)`), and the receive path: stream kind
  vs frame delivery consistency (`:519-524`), and the per-`(channel, body)`
  generation filter that drops older `(producerEpoch, sequence)` (`:538-560`).
  Certificate decision on event 3: caller `validateProviderCertificate(target,
  der)` if supplied, else SHA-256 of DER equals the endpoint's `cert-sha256`
  (`:111-119`, case-insensitive hex); otherwise reject. Endpoint parsing:
  scheme `cultmesh-state+quic`, host, port, optional pin (`:98-109`).
- `CultMeshQuicRealtimeSessionManager` in the same file: takes an
  `ICultMeshRealtimeLookupSource`-shaped port `{ resolve(target): Promise<CultMeshVerseDescriptorMessage[]> }`,
  a trust policy from `cultnet-ts/authority`, and connectors. Binds
  candidates from `authorityRoutes` whose `protocolIds` include
  `cultmesh.realtime_state.v1` (`CultMeshSessions.cs:77`) and whose
  `authorityRuntimeId` matches; verifies each with `verifyAuthorityRoute`
  before any connector is asked; orders by connector priority then route
  priority; races at most `maxRacedCandidates` per tier (`:833-880`); requires
  `isVerifiedFor` after connect (`:885-896`); keys sessions by
  `verseId + "\u001f" + authorityRuntimeId` (`:46`); marks offline on transport
  failure. Ships with `CultMeshStaticRealtimeLookupSource` (an array of
  `CultMeshVerseDescriptorMessage` from `cultnet-ts/contracts:350-369`). No
  Odin WebSocket rendezvous in Node in this migration (Q7).
- `CultMesh` facade (`packages/cultmesh-ts/src/index.ts:4700`) gains statics
  beside the RUDP ones at `:6008-6186`: `parseQuicRealtimeEndpoint`,
  `createQuicRealtimeConnector(options)`, `createQuicRealtimeSessionManager(options)`,
  and in cut 5 `createQuicRealtimeProvider(options)`.
- `packages/cultmesh-ts/package.json:44-48` gains `"koffi": "3.3.0"` (exact)
  and `optionalDependencies` for the two platform packages (names per Q5).
  `koffi` is required lazily inside `realtime-quic-native.ts` so consumers
  that never touch QUIC (AetheriaEve Electron, Odin, Stonks, weksa) load
  nothing native.
- `packages/cultmesh-ts/test/realtime-quic-native.test.ts` (about 150 lines):
  the cut-3 FFI smoke, now against the runtime class.
- `packages/cultmesh-ts/test/realtime-quic-consumer.test.ts` (about 250
  lines): TypeScript consumer against a TypeScript listener opened through
  the raw runtime (no provider semantics yet): connect with correct pin;
  refuse wrong pin (`cert-sha256` of 64 zeros, mirroring
  `CultMeshNativeQuicRealtimeTransportTests.cs:79-99`); refuse missing
  certificate when no validator; caller validator observed with the target
  identity; generation filter drops a late older generation (mirrors
  `CultMeshQuicRealtimeTransportTests.cs:111-140`).
- Trust negatives, each a separate test with its own mutation, in the same
  file, through the session manager with a static lookup source and a signed
  route built with `node:crypto`: (a) expired certificate (`now` past
  `expiresAt`); (b) unsigned remote route (certificate absent, mode
  `authenticated-remote`); (c) wrong Odin root (trust has a different
  `keyId`); (d) `cert-sha256` pin mismatch (route verifies, TLS pin fails, so
  the connector rejects before `isVerifiedFor`); (e) replayed nonce. On the
  QUIC plane there is no `session_open` nonce exchange; the route's
  freshness is its generation plus the TLS handshake. The replay test
  therefore mutates `generation` on the connected transport's target and
  asserts `isVerifiedFor` fails and the session manager disposes the
  transport (`CultMeshSessions.cs:885-896`). If the operator wants a nonce on
  QUIC, that is a protocol addition on both runtimes and belongs to a later
  pipeline; this map does not invent it.
- Interop lane, C# managed provider to TypeScript consumer, separate
  processes: `tests/GameCult.Networking.InteropPeer/Program.cs:27-49` gains
  mode `quic-realtime-serve` (`--port`, `--frames N`, `--delivery
  latest-only|reliable-ordered`, `--interval-ms`): creates a self-signed
  certificate exactly as `CultMeshQuicRealtimeTransportTests.cs:203-228`,
  starts `CultMeshQuicRealtimeServer`, prints one JSON line
  `{ endpoint: "cultmesh-state+quic://127.0.0.1:<port>?cert-sha256=<HEX>" }`
  to stdout, then broadcasts `N` frames with increasing sequence and exits
  after the last `send_complete` or on stdin close.
  `GameCult.Networking.InteropPeer.csproj:9-11` gains a `ProjectReference` to
  `src/GameCult.Mesh.Quic/GameCult.Mesh.Quic.csproj`.
  `packages/cultnet-ts/test/interop/cultnet-interop.test.ts` gains
  `test("CultMesh QUIC realtime: C# managed provider and TypeScript consumer", ...)`
  using `spawnServeProcess` (`:2491`) for the C# peer and `runJsonCommand`
  (`:2557`) for a new TypeScript peer script
  `packages/cultmesh-ts/test/interop/cultmesh-quic-peer.ts` (`dial --endpoint
  --expect N`) that prints the received frames as JSON (fields plus payload
  hex). The harness drives processes; it does not import `cultmesh-ts`
  (dependency direction: `cultmesh-ts` depends on `cultnet-ts`, so the reverse
  import is forbidden). The peer script path follows the `tsPeerScript`
  pattern at `:41`.

Verification:

- `npm run test --workspace packages/cultmesh-ts` with
  `CULTMESH_QUIC_NATIVE_DIR=artifacts/quic-native/win32-x64`.
- `npm run test:interop --workspace packages/cultnet-ts` filtered with
  `--test-name-pattern "QUIC realtime"`; it builds the C# interop peer through
  the existing `csharpInteropPeerBuild` path (`:45-58`, `:71`).
- Negative greps: `rg -n "new Function|koffi\.register|\.async\(" packages/cultmesh-ts/src`
  must show `.async(` only on `nextEvent` in `realtime-quic-native.ts` and no
  `koffi.register` (no callbacks);
  `rg -n "verifyP256|canonicalRoute" packages/cultmesh-ts/src` must return
  nothing.
- Operator-only: none.

## 10. Cut 5 (behaviour): TypeScript provider and the coalescing outbox

Purpose: the StreamPixels role. A TypeScript listener that broadcasts with an
explicit delivery mode and cannot be backpressured by a slow consumer, proven
against both C# connectors in separate processes.

Deletes: none.

Adds (in `packages/cultmesh-ts/src/realtime-quic.ts`):

- `CultMeshQuicRealtimeProvider.listen({ host, port, serverCertificate:
  { pkcs12, password }, handshakeTimeoutMs })`: opens a listener, computes
  `advertisedEndpoint` (`cultmesh-state+quic://<host>:<boundPort>?cert-sha256=<HEX>`,
  uppercase, because the Unity connector requires the pin, P18), accepts
  connections into a peer set, exposes `connectionCount`, `broadcast(frame)`,
  `receive()` for client-originated frames, `dispose()`.
- Delivery semantics, mirroring `CultMeshQuicRealtimeTransport.cs`:
  `reliable-ordered` opens one unidirectional stream of kind 1 per peer on
  first use and serialises writes behind a per-peer promise chain; `broadcast`
  resolves when every peer's `send_complete` arrives or the peer is evicted
  (`:216-235`, `:237-255`). `latest-only` enqueues into a per-peer keyed
  outbox (`Map<"channel\u001fbody", frame>` plus a ready queue) and returns
  synchronously (`:363-367`, `:572-618`); each peer's pump opens a fresh kind-2
  stream per frame, sends with `fin`, and only dequeues the next frame after
  `send_complete`, so a stalled peer accumulates at most one pending frame per
  `(channel, body)` and never blocks `broadcast`. `unreliable` throws
  `NotSupported` with the same sentence as `:373-375` adjusted for the
  runtime.
- Peer eviction on send failure or `connection_shutdown`; no reconnect logic
  on the provider.

Interop lanes (all separate processes; all in
`packages/cultnet-ts/test/interop/cultnet-interop.test.ts`):

- TypeScript provider to C# native connector (the Unity one): the harness
  starts `cultmesh-quic-peer.ts serve --frames 5 --delivery latest-only`,
  reads its advertised endpoint from stdout, then runs
  `dotnet test tests/GameCult.Mesh.Quic.Native.Tests --filter FullyQualifiedName~NativeConnectorReceivesFromExternalManagedProvider`
  with `CULTMESH_NATIVE_EXTERNAL_ENDPOINT=<endpoint>` (the existing test at
  `CultMeshNativeQuicRealtimeTransportTests.cs:15-33` is reused unchanged: it
  already asserts a frame arrives with non-empty schema, body and payload).
  Rename the test to `NativeConnectorReceivesFromExternalProvider` since the
  provider is no longer necessarily managed; the env var name stays.
- TypeScript provider to C# managed connector: add
  `ManagedConnectorReceivesFromExternalProvider` to
  `tests/GameCult.Mesh.Quic.Tests/CultMeshQuicRealtimeTransportTests.cs`,
  gated by the same `CULTMESH_NATIVE_EXTERNAL_ENDPOINT`, using
  `CultMeshQuicRealtimeTransportConnector` with the pin from the endpoint
  (`:111-119`) and no validator, asserting frame fields and payload bytes
  equal what the TypeScript peer prints. The harness runs it the same way.
- `LatestOnly` under a stalled consumer: `cultmesh-quic-peer.ts serve
  --frames 200 --delivery latest-only --interval-ms 1` while the harness dials
  twice with the TypeScript consumer, pauses one consumer's pump for 500 ms
  (a test hook on the runtime that stops calling `next_event`), and asserts:
  the provider's 200 `broadcast` calls complete within a bounded time
  regardless of the pause; the healthy consumer reaches sequence 199; the
  paused consumer, once resumed, converges on 199 with fewer than 200 frames
  received. This is the TypeScript form of
  `CultMeshNativeQuicRealtimeTransportTests.cs:101-141` plus the convergence
  assertion from `CultMeshQuicRealtimeTransportTests.cs:111-140`.
- Golden bytes across processes: the C# managed connector lane above prints
  the received frame's re-encoding (via `CultMeshRealtimeWireProtocol.EncodeFrame`)
  as hex; the harness compares it with the TypeScript peer's `encodeRealtimeFrame`
  output for the same frame. That closes "TypeScript encodings decode in C#"
  by process, not by shared file.

Verification:

- `npm run test --workspace packages/cultmesh-ts` (provider unit tests with
  the TypeScript consumer in-process but separate connections: reliable
  ordering in both directions mirrors `CultMeshQuicRealtimeTransportTests.cs:71-110`;
  latest-only coalescing; unreliable fails closed).
- `npm run test:interop --workspace packages/cultnet-ts --test-name-pattern "QUIC realtime"`.
- `dotnet test tests/GameCult.Mesh.Quic.Tests` and
  `dotnet test tests/GameCult.Mesh.Quic.Native.Tests` without the env var
  (the new tests `Assert.Ignore`, the existing ones still pass).
- Negative greps: `rg -n "setInterval|setTimeout" packages/cultmesh-ts/src/realtime-quic.ts`
  must show no polling of the bridge (the pump is the only wait);
  `rg -n "reconnect" packages/cultmesh-ts/src/realtime-quic.ts` must return
  nothing on the provider side.
- Operator-only: none.

## 11. Cut 6 (packaging and release)

Purpose: a consumer host with no toolchain installs the binding.

Adds:

- `packages/cultmesh-quic-native/win32-x64/package.json` and
  `packages/cultmesh-quic-native/linux-x64/package.json` (names per Q5), each
  with `os`/`cpu` fields, `files` listing the two binaries and
  `MSQUIC-LICENSE.txt`, no scripts, and a `README.md` naming the MsQuic
  version and the build host. Their contents are copied from
  `artifacts/quic-native/<platform>` by the publish job, never committed.
- `.github/workflows/publish-packages.yml`: tag prefixes `cultnet-ts-v*`,
  `cultmesh-ts-v*`, `cultmesh-quic-native-v*` (`:20-24`); a `cultnet-ts` job
  and a `cultmesh-ts` job shaped like the `cultcache-ts` job (`:40-76`); a
  native job with two runners: `windows-latest` running
  `scripts/build-quic-native.ps1`, and `ubuntu-latest` with
  `container: debian:13` running `scripts/build-quic-native.sh` (Q6), each
  publishing its platform package, then the `cultmesh-ts` job depends on both.
- `scripts/test-typescript-package-closure.mjs:9` adds the platform packages
  to the pack list on the matching host and `:63-72` adds
  `assert.equal(typeof mesh.CultMesh.createQuicRealtimeConnector, "function")`
  plus a runtime smoke that opens and closes a `CultMeshQuicNativeRuntime`
  from the installed tarballs (proves the optional dependency resolves and the
  binaries load from `node_modules`, not from a repo path).
- `.github/workflows/cultnet-interop.yml` gains a step after the C# RUDP
  step that builds the Windows bridge (`build-quic-native.ps1`) and runs the
  `QUIC realtime` lanes with `CULTMESH_QUIC_NATIVE_DIR` set. Linux CI
  coverage of the lanes needs a Linux runner with dotnet and the deb; add a
  second job `interop-quic-linux` on `ubuntu-latest` + `container: debian:13`
  running only the QUIC lanes.
- `README.md` of `packages/cultmesh-ts` gains a "QUIC Realtime Plane" section
  after "RUDP Helpers" (`README.md:332`) with provider and consumer examples
  matching the C# README (`src/GameCult.Mesh.Quic/README.md:13-62`).

Verification:

- `node scripts/test-typescript-package-closure.mjs` on Windows.
- Manual dispatch of `publish-packages.yml` with `package=cultmesh-ts` builds
  and tests without publishing (`:24-32` semantics).
- Operator-only: the tag pushes that publish; `NPM_TOKEN` scope for new
  package names; and the Yggdrasil verification: `ssh gamecultadmin@yggdrasil.gamecult.org`,
  `mkdir /tmp/cultmesh-quic-smoke && cd $_ && npm init -y && npm i cultmesh-ts@<version>`,
  then `node -e "require('cultmesh-ts').CultMesh.createQuicRealtimeProvider({...}).then(p => { console.log(p.advertisedEndpoint); return p.dispose(); })"`
  with a throwaway PKCS12 from `openssl`, followed by a loopback dial from a
  second `node` process on the same host. `libmsquic`'s runtime dependencies
  (`libssl3t64`, `libnuma1`, `libxdp1`, `libnl-route-3-200`, P7) must be
  present on Yggdrasil; the smoke will say so if not. This is the only place
  the Linux artifact is proven on the deploy host, and only the operator holds
  the key.

## 12. Cut 7 (documentation and ledgers)

- `docs/runtime-parity-scope.md:38` TypeScript row: add "CultMesh QUIC
  realtime provider and consumer over CultLib-owned MsQuic bindings (Windows
  x64, Linux x64), byte-parity with C#, cross-process interop lanes" to
  "Claimed parity"; add "QUIC datagrams (`Unreliable`), browser QUIC" to "Not
  claimed".
- `src/GameCult.Mesh/docs/transport-planes.md:35-46`: name the TypeScript
  provider/consumer beside the .NET and Unity bodies; `:122-125`: state that
  `CULTMESH_NATIVE_EXTERNAL_ENDPOINT` now accepts any provider and is driven
  by the TypeScript interop harness.
- `docs/cultnet-transport-parity.md`: no change. It maps RUDP/TCP/LiteNetLib
  ownership; the realtime plane is a CultMesh plane, not a CultNet transport
  profile, and pretending otherwise would put QUIC into a document whose cut
  line says "Do not put UDP packet mechanics into CultMesh" the other way
  round.
- `docs/parked-features.md`: one entry, "Node Odin WebSocket rendezvous",
  pointing at `cultmesh-browser/src/index.ts:84-191` as the browser owner and
  the `ICultMeshRealtimeLookupSource` port as the seam (Q7).
- `docs/typescript-quic-realtime-target.md:5-7`: status line updated to point
  at this map.

Verification: `rg -n "CultMeshRealtimeTransports.cs" docs src/GameCult.Mesh/docs`
returns only correct paths.

## 13. Operator questions

The four rulings in the target (Q1-Q4) are closed and not reopened. These are
the forks the probes surfaced.

- **Q5. Registry names.** The binding cannot ship as prebuilt platform
  packages without `cultmesh-ts` itself being on a registry, and today every
  consumer uses `file:` paths and only `@gamecult/cultcache-ts` is published
  (P15, P16). All candidate names are unclaimed (P14). A: publish `cultnet-ts`
  and `cultmesh-ts` under their existing unscoped names (no import changes in
  any of the 11 consuming repos) and put only the new native platform packages
  under `@gamecult/cultmesh-quic-native-{win32-x64,linux-x64}`. B: scope
  everything as `@gamecult/*` now and take the import rename across consumers
  as a separate consumer task. C: no registry; ship the binaries inside the
  CultLib tarball the deploy script already extracts on Yggdrasil
  (`deploy-streampixels-preview.sh:95-99`), keeping `file:` consumption.
  **Recommended: A.** It reshapes nothing consumers use, matches the
  `cultcache-ts` precedent of scoping only what is new, and gives StreamPixels
  a versioned artifact instead of a tarball with a `.so` in it. C is the
  cheapest but leaves the deploy path owning a native binary by hand, which is
  the failure the target's "shipped through a registry" phrase was written to
  avoid.
- **Q6. Linux release builder.** A: GitHub Actions `ubuntu-latest` with
  `container: debian:13` (same OS as Yggdrasil, P8), artifact published by the
  workflow, verified on Yggdrasil by the operator smoke in cut 6. B: build on
  Yggdrasil in the deploy script (installs CMake and g++ on a live public
  host; every deploy compiles). C: build on Starfire's Docker `debian:13`
  engine (P12) and publish from the workstation. **Recommended: A.** It is a
  Linux host, it is reproducible from a tag, and it keeps compilers off
  Yggdrasil. C is kept for the development loop only.
- **Q7. Node-side Odin rendezvous.** The TypeScript consumer needs a route
  source. The browser package has an Odin WebSocket rendezvous
  (`cultmesh-browser/src/index.ts:84-191`); Node 24 has a global `WebSocket`,
  so it would run, but `cultmesh-ts` may not depend on `cultmesh-browser`
  (Q2's spirit), and the first consumer of the Node QUIC path is the interop
  harness, not a product. A: ship the lookup-source port plus a static source
  now, park the Node Odin rendezvous with a note. B: move the Odin rendezvous
  into `cultnet-ts` beside the verifier in this migration so Node and browser
  share it. **Recommended: A.** B is the right end state but is a second
  ownership move in one pipeline; it should follow once a Node consumer that
  discovers through Odin exists.

Not asked, because the target already decided them: datagrams stay out (Q4);
no TLS on TCP planes (Q1); Windows x64 and Linux x64 only (Q3); the verifier
moves to a shared browser-safe module (Q2, section 6 names the module).

## 14. Subtraction estimate

Both sides of the number, by cut, in source lines (tests included, generated
`dist*` excluded, vector JSON excluded):

| Cut | Removed or replaced | Added | Net |
| --- | ---: | ---: | ---: |
| 1 trust module | 119 (browser) | 190 (module) + 150 (tests) + 40 (C# vector test) | +261 |
| 2 codec | 0 | 130 + 120 (TS test) + 120 (C# test) | +370 |
| 3 bridge | ~200 rewritten of 440 C++; 24 CMake | ~1,000 C++ (replacing 440), 60 CMake, 70 sh, 40 README | +~700 net C++/build |
| 4 consumer | 0 | 260 (binding) + 400 (consumer/session manager) + 400 (tests) + 200 (C# interop mode) + 150 (harness) + 120 (peer script) | +1,530 |
| 5 provider | 0 | 250 (provider) + 300 (tests) + 150 (harness) + 40 (C# tests) | +740 |
| 6 packaging | 0 | 60 (package manifests) + 130 (workflow) + 30 (closure smoke) + 60 (README) | +280 |
| 7 docs | ~10 | ~40 | +30 |
| Total | ~350 | ~4,260 | about +3,900 |

Dependencies: +1 runtime dependency on `cultmesh-ts` (`koffi`, 1.7 MB, MIT,
loaded lazily); +2 optional platform packages; +0 build-time Node
dependencies (no node-gyp, no node-addon-api, no cmake-js). Targets: +0 .NET
projects (the C# interop peer gains one project reference); +1 CMake platform
branch; +1 shell build script; +3 publish jobs; +1 CI job.

Why the number is positive and still acceptable: the target orders a new
capability (a QUIC realtime plane in a runtime that has none), and every line
above either is that capability, proves it against the C# reference, or ships
it. The subtraction that was available (one verifier instead of two, one
MsQuic body instead of two, one framing implementation per language instead
of one per host) is taken in cuts 1 and 3. Anything that would have shrunk
the number further (a third-party QUIC stack, a callback-driven addon, a
tarball with a `.so`) was rejected in section 4 for reasons that are not
about line count.

## 15. Not assignable to a cut

- **The Unity package release** carrying the rebuilt bridge DLL. The v1 ABI
  is preserved, so no cut requires it, but the committed DLL will lag the
  source until the operator runs `scripts/build-unity-package.ps1` and
  commits. Recorded here so nobody reads the stale DLL as evidence the
  rewrite did not happen.
- **StreamPixels certificate provisioning** (which PKCS12, where it lives on
  Yggdrasil, how its pin reaches the Odin route). Consumer work by the target.
- **A nonce on the QUIC plane.** The target lists "a replayed nonce" among
  trust negatives, but the QUIC plane has no nonce exchange in C# either
  (`CultMeshQuicRealtimeTransport.cs` opens no session message; trust is the
  Odin route plus the TLS pin, `transport-planes.md:71-73`). Cut 4 tests the
  replay class that does exist (route generation). Adding a nonce is a
  cross-runtime protocol change and needs its own target.
- **Linux coverage of the C# native connector.** `CanConnect` is Windows-only
  (`CultMeshNativeQuicRealtimeTransport.cs:40`); the Linux interop job in cut 6
  therefore runs the TypeScript-provider-to-managed-connector lane and the
  TypeScript-to-TypeScript lanes only. The native-connector lane is Windows
  by design, matching where Unity runs.
- **The `cultcache-ts` naming split** across consumers (section 3). Out of
  scope; noted because Q5's answer should not make it worse.

## 16. Reminder of what a wrong cut looks like

If a cut touches `src/GameCult.Mesh/CultMeshTcp*.cs`, adds TLS to the TCP
schema or content connectors, adds WebTransport to `cultmesh-browser`, adds
QUIC to `cultnet-py`, `cultnet-rs`, or `cultmesh-kotlin`, or edits anything
under `F:\Projects\StreamPixels`, it has left the target's "Not in this
migration" list and is wrong. Stop and re-read this map.
