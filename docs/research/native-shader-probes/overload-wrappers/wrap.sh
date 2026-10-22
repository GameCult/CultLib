#!/usr/bin/env bash
# Imagination probe: build-only export wrappers -> dxc lib_6_3 -spirv -> SPIR-V edit -> SPIRV-Cross ES. /src = CultLib.
set -u; P=/tmp/p; W=/tmp/w; mkdir -p $W/tools; cd $W
step() { echo; echo "=== $*"; }
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null && apt-get install -y -qq --no-install-recommends ca-certificates curl git cmake g++ make python3 \
  mesa-vulkan-drivers libvulkan-dev glslang-tools spirv-tools >/dev/null 2>&1; echo "apt exit $?"
curl -sL --fail -o dxc.tgz https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2607/linux_dxc_2026_07_29.x86_x64.tar.gz
echo "dxc sha ok: $(sha256sum dxc.tgz | cut -c1-64 | grep -c 55665c87824051ed4774ff3280a79ccbbb7d39243b9736ca5e98222134112d54)"
mkdir -p tools/dxc && tar xzf dxc.tgz -C tools/dxc; DXC=$(find tools/dxc -name dxc -type f | head -1)
git clone -q --depth 1 --branch vulkan-sdk-1.4.363.0 https://github.com/KhronosGroup/SPIRV-Cross.git tools/sc
cmake -S tools/sc -B tools/scb -DCMAKE_BUILD_TYPE=Release -DSPIRV_CROSS_ENABLE_TESTS=OFF -DSPIRV_CROSS_CLI=ON >/dev/null && cmake --build tools/scb --target spirv-cross -j4 >/dev/null 2>&1; SC=tools/scb/spirv-cross; ls $SC >/dev/null && echo "spirv-cross built"
SH=/src/packages/cultmath/shaders
step "1 wrappers"
python3 $P/wrapgen.py $SH shw; head -3 shw/cm_wrappers.hlsl | cut -c1-160; grep -c '' shw/wrappers.tsv
echo "source defs per overloaded name: $(cut -f2 shw/wrappers.tsv | sort | uniq -c | awk '$1>1{n++; d+=$1} END{print n" names, "d" defs"}')"
step "2 dxc lib_6_3 on source plus wrappers"
for v in default Od; do f=""; [ $v = Od ] && f="-Od"
  $DXC -T lib_6_3 -HV 2021 -spirv -fspv-target-env=universal1.5 $f -I shw shw/cm_wrappers.hlsl -Fo w.$v.spv 2>&1 | head -3; echo "[$v] dxc exit ${PIPESTATUS[0]}"
  [ -s w.$v.spv ] && python3 $P/spvedit.py stats w.$v.spv shw/wrappers.tsv; done
V=default; python3 $P/spvedit.py stats w.default.spv shw/wrappers.tsv | grep -q 'inlined (no call) 0,' || V=Od; echo "using variant $V"
step "3 SPIR-V edit, validate, SPIRV-Cross ES 310"
for m in drop export; do python3 $P/spvedit.py edit w.$V.spv e.$m.spv shw/wrappers.tsv $m
  spirv-val --target-env spv1.5 e.$m.spv 2>&1 | head -2; echo "[$m] val exit ${PIPESTATUS[0]}"
  $SC e.$m.spv --es --version 310 --output e.$m.glsl 2>&1 | head -2; echo "[$m] spirv-cross exit ${PIPESTATUS[0]}, lines $(wc -l < e.$m.glsl 2>/dev/null)"
  spirv-dis e.$m.spv | grep -E 'OpCapability|OpExtension' | tr -s ' ' | sort | uniq -c | tr '\n' ' '; echo; done
