# The linux-x64 development loop for the CultMesh native QUIC bridge: build it,
# run its scenarios, and run the mutation harness over it. Not a release
# builder — the shipped Linux artifact comes from the workflow (see
# docs/typescript-quic-realtime-cut.md).
#
# This image is what the harness's linux-x64 entries are honest about. Before it
# was committed, the recipe in the cut map installed a C++ toolchain and no
# JavaScript runtime, so the container the bridge was built in could not run the
# runner that mutates it at all.
#
#   docker build -t cultlib-quic-native-dev -f scripts/quic-native-linux-dev.Dockerfile scripts
#   docker run --rm --security-opt seccomp=unconfined -v F:\Projects\CultLib:/src -w /src \
#       cultlib-quic-native-dev bash -lc "scripts/build-quic-native.sh && node scripts/mutate-cultmesh.mjs native"
#
# `--security-opt seccomp=unconfined` is not optional and not caution. The
# ThreadSanitizer configuration needs the process's address space where it
# expects it, so the harness re-executes it under `setarch -R`, which asks the
# kernel for personality(ADDR_NO_RANDOMIZE). Docker's default seccomp profile
# denies that call, and without it ThreadSanitizer dies before main with
# "unexpected memory mapping" — a whole configuration failing to start, which a
# mutation harness reads as every mutant being killed.
#
# Pinned by digest, not by the moving tag: the artifact is the toolchain that
# made it.
FROM debian:13@sha256:f324c7ff54321e8d9c588493a20244965938ce0aa50bbd1022d38010e9ffc4b1

# cmake, ninja and g++ build the bridge and its scenario runner; curl,
# ca-certificates and dpkg fetch and unpack the pinned libmsquic deb;
# util-linux carries setarch; nodejs runs scripts/mutate-cultmesh.mjs. The four
# libraries are MsQuic's own runtime dependencies, which the bridge does not
# vendor.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      bash ca-certificates cmake curl dpkg g++ ninja-build nodejs util-linux \
      libssl3t64 libnuma1 libxdp1 libnl-route-3-200 \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src
