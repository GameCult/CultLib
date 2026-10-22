#!/usr/bin/env bash
# Imagination probe: what dxc -Od leaves in the SPIRV-Cross GLSL and what cleans it. ubuntu:24.04, /src = CultLib, /cache persists tools.
set -u; P=/src/docs/research/native-shader-probes; O=$P/overload-wrappers; W=/tmp/w; mkdir -p $W /cache; cd $W
step() { echo; echo "=== $*"; }
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null && apt-get install -y -qq --no-install-recommends ca-certificates curl git cmake g++ make python3 gcc \
  mesa-vulkan-drivers libvulkan-dev glslang-tools spirv-tools >/dev/null 2>&1; echo "apt exit $?"
dpkg-query -W -f='${Package} ${Version}\n' spirv-tools glslang-tools mesa-vulkan-drivers
if [ ! -x /cache/sc/spirv-cross ]; then
  git clone -q --depth 1 --branch vulkan-sdk-1.4.363.0 https://github.com/KhronosGroup/SPIRV-Cross.git /cache/scsrc
  cmake -S /cache/scsrc -B /cache/scb -DCMAKE_BUILD_TYPE=Release -DSPIRV_CROSS_ENABLE_TESTS=OFF -DSPIRV_CROSS_CLI=ON >/dev/null && cmake --build /cache/scb --target spirv-cross -j8 >/dev/null 2>&1
  mkdir -p /cache/sc; cp /cache/scb/spirv-cross /cache/sc/
fi
if [ ! -x /cache/dxc/bin/dxc ]; then
  curl -sL --fail -o /cache/dxc.tgz https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2607/linux_dxc_2026_07_29.x86_x64.tar.gz
  mkdir -p /cache/dxc && tar xzf /cache/dxc.tgz -C /cache/dxc
fi
DXC=$(find /cache/dxc -name dxc -type f | head -1); SC=/cache/sc/spirv-cross; export LD_LIBRARY_PATH=$(dirname $DXC)/../lib:${LD_LIBRARY_PATH:-}
if [ ! -x /cache/st/spirv-opt ]; then
  git clone -q --depth 1 --branch vulkan-sdk-1.4.363.0 https://github.com/KhronosGroup/SPIRV-Tools.git /cache/stsrc
  git clone -q --depth 1 --branch vulkan-sdk-1.4.363.0 https://github.com/KhronosGroup/SPIRV-Headers.git /cache/stsrc/external/spirv-headers
  cmake -S /cache/stsrc -B /cache/stb -DCMAKE_BUILD_TYPE=Release -DSPIRV_SKIP_TESTS=ON -DSPIRV_WERROR=OFF >/dev/null && cmake --build /cache/stb --target spirv-opt spirv-val spirv-dis spirv-as -j8 >/dev/null 2>&1
  mkdir -p /cache/st; cp /cache/stb/tools/spirv-* /cache/st/
fi
export PATH=/cache/st:$PATH; echo "spirv-opt from $(which spirv-opt)"
echo "dxc $DXC"; $DXC --version 2>&1|head -2; spirv-opt --version | head -1
SH=/src/packages/cultmath/shaders; FX=/src/packages/cultmath/tests/CultMath.Tests/fixtures/glsl-parity.json
step "1 build the -Od library as in Pass 9b"
python3 $O/wrapgen.py $SH shw
$DXC -T lib_6_3 -HV 2021 -spirv -fspv-target-env=universal1.5 -Od -I shw shw/cm_wrappers.hlsl -Fo w.spv 2>&1 | head -3
python3 $O/spvedit.py edit w.spv e.spv shw/wrappers.tsv export; spirv-val --target-env spv1.5 e.spv && echo "val ok"
nf() { spirv-dis "$1" | grep -c ' = OpFunction '; }; echo "functions in e.spv: $(nf e.spv)"
step "2 variants: by-value lowering, then spirv-opt (never an inliner)"
cp e.spv v0.spv
python3 $P/od-temporaries/byvalue.py e.spv b0.spv; spirv-val --target-env spv1.5 b0.spv && echo "b0 val ok"
spirv-opt --target-env=spv1.5 --ssa-rewrite --eliminate-dead-code-aggressive --simplify-instructions e.spv -o v2.spv
SSA="--ssa-rewrite --eliminate-dead-code-aggressive"
declare -A VP=([b1]="--eliminate-local-single-store --eliminate-dead-code-aggressive" [b2]="$SSA" [b3]="$SSA --simplify-instructions" [b4]="--eliminate-local-single-block --eliminate-local-single-store --eliminate-local-multi-store --eliminate-dead-code-aggressive --merge-blocks")
for v in b1 b2 b3 b4; do spirv-opt --target-env=spv1.5 ${VP[$v]} b0.spv -o $v.spv 2>$v.err; echo "$v opt exit $? functions $(nf $v.spv 2>/dev/null) $(head -c 160 $v.err)"; done
cp b1.spv b1r.spv; VS="v0 b0 b1 b1r b3"
for v in $VS; do [ -s $v.spv ] || continue
  spirv-val --target-env spv1.5 $v.spv >$v.val 2>&1; vx=$?
  $SC $v.spv --es --version 310 $([ $v = b1r ] && echo --relax-nan-checks) --output $v.glsl >$v.sc 2>&1; sx=$?
  [ -s $v.glsl ] || { echo "$v val $vx sc exit $sx"; head -3 $v.val $v.sc; continue; }
  python3 - $v $vx $sx <<'PY'
