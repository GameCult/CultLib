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

pipeline() {   # shaders dir, work dir, out dir: HLSL to partitioned GLSL; returns 1 at the first refusal
  local sh=$1 w=$2 out=$3
  mkdir -p "$w" "$out"
  python3 "$G/wrappers.py" "$sh" "$w/shw" || return 1
  "$DXC" -T lib_6_3 -HV 2021 -spirv -fspv-target-env=universal1.5 -Od -I "$w/shw" "$w/shw/cm_wrappers.hlsl" -Fo "$w/lib.spv" >"$w/dxc.log" 2>&1 || { head -20 "$w/dxc.log"; return 1; }
  python3 "$G/spvedit.py" "$w/lib.spv" "$w/edited.spv" "$w/shw/wrappers.tsv" "$w/shw/capacity.tsv" || return 1
  spirv-val --target-env spv1.5 "$w/edited.spv" || return 1
  spirv-opt --target-env=spv1.5 --eliminate-local-single-store --eliminate-dead-code-aggressive "$w/edited.spv" -o "$w/opt.spv" || return 1
  spirv-val --target-env spv1.5 "$w/opt.spv" || return 1
  spirv-cross "$w/opt.spv" --es --version 310 --relax-nan-checks --output "$w/cross.glsl" || return 1
  python3 "$G/partition.py" "$sh" "$w/cross.glsl" "$out" || return 1
}
pipeline "$CM/shaders" "$W" "$OUT" || exit 1

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
consume() {   # label, expected (ok|fail), define lines, GLSL library file, main body
  local f=$W/cap.frag
  printf '#version 300 es
precision highp float;
precision highp int;
%s
' "$3" >"$f"; cat "$4" >>"$f"
  printf 'out highp vec4 c;
void main() { %s }
' "$5" >>"$f"
  local got=fail; glslangValidator "$f" >"$W/cap.log" 2>&1 && got=ok
  [ "$got" = "$2" ] || { echo "capacity $1 failed: expected $2, got $got"; head -6 "$W/cap.log"; exit 1; }
  echo "capacity $1 ok: $2"
}
cap_check() {   # label, expected, define lines, GLSL library file, site array length
  consume "$1" "$2" "$3" "$4" "vec4 s[$5]; for (int i = 0; i < $5; i++) s[i] = vec4(float(i)); c = cultmath_power_border(gl_FragCoord.xyz, s, 4);"
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
# Build shape: the lowering leaves none of these, and the NaN-check option leaves no mix(mix( expansions. A literal
# [128] is not among them: the capacity is selected by name, so a library array of length 128 is legitimate output.
SHAPE='inout|param_var_|cmw[0-9]{3}_|export |mix\(mix\(|\b_cos\b|\b_sin\b'
shape_check() {   # output directory; returns 1 when the shape fails
  if cat "$1"/*.glsl | grep -Eq "$SHAPE"; then
    echo "build shape failed:"; cat "$1"/*.glsl | grep -Eo "$SHAPE" | sort | uniq -c; return 1
  fi
}
shape_check "$OUT" || exit 1
echo "build shape ok"

# Tool refusals, and the capacity's reach: each variant appends one probe function to a scratch copy of CultMath.hlsl and
# runs the whole pipeline on it.
variant() {   # label, probe HLSL; leaves the variant directory in VD
  VD=$W/v/$1; rm -rf "$VD"; mkdir -p "$VD"; cp -r "$CM/shaders" "$VD/sh"
  printf '
%s
' "$2" >>"$VD/sh/CultMath.hlsl"
  pipeline "$VD/sh" "$VD/w" "$VD/out" >"$VD/log" 2>&1
}
expect_refused() {   # label, probe HLSL, pattern the refusal prints
  if variant "$1" "$2"; then echo "refusal $1 failed: the build passed"; exit 1; fi
  grep -q "$3" "$VD/log" || { echo "refusal $1 failed: no '$3' in the log"; tail -5 "$VD/log"; exit 1; }
  echo "refusal $1 ok"
}
SENTINEL=$(cut -f2 "$W/shw/capacity.tsv")
expect_refused macro-in-expression 'int cultmath_zz_probe(int n) { return n + CULTMATH_MAX_SITES; }' 'outside its guard'
# the sentinel spelled in hex: wrappers.py's scan reads decimal tokens, so only the length check can see it
expect_refused length-in-expression "uint cultmath_zz_probe(uint n) { return n + $(printf '0x%xu' "$SENTINEL"); }" 'also used by opcode'
# the sentinel as another element type's array length: not a capacity parameter type
expect_refused other-element-same-length "float3 cultmath_zz_probe(float3 x[$(printf '0x%x' "$SENTINEL")], int n) { return x[n]; }" 'not a capacity parameter type'
variant array-128 'float4 cultmath_zz_probe(float4 x[128], int n) { return x[n]; }' || { echo "array-128 failed: a library array of length 128 broke the build"; tail -5 "$VD/log"; exit 1; }
grep -Eq 'cultmath_zz_probe\(.*\[128\]' "$VD/out/core.glsl" || { echo "array-128 failed: the 128 array is not literal in core.glsl"; exit 1; }
shape_check "$VD/out" || { echo "array-128 failed: the shape check refused a library array of length 128"; exit 1; }
PROBE='vec4 x[128]; c = cultmath_zz_probe(x, 1);'
consume "(f) non-site array, define 4, vec4 x[128]" ok "$D4" "$VD/out/core.glsl" "$PROBE"
consume "(f) non-site array, define 4, vec4 x[4]" fail "$D4" "$VD/out/core.glsl" 'vec4 x[4]; c = cultmath_zz_probe(x, 1);'
consume "(g) consumer's own SPIRV_CROSS_CONSTANT_ID_0" ok '#define SPIRV_CROSS_CONSTANT_ID_0 7u' "$OUT/core.glsl" 'c = vec4(float(cultmath_max_sites));'
