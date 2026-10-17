#!/usr/bin/env bash
# Probe for shader-weighted-lattice-loop: the prototype through the dxc + SPIRV-Cross build and on lavapipe, against C#.
# Run from a CultLib clone at a revision with packages/cultmath/tools/glsl/build-glsl.sh (hands/cultmath-glsl-build-dxc
# 23ac4e89), inside mcr.microsoft.com/dotnet/sdk:10.0, with this directory at $P. The clone is disposable: the
# prototype is appended to its CultMath.hlsl, and two build-tool lines are patched (array arguments, int constants).
set -euo pipefail
P=${P:-/tmp/probe}; ROOT=$(pwd); CM=$ROOT/packages/cultmath; W=/tmp/pw; mkdir -p $W
apt-get update -qq >/dev/null && apt-get install -y -qq --no-install-recommends python3 >/dev/null
cat $P/prototype.hlsl >> $CM/shaders/CultMath.hlsl
python3 - $CM/tools/glsl/wrappers.py <<'PY'
import sys; f = sys.argv[1]; t = open(f).read()
t = t.replace(r"args = [re.split(r'\s+', p)[-1] for p in params]", r"args = [re.split(r'\s+', re.sub(r'\[.*\]', '', p))[-1] for p in params]")
open(f, 'w').write(t)
PY
grep -c "re.sub(r'" $CM/tools/glsl/wrappers.py
python3 - $CM/tools/glsl/partition.py <<'PY'
import sys; f = sys.argv[1]; t = open(f).read()
t = t.replace(r"r'^static const float (CULTMATH_\w+) = (-?\d+(?:\.\d*)?(?:[eE][+-]?\d+)?);$'", r"r'^static const (float|int) (CULTMATH_\w+) = (-?\d+(?:\.\d*)?(?:[eE][+-]?\d+)?);$'")
t = t.replace("consts[o].append('const float %s = %s;' % m.groups())", "consts[o].append('const %s %s = %s;' % m.groups())")
open(f, 'w').write(t)
PY
grep -c "(float|int)" $CM/tools/glsl/partition.py
echo "== build"
bash $CM/tools/glsl/build-glsl.sh $W/built 2>&1 | tail -12
echo "== array functions in the built core.glsl"
grep -nE "^(const int CULTMATH_MAX_SITES|vec4 cultmath_power_border\(|float cultmath_lattice_cell_distance\(|vec4 cultmath_weighted_lattice_cells\()" $W/built/core.glsl
echo "== ES 3.00 fragment consumer passing a uniform array"
{ printf '#version 300 es\nprecision highp float;\nprecision highp int;\n'; cat $W/built/core.glsl
  printf 'uniform vec4 zone[CULTMATH_MAX_SITES];\nuniform int zoneCount;\nout highp vec4 c;\nvoid main() { c = cultmath_weighted_lattice_cells(gl_FragCoord.xy, 1.0, zone, zoneCount, 260.0, 0.7, 1.6, 120.0, 0.35, vec2(0.6, 0.8), 1.5); }\n'; } > $W/consumer.frag
glslangValidator $W/consumer.frag && echo "consumer ES 3.00 ok"
echo "== lavapipe"
apt-get install -y -qq --no-install-recommends mesa-vulkan-drivers libvulkan-dev gcc libc6-dev >/dev/null
gcc -O2 -o $W/vkrun $CM/tools/device/vkrun.c -lvulkan
cp -r $P/ref $W/ref && sed -i 's#../../../../packages#'"$CM"'/..#' $W/ref/Ref.csproj
N=$(dotnet run --project $W/ref -c Release -- $W/in.bin $W/ref.bin | tail -1); echo "cases $N"
DXC=$(find $CM/.tools/glsl/dxc -name dxc -type f | head -1); export LD_LIBRARY_PATH=$(dirname $DXC)/../lib:$(dirname $DXC)
{ printf '#version 310 es\nprecision highp float;\nprecision highp int;\n#define NCASES %su\n' $N; cat $W/built/core.glsl $P/entry.comp.tail; } > $W/e.comp
glslangValidator -V --target-env vulkan1.2 -o $W/g.spv $W/e.comp >$W/g.log || { head $W/g.log; exit 1; }
$W/vkrun $W/g.spv $W/in.bin $W/g.out $N 4
python3 $P/judge.py $W/ref.bin $W/g.out built-glsl
run_hlsl() {  # $1 label, $2 sed expression applied to a copy of the shaders
  rm -rf $W/s && cp -r $CM/shaders $W/s && sed -i "$2" $W/s/CultMath.hlsl
  $DXC -T cs_6_0 -E main -HV 2021 -spirv -fspv-target-env=vulkan1.2 -D NCASES=${N}u -I $W/s $P/entry.hlsl -Fo $W/h.spv >$W/h.log 2>&1 || { head -5 $W/h.log; exit 1; }
  $W/vkrun $W/h.spv $W/in.bin $W/h.out $N 4 >/dev/null
  python3 $P/judge.py $W/ref.bin $W/h.out "$1"
}
run_hlsl hlsl 's/^$//'
cmp $W/h.out $W/g.out && echo "hlsl equals built glsl bit for bit" || echo "hlsl and built glsl differ (plain form; the gate is strict form)"
run_hlsl "M1 exit at 0.5k" 's/0\.5 \* (k - 1) >= max/0.5 * k >= max/'
run_hlsl "M2 exit ignores the smooth band" 's/>= max(f2, f1 + 2\.0 \* smoothing))$/>= f2)/'
run_hlsl "M3 normaliser drops the pre-scale" 's/normal = stretchNormal \/ scale;/normal = stretchNormal;/'
run_hlsl "M4 normaliser drops the length" 's/normal = normal \/ length(normal);//'
run_hlsl "M5 smooth step loses its cap" 's/return min(next, f1 + smoothing);/return next;/'
run_hlsl "M6 box pass skips its first row" 's/for (int dz = -reach; dz <= reach; dz++)/for (int dz = -reach + 1; dz <= reach; dz++)/'
run_hlsl "M7 late exit (equivalent: window-independent)" 's/0\.5 \* (k - 1) >= max/0.5 * (k - 2) >= max/'
