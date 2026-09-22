# The linux-x64 development loop for the CultMesh native QUIC bridge: build it
# and run its runtime-lifetime scenarios directly. Not a release builder — the
# shipped Linux artifact comes from the workflow (see
# docs/typescript-quic-realtime-cut.md).
#
# See native/GameCult.Mesh.Quic.Native/README.md for the build and run commands
# (CULTMESH_QUIC_BUILD_TESTS, CULTMESH_QUIC_DEBUG_ASSERTS, and the scenario
# names the built binary takes as its first argument).
#
#   docker build -t cultlib-quic-native-dev -f scripts/quic-native-linux-dev.Dockerfile scripts
#
# From the repository root; the mount for `docker run` is that root wherever it
# is: `${PWD}` in PowerShell, `$(pwd)` in a POSIX shell.
#
# `--security-opt seccomp=unconfined` is not optional and not caution. A
# ThreadSanitizer configuration needs the process's address space where it
# expects it, so a TSan run is re-executed under `setarch -R`, which asks the
# kernel for personality(ADDR_NO_RANDOMIZE). Docker's default seccomp profile
# denies that call, and without it ThreadSanitizer dies before main with
# "unexpected memory mapping" — a configuration that never starts, not a
# scenario that passed.
#
# Pinned by digest, not by the moving tag: the artifact is the toolchain that
# made it.
FROM debian:13@sha256:f324c7ff54321e8d9c588493a20244965938ce0aa50bbd1022d38010e9ffc4b1

# cmake, ninja and g++ build the bridge and its scenario runner; curl,
# ca-certificates and dpkg fetch and unpack the pinned libmsquic deb;
# util-linux carries setarch, needed to re-execute the TSan scenarios under
# ADDR_NO_RANDOMIZE. The four libraries are MsQuic's own runtime dependencies,
# which the bridge does not vendor.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      bash ca-certificates cmake curl dpkg g++ ninja-build util-linux \
      libssl3t64 libnuma1 libxdp1 libnl-route-3-200 \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src
