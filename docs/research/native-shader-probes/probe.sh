#!/usr/bin/env bash
# Probe job, runs in ubuntu:24.04 with /src = CultLib at the probed revision. Q1 library path, Q2 lavapipe runs.
set -u
P=/tmp/p; W=/tmp/w; mkdir -p $W/fam $W/tools; cd $W
step() { echo; echo "=== $*"; }
step apt
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null && apt-get install -y -qq --no-install-recommends ca-certificates curl git cmake g++ make python3 \
  mesa-vulkan-drivers libvulkan-dev vulkan-tools glslang-tools spirv-tools >/dev/null 2>&1; echo "apt exit $?"
dpkg-query -W -f='${Package} ${Version}\n' mesa-vulkan-drivers glslang-tools spirv-tools libvulkan-dev
step dxc
url=$(curl -s https://api.github.com/repos/microsoft/DirectXShaderCompiler/releases/tags/v1.9.2607 | python3 -c "import json,sys;print([a['browser_download_url'] for a in json.load(sys.stdin)['assets'] if 'linux' in a['name'] and a['name'].endswith('.tar.gz')][0])")
echo "$url"; curl -sL --fail -o dxc.tgz "$url" && mkdir -p tools/dxc && tar xzf dxc.tgz -C tools/dxc; DXC=$(find tools/dxc -name dxc -type f | head -1); echo "DXC=$DXC"; $DXC --version 2>&1 | head -2
step spirv-cross
git clone -q --depth 1 https://github.com/KhronosGroup/SPIRV-Cross.git tools/sc && git -C tools/sc log -1 --format='SPIRV-Cross %H %cs' && \
  cmake -S tools/sc -B tools/scb -DCMAKE_BUILD_TYPE=Release -DSPIRV_CROSS_ENABLE_TESTS=OFF >/dev/null && cmake --build tools/scb --target spirv-cross -j4 >/dev/null 2>&1; SC=tools/scb/spirv-cross; ls -la $SC | cut -c1-120
SH=/src/packages/cultmath/shaders
step "Q1a dxc library SPIR-V, no wrapper (lib_6_6, vulkan1.2)"
$DXC -T lib_6_6 -HV 2021 -spirv -fspv-target-env=vulkan1.2 -I $SH $SH/CultMath.hlsl -Fo lib.spv 2>&1 | head -8; echo "dxc exit ${PIPESTATUS[0]}"
[ -f lib.spv ] && { spirv-dis lib.spv -o lib.spvasm; echo "functions: $(grep -c ' = OpFunction ' lib.spvasm) exports: $(grep -c 'LinkageAttributes.*Export' lib.spvasm) entrypoints: $(grep -c OpEntryPoint lib.spvasm)"; }
step "Q1b dxc library with 'export' added to every top-level cultmath_ definition"
python3 - "$SH" <<'PY'
import re,sys,os,shutil
src=sys.argv[1]; dst='/tmp/w/shx'; shutil.copytree(src,dst,dirs_exist_ok=True)
n=0
for fn in os.listdir(dst):
    if not fn.endswith('.hlsl'): continue
    t=open(os.path.join(dst,fn)).read()
    t2,k=re.subn(r'(?m)^(?!static|export)(\w+(?:\dx\d)?\s+cultmath_\w+\s*\()', r'export \1', t); n+=k
    open(os.path.join(dst,fn),'w').write(t2)
print('export keywords added', n)
PY
for v in "universal1.5" "vulkan1.2 -Vd" "vulkan1.3 -Vd"; do echo "--- variant: -fspv-target-env=$v"; rm -f libx.spv
  $DXC -T lib_6_6 -HV 2021 -spirv -fspv-target-env=$v -I shx shx/CultMath.hlsl -Fo libx.spv 2>&1 | head -4; echo "dxc exit ${PIPESTATUS[0]}"
  [ -s libx.spv ] && { spirv-dis libx.spv -o libx.spvasm; echo "functions: $(grep -c ' = OpFunction ' libx.spvasm) exports: $(grep -c 'LinkageAttributes.*Export' libx.spvasm) OpName cultmath_: $(grep -c 'OpName.*"cultmath_' libx.spvasm) Fma: $(grep -c ' Fma ' libx.spvasm) NoContraction: $(grep -c NoContraction libx.spvasm)"; spirv-val --target-env vulkan1.2 libx.spv 2>&1 | head -2; break; }
done
LIB=libx.spv
step "Q1c SPIRV-Cross ES output of $LIB"
for v in 300 310; do $SC $LIB --es --version $v --output lib$v.glsl 2>&1 | head -3; echo "spirv-cross --es --version $v exit ${PIPESTATUS[0]}"; done
for v in 300 310; do [ -s lib$v.glsl ] || continue
  echo "lib$v.glsl: $(wc -l < lib$v.glsl) lines; defs named cultmath_: $(grep -cE '^\w+ cultmath_\w+\(' lib$v.glsl); distinct names: $(grep -oE '^\w+ cultmath_\w+\(' lib$v.glsl | sed -E 's/^\w+ //' | sort -u | wc -l); suffixed _N names: $(grep -oE '^\w+ cultmath_\w+_[0-9]+\(' lib$v.glsl | wc -l); _NNN temporaries: $(grep -oE '\b_[0-9]+\b' lib$v.glsl | wc -l); precision lines: $(grep -c precision lib$v.glsl); main: $(grep -c 'void main' lib$v.glsl)"; done
echo "committed CultMath.glsl: $(wc -l < $SH/CultMath.glsl) lines; defs $(grep -cE '^\w+ cultmath_\w+\(' $SH/CultMath.glsl); distinct names $(grep -oE '^\w+ cultmath_\w+\(' $SH/CultMath.glsl | sed -E 's/^\w+ //' | sort -u | wc -l)"
F=$(ls lib310.glsl lib300.glsl 2>/dev/null | head -1); [ -n "$F" ] && { echo "--- head of $F"; head -12 $F; echo "--- cultmath_snoise(vec3) as emitted (first 25 lines)"; awk '/^float cultmath_snoise\(/{p=1} p{print; if(++n>=25) exit}' $F; }
for v in 300 310; do [ -s lib$v.glsl ] && { glslangValidator -S comp lib$v.glsl >gv$v.txt 2>&1; echo "glslang -S comp lib$v.glsl exit $? : $(head -3 gv$v.txt | tr '\n' ' ' | cut -c1-300)"; }; done
step "Q2 lavapipe runner"
gcc -O2 -o vkrun $P/vkrun.c -lvulkan && echo "vkrun built"
python3 $P/fam.py gen /src/packages/cultmath/tests/CultMath.Tests/fixtures/glsl-parity.json fam
cat $SH/CultMath.glsl $SH/CultMath.Cycles.glsl $SH/CultMath.Phacelle.glsl > all.glsl
python3 - <<'PY'
import json; print('families', len(json.load(open('/tmp/w/fam/manifest.json'))))
PY
ok_h=0; ok_g=0; fail=0; first=1
for h in fam/*.hlsl; do t=${h%.hlsl}; tag=$(basename $t)
  n=$(python3 -c "import json;m={x['tag']:x for x in json.load(open('fam/manifest.json'))}['$tag'];print(m['n'],m['nout'])")
  if $DXC -T cs_6_0 -E main -HV 2021 -spirv -fspv-target-env=vulkan1.2 -I $SH $h -Fo $t.h.spv >$t.h.log 2>&1; then
    if [ $first = 1 ]; then VKRUN_INFO=1 ./vkrun $t.h.spv $t.in $t.hlsl.out $n; first=0; else ./vkrun $t.h.spv $t.in $t.hlsl.out $n; fi && ok_h=$((ok_h+1)) || echo "run fail hlsl $tag"
  else fail=$((fail+1)); echo "dxc fail $tag: $(head -2 $t.h.log | tr '\n' ' ' | cut -c1-200)"; fi
  { printf '#version 310 es\nprecision highp float;\nprecision highp int;\n'; cat all.glsl $t.body.glsl; } > $t.comp
  if glslangValidator -V --target-env vulkan1.2 -o $t.g.spv $t.comp >$t.g.log 2>&1; then ./vkrun $t.g.spv $t.in $t.glsl.out $n && ok_g=$((ok_g+1)) || echo "run fail glsl $tag"
  else echo "glslang fail $tag: $(grep -m2 ERROR $t.g.log | tr '\n' ' ' | cut -c1-200)"; fi
done
echo "ran: hlsl->dxc->lavapipe $ok_h, committed glsl->glslang->lavapipe $ok_g, dxc compile failures $fail"
echo "dxc-compute Fma total: $(for s in fam/*.h.spv; do spirv-dis $s | grep -c ' Fma '; done | python3 -c "import sys;print(sum(int(l) for l in sys.stdin))")"
python3 $P/fam.py cmp /src/packages/cultmath/tests/CultMath.Tests/fixtures/glsl-parity.json fam hlsl
python3 $P/fam.py cmp /src/packages/cultmath/tests/CultMath.Tests/fixtures/glsl-parity.json fam glsl
python3 - <<'PY2'
import os,filecmp
fs=sorted(f[:-9] for f in os.listdir('/tmp/w/fam') if f.endswith('.hlsl.out'))
same=sum(filecmp.cmp('/tmp/w/fam/%s.hlsl.out'%f,'/tmp/w/fam/%s.glsl.out'%f,shallow=False) for f in fs if os.path.exists('/tmp/w/fam/%s.glsl.out'%f))
print('lavapipe: HLSL-path and GLSL-path outputs bit-identical in',same,'of',len(fs),'families')
PY2
t0=$(date +%s); for h in fam/*.h.spv; do t=${h%.h.spv}; n=$(python3 -c "import json;m={x['tag']:x for x in json.load(open('fam/manifest.json'))}['$(basename $t)'];print(m['n'],m['nout'])"); ./vkrun $h $t.in $t.rerun.out $n; cmp -s $t.rerun.out $t.hlsl.out || echo "NONDETERMINISTIC $(basename $t)"; done; echo "rerun of 105 SPIR-V modules on lavapipe: $(( $(date +%s) - t0 ))s, determinism check done"
step "Q2b SPIRV-Cross ES 3.10 library through glslang on lavapipe (only if Q1 produced one)"
if [ -s lib310.glsl ]; then sed -E '/^#version/d; /^void main\(\)/,/^\}/d' lib310.glsl > lib.body.glsl; okl=0
  for b in fam/*.body.glsl; do t=${b%.body.glsl}; tag=$(basename $t); n=$(python3 -c "import json;m={x['tag']:x for x in json.load(open('fam/manifest.json'))}['$tag'];print(m['n'],m['nout'])")
    { printf '#version 310 es\nprecision highp float;\nprecision highp int;\n'; cat lib.body.glsl $b; } > $t.l.comp
    glslangValidator -V --target-env vulkan1.2 -o $t.l.spv $t.l.comp >$t.l.log 2>&1 && ./vkrun $t.l.spv $t.in $t.lib.out $n && okl=$((okl+1))
  done; echo "lib path ran $okl"; grep -h -m1 ERROR fam/*.l.log | sort | uniq -c | sort -rn | head -5
  python3 $P/fam.py cmp /src/packages/cultmath/tests/CultMath.Tests/fixtures/glsl-parity.json fam lib | head -15
fi
echo PROBE-DONE
