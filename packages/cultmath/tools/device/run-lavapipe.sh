#!/usr/bin/env bash
# The lavapipe conforming run: every fixture family, through HLSL (dxc) and GLSL (glslang), on Mesa lavapipe,
# judged by GlslMirrorTests.DeviceResultsHoldFixture. This script spells, compiles and dispatches; the C# judge
# is the only thing that compares.
#
# Run from the CultLib root inside mcr.microsoft.com/dotnet/sdk:10.0 (Ubuntu 24.04), for example
#   SUMMARY=<log> tools/stopgap/ygg-verify.sh <repo> <rev> mcr.microsoft.com/dotnet/sdk:10.0 'bash packages/cultmath/tools/device/run-lavapipe.sh'
# WRITE_BOUNDS=1 re-records packages/cultmath/tests/CultMath.Tests/fixtures/device-bounds-lavapipe.txt and prints it
# between BOUNDS-BEGIN and BOUNDS-END, because the job's clone is discarded.
set -euo pipefail
ROOT=$(pwd)
CM=$ROOT/packages/cultmath
TESTS=$CM/tests/CultMath.Tests
DEV=$CM/tools/device
W=/tmp/device
SNAPSHOT=20261016T000000Z
DXC_URL=https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2607/linux_dxc_2026_07_29.x86_x64.tar.gz
DXC_SHA=55665c87824051ed4774ff3280a79ccbbb7d39243b9736ca5e98222134112d54
mkdir -p "$W/dxc" "$W/results/lavapipe-hlsl" "$W/results/lavapipe-glsl"

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq --snapshot "$SNAPSHOT" >/dev/null
apt-get install -y -qq --no-install-recommends mesa-vulkan-drivers libvulkan-dev glslang-tools spirv-tools gcc libc6-dev curl ca-certificates python3 >/dev/null

curl -sL --fail -o "$W/dxc.tgz" "$DXC_URL"
echo "$DXC_SHA  $W/dxc.tgz" | sha256sum -c - >/dev/null || { echo "dxc tarball sha256 differs from the pinned one"; exit 1; }
tar xzf "$W/dxc.tgz" -C "$W/dxc"
DXC=$(find "$W/dxc" -name dxc -type f | head -1)
export LD_LIBRARY_PATH=$(dirname "$DXC")/../lib:$(dirname "$DXC")${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}
gcc -O2 -o "$W/vkrun" "$DEV/vkrun.c" -lvulkan

dotnet build "$TESTS" -nologo -v q >/dev/null
export CULTMATH_DEVICE_WORK=$W/work
dotnet test "$TESTS" --no-build --filter "FullyQualifiedName~GlslMirrorTests.WriteDeviceWork"
python3 "$DEV/gen.py" "$W/work" "$CM"

while IFS=$'\t' read -r tag _ _ n _ wout; do
  in=$W/work/$tag.in
  "$DXC" -T cs_6_0 -E main -HV 2021 -spirv -fspv-target-env=vulkan1.2 -I "$CM/shaders" "$W/work/$tag.hlsl" -Fo "$W/work/$tag.h.spv" >"$W/work/$tag.h.log" 2>&1 \
    || { echo "dxc failed for $tag"; head -5 "$W/work/$tag.h.log"; exit 1; }
  glslangValidator -V --target-env vulkan1.2 -o "$W/work/$tag.g.spv" "$W/work/$tag.comp" >"$W/work/$tag.g.log" 2>&1 \
    || { echo "glslang failed for $tag"; head -5 "$W/work/$tag.g.log"; exit 1; }
  "$W/vkrun" "$W/work/$tag.h.spv" "$in" "$W/results/lavapipe-hlsl/$tag.out" "$n" "$wout" >>"$W/device.txt"
  "$W/vkrun" "$W/work/$tag.g.spv" "$in" "$W/results/lavapipe-glsl/$tag.out" "$n" "$wout" >>"$W/device.txt"
done <"$W/work/manifest.txt"

RENDERER=$(sort -u "$W/device.txt")
[ "$(echo "$RENDERER" | wc -l)" = 1 ] || { echo "the runs saw more than one device: $RENDERER"; exit 1; }
export CULTMATH_DEVICE_NAME=${RENDERER#device=}
export CULTMATH_DEVICE_IDENTITY="$(dpkg-query -W -f='${Package} ${Version}; ' mesa-vulkan-drivers glslang-tools)dxc $("$DXC" --version | head -1); apt snapshot $SNAPSHOT"
export CULTMATH_DEVICE_RESULTS=$W/results
echo "device: $CULTMATH_DEVICE_NAME"
echo "identity: $CULTMATH_DEVICE_IDENTITY"

if [ "${WRITE_BOUNDS:-0}" = 1 ]; then
  export CULTMATH_WRITE_DEVICE_BOUNDS=1
  trap 'echo BOUNDS-BEGIN; cat "$TESTS/fixtures/device-bounds-lavapipe.txt"; echo BOUNDS-END' EXIT
fi
dotnet test "$TESTS" --no-build --filter "FullyQualifiedName~GlslMirrorTests.DeviceResultsHoldFixture" --logger "console;verbosity=detailed"
