# GameCult.Mesh.Quic.Native

One MsQuic body for every CultMesh host that is not .NET: two roles (provider
and consumer), two platforms (Windows x64 and Linux x64), and no callbacks
across the host boundary.

The bridge owns MsQuic registration, configuration, listener, connection and
stream handles, TLS credential loading, the `cultmesh-state-v1` ALPN, the
1-byte-kind plus 4-byte-LE-length QUIC stream framing, and one event queue per
runtime. It owns transport and nothing else: it never decodes a CultMesh frame,
never decides delivery semantics, never coalesces, never reconnects, and in the
v2 path never decides whether to trust a certificate. Frame meaning stays in
`GameCult.Mesh` and in the TypeScript codec; trust stays with the host.

## No host callbacks

Every crossing is the host calling in, including the one blocking wait. MsQuic's
worker threads only ever push onto the queue and wake that wait, so no host
runtime is called from a thread it does not own. That is what lets one binary
serve a Node process through FFI and a Unity player through P/Invoke without
either one's threading rules leaking into the other.

It is also why nothing here includes a Node header, a Unity header, or a
JavaScript engine: the bridge does not know what is on the other side.

## Two ABIs

**v2** is the runtime-neutral queue API, and the one new hosts should use. It is
stated once, in [`include/cultmesh_quic_native.h`](include/cultmesh_quic_native.h):
the 64-byte event struct with its field order and asserted offsets, the thirteen
exports with their signatures, the id and return-code conventions, the object
lifetime rules and the threading rules. That file is the contract. This README
does not restate it, and where the two ever disagree the header is right.

A host that can include it should. A host that cannot — koffi, Unity's
P/Invoke — declares the same thing in its own FFI and is checked against the
header by a test rather than by eye.

Two things worth reading before writing a host, because they are easy to get
wrong from the outside:

- **Return codes are negative on a bad call, on every platform.** `-1` is a bad
  call, `-2` a rejected argument, `<= -1000` a MsQuic refusal. `QUIC_STATUS`
  itself is not portable — a negative HRESULT on Windows, a positive errno on
  POSIX — so it never reaches a host; `cultmesh_quic_last_status` hands over the
  raw value for diagnostics and nothing else.
- **`cultmesh_quic_runtime_close` quiesces.** It marks the runtime closing, wakes
  every blocked `cultmesh_quic_next_event`, and waits for every in-flight call to
  return before it frees anything. No host call may begin after it starts.

A client connection defers certificate validation: the bridge indicates the
certificate, emits event 3 carrying the DER, and answers MsQuic only when the
host calls `cultmesh_quic_connection_certificate_complete`. If the host never
answers, MsQuic's 10-second handshake timeout closes the connection and event 4
reports it. Providers load their certificate as PKCS12; the advertised
`cert-sha256` pin is computed by the host from that same PKCS12, not here.

**v1** is the five exports the shipped Unity package imports by name
(`cultmesh_quic_open`, `_state`, `_poll`, `_error`, `_close`). They are Windows
only, matching `CultMeshNativeQuicRealtimeTransport.CanConnect`, and are
implemented on top of a private v2 runtime holding one connection. They decide
nothing the v2 path does not, with one exception: a v1 connection carries a
SHA-256 pin that is compared here, in C, because that is the contract the
managed connector was built against. A v2 connection never carries one.

## Building

Windows x64, from the repo root:

    powershell -File scripts/build-quic-native.ps1

MsQuic comes from `Microsoft.Native.Quic.MsQuic.OpenSSL` 2.5.9, pinned twice: the
NuGet zip by digest and size, and the `msquic.dll` inside it by digest and size,
because the archive is what the download is checked against and the DLL is what
actually ships beside the bridge. The OpenSSL flavour rather than Schannel
because Schannel's MsQuic refuses `QUIC_CREDENTIAL_TYPE_CERTIFICATE_PKCS12`,
which is the only credential type a provider has on both platforms.

The build uses MSVC with `/Brepro` so that a build reproduces itself: the link
timestamps are dropped, and building the same source twice gives the same bytes.
It links the CRT statically (`/MT`), because a Node or Unity host without the
Visual C++ redistributable cannot load a DLL that imports `vcruntime140.dll`,
and the failure it reports names no missing runtime; the bridge carries its own.

The committed Unity plugin
(`unity/org.gamecult.cultlib/Runtime/Plugins/x86_64/gamecult_mesh_quic_native.dll`)
is this build: 302,080 bytes, `/MT`, OpenSSL MsQuic, no CRT import. It replaced
an older 40,448-byte build — before `/MT` and before the move off Schannel,
still importing `VCRUNTIME140.dll` and `MSVCP140.dll` — at the CultLib 1.0.60 /
Studio 1.4.0 release.

