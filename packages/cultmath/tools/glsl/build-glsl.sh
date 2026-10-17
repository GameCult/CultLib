#!/usr/bin/env bash
# Builds the library GLSL from the HLSL: dxc lib_6_3 -spirv -Od, one SPIR-V edit, a fixed spirv-opt pair, SPIRV-Cross,
# partition. Linux, run from the repository root:  build-glsl.sh <outdir>
# Writes core.glsl, cycles.glsl and phacelle.glsl bodies into <outdir>; the committed GLSL is untouched.
# Tools are cached in GLSL_TOOLS (default packages/cultmath/.tools/glsl, gitignored): the pinned dxc tarball and
# SPIRV-Cross, SPIRV-Tools, SPIRV-Headers at TAG built with cmake.
set -euo pipefail
OUT=$(realpath -m "${1:?usage: build-glsl.sh <outdir>}")
ROOT=$(pwd)
CM=$ROOT/packages/cultmath
G=$CM/tools/glsl
TOOLS=$(realpath -m "${GLSL_TOOLS:-$CM/.tools/glsl}")
TAG=vulkan-sdk-1.4.363.0
DXC_URL=https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2607/linux_dxc_2026_07_29.x86_x64.tar.gz
DXC_SHA=55665c87824051ed4774ff3280a79ccbbb7d39243b9736ca5e98222134112d54
W=$(mktemp -d)
mkdir -p "$TOOLS" "$OUT"

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq --no-install-recommends ca-certificates curl git cmake g++ make python3 glslang-tools >/dev/null

if [ ! -x "$TOOLS/dxc.ok" ]; then
  curl -sL --fail -o "$TOOLS/dxc.tgz" "$DXC_URL"
  echo "$DXC_SHA  $TOOLS/dxc.tgz" | sha256sum -c - >/dev/null || { echo "dxc tarball sha256 differs from the pinned one"; rm -f "$TOOLS/dxc.tgz"; exit 1; }
  rm -rf "$TOOLS/dxc"; mkdir -p "$TOOLS/dxc"; tar xzf "$TOOLS/dxc.tgz" -C "$TOOLS/dxc"; : >"$TOOLS/dxc.ok"
fi
DXC=$(find "$TOOLS/dxc" -name dxc -type f | head -1)
export LD_LIBRARY_PATH=$(dirname "$DXC")/../lib:$(dirname "$DXC")${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}

if [ ! -x "$TOOLS/bin/spirv-cross" ]; then
  git clone -q --depth 1 --branch $TAG https://github.com/KhronosGroup/SPIRV-Cross.git "$TOOLS/sc-src"
  cmake -S "$TOOLS/sc-src" -B "$TOOLS/sc-build" -DCMAKE_BUILD_TYPE=Release -DSPIRV_CROSS_ENABLE_TESTS=OFF -DSPIRV_CROSS_CLI=ON >/dev/null
  cmake --build "$TOOLS/sc-build" --target spirv-cross -j8 >/dev/null
  mkdir -p "$TOOLS/bin"; cp "$TOOLS/sc-build/spirv-cross" "$TOOLS/bin/"
fi
if [ ! -x "$TOOLS/bin/spirv-opt" ]; then
  git clone -q --depth 1 --branch $TAG https://github.com/KhronosGroup/SPIRV-Tools.git "$TOOLS/st-src"
  git clone -q --depth 1 --branch $TAG https://github.com/KhronosGroup/SPIRV-Headers.git "$TOOLS/st-src/external/spirv-headers"
  cmake -S "$TOOLS/st-src" -B "$TOOLS/st-build" -DCMAKE_BUILD_TYPE=Release -DSPIRV_SKIP_TESTS=ON -DSPIRV_WERROR=OFF >/dev/null
  cmake --build "$TOOLS/st-build" --target spirv-opt spirv-val -j8 >/dev/null
  mkdir -p "$TOOLS/bin"; cp "$TOOLS/st-build/tools/spirv-opt" "$TOOLS/st-build/tools/spirv-val" "$TOOLS/bin/"
fi
export PATH=$TOOLS/bin:$PATH
echo "dxc: $("$DXC" --version | head -1)"; echo "spirv-opt: $(spirv-opt --version | head -1)"

python3 "$G/wrappers.py" "$CM/shaders" "$W/shw"
"$DXC" -T lib_6_3 -HV 2021 -spirv -fspv-target-env=universal1.5 -Od -I "$W/shw" "$W/shw/cm_wrappers.hlsl" -Fo "$W/lib.spv" >"$W/dxc.log" 2>&1 || { head -20 "$W/dxc.log"; exit 1; }
python3 "$G/spvedit.py" "$W/lib.spv" "$W/edited.spv" "$W/shw/wrappers.tsv"
spirv-val --target-env spv1.5 "$W/edited.spv"
spirv-opt --target-env=spv1.5 --eliminate-local-single-store --eliminate-dead-code-aggressive "$W/edited.spv" -o "$W/opt.spv"
spirv-val --target-env spv1.5 "$W/opt.spv"
spirv-cross "$W/opt.spv" --es --version 310 --relax-nan-checks --output "$W/cross.glsl"
python3 "$G/partition.py" "$CM/shaders" "$W/cross.glsl" "$OUT"