import re,sys
v,vx,sx=sys.argv[1:4]; g=open(f'/tmp/w/{v}.glsl').read()
defs=re.findall(r'(?m)^(?:\w+ )*?(\w+) (cultmath_\w+)\(([^)]*)\)\n\{',g)
ids=re.findall(r'\b_\d+\b',g)
print(f'{v}: val={vx} sc={sx} lines={g.count(chr(10))} bytes={len(g)} cultmath_defs={len(defs)} distinct={len(set(d[1] for d in defs))} id_tokens={len(ids)} distinct_ids={len(set(ids))} param_var={g.count("param_var_")} inout={len(re.findall(r"inout",g))} suffixed={len(re.findall(r"cultmath_\w+_\d+\(",g))}')
PY
done
mkdir -p /cache/out; cp *.glsl *.spv /cache/out/ 2>/dev/null
step "3 SPIRV-Cross options"
for opt in "--remove-unused-variables"; do for v in v0 b2; do $SC $v.spv --es --version 310 $opt --output $v.o.glsl >/dev/null 2>&1; echo "$v + $opt: lines $(wc -l < $v.o.glsl)"; done; done
step "4 lerp(vec3) as emitted: v0 vs b2, vs committed"
for v in v0 b2; do echo "--- $v lerp(vec3)"; awk '/^(highp )?vec3 cultmath_lerp\((highp )?vec3/{p=1} p{print} p&&/^}/{exit}' $v.glsl; done
echo "--- committed lerp(vec3)"; grep -n -A3 -E '^(highp )?vec3 cultmath_lerp\(' $SH/CultMath.glsl | head -6
for v in v0 b2; do echo "--- $v snoise(vec3) head"; awk '/^(highp )?float cultmath_snoise\((highp )?vec3/{p=1} p{print; if(++n>=40) exit}' $v.glsl; done
step "5 conformance on lavapipe, every family, per variant vs committed GLSL and HLSL"
mkdir -p fam; gcc -O2 -o vkrun $P/vkrun.c -lvulkan && python3 $P/fam.py gen $FX fam >/dev/null
cat $SH/CultMath.glsl $SH/CultMath.Cycles.glsl $SH/CultMath.Phacelle.glsl > all.glsl
body() { sed -E '/^#version/d; /^#ifdef SPIRV_CROSS/d; /^#endif/d; /^layout\(local_size/d' $1 | awk 'BEGIN{s=0} /^void main\(\)/{s=1} s==0{print} s==1&&/^\}/{s=0}'; }
for v in $VS; do [ -s $v.glsl ] && body $v.glsl > $v.body; done
run() { local t=fam/$1 n; n=$(python3 -c "import json;m={x['tag']:x for x in json.load(open('fam/manifest.json'))}['$1'];print(m['n'],m['nout'])")
  { printf '#version 310 es\nprecision highp float;\nprecision highp int;\n'; cat $3 $t.body.glsl; } > $t.$2.comp
  glslangValidator -V --target-env vulkan1.2 -o $t.$2.spv $t.$2.comp >$t.$2.log 2>&1 && ./vkrun $t.$2.spv $t.in $t.$2.out $n; }
for b in fam/*.body.glsl; do tag=$(basename ${b%.body.glsl})
  n=$(python3 -c "import json;m={x['tag']:x for x in json.load(open('fam/manifest.json'))}['$tag'];print(m['n'],m['nout'])")
  $DXC -T cs_6_0 -E main -HV 2021 -spirv -fspv-target-env=vulkan1.2 -I $SH fam/$tag.hlsl -Fo fam/$tag.h.spv >/dev/null 2>&1 && ./vkrun fam/$tag.h.spv fam/$tag.in fam/$tag.h.out $n
  run $tag g all.glsl; for v in $VS; do [ -s $v.body ] && run $tag $v $v.body; done; done
export VS; python3 - <<'PY'
import os,filecmp
d='/tmp/w/fam'; tags=sorted(f[:-10] for f in os.listdir(d) if f.endswith('.body.glsl'))
def same(a,b): return [t for t in tags if os.path.exists(f'{d}/{t}.{a}.out') and os.path.exists(f'{d}/{t}.{b}.out') and filecmp.cmp(f'{d}/{t}.{a}.out',f'{d}/{t}.{b}.out',shallow=False)]
print('families',len(tags),'hlsl ran',len([t for t in tags if os.path.exists(f'{d}/{t}.h.out')]),'committed glsl ran',len([t for t in tags if os.path.exists(f'{d}/{t}.g.out')]))
for v in os.environ['VS'].split():
    ran=[t for t in tags if os.path.exists(f'{d}/{t}.{v}.out')]
    sg=same(v,'g'); print(v,'ran',len(ran),'bit-identical to committed glsl',len(sg),'to hlsl',len(same(v,'h')),'| differs from g:',[t for t in ran if t not in sg][:8])
print('committed vs hlsl',len(same('g','h')),'of',len(tags))
PY
for v in v0 b2; do { printf '#version 300 es\nprecision highp float;\nprecision highp int;\n'; cat $v.body; printf 'out highp vec4 c;\nvoid main() { vec3 p = gl_FragCoord.xyz; c = vec4(cultmath_lerp(p, p.yzx, vec3(0.5)), cultmath_lerp(1.0, 2.0, p.x)) + cultmath_saturate(vec4(p, 1.0)) + vec4(cultmath_snoise(p)); }\n'; } > $v.es300.frag; glslangValidator $v.es300.frag >$v.es300.txt 2>&1; echo "$v ES 3.00 glslang exit $? $(grep -m2 ERROR $v.es300.txt | tr '\n' ' ' | cut -c1-200)"; done
for t in g $VS; do echo "--- cmp vs C# fixture: $t"; CMP_TOP=3 python3 $P/fam.py cmp $FX fam $t 2>&1 | grep -v "^ *$" | head -8; done
echo PROBE-DONE