Linux x64, on a Debian 13 host:

    scripts/build-quic-native.sh

MsQuic comes from Microsoft's Debian 13 pool as a pinned `.deb`, and the POSIX
headers from the msquic source tree at the matching tag; all four downloads are
verified by SHA-256. The bridge is linked with `-fvisibility=hidden` so only the
v2 exports leave the `.so`, and with `RPATH $ORIGIN` so the `libmsquic.so.2`
shipped beside it resolves without being installed system-wide.

A Linux host still needs MsQuic's own runtime dependencies present:
`libssl3t64`, `libnuma1`, `libxdp1`, `libnl-route-3-200`.

The development loop for it is one committed image,
[`scripts/quic-native-linux-dev.Dockerfile`](../../scripts/quic-native-linux-dev.Dockerfile),
pinned to the same `debian:13` digest the release workflow builds in. It carries
the toolchain, MsQuic's runtime dependencies, and `setarch`, because a
sanitizer build of the runtime-lifetime scenarios needs `personality(ADDR_NO_RANDOMIZE)`
and a container that cannot ask for that cannot run them:

    docker build -t cultlib-quic-native-dev -f scripts/quic-native-linux-dev.Dockerfile scripts
    docker run --rm --security-opt seccomp=unconfined -v "${PWD}:/src" -w /src cultlib-quic-native-dev bash -lc "
      scripts/build-quic-native.sh
      cmake -S native/GameCult.Mesh.Quic.Native -B artifacts/quic-native-tests/linux-x64 -G Ninja \
        -DCMAKE_BUILD_TYPE=Release -DCULTMESH_QUIC_BUILD_TESTS=ON -DCULTMESH_QUIC_DEBUG_ASSERTS=ON \
        -DMSQUIC_INCLUDE_DIR=artifacts/dependencies/msquic-linux-2.5.9/include \
        -DMSQUIC_LIB_DIR=artifacts/dependencies/msquic-linux-2.5.9/package/usr/lib/x86_64-linux-gnu
      cmake --build artifacts/quic-native-tests/linux-x64
      for scenario in closerace holdclose holdtimeout polltimeout pollbusy pollhammer zerotimeout payloadfit latecall waitseam; do
        setarch -R artifacts/quic-native-tests/linux-x64/bin/cultmesh_quic_native_tests \"\${scenario}\" || exit 1
      done
    "

Both lines are run from the repository root, and the mount is that root wherever
it is: `${PWD}` in PowerShell, `$(pwd)` in a POSIX shell. `CULTMESH_QUIC_BUILD_TESTS`
is off in the shipped configuration (see the CMakeLists.txt option comment); it
turns on the scenario runner `tests/cultmesh_quic_native_tests.cpp` compiles into,
and `CULTMESH_QUIC_DEBUG_ASSERTS` turns on both the bridge's internal invariant
assertions and the four scenarios (`holdclose`, `latecall`, `holdtimeout`,
`waitseam`) that need the development seam in section 7 of the header. Each
scenario name is a positional argument to the built binary; run them individually
to bisect a failure, or all ten as shown to cover the runtime-lifetime rules.

A Windows clone made before `.gitattributes` kept shell scripts at LF still has
`scripts/build-quic-native.sh` with carriage returns, and pulling does not fix
it: the script's content did not change, so git neither rewrites it nor reports
it modified. The container then dies at once on the interpreter `bash\r`. From
the repository root, in either shell, this checks the script out again under the
current attributes:

    git rm --cached -q scripts/build-quic-native.sh; git checkout HEAD -- scripts/build-quic-native.sh

`--security-opt seccomp=unconfined` is load-bearing, not caution. A
ThreadSanitizer configuration needs the process's address space where it expects
it, so a TSan run must be re-executed under `setarch -R`, which asks the kernel
for `personality(ADDR_NO_RANDOMIZE)`. Docker's default seccomp profile denies
that call. Without it ThreadSanitizer dies before `main` with "unexpected memory
mapping" — a configuration that never starts, not a scenario that passed.

A win32-x64 build of the scenario runner needs the checkout somewhere short —
near a drive root rather than under a deep temporary directory. MSVC builds the
bridge and its scenario runner through MSBuild, whose file tracker gives out on
long paths, and the build then fails outright rather than naming the path as the
cause.

Where an artifact was built is part of what it is. The Linux binary is linked
against Debian 13's glibc, so a build from anywhere else is a different artifact
whatever the file name says.