# Consumer checks: glslang ES 3.00 fragment compiles of every include combination, and a consumer calling overloads
# with an r-value argument.
CONSUMER='out highp vec4 c;
void main() { vec3 p = gl_FragCoord.xyz; c = vec4(cultmath_lerp(p, p.yzx, vec3(0.5)), cultmath_lerp(1.0, 2.0, p.x)) + cultmath_saturate(vec4(p, 1.0)) + vec4(cultmath_smootherstep(gl_FragCoord.x + 1.0)); }'
for combo in "core" "core cycles" "core phacelle" "core cycles phacelle"; do
  f=$W/check.frag; printf '#version 300 es\nprecision highp float;\nprecision highp int;\n' >"$f"
  for part in $combo; do cat "$OUT/$part.glsl" >>"$f"; done
  printf '%s\n' "$CONSUMER" >>"$f"
  glslangValidator "$f" >"$W/check.log" 2>&1 || { echo "ES 3.00 compile failed for [$combo]"; head -8 "$W/check.log"; exit 1; }
  echo "ES 3.00 ok: $combo"
done

# Capacity checks: a consumer's prior #define CULTMATH_MAX_SITES changes the array type through dxc, the committed GLSL
# and the built GLSL, and 128 still works with no define.
printf '#define CULTMATH_MAX_SITES 4\n#include "CultMath.hlsl"\nfloat4 cap4(float3 p, float4 s[4]) { return cultmath_power_border(p, s, 4); }\n' >"$W/shw/cap4.hlsl"
"$DXC" -T lib_6_3 -HV 2021 -Od -I "$W/shw" "$W/shw/cap4.hlsl" -Fo "$W/cap4.dxil" >"$W/cap4.log" 2>&1 || { echo "capacity (a) failed: dxc with CULTMATH_MAX_SITES 4"; head -8 "$W/cap4.log"; exit 1; }
echo "capacity (a) ok: dxc lib_6_3 with a prior define 4 takes float4 s[4]"
cap_check() {   # label, expected (ok|fail), define lines, GLSL library file, array length
  local f=$W/cap.frag
  printf '#version 300 es\nprecision highp float;\nprecision highp int;\n%s\n' "$3" >"$f"; cat "$4" >>"$f"
  printf 'out highp vec4 c;\nvoid main() { vec4 s[%s]; for (int i = 0; i < %s; i++) s[i] = vec4(float(i)); c = cultmath_power_border(gl_FragCoord.xyz, s, 4); }\n' "$5" "$5" >>"$f"
  local got=fail; glslangValidator "$f" >"$W/cap.log" 2>&1 && got=ok
  [ "$got" = "$2" ] || { echo "capacity $1 failed: expected $2, got $got"; head -6 "$W/cap.log"; exit 1; }
  echo "capacity $1 ok: $2"
}
D4='#define CULTMATH_MAX_SITES 4'
cap_check "(b) built, define 4, vec4 s[4]" ok "$D4" "$OUT/core.glsl" 4
cap_check "(c) built, define 4, vec4 s[128]" fail "$D4" "$OUT/core.glsl" 128
cap_check "(d) built, no define, vec4 s[128]" ok "" "$OUT/core.glsl" 128
cap_check "(e) committed, define 4, vec4 s[4]" ok "$D4" "$CM/shaders/CultMath.glsl" 4
cap_check "(e) committed, define 4, vec4 s[128]" fail "$D4" "$CM/shaders/CultMath.glsl" 128

for part in core cycles phacelle; do
  g=$OUT/$part.glsl
  fn=$(grep -cE '^[A-Za-z_]\w* [A-Za-z_]\w*\(.*\)$' "$g" || true)
  names=$(grep -oE '^[A-Za-z_]\w* [A-Za-z_]\w*\(' "$g" | awk '{print $2}' | sort -u | wc -l)
  echo "$part: functions $fn, distinct names $names, lines $(wc -l <"$g"), bytes $(wc -c <"$g"), sha256 $(sha256sum "$g" | cut -c1-64)"
done
echo "suffixed ids: $(cat "$OUT"/*.glsl | grep -oE '\bcultmath_\w+_[0-9]+\(' | sort -u | wc -l); inout: $(cat "$OUT"/*.glsl | grep -c inout || true)"
# Build shape: the lowering leaves none of these, and the NaN-check option leaves no mix(mix( expansions.
SHAPE='inout|param_var_|cmw[0-9]{3}_|export |mix\(mix\(|\[128\]|\b_cos\b|\b_sin\b'
if cat "$OUT"/*.glsl | grep -Eq "$SHAPE"; then
  echo "build shape failed:"; cat "$OUT"/*.glsl | grep -Eo "$SHAPE" | sort | uniq -c; exit 1
fi
echo "build shape ok"