cmp -s e.drop.glsl e.export.glsl && echo "drop and export outputs identical" || echo "drop and export outputs DIFFER"
for m in drop export; do G=e.$m.glsl; [ -s $G ] || continue; python3 - $G shw/wrappers.tsv <<'PY'
import re,sys,collections
g=open(sys.argv[1]).read(); rows=[l.split('\t') for l in open(sys.argv[2]).read().splitlines() if l]
src=collections.Counter(r[1] for r in rows)
defs=re.findall(r'(?m)^(?:\w+ )*?(\w+) (\w+)\(([^)]*)\)\n\{',g); out=collections.Counter(d[1] for d in defs)
print(sys.argv[1],'defs',len(defs),'cultmath_ defs',sum(v for k,v in out.items() if k.startswith('cultmath_')),'source defs',sum(src.values()))
print(' names whose def count differs from source:',[(k,src[k],out.get(k,0)) for k in src if src[k]!=out.get(k,0)][:8])
print(' non-source names emitted:',[k for k in out if k not in src][:10],' cmw in text:',g.count('cmw'),' suffixed _N cultmath names:',len(re.findall(r'\bcultmath_\w+_\d+\(',g)))
print(' prologue:',' | '.join(l for l in g.split('\n')[:6] if l.startswith('#'))[:200], ' main:', g.count('void main'))
PY
done
G=e.export.glsl; echo "--- cultmath_lerp and cultmath_saturate as emitted:"; grep -E '^\w+ cultmath_(lerp|saturate)\(' $G
step "3b glslang ES 3.00 fragment, call sites resolve per overload"
sed -E '/^#version/d; /^#ifdef SPIRV_CROSS/d; /^#endif/d; /^layout\(local_size/d' $G | awk 'BEGIN{s=0} /^void main\(\)/{s=1} s==0{print} s==1&&/^\}/{s=0}' > lib.body.glsl
{ printf '#version 300 es\nprecision highp float;\nprecision highp int;\n'; cat lib.body.glsl; printf 'out highp vec4 c;\nvoid main() { vec3 p = gl_FragCoord.xyz; float a = cultmath_lerp(1.0, 2.0, p.x); vec3 b = cultmath_lerp(p, p.yzx, vec3(0.5)); vec4 s = cultmath_saturate(vec4(p, a)); c = vec4(b, cultmath_saturate(a)) + s + vec4(cultmath_snoise(p)); }\n'; } > es300.frag
glslangValidator es300.frag > es300.txt 2>&1; echo "glslang ES 3.00 fragment: exit $? $(grep -m3 ERROR es300.txt | tr '\n' ' ' | cut -c1-300)"
sed -i 's/cultmath_saturate(a))/cultmath_saturate(ivec2(1)))/' es300.frag; glslangValidator es300.frag > neg.txt 2>&1; echo "negative (no ivec2 overload) refused: exit $? $(grep -m1 ERROR neg.txt | cut -c1-160)"
step "4 split, constants, capability strip"
python3 $P/split.py $SH $G $W/split 2>&1 | head -6
for combo in "core" "core cycles" "core phacelle" "core cycles phacelle"; do f=$W/combo.frag; { printf '#version 300 es\nprecision highp float;\nprecision highp int;\n'; for c in $combo; do cat $W/split/$c.glsl; done; printf 'out highp vec4 c;\nvoid main() { c = vec4(cultmath_snoise(gl_FragCoord.xyz)) + vec4(cultmath_lerp(gl_FragCoord.xyz, vec3(1.0), vec3(0.5)), 1.0); }\n'; } > $f; glslangValidator $f > $W/c.txt 2>&1; echo "glslang [$combo]: exit $? $(grep -m2 ERROR $W/c.txt | tr '\n' ' ' | cut -c1-200)"; done
echo "static const lines: $(grep -hcE '^static const' $SH/*.hlsl | awk '{s+=$1}END{print s}'); of form 'static const float CULTMATH_X = lit;': $(grep -hcE '^static const float CULTMATH_\w+ = [-0-9.eEfx]+;' $SH/*.hlsl | awk '{s+=$1}END{print s}'); CULTMATH_ in output $(grep -c 'CULTMATH_' $G); in committed glsl $(cat $SH/*.glsl | grep -cE '^(const )?\w* ?float CULTMATH_')"
DXC_PATH=$DXC bash $P/lava.sh
echo PROBE-DONE
